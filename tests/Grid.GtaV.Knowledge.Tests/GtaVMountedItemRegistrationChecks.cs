using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVMountedItemRegistrationChecks
{
    public static async Task<int> RunAsync()
    {
        int checks = 0;
        void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var coordinate = "update/x64/dlcpacks/fixture/dlc.rpf!/common/data/vehicles.meta";
        var xml = Frozen(coordinate, "vehicles", Encoding.UTF8.GetBytes("""
            <CVehicleModelInfo__InitDataList><InitDatas>
              <Item><modelName>model_a</modelName><gameName>LABEL_A</gameName><vehicleClass>VC_SPORT</vehicleClass><handlingId>HANDLING_A</handlingId></Item>
              <Item><modelName>model_a</modelName><gameName>LABEL_A</gameName><vehicleClass>VC_SPORT</vehicleClass><handlingId>HANDLING_B</handlingId></Item>
              <Item><modelName>model_b</modelName><gameName>LABEL_A</gameName><vehicleClass>VC_UNKNOWN</vehicleClass><dashboardType>A</dashboardType><dashboardType>B</dashboardType></Item>
            </InitDatas></CVehicleModelInfo__InitDataList>
            """));
        var local = Frozen("update/x64/dlcpacks/fixture/dlc.rpf!/x64/data/lang/americandlc.rpf!/global.gxt2", "localization", Gxt("LABEL_A", "Exact DLC Title"));
        var global = Frozen("x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2", "localization", Gxt("LABEL_A", "Different Base Title"));
        var bytes = Index((xml, "vehicles", "fixture", 1), (local, "localization", "fixture", 1), (global, "localization", "", -3));
        var index = new GtaVItemCorpusIndex(bytes, [xml, local, global]);
        Assert(index.Rows.Length == 3, "Duplicate shipped definitions must retain distinct evidence locators.");
        Assert(index.Unresolved.Single().Contains("dashboardType", StringComparison.Ordinal) && !index.Rows[2].Fields.ContainsKey("dashboardType"),
            "Repeated raw scalar fields remain explicitly unresolved without aborting the source or selecting a winner.");
        Assert(index.ResolveText(index.Rows[0].Source, "LABEL_A").Single().Text == "Exact DLC Title", "DLC text must resolve in its own source scope.");
        Assert(index.ResolveText(index.Rows[0].Source, "MISSING_LABEL").IsEmpty, "An absent label must not become a display identifier.");
        Assert(index.Rows.SelectMany(x => x.References).Select(x => x.NativeKey).SequenceEqual(["HANDLING_A", "HANDLING_B"]), "Conflicting native relationships must remain separate.");
        var digest = ContentDigest.ComputeSha256("item-fixture-adapter"u8);
        var adapter = new GtaVMountedVehicleKnowledgeAdapter(digest, index);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture");
        var extraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, adapter.Descriptor, adapter.PrimaryArtifacts));
        var records = extraction.CanonicalRegistrations.SelectMany(x => x.Registration.KnowledgeRecords).ToArray();
        Assert(records.Length == 2 && records.Select(x => x.Id).Distinct().Count() == 2, "Repeated definitions must not multiply identities; separate models sharing a name remain separate.");
        var history = GtaVKnowledgePackageProjection.CreatePayload(extraction);
        Assert(new GtaVMountedVehicleKnowledgeAdapter(digest, index, history).PrimaryArtifacts.IsEmpty,
            "Historical identities must be excluded from primary re-registration.");
        var reverse = new GtaVItemCorpusIndex(bytes, new[] { global, local, xml });
        Assert(reverse.Rows.Select(x => x.Locator).SequenceEqual(index.Rows.Select(x => x.Locator)), "Index enumeration order must not change evidence.");
        try { _ = new GtaVItemCorpusIndex(bytes, [xml, local]); throw new InvalidOperationException("Missing closure accepted."); }
        catch (InvalidDataException) { checks++; }
        var scope = KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);
        var secondary = new GtaVMountedItemSecondaryAssertionAdapter(digest, index).Extract(history, scope).Single();
        Assert(secondary.TerminologyAssertions.All(x => x.LanguageTag == "en-US" && x.VerbatimValue == "Exact DLC Title"), "Primary titles must be exact scoped localized text.");
        Assert(secondary.RelationshipAssertions.Length == 2 && secondary.RelationshipAssertions.All(x => x.ResolvedTargetKnowledgeRecordId is null), "Native handling references remain unresolved evidence, never invented canonical targets.");
        Assert(secondary.OrganizationalValues.IsEmpty, "A vehicle class without its pinned bridge cannot become a category label.");
        Assert(secondary.ReferenceEvidenceReceipts.IsEmpty && secondary.CrossSourceAssertions.Length > 0, "Pure shipped-file facts must keep FILE provenance and exact cross-source envelopes.");
        var acquisition = Acquisition(version, [xml, local, global]);
        var verifiedHistory = GtaVKnowledgePackageProjection.CreatePayload([extraction], acquisition.Receipts, acquisition.Bindings);
        var payload = secondary.ApplyTo(verifiedHistory, acquisition.Receipts, acquisition.Bindings);
        var package = CanonicalCatalogPackageKernel.CreateV6(CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, payload.Artifacts.Select(x => x.Id).ToImmutableArray()),
            null, [], "grid.catalog-composition.v1", payload,
            new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.gta-v.item-tests", "1", digest),
            new CatalogBuildProvenance(CatalogBuildProvenance.CurrentSchemaVersion, "grid.gta-v.item-tests", "1", new string('a', 40),
                [new CatalogCommittedBuildInput("src/Grid.GtaV.Knowledge/GtaVMountedItemSecondaryAssertionAdapter.cs", new string('b', 40))]));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        Assert(verification.IsStructurallyValid, "FILE-only Item terminology, classifications and relationships must close through the kernel: " + string.Join("; ", verification.Issues));
        var weapon = Frozen("common.rpf!/data/ai/weapons.meta", "weapons", Encoding.UTF8.GetBytes("""
            <CWeaponInfoBlob><Infos>
              <Item type="CWeaponInfo"><Name>WEAPON_PLAYER</Name><HumanNameHash>LABEL_A</HumanNameHash><WeaponFlags>CarriedInHand UsableOnFoot</WeaponFlags><AmmoInfo ref="AMMO_TEST"/></Item>
              <Item type="CWeaponInfo"><Name>WEAPON_TECHNICAL</Name><HumanNameHash>LABEL_A</HumanNameHash><WeaponFlags>HiddenFromWeaponWheel UsableOnFoot</WeaponFlags></Item>
              <Item type="CAmmoInfo"><Name>AMMO_TEST</Name></Item>
            </Infos></CWeaponInfoBlob>
            """));
        var weaponIndex = new GtaVItemCorpusIndex(Index((weapon, "weapons", "", -3), (global, "localization", "", -3)), [weapon, global]);
        var weaponAdapter = new GtaVMountedItemKnowledgeAdapter("weapons", digest, weaponIndex);
        var weaponExtraction = await weaponAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, weaponAdapter.Descriptor, weaponAdapter.PrimaryArtifacts));
        var weaponHistory = GtaVKnowledgePackageProjection.CreatePayload(weaponExtraction);
        var weaponFacts = new GtaVMountedItemSecondaryAssertionAdapter(digest, weaponIndex).Extract(weaponHistory, scope).Single();
        var visible = weaponFacts.SemanticClassifications.Where(x => x.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable).ToArray();
        Assert(visible.Length == 1 && weaponHistory.KnowledgeRecords.Single(x => x.Id == visible[0].KnowledgeRecordId).NativeIdentity.ExactRepresentation == "WEAPON_PLAYER",
            "A localized technical weapon and ammunition must remain excluded from normal presentation.");
        Assert(weaponFacts.RelationshipAssertions.Single().ResolvedTargetKnowledgeRecordId == weaponHistory.KnowledgeRecords.Single(x => x.NativeIdentity.ExactRepresentation == "AMMO_TEST").Id,
            "A typed ammunition reference resolves through exact native identity.");
        var apparel = Frozen("update/x64/dlcpacks/fixture/dlc.rpf!/common/data/apparel.meta", "apparel", Encoding.UTF8.GetBytes("""
            <ShopPedApparel><pedName>mp_m_freemode_01</pedName><fullDlcName>fixture_male</fullDlcName><eCharacter>SCR_CHAR_MULTIPLAYER</eCharacter><pedComponents>
              <Item><uniqueNameHash>variant_a</uniqueNameHash><textLabel>LABEL_A</textLabel><drawableIndex value="1"/><textureIndex value="0"/></Item>
              <Item><uniqueNameHash>variant_b</uniqueNameHash><textLabel>LABEL_A</textLabel><drawableIndex value="1"/><textureIndex value="1"/></Item>
            </pedComponents><pedOutfits><Item><uniqueNameHash>outfit_a</uniqueNameHash><textLabel>LABEL_A</textLabel><includedPedComponents><Item><nameHash>variant_a</nameHash></Item></includedPedComponents></Item></pedOutfits></ShopPedApparel>
            """));
        var apparelIndex = new GtaVItemCorpusIndex(Index((apparel, "apparel", "fixture", 1), (local, "localization", "fixture", 1)), [apparel, local]);
        Assert(apparelIndex.Rows.Select(x => x.NativeKey).Distinct().Count() == 3 && apparelIndex.Rows.All(x => x.NativeKey.StartsWith("mp_m_freemode_01/fixture_male/", StringComparison.Ordinal)),
            "Apparel variants preserve character, collection and native variant identity despite equal names.");
        Assert(apparelIndex.Rows.SelectMany(x => x.References).Count(x => x.Kind == "textureIndex") == 2,
            "Attribute-valued apparel texture variants retain exact source references.");
        var apparelAdapter = new GtaVApparelKnowledgeAdapter(digest, apparelIndex);
        var apparelExtraction = await apparelAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null, apparelAdapter.Descriptor, apparelAdapter.PrimaryArtifacts));
        var apparelHistory = GtaVKnowledgePackageProjection.CreatePayload(apparelExtraction);
        var apparelFacts = new GtaVMountedItemSecondaryAssertionAdapter(digest, apparelIndex).Extract(apparelHistory, scope).Single();
        Assert(apparelFacts.RelationshipAssertions.Single(x => x.SourceNativeRelationshipType == "nameHash").ResolvedTargetKnowledgeRecordId ==
            apparelHistory.KnowledgeRecords.Single(x => x.NativeIdentity.ExactRepresentation.EndsWith("/variant_a", StringComparison.Ordinal)).Id,
            "Outfit component links resolve only through an unambiguous exact native hash within the same character model.");
        if (Environment.GetEnvironmentVariable("GRID_GTA_ITEM_FIXTURE_ROOT") is { Length: > 0 } fixtureRoot)
        {
            using var receipt = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "gta-v-enhanced-item-acquisition-receipt.v1.json")));
            var artifacts = receipt.RootElement.GetProperty("containers").EnumerateArray()
                .SelectMany(x => x.GetProperty("artifacts").EnumerateArray()).Select(x =>
                    Frozen(x.GetProperty("sourceCoordinate").GetString()!, x.GetProperty("role").GetString()!,
                        File.ReadAllBytes(Path.Combine(fixtureRoot, x.GetProperty("frozenRelativePath").GetString()!)))).ToArray();
            var actual = new GtaVItemCorpusIndex(await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, "item-corpus-index.v1.json")), artifacts);
            Assert(actual.Rows.Any(x => x.Source.Family == "vehicles") && actual.Rows.Any(x => x.Source.Family == "apparel"),
                "The supplied immutable mounted source fixture must parse real vehicle and apparel families.");
        }
        return checks;
    }

    private static byte[] Index(params (FrozenSourceArtifact Artifact, string Family, string Pack, int Ordinal)[] values) =>
        JsonSerializer.SerializeToUtf8Bytes(new { schemaId = GtaVItemCorpusIndex.SchemaId, schemaVersion = 1,
            sources = values.Select(x => new { sourceCoordinate = x.Artifact.SourceCoordinate.ExactRepresentation,
                family = x.Family, pack = x.Pack, mountOrdinal = x.Ordinal, locale = x.Family == "localization" ? "en-US" : null, declaration = (string?)null }), mountLedger = Array.Empty<object>() });

    private static (ImmutableArray<SourceAcquisitionReceipt> Receipts, ImmutableArray<SourceArtifactAcquisitionBinding> Bindings)
        Acquisition(SourceNativeVersion version, ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        var method = new AcquisitionMethodCoordinate("grid.gta-v-enhanced.item-mounted-acquisition", "1", "fivefury.rpf-read",
            GtaVItemCorpusIndex.DecoderVersion, new ContentDigest(ContentDigest.Sha256Algorithm, GtaVItemCorpusIndex.ApprovedDecoderArtifactSha256));
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220", "valve.steam.app-id.exact-utf8", 1);
        var container = SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", "item-fixture.rpf", "grid.gta-v.container-coordinate.exact-utf8", 1);
        var containerDigest = ContentDigest.ComputeSha256("item-fixture-container"u8);
        var members = artifacts.OrderBy(x => x.Id.Value, StringComparer.Ordinal).Select(x =>
            new SourceAcquisitionMember(x.SourceCoordinate, x.ExactBytes.Length, x.Digest, x.Id)).ToImmutableArray();
        var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, container, 1_000_000, containerDigest, method, members);
        var receipt = new SourceAcquisitionReceipt(id, SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, app, version, container, 1_000_000, containerDigest, method, members);
        return ([receipt], members.Select(x => new SourceArtifactAcquisitionBinding(x.ArtifactId, receipt.Id, x.MemberCoordinate, x.ByteLength, x.Digest)).ToImmutableArray());
    }

    private static FrozenSourceArtifact Frozen(string coordinate, string family, byte[] bytes)
    {
        var format = family switch { "localization" => "rockstar.gta-v.gxt2-binary", "localization-container" => "rockstar.rpf7-container", _ => GtaVItemCorpusIndex.FormatPrefix + family + "-xml" };
        var digest = ContentDigest.ComputeSha256(bytes);
        return new(SourceArtifactId.DeriveV1(digest), digest, SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.resource-coordinate", GtaVMountedItemKnowledgeAdapter.SourceObjectType(format), coordinate,
            "grid.gta-v.resource-coordinate.exact-utf8", 1), new(format, "1"), bytes.ToImmutableArray());
    }

    private static byte[] Gxt(string label, string title)
    {
        var text = Encoding.UTF8.GetBytes(title); var bytes = new byte[25 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), GtaVItemCorpusIndex.Joaat(label));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)bytes.Length);
        text.CopyTo(bytes.AsSpan(24)); return bytes;
    }
}
