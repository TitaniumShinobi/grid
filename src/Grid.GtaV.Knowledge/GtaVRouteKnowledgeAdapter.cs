using System.Buffers.Binary;
using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Registers named route-label keys, not inferred physical roads or geographic parents.</summary>
public sealed class GtaVRouteKnowledgeAdapter : IGameKnowledgeAdapter
{
    public const string AdapterId = "grid.gta-v.enhanced.update-paths-named-routes";
    public const string ParserId = "grid.gta-v.update-paths-route-index";
    public const string FamilyId = "rockstar.gta-v.enhanced.update-paths-named-routes";
    private const string ClassificationMethod = "grid.gta-v.ynd-street-text-key.exact-gxt2";
    private readonly GtaVRouteCorpusIndex _index;

    public GtaVRouteKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVRouteCorpusIndex index)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        Descriptor = new GameKnowledgeAdapterDescriptor(new KnowledgeAdapterId(AdapterId), "1", adapterArtifactDigest,
            1, "ynd-route-label-key-base-american-gxt2-v1", [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [Supported(GtaVRouteCorpusIndex.YndFormatId, "YndStreetNameHash", false),
             Supported(GtaVRouteCorpusIndex.GxtFormatId, "GXT2", true),
             Supported(GtaVRouteCorpusIndex.RpfFormatId, "Rpf7Container", false)],
            new KnowledgeAdapterResourceLimits(64L * 1024 * 1024, 1024, 100_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public GtaVRouteMetrics? LastMetrics { get; private set; }

    public Task<SourceDiscoveryResult> DiscoverAsync(PreproductionSourceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            request.ExactGameVersion?.ExactRepresentation != "25261616" || request.ExactModIdentity is not null)
            return Task.FromResult(new SourceDiscoveryResult([], request.Artifacts.Select(a =>
                new UnsupportedKnowledgeArtifact(a.Id, "unsupported-game-id")).ToImmutableArray(),
                KnowledgeCoverageState.Unsupported, []));
        _index.RequireExactArtifacts(request.Artifacts);
        return Task.FromResult(new SourceDiscoveryResult([new DiscoveredKnowledgeSource(CreateSource(), null,
            request.Artifacts.Select(a => a.FormatBinding).ToImmutableArray())], [], KnowledgeCoverageState.Partial, []));
    }

    public Task<KnowledgeExtractionResult> ExtractAsync(PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            request.ExactGameVersion?.ExactRepresentation != "25261616" || request.ExactModIdentity is not null ||
            request.ExactModVersion is not null || request.AdapterRevisionId != Descriptor.RevisionId)
            throw new InvalidDataException("Named routes require the exact Enhanced base-game adapter scope.");
        _index.RequireExactArtifacts(request.Artifacts);
        var source = CreateSource();
        var artifactIds = request.Artifacts.Select(a => a.Id).OrderBy(a => a.Value, StringComparer.Ordinal).ToImmutableArray();
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, artifactIds, Descriptor.RevisionId);
        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>();
        var terminology = ImmutableArray.CreateBuilder<TerminologyAssertion>();
        var nativeTypes = ImmutableArray.CreateBuilder<SourceNativeLocationTypeAssertion>();
        var roles = ImmutableArray.CreateBuilder<LocationSemanticClassificationAssertion>();
        var eligible = ImmutableArray.CreateBuilder<CanonicalSemanticClassificationAssertion>();
        var receipts = new Dictionary<EvidenceReceiptId, CatalogFileEvidenceReceipt>();
        var bindings = new Dictionary<EvidenceBindingId, EvidenceBinding>();
        // Every occurrence remains in the immutable corpus index. One exact source node is the
        // identity witness; repeated occurrences do not create redundant canonical route records.
        var witnesses = _index.Ynds.SelectMany(s => s.Nodes.Select(n => (Source: s, Node: n)))
            .Where(x => x.Node.StreetNameHash != 0).GroupBy(x => x.Node.StreetNameHash)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var hash in _index.NamedHashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var witness = witnesses[hash];
            var native = HashIdentity(hash, "rockstar.gta-v.enhanced.route-label", "YndStreetNameHash");
            var nativeId = NativeRecordIdentityId.DeriveV1(request.GameId, native);
            var id = KnowledgeRecordId.DeriveV1(request.GameId, request.ExactGameVersion, null,
                revisionId, KnowledgeKind.Location, nativeId);
            var record = new CanonicalKnowledgeRecord(id, request.GameId, request.ExactGameVersion, null,
                revisionId, KnowledgeKind.Location, nativeId, native);
            records.Add(record);
            var locator = witness.Node.FieldLocator;
            Evidence(record, witness.Source.Artifact, revisionId, EvidenceClaimKind.KnowledgeIdentity, locator,
                null, receipts, bindings);
            var nativeType = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.route.location-type",
                "location-record-type", "YndStreetNameHash", "grid.gta-v.location-native-type.exact-utf8", 1);
            var typeId = SourceNativeLocationTypeAssertionId.DeriveV1(id, revisionId, nativeType, locator);
            var type = new SourceNativeLocationTypeAssertion(typeId, id, revisionId, nativeType, locator);
            nativeTypes.Add(type);
            Evidence(record, witness.Source.Artifact, revisionId, EvidenceClaimKind.LocationNativeType,
                locator, EvidenceClaimContentId.DeriveV1(type), receipts, bindings);
            var roleId = LocationSemanticClassificationAssertionId.DeriveV1(id, revisionId, typeId,
                LocationSemanticRoles.Route, LocationSemanticRoles.CurrentVocabularyVersion, ClassificationMethod, "1", locator);
            var role = new LocationSemanticClassificationAssertion(roleId, id, revisionId, typeId,
                LocationSemanticRoles.Route, LocationSemanticRoles.CurrentVocabularyVersion, ClassificationMethod, "1", locator);
            roles.Add(role);
            Evidence(record, witness.Source.Artifact, revisionId, EvidenceClaimKind.LocationSemanticClassification,
                locator, EvidenceClaimContentId.DeriveV1(role), receipts, bindings);
            var text = _index.Terminology[hash];
            var term = new TerminologyAssertion(id, revisionId, TerminologyAssertionRole.PrimaryName, text.Text,
                text.FieldLocator, GtaVRouteCorpusIndex.Locale,
                HashIdentity(hash, "rockstar.gta-v.gxt2", "Gxt2LabelHash"));
            terminology.Add(term);
            Evidence(record, _index.Gxt, revisionId, EvidenceClaimKind.Terminology, text.FieldLocator,
                EvidenceClaimContentId.DeriveV1(term), receipts, bindings, text);
            const string vocabulary = "grid.selector.player-addressable";
            var eligibilityId = CanonicalSemanticClassificationAssertionId.DeriveV1(id, revisionId,
                CanonicalProjectionSemantics.SelectorPlayerAddressable, vocabulary, "1", ClassificationMethod, "1", locator);
            var eligibility = new CanonicalSemanticClassificationAssertion(eligibilityId, id, revisionId,
                CanonicalProjectionSemantics.SelectorPlayerAddressable, vocabulary, "1", ClassificationMethod, "1", locator);
            eligible.Add(eligibility);
            Evidence(record, witness.Source.Artifact, revisionId, EvidenceClaimKind.SemanticClassification,
                locator, EvidenceClaimContentId.DeriveV1(eligibility), receipts, bindings);
        }
        var registration = new CanonicalCatalogRegistration(source,
            request.Artifacts.Select(a => new SourceArtifactRecord(a.Id, a.Digest)).OrderBy(a => a.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            new CatalogSourceRevisionRecord(revisionId, source.Id, null, artifactIds),
            records.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            terminology.OrderBy(t => t.KnowledgeRecordId.Value, StringComparer.Ordinal).ToImmutableArray(), [],
            receipts.Values.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToImmutableArray(), [],
            bindings.Values.OrderBy(b => b.Id.Value, StringComparer.Ordinal).ToImmutableArray())
        {
            SourceNativeLocationTypeAssertions = nativeTypes.OrderBy(t => t.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            LocationSemanticClassificationAssertions = roles.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
        };
        var bound = new AdapterBoundCanonicalCatalogRegistration(registration, Descriptor, request.SourceScope,
            request.Artifacts.Select(a => a.FormatBinding).ToImmutableArray())
        { SemanticClassificationAssertions = eligible.OrderBy(e => e.Id.Value, StringComparer.Ordinal).ToImmutableArray() };
        LastMetrics = _index.Metrics;
        return Task.FromResult(new KnowledgeExtractionResult([bound], [], [], KnowledgeCoverageState.Partial, [])
        { LocationCoverageReports = [Coverage(bound, request.SourceScope)] });
    }

    private LocationCoverageReport Coverage(AdapterBoundCanonicalCatalogRegistration bound, KnowledgeSourceScope scope)
    {
        var registration = bound.Registration;
        var ids = registration.KnowledgeRecords.Select(r => r.Id).ToImmutableArray();
        var artifacts = _index.Artifacts.Select(a => a.Id).ToImmutableArray();
        var revisions = ImmutableArray.Create(registration.SourceRevision.Id);
        var familyId = new LocationSourceFamilyId(FamilyId);
        var declaration = new LocationSourceFamilyDeclaration(familyId,
            new KnowledgeFormatCoordinate(GtaVRouteCorpusIndex.YndFormatId, "1"), Descriptor.RevisionId, true,
            artifacts, revisions, [LocationSemanticRoles.Route]);
        var validation = new CatalogValidationSummary(CatalogValidationStatus.Candidate,
            "grid.location.coverage.qcs", "1", ContentDigest.ComputeSha256("grid.location.coverage.qcs.pending"u8));
        var manifestId = LocationCoverageManifestId.DeriveV1(scope, "grid.gta-v.route-coverage.v1", false, validation, [declaration]);
        var manifest = new LocationCoverageManifest(manifestId, scope, "grid.gta-v.route-coverage.v1", false, validation, [declaration]);
        var family = new LocationSourceFamilyCoverage(familyId, artifacts, revisions,
            _index.Metrics.DistinctNonzeroHashes, _index.Metrics.DistinctNonzeroHashes, _index.Metrics.DistinctNonzeroHashes, ids,
            [], [], 0, 0, 0, 0);
        var nativeType = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.route.location-type", "location-record-type",
            "YndStreetNameHash", "grid.gta-v.location-native-type.exact-utf8", 1);
        return LocationCoverageReport.Create(manifest, [family],
            [new LocationSemanticCategoryCoverage(nativeType, LocationSemanticRoles.Route, ids, 0)],
            new LocationTerminologyCoverage(ids.Length, ids.Length, 0, 0, 0),
            new LocationHierarchyCoverage(0, 0, 0, 0, true), [], []);
    }

    private static void Evidence(CanonicalKnowledgeRecord record, FrozenSourceArtifact artifact,
        CatalogSourceRevisionId revision, EvidenceClaimKind kind, string locator, EvidenceClaimContentId? claim,
        Dictionary<EvidenceReceiptId, CatalogFileEvidenceReceipt> receipts,
        Dictionary<EvidenceBindingId, EvidenceBinding> bindings, GtaVIndexedGxt2Entry? text = null)
    {
        var receipt = new FileEvidenceReceipt(revision, artifact.Id, artifact.Digest, ParserId, "1", locator, locator,
            text?.TextOffset, text?.TextLength,
            text is null ? null : ContentDigest.ComputeSha256(artifact.ExactBytes.AsSpan(text.TextOffset, text.TextLength)),
            artifact.ObservedAtUtc, record.NativeIdentity);
        var receiptId = EvidenceReceiptId.DeriveV2(receipt);
        receipts.TryAdd(receiptId, new CatalogFileEvidenceReceipt(receiptId, receipt));
        var bindingId = EvidenceBindingId.DeriveV2(receiptId, kind, record.Id, revision, locator, claim);
        bindings.TryAdd(bindingId, new EvidenceBinding(bindingId, receiptId, kind, record.Id, revision, locator, claim));
    }

    private static SourceNativeIdentifier HashIdentity(uint hash, string ns, string type)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, hash);
        return new SourceNativeIdentifier(ns, type, $"0x{hash:X8}", bytes.ToImmutableArray(), "rockstar.gta-v.joaat32-little-endian", 1);
    }
    private static SupportedKnowledgeFormat Supported(string format, string type, bool terminology) =>
        new(format, "1", ["rpf7-member"], [type], [KnowledgeKind.Location], terminology, false, false);
    private static CatalogSourceRecord CreateSource()
    {
        var identity = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.source-family", "UpdatePathsNamedRoutes",
            FamilyId, "grid.gta-v.source-family.exact-utf8", 1);
        return new CatalogSourceRecord(CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, identity),
            KnowledgeSourceKind.LocalGameDistribution, identity);
    }
}
