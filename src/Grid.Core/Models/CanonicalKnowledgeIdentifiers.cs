using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public sealed class SourceNativeIdentifier : IEquatable<SourceNativeIdentifier>
{
    [JsonConstructor]
    public SourceNativeIdentifier(
        string @namespace,
        string objectType,
        string exactRepresentation,
        ImmutableArray<byte> identityBytes,
        string comparisonMethodId,
        int comparisonMethodVersion)
    {
        Namespace = CanonicalKnowledgeContract.RequireText(@namespace, nameof(@namespace));
        ObjectType = CanonicalKnowledgeContract.RequireText(objectType, nameof(objectType));
        ExactRepresentation = CanonicalKnowledgeContract.RequireText(exactRepresentation, nameof(exactRepresentation));
        if (identityBytes.IsDefaultOrEmpty)
            throw new ArgumentException("Source-native identity bytes must be non-empty.", nameof(identityBytes));
        IdentityBytes = identityBytes.ToImmutableArray();
        ComparisonMethodId = CanonicalKnowledgeContract.RequireText(comparisonMethodId, nameof(comparisonMethodId));
        if (comparisonMethodVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(comparisonMethodVersion), "Comparison method version must be positive.");
        ComparisonMethodVersion = comparisonMethodVersion;

        CanonicalUtf8.Validate(Namespace);
        CanonicalUtf8.Validate(ObjectType);
        CanonicalUtf8.Validate(ExactRepresentation);
        CanonicalUtf8.Validate(ComparisonMethodId);
    }

    public string Namespace { get; }
    public string ObjectType { get; }
    public string ExactRepresentation { get; }
    public ImmutableArray<byte> IdentityBytes { get; }
    public string ComparisonMethodId { get; }
    public int ComparisonMethodVersion { get; }

    public bool Equals(SourceNativeIdentifier? other) =>
        other is not null &&
        string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) &&
        string.Equals(ObjectType, other.ObjectType, StringComparison.Ordinal) &&
        string.Equals(ExactRepresentation, other.ExactRepresentation, StringComparison.Ordinal) &&
        IdentityBytes.AsSpan().SequenceEqual(other.IdentityBytes.AsSpan()) &&
        string.Equals(ComparisonMethodId, other.ComparisonMethodId, StringComparison.Ordinal) &&
        ComparisonMethodVersion == other.ComparisonMethodVersion;

    public override bool Equals(object? obj) => Equals(obj as SourceNativeIdentifier);

    public static bool operator ==(SourceNativeIdentifier? left, SourceNativeIdentifier? right) =>
        EqualityComparer<SourceNativeIdentifier>.Default.Equals(left, right);

    public static bool operator !=(SourceNativeIdentifier? left, SourceNativeIdentifier? right) => !(left == right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Namespace, StringComparer.Ordinal);
        hash.Add(ObjectType, StringComparer.Ordinal);
        hash.Add(ExactRepresentation, StringComparer.Ordinal);
        foreach (var value in IdentityBytes) hash.Add(value);
        hash.Add(ComparisonMethodId, StringComparer.Ordinal);
        hash.Add(ComparisonMethodVersion);
        return hash.ToHashCode();
    }

    public static SourceNativeIdentifier FromExactUtf8(
        string @namespace,
        string objectType,
        string exactRepresentation,
        string comparisonMethodId = "grid.exact-utf8",
        int comparisonMethodVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(exactRepresentation);
        return new(
            @namespace,
            objectType,
            exactRepresentation,
            CanonicalUtf8.GetBytes(exactRepresentation).ToImmutableArray(),
            comparisonMethodId,
            comparisonMethodVersion);
    }
}

public sealed class SourceNativeVersion : IEquatable<SourceNativeVersion>
{
    [JsonConstructor]
    public SourceNativeVersion(
        string @namespace,
        string exactRepresentation,
        ImmutableArray<byte> identityBytes,
        string comparisonMethodId,
        int comparisonMethodVersion)
    {
        Namespace = CanonicalKnowledgeContract.RequireText(@namespace, nameof(@namespace));
        ExactRepresentation = CanonicalKnowledgeContract.RequireText(exactRepresentation, nameof(exactRepresentation));
        if (identityBytes.IsDefaultOrEmpty)
            throw new ArgumentException("Source-native version identity bytes must be non-empty.", nameof(identityBytes));
        IdentityBytes = identityBytes.ToImmutableArray();
        ComparisonMethodId = CanonicalKnowledgeContract.RequireText(comparisonMethodId, nameof(comparisonMethodId));
        if (comparisonMethodVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(comparisonMethodVersion), "Comparison method version must be positive.");
        ComparisonMethodVersion = comparisonMethodVersion;

        CanonicalUtf8.Validate(Namespace);
        CanonicalUtf8.Validate(ExactRepresentation);
        CanonicalUtf8.Validate(ComparisonMethodId);
    }

    public string Namespace { get; }
    public string ExactRepresentation { get; }
    public ImmutableArray<byte> IdentityBytes { get; }
    public string ComparisonMethodId { get; }
    public int ComparisonMethodVersion { get; }

    public bool Equals(SourceNativeVersion? other) =>
        other is not null &&
        string.Equals(Namespace, other.Namespace, StringComparison.Ordinal) &&
        string.Equals(ExactRepresentation, other.ExactRepresentation, StringComparison.Ordinal) &&
        IdentityBytes.AsSpan().SequenceEqual(other.IdentityBytes.AsSpan()) &&
        string.Equals(ComparisonMethodId, other.ComparisonMethodId, StringComparison.Ordinal) &&
        ComparisonMethodVersion == other.ComparisonMethodVersion;

    public override bool Equals(object? obj) => Equals(obj as SourceNativeVersion);

    public static bool operator ==(SourceNativeVersion? left, SourceNativeVersion? right) =>
        EqualityComparer<SourceNativeVersion>.Default.Equals(left, right);

    public static bool operator !=(SourceNativeVersion? left, SourceNativeVersion? right) => !(left == right);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Namespace, StringComparer.Ordinal);
        hash.Add(ExactRepresentation, StringComparer.Ordinal);
        foreach (var value in IdentityBytes) hash.Add(value);
        hash.Add(ComparisonMethodId, StringComparer.Ordinal);
        hash.Add(ComparisonMethodVersion);
        return hash.ToHashCode();
    }

    public static SourceNativeVersion FromExactUtf8(
        string @namespace,
        string exactRepresentation,
        string comparisonMethodId = "grid.exact-utf8",
        int comparisonMethodVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(exactRepresentation);
        return new(
            @namespace,
            exactRepresentation,
            CanonicalUtf8.GetBytes(exactRepresentation).ToImmutableArray(),
            comparisonMethodId,
            comparisonMethodVersion);
    }
}

public readonly record struct ContentDigest
{
    public const string Sha256Algorithm = "sha256";

    [JsonConstructor]
    public ContentDigest(string algorithm, string hexValue)
    {
        if (!string.Equals(algorithm, Sha256Algorithm, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only SHA-256 content digests are supported by the v1 kernel.", nameof(algorithm));
        if (hexValue is null || hexValue.Length != 64 || !hexValue.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(hexValue));

        Algorithm = Sha256Algorithm;
        HexValue = hexValue.ToLowerInvariant();
    }

    public string Algorithm { get; }
    public string HexValue { get; }

    public static ContentDigest ComputeSha256(ReadOnlySpan<byte> content) =>
        new(Sha256Algorithm, Convert.ToHexString(SHA256.HashData(content)));

    internal byte[] GetBytes() => Convert.FromHexString(HexValue);

    public override string ToString() => $"{Algorithm}:{HexValue}";
}

public readonly record struct CatalogSourceId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "catalog-source";

    [JsonConstructor]
    public CatalogSourceId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain);

    public string Value { get; }

    public static CatalogSourceId DeriveV1(KnowledgeSourceKind sourceKind, SourceNativeIdentifier nativeIdentity)
    {
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        if (!Enum.IsDefined(sourceKind)) throw new ArgumentOutOfRangeException(nameof(sourceKind));

        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("source-kind", (int)sourceKind);
        writer.AddNativeIdentifier("native", nativeIdentity);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct SourceArtifactId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "source-artifact";

    [JsonConstructor]
    public SourceArtifactId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain);

    public string Value { get; }

    public static SourceArtifactId DeriveV1(ContentDigest digest)
    {
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddString("digest-algorithm", digest.Algorithm);
        writer.AddBytes("digest-bytes", digest.GetBytes());
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct CatalogSourceRevisionId
{
    public const int LegacyAlgorithmVersion = 1;
    public const int CurrentAlgorithmVersion = 2;
    private const string Domain = "catalog-source-revision";

    [JsonConstructor]
    public CatalogSourceRevisionId(string value)
    {
        var v1Prefix = $"grid.{Domain}.v{LegacyAlgorithmVersion}.sha256.";
        Value = value?.StartsWith(v1Prefix, StringComparison.Ordinal) == true
            ? CanonicalIdentityV1.Validate(value, Domain, LegacyAlgorithmVersion)
            : CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);
    }

    public string Value { get; }

    public static CatalogSourceRevisionId DeriveV1(
        CatalogSourceId sourceId,
        SourceNativeVersion? nativeRevision,
        ImmutableArray<SourceArtifactId> artifactIds)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceId.Value, nameof(sourceId));
        if (artifactIds.IsDefault)
            throw new ArgumentException("Artifact identities must be an initialized immutable array.", nameof(artifactIds));
        if (nativeRevision is null && artifactIds.IsEmpty)
            throw new ArgumentException("A source revision requires a native revision or at least one artifact.", nameof(artifactIds));

        var orderedArtifacts = artifactIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        for (var index = 0; index < orderedArtifacts.Length; index++)
        {
            CanonicalKnowledgeContract.RequireIdentifier(orderedArtifacts[index].Value, nameof(artifactIds));
            if (index > 0 && orderedArtifacts[index - 1] == orderedArtifacts[index])
                throw new ArgumentException("Source revision artifact identities must be distinct.", nameof(artifactIds));
        }

        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddString("source-id", sourceId.Value);
        writer.AddOptionalNativeVersion("native-revision", nativeRevision);
        writer.AddInt32("artifact-count", orderedArtifacts.Length);
        for (var index = 0; index < orderedArtifacts.Length; index++)
            writer.AddString($"artifact-{index}", orderedArtifacts[index].Value);
        return new(writer.Derive());
    }

    public static CatalogSourceRevisionId DeriveV2(
        CatalogSourceId sourceId,
        SourceNativeVersion? nativeRevision,
        ImmutableArray<SourceArtifactId> artifactIds,
        KnowledgeAdapterRevisionId adapterRevisionId)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceId.Value, nameof(sourceId));
        CanonicalKnowledgeContract.RequireIdentifier(adapterRevisionId.Value, nameof(adapterRevisionId));
        if (artifactIds.IsDefault)
            throw new ArgumentException("Artifact identities must be an initialized immutable array.", nameof(artifactIds));
        if (nativeRevision is null && artifactIds.IsEmpty)
            throw new ArgumentException("A source revision requires a native revision or at least one artifact.", nameof(artifactIds));

        var orderedArtifacts = artifactIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        for (var index = 0; index < orderedArtifacts.Length; index++)
        {
            CanonicalKnowledgeContract.RequireIdentifier(orderedArtifacts[index].Value, nameof(artifactIds));
            if (index > 0 && orderedArtifacts[index - 1] == orderedArtifacts[index])
                throw new ArgumentException("Source revision artifact identities must be distinct.", nameof(artifactIds));
        }

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("source-id", sourceId.Value);
        writer.AddOptionalNativeVersion("native-revision", nativeRevision);
        writer.AddInt32("artifact-count", orderedArtifacts.Length);
        for (var index = 0; index < orderedArtifacts.Length; index++)
            writer.AddString($"artifact-{index}", orderedArtifacts[index].Value);
        writer.AddString("adapter-revision-id", adapterRevisionId.Value);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct NativeRecordIdentityId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "native-record-identity";

    [JsonConstructor]
    public NativeRecordIdentityId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain);

    public string Value { get; }

    public static NativeRecordIdentityId DeriveV1(GameId gameId, SourceNativeIdentifier nativeIdentity)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        ArgumentNullException.ThrowIfNull(nativeIdentity);

        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddString("game-id", gameId.Value);
        writer.AddNativeIdentifier("native", nativeIdentity);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct KnowledgeRecordId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "knowledge-record";
    public const string RegistrationEntityPrefix = "grid.registration.entity.v1.";

    [JsonConstructor]
    public KnowledgeRecordId(string value) =>
        Value = TryValidateRegistrationEntityBacked(value) ?? CanonicalIdentityV1.Validate(value, Domain);

    public string Value { get; }

    public static bool IsRegistrationEntityBacked(string value) =>
        TryValidateRegistrationEntityBacked(value) is not null;

    internal static string? TryValidateRegistrationEntityBacked(string? value)
    {
        if (value is null || !value.StartsWith(RegistrationEntityPrefix, StringComparison.Ordinal) ||
            value.Length != RegistrationEntityPrefix.Length + 64)
            return null;
        var digest = value[RegistrationEntityPrefix.Length..];
        if (digest.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
            throw new ArgumentException("Registration-backed knowledge record identity digest must be lowercase hexadecimal.", nameof(value));
        return value;
    }

    public static KnowledgeRecordId DeriveV1(
        GameId gameId,
        SourceNativeVersion? gameVersion,
        SourceNativeVersion? modVersion,
        CatalogSourceRevisionId sourceRevisionId,
        KnowledgeKind knowledgeKind,
        NativeRecordIdentityId nativeRecordIdentityId)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(nativeRecordIdentityId.Value, nameof(nativeRecordIdentityId));
        if (!Enum.IsDefined(knowledgeKind)) throw new ArgumentOutOfRangeException(nameof(knowledgeKind));

        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddString("game-id", gameId.Value);
        writer.AddOptionalNativeVersion("game-version", gameVersion);
        writer.AddOptionalNativeVersion("mod-version", modVersion);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddInt32("knowledge-kind", (int)knowledgeKind);
        writer.AddString("native-record-id", nativeRecordIdentityId.Value);
        return new(writer.Derive());
    }

    public static string DeriveRegistrationBackedLocationEntityId(CanonicalKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != KnowledgeKind.Location)
            throw new ArgumentException("Registration-backed publication ids apply only to Location records.", nameof(record));
        return CanonicalRegistrationEncoding.Identity(
            record.Kind.ToString(),
            record.GameId.Value,
            record.NativeIdentity.Namespace,
            record.NativeIdentity.ExactRepresentation);
    }

    public static bool MatchesPackageIdentity(CanonicalKnowledgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (NativeRecordIdentityId.DeriveV1(record.GameId, record.NativeIdentity) != record.NativeRecordIdentityId)
            return false;
        var derived = DeriveV1(
            record.GameId,
            record.GameVersion,
            record.ModVersion,
            record.SourceRevisionId,
            record.Kind,
            record.NativeRecordIdentityId);
        if (record.Id == derived)
            return true;
        if (!IsRegistrationEntityBacked(record.Id.Value) || record.Kind != KnowledgeKind.Location)
            return false;
        return string.Equals(
            record.Id.Value,
            DeriveRegistrationBackedLocationEntityId(record),
            StringComparison.Ordinal);
    }

    public override string ToString() => Value;
}

public readonly record struct EvidenceReceiptId
{
    public const int LegacyAlgorithmVersion = 1;
    public const int CurrentAlgorithmVersion = 2;
    private const string Domain = "evidence-receipt";

    [JsonConstructor]
    public EvidenceReceiptId(string value)
    {
        try
        {
            Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);
            AlgorithmVersion = CurrentAlgorithmVersion;
        }
        catch (ArgumentException)
        {
            Value = CanonicalIdentityV1.Validate(value, Domain, LegacyAlgorithmVersion);
            AlgorithmVersion = LegacyAlgorithmVersion;
        }
    }

    public string Value { get; }
    [JsonIgnore]
    public int AlgorithmVersion { get; }

    public static EvidenceReceiptId DeriveV1(FileEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("evidence-kind", 0);
        writer.AddString("source-revision-id", receipt.SourceRevisionId.Value);
        writer.AddString("artifact-id", receipt.SourceArtifactId.Value);
        writer.AddContentDigest("artifact-digest", receipt.ArtifactDigest);
        writer.AddString("parser-id", receipt.ParserId);
        writer.AddString("parser-version", receipt.ParserVersion);
        writer.AddString("native-record-locator", receipt.NativeRecordLocator);
        writer.AddString("source-field-path", receipt.SourceFieldPath);
        writer.AddOptionalInt64("byte-offset", receipt.ByteOffset);
        writer.AddOptionalInt64("byte-length", receipt.ByteLength);
        writer.AddOptionalContentDigest("interpreted-bytes-digest", receipt.InterpretedBytesDigest);
        return new(writer.Derive());
    }

    public static EvidenceReceiptId DeriveV2(FileEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.NativeObjectIdentity is null)
            throw new ArgumentException(
                "A v2 FILE evidence receipt requires the complete source-native object identity.",
                nameof(receipt));
        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddInt32("evidence-kind", 0);
        writer.AddString("source-revision-id", receipt.SourceRevisionId.Value);
        writer.AddString("artifact-id", receipt.SourceArtifactId.Value);
        writer.AddContentDigest("artifact-digest", receipt.ArtifactDigest);
        writer.AddString("parser-id", receipt.ParserId);
        writer.AddString("parser-version", receipt.ParserVersion);
        writer.AddString("native-record-locator", receipt.NativeRecordLocator);
        writer.AddExactNativeIdentifier("native-object-identity", receipt.NativeObjectIdentity);
        writer.AddString("source-field-path", receipt.SourceFieldPath);
        writer.AddOptionalInt64("byte-offset", receipt.ByteOffset);
        writer.AddOptionalInt64("byte-length", receipt.ByteLength);
        writer.AddOptionalContentDigest("interpreted-bytes-digest", receipt.InterpretedBytesDigest);
        return new(writer.Derive());
    }

    public static EvidenceReceiptId DeriveV1(ReferenceEvidenceReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("evidence-kind", 1);
        writer.AddString("provider-source-id", receipt.ProviderCatalogSourceId.Value);
        writer.AddString("source-revision-id", receipt.SourceRevisionId.Value);
        writer.AddString("response-artifact-id", receipt.ResponseArtifactId.Value);
        writer.AddContentDigest("response-content-digest", receipt.ResponseContentDigest);
        writer.AddOptionalNativeIdentifier("native-object", receipt.NativeObjectIdentity);
        writer.AddOptionalNativeVersion("native-revision", receipt.NativeRevisionIdentity);
        writer.AddString("response-field-path", receipt.ResponseFieldPath);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct EvidenceClaimContentId
{
    public const int CurrentAlgorithmVersion = 1;
    private const string Domain = "evidence-claim-content";

    [JsonConstructor]
    public EvidenceClaimContentId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain);

    public string Value { get; }

    public static EvidenceClaimContentId DeriveV1(TerminologyAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.Terminology);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddInt32("terminology-role", (int)assertion.Role);
        writer.AddString("verbatim-value", assertion.VerbatimValue);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        writer.AddOptionalString("language-tag", assertion.LanguageTag);
        writer.AddOptionalExactNativeIdentifier("native-string-identifier", assertion.NativeStringIdentifier);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(RelationshipAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.Relationship);
        writer.AddString("knowledge-record-id", assertion.SubjectKnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddString("semantic-id", assertion.SemanticId.Value);
        writer.AddString("source-native-relationship-type", assertion.SourceNativeRelationshipType);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        writer.AddExactNativeIdentifier("source-native-target", assertion.SourceNativeTarget);
        writer.AddOptionalString("resolved-target-knowledge-record-id", assertion.ResolvedTargetKnowledgeRecordId?.Value);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(SourceNativeLocationTypeAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.LocationNativeType);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddExactNativeIdentifier("exact-native-type", assertion.ExactNativeType);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(LocationSemanticClassificationAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.LocationSemanticClassification);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddOptionalString("source-native-type-assertion-id", assertion.SourceNativeTypeAssertionId?.Value);
        writer.AddString("role-id", assertion.RoleId.Value);
        writer.AddInt32("vocabulary-version", assertion.VocabularyVersion.Value);
        writer.AddString("classification-method-id", assertion.ClassificationMethodId);
        writer.AddString("classification-method-version", assertion.ClassificationMethodVersion);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(CanonicalRecordLifecycleAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.RecordLifecycle);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddInt32("state", (int)assertion.State);
        writer.AddString("source-native-lifecycle-type", assertion.SourceNativeLifecycleType);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(CanonicalSemanticClassificationAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.SemanticClassification);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddString("role-id", assertion.RoleId.Value);
        writer.AddString("vocabulary-id", assertion.VocabularyId);
        writer.AddString("vocabulary-version", assertion.VocabularyVersion);
        writer.AddString("method-id", assertion.ClassificationMethodId);
        writer.AddString("method-version", assertion.ClassificationMethodVersion);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(CanonicalRecordContributionAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.RecordContribution);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddInt32("contribution-kind", (int)assertion.ContributionKind);
        writer.AddOptionalString("origin-record-id", assertion.OriginKnowledgeRecordId?.Value);
        writer.AddOptionalExactNativeIdentifier("mod-identity", assertion.ExactModIdentity);
        writer.AddOptionalNativeVersion("mod-version", assertion.ExactModVersion);
        writer.AddString("method-id", assertion.MethodId);
        writer.AddString("method-version", assertion.MethodVersion);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(CanonicalOrganizationalValueAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.OrganizationalValue);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("knowledge-record-id", assertion.KnowledgeRecordId.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddString("dimension-id", assertion.DimensionId.Value);
        writer.AddExactNativeIdentifier("value-identity", assertion.ExactValueIdentity);
        writer.AddOptionalString("verbatim-display-value", assertion.VerbatimDisplayValue);
        writer.AddString("method-id", assertion.MethodId);
        writer.AddString("method-version", assertion.MethodVersion);
        writer.AddString("source-field-path", assertion.SourceFieldPath);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(InstructionAssertion assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.Instruction);
        writer.AddString("assertion-id", assertion.Id.Value);
        writer.AddString("source-revision-id", assertion.SourceRevisionId.Value);
        writer.AddExactNativeIdentifier("native-identity", assertion.NativeIdentity);
        writer.AddOptionalString("category-id", assertion.CategoryId?.Value);
        writer.AddContentDigest("content-digest", assertion.Content.ContentDigest);
        writer.AddString("source-field-path", assertion.SourceLocator.ExactFieldPathOrFragment);
        return new(writer.Derive());
    }

    public static EvidenceClaimContentId DeriveV1(CrossSourceTargetLinkClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        var writer = new CanonicalIdentityWriter(Domain);
        writer.AddInt32("claim-kind", (int)EvidenceClaimKind.CrossSourceTargetLink);
        writer.AddString("claim-id", claim.Id.Value);
        writer.AddString("target-record-id", claim.TargetKnowledgeRecordId.Value);
        writer.AddString("target-origin-revision-id", claim.TargetOriginSourceRevisionId.Value);
        writer.AddString("target-native-record-id", claim.TargetNativeRecordIdentityId.Value);
        writer.AddExactNativeIdentifier("target-coordinate", claim.ExactTargetCoordinate);
        writer.AddString("target-coordinate-artifact-id", claim.TargetCoordinateArtifactId.Value);
        writer.AddString("target-coordinate-field-path", claim.TargetCoordinateFieldPath);
        writer.AddString("asserting-source-revision-id", claim.AssertingSourceRevisionId.Value);
        writer.AddExactNativeIdentifier("asserting-coordinate", claim.ExactAssertingCoordinate);
        writer.AddString("asserting-coordinate-artifact-id", claim.AssertingCoordinateArtifactId.Value);
        writer.AddString("asserting-coordinate-field-path", claim.AssertingCoordinateFieldPath);
        writer.AddInt32("link-kind", (int)claim.LinkKind);
        writer.AddString("method-id", claim.MethodId);
        writer.AddString("method-version", claim.MethodVersion);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

public readonly record struct EvidenceBindingId
{
    public const int CurrentAlgorithmVersion = 2;
    private const string Domain = "evidence-binding";

    [JsonConstructor]
    public EvidenceBindingId(string value) => Value = CanonicalIdentityV1.Validate(value, Domain, CurrentAlgorithmVersion);

    public string Value { get; }

    public static EvidenceBindingId DeriveV2(
        EvidenceReceiptId evidenceReceiptId,
        EvidenceClaimKind claimKind,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        string claimLocator,
        EvidenceClaimContentId? claimContentId)
    {
        CanonicalKnowledgeContract.RequireIdentifier(evidenceReceiptId.Value, nameof(evidenceReceiptId));
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        if (!Enum.IsDefined(claimKind)) throw new ArgumentOutOfRangeException(nameof(claimKind));
        CanonicalKnowledgeContract.RequireText(claimLocator, nameof(claimLocator));
        if (claimKind == EvidenceClaimKind.KnowledgeIdentity && claimContentId is not null)
            throw new ArgumentException("Knowledge identity evidence cannot reference assertion claim content.", nameof(claimContentId));
        if (claimKind != EvidenceClaimKind.KnowledgeIdentity && claimContentId is null)
            throw new ArgumentException("Assertion evidence requires exact claim content identity.", nameof(claimContentId));

        var writer = new CanonicalIdentityWriter(Domain, CurrentAlgorithmVersion);
        writer.AddString("evidence-receipt-id", evidenceReceiptId.Value);
        writer.AddInt32("claim-kind", (int)claimKind);
        writer.AddString("knowledge-record-id", knowledgeRecordId.Value);
        writer.AddString("source-revision-id", sourceRevisionId.Value);
        writer.AddString("claim-locator", claimLocator);
        writer.AddOptionalString("claim-content-id", claimContentId?.Value);
        return new(writer.Derive());
    }

    public override string ToString() => Value;
}

internal static class CanonicalIdentityV1
{
    public const int WireVersion = 1;
    private static readonly byte[] Magic = "GRID-CANONICAL-ID"u8.ToArray();

    public static ReadOnlySpan<byte> MagicBytes => Magic;

    public static string Validate(string? value, string domain, int identityAlgorithmVersion = 1)
    {
        if (identityAlgorithmVersion <= 0) throw new ArgumentOutOfRangeException(nameof(identityAlgorithmVersion));
        var prefix = $"grid.{domain}.v{identityAlgorithmVersion}.sha256.";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64)
            throw new ArgumentException(
                $"Canonical {domain} identity must use the v{identityAlgorithmVersion} SHA-256 format.",
                nameof(value));
        var digest = value[prefix.Length..];
        if (digest.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
            throw new ArgumentException($"Canonical {domain} identity digest must be lowercase hexadecimal.", nameof(value));
        return value;
    }
}

internal static class CanonicalUtf8
{
    private static readonly UTF8Encoding StrictEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] GetBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StrictEncoding.GetBytes(value);
    }

    public static void Validate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = StrictEncoding.GetByteCount(value);
    }
}

internal sealed class CanonicalIdentityWriter
{
    private const byte NullKind = 0;
    private const byte Utf8Kind = 1;
    private const byte BytesKind = 2;
    private const byte Int32Kind = 3;
    private const byte Int64Kind = 4;
    private readonly string domain;
    private readonly int identityAlgorithmVersion;
    private readonly List<Component> components = [];

    public CanonicalIdentityWriter(string domain, int identityAlgorithmVersion = 1)
    {
        this.domain = CanonicalKnowledgeContract.RequireText(domain, nameof(domain));
        if (identityAlgorithmVersion <= 0) throw new ArgumentOutOfRangeException(nameof(identityAlgorithmVersion));
        this.identityAlgorithmVersion = identityAlgorithmVersion;
    }

    public void AddString(string tag, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Add(tag, Utf8Kind, CanonicalUtf8.GetBytes(value));
    }

    public void AddBytes(string tag, ReadOnlySpan<byte> value) => Add(tag, BytesKind, value.ToArray());

    public void AddOptionalString(string tag, string? value)
    {
        if (value is null)
        {
            Add(tag, NullKind, []);
            return;
        }

        AddString(tag, value);
    }

    public void AddInt32(string tag, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        Add(tag, Int32Kind, bytes.ToArray());
    }

    public void AddInt64(string tag, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        Add(tag, Int64Kind, bytes.ToArray());
    }

    public void AddOptionalInt64(string tag, long? value)
    {
        if (value is null)
        {
            Add(tag, NullKind, []);
            return;
        }

        AddInt64(tag, value.Value);
    }

    public void AddContentDigest(string prefix, ContentDigest value)
    {
        AddString($"{prefix}.algorithm", value.Algorithm);
        AddBytes($"{prefix}.bytes", value.GetBytes());
    }

    public void AddOptionalContentDigest(string prefix, ContentDigest? value)
    {
        if (value is null)
        {
            Add(prefix, NullKind, []);
            return;
        }

        AddContentDigest(prefix, value.Value);
    }

    public void AddNativeIdentifier(string prefix, SourceNativeIdentifier value)
    {
        AddString($"{prefix}.namespace", value.Namespace);
        AddString($"{prefix}.object-type", value.ObjectType);
        AddBytes($"{prefix}.identity-bytes", value.IdentityBytes.AsSpan());
        AddString($"{prefix}.comparison-method-id", value.ComparisonMethodId);
        AddInt32($"{prefix}.comparison-method-version", value.ComparisonMethodVersion);
    }

    public void AddExactNativeIdentifier(string prefix, SourceNativeIdentifier value)
    {
        AddNativeIdentifier(prefix, value);
        AddString($"{prefix}.exact-representation", value.ExactRepresentation);
    }

    public void AddOptionalNativeIdentifier(string prefix, SourceNativeIdentifier? value)
    {
        if (value is null)
        {
            Add(prefix, NullKind, []);
            return;
        }

        AddNativeIdentifier(prefix, value);
    }

    public void AddOptionalExactNativeIdentifier(string prefix, SourceNativeIdentifier? value)
    {
        if (value is null)
        {
            Add(prefix, NullKind, []);
            return;
        }

        AddExactNativeIdentifier(prefix, value);
    }

    public void AddOptionalNativeVersion(string prefix, SourceNativeVersion? value)
    {
        if (value is null)
        {
            Add(prefix, NullKind, []);
            return;
        }

        AddString($"{prefix}.namespace", value.Namespace);
        AddBytes($"{prefix}.identity-bytes", value.IdentityBytes.AsSpan());
        AddString($"{prefix}.comparison-method-id", value.ComparisonMethodId);
        AddInt32($"{prefix}.comparison-method-version", value.ComparisonMethodVersion);
    }

    public string Derive()
    {
        using var stream = new MemoryStream();
        stream.Write(CanonicalIdentityV1.MagicBytes);
        WriteUInt16(stream, CanonicalIdentityV1.WireVersion);
        WriteUtf8(stream, domain);
        WriteUInt32(stream, checked((uint)components.Count));

        foreach (var component in components)
        {
            WriteUtf8(stream, component.Tag);
            stream.WriteByte(component.Kind);
            WriteUInt64(stream, checked((ulong)component.Payload.Length));
            stream.Write(component.Payload);
        }

        var digest = SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return $"grid.{domain}.v{identityAlgorithmVersion}.sha256.{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private void Add(string tag, byte kind, byte[] payload) =>
        components.Add(new(CanonicalKnowledgeContract.RequireText(tag, nameof(tag)), kind, payload));

    private static void WriteUtf8(Stream stream, string value)
    {
        var bytes = CanonicalUtf8.GetBytes(value);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record Component(string Tag, byte Kind, byte[] Payload);
}
