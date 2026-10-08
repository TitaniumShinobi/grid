using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVActorPresentationChecks
{
    public static Task<int> RunAsync()
    {
        int[] categories = [35, 37, 38, 39, 41, 42, 44, 46, 47, 48, 49, 50];
        var source = "char* func_167(int i)\n{\nswitch(i){" + string.Join("", categories.Select(x => $"case {x}: return \"CATEGORY_{x}\"; break;")) +
            "case 83: return \"ACTOR_LABEL\" /* GXT comment is not data */; break; case 84: return \"UNSAFE\"; break;}}\n" +
            "int func_861(int i)\n{\nswitch(i){case 83: return joaat(\"Exact_Model\"); break; case 84: if(i) return joaat(\"Guessed_Model\"); break;}}\n" +
            "void func_903(int i)\n{\nswitch(i){\n" + string.Join("\n", categories.Select(x => $"case {x}: func_905({(x == 35 ? 83 : 84)},1); break;")) + "\n}}";
        var rows = GtaVPresentationCorpusIndex.ParseActors(source);
        if (rows.Length != 1 || rows[0].Case != 83 || rows[0].ModelHash != GtaVPresentationCorpusIndex.Hash("exact_model") || rows[0].Category != 35 || rows[0].LabelKey != "ACTOR_LABEL")
            throw new InvalidOperationException("Actor parser must accept only immediate literal models and exact source category membership.");
        var html = string.Join("", Enumerable.Range(0, 23).Select(x => $"<tr><td>{x}</td><td>VC_CLASS_{(char)('A' + x)}</td></tr>"));
        var classes = GtaVPresentationCorpusIndex.ParseVehicleClasses(html);
        if (classes.Count != 23 || classes["VC_CLASS_A"].LabelKey != "VEH_CLASS_0") throw new InvalidOperationException("Vehicle class bridge must retain exact enum/numeric coordinates.");
        bool rejected = false;
        try { GtaVPresentationCorpusIndex.ParseVehicleClasses(html + "<tr><td>0</td><td>VC_CLASS_A</td></tr>"); } catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("Duplicate reference class rows must fail closed.");
        var fixture = GtaVPresentationFrozenFixture.Load();
        if (fixture is null) return Task.FromResult(3);
        var batch = new GtaVActorPresentationSecondaryAssertionAdapter(ContentDigest.ComputeSha256("actor-presentation-focused"u8), fixture.Index).Extract(fixture.Origin.Payload, fixture.Scope);
        if (batch.TerminologyAssertions.Length != 177 || batch.SemanticClassifications.Count(x => x.RoleId == CanonicalProjectionSemantics.ActorNamedCharacter) != 24 || batch.SemanticClassifications.Count(x => x.RoleId == CanonicalProjectionSemantics.ActorGenericType) != 150 || batch.SemanticClassifications.Count(x => x.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable) != 177)
            throw new InvalidOperationException("Frozen Actor source closure must yield 177 named eligible records / 24 named NPC / 150 generic types.");
        fixture.AssertMixed(batch);
        var payload = fixture.Apply(batch);
        if (!payload.KnowledgeRecords.SequenceEqual(fixture.Origin.Payload.KnowledgeRecords) || fixture.Origin.Payload.TerminologyAssertions.Any(x => !payload.TerminologyAssertions.Contains(x))) throw new InvalidOperationException("Actor presentation changed established identities/history.");
        var leaves = fixture.Project(payload, KnowledgeKind.Actor);
        if (leaves.Select(x => x.KnowledgeRecordId).Distinct().Count() != 177 || leaves.Any(x => x.DisplayKind != CanonicalNavigationDisplayKind.CanonicalTerminology)) throw new InvalidOperationException("Frozen Actor projection must expose 177 exact localized options.");
        Console.WriteLine("PASS  Frozen Actor presentation: 177 options, 3 players / 24 named NPC / 150 generic, mixed evidence closure.");
        return Task.FromResult(7);
    }
}

internal sealed record GtaVPresentationFrozenFixture(CanonicalCatalogPackage Origin, GtaVPresentationCorpusIndex Index, KnowledgeSourceScope Scope,
    ImmutableArray<SourceAcquisitionReceipt> AcquisitionReceipts, ImmutableArray<SourceArtifactAcquisitionBinding> AcquisitionBindings)
{
    private static GtaVPresentationFrozenFixture? cached;
    public static GtaVPresentationFrozenFixture? Load()
    {
        if (cached is not null) return cached;
        var root = Environment.GetEnvironmentVariable("GRID_GTA_PLAN2_FIXTURE_ROOT");
        var replay = Environment.GetEnvironmentVariable("GRID_GTA_PLAN2_REPLAY_ROOT");
        if (string.IsNullOrEmpty(root) && string.IsNullOrEmpty(replay)) return null;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(replay)) throw new InvalidDataException("Both bounded frozen fixture roots are required.");
        var package = JsonSerializer.Deserialize<CanonicalCatalogPackage>(File.ReadAllBytes(Path.Combine(replay, "registration", "canary-package.v7.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var bundle = Path.Combine(root, "presentation-bundle");
        var acquisition = GtaPresentationAcquisitionReceiptLoader.LoadAsync(Path.Combine(bundle, "gta-v-enhanced-presentation-acquisition-receipt.v1.json"),
            Path.Combine("scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_presentation_source_families.v1.json")).GetAwaiter().GetResult();
        var artifacts = acquisition.Artifacts.ToList();
        var stamp = acquisition.ObservedAtUtc;
        var memberRoot = Path.Combine(replay, "acquisition", "members");
        foreach (var relative in new[] { "common.rpf/data/ugc", "update/update2.rpf/common/data/ugc" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(memberRoot, relative), "*.ugc"))
            {
                var bytes = File.ReadAllBytes(path);
                var coordinate = Path.GetRelativePath(memberRoot, path).Replace('\\', '/');
                var split = coordinate.IndexOf(".rpf/", StringComparison.Ordinal);
                coordinate = coordinate[..(split + 4)] + "!" + coordinate[(split + 4)..];
                var artifact = Frozen(coordinate, GtaVUgcMissionKnowledgeAdapter.FormatId, bytes, stamp);
                if (package.Payload.Artifacts.Any(x => x.Id == artifact.Id)) artifacts.Add(artifact);
            }
        }
        return cached = new(package, new GtaVPresentationCorpusIndex(artifacts), KnowledgeSourceScope.BaseGame(ProductionGridCatalogService.GrandTheftAutoVEnhancedId, acquisition.GameVersion),
            package.Payload.AcquisitionReceipts.AddRange(acquisition.Receipts), package.Payload.ArtifactAcquisitionBindings.AddRange(acquisition.Bindings).DistinctBy(x => x.ArtifactId).ToImmutableArray());
    }
    public CanonicalCatalogPayload Apply(GtaVSecondaryAssertionBatch batch) => batch.ApplyTo(Origin.Payload, AcquisitionReceipts, AcquisitionBindings);
    private static FrozenSourceArtifact Frozen(string coordinate, string format, byte[] bytes, DateTimeOffset observed)
    {
        var digest = ContentDigest.ComputeSha256(bytes);
        var objectType = format == GtaVPresentationCorpusIndex.ReferenceFormatId ? "PinnedReference" : format == GtaVPresentationCorpusIndex.GxtFormatId ? "Gxt2" : "GtaVUgcMissionResource";
        return new(SourceArtifactId.DeriveV1(digest), digest, SourceNativeIdentifier.FromExactUtf8("rockstar.gta-v.enhanced.resource-coordinate", objectType, coordinate, "grid.gta-v.resource-coordinate.exact-utf8", 1), new KnowledgeFormatCoordinate(format, "1"), bytes.ToImmutableArray(), observed);
    }
    public void AssertMixed(GtaVSecondaryAssertionBatch batch)
    {
        foreach (var envelope in batch.CrossSourceAssertions.Where(x => x.AssertionKind == CrossSourceCanonicalAssertionKind.Terminology))
            if (!envelope.SupportingEvidenceReceiptIds.Any(id => batch.ReferenceEvidenceReceipts.Any(r => r.Id == id)) || !envelope.SupportingEvidenceReceiptIds.Any(id => batch.FileEvidenceReceipts.Any(r => r.Id == id))) throw new InvalidOperationException("Presentation title lost its mixed reference/file dependency chain.");
    }
    public List<CanonicalNavigationNode> Project(CanonicalCatalogPayload payload, KnowledgeKind kind)
    {
        payload = Narrow(payload, kind);
        var manifest = Origin.Manifest;
        var package = CanonicalCatalogPackageKernel.CreateV6(manifest.PackageKind, manifest.GameScope, manifest.ModScope, manifest.RequiredBasePackageIds, manifest.CompositionPolicyVersion, payload,
            new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.gta-v.presentation-test", "1", ContentDigest.ComputeSha256("in-memory-test-only"u8)),
            new CatalogBuildProvenance(CatalogBuildProvenance.CurrentSchemaVersion, "grid.gta-v.presentation-test", "1", new string('a', 40), [new CatalogCommittedBuildInput("src/Grid.GtaV.Knowledge/GtaVPresentationCorpusIndex.cs", new string('b', 40))]));
        var composition = new CatalogCompositionId("composition.gta-v.presentation-test");
        var policy = CanonicalSelectorProjectionPolicy.V4;
        var input = CanonicalSelectorProjectionEngine.CreateVerifiedInput(package, composition, policy, new CanonicalApplicabilityProjection(composition, payload.KnowledgeRecords.Select(x => x.Id).ToImmutableArray(), [], "1"));
        var pending = new Queue<CanonicalNavigationPathId?>(); pending.Enqueue(null);
        var leaves = new List<CanonicalNavigationNode>();
        while (pending.Count > 0)
        {
            var result = CanonicalSelectorProjectionEngine.Query(input, new CanonicalSelectorQuery(package.Manifest.CatalogRevisionId, composition, kind, policy.Id, policy.ExactVersion, pending.Dequeue(), null, false, false, new CanonicalTerminologyLocalePreference("en-US", [])));
            foreach (var node in result.ImmediateChildren) { if (node.IsSelectable) leaves.Add(node); if (node.CanDescend) pending.Enqueue(node.PathId); }
        }
        return leaves;
    }
    private static CanonicalCatalogPayload Narrow(CanonicalCatalogPayload payload, KnowledgeKind kind)
    {
        // Validate the complete new source-family closure, without repeatedly revalidating unrelated games/selectors.
        var selected = payload.KnowledgeRecords.Where(x => x.Kind == kind &&
            (kind == KnowledgeKind.MissionQuest ? x.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.online-activity-registry" :
                payload.SemanticClassificationAssertions.Any(a => a.KnowledgeRecordId == x.Id && a.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable)))
            .Select(x => x.Id).ToHashSet();
        bool changed;
        do
        {
            var before = selected.Count;
            foreach (var relationship in payload.RelationshipAssertions.Where(x => selected.Contains(x.SubjectKnowledgeRecordId) && x.ResolvedTargetKnowledgeRecordId.HasValue)) selected.Add(relationship.ResolvedTargetKnowledgeRecordId!.Value);
            var correlations = payload.CrossSourceAssertions.Where(x => selected.Contains(x.TargetKnowledgeRecordId)).SelectMany(x => x.CorrelationRecordIds).ToHashSet();
            foreach (var correlation in payload.CorrelationEnvelopes.Where(x => correlations.Contains(x.Id))) selected.UnionWith(correlation.Record.MemberIds);
            changed = selected.Count != before;
        } while (changed);
        var terms = payload.TerminologyAssertions.Where(x => selected.Contains(x.KnowledgeRecordId)).ToImmutableArray();
        var relationships = payload.RelationshipAssertions.Where(x => selected.Contains(x.SubjectKnowledgeRecordId)).ToImmutableArray();
        var classes = payload.SemanticClassificationAssertions.Where(x => selected.Contains(x.KnowledgeRecordId)).ToImmutableArray();
        var organizations = payload.OrganizationalValueAssertions.Where(x => selected.Contains(x.KnowledgeRecordId)).ToImmutableArray();
        var lifecycles = payload.RecordLifecycleAssertions.Where(x => selected.Contains(x.KnowledgeRecordId)).ToImmutableArray();
        var contributions = payload.RecordContributionAssertions.Where(x => selected.Contains(x.KnowledgeRecordId)).ToImmutableArray();
        var claimIds = terms.Select(x => EvidenceClaimContentId.DeriveV1(x))
            .Concat(relationships.Select(x => EvidenceClaimContentId.DeriveV1(x)))
            .Concat(classes.Select(x => EvidenceClaimContentId.DeriveV1(x)))
            .Concat(organizations.Select(x => EvidenceClaimContentId.DeriveV1(x)))
            .Concat(lifecycles.Select(x => EvidenceClaimContentId.DeriveV1(x)))
            .Concat(contributions.Select(x => EvidenceClaimContentId.DeriveV1(x))).ToHashSet();
        var envelopes = payload.CrossSourceAssertions.Where(x => claimIds.Contains(x.UnderlyingClaimContentId)).ToImmutableArray();
        var linkIds = envelopes.Select(x => x.TargetLinkClaimId).ToHashSet();
        var links = payload.CrossSourceTargetLinkClaims.Where(x => linkIds.Contains(x.Id)).ToImmutableArray();
        claimIds.UnionWith(links.Select(x => EvidenceClaimContentId.DeriveV1(x)));
        var bindings = payload.EvidenceBindings.Where(x => selected.Contains(x.KnowledgeRecordId) &&
            (x.ClaimKind == EvidenceClaimKind.KnowledgeIdentity || x.ClaimContentId.HasValue && claimIds.Contains(x.ClaimContentId.Value))).ToImmutableArray();
        var correlationIds = envelopes.SelectMany(x => x.CorrelationRecordIds).ToHashSet();
        var correlationEnvelopes = payload.CorrelationEnvelopes.Where(x => correlationIds.Contains(x.Id)).ToImmutableArray();
        var receiptIds = bindings.Select(x => x.EvidenceReceiptId).Concat(correlationEnvelopes.SelectMany(x => x.SupportingEvidenceReceiptIds)).ToHashSet();
        return new CanonicalCatalogPayload(payload.EffectiveCoverage, payload.AdapterDescriptors, payload.Sources, payload.Artifacts, payload.SourceRevisions,
            payload.KnowledgeRecords.Where(x => selected.Contains(x.Id)).ToImmutableArray(), terms, relationships,
            payload.FileEvidenceReceipts.Where(x => receiptIds.Contains(x.Id)).ToImmutableArray(), payload.ReferenceEvidenceReceipts.Where(x => receiptIds.Contains(x.Id)).ToImmutableArray(),
            bindings, correlationEnvelopes, [], payload.AcquisitionReceipts, payload.ArtifactAcquisitionBindings)
        { SemanticClassificationAssertions = classes, OrganizationalValueAssertions = organizations, RecordLifecycleAssertions = lifecycles,
          RecordContributionAssertions = contributions, CrossSourceAssertions = envelopes, CrossSourceTargetLinkClaims = links };
    }
}
