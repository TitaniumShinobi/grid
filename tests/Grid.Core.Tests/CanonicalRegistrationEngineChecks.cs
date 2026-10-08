using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.App.Controls;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalRegistrationEngineChecks
{
    private sealed record OracleNode(string Id, string Selector, string? ParentId, string Label, bool Terminal, string[] Views);
    private sealed record NativeName(string Text, string Locale, bool Alias = false);
    private sealed record NativeScope(string Game, string? Profile);
    private sealed record NativeFacet(string Dimension, string Value, string Locale, bool Filter = false);
    private sealed record NativeRow(string Key, string NativeId, string Selector, string? Game, string Classification,
        RegistrationEntityKind Kind, NativeName[] Names, NativeScope[] Scopes, string[] Parents,
        NativeFacet[] Facets, string Origin = "Game", string? ExistingRecordId = null, string? ExistingPackageId = null,
        string NativeNamespace = "fixture.native");
    private sealed record NativeFamily(NativeRow[] Rows);

    // A deliberately small source-family adapter: raw native rows are the input, never a projected menu.
    private sealed class FixtureAdapter(bool reverse = false, bool flipBasis = false, bool mutateSource = false) : IRegistrationEvidenceAdapter
    {
        public string Id => "fixture.family-json";
        public string Version => "1";
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new List<RegistrationEvidence>();
            var entities = new List<RegistrationEntityClaim>();
            var relationships = new List<RegistrationRelationshipClaim>();
            var facets = new List<RegistrationFacetClaim>();
            foreach (var artifact in sources)
            {
                var family = JsonSerializer.Deserialize<NativeFamily>(artifact.Bytes, CanonicalRegistrationEncoding.Json)
                    ?? throw new InvalidDataException("Empty source family fixture.");
                foreach (var row in family.Rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var eid = artifact.Source.Id + ":" + row.Key;
                    var basis = artifact.Source.Kind == KnowledgeSourceKind.ReferenceProvider
                        ? EvidenceVerificationKind.ReferenceVerified : EvidenceVerificationKind.FileVerified;
                    evidence.Add(new(eid, artifact.Source.Id, "/rows/" + row.Key,
                        flipBasis ? (basis == EvidenceVerificationKind.FileVerified ? EvidenceVerificationKind.ReferenceVerified : EvidenceVerificationKind.FileVerified) : basis));
                    entities.Add(new(row.Key, artifact.Source.Id, row.NativeNamespace, row.NativeId, row.Selector, row.Game,
                        row.Classification, row.Kind,
                        row.Names.Select(n => new RegistrationName(n.Text, n.Locale, n.Alias, [eid])).ToArray(),
                        row.Scopes.Select(s => new RegistrationApplicability(s.Game, s.Profile, [eid])).ToArray(),
                        [new(row.Origin, "fixture.native", row.NativeId, [eid])], [eid], row.ExistingRecordId, row.ExistingPackageId));
                    foreach (var parent in row.Parents)
                        relationships.Add(new("edge:" + row.Key + ":" + parent, row.Key, parent, "contained-by", [eid],
                            artifact.Source.Id, "fixture.contained-by", "/rows/" + row.Key + "/parent/" + parent));
                    foreach (var facet in row.Facets)
                        facets.Add(new("facet:" + row.Key + ":" + facet.Dimension, row.Key,
                            facet.Filter ? RegistrationFacetKind.Filter : RegistrationFacetKind.AlternateView,
                            facet.Dimension, facet.Value, facet.Locale, [eid]));
                }
            }
            if (reverse) { evidence.Reverse(); entities.Reverse(); relationships.Reverse(); facets.Reverse(); }
            if (mutateSource && sources.Count > 0) sources[0].Bytes[0] ^= 1;
            return Task.FromResult(new RegistrationEvidenceSet(evidence.ToArray(), entities.ToArray(), relationships.ToArray(), facets.ToArray()));
        }
    }

    public static async Task<int> RunAsync(string repositoryRoot, string outputRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        var allowed = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp/grid-contract2-registration-20261003/proof"));
        if (outputRoot != allowed && !outputRoot.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Proof output must remain inside the approved isolated Contract 2 proof directory.");
        Directory.CreateDirectory(outputRoot);
        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Registration proof: " + reason);
            checks++;
        }
        async Task Reject(Func<Task> action, string reason)
        {
            try { await action(); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or KeyNotFoundException or OperationCanceledException)
            { checks++; return; }
            throw new InvalidOperationException("Registration proof expected rejection: " + reason);
        }

        var oracle = BuildOracle(repositoryRoot, Check);
        var (sources, rules) = CreateFixtures(oracle);
        await Reject(() => CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(flipBasis: true), sources, rules),
            "Adapter cannot promote reference facts to file-verified or demote shipped facts through forged basis.");
        await Reject(() => CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(mutateSource: true), sources, rules),
            "Adapter cannot mutate receipt-bound source bytes during extraction.");
        await Reject(() => CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(), sources.Select((s, i) =>
            i == 0 ? s with { Source = s.Source with { Sha256 = new string('0', 64) } } : s).ToArray(), rules),
            "Wrong source digest fails before extraction.");
        var candidate = await CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(), sources, rules);
        CanonicalRegistrationCandidateVerifier.Verify(candidate);
        Check(candidate.PublicationState == CanonicalRegistrationEncoding.NotPublished, "Proof artifacts cannot claim publication.");
        var reordered = await CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(true), sources.Reverse().ToArray(), rules with { Rules = rules.Rules.Reverse().ToArray() });
        Check(CanonicalRegistrationEncoding.Bytes(candidate).SequenceEqual(CanonicalRegistrationEncoding.Bytes(reordered)),
            "Candidate bytes must be identical after source, adapter-output and rule ordering changes.");
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "canonical-candidate.json"), CanonicalRegistrationEncoding.Bytes(candidate));
        Directory.CreateDirectory(Path.Combine(outputRoot, "repeat"));
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "repeat", "canonical-candidate.json"), CanonicalRegistrationEncoding.Bytes(reordered));
        Check((await File.ReadAllBytesAsync(Path.Combine(outputRoot, "canonical-candidate.json"))).SequenceEqual(
            await File.ReadAllBytesAsync(Path.Combine(outputRoot, "repeat", "canonical-candidate.json"))), "Persisted canonical candidate files are byte-identical.");
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "rules.json"), CanonicalRegistrationEncoding.Bytes(rules));
        var fixtureDirectory = Path.Combine(outputRoot, "source-families");
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var source in sources) await File.WriteAllBytesAsync(Path.Combine(fixtureDirectory, source.Source.Id + ".json"), source.Bytes);

        RegisteredCanonicalEntity Entity(string key) => candidate.Entities.Single(e => e.SourceKeys.Contains(key, StringComparer.Ordinal));
        Check(candidate.Entities.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == candidate.Entities.Length, "Canonical identities remain unique.");
        foreach (var terminal in oracle.Where(n => n.Terminal))
            Check(candidate.Entities.Any(e => e.Anchor == terminal.Id && e.Kind == RegistrationEntityKind.Entity), "Independent terminal target is populated: " + terminal.Id);
        Check(candidate.Rulings.Any(r => r.ClaimId == "preserved" && r.Outcome == RegistrationOutcome.Unresolved) &&
              !candidate.Entities.Any(e => e.Id == "existing.canonical.fixture"),
            "An unverified caller-supplied historical ID cannot override canonical identity.");
        Check(candidate.Input.Sources.Any(s => s.Kind == KnowledgeSourceKind.LocalGameDistribution) &&
              candidate.Input.Sources.Any(s => s.Kind == KnowledgeSourceKind.ReferenceProvider) &&
              candidate.Input.Evidence.Evidence.Any(e => e.Basis == EvidenceVerificationKind.ReferenceVerified),
            "File and reference source provenance remain distinct.");
        Check(candidate.Rulings.Any(r => r.ClaimId == "unmapped" && r.Outcome == RegistrationOutcome.Unresolved), "Unknown classification remains unresolved.");
        Check(candidate.Rulings.Any(r => r.ClaimId == "ambiguous-map" && r.Outcome == RegistrationOutcome.Ambiguous), "Competing mappings remain ambiguous.");
        Check(candidate.Rulings.Any(r => r.Outcome == RegistrationOutcome.Rejected && r.ClaimId.StartsWith("edge:cycle", StringComparison.Ordinal)),
            "Cyclic hierarchy evidence is rejected.");
        Check(candidate.Rulings.Any(r => r.ClaimId == "edge:orphan:no-such-row" && r.Outcome != RegistrationOutcome.Correlated), "Missing relationship endpoints remain explicit.");
        Check(candidate.Rulings.Any(r => r.ClaimId == "facet:bad-facet:Imagined Dimension" && r.Outcome != RegistrationOutcome.Correlated),
            "An unsupported facet does not invent a view.");
        var alias = CanonicalRegistrationEngine.ResolveAlias(candidate, "Location", "Shared nickname", "en-US");
        Check(alias.Length == 2 && alias.Distinct(StringComparer.Ordinal).Count() == 2, "Ambiguous aliases return all exact identities without merging.");
        Check(CanonicalRegistrationEngine.ResolveAlias(candidate, "Location", "Unique source nickname", "en-US").SequenceEqual([Entity("alias-a").Id]),
            "An exact unique source alias resolves its single canonical identity.");
        Check(CanonicalRegistrationEngine.ResolveAlias(candidate, "Location", "Shared nickname", "fr-FR").Length == 0, "Alias resolution never substitutes another locale.");
        Check(Entity("technical").Kind == RegistrationEntityKind.TechnicalArtifact && Entity("variant").Kind == RegistrationEntityKind.Variant,
            "Technical artifacts and variants remain represented underneath presentation.");
        Check(Entity("shared-tool").GameId is null && Entity("shared-tool").Applicability.Select(a => a.GameId).Distinct().Count() == 2,
            "A shared Tool has one identity with evidence for two games.");
        Check(Entity("shared-mod").Origins.Single().Kind == "Mod", "Mod origin remains evidence-backed native identity.");
        Check(Entity("duplicate-a").Id == Entity("duplicate-b").Id && candidate.Rulings.Any(r => r.Reason == "conflicting-primary-names"),
            "Duplicate native identity retains its conflicting source names rather than choosing one.");
        var inputBytes = CanonicalRegistrationEncoding.Bytes(candidate.Input);
        Check(inputBytes.Length > 0 && candidate.Relationships.All(r => r.EvidenceIds.Length > 0 && r.EvidenceIds.All(id => candidate.Input.Evidence.Evidence.Any(e => e.Id == id))),
            "Every admitted relationship retains its complete evidence linkage.");

        var contexts = new[] { new RegistrationNavigationContext("game.fixture.alpha", "profile.a", "en-US"),
            new RegistrationNavigationContext("game.fixture.alpha", "profile.b", "en-US"),
            new RegistrationNavigationContext("game.fixture.beta", "profile.a", "en-US"),
            new RegistrationNavigationContext("game.fixture.beta", "profile.b", "en-US"),
            new RegistrationNavigationContext("game.fixture.alpha", "profile.a", "fr-FR") };
        var prepared = new List<RegistrationPreparedCandidate>();
        var reports = new List<object>();
        foreach (var context in contexts)
        {
            var result = await CanonicalRegistrationPreparationBuilder.BuildAsync(Path.Combine(outputRoot, "prepared"), candidate, context);
            var again = await CanonicalRegistrationPreparationBuilder.BuildAsync(Path.Combine(outputRoot, "prepared-repeat"), reordered, context);
            Check(result.Digest == again.Digest, "Prepared generation digest is deterministic for " + context);
            prepared.Add(result);
            using var reader = await CanonicalRegistrationPreparedReader.OpenAsync(result.DirectoryPath, result.Digest, context);
            Check(reader.Statistics.PageReads == 0 && reader.Statistics.RowsMaterialized == 0, "Opening the descriptor must not materialize navigation pages.");
            var firstRoot = reader.Descriptor.Views.First(v => v.Id == "Location").RootPathId;
            var first = await reader.GetChildrenAsync(firstRoot);
            Check(first.Children.Length <= 64 && reader.Statistics.PageReads == 1 && reader.Statistics.RowsMaterialized <= 64,
                "A root request retrieves only one bounded level page.");
            var rows = await Traverse(reader);
            Check(rows.All(r => !r.Selectable || r.EntityId is not null), "Only real canonical identities can be selected.");
            Check(rows.Where(r => r.Selectable).All(r => !string.IsNullOrWhiteSpace(r.Label)), "No unnamed selectable rows are materialized.");
            Check(rows.All(r => r.EntityId != Entity("duplicate-a").Id && r.EntityId != Entity("unnamed").Id),
                "Conflicting or missing primary terminology stays hidden from normal navigation.");
            Check(rows.All(r => r.Label is not ("Type/Class" or "Equipment Type" or "Weapon" or "Job" or "Room")),
                "Empty entity slots and dynamic category placeholders cannot become invented menu rows.");
            Check(rows.Where(r => r.Selectable).All(r => r.EntityId != Entity("technical").Id && r.EntityId != Entity("variant").Id),
                "Technical records and asset variants do not leak into normal navigation.");
            Check(rows.Where(r => r.Selectable).All(r => candidate.Entities.Single(e => e.Id == r.EntityId).Names.Any(n => !n.IsAlias && n.Locale == context.Locale && n.Value == r.Label)),
                "Every selectable label is exact source-owned terminology for the requested locale.");
            Check(rows.Where(r => r.Selectable).All(r => candidate.Entities.Single(e => e.Id == r.EntityId).Applicability.Any(a => a.GameId == context.GameId && (a.ProfileId is null || a.ProfileId == context.ProfileId))),
                "Every selected row respects game and profile applicability.");
            foreach (var row in rows.Where(r => r.Selectable))
            {
                Check(await reader.ValidateSelectionAsync(row.PathId, row.EntityId!), "Path/record pair validates directly.");
                Check(!await reader.ValidateSelectionAsync(row.PathId, "forged.record"), "Forged record selection is rejected.");
            }
            Check(!await reader.ValidateSelectionAsync("forged.path", Entity("shared-tool").Id), "Unknown selected paths cannot resolve.");
            await Reject(() => reader.GetChildrenAsync("forged.path"), "Unknown path retrieval");
            await Reject(() => reader.GetChildrenAsync(firstRoot, "forged.cursor"), "Forged continuation cursor");
            await Reject(() => CanonicalRegistrationPreparedReader.OpenAsync(result.DirectoryPath, result.Digest, context with { Locale = "xx-XX" }), "Wrong locale binding");
            var chosen = rows.FirstOrDefault(r => r.Selectable);
            if (chosen is not null)
            {
                var evidence = await reader.GetEvidenceAsync(chosen.PathId);
                Check(evidence.Select(e => e.Id).Order().SequenceEqual(chosen.EvidenceIds.Order()), "Selected evidence is retrievable independently from navigation rows.");
                var parent = await reader.GetParentAsync(chosen.PathId);
                Check(parent?.PathId == chosen.ParentPathId, "Back resolves the exact parent directly.");
            }
            var initialReads = reader.Statistics.PageReads;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reader.GetChildrenAsync(firstRoot)));
            Check(reader.Statistics.PageReads == initialReads, "Concurrent repeated navigation reuses cached immutable pages.");
            Check(reader.CachedBytes <= CanonicalRegistrationPreparedReader.MaximumCachedBytes, "Decoded page cache remains within its fixed bound.");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Reject(() => reader.GetChildrenAsync(firstRoot, cancellationToken: cancelled.Token), "Cancelled page read");
            if (context == contexts[0])
            {
                var preparedDescriptor = new PreparedCanonicalGenerationDescriptor(
                    1,
                    new CatalogPackageId("registration-engine.fixture"),
                    new CatalogRevisionId("revision.registration-engine.fixture"),
                    new CatalogCompositionId("composition.registration-engine.fixture"),
                    1,
                    new GameId(context.GameId),
                    CatalogValidationStatus.Passed,
                    null,
                    null,
                    null,
                    new string('f', 64),
                    new CanonicalTerminologyLocalePreference(context.Locale, [context.Locale]),
                    []);
                var composerReader = await CanonicalRegistrationPreparedReader.OpenAsync(result.DirectoryPath, result.Digest, context);
                var composer = new RegistrationPreparedNavigationComposer(composerReader, preparedDescriptor);
                var rootPage = await composer.QueryAsync(null, null, CancellationToken.None);
                Check(rootPage is not null, "Registration prepared composer serves the mold Location root.");
                var moldRootPath = rootPage!.Result.CurrentPathId;
                var repeatRoot = await composer.QueryAsync(moldRootPath, null, CancellationToken.None);
                Check(repeatRoot is not null && repeatRoot!.Result.CurrentPathId == moldRootPath &&
                    repeatRoot.Result.ImmediateChildren.Length == rootPage.Result.ImmediateChildren.Length,
                    "Repeating the mold Location root query does not fall through to canonical prepared navigation.");
                composerReader.Dispose();
                var wearables = rows.Single(r => r.EntityId is null && r.Selector == "Item" && r.Label == "Wearables");
                var wearableLevel = await reader.GetChildrenAsync(wearables.PathId);
                var expectedWearableOrder = SelectorScaffoldContract.Resolve(KnowledgeKind.Item, "wearables")!.Children
                    .Where(n => n.Key is "wearables/head" or "wearables/top" or "wearables/bottom").Select(n => n.Label);
                Check(wearableLevel.Children.Where(r => r.EntityId is null).Select(r => r.Label).SequenceEqual(expectedWearableOrder),
                    "Fixed Wearables categories retain the independent scaffold order Head, Top, Bottom instead of alphabetizing.");
                foreach (var terminal in oracle.Where(n => n.Terminal))
                    Check(rows.Any(r => r.Selectable && candidate.Entities.Single(e => e.Id == r.EntityId).Anchor == terminal.Id),
                        "Every independent terminal has a prepared source-owned selectable record: " + terminal.Id);
                foreach (var binding in oracle.SelectMany(n => n.Views.Select(d => (n.Id, n.Selector, Dimension: d))))
                {
                    var expectedViewId = "alternate." + CanonicalRegistrationEncoding.Digest(CanonicalRegistrationEncoding.Bytes(new[] { binding.Selector, binding.Id, binding.Dimension, context.Locale }));
                    var view = reader.Descriptor.Views.SingleOrDefault(v => v.Id == expectedViewId);
                    Check(view is not null && view.Dimension == binding.Dimension, "Prepared alternate view preserves each of the twelve anchor-specific bindings.");
                    var actualIds = rows.Where(r => r.EntityId is not null && DescendsFrom(r.PathId, view!.RootPathId, reader.Descriptor)).Select(r => r.EntityId!).ToHashSet(StringComparer.Ordinal);
                    var expectedIds = candidate.Entities.Where(e => (e.Anchor == binding.Id || e.Anchor.StartsWith(binding.Id + "/", StringComparison.Ordinal)) &&
                        e.Applicability.Any(a => a.GameId == context.GameId && (a.ProfileId is null || a.ProfileId == context.ProfileId)) &&
                        e.Names.Any(n => !n.IsAlias && n.Locale == context.Locale)).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                    Check(actualIds.SetEquals(expectedIds), "Alternate view retains the exact applicable canonical identity set, including unresolved facet values.");
                }
                var uncategorized = reader.Descriptor.Views.Single(v => v.Selector == "Mod" && v.Dimension == "Category" && v.Value == "Uncategorized");
                Check(rows.Any(r => r.EntityId == Entity("uncategorized-mod").Id && DescendsFrom(r.PathId, uncategorized.RootPathId, reader.Descriptor)),
                    "Mod without category remains selectable in the explicit uncategorized filter.");
                var shared = rows.Where(r => r.EntityId == Entity("multi-parent").Id).ToArray();
                Check(shared.Select(r => r.ParentPathId).Distinct().Count() >= 2, "One entity retains multiple evidence-backed parent paths.");
                Check(rows.Any(r => r.EntityId == Entity("deep-11").Id), "Source-owned hierarchy deeper than the universal scaffold survives.");
                var namedParent = rows.Single(r => r.EntityId == Entity("selectable-parent").Id);
                var namedChild = rows.Single(r => r.EntityId == Entity("selectable-child").Id);
                Check(namedParent.Selectable && namedParent.CanDescend && namedChild.Selectable && namedChild.ParentPathId == namedParent.PathId,
                    "Named entity parent supports selection and descent independently; its child remains selectable.");
                var equalLabels = rows.Where(r => r.Label == "Same source label" && r.Selectable).ToArray();
                Check(equalLabels.Length == 2 && equalLabels.Select(r => r.EntityId).Distinct().Count() == 2,
                    "Identical primary names on different native identities remain separate selectable records.");
                var deep = rows.Single(r => r.EntityId == Entity("deep-11").Id);
                Check(Ancestors(deep.PathId, reader.Descriptor).Length >= 13, "Actual prepared ancestry retains the twelve-level source chain.");
                foreach (var relationship in candidate.Relationships)
                {
                    var paths = rows.Where(r => r.EntityId == relationship.SubjectId && r.ParentPathId is not null &&
                        reader.Descriptor.Paths.Single(p => p.PathId == r.ParentPathId).EntityId == relationship.TargetId).ToArray();
                    foreach (var path in paths)
                    {
                        var preservedEvidence = await reader.GetEvidenceAsync(path.PathId);
                        Check(relationship.EvidenceIds.All(id => preservedEvidence.Any(e => e.Id == id)), "Prepared parent/child path retains every relationship evidence reference.");
                    }
                }
                Check(rows.Any(r => r.EntityId == Entity("shared-tool").Id) && rows.Any(r => r.EntityId == Entity("shared-mod").Id), "Shared Tool and Mod appear for applicable profile A.");
                Check(!rows.Any(r => r.EntityId == Entity("french-only").Id), "French-only source names do not silently appear in English.");
                var widePath = reader.Descriptor.Paths.Single(p => p.EntityId == Entity("wide").Id && p.PageDigests.Length > 1);
                var wideFirst = await reader.GetChildrenAsync(widePath.PathId);
                Check(wideFirst.Children.Length == 64 && wideFirst.ContinuationCursor is not null, "Wide sibling sets begin with exactly one 64-row page.");
                Check(wideFirst.Children.Select(r => r.Label).SequenceEqual(wideFirst.Children.Select(r => r.Label).Order(StringComparer.OrdinalIgnoreCase)),
                    "Source-owned sibling rows remain deterministically alphabetical while fixed scaffold categories retain contract order.");
                var wideSecond = await reader.GetChildrenAsync(widePath.PathId, wideFirst.ContinuationCursor);
                Check(wideSecond.Children.Length == 64 && !wideFirst.Children.Select(r => r.PathId).Intersect(wideSecond.Children.Select(r => r.PathId)).Any(), "Continuation retrieves distinct immediate siblings.");
                await Reject(() => reader.GetChildrenAsync(firstRoot, wideFirst.ContinuationCursor), "Cursor cannot be used on another level");
            }
            reports.Add(new { context, generation = result.Digest, rows = rows.Count, selectableIdentities = rows.Where(r => r.Selectable).Select(r => r.EntityId).Distinct().Count(), reader.Statistics,
                paths = rows.Where(r => r.Selectable).Take(12).Select(r => new { r.PathId, r.ParentPathId, r.Label, r.EntityId }) });
        }
        var receipt = CanonicalRegistrationEngine.CreateReceipt(candidate, prepared.ToArray());
        var receiptAgain = CanonicalRegistrationEngine.CreateReceipt(reordered, prepared.AsEnumerable().Reverse().ToArray());
        Check(CanonicalRegistrationEncoding.Bytes(receipt).SequenceEqual(CanonicalRegistrationEncoding.Bytes(receiptAgain)), "Receipt bytes are deterministic independent of preparation order.");
        Check(receipt.PublicationState == CanonicalRegistrationEncoding.NotPublished && receipt.ValidationResult == "PASSED", "Proof receipt establishes validation without publication.");
        VerifyRefresh(candidate, prepared.ToArray(), Check);
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "receipt.json"), CanonicalRegistrationEncoding.Bytes(receipt));
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "repeat", "receipt.json"), CanonicalRegistrationEncoding.Bytes(receiptAgain));
        Check((await File.ReadAllBytesAsync(Path.Combine(outputRoot, "receipt.json"))).SequenceEqual(
            await File.ReadAllBytesAsync(Path.Combine(outputRoot, "repeat", "receipt.json"))), "Persisted registration receipts are byte-identical.");
        await VerifyFakePublication(candidate, prepared[0], outputRoot, Check, Reject);
        await VerifyCorruption(prepared[0], outputRoot, Reject);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject(() => CanonicalRegistrationEngine.RegisterAsync(new FixtureAdapter(), sources, rules, cancelled.Token), "Cancelled registration");
            await Reject(() => CanonicalRegistrationPreparationBuilder.BuildAsync(Path.Combine(outputRoot, "cancelled"), candidate, contexts[0], cancelled.Token), "Cancelled preparation");
        }
        var tampered = candidate with { Entities = candidate.Entities.Select((e, i) => i == 0 ? e with { EvidenceIds = ["missing.evidence"] } : e).ToArray() };
        await Reject(() => { CanonicalRegistrationCandidateVerifier.Verify(tampered); return Task.CompletedTask; }, "Corrupt candidate cannot be verified");
        await File.WriteAllBytesAsync(Path.Combine(outputRoot, "proof-report.json"), CanonicalRegistrationEncoding.Bytes(new
        { checks, result = "PASS", publicationState = CanonicalRegistrationEncoding.NotPublished, oracleNodes = oracle.Length,
            oracleTerminals = oracle.Count(n => n.Terminal), oracleViewBindings = oracle.Sum(n => n.Views.Length), contexts = reports }));
        Console.WriteLine($"PASS: {checks} registration engine checks; 98 independent nodes, 33 terminal targets, 12 view bindings; NOT_PUBLISHED.");
        return checks;
    }

    private static OracleNode[] BuildOracle(string repository, Action<bool, string> check)
    {
        var oracle = new List<OracleNode> { new("Tool", "Tool", null, "Tool", true, []), new("Mod", "Mod", null, "Mod", true, []) };
        foreach (var kind in new[] { KnowledgeKind.Location, KnowledgeKind.MissionQuest, KnowledgeKind.Item, KnowledgeKind.Actor })
        {
            var selector = kind.ToString();
            oracle.Add(new(selector, selector, null, SelectorScaffoldContract.Title(kind), false, []));
            foreach (var node in SelectorScaffoldContract.Enumerate(kind))
            {
                var parent = node.Key.Contains('/') ? selector + "/" + node.Key[..node.Key.LastIndexOf('/')] : selector;
                oracle.Add(new(selector + "/" + node.Key, selector, parent, node.Label, node.Children.IsEmpty, node.SortViews.ToArray()));
            }
        }
        check(oracle.Count == 98 && oracle.Count(n => n.Terminal) == 33, "Existing UI independently defines exactly 98 nodes and 33 terminal targets including Tool/Mod.");
        check(oracle.Sum(n => n.Views.Length) == 12 && oracle.SelectMany(n => n.Views).Distinct().Count() == 6, "Existing UI independently defines twelve bindings for six sort dimensions.");
        var markdown = File.ReadAllText(Path.Combine(repository, "GRID.md")).Replace("\r\n", "\n", StringComparison.Ordinal);
        var section = markdown[markdown.IndexOf("Tool\n- Reliant on profile", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("Goal (deterministic resolution)", StringComparison.Ordinal)];
        var sourceLabels = section.Split('\n').Select(line => Regex.Replace(line.Trim(), "^-+\\s*", string.Empty))
            .Select(line => Regex.Replace(line, "\\.esl.*$", string.Empty)).ToHashSet(StringComparer.Ordinal);
        foreach (var node in oracle.Where(n => n.ParentId is not null))
            check(sourceLabels.Contains(node.Label), "GRID.md independently retains exact node spelling: " + node.Id);
        check(section.Contains("Mod (optional category filtering)", StringComparison.Ordinal), "GRID.md specifies the single Mod category filter.");
        using var checklist = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "src/Grid.Core/Contracts/canonical-registration-checklist.v1.json")));
        var actual = checklist.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        check(actual.Length == oracle.Count, "Machine checklist has no missing or extra nodes.");
        foreach (var expected in oracle)
        {
            var matches = actual.Where(n => n.GetProperty("id").GetString() == expected.Id).ToArray();
            check(matches.Length == 1, "Exactly one checklist node exists for " + expected.Id);
            var node = matches.Single();
            check(node.GetProperty("selector").GetString() == expected.Selector && node.GetProperty("label").GetString() == expected.Label &&
                  node.GetProperty("parentId").GetString() == expected.ParentId && node.GetProperty("terminal").GetBoolean() == expected.Terminal,
                "Checklist preserves independent parent/label/selector/terminal contract: " + expected.Id);
            check(node.GetProperty("sortDimensions").EnumerateArray().Select(v => v.GetString()).SequenceEqual(expected.Views), "Exact source-declared sort binding: " + expected.Id);
        }
        check(actual.Sum(n => n.GetProperty("filterDimensions").GetArrayLength()) == 1 &&
              actual.Single(n => n.GetProperty("id").GetString() == "Mod").GetProperty("filterDimensions")[0].GetString() == "Category",
            "Only Mod owns the optional Category filter.");
        return oracle.ToArray();
    }

    private static (RegistrationSourceArtifact[], RegistrationRuleSet) CreateFixtures(OracleNode[] oracle)
    {
        const string a = "game.fixture.alpha", b = "game.fixture.beta";
        var rows = new List<NativeRow>();
        var rules = oracle.Where(n => n.ParentId is not null || n.Terminal).Select(n =>
            new RegistrationMappingRule("map:" + n.Id, "fixture-rows", n.Selector, "native:" + n.Id, n.Id)).ToList();
        foreach (var game in new[] { a, b })
        foreach (var node in oracle.Where(n => n.ParentId is not null))
        {
            var key = game + ":" + node.Id;
            var parents = node.ParentId is not null && node.ParentId.Contains('/') ? new[] { game + ":" + node.ParentId } : [];
            rows.Add(new(key, key, node.Selector, game, "native:" + node.Id,
                node.Terminal ? RegistrationEntityKind.Entity : RegistrationEntityKind.Category,
                [new("Native " + game + " " + node.Label, "en-US")], [new(game, "profile.a")], parents,
                node.Views.Select(v => new NativeFacet(v, "Source " + v, "en-US")).ToArray()));
        }
        NativeRow Row(string key, string selector = "Location", string? anchor = null, RegistrationEntityKind kind = RegistrationEntityKind.Entity,
            string[]? parents = null, NativeName[]? names = null, NativeScope[]? scopes = null, NativeFacet[]? facets = null) =>
            new(key, key, selector, a, "native:" + (anchor ?? "Location/world"), kind,
                names ?? [new("Source " + key, "en-US")], scopes ?? [new(a, "profile.a")], parents ?? [], facets ?? []);
        rows.Add(Row("shared-tool", "Tool", "Tool", scopes: [new(a, "profile.a"), new(b, "profile.b")]) with { Game = null, Origin = "Tool" });
        rows.Add(Row("shared-mod", "Mod", "Mod", scopes: [new(a, "profile.a"), new(b, "profile.b")], facets: [new("Category", "Source category", "en-US", true)]) with { Game = null, Origin = "Mod" });
        rows.Add(Row("uncategorized-mod", "Mod", "Mod") with { Origin = "Mod" });
        rows.Add(Row("preserved") with { ExistingRecordId = "existing.canonical.fixture", ExistingPackageId = "existing.package.fixture" });
        rows.Add(Row("left-parent", kind: RegistrationEntityKind.Category));
        rows.Add(Row("right-parent", kind: RegistrationEntityKind.Category));
        rows.Add(Row("multi-parent", parents: ["left-parent", "right-parent"]));
        rows.Add(Row("selectable-parent")); rows.Add(Row("selectable-child", parents: ["selectable-parent"]));
        rows.Add(Row("same-label-a", names: [new("Same source label", "en-US")]));
        rows.Add(Row("same-label-b", names: [new("Same source label", "en-US")]));
        foreach (var slot in new[] { "head", "top", "bottom" })
            rows.Add(Row("unparented-wearable-" + slot, "Item", "Item/wearables/" + slot + "/type-class/item"));
        for (var i = 0; i < 12; i++) rows.Add(Row("deep-" + i, kind: i == 11 ? RegistrationEntityKind.Entity : RegistrationEntityKind.Category, parents: i == 0 ? [] : ["deep-" + (i - 1)]));
        rows.Add(Row("wide", kind: RegistrationEntityKind.Category));
        for (var i = 0; i < 130; i++) rows.Add(Row("wide-" + i.ToString("D3"), parents: ["wide"]));
        rows.Add(Row("cycle-a", parents: ["cycle-b"])); rows.Add(Row("cycle-b", parents: ["cycle-a"]));
        rows.Add(Row("orphan", parents: ["no-such-row"]));
        rows.Add(Row("technical", kind: RegistrationEntityKind.TechnicalArtifact));
        rows.Add(Row("variant", kind: RegistrationEntityKind.Variant));
        rows.Add(Row("unnamed", names: []));
        rows.Add(Row("french-only", names: [new("Lieu local", "fr-FR")]));
        rows.Add(Row("profile-b-only", scopes: [new(a, "profile.b")]));
        rows.Add(Row("alias-a", names: [new("Native alias A", "en-US"), new("Shared nickname", "en-US", true), new("Unique source nickname", "en-US", true)]));
        rows.Add(Row("alias-b", names: [new("Native alias B", "en-US"), new("Shared nickname", "en-US", true)]));
        rows.Add(Row("duplicate-a") with { NativeId = "duplicate-native" });
        rows.Add(Row("duplicate-b") with { NativeId = "duplicate-native" });
        rows.Add(Row("unmapped") with { Classification = "unknown-native-class" });
        rows.Add(Row("ambiguous-map") with { Classification = "competing-native-class" });
        rules.Add(new("map:ambiguous-a", "fixture-rows", "Location", "competing-native-class", "Location/world"));
        rules.Add(new("map:ambiguous-b", "fixture-rows", "Location", "competing-native-class", "Location/world/continent"));
        rows.Add(Row("bad-facet", facets: [new("Imagined Dimension", "Unsupported", "en-US")]));
        var reference = Row("reference-backed", names: [new("Reference-owned place", "en-US")]);
        RegistrationSourceArtifact Artifact(string id, KnowledgeSourceKind kind, NativeRow[] sourceRows)
        {
            var bytes = CanonicalRegistrationEncoding.Bytes(new NativeFamily(sourceRows));
            return new(new(id, "fixture://" + id, kind, CanonicalRegistrationEncoding.Digest(bytes), "fixture.family-json", "1", "fixture-rows"), bytes);
        }
        return ([Artifact("local", KnowledgeSourceKind.LocalGameDistribution, rows.ToArray()), Artifact("reference", KnowledgeSourceKind.ReferenceProvider, [reference])], new("fixture.rules.v1", rules.ToArray()));
    }

    public static async Task<int> VerifyLegacyAsync(CanonicalCatalogPackage package)
    {
        var before = CanonicalRegistrationEncoding.Digest(package);
        var record = package.Payload.KnowledgeRecords.Single(r => r.Kind == KnowledgeKind.Location);
        var row = new NativeRow("legacy-location", record.NativeIdentity.ExactRepresentation, "Location", record.GameId.Value,
            "legacy-world", RegistrationEntityKind.Entity, [new("Existing synthetic place", "en-US")],
            [new(record.GameId.Value, "profile.legacy")], [], [], "Game", record.Id.Value, package.Id.Value, record.NativeIdentity.Namespace);
        var bytes = CanonicalRegistrationEncoding.Bytes(new NativeFamily([row]));
        var artifact = new RegistrationSourceArtifact(new("legacy", "fixture://legacy", KnowledgeSourceKind.FrozenRepositoryDataset,
            CanonicalRegistrationEncoding.Digest(bytes), "fixture.family-json", "1", "legacy-family"), bytes);
        var rules = new RegistrationRuleSet("legacy-proof", [new("legacy-rule", "legacy-family", "Location", "legacy-world", "Location/world")]);
        var candidate = await CanonicalRegistrationEngine.RegisterWithLegacyAsync(new FixtureAdapter(), [artifact], rules, [package]);
        CanonicalRegistrationCandidateVerifier.Verify(candidate, [package]);
        if (candidate.Entities.Single().Id != record.Id.Value || candidate.Entities.Single().ExistingPackageId != package.Id.Value)
            throw new InvalidOperationException("Verified legacy canonical identity/package reference changed.");
        if (CanonicalRegistrationEncoding.Digest(package) != before)
            throw new InvalidOperationException("Registration changed the historical package payload.");
        try { CanonicalRegistrationCandidateVerifier.Verify(candidate); }
        catch (InvalidDataException) { return 3; }
        throw new InvalidOperationException("A legacy candidate must not verify without its exact established package.");
    }

    private static string[] Ancestors(string path, RegistrationPreparedDescriptor descriptor)
    {
        var paths = descriptor.Paths.ToDictionary(p => p.PathId, StringComparer.Ordinal);
        var result = new List<string>();
        for (var current = paths[path];; current = paths[current.ParentPathId!])
        {
            result.Add(current.PathId);
            if (current.ParentPathId is null) return result.ToArray();
        }
    }
    private static bool DescendsFrom(string path, string ancestor, RegistrationPreparedDescriptor descriptor) =>
        Ancestors(path, descriptor).Contains(ancestor, StringComparer.Ordinal);

    private static void VerifyRefresh(CanonicalRegistrationCandidate candidate, RegistrationPreparedCandidate[] prepared, Action<bool, string> check)
    {
        var current = new RegistrationRefreshFingerprint(candidate.EngineVersion, candidate.ChecklistDigest, candidate.RuleDigest,
            candidate.Input.Sources.Select(s => s.Sha256).ToArray(), candidate.Input.Sources.Select(s => s.AdapterId + ":" + s.AdapterVersion).ToArray(),
            prepared.Select(p => CanonicalRegistrationEncoding.Digest(p.Descriptor.Context)).ToArray());
        check(!CanonicalRegistrationRefreshPlanner.Plan(current, current).IsStale, "Unchanged explicit refresh reuses a verified generation.");
        check(!CanonicalRegistrationRefreshPlanner.Plan(current, current with { SourceDigests = current.SourceDigests.Reverse().ToArray() }).IsStale,
            "Source enumeration order does not trigger a refresh.");
        var changes = new[] { current with { EngineVersion = "new-engine" }, current with { ChecklistDigest = new string('a', 64) },
            current with { RuleDigest = new string('b', 64) }, current with { SourceDigests = [new string('c', 64)] },
            current with { AdapterVersions = ["new-adapter"] }, current with { ContextDigests = [new string('d', 64)] } };
        foreach (var changed in changes)
        {
            var plan = CanonicalRegistrationRefreshPlanner.Plan(current, changed);
            check(plan.IsStale && plan.Reasons.Length == 1 && plan.PublicationState == CanonicalRegistrationEncoding.NotPublished,
                "Each changed runtime/source coordinate independently invalidates preparation without publishing.");
            check(Array.IndexOf(plan.Stages, "verify-prepared-candidate") < Array.IndexOf(plan.Stages, "publish-atomic-pointer") &&
                  plan.Stages.Contains("retain-previous-generation-for-rollback", StringComparer.Ordinal), "Refresh handoff orders verification before atomic publication and retains rollback.");
        }
        var fresh = CanonicalRegistrationRefreshPlanner.Plan(null, current);
        check(fresh.IsStale && fresh.Reasons.SequenceEqual(["no-verified-generation"]), "Missing generation requires explicit preparation.");
    }

    private static async Task VerifyFakePublication(CanonicalRegistrationCandidate candidate, RegistrationPreparedCandidate prepared,
        string outputRoot, Action<bool, string> check, Func<Func<Task>, string, Task> reject)
    {
        var fakeRoot = Path.Combine(outputRoot, "fake-publication-only"); Directory.CreateDirectory(fakeRoot);
        var pointer = Path.Combine(fakeRoot, "active.json");
        var backup = Path.Combine(fakeRoot, "rollback.json");
        var previousBytes = CanonicalRegistrationEncoding.Bytes(new { generation = "previous.verified.fixture", fakeOnly = true });
        await File.WriteAllBytesAsync(pointer, previousBytes);
        async Task Publish(string expectedCandidate, bool interrupt)
        {
            CanonicalRegistrationCandidateVerifier.Verify(candidate);
            if (CanonicalRegistrationEncoding.Digest(candidate) != expectedCandidate || prepared.Descriptor.CandidateDigest != expectedCandidate)
                throw new InvalidDataException("Fake handoff refuses stale candidate coordinates.");
            using var reader = await CanonicalRegistrationPreparedReader.OpenAsync(prepared.DirectoryPath, prepared.Digest, prepared.Descriptor.Context);
            foreach (var view in reader.Descriptor.Views) await reader.GetChildrenAsync(view.RootPathId);
            var staged = Path.Combine(fakeRoot, "staged-pointer.json");
            await File.WriteAllBytesAsync(staged, CanonicalRegistrationEncoding.Bytes(new
            { candidate = expectedCandidate, generation = prepared.Digest, fakeOnly = true }));
            if (interrupt) throw new OperationCanceledException("Injected interruption before fake atomic pointer replacement.");
            File.Replace(staged, pointer, backup, true);
            await File.WriteAllBytesAsync(Path.Combine(fakeRoot, "fake-publication-receipt.json"), CanonicalRegistrationEncoding.Bytes(new
            { candidate = expectedCandidate, generation = prepared.Digest, rollback = "rollback.json", fakeOnly = true, state = "FAKE_PUBLISHED" }));
        }
        await reject(() => Publish(new string('0', 64), false), "Fake publisher rejects stale candidate digest");
        check((await File.ReadAllBytesAsync(pointer)).SequenceEqual(previousBytes), "A stale fake handoff leaves the active pointer untouched.");
        await reject(() => Publish(CanonicalRegistrationEncoding.Digest(candidate), true), "Interrupted fake publication before commit");
        check((await File.ReadAllBytesAsync(pointer)).SequenceEqual(previousBytes), "Staged interruption preserves the previous active generation.");
        await Publish(CanonicalRegistrationEncoding.Digest(candidate), false);
        check((await File.ReadAllBytesAsync(backup)).SequenceEqual(previousBytes), "Successful fake atomic replacement retains the exact rollback pointer.");
        using var active = JsonDocument.Parse(await File.ReadAllBytesAsync(pointer));
        check(active.RootElement.GetProperty("generation").GetString() == prepared.Digest && active.RootElement.GetProperty("fakeOnly").GetBoolean(),
            "Only the verified synthetic generation becomes active inside the isolated fake publication directory.");
        check(File.Exists(Path.Combine(fakeRoot, "fake-publication-receipt.json")), "Fake publication writes an explicit fake-only receipt after pointer commit.");
    }

    private static async Task<List<RegistrationNavigationRow>> Traverse(CanonicalRegistrationPreparedReader reader)
    {
        var rows = new List<RegistrationNavigationRow>();
        var pending = new Queue<string>(reader.Descriptor.Views.Select(v => v.RootPathId));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.TryDequeue(out var path))
        {
            if (!seen.Add(path)) continue;
            string? cursor = null;
            do
            {
                var page = await reader.GetChildrenAsync(path, cursor);
                if (cursor is null) rows.Add(page.Current);
                foreach (var child in page.Children) pending.Enqueue(child.PathId);
                cursor = page.ContinuationCursor;
            } while (cursor is not null);
        }
        return rows;
    }

    private static async Task VerifyCorruption(RegistrationPreparedCandidate prepared, string outputRoot, Func<Func<Task>, string, Task> reject)
    {
        var corrupt = Path.Combine(outputRoot, "corrupt", prepared.Digest);
        Directory.CreateDirectory(corrupt);
        foreach (var file in Directory.EnumerateFiles(prepared.DirectoryPath)) File.Copy(file, Path.Combine(corrupt, Path.GetFileName(file)), true);
        var root = prepared.Descriptor.Views.First(v => v.Id == "Location").RootPathId;
        var page = prepared.Descriptor.Paths.Single(p => p.PathId == root).PageDigests[0];
        await File.WriteAllTextAsync(Path.Combine(corrupt, page + ".json"), "{\"corrupt\":true}");
        using (var reader = await CanonicalRegistrationPreparedReader.OpenAsync(corrupt, prepared.Digest, prepared.Descriptor.Context))
            await reject(() => reader.GetChildrenAsync(root), "Tampered requested page must fail closed");
        await File.WriteAllTextAsync(Path.Combine(corrupt, "descriptor.json"), "{}");
        await reject(() => CanonicalRegistrationPreparedReader.OpenAsync(corrupt, prepared.Digest, prepared.Descriptor.Context), "Tampered descriptor must fail closed");
        var incomplete = Path.Combine(outputRoot, "incomplete", prepared.Digest);
        Directory.CreateDirectory(incomplete);
        await reject(() => CanonicalRegistrationPreparedReader.OpenAsync(incomplete, prepared.Digest, prepared.Descriptor.Context), "Interrupted preparation has no valid descriptor");
    }
}
