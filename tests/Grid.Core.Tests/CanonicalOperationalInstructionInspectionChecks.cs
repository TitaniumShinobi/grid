using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalOperationalInstructionInspectionChecks
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly RegistrationRuleSet Rules = new("1", []);
    private static int checks;

    public static int Run() => RunAsync().GetAwaiter().GetResult();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Operational instruction: " + message);
        checks++;
    }
    private static async Task Reject(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or JsonException) { checks++; return; }
        throw new InvalidOperationException("Expected instruction rejection: " + message);
    }
    private static Task Reject(Action action, string message) => Reject(() => { action(); return Task.CompletedTask; }, message);

    private static async Task<int> RunAsync()
    {
        checks = 0;
        var provider = new ProviderAdapter();
        var package = new PackageAdapter();
        var providerSources = ProviderSources();
        var packageSources = PackageSources();
        Check(provider.Id != package.Id && providerSources[0].Source.Family != packageSources[0].Source.Family,
            "Two unrelated provider/package fixtures use different adapters and shapes.");
        foreach (var fixture in new[] { (Adapter: (FixtureAdapter)provider, Sources: providerSources),
                                        (Adapter: (FixtureAdapter)package, Sources: packageSources) })
        {
            var sourceDigests = fixture.Sources.Select(s => CanonicalRegistrationEncoding.Digest(s.Bytes)).ToArray();
            var candidate = await Register(fixture.Adapter, fixture.Sources);
            Check(sourceDigests.SequenceEqual(fixture.Sources.Select(s => CanonicalRegistrationEncoding.Digest(s.Bytes))),
                "Registration leaves admitted source bytes unchanged.");
            CanonicalRegistrationCandidateVerifier.Verify(candidate);
            await CanonicalRegistrationCandidateVerifier.VerifyAgainstSourcesAsync(candidate, fixture.Adapter, fixture.Sources);
            var roundTrip = JsonSerializer.Deserialize<CanonicalRegistrationCandidate>(
                CanonicalRegistrationEncoding.Bytes(candidate), CanonicalRegistrationEncoding.Json)!;
            CanonicalRegistrationCandidateVerifier.Verify(roundTrip);
            Check(CanonicalRegistrationEncoding.Bytes(candidate).SequenceEqual(CanonicalRegistrationEncoding.Bytes(roundTrip)),
                "Canonical assertion/receipt JSON round trip is exact.");
            Check(candidate.PublicationState == "NOT_PUBLISHED" && candidate.Entities.Length == 0 &&
                  candidate.Relationships.Length == 0 && candidate.Facets.Length == 0,
                "Instruction-only candidate creates no entity, navigation, or live state.");
            Check(candidate.Instructions!.Length == 5 && candidate.Input.Evidence.Instructions!.Length == 5,
                "Both fixtures retain all observations on input and candidate.");
            foreach (var state in Enum.GetValues<OperationalInstructionSectionPresence>())
                Check(candidate.Instructions.Any(i => i.Claim.Presence == state), "Distinct observation state: " + state);
            Check(candidate.Instructions.Single(i => i.Claim.Presence == OperationalInstructionSectionPresence.EmptyUnresolved)
                    .Claim.Assertion.Content.VerbatimText == (fixture.Adapter is ProviderAdapter ? "\r\n \t" : ""),
                "Empty/unresolved source whitespace is retained without trimming or fabricated absence.");
            var first = candidate.Instructions.Single(i => i.Claim.Assertion.NativeIdentity.ExactRepresentation == "install");
            Check(first.Claim.Assertion.Content.VerbatimText == FixtureText,
                "CRLF, LF, Unicode, escapes, spaces and leading/trailing blank lines survive exactly.");
            Check(candidate.Instructions.Where(i => i.Claim.Presence == OperationalInstructionSectionPresence.Present)
                    .Select(i => i.Claim.Assertion.Content.VerbatimText).Distinct(StringComparer.Ordinal).Count() == 2,
                "Contradictory source wording remains as two independent assertions.");
            Check(first.Claim.Assertion.Content.ContentDigest == InstructionContent.FromVerbatim(FixtureText).ContentDigest,
                "Canonical content digest binds exact wording.");
            Check(candidate.InstructionConflicts is { Length: 1 } &&
                  candidate.InstructionConflicts[0].MemberIds.Length == 2 &&
                  candidate.Rulings.Count(r => r.Reason == "instruction-guidance-conflicting") == 2,
                "Explicit adapter conflict survives independently without synthesizing a winner.");
            Check(candidate.Instructions.All(i => i.Applicability.Length == 0 &&
                  i.ApplicabilityResolution == CanonicalResolutionState.Unresolved &&
                  i.Claim.UnresolvedApplicability.Single().GameId == "game.unproven" &&
                  i.Claim.UnresolvedApplicability.Single().ProfileId == "profile.unproven"),
                "Caller game/profile claims remain unresolved despite text evidence.");
            Check(candidate.Instructions.All(i => i.Claim.Binding.Id.AlgorithmVersion == 3 &&
                  i.Claim.Binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(i.Claim.Assertion)),
                "Existing v3 evidence bindings and canonical claim-content identities are reused.");

            var reordered = await Register(fixture.Adapter with { Reverse = true }, fixture.Sources.Reverse().ToArray());
            Check(Bytes(candidate).SequenceEqual(Bytes(reordered)), "Enumeration order does not change semantic/candidate bytes.");
            var composer = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(fixture.Adapter, fixture.Sources, Rules);
            var pipeline = await CanonicalRelationshipRegistrationPipeline.RegisterAsync(fixture.Adapter, fixture.Sources, Rules);
            Check(Bytes(candidate).SequenceEqual(Bytes(composer)) && Bytes(candidate).SequenceEqual(Bytes(pipeline)),
                "MDBO composer and registration pipeline reach identical candidate behavior.");

            await Reject(() => Register(fixture.Adapter with { CorruptDiscovery = true }, fixture.Sources),
                "Discovery cannot replace admitted source metadata.");
            await Reject(() => CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
                    fixture.Adapter with { CorruptDiscovery = true }, fixture.Sources, Rules),
                "MDBO discovery enforces the same instruction source closure.");
            var changed = fixture.Sources.Select(s => s with { Bytes = s.Bytes.ToArray() }).ToArray();
            changed[0].Bytes[^1] ^= 1;
            await Reject(() => Register(fixture.Adapter, changed), "Changed source bytes.");
            var digestWrong = fixture.Sources.Select((s, n) => n == 0 ? s with { Source = s.Source with { Sha256 = new string('0', 64) } } : s).ToArray();
            await Reject(() => Register(fixture.Adapter, digestWrong), "Wrong source digest.");
            var revisionWrong = fixture.Sources.Select((s, n) => n == 0 ? s with { Source = s.Source with
            {
                InstructionSource = s.Source.InstructionSource! with
                { Revision = s.Source.InstructionSource.Revision with { NativeRevision = SourceNativeVersion.FromExactUtf8("fixture.revision", "wrong") } }
            } } : s).ToArray();
            await Reject(() => Register(fixture.Adapter, revisionWrong), "Wrong revision closure.");
            var unsupported = fixture.Sources.Select((s, n) => n == 0 ? s with { Source = s.Source with
            { InstructionSource = s.Source.InstructionSource! with { Format = new("unsupported.binary", "99") } } } : s).ToArray();
            await Reject(() => Register(fixture.Adapter, unsupported), "Unsupported source format.");
            var repinnedRevision = fixture.Sources.Select(s => Artifact(s.Source.Id, s.Source.AdapterId, s.Source.Family,
                s.Source.Kind, s.Source.InstructionSource!.Format.FormatId,
                Utf8.GetBytes(Utf8.GetString(s.Bytes).Replace("r1", "r2", StringComparison.Ordinal)), "r2")).ToArray();
            var revised = await Register(fixture.Adapter, repinnedRevision);
            Check(!candidate.Instructions.Select(i => i.Claim.Assertion.Id).Intersect(revised.Instructions!.Select(i => i.Claim.Assertion.Id)).Any(),
                "A correctly re-admitted native revision changes canonical instruction identities.");
            var repinnedBytes = fixture.Sources.Select(s => Artifact(s.Source.Id, s.Source.AdapterId, s.Source.Family,
                s.Source.Kind, s.Source.InstructionSource!.Format.FormatId,
                Utf8.GetBytes(Utf8.GetString(s.Bytes).Replace("Install", "Place", StringComparison.Ordinal)))).ToArray();
            var reworded = await Register(fixture.Adapter, repinnedBytes);
            Check(first.Claim.Assertion.Id != reworded.Instructions!.Single(i =>
                    i.Claim.Assertion.NativeIdentity.ExactRepresentation == "install").Claim.Assertion.Id,
                "Changed wording with a correct fresh digest changes identity.");
            await Reject(() => CanonicalRegistrationCandidateVerifier.VerifyAgainstSourcesAsync(candidate, fixture.Adapter, repinnedRevision),
                "Old candidate fails against a newer admitted revision.");
            var input = candidate.Input.Evidence.Instructions!;
            var original = input[0];
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate,
                original with { Id = new string('f', 64) })), "Forged instruction identity.");
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate,
                original with { SourceId = fixture.Sources.Single(s => s.Source.Id != original.SourceId).Source.Id })),
                "Wrong source identity.");
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate,
                original with { Presence = original.Presence == OperationalInstructionSectionPresence.Present ? OperationalInstructionSectionPresence.Missing : OperationalInstructionSectionPresence.Present })),
                "Observation/content tamper.");
            var alteredAssertion = Assertion(original.Assertion, text: original.Assertion.Content.VerbatimText + "edited");
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate,
                original with { Assertion = alteredAssertion })), "Content changed but evidence/identity unchanged.");
            var alteredLocator = Assertion(original.Assertion, locator: new InstructionSourceLocator(original.Assertion.SourceLocator.ArtifactId, original.Assertion.SourceLocator.NativeObjectIdentity, "wrong-locator", original.Assertion.SourceLocator.ProviderObjectRevision));
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate,
                original with { Assertion = alteredLocator })), "Wrong source locator.");
            var swappedScope = original with { ScopeEvidence = original.ScopeEvidence with { Receipt = original.Receipt } };
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(candidate, swappedScope)),
                "Instruction text receipt cannot prove scope.");
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(candidate with { PublicationState = "PUBLISHED" }),
                "Published candidate.");
            var editedOutput = candidate.Instructions!.Select((i, n) => n == 0 ? i with
            { ApplicabilityResolution = CanonicalResolutionState.Resolved, Applicability = [new("game.unproven", "profile.unproven", [])] } : i).ToArray();
            await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(candidate with { Instructions = editedOutput }),
                "Candidate applicability tampering.");

            // Even an internally re-hashed forged extraction is rejected against the external source anchor.
            var fabricated = OperationalInstructionInspection.Create(original.SourceId, alteredAssertion,
                OperationalInstructionSectionPresence.Present, original.Receipt, original.ScopeEvidence, original.UnresolvedApplicability);
            var forgedEvidence = candidate.Input.Evidence with
            {
                Instructions = input.Select(i => i.Id == original.Id ? fabricated : i).ToArray(),
                InstructionConflicts = null,
            };
            var forged = await Register(new ReplayAdapter(fixture.Adapter.Id, forgedEvidence), fixture.Sources);
            await Reject(() => CanonicalRegistrationCandidateVerifier.VerifyAgainstSourcesAsync(forged, fixture.Adapter, fixture.Sources),
                "Coordinated content/claim/binding changes cannot bypass source replay.");
        }

        var providerCandidate = await Register(provider, providerSources);
        var packageCandidate = await Register(package, packageSources);
        var providerClaim = providerCandidate.Input.Evidence.Instructions![0];
        var packageClaim = packageCandidate.Input.Evidence.Instructions![0];
        await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(providerCandidate,
            providerClaim with { Receipt = packageClaim.Receipt })), "FILE receipt borrowed for REFERENCE.");
        await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(packageCandidate,
            packageClaim with { Receipt = providerClaim.Receipt })), "REFERENCE receipt borrowed for FILE.");
        var mixed = providerClaim with { Receipt = new(packageClaim.Receipt.File, providerClaim.Receipt.Reference) };
        await Reject(() => CanonicalRegistrationCandidateVerifier.Verify(ChangeInput(providerCandidate, mixed)), "Mixed evidence classes.");

        var plain = new ReplayAdapter("fixture.empty", new([], [], [], []));
        var noInstructions = await Register(plain, []);
        var emptyInstructions = await Register(plain with { Evidence = new([], [], [], [], [], []) }, []);
        Check(Bytes(noInstructions).SequenceEqual(Bytes(emptyInstructions)) &&
              !Utf8.GetString(Bytes(noInstructions)).Contains("\"instructions\"", StringComparison.Ordinal),
            "Null and empty instruction collections preserve the historical candidate shape.");
        // Golden digest captured from the unmodified origin/main engine using the same empty input/rules.
        Check(CanonicalRegistrationEncoding.Digest(noInstructions) == LegacyEmptyCandidateDigest,
            "No-instruction bytes equal the pre-PR engine golden vector.");
        var legacySources = new RegistrationSourceArtifact[] { new(new("legacy", "https://fixtures.invalid/legacy",
            KnowledgeSourceKind.ReferenceProvider, CanonicalRegistrationEncoding.Digest(new byte[] { 123, 125 }),
            "fixture.empty", "1", "legacy"), [123, 125]) };
        var legacy = await Register(plain, legacySources);
        Check(CanonicalRegistrationEncoding.Digest(legacy) == "3e9e936ec47c6d1194b71b5c5b0d1ae9d8c6f0575c0ab40e4540ae6e02cf5e7e",
            "Existing nonempty source input preserves the pre-PR candidate bytes.");
        Check(CanonicalRegistrationChecklist.Load().Nodes.Length == 98, "Frozen DIF mold remains 98 nodes.");
        return checks;
    }

    private const string LegacyEmptyCandidateDigest = "3dd405f7d4a44fdebd81b82c7fd55ec1d9ba7ac4e3b9cd2ec69d8406a96f5ea9";
    private const string FixtureText = "\r\n\n  Install café e\u0301 Ω 🛠 through the manager.\\mods\\new\r\nKeep \"quoted\" paths.\n\r\n ";
    private static byte[] Bytes(CanonicalRegistrationCandidate c) => CanonicalRegistrationEncoding.Bytes(c);
    private static Task<CanonicalRegistrationCandidate> Register(IRegistrationEvidenceAdapter adapter, RegistrationSourceArtifact[] sources) =>
        CanonicalRegistrationEngine.RegisterAsync(adapter, sources, Rules);
    private static CanonicalRegistrationCandidate ChangeInput(CanonicalRegistrationCandidate c, RegistrationInstructionClaim value) =>
        c with { Input = c.Input with { Evidence = c.Input.Evidence with
        { Instructions = [value, .. c.Input.Evidence.Instructions!.Skip(1)] } } };

    private static InstructionAssertion Assertion(InstructionAssertion a, string? text = null, InstructionSourceLocator? locator = null)
    {
        var content = text is null ? a.Content : InstructionContent.FromVerbatim(text);
        var path = locator ?? a.SourceLocator;
        var id = InstructionAssertionId.DeriveV1(a.SourceRevisionId, a.SourceScope, a.NativeIdentity,
            a.CategoryId, content.RetentionMode, content.ContentDigest, path, a.ExactLanguageTag,
            a.CategoryMappingMethodId, a.CategoryMappingMethodVersion, [], []);
        return new(id, a.SourceRevisionId, a.SourceScope, a.NativeIdentity, a.CategoryId,
            a.CategoryMappingMethodId, a.CategoryMappingMethodVersion, a.ExactLanguageTag, content, path, [], []);
    }

    private static RegistrationSourceArtifact Artifact(string id, string adapter, string family, KnowledgeSourceKind kind,
        string format, byte[] bytes, string revision = "r1")
    {
        var native = SourceNativeIdentifier.FromExactUtf8("fixture.source", "document", id);
        var source = new CatalogSourceRecord(CatalogSourceId.DeriveV1(kind, native), kind, native);
        var digest = ContentDigest.ComputeSha256(bytes);
        var artifact = SourceArtifactId.DeriveV1(digest);
        var nativeRevision = SourceNativeVersion.FromExactUtf8("fixture.revision", revision);
        var rev = new CatalogSourceRevisionRecord(CatalogSourceRevisionId.DeriveV1(source.Id, nativeRevision, [artifact]),
            source.Id, nativeRevision, [artifact]);
        return new(new(id, "https://fixtures.invalid/" + id, kind, digest.HexValue, adapter, "1", family,
            new(source, rev, new(format, "1"))), bytes);
    }

    private static RegistrationInstructionReceipt Receipt(RegistrationSource source, string locator, SourceNativeIdentifier obj)
    {
        var closure = source.InstructionSource!;
        var rev = closure.Revision;
        var digest = new ContentDigest("sha256", source.Sha256);
        var artifact = SourceArtifactId.DeriveV1(digest);
        if (source.Kind == KnowledgeSourceKind.ModProvider)
        {
            var r = new ReferenceEvidenceReceipt(closure.Source.Id, rev.Id, artifact, digest,
                obj, rev.NativeRevision, locator, Observed);
            return new(null, new(EvidenceReceiptId.DeriveV1(r), r));
        }
        var f = new FileEvidenceReceipt(rev.Id, artifact, digest, source.AdapterId, source.AdapterVersion,
            obj.ExactRepresentation, locator, null, null, null, Observed, obj);
        return new(new(EvidenceReceiptId.DeriveV2(f), f), null);
    }

    private static RegistrationInstructionClaim Claim(RegistrationSource source, string game, string scopePath,
        string section, string locator, OperationalInstructionSectionPresence presence, string text)
    {
        var scope = KnowledgeSourceScope.BaseGame(new(game), null);
        var obj = SourceNativeIdentifier.FromExactUtf8("fixture.document", "document", source.Id);
        var native = SourceNativeIdentifier.FromExactUtf8("fixture.section", "section", section);
        var c = InstructionContent.FromVerbatim(text);
        var path = new InstructionSourceLocator(source.InstructionSource!.Revision.ArtifactIds.Single(), obj, locator,
            source.Kind == KnowledgeSourceKind.ModProvider ? source.InstructionSource.Revision.NativeRevision : null);
        var category = section switch { "absent" => InstructionCategories.Uninstall, "empty" => InstructionCategories.Configuration,
            "missing" => InstructionCategories.Troubleshooting, _ => InstructionCategories.Installation };
        var id = InstructionAssertionId.DeriveV1(source.InstructionSource.Revision.Id, scope, native,
            category, c.RetentionMode, c.ContentDigest, path, "en",
            source.AdapterId, "1", [], []);
        var a = new InstructionAssertion(id, source.InstructionSource.Revision.Id, scope, native,
            category, source.AdapterId, "1", "en", c, path, [], []);
        var receipt = Receipt(source, locator, obj);
        // Explicit caller claim is retained, including its attempted section-receipt citation, but never resolved.
        var requested = new RegistrationApplicability("game.unproven", "profile.unproven",
            [(receipt.File?.Id ?? receipt.Reference!.Id).Value]);
        return OperationalInstructionInspection.Create(source.Id, a, presence, receipt,
            new(source.Id, scope, Receipt(source, scopePath, obj)), [requested]);
    }

    private abstract record FixtureAdapter : IRegistrationEvidenceAdapter
    {
        public abstract string Id { get; }
        public string Version => "1";
        public bool Reverse { get; init; }
        public bool CorruptDiscovery { get; init; }
        public Task<IReadOnlyList<RegistrationSource>> DiscoverAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            var copies = JsonSerializer.Deserialize<RegistrationSource[]>(CanonicalRegistrationEncoding.Bytes(sources.Select(s => s.Source).ToArray()),
                CanonicalRegistrationEncoding.Json)!;
            if (CorruptDiscovery) copies[0] = copies[0] with { Uri = "https://wrong.invalid/" };
            return Task.FromResult<IReadOnlyList<RegistrationSource>>(copies);
        }
        protected abstract IEnumerable<RegistrationInstructionClaim> Extract(RegistrationSourceArtifact source);
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var claims = sources.SelectMany(Extract).ToArray();
            var members = claims.Where(c => c.Assertion.NativeIdentity.ExactRepresentation is "install" or "contradiction")
                .Select(c => c.Assertion.Id).OrderBy(i => i.Value, StringComparer.Ordinal).ToImmutableArray();
            var conflict = new InstructionConflictGroup(InstructionConflictGroupId.DeriveV1(members, Id + ".explicit-conflict", "1"),
                members, Id + ".explicit-conflict", "1");
            if (Reverse) Array.Reverse(claims);
            return Task.FromResult(new RegistrationEvidenceSet([], [], [], [], claims, [conflict]));
        }
    }

    // Escaped provider fields (representative of a frozen Discord/Nexus/Steam API response).
    // This grammar is owned by the fixture evidence adapter, never the canonical engine.
    private sealed record ProviderAdapter : FixtureAdapter
    {
        public override string Id => "fixture.widget-provider";
        protected override IEnumerable<RegistrationInstructionClaim> Extract(RegistrationSourceArtifact artifact)
        {
            if (artifact.Source.InstructionSource!.Format != new KnowledgeFormatCoordinate("fixture.provider-response", "1"))
                throw new InvalidDataException("Unsupported provider response format.");
            using var doc = JsonDocument.Parse(artifact.Bytes);
            var root = doc.RootElement;
            if (root.GetProperty("revision").GetString() != artifact.Source.InstructionSource.Revision.NativeRevision!.ExactRepresentation)
                throw new InvalidDataException("Provider response revision differs from admission.");
            var game = root.GetProperty("game").GetString()!;
            var sections = root.GetProperty("fields");
            var keys = artifact.Source.Id.EndsWith("alternate", StringComparison.Ordinal) ? new[] { "contradiction" } :
                new[] { "install", "absent", "empty", "missing" };
            foreach (var key in keys)
            {
                var found = sections.TryGetProperty(key, out var field);
                var state = !found ? OperationalInstructionSectionPresence.Missing :
                    field.ValueKind == JsonValueKind.Null || field.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(field.GetString())
                        ? OperationalInstructionSectionPresence.EmptyUnresolved :
                    field.ValueKind == JsonValueKind.Object ? OperationalInstructionSectionPresence.ProvenAbsence :
                    OperationalInstructionSectionPresence.Present;
                // Absence must be explicit source data, never an empty-is-absence caller option.
                if (state == OperationalInstructionSectionPresence.ProvenAbsence &&
                    (!field.GetProperty("absent").GetBoolean() || field.GetProperty("text").ValueKind != JsonValueKind.String))
                    throw new InvalidDataException("Unsupported provider absence encoding.");
                var text = !found || field.ValueKind == JsonValueKind.Null ? "" :
                    field.ValueKind == JsonValueKind.Object ? field.GetProperty("text").GetString()! : field.GetString()!;
                yield return Claim(artifact.Source, game, "/game", key, "/fields/" + key, state, text);
            }
        }
    }

    // An unrelated installer/package text document with explicit byte-span locators.
    private sealed record PackageAdapter : FixtureAdapter
    {
        public override string Id => "fixture.archive-tool";
        protected override IEnumerable<RegistrationInstructionClaim> Extract(RegistrationSourceArtifact artifact)
        {
            if (artifact.Source.InstructionSource!.Format != new KnowledgeFormatCoordinate("fixture.installer-text", "1"))
                throw new InvalidDataException("Unsupported package text format.");
            var bytes = artifact.Bytes;
            var newline = Array.IndexOf(bytes, (byte)'\n');
            if (newline < 0) throw new InvalidDataException("Missing package metadata line.");
            var metadata = Utf8.GetString(bytes, 0, newline).Split('|');
            if (metadata.Length != 4 || metadata[0] != "archive-tool" || !metadata[3].StartsWith("uninstall-absent:", StringComparison.Ordinal) ||
                metadata[2] != artifact.Source.InstructionSource.Revision.NativeRevision!.ExactRepresentation)
                throw new InvalidDataException("Package metadata/revision differs from admission.");
            var game = metadata[1];
            var offset = newline + 1;
            var alternate = artifact.Source.Id.EndsWith("alternate", StringComparison.Ordinal);
            var text = Utf8.GetString(bytes, offset, bytes.Length - offset);
            yield return Claim(artifact.Source, game, "metadata:game", alternate ? "contradiction" : "install",
                $"utf8-bytes:{offset}:{bytes.Length - offset}", OperationalInstructionSectionPresence.Present, text);
            if (alternate) yield break;
            yield return Claim(artifact.Source, game, "metadata:game", "absent", "metadata:no-uninstall",
                OperationalInstructionSectionPresence.ProvenAbsence, metadata[3]["uninstall-absent:".Length..]);
            // The separate metadata field explicitly states the absence of uninstall guidance.
            yield return Claim(artifact.Source, game, "metadata:game", "empty", $"utf8-bytes:{bytes.Length}:0",
                OperationalInstructionSectionPresence.EmptyUnresolved, "");
            yield return Claim(artifact.Source, game, "metadata:game", "missing", "section:troubleshooting",
                OperationalInstructionSectionPresence.Missing, "");
        }
    }

    private sealed record ReplayAdapter(string Id, RegistrationEvidenceSet Evidence) : IRegistrationEvidenceAdapter
    {
        public string Version => "1";
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default) =>
            Task.FromResult(Evidence);
    }

    private static RegistrationSourceArtifact[] ProviderSources() =>
    [
        Artifact("widget-primary", "fixture.widget-provider", "widget-overlay", KnowledgeSourceKind.ModProvider,
            "fixture.provider-response", Utf8.GetBytes(JsonSerializer.Serialize(new { game = "game.widget", revision = "r1",
                fields = new { install = FixtureText, absent = new { absent = true, text = "No uninstall guidance." }, empty = "\r\n \t" } }))),
        Artifact("widget-alternate", "fixture.widget-provider", "widget-overlay", KnowledgeSourceKind.ModProvider,
            "fixture.provider-response", Utf8.GetBytes(JsonSerializer.Serialize(new { game = "game.widget", revision = "r1",
                fields = new { contradiction = "Never use a manager. Install manually." } }))),
    ];
    private static RegistrationSourceArtifact[] PackageSources() =>
    [
        Artifact("archive-primary", "fixture.archive-tool", "archive-repair-cli", KnowledgeSourceKind.LocalModArtifact,
            "fixture.installer-text", Utf8.GetBytes("archive-tool|game.archive|r1|uninstall-absent:No uninstall guidance.\n" + FixtureText)),
        Artifact("archive-alternate", "fixture.archive-tool", "archive-repair-cli", KnowledgeSourceKind.LocalModArtifact,
            "fixture.installer-text", Utf8.GetBytes("archive-tool|game.archive|r1|uninstall-absent:No uninstall guidance.\nNever use a manager. Install manually.")),
    ];
}
