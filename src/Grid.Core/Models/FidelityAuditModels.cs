using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum FidelityAuditItemStatus
{
    Pass,
    Warning,
    Failure,
    Unsupported,
    Stale,
}

public enum FidelityAuditArea
{
    Connection,
    ActiveProfile,
    ProfileSources,
    Mods,
    Plugins,
    Archives,
    DataProviders,
    ProfileSettings,
    Saves,
    Executables,
    Overwrite,
    GeneratedOutputs,
    NonMutation,
}

public enum FidelityAuditCriticality
{
    Informational,
    LaunchRelevant,
    LaunchCritical,
}

public enum FidelityAuditDisposition
{
    Satisfied,
    AcknowledgementRequired,
    Blocking,
}

public enum FidelityAuditReadiness
{
    Ready,
    RequiresAcknowledgement,
    Blocked,
    Stale,
}

public enum FidelityAuditRunStatus
{
    Completed,
    Unavailable,
    Failed,
    Canceled,
}

public enum FidelityAuditStage
{
    RevalidatingConnection,
    ComparingProfileSources,
    ComparingInventory,
    ComparingEnvironment,
    ComparingToolsAndOutputs,
    VerifyingNonMutation,
    Publishing,
}

public sealed record FidelityAuditContext(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    InstallationReferenceId ReferenceId,
    string CatalogRevision,
    string ConnectionKey,
    string ProfileSnapshotFingerprint,
    string? InventoryFingerprint,
    ResolvedSnapshotId? EnvironmentSnapshotId,
    string? EnvironmentFingerprint,
    ExternalObservationSnapshotId? ToolOutputSnapshotId,
    string? ToolOutputFingerprint,
    ObservedExecutableId? SelectedExecutableId,
    string? SelectedExecutableFingerprint)
{
    public bool IsCompleteForLaunch =>
        !string.IsNullOrWhiteSpace(CatalogRevision) &&
        !string.IsNullOrWhiteSpace(ConnectionKey) &&
        !string.IsNullOrWhiteSpace(ProfileSnapshotFingerprint) &&
        SelectedExecutableId is not null &&
        !string.IsNullOrWhiteSpace(SelectedExecutableFingerprint);
}

public sealed record FidelityAuditItem(
    string Code,
    FidelityAuditArea Area,
    FidelityAuditItemStatus Status,
    FidelityAuditCriticality Criticality,
    FidelityAuditDisposition Disposition,
    string Title,
    string Detail,
    string? EvidenceFingerprint = null)
{
    public bool IsBlocking => Disposition == FidelityAuditDisposition.Blocking;

    public bool RequiresAcknowledgement =>
        Disposition == FidelityAuditDisposition.AcknowledgementRequired;
}

public sealed record FidelityAuditProgress(
    FidelityAuditStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Status)
{
    public double? Fraction => TotalUnits is > 0
        ? Math.Clamp((double)CompletedUnits / TotalUnits.Value, 0, 1)
        : null;
}

public sealed record FidelityAuditSnapshot(
    FidelityAuditId Id,
    FidelityAuditContext Context,
    DateTimeOffset ObservedAtUtc,
    string Fingerprint,
    FidelityAuditReadiness Readiness,
    ImmutableArray<FidelityAuditItem> Items,
    string Summary)
{
    public bool HasBlockingItems => Items.Any(item => item.IsBlocking);

    public bool RequiresAcknowledgement => Items.Any(item => item.RequiresAcknowledgement);

    public int DiscrepancyCount => Items.Count(item => item.Status != FidelityAuditItemStatus.Pass);
}

public sealed record FidelityAuditResult(
    FidelityAuditRunStatus Status,
    FidelityAuditSnapshot? Snapshot,
    string Detail)
{
    public bool HasCurrentSnapshot =>
        Status == FidelityAuditRunStatus.Completed && Snapshot is not null;
}
