using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum ExternalObservationStatus
{
    Complete,
    Partial,
    Stale,
    AuthorizationRequired,
    Unavailable,
    Failed,
}

public enum ExternalObservationRefreshStatus
{
    Completed,
    Partial,
    AuthorizationRequired,
    Unavailable,
    Failed,
    Canceled,
}

public enum ExternalObservationStage
{
    Validating,
    ReadingConfiguration,
    ValidatingExecutables,
    ObservingOutputs,
    ComparingSnapshots,
    Publishing,
}

public enum ObservedExecutableAvailability
{
    Available,
    Missing,
    Inaccessible,
    OutsideExpectedRoots,
    Duplicate,
    Ambiguous,
    Unsupported,
}

public enum ObservedLocationTrust
{
    ExpectedRoot,
    SessionAuthorized,
    OutsideExpectedRoots,
    Unknown,
}

public enum ObservedToolFamily
{
    Unknown,
    Skse,
    SkyrimLauncher,
    SseEdit,
    ZEdit,
    Synthesis,
    Pandora,
    Loot,
    BodySlide,
    TexGen,
    DynDoLod,
    XLodGen,
    WryeBash,
    CreationKit,
}

public enum EvidenceConfidence
{
    None,
    Candidate,
    Corroborated,
    Confirmed,
    Ambiguous,
}

public enum ObservedIconAvailability
{
    NotConfigured,
    Available,
    Missing,
    Inaccessible,
    Unsupported,
    Rejected,
}

public enum ExternalObservationChange
{
    FirstObservation,
    Added,
    Removed,
    Unchanged,
    NoChangeObserved,
    Modified,
    Reclassified,
    AvailabilityChanged,
    AssociationChanged,
    Indeterminate,
}

public enum GeneratedOutputKind
{
    Overwrite,
    BodySlide,
    BehaviorGeneration,
    Synthesis,
    WryeBash,
    TexGen,
    DynDoLod,
    XLodGen,
    UserDesignated,
    Unknown,
}

public enum GeneratedOutputLocationKind
{
    Overwrite,
    InventoryMod,
}

public enum GeneratedOutputAvailability
{
    Available,
    Missing,
    Inaccessible,
    Ambiguous,
    Inconsistent,
    AuthorizationRequired,
    Unsupported,
}

public enum GeneratedOutputEnabledState
{
    Enabled,
    Disabled,
    NotApplicable,
    Unknown,
}

public enum OutputFingerprintStrength
{
    ContentComplete,
    Structural,
    Partial,
    Indeterminate,
}

public sealed record ObservedExecutableSummary(
    ObservedExecutableId Id,
    string Title,
    ObservedExecutableAvailability Availability,
    ObservedLocationTrust LocationTrust,
    ObservedToolFamily RecognizedFamily,
    EvidenceConfidence RecognitionConfidence,
    ObservedIconAvailability IconAvailability,
    int SourceOrder,
    int WarningCount,
    string Fingerprint,
    ExternalObservationChange Change);

public sealed record GeneratedOutputSummary(
    GeneratedOutputId Id,
    string Name,
    GeneratedOutputKind Kind,
    GeneratedOutputLocationKind LocationKind,
    GeneratedOutputAvailability Availability,
    GeneratedOutputEnabledState EnabledState,
    EvidenceConfidence AssociationConfidence,
    OutputFingerprintStrength FingerprintStrength,
    long FileCount,
    long DirectoryCount,
    long TotalBytes,
    int WarningCount,
    string Fingerprint,
    ExternalObservationChange Change,
    ObservedExecutableId? AssociatedExecutableId,
    ModId? ModId);

public sealed record ToolOutputObservationSummary(
    ExternalObservationStatus Status,
    DateTimeOffset ObservedAtUtc,
    string Fingerprint,
    int ExecutableCount,
    int OutputCount,
    int WarningCount,
    bool HasPreviousObservation);

public readonly record struct WorkspaceToolOutputContext(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    InstallationReferenceId ReferenceId,
    string CatalogRevision,
    string? ProfileSnapshotFingerprint,
    string? InventoryFingerprint);

public sealed record ExternalObservationProgress(
    ExternalObservationStage Stage,
    long CompletedUnits,
    long? TotalUnits,
    string Status)
{
    public double? Fraction => TotalUnits is > 0
        ? Math.Clamp((double)CompletedUnits / TotalUnits.Value, 0, 1)
        : null;
}

public sealed record ToolOutputObservationSnapshot(
    ExternalObservationSnapshotId Id,
    WorkspaceToolOutputContext Context,
    ToolOutputObservationSummary Summary,
    ImmutableArray<ObservedExecutableSummary> Executables,
    ImmutableArray<GeneratedOutputSummary> Outputs,
    ImmutableArray<string> Warnings,
    bool IsFromSessionCache);

public sealed record ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus Status,
    ToolOutputObservationSnapshot? Snapshot,
    string Detail,
    ImmutableArray<string> RequiredAuthorizations)
{
    public bool HasCurrentSnapshot =>
        Snapshot is not null &&
        Status is ExternalObservationRefreshStatus.Completed or ExternalObservationRefreshStatus.Partial;
}
