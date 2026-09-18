using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum ResolvedEnvironmentStatus
{
    Complete,
    Partial,
    Stale,
    AuthorizationRequired,
    Unavailable,
    Failed,
}

public enum ResolvedEnvironmentRefreshStatus
{
    Completed,
    Partial,
    AuthorizationRequired,
    Unavailable,
    Failed,
    Canceled,
}

public enum EnvironmentObservationStage
{
    Validating,
    ObservingLooseFiles,
    ParsingPlugins,
    ParsingArchives,
    ResolvingProviders,
    Publishing,
}

public enum EnvironmentDiscrepancySeverity
{
    Information,
    Warning,
    Error,
}

public enum EnvironmentDiscrepancyKind
{
    MissingEvidence,
    ConflictingEvidence,
    UnsupportedFormat,
    MalformedInput,
    InaccessibleInput,
    ChangedDuringRead,
    SafetyLimitExceeded,
    ResolutionUncertain,
}

public enum PluginFileExtension
{
    Unknown,
    Esm,
    Esp,
    Esl,
}

public enum PluginFileAvailability
{
    Present,
    Missing,
    Inaccessible,
    Malformed,
    Oversized,
    ChangedDuringRead,
    Unknown,
}

public enum PluginActivationProvenance
{
    PluginsFileMarker,
    CoreGameImplicit,
    CreationManifestImplicit,
    NotListed,
    Unknown,
}

public enum PluginOrderProvenance
{
    LoadOrderFile,
    DiscoveredUnordered,
    CoreGameImplicit,
    Unknown,
}

public enum PluginMasterStatus
{
    PresentEnabled,
    PresentDisabled,
    Missing,
    Unknown,
}

public sealed record PluginMasterReference(
    string Name,
    int SourceOrder,
    PluginMasterStatus Status,
    PluginId? ResolvedPluginId);

public sealed record PluginObservation(
    int? SourcePriority,
    PluginFileExtension Extension,
    bool? HasMasterFlag,
    bool? HasLightFlag,
    PluginActivationProvenance ActivationProvenance,
    PluginOrderProvenance OrderProvenance,
    PluginFileAvailability FileAvailability,
    string? SourceProvider,
    string Fingerprint,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<PluginMasterReference> Masters,
    ImmutableArray<string> Warnings);

public enum ArchiveFormat
{
    Bsa,
    Ba2,
    Unknown,
}

public enum ArchiveSupportStatus
{
    Supported,
    UnsupportedVersion,
    UnsupportedFormat,
    Malformed,
    Inaccessible,
    ChangedDuringRead,
}

public enum ArchiveActivationState
{
    Active,
    Inactive,
    Uncertain,
}

public enum ArchiveActivationProvenance
{
    IniResourceList,
    EnabledPluginAssociation,
    NoActivationEvidence,
    ConflictingEvidence,
    Unknown,
}

public sealed record ResolvedArchiveEntry(
    ArchiveId Id,
    string Name,
    ArchiveFormat Format,
    int? FormatVersion,
    ArchiveSupportStatus SupportStatus,
    ArchiveActivationState Activation,
    ArchiveActivationProvenance ActivationProvenance,
    PluginId? AssociatedPluginId,
    string SourceProvider,
    long? MemberCount,
    string Fingerprint,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<string> Warnings);

public enum VirtualProviderKind
{
    BaseGameLooseFile,
    ModLooseFile,
    OverwriteLooseFile,
    ArchiveMember,
}

public enum ProviderWinnerConfidence
{
    Established,
    Uncertain,
    Unsupported,
    Unavailable,
}

public sealed record VirtualFileProvider(
    ProviderId Id,
    VirtualProviderKind Kind,
    string SourceName,
    ModId? ModId,
    ArchiveId? ArchiveId,
    int Precedence,
    bool IsWinner,
    string Reason,
    string Fingerprint,
    ImmutableArray<string> Warnings);

public sealed record VirtualDataEntry(
    VirtualPathId Id,
    string VirtualPath,
    int ProviderCount,
    ProviderId? WinningProviderId,
    string? WinningProviderName,
    ProviderWinnerConfidence WinnerConfidence,
    int DiscrepancyCount);

public sealed record ProviderChain(
    ResolvedSnapshotId SnapshotId,
    VirtualPathId VirtualPathId,
    string VirtualPath,
    ImmutableArray<VirtualFileProvider> Providers,
    ProviderId? WinningProviderId,
    ProviderWinnerConfidence WinnerConfidence,
    string ResolutionDetail,
    ImmutableArray<EnvironmentDiscrepancy> Discrepancies);

public sealed record EnvironmentDiscrepancy(
    EnvironmentDiscrepancyId Id,
    EnvironmentDiscrepancyKind Kind,
    EnvironmentDiscrepancySeverity Severity,
    string Title,
    string Detail);

public sealed record ResolvedEnvironmentProgress(
    EnvironmentObservationStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Status)
{
    public double? Fraction => TotalUnits is > 0
        ? Math.Clamp((double)CompletedUnits / TotalUnits.Value, 0, 1)
        : null;
}

public readonly record struct WorkspaceEnvironmentContext(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    string CatalogRevision,
    string? InventoryFingerprint);

public sealed record ResolvedEnvironmentSummary(
    ResolvedEnvironmentStatus Status,
    DateTimeOffset ObservedAtUtc,
    string Fingerprint,
    int PluginCount,
    int ArchiveCount,
    long VirtualPathCount,
    long ProviderCount,
    int DiscrepancyCount);

public sealed record ResolvedEnvironmentSnapshot(
    ResolvedSnapshotId Id,
    WorkspaceEnvironmentContext Context,
    ResolvedEnvironmentSummary Summary,
    ImmutableArray<EnvironmentDiscrepancy> Discrepancies,
    bool IsFromSessionCache);

public sealed record ResolvedEnvironmentRefreshResult(
    ResolvedEnvironmentRefreshStatus Status,
    ResolvedEnvironmentSnapshot? Snapshot,
    string Detail,
    ImmutableArray<string> RequiredAuthorizations)
{
    public bool HasCurrentSnapshot =>
        Status is ResolvedEnvironmentRefreshStatus.Completed or ResolvedEnvironmentRefreshStatus.Partial &&
        Snapshot is not null;
}

public sealed record EnvironmentPage<T>(
    ImmutableArray<T> Items,
    int Offset,
    int TotalCount,
    int PageSize,
    ResolvedSnapshotId SnapshotId)
{
    public bool HasMore => Offset + Items.Length < TotalCount;
}

public readonly record struct PluginQuery(
    string SearchText,
    bool? Enabled,
    PluginFileExtension? Extension,
    PluginMasterStatus? MasterStatus,
    int Offset = 0,
    int PageSize = 200)
{
    public static PluginQuery Default { get; } = new(string.Empty, null, null, null);
}

public readonly record struct ArchiveQuery(
    string SearchText,
    ArchiveActivationState? Activation,
    ArchiveSupportStatus? SupportStatus,
    int Offset = 0,
    int PageSize = 200)
{
    public static ArchiveQuery Default { get; } = new(string.Empty, null, null);
}

public readonly record struct VirtualDataQuery(
    string SearchText,
    bool ConflictsOnly,
    ProviderWinnerConfidence? WinnerConfidence,
    int Offset = 0,
    int PageSize = 200)
{
    public static VirtualDataQuery Default { get; } = new(string.Empty, false, null);
}

public enum ResolvedSelectionKind
{
    Plugin,
    Archive,
    VirtualPath,
}

public sealed record ResolvedEnvironmentSelection(
    ResolvedSelectionKind Kind,
    string StableId,
    string Name,
    string Detail);
