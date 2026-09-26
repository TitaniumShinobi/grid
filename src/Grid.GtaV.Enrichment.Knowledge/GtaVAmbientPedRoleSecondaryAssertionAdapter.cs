using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

namespace Grid.GtaV.Enrichment.Knowledge;

/// <summary>
/// Adds NPC role assertions to established Actors only when their exact PedModelName is present
/// in an authoritative CAmbientModelSet Models/Item/Name membership.
/// </summary>
public sealed class GtaVAmbientPedRoleSecondaryAssertionAdapter
{
    public const string Coordinate = "update/update.rpf!/common/data/ai/ambientpedmodelsets.meta";
    public const string FormatId = "rockstar.gta-v.ambient-ped-model-sets-xml";
    public const string ExactFormatVersion = "1";
    public const string ParserId = "grid.gta-v-enhanced.ambient-ped-model-sets-secondary";
    public const string ParserVersion = "1";
    public const string MappingMethodId = "grid.gta-v.ambient-ped-model-membership.actor-npc";
    public const string MappingMethodVersion = "1";
    public const long MaximumArtifactBytes = 32L * 1024 * 1024;
    private const string ActorVocabularyId = "grid.actor-role";
    private const string ActorVocabularyVersion = "1";

    public GtaVAmbientPedRoleSecondaryAssertionAdapter(ContentDigest adapterArtifactDigest)
    {
        AmbientFormat = new KnowledgeFormatCoordinate(FormatId, ExactFormatVersion);
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.ambient-ped-npc-secondary"),
            "1", adapterArtifactDigest, 1, "ambient-ped-membership-npc-role-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [
                new SupportedKnowledgeFormat(
                    GtaVGen9PedsKnowledgeAdapter.FormatId,
                    GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion,
                    ["rpf7-member"], ["Gen9ExclusiveAssetsDataPeds"], [KnowledgeKind.Actor],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
                new SupportedKnowledgeFormat(
                    FormatId, ExactFormatVersion,
                    ["rpf7-member"], ["CAmbientModelSets"], [KnowledgeKind.Actor],
                    supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false),
            ],
            new KnowledgeAdapterResourceLimits(MaximumArtifactBytes, 2, 100_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public KnowledgeFormatCoordinate AmbientFormat { get; }

    public SourceNativeIdentifier CreateSourceCoordinate() =>
        GtaVEnrichmentParsing.ResourceCoordinate("CAmbientModelSets", Coordinate);

    public GtaVSecondaryAssertionBatch Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact actorOriginArtifact,
        FrozenSourceArtifact ambientModelSetsArtifact)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(actorOriginArtifact);
        ArgumentNullException.ThrowIfNull(ambientModelSetsArtifact);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame)
            throw new InvalidDataException("Ambient-ped classification requires the exact Enhanced base-game scope.");
        GtaVEnrichmentParsing.ValidateArtifact(
            actorOriginArtifact,
            GtaVEnrichmentParsing.ResourceCoordinate(
                "Gen9ExclusiveAssetsDataPeds",
                "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta"),
            new KnowledgeFormatCoordinate(
                GtaVGen9PedsKnowledgeAdapter.FormatId,
                GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion));
        GtaVEnrichmentParsing.ValidateArtifact(
            ambientModelSetsArtifact, CreateSourceCoordinate(), AmbientFormat);

        var records = origin.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId && value.Kind == KnowledgeKind.Actor)
            .ToDictionary(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal);
        if (records.Count == 0) throw new InvalidDataException("No established Actor records are available.");
        var originRevisions = records.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (originRevisions.Length != 1 || !origin.SourceRevisions.Any(value =>
                value.Revision.Id == originRevisions[0] &&
                value.Revision.ArtifactIds.SequenceEqual([actorOriginArtifact.Id])))
            throw new InvalidDataException("Established Actors do not share the exact acquired peds origin.");
        var originRevision = origin.SourceRevisions.Single(value => value.Revision.Id == originRevisions[0]);
        var source = origin.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var artifacts = ImmutableArray.Create(actorOriginArtifact.Id, ambientModelSetsArtifact.Id);
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, artifacts, Descriptor.RevisionId);
        var revision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, artifacts),
            Descriptor.RevisionId, sourceScope,
            [actorOriginArtifact.FormatBinding, ambientModelSetsArtifact.FormatBinding]);

        var memberships = Parse(ambientModelSetsArtifact)
            .Where(value => records.ContainsKey(value.ActorNativeIdentity))
            .OrderBy(value => value.ActorNativeIdentity, StringComparer.Ordinal)
            .ThenBy(value => value.ActorFieldPath, StringComparer.Ordinal)
            .ToImmutableArray();
        // The source contains unrelated repeated memberships. Only records targeted by this
        // secondary assertion are cardinality-critical: each established Actor must be proven
        // by exactly one direct membership, while duplicates outside that target set are inert.
        if (memberships.Select(value => value.ActorNativeIdentity).Distinct(StringComparer.Ordinal).Count() != records.Count ||
            memberships.Length != records.Count)
            throw new InvalidDataException("Every established Actor must have exactly one direct ambient-model membership.");

        var classifications = ImmutableArray.CreateBuilder<CanonicalSemanticClassificationAssertion>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();
        foreach (var membership in memberships)
        {
            var target = records[membership.ActorNativeIdentity];
            var assertionId = CanonicalSemanticClassificationAssertionId.DeriveV1(
                target.Id, revisionId, CanonicalProjectionSemantics.ActorNpc,
                ActorVocabularyId, ActorVocabularyVersion,
                MappingMethodId, MappingMethodVersion, membership.ActorFieldPath);
            var assertion = new CanonicalSemanticClassificationAssertion(
                assertionId, target.Id, revisionId, CanonicalProjectionSemantics.ActorNpc,
                ActorVocabularyId, ActorVocabularyVersion,
                MappingMethodId, MappingMethodVersion, membership.ActorFieldPath);
            classifications.Add(assertion);
            var claimContent = EvidenceClaimContentId.DeriveV1(assertion);
            var assertingIdentity = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.ambient-ped-model-sets",
                "CAmbientModelSet.Models.Item.Name",
                membership.ActorNativeIdentity,
                "grid.gta-v.ambient-ped-model-name.exact-utf8", 1);
            var claimReceipt = AddReceipt(
                revisionId, ambientModelSetsArtifact, assertingIdentity,
                membership.ActorRecordLocator, membership.ActorFieldPath, receipts);
            var claimBinding = AddBinding(
                claimReceipt.Id, EvidenceClaimKind.SemanticClassification,
                target.Id, revisionId, membership.ActorFieldPath, claimContent, bindings);

            var originBinding = origin.EvidenceBindings
                .Where(value => value.KnowledgeRecordId == target.Id &&
                                value.SourceRevisionId == target.SourceRevisionId &&
                                value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                                value.ClaimContentId is null)
                .OrderBy(value => value.ClaimLocator, StringComparer.Ordinal)
                .First();
            var originReceipt = origin.FileEvidenceReceipts.Single(value => value.Id == originBinding.EvidenceReceiptId);
            var targetFieldPath = originBinding.ClaimLocator;
            var targetReceipt = AddReceipt(
                revisionId, actorOriginArtifact, target.NativeIdentity,
                originReceipt.Receipt.NativeRecordLocator, targetFieldPath, receipts);

            var linkId = CrossSourceTargetLinkClaimId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                target.NativeIdentity, actorOriginArtifact.Id, targetFieldPath,
                revisionId, assertingIdentity, ambientModelSetsArtifact.Id, membership.ActorFieldPath,
                CrossSourceTargetLinkKind.VersionedExactMapping, MappingMethodId, MappingMethodVersion);
            var link = new CrossSourceTargetLinkClaim(
                linkId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                target.NativeIdentity, actorOriginArtifact.Id, targetFieldPath,
                revisionId, assertingIdentity, ambientModelSetsArtifact.Id, membership.ActorFieldPath,
                CrossSourceTargetLinkKind.VersionedExactMapping, MappingMethodId, MappingMethodVersion);
            links.Add(link);
            var linkContent = EvidenceClaimContentId.DeriveV1(link);
            var targetLinkBinding = AddBinding(
                targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                target.Id, revisionId, targetFieldPath, linkContent, bindings);
            var assertingLinkBinding = AddBinding(
                claimReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                target.Id, revisionId, membership.ActorFieldPath, linkContent, bindings);

            var receiptIds = ImmutableArray.Create(
                originBinding.EvidenceReceiptId, targetReceipt.Id, claimReceipt.Id);
            var bindingIds = ImmutableArray.Create(
                originBinding.Id, claimBinding.Id, targetLinkBinding.Id, assertingLinkBinding.Id);
            var envelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.SemanticClassification,
                claimContent, link.Id, link.LinkKind, assertingIdentity,
                MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []);
            envelopes.Add(new CrossSourceCanonicalAssertion(
                envelopeId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.SemanticClassification,
                claimContent, link.Id, link.LinkKind, assertingIdentity,
                MappingMethodId, MappingMethodVersion, receiptIds, bindingIds, [], []));
        }

        return new GtaVSecondaryAssertionBatch(
            Descriptor, revision,
            [new SourceArtifactRecord(ambientModelSetsArtifact.Id, ambientModelSetsArtifact.Digest)],
            [], classifications.ToImmutable(), [],
            receipts.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.ToImmutable(), envelopes.ToImmutable());
    }

    private static ImmutableArray<AmbientMembership> Parse(FrozenSourceArtifact artifact)
    {
        XDocument document;
        try
        {
            var text = GtaVEnrichmentParsing.DecodeStrictUtf8(
                artifact.ExactBytes.AsSpan(), MaximumArtifactBytes);
            using var textReader = new StringReader(text);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException("The ambient-ped model-set source is malformed.", exception);
        }
        var root = document.Root;
        if (root?.Name != XName.Get("CAmbientModelSets") ||
            root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Only exact non-namespaced CAmbientModelSets XML is supported.");
        var modelSets = root.Elements("ModelSets").ToArray();
        if (modelSets.Length != 1)
            throw new InvalidDataException("Exactly one direct ModelSets collection is required.");

        var result = ImmutableArray.CreateBuilder<AmbientMembership>();
        foreach (var set in modelSets[0].Elements("Item"))
        {
            var setNames = set.Elements("Name").ToArray();
            var models = set.Elements("Models").ToArray();
            if (setNames.Length != 1 || models.Length != 1)
                throw new InvalidDataException("Every CAmbientModelSet requires one Name and one Models collection.");
            GtaVEnrichmentParsing.ValidateExactText(setNames[0].Value, "ambient model-set Name");
            foreach (var item in models[0].Elements("Item"))
            {
                var names = item.Elements("Name").ToArray();
                if (names.Length != 1)
                    throw new InvalidDataException("Every ambient model entry requires one direct Name.");
                GtaVEnrichmentParsing.ValidateExactText(names[0].Value, "ambient model Name");
                result.Add(new AmbientMembership(
                    setNames[0].Value,
                    names[0].Value,
                    Locator(artifact.SourceCoordinate.ExactRepresentation, Path(item)),
                    Locator(artifact.SourceCoordinate.ExactRepresentation, Path(names[0]))));
            }
        }
        if (result.Count == 0)
            throw new InvalidDataException("The ambient model-set source contains no model memberships.");
        return result.ToImmutable();
    }

    private static CatalogFileEvidenceReceipt AddReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string recordLocator,
        string fieldPath,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest, ParserId, ParserVersion,
            recordLocator, fieldPath, null, null, null, artifact.ObservedAtUtc, nativeObject);
        var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
        receipts.Add(receipt);
        return receipt;
    }

    private static EvidenceBinding AddBinding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string locator,
        EvidenceClaimContentId content,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
        var binding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, locator, content),
            receiptId, kind, recordId, revisionId, locator, content);
        bindings.Add(binding);
        return binding;
    }

    private static string Path(XElement element) => string.Concat(
        element.AncestorsAndSelf().Reverse().Select(value =>
            $"/{value.Name.LocalName}[{(value.Parent is null ? 1 : value.ElementsBeforeSelf(value.Name).Count() + 1)}]"));

    private static string Locator(string coordinate, string path) =>
        $"rpf7-member:{coordinate}#{path}";

    private sealed record AmbientMembership(
        string SetName,
        string ActorNativeIdentity,
        string ActorRecordLocator,
        string ActorFieldPath);
}
