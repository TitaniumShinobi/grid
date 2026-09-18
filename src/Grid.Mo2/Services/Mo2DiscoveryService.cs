using System.Collections.Immutable;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2DiscoveryService(
    IWindowsMo2EvidenceSource evidenceSource,
    IMo2InstallationReferenceStore referenceStore,
    IMo2PathCanonicalizer paths) : IMo2DiscoveryService
{
    public async Task<Mo2DiscoveryResult> DiscoverAsync(
        Mo2DiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        var evidence = ImmutableArray.CreateBuilder<Mo2DiscoveryEvidence>();
        var stored = await referenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        issues.AddRange(stored.Issues);
        evidence.AddRange(stored.References
            .Where(reference => reference.GameId == options.ExpectedGameId)
            .Select(reference => new Mo2DiscoveryEvidence(
                Mo2EvidenceKind.PersistedReference,
                Path.GetDirectoryName(reference.ExecutablePath),
                reference.InstanceDirectory,
                "Grid connected-installation reference",
                100)));
        evidence.AddRange(await evidenceSource.FindAsync(options, cancellationToken).ConfigureAwait(false));

        var candidates = new Dictionary<string, Mo2DiscoveryCandidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in evidence.OrderByDescending(item => item.Rank))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = item.ApplicationDirectory;
            var instance = item.InstancePath;
            if (application is null && instance is null)
            {
                continue;
            }

            var identityPaths = new[] { application, instance }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .ToArray();
            string key;
            try
            {
                key = paths.GetIdentityKey(identityPaths);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (candidates.ContainsKey(key))
            {
                continue;
            }

            var displayPath = instance ?? application!;
            candidates.Add(key, new(
                key,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(displayPath)),
                application,
                instance,
                item.Kind,
                item.Description));
        }

        return new(
            candidates.Values.ToImmutableArray(),
            issues.ToImmutable());
    }
}
