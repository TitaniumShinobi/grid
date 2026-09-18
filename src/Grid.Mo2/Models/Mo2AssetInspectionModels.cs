using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2AssetKind
{
    Nif,
    Dds,
    Psc,
    Pex,
}

public enum Mo2AssetInspectionStatus
{
    Complete,
    Missing,
    Inaccessible,
    Unsupported,
    Malformed,
    Oversized,
    ChangedDuringRead,
    DigestMismatch,
    Cancelled,
}

public enum Mo2AssetByteOrigin
{
    LooseFile,
    BsaMember,
}

public sealed record Mo2AssetInspectionLimits(
    int MaximumTargets = 4_096,
    long MaximumSourceBytes = 256L * 1024 * 1024,
    long MaximumAggregateBytes = 8L * 1024 * 1024 * 1024,
    long MaximumArchiveIndexBytes = 64L * 1024 * 1024,
    int MaximumArchiveMembers = 2_000_000,
    long MaximumArchiveBytesHashed = 2L * 1024 * 1024 * 1024,
    int BufferBytes = 64 * 1024,
    int MaximumNifBlocks = 65_536,
    int MaximumNifStrings = 262_144,
    int MaximumNifStringBytes = 4_096,
    int MaximumNifTexturePaths = 4_096)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTargets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveIndexBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveMembers);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumArchiveBytesHashed);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNifBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNifStrings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNifStringBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNifTexturePaths);
        if (MaximumAggregateBytes < MaximumSourceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumAggregateBytes));
        }
    }
}

public sealed record Mo2AssetProviderEvidence(
    string SourceName,
    string ProviderKind,
    string? ModId,
    string? ArchiveId,
    int Precedence,
    bool IsWinner,
    string WinnerConfidence,
    string ResolutionReason,
    ImmutableArray<string> EvidenceIds);

public sealed record Mo2AssetTarget(
    string VirtualPath,
    Mo2AssetKind Kind,
    string CanonicalSourcePath,
    string? ArchiveMemberPath,
    Mo2AssetProviderEvidence Provider,
    string? ExpectedSha256 = null);

public sealed record Mo2AssetInspectionRequest(
    ImmutableArray<Mo2AssetTarget> Targets,
    Mo2AssetInspectionLimits Limits);

public sealed record Mo2AssetIssue(string Code, string Detail);

public sealed record Mo2NifInspection(
    Mo2AssetInspectionStatus Status,
    string? Header,
    uint? Version,
    byte? Endian,
    uint? UserVersion,
    uint? BethesdaVersion,
    int? BlockCount,
    int? StringCount,
    ImmutableArray<string> TexturePaths,
    ImmutableArray<Mo2AssetIssue> Issues);

public sealed record Mo2DdsInspection(
    Mo2AssetInspectionStatus Status,
    uint? Width,
    uint? Height,
    uint? MipCount,
    string? Format,
    long? ExpectedPayloadBytes,
    long? ObservedPayloadBytes,
    ImmutableArray<Mo2AssetIssue> Issues);

public sealed record Mo2BsaMemberReadResult(
    Mo2AssetInspectionStatus Status,
    string ArchivePath,
    string ExactMemberPath,
    long ArchiveLength,
    string? ArchiveSha256,
    string? MemberSha256,
    int? SourceOrder,
    long? PackedBytes,
    long? UnpackedBytes,
    bool? Compressed,
    ImmutableArray<byte> Content,
    Mo2RandomAccessStamp? Before,
    Mo2RandomAccessStamp? After,
    ImmutableArray<Mo2AssetIssue> Issues);

public sealed record Mo2AssetTargetInspection(
    string VirtualPath,
    Mo2AssetKind Kind,
    Mo2AssetByteOrigin Origin,
    Mo2AssetInspectionStatus Status,
    string SourcePath,
    string? ArchiveMemberPath,
    long? Length,
    string? Sha256,
    Mo2RandomAccessStamp? Before,
    Mo2RandomAccessStamp? After,
    Mo2AssetProviderEvidence Provider,
    Mo2NifInspection? Nif,
    Mo2DdsInspection? Dds,
    Mo2PapyrusInspection? Papyrus,
    ImmutableArray<Mo2AssetIssue> Issues);

public sealed record Mo2AssetInspectionResult(
    Mo2AssetInspectionStatus Status,
    long InspectedBytes,
    ImmutableArray<Mo2AssetTargetInspection> Targets,
    ImmutableArray<Mo2AssetIssue> Issues);

public sealed record Mo2NifTextureDependencyLimits(
    int MaximumMeshes = 65_536,
    int MaximumTextureReferences = 2_000_000,
    int MaximumIssues = 10_000,
    long MaximumSourceBytes = 256L * 1024 * 1024,
    long MaximumAggregateBytes = 8L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMeshes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTextureReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIssues);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateBytes);
        if (MaximumAggregateBytes < MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(MaximumAggregateBytes));
    }
}

public sealed record Mo2NifTextureDependencyRequest(
    Mo2ResolvedBaselineSnapshot Baseline,
    ImmutableArray<Mo2PluginAssetReference> PluginAssetReferences,
    Mo2NifTextureDependencyLimits Limits);

public sealed record Mo2NifTextureDependencyIssue(
    string Code,
    string Detail,
    string? VirtualPath = null);

public sealed record Mo2NifTextureDependencyResult(
    Mo2PluginScriptDependencyStatus Status,
    int MeshesReferenced,
    int MeshesInspected,
    int TextureReferences,
    ImmutableArray<Mo2PluginAssetReference> References,
    ImmutableArray<Mo2MissingPluginAssetDependency> Missing,
    ImmutableArray<Mo2NifTextureDependencyIssue> Issues,
    string SemanticFingerprint);
