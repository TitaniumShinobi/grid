using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Pinned Location hierarchy containment assertions including reference geography endpoints.</summary>
public sealed class GtaVLocationHierarchySecondaryAssertionAdapter
{
    private readonly GtaVLocationHierarchyCorpusIndex index;
    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public GtaVLocationHierarchySecondaryAssertionAdapter(ContentDigest digest, GtaVLocationHierarchyCorpusIndex index)
    {
        this.index = index;
        Descriptor = GtaVLocationHierarchyEvidenceBuilder.DescriptorFor("location-hierarchy", digest, KnowledgeKind.Location);
    }
    public GtaVSecondaryAssertionBatch Extract(CanonicalCatalogPayload origin, KnowledgeSourceScope scope)
    {
        var builder = new GtaVLocationHierarchyEvidenceBuilder(origin, scope, Descriptor, index.Artifacts);
        var resolution = index.ResolveForRegistration(origin);
        if (!resolution.UnresolvedEdges.IsEmpty)
            throw new InvalidDataException("Hierarchy endpoint resolution left unresolved edges.");
        if (resolution.CorrelatedRows.Length != index.ExpectedRelationshipCount)
            throw new InvalidDataException("Resolved hierarchy row count does not match the pinned source catalog.");
        var native = new GtaVPresentationEvidence(index.NativeTable, "Zones", true);
        foreach (var row in resolution.CorrelatedRows)
        {
            var childPath = row.SourceFieldPath + "/child";
            var parentPath = row.SourceFieldPath + "/parent";
            var reference = new GtaVPresentationEvidence(row.EvidenceArtifact, row.SourceFieldPath, true);
            var child = builder.EnsureEndpoint(row.Child, row.EvidenceArtifact, childPath, row.SourceFieldPath);
            var parent = builder.EnsureEndpoint(row.Parent, row.EvidenceArtifact, parentPath, row.SourceFieldPath);
            var childIdentityEvidence = new GtaVPresentationEvidence(row.EvidenceArtifact, childPath, true);
            var link = row.Child.ReferenceOnly
                ? builder.LinkReferenceEndpoint(child, childIdentityEvidence)
                : builder.Link(child, native);
            var relationship = new RelationshipAssertion(child.Id, builder.RevisionId,
                LocationRelationshipSemantics.ContainedBy, row.SourceNativeRelationshipType,
                row.SourceFieldPath, parent.NativeIdentity, parent.Id);
            builder.AddRelationship(child, link, relationship, [reference]);
        }
        return builder.Build();
    }

    public GtaVSecondaryAssertionBatch ProjectFromCandidate(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope scope,
        CanonicalRegistrationCandidate candidate,
        GtaVLocationHierarchyCorpusIndex index)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var correlated = candidate.Relationships
            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated)
            .OrderBy(r => r.SourceFieldPath, StringComparer.Ordinal)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
        if (correlated.Length != index.ExpectedRelationshipCount)
            throw new InvalidDataException("Registration candidate relationship count does not match the pinned hierarchy catalog.");
        var builder = new GtaVLocationHierarchyEvidenceBuilder(origin, scope, Descriptor, index.Artifacts);
        var evidenceById = candidate.Input.Evidence.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var entityRecords = new Dictionary<string, CanonicalKnowledgeRecord>(StringComparer.Ordinal);
        foreach (var entity in candidate.Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var record = builder.EnsureRegistrationEntity(entity, evidenceById);
            if (!string.Equals(record.Id.Value, entity.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Projected knowledge record id must match the registration candidate entity id.");
            entityRecords[entity.Id] = record;
        }
        var native = new GtaVPresentationEvidence(index.NativeTable, "Zones", true);
        foreach (var relationship in correlated)
        {
            if (string.IsNullOrWhiteSpace(relationship.SourceId) ||
                string.IsNullOrWhiteSpace(relationship.SourceFieldPath) ||
                string.IsNullOrWhiteSpace(relationship.SourceNativeRelationshipType))
                throw new InvalidDataException("Correlated hierarchy relationship lacks source provenance.");
            var child = entityRecords[relationship.SubjectId];
            var parent = entityRecords[relationship.TargetId];
            var artifact = index.Artifacts.FirstOrDefault(a => a.Id.Value == relationship.SourceId)
                ?? throw new InvalidDataException("Hierarchy relationship source artifact is absent from the pinned index.");
            var reference = new GtaVPresentationEvidence(artifact, relationship.SourceFieldPath, true);
            var childPath = relationship.SourceFieldPath + "/child";
            var childEntity = candidate.Entities.Single(e => e.Id == relationship.SubjectId);
            var childIsReference = string.Equals(
                childEntity.NativeNamespace,
                GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace,
                StringComparison.Ordinal);
            var childIdentityEvidence = new GtaVPresentationEvidence(artifact, childPath, true);
            var link = childIsReference
                ? builder.LinkReferenceEndpoint(child, childIdentityEvidence)
                : builder.Link(child, native);
            var relationshipAssertion = new RelationshipAssertion(
                child.Id,
                builder.RevisionId,
                LocationRelationshipSemantics.ContainedBy,
                relationship.SourceNativeRelationshipType!,
                relationship.SourceFieldPath,
                parent.NativeIdentity,
                parent.Id);
            builder.AddRelationship(child, link, relationshipAssertion, [reference]);
        }
        return builder.Build();
    }
}

internal sealed class GtaVLocationHierarchyEvidenceBuilder
{
    private const string Method = "grid.gta-v.location-hierarchy.pinned-exact-name-join";
    private readonly CanonicalCatalogPayload origin;
    private readonly KnowledgeSourceScope scope;
    private readonly GameKnowledgeAdapterDescriptor descriptor;
    private readonly CatalogSourceRecord source;
    private readonly AdapterBoundCatalogSourceRevisionRecord revision;
    private readonly ImmutableArray<FrozenSourceArtifact> artifacts;
    private readonly List<CanonicalKnowledgeRecord> additionalKnowledgeRecords = [];
    private readonly List<AdapterBoundCatalogSourceRevisionRecord> additionalReferenceSourceRevisions = [];
    private readonly List<CatalogSourceRecord> additionalReferenceSources = [];
    private readonly Dictionary<SourceArtifactId, (CatalogSourceRecord Source, CatalogSourceRevisionId RevisionId)> referenceOrigins = [];
    private readonly Dictionary<string, CanonicalKnowledgeRecord> materializedReferenceEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CanonicalKnowledgeRecord> materializedRegistrationEntities = new(StringComparer.Ordinal);
    private readonly List<TerminologyAssertion> titles = [];
    private readonly List<RelationshipAssertion> relationships = [];
    private readonly List<CanonicalSemanticClassificationAssertion> classes = [];
    private readonly List<CanonicalOrganizationalValueAssertion> organizations = [];
    private readonly List<CatalogFileEvidenceReceipt> files = [];
    private readonly List<CatalogReferenceEvidenceReceipt> references = [];
    private readonly List<EvidenceBinding> bindings = [];
    private readonly List<CrossSourceTargetLinkClaim> links = [];
    private readonly List<CrossSourceCanonicalAssertion> envelopes = [];
    private readonly Dictionary<CrossSourceTargetLinkClaimId, (ImmutableArray<EvidenceBinding> Origins, EvidenceReceiptId Target, EvidenceReceiptId Reference, EvidenceBindingId TargetBinding, EvidenceBindingId ReferenceBinding)> closure = [];
    public CatalogSourceRevisionId RevisionId => revision.Revision.Id;

    public static GameKnowledgeAdapterDescriptor DescriptorFor(string domain, ContentDigest digest, KnowledgeKind kind) => new(
        new KnowledgeAdapterId("grid.gta-v.enhanced.presentation-" + domain), "1", digest, 1, "gta-presentation-mixed-evidence-v1",
        [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
        [new SupportedKnowledgeFormat(GtaVPresentationCorpusIndex.ReferenceFormatId, "1", ["reference-snapshot"], ["PinnedReference"], [kind], true, false, false),
         new SupportedKnowledgeFormat("rockstar.gta-v.population-zones-ipl", "1", ["rpf7-member"], ["NameLabel"], [kind], true, true, false)],
        new KnowledgeAdapterResourceLimits(1024L * 1024, 3, 5, 4), KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);

    private readonly Func<string, string> resolvePinnedRevision;

    public GtaVLocationHierarchyEvidenceBuilder(CanonicalCatalogPayload origin, KnowledgeSourceScope scope, GameKnowledgeAdapterDescriptor descriptor, IEnumerable<FrozenSourceArtifact> artifacts,
        Func<string, string>? resolvePinnedRevision = null, string? revisionVersion = null)
    {
        if (scope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId || scope.ScopeKind != KnowledgeSourceScopeKind.BaseGame) throw new InvalidDataException("Presentation requires Enhanced base scope.");
        this.origin = origin; this.scope = scope; this.descriptor = descriptor;
        this.resolvePinnedRevision = resolvePinnedRevision ?? GtaVLocationHierarchyCorpusIndex.ResolvePinnedReferenceRevision;
        this.artifacts = artifacts.DistinctBy(x => x.Id).OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var native = SourceNativeIdentifier.FromExactUtf8("grid.gta-v.presentation", "PinnedReferenceSet", descriptor.AdapterId.Value, "grid.exact-utf8", 1);
        source = new(CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, native), KnowledgeSourceKind.FrozenRepositoryDataset, native);
        // The join index selects which pinned sentences become edges but is not an admitted artifact, so its digest
        // must enter the revision identity; otherwise a changed edge set would reopen an imported immutable revision.
        var version = SourceNativeVersion.FromExactUtf8("grid.gta-v.presentation",
            revisionVersion ?? "1+join-index.sha256." + GtaVLocationHierarchyCorpusIndex.JoinIndexDigest, "grid.exact-utf8", 1);
        var formats = origin.SourceRevisions.SelectMany(x => x.ArtifactFormats).Concat(this.artifacts.Select(x => x.FormatBinding))
            .Where(x => descriptor.SupportedFormats.Any(f => f.FormatId == x.Format.FormatId && f.ExactFormatVersion == x.Format.ExactFormatVersion))
            .DistinctBy(x => x.ArtifactId).OrderBy(x => x.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
        var ids = formats.Select(x => x.ArtifactId).ToImmutableArray();
        var id = CatalogSourceRevisionId.DeriveV2(source.Id, version, ids, descriptor.RevisionId);
        revision = new(new CatalogSourceRevisionRecord(id, source.Id, version, ids), descriptor.RevisionId, scope, formats);
    }

    public CanonicalKnowledgeRecord EnsureRegistrationEntity(
        RegisteredCanonicalEntity entity,
        IReadOnlyDictionary<string, RegistrationEvidence> evidenceById)
    {
        if (materializedRegistrationEntities.TryGetValue(entity.Id, out var cached))
            return cached;
        if (string.Equals(entity.NativeNamespace, GtaVLocationRegistrationRules.ReferenceSubjectNamespace, StringComparison.Ordinal))
        {
            // The native id is the identity page coordinate; the record is bound to that page's artifact.
            var identity = entity.EvidenceIds.Select(id => evidenceById[id])
                .Where(e => artifacts.Any(a => a.Id.Value == e.SourceId && a.SourceCoordinate.ExactRepresentation == entity.NativeId))
                .OrderBy(e => e.Locator, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).First();
            var subjectRecord = MaterializeReferenceRecord(entity, FindArtifact(new SourceArtifactId(identity.SourceId)), identity.Locator);
            materializedRegistrationEntities[entity.Id] = subjectRecord;
            return subjectRecord;
        }
        if (string.Equals(
                entity.NativeNamespace,
                GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace,
                StringComparison.Ordinal))
        {
            var chosen = entity.EvidenceIds
                .Select(id => evidenceById[id])
                .OrderBy(e => e.Locator, StringComparer.Ordinal)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .First();
            var artifact = FindArtifact(new SourceArtifactId(chosen.SourceId));
            var record = MaterializeReferenceRecord(entity, artifact, chosen.Locator);
            materializedRegistrationEntities[entity.Id] = record;
            return record;
        }

        var native = origin.KnowledgeRecords.SingleOrDefault(r => r.Id.Value == entity.Id)
            ?? origin.KnowledgeRecords.Single(r =>
                r.Kind == KnowledgeKind.Location &&
                string.Equals(r.NativeIdentity.Namespace, entity.NativeNamespace, StringComparison.Ordinal) &&
                string.Equals(r.NativeIdentity.ExactRepresentation, entity.NativeId, StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(native.Id.Value, entity.Id, StringComparison.Ordinal))
            throw new InvalidDataException("Native registration entity id must match the baseline knowledge record id.");
        materializedRegistrationEntities[entity.Id] = native;
        return native;
    }

    private CanonicalKnowledgeRecord MaterializeReferenceRecord(
        RegisteredCanonicalEntity entity,
        FrozenSourceArtifact evidenceArtifact,
        string identityFieldPath)
    {
        if (materializedReferenceEndpoints.TryGetValue(entity.Id, out var existing))
            return existing;
        var (referenceSource, referenceOriginRevisionId) = ReferenceOrigin(evidenceArtifact);
        var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
            entity.NativeNamespace,
            entity.NativeNamespace == GtaVLocationRegistrationRules.ReferenceSubjectNamespace
                ? "PinnedReferencePageSubject"
                : GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeObjectType,
            entity.NativeId,
            "grid.exact-utf8",
            1);
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, nativeIdentity);
        if (!KnowledgeRecordId.IsRegistrationEntityBacked(entity.Id))
            throw new InvalidDataException("Reference geography registration entity id is required for publication projection.");
        var recordId = new KnowledgeRecordId(entity.Id);
        var record = new CanonicalKnowledgeRecord(
            recordId,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            scope.ExactGameVersion,
            scope.ExactModVersion,
            referenceOriginRevisionId,
            KnowledgeKind.Location,
            nativeRecordId,
            nativeIdentity);
        additionalKnowledgeRecords.Add(record);
        materializedReferenceEndpoints[entity.Id] = record;
        var identityEvidence = new GtaVPresentationEvidence(evidenceArtifact, identityFieldPath, true);
        var identityReceiptId = ReferenceIdentityEvidence(record, referenceSource, identityEvidence, nativeIdentity);
        Bind(identityReceiptId, record, EvidenceClaimKind.KnowledgeIdentity, identityFieldPath, null);
        var primaryName = entity.Names.First(n => !n.IsAlias).Value;
        var link = LinkReferenceEndpoint(record, identityEvidence);
        Title(record, link, primaryName, identityFieldPath, [identityEvidence]);
        return record;
    }

    public CanonicalKnowledgeRecord EnsureEndpoint(GtaVLocationHierarchyResolvedEndpoint endpoint, FrozenSourceArtifact evidenceArtifact, string identityFieldPath, string containmentFieldPath)
    {
        if (!endpoint.ReferenceOnly)
            return endpoint.CatalogRecord ?? throw new InvalidDataException("Native hierarchy endpoint lacks catalog record.");
        if (materializedReferenceEndpoints.TryGetValue(endpoint.EntityKey, out var existing))
            return existing;
        var artifact = FindArtifact(evidenceArtifact.Id);
        var (referenceSource, referenceOriginRevisionId) = ReferenceOrigin(artifact);
        var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
            GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace,
            GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeObjectType,
            endpoint.PrimaryName,
            "grid.exact-utf8",
            1);
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, nativeIdentity);
        if (!KnowledgeRecordId.IsRegistrationEntityBacked(endpoint.EntityKey))
            throw new InvalidDataException("Reference geography entity key must be a registration entity id.");
        var recordId = new KnowledgeRecordId(endpoint.EntityKey);
        var record = new CanonicalKnowledgeRecord(
            recordId,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            scope.ExactGameVersion,
            scope.ExactModVersion,
            referenceOriginRevisionId,
            KnowledgeKind.Location,
            nativeRecordId,
            nativeIdentity);
        additionalKnowledgeRecords.Add(record);
        materializedReferenceEndpoints[endpoint.EntityKey] = record;
        var identityEvidence = new GtaVPresentationEvidence(artifact, identityFieldPath, true);
        var identityReceiptId = ReferenceIdentityEvidence(record, referenceSource, identityEvidence, nativeIdentity);
        Bind(identityReceiptId, record, EvidenceClaimKind.KnowledgeIdentity, identityFieldPath, null);
        var containmentEvidence = new GtaVPresentationEvidence(artifact, containmentFieldPath, true);
        var link = LinkReferenceEndpoint(record, containmentEvidence);
        Title(record, link, endpoint.PrimaryName, identityFieldPath, [identityEvidence]);
        return record;
    }

    private (CatalogSourceRecord Source, CatalogSourceRevisionId RevisionId) ReferenceOrigin(FrozenSourceArtifact artifact)
    {
        if (referenceOrigins.TryGetValue(artifact.Id, out var cached))
            return cached;
        var native = SourceNativeIdentifier.FromExactUtf8(
            "grid.gta-v.location-hierarchy.reference-geography",
            "PinnedReferencePage",
            artifact.SourceCoordinate.ExactRepresentation,
            "grid.exact-utf8",
            1);
        var referenceSource = new CatalogSourceRecord(
            CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, native),
            KnowledgeSourceKind.FrozenRepositoryDataset,
            native);
        var version = SourceNativeVersion.FromExactUtf8("grid.gta-v.location-hierarchy.reference-geography", "1", "grid.exact-utf8", 1);
        var revisionId = CatalogSourceRevisionId.DeriveV2(referenceSource.Id, version, [artifact.Id], descriptor.RevisionId);
        var bound = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, referenceSource.Id, version, [artifact.Id]),
            descriptor.RevisionId,
            scope,
            [artifact.FormatBinding]);
        additionalReferenceSourceRevisions.Add(bound);
        additionalReferenceSources.Add(referenceSource);
        cached = (referenceSource, revisionId);
        referenceOrigins[artifact.Id] = cached;
        return cached;
    }

    public CrossSourceTargetLinkClaim Link(CanonicalKnowledgeRecord target, GtaVPresentationEvidence map)
    {
        var identities = IdentityBindings(target);
        if (identities.IsEmpty) throw new InvalidDataException("Hierarchy endpoint lacks source identity evidence.");
        var identity = identities[0];
        var prior = origin.FileEvidenceReceipts.Single(x => x.Id == identity.EvidenceReceiptId).Receipt;
        var coordinate = SourceNativeIdentifier.FromExactUtf8("grid.gta-v.location-hierarchy.reference-table", "ReferenceTable", map.Artifact.SourceCoordinate.ExactRepresentation + "#" + map.FieldPath, "grid.exact-utf8", 1);
        var id = CrossSourceTargetLinkClaimId.DeriveV1(target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, target.NativeIdentity, prior.SourceArtifactId, prior.SourceFieldPath, revision.Revision.Id, coordinate, map.Artifact.Id, map.FieldPath, CrossSourceTargetLinkKind.VersionedExactMapping, Method, "1");
        var link = new CrossSourceTargetLinkClaim(id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, target.NativeIdentity, prior.SourceArtifactId, prior.SourceFieldPath, revision.Revision.Id, coordinate, map.Artifact.Id, map.FieldPath, CrossSourceTargetLinkKind.VersionedExactMapping, Method, "1");
        if (closure.ContainsKey(id)) return link;
        links.Add(link);
        var receipt = new FileEvidenceReceipt(revision.Revision.Id, prior.SourceArtifactId, prior.ArtifactDigest, prior.ParserId, prior.ParserVersion, prior.NativeRecordLocator, prior.SourceFieldPath, prior.ByteOffset, prior.ByteLength, prior.InterpretedBytesDigest, prior.ObservedAtUtc, target.NativeIdentity);
        var file = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(receipt), receipt); files.Add(file);
        var reference = Evidence(target, map, coordinate);
        var content = EvidenceClaimContentId.DeriveV1(link);
        closure.Add(id, (identities, file.Id, reference, Bind(file.Id, target, EvidenceClaimKind.CrossSourceTargetLink, prior.SourceFieldPath, content).Id, Bind(reference, target, EvidenceClaimKind.CrossSourceTargetLink, map.FieldPath, content).Id));
        return link;
    }

    public CrossSourceTargetLinkClaim LinkReferenceEndpoint(CanonicalKnowledgeRecord target, GtaVPresentationEvidence map)
    {
        var identities = IdentityBindings(target);
        if (identities.IsEmpty) throw new InvalidDataException("Reference hierarchy endpoint lacks identity evidence.");
        var identity = identities[0];
        var priorReference = ResolveReferenceReceipt(identity.EvidenceReceiptId);
        var prior = priorReference.Receipt;
        var coordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.gta-v.location-hierarchy.reference-table",
            "ReferenceTable",
            map.Artifact.SourceCoordinate.ExactRepresentation + "#" + map.FieldPath,
            "grid.exact-utf8",
            1);
        var id = CrossSourceTargetLinkClaimId.DeriveV1(
            target.Id,
            target.SourceRevisionId,
            target.NativeRecordIdentityId,
            target.NativeIdentity,
            prior.ResponseArtifactId,
            prior.ResponseFieldPath,
            revision.Revision.Id,
            coordinate,
            map.Artifact.Id,
            map.FieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            Method,
            "1");
        var link = new CrossSourceTargetLinkClaim(
            id,
            target.Id,
            target.SourceRevisionId,
            target.NativeRecordIdentityId,
            target.NativeIdentity,
            prior.ResponseArtifactId,
            prior.ResponseFieldPath,
            revision.Revision.Id,
            coordinate,
            map.Artifact.Id,
            map.FieldPath,
            CrossSourceTargetLinkKind.VersionedExactMapping,
            Method,
            "1");
        if (closure.ContainsKey(id)) return link;
        links.Add(link);
        var targetReceipt = new ReferenceEvidenceReceipt(
            source.Id,
            revision.Revision.Id,
            prior.ResponseArtifactId,
            prior.ResponseContentDigest,
            target.NativeIdentity,
            prior.NativeRevisionIdentity,
            prior.ResponseFieldPath,
            prior.RetrievedAtUtc);
        var targetRecord = new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(targetReceipt), targetReceipt);
        references.Add(targetRecord);
        var assertingReceipt = Evidence(target, map, coordinate);
        var content = EvidenceClaimContentId.DeriveV1(link);
        closure.Add(id, (
            identities,
            targetRecord.Id,
            assertingReceipt,
            Bind(targetRecord.Id, target, EvidenceClaimKind.CrossSourceTargetLink, prior.ResponseFieldPath, content).Id,
            Bind(assertingReceipt, target, EvidenceClaimKind.CrossSourceTargetLink, map.FieldPath, content).Id));
        return link;
    }

    private CatalogReferenceEvidenceReceipt ResolveReferenceReceipt(EvidenceReceiptId receiptId) =>
        references.FirstOrDefault(x => x.Id == receiptId)
        ?? origin.ReferenceEvidenceReceipts.FirstOrDefault(x => x.Id == receiptId)
        ?? throw new InvalidDataException("Reference hierarchy evidence receipt is absent.");

    public void Title(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, string title, string field, IEnumerable<GtaVPresentationEvidence> evidence, string languageTag = "en-US")
    {
        var value = new TerminologyAssertion(target.Id, revision.Revision.Id, TerminologyAssertionRole.PrimaryName, title, field, languageTag);
        titles.Add(value); Envelope(target, link, EvidenceClaimKind.Terminology, CrossSourceCanonicalAssertionKind.Terminology, EvidenceClaimContentId.DeriveV1(value), evidence);
    }
    public void AddRelationship(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, RelationshipAssertion value, IEnumerable<GtaVPresentationEvidence> evidence)
    {
        relationships.Add(value); Envelope(target, link, EvidenceClaimKind.Relationship, CrossSourceCanonicalAssertionKind.Relationship, EvidenceClaimContentId.DeriveV1(value), evidence);
    }
    public void Classify(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, CanonicalSemanticRoleId role, string field, IEnumerable<GtaVPresentationEvidence> evidence)
    {
        var id = CanonicalSemanticClassificationAssertionId.DeriveV1(target.Id, revision.Revision.Id, role, "grid.gta-v.presentation", "1", Method, "1", field);
        var value = new CanonicalSemanticClassificationAssertion(id, target.Id, revision.Revision.Id, role, "grid.gta-v.presentation", "1", Method, "1", field);
        classes.Add(value); Envelope(target, link, EvidenceClaimKind.SemanticClassification, CrossSourceCanonicalAssertionKind.SemanticClassification, EvidenceClaimContentId.DeriveV1(value), evidence);
    }
    public void Organize(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, CanonicalOrganizationalSemanticId dimension, string exactValue, string label, string field, IEnumerable<GtaVPresentationEvidence> evidence)
    {
        var native = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.presentation.organization", "LocalizationKey", exactValue, "grid.exact-utf8", 1);
        var id = CanonicalOrganizationalValueAssertionId.DeriveV2(target.Id, revision.Revision.Id, dimension, native, label, Method, "1", field, "en-US");
        var value = new CanonicalOrganizationalValueAssertion(id, target.Id, revision.Revision.Id, dimension, native, label, Method, "1", field, "en-US");
        organizations.Add(value); Envelope(target, link, EvidenceClaimKind.OrganizationalValue, CrossSourceCanonicalAssertionKind.OrganizationalValue, EvidenceClaimContentId.DeriveV1(value), evidence);
    }

    private ImmutableArray<EvidenceBinding> IdentityBindings(CanonicalKnowledgeRecord target) =>
        origin.EvidenceBindings
            .Where(x => x.KnowledgeRecordId == target.Id && x.SourceRevisionId == target.SourceRevisionId && x.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && x.ClaimContentId is null)
            .Concat(bindings.Where(x => x.KnowledgeRecordId == target.Id && x.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && x.ClaimContentId is null))
            .DistinctBy(x => x.Id.Value)
            .OrderBy(x => x.ClaimLocator, StringComparer.Ordinal)
            .ThenBy(x => x.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();

    private (EvidenceReceiptId ReceiptId, SourceArtifactId ArtifactId, string FieldPath) ResolveReceipt(EvidenceReceiptId receiptId)
    {
        if (references.FirstOrDefault(x => x.Id == receiptId) is { } localReference)
            return (localReference.Id, localReference.Receipt.ResponseArtifactId, localReference.Receipt.ResponseFieldPath);
        if (origin.ReferenceEvidenceReceipts.FirstOrDefault(x => x.Id == receiptId) is { } originReference)
            return (originReference.Id, originReference.Receipt.ResponseArtifactId, originReference.Receipt.ResponseFieldPath);
        if (origin.FileEvidenceReceipts.FirstOrDefault(x => x.Id == receiptId) is { } originFile)
            return (originFile.Id, originFile.Receipt.SourceArtifactId, originFile.Receipt.SourceFieldPath);
        if (files.FirstOrDefault(x => x.Id == receiptId) is { } localFile)
            return (localFile.Id, localFile.Receipt.SourceArtifactId, localFile.Receipt.SourceFieldPath);
        throw new InvalidDataException("Hierarchy evidence receipt is absent.");
    }

    private FrozenSourceArtifact FindArtifact(SourceArtifactId artifactId) =>
        artifacts.FirstOrDefault(x => x.Id == artifactId)
        ?? throw new InvalidDataException("Hierarchy evidence artifact is absent.");

    private EvidenceReceiptId ReferenceIdentityEvidence(
        CanonicalKnowledgeRecord target,
        CatalogSourceRecord referenceSource,
        GtaVPresentationEvidence evidence,
        SourceNativeIdentifier nativeObject)
    {
        var artifact = evidence.Artifact;
        var exactRevision = resolvePinnedRevision(artifact.SourceCoordinate.ExactRepresentation);
        var referenceRevision = SourceNativeVersion.FromExactUtf8("grid.gta-v.presentation.reference-revision", exactRevision, "grid.exact-utf8", 1);
        var receipt = new ReferenceEvidenceReceipt(referenceSource.Id, target.SourceRevisionId, artifact.Id, artifact.Digest, nativeObject, referenceRevision, evidence.FieldPath, artifact.ObservedAtUtc);
        var record = new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt);
        references.Add(record);
        return record.Id;
    }

    private EvidenceReceiptId Evidence(CanonicalKnowledgeRecord target, GtaVPresentationEvidence evidence, SourceNativeIdentifier? nativeObject = null)
    {
        var artifact = evidence.Artifact;
        if (evidence.Reference)
        {
            var exactRevision = resolvePinnedRevision(artifact.SourceCoordinate.ExactRepresentation);
            var referenceRevision = SourceNativeVersion.FromExactUtf8("grid.gta-v.presentation.reference-revision", exactRevision, "grid.exact-utf8", 1);
            var receipt = new ReferenceEvidenceReceipt(source.Id, revision.Revision.Id, artifact.Id, artifact.Digest, nativeObject ?? artifact.SourceCoordinate, referenceRevision, evidence.FieldPath, artifact.ObservedAtUtc);
            var record = new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt); references.Add(record); return record.Id;
        }
        var file = new FileEvidenceReceipt(revision.Revision.Id, artifact.Id, artifact.Digest, Method, "1", target.NativeIdentity.ExactRepresentation, evidence.FieldPath, evidence.Offset, evidence.Length, null, artifact.ObservedAtUtc, nativeObject ?? target.NativeIdentity);
        var value = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(file), file); files.Add(value); return value.Id;
    }
    private EvidenceBinding Bind(EvidenceReceiptId receipt, CanonicalKnowledgeRecord target, EvidenceClaimKind kind, string field, EvidenceClaimContentId? content)
    {
        var bindingRevision = kind == EvidenceClaimKind.KnowledgeIdentity ? target.SourceRevisionId : revision.Revision.Id;
        var value = new EvidenceBinding(EvidenceBindingId.DeriveV2(receipt, kind, target.Id, bindingRevision, field, content), receipt, kind, target.Id, bindingRevision, field, content); bindings.Add(value); return value;
    }
    private void Envelope(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, EvidenceClaimKind claim, CrossSourceCanonicalAssertionKind kind, EvidenceClaimContentId content, IEnumerable<GtaVPresentationEvidence> evidence)
    {
        var anchor = closure[link.Id];
        var receiptIds = anchor.Origins.Select(x => x.EvidenceReceiptId).Concat(new[] { anchor.Target, anchor.Reference }).ToList();
        var bindingIds = anchor.Origins.Select(x => x.Id).Concat(new[] { anchor.TargetBinding, anchor.ReferenceBinding }).ToList();
        foreach (var item in evidence) { var receipt = Evidence(target, item); receiptIds.Add(receipt); bindingIds.Add(Bind(receipt, target, claim, item.FieldPath, content).Id); }
        var receipts = receiptIds.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var binds = bindingIds.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var id = CrossSourceCanonicalAssertionId.DeriveV1(target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revision.Revision.Id, kind, content, link.Id, link.LinkKind, link.ExactAssertingCoordinate, Method, "1", receipts, binds, [], []);
        envelopes.Add(new(id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revision.Revision.Id, kind, content, link.Id, link.LinkKind, link.ExactAssertingCoordinate, Method, "1", receipts, binds, [], []));
    }
    private ImmutableArray<LocationCoverageReport> BuildReferenceCoverageReports()
    {
        if (additionalKnowledgeRecords.Count == 0) return [];
        var records = additionalKnowledgeRecords.Select(r => r.Id).OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var sourceFamily = new LocationSourceFamilyId("grid.gta-v.location-hierarchy.reference-geography");
        var format = new KnowledgeFormatCoordinate(GtaVPresentationCorpusIndex.ReferenceFormatId, "1");
        var artifactIds = additionalReferenceSourceRevisions
            .SelectMany(x => x.Revision.ArtifactIds)
            .Distinct()
            .OrderBy(x => x.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var revisionIds = additionalReferenceSourceRevisions
            .Select(x => x.Revision.Id)
            .Distinct()
            .OrderBy(x => x.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var declaration = new LocationSourceFamilyDeclaration(sourceFamily, format, descriptor.RevisionId, true, artifactIds, revisionIds, []);
        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate,
            "grid.location-hierarchy.reference-geography",
            "1",
            ContentDigest.ComputeSha256("grid.location-hierarchy.reference-geography.coverage"u8));
        var manifest = new LocationCoverageManifest(
            LocationCoverageManifestId.DeriveV1(scope, "location-hierarchy-reference-geography", false, validation, [declaration]),
            scope,
            "location-hierarchy-reference-geography",
            false,
            validation,
            [declaration]);
        var family = new LocationSourceFamilyCoverage(
            sourceFamily,
            artifactIds,
            revisionIds,
            records.Length,
            records.Length,
            records.Length,
            records,
            [],
            [],
            0,
            0,
            0,
            0);
        return
        [
            LocationCoverageReport.Create(
                manifest,
                [family],
                [new LocationSemanticCategoryCoverage(null, null, records, records.Length)],
                new LocationTerminologyCoverage(records.Length, 0, 0, records.Length, 0),
                new LocationHierarchyCoverage(0, 0, 0, 0, true),
                [],
                []),
        ];
    }

    public GtaVSecondaryAssertionBatch Build() => new(descriptor, revision, artifacts.Select(x => new SourceArtifactRecord(x.Id, x.Digest)).ToImmutableArray(), titles.Distinct().ToImmutableArray(), classes.DistinctBy(x => x.Id).ToImmutableArray(), organizations.DistinctBy(x => x.Id).ToImmutableArray(), files.DistinctBy(x => x.Id).ToImmutableArray(), bindings.DistinctBy(x => x.Id).ToImmutableArray(), links.ToImmutableArray(), envelopes.DistinctBy(x => x.Id).ToImmutableArray())
    {
        AdditionalSources = [source, .. additionalReferenceSources.DistinctBy(x => x.Id.Value).OrderBy(x => x.Id.Value, StringComparer.Ordinal)],
        ReferenceEvidenceReceipts = references.DistinctBy(x => x.Id).ToImmutableArray(),
        RelationshipAssertions = relationships.Distinct().ToImmutableArray(),
        AdditionalKnowledgeRecords = additionalKnowledgeRecords.OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        AdditionalSourceRevisions = additionalReferenceSourceRevisions
            .DistinctBy(x => x.Revision.Id.Value)
            .OrderBy(x => x.Revision.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray(),
        AdditionalLocationCoverageReports = BuildReferenceCoverageReports(),
    };
}
