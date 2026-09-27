using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVMountedSpatialRegistrationChecks
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 9, 26, 20, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var fixture = CreateFixture("spatial-a");
        var index = GtaVSpatialCorpusIndex.Load(fixture.Sidecar, fixture.Artifacts, fixture.Binding);
        Assert(index.Ytyps.Length == 1 && index.Ymaps.Length == 1 && index.Ymfs.Length == 1 &&
               index.Ytyps[0].Archetypes.Length == 2 && index.Ytyps[0].Archetypes.Sum(value => value.Rooms.Length) == 2,
            "The receipt-bound spatial index closes over the exact YTYP/YMAP/YMF fixture corpus.");

        var adapter = new GtaVMountedSpatialKnowledgeAdapter(ContentDigest.ComputeSha256("spatial-adapter"u8), index);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "25261616");
        var extraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            adapter.Descriptor, fixture.Artifacts));
        var acquisition = CreateAcquisition(version, fixture.Artifacts);
        var payload = GtaVKnowledgePackageProjection.CreatePayload(
            [extraction], acquisition.Receipts, acquisition.Bindings);
        Assert(payload.KnowledgeRecords.Length == 6 && payload.KnowledgeRecords.All(value => value.Kind == KnowledgeKind.Location),
            "Two MLO definitions, two rooms, and two placed instances become six distinct Locations.");
        Assert(adapter.LastMetrics is
            {
                ArchetypeCount: 2, RoomCount: 2, InstanceCount: 2,
                ContainedByCount: 2, ResolvedInstanceOfCount: 1, UnresolvedInstanceOfCount: 1,
                ConnectsToCount: 1,
            }, "Only exact MLO ownership, hash references, and portal endpoints become spatial relationships.");
        Assert(payload.RelationshipAssertions.Count(value => value.SemanticId == LocationRelationshipSemantics.ContainedBy) == 2 &&
               payload.RelationshipAssertions.Count(value => value.SemanticId == LocationRelationshipSemantics.InstanceOf) == 2 &&
               payload.RelationshipAssertions.Count(value => value.SemanticId == LocationRelationshipSemantics.ConnectsTo) == 1 &&
               payload.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Unresolved) == 1,
            "An unmatched archetype hash remains an unresolved instance-of edge and is never guessed.");
        Assert(payload.RelationshipAssertions.All(value =>
                   value.SemanticId != LocationRelationshipSemantics.SpatialMemberOf) &&
               payload.TerminologyAssertions.IsEmpty,
            "Geometry, overlap, and technical room/model values create neither hierarchy nor terminology.");
        Assert(payload.LocationSemanticClassificationAssertions.Count(value => value.RoleId == LocationSemanticRoles.Interior) == 4 &&
               payload.LocationSemanticClassificationAssertions.Count(value => value.RoleId == LocationSemanticRoles.Room) == 2 &&
               payload.LocationSemanticClassificationAssertions.Count(value => value.RoleId == LocationSemanticRoles.Instance) == 2,
            "MLO definitions, rooms, and placed instances retain exact, independently evidenced roles.");
        Assert(payload.FileEvidenceReceipts.All(value => value.Receipt.NativeObjectIdentity is not null) &&
               payload.EvidenceBindings.All(binding => payload.FileEvidenceReceipts.Any(receipt => receipt.Id == binding.EvidenceReceiptId)),
            "Every identity, type, classification, and relationship is bound to a complete native object and raw artifact receipt.");

        var package = CanonicalCatalogPackageKernel.CreateV6(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version,
                payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
            null, [], "grid.catalog-composition.v1", payload,
            new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.gta-v.spatial-test", "1",
                ContentDigest.ComputeSha256("spatial-candidate"u8)),
            new CatalogBuildProvenance(CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.gta-v.spatial-test", "1", new string('a', 40),
                [new CatalogCommittedBuildInput("src/Grid.GtaV.Knowledge/GtaVMountedSpatialKnowledgeAdapter.cs", new string('b', 40))]));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        Assert(verification.IsStructurallyValid,
            "The bounded MLO Location identity/type/hierarchy evidence graph is package-v6 valid: " + string.Join(" | ", verification.Issues));

        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var replayIndex = GtaVSpatialCorpusIndex.Load(fixture.Sidecar, fixture.Artifacts.Reverse(), fixture.Binding);
            var replayAdapter = new GtaVMountedSpatialKnowledgeAdapter(ContentDigest.ComputeSha256("spatial-adapter"u8), replayIndex);
            var replay = await replayAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
                replayAdapter.Descriptor, fixture.Artifacts.Reverse().ToImmutableArray()));
            var replayPayload = GtaVKnowledgePackageProjection.CreatePayload(
                [replay], acquisition.Receipts, acquisition.Bindings);
            Assert(payload.KnowledgeRecords.Select(value => value.Id).SequenceEqual(replayPayload.KnowledgeRecords.Select(value => value.Id)) &&
                   payload.RelationshipAssertions.Select(EvidenceClaimContentId.DeriveV1)
                       .SequenceEqual(replayPayload.RelationshipAssertions.Select(EvidenceClaimContentId.DeriveV1)),
                "Spatial registration is invariant to culture and artifact enumeration.");
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }

        ExpectInvalid(() => GtaVSpatialCorpusIndex.Load(
            Replace(fixture.Sidecar, "0x10000001", "0x10000003"), fixture.Artifacts, fixture.Binding)); checks++;
        var wrongLocator = Replace(fixture.Sidecar, fixture.Ytyp.SourceCoordinate.ExactRepresentation + "#/archetypes[40]/rooms[1]",
            fixture.Ymap.SourceCoordinate.ExactRepresentation + "#/archetypes[40]/rooms[1]");
        ExpectInvalid(() => GtaVSpatialCorpusIndex.Load(wrongLocator, fixture.Artifacts, Binding(wrongLocator))); checks++;
        var selfPortal = Replace(fixture.Sidecar, "\"roomToOrdinal\":7", "\"roomToOrdinal\":5");
        ExpectInvalid(() => GtaVSpatialCorpusIndex.Load(selfPortal, fixture.Artifacts, Binding(selfPortal))); checks++;
        var wrongFormatArtifact = Frozen(fixture.Ytyp.SourceCoordinate.ExactRepresentation,
            GtaVSpatialCorpusIndex.YmapFormatId, fixture.Ytyp.ExactBytes.ToArray());
        ExpectInvalid(() => GtaVSpatialCorpusIndex.Load(fixture.Sidecar,
            fixture.Artifacts.Replace(fixture.Ytyp, wrongFormatArtifact), fixture.Binding)); checks++;
        var unknownField = Replace(fixture.Sidecar, "\"position\":[1,2,3]", "\"position\":[1,2,3],\"lodParent\":4");
        ExpectInvalid(() => GtaVSpatialCorpusIndex.Load(unknownField, fixture.Artifacts, Binding(unknownField))); checks++;

        Console.WriteLine("PASS  GTA V mounted MLO spatial index, identity, hierarchy, and evidence registration.");
        return checks;
    }

    private static Fixture CreateFixture(string seed)
    {
        var ytyp = Frozen("update/x64/dlcpacks/test/dlc.rpf!/x64/levels/gta5/test/test.ytyp",
            GtaVSpatialCorpusIndex.YtypFormatId, Encoding.UTF8.GetBytes(seed + "-ytyp"));
        var ymap = Frozen("update/x64/dlcpacks/test/dlc.rpf!/x64/levels/gta5/test/test.ymap",
            GtaVSpatialCorpusIndex.YmapFormatId, Encoding.UTF8.GetBytes(seed + "-ymap"));
        var ymf = Frozen("update/x64/dlcpacks/test/dlc.rpf!/x64/levels/gta5/test/test.ymf",
            GtaVSpatialCorpusIndex.YmfFormatId, Encoding.UTF8.GetBytes(seed + "-ymf"));
        string L(FrozenSourceArtifact artifact, string path) =>
            "rpf7-member:" + artifact.SourceCoordinate.ExactRepresentation + "#" + path;
        var document = new
        {
            schemaId = GtaVSpatialCorpusIndex.SchemaId,
            schemaVersion = GtaVSpatialCorpusIndex.SchemaVersion,
            decoder = new { methodId = GtaVSpatialCorpusIndex.DecoderMethodId, exactVersion = GtaVSpatialCorpusIndex.DecoderVersion, artifactSha256 = new string('c', 64) },
            artifacts = new[] { Artifact(ytyp), Artifact(ymap), Artifact(ymf) },
            ytyps = new[]
            {
                new
                {
                    artifact = ytyp.SourceCoordinate.ExactRepresentation,
                    archetypes = new object[]
                    {
                        new
                        {
                            ordinal = 39, nameHash = "0x20000001", assetNameHash = "0x10000001", nativeType = "CMloArchetypeDef",
                            fieldLocator = L(ytyp, "/archetypes[40]"),
                            rooms = new object[]
                            {
                                new { ordinal = 5, nameExact = "technical_room_a", fieldLocator = L(ytyp, "/archetypes[40]/rooms[1]") },
                                new { ordinal = 7, nameExact = "technical_room_b", fieldLocator = L(ytyp, "/archetypes[40]/rooms[2]") },
                            },
                            portals = new[] { new { ordinal = 12, roomFromOrdinal = 5, roomToOrdinal = 7, fieldLocator = L(ytyp, "/archetypes[40]/portals[13]") } },
                            entitySets = Array.Empty<object>(),
                        },
                        new
                        {
                            ordinal = 41, nameHash = "0x20000002", assetNameHash = "0x10000002", nativeType = "CMloArchetypeDef",
                            fieldLocator = L(ytyp, "/archetypes[42]"), rooms = Array.Empty<object>(), portals = Array.Empty<object>(), entitySets = Array.Empty<object>(),
                        },
                    },
                },
            },
            ymaps = new[]
            {
                new
                {
                    artifact = ymap.SourceCoordinate.ExactRepresentation,
                    mapNameHash = "0x30000001",
                    instances = new[]
                    {
                        new { ordinal = 0, guid = "1073741825", archetypeNameHash = "0x20000001", fieldLocator = L(ymap, "/entities[0]"), position = new[] { 1, 2, 3 } },
                        new { ordinal = 1, guid = "1073741826", archetypeNameHash = "0x99999999", fieldLocator = L(ymap, "/entities[1]"), position = new[] { 4, 5, 6 } },
                    },
                },
            },
            ymfs = new[] { new { artifact = ymf.SourceCoordinate.ExactRepresentation, relations = Array.Empty<object>() } },
            unsupported = Array.Empty<object>(),
        };
        var sidecar = JsonSerializer.SerializeToUtf8Bytes(document);
        return new Fixture(sidecar, [ytyp, ymap, ymf], Binding(sidecar), ytyp, ymap);
    }

    private static object Artifact(FrozenSourceArtifact artifact) => new
    {
        sourceCoordinate = artifact.SourceCoordinate.ExactRepresentation,
        byteLength = artifact.ExactBytes.Length,
        sha256 = artifact.Digest.HexValue,
        formatId = artifact.DeclaredFormat.FormatId,
        formatVersion = artifact.DeclaredFormat.ExactFormatVersion,
        consumptionStatus = "semantic",
    };

    private static FrozenSourceArtifact Frozen(string coordinate, string format, byte[] bytes)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new FrozenSourceArtifact(SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", "MountedSpatialSource",
                coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1),
            new KnowledgeFormatCoordinate(format, GtaVSpatialCorpusIndex.ExactFormatVersion),
            bytes.ToImmutableArray(), ObservedAt);
    }

    private static GtaVSpatialCorpusIndexReceiptBinding Binding(byte[] bytes) => new(
        GtaVSpatialCorpusIndex.SchemaId, GtaVSpatialCorpusIndex.SchemaVersion,
        GtaVSpatialCorpusIndex.DecoderMethodId, GtaVSpatialCorpusIndex.DecoderVersion,
        new string('c', 64), bytes.Length, ContentDigest.ComputeSha256(bytes));

    private static AcquisitionFixture CreateAcquisition(SourceNativeVersion version,
        ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        var method = new AcquisitionMethodCoordinate("grid.gta-v-enhanced.spatial-acquisition", "1",
            "fivefury.rpf-read+mlo-spatial", GtaVSpatialCorpusIndex.DecoderVersion,
            new ContentDigest(ContentDigest.Sha256Algorithm, new string('c', 64)));
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220",
            "valve.steam.app-id.exact-utf8", 1);
        var container = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.container-coordinate",
            "Rpf7Container", "spatial-fixture.rpf", "grid.gta-v.container-coordinate.exact-utf8", 1);
        var containerDigest = ContentDigest.ComputeSha256("spatial-fixture-container"u8);
        var members = artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .Select(value => new SourceAcquisitionMember(value.SourceCoordinate,
                value.ExactBytes.Length, value.Digest, value.Id)).ToImmutableArray();
        var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, container,
            1_000_000, containerDigest, method, members);
        var receipt = new SourceAcquisitionReceipt(id, SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, container,
            1_000_000, containerDigest, method, members);
        return new AcquisitionFixture([receipt], members.Select(value => new SourceArtifactAcquisitionBinding(
            value.ArtifactId, receipt.Id, value.MemberCoordinate, value.ByteLength, value.Digest)).ToImmutableArray());
    }

    private static byte[] Replace(byte[] bytes, string oldValue, string newValue)
    {
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains(oldValue, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture replacement target absent.");
        return Encoding.UTF8.GetBytes(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }

    private static void ExpectInvalid(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected the spatial contract to fail closed.");
    }

    private sealed record Fixture(byte[] Sidecar, ImmutableArray<FrozenSourceArtifact> Artifacts,
        GtaVSpatialCorpusIndexReceiptBinding Binding, FrozenSourceArtifact Ytyp, FrozenSourceArtifact Ymap);
    private sealed record AcquisitionFixture(ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);
}
