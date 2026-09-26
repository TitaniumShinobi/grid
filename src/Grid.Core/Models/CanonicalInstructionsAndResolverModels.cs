using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct InstructionCategoryId
{
    [JsonConstructor]
    public InstructionCategoryId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public static class InstructionCategories
{
    public static InstructionCategoryId Requirements { get; } = new("grid.instructions.requirements");
    public static InstructionCategoryId Installation { get; } = new("grid.instructions.installation");
    public static InstructionCategoryId Configuration { get; } = new("grid.instructions.configuration");
    public static InstructionCategoryId Usage { get; } = new("grid.instructions.usage");
    public static InstructionCategoryId LoadOrderCompatibility { get; } = new("grid.instructions.load-order-compatibility");
    public static InstructionCategoryId Update { get; } = new("grid.instructions.update");
    public static InstructionCategoryId Uninstall { get; } = new("grid.instructions.uninstall");
    public static InstructionCategoryId Troubleshooting { get; } = new("grid.instructions.troubleshooting");
}

public readonly record struct InstructionAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "instruction-assertion";
    [JsonConstructor]
    public InstructionAssertionId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);
    public string Value { get; }

    public static InstructionAssertionId DeriveV1(
        CatalogSourceRevisionId sourceRevisionId,
        KnowledgeSourceScope scope,
        SourceNativeIdentifier nativeIdentity,
        InstructionCategoryId? categoryId,
        InstructionRetentionMode retentionMode,
        ContentDigest contentDigest,
        InstructionSourceLocator sourceLocator,
        string? languageTag,
        string? categoryMethodId,
        string? categoryMethodVersion,
        ImmutableArray<KnowledgeRecordId> applicableRecordIds,
        ImmutableArray<CatalogPackageId> applicablePackageIds)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        ArgumentNullException.ThrowIfNull(sourceLocator);
        if (applicableRecordIds.IsDefault || applicablePackageIds.IsDefault)
            throw new ArgumentException("Instruction applicability coordinates must be initialized.");
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("scope.game-id", scope.GameId.Value);
        writer.AddOptionalNativeVersion("scope.game-version", scope.ExactGameVersion);
        writer.AddInt32("scope.kind", (int)scope.ScopeKind);
        writer.AddOptionalExactNativeIdentifier("scope.mod-identity", scope.ExactModIdentity);
        writer.AddOptionalNativeVersion("scope.mod-version", scope.ExactModVersion);
        writer.AddExactNativeIdentifier("native-identity", nativeIdentity);
        writer.AddOptionalString("category-id", categoryId?.Value);
        writer.AddInt32("retention-mode", (int)retentionMode);
        writer.AddContentDigest("content-digest", contentDigest);
        writer.AddOptionalString("source-artifact-id", sourceLocator.ArtifactId?.Value);
        writer.AddExactNativeIdentifier("source-object-identity", sourceLocator.NativeObjectIdentity);
        writer.AddString("exact-locator", sourceLocator.ExactFieldPathOrFragment);
        writer.AddOptionalNativeVersion("provider-object-revision", sourceLocator.ProviderObjectRevision);
        writer.AddOptionalString("language-tag", languageTag);
        writer.AddOptionalString("category-method-id", categoryMethodId);
        writer.AddOptionalString("category-method-version", categoryMethodVersion);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "applicable-records", applicableRecordIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer, "applicable-packages", applicablePackageIds.Select(value => value.Value));
        return new(writer.Derive());
    }
    public override string ToString() => Value;
}

public readonly record struct InstructionConflictGroupId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "instruction-conflict-group";
    [JsonConstructor]
    public InstructionConflictGroupId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);
    public string Value { get; }
    public static InstructionConflictGroupId DeriveV1(
        ImmutableArray<InstructionAssertionId> memberIds,
        string methodId,
        string methodVersion)
    {
        var ordered = memberIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Length < 2 || ordered.Distinct().Count() != ordered.Length)
            throw new ArgumentException("An instruction conflict requires at least two distinct assertions.", nameof(memberIds));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("method-id", CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId)));
        writer.AddString("method-version", CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion)));
        writer.AddInt32("member-count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++) writer.AddString($"member.{index}", ordered[index].Value);
        return new(writer.Derive());
    }
    public override string ToString() => Value;
}

public enum InstructionRetentionMode { Verbatim = 0, StructuredFields = 1, ReferenceOnly = 2 }

public sealed record InstructionStructuredField
{
    public InstructionStructuredField(string exactKey, string exactValue)
    {
        ArgumentNullException.ThrowIfNull(exactKey);
        ArgumentNullException.ThrowIfNull(exactValue);
        CanonicalUtf8.Validate(exactKey);
        CanonicalUtf8.Validate(exactValue);
        ExactKey = exactKey;
        ExactValue = exactValue;
    }
    public string ExactKey { get; }
    public string ExactValue { get; }
}

public sealed record InstructionContent
{
    public InstructionContent(
        InstructionRetentionMode retentionMode,
        string? verbatimText,
        ImmutableArray<InstructionStructuredField> structuredFields,
        ContentDigest contentDigest)
    {
        if (!Enum.IsDefined(retentionMode)) throw new ArgumentOutOfRangeException(nameof(retentionMode));
        if (structuredFields.IsDefault || structuredFields.Any(value => value is null))
            throw new ArgumentException("Instruction fields must be initialized.", nameof(structuredFields));
        if (verbatimText is not null) CanonicalUtf8.Validate(verbatimText);
        if (retentionMode == InstructionRetentionMode.Verbatim && verbatimText is null)
            throw new ArgumentException("Verbatim retention requires exact source text.", nameof(verbatimText));
        if (retentionMode == InstructionRetentionMode.StructuredFields && structuredFields.IsEmpty)
            throw new ArgumentException("Structured retention requires exact fields.", nameof(structuredFields));
        if (retentionMode == InstructionRetentionMode.ReferenceOnly && (verbatimText is not null || !structuredFields.IsEmpty))
            throw new ArgumentException("Reference-only instructions cannot retain source prose or fields.");
        var orderedFields = structuredFields.OrderBy(value => value.ExactKey, StringComparer.Ordinal)
            .ThenBy(value => value.ExactValue, StringComparer.Ordinal).ToImmutableArray();
        var expectedDigest = retentionMode switch
        {
            InstructionRetentionMode.Verbatim => ContentDigest.ComputeSha256(CanonicalUtf8.GetBytes(verbatimText!)),
            InstructionRetentionMode.StructuredFields => ComputeStructuredDigest(orderedFields),
            _ => contentDigest,
        };
        if (contentDigest != expectedDigest)
            throw new ArgumentException("Instruction content digest does not match the exact retained content.", nameof(contentDigest));
        RetentionMode = retentionMode;
        VerbatimText = verbatimText;
        StructuredFields = orderedFields;
        ContentDigest = contentDigest;
    }
    public InstructionRetentionMode RetentionMode { get; }
    public string? VerbatimText { get; }
    public ImmutableArray<InstructionStructuredField> StructuredFields { get; }
    public ContentDigest ContentDigest { get; }

    public static InstructionContent FromVerbatim(string exactText) => new(
        InstructionRetentionMode.Verbatim,
        exactText,
        [],
        ContentDigest.ComputeSha256(CanonicalUtf8.GetBytes(exactText)));

    public static InstructionContent FromStructuredFields(ImmutableArray<InstructionStructuredField> fields)
    {
        if (fields.IsDefault) throw new ArgumentException("Instruction fields must be initialized.", nameof(fields));
        var ordered = fields.OrderBy(value => value.ExactKey, StringComparer.Ordinal)
            .ThenBy(value => value.ExactValue, StringComparer.Ordinal).ToImmutableArray();
        return new(InstructionRetentionMode.StructuredFields, null, ordered, ComputeStructuredDigest(ordered));
    }

    public static InstructionContent ReferenceOnly(ContentDigest exactSourceContentDigest) =>
        new(InstructionRetentionMode.ReferenceOnly, null, [], exactSourceContentDigest);

    private static ContentDigest ComputeStructuredDigest(ImmutableArray<InstructionStructuredField> fields)
    {
        using var stream = new MemoryStream();
        WriteInt32(stream, fields.Length);
        foreach (var field in fields)
        {
            WriteFramedUtf8(stream, field.ExactKey);
            WriteFramedUtf8(stream, field.ExactValue);
        }
        return ContentDigest.ComputeSha256(stream.ToArray());
    }

    private static void WriteFramedUtf8(Stream stream, string value)
    {
        var bytes = CanonicalUtf8.GetBytes(value);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }
}

public sealed record InstructionSourceLocator(
    SourceArtifactId? ArtifactId,
    SourceNativeIdentifier NativeObjectIdentity,
    string ExactFieldPathOrFragment,
    SourceNativeVersion? ProviderObjectRevision)
{
    public string ExactFieldPathOrFragment { get; } =
        CanonicalKnowledgeContract.RequireText(ExactFieldPathOrFragment, nameof(ExactFieldPathOrFragment));
}

public sealed record InstructionAssertion
{
    [JsonConstructor]
    public InstructionAssertion(
        InstructionAssertionId id,
        CatalogSourceRevisionId sourceRevisionId,
        KnowledgeSourceScope sourceScope,
        SourceNativeIdentifier nativeIdentity,
        InstructionCategoryId? categoryId,
        string? categoryMappingMethodId,
        string? categoryMappingMethodVersion,
        string? exactLanguageTag,
        InstructionContent content,
        InstructionSourceLocator sourceLocator,
        ImmutableArray<KnowledgeRecordId> applicableRecordIds,
        ImmutableArray<CatalogPackageId> applicablePackageIds)
    {
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sourceLocator);
        if ((categoryId is null && (categoryMappingMethodId is not null || categoryMappingMethodVersion is not null)) ||
            (categoryId is not null && (categoryMappingMethodId is null || categoryMappingMethodVersion is null)))
            throw new ArgumentException("A mapped instruction category requires method ID and version, and an unclassified assertion cannot claim them.");
        if (exactLanguageTag is not null) CanonicalUtf8.Validate(exactLanguageTag);
        var normalizedRecordIds = Normalize(applicableRecordIds, value => value.Value, nameof(applicableRecordIds));
        var normalizedPackageIds = Normalize(applicablePackageIds, value => value.Value, nameof(applicablePackageIds));
        var expected = InstructionAssertionId.DeriveV1(
            sourceRevisionId, sourceScope, nativeIdentity, categoryId, content.RetentionMode,
            content.ContentDigest, sourceLocator, exactLanguageTag,
            categoryMappingMethodId, categoryMappingMethodVersion,
            normalizedRecordIds, normalizedPackageIds);
        if (id != expected) throw new ArgumentException("Instruction assertion ID does not match its exact claim.", nameof(id));
        Id = id;
        SourceRevisionId = sourceRevisionId;
        SourceScope = sourceScope;
        NativeIdentity = nativeIdentity;
        CategoryId = categoryId;
        CategoryMappingMethodId = categoryMappingMethodId;
        CategoryMappingMethodVersion = categoryMappingMethodVersion;
        ExactLanguageTag = exactLanguageTag;
        Content = content;
        SourceLocator = sourceLocator;
        ApplicableRecordIds = normalizedRecordIds;
        ApplicablePackageIds = normalizedPackageIds;
    }

    public InstructionAssertionId Id { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public KnowledgeSourceScope SourceScope { get; }
    public SourceNativeIdentifier NativeIdentity { get; }
    public InstructionCategoryId? CategoryId { get; }
    public string? CategoryMappingMethodId { get; }
    public string? CategoryMappingMethodVersion { get; }
    public string? ExactLanguageTag { get; }
    public InstructionContent Content { get; }
    public InstructionSourceLocator SourceLocator { get; }
    public ImmutableArray<KnowledgeRecordId> ApplicableRecordIds { get; }
    public ImmutableArray<CatalogPackageId> ApplicablePackageIds { get; }

    private static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values, Func<T, string> key, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", name);
        var ordered = values.OrderBy(key, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(key).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection values must be distinct.", name);
        return ordered;
    }
}

public sealed record InstructionConflictGroup
{
    public InstructionConflictGroup(
        InstructionConflictGroupId id,
        ImmutableArray<InstructionAssertionId> memberIds,
        string methodId,
        string methodVersion)
    {
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        MemberIds = memberIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (InstructionConflictGroupId.DeriveV1(MemberIds, MethodId, MethodVersion) != id)
            throw new ArgumentException("Instruction conflict ID does not match its members.", nameof(id));
        Id = id;
    }
    public InstructionConflictGroupId Id { get; }
    public ImmutableArray<InstructionAssertionId> MemberIds { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
}

public readonly record struct InstructionEvidenceBindingId
{
    public const int LegacyAlgorithmVersion = 1;
    public const int EvidenceClassAlgorithmVersion = 2;
    public const int CurrentAlgorithmVersion = 3;
    private const string Domain = "instruction-evidence-binding";
    [JsonConstructor]
    public InstructionEvidenceBindingId(string value)
    {
        try
        {
            Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);
            AlgorithmVersion = CurrentAlgorithmVersion;
        }
        catch (ArgumentException)
        {
            try
            {
                Value = CanonicalIdentityV1.Validate(value, Domain, EvidenceClassAlgorithmVersion);
                AlgorithmVersion = EvidenceClassAlgorithmVersion;
            }
            catch (ArgumentException)
            {
                Value = CanonicalIdentityV1.Validate(value, Domain, LegacyAlgorithmVersion);
                AlgorithmVersion = LegacyAlgorithmVersion;
            }
        }
    }
    public string Value { get; }
    [JsonIgnore]
    public int AlgorithmVersion { get; }
    public static InstructionEvidenceBindingId DeriveV1(
        EvidenceReceiptId receiptId,
        InstructionAssertionId assertionId,
        CatalogSourceRevisionId sourceRevisionId,
        string exactLocator,
        EvidenceClaimContentId claimContentId)
    {
        var writer = new CanonicalIdentityWriter(Domain, LegacyAlgorithmVersion);
        writer.AddString("receipt-id", receiptId.Value);
        writer.AddString("assertion-id", assertionId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("exact-locator", CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator)));
        writer.AddString("claim-content-id", claimContentId.Value);
        return new(writer.Derive());
    }

    public static InstructionEvidenceBindingId DeriveV2(
        EvidenceReceiptId receiptId,
        InstructionAssertionId assertionId,
        CatalogSourceRevisionId sourceRevisionId,
        string exactLocator,
        EvidenceClaimContentId claimContentId,
        EvidenceVerificationKind verification)
    {
        if (!Enum.IsDefined(verification)) throw new ArgumentOutOfRangeException(nameof(verification));
        var writer = new CanonicalIdentityWriter(Domain, EvidenceClassAlgorithmVersion);
        writer.AddString("receipt-id", receiptId.Value);
        writer.AddString("assertion-id", assertionId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("exact-locator", CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator)));
        writer.AddString("claim-content-id", claimContentId.Value);
        writer.AddInt32("verification", (int)verification);
        return new(writer.Derive());
    }

    public static InstructionEvidenceBindingId DeriveV3(
        EvidenceReceiptId receiptId,
        InstructionAssertionId assertionId,
        CatalogSourceRevisionId sourceRevisionId,
        string exactLocator,
        EvidenceClaimContentId claimContentId,
        EvidenceVerificationKind verification)
    {
        if (!Enum.IsDefined(verification)) throw new ArgumentOutOfRangeException(nameof(verification));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("receipt-id", receiptId.Value);
        writer.AddString("assertion-id", assertionId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("exact-locator", CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator)));
        writer.AddString("claim-content-id", claimContentId.Value);
        writer.AddInt32("verification", (int)verification);
        writer.AddInt32("evidence-contract-version", CurrentAlgorithmVersion);
        return new(writer.Derive());
    }
}

public sealed record InstructionEvidenceBinding
{
    public InstructionEvidenceBinding(
        InstructionEvidenceBindingId id,
        EvidenceReceiptId evidenceReceiptId,
        InstructionAssertionId instructionAssertionId,
        CatalogSourceRevisionId sourceRevisionId,
        string exactLocator,
        EvidenceClaimContentId claimContentId,
        EvidenceVerificationKind? verification = null)
    {
        if (verification is { } declaredVerification && !Enum.IsDefined(declaredVerification))
            throw new ArgumentOutOfRangeException(nameof(verification));
        ExactLocator = CanonicalKnowledgeContract.RequireText(exactLocator, nameof(exactLocator));
        var expected = id.AlgorithmVersion switch
        {
            InstructionEvidenceBindingId.LegacyAlgorithmVersion when verification is null =>
                InstructionEvidenceBindingId.DeriveV1(
                    evidenceReceiptId, instructionAssertionId, sourceRevisionId, ExactLocator, claimContentId),
            InstructionEvidenceBindingId.EvidenceClassAlgorithmVersion when verification is { } v2 =>
                InstructionEvidenceBindingId.DeriveV2(
                    evidenceReceiptId, instructionAssertionId, sourceRevisionId, ExactLocator, claimContentId, v2),
            InstructionEvidenceBindingId.CurrentAlgorithmVersion when verification is { } v3 =>
                InstructionEvidenceBindingId.DeriveV3(
                    evidenceReceiptId, instructionAssertionId, sourceRevisionId, ExactLocator, claimContentId, v3),
            _ => default,
        };
        if (expected != id)
            throw new ArgumentException("Instruction evidence binding ID does not match its exact claim.", nameof(id));
        Id = id;
        EvidenceReceiptId = evidenceReceiptId;
        InstructionAssertionId = instructionAssertionId;
        SourceRevisionId = sourceRevisionId;
        ClaimContentId = claimContentId;
        Verification = verification;
    }
    public InstructionEvidenceBindingId Id { get; }
    public EvidenceReceiptId EvidenceReceiptId { get; }
    public InstructionAssertionId InstructionAssertionId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public string ExactLocator { get; }
    public EvidenceClaimContentId ClaimContentId { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvidenceVerificationKind? Verification { get; }
}

public sealed record InstructionQuery(
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    ImmutableArray<InstructionCategoryId> CategoryIds,
    KnowledgeRecordId? KnowledgeRecordId,
    CatalogSourceRevisionId? SourceRevisionId,
    string? ExactLanguageTag,
    bool IncludeConflicts);

public sealed record InstructionQueryResult(
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    ImmutableArray<InstructionAssertion> Assertions,
    ImmutableArray<InstructionConflictGroup> ConflictGroups,
    KnowledgeCoverageState CoverageState);

public enum ResolverEvidenceFeatureKind
{
    ExactSourceNativeIdentity = 0,
    ExactPluginRecordIdentity = 1,
    ExactProviderObjectRevision = 2,
    ExactArtifactRecordLocator = 3,
    DeterministicCorrelation = 4,
    ExactGameVersion = 5,
    ExactModIdentityVersion = 6,
    ExactApplicability = 7,
    FileVerifiedIdentity = 8,
    ReferenceVerifiedIdentity = 9,
    ExactPrimaryTerminology = 10,
    ExactAuthoritativeAlias = 11,
}

public sealed record ResolverEvidenceFeature
{
    public ResolverEvidenceFeature(
        KnowledgeRecordId candidateId,
        ResolverEvidenceFeatureKind kind,
        string exactCoordinate,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ExactCoordinate = CanonicalKnowledgeContract.RequireText(exactCoordinate, nameof(exactCoordinate));
        CandidateId = candidateId;
        Kind = kind;
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("A resolver feature requires supporting evidence.", nameof(supportingEvidenceReceiptIds));
        SupportingEvidenceReceiptIds = supportingEvidenceReceiptIds.OrderBy(value => value.Value, StringComparer.Ordinal)
            .Distinct().ToImmutableArray();
    }
    public KnowledgeRecordId CandidateId { get; }
    public ResolverEvidenceFeatureKind Kind { get; }
    public string ExactCoordinate { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }
}

public sealed record DeterministicResolverPolicy(
    string PolicyId,
    string ExactVersion,
    int MinimumScore,
    int MinimumStrongFeatureWeight,
    int RequiredWinningMargin)
{
    public static DeterministicResolverPolicy V1 { get; } = new(
        "grid.canonical-resolver", "1", 1300, 700, 300);

    public int Weight(ResolverEvidenceFeatureKind kind) => kind switch
    {
        ResolverEvidenceFeatureKind.ExactSourceNativeIdentity => 1000,
        ResolverEvidenceFeatureKind.ExactPluginRecordIdentity => 1000,
        ResolverEvidenceFeatureKind.ExactProviderObjectRevision => 900,
        ResolverEvidenceFeatureKind.ExactArtifactRecordLocator => 900,
        ResolverEvidenceFeatureKind.DeterministicCorrelation => 700,
        ResolverEvidenceFeatureKind.ExactGameVersion => 300,
        ResolverEvidenceFeatureKind.ExactModIdentityVersion => 300,
        ResolverEvidenceFeatureKind.ExactApplicability => 200,
        ResolverEvidenceFeatureKind.FileVerifiedIdentity => 200,
        ResolverEvidenceFeatureKind.ReferenceVerifiedIdentity => 75,
        ResolverEvidenceFeatureKind.ExactPrimaryTerminology => 100,
        ResolverEvidenceFeatureKind.ExactAuthoritativeAlias => 75,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed record DeterministicResolverInput(
    DeterministicResolverPolicy Policy,
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    GameId RequiredGameId,
    KnowledgeKind RequiredKnowledgeKind,
    ImmutableArray<KnowledgeRecordId> CandidateKnowledgeRecordIds,
    ImmutableArray<ResolverEvidenceFeature> Features);

public sealed record ResolverCandidateScore(
    KnowledgeRecordId KnowledgeRecordId,
    int Score,
    int StrongestFeatureWeight,
    ImmutableArray<ResolverEvidenceFeature> CountedFeatures,
    ImmutableArray<ResolverFeatureEvaluation> RejectedFeatures);

public enum ResolverFeatureRejectionReason
{
    None = 0,
    EvidenceReceiptNotFound = 1,
    ReceiptNotBoundToCandidate = 2,
    EvidenceClassMismatch = 3,
    EvidenceCoordinateMismatch = 4,
    DuplicateFeature = 5,
    DuplicateEvidence = 6,
    AuthoritativeConflict = 7,
}

public sealed record ResolverFeatureEvaluation(
    ResolverEvidenceFeature Feature,
    ResolverFeatureRejectionReason RejectionReason);

public enum DeterministicResolverOutcome { Resolved = 0, Unresolved = 1 }
public enum DeterministicResolverUnresolvedReason
{
    None = 0,
    NoEligibleCandidate = 1,
    BelowThreshold = 2,
    NoStrongIdentity = 3,
    Tie = 4,
    InsufficientMargin = 5,
    AuthoritativeIdentityConflict = 6,
}

public sealed record DeterministicResolverExplanation(
    string PolicyId,
    string PolicyVersion,
    int MinimumScore,
    int MinimumStrongFeatureWeight,
    int RequiredMargin,
    ImmutableArray<ResolverCandidateScore> CandidateScores,
    DeterministicResolverUnresolvedReason UnresolvedReason,
    int? WinningScore,
    int? RunnerUpScore,
    ImmutableArray<string> AuthoritativeConflictCoordinates,
    string DeterministicDigest);

public sealed record DeterministicResolverResult(
    DeterministicResolverOutcome Outcome,
    KnowledgeRecordId? ResolvedKnowledgeRecordId,
    DeterministicResolverExplanation Explanation);

public readonly record struct ModelCanonicalProposalId
{
    [JsonConstructor]
    public ModelCanonicalProposalId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }
    public string Value { get; }
}

public sealed record ModelCanonicalProposal(
    ModelCanonicalProposalId ProposalId,
    KnowledgeKind RequestedKnowledgeKind,
    ImmutableArray<string> VerbatimInputSpanReferences,
    ImmutableArray<KnowledgeRecordId> ProposedExistingKnowledgeRecordIds,
    string ModelExecutionReference);

public sealed record CanonicalProposalValidationResult(
    ModelCanonicalProposalId ProposalId,
    ImmutableArray<KnowledgeRecordId> ValidExistingCandidateIds,
    ImmutableArray<KnowledgeRecordId> RejectedCandidateIds);
