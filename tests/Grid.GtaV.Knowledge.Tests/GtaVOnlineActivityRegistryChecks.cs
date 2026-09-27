using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVOnlineActivityRegistryChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var digest = ContentDigest.ComputeSha256("online-activity-registry-adapter"u8);
        var adapter = new GtaVOnlineActivityRegistryKnowledgeAdapter(digest);
        var version = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id", "25261616");
        var artifacts = ImmutableArray.Create(
            Frozen(adapter, "common.rpf!/data/ugc/generated.ugc",
                """{"mission":{"gen":{"nm":"Learning The Ropes"}}}"""),
            Frozen(adapter, "common.rpf!/data/ugc/playlist.ugc",
                """{"name":"Venture Off Road","list":[{"cid":"activity-a","name":"Just Deserts","type":2},{"cid":"activity-b","name":"Power Trip","type":2}]}"""),
            Frozen(adapter, "common.rpf!/data/ugc/rockstarplaylists_00.ugc",
                """{"list":[{"cid":"playlist-a","name":"Venture Off Road"}]}"""));

        var extraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, adapter.Descriptor, artifacts));
        var payload = GtaVKnowledgePackageProjection.CreatePayload(extraction);
        Assert(payload.KnowledgeRecords.Length == 4 &&
               payload.KnowledgeRecords.All(value => value.Kind == KnowledgeKind.MissionQuest),
            "The complete supported registry fixtures are registered in one batch.");
        Assert(payload.TerminologyAssertions.Select(value => value.VerbatimValue)
                .Order(StringComparer.Ordinal).SequenceEqual(
                    new[] { "Just Deserts", "Learning The Ropes", "Power Trip", "Venture Off Road" }
                        .Order(StringComparer.Ordinal)) &&
               payload.TerminologyAssertions.All(value =>
                   value.Role == TerminologyAssertionRole.PrimaryName && value.LanguageTag == "und"),
            "Exact Rockstar title fields become verbatim primary terminology without prettification.");
        Assert(payload.SemanticClassificationAssertions.Length == 4 &&
               payload.SemanticClassificationAssertions.All(value =>
                   value.RoleId == CanonicalProjectionSemantics.MissionOnline),
            "Every supported shipped activity-registry object has an evidence-bound Online classification.");
        Assert(payload.OrganizationalValueAssertions.Length == 2 &&
               payload.OrganizationalValueAssertions.All(value =>
                   value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode &&
                   value.VerbatimDisplayValue == "Venture Off Road"),
            "Playlist entries retain their exact source-provided Rockstar playlist organization.");
        Assert(payload.EvidenceBindings.Count(value => value.ClaimKind == EvidenceClaimKind.Terminology) == 4 &&
               payload.EvidenceBindings.Count(value => value.ClaimKind == EvidenceClaimKind.SemanticClassification) == 4 &&
               payload.EvidenceBindings.Count(value => value.ClaimKind == EvidenceClaimKind.OrganizationalValue) == 2 &&
               payload.EvidenceBindings.Where(value => value.ClaimKind != EvidenceClaimKind.KnowledgeIdentity)
                   .All(value => value.ClaimContentId is not null),
            "Every title, classification, and playlist value has exact content-bound FILE_VERIFIED evidence.");

        var indexedArtifacts = artifacts.Add(Frozen(adapter, "update/update2.rpf!/common/data/ugc/generated.ugc",
            """{"mission":{"gen":{"nm":"Indexed Activity"}}}"""));
        var corpusIndex = new GtaVSupportedSourceCorpusIndex(indexedArtifacts);
        var indexedRegistry = new GtaVOnlineActivityRegistryKnowledgeAdapter(digest, corpusIndex);
        var indexedLegacy = new GtaVUgcMissionKnowledgeAdapter(digest, corpusIndex);
        var sharedArtifact = indexedArtifacts[^1];
        var indexedRegistryResult = await indexedRegistry.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, indexedRegistry.Descriptor, [sharedArtifact]));
        var indexedLegacyResult = await indexedLegacy.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, indexedLegacy.Descriptor, [sharedArtifact]));
        Assert(indexedRegistryResult.CanonicalRegistrations.Length == 1 &&
               indexedLegacyResult.UnresolvedSourceAssertions.Length == 1 &&
               corpusIndex.GetPhysicalParseCount(sharedArtifact, "strict-utf8") == 1,
            "The complete UGC adapter batch shares one strict-UTF8 corpus parse per exact artifact.");

        var reversed = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, adapter.Descriptor, artifacts.Reverse().ToImmutableArray()));
        var replay = GtaVKnowledgePackageProjection.CreatePayload(reversed);
        Assert(payload.KnowledgeRecords.Select(value => value.Id).SequenceEqual(
                   replay.KnowledgeRecords.Select(value => value.Id)) &&
               payload.TerminologyAssertions.Select(EvidenceClaimContentId.DeriveV1).SequenceEqual(
                   replay.TerminologyAssertions.Select(EvidenceClaimContentId.DeriveV1)),
            "Registry extraction is independent of artifact enumeration order.");

        var legacy = new GtaVUgcMissionKnowledgeAdapter(digest);
        var fmnmArtifact = Frozen(legacy, "update/update2.rpf!/common/data/ugc/native.ugc",
            """{"mission":{"fmnm":"Arms_Trafficking_Cargobob_0"}}""");
        var legacyResult = await legacy.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version, null, null, legacy.Descriptor, [fmnmArtifact]));
        var legacyId = legacyResult.CanonicalRegistrations.Single().Registration.KnowledgeRecords.Single().Id;
        var registryDiscovery = await adapter.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId, version, null, null,
            [Frozen(adapter, "update/update2.rpf!/common/data/ugc/native.ugc",
                """{"mission":{"fmnm":"Arms_Trafficking_Cargobob_0"}}""")]));
        Assert(registryDiscovery.SourceCandidates.IsEmpty && legacyId ==
               legacyResult.CanonicalRegistrations.Single().Registration.KnowledgeRecords.Single().Id,
            "The new family does not guess a title or regenerate an established fmnm canonical identity.");

        Console.WriteLine("PASS  GTA V Online activity-registry source-family adapter.");
        return checks;
    }

    private static FrozenSourceArtifact Frozen(
        GtaVEnhancedKnowledgeAdapterBase adapter,
        string coordinate,
        string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json).ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(bytes.AsSpan());
        return new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(digest), digest,
            adapter.CreateSourceCoordinate(coordinate),
            adapter.Format,
            bytes,
            new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero));
    }
}
