using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2ArchiveInspectionStatus
{
    Complete,
    Extracted,
    Rejected,
    Unsupported,
    Inaccessible,
    ChangedDuringRead,
}

public enum Mo2ArchiveExtractionMode
{
    None,
    FullArchive,
    FomodSelection,
}

public enum Mo2FomodStatus
{
    Absent,
    Valid,
    SelectionRequired,
    Unsupported,
    Malformed,
}

public sealed record Mo2ArchiveInspectionLimits(
    long MaximumArchiveBytes = 1_099_511_627_776L,
    int MaximumEntries = 1_000_000,
    int MaximumDepth = 64,
    int MaximumPathLength = 1_024,
    int MaximumSegmentLength = 255,
    long MaximumExpandedBytes = 17_179_869_184L,
    double MaximumCompressionRatio = 1_000d,
    int MaximumFomodDocumentBytes = 4 * 1024 * 1024,
    int BufferBytes = 64 * 1024)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPathLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSegmentLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumExpandedBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumCompressionRatio, 1d);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFomodDocumentBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferBytes, 4 * 1024);
    }
}

public sealed record Mo2FomodGroupSelection(
    string GroupName,
    ImmutableArray<string> PluginNames)
{
    public ImmutableArray<string> EffectivePluginNames => PluginNames.IsDefault ? [] : PluginNames;
}

public sealed record Mo2ArchiveInspectionRequest(
    string ArchivePath,
    string? ExpectedSha256,
    Mo2ArchiveExtractionMode ExtractionMode,
    string? StagingDirectory,
    ImmutableArray<Mo2FomodGroupSelection> FomodSelections,
    Mo2ArchiveInspectionLimits Limits)
{
    public ImmutableArray<Mo2FomodGroupSelection> EffectiveFomodSelections =>
        FomodSelections.IsDefault ? [] : FomodSelections;
}

public sealed record Mo2ArchiveInspectionIssue(string Code, string Detail, string? EntryPath = null);

public sealed record Mo2ArchiveEntryInspection(
    int Ordinal,
    string ArchivePath,
    string NormalizedPath,
    bool IsDirectory,
    bool IsEncrypted,
    bool IsLink,
    long CompressedSize,
    long ExpandedSize,
    string? Sha256);

public sealed record Mo2FomodInstallerInfo(
    Mo2FomodStatus Status,
    string? Name,
    string? Author,
    string? Version,
    string? Website,
    ImmutableArray<Mo2FomodGroup> Groups,
    ImmutableArray<Mo2FomodGroupSelection> SelectionVector,
    ImmutableArray<Mo2ArchiveInspectionIssue> Issues);

public sealed record Mo2FomodGroup(
    string Name,
    string Type,
    ImmutableArray<string> PluginNames,
    ImmutableArray<Mo2FomodPluginOption> PluginOptions = default)
{
    public ImmutableArray<Mo2FomodPluginOption> EffectivePluginOptions =>
        PluginOptions.IsDefault ? [] : PluginOptions;
}

public sealed record Mo2FomodPluginOption(
    string Name,
    ImmutableArray<Mo2ArchiveInstallMapping> InstallMappings);

public sealed record Mo2InstalledFileEvidence(
    string VirtualPath,
    string ProviderName,
    string Sha256);

public enum Mo2FomodOptionReconciliationStatus
{
    Selected,
    NotSelected,
    Ambiguous,
    NoFileEvidence,
}

public sealed record Mo2FomodOptionReconciliation(
    string Name,
    Mo2FomodOptionReconciliationStatus Status,
    int MappedFileCount,
    int ExactMatchCount,
    int ObservedDifferentCount,
    int AbsentCount);

public sealed record Mo2FomodGroupReconciliation(
    string Name,
    string Type,
    string Status,
    ImmutableArray<string> SelectedPluginNames,
    ImmutableArray<Mo2FomodOptionReconciliation> Options);

public sealed record Mo2FomodSelectionReconciliationResult(
    int SchemaVersion,
    string Status,
    string ArchivePath,
    string? ArchiveSha256,
    ImmutableArray<string> ProviderNames,
    ImmutableArray<Mo2FomodGroupSelection> SelectionVector,
    ImmutableArray<Mo2FomodGroupReconciliation> Groups,
    ImmutableArray<Mo2ArchiveInspectionIssue> Issues,
    string EvidenceFingerprint,
    string EvidenceId,
    string? InstalledPrimaryArchiveSha256 = null);

public sealed record Mo2ArchiveInstallMapping(
    string SourcePath,
    string DestinationPath,
    int Priority,
    string Origin);

public sealed record Mo2ArchiveInspectionResult(
    Mo2ArchiveInspectionStatus Status,
    string ArchivePath,
    long ArchiveLength,
    string? ArchiveSha256,
    string? Format,
    DateTimeOffset? LastWriteTimeBeforeUtc,
    DateTimeOffset? LastWriteTimeAfterUtc,
    ImmutableArray<Mo2ArchiveEntryInspection> Entries,
    Mo2FomodInstallerInfo Fomod,
    ImmutableArray<Mo2ArchiveInstallMapping> InstallMappings,
    string? StagingDirectory,
    ImmutableArray<Mo2ArchiveInspectionIssue> Issues);

public sealed record Mo2ArchiveRequiredFile(
    string PluginName,
    string RequiredVirtualPath);

public sealed record Mo2ArchiveRequiredFileMatch(
    string PluginName,
    string RequiredVirtualPath,
    string ArchiveEntryPath,
    string NormalizedArchivePath,
    string MappingRule,
    long ExpandedSize,
    string Sha256);

public sealed record Mo2ArchiveRequirementMatchResult(
    int SchemaVersion,
    string Status,
    string ArchivePath,
    long ArchiveLength,
    string? ArchiveSha256,
    string? Format,
    int InspectedEntryCount,
    string? EntrySetSha256,
    int RequiredFileCount,
    int MatchedRequiredFileCount,
    int AmbiguousRequiredFileCount,
    ImmutableArray<Mo2ArchiveRequiredFileMatch> Matches,
    Mo2FomodStatus FomodStatus,
    ImmutableArray<Mo2ArchiveInspectionIssue> Issues,
    string? EvidenceId);

public enum Mo2ArchiveCandidateRole
{
    ExactRestoration,
    CompleteUpdateCandidate,
    CompleteRollbackCandidate,
    CompleteAlternativeCandidate,
    IncompleteCandidate,
    Rejected,
}

public enum Mo2ArchiveVersionRelation
{
    Same,
    Newer,
    Older,
    DifferentUnordered,
    Unavailable,
}

public enum Mo2ArchivePayloadPairStatus
{
    Complete,
    PluginOnly,
    ArchiveOnly,
    Missing,
}

public enum Mo2ArchiveMixingPolicy
{
    InstalledPluginCompatibleWithCandidateAssets,
    CandidatePluginAndAssetsRequiredTogether,
    CandidateUnusable,
}

public enum Mo2BundledPluginDisposition
{
    AlreadyMatchesCandidate,
    ReplacementRequired,
    NotBundled,
}

public enum Mo2ArchiveCandidateCompatibilityStatus
{
    NotRequiredForExactRestoration,
    RequiresDependentCompatibilityEvidence,
    CandidateIncomplete,
    CandidateRejected,
}

public sealed record Mo2InstalledDependentPluginEvidence(
    string Name,
    string Sha256,
    string? CandidateEntryPath);

public sealed record Mo2ArchiveCandidateAssessmentRequest(
    Mo2ArchiveInspectionResult Inspection,
    string PrimaryPluginName,
    string? PrimaryPluginEntryPath,
    string? PrimaryArchiveEntryPath,
    string InstalledPrimaryPluginSha256,
    string? InstalledVersion,
    string? CandidateVersion,
    ImmutableArray<Mo2InstalledDependentPluginEvidence> DependentPlugins,
    string? InstalledPrimaryArchiveSha256 = null);

public sealed record Mo2BundledPluginAssessment(
    string Name,
    string InstalledSha256,
    string? CandidateEntryPath,
    string? CandidateSha256,
    Mo2BundledPluginDisposition Disposition);

public sealed record Mo2ArchiveCandidateAssessmentResult(
    int SchemaVersion,
    string Status,
    string ArchivePath,
    string? ArchiveSha256,
    string PrimaryPluginName,
    string InstalledPrimaryPluginSha256,
    string? CandidatePrimaryPluginSha256,
    string? CandidatePrimaryArchiveSha256,
    string? InstalledVersion,
    string? CandidateVersion,
    Mo2ArchiveVersionRelation VersionRelation,
    Mo2ArchivePayloadPairStatus PayloadPairStatus,
    Mo2ArchiveCandidateRole CandidateRole,
    Mo2ArchiveMixingPolicy MixingPolicy,
    Mo2ArchiveCandidateCompatibilityStatus CompatibilityStatus,
    ImmutableArray<Mo2BundledPluginAssessment> DependentPlugins,
    ImmutableArray<string> RequiredEvidence,
    string EvidenceFingerprint,
    string EvidenceId,
    string? CandidatePrimaryPluginEntryPath = null,
    string? CandidatePrimaryArchiveEntryPath = null,
    string? InstalledPrimaryArchiveSha256 = null);
