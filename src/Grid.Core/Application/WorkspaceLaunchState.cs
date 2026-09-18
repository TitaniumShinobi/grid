using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class WorkspaceLaunchState : IDisposable
{
    private readonly IWorkspaceLaunchService service;
    private readonly TimeProvider timeProvider;
    private CancellationTokenSource? activeCancellation;
    private readonly List<ExternalLaunchEvent> events = [];
    private long generation;
    private int approvalSequence;
    private bool disposed;

    public WorkspaceLaunchState(
        IWorkspaceLaunchService service,
        TimeProvider? timeProvider = null)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ExternalLaunchIntent? Intent { get; private set; }

    public ExternalLaunchPreparation? Preparation { get; private set; }

    public ExternalLaunchPreparationResult? LastPreparationResult { get; private set; }

    public ExternalLaunchSession? CurrentSession { get; private set; }

    public ExternalLaunchResult? LastLaunchResult { get; private set; }

    public bool IsPreparing { get; private set; }

    public bool IsLaunching { get; private set; }

    public ImmutableArray<ExternalLaunchEvent> Events => events.ToImmutableArray();

    public bool CanApprove => Preparation?.CanApprove == true && !IsPreparing && !IsLaunching;

    public void SetIntent(ExternalLaunchIntent? intent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Intent == intent)
        {
            return;
        }

        CancelActiveOperation();
        activeCancellation?.Dispose();
        activeCancellation = null;
        generation++;
        Intent = intent;
        Preparation = null;
        LastPreparationResult = null;
        CurrentSession = null;
        LastLaunchResult = null;
        IsPreparing = false;
        IsLaunching = false;
        events.Clear();
    }

    public async Task<ExternalLaunchPreparationResult> PrepareAsync(
        FidelityAuditSnapshot audit,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(audit);
        if (Intent is not ExternalLaunchIntent intent)
        {
            var unavailable = new ExternalLaunchPreparationResult(
                ExternalLaunchPreparationStatus.Unavailable,
                null,
                "No selected connected executable is available.");
            LastPreparationResult = unavailable;
            return unavailable;
        }

        if (!AuditMatchesIntent(audit, intent))
        {
            var stale = new ExternalLaunchPreparationResult(
                ExternalLaunchPreparationStatus.Stale,
                null,
                "The launch intent is not bound to the current fidelity audit.");
            LastPreparationResult = stale;
            return stale;
        }

        CancelActiveOperation();
        activeCancellation?.Dispose();
        var capturedGeneration = ++generation;
        activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = activeCancellation.Token;
        IsPreparing = true;

        ExternalLaunchPreparationResult result;
        try
        {
            result = await service.PrepareAsync(intent, audit, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new(
                ExternalLaunchPreparationStatus.Canceled,
                null,
                "Launch preparation was canceled before any process was started.");
        }
        catch (Exception exception)
        {
            result = new(
                ExternalLaunchPreparationStatus.Failed,
                null,
                $"Launch preparation failed safely: {exception.GetType().Name}.");
        }

        if (result.Preparation is ExternalLaunchPreparation returnedPreparation &&
            (returnedPreparation.Intent != intent || returnedPreparation.Status != result.Status))
        {
            result = new(
                ExternalLaunchPreparationStatus.Failed,
                null,
                "The launch service returned a preparation for a different intent or status.");
        }

        if (capturedGeneration != generation || Intent != intent)
        {
            return result;
        }

        IsPreparing = false;
        LastPreparationResult = result;
        Preparation = result.Preparation;
        CurrentSession = result.Preparation is null
            ? null
            : new ExternalLaunchSession(
                result.Preparation.SessionId,
                intent,
                result.Preparation.CanApprove
                    ? ExternalLaunchSessionStatus.Ready
                    : ExternalLaunchSessionStatus.Preparing,
                result.Preparation.PreparedAtUtc,
                null,
                [],
                result.Detail);
        events.Clear();
        return result;
    }

    public ExternalLaunchApproval CreateApproval(bool acknowledgeWarningsAndUnsupported)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var preparation = Preparation ??
            throw new InvalidOperationException("A current launch preparation is required before approval.");
        if (!preparation.CanApprove)
        {
            throw new InvalidOperationException("The current launch preparation is blocked.");
        }

        if (!preparation.AcknowledgementItems.IsEmpty && !acknowledgeWarningsAndUnsupported)
        {
            throw new InvalidOperationException("All launch-relevant warnings and unsupported evidence must be acknowledged.");
        }

        var number = ++approvalSequence;
        return new ExternalLaunchApproval(
            new($"launch-approval.{preparation.SessionId.Value}.{number:D3}"),
            preparation.SessionId,
            preparation.PreparationFingerprint,
            preparation.Intent.AuditFingerprint,
            timeProvider.GetUtcNow(),
            acknowledgeWarningsAndUnsupported,
            ExplicitUserAction: true);
    }

    public async Task<ExternalLaunchResult> LaunchAsync(
        ExternalLaunchApproval approval,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(approval);
        var preparation = Preparation ??
            throw new InvalidOperationException("A current launch preparation is required before launch.");
        ValidateApproval(preparation, approval);
        if (IsLaunching)
        {
            throw new InvalidOperationException("A launch is already active.");
        }

        CancelActiveOperation();
        activeCancellation?.Dispose();
        var capturedGeneration = ++generation;
        activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = activeCancellation.Token;
        IsLaunching = true;
        events.Clear();
        var progress = new InlineProgress(value =>
        {
            if (capturedGeneration == generation && value.SessionId == preparation.SessionId)
            {
                AppendEvent(value);
            }
        });

        ExternalLaunchResult result;
        try
        {
            result = await service.LaunchAsync(preparation, approval, progress, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            result = new(
                ExternalLaunchSessionStatus.Canceled,
                null,
                "Launch monitoring was canceled. No external process was terminated by Grid.");
        }
        catch (Exception exception)
        {
            result = new(
                ExternalLaunchSessionStatus.Failed,
                null,
                $"The launch request failed safely: {exception.GetType().Name}.");
        }

        if (result.Session is ExternalLaunchSession returnedSession &&
            (returnedSession.Id != preparation.SessionId || returnedSession.Intent != preparation.Intent))
        {
            result = new(
                ExternalLaunchSessionStatus.Failed,
                null,
                "The launch service returned lifecycle evidence for a different preparation.");
        }

        if (capturedGeneration != generation || Preparation != preparation)
        {
            return result;
        }

        IsLaunching = false;
        LastLaunchResult = result;
        if (result.Session is not null)
        {
            foreach (var launchEvent in result.Session.Events)
            {
                AppendEvent(launchEvent);
            }

            CurrentSession = result.Session with { Events = events.ToImmutableArray() };
        }
        else
        {
            CurrentSession = CurrentSession is null
                ? null
                : CurrentSession with
                {
                    Status = result.Status,
                    CompletedAtUtc = timeProvider.GetUtcNow(),
                    Events = events.ToImmutableArray(),
                    Detail = result.Detail,
                };
        }

        return result;
    }

    public void CancelOrDetachMonitoring() => CancelActiveOperation();

    public void Invalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        CancelActiveOperation();
        generation++;
        Preparation = null;
        LastPreparationResult = null;
        IsPreparing = false;
        if (!IsLaunching)
        {
            CurrentSession = null;
            events.Clear();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelActiveOperation();
        activeCancellation?.Dispose();
    }

    private static bool AuditMatchesIntent(FidelityAuditSnapshot audit, ExternalLaunchIntent intent) =>
        audit.Id == intent.AuditId &&
        string.Equals(audit.Fingerprint, intent.AuditFingerprint, StringComparison.Ordinal) &&
        audit.Context.GameId == intent.GameId &&
        audit.Context.InstallationId == intent.InstallationId &&
        audit.Context.ProfileId == intent.ProfileId &&
        audit.Context.ReferenceId == intent.ReferenceId &&
        audit.Context.SelectedExecutableId == intent.ExecutableId &&
        string.Equals(
            audit.Context.SelectedExecutableFingerprint,
            intent.ExecutableFingerprint,
            StringComparison.Ordinal);

    private static void ValidateApproval(
        ExternalLaunchPreparation preparation,
        ExternalLaunchApproval approval)
    {
        if (!approval.ExplicitUserAction ||
            approval.SessionId != preparation.SessionId ||
            !string.Equals(approval.PreparationFingerprint, preparation.PreparationFingerprint, StringComparison.Ordinal) ||
            !string.Equals(approval.AuditFingerprint, preparation.Intent.AuditFingerprint, StringComparison.Ordinal))
        {
            throw new ArgumentException("Approval is not bound to the current launch preparation.", nameof(approval));
        }

        if (!preparation.AcknowledgementItems.IsEmpty && !approval.AcknowledgedWarningsAndUnsupported)
        {
            throw new ArgumentException("The approval does not acknowledge launch-relevant evidence.", nameof(approval));
        }
    }

    private void AppendEvent(ExternalLaunchEvent value)
    {
        if (events.Any(existing => existing.Id == value.Id))
        {
            return;
        }

        if (value.Sequence <= 0 ||
            events.Any(existing => existing.Sequence == value.Sequence) ||
            (events.Count > 0 && value.Sequence <= events[^1].Sequence))
        {
            throw new InvalidOperationException("Launch lifecycle events must be append-only with increasing positive sequences.");
        }

        events.Add(value);
    }

    private void CancelActiveOperation()
    {
        if (activeCancellation is { IsCancellationRequested: false })
        {
            activeCancellation.Cancel();
        }
    }

    private sealed class InlineProgress(Action<ExternalLaunchEvent> report)
        : IProgress<ExternalLaunchEvent>
    {
        public void Report(ExternalLaunchEvent value) => report(value);
    }
}
