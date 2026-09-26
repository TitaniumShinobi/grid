using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public enum KnowledgeSourceKind
{
    LocalGameDistribution = 0,
    LocalModArtifact = 1,
    PluginRecordFile = 2,
    OfficialProvider = 3,
    ModProvider = 4,
    ReferenceProvider = 5,
    FrozenRepositoryDataset = 6,
}

public enum KnowledgeKind
{
    Location = 0,
    MissionQuest = 1,
    Item = 2,
    Actor = 3,
}

public enum CanonicalResolutionState
{
    Resolved,
    Unresolved,
}

public enum TerminologyAssertionRole
{
    PrimaryName,
    Alias,
    PreviousTitle,
    RedirectSourceTitle,
    RedirectTargetTitle,
}

public enum EvidenceVerificationKind
{
    FileVerified,
    ReferenceVerified,
}

public enum EvidenceClaimKind
{
    KnowledgeIdentity = 0,
    Terminology = 1,
    Relationship = 2,
    LocationNativeType = 3,
    LocationSemanticClassification = 4,
    RecordLifecycle = 5,
    SemanticClassification = 6,
    RecordContribution = 7,
    OrganizationalValue = 8,
    Instruction = 9,
    CrossSourceTargetLink = 10,
}

public enum CorrelationOutcome
{
    Correlated,
    Ambiguous,
    Rejected,
    Unresolved,
}

public readonly record struct RelationshipSemanticId
{
    [JsonConstructor]
    public RelationshipSemanticId(string value) =>
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public sealed record TerminologyAssertion
{
    public TerminologyAssertion(
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        TerminologyAssertionRole role,
        string verbatimValue,
        string sourceFieldPath,
        string? languageTag = null,
        SourceNativeIdentifier? nativeStringIdentifier = null)
    {
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(verbatimValue);

        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        Role = role;
        VerbatimValue = verbatimValue;
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        LanguageTag = languageTag is null
            ? null
            : CanonicalKnowledgeContract.RequireText(languageTag, nameof(languageTag));
        NativeStringIdentifier = nativeStringIdentifier;
    }

    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public TerminologyAssertionRole Role { get; }
    public string VerbatimValue { get; }
    public string SourceFieldPath { get; }
    public string? LanguageTag { get; }
    public SourceNativeIdentifier? NativeStringIdentifier { get; }
}

public sealed record RelationshipAssertion
{
    public RelationshipAssertion(
        KnowledgeRecordId subjectKnowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        RelationshipSemanticId semanticId,
        string sourceNativeRelationshipType,
        string sourceFieldPath,
        SourceNativeIdentifier sourceNativeTarget,
        KnowledgeRecordId? resolvedTargetKnowledgeRecordId = null)
    {
        CanonicalKnowledgeContract.RequireIdentifier(subjectKnowledgeRecordId.Value, nameof(subjectKnowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(semanticId.Value, nameof(semanticId));
        ArgumentNullException.ThrowIfNull(sourceNativeTarget);
        if (resolvedTargetKnowledgeRecordId is KnowledgeRecordId target)
            CanonicalKnowledgeContract.RequireIdentifier(target.Value, nameof(resolvedTargetKnowledgeRecordId));

        SubjectKnowledgeRecordId = subjectKnowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        SemanticId = semanticId;
        SourceNativeRelationshipType = CanonicalKnowledgeContract.RequireText(
            sourceNativeRelationshipType,
            nameof(sourceNativeRelationshipType));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        SourceNativeTarget = sourceNativeTarget;
        ResolvedTargetKnowledgeRecordId = resolvedTargetKnowledgeRecordId;
    }

    public KnowledgeRecordId SubjectKnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public RelationshipSemanticId SemanticId { get; }
    public string SourceNativeRelationshipType { get; }
    public string SourceFieldPath { get; }
    public SourceNativeIdentifier SourceNativeTarget { get; }
    public KnowledgeRecordId? ResolvedTargetKnowledgeRecordId { get; }
    public CanonicalResolutionState Resolution =>
        ResolvedTargetKnowledgeRecordId is null ? CanonicalResolutionState.Unresolved : CanonicalResolutionState.Resolved;
}

public sealed record FileEvidenceReceipt
{
    [JsonConstructor]
    public FileEvidenceReceipt(
        CatalogSourceRevisionId sourceRevisionId,
        SourceArtifactId sourceArtifactId,
        ContentDigest artifactDigest,
        string parserId,
        string parserVersion,
        string nativeRecordLocator,
        string sourceFieldPath,
        long? byteOffset,
        long? byteLength,
        ContentDigest? interpretedBytesDigest,
        DateTimeOffset observedAtUtc,
        SourceNativeIdentifier? nativeObjectIdentity = null)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceArtifactId.Value, nameof(sourceArtifactId));
        if (SourceArtifactId.DeriveV1(artifactDigest) != sourceArtifactId)
            throw new ArgumentException("Source artifact identity does not match the supplied artifact digest.", nameof(artifactDigest));
        if (byteOffset.HasValue != byteLength.HasValue)
            throw new ArgumentException("Byte offset and byte length must either both be present or both be absent.");
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        if (byteLength <= 0) throw new ArgumentOutOfRangeException(nameof(byteLength));

        SourceRevisionId = sourceRevisionId;
        SourceArtifactId = sourceArtifactId;
        ArtifactDigest = artifactDigest;
        ParserId = CanonicalKnowledgeContract.RequireText(parserId, nameof(parserId));
        ParserVersion = CanonicalKnowledgeContract.RequireText(parserVersion, nameof(parserVersion));
        NativeRecordLocator = CanonicalKnowledgeContract.RequireText(nativeRecordLocator, nameof(nativeRecordLocator));
        NativeObjectIdentity = nativeObjectIdentity;
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        ByteOffset = byteOffset;
        ByteLength = byteLength;
        InterpretedBytesDigest = interpretedBytesDigest;
        ObservedAtUtc = observedAtUtc;
    }

    public CatalogSourceRevisionId SourceRevisionId { get; }
    public SourceArtifactId SourceArtifactId { get; }
    public ContentDigest ArtifactDigest { get; }
    public string ParserId { get; }
    public string ParserVersion { get; }
    public string NativeRecordLocator { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceNativeIdentifier? NativeObjectIdentity { get; }
    public string SourceFieldPath { get; }
    public long? ByteOffset { get; }
    public long? ByteLength { get; }
    public ContentDigest? InterpretedBytesDigest { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public EvidenceVerificationKind Verification => EvidenceVerificationKind.FileVerified;
}

public sealed record ReferenceEvidenceReceipt
{
    public ReferenceEvidenceReceipt(
        CatalogSourceId providerCatalogSourceId,
        CatalogSourceRevisionId sourceRevisionId,
        SourceArtifactId responseArtifactId,
        ContentDigest responseContentDigest,
        SourceNativeIdentifier? nativeObjectIdentity,
        SourceNativeVersion? nativeRevisionIdentity,
        string responseFieldPath,
        DateTimeOffset retrievedAtUtc)
    {
        CanonicalKnowledgeContract.RequireIdentifier(providerCatalogSourceId.Value, nameof(providerCatalogSourceId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(responseArtifactId.Value, nameof(responseArtifactId));
        if (SourceArtifactId.DeriveV1(responseContentDigest) != responseArtifactId)
            throw new ArgumentException("Response artifact identity does not match the supplied response digest.", nameof(responseContentDigest));
        ProviderCatalogSourceId = providerCatalogSourceId;
        SourceRevisionId = sourceRevisionId;
        ResponseArtifactId = responseArtifactId;
        ResponseContentDigest = responseContentDigest;
        NativeObjectIdentity = nativeObjectIdentity;
        NativeRevisionIdentity = nativeRevisionIdentity;
        ResponseFieldPath = CanonicalKnowledgeContract.RequireText(responseFieldPath, nameof(responseFieldPath));
        RetrievedAtUtc = retrievedAtUtc;
    }

    public CatalogSourceId ProviderCatalogSourceId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public SourceArtifactId ResponseArtifactId { get; }
    public ContentDigest ResponseContentDigest { get; }
    public SourceNativeIdentifier? NativeObjectIdentity { get; }
    public SourceNativeVersion? NativeRevisionIdentity { get; }
    public string ResponseFieldPath { get; }
    public DateTimeOffset RetrievedAtUtc { get; }
    public EvidenceVerificationKind Verification => EvidenceVerificationKind.ReferenceVerified;
}

public sealed record CorrelationRecord
{
    public CorrelationRecord(
        ImmutableArray<KnowledgeRecordId> memberIds,
        string methodId,
        string methodVersion,
        CorrelationOutcome outcome)
    {
        if (memberIds.IsDefault)
            throw new ArgumentException("Correlation members must be an initialized immutable array.", nameof(memberIds));
        if (memberIds.Length < 2)
            throw new ArgumentException("A correlation requires at least two members.", nameof(memberIds));
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));

        var orderedMembers = memberIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        for (var index = 0; index < orderedMembers.Length; index++)
        {
            CanonicalKnowledgeContract.RequireIdentifier(orderedMembers[index].Value, nameof(memberIds));
            if (index > 0 && orderedMembers[index - 1] == orderedMembers[index])
                throw new ArgumentException("Correlation members must be distinct.", nameof(memberIds));
        }

        MemberIds = orderedMembers;
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        Outcome = outcome;
    }

    public ImmutableArray<KnowledgeRecordId> MemberIds { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public CorrelationOutcome Outcome { get; }
}

internal static class CanonicalKnowledgeContract
{
    public static string RequireText(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value must be non-empty.", parameterName);
        return value;
    }

    public static void RequireIdentifier(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Canonical identifier must be non-empty.", parameterName);
    }
}
