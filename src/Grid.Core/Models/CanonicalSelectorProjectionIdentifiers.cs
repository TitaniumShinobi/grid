using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct CanonicalSelectorProjectionPolicyId
{
    [JsonConstructor]
    public CanonicalSelectorProjectionPolicyId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CanonicalOrganizationalSemanticId
{
    [JsonConstructor]
    public CanonicalOrganizationalSemanticId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CanonicalSemanticRoleId
{
    [JsonConstructor]
    public CanonicalSemanticRoleId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct CanonicalOrganizationalNodeId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-organizational-node";

    [JsonConstructor]
    public CanonicalOrganizationalNodeId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalOrganizationalNodeId DeriveV1(
        CanonicalSelectorProjectionPolicyId policyId,
        string policyVersion,
        KnowledgeKind knowledgeKind,
        CanonicalOrganizationalSemanticId semanticId,
        SourceNativeIdentifier? sourceValue)
    {
        policyVersion = CanonicalKnowledgeContract.RequireText(policyVersion, nameof(policyVersion));
        if (!Enum.IsDefined(knowledgeKind)) throw new ArgumentOutOfRangeException(nameof(knowledgeKind));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("policy-id", policyId.Value);
        writer.AddString("policy-version", policyVersion);
        writer.AddInt32("knowledge-kind", (int)knowledgeKind);
        writer.AddString("semantic-id", semanticId.Value);
        writer.AddOptionalExactNativeIdentifier("source-value", sourceValue);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalNavigationNodeId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-navigation-node";

    [JsonConstructor]
    public CanonicalNavigationNodeId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalNavigationNodeId ForOrganizationalNode(CanonicalOrganizationalNodeId id)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddInt32("node-kind", 0);
        writer.AddString("organizational-node-id", id.Value);
        return new(writer.Derive());
    }

    public static CanonicalNavigationNodeId ForCanonicalRecord(KnowledgeRecordId id)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddInt32("node-kind", 1);
        writer.AddString("knowledge-record-id", id.Value);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalNavigationPathId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-navigation-path";

    [JsonConstructor]
    public CanonicalNavigationPathId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalNavigationPathId DeriveV1(
        CatalogRevisionId catalogRevisionId,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicyId policyId,
        string policyVersion,
        KnowledgeKind knowledgeKind,
        ImmutableArray<CanonicalNavigationNodeId> segments)
    {
        if (segments.IsDefaultOrEmpty)
            throw new ArgumentException("A navigation path requires at least its root segment.", nameof(segments));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("catalog-revision-id", catalogRevisionId.Value);
        writer.AddString("catalog-composition-id", catalogCompositionId.Value);
        writer.AddString("policy-id", policyId.Value);
        writer.AddString("policy-version", CanonicalKnowledgeContract.RequireText(policyVersion, nameof(policyVersion)));
        writer.AddInt32("knowledge-kind", (int)knowledgeKind);
        writer.AddInt32("segment-count", segments.Length);
        for (var index = 0; index < segments.Length; index++)
            writer.AddString($"segment.{index}", segments[index].Value);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalSemanticClassificationAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-semantic-classification-assertion";

    [JsonConstructor]
    public CanonicalSemanticClassificationAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalSemanticClassificationAssertionId DeriveV1(
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalSemanticRoleId roleId,
        string vocabularyId,
        string vocabularyVersion,
        string methodId,
        string methodVersion,
        string sourceFieldPath)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("record-id", recordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("role-id", roleId.Value);
        writer.AddString("vocabulary-id", CanonicalKnowledgeContract.RequireText(vocabularyId, nameof(vocabularyId)));
        writer.AddString("vocabulary-version", CanonicalKnowledgeContract.RequireText(vocabularyVersion, nameof(vocabularyVersion)));
        writer.AddString("method-id", CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId)));
        writer.AddString("method-version", CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion)));
        writer.AddString("source-field-path", CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath)));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalRecordContributionAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-record-contribution-assertion";

    [JsonConstructor]
    public CanonicalRecordContributionAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalRecordContributionAssertionId DeriveV1(
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalRecordContributionKind contributionKind,
        KnowledgeRecordId? originRecordId,
        SourceNativeIdentifier? modIdentity,
        SourceNativeVersion? modVersion,
        string methodId,
        string methodVersion,
        string sourceFieldPath)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("record-id", recordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddInt32("contribution-kind", (int)contributionKind);
        writer.AddOptionalString("origin-record-id", originRecordId?.Value);
        writer.AddOptionalExactNativeIdentifier("mod-identity", modIdentity);
        writer.AddOptionalNativeVersion("mod-version", modVersion);
        writer.AddString("method-id", CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId)));
        writer.AddString("method-version", CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion)));
        writer.AddString("source-field-path", CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath)));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CanonicalOrganizationalValueAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "canonical-organizational-value-assertion";

    [JsonConstructor]
    public CanonicalOrganizationalValueAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CanonicalOrganizationalValueAssertionId DeriveV1(
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalOrganizationalSemanticId dimensionId,
        SourceNativeIdentifier exactValueIdentity,
        string? verbatimDisplayValue,
        string methodId,
        string methodVersion,
        string sourceFieldPath)
    {
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("record-id", recordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("dimension-id", dimensionId.Value);
        writer.AddExactNativeIdentifier("value-identity", exactValueIdentity);
        writer.AddOptionalString("verbatim-display-value", verbatimDisplayValue);
        writer.AddString("method-id", CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId)));
        writer.AddString("method-version", CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion)));
        writer.AddString("source-field-path", CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath)));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}
