using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class CanonicalRelationshipRegistrationMdboChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Relationship MDBO composition: " + reason);
            checks++;
        }

        Check(HealthMdboCapabilityRegistryLoader.TryLoadHealthManifest(out var healthManifest),
            "Contract 2 capabilities are registered in scripts/health/mdbo-capabilities.v1.json.");
        Check(healthManifest.Capabilities.Length == 6,
            "Health MDBO registry exposes six relationship capabilities.");

        var manifest = CanonicalRelationshipRegistrationCapabilities.Load();
        CanonicalRelationshipRegistrationRealizations.EnsureManifestBindings(manifest);
        Check(CanonicalMdboCapabilityRegistry.RelationshipRegistration.Capabilities.Count == 6,
            "Runtime MDBO registry exposes six relationship capabilities.");
        Check(manifest.Capabilities.Select(c => c.CapabilityId).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(healthManifest.Capabilities.Select(c => c.CapabilityId).OrderBy(id => id, StringComparer.Ordinal)),
            "Runtime registry discovery matches the health MDBO manifest.");

        var order = CanonicalRelationshipRegistrationCapabilities.PlannerOrder();
        var expected = new[]
        {
            "grid.registration.relationship.discover-evidence",
            "grid.registration.relationship.resolve-endpoints",
            "grid.registration.relationship.resolve-canonical",
            "grid.registration.location-relationship.resolve",
            "grid.registration.relationship.validate-graph",
            "grid.registration.relationship.project-navigation",
        };
        Check(order.SequenceEqual(expected, StringComparer.Ordinal),
            "Planner order follows semantic-parent dependencies, not lexical sorting.");
        Check(!order.SequenceEqual(order.OrderBy(id => id, StringComparer.Ordinal)),
            "Planner order must not be alphabetical-only.");

        Check(CanonicalRelationshipRegistrationSemantics.OrientEndpoints(
                CanonicalRelationshipRegistrationSemantics.Contains, "parent", "child") == ("child", "parent"),
            "Contains orients parent as subject and child as target.");
        Check(CanonicalRelationshipRegistrationSemantics.OrientEndpoints(
                CanonicalRelationshipRegistrationSemantics.ContainedBy, "child", "parent") == ("child", "parent"),
            "Contained-by orients child as subject and parent as target.");

        checks += RunProvenanceAndContainsRegistrationChecks();
        checks += RunProductionAndLegacyBypassChecks();
        return checks;
    }

    private static int RunProductionAndLegacyBypassChecks()
    {
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Relationship MDBO composition: " + reason);
            checks++;
        }

        checks += RunProductionRefreshCompositionChecks();

        const string game = "game.fixture.legacy-bypass";
        var rules = new RegistrationRuleSet("legacy-bypass", [
            new("map:Location/world", "scenario-rows", "Location", "native:Location/world", "Location/world"),
        ]);
        var bytes = CanonicalRegistrationEncoding.Bytes(new
        {
            rows = new[]
            {
                new
                {
                    key = "solo",
                    nativeId = "native-solo",
                    selector = "Location",
                    game,
                    classification = "native:Location/world",
                    kind = nameof(RegistrationEntityKind.Entity),
                    names = new[] { new { text = "Solo", locale = "en-US" } },
                    scopes = new[] { new { game, profile = "profile.a" } },
                    parents = Array.Empty<string>(),
                },
            },
        });
        var artifact = new RegistrationSourceArtifact(
            new("legacy-bypass", "fixture://legacy-bypass", KnowledgeSourceKind.LocalGameDistribution,
                CanonicalRegistrationEncoding.Digest(bytes), "fixture.mdobo", "1", "scenario-rows"), bytes);
        var legacyCandidate = CanonicalRegistrationEngine.RegisterAsync(new ContainsScenarioAdapter(), [artifact], rules)
            .GetAwaiter().GetResult();
        Check(legacyCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "Legacy registration engine path remains available and stays NOT_PUBLISHED.");
        Check(
            CanonicalRegistrationEngine.RegisterAsync(new ContainsScenarioAdapter(), [artifact], rules).GetAwaiter().GetResult()
                .Entities.Length == legacyCandidate.Entities.Length,
            "MDBO composer is authoritative for refresh; legacy engine remains a parallel evaluate path.");
        return checks;
    }

    private static int RunProductionRefreshCompositionChecks()
    {
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Relationship MDBO composition: " + reason);
            checks++;
        }

        var repositoryRoot = LocateRepositoryRoot();
        var (baselinePackage, index) =
            GtaVLocationHierarchyRegistrationProofFixtures.CreateImportedBaselineWithoutHierarchy(repositoryRoot);
        var baselinePayload = baselinePackage.Payload;
        var profileId = new ProfileId("profile.registration-proof");
        var sources = GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index);
        var productionAdapter = new GtaVLocationHierarchyRegistrationEvidenceAdapter(
            baselinePayload, index, profileId, baselinePackage.Id);
        CanonicalRelationshipRegistrationProductionBindings.EnsureProductionEvidenceAdapter(productionAdapter);
        var registrationCandidate = CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            productionAdapter, sources, GtaVLocationHierarchyRegistrationRules.Create(), [baselinePackage])
            .GetAwaiter().GetResult();
        Check(registrationCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "Production MDBO registration candidate remains NOT_PUBLISHED.");
        Check(registrationCandidate.Relationships.Count(r => r.CorrelationOutcome == RegistrationOutcome.Correlated) ==
              index.ExpectedRelationshipCount,
            "Production hierarchy evidence adapter admits all pinned relationships through the composer.");

        var fixtureRoot = Path.Combine(repositoryRoot, ".tmp", "grid-contract2-registration-20261003", "proof",
            "mdobo-production-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        var storePath = Path.Combine(fixtureRoot, "catalog.json");
        var store = new JsonCanonicalKnowledgeCatalogStore(storePath);
        var import = store.ImportPackageAsync(0, baselinePackage).GetAwaiter().GetResult();
        Check(import.Status == CanonicalCatalogImportStatus.Imported, "Proof baseline package imports for refresh composition.");
        var catalog = store.LoadAsync().GetAwaiter().GetResult();
        Check(catalog.IsValid, "Proof catalog reload validates.");

        var observationContributor = new GtaVEnhancedRegistrationKnowledgeRefreshContributor();
        var mdoboContributor = new CanonicalRegistrationMdboKnowledgeRefreshContributor(
            observationContributor, new GtaVRegistrationMdboRefreshAuthor());
        var context = new RegistrationRefreshContext(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            new InstallationId("installation.proof"),
            profileId,
            baselinePackage.Id,
            storePath,
            Path.Combine(fixtureRoot, "prepared"),
            Path.Combine(fixtureRoot, "binding.json"),
            Path.Combine(fixtureRoot, "receipts"),
            null,
            false,
            new RegistrationRefreshResourcePaths(
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/vinewood-411759.normalized.txt"),
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/cfx-zones-ad60ae80.md")));
        var observation = observationContributor.Observe(context);
        var contribution = mdoboContributor.TryRebuildAsync(context, observation, catalog, null, default)
            .GetAwaiter().GetResult();
        Check(contribution is not null, "MDBO refresh author produced a candidate package.");
        Check(contribution!.CandidateReadyWithoutImport,
            "MDBO refresh composition terminates at CandidateReadyWithoutImport without import.");
        Check(contribution.Package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate,
            "MDBO refresh candidate package remains Candidate validation status.");

        var orchestrator = new CanonicalRegistrationRefreshOrchestrator([mdoboContributor], new CanonicalTerminologyLocalePreference("en-US", []));
        var refresh = orchestrator.RefreshAsync(context, null, default).GetAwaiter().GetResult();
        Check(refresh.Status == RegistrationRefreshStatus.Completed &&
              refresh.Mode == RegistrationRefreshMode.KnowledgeRebuild &&
              refresh.PublishedPackageId is null,
            "Production refresh orchestrator completes candidate-ready rebuild without publication.");
        return checks;
    }

    private static string LocateRepositoryRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("GRID_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(Path.Combine(fromEnvironment, ".git")))
            return Path.GetFullPath(fromEnvironment);
        for (var directory = AppContext.BaseDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)!)
        {
            if (Directory.Exists(Path.Combine(directory, ".git")))
                return directory;
        }

        throw new InvalidOperationException("Repository root is unavailable for production refresh composition checks.");
    }

    private static int RunProvenanceAndContainsRegistrationChecks()
    {
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Relationship MDBO composition: " + reason);
            checks++;
        }

        const string game = "game.fixture.mdobo";
        var rules = new RegistrationRuleSet("mdobo.rules.v1", [
            new("map:Location/world", "scenario-rows", "Location", "native:Location/world", "Location/world"),
            new("map:Location/world/continent", "scenario-rows", "Location", "native:Location/world/continent", "Location/world/continent"),
            new("map:Location/world/continent/country", "scenario-rows", "Location", "native:Location/world/continent/country", "Location/world/continent/country"),
        ]);

        var bytes = CanonicalRegistrationEncoding.Bytes(new
        {
            rows = new[]
            {
                new
                {
                    key = "container",
                    nativeId = "native-container",
                    selector = "Location",
                    game,
                    classification = "native:Location/world/continent",
                    kind = nameof(RegistrationEntityKind.Category),
                    names = new[] { new { text = "Container", locale = "en-US" } },
                    scopes = new[] { new { game, profile = "profile.a" } },
                    parents = Array.Empty<string>(),
                },
                new
                {
                    key = "contained",
                    nativeId = "native-contained",
                    selector = "Location",
                    game,
                    classification = "native:Location/world/continent/country",
                    kind = nameof(RegistrationEntityKind.Entity),
                    names = new[] { new { text = "Contained", locale = "en-US" } },
                    scopes = new[] { new { game, profile = "profile.a" } },
                    parents = Array.Empty<string>(),
                },
            },
        });

        var source = new RegistrationSource("source-a", "fixture://source-a", KnowledgeSourceKind.LocalGameDistribution,
            CanonicalRegistrationEncoding.Digest(bytes), "fixture.mdobo", "1", "scenario-rows");
        var artifact = new RegistrationSourceArtifact(source, bytes);

        var adapter = new ContainsScenarioAdapter();
        var containsCandidate = CanonicalRelationshipRegistrationMdboComposer
            .RegisterCandidateAsync(adapter, [artifact], rules).GetAwaiter().GetResult();
        Check(containsCandidate.Relationships.Any(r =>
                r.Semantic == CanonicalRelationshipRegistrationSemantics.Contains &&
                r.SubjectId == containsCandidate.Entities.Single(e => e.SourceKeys.Contains("contained")).Id &&
                r.TargetId == containsCandidate.Entities.Single(e => e.SourceKeys.Contains("container")).Id),
            "Contains edges persist with child subject and parent target.");

        var mismatchCandidate = CanonicalRelationshipRegistrationMdboComposer
            .RegisterCandidateAsync(new ProvenanceMismatchAdapter(), [artifact], rules).GetAwaiter().GetResult();
        Check(mismatchCandidate.Rulings.Any(r => r.Reason == "relationship-source-mismatch"),
            "Declared relationship source must match evidence source ids.");

        return checks;
    }

    private sealed class ContainsScenarioAdapter : IRegistrationEvidenceAdapter
    {
        public string Id => "fixture.mdobo";
        public string Version => "1";

        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            var family = JsonSerializer.Deserialize<JsonElement>(sources[0].Bytes, CanonicalRegistrationEncoding.Json);
            var evidence = new List<RegistrationEvidence>();
            var entities = new List<RegistrationEntityClaim>();
            var relationships = new List<RegistrationRelationshipClaim>();
            foreach (var row in family.GetProperty("rows").EnumerateArray())
            {
                var key = row.GetProperty("key").GetString()!;
                var eid = "evidence:" + key;
                evidence.Add(new(eid, sources[0].Source.Id, "/rows/" + key, EvidenceVerificationKind.FileVerified));
                var kind = string.Equals(key, "container", StringComparison.Ordinal)
                    ? RegistrationEntityKind.Category
                    : RegistrationEntityKind.Entity;
                entities.Add(new(key, sources[0].Source.Id, "fixture.native", row.GetProperty("nativeId").GetString()!,
                    row.GetProperty("selector").GetString()!, game, row.GetProperty("classification").GetString()!, kind,
                    [new("Name " + key, "en-US", false, [eid])],
                    [new(game, "profile.a", [eid])],
                    [new("Game", "fixture.native", row.GetProperty("nativeId").GetString()!, [eid])],
                    [eid]));
            }

            var containsLocator = "/rows/container/contains/contained";
            evidence.Add(new("evidence:contains-edge", sources[0].Source.Id, containsLocator, EvidenceVerificationKind.FileVerified));
            relationships.Add(new("edge:contains", "container", "contained",
                CanonicalRelationshipRegistrationSemantics.Contains, ["evidence:contains-edge"],
                sources[0].Source.Id, "fixture.contains", containsLocator));
            return Task.FromResult(new RegistrationEvidenceSet(evidence.ToArray(), entities.ToArray(), relationships.ToArray(), []));
        }

        private const string game = "game.fixture.mdobo";
    }

    private sealed class ProvenanceMismatchAdapter : IRegistrationEvidenceAdapter
    {
        public string Id => "fixture.mdobo";
        public string Version => "1";

        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            var evidence = new[]
            {
                new RegistrationEvidence("evidence:container", sources[0].Source.Id, "/rows/container", EvidenceVerificationKind.FileVerified),
                new RegistrationEvidence("evidence:contained", sources[0].Source.Id, "/rows/contained", EvidenceVerificationKind.FileVerified),
            };
            var entities = new[]
            {
                new RegistrationEntityClaim("container", sources[0].Source.Id, "fixture.native", "native-container", "Location",
                    "game.fixture.mdobo", "native:Location/world/continent", RegistrationEntityKind.Category,
                    [new("Container", "en-US", false, ["evidence:container"])],
                    [new("game.fixture.mdobo", "profile.a", ["evidence:container"])],
                    [new("Game", "fixture.native", "native-container", ["evidence:container"])], ["evidence:container"]),
                new RegistrationEntityClaim("contained", sources[0].Source.Id, "fixture.native", "native-contained", "Location",
                    "game.fixture.mdobo", "native:Location/world/continent/country", RegistrationEntityKind.Entity,
                    [new("Contained", "en-US", false, ["evidence:contained"])],
                    [new("game.fixture.mdobo", "profile.a", ["evidence:contained"])],
                    [new("Game", "fixture.native", "native-contained", ["evidence:contained"])], ["evidence:contained"]),
            };
            var relationships = new[]
            {
                new RegistrationRelationshipClaim("edge:mismatch", "contained", "container", "contained-by",
                    ["evidence:contained"], "declared-other-source", "fixture.contained-by", "/rows/contained/parent/container"),
            };
            return Task.FromResult(new RegistrationEvidenceSet(evidence, entities, relationships, []));
        }
    }
}
