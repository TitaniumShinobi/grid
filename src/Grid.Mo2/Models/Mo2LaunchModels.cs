using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2RuntimeKind { SkyrimSpecialEdition, Skse, InspectOnly }
public enum Mo2VersionEvidenceStatus { Supported, TooOld, Missing, Malformed, Unverifiable }
public enum Mo2ProcessProbeStatus { Clear, Running, Indeterminate }
public enum Mo2LaunchStatus { Started, Completed, Rejected, Failed, MonitoringDetached, Canceled }
public enum Mo2LaunchEventKind
{
    Requested,
    Preflight,
    Mo2CommandStarted,
    AwaitingResult,
    Mo2ReportedCompletion,
    RejectedOrFailed,
    MonitoringDetached,
    PostRunRefreshResult,
}

public sealed record Mo2VersionEvidence(
    Mo2VersionEvidenceStatus Status,
    Version? Version,
    string ExecutableIdentity,
    string Detail);

public sealed record Mo2ProcessObservation(
    int ProcessId,
    DateTimeOffset? StartTimeUtc,
    string? CanonicalMainModulePath,
    bool IdentityConfirmed);

public sealed record Mo2ProcessProbeResult(
    Mo2ProcessProbeStatus Status,
    ImmutableArray<Mo2ProcessObservation> Processes,
    string Detail);

public sealed record Mo2LaunchInvocation(
    string ExecutablePath,
    ImmutableArray<string> Arguments,
    string ExecutableIdentity,
    string RouteFingerprint);

public sealed record Mo2LaunchCandidate(
    InstallationReferenceId ReferenceId,
    ProfileId ProfileId,
    ManagerProfileState ProfileState,
    ObservedExecutableId ExecutableId,
    int SourceIndex,
    string ExactTitle,
    string ConfigurationFingerprint,
    Mo2RuntimeKind RuntimeKind,
    bool IsDuplicate,
    Mo2ExecutablePathAvailability BinaryAvailability,
    string AuditFingerprint,
    bool AuditAcknowledged,
    string Mo2ExecutableIdentity);

public sealed record Mo2LaunchApproval(
    Guid Token,
    ObservedExecutableId ExecutableId,
    string AuditFingerprint,
    DateTimeOffset ApprovedAtUtc);

public sealed record Mo2LaunchRequest(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    Mo2LaunchCandidate Candidate,
    Mo2LaunchApproval Approval);

public sealed record Mo2LaunchEvidence(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    Mo2ExecutableConfigurationSnapshot Executables,
    ProfileId SelectedProfileId,
    ObservedExecutableId SelectedExecutableId,
    Mo2ObservedExecutable SelectedExecutable,
    ManagerProfileState ActiveProfileState,
    string ActiveProfileFingerprint);

public sealed record Mo2LaunchEvent(
    Mo2LaunchEventKind Kind,
    DateTimeOffset AtUtc,
    string Detail,
    string AuditFingerprint,
    ObservedExecutableId ExecutableId);

public sealed record Mo2LaunchResult(
    Mo2LaunchStatus Status,
    int? Mo2ExitCode,
    ImmutableArray<Mo2LaunchEvent> Events,
    string Detail);

public interface IMo2ProcessHandle : IAsyncDisposable
{
    int ProcessId { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}
