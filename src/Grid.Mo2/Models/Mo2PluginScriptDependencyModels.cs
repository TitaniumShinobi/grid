using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2PluginScriptDependencyStatus
{
    Complete,
    Partial,
    Refused,
}

public sealed record Mo2PluginScriptDependencyLimits(
    int MaximumPlugins = 8_192,
    long MaximumAggregateBytesScanned = 32L * 1024 * 1024 * 1024,
    long MaximumRecordHeaders = 10_000_000,
    int MaximumRecordDataBytes = 64 * 1024 * 1024,
    int MaximumScriptReferences = 2_000_000,
    int MaximumAssetReferences = 2_000_000,
    int MaximumIssues = 10_000,
    int MaximumRecordConflictGroups = 100_000,
    int MaximumRecordProvenanceGroups = 100_000,
    long MaximumRecordCatalogEntries = 2_000_000)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPlugins);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateBytesScanned);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordHeaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordDataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumScriptReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAssetReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIssues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordConflictGroups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordProvenanceGroups);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordCatalogEntries);
    }
}

public sealed record Mo2PluginScriptDependencyRequest(
    ImmutableArray<Mo2Tes4PluginInput> Plugins,
    ImmutableArray<string> AvailableVirtualPaths,
    Mo2PluginScriptDependencyLimits Limits);

public sealed record Mo2PluginScriptDependencyProgress(
    int PluginIndex,
    int TotalPlugins,
    string PluginName,
    string Stage,
    long PluginsScanned,
    long RecordHeadersExamined,
    long BytesScanned);

public sealed record Mo2PluginScriptReference(
    string PluginName,
    string RecordSignature,
    uint RawFormId,
    long RecordOffset,
    string ScriptName,
    string RequiredVirtualPath);

public sealed record Mo2MissingPluginScriptDependency(
    string PluginName,
    string ScriptName,
    string RequiredVirtualPath,
    int ReferenceCount,
    ImmutableArray<Mo2PluginScriptReference> Samples);

public enum Mo2PluginAssetReferenceKind
{
    Mesh,
    Texture,
}

public sealed record Mo2PluginAssetReference(
    string PluginName,
    string RecordSignature,
    uint RawFormId,
    long RecordOffset,
    string SubrecordSignature,
    string DeclaredPath,
    string RequiredVirtualPath,
    Mo2PluginAssetReferenceKind Kind,
    string? DiscoveredThroughVirtualPath = null);

public sealed record Mo2MissingPluginAssetDependency(
    string PluginName,
    string RequiredVirtualPath,
    Mo2PluginAssetReferenceKind Kind,
    int ReferenceCount,
    ImmutableArray<Mo2PluginAssetReference> Samples);

public sealed record Mo2PluginScriptDependencyIssue(
    string Code,
    string Detail,
    string? PluginName = null,
    long? RecordOffset = null);

public sealed record Mo2PluginRecordConflictSample(
    string OriginPlugin,
    uint LocalFormId,
    uint PreviousRawFormId,
    uint WinnerRawFormId,
    long PreviousRecordOffset,
    long WinnerRecordOffset,
    uint PreviousRecordFlags,
    uint WinnerRecordFlags,
    string? PreviousEditorId,
    string? WinnerEditorId,
    string PreviousDataSha256,
    string WinnerDataSha256);

public sealed record Mo2PluginRecordProvenanceSample(
    string OriginPlugin,
    uint LocalFormId,
    uint RawFormId,
    long RecordOffset,
    uint RecordFlags,
    string? EditorId,
    string DataSha256,
    bool IsOverride);

public sealed record Mo2PluginRecordProvenanceSummary(
    string RecordSignature,
    string PluginName,
    int? LoadOrder,
    int NewRecordCount,
    int OverrideRecordCount,
    int DeletedRecordCount,
    int InitiallyDisabledRecordCount,
    ImmutableArray<Mo2PluginRecordProvenanceSample> Samples);

public sealed record Mo2PluginRecordCatalogEntry(
    string RecordSignature,
    string OriginPlugin,
    uint LocalFormId,
    string PluginName,
    int? LoadOrder,
    uint RawFormId,
    long RecordOffset,
    uint RecordFlags,
    string? EditorId,
    string DataSha256,
    bool IsOverride);

public sealed record Mo2PluginRecordConflictSummary(
    string RecordSignature,
    string PreviousPlugin,
    string WinningPlugin,
    int? PreviousLoadOrder,
    int? WinningLoadOrder,
    int RecordCount,
    int ContentChangedCount,
    int FlagsChangedCount,
    int DeletedWinnerCount,
    int InitiallyDisabledWinnerCount,
    ImmutableArray<Mo2PluginRecordConflictSample> Samples);

public sealed record Mo2PluginScriptDependencyResult(
    Mo2PluginScriptDependencyStatus Status,
    ImmutableArray<Mo2PluginScriptReference> References,
    ImmutableArray<Mo2MissingPluginScriptDependency> Missing,
    ImmutableArray<Mo2PluginScriptDependencyIssue> Issues,
    long PluginsScanned,
    long RecordHeadersExamined,
    long BytesScanned,
    string SemanticFingerprint,
    ImmutableArray<Mo2PluginAssetReference> AssetReferences = default,
    ImmutableArray<Mo2MissingPluginAssetDependency> MissingAssets = default,
    ImmutableArray<Mo2PluginRecordConflictSummary> RecordConflicts = default,
    ImmutableArray<Mo2PluginRecordProvenanceSummary> RecordProvenance = default,
    ImmutableArray<Mo2PluginRecordCatalogEntry> RecordCatalog = default,
    long RecordCatalogEntryCount = 0);
