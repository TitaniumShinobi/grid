using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Enrichment.Knowledge;

/// <summary>
/// Preproduction-only adapter for exact GTA V Enhanced population-zone IPL bytes.
/// NameLabel is the source-native place identity; bounding boxes and row IDs remain evidence only.
/// </summary>
public sealed class GtaVPopulationZonesKnowledgeAdapter : IGameKnowledgeAdapter
{
    public const string Coordinate = "update/update.rpf!/common/data/levels/gta5/popzone.ipl";
    public const string FormatId = "rockstar.gta-v.population-zones-ipl";
    public const string ExactFormatVersion = "1";
    public const string ParserId = "grid.gta-v-enhanced.population-zones-ipl";
    public const string ParserVersion = "1";
    public const string NativeLocationType = "zone";
    public const string AreaZoneSemanticRole = "grid.location.role.area-zone";
    public const string ClassificationMethodId = "grid.gta-v.population-zone.section-name-label";
    public const string ClassificationMethodVersion = "1";
    public const string CoverageSourceFamily = "rockstar.gta-v.enhanced.population-zones";
    public const long MaximumArtifactBytes = 4L * 1024 * 1024;

    private const string NativeNamespace = "rockstar.gta-v.enhanced.population-zones";
    private const string NativeComparison = "grid.gta-v.population-zone-name-label.exact-utf8";
    private static readonly LocationSemanticRoleId AreaZoneRole = new(AreaZoneSemanticRole);
    private static readonly LocationSemanticVocabularyVersion VocabularyVersion = new(1);
    private static readonly LocationSourceFamilyId SourceFamily = new(CoverageSourceFamily);
    private readonly GtaVEnrichmentSourceCorpusIndex? _corpusIndex;

    public GtaVPopulationZonesKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVEnrichmentSourceCorpusIndex? corpusIndex = null)
    {
        _corpusIndex = corpusIndex;
        Format = new KnowledgeFormatCoordinate(FormatId, ExactFormatVersion);
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.gta-v.enhanced.population-zones"),
            "1",
            adapterArtifactDigest,
            1,
            "population-zone-name-label-locations-v1",
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [new SupportedKnowledgeFormat(
                FormatId,
                ExactFormatVersion,
                ["rpf7-member"],
                ["PopulationZoneIplSection"],
                [KnowledgeKind.Location],
                supportsTerminology: false,
                supportsRelationships: false,
                supportsHierarchy: false)],
            new KnowledgeAdapterResourceLimits(MaximumArtifactBytes, 1, 10_000, 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public KnowledgeFormatCoordinate Format { get; }

    public SourceNativeIdentifier CreateSourceCoordinate() =>
        GtaVEnrichmentParsing.ResourceCoordinate("PopulationZoneIplSection", Coordinate);

    public Task<SourceDiscoveryResult> DiscoverAsync(
        PreproductionSourceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var unsupported = ImmutableArray.CreateBuilder<UnsupportedKnowledgeArtifact>();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId || request.Artifacts.Length != 1)
            return Task.FromResult(Unsupported(request.Artifacts, "gta.population-zones.scope-or-count.unsupported"));
        var artifact = request.Artifacts[0];
        try
        {
            RequireArtifact(artifact);
            _ = ParseGroups(artifact);
        }
        catch (InvalidDataException)
        {
            unsupported.Add(new UnsupportedKnowledgeArtifact(artifact.Id, "gta.population-zones.malformed-or-unsupported"));
            return Task.FromResult(new SourceDiscoveryResult(
                [], unsupported.ToImmutable(), KnowledgeCoverageState.Unsupported,
                [new KnowledgeBuildIssue("gta.population-zones.unsupported", "No exact supported population-zone source was discovered.")]));
        }
        var source = CreateSource(request.SourceScope, artifact.SourceCoordinate);
        return Task.FromResult(new SourceDiscoveryResult(
            [new DiscoveredKnowledgeSource(source, null, [artifact.FormatBinding])],
            [], KnowledgeCoverageState.Partial,
            [new KnowledgeBuildIssue("gta.location.coverage.partial", "Population-zone NameLabel locations are one bounded Location source family.")]));
    }

    public Task<KnowledgeExtractionResult> ExtractAsync(
        PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            request.AdapterRevisionId != Descriptor.RevisionId ||
            request.Artifacts.Length != 1)
            return Task.FromResult(UnsupportedExtraction("gta.population-zones.adapter-or-scope.unsupported"));

        var artifact = request.Artifacts[0];
        try
        {
            RequireArtifact(artifact);
            var registration = CreateRegistration(request.SourceScope, artifact);
            var report = CreateCoverageReport(request.SourceScope, registration);
            return Task.FromResult(new KnowledgeExtractionResult(
                [registration], [], [], KnowledgeCoverageState.Partial,
                [new KnowledgeBuildIssue("gta.location.coverage.partial", "Population-zone NameLabel locations provide partial Location coverage.")])
            {
                LocationCoverageReports = [report],
            });
        }
        catch (InvalidDataException exception)
        {
            return Task.FromResult(UnsupportedExtraction("gta.population-zones.malformed-or-unsupported", exception.Message));
        }
    }

    private AdapterBoundCanonicalCatalogRegistration CreateRegistration(
        KnowledgeSourceScope scope,
        FrozenSourceArtifact artifact)
    {
        var groups = ParseGroups(artifact);
        var source = CreateSource(scope, artifact.SourceCoordinate);
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, [artifact.Id], Descriptor.RevisionId);
        var revision = new CatalogSourceRevisionRecord(revisionId, source.Id, null, [artifact.Id]);
        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>();
        var nativeTypes = ImmutableArray.CreateBuilder<SourceNativeLocationTypeAssertion>();
        var classifications = ImmutableArray.CreateBuilder<LocationSemanticClassificationAssertion>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();

        foreach (var group in groups)
        {
            var nativeIdentity = SourceNativeIdentifier.FromExactUtf8(
                NativeNamespace, "NameLabel", group.NameLabel, NativeComparison, 1);
            var nativeRecordId = NativeRecordIdentityId.DeriveV1(scope.GameId, nativeIdentity);
            var recordId = KnowledgeRecordId.DeriveV1(
                scope.GameId, scope.ExactGameVersion, scope.ExactModVersion,
                revisionId, KnowledgeKind.Location, nativeRecordId);
            var record = new CanonicalKnowledgeRecord(
                recordId, scope.GameId, scope.ExactGameVersion, scope.ExactModVersion,
                revisionId, KnowledgeKind.Location, nativeRecordId, nativeIdentity);
            records.Add(record);

            foreach (var occurrence in group.Occurrences)
                AddEvidence(receipts, bindings, revisionId, artifact, record,
                    EvidenceClaimKind.KnowledgeIdentity, occurrence.RowLocator,
                    occurrence.LabelFieldLocator, null);

            var first = group.Occurrences[0];
            var nativeTypeLocator = GtaVEnrichmentParsing.Locator(
                artifact.SourceCoordinate.ExactRepresentation, "zone/header");
            var nativeTypeIdentity = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.population-zones.location-type",
                "location-record-type", NativeLocationType,
                "grid.gta-v.location-native-type.exact-utf8", 1);
            var nativeTypeId = SourceNativeLocationTypeAssertionId.DeriveV1(
                record.Id, revisionId, nativeTypeIdentity, nativeTypeLocator);
            var nativeType = new SourceNativeLocationTypeAssertion(
                nativeTypeId, record.Id, revisionId, nativeTypeIdentity, nativeTypeLocator);
            nativeTypes.Add(nativeType);
            AddEvidence(receipts, bindings, revisionId, artifact, record,
                EvidenceClaimKind.LocationNativeType, first.RowLocator, nativeTypeLocator,
                EvidenceClaimContentId.DeriveV1(nativeType));

            var classificationId = LocationSemanticClassificationAssertionId.DeriveV1(
                record.Id, revisionId, nativeType.Id, AreaZoneRole, VocabularyVersion,
                ClassificationMethodId, ClassificationMethodVersion, nativeTypeLocator);
            var classification = new LocationSemanticClassificationAssertion(
                classificationId, record.Id, revisionId, nativeType.Id, AreaZoneRole, VocabularyVersion,
                ClassificationMethodId, ClassificationMethodVersion, nativeTypeLocator);
            classifications.Add(classification);
            AddEvidence(receipts, bindings, revisionId, artifact, record,
                EvidenceClaimKind.LocationSemanticClassification, first.RowLocator, nativeTypeLocator,
                EvidenceClaimContentId.DeriveV1(classification));
        }

        var registration = new CanonicalCatalogRegistration(
            source,
            [new SourceArtifactRecord(artifact.Id, artifact.Digest)],
            revision,
            records.ToImmutable(),
            [], [],
            receipts.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray(),
            [],
            bindings.DistinctBy(value => value.Id).OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray())
        {
            SourceNativeLocationTypeAssertions = nativeTypes.ToImmutable(),
            LocationSemanticClassificationAssertions = classifications.ToImmutable(),
        };
        return new AdapterBoundCanonicalCatalogRegistration(registration, Descriptor, scope, [artifact.FormatBinding]);
    }

    private LocationCoverageReport CreateCoverageReport(
        KnowledgeSourceScope scope,
        AdapterBoundCanonicalCatalogRegistration registration)
    {
        var records = registration.Registration.KnowledgeRecords.Select(value => value.Id).ToImmutableArray();
        var nativeType = registration.Registration.SourceNativeLocationTypeAssertions
            .Select(value => value.ExactNativeType).Distinct().Single();
        var declaration = new LocationSourceFamilyDeclaration(
            SourceFamily, Format, Descriptor.RevisionId, true,
            registration.Registration.Artifacts.Select(value => value.Id).ToImmutableArray(),
            [registration.Registration.SourceRevision.Id], [AreaZoneRole]);
        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate, "grid.location.coverage.qcs", "1",
            ContentDigest.ComputeSha256("grid.location.coverage.qcs.pending"u8));
        var manifest = new LocationCoverageManifest(
            LocationCoverageManifestId.DeriveV1(scope, "1", false, validation, [declaration]),
            scope, "1", false, validation, [declaration]);
        var family = new LocationSourceFamilyCoverage(
            SourceFamily,
            registration.Registration.Artifacts.Select(value => value.Id).ToImmutableArray(),
            [registration.Registration.SourceRevision.Id],
            records.Length, records.Length, records.Length, records,
            [], [], 0, 0, 0, 0);
        return LocationCoverageReport.Create(
            manifest,
            [family],
            [new LocationSemanticCategoryCoverage(nativeType, AreaZoneRole, records, 0)],
            new LocationTerminologyCoverage(records.Length, 0, 0, records.Length, 0),
            new LocationHierarchyCoverage(0, 0, 0, 0, true),
            [], []);
    }

    private ImmutableArray<PopulationZoneGroup> ParseGroups(FrozenSourceArtifact artifact) =>
        GtaVEnrichmentParsing.ParsePopulationZones(artifact, _corpusIndex)
            .GroupBy(value => value.NameLabel, StringComparer.Ordinal)
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new PopulationZoneGroup(
                value.Key,
                value.OrderBy(item => item.LabelFieldLocator, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();

    private static void AddEvidence(
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder receipts,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        CanonicalKnowledgeRecord record,
        EvidenceClaimKind kind,
        string recordLocator,
        string fieldLocator,
        EvidenceClaimContentId? contentId)
    {
        var value = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest, ParserId, ParserVersion,
            recordLocator, fieldLocator, null, null, null, artifact.ObservedAtUtc, record.NativeIdentity);
        var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
        receipts.Add(receipt);
        bindings.Add(new EvidenceBinding(
            EvidenceBindingId.DeriveV2(receipt.Id, kind, record.Id, revisionId, fieldLocator, contentId),
            receipt.Id, kind, record.Id, revisionId, fieldLocator, contentId));
    }

    private static CatalogSourceRecord CreateSource(KnowledgeSourceScope scope, SourceNativeIdentifier coordinate)
    {
        var kind = scope.ScopeKind == KnowledgeSourceScopeKind.BaseGame
            ? KnowledgeSourceKind.LocalGameDistribution
            : KnowledgeSourceKind.LocalModArtifact;
        return new CatalogSourceRecord(CatalogSourceId.DeriveV1(kind, coordinate), kind, coordinate);
    }

    private void RequireArtifact(FrozenSourceArtifact artifact) =>
        GtaVEnrichmentParsing.ValidateArtifact(artifact, CreateSourceCoordinate(), Format);

    private static SourceDiscoveryResult Unsupported(
        ImmutableArray<FrozenSourceArtifact> artifacts,
        string code) => new(
        [], artifacts.Select(value => new UnsupportedKnowledgeArtifact(value.Id, code)).ToImmutableArray(),
        KnowledgeCoverageState.Unsupported,
        [new KnowledgeBuildIssue(code, "The exact GTA V Enhanced population-zone source is unsupported.")]);

    private static KnowledgeExtractionResult UnsupportedExtraction(string code, string? detail = null) =>
        new([], [], [], KnowledgeCoverageState.Unsupported,
            [new KnowledgeBuildIssue(code, detail ?? "The exact GTA V Enhanced population-zone source is unsupported.")]);

    private sealed record PopulationZoneGroup(
        string NameLabel,
        ImmutableArray<GtaVEnrichmentParsing.PopulationZone> Occurrences);
}
