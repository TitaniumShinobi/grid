using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

internal static class GtaVCorpusIndexChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var observed = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);
        var adapterDigest = ContentDigest.ComputeSha256("corpus-index-adapter"u8);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture-build");
        var scope = KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);
        var weapons = Frozen(
            "common.rpf!/data/ai/weapons.meta", "CWeaponInfoBlob",
            GtaVWeaponsMetaKnowledgeAdapter.Format,
            Encoding.UTF8.GetBytes("""
                <CWeaponInfoBlob>
                  <SlotNavigateOrder><Item><WeaponSlots><Item><OrderNumber value="1"/><Entry>SLOT_PISTOL</Entry></Item></WeaponSlots></Item></SlotNavigateOrder>
                  <SlotBestOrder><WeaponSlots><Item><OrderNumber value="2"/><Entry>SLOT_PISTOL</Entry></Item></WeaponSlots></SlotBestOrder>
                  <Infos><Item type="CWeaponInfo"><Name>WEAPON_PISTOL</Name><HumanNameHash>WT_PIST</HumanNameHash><WheelSlot>WHEEL_PISTOL</WheelSlot><Group>GROUP_PISTOL</Group><Slot>SLOT_PISTOL</Slot><AmmoInfo ref="AMMO_PISTOL"/></Item><Item type="CAmmoInfo"><Name>AMMO_PISTOL</Name></Item></Infos>
                </CWeaponInfoBlob>
                """), observed);
        var languageRpf = Frozen(
            "x64b.rpf!/data/lang/american_rel.rpf", "Rpf7Container",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion),
            "language-rpf-index-fixture"u8.ToArray(), observed);
        var labelHash = Joaat("WT_PIST");
        var populationHash = Joaat("LABEL_A");
        var gxt = Frozen(
            "x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2", "GXT2",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId,
                GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion),
            CreateGxt2((labelHash, "Pistol"), (populationHash, "Named Area")), observed);
        var population = Frozen(
            GtaVPopulationZonesKnowledgeAdapter.Coordinate, "PopulationZoneIplSection",
            new KnowledgeFormatCoordinate(GtaVPopulationZonesKnowledgeAdapter.FormatId,
                GtaVPopulationZonesKnowledgeAdapter.ExactFormatVersion),
            Encoding.UTF8.GetBytes("zone\n1,0,0,0,1,1,1,LABEL_A,0\n2,1,1,1,2,2,2,LABEL_A,0\nend\n"), observed);
        var index = new GtaVSupportedSourceCorpusIndex([weapons, languageRpf, gxt, population]);
        var enrichmentIndex = new GtaVEnrichmentSourceCorpusIndex(index);

        var directAdapter = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, adapterDigest);
        var indexedAdapter = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, adapterDigest, index);
        var direct = await directAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            directAdapter.Descriptor, [weapons]));
        var indexed = await indexedAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            indexedAdapter.Descriptor, [weapons]));
        Assert(direct.CanonicalRegistrations.SelectMany(value => value.Registration.KnowledgeRecords).Select(value => value.Id)
                .SequenceEqual(indexed.CanonicalRegistrations.SelectMany(value => value.Registration.KnowledgeRecords).Select(value => value.Id)) &&
               direct.CanonicalRegistrations.SelectMany(value => value.Registration.EvidenceBindings).Select(value => value.Id)
                .SequenceEqual(indexed.CanonicalRegistrations.SelectMany(value => value.Registration.EvidenceBindings).Select(value => value.Id)),
            "Indexed primary extraction is identity/evidence equivalent to direct extraction.");

        var origin = GtaVKnowledgePackageProjection.CreatePayload(direct);
        var directSecondary = new GtaVWeaponsSecondaryAssertionAdapter(adapterDigest)
            .Extract(origin, scope, weapons, languageRpf, gxt);
        var indexedSecondary = new GtaVWeaponsSecondaryAssertionAdapter(adapterDigest, index)
            .Extract(origin, scope, weapons, languageRpf, gxt);
        Assert(SemanticIds(directSecondary).SequenceEqual(SemanticIds(indexedSecondary)),
            "Indexed terminology/classification extraction is byte-semantic equivalent to direct extraction.");

        var directOrganization = new GtaVWeaponsOrganizationSecondaryAssertionAdapter(adapterDigest)
            .Extract(origin, scope, weapons);
        var indexedOrganization = new GtaVWeaponsOrganizationSecondaryAssertionAdapter(adapterDigest, index)
            .Extract(origin, scope, weapons);
        Assert(SemanticIds(directOrganization).SequenceEqual(SemanticIds(indexedOrganization)),
            "Indexed organizational extraction is byte-semantic equivalent to direct extraction.");
        Assert(index.GetPhysicalParseCount(weapons, "xml-v1") == 1 &&
               index.GetPhysicalParseCount(gxt, "gxt2-v1") == 1,
            "Multiple adapters decode each XML/GXT2 artifact representation exactly once.");

        var directPopulationAdapter = new GtaVPopulationZonesKnowledgeAdapter(adapterDigest);
        var indexedPopulationAdapter = new GtaVPopulationZonesKnowledgeAdapter(adapterDigest, enrichmentIndex);
        var directPopulation = await directPopulationAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            directPopulationAdapter.Descriptor, [population]));
        var indexedPopulation = await indexedPopulationAdapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            indexedPopulationAdapter.Descriptor, [population]));
        Assert(directPopulation.CanonicalRegistrations.SelectMany(value => value.Registration.KnowledgeRecords).Select(value => value.Id)
                .SequenceEqual(indexedPopulation.CanonicalRegistrations.SelectMany(value => value.Registration.KnowledgeRecords).Select(value => value.Id)),
            "Indexed population-zone grouping is identity-equivalent to direct extraction.");
        var populationOrigin = GtaVKnowledgePackageProjection.CreatePayload(indexedPopulation);
        _ = new GtaVPopulationZoneTerminologyAdapter(adapterDigest, enrichmentIndex)
            .Extract(populationOrigin, scope, population, languageRpf, gxt);
        Assert(enrichmentIndex.GetPopulationZoneParseCount(population) == 1,
            "Population identity and terminology adapters reuse one parsed row index.");

        var relabeled = new FrozenSourceArtifact(
            weapons.Id, weapons.Digest,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.resource-coordinate", "CWeaponInfoBlob",
                "common.rpf!/data/ai/not-weapons.meta", "grid.gta-v.resource-coordinate.exact-utf8", 1),
            weapons.DeclaredFormat, weapons.ExactBytes, weapons.ObservedAtUtc);
        var rejected = false;
        try { _ = index.GetXmlDocument(relabeled, GtaVWeaponsMetaKnowledgeAdapter.MaximumArtifactBytes); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "A byte-identical artifact with a relabeled coordinate cannot borrow an indexed parse.");

        var otherGxt = Frozen(
            "update/update.rpf!/x64/data/lang/american_rel.rpf!/global.gxt2", "GXT2",
            gxt.DeclaredFormat, CreateGxt2((labelHash, "Different resource text")), observed);
        var collisionIndex = new GtaVSupportedSourceCorpusIndex([gxt, otherGxt]);
        Assert(collisionIndex.GetGxt2(gxt, 1024 * 1024)[labelHash].Text == "Pistol" &&
               collisionIndex.GetGxt2(otherGxt, 1024 * 1024)[labelHash].Text == "Different resource text",
            "Equal GXT2 hashes remain resource-scoped and cannot overwrite one another.");

        return checks;
    }

    private static IEnumerable<string> SemanticIds(GtaVSecondaryAssertionBatch batch) =>
        batch.TerminologyAssertions.Select(value => EvidenceClaimContentId.DeriveV1(value).Value)
            .Concat(batch.SemanticClassifications.Select(value => value.Id.Value))
            .Concat(batch.OrganizationalValues.Select(value => value.Id.Value))
            .Concat(batch.FileEvidenceReceipts.Select(value => value.Id.Value))
            .Concat(batch.EvidenceBindings.Select(value => value.Id.Value))
            .Concat(batch.TargetLinkClaims.Select(value => value.Id.Value))
            .Concat(batch.CrossSourceAssertions.Select(value => value.Id.Value));

    private static FrozenSourceArtifact Frozen(
        string coordinate,
        string objectType,
        KnowledgeFormatCoordinate format,
        byte[] bytes,
        DateTimeOffset observed)
    {
        var exact = bytes.ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(exact.AsSpan());
        return new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.resource-coordinate", objectType, coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8", 1),
            format, exact, observed);
    }

    private static byte[] CreateGxt2(params (uint Hash, string Text)[] entries)
    {
        var ordered = entries.OrderBy(value => value.Hash).ToArray();
        var encoded = ordered.Select(value => Encoding.UTF8.GetBytes(value.Text)).ToArray();
        var tableEnd = 8 + ordered.Length * 8;
        var cursor = tableEnd + 8;
        var result = new byte[cursor + encoded.Sum(value => value.Length + 1)];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8 + index * 8), ordered[index].Hash);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12 + index * 8), (uint)cursor);
            encoded[index].CopyTo(result.AsSpan(cursor));
            cursor += encoded[index].Length + 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd), 0x47585432);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tableEnd + 4), (uint)cursor);
        return result;
    }

    private static uint Joaat(string value)
    {
        uint hash = 0;
        foreach (var character in value)
        {
            var lowered = character is >= 'A' and <= 'Z' ? (byte)(character + 32) : (byte)character;
            hash += lowered;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }
}
