using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalOperationalInstructionInspectionChecks
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Operational instruction inspection: " + message);
            checks++;
        }
        void Reject(Action action, string reason)
        {
            try { action(); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                checks++;
                return;
            }
            throw new InvalidOperationException("Operational instruction inspection expected rejection: " + reason);
        }

        var widget = WidgetOverlayFixture();
        var archive = ArchiveRepairFixture();
        Assert(widget.Family != archive.Family, "The two fixtures are unrelated source families.");

        var widgetResult = CanonicalOperationalInstructionInspector.Inspect(widget.Sources, widget.Requests);
        var archiveResult = CanonicalOperationalInstructionInspector.Inspect(archive.Sources, archive.Requests);
        CanonicalOperationalInstructionInspector.Verify(widgetResult, widget.Sources);
        CanonicalOperationalInstructionInspector.Verify(archiveResult, archive.Sources);

        Assert(widgetResult.InspectorVersion == archiveResult.InspectorVersion &&
               widgetResult.InspectionCategory == "GR-20" &&
               archiveResult.InspectionCategory == "GR-20",
            "The same inspector admits both fixtures without a game-specific branch.");
        Assert(widgetResult.PublicationState == CanonicalRegistrationEncoding.NotPublished &&
               archiveResult.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "Inspection output remains NOT_PUBLISHED.");
        Assert(widgetResult.BoundCandidateDigest is null && archiveResult.BoundCandidateDigest is null,
            "Unbound inspection does not invent a live-store or candidate digest.");

        TracePresent(widgetResult, widget.Sources, "widget.install", "## Installation",
            "Install through the manager. Enable Widget Overlay.esm last.", Assert);
        TracePresent(widgetResult, widget.Sources, "widget.config", "## Configuration",
            "Set overlayOpacity to 0.4 in WidgetOverlay.ini.", Assert);
        TracePresent(archiveResult, archive.Sources, "tool.install", "/instructions/installation",
            "Copy archive-repair.exe next to the game executable.", Assert);
        TracePresent(archiveResult, archive.Sources, "tool.usage", "/instructions/usage",
            "Run archive-repair.exe --verify then --repair.", Assert);

        Assert(widgetResult.Instructions.Single(value => value.Id == "widget.update").Presence ==
               OperationalInstructionSectionPresence.Missing,
            "A requested heading that is not in the source is missing, not proven absence.");
        Assert(widgetResult.Rulings.Any(value => value.ClaimId == "widget.update" &&
                                                value.Outcome == RegistrationOutcome.Unresolved &&
                                                value.Reason == "instruction-section-missing"),
            "Missing instruction sections stay unresolved.");
        Assert(widgetResult.Instructions.Single(value => value.Id == "widget.uninstall").Presence ==
               OperationalInstructionSectionPresence.ProvenAbsence &&
               widgetResult.Instructions.Single(value => value.Id == "widget.uninstall").VerbatimText ==
               "This package does not include uninstall guidance.",
            "An explicit absence statement is proven absence and retains source wording.");
        Assert(archiveResult.Instructions.Single(value => value.Id == "tool.update").Presence ==
               OperationalInstructionSectionPresence.ProvenAbsence,
            "JSON absent:true is proven absence, not a missing pointer.");
        Assert(archiveResult.Instructions.Single(value => value.Id == "tool.missing").Presence ==
               OperationalInstructionSectionPresence.Missing,
            "A JSON pointer to an absent key is missing, not proven absence.");

        Assert(widgetResult.Rulings.Any(value => value.ClaimId == "widget.install" &&
                                                value.Outcome == RegistrationOutcome.Ambiguous &&
                                                value.Reason == "instruction-guidance-conflicting") &&
               widgetResult.Instructions.Count(value => value.Kind == OperationalInstructionKind.Installation &&
                                                       value.Presence == OperationalInstructionSectionPresence.Present) == 2,
            "Contradictory installation wording is retained without choosing a winner.");
        Assert(archiveResult.Rulings.Any(value => value.ClaimId == "tool.usage" &&
                                                 value.Outcome == RegistrationOutcome.Ambiguous) &&
               archiveResult.Instructions.Count(value => value.Kind == OperationalInstructionKind.Usage &&
                                                        value.Presence == OperationalInstructionSectionPresence.Present) == 2,
            "Contradictory tool usage wording is retained without choosing a winner.");

        var widgetAgain = CanonicalOperationalInstructionInspector.Inspect(widget.Sources.Reverse().ToArray(),
            widget.Requests.Reverse().ToArray());
        var archiveAgain = CanonicalOperationalInstructionInspector.Inspect(archive.Sources.Reverse().ToArray(),
            archive.Requests.Reverse().ToArray());
        Assert(CanonicalRegistrationEncoding.Bytes(widgetResult)
                   .SequenceEqual(CanonicalRegistrationEncoding.Bytes(widgetAgain)) &&
               CanonicalRegistrationEncoding.Bytes(archiveResult)
                   .SequenceEqual(CanonicalRegistrationEncoding.Bytes(archiveAgain)),
            "Inspection bytes are deterministic after source and request reordering.");

        var mutated = widget.Sources[0] with { Bytes = widget.Sources[0].Bytes.ToArray() };
        mutated.Bytes[0] ^= 1;
        Reject(() => CanonicalOperationalInstructionInspector.Inspect([mutated, .. widget.Sources.Skip(1)], widget.Requests),
            "Changed source bytes fail closed.");
        Reject(() => CanonicalOperationalInstructionInspector.Inspect(
                widget.Sources.Select((item, index) => index == 0
                    ? item with { Source = item.Source with { Sha256 = new string('0', 64) } }
                    : item).ToArray(),
                widget.Requests),
            "Wrong admitted digest fails closed.");

        var published = UnpublishedCandidate() with { PublicationState = "PUBLISHED" };
        Reject(() => CanonicalOperationalInstructionInspector.Inspect(widget.Sources, widget.Requests, published),
            "A published bound candidate is rejected.");
        var bound = UnpublishedCandidate();
        var attached = CanonicalOperationalInstructionInspector.Inspect(widget.Sources, widget.Requests, bound);
        Assert(attached.PublicationState == CanonicalRegistrationEncoding.NotPublished &&
               attached.BoundCandidateDigest == CanonicalRegistrationEncoding.Digest(bound) &&
               CanonicalRegistrationEncoding.Bytes(bound).SequenceEqual(CanonicalRegistrationEncoding.Bytes(UnpublishedCandidate())),
            "Binding records the candidate digest and does not mutate the bound candidate.");
        CanonicalOperationalInstructionInspector.Verify(attached, widget.Sources, bound);

        Assert(widgetResult.Instructions.All(value => value.CategoryId.StartsWith("grid.instructions.", StringComparison.Ordinal)) &&
               archiveResult.Instructions.All(value => value.CategoryId.StartsWith("grid.instructions.", StringComparison.Ordinal)),
            "Admitted sections reuse InstructionCategories identifiers.");
        var checklist = CanonicalRegistrationChecklist.Load();
        Assert(checklist.Nodes.Length == 98 && checklist.Version == "1",
            "This slice does not change the frozen 98-node DIF mold.");
        checks += RunRegistrationPathAsync().GetAwaiter().GetResult();
        checks += RunComposerPathAsync().GetAwaiter().GetResult();
        checks += RunRealAdmittedSourceBoundary();
        return checks;
    }

    private static async Task<int> RunRegistrationPathAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Operational instruction registration: " + message);
            checks++;
        }
        async Task Reject(Func<Task> action, string reason)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                checks++;
                return;
            }
            throw new InvalidOperationException("Operational instruction registration expected rejection: " + reason);
        }

        var widget = WidgetOverlayFixture();
        var archive = ArchiveRepairFixture();
        var widgetSources = WithEntity(widget, "src.widget.entity", "Mod", "Overlay", "widget-overlay",
            "game.fixture.widget", "profile.overlay", "Widget Overlay");
        var archiveSources = WithEntity(archive, "src.tool.entity", "Tool", "Utility", "archive-repair-cli",
            "game.fixture.archive", null, "Archive Repair CLI");
        var rules = new RegistrationRuleSet("1",
        [
            new("map.mod", "fixture.mod", "Mod", "Overlay", "Mod"),
            new("map.tool", "fixture.tool", "Tool", "Utility", "Tool"),
        ]);

        var widgetCandidate = await CanonicalRegistrationEngine.RegisterAsync(
            new FamilyAdapter(), widgetSources, rules, widget.Requests);
        var archiveCandidate = await CanonicalRegistrationEngine.RegisterAsync(
            new FamilyAdapter(), archiveSources, rules, archive.Requests);
        CanonicalRegistrationCandidateVerifier.Verify(widgetCandidate);
        CanonicalRegistrationCandidateVerifier.Verify(archiveCandidate);

        Assert(widgetCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished &&
               archiveCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "RegisterAsync candidates remain NOT_PUBLISHED.");
        Assert(widgetCandidate.Instructions is { Length: > 0 } && archiveCandidate.Instructions is { Length: > 0 },
            "RegisterAsync stores operational instructions on the canonical candidate.");
        Assert(widgetCandidate.Input.Evidence.Instructions is { Length: > 0 } &&
               archiveCandidate.Input.Evidence.Instructions is { Length: > 0 },
            "Instruction claims are durable on the registration input that Verify re-evaluates.");

        TraceRegistered(widgetCandidate, widgetSources, "widget.install", "## Installation",
            "Install through the manager. Enable Widget Overlay.esm last.", Assert);
        TraceRegistered(archiveCandidate, archiveSources, "tool.install", "/instructions/installation",
            "Copy archive-repair.exe next to the game executable.", Assert);
        Assert(widgetCandidate.Rulings.Any(value => value.ClaimId == "widget.update" &&
                                                    value.Reason == "instruction-section-missing") &&
               archiveCandidate.Rulings.Any(value => value.ClaimId == "tool.update" &&
                                                     value.Reason == "instruction-section-absent"),
            "Registration retains missing versus proven-absence instruction rulings.");
        Assert(widgetCandidate.Rulings.Any(value => value.ClaimId == "widget.install" &&
                                                    value.Reason == "instruction-guidance-conflicting") &&
               archiveCandidate.Rulings.Any(value => value.ClaimId == "tool.usage" &&
                                                     value.Reason == "instruction-guidance-conflicting"),
            "Registration retains contradictory guidance without resolving it.");

        var widgetAgain = await CanonicalRegistrationEngine.RegisterAsync(
            new FamilyAdapter(), widgetSources.Reverse().ToArray(), rules, widget.Requests.Reverse().ToArray());
        Assert(CanonicalRegistrationEncoding.Bytes(widgetCandidate)
                   .SequenceEqual(CanonicalRegistrationEncoding.Bytes(widgetAgain)),
            "RegisterAsync instruction attachment is deterministic.");

        var mutated = widgetSources[1] with { Bytes = widgetSources[1].Bytes.ToArray() };
        mutated.Bytes[0] ^= 1;
        await Reject(() => CanonicalRegistrationEngine.RegisterAsync(
                new FamilyAdapter(), [widgetSources[0], mutated, .. widgetSources.Skip(2)], rules, widget.Requests),
            "RegisterAsync fails closed on changed instruction source bytes.");

        var baseline = await CanonicalRegistrationEngine.RegisterAsync(
            new FamilyAdapter(), widgetSources, rules);
        CanonicalRegistrationCandidateVerifier.Verify(baseline);
        Assert(baseline.Instructions is null && baseline.Input.Evidence.Instructions is null,
            "Registration without explicit instruction sections does not invent instruction claims.");
        Assert(!System.Text.Encoding.UTF8.GetString(CanonicalRegistrationEncoding.Bytes(baseline))
                .Contains("\"instructions\"", StringComparison.Ordinal),
            "Omitting instruction sections keeps existing candidate JSON byte-compatible.");
        return checks;
    }

    private static async Task<int> RunComposerPathAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Operational instruction MDBO composer: " + message);
            checks++;
        }

        var widget = WidgetOverlayFixture();
        var archive = ArchiveRepairFixture();
        var widgetSources = WithEntity(widget, "src.widget.entity", "Mod", "Overlay", "widget-overlay",
            "game.fixture.widget", "profile.overlay", "Widget Overlay");
        var archiveSources = WithEntity(archive, "src.tool.entity", "Tool", "Utility", "archive-repair-cli",
            "game.fixture.archive", null, "Archive Repair CLI");
        var rules = new RegistrationRuleSet("1",
        [
            new("map.mod", "fixture.mod", "Mod", "Overlay", "Mod"),
            new("map.tool", "fixture.tool", "Tool", "Utility", "Tool"),
        ]);

        var engineBaseline = await CanonicalRegistrationEngine.RegisterAsync(new FamilyAdapter(), widgetSources, rules);
        var composerBaseline = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            new FamilyAdapter(), widgetSources, rules);
        var pipelineBaseline = await CanonicalRelationshipRegistrationPipeline.RegisterAsync(
            new FamilyAdapter(), widgetSources, rules);
        CanonicalRegistrationCandidateVerifier.Verify(engineBaseline);
        CanonicalRegistrationCandidateVerifier.Verify(composerBaseline);
        CanonicalRegistrationCandidateVerifier.Verify(pipelineBaseline);
        Assert(CanonicalRegistrationEncoding.Bytes(engineBaseline)
                   .SequenceEqual(CanonicalRegistrationEncoding.Bytes(composerBaseline)) &&
               CanonicalRegistrationEncoding.Bytes(composerBaseline)
                   .SequenceEqual(CanonicalRegistrationEncoding.Bytes(pipelineBaseline)),
            "No-instruction composer/pipeline candidates are byte-identical to the engine path.");
        Assert(composerBaseline.Instructions is null &&
               !System.Text.Encoding.UTF8.GetString(CanonicalRegistrationEncoding.Bytes(composerBaseline))
                   .Contains("\"instructions\"", StringComparison.Ordinal),
            "MDBO composer without section requests does not add an instructions property.");

        var widgetComposer = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            new FamilyAdapter(), widgetSources, rules, null, widget.Requests);
        var archivePipeline = await CanonicalRelationshipRegistrationPipeline.RegisterAsync(
            new FamilyAdapter(), archiveSources, rules, archive.Requests);
        CanonicalRegistrationCandidateVerifier.Verify(widgetComposer);
        CanonicalRegistrationCandidateVerifier.Verify(archivePipeline);
        Assert(widgetComposer.PublicationState == CanonicalRegistrationEncoding.NotPublished &&
               archivePipeline.PublicationState == CanonicalRegistrationEncoding.NotPublished,
            "MDBO composer instruction candidates remain NOT_PUBLISHED.");
        TraceRegistered(widgetComposer, widgetSources, "widget.install", "## Installation",
            "Install through the manager. Enable Widget Overlay.esm last.", Assert);
        TraceRegistered(archivePipeline, archiveSources, "tool.install", "/instructions/installation",
            "Copy archive-repair.exe next to the game executable.", Assert);
        Assert(widgetComposer.Rulings.Any(value => value.Reason == "instruction-guidance-conflicting") &&
               archivePipeline.Rulings.Any(value => value.Reason == "instruction-section-absent"),
            "Composer path retains conflicting and proven-absence instruction rulings.");
        return checks;
    }

    /// <summary>
    /// Records the remaining LIF-10 product-integration gap: no in-repo admitted
    /// game/mod/tool source uniquely identifies GR-20 instruction locators.
    /// Does not invent headings, admit operator docs, or mark integration done.
    /// If a unique source-owned locator later appears in the admitted trees,
    /// this check fails so that locator can be wired instead of ignored.
    /// </summary>
    private static int RunRealAdmittedSourceBoundary()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Operational instruction real-source boundary: " + message);
            checks++;
        }

        var root = LocateRepositoryRoot();
        var srcRoot = Path.Combine(root, "src");
        var adapterFiles = Directory.GetFiles(srcRoot, "*RegistrationEvidenceAdapter*.cs", SearchOption.AllDirectories);
        var productionAdapters = adapterFiles
            .Where(path => !string.Equals(Path.GetFileName(path), "IRegistrationEvidenceAdapter.cs", StringComparison.Ordinal))
            .ToArray();
        Assert(adapterFiles.Any(path => string.Equals(Path.GetFileName(path), "IRegistrationEvidenceAdapter.cs",
                StringComparison.Ordinal)) &&
               productionAdapters.Length == 2 &&
               productionAdapters.All(path => path.Contains("Grid.GtaV.Knowledge" + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal)),
            "The only production IRegistrationEvidenceAdapter implementations are the GTA location adapters.");

        var locationAdapter = File.ReadAllText(Path.Combine(srcRoot, "Grid.GtaV.Knowledge",
            "GtaVLocationRegistrationEvidenceAdapter.cs"));
        var hierarchyAdapter = File.ReadAllText(Path.Combine(srcRoot, "Grid.GtaV.Knowledge",
            "GtaVLocationHierarchyRegistrationEvidenceAdapter.cs"));
        Assert(locationAdapter.Contains("do not match the pinned corpus and native inventory.", StringComparison.Ordinal) &&
               hierarchyAdapter.Contains("do not match the pinned corpus index.", StringComparison.Ordinal),
            "GTA adapters pin the admitted location corpus and reject unexpected sources.");

        var refreshAuthor = File.ReadAllText(Path.Combine(srcRoot, "Grid.GtaV.Knowledge",
            "GtaVRegistrationMdboRefreshAuthor.cs"));
        var rebuildAuthor = File.ReadAllText(Path.Combine(srcRoot, "Grid.GtaV.Knowledge",
            "GtaVRegistrationPreparedNavigationRebuild.cs"));
        Assert(!ContainsInstructionSectionSupply(refreshAuthor) && !ContainsInstructionSectionSupply(rebuildAuthor),
            "GTA MDBO authors do not invent or supply instruction locators.");

        var skyrimRoot = Path.Combine(root, "scripts", "games", "skyrimspecialedition");
        Assert(Directory.Exists(skyrimRoot) &&
               Directory.GetFiles(skyrimRoot, "*RegistrationEvidenceAdapter*", SearchOption.AllDirectories).Length == 0 &&
               Directory.GetFiles(srcRoot, "*RegistrationEvidenceAdapter*.cs", SearchOption.AllDirectories)
                   .All(path => !path.Contains("skyrim", StringComparison.OrdinalIgnoreCase)),
            "Skyrim has tool-registry material but no registration evidence adapter.");

        var admittedReferences = Path.Combine(root, "scripts", "games", "grandtheftautov", "catalog", "references");
        Assert(Directory.Exists(admittedReferences), "The admitted GTA registration reference tree is present.");
        var headingHits = FindExactInstructionHeadings(admittedReferences);
        Assert(headingHits.Count == 0,
            "Admitted GTA location/wiki/native references uniquely identify GR-20 headings; wire those locators instead of reporting this gap. Hits: " +
            string.Join("; ", headingHits.Take(8)));
        var instructionJsonHits = FindInstructionJsonObjects(admittedReferences);
        Assert(instructionJsonHits.Count == 0,
            "Admitted GTA JSON uniquely identifies /instructions pointers; wire those locators instead of reporting this gap. Hits: " +
            string.Join("; ", instructionJsonHits.Take(8)));

        var foreverTogether = Path.Combine(root, "scripts", "games", "grandtheftautov", "setup", "recipes",
            "forever-together.v1.json");
        var toolRegistry = Path.Combine(root, "scripts", "games", "skyrimspecialedition", "health",
            "tool-registry.v2.json");
        var guidedSetup = Path.Combine(root, "docs", "gta-v-guided-setup.md");
        Assert(File.Exists(foreverTogether) && File.Exists(toolRegistry) && File.Exists(guidedSetup),
            "Near-miss operator/recipe/registry files exist in-repo.");
        Assert(!foreverTogether.StartsWith(admittedReferences, StringComparison.Ordinal) &&
               !toolRegistry.StartsWith(admittedReferences, StringComparison.Ordinal) &&
               !guidedSetup.StartsWith(admittedReferences, StringComparison.Ordinal),
            "Near-miss setup recipe, Skyrim tool-registry, and guided-setup docs are not admitted registration sources.");
        using (var recipe = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(foreverTogether)))
            Assert(recipe.RootElement.TryGetProperty("recipeId", out _) &&
                   recipe.RootElement.TryGetProperty("configuration", out var configuration) &&
                   configuration.ValueKind == System.Text.Json.JsonValueKind.Array,
                "forever-together.v1.json is a setup recipe with a configuration array, not admitted instruction text.");
        using (var registry = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(toolRegistry)))
        {
            var firstCompatibility = registry.RootElement.GetProperty("tools")[0].GetProperty("compatibility")[0];
            Assert(firstCompatibility.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   firstCompatibility.TryGetProperty("gameId", out _) &&
                   firstCompatibility.TryGetProperty("evidenceCapabilityIds", out _),
                "Skyrim tool-registry compatibility is capability-routing metadata, not operational instruction wording.");
        }

        return checks;
    }

    private static bool ContainsInstructionSectionSupply(string source) =>
        source.Contains("OperationalInstructionSectionRequest", StringComparison.Ordinal) ||
        source.Contains("instructionSections", StringComparison.Ordinal);

    private static readonly string[] InstructionHeadingTitles =
    [
        "Installation", "Configuration", "Usage", "Compatibility", "Update", "Uninstall", "Troubleshooting",
    ];

    private static List<string> FindExactInstructionHeadings(string directory)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".md" or ".txt")) continue;
            var lines = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var level = 0;
                while (level < line.Length && line[level] == '#') level++;
                if (level is < 1 or > 6 || level >= line.Length || line[level] != ' ') continue;
                var title = line[(level + 1)..];
                if (InstructionHeadingTitles.Contains(title, StringComparer.Ordinal))
                    hits.Add(Path.GetRelativePath(directory, file) + ":" + (i + 1) + ":" + line);
            }
        }
        return hits;
    }

    private static List<string> FindInstructionJsonObjects(string directory)
    {
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            if (text.Contains("\"instructions\"", StringComparison.Ordinal))
                hits.Add(Path.GetRelativePath(directory, file));
        }
        return hits;
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

        throw new InvalidOperationException("Repository root is unavailable for operational-instruction real-source checks.");
    }

    private static void TraceRegistered(
        CanonicalRegistrationCandidate candidate,
        IReadOnlyList<RegistrationSourceArtifact> sources,
        string requestId,
        string locator,
        string expectedSpan,
        Action<bool, string> assert)
    {
        var instruction = candidate.Instructions!.Single(value => value.Id == requestId);
        var claim = candidate.Input.Evidence.Instructions!.Single(value => value.Id == requestId);
        var evidence = candidate.Input.Evidence.Evidence.Single(value => value.Id == instruction.EvidenceIds.Single());
        var source = sources.Single(value => value.Source.Id == instruction.SourceId);
        var text = Utf8.GetString(source.Bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        assert(instruction.Locator == locator && claim.Locator == locator && evidence.Locator == locator &&
               evidence.SourceId == instruction.SourceId,
            requestId + " registration evidence keeps source identity and locator.");
        assert(instruction.Applicability.Length > 0 && instruction.Applicability.All(value =>
                value.EvidenceIds.Contains(evidence.Id, StringComparer.Ordinal)),
            requestId + " registration applicability is evidence-backed.");
        assert(instruction.VerbatimText.Contains(expectedSpan, StringComparison.Ordinal) &&
               text.Contains(instruction.VerbatimText, StringComparison.Ordinal) &&
               instruction.ContentSha256 == OperationalInstructionInspection.ContentDigest(instruction.VerbatimText),
            requestId + " registration wording is exact admitted source evidence.");
    }

    private static void TracePresent(
        CanonicalOperationalInstructionCandidate candidate,
        IReadOnlyList<RegistrationSourceArtifact> sources,
        string requestId,
        string locator,
        string expectedSpan,
        Action<bool, string> assert)
    {
        var instruction = candidate.Instructions.Single(value => value.Id == requestId);
        var evidence = candidate.Evidence.Single(value => value.Id == instruction.EvidenceIds.Single());
        var source = sources.Single(value => value.Source.Id == instruction.SourceId);
        var text = Utf8.GetString(source.Bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        assert(instruction.Presence == OperationalInstructionSectionPresence.Present, requestId + " is present.");
        assert(instruction.Locator == locator && evidence.Locator == locator && evidence.SourceId == instruction.SourceId,
            requestId + " keeps source identity and locator.");
        assert(instruction.VerbatimText.Contains(expectedSpan, StringComparison.Ordinal) &&
               text.Contains(instruction.VerbatimText, StringComparison.Ordinal),
            requestId + " verbatim wording is exact source evidence.");
        assert(evidence.Basis == (source.Source.Kind is KnowledgeSourceKind.ModProvider or KnowledgeSourceKind.OfficialProvider
                ? EvidenceVerificationKind.ReferenceVerified
                : EvidenceVerificationKind.FileVerified),
            requestId + " verification basis matches the admitted source kind.");
    }

    private static CanonicalRegistrationCandidate UnpublishedCandidate() =>
        new(1, CanonicalRegistrationEncoding.EngineVersion, "1", new string('a', 64), new string('b', 64),
            new RegistrationInput([], new RegistrationRuleSet("1", []), new RegistrationEvidenceSet([], [], [], [])),
            [], [], [], [], CanonicalRegistrationEncoding.NotPublished);

    private static Fixture WidgetOverlayFixture()
    {
        var readme = """
            # Widget Overlay

            ## Installation
            Install through the manager. Enable Widget Overlay.esm last.

            ## Configuration
            Set overlayOpacity to 0.4 in WidgetOverlay.ini.

            ## Usage
            Press F8 to toggle the overlay.

            ## Compatibility
            Works with Community Overlay Framework 3. Do not use with Legacy Overlay.

            ## Uninstall
            This package does not include uninstall guidance.

            ## Troubleshooting
            If the overlay is blank, disable ENB first.
            """;
        var forum = """
            # Widget Overlay forum note

            ## Installation
            Do not use a manager. Copy Widget Overlay.esm into Data by hand.
            """;
        var readmeSource = Artifact("src.widget.readme", "mod://widget-overlay/readme",
            KnowledgeSourceKind.ModProvider, "fixture.mod", readme);
        var forumSource = Artifact("src.widget.forum", "mod://widget-overlay/forum",
            KnowledgeSourceKind.ModProvider, "fixture.mod", forum);
        var apply = new RegistrationApplicability("game.fixture.widget", "profile.overlay", []);
        return new("widget-overlay-mod",
            [readmeSource, forumSource],
            [
                Request("widget.install", readmeSource.Source.Id, "## Installation", OperationalInstructionKind.Installation, apply),
                Request("widget.install.forum", forumSource.Source.Id, "## Installation", OperationalInstructionKind.Installation, apply),
                Request("widget.config", readmeSource.Source.Id, "## Configuration", OperationalInstructionKind.Configuration, apply),
                Request("widget.usage", readmeSource.Source.Id, "## Usage", OperationalInstructionKind.Usage, apply),
                Request("widget.compat", readmeSource.Source.Id, "## Compatibility", OperationalInstructionKind.Compatibility, apply),
                Request("widget.update", readmeSource.Source.Id, "## Update", OperationalInstructionKind.Update, apply),
                Request("widget.uninstall", readmeSource.Source.Id, "## Uninstall", OperationalInstructionKind.Uninstall, apply,
                    absenceMarker: "This package does not include uninstall guidance."),
                Request("widget.trouble", readmeSource.Source.Id, "## Troubleshooting", OperationalInstructionKind.Troubleshooting, apply),
            ]);
    }

    private static Fixture ArchiveRepairFixture()
    {
        var guide = """
            {
              "tool": "archive-repair-cli",
              "instructions": {
                "installation": "Copy archive-repair.exe next to the game executable.",
                "configuration": "Create repair.toml with verify=true.",
                "usage": "Run archive-repair.exe --verify then --repair.",
                "compatibility": "Requires the game's shipped archive format v2.",
                "update": { "absent": true },
                "uninstall": "Delete archive-repair.exe and repair.toml.",
                "troubleshooting": "If verify fails, do not repair. Capture the log."
              }
            }
            """;
        var alt = """
            {
              "tool": "archive-repair-cli",
              "instructions": {
                "usage": "Always run --repair before --verify."
              }
            }
            """;
        var guideSource = Artifact("src.tool.guide", "file:///opt/archive-repair/guide.json",
            KnowledgeSourceKind.LocalModArtifact, "fixture.tool", guide);
        var altSource = Artifact("src.tool.alt", "file:///opt/archive-repair/alt.json",
            KnowledgeSourceKind.LocalModArtifact, "fixture.tool", alt);
        var apply = new RegistrationApplicability("game.fixture.archive", null, []);
        return new("archive-repair-cli",
            [guideSource, altSource],
            [
                Request("tool.install", guideSource.Source.Id, "/instructions/installation", OperationalInstructionKind.Installation, apply),
                Request("tool.config", guideSource.Source.Id, "/instructions/configuration", OperationalInstructionKind.Configuration, apply),
                Request("tool.usage", guideSource.Source.Id, "/instructions/usage", OperationalInstructionKind.Usage, apply),
                Request("tool.usage.alt", altSource.Source.Id, "/instructions/usage", OperationalInstructionKind.Usage, apply),
                Request("tool.compat", guideSource.Source.Id, "/instructions/compatibility", OperationalInstructionKind.Compatibility, apply),
                Request("tool.update", guideSource.Source.Id, "/instructions/update", OperationalInstructionKind.Update, apply),
                Request("tool.uninstall", guideSource.Source.Id, "/instructions/uninstall", OperationalInstructionKind.Uninstall, apply),
                Request("tool.trouble", guideSource.Source.Id, "/instructions/troubleshooting", OperationalInstructionKind.Troubleshooting, apply),
                Request("tool.missing", guideSource.Source.Id, "/instructions/changelog", OperationalInstructionKind.Update, apply),
            ]);
    }

    private static OperationalInstructionSectionRequest Request(
        string id, string sourceId, string locator, OperationalInstructionKind kind,
        RegistrationApplicability applicability, string? absenceMarker = null) =>
        new(id, sourceId, locator, kind, [applicability], absenceMarker);

    private static RegistrationSourceArtifact Artifact(
        string id, string uri, KnowledgeSourceKind kind, string family, string text)
    {
        var bytes = Utf8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        return new(new RegistrationSource(id, uri, kind, CanonicalRegistrationEncoding.Digest(bytes),
            "fixture.operational-instruction", "1", family), bytes);
    }

    private static RegistrationSourceArtifact[] WithEntity(
        Fixture fixture, string entityId, string selector, string classification, string nativeId,
        string game, string? profile, string displayName)
    {
        var profileJson = profile is null ? "null" : $"\"{profile}\"";
        var entity = $$"""
            {
              "rows": [
                {
                  "key": "{{entityId}}",
                  "nativeId": "{{nativeId}}",
                  "selector": "{{selector}}",
                  "game": "{{game}}",
                  "classification": "{{classification}}",
                  "kind": "Entity",
                  "names": [{ "text": "{{displayName}}", "locale": "en-US" }],
                  "scopes": [{ "game": "{{game}}", "profile": {{profileJson}} }],
                  "parents": [],
                  "facets": [],
                  "origin": "{{selector}}",
                  "nativeNamespace": "fixture.native"
                }
              ]
            }
            """;
        var entitySource = Artifact(entityId, "mod://entities/" + entityId,
            KnowledgeSourceKind.LocalModArtifact, fixture.Family, entity);
        return [entitySource, .. fixture.Sources];
    }

    private sealed class FamilyAdapter : IRegistrationEvidenceAdapter
    {
        public string Id => "fixture.operational-instruction";
        public string Version => "1";
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new List<RegistrationEvidence>();
            var entities = new List<RegistrationEntityClaim>();
            foreach (var artifact in sources)
            {
                NativeFamily? family;
                try
                {
                    family = System.Text.Json.JsonSerializer.Deserialize<NativeFamily>(
                        artifact.Bytes, CanonicalRegistrationEncoding.Json);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }
                if (family?.Rows is null) continue;
                foreach (var row in family.Rows)
                {
                    var eid = artifact.Source.Id + ":" + row.Key;
                    var basis = artifact.Source.Kind == KnowledgeSourceKind.ModProvider
                        ? EvidenceVerificationKind.ReferenceVerified : EvidenceVerificationKind.FileVerified;
                    evidence.Add(new(eid, artifact.Source.Id, "/rows/" + row.Key, basis));
                    entities.Add(new(row.Key, artifact.Source.Id, row.NativeNamespace, row.NativeId, row.Selector,
                        row.Game, row.Classification, row.Kind,
                        row.Names.Select(n => new RegistrationName(n.Text, n.Locale, n.Alias, [eid])).ToArray(),
                        row.Scopes.Select(s => new RegistrationApplicability(s.Game, s.Profile, [eid])).ToArray(),
                        [new(row.Origin, row.NativeNamespace, row.NativeId, [eid])], [eid]));
                }
            }
            return Task.FromResult(new RegistrationEvidenceSet(evidence.ToArray(), entities.ToArray(), [], []));
        }
    }

    private sealed record NativeName(string Text, string Locale, bool Alias = false);
    private sealed record NativeScope(string Game, string? Profile);
    private sealed record NativeRow(string Key, string NativeId, string Selector, string? Game, string Classification,
        RegistrationEntityKind Kind, NativeName[] Names, NativeScope[] Scopes, string[] Parents,
        object[] Facets, string Origin = "Game", string NativeNamespace = "fixture.native");
    private sealed record NativeFamily(NativeRow[] Rows);

    private sealed record Fixture(string Family, RegistrationSourceArtifact[] Sources,
        OperationalInstructionSectionRequest[] Requests);
}
