using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum OperatorSubjectKind
{
    Mod,
    Plugin,
    Archive,
    DataFile,
    Save,
    Download,
    Conflict,
    Output,
    Activity,
}

public enum OperatorContentKind
{
    Evidence,
    Inference,
    Warning,
}

public enum OperatorMessageRole
{
    User,
    Operator,
    System,
}

public enum OperatorActionAvailability
{
    ReadOnly,
    PreviewOnly,
    Unavailable,
}

public enum OperatorRollbackAvailability
{
    NotApplicable,
    RequiredForExecution,
    Unavailable,
}

public enum OperatorAuditEventKind
{
    ContextActivated,
    UserMessageRecorded,
    MockResponseRecorded,
    ProposalPresented,
    ServiceResponseRejected,
}

public readonly record struct OperatorContextKey(
    GameId? GameId,
    InstallationId? InstallationId,
    ProfileId? ProfileId)
{
    public static OperatorContextKey Empty { get; } = new(null, null, null);

    public override string ToString() =>
        $"{GameId?.Value ?? "none"}/{InstallationId?.Value ?? "none"}/{ProfileId?.Value ?? "none"}";
}

public readonly record struct OperatorContextFingerprint
{
    public OperatorContextFingerprint(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record OperatorSubject(
    OperatorSubjectKind Kind,
    string StableId,
    string Name,
    string Detail);

public sealed record OperatorActivityEvidence(
    EnvironmentEntryId Id,
    string Name,
    string Detail,
    string Status);

public sealed record OperatorPermissionState(
    bool CanReadContext,
    bool CanDraftProposals,
    bool CanApprove,
    bool CanExecute,
    string Summary);

public sealed record DeterministicActionDescriptor(
    DeterministicActionId Id,
    string Title,
    string Preview,
    OperatorActionAvailability Availability,
    ImmutableArray<string> ExpectedEffects,
    ImmutableArray<string> Exclusions,
    string Verification,
    OperatorRollbackAvailability RollbackAvailability,
    string RollbackDetail);

public sealed record OperatorContextSnapshot(
    OperatorContextKey Key,
    OperatorContextFingerprint Fingerprint,
    string CatalogRevision,
    CatalogSourceKind SourceKind,
    string GameName,
    string? InstallationName,
    string? ProfileName,
    string? LaunchTargetName,
    ImmutableArray<OperatorSubject> Subjects,
    int OmittedSubjectCount,
    HealthSummary Health,
    ImmutableArray<OperatorActivityEvidence> RecentActivity,
    ImmutableArray<DeterministicActionDescriptor> AvailableActions,
    OperatorPermissionState Permissions);

public sealed record OperatorStatement(
    OperatorContentKind Kind,
    string Title,
    string Detail);

public sealed record OperatorProposal(
    OperatorProposalId Id,
    DeterministicActionId ActionId,
    OperatorContextFingerprint ContextFingerprint,
    string Title,
    string Preview,
    ImmutableArray<string> ExpectedEffects,
    ImmutableArray<string> Exclusions,
    string Verification,
    OperatorRollbackAvailability RollbackAvailability,
    string RollbackDetail,
    string ApprovalStatus,
    string ExecutionStatus);

public sealed record OperatorMessage(
    OperatorMessageId Id,
    OperatorMessageRole Role,
    string Body,
    ImmutableArray<OperatorStatement> Statements,
    OperatorProposal? Proposal,
    OperatorContextFingerprint ContextFingerprint,
    DateTimeOffset RepresentedAt);

public sealed record OperatorAuditRecord(
    OperatorAuditId Id,
    OperatorAuditEventKind Kind,
    OperatorContextKey ContextKey,
    OperatorContextFingerprint ContextFingerprint,
    string Detail,
    DateTimeOffset RepresentedAt);

public sealed record OperatorTranscriptSnapshot(
    OperatorContextKey ContextKey,
    int Revision,
    ImmutableArray<OperatorMessage> Messages);

public sealed record OperatorSessionSnapshot(
    bool IsExpanded,
    double PanelWidth,
    OperatorContextSnapshot CurrentContext,
    ImmutableArray<OperatorTranscriptSnapshot> Transcripts,
    ImmutableArray<OperatorAuditRecord> AuditRecords);

public sealed record OperatorRequest(
    OperatorContextSnapshot Context,
    ImmutableArray<OperatorMessage> RecentHistory,
    string UserText);

public sealed record OperatorServiceResponse(
    string Body,
    ImmutableArray<OperatorStatement> Statements,
    DeterministicActionId? ProposedActionId);
