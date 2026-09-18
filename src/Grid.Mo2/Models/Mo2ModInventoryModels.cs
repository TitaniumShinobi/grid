using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2InventoryObservationStatus
{
    Complete,
    Partial,
    Unavailable,
    Inconsistent,
}

public enum Mo2ModReconciliationState
{
    Matched,
    Separator,
    Foreign,
    Backup,
    Missing,
    Duplicate,
    Ambiguous,
    Unlisted,
    Inaccessible,
    AuthorizationRequired,
    Inconsistent,
}

public enum Mo2MetadataAvailability
{
    Available,
    Missing,
    Inaccessible,
    Oversized,
    Malformed,
    ChangedDuringRead,
    NotApplicable,
}

public sealed record Mo2RawIniValue(
    string Section,
    string Key,
    string RawValue,
    int LineIndex,
    bool IsSupported);

public sealed record Mo2CategoryDefinition(
    int Id,
    string Name,
    int? ParentId,
    string RawLine,
    int LineIndex);

public sealed record Mo2CategoryCatalog(
    ImmutableArray<Mo2CategoryDefinition> Categories,
    ImmutableArray<Mo2ParseWarning> Warnings,
    Mo2ProfileSourceSnapshot Source);

public sealed record Mo2NormalizedModMetadata(
    string? Version,
    string? NewestVersion,
    string? IgnoredVersion,
    ImmutableArray<int> CategoryIds,
    ImmutableArray<string> CategoryNames,
    string? NexusGameName,
    long? NexusModId,
    string? InstallationFile,
    string? Notes,
    string? Comments,
    string? Repository,
    DateTimeOffset? ProviderUpdatedAtUtc,
    string? ProviderStatus,
    ImmutableArray<Mo2RawIniValue> RawValues);

public sealed record Mo2MetaIniSnapshot(
    Mo2MetadataAvailability Availability,
    Mo2NormalizedModMetadata? Metadata,
    Mo2ProfileSourceSnapshot Source,
    ImmutableArray<Mo2ParseWarning> Warnings);

public sealed record Mo2ModDirectoryObservation(
    string Name,
    string CanonicalPath,
    string IdentityKey,
    Mo2PathState State,
    DateTimeOffset? CreatedAtUtc,
    Mo2MetaIniSnapshot Metadata,
    ImmutableArray<Mo2ParseWarning> Warnings);

public sealed record Mo2ReconciledMod(
    ProfileId ProfileId,
    ModId Id,
    string Name,
    Mo2ModListMarker Marker,
    bool IsEnabled,
    int? SourceLineIndex,
    int? SourceOrder,
    int? Mo2Priority,
    Mo2ModReconciliationState Reconciliation,
    Mo2ModDirectoryObservation? Directory,
    ImmutableArray<Mo2ParseWarning> Warnings);

public sealed record Mo2ProfileModInventory(
    ProfileId ProfileId,
    ImmutableArray<Mo2ReconciledMod> Entries,
    Mo2InventoryObservationStatus Status,
    string Fingerprint,
    ImmutableArray<Mo2ParseWarning> Warnings);

public sealed record Mo2ModInventorySnapshot(
    InstallationReferenceId ReferenceId,
    string Revision,
    Mo2InventoryObservationStatus Status,
    string? CanonicalModsRoot,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<Mo2ModDirectoryObservation> Directories,
    ImmutableArray<Mo2ProfileModInventory> Profiles,
    Mo2CategoryCatalog? Categories,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ModInventoryRequest(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    Mo2ProfileSnapshot Profiles);

public sealed record Mo2ModsRootAuthorization(
    InstallationReferenceId ReferenceId,
    string CanonicalModsRoot);
