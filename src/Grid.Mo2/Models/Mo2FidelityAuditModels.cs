using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2FidelityStatus { Pass, Warning, Failure, Unsupported, Stale }
public enum Mo2FidelityArea
{
    Connection, ActiveProfile, ProfileSources, Mods, Plugins, Archives, DataProviders,
    IniAndSaves, Executables, Overwrite, GeneratedOutputs, NonMutationEvidence,
}
public enum Mo2Readiness { Ready, ReviewRequired, Blocked }

public sealed record Mo2FidelityAuditItem(
    string Code,
    Mo2FidelityArea Area,
    Mo2FidelityStatus Status,
    string Summary,
    string Provenance,
    int? ExpectedCount = null,
    int? ObservedCount = null,
    bool LaunchCritical = false);

public sealed record Mo2FidelityAuditContext(
    InstallationReferenceId ReferenceId,
    string ConnectionIdentity,
    string CatalogRevision,
    ProfileId ProfileId,
    string ProfileSnapshotFingerprint,
    ManagerProfileState ProfileState,
    string? InventoryFingerprint,
    ResolvedSnapshotId? EnvironmentSnapshotId,
    string? EnvironmentFingerprint,
    ExternalObservationSnapshotId? ToolOutputSnapshotId,
    string? ToolOutputFingerprint,
    ObservedExecutableId? ExecutableId,
    int? ExecutableSourceIndex,
    string? ExecutableTitle,
    string? ExecutableConfigurationFingerprint,
    string Mo2ExecutableIdentity,
    ImmutableArray<Mo2FidelityAuditItem> Evidence);

public sealed record Mo2FidelityAuditResult(
    Mo2FidelityAuditContext Context,
    string Fingerprint,
    DateTimeOffset ObservedAtUtc,
    Mo2Readiness Readiness,
    ImmutableArray<Mo2FidelityAuditItem> Items,
    int WarningCount,
    int FailureCount,
    int UnsupportedCount);

public sealed record Mo2FidelityAcknowledgement(string AuditFingerprint, DateTimeOffset AcknowledgedAtUtc);
