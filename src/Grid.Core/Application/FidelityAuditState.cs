using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class FidelityAuditState : IDisposable
{
    private readonly IFidelityAuditService service;
    private CancellationTokenSource? activeCancellation;
    private long generation;
    private bool disposed;

    public FidelityAuditState(IFidelityAuditService service) =>
        this.service = service ?? throw new ArgumentNullException(nameof(service));

    public FidelityAuditContext? Context { get; private set; }

    public FidelityAuditSnapshot? Snapshot { get; private set; }

    public FidelityAuditResult? LastResult { get; private set; }

    public FidelityAuditProgress? Progress { get; private set; }

    public bool IsRunning { get; private set; }

    public bool IsCurrentSnapshotStale { get; private set; }

    public void SetContext(FidelityAuditContext? context)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Context == context)
        {
            return;
        }

        CancelActiveRun();
        activeCancellation?.Dispose();
        activeCancellation = null;
        generation++;
        Context = context;
        IsRunning = false;
        Progress = null;
        LastResult = null;
        IsCurrentSnapshotStale = Snapshot is not null;
    }

    public async Task<FidelityAuditResult> RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Context is not FidelityAuditContext context)
        {
            var unavailable = new FidelityAuditResult(
                FidelityAuditRunStatus.Unavailable,
                null,
                "No selected connected-profile evidence is available for an audit.");
            LastResult = unavailable;
            return unavailable;
        }

        CancelActiveRun();
        activeCancellation?.Dispose();
        var capturedGeneration = ++generation;
        activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = activeCancellation.Token;
        IsRunning = true;
        Progress = null;
        var progress = new InlineProgress(value =>
        {
            if (capturedGeneration == generation)
            {
                Progress = value;
            }
        });

        FidelityAuditResult result;
        try
        {
            result = await service.RunAsync(context, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new(
                FidelityAuditRunStatus.Canceled,
                null,
                "The fidelity audit was canceled. Any previous audit is stale.");
        }
        catch (Exception exception)
        {
            result = new(
                FidelityAuditRunStatus.Failed,
                null,
                $"The fidelity audit failed safely: {exception.GetType().Name}.");
        }

        if (result.Snapshot is FidelityAuditSnapshot returnedSnapshot && returnedSnapshot.Context != context)
        {
            result = new(
                FidelityAuditRunStatus.Failed,
                null,
                "The fidelity-audit service returned evidence for a different context.");
        }

        if (capturedGeneration != generation || Context != context)
        {
            return result;
        }

        IsRunning = false;
        LastResult = result;
        if (result.HasCurrentSnapshot && result.Snapshot is not null)
        {
            Snapshot = result.Snapshot;
            IsCurrentSnapshotStale = false;
        }
        else
        {
            IsCurrentSnapshotStale = Snapshot is not null;
        }

        return result;
    }

    public void Invalidate(string? detail = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancelActiveRun();
        generation++;
        IsRunning = false;
        Progress = null;
        IsCurrentSnapshotStale = Snapshot is not null;
        if (!string.IsNullOrWhiteSpace(detail))
        {
            LastResult = new FidelityAuditResult(FidelityAuditRunStatus.Unavailable, null, detail);
        }
    }

    public void Cancel() => CancelActiveRun();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelActiveRun();
        activeCancellation?.Dispose();
    }

    private void CancelActiveRun()
    {
        if (activeCancellation is { IsCancellationRequested: false })
        {
            activeCancellation.Cancel();
        }
    }

    private sealed class InlineProgress(Action<FidelityAuditProgress> report)
        : IProgress<FidelityAuditProgress>
    {
        public void Report(FidelityAuditProgress value) => report(value);
    }
}
