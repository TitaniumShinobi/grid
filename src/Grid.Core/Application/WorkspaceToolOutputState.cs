using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

/// <summary>
/// Owns the selected connected-profile tool/output observation for one application session.
/// It grants no filesystem, configuration, or process authority.
/// </summary>
public sealed class WorkspaceToolOutputState : IDisposable
{
    private readonly IWorkspaceToolOutputQueryService _service;
    private CancellationTokenSource? _refreshCancellation;
    private long _generation;
    private bool _disposed;

    public WorkspaceToolOutputState(IWorkspaceToolOutputQueryService service) =>
        _service = service ?? throw new ArgumentNullException(nameof(service));

    public WorkspaceToolOutputContext? Context { get; private set; }

    public ToolOutputObservationSnapshot? Snapshot { get; private set; }

    public ToolOutputObservationRefreshResult? LastResult { get; private set; }

    public ExternalObservationProgress? Progress { get; private set; }

    public bool IsRefreshing { get; private set; }

    public bool IsCurrentSnapshotStale { get; private set; }

    public ObservedExecutableId? SelectedExecutableId { get; private set; }

    public GeneratedOutputId? SelectedOutputId { get; private set; }

    public void SetContext(WorkspaceToolOutputContext? context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Context == context)
        {
            return;
        }

        CancelActiveRefresh();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
        _generation++;
        Context = context;
        Snapshot = null;
        LastResult = null;
        Progress = null;
        IsRefreshing = false;
        IsCurrentSnapshotStale = false;
        ClearSelection();
    }

    public async Task<ToolOutputObservationRefreshResult> RefreshAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Context is not WorkspaceToolOutputContext context)
        {
            var unavailable = new ToolOutputObservationRefreshResult(
                ExternalObservationRefreshStatus.Unavailable,
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

        ToolOutputObservationRefreshResult result;
        try
        {
            result = await _service.RefreshAsync(context, forceRefresh, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new ToolOutputObservationRefreshResult(
                ExternalObservationRefreshStatus.Canceled,
                null,
                "The read-only tool and output observation was canceled.",
                []);
        }
        catch (Exception exception)
        {
            result = new ToolOutputObservationRefreshResult(
                ExternalObservationRefreshStatus.Failed,
                null,
                $"The read-only tool and output observation failed: {exception.GetType().Name}.",
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

    public bool SelectExecutable(ObservedExecutableId? id)
    {
        if (id is not null && Snapshot?.Executables.Any(entry => entry.Id == id.Value) != true)
        {
            return false;
        }

        SelectedExecutableId = id;
        SelectedOutputId = null;
        return true;
    }

    public bool SelectOutput(GeneratedOutputId? id)
    {
        if (id is not null && Snapshot?.Outputs.Any(entry => entry.Id == id.Value) != true)
        {
            return false;
        }

        SelectedExecutableId = null;
        SelectedOutputId = id;
        return true;
    }

    public ObservedExecutableSummary? GetSelectedExecutable() =>
        SelectedExecutableId is ObservedExecutableId id
            ? Snapshot?.Executables.FirstOrDefault(entry => entry.Id == id)
            : null;

    public GeneratedOutputSummary? GetSelectedOutput() =>
        SelectedOutputId is GeneratedOutputId id
            ? Snapshot?.Outputs.FirstOrDefault(entry => entry.Id == id)
            : null;

    public void ClearSelection()
    {
        SelectedExecutableId = null;
        SelectedOutputId = null;
    }

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

    private void ReconcileSelections()
    {
        // Observed identities are snapshot-bound. A refresh must not silently retarget a
        // configuration or output solely because its display title still matches.
        ClearSelection();
    }

    private void CancelActiveRefresh()
    {
        if (_refreshCancellation is { IsCancellationRequested: false })
        {
            _refreshCancellation.Cancel();
        }
    }

    private sealed class InlineProgress(Action<ExternalObservationProgress> report)
        : IProgress<ExternalObservationProgress>
    {
        public void Report(ExternalObservationProgress value) => report(value);
    }
}
