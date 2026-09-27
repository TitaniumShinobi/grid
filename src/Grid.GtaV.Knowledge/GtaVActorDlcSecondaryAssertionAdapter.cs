using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Attaches exact DLCData/dlcName membership facts to established Enhanced Actor records.</summary>
public sealed class GtaVActorDlcSecondaryAssertionAdapter
{
    public const string ParserId = "grid.gta-v-enhanced.gen9-ped-dlc-secondary";
    public const string ParserVersion = "1";
    public const string MethodId = "grid.gta-v.gen9-ped-dlc-membership";
    public const string MethodVersion = "1";
    private const string Coordinate = "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta";
    private readonly GtaVSupportedSourceCorpusIndex? _corpusIndex;

    public GtaVActorDlcSecondaryAssertionAdapter(ContentDigest adapterArtifactDigest, GtaVSupportedSourceCorpusIndex? corpusIndex = null)
    {
        _corpusIndex = corpusIndex;
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.gen9-ped-dlc-secondary"),
            "1", adapterArtifactDigest, 1, "ped-dlc-organizational-values-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [new SupportedKnowledgeFormat(
                GtaVGen9PedsKnowledgeAdapter.FormatId,
                GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion,
                ["rpf7-member"], ["Gen9ExclusiveAssetsDataPeds"], [KnowledgeKind.Actor],
                supportsTerminology: false, supportsRelationships: false, supportsHierarchy: false)],
            new KnowledgeAdapterResourceLimits(
                GtaVGen9PedsKnowledgeAdapter.MaximumArtifactBytes, 1, 10_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }

    public GtaVSecondaryAssertionBatch Extract(
        CanonicalCatalogPayload origin,
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(artifact);
        if (sourceScope.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            sourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame ||
            !string.Equals(artifact.SourceCoordinate.ExactRepresentation, Coordinate, StringComparison.Ordinal) ||
            artifact.DeclaredFormat != new KnowledgeFormatCoordinate(
                GtaVGen9PedsKnowledgeAdapter.FormatId, GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion) ||
            SourceArtifactId.DeriveV1(artifact.Digest) != artifact.Id ||
            ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan()) != artifact.Digest)
            throw new InvalidDataException("Actor DLC facts require the exact acquired Enhanced peds artifact.");

        var records = origin.KnowledgeRecords
            .Where(value => value.GameId == sourceScope.GameId &&
                value.Kind == KnowledgeKind.Actor &&
                string.Equals(value.NativeIdentity.Namespace,
                    GtaVGen9PedsKnowledgeAdapter.NativeIdentityNamespace, StringComparison.Ordinal) &&
                string.Equals(value.NativeIdentity.ObjectType, "PedModelName", StringComparison.Ordinal))
            .ToDictionary(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal);
        if (records.Count == 0) throw new InvalidDataException("No established Gen9 Actor records are available.");
        var revisions = records.Values.Select(value => value.SourceRevisionId).Distinct().ToImmutableArray();
        if (revisions.Length != 1 || !origin.SourceRevisions.Any(value =>
                value.Revision.Id == revisions[0] && value.Revision.ArtifactIds.Contains(artifact.Id)))
            throw new InvalidDataException("The established Actor records do not share the exact acquired origin.");
        var originRevision = origin.SourceRevisions.Single(value => value.Revision.Id == revisions[0]);
        var source = origin.Sources.Single(value => value.Id == originRevision.Revision.SourceId);
        var revisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id, null, [artifact.Id], Descriptor.RevisionId);
        var revision = new AdapterBoundCatalogSourceRevisionRecord(
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, [artifact.Id]),
            Descriptor.RevisionId, sourceScope, [artifact.FormatBinding]);

        var values = ImmutableArray.CreateBuilder<CanonicalOrganizationalValueAssertion>();
        var evidence = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var links = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        var envelopes = ImmutableArray.CreateBuilder<CrossSourceCanonicalAssertion>();
        foreach (var fact in Parse(artifact, _corpusIndex?.GetXmlDocument(artifact, GtaVGen9PedsKnowledgeAdapter.MaximumArtifactBytes)))
        {
            if (!records.TryGetValue(fact.ActorNativeIdentity, out var target))
                throw new InvalidDataException("A DLC membership cannot target an absent established Actor.");
            var exactValue = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.gen9-exclusive-peds.dlc",
                "dlcName", fact.DlcName,
                "grid.gta-v.dlc-name.exact-utf8", 1);
            var assertionId = CanonicalOrganizationalValueAssertionId.DeriveV1(
                target.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
                exactValue, fact.DlcName, MethodId, MethodVersion, fact.DlcFieldPath);
            var assertion = new CanonicalOrganizationalValueAssertion(
                assertionId, target.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
                exactValue, fact.DlcName, MethodId, MethodVersion, fact.DlcFieldPath);
            values.Add(assertion);

            var claimContent = EvidenceClaimContentId.DeriveV1(assertion);
            var claimReceipt = Receipt(revisionId, artifact, target.NativeIdentity, fact.DlcFieldPath);
            evidence.Add(claimReceipt);
            var claimBinding = Binding(
                claimReceipt.Id, EvidenceClaimKind.OrganizationalValue, target.Id,
                revisionId, fact.DlcFieldPath, claimContent);
            bindings.Add(claimBinding);

            var linkReceipt = Receipt(revisionId, artifact, target.NativeIdentity, fact.ActorFieldPath);
            evidence.Add(linkReceipt);
            var linkId = CrossSourceTargetLinkClaimId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                target.NativeIdentity, artifact.Id, fact.ActorFieldPath,
                revisionId, target.NativeIdentity, artifact.Id, fact.ActorFieldPath,
                CrossSourceTargetLinkKind.ExactSourceNativeIdentity, MethodId, MethodVersion);
            var link = new CrossSourceTargetLinkClaim(
                linkId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                target.NativeIdentity, artifact.Id, fact.ActorFieldPath,
                revisionId, target.NativeIdentity, artifact.Id, fact.ActorFieldPath,
                CrossSourceTargetLinkKind.ExactSourceNativeIdentity, MethodId, MethodVersion);
            links.Add(link);
            var linkContent = EvidenceClaimContentId.DeriveV1(link);
            var linkBinding = Binding(
                linkReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink, target.Id,
                revisionId, fact.ActorFieldPath, linkContent);
            bindings.Add(linkBinding);

            var originBinding = origin.EvidenceBindings.Single(value =>
                value.KnowledgeRecordId == target.Id &&
                value.SourceRevisionId == target.SourceRevisionId &&
                value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                value.ClaimContentId is null);
            var receiptIds = ImmutableArray.Create(
                originBinding.EvidenceReceiptId, claimReceipt.Id, linkReceipt.Id);
            var bindingIds = ImmutableArray.Create(originBinding.Id, claimBinding.Id, linkBinding.Id);
            var envelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.OrganizationalValue,
                claimContent, link.Id, link.LinkKind, target.NativeIdentity,
                MethodId, MethodVersion, receiptIds, bindingIds, [], []);
            envelopes.Add(new CrossSourceCanonicalAssertion(
                envelopeId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                revisionId, CrossSourceCanonicalAssertionKind.OrganizationalValue,
                claimContent, link.Id, link.LinkKind, target.NativeIdentity,
                MethodId, MethodVersion, receiptIds, bindingIds, [], []));
        }

        return new GtaVSecondaryAssertionBatch(
            Descriptor, revision, [], [], [], values.ToImmutable(),
            evidence.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            links.ToImmutable(), envelopes.ToImmutable());
    }

    private static ImmutableArray<ActorDlcFact> Parse(FrozenSourceArtifact artifact, XDocument? indexedDocument = null)
    {
        XDocument document;
        if (indexedDocument is not null)
        {
            document = indexedDocument;
        }
        else
        try
        {
            using var stream = new MemoryStream(artifact.ExactBytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = GtaVGen9PedsKnowledgeAdapter.MaximumArtifactBytes,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException("The exact peds metadata is malformed.", exception);
        }
        var root = document.Root;
        if (root?.Name != XName.Get("Gen9ExclusiveAssetsDataPeds") ||
            root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Only exact non-namespaced Gen9 peds metadata is supported.");
        var pedData = root.Elements("PedData").ToArray();
        if (pedData.Length != 1) throw new InvalidDataException("Exactly one PedData collection is required.");
        var result = ImmutableArray.CreateBuilder<ActorDlcFact>();
        foreach (var actor in pedData[0].Elements("Item"))
        {
            var names = actor.Elements("PedModelName").ToArray();
            var dlcData = actor.Elements("DLCData").ToArray();
            if (names.Length != 1 || dlcData.Length != 1)
                throw new InvalidDataException("Every supported Actor requires one PedModelName and one DLCData collection.");
            foreach (var membership in dlcData[0].Elements("Item"))
            {
                var namesInMembership = membership.Elements("dlcName").ToArray();
                if (namesInMembership.Length != 1)
                    throw new InvalidDataException("Every DLCData entry requires one dlcName.");
                result.Add(new ActorDlcFact(
                    names[0].Value, namesInMembership[0].Value,
                    Locator(artifact.SourceCoordinate.ExactRepresentation, Path(names[0])),
                    Locator(artifact.SourceCoordinate.ExactRepresentation, Path(namesInMembership[0]))));
            }
        }
        if (result.Select(value => (value.ActorNativeIdentity, value.DlcName)).Distinct().Count() != result.Count)
            throw new InvalidDataException("Duplicate Actor/DLC memberships are ambiguous.");
        return result.ToImmutable();
    }

    private static CatalogFileEvidenceReceipt Receipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        SourceNativeIdentifier nativeObject,
        string fieldPath)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            ParserId, ParserVersion, nativeObject.ExactRepresentation, fieldPath,
            null, null, null, artifact.ObservedAtUtc, nativeObject);
        return new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
    }

    private static EvidenceBinding Binding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind kind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string fieldPath,
        EvidenceClaimContentId claimContent) => new(
            EvidenceBindingId.DeriveV2(receiptId, kind, recordId, revisionId, fieldPath, claimContent),
            receiptId, kind, recordId, revisionId, fieldPath, claimContent);

    private static string Path(XElement element) => string.Concat(
        element.AncestorsAndSelf().Reverse().Select(value =>
            $"/{value.Name.LocalName}[{(value.Parent is null ? 1 : value.ElementsBeforeSelf(value.Name).Count() + 1)}]"));
    private static string Locator(string coordinate, string path) => $"rpf7-member:{coordinate}#{path}";

    private sealed record ActorDlcFact(
        string ActorNativeIdentity,
        string DlcName,
        string ActorFieldPath,
        string DlcFieldPath);
}
