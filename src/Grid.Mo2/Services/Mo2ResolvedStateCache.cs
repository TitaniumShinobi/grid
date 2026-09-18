using System.Collections.Concurrent;
using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ResolvedStateCache
{
    private readonly ConcurrentDictionary<ResolvedSnapshotId, Mo2ResolvedEnvironmentData> _snapshots = new();
    private readonly ConcurrentDictionary<WorkspaceEnvironmentContext, ResolvedSnapshotId> _current = new();

    internal bool TryGetCurrent(
        WorkspaceEnvironmentContext context,
        out Mo2ResolvedEnvironmentData snapshot)
    {
        snapshot = default!;
        if (!_current.TryGetValue(context, out var id) || !_snapshots.TryGetValue(id, out var found))
        {
            return false;
        }

        snapshot = found;
        return true;
    }

    internal bool TryGet(ResolvedSnapshotId id, out Mo2ResolvedEnvironmentData snapshot) =>
        _snapshots.TryGetValue(id, out snapshot!);

    internal void Store(Mo2ResolvedEnvironmentData snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.PublicSnapshot.Summary.Status != ResolvedEnvironmentStatus.Complete)
        {
            return;
        }

        _snapshots[snapshot.PublicSnapshot.Id] = snapshot;
        if (_current.TryGetValue(snapshot.PublicSnapshot.Context, out var previous) && previous != snapshot.PublicSnapshot.Id)
        {
            _snapshots.TryRemove(previous, out _);
        }

        _current[snapshot.PublicSnapshot.Context] = snapshot.PublicSnapshot.Id;
    }

    internal void PublishTransient(Mo2ResolvedEnvironmentData snapshot) =>
        _snapshots[snapshot.PublicSnapshot.Id] = snapshot;

    internal ResolvedEnvironmentSummary? FindProfileSummary(InstallationId installationId, ProfileId profileId) =>
        _snapshots.Values
            .Where(value => value.PublicSnapshot.Context.InstallationId == installationId &&
                value.PublicSnapshot.Context.ProfileId == profileId)
            .OrderByDescending(value => value.PublicSnapshot.Summary.ObservedAtUtc)
            .Select(value => value.PublicSnapshot.Summary)
            .FirstOrDefault();
}

internal sealed record Mo2ResolvedEnvironmentData(
    ResolvedEnvironmentSnapshot PublicSnapshot,
    ImmutableArray<PluginEntry> Plugins,
    ImmutableArray<ResolvedArchiveEntry> Archives,
    ImmutableArray<VirtualDataEntry> Data,
    ImmutableDictionary<VirtualPathId, ProviderChain> ProviderChains,
    ImmutableArray<Mo2BaselinePhysicalFile> PhysicalFiles,
    string EvidenceFingerprint);
