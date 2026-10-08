using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalSelectorRuntimeChecks
{
    internal static CanonicalSelectorAndInstructionChecks.Fixture CreateEnhancedFixture()
    {
        var seed = CanonicalSelectorAndInstructionChecks.Fixture.Create(enhanced: true);
        CanonicalSemanticClassificationAssertion Role(CanonicalKnowledgeRecord record, CanonicalSemanticRoleId role)
        {
            var id = CanonicalSemanticClassificationAssertionId.DeriveV1(record.Id, seed.SourceRevisionId,
                role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
            return new(id, record.Id, seed.SourceRevisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
        }
        CanonicalOrganizationalValueAssertion Organization(CanonicalKnowledgeRecord record,
            CanonicalOrganizationalSemanticId dimension, string key, string text)
        {
            var native = SourceNativeIdentifier.FromExactUtf8("grid.test.organization", "Value", key);
            var id = CanonicalOrganizationalValueAssertionId.DeriveV2(record.Id, seed.SourceRevisionId,
                dimension, native, text, "grid.test.organization", "1", "/organization", "en-US");
            return new(id, record.Id, seed.SourceRevisionId, dimension, native, text,
                "grid.test.organization", "1", "/organization", "en-US");
        }
        return (seed with
        {
            Terms = seed.Terms.AddRange(new TerminologyAssertion[]
            {
                new(seed.Item.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact Vehicle", "/vehicle/name", "en-US"),
                new(seed.Actor.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact Actor Type", "/actor/name", "en-US"),
            }),
            Classifications = seed.Classifications.Where(value => value.KnowledgeRecordId != seed.Item.Id)
                .Concat(new[]
                {
                    Role(seed.Item, CanonicalProjectionSemantics.ItemVehicles),
                    Role(seed.Item, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                    Role(seed.Actor, CanonicalProjectionSemantics.ActorGenericType),
                    Role(seed.Actor, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                }).ToImmutableArray(),
            OrganizationalValues = seed.OrganizationalValues.AddRange(new[]
            {
                Organization(seed.Item, CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, "CLASS_1", "Exact Class"),
                Organization(seed.Actor, CanonicalProjectionSemantics.ActorSourceCategoryDimensionNode, "CATEGORY_1", "Exact Category"),
            }),
        }).Repackage();
    }

    internal static CanonicalSelectorAndInstructionChecks.Fixture CreateAllSelectorEnhancedFixture()
    {
        var seed = CreateEnhancedFixture();
        CanonicalSemanticClassificationAssertion Role(CanonicalKnowledgeRecord record, CanonicalSemanticRoleId role)
        {
            var id = CanonicalSemanticClassificationAssertionId.DeriveV1(record.Id, seed.SourceRevisionId,
                role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
            return new(id, record.Id, seed.SourceRevisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
        }
        var activity = SourceNativeIdentifier.FromExactUtf8("grid.test.organization", "Value", "MISSION");
        var categoryId = CanonicalOrganizationalValueAssertionId.DeriveV2(seed.Mission.Id, seed.SourceRevisionId,
            CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode, activity, "Exact Activity Family",
            "grid.test.organization", "1", "/activity/category", "en-US");
        return (seed with
        {
            Terms = seed.Terms.AddRange(new TerminologyAssertion[]
            {
                new(seed.LocationParent.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact Route", "/route/name", "en-US"),
                new(seed.Mission.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact English Activity", "/activity/name", "en-US"),
                new(seed.Mission.Id, seed.SourceRevisionId, TerminologyAssertionRole.PrimaryName, "Exact French Activity", "/activity/name", "fr-FR"),
            }),
            Classifications = seed.Classifications.Where(value => value.KnowledgeRecordId != seed.Mission.Id)
                .Concat(new[]
                {
                    Role(seed.LocationParent, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                    Role(seed.Mission, CanonicalProjectionSemantics.SelectorPlayerAddressable),
                    Role(seed.Mission, CanonicalProjectionSemantics.MissionOnline),
                }).ToImmutableArray(),
            OrganizationalValues = seed.OrganizationalValues.Add(new(categoryId, seed.Mission.Id,
                seed.SourceRevisionId, CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode,
                activity, "Exact Activity Family", "grid.test.organization", "1", "/activity/category", "en-US")),
        }).Repackage();
    }

    public static void Capture(string outputPath)
    {
        var fixtures = new[]
        {
            (Name: "ordinary", Fixture: CanonicalSelectorAndInstructionChecks.Fixture.Create()),
            (Name: "enhanced", Fixture: CreateEnhancedFixture()),
        };
        var policies = new[] { CanonicalSelectorProjectionPolicy.V1, CanonicalSelectorProjectionPolicy.V2,
            CanonicalSelectorProjectionPolicy.V3, CanonicalSelectorProjectionPolicy.V4 };
        CanonicalTerminologyLocalePreference[] locales = [new("en-US", []), new("fr-FR", ["en"])];
        var rows = new List<object>();
        foreach (var fixture in fixtures)
        foreach (var policy in policies)
        {
            if (fixture.Name == "enhanced" &&
                policy != CanonicalSelectorProjectionPolicy.GtaEnhanced) continue;
            var input = fixture.Fixture.ToInput(policy);
            foreach (var locale in locales)
            foreach (var identifiers in new[] { false, true })
            foreach (var inspection in new[] { false, true })
            foreach (var kind in Enum.GetValues<KnowledgeKind>())
            {
                var pending = new Queue<CanonicalNavigationPathId?>();
                var visited = new HashSet<CanonicalNavigationPathId>();
                pending.Enqueue(null);
                while (pending.Count > 0)
                {
                    var path = pending.Dequeue();
                    var query = new CanonicalSelectorQuery(input.CatalogRevisionId, input.CatalogCompositionId,
                        kind, policy.Id, policy.ExactVersion, path, null, identifiers, inspection, locale);
                    var result = CanonicalSelectorProjectionEngine.Query(input, query);
                    if (!visited.Add(result.CurrentPathId)) continue;
                    rows.Add(new { fixture = fixture.Name, query, result });
                    var searched = query with { SearchText = "Exact" };
                    rows.Add(new { fixture = fixture.Name, query = searched,
                        result = CanonicalSelectorProjectionEngine.Query(input, searched) });
                    foreach (var child in result.ImmediateChildren) pending.Enqueue(child.PathId);
                }
            }
        }
        File.WriteAllText(outputPath, JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Captured {rows.Count} complete selector query/result contracts to {outputPath}.");
    }

    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        void Reject<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(message);
        }
        var fixture = CanonicalSelectorAndInstructionChecks.Fixture.Create();
        var stages = new ConcurrentBag<CanonicalRuntimeStageMeasurement>();
        using var observation = CanonicalRuntimeDiagnostics.Observe(stages.Add);
        var input = fixture.ToInput();
        CanonicalSelectorQuery Query(KnowledgeKind kind, bool identifiers = true,
            CanonicalTerminologyLocalePreference? locale = null) => new(
                input.CatalogRevisionId, input.CatalogCompositionId, kind, input.Policy.Id,
                input.Policy.ExactVersion, null, null, identifiers, false, locale ?? new("en-US", ["en"]));
        int Builds() => stages.Count(value => value.Stage == "projection.graph-build");
        var concurrent = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => CanonicalSelectorProjectionEngine.Query(input, Query(KnowledgeKind.Item))))
            .ToArray();
        Task.WaitAll(concurrent);
        Assert(Builds() == 1, "Concurrent first queries construct one immutable selector graph.");
        Assert(concurrent.All(value => JsonSerializer.Serialize(value.Result) == JsonSerializer.Serialize(concurrent[0].Result)),
            "Concurrent queries return semantically identical ordered contracts.");
        Assert(stages.Count(value => value.Stage == "projection.index-build") == 1,
            "One frozen projection input builds its assertion indexes once.");
        var locationQuery = Query(KnowledgeKind.Location);
        var root = CanonicalSelectorProjectionEngine.Query(input, locationQuery);
        var parent = root.ImmediateChildren.First(value => value.CanDescend);
        var child = CanonicalSelectorProjectionEngine.Query(input, locationQuery with { CurrentPathId = parent.PathId });
        var beforeWarm = Builds();
        var back = CanonicalSelectorProjectionEngine.Query(input, locationQuery with { CurrentPathId = child.ParentPathId });
        var reopened = CanonicalSelectorProjectionEngine.Query(input, locationQuery);
        _ = CanonicalSelectorProjectionEngine.Query(input, locationQuery with { SearchText = "Exact" });
        Assert(Builds() == beforeWarm && JsonSerializer.Serialize(back) == JsonSerializer.Serialize(reopened),
            "Nested descent, Back, reopening and search reuse the same graph and ordering.");
        Reject<ArgumentException>(() => CanonicalSelectorProjectionEngine.Query(input, locationQuery with
        { CurrentPathId = new CanonicalNavigationPathId("grid.canonical-navigation-path.v1.sha256." + new string('0', 64)) }),
            "An unknown path cannot select a node from cached state.");
        Reject<ArgumentException>(() => CanonicalSelectorProjectionEngine.Query(input, locationQuery with
        { CatalogCompositionId = new CatalogCompositionId("composition.forged") }),
            "A cached projection still rejects a forged composition coordinate.");
        var sameLocale = Query(KnowledgeKind.Location, locale: new("en-US", ["en"]));
        _ = CanonicalSelectorProjectionEngine.Query(input, sameLocale);
        Assert(Builds() == beforeWarm, "Equivalent locale preferences reuse state despite separate array allocations.");

        // Use a fresh input so the eight-entry LRU boundary has an exact known access order.
        var boundedInput = fixture.ToInput();
        var start = Builds();
        foreach (var kind in Enum.GetValues<KnowledgeKind>())
        foreach (var identifiers in new[] { false, true })
            _ = CanonicalSelectorProjectionEngine.Query(boundedInput, Query(kind, identifiers));
        Assert(Builds() - start == 8, "Four kinds and two identifier modes create eight separate graph entries.");
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, Query(KnowledgeKind.Location, false));
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, Query(KnowledgeKind.Actor, locale: new("fr-FR", ["en"])));
        Assert(Builds() - start == 9, "A new locale creates its own graph after the bounded cache fills.");
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, Query(KnowledgeKind.Location, false));
        Assert(Builds() - start == 9, "A recently accessed entry survives LRU eviction.");
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, Query(KnowledgeKind.Location, true));
        Assert(Builds() - start == 10, "The least recently accessed entry is evicted and reconstructed on demand.");
        var preferencesStart = Builds();
        var englishFirst = Query(KnowledgeKind.Actor, locale: new("de-DE", ["en", "fr"]));
        var frenchFirst = Query(KnowledgeKind.Actor, locale: new("de-DE", ["fr", "en"]));
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, englishFirst);
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, frenchFirst);
        Assert(Builds() - preferencesStart == 2,
            "Ordered fallback preferences are distinct cache coordinates even when the fixture resolves the same fallback text.");
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput,
            Query(KnowledgeKind.Actor, locale: new("de-DE", ["en", "fr"])));
        Assert(Builds() - preferencesStart == 2, "A value-equal ordered fallback preference reuses its graph.");
        _ = CanonicalSelectorProjectionEngine.Query(boundedInput, englishFirst with { InspectionMode = true });
        Assert(Builds() - preferencesStart == 3, "Inspection visibility has a separate graph cache coordinate.");
        var badPayload = fixture.Package.Payload with { KnowledgeRecords = [] };
        var badPackage = fixture.Package with { Payload = badPayload };
        Reject<InvalidDataException>(() => CanonicalSelectorProjectionEngine.CreateVerifiedInput(badPackage,
            fixture.CompositionId, CanonicalSelectorProjectionPolicy.V1, fixture.Applicability),
            "A forged package sharing a verified package ID cannot borrow its validation state.");
        Assert(stages.Count(value => value.Stage == "projection.index-build") == 2,
            "Rejected packages do not publish a usable projection index.");
        checks += AllSelectorEnhancedChecks();
        return checks;
    }

    private static int AllSelectorEnhancedChecks()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var fixture = CreateAllSelectorEnhancedFixture();
        CanonicalSelectorResult Project(CanonicalSelectorAndInstructionChecks.Fixture value, KnowledgeKind kind,
            CanonicalNavigationPathId? path = null, CanonicalTerminologyLocalePreference? locale = null, bool inspection = false)
        {
            var input = value.ToInput(CanonicalSelectorProjectionPolicy.V4);
            return CanonicalSelectorProjectionEngine.Query(input, new(input.CatalogRevisionId, input.CatalogCompositionId,
                kind, input.Policy.Id, input.Policy.ExactVersion, path, null, true, inspection, locale ?? new("en-US", [])));
        }
        var route = Project(fixture, KnowledgeKind.Location).ImmediateChildren.Single();
        Assert(route.KnowledgeRecordId == fixture.LocationParent.Id && route.DisplayAnchor == "Exact Route" && route.IsSelectable,
            "An admitted exact-locale named route remains a selectable root without invented geographic parents.");
        var online = Project(fixture, KnowledgeKind.MissionQuest).ImmediateChildren.Single();
        var family = Project(fixture, KnowledgeKind.MissionQuest, online.PathId).ImmediateChildren.Single();
        var activity = Project(fixture, KnowledgeKind.MissionQuest, family.PathId).ImmediateChildren.Single();
        Assert(online.OrganizationalSemanticId == CanonicalProjectionSemantics.MissionOnlineNode && !online.IsSelectable &&
               family.DisplayAnchor == "Exact Activity Family" && !family.IsSelectable &&
               activity.DisplayAnchor == "Exact English Activity" && activity.KnowledgeRecordId == fixture.Mission.Id,
            "V4 retains exact Online/activity-family/English activity organization with organizational nodes nonselectable.");
        Assert(activity.DisplayAnchor == "Exact English Activity" &&
               activity.ExactTerminologyAssertions.Any(value => value.LanguageTag == "en-US") &&
               activity.ExactTerminologyAssertions.Any(value => value.LanguageTag == "fr-FR"),
            "English presentation preserves other-locale terminology as evidence without displaying it as the selected title.");
        Assert(Project(fixture, KnowledgeKind.Location, locale: new("de-DE", ["en-US"])).ImmediateChildren.IsEmpty &&
               Project(fixture, KnowledgeKind.MissionQuest, locale: new("de-DE", ["en-US"])).ImmediateChildren.IsEmpty,
            "V4 route and activity eligibility requires the requested locale even when a fallback is approved.");
        var role = CanonicalProjectionSemantics.MissionPlaylist;
        var roleId = CanonicalSemanticClassificationAssertionId.DeriveV1(fixture.Mission.Id, fixture.SourceRevisionId,
            role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type");
        var playlist = (fixture with { Classifications = fixture.Classifications.Add(new(roleId, fixture.Mission.Id,
            fixture.SourceRevisionId, role, "grid.test.roles", "1", "grid.test.classifier", "1", "/type")) }).Repackage();
        Assert(Project(playlist, KnowledgeKind.MissionQuest).ImmediateChildren.IsEmpty,
            "Playlist role stays outside normal English choices despite an otherwise resolved title.");
        Assert(!Project(playlist, KnowledgeKind.MissionQuest, inspection: true).ImmediateChildren.IsEmpty,
            "Playlist identity remains inspectable beneath normal presentation filtering.");
        var conflicting = (fixture with { Terms = fixture.Terms.Add(new(fixture.LocationParent.Id, fixture.SourceRevisionId,
            TerminologyAssertionRole.PrimaryName, "Conflicting Route", "/route/conflict", "en-US")) }).Repackage();
        Assert(Project(conflicting, KnowledgeKind.Location).ImmediateChildren.IsEmpty,
            "Conflicting exact-locale route names remain unresolved rather than arbitrarily selected.");
        Assert(Enum.GetValues<KnowledgeKind>().All(kind => !Project(fixture, kind).ImmediateChildren.IsEmpty),
            "The focused Enhanced fixture exercises material normal presentation across all four selectors.");
        return checks;
    }

    public static async Task<int> RunStoreValidationAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var fixture = CanonicalSelectorAndInstructionChecks.Fixture.Create();
        var directory = Path.Combine(Path.GetTempPath(), "grid-canonical-runtime-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new JsonCanonicalKnowledgeCatalogStore(Path.Combine(directory, "tiny-synthetic-store.json"));
            var imported = await store.ImportPackageAsync(0, fixture.Package);
            Assert(imported.Status == CanonicalCatalogImportStatus.Imported, "The synthetic validation fixture imports successfully.");
            var loaded = await store.LoadAsync();
            Assert(loaded.IsValid, "The synthetic store passes unchanged structural validation.");
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "tiny-synthetic-store.json"));
            Encoding[] encodings = [new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode,
                Encoding.UTF32, new UTF32Encoding(true, true)];
            foreach (var encoding in encodings)
            {
                var encodedPath = Path.Combine(directory, "bom-" + encoding.CodePage + ".json");
                await File.WriteAllTextAsync(encodedPath, text, encoding);
                var encodedLoad = await new JsonCanonicalKnowledgeCatalogStore(encodedPath).LoadAsync();
                Assert(encodedLoad.IsValid && encodedLoad.Snapshot.ImportedPackages.Single().Id == fixture.Package.Id,
                    $"Streaming load preserves the prior BOM-detected {encoding.WebName} store behavior.");
            }
            var exact = loaded.Snapshot.FindImportedPackage(fixture.Package.Id)!;
            Assert(loaded.HasValidatedPackage(exact), "Only the exact package loaded and validated from this snapshot has a validation capability.");
            Assert(!loaded.HasValidatedPackage(fixture.Package), "An equivalent package object cannot borrow another object's validation capability.");
            var constructed = new CanonicalCatalogLoadResult(loaded.Snapshot, []);
            Assert(constructed.IsValid && !constructed.HasValidatedPackage(exact),
                "Publicly constructing a load result with empty issues does not grant validation authority.");
            var substitutedSnapshot = loaded with { Snapshot = loaded.Snapshot with { Revision = loaded.Snapshot.Revision + 1 } };
            Assert(!substitutedSnapshot.HasValidatedPackage(exact), "Replacing the snapshot invalidates the old exact-object validation capability.");
            var changedPackage = exact with { Payload = exact.Payload with { KnowledgeRecords = [] } };
            Assert(!loaded.HasValidatedPackage(changedPackage), "A changed package cannot borrow validation by keeping its package ID.");
            var forged = new CanonicalCatalogLoadResult(loaded.Snapshot with { ImportedPackages = [changedPackage] }, []);
            try
            {
                _ = CanonicalSelectorProjectionEngine.CreateVerifiedInput(forged, changedPackage.Id,
                    fixture.CompositionId, CanonicalSelectorProjectionPolicy.V1, fixture.Applicability);
            }
            catch (InvalidDataException) { checks++; goto Rejected; }
            throw new InvalidOperationException("A forged public load result bypassed package validation.");
            Rejected:
            var invalidPath = Path.Combine(directory, "invalid.json");
            await File.WriteAllTextAsync(invalidPath, "{}");
            var invalid = await new JsonCanonicalKnowledgeCatalogStore(invalidPath).LoadAsync();
            Assert(!invalid.IsValid && !invalid.HasValidatedPackage(exact), "Failed store validation never publishes a trusted package.");
            return checks;
        }
        finally
        {
            // This directory is newly generated by this fixture and never contains user state.
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            var resolvedDirectory = Path.GetFullPath(directory);
            if (!resolvedDirectory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture cleanup escaped its isolated temporary root.");
            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }
}
