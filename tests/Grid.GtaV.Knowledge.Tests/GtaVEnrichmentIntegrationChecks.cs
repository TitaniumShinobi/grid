using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

internal static class GtaVEnrichmentIntegrationChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var observed = new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero);
        var oldDigest = ContentDigest.ComputeSha256("gta-origin-fixture"u8);
        var enrichmentDigest = ContentDigest.ComputeSha256("gta-enrichment-fixture"u8);
        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "fixture-build");
        var scope = KnowledgeSourceScope.BaseGame(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);

        var mapAdapter = new GtaVMapZonesKnowledgeAdapter(oldDigest);
        var populationAdapter = new GtaVPopulationZonesKnowledgeAdapter(enrichmentDigest);
        var actorAdapter = new GtaVGen9PedsKnowledgeAdapter(oldDigest);
        var terminologyAdapter = new GtaVPopulationZoneTerminologyAdapter(enrichmentDigest);
        var ambientAdapter = new GtaVAmbientPedRoleSecondaryAssertionAdapter(enrichmentDigest);
        var map = Frozen(
            "common.rpf!/data/levels/gta5/mapzones.xml", "CMapZonesContainer", mapAdapter.Format,
            Encoding.UTF8.GetBytes("<CMapZonesContainer><Zones><Item><Name>MAP_ONLY</Name><ZoneAreas><Item /></ZoneAreas><BoundBox /></Item></Zones></CMapZonesContainer>"),
            observed);
        var population = Frozen(
            GtaVPopulationZonesKnowledgeAdapter.Coordinate, "PopulationZoneIplSection", populationAdapter.Format,
            Encoding.UTF8.GetBytes("zone\n1,0,0,0,1,1,1,LABEL_A,0\n2,1,1,1,2,2,2,LABEL_A,0\n3,2,2,2,3,3,3,LABEL_B,0\n4,3,3,3,4,4,4,label_a,0\nend\n"),
            observed);
        var actors = Frozen(
            "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta", "Gen9ExclusiveAssetsDataPeds",
            new KnowledgeFormatCoordinate(GtaVGen9PedsKnowledgeAdapter.FormatId, GtaVGen9PedsKnowledgeAdapter.ExactFormatVersion),
            Encoding.UTF8.GetBytes("<Gen9ExclusiveAssetsDataPeds><PedData><Item><PedModelName>mp_m_freemode_01</PedModelName><DLCData><Item><dlcName>mp_m_g9ec</dlcName></Item></DLCData></Item><Item><PedModelName>mp_f_freemode_01</PedModelName><DLCData><Item><dlcName>mp_f_g9ec</dlcName></Item></DLCData></Item></PedData></Gen9ExclusiveAssetsDataPeds>"),
            observed);
        var ambient = Frozen(
            GtaVAmbientPedRoleSecondaryAssertionAdapter.Coordinate, "CAmbientModelSets", ambientAdapter.AmbientFormat,
            Encoding.UTF8.GetBytes("<CAmbientModelSets><ModelSets><Item><Name>exact_source_set</Name><Models><Item><Name>mp_m_freemode_01</Name></Item><Item><Name>mp_f_freemode_01</Name></Item></Models></Item></ModelSets></CAmbientModelSets>"),
            observed);
        var languageRpf = Frozen(
            GtaVPopulationZoneTerminologyAdapter.BaseLanguageRpfCoordinate, "Rpf7Container",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId, GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion),
            "exact language rpf fixture bytes"u8.ToArray(), observed);
        var gxt = Frozen(
            GtaVPopulationZoneTerminologyAdapter.BaseGxt2Coordinate, "GXT2",
            new KnowledgeFormatCoordinate(GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId, GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion),
            CreateGxt2((Joaat("LABEL_A"), "  Source Place!  ")), observed);
        var acquisition = CreateAcquisition(version, map, population, actors, ambient, languageRpf, gxt);

        var mapResult = await ExtractAsync(mapAdapter, [map], version);
        var populationResult = await ExtractAsync(populationAdapter, [population], version);
        var actorResult = await ExtractAsync(actorAdapter, [actors], version);
        var origin = GtaVKnowledgePackageProjection.CreatePayload(
            [mapResult, populationResult, actorResult], acquisition.Receipts, acquisition.Bindings);
        var populationRecords = populationResult.CanonicalRegistrations
            .SelectMany(value => value.Registration.KnowledgeRecords)
            .ToImmutableArray();
        Assert(populationRecords.Length == 3 &&
               populationRecords.Any(value => value.NativeIdentity.ExactRepresentation == "LABEL_A") &&
               populationRecords.Any(value => value.NativeIdentity.ExactRepresentation == "label_a"),
            "Repeated IPL rows share one exact identity while ordinal case-distinct labels remain distinct Locations.");
        var labelA = populationRecords.Single(value => value.NativeIdentity.ExactRepresentation == "LABEL_A");
        Assert(populationResult.CanonicalRegistrations.SelectMany(value => value.Registration.EvidenceBindings)
                   .Count(value => value.KnowledgeRecordId == labelA.Id &&
                                   value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity) == 2,
            "Every repeated geometry occurrence remains independently bound as identity evidence.");

        var terminology = terminologyAdapter.Extract(origin, scope, population, languageRpf, gxt);
        var withTerminology = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            origin, terminology, acquisition.Receipts, acquisition.Bindings);
        var actorRoles = ambientAdapter.Extract(withTerminology, scope, actors, ambient);
        var payload = GtaVEnrichmentCoverageProjection.ApplyConsolidatedLocationCoverage(
            GtaVKnowledgePackageProjection.AddSecondaryAssertions(
                withTerminology, actorRoles, acquisition.Receipts, acquisition.Bindings),
            scope);

        Assert(payload.TerminologyAssertions.Length == 2 &&
               payload.TerminologyAssertions.All(value =>
                   value is { VerbatimValue: "  Source Place!  ", LanguageTag: "en-US", Role: TerminologyAssertionRole.PrimaryName }) &&
               payload.TerminologyAssertions.Select(value => value.KnowledgeRecordId).Distinct().Count() == 2,
            "One exact case-folding JOAAT label may name two ordinal-distinct source identities without merging them.");
        Assert(payload.KnowledgeRecords.Any(value => value.Kind == KnowledgeKind.Location &&
                   value.NativeIdentity.ExactRepresentation == "LABEL_B") &&
               payload.TerminologyAssertions.All(value => payload.KnowledgeRecords.Single(record =>
                   record.Id == value.KnowledgeRecordId).NativeIdentity.ExactRepresentation != "LABEL_B"),
            "A population-zone Location without an exact label-table entry remains identifier-only.");
        Assert(payload.SemanticClassificationAssertions.Count(value =>
                   value.RoleId == CanonicalProjectionSemantics.ActorNpc) == 2,
            "Exact ambient-model membership classifies both established Actors as NPCs.");
        Assert(payload.LocationCoverageReports is [{ Status: LocationCoverageStatus.Partial }] &&
               payload.LocationCoverageReports[0].SourceFamilies.Length == 2 &&
               payload.LocationCoverageReports[0].Terminology is
                   { TotalRecordCount: 4, PrimaryNamedRecordCount: 2, IdentifierOnlyRecordCount: 2 } &&
               payload.LocationCoverageReports[0].Hierarchy.NotProvidedBySource,
            "Map-zone and population-zone coverage is one truthful Partial ledger with no invented hierarchy.");
        Assert(payload.KnowledgeRecords.Select(value => value.Id).SequenceEqual(
                origin.KnowledgeRecords.Select(value => value.Id)),
            "Secondary enrichment preserves every established canonical record identity.");
        Assert(actorRoles.SourceRevision.Revision.ArtifactIds.OrderBy(value => value.Value, StringComparer.Ordinal)
                .SequenceEqual(new[] { actors.Id, ambient.Id }.OrderBy(value => value.Value, StringComparer.Ordinal)),
            "Actor-role provenance retains both the actor origin and ambient membership artifacts.");

        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate, "grid.gta-v.enrichment.structural", "1",
            ContentDigest.ComputeSha256("candidate-enrichment"u8));
        var package = CanonicalCatalogPackageKernel.CreateV6(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version,
                payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
            null, [], "grid.catalog-composition.v1", payload, validation,
            new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.gta-v.enrichment.tests", "1", new string('a', 40),
                [new CatalogCommittedBuildInput(
                    "src/Grid.GtaV.Enrichment.Knowledge/GtaVPopulationZonesKnowledgeAdapter.cs",
                    new string('b', 40))]));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        Assert(verification.IsStructurallyValid,
            "The enriched payload must produce a structurally valid schema-v6 Candidate package: " +
            string.Join("; ", verification.Issues));
        Assert(CanonicalCatalogPackageKernel.CreateV6(
                package.Manifest.PackageKind, package.Manifest.GameScope, package.Manifest.ModScope,
                package.Manifest.RequiredBasePackageIds, package.Manifest.CompositionPolicyVersion,
                payload, validation, package.Manifest.BuildProvenance).Id == package.Id,
            "Repeated package construction from identical enriched evidence is deterministic.");

        foreach (var malformed in new[]
        {
            Encoding.UTF8.GetBytes("zones\n1,0,0,0,1,1,1,LABEL,0\nend\n"),
            Encoding.UTF8.GetBytes("zone\n1,0,0,0,1,1,LABEL,0\nend\n"),
            Encoding.UTF8.GetBytes("zone\n1,NaN,0,0,1,1,1,LABEL,0\nend\n"),
            new byte[] { (byte)'z', (byte)'o', (byte)'n', (byte)'e', (byte)'\n', 0xff, (byte)'\n' },
        })
        {
            var invalid = Frozen(
                GtaVPopulationZonesKnowledgeAdapter.Coordinate,
                "PopulationZoneIplSection",
                populationAdapter.Format,
                malformed,
                observed);
            var result = await ExtractRawAsync(populationAdapter, [invalid], version);
            Assert(result.CoverageState == KnowledgeCoverageState.Unsupported &&
                   result.CanonicalRegistrations.IsEmpty,
                "Malformed IPL framing, cardinality, numbers, and UTF-8 must fail closed without canonical output.");
        }

        var relabeledCoordinate = new FrozenSourceArtifact(
            population.Id,
            population.Digest,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.resource-coordinate",
                "RelabeledObjectType",
                GtaVPopulationZonesKnowledgeAdapter.Coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8",
                1),
            population.DeclaredFormat,
            population.ExactBytes,
            population.ObservedAtUtc);
        var relabeledResult = await ExtractRawAsync(populationAdapter, [relabeledCoordinate], version);
        Assert(relabeledResult.CoverageState == KnowledgeCoverageState.Unsupported &&
               relabeledResult.CanonicalRegistrations.IsEmpty,
            "A source artifact cannot retain its text while relabeling the complete coordinate identity.");

        var malformedGxt = Frozen(
            GtaVPopulationZoneTerminologyAdapter.BaseGxt2Coordinate,
            "GXT2",
            gxt.DeclaredFormat,
            new byte[16],
            observed);
        AssertThrows<InvalidDataException>(() =>
            terminologyAdapter.Extract(origin, scope, population, languageRpf, malformedGxt),
            "Malformed GXT2 must fail closed before terminology is emitted.");

        var malformedAmbient = Frozen(
            GtaVAmbientPedRoleSecondaryAssertionAdapter.Coordinate,
            "CAmbientModelSets",
            ambientAdapter.AmbientFormat,
            Encoding.UTF8.GetBytes("<CAmbientModelSets><ModelSets><Item><Name>set</Name></Item></ModelSets></CAmbientModelSets>"),
            observed);
        AssertThrows<InvalidDataException>(() =>
            ambientAdapter.Extract(withTerminology, scope, actors, malformedAmbient),
            "Malformed ambient-ped structure must fail closed before Actor classification is emitted.");

        Console.WriteLine("PASS  GTA V Enhanced canonical enrichment integration.");
        return checks;
    }

    private static async Task<KnowledgeExtractionResult> ExtractAsync(
        IGameKnowledgeAdapter adapter,
        ImmutableArray<FrozenSourceArtifact> artifacts,
        SourceNativeVersion version)
    {
        var result = await ExtractRawAsync(adapter, artifacts, version);
        if (result.CoverageState == KnowledgeCoverageState.Unsupported)
            throw new InvalidOperationException("Fixture extraction unexpectedly failed closed.");
        return result;
    }

    private static async Task<KnowledgeExtractionResult> ExtractRawAsync(
        IGameKnowledgeAdapter adapter,
        ImmutableArray<FrozenSourceArtifact> artifacts,
        SourceNativeVersion version)
    {
        var request = new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, adapter.Descriptor, artifacts);
        return await adapter.ExtractAsync(request);
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException("Check failed: " + message);
    }

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

    private static AcquisitionFixture CreateAcquisition(
        SourceNativeVersion version,
        FrozenSourceArtifact map,
        FrozenSourceArtifact population,
        FrozenSourceArtifact actors,
        FrozenSourceArtifact ambient,
        FrozenSourceArtifact languageRpf,
        FrozenSourceArtifact gxt)
    {
        var method = new AcquisitionMethodCoordinate(
            "grid.gta-v-enhanced.rpf-member-acquisition", "1", "fivefury.rpf-read", "0.5.1",
            ContentDigest.ComputeSha256("pinned-fivefury-fixture"u8));
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220");
        SourceAcquisitionReceipt Receipt(string container, params FrozenSourceArtifact[] members)
        {
            var coordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", container);
            var digest = ContentDigest.ComputeSha256(Encoding.UTF8.GetBytes("container:" + container));
            var values = members.Select(value => new SourceAcquisitionMember(
                value.SourceCoordinate, value.ExactBytes.Length, value.Digest, value.Id)).ToImmutableArray();
            var id = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, version, coordinate, 1024, digest, method, values);
            return new SourceAcquisitionReceipt(
                id, SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, version, coordinate, 1024, digest, method, values);
        }
        var receipts = ImmutableArray.Create(
            Receipt("common.rpf", map),
            Receipt("update/update.rpf", population, actors, ambient),
            Receipt("x64b.rpf", languageRpf, gxt));
        var bindings = receipts.SelectMany(receipt => receipt.Members.Select(member =>
            new SourceArtifactAcquisitionBinding(
                member.ArtifactId, receipt.Id, member.MemberCoordinate, member.ByteLength, member.Digest)))
            .ToImmutableArray();
        return new AcquisitionFixture(receipts, bindings);
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

    private sealed record AcquisitionFixture(
        ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);
}
