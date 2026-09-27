using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

internal static class GtaVMountedActorRegistrationChecks
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var fixture = CreateFixture("actor-fixture-a");
        var index = GtaVActorCorpusIndex.Load(fixture.Sidecar, fixture.Artifacts, fixture.Binding);
        Assert(index.ResidentCandidates.Length == 2 && index.Resident.Records.Length == 683 &&
               index.DlcPacks.Length == 25 && index.DlcPacks.Sum(value => value.Records.Length) == 435,
            "The receipt-bound Actor index closes over the exact two-candidate, 25-pack, 1,118-row corpus.");

        var digest = ContentDigest.ComputeSha256("mounted-actor-adapter-fixture"u8);
        var residentAdapter = new GtaVResidentPedsKnowledgeAdapter(digest, index);
        var dlcAdapter = new GtaVMountedDlcPedsKnowledgeAdapter(digest, index);
        var gen9Adapter = new GtaVGen9PedsKnowledgeAdapter(digest);
        var version = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id", "25261616");
        var residentExtraction = await Extract(residentAdapter, version, [fixture.ResidentEffective]);
        var dlcExtraction = await Extract(dlcAdapter, version, fixture.DlcPedsArtifacts);
        var gen9Extraction = await Extract(gen9Adapter, version, [fixture.Gen9Artifact]);
        var acquisition = CreateAcquisition(
            version,
            fixture.DlcClosureArtifacts.Append(fixture.ResidentEffective).Append(fixture.Gen9Artifact)
                .DistinctBy(value => value.Id).ToImmutableArray());
        var origin = GtaVKnowledgePackageProjection.CreatePayload(
            [residentExtraction, dlcExtraction, gen9Extraction], acquisition.Receipts, acquisition.Bindings);
        var historicalDlc = new GtaVActorDlcSecondaryAssertionAdapter(digest).Extract(
            origin,
            KnowledgeSourceScope.BaseGame(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            fixture.Gen9Artifact);
        var gen9Ids = gen9Extraction.CanonicalRegistrations.SelectMany(value =>
            value.Registration.KnowledgeRecords).Select(value => value.Id).ToImmutableHashSet();
        Assert(historicalDlc.OrganizationalValues.Length == 2 &&
               historicalDlc.OrganizationalValues.All(value => gen9Ids.Contains(value.KnowledgeRecordId)),
            "The historical Gen9 DLC adapter excludes every resident and mounted-DLC Actor identity.");
        var historicalAmbient = new GtaVAmbientPedRoleSecondaryAssertionAdapter(digest).Extract(
            origin,
            KnowledgeSourceScope.BaseGame(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version),
            fixture.Gen9Artifact,
            fixture.AmbientArtifact);
        Assert(historicalAmbient.SemanticClassifications.Length == 2 &&
               historicalAmbient.SemanticClassifications.All(value =>
                   gen9Ids.Contains(value.KnowledgeRecordId) &&
                   value.RoleId == CanonicalProjectionSemantics.ActorNpc),
            "The historical ambient-role adapter excludes every resident and mounted-DLC Actor identity.");

        var residentIds = residentExtraction.CanonicalRegistrations.SelectMany(value =>
            value.Registration.KnowledgeRecords).Select(value => value.Id).ToImmutableHashSet();
        var dlcIds = dlcExtraction.CanonicalRegistrations.SelectMany(value =>
            value.Registration.KnowledgeRecords).Select(value => value.Id).ToImmutableHashSet();
        Assert(residentIds.Count == 683 && dlcIds.Count == 435 && !residentIds.Overlaps(dlcIds),
            "Resident hash identities and mounted-DLC UTF-8 identities are independently preserved without merging.");

        var scope = KnowledgeSourceScope.BaseGame(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version);
        var secondaryAdapter = new GtaVMountedActorSecondaryAssertionAdapter(digest, index);
        var secondary = secondaryAdapter.Extract(
            origin, scope, fixture.ResidentEffective, fixture.DlcClosureArtifacts, fixture.Gen9Artifact);
        var payload = secondary.Batches.Aggregate(origin, (current, batch) =>
            GtaVKnowledgePackageProjection.AddSecondaryAssertions(
                current, batch, acquisition.Receipts, acquisition.Bindings));

        Assert(secondary.Metrics.ResidentRecordCount == 683 && secondary.Metrics.DlcRecordCount == 435 &&
               secondary.Metrics.PlayerCharacterClassificationCount == 6 &&
               secondary.Metrics.NpcClassificationCount == 1_110 &&
               secondary.Metrics.UnclassifiedRecordCount == 2 &&
               secondary.Metrics.DlcOrganizationalValueCount == 435 &&
               secondary.Metrics.Gen9ResidentCorrelationCount == 2,
            "Only exact mapped Pedtype hashes/text produce roles; zero, unknown, and misspelled values remain unclassified.");
        Assert(payload.SemanticClassificationAssertions.Count(value =>
                   value.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter) == 6 &&
               payload.SemanticClassificationAssertions.Count(value =>
                   value.RoleId == CanonicalProjectionSemantics.ActorNpc) == 1_110,
            "All and only the exact Player Character and NPC Pedtype mappings are registered.");
        Assert(payload.TerminologyAssertions.IsEmpty && payload.RelationshipAssertions.IsEmpty &&
               payload.OrganizationalValueAssertions.Length == 435 &&
               payload.OrganizationalValueAssertions.All(value =>
                   value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode) &&
               payload.OrganizationalValueAssertions.All(value =>
                   !value.ExactValueIdentity.ObjectType.Contains("Faction", StringComparison.Ordinal)),
            "The mounted Actor slice emits no terminology, faction, or relationship claims and only exact DLC organization.");
        Assert(payload.CorrelationEnvelopes.Length == 2 &&
               payload.CorrelationEnvelopes.All(value => value.Record.MemberIds.Length == 2) &&
               payload.KnowledgeRecords.Length == 1_120,
            "Two exact Gen9-to-resident JOAAT correlations preserve all 1,120 independent Actor identities.");
        Assert(secondary.Batches.Skip(1).All(value =>
                   value.AdditionalArtifacts.Length == 3 &&
                   value.SourceRevision.Revision.ArtifactIds.Length == 4) &&
               secondary.Batches.Skip(1).All(value => value.FileEvidenceReceipts.Any(receipt =>
                   receipt.Receipt.SourceFieldPath.Contains(
                       "/SMandatoryPacksData[1]/Paths[1]/*[", StringComparison.Ordinal))),
            "Each DLC assertion revision retains dlclist, peds, setup, content, and exact mount-occurrence evidence.");

        var package = CanonicalCatalogPackageKernel.CreateV6(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version,
                payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
            null, [], "grid.catalog-composition.v1", payload,
            new CatalogValidationSummary(
                CatalogValidationStatus.Candidate, "grid.gta-v.mounted-actor-tests", "1",
                ContentDigest.ComputeSha256("mounted-actor-candidate"u8)),
            new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.gta-v.mounted-actor-tests", "1", new string('a', 40),
                [new CatalogCommittedBuildInput(
                    "src/Grid.GtaV.Knowledge/GtaVMountedActorSecondaryAssertionAdapter.cs",
                    new string('b', 40))]));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        Assert(verification.IsStructurallyValid,
            "The full mounted Actor identity/classification/DLC/correlation evidence graph is structurally valid.");

        var reversedIndex = GtaVActorCorpusIndex.Load(
            fixture.Sidecar, fixture.Artifacts.Reverse(), fixture.Binding);
        var reversedAdapter = new GtaVMountedActorSecondaryAssertionAdapter(digest, reversedIndex);
        var oldCulture = CultureInfo.CurrentCulture;
        var oldUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            var replay = reversedAdapter.Extract(
                origin, scope, fixture.ResidentEffective,
                fixture.DlcClosureArtifacts.Reverse().ToImmutableArray(), fixture.Gen9Artifact);
            Assert(secondary.Batches.Select(value => value.SourceRevision.Revision.Id)
                       .SequenceEqual(replay.Batches.Select(value => value.SourceRevision.Revision.Id)) &&
                   secondary.Batches.SelectMany(value => value.SemanticClassifications).Select(value => value.Id)
                       .SequenceEqual(replay.Batches.SelectMany(value => value.SemanticClassifications).Select(value => value.Id)) &&
                   secondary.Batches.SelectMany(value => value.OrganizationalValues).Select(value => value.Id)
                       .SequenceEqual(replay.Batches.SelectMany(value => value.OrganizationalValues).Select(value => value.Id)) &&
                   secondary.Batches.SelectMany(value => value.CorrelationEnvelopes).Select(value => value.Id)
                       .SequenceEqual(replay.Batches.SelectMany(value => value.CorrelationEnvelopes).Select(value => value.Id)),
                "Actor registration is invariant to artifact enumeration and current culture.");
        }
        finally
        {
            CultureInfo.CurrentCulture = oldCulture;
            CultureInfo.CurrentUICulture = oldUiCulture;
        }

        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            ReplaceExact(fixture.Sidecar, "0x1000000B", "0x1000000C"), fixture.Artifacts, fixture.Binding));
        checks++;
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            ReplaceExact(fixture.Sidecar, "0x0CDC5452", "0x0CDC5453"), fixture.Artifacts, fixture.Binding));
        checks++;
        var changedLocator = ReplaceExact(
            fixture.Sidecar,
            "#/CPedModelInfo__InitDataList/InitDatas/Item[1]/Name",
            "#/CPedModelInfo__InitDataList/InitDatas/Item[2]/Name");
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            changedLocator, fixture.Artifacts, Binding(changedLocator)));
        checks++;
        var tagSpecificDlcLocator = ReplaceExact(
            fixture.Sidecar,
            "/SMandatoryPacksData[1]/Paths[1]/*[1]",
            "/SMandatoryPacksData[1]/Paths[1]/Item[1]");
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            tagSpecificDlcLocator, fixture.Artifacts, Binding(tagSpecificDlcLocator)));
        checks++;
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            fixture.Sidecar, fixture.Artifacts.RemoveAt(fixture.Artifacts.Length - 1), fixture.Binding));
        checks++;
        var originalArtifact = fixture.Artifacts[0];
        var alteredArtifact = Frozen(
            originalArtifact.SourceCoordinate.ExactRepresentation,
            originalArtifact.SourceCoordinate.ObjectType,
            originalArtifact.DeclaredFormat.FormatId,
            Encoding.UTF8.GetBytes("altered exact Rockstar bytes"));
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(
            fixture.Sidecar, fixture.Artifacts.SetItem(0, alteredArtifact), fixture.Binding));
        checks++;
        var other = CreateFixture("actor-fixture-b");
        ExpectInvalid(() => GtaVActorCorpusIndex.Load(other.Sidecar, other.Artifacts, fixture.Binding));
        checks++;

        Console.WriteLine("PASS  GTA V mounted Actor corpus, role, DLC, and correlation registration.");
        return checks;
    }

    private static async Task<KnowledgeExtractionResult> Extract(
        IGameKnowledgeAdapter adapter,
        SourceNativeVersion version,
        ImmutableArray<FrozenSourceArtifact> artifacts) =>
        await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, adapter.Descriptor, artifacts));

    private static Fixture CreateFixture(string seed)
    {
        var baseResident = Frozen(
            "x64a.rpf!/data/peds.ymt", "CPedModelInfo__InitDataListPso",
            GtaVActorCorpusIndex.ResidentFormatId, Encoding.UTF8.GetBytes(seed + "-resident-base"));
        var updateResident = Frozen(
            "update/update.rpf!/x64/data/peds.ymt", "CPedModelInfo__InitDataListPso",
            GtaVActorCorpusIndex.ResidentFormatId, Encoding.UTF8.GetBytes(seed + "-resident-update"));
        var artifacts = ImmutableArray.CreateBuilder<FrozenSourceArtifact>();
        artifacts.Add(baseResident);
        artifacts.Add(updateResident);
        var dlcList = Frozen(
            "update/update.rpf!/common/data/dlclist.xml", "MountedActorSource",
            GtaVActorCorpusIndex.DlcListFormatId, Encoding.UTF8.GetBytes(seed + "-dlclist"));
        artifacts.Add(dlcList);
        var dlcPeds = ImmutableArray.CreateBuilder<FrozenSourceArtifact>();
        var dlcClosure = ImmutableArray.CreateBuilder<FrozenSourceArtifact>();
        dlcClosure.Add(dlcList);
        var dlcRows = new List<object>();
        var dlcRecordOrdinal = 0;
        var exactDlcPedtypes = new[]
        {
            "PLAYER_0", "PLAYER_1", "PLAYER_2", "CIVMALE", "CIVFEMALE", "ANIMAL",
            "COP", "ARMY", "MEDIC", "FIREMAN", "SWAT"
        };
        for (var pack = 0; pack < 25; pack++)
        {
            var packName = $"pack{pack:D2}";
            var container = $"update/x64/dlcpacks/{packName}/dlc.rpf";
            var setup = Frozen(container + "!/setup2.xml", "MountedActorSource",
                GtaVActorCorpusIndex.DlcSetupFormatId, Encoding.UTF8.GetBytes(seed + $"-setup-{pack:D2}"));
            var content = Frozen(container + "!/content.xml", "MountedActorSource",
                GtaVActorCorpusIndex.DlcContentFormatId, Encoding.UTF8.GetBytes(seed + $"-content-{pack:D2}"));
            var peds = Frozen(container + "!/common/data/peds.meta", "CPedModelInfo__InitDataListXml",
                GtaVActorCorpusIndex.DlcXmlFormatId, Encoding.UTF8.GetBytes(seed + $"-peds-{pack:D2}"));
            artifacts.Add(setup); artifacts.Add(content); artifacts.Add(peds);
            dlcPeds.Add(peds); dlcClosure.Add(setup); dlcClosure.Add(content); dlcClosure.Add(peds);
            var recordCount = pack < 10 ? 18 : 17;
            var rows = new List<object>();
            for (var row = 0; row < recordCount; row++, dlcRecordOrdinal++)
            {
                var path = $"rpf7-member:{peds.SourceCoordinate.ExactRepresentation}#" +
                    $"/CPedModelInfo__InitDataList[1]/InitDatas[1]/Item[{row + 1}]";
                var pedtype = dlcRecordOrdinal < exactDlcPedtypes.Length
                    ? exactDlcPedtypes[dlcRecordOrdinal]
                    : dlcRecordOrdinal == 434 ? "CivMale" : "CIVMALE";
                rows.Add(new
                {
                    ordinal = row,
                    nameExact = $"dlc_actor_{pack:D2}_{row:D2}",
                    pedtypeExact = pedtype,
                    recordLocator = path,
                    nameFieldLocator = path + "/Name[1]",
                    pedtypeFieldLocator = path + "/Pedtype[1]",
                });
            }
            dlcRows.Add(new
            {
                packNameExact = packName,
                dlclistOccurrenceOrdinals = new[] { pack },
                dlclistOccurrences = new[]
                {
                    new
                    {
                        ordinal = pack,
                        rawMountPath = $"dlcpacks:/{packName}/",
                        fieldLocator = $"rpf7-member:{dlcList.SourceCoordinate.ExactRepresentation}#" +
                            $"/SMandatoryPacksData[1]/Paths[1]/*[{pack + 1}]",
                    }
                },
                normalizedMountPath = $"dlcpacks:/{packName}/",
                physicalContainerCoordinate = container,
                setupArtifact = Artifact(setup, 0, includeTier: false),
                contentArtifact = Artifact(content, 0, includeTier: false),
                pedMetadataDeclarationLocator =
                    $"rpf7-member:{content.SourceCoordinate.ExactRepresentation}#" +
                    "/CDataFileMgr__ContentsOfDataFileXml[1]/dataFiles[1]/Item[1]",
                pedsArtifact = Artifact(peds, 0, includeTier: false),
                records = rows,
            });
        }

        var residentRows = new List<object>();
        var gen9Names = new[] { "mp_m_freemode_01", "mp_f_freemode_01" };
        var residentNames = new HashSet<uint> { Joaat(gen9Names[0]), Joaat(gen9Names[1]) };
        for (uint candidate = 0x10000000; residentNames.Count < 683; candidate++) residentNames.Add(candidate);
        var exactHashPedtypes = new uint[]
        {
            0x0CDC5452, 0xBEA537E5, 0x7AF2B04D, 0x02B8FA80, 0x47033600, 0x3A0EB4FD,
            0xA49E591C, 0xE3D976F3, 0xB0423AA0, 0xFC2CA767, 0x98787966
        };
        var residentOrdinal = 0;
        foreach (var nameHash in residentNames.Order())
        {
            var path = $"pso:{updateResident.SourceCoordinate.ExactRepresentation}#" +
                $"/CPedModelInfo__InitDataList/InitDatas/Item[{residentOrdinal + 1}]";
            var pedtype = residentOrdinal < exactHashPedtypes.Length
                ? exactHashPedtypes[residentOrdinal]
                : residentOrdinal == 682 ? 0u : 0x02B8FA80;
            residentRows.Add(new
            {
                ordinal = residentOrdinal,
                nameHash = $"0x{nameHash:X8}",
                pedtypeHash = $"0x{pedtype:X8}",
                recordLocator = path,
                nameFieldLocator = path + "/Name",
                pedtypeFieldLocator = path + "/Pedtype",
            });
            residentOrdinal++;
        }

        var document = new
        {
            schemaId = GtaVActorCorpusIndex.SchemaId,
            schemaVersion = GtaVActorCorpusIndex.SchemaVersion,
            decoder = new
            {
                methodId = GtaVActorCorpusIndex.DecoderMethodId,
                exactVersion = GtaVActorCorpusIndex.DecoderVersion,
                artifactSha256 = GtaVActorCorpusIndex.ApprovedDecoderArtifactSha256,
            },
            resident = new
            {
                effectiveArtifact = Artifact(updateResident, 2, includeTier: true),
                candidateArtifacts = new[]
                {
                    Artifact(updateResident, 2, includeTier: true),
                    Artifact(baseResident, 3, includeTier: true),
                },
                records = residentRows,
            },
            dlcListArtifact = Artifact(dlcList, 0, includeTier: false),
            dlcPacks = dlcRows,
        };
        var sidecar = JsonSerializer.SerializeToUtf8Bytes(document);
        var gen9Artifact = Frozen(
            "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta",
            "Gen9ExclusiveAssetsDataPeds", GtaVGen9PedsKnowledgeAdapter.FormatId,
            Encoding.UTF8.GetBytes("<Gen9ExclusiveAssetsDataPeds><PedData>" +
                string.Concat(gen9Names.Select((value, ordinal) =>
                    $"<Item><PedModelName>{value}</PedModelName><DLCData><Item><dlcName>fixture_dlc_{ordinal}</dlcName></Item></DLCData></Item>")) +
                "</PedData></Gen9ExclusiveAssetsDataPeds>"));
        var ambientArtifact = Frozen(
            GtaVAmbientPedRoleSecondaryAssertionAdapter.Coordinate,
            "CAmbientModelSets",
            GtaVAmbientPedRoleSecondaryAssertionAdapter.FormatId,
            Encoding.UTF8.GetBytes(
                "<CAmbientModelSets><ModelSets><Item><Name>fixture_set</Name><Models>" +
                string.Concat(gen9Names.Select(value => $"<Item><Name>{value}</Name></Item>")) +
                "</Models></Item></ModelSets></CAmbientModelSets>"));
        return new Fixture(
            sidecar, Binding(sidecar), artifacts.ToImmutable(), updateResident,
            dlcPeds.ToImmutable(), dlcClosure.ToImmutable(), gen9Artifact, ambientArtifact);
    }

    private static object Artifact(FrozenSourceArtifact artifact, int sourceTier, bool includeTier) =>
        includeTier
            ? new
            {
                sourceCoordinate = artifact.SourceCoordinate.ExactRepresentation,
                byteLength = artifact.ExactBytes.Length,
                sha256 = artifact.Digest.HexValue,
                formatId = artifact.DeclaredFormat.FormatId,
                formatVersion = artifact.DeclaredFormat.ExactFormatVersion,
                sourceTier,
            }
            : new
            {
                sourceCoordinate = artifact.SourceCoordinate.ExactRepresentation,
                byteLength = artifact.ExactBytes.Length,
                sha256 = artifact.Digest.HexValue,
                formatId = artifact.DeclaredFormat.FormatId,
                formatVersion = artifact.DeclaredFormat.ExactFormatVersion,
            };

    private static GtaVActorCorpusIndexReceiptBinding Binding(byte[] sidecar) => new(
        GtaVActorCorpusIndex.SchemaId,
        GtaVActorCorpusIndex.SchemaVersion,
        GtaVActorCorpusIndex.DecoderMethodId,
        GtaVActorCorpusIndex.DecoderVersion,
        GtaVActorCorpusIndex.ApprovedDecoderArtifactSha256,
        sidecar.LongLength,
        ContentDigest.ComputeSha256(sidecar));

    private static AcquisitionFixture CreateAcquisition(
        SourceNativeVersion version,
        ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        var method = new AcquisitionMethodCoordinate(
            "grid.gta-v-enhanced.actor-mounted-ped-acquisition", "1",
            "fivefury.rpf-read+ped-metadata", "0.5.1",
            new ContentDigest(ContentDigest.Sha256Algorithm,
                GtaVActorCorpusIndex.ApprovedDecoderArtifactSha256));
        var app = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam", "Application", "3240220", "valve.steam.app-id.exact-utf8", 1);
        var container = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.container-coordinate", "Rpf7Container", "actor-fixture.rpf",
            "grid.gta-v.container-coordinate.exact-utf8", 1);
        var containerDigest = ContentDigest.ComputeSha256("actor-fixture-container"u8);
        var members = artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .Select(value => new SourceAcquisitionMember(
                value.SourceCoordinate, value.ExactBytes.Length, value.Digest, value.Id))
            .ToImmutableArray();
        var id = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            app, version, container, 1_000_000, containerDigest, method, members);
        var receipt = new SourceAcquisitionReceipt(
            id, SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            app, version, container, 1_000_000, containerDigest, method, members);
        return new AcquisitionFixture(
            [receipt],
            members.Select(value => new SourceArtifactAcquisitionBinding(
                value.ArtifactId, receipt.Id, value.MemberCoordinate, value.ByteLength, value.Digest))
                .ToImmutableArray());
    }

    private static FrozenSourceArtifact Frozen(
        string coordinate, string objectType, string formatId, byte[] bytes)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        return new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(digest), digest,
            SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.resource-coordinate", objectType, coordinate,
                "grid.gta-v.resource-coordinate.exact-utf8", 1),
            new KnowledgeFormatCoordinate(formatId, "1"), bytes.ToImmutableArray(), ObservedAt);
    }

    private static byte[] ReplaceExact(byte[] value, string before, string after)
    {
        var text = Encoding.UTF8.GetString(value);
        var index = text.IndexOf(before, StringComparison.Ordinal);
        if (index < 0) throw new InvalidOperationException("Fixture replacement target absent: " + before);
        return Encoding.UTF8.GetBytes(text[..index] + after + text[(index + before.Length)..]);
    }

    private static void ExpectInvalid(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Expected an InvalidDataException.");
    }

    private static uint Joaat(string value)
    {
        uint hash = 0;
        foreach (var character in value)
        {
            hash += character is >= 'A' and <= 'Z' ? (byte)(character + 32) : (byte)character;
            hash += hash << 10;
            hash ^= hash >> 6;
        }
        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;
        return hash;
    }

    private sealed record Fixture(
        byte[] Sidecar,
        GtaVActorCorpusIndexReceiptBinding Binding,
        ImmutableArray<FrozenSourceArtifact> Artifacts,
        FrozenSourceArtifact ResidentEffective,
        ImmutableArray<FrozenSourceArtifact> DlcPedsArtifacts,
        ImmutableArray<FrozenSourceArtifact> DlcClosureArtifacts,
        FrozenSourceArtifact Gen9Artifact,
        FrozenSourceArtifact AmbientArtifact);

    private sealed record AcquisitionFixture(
        ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);
}
