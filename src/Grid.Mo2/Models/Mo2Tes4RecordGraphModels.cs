using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2Tes4RecordGraphStatus
{
    Complete,
    Partial,
    Refused,
}

public enum Mo2Tes4CellPlacement
{
    None,
    CellChildren,
    Persistent,
    Temporary,
    VisibleDistant,
}

public sealed record Mo2Tes4CanonicalRecordKey(string OriginPlugin, uint LocalFormId);

public sealed record Mo2Tes4PluginInput(
    string Name,
    string CanonicalPath,
    int SourceOrder,
    int? LoadOrder,
    bool IsEnabled);

public sealed record Mo2Tes4SpatialSeed(float X, float Y, float Z);

public sealed record Mo2Tes4CellScopeRequest(
    Mo2Tes4CanonicalRecordKey Worldspace,
    ImmutableArray<Mo2Tes4CanonicalRecordKey> Cells,
    ImmutableArray<Mo2Tes4SpatialSeed> SpatialSeeds,
    float Radius = 4096,
    int MaximumCells = 9,
    int MaximumReferences = 25_000);

public sealed record Mo2Tes4RecordGraphLimits(
    int MaximumPlugins = 2_048,
    long MaximumAggregateBytesScanned = 32L * 1024 * 1024 * 1024,
    long MaximumRecordHeaders = 10_000_000,
    int MaximumTargetKeys = 100_000,
    int MaximumGraphNodes = 10_000,
    int MaximumGraphDepth = 32,
    int MaximumGroupDepth = 64,
    int MaximumOutputRecords = 100_000,
    int MaximumRecordDataBytes = 64 * 1024 * 1024,
    int BufferBytes = 64 * 1024)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPlugins);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateBytesScanned);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordHeaders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTargetKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGraphNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGraphDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumGroupDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumOutputRecords);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumRecordDataBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BufferBytes);
    }
}

public sealed record Mo2Tes4RecordGraphRequest(
    ImmutableArray<Mo2Tes4PluginInput> Plugins,
    ImmutableArray<Mo2Tes4CanonicalRecordKey> Targets,
    Mo2Tes4RecordGraphLimits Limits,
    Mo2Tes4CellScopeRequest? CellScope = null);

public sealed record Mo2Tes4GroupAncestry(
    int GroupType,
    uint RawLabel,
    Mo2Tes4CanonicalRecordKey? CanonicalLabel,
    long Offset,
    long EndOffset);

public sealed record Mo2Tes4Transform(
    float X,
    float Y,
    float Z,
    float RotationX,
    float RotationY,
    float RotationZ);

public sealed record Mo2Tes4EnableParent(
    Mo2Tes4CanonicalRecordKey Parent,
    uint Flags);

public sealed record Mo2Tes4LinkedReference(
    Mo2Tes4CanonicalRecordKey Reference,
    Mo2Tes4CanonicalRecordKey? Keyword);

public enum Mo2Tes4RecordReferenceKind
{
    SpellEffect,
    ActorTemplate,
    ActorPackage,
    ActorDefaultOutfit,
    ActorSleepingOutfit,
    ActorDefaultPackageList,
    ActorSpectatorPackageList,
    ActorObserveDeadBodyPackageList,
    ActorGuardWarnPackageList,
    ActorCombatPackageList,
    OutfitItem,
    ArmorArmature,
    ArmorTemplate,
    MagicEffectAssociatedItem,
    MagicEffectCastingLight,
    MagicEffectHitShader,
    MagicEffectEnchantShader,
    MagicEffectProjectile,
    MagicEffectExplosion,
    MagicEffectCastingArt,
    MagicEffectHitEffectArt,
    MagicEffectImpactData,
    MagicEffectDualCastingArt,
    MagicEffectEnchantArt,
    MagicEffectHitVisuals,
    MagicEffectEnchantVisuals,
    MagicEffectEquipAbility,
    MagicEffectImageSpaceModifier,
    MagicEffectPerk,
    MagicEffectCounterEffect,
}

public sealed record Mo2Tes4RecordReference(
    Mo2Tes4RecordReferenceKind Kind,
    string SubrecordSignature,
    int RecordDataOffset,
    Mo2Tes4CanonicalRecordKey Target);

public sealed record Mo2Tes4PluginObservation(
    string Name,
    string CanonicalPath,
    int SourceOrder,
    int? LoadOrder,
    bool IsEnabled,
    bool HasMasterFlag,
    bool HasLightFlag,
    ImmutableArray<string> Masters,
    string RawSha256,
    long Length,
    Mo2RandomAccessStamp Before,
    Mo2RandomAccessStamp After);

[Flags]
public enum Mo2Tes4ActorTemplateFlags : ushort
{
    None = 0,
    UseTraits = 1,
    UseStats = 2,
    UseFactions = 4,
    UseSpellList = 8,
    UseAiData = 16,
    UseAiPackages = 32,
    UseModelAnimation = 64,
    UseBaseData = 128,
    UseInventory = 256,
    UseScripts = 512,
    UseDefaultPackageList = 1024,
    UseAttackData = 2048,
    UseKeywords = 4096,
}

public sealed record Mo2Tes4RecordSnapshot(
    Mo2Tes4CanonicalRecordKey Key,
    string Signature,
    string PluginName,
    int SourceOrder,
    int? LoadOrder,
    bool IsEnabled,
    string PluginRawSha256,
    uint RawFormId,
    uint RecordFlags,
    bool IsDeleted,
    bool IsInitiallyDisabled,
    long RecordOffset,
    uint DataSize,
    ImmutableArray<Mo2Tes4GroupAncestry> GroupAncestry,
    Mo2Tes4CanonicalRecordKey? Worldspace,
    Mo2Tes4CanonicalRecordKey? Cell,
    Mo2Tes4CellPlacement Placement,
    string? EditorId,
    string? ModelPath,
    Mo2Tes4CanonicalRecordKey? BaseObject,
    Mo2Tes4EnableParent? EnableParent,
    ImmutableArray<Mo2Tes4LinkedReference> LinkedReferences,
    ImmutableArray<Mo2Tes4RecordReference> RecordReferences,
    Mo2Tes4ActorTemplateFlags? ActorTemplateFlags,
    Mo2Tes4Transform? Transform,
    float? Scale);

public sealed record Mo2Tes4OverrideChain(
    Mo2Tes4CanonicalRecordKey Target,
    ImmutableArray<Mo2Tes4RecordSnapshot> Records,
    Mo2Tes4RecordSnapshot? Winner);

public sealed record Mo2Tes4CellScopeDiscovery(
    Mo2Tes4CanonicalRecordKey Key,
    Mo2Tes4CanonicalRecordKey Worldspace,
    Mo2Tes4CanonicalRecordKey Cell,
    Mo2Tes4CellPlacement Placement,
    Mo2Tes4Transform? Transform,
    float? DistanceFromNearestSeed);

public sealed record Mo2Tes4RecordGraphIssue(
    string Code,
    string Detail,
    string? PluginName = null,
    string? CanonicalPath = null);

public sealed record Mo2Tes4RecordGraphResult(
    Mo2Tes4RecordGraphStatus Status,
    ImmutableArray<Mo2Tes4PluginObservation> Plugins,
    ImmutableArray<Mo2Tes4OverrideChain> Chains,
    ImmutableArray<Mo2Tes4CanonicalRecordKey> TraversedKeys,
    ImmutableArray<Mo2Tes4CellScopeDiscovery> CellScopeDiscoveries,
    ImmutableArray<Mo2Tes4CanonicalRecordKey> DiscoveredKeys,
    long RecordHeadersExamined,
    long BytesScanned,
    ImmutableArray<Mo2Tes4RecordGraphIssue> Issues,
    string SemanticFingerprint);
