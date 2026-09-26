using System.Collections.Immutable;

namespace Grid.Core.Models;

public sealed record CatalogSourceRecord(
    CatalogSourceId Id,
    KnowledgeSourceKind Kind,
    SourceNativeIdentifier NativeIdentity);

public sealed record SourceArtifactRecord(
    SourceArtifactId Id,
    ContentDigest Digest);

public sealed record CatalogSourceRevisionRecord(
    CatalogSourceRevisionId Id,
    CatalogSourceId SourceId,
    SourceNativeVersion? NativeRevision,
    ImmutableArray<SourceArtifactId> ArtifactIds);

public sealed record CanonicalKnowledgeRecord(
    KnowledgeRecordId Id,
    GameId GameId,
    SourceNativeVersion? GameVersion,
    SourceNativeVersion? ModVersion,
    CatalogSourceRevisionId SourceRevisionId,
    KnowledgeKind Kind,
    NativeRecordIdentityId NativeRecordIdentityId,
    SourceNativeIdentifier NativeIdentity);

public sealed record CatalogFileEvidenceReceipt(
    EvidenceReceiptId Id,
    FileEvidenceReceipt Receipt);

public sealed record CatalogReferenceEvidenceReceipt(
    EvidenceReceiptId Id,
    ReferenceEvidenceReceipt Receipt);

public sealed record EvidenceBinding(
    EvidenceBindingId Id,
    EvidenceReceiptId EvidenceReceiptId,
    EvidenceClaimKind ClaimKind,
    KnowledgeRecordId KnowledgeRecordId,
    CatalogSourceRevisionId SourceRevisionId,
    string ClaimLocator,
    EvidenceClaimContentId? ClaimContentId);

public sealed record CanonicalCatalogRegistration(
    CatalogSourceRecord Source,
    ImmutableArray<SourceArtifactRecord> Artifacts,
    CatalogSourceRevisionRecord SourceRevision,
    ImmutableArray<CanonicalKnowledgeRecord> KnowledgeRecords,
    ImmutableArray<TerminologyAssertion> TerminologyAssertions,
    ImmutableArray<RelationshipAssertion> RelationshipAssertions,
    ImmutableArray<CatalogFileEvidenceReceipt> FileEvidenceReceipts,
    ImmutableArray<CatalogReferenceEvidenceReceipt> ReferenceEvidenceReceipts,
    ImmutableArray<EvidenceBinding> EvidenceBindings)
{
    public ImmutableArray<SourceNativeLocationTypeAssertion> SourceNativeLocationTypeAssertions { get; init; } = [];
    public ImmutableArray<LocationSemanticClassificationAssertion> LocationSemanticClassificationAssertions { get; init; } = [];
    public ImmutableArray<CanonicalRecordLifecycleAssertion> RecordLifecycleAssertions { get; init; } = [];
}

public sealed record CanonicalKnowledgeCatalogSnapshot(
    long Revision,
    ImmutableArray<CatalogSourceRecord> Sources,
    ImmutableArray<SourceArtifactRecord> Artifacts,
    ImmutableArray<CatalogSourceRevisionRecord> SourceRevisions,
    ImmutableArray<CanonicalKnowledgeRecord> KnowledgeRecords,
    ImmutableArray<TerminologyAssertion> TerminologyAssertions,
    ImmutableArray<RelationshipAssertion> RelationshipAssertions,
    ImmutableArray<CatalogFileEvidenceReceipt> FileEvidenceReceipts,
    ImmutableArray<CatalogReferenceEvidenceReceipt> ReferenceEvidenceReceipts,
    ImmutableArray<EvidenceBinding> EvidenceBindings)
{
    public static CanonicalKnowledgeCatalogSnapshot Empty { get; } = new(
        0, [], [], [], [], [], [], [], [], []);

    /// <summary>
    /// Exact adapter descriptors retained for adapter-bound (v2) source revisions.
    /// Schema-v1 stores load this collection as empty without reinterpreting their v1 revisions.
    /// </summary>
    public ImmutableArray<GameKnowledgeAdapterDescriptor> AdapterDescriptors { get; init; } = [];

    /// <summary>
    /// Adapter/scope/format coordinates for the v2 revisions also present in <see cref="SourceRevisions"/>.
    /// </summary>
    public ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> AdapterBoundSourceRevisions { get; init; } = [];

    public ImmutableArray<CanonicalCorrelationEnvelope> CorrelationEnvelopes { get; init; } = [];

    public ImmutableArray<UnresolvedSourceAssertion> UnresolvedSourceAssertions { get; init; } = [];

    public ImmutableArray<SourceAcquisitionReceipt> AcquisitionReceipts { get; init; } = [];

    public ImmutableArray<SourceArtifactAcquisitionBinding> ArtifactAcquisitionBindings { get; init; } = [];

    /// <summary>
    /// Immutable, structurally verified packages imported into this shared preproduction library.
    /// Candidate status is retained as status and does not imply publication approval.
    /// </summary>
    public ImmutableArray<CanonicalCatalogPackage> ImportedPackages { get; init; } = [];

    /// <summary>Schema-v3 flat indexes for the frozen universal Location contract.</summary>
    public ImmutableArray<SourceNativeLocationTypeAssertion> SourceNativeLocationTypeAssertions { get; init; } = [];
    public ImmutableArray<LocationSemanticClassificationAssertion> LocationSemanticClassificationAssertions { get; init; } = [];
    public ImmutableArray<CanonicalRecordLifecycleAssertion> RecordLifecycleAssertions { get; init; } = [];
    public ImmutableArray<CorrelatedRelationshipEnvelope> CorrelatedRelationshipEnvelopes { get; init; } = [];
    public ImmutableArray<LocationCoverageReport> LocationCoverageReports { get; init; } = [];

    /// <summary>Schema-v4 package-import indexes for selector projection and source-backed Instructions.</summary>
    public ImmutableArray<CanonicalSemanticClassificationAssertion> SemanticClassificationAssertions { get; init; } = [];
    public ImmutableArray<CanonicalRecordContributionAssertion> RecordContributionAssertions { get; init; } = [];
    public ImmutableArray<CanonicalOrganizationalValueAssertion> OrganizationalValueAssertions { get; init; } = [];
    public ImmutableArray<InstructionAssertion> InstructionAssertions { get; init; } = [];
    public ImmutableArray<InstructionEvidenceBinding> InstructionEvidenceBindings { get; init; } = [];
    public ImmutableArray<InstructionConflictGroup> InstructionConflictGroups { get; init; } = [];

    /// <summary>
    /// Schema-v5 envelopes that preserve secondary-source provenance while attaching typed facts to
    /// immutable records owned by a different source revision.
    /// </summary>
    public ImmutableArray<CrossSourceCanonicalAssertion> CrossSourceAssertions { get; init; } = [];
    public ImmutableArray<CrossSourceTargetLinkClaim> CrossSourceTargetLinkClaims { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceClaimContent> UnresolvedCrossSourceClaimContents { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceEvidenceBinding> UnresolvedCrossSourceEvidenceBindings { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceAssertion> UnresolvedCrossSourceAssertions { get; init; } = [];

    public CatalogSourceRecord? FindSource(CatalogSourceId id) =>
        Sources.FirstOrDefault(value => value.Id == id);

    public SourceArtifactRecord? FindArtifact(SourceArtifactId id) =>
        Artifacts.FirstOrDefault(value => value.Id == id);

    public CanonicalKnowledgeRecord? FindKnowledgeRecord(KnowledgeRecordId id) =>
        KnowledgeRecords.FirstOrDefault(value => value.Id == id);

    public CatalogSourceRevisionRecord? FindSourceRevision(CatalogSourceRevisionId id) =>
        SourceRevisions.FirstOrDefault(value => value.Id == id);

    public CatalogEvidenceLookup? FindEvidenceReceipt(EvidenceReceiptId id)
    {
        var file = FileEvidenceReceipts.FirstOrDefault(value => value.Id == id);
        if (file is not null)
            return new(id, EvidenceVerificationKind.FileVerified, file.Receipt, null);

        var reference = ReferenceEvidenceReceipts.FirstOrDefault(value => value.Id == id);
        return reference is null
            ? null
            : new(id, EvidenceVerificationKind.ReferenceVerified, null, reference.Receipt);
    }

    public EvidenceBinding? FindEvidenceBinding(EvidenceBindingId id) =>
        EvidenceBindings.FirstOrDefault(value => value.Id == id);

    public GameKnowledgeAdapterDescriptor? FindAdapterRevision(KnowledgeAdapterRevisionId id) =>
        AdapterDescriptors.FirstOrDefault(value => value.RevisionId == id);

    public AdapterBoundCatalogSourceRevisionRecord? FindAdapterBoundSourceRevision(CatalogSourceRevisionId id) =>
        AdapterBoundSourceRevisions.FirstOrDefault(value => value.Revision.Id == id);

    public CanonicalCorrelationEnvelope? FindCorrelation(CorrelationRecordId id) =>
        CorrelationEnvelopes.FirstOrDefault(value => value.Id == id);

    public UnresolvedSourceAssertion? FindUnresolvedSourceAssertion(UnresolvedSourceAssertionId id) =>
        UnresolvedSourceAssertions.FirstOrDefault(value => value.Id == id);

    public SourceAcquisitionReceipt? FindAcquisitionReceipt(SourceAcquisitionReceiptId id) =>
        AcquisitionReceipts.FirstOrDefault(value => value.Id == id);

    public ImmutableArray<SourceArtifactAcquisitionBinding> FindArtifactAcquisitionBindings(SourceArtifactId id) =>
        ArtifactAcquisitionBindings.Where(value => value.ArtifactId == id).ToImmutableArray();

    public CanonicalCatalogPackage? FindImportedPackage(CatalogPackageId id) =>
        ImportedPackages.FirstOrDefault(value => value.Id == id);

    public SourceNativeLocationTypeAssertion? FindLocationNativeType(SourceNativeLocationTypeAssertionId id) =>
        SourceNativeLocationTypeAssertions.FirstOrDefault(value => value.Id == id);

    public LocationSemanticClassificationAssertion? FindLocationClassification(
        LocationSemanticClassificationAssertionId id) =>
        LocationSemanticClassificationAssertions.FirstOrDefault(value => value.Id == id);

    public LocationCoverageReport? FindLocationCoverageReport(LocationCoverageReportId id) =>
        LocationCoverageReports.FirstOrDefault(value => value.Id == id);

    public CanonicalSemanticClassificationAssertion? FindSemanticClassification(
        CanonicalSemanticClassificationAssertionId id) =>
        SemanticClassificationAssertions.FirstOrDefault(value => value.Id == id);

    public CanonicalRecordContributionAssertion? FindRecordContribution(
        CanonicalRecordContributionAssertionId id) =>
        RecordContributionAssertions.FirstOrDefault(value => value.Id == id);

    public CanonicalOrganizationalValueAssertion? FindOrganizationalValue(
        CanonicalOrganizationalValueAssertionId id) =>
        OrganizationalValueAssertions.FirstOrDefault(value => value.Id == id);

    public InstructionAssertion? FindInstructionAssertion(InstructionAssertionId id) =>
        InstructionAssertions.FirstOrDefault(value => value.Id == id);

    public InstructionEvidenceBinding? FindInstructionEvidenceBinding(InstructionEvidenceBindingId id) =>
        InstructionEvidenceBindings.FirstOrDefault(value => value.Id == id);

    public InstructionConflictGroup? FindInstructionConflictGroup(InstructionConflictGroupId id) =>
        InstructionConflictGroups.FirstOrDefault(value => value.Id == id);

    public CrossSourceCanonicalAssertion? FindCrossSourceAssertion(CrossSourceCanonicalAssertionId id) =>
        CrossSourceAssertions.FirstOrDefault(value => value.Id == id);

    public CrossSourceTargetLinkClaim? FindCrossSourceTargetLinkClaim(CrossSourceTargetLinkClaimId id) =>
        CrossSourceTargetLinkClaims.FirstOrDefault(value => value.Id == id);

    public UnresolvedCrossSourceAssertion? FindUnresolvedCrossSourceAssertion(UnresolvedCrossSourceAssertionId id) =>
        UnresolvedCrossSourceAssertions.FirstOrDefault(value => value.Id == id);

    public UnresolvedCrossSourceClaimContent? FindUnresolvedCrossSourceClaimContent(
        UnresolvedCrossSourceClaimContentId id) =>
        UnresolvedCrossSourceClaimContents.FirstOrDefault(value => value.Id == id);

    public UnresolvedCrossSourceEvidenceBinding? FindUnresolvedCrossSourceEvidenceBinding(
        UnresolvedCrossSourceEvidenceBindingId id) =>
        UnresolvedCrossSourceEvidenceBindings.FirstOrDefault(value => value.Id == id);
}

public sealed record CatalogEvidenceLookup(
    EvidenceReceiptId Id,
    EvidenceVerificationKind Verification,
    FileEvidenceReceipt? FileReceipt,
    ReferenceEvidenceReceipt? ReferenceReceipt);

public sealed record CanonicalCatalogLoadResult(
    CanonicalKnowledgeCatalogSnapshot Snapshot,
    ImmutableArray<string> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}

public enum CanonicalCatalogAppendStatus
{
    Appended,
    Unchanged,
    Conflict,
    Invalid,
    Failed,
}

public sealed record CanonicalCatalogAppendResult(
    CanonicalCatalogAppendStatus Status,
    long Revision,
    string Detail);

public enum CanonicalCatalogImportStatus
{
    Imported,
    Unchanged,
    Conflict,
    Invalid,
    Failed,
}

public sealed record CanonicalCatalogImportResult(
    CanonicalCatalogImportStatus Status,
    long Revision,
    int ImportedSourceRevisionCount,
    string Detail);
