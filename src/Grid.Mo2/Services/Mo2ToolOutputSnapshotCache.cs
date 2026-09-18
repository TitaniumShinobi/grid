using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ToolOutputSnapshotCache
{
    private readonly Mo2ToolOutputService service;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<CacheKey, Mo2ToolOutputSnapshot> current = [];

    public Mo2ToolOutputSnapshotCache(Mo2ToolOutputService service) =>
        this.service = service ?? throw new ArgumentNullException(nameof(service));

    public async Task<Mo2ToolOutputSnapshotComparison> RefreshAsync(
        Mo2ToolOutputObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var observed = await service.ObserveAsync(request, cancellationToken).ConfigureAwait(false);
        var key = new CacheKey(request.Reference.Id, request.Profile.Id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current.TryGetValue(key, out var previous);
            var compared = ApplyComparison(previous, observed, out var removed);
            if (observed.Status == Mo2ToolOutputObservationStatus.Complete)
            {
                current[key] = compared;
            }

            return new(previous, compared, removed);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Mo2ToolOutputSnapshot?> GetCurrentAsync(
        InstallationReferenceId referenceId,
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return current.GetValueOrDefault(new(referenceId, profileId));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RevokeAsync(
        InstallationReferenceId referenceId,
        ProfileId? profileId = null,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var key in current.Keys.Where(key =>
                key.ReferenceId == referenceId && (profileId is null || key.ProfileId == profileId)).ToArray())
            {
                current.Remove(key);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static Mo2ToolOutputSnapshot ApplyComparison(
        Mo2ToolOutputSnapshot? previous,
        Mo2ToolOutputSnapshot current,
        out ImmutableArray<Mo2GeneratedOutputObservation> removed)
    {
        if (previous is null)
        {
            removed = [];
            return current;
        }

        var old = previous.Outputs.ToDictionary(output => output.Id);
        var compared = current.Outputs.Select(output =>
        {
            if (!old.TryGetValue(output.Id, out var prior))
            {
                return output with { Comparison = Mo2OutputComparisonState.Added };
            }

            old.Remove(output.Id);
            return output with { Comparison = Compare(prior, output) };
        }).ToImmutableArray();
        removed = old.Values
            .OrderBy(output => output.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(output => output with { Comparison = Mo2OutputComparisonState.Removed })
            .ToImmutableArray();
        return current with { Outputs = compared };
    }

    private static Mo2OutputComparisonState Compare(
        Mo2GeneratedOutputObservation previous,
        Mo2GeneratedOutputObservation current)
    {
        if (previous.Availability != current.Availability)
            return Mo2OutputComparisonState.AvailabilityChanged;
        if (previous.Kind != current.Kind || previous.AssociatedToolFamily != current.AssociatedToolFamily)
            return Mo2OutputComparisonState.Reclassified;
        if (previous.AssociationConfidence != current.AssociationConfidence ||
            previous.AssociatedExecutableId != current.AssociatedExecutableId)
            return Mo2OutputComparisonState.AssociationChanged;
        if (previous.Fingerprint.Strength == Mo2OutputFingerprintStrength.CompleteContent &&
            current.Fingerprint.Strength == Mo2OutputFingerprintStrength.CompleteContent)
        {
            return string.Equals(
                previous.Fingerprint.ContentFingerprint,
                current.Fingerprint.ContentFingerprint,
                StringComparison.Ordinal)
                    ? Mo2OutputComparisonState.NoChange
                    : Mo2OutputComparisonState.Modified;
        }

        if (!string.IsNullOrEmpty(previous.Fingerprint.StructuralFingerprint) &&
            string.Equals(
                previous.Fingerprint.StructuralFingerprint,
                current.Fingerprint.StructuralFingerprint,
                StringComparison.Ordinal))
            return Mo2OutputComparisonState.NoChangeObserved;
        if (previous.Fingerprint.Strength is Mo2OutputFingerprintStrength.Unavailable or Mo2OutputFingerprintStrength.Partial ||
            current.Fingerprint.Strength is Mo2OutputFingerprintStrength.Unavailable or Mo2OutputFingerprintStrength.Partial)
            return Mo2OutputComparisonState.Indeterminate;
        return Mo2OutputComparisonState.Modified;
    }

    private readonly record struct CacheKey(
        InstallationReferenceId ReferenceId,
        ProfileId ProfileId);
}
