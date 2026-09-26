using System.Collections.Immutable;

namespace Grid.Core.Models;

/// <summary>
/// Immutable, read-only projection of one preproduction catalog package for developer/QCS inspection.
/// This contract intentionally exposes no catalog mutation, approval, or publication operation.
/// </summary>
public sealed record CanonicalCatalogInspectionReport(
    CatalogInspectionSummary Summary,
    ImmutableArray<CatalogInspectionRecord> Records,
    ImmutableArray<CatalogInspectionRelationship> Relationships,
    ImmutableArray<CatalogInspectionEvidenceTrace> Evidence,
    ImmutableArray<CatalogInspectionUnresolvedItem> Unresolved,
    ImmutableArray<CatalogInspectionConflict> Conflicts,
    ImmutableArray<CatalogInspectionSource> Sources,
    CatalogInspectionPackageValidation PackageValidation);

public sealed record CatalogInspectionSummary(
    GameId GameId,
    CatalogPackageKind PackageKind,
    SourceNativeVersion? ExactGameVersion,
    SourceNativeIdentifier? ExactModIdentity,
    SourceNativeVersion? ExactModVersion,
    KnowledgeCoverageState CoverageState,
    CatalogPackageId PackageId,
    CatalogRevisionId CatalogRevisionId,
    CatalogPayloadDigest PayloadDigest,
    ImmutableArray<KnowledgeAdapterRevisionCoordinate> AdapterRevisions,
    ImmutableDictionary<KnowledgeKind, int> RecordCounts,
    int TerminologyCount,
    int RelationshipCount,
    int CorrelationCount,
    int UnresolvedCount,
    int ConflictCount,
    int FileVerifiedCount,
    int ReferenceVerifiedCount,
    int SourceCount,
    int ArtifactCount,
    bool IsStructurallyValid,
    CatalogValidationStatus QcsStatus);

public sealed record CatalogInspectionRecord(
    CanonicalKnowledgeRecord Record,
    ImmutableArray<TerminologyAssertion> TerminologyAssertions,
    ImmutableArray<RelationshipAssertion> RelationshipAssertions,
    ImmutableArray<EvidenceBinding> EvidenceBindings,
    ImmutableArray<CorrelationRecordId> CorrelationIds,
    ImmutableArray<EvidenceVerificationKind> EvidenceStates,
    bool IsTerminologyUnresolved);

public sealed record CatalogInspectionRelationship(
    RelationshipAssertion Assertion,
    EvidenceClaimContentId ClaimContentId,
    ImmutableArray<EvidenceBindingId> EvidenceBindingIds);

public sealed record CatalogInspectionEvidenceTrace(
    EvidenceReceiptId EvidenceReceiptId,
    EvidenceVerificationKind Verification,
    CatalogFileEvidenceReceipt? FileEvidence,
    CatalogReferenceEvidenceReceipt? ReferenceEvidence,
    ImmutableArray<EvidenceBinding> EvidenceBindings,
    ImmutableArray<CorrelationRecordId> CorrelationIds,
    ImmutableArray<UnresolvedSourceAssertionId> UnresolvedAssertionIds,
    AdapterBoundCatalogSourceRevisionRecord SourceRevision,
    ImmutableArray<SourceArtifactRecord> Artifacts,
    GameKnowledgeAdapterDescriptor AdapterDescriptor);

public enum CatalogInspectionUnresolvedKind
{
    SourceAssertion,
    Relationship,
    Correlation,
    MissingTerminology,
    UnsupportedCoverage,
}

public sealed record CatalogInspectionUnresolvedItem(
    CatalogInspectionUnresolvedKind Kind,
    string CanonicalCoordinate,
    string ExactDetail,
    UnresolvedSourceAssertion? SourceAssertion = null,
    RelationshipAssertion? Relationship = null,
    CanonicalCorrelationEnvelope? Correlation = null,
    KnowledgeRecordId? KnowledgeRecordId = null);

public enum CatalogInspectionConflictKind
{
    TerminologyDisagreement,
    RelationshipDisagreement,
    AmbiguousCorrelation,
}

public sealed record CatalogInspectionConflict(
    CatalogInspectionConflictKind Kind,
    string CanonicalCoordinate,
    ImmutableArray<string> ExactAssertions,
    ImmutableArray<KnowledgeRecordId> KnowledgeRecordIds);

public sealed record CatalogInspectionSource(
    CatalogSourceRecord Source,
    ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> Revisions,
    ImmutableArray<SourceArtifactRecord> Artifacts);

public sealed record CatalogInspectionPackageValidation(
    int PackageSchemaVersion,
    CatalogPackageId PackageId,
    CatalogRevisionId CatalogRevisionId,
    CatalogPayloadDigest PayloadDigest,
    CatalogValidationStatus QcsStatus,
    string ValidationPolicyId,
    string ValidationPolicyVersion,
    ContentDigest ValidationResultDigest,
    bool IsStructurallyValid,
    ImmutableArray<string> StructuralIssues);
