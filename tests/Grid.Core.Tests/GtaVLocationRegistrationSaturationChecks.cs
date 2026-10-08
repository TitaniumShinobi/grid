using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

/// <summary>
/// Corpus-driven Location registration saturation proof. Checks are structural (coverage, evidence, level order,
/// exhaustion); no place name, tree shape or count is asserted.
/// </summary>
internal static class GtaVLocationRegistrationSaturationChecks
{
    public static async Task<int> RunAsync(string repositoryRoot, string outputRoot, bool live)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        var allowed = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp"));
        if (!outputRoot.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Saturation proof output must remain inside the repository .tmp directory.");
        Directory.CreateDirectory(outputRoot);
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("GTA V Location saturation: " + reason);
            checks++;
        }

        var references = Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references");
        var corpus = GtaVLocationRegistrationCorpus.LoadFromRepository(references);
        Check(corpus.Pages.Length > 0, "v3 corpus loads with every manifest digest verified.");

        // Exhaustion: each discovery source terminated on an empty frontier within budget.
        using var discovery = JsonDocument.Parse(corpus.DiscoveryLedger);
        var sourceSummaries = new List<object>();
        foreach (var source in discovery.RootElement.GetProperty("sources").EnumerateArray())
        {
            var termination = source.GetProperty("termination").GetString();
            var frontier = source.GetProperty("frontierRemaining");
            var examined = source.GetProperty("examinedPages").EnumerateArray().ToArray();
            sourceSummaries.Add(new
            {
                source = source.GetProperty("source").GetString(),
                termination,
                requests = source.GetProperty("requests").GetInt32(),
                frontierPages = frontier.GetProperty("pages").GetInt32(),
                frontierCategories = frontier.GetProperty("categories").GetInt32(),
                examinedPages = examined.Length,
                examinedCategories = source.GetProperty("examinedCategories").GetArrayLength(),
                pinned = corpus.Pages.Count(p => p.ReferenceSourceId == source.GetProperty("source").GetString()),
                byStatus = examined.GroupBy(e => e.GetProperty("status").GetString() ?? "?").ToDictionary(g => g.Key, g => g.Count()),
            });
            Check(termination == "frontier-exhausted" && frontier.GetProperty("pages").GetInt32() == 0 && frontier.GetProperty("categories").GetInt32() == 0,
                "Discovery source " + source.GetProperty("source").GetString() + " exhausted its frontier.");
        }

        var (storePath, bindingPath, package) = await LoadActiveAsync();
        var baseline = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(package.Payload);
        var profileId = live ? ResolveLiveProfile(storePath) : new ProfileId("profile.location-saturation-proof");
        var adapter = new GtaVLocationRegistrationEvidenceAdapter(baseline, corpus, profileId, package.Id);
        var candidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            adapter, GtaVLocationRegistrationSourceArtifacts.CreateAdmittedSources(corpus, baseline),
            GtaVLocationRegistrationRules.Create(), [package]);
        var resolution = adapter.Resolution!;
        var checklist = CanonicalRegistrationChecklist.Load();
        decimal Level(RegisteredCanonicalEntity e) => checklist.SemanticLevel(e.Anchor, e.SemanticLevel);
        var entities = candidate.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var correlated = candidate.Relationships.Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();

        var nativeZones = GtaVLocationRegistrationResolver.NativeZones(baseline);
        Check(nativeZones.All(z => entities.ContainsKey(z.Record.Id.Value)),
            "Every English-named native population zone is a registered Location entity.");
        Check(correlated.All(r => Level(entities[r.SubjectId]) > Level(entities[r.TargetId])),
            "Every correlated containment edge places the child strictly below its container.");
        Check(correlated.All(r => r.EvidenceIds.Any(id => candidate.Input.Evidence.Evidence.Any(e => e.Id == id && e.Locator == r.SourceFieldPath && e.SourceId == r.SourceId))),
            "Every correlated edge carries exact source-span evidence from a pinned page.");
        Check(correlated.Select(r => (r.SubjectId, r.TargetId)).Distinct().Count() == correlated.Length,
            "No duplicate containment edges after evidence merging.");
        var examinedTitles = resolution.Ledger.Where(e => e.Surface == "reference-page").Select(e => e.Coordinate).ToHashSet(StringComparer.Ordinal);
        Check(corpus.Pages.All(p => examinedTitles.Contains(p.Coordinate)),
            "Every pinned reference page is examined by registration and recorded in the coverage ledger.");
        Check(candidate.Rulings.Where(r => r.Stage == "relationship" && r.Outcome == RegistrationOutcome.Unresolved).Count() ==
              resolution.Ledger.Count(e => e.Surface == "statement" && e.Outcome == "unresolved"),
            "Every unresolved containment statement is preserved as an unresolved engine ruling.");
        Check(candidate.Entities.All(e => e.SemanticLevel is null || (e.SemanticLevel % 1m == 0.5m)),
            "Fractional levels are only evidenced half levels.");

        var navContext = new RegistrationNavigationContext(ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value, profileId.Value, "en-US");
        var prepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(
            Path.Combine(outputRoot, "prepared-registration"), candidate, navContext, [package]);
        using (var reader = await CanonicalRegistrationPreparedReader.OpenAsync(prepared.DirectoryPath, prepared.Digest, navContext))
        {
            var (text, json, rows) = await DumpAsync(reader, id => entities.TryGetValue(id, out var e) ? Level(e) : null);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "prepared-tree.txt"), text);
            await File.WriteAllBytesAsync(Path.Combine(outputRoot, "prepared-tree.json"), CanonicalRegistrationEncoding.Bytes(json));
            Check(rows.Where(r => r.EntityId is not null).Select(r => r.EntityId).Distinct().Count() == candidate.Entities.Length,
                "Prepared navigation reaches every admitted Location entity.");
        }

        await WriteLedgerAsync(repositoryRoot, outputRoot, corpus, candidate, resolution, Level, sourceSummaries, package);

        object? liveReport = null;
        if (live)
        {
            Check(!Process.GetProcesses().Any(p => p.ProcessName.Equals("Grid.App", StringComparison.OrdinalIgnoreCase) || p.ProcessName.Equals("Grid", StringComparison.OrdinalIgnoreCase)),
                "Grid is not running, so the live catalog store is not held by the application.");
            liveReport = await PublishLiveAsync(repositoryRoot, outputRoot, storePath, bindingPath, package, profileId, entities, Level, Check);
        }

        var report = new
        {
            result = "PASS",
            mode = live ? "LIVE" : "PREPARED",
            checks,
            corpusManifestSha256 = corpus.ManifestDigest.HexValue,
            discoveryLedgerSha256 = corpus.DiscoveryLedgerDigest.HexValue,
            originPackageId = package.Id.Value,
            entities = candidate.Entities.Length,
            correlatedRelationships = correlated.Length,
            live = liveReport,
        };
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, live ? "saturation-live-report.json" : "saturation-report.json"), CanonicalRegistrationEncoding.Bytes(report));
        Console.WriteLine($"PASS {(live ? "LIVE" : "PREPARED")}: {checks} checks; entities={candidate.Entities.Length}; edges={correlated.Length}; out={outputRoot}");
        return checks;
    }

    private static async Task WriteLedgerAsync(string repositoryRoot, string outputRoot, GtaVLocationRegistrationCorpus corpus,
        CanonicalRegistrationCandidate candidate, GtaVLocationResolution resolution, Func<RegisteredCanonicalEntity, decimal> level,
        List<object> sourceSummaries, CanonicalCatalogPackage package)
    {
        var entities = candidate.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var correlated = candidate.Relationships.Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();
        string Name(string id) => entities.TryGetValue(id, out var e) ? e.Names.First(n => !n.IsAlias).Value : id;
        var ledger = new
        {
            schemaVersion = 1,
            kind = "grid.location-registration.coverage-ledger",
            gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value,
            corpusManifestSha256 = corpus.ManifestDigest.HexValue,
            originPackageId = package.Id.Value,
            discovery = sourceSummaries,
            totals = new
            {
                pinnedPages = corpus.Pages.Length,
                nativeZones = resolution.NativeZones.Length,
                entities = candidate.Entities.Length,
                nativeEntities = candidate.Entities.Count(e => e.NativeNamespace == GtaVLocationRegistrationResolver.PopulationZoneNamespace),
                referenceEntities = candidate.Entities.Count(e => e.NativeNamespace == GtaVLocationRegistrationRules.ReferenceSubjectNamespace),
                correlatedRelationships = correlated.Length,
                multiParentEntities = correlated.GroupBy(r => r.SubjectId).Count(g => g.Count() > 1),
                parentlessEntities = candidate.Entities.Count(e => correlated.All(r => r.SubjectId != e.Id)),
                rulings = candidate.Rulings.GroupBy(r => r.Stage + ":" + r.Outcome + ":" + r.Reason).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ledgerOutcomes = resolution.Ledger.GroupBy(e => e.Surface + ":" + e.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ledgerReasons = resolution.Ledger.GroupBy(e => e.Surface + ":" + e.Outcome + ":" + e.Reason.Split(':')[0]).OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
            },
            semanticDistribution = candidate.Entities.GroupBy(e => level(e)).OrderBy(g => g.Key)
                .ToDictionary(g => "L" + g.Key.ToString(CultureInfo.InvariantCulture), g => g.Count()),
            provenance = new
            {
                relationshipGrammars = correlated.GroupBy(r => r.SourceNativeRelationshipType ?? "?").OrderBy(g => g.Key, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count()),
                relationshipSources = correlated.GroupBy(r => corpus.Pages.FirstOrDefault(p => p.Artifact.Id.Value == r.SourceId)?.ReferenceSourceId ?? "native")
                    .ToDictionary(g => g.Key, g => g.Count()),
            },
            entitiesDetail = candidate.Entities.OrderBy(e => level(e)).ThenBy(e => Name(e.Id), StringComparer.Ordinal).Select(e => new
            {
                id = e.Id,
                name = Name(e.Id),
                aliases = e.Names.Where(n => n.IsAlias).Select(n => n.Value).ToArray(),
                level = level(e),
                origin = e.NativeNamespace == GtaVLocationRegistrationResolver.PopulationZoneNamespace ? "native:" + e.NativeId : "reference",
                levelBasis = resolution.Nodes.FirstOrDefault(n => n.Key == e.Id)?.LevelBasis,
                parents = correlated.Where(r => r.SubjectId == e.Id).Select(r => Name(r.TargetId)).Order(StringComparer.Ordinal).ToArray(),
            }).ToArray(),
            relationships = correlated.OrderBy(r => Name(r.TargetId), StringComparer.Ordinal).ThenBy(r => Name(r.SubjectId), StringComparer.Ordinal).Select(r => new
            {
                child = Name(r.SubjectId),
                parent = Name(r.TargetId),
                grammar = r.SourceNativeRelationshipType,
                coordinate = corpus.Pages.FirstOrDefault(p => p.Artifact.Id.Value == r.SourceId)?.Coordinate,
                locator = r.SourceFieldPath,
                supportingSpans = r.EvidenceIds.Length,
            }).ToArray(),
            gameFileZoneSurfaces = GameFileZoneCoverage(repositoryRoot, resolution),
            unresolvedEntities = resolution.UnresolvedNodes.Select(n => new { name = n.PrimaryName, reason = n.LevelBasis, pages = n.Pages.Select(p => p.Page.Coordinate).ToArray() }).ToArray(),
            ledger = resolution.Ledger.Select(e => new { e.Surface, e.Subject, e.Outcome, e.Reason, e.Coordinate, e.Locator }).ToArray(),
        };
        var bytes = CanonicalRegistrationEncoding.Bytes(ledger);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "coverage-ledger.v1.json"), bytes);
        // Registration-owned persistent ledger beside the corpus it covers.
        await File.WriteAllBytesAsync(Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references", "location-corpus", "coverage-ledger.v1.json"), bytes);
    }

    private static object? GameFileZoneCoverage(string repositoryRoot, GtaVLocationResolution resolution)
    {
        var path = Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references", "location-corpus", "game-file-zone-inventory.v1.json");
        if (!File.Exists(path))
            return null;
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        var labels = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tables = new List<object>();
        foreach (var member in root.GetProperty("zoneMembers").EnumerateArray())
        {
            if (!member.TryGetProperty("labels", out var memberLabels))
                continue;
            var values = memberLabels.EnumerateArray().Select(v => v.GetString()!).ToArray();
            labels.UnionWith(values);
            tables.Add(new { coordinate = member.GetProperty("coordinate").GetString(), sha256 = member.GetProperty("sha256").GetString(), labels = values.Length });
        }
        var native = resolution.NativeZones.SelectMany(z => z.CaseVariantCodes.Append(z.Code)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new
        {
            inventorySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
            surfaces = root.GetProperty("surfaces").EnumerateArray().GroupBy(s => s.GetProperty("status").GetString()).ToDictionary(g => g.Key!, g => g.Count()),
            populationZoneTables = tables,
            unexaminedNestedArchives = root.GetProperty("unexamined").GetArrayLength(),
            gameFileLabels = labels.Count,
            gameFileLabelsNotRegistered = labels.Where(l => !native.Contains(l)).ToArray(),
            registeredNativeCodesNotInGameFiles = resolution.NativeZones.Where(z => !labels.Contains(z.Code)).Select(z => z.Code).ToArray(),
        };
    }

    private static async Task<object> PublishLiveAsync(string repositoryRoot, string outputRoot, string storePath, string bindingPath,
        CanonicalCatalogPackage package, ProfileId profileId, Dictionary<string, RegisteredCanonicalEntity> entities,
        Func<RegisteredCanonicalEntity, decimal> level, Action<bool, string> check)
    {
        var catalogsRoot = Path.GetDirectoryName(storePath)!;
        var dataRoot = Path.GetDirectoryName(catalogsRoot)!;
        var preparedRoot = Path.Combine(catalogsRoot, "prepared-canonical");
        var author = new GtaVRegistrationMdboRefreshAuthor();
        var contributor = new CanonicalRegistrationMdboKnowledgeRefreshContributor(new GtaVEnhancedRegistrationKnowledgeRefreshContributor(), author);
        var context = new RegistrationRefreshContext(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            new InstallationId("installation.location-saturation-live"),
            profileId,
            package.Id,
            storePath,
            preparedRoot,
            bindingPath,
            Path.Combine(dataRoot, "evidence", "registration-refresh"),
            null,
            true,
            new RegistrationRefreshResourcePaths(
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/vinewood-411759.normalized.txt"),
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/cfx-zones-ad60ae80.md")));
        var refresh = await new CanonicalRegistrationRefreshOrchestrator([contributor], new CanonicalTerminologyLocalePreference("en-US", []))
            .RefreshAsync(context, null, default);
        check(refresh.Status == RegistrationRefreshStatus.Completed, "Live registration refresh completed: " + refresh.Detail);
        var publishedId = refresh.PublishedPackageId ?? package.Id;
        var catalog = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
        check(catalog.IsValid, "Live catalog store validates after publication.");
        var published = catalog.Snapshot.FindImportedPackage(publishedId) ?? throw new InvalidOperationException("Published package missing.");
        check(GtaVEnhancedRegistrationKnowledgeRefreshContributor_HierarchyPresent(published, repositoryRoot),
            "Published package carries the current v3 corpus revision.");
        using (var binding = JsonDocument.Parse(await File.ReadAllTextAsync(bindingPath)))
            check(binding.RootElement.GetProperty("packageId").GetString() == publishedId.Value, "Runtime binding targets the published package.");
        await GtaVRegistrationPreparedNavigationRebuild.RebuildAsync(context, catalog, publishedId);
        var publicationStore = new RegistrationPreparedNavigationPublicationStore(Path.Combine(catalogsRoot, "prepared-registration"));
        var publication = publicationStore.Load(profileId) ?? throw new InvalidOperationException("Prepared registration publication missing.");
        check(publication.PackageId == publishedId, "Profile prepared-registration publication targets the published package.");
        var nav = new RegistrationNavigationContext(publication.GameId.Value, publication.ProfileId.Value, publication.Locale);
        using var reader = await CanonicalRegistrationPreparedReader.OpenAsync(
            Path.Combine(publicationStore.Root, publication.GenerationDigest), publication.GenerationDigest, nav);
        var (text, json, rows) = await DumpAsync(reader, id => entities.TryGetValue(id, out var e) ? level(e) : null);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "live-tree.txt"), text);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "live-tree.json"), CanonicalRegistrationEncoding.Bytes(json));
        var publishedEdges = published.Payload.RelationshipAssertions.Count(r => r.SemanticId == LocationRelationshipSemantics.ContainedBy &&
            r.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal) && r.ResolvedTargetKnowledgeRecordId is not null);
        return new
        {
            refresh = refresh.Status.ToString(),
            mode = refresh.Mode.ToString(),
            refresh.Detail,
            previousPackageId = package.Id.Value,
            publishedPackageId = publishedId.Value,
            refresh.BindingRollbackPath,
            refresh.CatalogRollbackPath,
            refresh.ReceiptPath,
            profileId = profileId.Value,
            preparedGeneration = publication.GenerationDigest,
            publishedReferenceEdges = publishedEdges,
            liveTreePaths = rows.Count,
            liveTreeEntities = rows.Where(r => r.EntityId is not null).Select(r => r.EntityId).Distinct().Count(),
        };
    }

    private static bool GtaVEnhancedRegistrationKnowledgeRefreshContributor_HierarchyPresent(CanonicalCatalogPackage package, string repositoryRoot)
    {
        var references = Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references");
        if (!GtaVLocationRegistrationCorpus.IsAvailable(references))
            return false;
        var corpus = GtaVLocationRegistrationCorpus.LoadFromRepository(references);
        var version = GtaVLocationRegistrationCandidatePackageProjector.RevisionVersion(corpus);
        return package.Payload.SourceRevisions.Any(v => v.Revision.NativeRevision?.ExactRepresentation == version);
    }

    private static async Task<(string Text, object Json, List<RegistrationNavigationRow> Rows)> DumpAsync(
        CanonicalRegistrationPreparedReader reader, Func<string, decimal?> level)
    {
        var rows = new List<RegistrationNavigationRow>();
        var text = new StringBuilder("Location\n");
        var root = reader.Descriptor.Views.Single(v => v.Selector == "Location" && v.Dimension is null).RootPathId;
        async Task<List<object>> Walk(string pathId, int depth)
        {
            var nodes = new List<object>();
            string? cursor = null;
            do
            {
                var page = await reader.GetChildrenAsync(pathId, cursor);
                foreach (var child in page.Children)
                {
                    rows.Add(child);
                    var lv = child.EntityId is { } id ? level(id) : null;
                    var tag = lv is { } l ? " [L" + l.ToString(CultureInfo.InvariantCulture) + "]"
                        : child.Label == CanonicalRegistrationPreparationBuilder.MiscellaneousLabel ? " (presentation)" : " [frame]";
                    text.Append(new string(' ', depth * 2)).Append("- ").Append(child.Label).Append(tag).Append('\n');
                    var children = await Walk(child.PathId, depth + 1);
                    nodes.Add(new { label = child.Label, entityId = child.EntityId, level = lv, selectable = child.Selectable, children });
                }
                cursor = page.ContinuationCursor;
            } while (cursor is not null);
            return nodes;
        }
        var tree = await Walk(root, 0);
        return (text.ToString(), new { root = "Location", paths = rows.Count, tree }, rows);
    }

    private static async Task<(string StorePath, string BindingPath, CanonicalCatalogPackage Package)> LoadActiveAsync()
    {
        var dataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid");
        var catalogs = Path.Combine(Path.GetFullPath(dataRoot), "catalogs");
        var storePath = Path.Combine(catalogs, "shared-canonical-library.v5.json");
        var bindingPath = Path.Combine(catalogs, "canonical-runtime-binding.v1.json");
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(bindingPath));
        var packageId = new CatalogPackageId(binding.RootElement.GetProperty("packageId").GetString()!);
        var loaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
        if (!loaded.IsValid) throw new InvalidOperationException("Active catalog store failed validation.");
        return (storePath, bindingPath, loaded.Snapshot.FindImportedPackage(packageId) ?? throw new InvalidOperationException("Pinned package absent."));
    }

    /// <summary>The installed Grid profile that already owns a GTA prepared-registration publication.</summary>
    private static ProfileId ResolveLiveProfile(string storePath)
    {
        var store = new RegistrationPreparedNavigationPublicationStore(Path.Combine(Path.GetDirectoryName(storePath)!, "prepared-registration"));
        var profiles = Directory.Exists(store.Root)
            ? Directory.GetDirectories(store.Root).Select(d => new ProfileId(Path.GetFileName(d)))
                .Where(p => store.Load(p)?.GameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId).ToArray()
            : [];
        if (profiles.Length != 1)
            throw new InvalidOperationException("Live proof requires exactly one installed GTA prepared-registration profile; found " + profiles.Length);
        return profiles[0];
    }
}
