using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public enum CrossSourceCanonicalAssertionKind
{
    Terminology = 0,
    Relationship = 1,
    LocationNativeType = 2,
    LocationSemanticClassification = 3,
    RecordLifecycle = 4,
    SemanticClassification = 5,
    RecordContribution = 6,
    OrganizationalValue = 7,
    InstructionApplicability = 8,
}

public enum CrossSourceTargetLinkKind
{
    ExactSourceNativeIdentity = 0,
    ExactLabelOrHashKey = 1,
    ExactPluginRecordIdentity = 2,
    ExactProviderNativeIdentity = 3,
    VerifiedDeterministicCorrelation = 4,
    VersionedExactMapping = 5,
}

public readonly record struct CrossSourceTargetLinkClaimId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "cross-source-target-link-claim";

    [JsonConstructor]
    public CrossSourceTargetLinkClaimId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CrossSourceTargetLinkClaimId DeriveV1(
        KnowledgeRecordId targetKnowledgeRecordId,
        CatalogSourceRevisionId targetOriginSourceRevisionId,
        NativeRecordIdentityId targetNativeRecordIdentityId,
        SourceNativeIdentifier exactTargetCoordinate,
        SourceArtifactId targetCoordinateArtifactId,
        string targetCoordinateFieldPath,
        CatalogSourceRevisionId assertingSourceRevisionId,
        SourceNativeIdentifier exactAssertingCoordinate,
        SourceArtifactId assertingCoordinateArtifactId,
        string assertingCoordinateFieldPath,
        CrossSourceTargetLinkKind linkKind,
        string methodId,
        string methodVersion)
    {
        ArgumentNullException.ThrowIfNull(exactTargetCoordinate);
        ArgumentNullException.ThrowIfNull(exactAssertingCoordinate);
        if (!Enum.IsDefined(linkKind)) throw new ArgumentOutOfRangeException(nameof(linkKind));
        methodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        methodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        targetCoordinateFieldPath = CanonicalKnowledgeContract.RequireText(targetCoordinateFieldPath, nameof(targetCoordinateFieldPath));
        assertingCoordinateFieldPath = CanonicalKnowledgeContract.RequireText(assertingCoordinateFieldPath, nameof(assertingCoordinateFieldPath));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("target-record-id", targetKnowledgeRecordId.Value);
        writer.AddString("target-origin-revision-id", targetOriginSourceRevisionId.Value);
        writer.AddString("target-native-record-id", targetNativeRecordIdentityId.Value);
        writer.AddExactNativeIdentifier("target-coordinate", exactTargetCoordinate);
        writer.AddString("target-coordinate-artifact-id", targetCoordinateArtifactId.Value);
        writer.AddString("target-coordinate-field-path", targetCoordinateFieldPath);
        writer.AddString("asserting-source-revision-id", assertingSourceRevisionId.Value);
        writer.AddExactNativeIdentifier("asserting-coordinate", exactAssertingCoordinate);
        writer.AddString("asserting-coordinate-artifact-id", assertingCoordinateArtifactId.Value);
        writer.AddString("asserting-coordinate-field-path", assertingCoordinateFieldPath);
        writer.AddInt32("link-kind", (int)linkKind);
        writer.AddString("method-id", methodId);
        writer.AddString("method-version", methodVersion);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

/// <summary>
/// A typed exact-mapping claim emitted by the asserting source. Claim-level evidence must bind
/// this complete content before an assertion envelope may use it to attach facts to the target.
/// </summary>
public sealed record CrossSourceTargetLinkClaim
{
    [JsonConstructor]
    public CrossSourceTargetLinkClaim(
        CrossSourceTargetLinkClaimId id,
        KnowledgeRecordId targetKnowledgeRecordId,
        CatalogSourceRevisionId targetOriginSourceRevisionId,
        NativeRecordIdentityId targetNativeRecordIdentityId,
        SourceNativeIdentifier exactTargetCoordinate,
        SourceArtifactId targetCoordinateArtifactId,
        string targetCoordinateFieldPath,
        CatalogSourceRevisionId assertingSourceRevisionId,
        SourceNativeIdentifier exactAssertingCoordinate,
        SourceArtifactId assertingCoordinateArtifactId,
        string assertingCoordinateFieldPath,
        CrossSourceTargetLinkKind linkKind,
        string methodId,
        string methodVersion)
    {
        if (targetOriginSourceRevisionId == assertingSourceRevisionId)
            throw new ArgumentException("A cross-source target-link claim requires distinct source revisions.");
        ArgumentNullException.ThrowIfNull(exactTargetCoordinate);
        ArgumentNullException.ThrowIfNull(exactAssertingCoordinate);
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        TargetCoordinateFieldPath = CanonicalKnowledgeContract.RequireText(targetCoordinateFieldPath, nameof(targetCoordinateFieldPath));
        AssertingCoordinateFieldPath = CanonicalKnowledgeContract.RequireText(assertingCoordinateFieldPath, nameof(assertingCoordinateFieldPath));
        var expected = CrossSourceTargetLinkClaimId.DeriveV1(
            targetKnowledgeRecordId, targetOriginSourceRevisionId, targetNativeRecordIdentityId,
            exactTargetCoordinate, targetCoordinateArtifactId, TargetCoordinateFieldPath,
            assertingSourceRevisionId, exactAssertingCoordinate,
            assertingCoordinateArtifactId, AssertingCoordinateFieldPath,
            linkKind, MethodId, MethodVersion);
        if (id != expected)
            throw new ArgumentException("Cross-source target-link claim identity does not match its exact content.", nameof(id));
        Id = id;
        TargetKnowledgeRecordId = targetKnowledgeRecordId;
        TargetOriginSourceRevisionId = targetOriginSourceRevisionId;
        TargetNativeRecordIdentityId = targetNativeRecordIdentityId;
        ExactTargetCoordinate = exactTargetCoordinate;
        TargetCoordinateArtifactId = targetCoordinateArtifactId;
        AssertingSourceRevisionId = assertingSourceRevisionId;
        ExactAssertingCoordinate = exactAssertingCoordinate;
        AssertingCoordinateArtifactId = assertingCoordinateArtifactId;
        LinkKind = linkKind;
    }

    public CrossSourceTargetLinkClaimId Id { get; }
    public KnowledgeRecordId TargetKnowledgeRecordId { get; }
    public CatalogSourceRevisionId TargetOriginSourceRevisionId { get; }
    public NativeRecordIdentityId TargetNativeRecordIdentityId { get; }
    public SourceNativeIdentifier ExactTargetCoordinate { get; }
    public SourceArtifactId TargetCoordinateArtifactId { get; }
    public string TargetCoordinateFieldPath { get; }
    public CatalogSourceRevisionId AssertingSourceRevisionId { get; }
    public SourceNativeIdentifier ExactAssertingCoordinate { get; }
    public SourceArtifactId AssertingCoordinateArtifactId { get; }
    public string AssertingCoordinateFieldPath { get; }
    public CrossSourceTargetLinkKind LinkKind { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
}

public readonly record struct CrossSourceCanonicalAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "cross-source-canonical-assertion";

    [JsonConstructor]
    public CrossSourceCanonicalAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CrossSourceCanonicalAssertionId DeriveV1(
        KnowledgeRecordId targetKnowledgeRecordId,
        CatalogSourceRevisionId targetOriginSourceRevisionId,
        NativeRecordIdentityId targetNativeRecordIdentityId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        CrossSourceCanonicalAssertionKind assertionKind,
        EvidenceClaimContentId underlyingClaimContentId,
        CrossSourceTargetLinkClaimId targetLinkClaimId,
        CrossSourceTargetLinkKind targetLinkKind,
        SourceNativeIdentifier exactLinkKey,
        string targetLinkMethodId,
        string targetLinkMethodVersion,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds,
        ImmutableArray<EvidenceBindingId> supportingEvidenceBindingIds,
        ImmutableArray<InstructionEvidenceBindingId> supportingInstructionEvidenceBindingIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds)
    {
        if (!Enum.IsDefined(assertionKind)) throw new ArgumentOutOfRangeException(nameof(assertionKind));
        if (!Enum.IsDefined(targetLinkKind)) throw new ArgumentOutOfRangeException(nameof(targetLinkKind));
        ArgumentNullException.ThrowIfNull(exactLinkKey);
        targetLinkMethodId = CanonicalKnowledgeContract.RequireText(targetLinkMethodId, nameof(targetLinkMethodId));
        targetLinkMethodVersion = CanonicalKnowledgeContract.RequireText(targetLinkMethodVersion, nameof(targetLinkMethodVersion));
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("Cross-source linkage requires evidence receipts.", nameof(supportingEvidenceReceiptIds));
        if (supportingEvidenceBindingIds.IsDefaultOrEmpty)
            throw new ArgumentException("Cross-source linkage requires evidence bindings.", nameof(supportingEvidenceBindingIds));
        if (supportingInstructionEvidenceBindingIds.IsDefault)
            throw new ArgumentException("Instruction evidence binding identities must be initialized.", nameof(supportingInstructionEvidenceBindingIds));
        if (correlationRecordIds.IsDefault)
            throw new ArgumentException("Correlation identities must be initialized.", nameof(correlationRecordIds));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("target-record-id", targetKnowledgeRecordId.Value);
        writer.AddString("target-origin-revision-id", targetOriginSourceRevisionId.Value);
        writer.AddString("target-native-record-id", targetNativeRecordIdentityId.Value);
        writer.AddString("asserting-source-revision-id", assertingSourceRevisionId.Value);
        writer.AddInt32("assertion-kind", (int)assertionKind);
        writer.AddString("claim-content-id", underlyingClaimContentId.Value);
        writer.AddString("target-link-claim-id", targetLinkClaimId.Value);
        writer.AddInt32("link-kind", (int)targetLinkKind);
        writer.AddExactNativeIdentifier("exact-link-key", exactLinkKey);
        writer.AddString("link-method-id", targetLinkMethodId);
        writer.AddString("link-method-version", targetLinkMethodVersion);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "supporting-receipts", supportingEvidenceReceiptIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "supporting-bindings", supportingEvidenceBindingIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "supporting-instruction-bindings", supportingInstructionEvidenceBindingIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "correlations", correlationRecordIds.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public sealed record CrossSourceCanonicalAssertion
{
    [JsonConstructor]
    public CrossSourceCanonicalAssertion(
        CrossSourceCanonicalAssertionId id,
        KnowledgeRecordId targetKnowledgeRecordId,
        CatalogSourceRevisionId targetOriginSourceRevisionId,
        NativeRecordIdentityId targetNativeRecordIdentityId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        CrossSourceCanonicalAssertionKind assertionKind,
        EvidenceClaimContentId underlyingClaimContentId,
        CrossSourceTargetLinkClaimId targetLinkClaimId,
        CrossSourceTargetLinkKind targetLinkKind,
        SourceNativeIdentifier exactLinkKey,
        string targetLinkMethodId,
        string targetLinkMethodVersion,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds,
        ImmutableArray<EvidenceBindingId> supportingEvidenceBindingIds,
        ImmutableArray<InstructionEvidenceBindingId> supportingInstructionEvidenceBindingIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds)
    {
        if (targetOriginSourceRevisionId == assertingSourceRevisionId)
            throw new ArgumentException("A cross-source assertion requires a source revision distinct from the target origin.");
        if (!Enum.IsDefined(assertionKind)) throw new ArgumentOutOfRangeException(nameof(assertionKind));
        if (!Enum.IsDefined(targetLinkKind)) throw new ArgumentOutOfRangeException(nameof(targetLinkKind));
        ArgumentNullException.ThrowIfNull(exactLinkKey);
        TargetLinkMethodId = CanonicalKnowledgeContract.RequireText(targetLinkMethodId, nameof(targetLinkMethodId));
        TargetLinkMethodVersion = CanonicalKnowledgeContract.RequireText(targetLinkMethodVersion, nameof(targetLinkMethodVersion));
        SupportingEvidenceReceiptIds = Normalize(
            supportingEvidenceReceiptIds, value => value.Value, nameof(supportingEvidenceReceiptIds), requireNonEmpty: true);
        SupportingEvidenceBindingIds = Normalize(
            supportingEvidenceBindingIds, value => value.Value, nameof(supportingEvidenceBindingIds), requireNonEmpty: true);
        SupportingInstructionEvidenceBindingIds = Normalize(
            supportingInstructionEvidenceBindingIds,
            value => value.Value,
            nameof(supportingInstructionEvidenceBindingIds),
            requireNonEmpty: assertionKind == CrossSourceCanonicalAssertionKind.InstructionApplicability);
        if (assertionKind != CrossSourceCanonicalAssertionKind.InstructionApplicability &&
            !SupportingInstructionEvidenceBindingIds.IsEmpty)
            throw new ArgumentException("Only Instruction applicability may carry Instruction evidence bindings.", nameof(supportingInstructionEvidenceBindingIds));
        CorrelationRecordIds = Normalize(
            correlationRecordIds, value => value.Value, nameof(correlationRecordIds), requireNonEmpty: false);
        if (targetLinkKind == CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation && CorrelationRecordIds.IsEmpty)
            throw new ArgumentException("A correlation linkage requires a verified correlation record.", nameof(correlationRecordIds));
        if (targetLinkKind != CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation && !CorrelationRecordIds.IsEmpty)
            throw new ArgumentException("Only a verified deterministic-correlation linkage may carry correlation records.", nameof(correlationRecordIds));

        var expected = CrossSourceCanonicalAssertionId.DeriveV1(
            targetKnowledgeRecordId,
            targetOriginSourceRevisionId,
            targetNativeRecordIdentityId,
            assertingSourceRevisionId,
            assertionKind,
            underlyingClaimContentId,
            targetLinkClaimId,
            targetLinkKind,
            exactLinkKey,
            TargetLinkMethodId,
            TargetLinkMethodVersion,
            SupportingEvidenceReceiptIds,
            SupportingEvidenceBindingIds,
            SupportingInstructionEvidenceBindingIds,
            CorrelationRecordIds);
        if (id != expected)
            throw new ArgumentException("Cross-source assertion identity does not match its exact immutable inputs.", nameof(id));

        Id = id;
        TargetKnowledgeRecordId = targetKnowledgeRecordId;
        TargetOriginSourceRevisionId = targetOriginSourceRevisionId;
        TargetNativeRecordIdentityId = targetNativeRecordIdentityId;
        AssertingSourceRevisionId = assertingSourceRevisionId;
        AssertionKind = assertionKind;
        UnderlyingClaimContentId = underlyingClaimContentId;
        TargetLinkClaimId = targetLinkClaimId;
        TargetLinkKind = targetLinkKind;
        ExactLinkKey = exactLinkKey;
    }

    public CrossSourceCanonicalAssertionId Id { get; }
    public KnowledgeRecordId TargetKnowledgeRecordId { get; }
    public CatalogSourceRevisionId TargetOriginSourceRevisionId { get; }
    public NativeRecordIdentityId TargetNativeRecordIdentityId { get; }
    public CatalogSourceRevisionId AssertingSourceRevisionId { get; }
    public CrossSourceCanonicalAssertionKind AssertionKind { get; }
    public EvidenceClaimContentId UnderlyingClaimContentId { get; }
    public CrossSourceTargetLinkClaimId TargetLinkClaimId { get; }
    public CrossSourceTargetLinkKind TargetLinkKind { get; }
    public SourceNativeIdentifier ExactLinkKey { get; }
    public string TargetLinkMethodId { get; }
    public string TargetLinkMethodVersion { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }
    public ImmutableArray<EvidenceBindingId> SupportingEvidenceBindingIds { get; }
    public ImmutableArray<InstructionEvidenceBindingId> SupportingInstructionEvidenceBindingIds { get; }
    public ImmutableArray<CorrelationRecordId> CorrelationRecordIds { get; }

    public EvidenceClaimKind ClaimKind => AssertionKind switch
    {
        CrossSourceCanonicalAssertionKind.Terminology => EvidenceClaimKind.Terminology,
        CrossSourceCanonicalAssertionKind.Relationship => EvidenceClaimKind.Relationship,
        CrossSourceCanonicalAssertionKind.LocationNativeType => EvidenceClaimKind.LocationNativeType,
        CrossSourceCanonicalAssertionKind.LocationSemanticClassification => EvidenceClaimKind.LocationSemanticClassification,
        CrossSourceCanonicalAssertionKind.RecordLifecycle => EvidenceClaimKind.RecordLifecycle,
        CrossSourceCanonicalAssertionKind.SemanticClassification => EvidenceClaimKind.SemanticClassification,
        CrossSourceCanonicalAssertionKind.RecordContribution => EvidenceClaimKind.RecordContribution,
        CrossSourceCanonicalAssertionKind.OrganizationalValue => EvidenceClaimKind.OrganizationalValue,
        CrossSourceCanonicalAssertionKind.InstructionApplicability => EvidenceClaimKind.Instruction,
        _ => throw new ArgumentOutOfRangeException(nameof(AssertionKind)),
    };

    private static ImmutableArray<T> Normalize<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string parameterName,
        bool requireNonEmpty)
    {
        if (values.IsDefault || requireNonEmpty && values.IsEmpty)
            throw new ArgumentException("Collection must be initialized and satisfy its required cardinality.", parameterName);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection values must be distinct.", parameterName);
        return ordered;
    }
}

public sealed record UnresolvedCrossSourceTerminologyClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceTerminologyClaim(TerminologyAssertionRole role, string verbatimValue,
        string sourceFieldPath, string? languageTag, SourceNativeIdentifier? nativeStringIdentifier)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentNullException.ThrowIfNull(verbatimValue);
        CanonicalUtf8.Validate(verbatimValue);
        Role = role;
        VerbatimValue = verbatimValue;
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        LanguageTag = languageTag is null ? null : CanonicalKnowledgeContract.RequireText(languageTag, nameof(languageTag));
        NativeStringIdentifier = nativeStringIdentifier;
    }
    public TerminologyAssertionRole Role { get; }
    public string VerbatimValue { get; }
    public string SourceFieldPath { get; }
    public string? LanguageTag { get; }
    public SourceNativeIdentifier? NativeStringIdentifier { get; }
}

public sealed record UnresolvedCrossSourceRelationshipClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceRelationshipClaim(RelationshipSemanticId semanticId,
        string sourceNativeRelationshipType, string sourceFieldPath, SourceNativeIdentifier sourceNativeTarget)
    {
        ArgumentNullException.ThrowIfNull(sourceNativeTarget);
        SemanticId = semanticId;
        SourceNativeRelationshipType = CanonicalKnowledgeContract.RequireText(sourceNativeRelationshipType, nameof(sourceNativeRelationshipType));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        SourceNativeTarget = sourceNativeTarget;
    }
    public RelationshipSemanticId SemanticId { get; }
    public string SourceNativeRelationshipType { get; }
    public string SourceFieldPath { get; }
    public SourceNativeIdentifier SourceNativeTarget { get; }
}

public sealed record UnresolvedCrossSourceLocationNativeTypeClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceLocationNativeTypeClaim(SourceNativeIdentifier exactNativeType, string sourceFieldPath)
    {
        ArgumentNullException.ThrowIfNull(exactNativeType);
        ExactNativeType = exactNativeType;
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public SourceNativeIdentifier ExactNativeType { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceLocationClassificationClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceLocationClassificationClaim(SourceNativeIdentifier? exactSourceNativeType,
        LocationSemanticRoleId roleId, LocationSemanticVocabularyVersion vocabularyVersion,
        string classificationMethodId, string classificationMethodVersion, string sourceFieldPath)
    {
        ExactSourceNativeType = exactSourceNativeType;
        RoleId = roleId;
        VocabularyVersion = vocabularyVersion;
        ClassificationMethodId = CanonicalKnowledgeContract.RequireText(classificationMethodId, nameof(classificationMethodId));
        ClassificationMethodVersion = CanonicalKnowledgeContract.RequireText(classificationMethodVersion, nameof(classificationMethodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public SourceNativeIdentifier? ExactSourceNativeType { get; }
    public LocationSemanticRoleId RoleId { get; }
    public LocationSemanticVocabularyVersion VocabularyVersion { get; }
    public string ClassificationMethodId { get; }
    public string ClassificationMethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceLifecycleClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceLifecycleClaim(CanonicalRecordLifecycleState state,
        string sourceNativeLifecycleType, string sourceFieldPath)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        State = state;
        SourceNativeLifecycleType = CanonicalKnowledgeContract.RequireText(sourceNativeLifecycleType, nameof(sourceNativeLifecycleType));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public CanonicalRecordLifecycleState State { get; }
    public string SourceNativeLifecycleType { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceSemanticClassificationClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceSemanticClassificationClaim(CanonicalSemanticRoleId roleId,
        string vocabularyId, string vocabularyVersion, string classificationMethodId,
        string classificationMethodVersion, string sourceFieldPath)
    {
        RoleId = roleId;
        VocabularyId = CanonicalKnowledgeContract.RequireText(vocabularyId, nameof(vocabularyId));
        VocabularyVersion = CanonicalKnowledgeContract.RequireText(vocabularyVersion, nameof(vocabularyVersion));
        ClassificationMethodId = CanonicalKnowledgeContract.RequireText(classificationMethodId, nameof(classificationMethodId));
        ClassificationMethodVersion = CanonicalKnowledgeContract.RequireText(classificationMethodVersion, nameof(classificationMethodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public CanonicalSemanticRoleId RoleId { get; }
    public string VocabularyId { get; }
    public string VocabularyVersion { get; }
    public string ClassificationMethodId { get; }
    public string ClassificationMethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceContributionClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceContributionClaim(CanonicalRecordContributionKind contributionKind,
        SourceNativeIdentifier? originNativeIdentity, SourceNativeIdentifier? exactModIdentity,
        SourceNativeVersion? exactModVersion, string methodId, string methodVersion, string sourceFieldPath)
    {
        if (!Enum.IsDefined(contributionKind)) throw new ArgumentOutOfRangeException(nameof(contributionKind));
        if (exactModIdentity is null != (exactModVersion is null))
            throw new ArgumentException("Mod identity and version must be supplied together.");
        if (contributionKind == CanonicalRecordContributionKind.Modified && originNativeIdentity is null)
            throw new ArgumentException("A modification must retain its exact source-native origin.", nameof(originNativeIdentity));
        ContributionKind = contributionKind;
        OriginNativeIdentity = originNativeIdentity;
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public CanonicalRecordContributionKind ContributionKind { get; }
    public SourceNativeIdentifier? OriginNativeIdentity { get; }
    public SourceNativeIdentifier? ExactModIdentity { get; }
    public SourceNativeVersion? ExactModVersion { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceOrganizationalValueClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceOrganizationalValueClaim(CanonicalOrganizationalSemanticId dimensionId,
        SourceNativeIdentifier exactValueIdentity, string? verbatimDisplayValue,
        string methodId, string methodVersion, string sourceFieldPath)
    {
        ArgumentNullException.ThrowIfNull(exactValueIdentity);
        if (verbatimDisplayValue is not null) CanonicalUtf8.Validate(verbatimDisplayValue);
        DimensionId = dimensionId;
        ExactValueIdentity = exactValueIdentity;
        VerbatimDisplayValue = verbatimDisplayValue;
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public CanonicalOrganizationalSemanticId DimensionId { get; }
    public SourceNativeIdentifier ExactValueIdentity { get; }
    public string? VerbatimDisplayValue { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record UnresolvedCrossSourceInstructionApplicabilityClaim
{
    [JsonConstructor]
    public UnresolvedCrossSourceInstructionApplicabilityClaim(
        InstructionAssertionId instructionAssertionId, string sourceFieldPath)
    {
        InstructionAssertionId = instructionAssertionId;
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
    }
    public InstructionAssertionId InstructionAssertionId { get; }
    public string SourceFieldPath { get; }
}

/// <summary>
/// A targetless, strongly typed assertion body retained when exact linkage to an existing
/// canonical record cannot be established. Exactly one domain body is present.
/// </summary>
public sealed record UnresolvedCrossSourceClaimContent
{
    [JsonConstructor]
    public UnresolvedCrossSourceClaimContent(
        UnresolvedCrossSourceClaimContentId id,
        CatalogSourceRevisionId assertingSourceRevisionId,
        SourceNativeIdentifier exactAssertingSubject,
        CrossSourceCanonicalAssertionKind assertionKind,
        UnresolvedCrossSourceTerminologyClaim? terminology,
        UnresolvedCrossSourceRelationshipClaim? relationship,
        UnresolvedCrossSourceLocationNativeTypeClaim? locationNativeType,
        UnresolvedCrossSourceLocationClassificationClaim? locationSemanticClassification,
        UnresolvedCrossSourceLifecycleClaim? recordLifecycle,
        UnresolvedCrossSourceSemanticClassificationClaim? semanticClassification,
        UnresolvedCrossSourceContributionClaim? recordContribution,
        UnresolvedCrossSourceOrganizationalValueClaim? organizationalValue,
        UnresolvedCrossSourceInstructionApplicabilityClaim? instructionApplicability)
    {
        ArgumentNullException.ThrowIfNull(exactAssertingSubject);
        if (!Enum.IsDefined(assertionKind)) throw new ArgumentOutOfRangeException(nameof(assertionKind));
        var bodies = new object?[] { terminology, relationship, locationNativeType,
            locationSemanticClassification, recordLifecycle, semanticClassification,
            recordContribution, organizationalValue, instructionApplicability };
        if (bodies.Count(value => value is not null) != 1 ||
            assertionKind switch
            {
                CrossSourceCanonicalAssertionKind.Terminology => terminology is null,
                CrossSourceCanonicalAssertionKind.Relationship => relationship is null,
                CrossSourceCanonicalAssertionKind.LocationNativeType => locationNativeType is null,
                CrossSourceCanonicalAssertionKind.LocationSemanticClassification => locationSemanticClassification is null,
                CrossSourceCanonicalAssertionKind.RecordLifecycle => recordLifecycle is null,
                CrossSourceCanonicalAssertionKind.SemanticClassification => semanticClassification is null,
                CrossSourceCanonicalAssertionKind.RecordContribution => recordContribution is null,
                CrossSourceCanonicalAssertionKind.OrganizationalValue => organizationalValue is null,
                CrossSourceCanonicalAssertionKind.InstructionApplicability => instructionApplicability is null,
                _ => true,
            })
            throw new ArgumentException("Exactly one typed unresolved body must match the assertion kind.");
        var expected = UnresolvedCrossSourceClaimContentId.DeriveV1(
            assertingSourceRevisionId, exactAssertingSubject, assertionKind,
            terminology, relationship, locationNativeType, locationSemanticClassification,
            recordLifecycle, semanticClassification, recordContribution, organizationalValue,
            instructionApplicability);
        if (id != expected)
            throw new ArgumentException("Unresolved cross-source claim identity does not match its exact typed content.", nameof(id));
        Id = id;
        AssertingSourceRevisionId = assertingSourceRevisionId;
        ExactAssertingSubject = exactAssertingSubject;
        AssertionKind = assertionKind;
        Terminology = terminology;
        Relationship = relationship;
        LocationNativeType = locationNativeType;
        LocationSemanticClassification = locationSemanticClassification;
        RecordLifecycle = recordLifecycle;
        SemanticClassification = semanticClassification;
        RecordContribution = recordContribution;
        OrganizationalValue = organizationalValue;
        InstructionApplicability = instructionApplicability;
    }

    public UnresolvedCrossSourceClaimContentId Id { get; }
    public CatalogSourceRevisionId AssertingSourceRevisionId { get; }
    public SourceNativeIdentifier ExactAssertingSubject { get; }
    public CrossSourceCanonicalAssertionKind AssertionKind { get; }
    public UnresolvedCrossSourceTerminologyClaim? Terminology { get; }
    public UnresolvedCrossSourceRelationshipClaim? Relationship { get; }
    public UnresolvedCrossSourceLocationNativeTypeClaim? LocationNativeType { get; }
    public UnresolvedCrossSourceLocationClassificationClaim? LocationSemanticClassification { get; }
    public UnresolvedCrossSourceLifecycleClaim? RecordLifecycle { get; }
    public UnresolvedCrossSourceSemanticClassificationClaim? SemanticClassification { get; }
    public UnresolvedCrossSourceContributionClaim? RecordContribution { get; }
    public UnresolvedCrossSourceOrganizationalValueClaim? OrganizationalValue { get; }
    public UnresolvedCrossSourceInstructionApplicabilityClaim? InstructionApplicability { get; }

    public string SourceFieldPath => AssertionKind switch
    {
        CrossSourceCanonicalAssertionKind.Terminology => Terminology!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.Relationship => Relationship!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.LocationNativeType => LocationNativeType!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.LocationSemanticClassification => LocationSemanticClassification!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.RecordLifecycle => RecordLifecycle!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.SemanticClassification => SemanticClassification!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.RecordContribution => RecordContribution!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.OrganizationalValue => OrganizationalValue!.SourceFieldPath,
        CrossSourceCanonicalAssertionKind.InstructionApplicability => InstructionApplicability!.SourceFieldPath,
        _ => throw new ArgumentOutOfRangeException(),
    };
}

public readonly record struct UnresolvedCrossSourceClaimContentId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "unresolved-cross-source-claim";

    [JsonConstructor]
    public UnresolvedCrossSourceClaimContentId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static UnresolvedCrossSourceClaimContentId DeriveV1(
        CatalogSourceRevisionId assertingSourceRevisionId,
        SourceNativeIdentifier exactAssertingSubject,
        CrossSourceCanonicalAssertionKind assertionKind,
        UnresolvedCrossSourceTerminologyClaim? terminology,
        UnresolvedCrossSourceRelationshipClaim? relationship,
        UnresolvedCrossSourceLocationNativeTypeClaim? locationNativeType,
        UnresolvedCrossSourceLocationClassificationClaim? locationSemanticClassification,
        UnresolvedCrossSourceLifecycleClaim? recordLifecycle,
        UnresolvedCrossSourceSemanticClassificationClaim? semanticClassification,
        UnresolvedCrossSourceContributionClaim? recordContribution,
        UnresolvedCrossSourceOrganizationalValueClaim? organizationalValue,
        UnresolvedCrossSourceInstructionApplicabilityClaim? instructionApplicability)
    {
        ArgumentNullException.ThrowIfNull(exactAssertingSubject);
        if (!Enum.IsDefined(assertionKind)) throw new ArgumentOutOfRangeException(nameof(assertionKind));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("asserting-revision", assertingSourceRevisionId.Value);
        writer.AddExactNativeIdentifier("asserting-subject", exactAssertingSubject);
        writer.AddInt32("assertion-kind", (int)assertionKind);
        switch (assertionKind)
        {
            case CrossSourceCanonicalAssertionKind.Terminology when terminology is not null:
                if (!Enum.IsDefined(terminology.Role)) throw new ArgumentOutOfRangeException(nameof(terminology));
                writer.AddInt32("terminology.role", (int)terminology.Role);
                writer.AddString("terminology.value", terminology.VerbatimValue);
                writer.AddString("terminology.field", terminology.SourceFieldPath);
                writer.AddOptionalString("terminology.language", terminology.LanguageTag);
                writer.AddOptionalExactNativeIdentifier("terminology.native-string", terminology.NativeStringIdentifier);
                break;
            case CrossSourceCanonicalAssertionKind.Relationship when relationship is not null:
                writer.AddString("relationship.semantic", relationship.SemanticId.Value);
                writer.AddString("relationship.native-type", relationship.SourceNativeRelationshipType);
                writer.AddString("relationship.field", relationship.SourceFieldPath);
                writer.AddExactNativeIdentifier("relationship.native-target", relationship.SourceNativeTarget);
                break;
            case CrossSourceCanonicalAssertionKind.LocationNativeType when locationNativeType is not null:
                writer.AddExactNativeIdentifier("location-native-type.value", locationNativeType.ExactNativeType);
                writer.AddString("location-native-type.field", locationNativeType.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.LocationSemanticClassification when locationSemanticClassification is not null:
                writer.AddOptionalExactNativeIdentifier("location-classification.native-type", locationSemanticClassification.ExactSourceNativeType);
                writer.AddString("location-classification.role", locationSemanticClassification.RoleId.Value);
                writer.AddInt32("location-classification.vocabulary", locationSemanticClassification.VocabularyVersion.Value);
                writer.AddString("location-classification.method", locationSemanticClassification.ClassificationMethodId);
                writer.AddString("location-classification.method-version", locationSemanticClassification.ClassificationMethodVersion);
                writer.AddString("location-classification.field", locationSemanticClassification.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.RecordLifecycle when recordLifecycle is not null:
                if (!Enum.IsDefined(recordLifecycle.State)) throw new ArgumentOutOfRangeException(nameof(recordLifecycle));
                writer.AddInt32("lifecycle.state", (int)recordLifecycle.State);
                writer.AddString("lifecycle.native-type", recordLifecycle.SourceNativeLifecycleType);
                writer.AddString("lifecycle.field", recordLifecycle.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.SemanticClassification when semanticClassification is not null:
                writer.AddString("classification.role", semanticClassification.RoleId.Value);
                writer.AddString("classification.vocabulary", semanticClassification.VocabularyId);
                writer.AddString("classification.vocabulary-version", semanticClassification.VocabularyVersion);
                writer.AddString("classification.method", semanticClassification.ClassificationMethodId);
                writer.AddString("classification.method-version", semanticClassification.ClassificationMethodVersion);
                writer.AddString("classification.field", semanticClassification.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.RecordContribution when recordContribution is not null:
                if (!Enum.IsDefined(recordContribution.ContributionKind) ||
                    recordContribution.ExactModIdentity is null != (recordContribution.ExactModVersion is null) ||
                    recordContribution.ContributionKind == CanonicalRecordContributionKind.Modified &&
                        recordContribution.OriginNativeIdentity is null)
                    throw new ArgumentException("Unresolved contribution coordinates are invalid.", nameof(recordContribution));
                writer.AddInt32("contribution.kind", (int)recordContribution.ContributionKind);
                writer.AddOptionalExactNativeIdentifier("contribution.origin", recordContribution.OriginNativeIdentity);
                writer.AddOptionalExactNativeIdentifier("contribution.mod", recordContribution.ExactModIdentity);
                writer.AddOptionalNativeVersion("contribution.mod-version", recordContribution.ExactModVersion);
                writer.AddString("contribution.method", recordContribution.MethodId);
                writer.AddString("contribution.method-version", recordContribution.MethodVersion);
                writer.AddString("contribution.field", recordContribution.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.OrganizationalValue when organizationalValue is not null:
                writer.AddString("organization.dimension", organizationalValue.DimensionId.Value);
                writer.AddExactNativeIdentifier("organization.value", organizationalValue.ExactValueIdentity);
                writer.AddOptionalString("organization.display", organizationalValue.VerbatimDisplayValue);
                writer.AddString("organization.method", organizationalValue.MethodId);
                writer.AddString("organization.method-version", organizationalValue.MethodVersion);
                writer.AddString("organization.field", organizationalValue.SourceFieldPath);
                break;
            case CrossSourceCanonicalAssertionKind.InstructionApplicability when instructionApplicability is not null:
                writer.AddString("instruction.id", instructionApplicability.InstructionAssertionId.Value);
                writer.AddString("instruction.field", instructionApplicability.SourceFieldPath);
                break;
            default:
                throw new ArgumentException("Exactly one typed unresolved body must match the assertion kind.");
        }
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct UnresolvedCrossSourceEvidenceBindingId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "unresolved-cross-source-evidence-binding";

    [JsonConstructor]
    public UnresolvedCrossSourceEvidenceBindingId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static UnresolvedCrossSourceEvidenceBindingId DeriveV1(
        EvidenceReceiptId receiptId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        UnresolvedCrossSourceClaimContentId claimContentId,
        UnresolvedCrossSourceTargetAttemptId? targetAttemptId,
        CrossSourceCanonicalAssertionKind assertionKind,
        string exactLocator,
        EvidenceVerificationKind verification)
    {
        if (!Enum.IsDefined(assertionKind)) throw new ArgumentOutOfRangeException(nameof(assertionKind));
        if (!Enum.IsDefined(verification)) throw new ArgumentOutOfRangeException(nameof(verification));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("receipt", receiptId.Value);
        writer.AddString("asserting-revision", assertingSourceRevisionId.Value);
        writer.AddString("claim", claimContentId.Value);
        writer.AddOptionalString("target-attempt", targetAttemptId?.Value);
        writer.AddInt32("assertion-kind", (int)assertionKind);
        writer.AddString("locator", CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator)));
        writer.AddInt32("verification", (int)verification);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct UnresolvedCrossSourceTargetAttemptId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "unresolved-cross-source-target-attempt";

    [JsonConstructor]
    public UnresolvedCrossSourceTargetAttemptId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static UnresolvedCrossSourceTargetAttemptId DeriveV1(
        UnresolvedCrossSourceClaimContentId claimContentId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        SourceNativeIdentifier exactAssertingSubject,
        CrossSourceTargetLinkKind attemptedLinkKind,
        string attemptedLinkMethodId,
        string attemptedLinkMethodVersion,
        CorrelationOutcome outcome,
        ImmutableArray<KnowledgeRecordId> candidateTargetIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds)
    {
        ArgumentNullException.ThrowIfNull(exactAssertingSubject);
        if (!Enum.IsDefined(attemptedLinkKind)) throw new ArgumentOutOfRangeException(nameof(attemptedLinkKind));
        if (outcome == CorrelationOutcome.Correlated || !Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("claim", claimContentId.Value);
        writer.AddString("asserting-revision", assertingSourceRevisionId.Value);
        writer.AddExactNativeIdentifier("asserting-subject", exactAssertingSubject);
        writer.AddInt32("link-kind", (int)attemptedLinkKind);
        writer.AddString("method", CanonicalKnowledgeContract.RequireText(attemptedLinkMethodId, nameof(attemptedLinkMethodId)));
        writer.AddString("method-version", CanonicalKnowledgeContract.RequireText(attemptedLinkMethodVersion, nameof(attemptedLinkMethodVersion)));
        writer.AddInt32("outcome", (int)outcome);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "candidates", candidateTargetIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "correlations", correlationRecordIds.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public sealed record UnresolvedCrossSourceEvidenceBinding
{
    [JsonConstructor]
    public UnresolvedCrossSourceEvidenceBinding(
        UnresolvedCrossSourceEvidenceBindingId id,
        EvidenceReceiptId evidenceReceiptId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        UnresolvedCrossSourceClaimContentId claimContentId,
        UnresolvedCrossSourceTargetAttemptId? targetAttemptId,
        CrossSourceCanonicalAssertionKind assertionKind,
        string exactLocator,
        EvidenceVerificationKind verification)
    {
        ExactLocator = CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator));
        var expected = UnresolvedCrossSourceEvidenceBindingId.DeriveV1(
            evidenceReceiptId, assertingSourceRevisionId, claimContentId, targetAttemptId,
            assertionKind, ExactLocator, verification);
        if (id != expected)
            throw new ArgumentException("Unresolved cross-source evidence binding identity does not match its exact content.", nameof(id));
        Id = id;
        EvidenceReceiptId = evidenceReceiptId;
        AssertingSourceRevisionId = assertingSourceRevisionId;
        ClaimContentId = claimContentId;
        TargetAttemptId = targetAttemptId;
        AssertionKind = assertionKind;
        Verification = verification;
    }

    public UnresolvedCrossSourceEvidenceBindingId Id { get; }
    public EvidenceReceiptId EvidenceReceiptId { get; }
    public CatalogSourceRevisionId AssertingSourceRevisionId { get; }
    public UnresolvedCrossSourceClaimContentId ClaimContentId { get; }
    public UnresolvedCrossSourceTargetAttemptId? TargetAttemptId { get; }
    public CrossSourceCanonicalAssertionKind AssertionKind { get; }
    public string ExactLocator { get; }
    public EvidenceVerificationKind Verification { get; }
}

public readonly record struct UnresolvedCrossSourceAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "unresolved-cross-source-assertion";

    [JsonConstructor]
    public UnresolvedCrossSourceAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static UnresolvedCrossSourceAssertionId DeriveV1(
        UnresolvedCrossSourceClaimContentId claimContentId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        KnowledgeAdapterRevisionId adapterRevisionId,
        CrossSourceTargetLinkKind attemptedLinkKind,
        string attemptedLinkMethodId,
        string attemptedLinkMethodVersion,
        CorrelationOutcome outcome,
        string reasonCode,
        ImmutableArray<KnowledgeRecordId> candidateTargetIds,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds,
        ImmutableArray<UnresolvedCrossSourceEvidenceBindingId> supportingEvidenceBindingIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds)
    {
        if (!Enum.IsDefined(attemptedLinkKind)) throw new ArgumentOutOfRangeException(nameof(attemptedLinkKind));
        if (outcome == CorrelationOutcome.Correlated || !Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), "A targetless assertion cannot claim a correlated outcome.");
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("claim", claimContentId.Value);
        writer.AddString("asserting-revision", assertingSourceRevisionId.Value);
        writer.AddString("adapter-revision", adapterRevisionId.Value);
        writer.AddInt32("attempted-link-kind", (int)attemptedLinkKind);
        writer.AddString("attempted-link-method", CanonicalKnowledgeContract.RequireText(attemptedLinkMethodId, nameof(attemptedLinkMethodId)));
        writer.AddString("attempted-link-version", CanonicalKnowledgeContract.RequireText(attemptedLinkMethodVersion, nameof(attemptedLinkMethodVersion)));
        writer.AddInt32("outcome", (int)outcome);
        writer.AddString("reason", CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode)));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "candidates", candidateTargetIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "receipts", supportingEvidenceReceiptIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "bindings", supportingEvidenceBindingIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "correlations", correlationRecordIds.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public sealed record UnresolvedCrossSourceAssertion
{
    [JsonConstructor]
    public UnresolvedCrossSourceAssertion(
        UnresolvedCrossSourceAssertionId id,
        UnresolvedCrossSourceClaimContentId claimContentId,
        CatalogSourceRevisionId assertingSourceRevisionId,
        KnowledgeAdapterRevisionId adapterRevisionId,
        CrossSourceTargetLinkKind attemptedLinkKind,
        string attemptedLinkMethodId,
        string attemptedLinkMethodVersion,
        CorrelationOutcome outcome,
        string reasonCode,
        ImmutableArray<KnowledgeRecordId> candidateTargetIds,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds,
        ImmutableArray<UnresolvedCrossSourceEvidenceBindingId> supportingEvidenceBindingIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds)
    {
        AttemptedLinkMethodId = CanonicalKnowledgeContract.RequireText(attemptedLinkMethodId, nameof(attemptedLinkMethodId));
        AttemptedLinkMethodVersion = CanonicalKnowledgeContract.RequireText(attemptedLinkMethodVersion, nameof(attemptedLinkMethodVersion));
        ReasonCode = CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode));
        CandidateTargetIds = Normalize(candidateTargetIds, value => value.Value, nameof(candidateTargetIds), false);
        SupportingEvidenceReceiptIds = Normalize(supportingEvidenceReceiptIds, value => value.Value, nameof(supportingEvidenceReceiptIds), true);
        SupportingEvidenceBindingIds = Normalize(supportingEvidenceBindingIds, value => value.Value, nameof(supportingEvidenceBindingIds), true);
        CorrelationRecordIds = Normalize(correlationRecordIds, value => value.Value, nameof(correlationRecordIds), false);
        var expected = UnresolvedCrossSourceAssertionId.DeriveV1(
            claimContentId, assertingSourceRevisionId, adapterRevisionId, attemptedLinkKind,
            AttemptedLinkMethodId, AttemptedLinkMethodVersion, outcome, ReasonCode,
            CandidateTargetIds, SupportingEvidenceReceiptIds, SupportingEvidenceBindingIds, CorrelationRecordIds);
        if (id != expected)
            throw new ArgumentException("Unresolved cross-source assertion identity does not match its exact inputs.", nameof(id));
        Id = id;
        ClaimContentId = claimContentId;
        AssertingSourceRevisionId = assertingSourceRevisionId;
        AdapterRevisionId = adapterRevisionId;
        AttemptedLinkKind = attemptedLinkKind;
        Outcome = outcome;
    }

    public UnresolvedCrossSourceAssertionId Id { get; }
    public UnresolvedCrossSourceClaimContentId ClaimContentId { get; }
    public CatalogSourceRevisionId AssertingSourceRevisionId { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId { get; }
    public CrossSourceTargetLinkKind AttemptedLinkKind { get; }
    public string AttemptedLinkMethodId { get; }
    public string AttemptedLinkMethodVersion { get; }
    public CorrelationOutcome Outcome { get; }
    public string ReasonCode { get; }
    public ImmutableArray<KnowledgeRecordId> CandidateTargetIds { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }
    public ImmutableArray<UnresolvedCrossSourceEvidenceBindingId> SupportingEvidenceBindingIds { get; }
    public ImmutableArray<CorrelationRecordId> CorrelationRecordIds { get; }

    private static ImmutableArray<T> Normalize<T>(
        ImmutableArray<T> values, Func<T, string> identity, string name, bool requireNonEmpty)
    {
        if (values.IsDefault || requireNonEmpty && values.IsEmpty)
            throw new ArgumentException("Collection must be initialized and satisfy its required cardinality.", name);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection values must be distinct.", name);
        return ordered;
    }
}
