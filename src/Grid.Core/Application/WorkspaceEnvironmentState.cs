using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class WorkspaceEnvironmentState : IDisposable
{
    private readonly IWorkspaceEnvironmentQueryService _service;
    private CancellationTokenSource? _refreshCancellation;
    private long _generation;
    private bool _disposed;

    public WorkspaceEnvironmentState(IWorkspaceEnvironmentQueryService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public WorkspaceEnvironmentContext? Context { get; private set; }

    public ResolvedEnvironmentSnapshot? Snapshot { get; private set; }

    public ResolvedEnvironmentRefreshResult? LastResult { get; private set; }

    public ResolvedEnvironmentProgress? Progress { get; private set; }

    public bool IsRefreshing { get; private set; }

    public bool IsCurrentSnapshotStale { get; private set; }

    public PluginId? SelectedPluginId { get; private set; }

    public ArchiveId? SelectedArchiveId { get; private set; }

    public VirtualPathId? SelectedVirtualPathId { get; private set; }

    public void SetContext(WorkspaceEnvironmentContext? context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Context == context)
        {
            return;
        }

        CancelActiveRefresh();
        _refreshCancellation?.Dispose();
        _generation++;
        Context = context;
        Snapshot = null;
        LastResult = null;
        Progress = null;
        IsRefreshing = false;
        IsCurrentSnapshotStale = false;
        ClearSelection();
    }

    public async Task<ResolvedEnvironmentRefreshResult> RefreshAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Context is not WorkspaceEnvironmentContext context)
        {
            var unavailable = new ResolvedEnvironmentRefreshResult(
                ResolvedEnvironmentRefreshStatus.Unavailable,
                null,
                "No selected connected profile context is available.",
                []);
            LastResult = unavailable;
            return unavailable;
        }

        CancelActiveRefresh();
        _refreshCancellation?.Dispose();
        var generation = ++_generation;
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _refreshCancellation.Token;
        IsRefreshing = true;
        Progress = null;
        var progress = new InlineProgress(value =>
        {
            if (generation == _generation)
            {
                Progress = value;
            }
        });

        ResolvedEnvironmentRefreshResult result;
        try
        {
            result = await _service.RefreshAsync(context, forceRefresh, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new ResolvedEnvironmentRefreshResult(
                ResolvedEnvironmentRefreshStatus.Canceled,
                null,
                "The read-only environment observation was canceled.",
                []);
        }
        catch (Exception exception)
        {
            result = new ResolvedEnvironmentRefreshResult(
                ResolvedEnvironmentRefreshStatus.Failed,
                null,
                $"The read-only environment observation failed: {exception.GetType().Name}.",
                []);
        }

        if (generation != _generation || Context != context)
        {
            return result;
        }

        IsRefreshing = false;
        LastResult = result;
        if (result.HasCurrentSnapshot && result.Snapshot is not null)
        {
            Snapshot = result.Snapshot;
            IsCurrentSnapshotStale = false;
            ReconcileSelections();
        }
        else
        {
            IsCurrentSnapshotStale = Snapshot is not null;
        }

        return result;
    }

    public void CancelRefresh() => CancelActiveRefresh();

    public void SelectPlugin(PluginId? id)
    {
        SelectedPluginId = id;
        SelectedArchiveId = null;
        SelectedVirtualPathId = null;
    }

    public void SelectArchive(ArchiveId? id)
    {
        SelectedPluginId = null;
        SelectedArchiveId = id;
        SelectedVirtualPathId = null;
    }

    public void SelectVirtualPath(VirtualPathId? id)
    {
        SelectedPluginId = null;
        SelectedArchiveId = null;
        SelectedVirtualPathId = id;
    }

    public void ClearSelection()
    {
        SelectedPluginId = null;
        SelectedArchiveId = null;
        SelectedVirtualPathId = null;
    }

    public Task<EnvironmentPage<PluginEntry>> QueryPluginsAsync(
        PluginQuery query,
        CancellationToken cancellationToken = default) =>
        _service.QueryPluginsAsync(RequireSnapshot(), query, cancellationToken);

    public Task<EnvironmentPage<ResolvedArchiveEntry>> QueryArchivesAsync(
        ArchiveQuery query,
        CancellationToken cancellationToken = default) =>
        _service.QueryArchivesAsync(RequireSnapshot(), query, cancellationToken);

    public Task<EnvironmentPage<VirtualDataEntry>> QueryDataAsync(
        VirtualDataQuery query,
        CancellationToken cancellationToken = default) =>
        _service.QueryDataAsync(RequireSnapshot(), query, cancellationToken);

    public Task<ProviderChain?> GetProviderChainAsync(
        VirtualPathId virtualPathId,
        CancellationToken cancellationToken = default) =>
        _service.GetProviderChainAsync(RequireSnapshot(), virtualPathId, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelActiveRefresh();
        _refreshCancellation?.Dispose();
    }

    private ResolvedSnapshotId RequireSnapshot() => Snapshot?.Id ??
        throw new InvalidOperationException("A current resolved-environment snapshot is required before querying evidence.");

    private void ReconcileSelections()
    {
        // Query-backed identities are snapshot-bound. A reconstructed snapshot must not
        // silently retarget a selection even when a display name happens to be unchanged.
        ClearSelection();
    }

    private void CancelActiveRefresh()
    {
        if (_refreshCancellation is { IsCancellationRequested: false })
        {
            _refreshCancellation.Cancel();
        }
    }

    private sealed class InlineProgress(Action<ResolvedEnvironmentProgress> report)
        : IProgress<ResolvedEnvironmentProgress>
    {
        public void Report(ResolvedEnvironmentProgress value) => report(value);
    }
}
