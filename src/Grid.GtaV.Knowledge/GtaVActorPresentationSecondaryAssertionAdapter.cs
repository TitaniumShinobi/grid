using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed class GtaVActorPresentationSecondaryAssertionAdapter
{
    private readonly GtaVPresentationCorpusIndex index;
    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public GtaVActorPresentationSecondaryAssertionAdapter(ContentDigest digest, GtaVPresentationCorpusIndex index)
    { this.index = index; Descriptor = GtaVPresentationEvidenceBuilder.DescriptorFor("actor", digest, KnowledgeKind.Actor); }

    public GtaVSecondaryAssertionBatch Extract(CanonicalCatalogPayload origin, KnowledgeSourceScope scope)
    {
        var builder = new GtaVPresentationEvidenceBuilder(origin, scope, Descriptor, index.Artifacts.Values);
        foreach (var group in index.Actors.GroupBy(x => x.ModelHash))
        {
            var targets = origin.KnowledgeRecords.Where(x => x.Kind == KnowledgeKind.Actor && x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.resident-ped-model" && x.NativeIdentity.ExactRepresentation == $"0x{group.Key:X8}").ToArray();
            if (targets.Length != 1 || group.Select(x => x.LabelKey).Distinct().Count() != 1) continue;
            var target = targets[0];
            var row = group.First();
            var text = index.ResolveText(row.LabelKey, true);
            if (text is null) continue;
            var reference = new GtaVPresentationEvidence(index.Director, $"func_861/case/{row.Case};func_167/case/{row.Case};func_903/category/{row.Category}", true);
            var link = builder.Link(target, reference);
            var evidence = new[] { reference, GtaVPresentationEvidence.FromText(text) };
            builder.Title(target, link, text.Entry.Text, text.Entry.FieldLocator, evidence);
            builder.Classify(target, link, CanonicalProjectionSemantics.SelectorPlayerAddressable, reference.FieldPath, evidence);
            if (!origin.SemanticClassificationAssertions.Any(x => x.KnowledgeRecordId == target.Id && x.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter))
                builder.Classify(target, link, row.Category == 47 ? CanonicalProjectionSemantics.ActorNamedCharacter : CanonicalProjectionSemantics.ActorGenericType, reference.FieldPath, evidence);
            foreach (var membership in group)
            {
                var label = index.ResolveText(membership.CategoryLabelKey, true);
                if (label is null) continue;
                builder.Organize(target, link, CanonicalProjectionSemantics.ActorSourceCategoryDimensionNode, membership.CategoryLabelKey,
                    label.Entry.Text, label.Entry.FieldLocator, [reference, GtaVPresentationEvidence.FromText(label)]);
            }
        }
        return builder.Build();
    }
}

public sealed record GtaVPresentationEvidence(FrozenSourceArtifact Artifact, string FieldPath, bool Reference, long? Offset = null, long? Length = null)
{
    public static GtaVPresentationEvidence FromText(GtaVPresentationText text) => new(text.Artifact, text.Entry.FieldLocator, false, text.Entry.TextOffset, text.Entry.TextLength);
}

/// <summary>Preserves both the local target identity and every reference/file step of a derived assertion.</summary>
public sealed class GtaVPresentationEvidenceBuilder
{
    private const string Method = "grid.gta-v.presentation.pinned-exact-table";
    private readonly CanonicalCatalogPayload origin;
    private readonly GameKnowledgeAdapterDescriptor descriptor;
    private readonly CatalogSourceRecord source;
    private readonly AdapterBoundCatalogSourceRevisionRecord revision;
    private readonly ImmutableArray<FrozenSourceArtifact> artifacts;
    private readonly List<TerminologyAssertion> titles = [];
    private readonly List<RelationshipAssertion> relationships = [];
    private readonly List<CanonicalSemanticClassificationAssertion> classes = [];
    private readonly List<CanonicalOrganizationalValueAssertion> organizations = [];
    private readonly List<CatalogFileEvidenceReceipt> files = [];
    private readonly List<CatalogReferenceEvidenceReceipt> references = [];
    private readonly List<EvidenceBinding> bindings = [];
    private readonly List<CrossSourceTargetLinkClaim> links = [];
    private readonly List<CrossSourceCanonicalAssertion> envelopes = [];
    private readonly Dictionary<CrossSourceTargetLinkClaimId, (EvidenceBinding Origin, EvidenceReceiptId Target, EvidenceReceiptId Reference, EvidenceBindingId TargetBinding, EvidenceBindingId ReferenceBinding)> closure = [];
    public CatalogSourceRevisionId RevisionId => revision.Revision.Id;

    public static GameKnowledgeAdapterDescriptor DescriptorFor(string domain, ContentDigest digest, KnowledgeKind kind) => new(
        new KnowledgeAdapterId("grid.gta-v.enhanced.presentation-" + domain), "1", digest, 1, "gta-presentation-mixed-evidence-v1",
        [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
        [new SupportedKnowledgeFormat(GtaVPresentationCorpusIndex.ReferenceFormatId, "1", ["reference-snapshot"], ["PinnedReference"], [kind], true, false, false),
         new SupportedKnowledgeFormat(GtaVPresentationCorpusIndex.GxtFormatId, "1", ["rpf7-member"], ["Gxt2"], [kind], true, false, false),
         new SupportedKnowledgeFormat(GtaVActorCorpusIndex.ResidentFormatId, "1", ["rpf7-member"], ["CPedModelInfo__InitDataListPso"], [kind], true, false, false),
         new SupportedKnowledgeFormat(GtaVUgcMissionKnowledgeAdapter.FormatId, "1", ["rpf7-member"], ["GtaVUgcMissionResource"], [kind], true, true, false)],
        new KnowledgeAdapterResourceLimits(128L * 1024 * 1024, 20_000, 20_000, 1), KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);

    public GtaVPresentationEvidenceBuilder(CanonicalCatalogPayload origin, KnowledgeSourceScope scope, GameKnowledgeAdapterDescriptor descriptor, IEnumerable<FrozenSourceArtifact> artifacts)
    {
        if (scope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId || scope.ScopeKind != KnowledgeSourceScopeKind.BaseGame) throw new InvalidDataException("Presentation requires Enhanced base scope.");
        this.origin = origin; this.descriptor = descriptor;
        this.artifacts = artifacts.DistinctBy(x => x.Id).OrderBy(x => x.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var native = SourceNativeIdentifier.FromExactUtf8("grid.gta-v.presentation", "PinnedReferenceSet", descriptor.AdapterId.Value, "grid.exact-utf8", 1);
        source = new(CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, native), KnowledgeSourceKind.FrozenRepositoryDataset, native);
        var version = SourceNativeVersion.FromExactUtf8("grid.gta-v.presentation", "1", "grid.exact-utf8", 1);
        var formats = origin.SourceRevisions.SelectMany(x => x.ArtifactFormats).Concat(this.artifacts.Select(x => x.FormatBinding))
            .Where(x => descriptor.SupportedFormats.Any(f => f.FormatId == x.Format.FormatId && f.ExactFormatVersion == x.Format.ExactFormatVersion))
            .DistinctBy(x => x.ArtifactId).OrderBy(x => x.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
        var ids = formats.Select(x => x.ArtifactId).ToImmutableArray();
        var id = CatalogSourceRevisionId.DeriveV2(source.Id, version, ids, descriptor.RevisionId);
        revision = new(new CatalogSourceRevisionRecord(id, source.Id, version, ids), descriptor.RevisionId, scope, formats);
    }

    public CrossSourceTargetLinkClaim Link(CanonicalKnowledgeRecord target, GtaVPresentationEvidence map)
    {
        var identity = origin.EvidenceBindings.Single(x => x.KnowledgeRecordId == target.Id && x.SourceRevisionId == target.SourceRevisionId && x.ClaimKind == EvidenceClaimKind.KnowledgeIdentity && x.ClaimContentId is null);
        var prior = origin.FileEvidenceReceipts.Single(x => x.Id == identity.EvidenceReceiptId).Receipt;
        var coordinate = SourceNativeIdentifier.FromExactUtf8("grid.gta-v.presentation.reference-row", "ReferenceRow", map.Artifact.SourceCoordinate.ExactRepresentation + "#" + map.FieldPath, "grid.exact-utf8", 1);
        var id = CrossSourceTargetLinkClaimId.DeriveV1(target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, target.NativeIdentity, prior.SourceArtifactId, prior.SourceFieldPath, revision.Revision.Id, coordinate, map.Artifact.Id, map.FieldPath, CrossSourceTargetLinkKind.VersionedExactMapping, Method, "1");
        var link = new CrossSourceTargetLinkClaim(id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, target.NativeIdentity, prior.SourceArtifactId, prior.SourceFieldPath, revision.Revision.Id, coordinate, map.Artifact.Id, map.FieldPath, CrossSourceTargetLinkKind.VersionedExactMapping, Method, "1");
        if (closure.ContainsKey(id)) return link;
        links.Add(link);
        var receipt = new FileEvidenceReceipt(revision.Revision.Id, prior.SourceArtifactId, prior.ArtifactDigest, prior.ParserId, prior.ParserVersion, prior.NativeRecordLocator, prior.SourceFieldPath, prior.ByteOffset, prior.ByteLength, prior.InterpretedBytesDigest, prior.ObservedAtUtc, target.NativeIdentity);
        var file = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(receipt), receipt); files.Add(file);
        var reference = Evidence(target, map, coordinate);
        var content = EvidenceClaimContentId.DeriveV1(link);
        closure.Add(id, (identity, file.Id, reference, Bind(file.Id, target, EvidenceClaimKind.CrossSourceTargetLink, prior.SourceFieldPath, content).Id, Bind(reference, target, EvidenceClaimKind.CrossSourceTargetLink, map.FieldPath, content).Id));
        return link;
    }

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

    private EvidenceReceiptId Evidence(CanonicalKnowledgeRecord target, GtaVPresentationEvidence evidence, SourceNativeIdentifier? nativeObject = null)
    {
        var artifact = evidence.Artifact;
        if (evidence.Reference)
        {
            var uri = new Uri(artifact.SourceCoordinate.ExactRepresentation);
            var exactRevision = uri.Host == "raw.githubusercontent.com" ? uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[2] : "19769";
            var referenceRevision = SourceNativeVersion.FromExactUtf8("grid.gta-v.presentation.reference-revision", exactRevision, "grid.exact-utf8", 1);
            var receipt = new ReferenceEvidenceReceipt(source.Id, revision.Revision.Id, artifact.Id, artifact.Digest, nativeObject ?? artifact.SourceCoordinate, referenceRevision, evidence.FieldPath, artifact.ObservedAtUtc);
            var record = new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt); references.Add(record); return record.Id;
        }
        var file = new FileEvidenceReceipt(revision.Revision.Id, artifact.Id, artifact.Digest, Method, "1", target.NativeIdentity.ExactRepresentation, evidence.FieldPath, evidence.Offset, evidence.Length, null, artifact.ObservedAtUtc, nativeObject ?? target.NativeIdentity);
        var value = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(file), file); files.Add(value); return value.Id;
    }
    private EvidenceBinding Bind(EvidenceReceiptId receipt, CanonicalKnowledgeRecord target, EvidenceClaimKind kind, string field, EvidenceClaimContentId content)
    {
        var value = new EvidenceBinding(EvidenceBindingId.DeriveV2(receipt, kind, target.Id, revision.Revision.Id, field, content), receipt, kind, target.Id, revision.Revision.Id, field, content); bindings.Add(value); return value;
    }
    private void Envelope(CanonicalKnowledgeRecord target, CrossSourceTargetLinkClaim link, EvidenceClaimKind claim, CrossSourceCanonicalAssertionKind kind, EvidenceClaimContentId content, IEnumerable<GtaVPresentationEvidence> evidence)
    {
        var anchor = closure[link.Id];
        var receiptIds = new List<EvidenceReceiptId> { anchor.Origin.EvidenceReceiptId, anchor.Target, anchor.Reference };
        var bindingIds = new List<EvidenceBindingId> { anchor.Origin.Id, anchor.TargetBinding, anchor.ReferenceBinding };
        foreach (var item in evidence) { var receipt = Evidence(target, item); receiptIds.Add(receipt); bindingIds.Add(Bind(receipt, target, claim, item.FieldPath, content).Id); }
        var receipts = receiptIds.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var binds = bindingIds.Distinct().OrderBy(x => x.Value, StringComparer.Ordinal).ToImmutableArray();
        var id = CrossSourceCanonicalAssertionId.DeriveV1(target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revision.Revision.Id, kind, content, link.Id, link.LinkKind, link.ExactAssertingCoordinate, Method, "1", receipts, binds, [], []);
        envelopes.Add(new(id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revision.Revision.Id, kind, content, link.Id, link.LinkKind, link.ExactAssertingCoordinate, Method, "1", receipts, binds, [], []));
    }
    public GtaVSecondaryAssertionBatch Build() => new(descriptor, revision, artifacts.Select(x => new SourceArtifactRecord(x.Id, x.Digest)).ToImmutableArray(), titles.Distinct().ToImmutableArray(), classes.DistinctBy(x => x.Id).ToImmutableArray(), organizations.DistinctBy(x => x.Id).ToImmutableArray(), files.DistinctBy(x => x.Id).ToImmutableArray(), bindings.DistinctBy(x => x.Id).ToImmutableArray(), links.ToImmutableArray(), envelopes.DistinctBy(x => x.Id).ToImmutableArray())
    { AdditionalSources = [source], ReferenceEvidenceReceipts = references.DistinctBy(x => x.Id).ToImmutableArray(), RelationshipAssertions = relationships.Distinct().ToImmutableArray() };
}
