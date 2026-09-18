using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum AssistantOperatingMode
{
    Ask,
    Assist,
    Auto,
}

public enum AssistantProviderAvailability
{
    NotConfigured,
    Available,
}

public enum AssistantLifecycleStage
{
    Idle,
    EvidenceAvailable,
    ProposalAvailable,
    AwaitingApproval,
    InProgress,
    Verifying,
    RecoveryRequired,
    Completed,
    Failed,
}

public enum AssistantSurface
{
    Home,
    Task,
    History,
}

public enum AssistantIntakeScope
{
    Game,
    Grid,
}

public enum AssistantDraftReadiness
{
    Incomplete,
    ReadyForDeterministicCollection,
    UnsupportedCoverage,
    RuntimeUnavailable,
}

public enum AssistantAuthorizationScope
{
    SelectedInstallationAndProfileReadOnly,
}

public enum AssistantCaseAction
{
    AttachEvidence,
    CaptureCurrentState,
    RefreshRecoverySources,
    Diagnose,
    ReviewEvidence,
    ReviewRepair,
    ApplyRepair,
    RollBack,
}

public sealed record AssistantGameplayCapabilityOption(
    string Id,
    string DisplayName,
    ImmutableArray<string> IntentPhrases = default);

public sealed record AssistantClassOption(
    string Id,
    string DisplayName,
    string IconId,
    bool IsRegistered,
    string RecipeVersion = "1.0.0",
    int MinimumMods = 0,
    int MinimumTools = 0,
    ImmutableArray<ToolId> AllowedToolIds = default,
    ImmutableArray<AssistantGameplayCapabilityOption> GameplayCapabilities = default);

public sealed record AssistantGameOption(GameId Id, string Name);

public sealed record AssistantInstallationOption(InstallationId Id, GameId GameId, string Name);

public sealed record AssistantProfileOption(ProfileId Id, InstallationId InstallationId, string Name);

public sealed record AssistantModOption(ModId Id, string Name, bool IsEnabled, ModEntryKind Kind);

public sealed record AssistantToolOption(
    ToolId Id,
    string Name,
    AvailabilityState Availability,
    string? AvailabilityDetail);

public sealed record AssistantDraftSnapshot(
    AssistantIntakeScope Scope,
    GameId? GameId,
    ImmutableArray<ModId> ModIds,
    ImmutableArray<ToolId> ToolIds,
    string? ClassId,
    string PlainText,
    string DisplayTitle,
    string ClassIconId,
    AssistantDraftReadiness Readiness,
    string ReadinessDetail,
    bool CanSubmit,
    InstallationId? InstallationId = null,
    ProfileId? ProfileId = null,
    string Problem = "",
    string ExpectedBehavior = "",
    string ReproductionOrLocation = "",
    string DesiredOutcome = "",
    ImmutableArray<AssistantAttachmentDraft> Attachments = default,
    AssistantAuthorizationScope AuthorizationScope = AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly,
    string? CapabilityId = null);

public sealed record AssistantTaskSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAtUtc,
    string Status);

public enum AssistantTranscriptKind
{
    UserClaim,
    Progress,
    Evidence,
    Result,
    Failure,
}

public sealed record AssistantRequestDraft(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    ImmutableArray<AssistantRequestModSelection> Mods,
    ImmutableArray<AssistantRequestToolSelection> Tools,
    string ClassId,
    string RecipeVersion,
    string VerbatimUserText,
    string DisplayTitle,
    string ExpectedBehavior = "",
    string ReproductionOrLocation = "",
    string DesiredOutcome = "",
    ImmutableArray<AssistantAttachmentDraft> Attachments = default,
    AssistantAuthorizationScope AuthorizationScope = AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly,
    string? CapabilityId = null)
{
    public string Problem => VerbatimUserText;
}

public sealed record AssistantAttachmentDraft(
    string Path,
    string OriginalName,
    string? MediaType = null,
    long? SizeBytes = null);

public sealed record AssistantRequestModSelection(ModId ModId, string ProviderName, string SelectionRole);

public sealed record AssistantRequestToolSelection(ToolId ToolId, string ProviderName, string SelectionSource);

public sealed record AssistantCanonicalRequest(
    string RequestId,
    string EnvelopeSha256,
    DateTimeOffset SubmittedAtUtc,
    AssistantRequestDraft Draft);

public sealed record AssistantReadScope(
    ToolId ToolId,
    string ProviderName,
    string? Adapter,
    string? ObservationMode,
    string Availability,
    ImmutableArray<string> ExactReadPaths);

public sealed record AssistantAuthorizationReview(
    string ReviewId,
    string SubmissionId,
    AssistantCanonicalRequest Request,
    string PlanSha256,
    ImmutableArray<AssistantReadScope> ReadScopes,
    string SemanticBindingSha256,
    bool MutationAuthorized,
    string Statement);

public sealed record AssistantAuthorizationGrant(
    string ReviewId,
    string RequestId,
    string SubmissionId,
    string GrantId,
    string AuthorizationSecret,
    DateTimeOffset ExpiresAtUtc,
    bool MutationAuthorized);

public sealed record AssistantExecutionProgress(
    DateTimeOffset TimestampUtc,
    string Stage,
    string Message);

public sealed record AssistantToolReceipt(
    ToolId ToolId,
    string ProviderName,
    string Availability,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    long DurationMilliseconds,
    int? ExitCode,
    string? Reason,
    string StandardOutput,
    string StandardError,
    string ReceiptSha256,
    ImmutableArray<string> EvidenceJson);

public sealed record AssistantModRole(string Mod, string Role);

public sealed record AssistantCapabilityRequired(
    ImmutableArray<string> CoverageGaps,
    ImmutableArray<string> MissingInputs,
    ImmutableArray<string> MissingCapabilityIds,
    ImmutableArray<AssistantCapabilityGapResolution> Resolutions = default);

public sealed record AssistantCapabilityGapResolution(
    string GapId,
    string RequiredCapabilityId,
    string Status,
    string Route,
    string? SelectedCandidateId,
    bool AutoMayContinue,
    string NextAction,
    string ResolutionSha256);

public enum AssistantInstalledCapabilityStatus
{
    Unresolved,
    Absent,
    Partial,
    Satisfied,
}

public enum AssistantProviderDiscoveryStatus
{
    NotRequested,
    Current,
    Stale,
    AuthenticationRequired,
    Unavailable,
}

public enum AssistantCandidateCompatibilityStatus
{
    Unresolved,
    Compatible,
    PatchRequired,
    Incompatible,
}

public sealed record AssistantInstalledCapabilityProvider(
    string Name,
    string Role,
    string Status,
    ImmutableArray<string> EvidenceIds);

public sealed record AssistantCommunityCapabilityCandidate(
    string Name,
    string Provider,
    string? Uri,
    string? Version,
    AssistantCandidateCompatibilityStatus CompatibilityStatus,
    string CompatibilityDetail,
    ImmutableArray<string> RequiredPatches,
    ImmutableArray<string> EvidenceIds);

/// <summary>
/// Supplemental deterministic coverage evidence for a requested gameplay
/// capability. This is deliberately separate from the four-field diagnosis:
/// provider discovery may identify options, but it cannot diagnose or authorize
/// a repair.
/// </summary>
public sealed record AssistantCapabilityAssessment(
    string CapabilityId,
    string DisplayName,
    AssistantInstalledCapabilityStatus InstalledStatus,
    ImmutableArray<AssistantInstalledCapabilityProvider> InstalledProviders,
    AssistantProviderDiscoveryStatus DiscoveryStatus,
    DateTimeOffset? ObservedAtUtc,
    ImmutableArray<AssistantCommunityCapabilityCandidate> CommunityCandidates,
    ImmutableArray<string> EvidenceIds);

public sealed record AssistantArchiveCandidate(
    string PluginName,
    string ProviderName,
    string ArchiveLeaf,
    string CandidateRole,
    string Status,
    string CompatibilityStatus,
    ImmutableArray<string> RequiredEvidence,
    string EvidenceId);

public sealed record AssistantRecoveryAcquisitionAction(
    string ActionId,
    ImmutableArray<string> ModNames,
    ImmutableArray<string> InstalledVersionClaims,
    string ExpectedArchiveLeaf,
    string OfficialFilesUri,
    ImmutableArray<string> AffectedPlugins,
    long MissingDependencyCount,
    string Instruction,
    string CompletionEvidence);

public sealed record AssistantRecoveryLineageRequirement(
    string PluginName,
    string CurrentOverrideProvider,
    int MissingDependencyCount,
    ImmutableArray<string> RequiredFileExamples,
    int OmittedRequiredFileCount,
    string Status,
    string NextAction);

public sealed record AssistantRecoveryCandidateAction(
    string PluginName,
    string ProviderName,
    string ArchiveLeaf,
    string CandidateRole,
    string CompatibilityStatus,
    string EvidenceId);

public sealed record AssistantRecoveryActionManifest(
    string ManifestId,
    string ManifestSha256,
    string PlanId,
    string PlanSha256,
    string Status,
    int AffectedModCount,
    int AffectedPluginCount,
    long MissingDependencyCount,
    int ExactArchiveAcquisitionCount,
    int LineageEvidenceRequiredCount,
    int VersionChangingCandidateCount,
    int ExactRestorationCandidateCount,
    int ProvenUpdateRequiredCount,
    int ProvenReinstallationRequiredCount,
    int PlannedPatchChangeCount,
    int RepairReadyCount,
    bool MutationAuthorized,
    ImmutableArray<AssistantRecoveryAcquisitionAction> ManualAcquisitions,
    ImmutableArray<AssistantRecoveryLineageRequirement> LineageRequirements,
    ImmutableArray<AssistantRecoveryCandidateAction> VersionChangingCandidates,
    ImmutableArray<AssistantRecoveryCandidateAction> ExactRestorationCandidates,
    ImmutableArray<string> AffectedMods,
    ImmutableArray<string> AffectedPlugins,
    ImmutableArray<string> GridFollowUpActions,
    ImmutableArray<string> PlannedPatchChanges,
    ImmutableArray<string> Classifications);

public sealed record AssistantRepairAvailability(
    string? SpecificationId,
    string? SpecificationSha256,
    bool HasExactSpecification,
    bool HasVerifiedRollbackReceipt,
    string Detail,
    bool HasRecoveryPlan = false,
    int ManualAcquisitionReadyCount = 0,
    string? NextManualAcquisitionUri = null,
    string? NextExpectedArchiveLeaf = null,
    string? HistoryState = null,
    string? RepairKind = null,
    InstallationId? InstallationId = null,
    ProfileId? ProfileId = null,
    ImmutableArray<AssistantArchiveCandidate> ArchiveCandidates = default,
    AssistantRecoveryActionManifest? HumanActionManifest = null);

public sealed record AssistantCaseActionState(
    AssistantCaseAction Action,
    bool IsVisible,
    bool IsEnabled,
    bool RequiresApproval,
    string Detail);

public sealed record AssistantDeterministicFinding(
    ImmutableArray<string> AffectedMods,
    ImmutableArray<AssistantModRole> ModRoles,
    string Finding,
    string Solution,
    ImmutableArray<ToolId> EvidenceToolIds,
    string Confidence = "Not evaluated",
    ImmutableArray<string> Evidence = default,
    AssistantCapabilityRequired? CapabilityRequired = null,
    AssistantCapabilityAssessment? CapabilityAssessment = null);

public sealed record AssistantExecutionResult(
    string TaskId,
    string CaseId,
    string TerminalState,
    AssistantDeterministicFinding Finding,
    ImmutableArray<AssistantToolReceipt> Receipts,
    DateTimeOffset CompletedAtUtc,
    ImmutableArray<AssistantCaseActionState> ActionStates = default,
    AssistantRepairAvailability? RepairAvailability = null);

public sealed record AssistantTranscriptEntry(
    DateTimeOffset TimestampUtc,
    AssistantTranscriptKind Kind,
    string Text);

public sealed record AssistantTaskRecord(
    AssistantTaskSummary Summary,
    AssistantCanonicalRequest? Request,
    string? CaseId,
    string TerminalState,
    ImmutableArray<AssistantTranscriptEntry> Transcript,
    ImmutableArray<AssistantToolReceipt> Receipts,
    AssistantDeterministicFinding? Finding,
    bool IsResumable,
    ImmutableArray<AssistantCaseActionState> ActionStates = default,
    AssistantRepairAvailability? RepairAvailability = null);

public sealed record AssistantSessionSnapshot(
    bool IsExpanded,
    double RequestedPanelWidth,
    AssistantOperatingMode Mode,
    AssistantProviderAvailability ProviderAvailability,
    AssistantLifecycleStage LifecycleStage,
    ApplicationContextSnapshot Context,
    string BoundaryStatement,
    AssistantSurface Surface,
    bool IsFormVisible,
    bool IsFullScreen,
    AssistantDraftSnapshot Draft,
    ImmutableArray<AssistantGameOption> Games,
    ImmutableArray<AssistantModOption> Mods,
    ImmutableArray<AssistantToolOption> Tools,
    ImmutableArray<AssistantClassOption> Classes,
    ImmutableArray<AssistantTaskSummary> Tasks,
    AssistantAuthorizationReview? PendingAuthorization,
    AssistantTaskRecord? ActiveTask,
    string? ExecutionError,
    ImmutableArray<AssistantInstallationOption> Installations = default,
    ImmutableArray<AssistantProfileOption> Profiles = default);
