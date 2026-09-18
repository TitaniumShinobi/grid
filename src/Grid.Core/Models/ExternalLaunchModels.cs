using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum ExternalLaunchPreparationStatus
{
    Ready,
    RequiresAcknowledgement,
    Blocked,
    Stale,
    Unavailable,
    Failed,
    Canceled,
}

public enum ExternalLaunchSessionStatus
{
    Preparing,
    Ready,
    LaunchRequested,
    StartingManager,
    Running,
    MonitoringDetached,
    Completed,
    Rejected,
    Failed,
    Canceled,
}

public enum ExternalLaunchEventKind
{
    LaunchRequested,
    ManagerStartRequested,
    ManagerProcessStarted,
    RequestAccepted,
    RequestRejected,
    ConfiguredProcessStarted,
    ConfiguredProcessExited,
    ManagerInvocationExited,
    MonitoringDetached,
    Error,
    PostRunRefreshRequested,
    PostRunRefreshCompleted,
}

public sealed record ExternalLaunchIntent(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    InstallationReferenceId ReferenceId,
    ObservedExecutableId ExecutableId,
    string ExecutableFingerprint,
    FidelityAuditId AuditId,
    string AuditFingerprint);

public sealed record ExternalLaunchPreparation(
    ExternalLaunchSessionId SessionId,
    ExternalLaunchIntent Intent,
    string PreparationFingerprint,
    DateTimeOffset PreparedAtUtc,
    ExternalLaunchPreparationStatus Status,
    ImmutableArray<FidelityAuditItem> BlockingItems,
    ImmutableArray<FidelityAuditItem> AcknowledgementItems,
    string Detail)
{
    public bool CanApprove =>
        Status is ExternalLaunchPreparationStatus.Ready or
            ExternalLaunchPreparationStatus.RequiresAcknowledgement;
}

public sealed record ExternalLaunchPreparationResult(
    ExternalLaunchPreparationStatus Status,
    ExternalLaunchPreparation? Preparation,
    string Detail);

public sealed record ExternalLaunchApproval(
    LaunchApprovalId Id,
    ExternalLaunchSessionId SessionId,
    string PreparationFingerprint,
    string AuditFingerprint,
    DateTimeOffset ApprovedAtUtc,
    bool AcknowledgedWarningsAndUnsupported,
    bool ExplicitUserAction);

public sealed record ExternalLaunchEvent(
    LaunchEventId Id,
    ExternalLaunchSessionId SessionId,
    int Sequence,
    DateTimeOffset OccurredAtUtc,
    ExternalLaunchEventKind Kind,
    string Detail,
    int? ExitCode = null,
    bool ConfiguredProcessLifecycleObserved = false);

public sealed record ExternalLaunchSession(
    ExternalLaunchSessionId Id,
    ExternalLaunchIntent Intent,
    ExternalLaunchSessionStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    ImmutableArray<ExternalLaunchEvent> Events,
    string Detail)
{
    public bool IsTerminal => Status is
        ExternalLaunchSessionStatus.MonitoringDetached or
        ExternalLaunchSessionStatus.Completed or
        ExternalLaunchSessionStatus.Rejected or
        ExternalLaunchSessionStatus.Failed or
        ExternalLaunchSessionStatus.Canceled;
}

public sealed record ExternalLaunchResult(
    ExternalLaunchSessionStatus Status,
    ExternalLaunchSession? Session,
    string Detail);
