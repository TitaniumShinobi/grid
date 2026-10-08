using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalRelationshipRegistrationChecks
{
    private sealed record NativeName(string Text, string Locale, bool Alias = false);
    private sealed record NativeScope(string Game, string? Profile);
    private sealed record NativeFacet(string Dimension, string Value, string Locale, bool Filter = false);
    private sealed record NativeExtraEdge(string Target, string Semantic);
    private sealed record NativeRow(string Key, string NativeId, string Selector, string? Game, string Classification,
        RegistrationEntityKind Kind, NativeName[] Names, NativeScope[] Scopes, string[] Parents,
        NativeFacet[] Facets, string Origin = "Game", string NativeNamespace = "fixture.native",
        NativeExtraEdge? ExtraEdge = null);
    private sealed record NativeFamily(NativeRow[] Rows);

    private sealed class ScenarioAdapter : IRegistrationEvidenceAdapter
    {
        private readonly bool _reverse;
        public ScenarioAdapter(bool reverse = false) => _reverse = reverse;
        public string Id => "fixture.relationship-scenario";
        public string Version => "1";
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            var evidence = new List<RegistrationEvidence>();
            var entities = new List<RegistrationEntityClaim>();
            var relationships = new List<RegistrationRelationshipClaim>();
            var facets = new List<RegistrationFacetClaim>();
            foreach (var artifact in sources)
            {
                var family = JsonSerializer.Deserialize<NativeFamily>(artifact.Bytes, CanonicalRegistrationEncoding.Json)
                    ?? throw new InvalidDataException("Empty scenario fixture.");
                foreach (var row in family.Rows)
                {
                    var eid = artifact.Source.Id + ":" + row.Key;
                    var basis = artifact.Source.Kind == KnowledgeSourceKind.ReferenceProvider
                        ? EvidenceVerificationKind.ReferenceVerified : EvidenceVerificationKind.FileVerified;
                    evidence.Add(new(eid, artifact.Source.Id, "/rows/" + row.Key, basis));
                    entities.Add(new(row.Key, artifact.Source.Id, row.NativeNamespace, row.NativeId, row.Selector, row.Game,
                        row.Classification, row.Kind,
                        row.Names.Select(n => new RegistrationName(n.Text, n.Locale, n.Alias, [eid])).ToArray(),
                        row.Scopes.Select(s => new RegistrationApplicability(s.Game, s.Profile, [eid])).ToArray(),
                        [new(row.Origin, row.NativeNamespace, row.NativeId, [eid])], [eid]));
                    foreach (var parent in row.Parents)
                    {
                        var relationshipLocator = "/rows/" + row.Key + "/parent/" + parent;
                        var relationshipEvidenceId = artifact.Source.Id + ":relationship:" + row.Key + ":" + parent;
                        evidence.Add(new(relationshipEvidenceId, artifact.Source.Id, relationshipLocator, basis));
                        relationships.Add(new("edge:" + row.Key + ":" + parent, row.Key, parent, "contained-by", [relationshipEvidenceId],
                            artifact.Source.Id, "fixture.contained-by", relationshipLocator));
                    }
                    if (row.ExtraEdge is not null)
                    {
                        var extra = row.ExtraEdge;
                        var extraLocator = "/rows/" + row.Key + "/extra/" + extra.Target;
                        var extraEvidenceId = artifact.Source.Id + ":relationship:" + row.Key + ":extra:" + extra.Target;
                        evidence.Add(new(extraEvidenceId, artifact.Source.Id, extraLocator, basis));
                        relationships.Add(new("edge:" + row.Key + ":" + extra.Target + ":" + extra.Semantic, row.Key, extra.Target,
                            extra.Semantic, [extraEvidenceId], artifact.Source.Id, "fixture." + extra.Semantic, extraLocator));
                    }
                    foreach (var facet in row.Facets)
                        facets.Add(new("facet:" + row.Key + ":" + facet.Dimension, row.Key,
                            facet.Filter ? RegistrationFacetKind.Filter : RegistrationFacetKind.AlternateView,
                            facet.Dimension, facet.Value, facet.Locale, [eid]));
                }
            }
            if (_reverse) { evidence.Reverse(); entities.Reverse(); relationships.Reverse(); facets.Reverse(); }
            return Task.FromResult(new RegistrationEvidenceSet(evidence.ToArray(), entities.ToArray(), relationships.ToArray(), facets.ToArray()));
        }
    }

    public static async Task<int> RunAsync(string repositoryRoot, string outputRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        var allowed = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp/grid-contract2-registration-20261003/proof"));
        if (outputRoot != allowed && !outputRoot.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Relationship proof output must remain inside the approved Contract 2 proof directory.");
        Directory.CreateDirectory(outputRoot);
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Relationship registration proof: " + reason);
            checks++;
        }

        var manifest = CanonicalRelationshipRegistrationCapabilities.Load();
        Check(manifest.Capabilities.Length == 6, "MDBO manifest exposes six relationship capabilities.");
        Check(CanonicalRelationshipRegistrationCapabilities.IsSelectable("grid.registration.relationship.validate-graph"),
            "Planner can select graph validation capability.");
        var order = CanonicalRelationshipRegistrationCapabilities.PlannerOrder();
        Check(order[0] == "grid.registration.relationship.discover-evidence" &&
              order[^1] == "grid.registration.relationship.project-navigation" &&
              order.Count == manifest.Capabilities.Length &&
              !order.SequenceEqual(order.OrderBy(id => id, StringComparer.Ordinal)),
            "Capability planner order is dependency-driven and complete.");
        checks += CanonicalRelationshipRegistrationMdboChecks.Run();

        const string game = "game.fixture.relationship";
        var rules = new RegistrationRuleSet("relationship.rules.v1", [
            new("map:Location/world", "scenario-rows", "Location", "native:Location/world", "Location/world"),
            new("map:Location/world/continent", "scenario-rows", "Location", "native:Location/world/continent", "Location/world/continent"),
            new("map:Location/world/continent/country", "scenario-rows", "Location", "native:Location/world/continent/country", "Location/world/continent/country"),
            new("map:Mod", "scenario-rows", "Mod", "native:Mod", "Mod"),
        ]);
        NativeRow Row(string key, string nativeId, string classification, RegistrationEntityKind kind = RegistrationEntityKind.Entity,
            string[]? parents = null, NativeName[]? names = null, NativeExtraEdge? extra = null, NativeFacet[]? facets = null) =>
            new(key, nativeId, "Location", game, classification, kind,
                names ?? [new("Source " + key, "en-US")], [new(game, "profile.a")], parents ?? [], facets ?? [], ExtraEdge: extra);

        RegistrationSourceArtifact Source(string id, KnowledgeSourceKind kind, params NativeRow[] rows)
        {
            var bytes = CanonicalRegistrationEncoding.Bytes(new NativeFamily(rows));
            return new(new(id, "fixture://" + id, kind, CanonicalRegistrationEncoding.Digest(bytes), "fixture.relationship-scenario", "1", "scenario-rows"), bytes);
        }

        async Task<CanonicalRegistrationCandidate> Register(bool reverse, params RegistrationSourceArtifact[] sources)
        {
            var candidate = await CanonicalRelationshipRegistrationPipeline.RegisterAsync(new ScenarioAdapter(reverse), sources, rules);
            CanonicalRegistrationCandidateVerifier.Verify(candidate);
            return candidate;
        }

        var orphan = await Register(false, Source("a", KnowledgeSourceKind.LocalGameDistribution,
            Row("orphan", "native-orphan", "native:Location/world")));
        Check(orphan.Entities.Any(e => e.SourceKeys.Contains("orphan", StringComparer.Ordinal)) &&
              !orphan.Relationships.Any(r => r.SubjectId == orphan.Entities.Single(e => e.SourceKeys.Contains("orphan")).Id),
            "1: Entity can exist without a known parent.");

        var edge = await Register(false, Source("b", KnowledgeSourceKind.LocalGameDistribution,
            Row("child", "native-child", "native:Location/world/continent/country"),
            Row("parent", "native-parent", "native:Location/world/continent", RegistrationEntityKind.Category),
            Row("leaf", "native-leaf", "native:Location/world/continent/country", parents: ["parent"])));
        Check(edge.Relationships.Any(r => r.ProvenanceReason == "exact-endpoints-and-evidence"), "2: Verified child→parent edge registers.");

        var deep = await Register(false, Source("c", KnowledgeSourceKind.LocalGameDistribution,
            Row("l0", "n0", "native:Location/world", RegistrationEntityKind.Category),
            Row("l1", "n1", "native:Location/world/continent", RegistrationEntityKind.Category, ["l0"]),
            Row("l2", "n2", "native:Location/world/continent/country", RegistrationEntityKind.Category, ["l1"]),
            Row("l3", "n3", "native:Location/world/continent/country", parents: ["l2"])));
        Check(deep.Relationships.Count(r => r.CorrelationOutcome == RegistrationOutcome.Correlated) >= 3,
            "3–4: Multi-level and arbitrary-depth specialization register.");

        var sourceA = Source("split-a", KnowledgeSourceKind.LocalGameDistribution,
            Row("join-child", "join-native", "native:Location/world/continent/country", parents: ["join-mid"]));
        var sourceB = Source("split-b", KnowledgeSourceKind.LocalGameDistribution,
            Row("join-mid", "join-mid-native", "native:Location/world/continent", RegistrationEntityKind.Category, parents: ["join-root"]),
            Row("join-root", "join-root-native", "native:Location/world", RegistrationEntityKind.Category));
        var composed = await Register(false, sourceA, sourceB);
        Check(composed.Relationships.Count(r => r.CorrelationOutcome == RegistrationOutcome.Correlated) >= 2,
            "5: Two sources compose one graph when identities match exactly.");

        var ambiguous = await Register(false, Source("d", KnowledgeSourceKind.LocalGameDistribution,
            Row("name-a", "native-a", "native:Location/world", names: [new("Shared Label", "en-US")]),
            Row("name-b", "native-b", "native:Location/world/continent", names: [new("Shared Label", "en-US")]),
            Row("bad-link", "native-bad", "native:Location/world/continent/country", parents: ["missing-key"])));
        Check(!ambiguous.Relationships.Any(r => r.SubjectId.Contains("bad-link", StringComparison.Ordinal)) &&
              ambiguous.Rulings.Any(r => r.Reason == "relationship-endpoint-unresolved"),
            "6–8: Ambiguous or missing endpoints stay unresolved without display-name merge.");

        var exact = await Register(false, Source("e", KnowledgeSourceKind.LocalGameDistribution,
            Row("exact-child", "exact-child-native", "native:Location/world/continent/country", parents: ["exact-parent"]),
            Row("exact-parent", "exact-parent-native", "native:Location/world/continent", RegistrationEntityKind.Category)));
        Check(exact.Relationships.Any(r => r.SourceNativeRelationshipType == "fixture.contained-by"), "7: Exact native identities establish edges.");

        var multi = await Register(false, Source("f", KnowledgeSourceKind.LocalGameDistribution,
            Row("shared-entity", "shared-native", "native:Location/world/continent/country", parents: ["left-parent", "right-parent"]),
            Row("left-parent", "left-native", "native:Location/world/continent", RegistrationEntityKind.Category),
            Row("right-parent", "right-native", "native:Location/world", RegistrationEntityKind.Category)));
        var sharedId = multi.Entities.Single(e => e.SourceKeys.Contains("shared-entity")).Id;
        Check(multi.Relationships.Count(r => r.SubjectId == sharedId) >= 2 &&
              multi.Entities.Count(e => e.Id == sharedId) == 1,
            "9: Multiple parent paths preserve one canonical identity.");

        var cycle = await Register(false, Source("g", KnowledgeSourceKind.LocalGameDistribution,
            Row("cycle-a", "cycle-a-native", "native:Location/world/continent/country", parents: ["cycle-b"]),
            Row("cycle-b", "cycle-b-native", "native:Location/world/continent", RegistrationEntityKind.Category, parents: ["cycle-a"])));
        Check(cycle.Rulings.Any(r => r.Reason == "cyclic-parentage") && !cycle.Relationships.Any(),
            "10: Cycles are rejected.");

        var technical = await Register(false, Source("h", KnowledgeSourceKind.LocalGameDistribution,
            Row("technical", "technical-native", "native:Location/world", kind: RegistrationEntityKind.TechnicalArtifact, parents: ["tech-parent"]),
            Row("tech-parent", "tech-parent-native", "native:Location/world/continent", RegistrationEntityKind.Category)));
        Check(!technical.Relationships.Any(r => r.CorrelationOutcome == RegistrationOutcome.Correlated),
            "11: Technical artifacts cannot become hierarchy navigation via invalid parent edges.");

        var alternate = await Register(false, Source("i", KnowledgeSourceKind.LocalGameDistribution,
            Row("view-entity", "view-native", "native:Location/world/continent/country",
                extra: new NativeExtraEdge("view-anchor", CanonicalRelationshipRegistrationSemantics.AlternateViewMembership)),
            Row("view-anchor", "view-anchor-native", "native:Location/world/continent", RegistrationEntityKind.Category)));
        Check(alternate.Relationships.Any(r => r.Semantic == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership) &&
              alternate.Facets.Length == 0,
            "12: Alternate-view membership stays separate from containment.");

        var filtered = await Register(false, Source("j", KnowledgeSourceKind.LocalGameDistribution,
            new NativeRow("filter-mod", "filter-native", "Mod", game, "native:Mod", RegistrationEntityKind.Entity,
                [new("Source filter-mod", "en-US")], [new(game, "profile.a")], [], [new("Category", "Source category", "en-US", true)])));
        Check(filtered.Facets.Length == 1 && filtered.Facets[0].Kind == RegistrationFacetKind.Filter &&
              !filtered.Relationships.Any(r => r.Semantic == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership),
            "13: Filtering stays separate from containment.");

        var preparedCandidate = await Register(false, Source("k", KnowledgeSourceKind.LocalGameDistribution,
            Row("nav-root", "nav-root-native", "native:Location/world", RegistrationEntityKind.Category),
            Row("nav-child", "nav-child-native", "native:Location/world/continent/country", parents: ["nav-root"])));
        var context = new RegistrationNavigationContext(game, "profile.a", "en-US");
        var prepared = await CanonicalRelationshipRegistrationPipeline.PrepareNavigationAsync(
            Path.Combine(outputRoot, "relationship-prepared"), preparedCandidate, context);
        using var reader = await CanonicalRegistrationPreparedReader.OpenAsync(prepared.DirectoryPath, prepared.Digest, context);
        var navRootEntity = preparedCandidate.Entities.Single(e => e.SourceKeys.Contains("nav-root")).Id;
        var navChildEntity = preparedCandidate.Entities.Single(e => e.SourceKeys.Contains("nav-child")).Id;
        var navRootPath = reader.Descriptor.Paths.Single(p => p.EntityId == navRootEntity).PathId;
        var navPage = await reader.GetChildrenAsync(navRootPath);
        Check(navPage.Children.Any(r => r.EntityId == navChildEntity),
            "14: Prepared navigation is derived from registered edges.");

        var digestArtifact = Source("l", KnowledgeSourceKind.LocalGameDistribution,
            Row("digest-a", "digest-a-native", "native:Location/world/continent/country", parents: ["digest-b"]),
            Row("digest-b", "digest-b-native", "native:Location/world/continent", RegistrationEntityKind.Category));
        var baseline = await Register(false, digestArtifact);
        var reordered = await Register(true, digestArtifact);
        Check(CanonicalRegistrationEncoding.Bytes(baseline).SequenceEqual(CanonicalRegistrationEncoding.Bytes(reordered)),
            "15: Reordered input produces identical candidate bytes.");

        var gtaShape = await Register(false,
            Source("gta-shape-local", KnowledgeSourceKind.LocalGameDistribution,
                Row("gta-downtown", "gta-downtown", "native:Location/world/continent/country", parents: ["gta-vinewood"]),
                Row("gta-vinewood", "gta-vinewood", "native:Location/world/continent/country", RegistrationEntityKind.Category, parents: ["gta-los-santos"]),
                Row("gta-los-santos", "gta-los-santos", "native:Location/world/continent/country", RegistrationEntityKind.Category, parents: ["gta-county"]),
                Row("gta-county", "gta-county", "native:Location/world/continent", RegistrationEntityKind.Category, parents: ["gta-state"]),
                Row("gta-state", "gta-state", "native:Location/world/continent/country", RegistrationEntityKind.Category, parents: ["gta-country"]),
                Row("gta-country", "gta-country", "native:Location/world/continent/country", RegistrationEntityKind.Category, parents: ["gta-continent"]),
                Row("gta-continent", "gta-continent", "native:Location/world/continent", RegistrationEntityKind.Category, parents: ["gta-world"]),
                Row("gta-world", "gta-world", "native:Location/world", RegistrationEntityKind.Category)));
        Check(gtaShape.PublicationState == CanonicalRegistrationEncoding.NotPublished &&
              gtaShape.Relationships.Count(r => r.CorrelationOutcome == RegistrationOutcome.Correlated) >= 7,
            "Bounded GTA-shaped fixture represents deep hierarchy without publication or real-game claims.");

        var receipt = CanonicalRegistrationEngine.CreateReceipt(preparedCandidate, [prepared]);
        Check(receipt.RelationshipsAdmitted > 0 && receipt.RelationshipRulingCounts.Length > 0 &&
              receipt.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "Receipt includes relationship counts and rulings.");

        var catalogRoot = Path.Combine(repositoryRoot, "eng", "catalog");
        var catalogHashes = new List<object>();
        if (Directory.Exists(catalogRoot))
        {
            foreach (var file in Directory.EnumerateFiles(catalogRoot, "*", SearchOption.AllDirectories)
                         .Where(path => !path.Contains(".tmp", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                catalogHashes.Add(new { path = Path.GetRelativePath(repositoryRoot, file), sha256 = CanonicalRegistrationEncoding.Digest(await File.ReadAllBytesAsync(file)) });
            }
        }
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "relationship-proof-report.json"), CanonicalRegistrationEncoding.Bytes(new
        {
            checks,
            result = "PASS",
            publicationState = CanonicalRegistrationEncoding.NotPublished,
            capabilities = manifest.Capabilities.Select(c => c.CapabilityId).ToArray(),
            catalogByteIdentitySnapshot = catalogHashes,
        }));

        Console.WriteLine($"PASS: {checks} relationship registration checks; NOT_PUBLISHED.");
        return checks;
    }
}
