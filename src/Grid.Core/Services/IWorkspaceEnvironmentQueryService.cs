using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IWorkspaceEnvironmentQueryService
{
    Task<ResolvedEnvironmentRefreshResult> RefreshAsync(
        WorkspaceEnvironmentContext context,
        bool forceRefresh,
        IProgress<ResolvedEnvironmentProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<EnvironmentPage<PluginEntry>> QueryPluginsAsync(
        ResolvedSnapshotId snapshotId,
        PluginQuery query,
        CancellationToken cancellationToken = default);

    Task<EnvironmentPage<ResolvedArchiveEntry>> QueryArchivesAsync(
        ResolvedSnapshotId snapshotId,
        ArchiveQuery query,
        CancellationToken cancellationToken = default);

    Task<EnvironmentPage<VirtualDataEntry>> QueryDataAsync(
        ResolvedSnapshotId snapshotId,
        VirtualDataQuery query,
        CancellationToken cancellationToken = default);

    Task<ProviderChain?> GetProviderChainAsync(
        ResolvedSnapshotId snapshotId,
        VirtualPathId virtualPathId,
        CancellationToken cancellationToken = default);
}
