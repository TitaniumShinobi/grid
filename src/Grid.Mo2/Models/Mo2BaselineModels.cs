using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2BaselineStatus
{
    Completed,
    Partial,
    AuthorizationRequired,
    ContextUnavailable,
    Unavailable,
    Failed,
    Canceled,
}

public enum Mo2BaselineHashStatus
{
    Complete,
    PerFileLimitExceeded,
    AggregateLimitExceeded,
    Inaccessible,
    ChangedDuringRead,
}

public enum Mo2BaselineFileKind
{
    Plugin,
    Archive,
    LooseFile,
    DiagnosticOutput,
}

public enum Mo2BaselineSourceArtifactKind
{
    InstallationArchive,
    MetadataSidecar,
}

public sealed record Mo2BaselineLimits(
    long MaximumEntries = 2_000_000,
    long MaximumTotalHashBytes = 1_099_511_627_776L,
    long MaximumFileBytes = 0,
    int CheckpointInterval = 100_000,
    long MaximumRecordCatalogEntries = 10_000_000)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTotalHashBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CheckpointInterval);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordCatalogEntries);
    }
}

public sealed record Mo2BaselineRequest(
    string ApplicationDirectory,
    string InstanceDirectory,
    string ProfileName,
    ImmutableArray<string> AuthorizedPaths,
    ImmutableArray<string> ExplicitProviderSeeds,
    ImmutableArray<string> ExplicitPluginSeeds,
    Mo2BaselineLimits Limits,
    ImmutableDictionary<string, Mo2BaselineFileHashRecord>? ResumeHashes = null)
{
    public ImmutableArray<string> EffectiveAuthorizedPaths =>
        AuthorizedPaths.IsDefault ? [] : AuthorizedPaths;

    public ImmutableArray<string> EffectiveProviderSeeds =>
        ExplicitProviderSeeds.IsDefault ? [] : ExplicitProviderSeeds;

    public ImmutableArray<string> EffectivePluginSeeds =>
        ExplicitPluginSeeds.IsDefault ? [] : ExplicitPluginSeeds;

    public ImmutableDictionary<string, Mo2BaselineFileHashRecord> EffectiveResumeHashes =>
        ResumeHashes ?? ImmutableDictionary<string, Mo2BaselineFileHashRecord>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
}

public sealed record Mo2BaselineEvent(string RecordType, object Payload);

public sealed record Mo2BaselineRootRecord(
    string Label,
    string Path,
    Mo2PathState State,
    bool Authorized);

public sealed record Mo2BaselineProtectedSnapshotRecord(
    string Kind,
    string Path,
    Mo2PathState State,
    string Policy);

public sealed record Mo2BaselineProfileRecord(
    string ProfileId,
    string Name,
    string ManagerState,
    string ObservationStatus,
    string Fingerprint);

public sealed record Mo2BaselineProfileSourceRecord(
    string Name,
    string Availability,
    string ParseStatus,
    string? Path,
    string? Sha256,
    int WarningCount);

public sealed record Mo2BaselineModRecord(
    string ModId,
    string Name,
    bool Enabled,
    int? Mo2Priority,
    string Reconciliation,
    string? CanonicalPath,
    string? MetadataSha256,
    string MetadataAvailability,
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
    ImmutableArray<Mo2RawIniValue> RawValues,
    int WarningCount);

public sealed record Mo2BaselineSourceArtifactRecord(
    string ModId,
    string ModName,
    string DeclaredInstallationFile,
    string Relationship,
    Mo2BaselineSourceArtifactKind Kind,
    string ExactLeafName,
    string? CanonicalPath,
    Mo2PathState State,
    long? Length,
    long? LastWriteTimeUtcTicks,
    Mo2BaselineHashStatus? HashStatus,
    string? Sha256,
    string? Detail,
    Mo2BaselineSourceProviderIdentity? ProviderIdentity = null);

public sealed record Mo2BaselineSourceProviderIdentity(
    string? Repository,
    string? GameName,
    long? ModId,
    long? FileId,
    string? Version,
    string ArchiveLeaf,
    string Status);

public sealed record Mo2BaselinePluginRecord(
    PluginEntry Plugin);

public sealed record Mo2BaselinePluginMasterRecord(
    string PluginId,
    string PluginName,
    PluginMasterReference Master);

public sealed record Mo2BaselinePluginScriptProgressRecord(
    int PluginIndex,
    int TotalPlugins,
    string PluginName,
    string Stage,
    long PluginsScanned,
    long RecordHeadersExamined,
    long BytesScanned);

public sealed record Mo2BaselinePluginScriptInspectionRecord(
    Mo2PluginScriptDependencyStatus Status,
    long PluginsScanned,
    long RecordHeadersExamined,
    long BytesScanned,
    int ScriptReferences,
    int MissingDependencies,
    int IssueCount,
    string SemanticFingerprint,
    int RecordConflictGroups = 0,
    long RecordConflictRecords = 0,
    int RecordProvenanceGroups = 0,
    long RecordProvenanceRecords = 0,
    long RecordCatalogEntries = 0);

public sealed record Mo2BaselinePluginScriptDependencyRecord(
    string PluginName,
    string? SourceProvider,
    string ScriptName,
    string RequiredVirtualPath,
    int ReferenceCount,
    ImmutableArray<Mo2PluginScriptReference> Samples);

public sealed record Mo2BaselinePluginAssetInspectionRecord(
    Mo2PluginScriptDependencyStatus Status,
    long PluginsScanned,
    long RecordHeadersExamined,
    long BytesScanned,
    int AssetReferences,
    int MissingDependencies,
    int IssueCount,
    string SemanticFingerprint,
    int NifMeshesInspected = 0,
    int EmbeddedTextureReferences = 0,
    Mo2PluginScriptDependencyStatus? DirectRecordStatus = null,
    Mo2PluginScriptDependencyStatus? NifTextureStatus = null);

public sealed record Mo2BaselinePluginAssetDependencyRecord(
    string PluginName,
    string? SourceProvider,
    string RequiredVirtualPath,
    Mo2PluginAssetReferenceKind Kind,
    int ReferenceCount,
    ImmutableArray<Mo2PluginAssetReference> Samples);

public sealed record Mo2BaselineSpidInspectionRecord(
    Mo2SpidDistributionStatus Status,
    int DocumentsScanned,
    long LinesScanned,
    long RulesScanned,
    int SourcePluginReferences,
    int DisabledSourceReferences,
    int MissingSourceReferences,
    int IssueCount,
    string SemanticFingerprint);

public sealed record Mo2BaselineSpidSourceIssueRecord(
    string VirtualPath,
    string ProviderName,
    string PluginName,
    Mo2SpidPluginReferenceStatus Status,
    int RuleCount,
    ImmutableArray<int> SampleLines);

public sealed record Mo2BaselineArchiveRecord(
    ResolvedArchiveEntry Archive);

public sealed record Mo2BaselineVirtualProviderRecord(
    string VirtualPath,
    ProviderWinnerConfidence WinnerConfidence,
    ProviderId? WinningProviderId,
    VirtualFileProvider Provider);

public sealed record Mo2BaselineGateRecord(
    string Label,
    string? ExactPath,
    string Code,
    string Detail);

public sealed record Mo2BaselineCheckpointRecord(
    string Stage,
    long Completed,
    long? Total,
    long HashedBytes);

public sealed record Mo2BaselinePageRecord(
    string Stage,
    long PageNumber,
    long FirstOrdinal,
    long LastOrdinal,
    long? Total);

public sealed record Mo2BaselinePhysicalFile(
    string VirtualPath,
    string CanonicalPath,
    VirtualProviderKind ProviderKind,
    string ProviderName,
    int Precedence,
    long Length,
    long LastWriteTimeUtcTicks);

public sealed record Mo2BaselineFileHashRecord(
    string VirtualPath,
    string CanonicalPath,
    Mo2BaselineFileKind Kind,
    string ProviderName,
    long Length,
    long LastWriteTimeUtcTicks,
    Mo2BaselineHashStatus Status,
    string? Sha256,
    string? Detail);

public sealed record Mo2ResolvedBaselineSnapshot(
    ResolvedEnvironmentSnapshot Snapshot,
    ImmutableArray<PluginEntry> Plugins,
    ImmutableArray<ResolvedArchiveEntry> Archives,
    ImmutableArray<ProviderChain> ProviderChains,
    ImmutableArray<Mo2BaselinePhysicalFile> PhysicalFiles);

public sealed record Mo2BaselineSummaryRecord(
    Mo2BaselineStatus Status,
    string? InstallationId,
    string? ProfileId,
    string? ConnectionFingerprint,
    string? ProfileFingerprint,
    string? InventoryFingerprint,
    string? EnvironmentFingerprint,
    long Mods,
    long Plugins,
    long Archives,
    long VirtualPaths,
    long PhysicalFiles,
    long CompleteHashes,
    long SourceArtifacts,
    long CompleteSourceArtifactHashes,
    long HashedBytes,
    int IssueCount,
    ImmutableArray<string> RequiredAuthorizations);
