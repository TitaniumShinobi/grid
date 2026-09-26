using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct LocationSemanticRoleId
{
    [JsonConstructor]
    public LocationSemanticRoleId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct LocationSemanticVocabularyVersion
{
    [JsonConstructor]
    public LocationSemanticVocabularyVersion(int value)
    {
        if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        Value = value;
    }

    public int Value { get; }
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct LocationSourceFamilyId
{
    [JsonConstructor]
    public LocationSourceFamilyId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct LocationPresentationPolicyId
{
    [JsonConstructor]
    public LocationPresentationPolicyId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CatalogCompositionId
{
    [JsonConstructor]
    public CatalogCompositionId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct SourceNativeLocationTypeAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "location-native-type-assertion";

    [JsonConstructor]
    public SourceNativeLocationTypeAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static SourceNativeLocationTypeAssertionId DeriveV1(
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeIdentifier exactNativeType,
        string sourceFieldPath)
    {
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        ArgumentNullException.ThrowIfNull(exactNativeType);
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("knowledge-record-id", knowledgeRecordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddExactNativeIdentifier("exact-native-type", exactNativeType);
        writer.AddString("source-field-path", sourceFieldPath);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct LocationSemanticClassificationAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "location-semantic-classification-assertion";

    [JsonConstructor]
    public LocationSemanticClassificationAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static LocationSemanticClassificationAssertionId DeriveV1(
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeLocationTypeAssertionId? sourceNativeTypeAssertionId,
        LocationSemanticRoleId roleId,
        LocationSemanticVocabularyVersion vocabularyVersion,
        string classificationMethodId,
        string classificationMethodVersion,
        string sourceFieldPath)
    {
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(roleId.Value, nameof(roleId));
        classificationMethodId = CanonicalKnowledgeContract.RequireText(classificationMethodId, nameof(classificationMethodId));
        classificationMethodVersion = CanonicalKnowledgeContract.RequireText(
            classificationMethodVersion,
            nameof(classificationMethodVersion));
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        if (sourceNativeTypeAssertionId is { } nativeTypeId)
            CanonicalKnowledgeContract.RequireIdentifier(nativeTypeId.Value, nameof(sourceNativeTypeAssertionId));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("knowledge-record-id", knowledgeRecordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddOptionalString("source-native-type-assertion-id", sourceNativeTypeAssertionId?.Value);
        writer.AddString("role-id", roleId.Value);
        writer.AddInt32("vocabulary-version", vocabularyVersion.Value);
        writer.AddString("classification-method-id", classificationMethodId);
        writer.AddString("classification-method-version", classificationMethodVersion);
        writer.AddString("source-field-path", sourceFieldPath);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalRecordLifecycleAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-record-lifecycle-assertion";

    [JsonConstructor]
    public CanonicalRecordLifecycleAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalRecordLifecycleAssertionId DeriveV1(
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalRecordLifecycleState state,
        string sourceNativeLifecycleType,
        string sourceFieldPath)
    {
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        if (!Enum.IsDefined(state)) throw new ArgumentOutOfRangeException(nameof(state));
        sourceNativeLifecycleType = CanonicalKnowledgeContract.RequireText(
            sourceNativeLifecycleType,
            nameof(sourceNativeLifecycleType));
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("knowledge-record-id", knowledgeRecordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddInt32("state", (int)state);
        writer.AddString("source-native-lifecycle-type", sourceNativeLifecycleType);
        writer.AddString("source-field-path", sourceFieldPath);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CorrelatedRelationshipEnvelopeId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "correlated-relationship-envelope";

    [JsonConstructor]
    public CorrelatedRelationshipEnvelopeId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CorrelatedRelationshipEnvelopeId DeriveV1(
        KnowledgeRecordId subjectKnowledgeRecordId,
        RelationshipSemanticId semanticId,
        ImmutableArray<KnowledgeRecordId> candidateTargetKnowledgeRecordIds,
        ImmutableArray<EvidenceClaimContentId> inputRelationshipClaimIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds,
        string methodId,
        string methodVersion,
        CorrelationOutcome outcome)
    {
        CanonicalKnowledgeContract.RequireIdentifier(subjectKnowledgeRecordId.Value, nameof(subjectKnowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(semanticId.Value, nameof(semanticId));
        if (candidateTargetKnowledgeRecordIds.IsDefault || inputRelationshipClaimIds.IsDefault || correlationRecordIds.IsDefault)
            throw new ArgumentException("Correlated relationship collections must be initialized.");
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        methodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        methodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("subject-knowledge-record-id", subjectKnowledgeRecordId.Value);
        writer.AddString("semantic-id", semanticId.Value);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "candidate-targets",
            candidateTargetKnowledgeRecordIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "input-relationship-claims",
            inputRelationshipClaimIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "correlation-records",
            correlationRecordIds.Select(value => value.Value));
        writer.AddString("method-id", methodId);
        writer.AddString("method-version", methodVersion);
        writer.AddInt32("outcome", (int)outcome);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct LocationCoverageExclusionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "location-coverage-exclusion";

    [JsonConstructor]
    public LocationCoverageExclusionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static LocationCoverageExclusionId DeriveV1(
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeIdentifier nativeIdentity,
        string reasonCode,
        string exclusionRuleId,
        string exclusionRuleVersion,
        string sourceFieldPath,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        reasonCode = CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode));
        exclusionRuleId = CanonicalKnowledgeContract.RequireText(exclusionRuleId, nameof(exclusionRuleId));
        exclusionRuleVersion = CanonicalKnowledgeContract.RequireText(exclusionRuleVersion, nameof(exclusionRuleVersion));
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("A coverage exclusion requires authoritative evidence.", nameof(supportingEvidenceReceiptIds));
        var evidence = supportingEvidenceReceiptIds
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (evidence.Any(value => string.IsNullOrWhiteSpace(value.Value)) ||
            evidence.Distinct().Count() != evidence.Length)
            throw new ArgumentException("Coverage exclusion evidence identities must be non-empty and distinct.", nameof(supportingEvidenceReceiptIds));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddExactNativeIdentifier("native-identity", nativeIdentity);
        writer.AddString("reason-code", reasonCode);
        writer.AddString("exclusion-rule-id", exclusionRuleId);
        writer.AddString("exclusion-rule-version", exclusionRuleVersion);
        writer.AddString("source-field-path", sourceFieldPath);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "supporting-evidence",
            evidence.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct LocationCoverageManifestId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "location-coverage-manifest";

    [JsonConstructor]
    public LocationCoverageManifestId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static LocationCoverageManifestId DeriveV1(
        KnowledgeSourceScope sourceScope,
        string exactManifestVersion,
        bool isClosed,
        CatalogValidationSummary qcsValidation,
        ImmutableArray<LocationSourceFamilyDeclaration> sourceFamilies)
    {
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(qcsValidation);
        exactManifestVersion = CanonicalKnowledgeContract.RequireText(exactManifestVersion, nameof(exactManifestVersion));
        if (sourceFamilies.IsDefault) throw new ArgumentException("Source families must be initialized.", nameof(sourceFamilies));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        AddSourceScope(writer, sourceScope);
        writer.AddString("exact-manifest-version", exactManifestVersion);
        writer.AddInt32("is-closed", isClosed ? 1 : 0);
        writer.AddInt32("qcs-status", (int)qcsValidation.Status);
        writer.AddString("qcs-policy-id", qcsValidation.PolicyId);
        writer.AddString("qcs-policy-version", qcsValidation.ExactPolicyVersion);
        writer.AddContentDigest("qcs-result", qcsValidation.ResultDigest);
        var ordered = sourceFamilies.OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("source-family-count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
            ordered[index].AddTo(writer, $"source-family.{index}");
        return new(writer.Derive());
    }

    public override string ToString() => Value;

    private static void AddSourceScope(CanonicalIdentityWriter writer, KnowledgeSourceScope scope)
    {
        writer.AddString("scope.game-id", scope.GameId.Value);
        writer.AddOptionalNativeVersion("scope.game-version", scope.ExactGameVersion);
        writer.AddInt32("scope.kind", (int)scope.ScopeKind);
        writer.AddOptionalExactNativeIdentifier("scope.mod-identity", scope.ExactModIdentity);
        writer.AddOptionalNativeVersion("scope.mod-version", scope.ExactModVersion);
    }
}

public readonly record struct LocationCoverageReportId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "location-coverage-report";

    [JsonConstructor]
    public LocationCoverageReportId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    internal static LocationCoverageReportId DeriveV1(
        LocationCoverageManifestId manifestId,
        LocationCoverageStatus status,
        ImmutableArray<LocationSourceFamilyCoverage> sourceFamilies,
        ImmutableArray<LocationSemanticCategoryCoverage> semanticCategories,
        LocationTerminologyCoverage terminology,
        LocationHierarchyCoverage hierarchy,
        ImmutableArray<LocationRelationshipCoverage> relationships,
        ImmutableArray<LocationUnresolvedCoverage> unresolved)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("manifest-id", manifestId.Value);
        writer.AddInt32("status", (int)status);
        LocationCoverageEncoding.AddSourceFamilies(writer, sourceFamilies);
        LocationCoverageEncoding.AddSemanticCategories(writer, semanticCategories);
        terminology.AddTo(writer, "terminology");
        hierarchy.AddTo(writer, "hierarchy");
        LocationCoverageEncoding.AddRelationships(writer, relationships);
        LocationCoverageEncoding.AddUnresolved(writer, unresolved);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}
