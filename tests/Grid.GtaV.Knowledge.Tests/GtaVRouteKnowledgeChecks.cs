using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVRouteKnowledgeChecks
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Route check failed: " + message); checks++; }
        var fixture = Fixture();
        var index = GtaVRouteCorpusIndex.Load(fixture.Json, fixture.Artifacts, Binding(fixture.Json));
        Assert(index.Metrics is { YndArtifactCount: 1, NodeCount: 5, DistinctNonzeroHashes: 3,
            NamedRouteCount: 2, UnmatchedHashCount: 1, ZeroHashNodeCount: 1 }, "native-key coverage retains repeats, zero and unmatched keys");
        Assert(index.NamedHashes.SequenceEqual(new uint[] { 1, 0xF1234567 }) && index.UnmatchedHashes.SequenceEqual(new uint[] { 3 }),
            "unsigned native keys join directly without string hashing");
        var adapter = new GtaVRouteKnowledgeAdapter(ContentDigest.ComputeSha256("route-test-adapter"u8), index);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "25261616");
        var extraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, adapter.Descriptor, fixture.Artifacts));
        var bound = extraction.CanonicalRegistrations.Single();
        var registration = bound.Registration;
        Assert(registration.KnowledgeRecords.Length == 2 && registration.KnowledgeRecords.Select(r => r.NativeRecordIdentityId).Distinct().Count() == 2,
            "equal displayed names do not merge distinct native keys");
        Assert(registration.TerminologyAssertions.Length == 2 && registration.TerminologyAssertions.All(t =>
            t.VerbatimValue == "Same Road" && t.LanguageTag == "en-US"), "exact locale-bearing source terms are preserved");
        Assert(registration.RelationshipAssertions.IsEmpty && registration.LocationSemanticClassificationAssertions.All(c => c.RoleId == LocationSemanticRoles.Route),
            "route role adds no geographic hierarchy");
        Assert(bound.SemanticClassificationAssertions.Length == 2 && bound.SemanticClassificationAssertions.All(c =>
            c.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable), "only source-named route keys are eligible");
        Assert(registration.FileEvidenceReceipts.All(e => e.Receipt.NativeObjectIdentity is not null) &&
            registration.FileEvidenceReceipts.Any(e => e.Receipt.SourceArtifactId == index.Gxt.Id), "identity and terminology retain exact file evidence");
        Assert(extraction.LocationCoverageReports.Single().Terminology.IdentifierOnlyRecordCount == 0 &&
            extraction.LocationCoverageReports.Single().Hierarchy.NotProvidedBySource, "coverage does not mistake unknown hashes for named options or parent edges");
        var payload = GtaVKnowledgePackageProjection.CreatePayload(extraction);
        var routeReport = extraction.LocationCoverageReports.Single();
        var baseReport = EmptyEstablishedCoverage(routeReport, bound.SourceScope, registration.SourceRevision.Id, fixture.Artifacts[0].Id);
        var combined = GtaAcquisitionSemanticProjection.CombinePlan2LocationReports(payload, bound.SourceScope, baseReport, routeReport);
        Assert(combined.SourceFamilies.Length == 2 && combined.SourceFamilies.Sum(f => f.UnsupportedObjectCount) == 7 &&
            combined.Terminology.TotalRecordCount == 2 && combined.Terminology.PrimaryNamedRecordCount == 2 &&
            combined.Terminology.ConflictingRecordCount == 0 && combined.Hierarchy.NotProvidedBySource && !combined.Manifest.IsClosed,
            "consolidation preserves prior partial source outcomes, recalculates names, and invents no hierarchy or completeness");
        var incompleteRejected = false;
        try { GtaAcquisitionSemanticProjection.ApplyPlan2LocationCoverage(payload, bound.SourceScope); }
        catch (InvalidDataException) { incompleteRejected = true; }
        Assert(incompleteRejected, "route-only coverage cannot substitute for the four Plan 2 source reports");
        var reordered = GtaVRouteCorpusIndex.Load(fixture.Json, fixture.Artifacts.Reverse(), Binding(fixture.Json));
        var replayAdapter = new GtaVRouteKnowledgeAdapter(ContentDigest.ComputeSha256("route-test-adapter"u8), reordered);
        var second = await replayAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, replayAdapter.Descriptor, fixture.Artifacts.Reverse().ToImmutableArray()));
        Assert(second.CanonicalRegistrations.Single().Registration.KnowledgeRecords.Select(r => r.Id)
            .SequenceEqual(registration.KnowledgeRecords.Select(r => r.Id)), "source enumeration preserves IDs");
        void Reject(byte[] json, bool retainBinding = false)
        {
            try { GtaVRouteCorpusIndex.Load(json, fixture.Artifacts, Binding(retainBinding ? fixture.Json : json)); }
            catch (InvalidDataException) { checks++; return; }
            throw new InvalidOperationException("Expected route source failure.");
        }
        Reject(Change(fixture.Json, node => node["locale"] = "fr-FR"));
        Reject(Change(fixture.Json, node => node["counts"]!["namedRouteCount"] = 9));
        Reject(Change(fixture.Json, node => node["ynds"]![0]!["nodes"]![0]!["ordinal"] = 5));
        Reject(Change(fixture.Json, node => node["ynds"]![0]!["nodes"]![0]!["streetNameHash"] = 9), true);
        Reject(Change(fixture.Json, node => node["unexpected"] = "not admitted"));
        var duplicate = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(fixture.Json).Replace("\"locale\":\"en-US\"", "\"locale\":\"en-US\",\"locale\":\"en-US\"", StringComparison.Ordinal));
        Reject(duplicate);
        Console.WriteLine("PASS  GTA V named-route source keys, locale, visibility, evidence, and partial coverage.");
        return checks;
    }

    private static (byte[] Json, ImmutableArray<FrozenSourceArtifact> Artifacts) Fixture()
    {
        var yndCoordinate = GtaVRouteCorpusIndex.PathsCoordinate + "!/nodes1.ynd";
        var artifacts = ImmutableArray.Create(
            Frozen(GtaVRouteCorpusIndex.PathsCoordinate, GtaVRouteCorpusIndex.RpfFormatId, "paths-fixture"u8.ToArray()),
            Frozen(GtaVRouteCorpusIndex.LanguageCoordinate, GtaVRouteCorpusIndex.RpfFormatId, "language-fixture"u8.ToArray()),
            Frozen(yndCoordinate, GtaVRouteCorpusIndex.YndFormatId, "ynd-fixture"u8.ToArray()),
            Frozen(GtaVRouteCorpusIndex.GxtCoordinate, GtaVRouteCorpusIndex.GxtFormatId, Gxt()));
        var hashes = new uint[] { 0, 1, 1, 0xF1234567, 3 };
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaId = GtaVRouteCorpusIndex.SchemaId, schemaVersion = 1,
            decoder = new { methodId = GtaVRouteCorpusIndex.DecoderMethodId, exactVersion = GtaVRouteCorpusIndex.DecoderVersion,
                artifactSha256 = GtaVRouteCorpusIndex.DecoderArtifactSha256 },
            locale = "en-US", scope = GtaVRouteCorpusIndex.Scope, gxtCoordinate = GtaVRouteCorpusIndex.GxtCoordinate,
            artifacts = artifacts.Select(a => new { sourceCoordinate = a.SourceCoordinate.ExactRepresentation,
                byteLength = a.ExactBytes.Length, sha256 = a.Digest.HexValue, formatId = a.DeclaredFormat.FormatId, formatVersion = "1" }),
            ynds = new[] { new { sourceCoordinate = yndCoordinate, nodes = hashes.Select((h, i) => new
                { ordinal = i, areaId = 1, nodeId = i, streetNameHash = h }) } },
            counts = new { yndArtifactCount = 1, nodeCount = 5, distinctNonzeroHashes = 3, namedRouteCount = 2, unmatchedHashCount = 1 },
        });
        return (json, artifacts);
    }
    private static FrozenSourceArtifact Frozen(string coordinate, string format, byte[] bytes)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new FrozenSourceArtifact(SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", "RouteSource", coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8", 1), new KnowledgeFormatCoordinate(format, "1"), bytes.ToImmutableArray(), Observed);
    }
    private static GtaVRouteCorpusIndexReceiptBinding Binding(byte[] bytes) => new(GtaVRouteCorpusIndex.SchemaId, 1,
        GtaVRouteCorpusIndex.DecoderMethodId, GtaVRouteCorpusIndex.DecoderVersion, GtaVRouteCorpusIndex.DecoderArtifactSha256,
        bytes.Length, ContentDigest.ComputeSha256(bytes));
    private static byte[] Change(byte[] original, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(original)!;
        edit(node);
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }
    private static byte[] Gxt()
    {
        var text = Encoding.UTF8.GetBytes("Same Road\0");
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x47585432u); writer.Write(2u);
        writer.Write(1u); writer.Write(32u);
        writer.Write(0xF1234567u); writer.Write(32u + (uint)text.Length);
        writer.Write(0x47585432u); writer.Write(32u + 2u * (uint)text.Length);
        writer.Write(text); writer.Write(text);
        return stream.ToArray();
    }

    private static LocationCoverageReport EmptyEstablishedCoverage(LocationCoverageReport routeReport, KnowledgeSourceScope scope,
        CatalogSourceRevisionId revision, SourceArtifactId artifact)
    {
        var familyId = new LocationSourceFamilyId("grid.fixture.location.established");
        var declaration = new LocationSourceFamilyDeclaration(familyId,
            new KnowledgeFormatCoordinate(GtaVRouteCorpusIndex.YndFormatId, "1"),
            routeReport.Manifest.SourceFamilies[0].AdapterRevisionId, true, [artifact], [revision], [LocationSemanticRoles.AreaZone]);
        var validation = routeReport.Manifest.QcsValidation;
        var manifest = new LocationCoverageManifest(LocationCoverageManifestId.DeriveV1(scope, "route-fixture-established-v1",
            false, validation, [declaration]), scope, "route-fixture-established-v1", false, validation, [declaration]);
        return LocationCoverageReport.Create(manifest,
            [new LocationSourceFamilyCoverage(familyId, [artifact], [revision], 7, 7, 0, [], [], [], 7, 0, 0, 0)],
            [], new LocationTerminologyCoverage(0, 0, 0, 0, 0), new LocationHierarchyCoverage(0, 0, 0, 0, true), [], []);
    }
}
