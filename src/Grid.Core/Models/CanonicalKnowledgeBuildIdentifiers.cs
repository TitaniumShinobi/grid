using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct KnowledgeAdapterId
{
    [JsonConstructor]
    public KnowledgeAdapterId(string value)
    {
        Value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(Value);
    }

    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct KnowledgeAdapterRevisionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "knowledge-adapter-revision";

    [JsonConstructor]
    public KnowledgeAdapterRevisionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static KnowledgeAdapterRevisionId DeriveV1(
        KnowledgeAdapterId adapterId,
        string exactAdapterVersion,
        ContentDigest adapterArtifactDigest,
        int adapterContractVersion,
        string mappingRulesVersion)
    {
        CanonicalKnowledgeContract.RequireIdentifier(adapterId.Value, nameof(adapterId));
        exactAdapterVersion = CanonicalKnowledgeContract.RequireText(exactAdapterVersion, nameof(exactAdapterVersion));
        mappingRulesVersion = CanonicalKnowledgeContract.RequireText(mappingRulesVersion, nameof(mappingRulesVersion));
        CanonicalUtf8.Validate(exactAdapterVersion);
        CanonicalUtf8.Validate(mappingRulesVersion);
        if (adapterContractVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(adapterContractVersion));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("adapter-id", adapterId.Value);
        writer.AddString("exact-adapter-version", exactAdapterVersion);
        writer.AddContentDigest("adapter-artifact-digest", adapterArtifactDigest);
        writer.AddInt32("adapter-contract-version", adapterContractVersion);
        writer.AddString("mapping-rules-version", mappingRulesVersion);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CatalogPayloadDigest
{
    public const int LegacyAlgorithmVersion = 1;
    public const int PreviousAlgorithmVersion = 2;
    public const int CurrentAlgorithmVersion = 3;
    public const int LocationContractAlgorithmVersion = 4;
    public const int ProjectionContractAlgorithmVersion = 5;
    public const int CrossSourceAssertionAlgorithmVersion = 6;
    private const string Domain = "catalog-payload";

    [JsonConstructor]
    public CatalogPayloadDigest(string value)
    {
        Value = ValidateKnownVersion(value, Domain);
    }

    public string Value { get; }
    internal static CatalogPayloadDigest FromCanonicalWriter(CanonicalIdentityWriter writer) => new(writer.Derive());
    public override string ToString() => Value;

    internal static string ValidateKnownVersion(string? value, string domain)
    {
        if (value?.StartsWith($"grid.{domain}.v{LegacyAlgorithmVersion}.sha256.", StringComparison.Ordinal) == true)
            return CanonicalIdentityV1.Validate(value, domain, LegacyAlgorithmVersion);
        if (value?.StartsWith($"grid.{domain}.v{PreviousAlgorithmVersion}.sha256.", StringComparison.Ordinal) == true)
            return CanonicalIdentityV1.Validate(value, domain, PreviousAlgorithmVersion);
        if (value?.StartsWith($"grid.{domain}.v{CurrentAlgorithmVersion}.sha256.", StringComparison.Ordinal) == true)
            return CanonicalIdentityV1.Validate(value, domain, CurrentAlgorithmVersion);
        if (value?.StartsWith($"grid.{domain}.v{LocationContractAlgorithmVersion}.sha256.", StringComparison.Ordinal) == true)
            return CanonicalIdentityV1.Validate(value, domain, LocationContractAlgorithmVersion);
        if (value?.StartsWith($"grid.{domain}.v{ProjectionContractAlgorithmVersion}.sha256.", StringComparison.Ordinal) == true)
            return CanonicalIdentityV1.Validate(value, domain, ProjectionContractAlgorithmVersion);
        return CanonicalIdentityV1.Validate(value, domain, CrossSourceAssertionAlgorithmVersion);
    }
}

public readonly record struct CatalogRevisionId
{
    public const int LegacyAlgorithmVersion = 1;
    public const int PreviousAlgorithmVersion = 2;
    public const int CurrentAlgorithmVersion = 3;
    public const int LocationContractAlgorithmVersion = 4;
    public const int ProjectionContractAlgorithmVersion = 5;
    public const int CrossSourceAssertionAlgorithmVersion = 6;
    private const string Domain = "catalog-revision";

    [JsonConstructor]
    public CatalogRevisionId(string value)
    {
        Value = CatalogPayloadDigest.ValidateKnownVersion(value, Domain);
    }

    public string Value { get; }

    public static CatalogRevisionId DeriveV1(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        CanonicalUtf8.Validate(compositionPolicyVersion);

        var writer = new CanonicalIdentityWriter(Domain, LegacyAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public static CatalogRevisionId DeriveV2(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        CanonicalUtf8.Validate(compositionPolicyVersion);

        var writer = new CanonicalIdentityWriter(Domain, PreviousAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        writer.AddInt32("effective-coverage", (int)effectiveCoverage);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public static CatalogRevisionId DeriveV3(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        CanonicalUtf8.Validate(compositionPolicyVersion);

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        writer.AddInt32("effective-coverage", (int)effectiveCoverage);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public static CatalogRevisionId DeriveV4(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        CanonicalUtf8.Validate(compositionPolicyVersion);

        var writer = new CanonicalIdentityWriter(Domain, LocationContractAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        writer.AddInt32("effective-coverage", (int)effectiveCoverage);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public static CatalogRevisionId DeriveV5(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        var writer = new CanonicalIdentityWriter(Domain, ProjectionContractAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        writer.AddInt32("effective-coverage", (int)effectiveCoverage);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public static CatalogRevisionId DeriveV6(
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionId> adapterRevisionIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest)
    {
        ArgumentNullException.ThrowIfNull(gameScope);
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ValidateInitialized(adapterRevisionIds, nameof(adapterRevisionIds));
        ValidateInitialized(sourceRevisionIds, nameof(sourceRevisionIds));
        ValidateInitialized(requiredBasePackageIds, nameof(requiredBasePackageIds));
        compositionPolicyVersion = CanonicalKnowledgeContract.RequireText(compositionPolicyVersion, nameof(compositionPolicyVersion));
        CanonicalKnowledgeContract.RequireIdentifier(payloadDigest.Value, nameof(payloadDigest));
        var writer = new CanonicalIdentityWriter(Domain, CrossSourceAssertionAlgorithmVersion);
        writer.AddInt32("package-kind", (int)packageKind);
        CanonicalKnowledgePackageEncoding.AddGameScope(writer, "game-scope", gameScope);
        CanonicalKnowledgePackageEncoding.AddOptionalModScope(writer, "mod-scope", modScope);
        writer.AddInt32("effective-coverage", (int)effectiveCoverage);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "adapter-revisions", adapterRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "source-revisions", sourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "required-base-packages", requiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", compositionPolicyVersion);
        writer.AddString("payload-digest", payloadDigest.Value);
        return new(writer.Derive());
    }

    public override string ToString() => Value;

    private static void ValidateInitialized<T>(ImmutableArray<T> values, string parameterName)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", parameterName);
    }
}

public readonly record struct CatalogPackageId
{
    public const int LegacyAlgorithmVersion = 1;
    public const int PreviousAlgorithmVersion = 2;
    public const int CurrentAlgorithmVersion = 3;
    public const int LocationContractAlgorithmVersion = 4;
    public const int ProjectionContractAlgorithmVersion = 5;
    public const int CrossSourceAssertionAlgorithmVersion = 6;
    private const string Domain = "catalog-package";

    [JsonConstructor]
    public CatalogPackageId(string value)
    {
        Value = CatalogPayloadDigest.ValidateKnownVersion(value, Domain);
    }

    public string Value { get; }
    internal static CatalogPackageId FromCanonicalWriter(CanonicalIdentityWriter writer) => new(writer.Derive());
    public override string ToString() => Value;
}

public readonly record struct CatalogArchiveDigest
{
    [JsonConstructor]
    public CatalogArchiveDigest(ContentDigest digest) =>
        Digest = new ContentDigest(digest.Algorithm, digest.HexValue);

    public ContentDigest Digest { get; }
    public static CatalogArchiveDigest ComputeSha256(ReadOnlySpan<byte> bytes) => new(ContentDigest.ComputeSha256(bytes));
    public override string ToString() => Digest.ToString();
}

public readonly record struct CorrelationRecordId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "correlation-record";

    [JsonConstructor]
    public CorrelationRecordId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static CorrelationRecordId DeriveV1(
        CorrelationRecord record,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (supportingEvidenceReceiptIds.IsDefault)
            throw new ArgumentException("Supporting evidence identities must be initialized.", nameof(supportingEvidenceReceiptIds));
        if (record.Outcome == CorrelationOutcome.Correlated && supportingEvidenceReceiptIds.IsEmpty)
            throw new ArgumentException("A successful correlation requires supporting evidence.", nameof(supportingEvidenceReceiptIds));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, "members", record.MemberIds.Select(value => value.Value));
        writer.AddString("method-id", record.MethodId);
        writer.AddString("method-version", record.MethodVersion);
        writer.AddInt32("outcome", (int)record.Outcome);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "supporting-evidence",
            supportingEvidenceReceiptIds.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct UnresolvedSourceAssertionId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "unresolved-source-assertion";

    [JsonConstructor]
    public UnresolvedSourceAssertionId(string value) =>
        Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static UnresolvedSourceAssertionId DeriveV1(
        GameId gameId,
        CatalogSourceRevisionId sourceRevisionId,
        KnowledgeAdapterRevisionId adapterRevisionId,
        SourceNativeIdentifier nativeIdentity,
        KnowledgeKind? candidateKind,
        string reasonCode,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(adapterRevisionId.Value, nameof(adapterRevisionId));
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        if (candidateKind is KnowledgeKind kind && !Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(candidateKind));
        reasonCode = CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode));
        CanonicalUtf8.Validate(reasonCode);
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("An unresolved source assertion requires source evidence.", nameof(supportingEvidenceReceiptIds));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("game-id", gameId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("adapter-revision-id", adapterRevisionId.Value);
        writer.AddExactNativeIdentifier("native-identity", nativeIdentity);
        writer.AddOptionalInt64("candidate-kind", candidateKind is null ? null : (int)candidateKind.Value);
        writer.AddString("reason-code", reasonCode);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            "supporting-evidence",
            supportingEvidenceReceiptIds.Select(value => value.Value));
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}
