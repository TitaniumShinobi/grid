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
