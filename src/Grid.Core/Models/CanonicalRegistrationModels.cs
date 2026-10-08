using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace Grid.Core.Models;

// Candidate-only contracts. These do not change the historical KnowledgeKind or identity formats.
public enum RegistrationEntityKind { Entity, Category, Variant, TechnicalArtifact }
public enum RegistrationOutcome { Correlated, Unresolved, Rejected, Ambiguous }
public enum RegistrationFacetKind { AlternateView, Filter }
public sealed record RegistrationSource(string Id, string Uri, KnowledgeSourceKind Kind, string Sha256,
    string AdapterId, string AdapterVersion, string Family);
public sealed record RegistrationSourceArtifact(RegistrationSource Source, byte[] Bytes);
public sealed record RegistrationEvidence(string Id, string SourceId, string Locator, EvidenceVerificationKind Basis);
public sealed record RegistrationName(string Value, string Locale, bool IsAlias, string[] EvidenceIds);
public sealed record RegistrationApplicability(string GameId, string? ProfileId, string[] EvidenceIds);
public sealed record RegistrationOrigin(string Kind, string NativeNamespace, string NativeId, string[] EvidenceIds);
public sealed record RegistrationEntityClaim(string Key, string SourceId, string NativeNamespace, string NativeId,
    string Selector, string? GameId, string SourceClassification, RegistrationEntityKind Kind,
    RegistrationName[] Names, RegistrationApplicability[] Applicability, RegistrationOrigin[] Origins,
    string[] EvidenceIds, string? ExistingRecordId = null, string? ExistingPackageId = null);
public sealed record RegistrationRelationshipClaim(string Id, string SubjectKey, string TargetKey,
    string Semantic, string[] EvidenceIds, string? SourceId = null,
    string? SourceNativeRelationshipType = null, string? SourceFieldPath = null);
public sealed record RegistrationFacetClaim(string Id, string EntityKey, RegistrationFacetKind Kind,
    string Dimension, string Value, string Locale, string[] EvidenceIds);
/// <summary>SemanticLevel places an entity strictly between its entity-slot anchor and the next slot
/// (e.g. 7.5 under Town/Neighborhood) without adding checklist nodes or placeholder rows.</summary>
public sealed record RegistrationMappingRule(string Id, string SourceFamily, string Selector,
    string SourceClassification, string ChecklistAnchor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? SemanticLevel = null);
/// <summary>Non-selectable geography frame placed above parentless entities at an entity-slot level.
/// Empty evidence marks a rule-declared setting premise rather than a source-established level.</summary>
public sealed record RegistrationLevelFrameNode(string Id, string Selector, string ChecklistAnchor,
    string Label, string Locale, string[] EvidenceIds);
public sealed record RegistrationRuleSet(string Version, RegistrationMappingRule[] Rules,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RegistrationLevelFrameNode[]? LevelFrame = null);
public sealed record RegistrationEvidenceSet(RegistrationEvidence[] Evidence, RegistrationEntityClaim[] Entities,
    RegistrationRelationshipClaim[] Relationships, RegistrationFacetClaim[] Facets);
public sealed record RegistrationInput(RegistrationSource[] Sources, RegistrationRuleSet RuleSet,
    RegistrationEvidenceSet Evidence);
public sealed record RegistrationRuling(string ClaimId, string Stage, RegistrationOutcome Outcome,
    string Reason, string[] EvidenceIds);
public sealed record RegisteredCanonicalEntity(string Id, string[] SourceKeys, string Selector,
    string NativeNamespace, string NativeId, string? GameId, string Anchor, RegistrationEntityKind Kind,
    RegistrationName[] Names, RegistrationApplicability[] Applicability, RegistrationOrigin[] Origins,
    string[] EvidenceIds, string? ExistingPackageId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? SemanticLevel = null);
public sealed record RegisteredCanonicalRelationship(string Id, string SubjectId, string TargetId,
    string Semantic, string SourceId, string? SourceNativeRelationshipType, string? SourceFieldPath,
    EvidenceVerificationKind VerificationBasis, RegistrationOutcome CorrelationOutcome, string ProvenanceReason,
    string[] EvidenceIds);
public sealed record RegisteredCanonicalFacet(string Id, string EntityId, RegistrationFacetKind Kind,
    string Anchor, string Dimension, string Value, string Locale, string[] EvidenceIds);
public sealed record CanonicalRegistrationCandidate(int FormatVersion, string EngineVersion,
    string ChecklistVersion, string ChecklistDigest, string RuleDigest, RegistrationInput Input,
    RegisteredCanonicalEntity[] Entities, RegisteredCanonicalRelationship[] Relationships,
    RegisteredCanonicalFacet[] Facets, RegistrationRuling[] Rulings, string PublicationState);
public sealed record RegistrationNavigationContext(string GameId, string ProfileId, string Locale);
public sealed record RegistrationNavigationRow(string PathId, string? ParentPathId, string Selector,
    string Label, string? EntityId, bool Selectable, bool CanDescend, string[] EvidenceIds);
public sealed record RegistrationPreparedPage(string PathId, RegistrationNavigationRow Current,
    RegistrationNavigationRow[] Children, string? ContinuationCursor);
public sealed record RegistrationPreparedPath(string PathId, string? ParentPathId, string Selector,
    string? EntityId, bool Selectable, string NodeDigest, string[] PageDigests);
public sealed record RegistrationPreparedView(string Id, string Selector, string? Dimension, string? Value,
    string RootPathId);
public sealed record RegistrationPreparedDescriptor(int FormatVersion, string CandidateDigest,
    string ChecklistDigest, RegistrationNavigationContext Context, RegistrationPreparedView[] Views,
    RegistrationPreparedPath[] Paths, string[] PageDigests, string PublicationState);
public sealed record RegistrationPreparedCandidate(string DirectoryPath, string Digest,
    RegistrationPreparedDescriptor Descriptor);
public sealed record RegistrationCount(string Key, int Count);
public sealed record CanonicalRegistrationRunReceipt(string EngineVersion, string ChecklistVersion,
    string ChecklistDigest, string RuleDigest, RegistrationSource[] Sources, RegistrationNavigationContext[] Contexts,
    int EntitiesObserved, int EntitiesAdmitted, int EntitiesUnresolved, int EntitiesRejected,
    int RelationshipsAdmitted, int RelationshipsRejected, int RelationshipsUnresolved,
    RegistrationCount[] RelationshipRulingCounts, RegistrationCount[] CountsPerSelector,
    RegistrationCount[] CountsPerRoot, int ArbitraryDepthCategories, string[] SortFacets, string[] FilterFacets,
    int AliasesResolved, int AmbiguousMappings, RegistrationRuling[] EvidenceRulings,
    string ValidationResult, string CanonicalCandidateDigest, string[] PreparedGenerationDigests,
    string PublicationState);

public static class CanonicalRegistrationEncoding
{
    public const string EngineVersion = "grid.canonical-registration.v1";
    public const string NotPublished = "NOT_PUBLISHED";
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    public static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Digest<T>(T value) => Digest(Bytes(value));
    public static string Identity(params string?[] parts) => "grid.registration.entity.v1." + Digest(Bytes(parts));
    public static string Path(params string?[] parts) => "path." + Digest(Bytes(parts));
    public static string[] Ordered(IEnumerable<string> values) => values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    public static bool IsDigest(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
