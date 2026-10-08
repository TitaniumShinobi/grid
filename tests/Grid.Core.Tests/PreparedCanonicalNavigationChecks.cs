using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class PreparedCanonicalNavigationChecks
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Prepared navigation integrity checks require Windows DPAPI.");
        var checks = 0;
        void Assert(bool condition, string detail)
        {
            if (!condition) throw new InvalidOperationException(detail);
            checks++;
        }
        async Task Reject<T>(Func<Task> action, string detail) where T : Exception
        {
            try { await action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException(detail);
        }
        var scratch = Path.Combine(Path.GetTempPath(), "grid-prepared-navigation-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var storePath = Path.Combine(scratch, "synthetic-catalog.json");
            var source = new JsonCanonicalKnowledgeCatalogStore(storePath);
            var package = CreatePagedFixture();
            var imported = await source.ImportPackageAsync(0, package);
            Assert(imported.Status == CanonicalCatalogImportStatus.Imported, "Prepared fixture is a genuinely verified imported package.");
            var loaded = await source.LoadAsync();
            package = loaded.Snapshot.FindImportedPackage(package.Id)!;
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(storePath))).ToLowerInvariant();
            var composition = new CatalogCompositionId("composition.prepared-test");
            var applicability = new CanonicalApplicabilityProjection(composition,
                package.Payload.KnowledgeRecords.Select(value => value.Id).ToImmutableArray(), [], "prepared-test.v1");
            var locale = new CanonicalTerminologyLocalePreference("en-US", ["en"]);
            Dictionary<KnowledgeKind, CanonicalSelectorProjectionInput> Inputs(CanonicalSelectorProjectionPolicy policy) =>
                Enum.GetValues<KnowledgeKind>().ToDictionary(kind => kind, _ =>
                    CanonicalSelectorProjectionEngine.CreateVerifiedInput(loaded, package.Id, composition, policy, applicability));
            var inputs = Inputs(CanonicalSelectorProjectionPolicy.V1);
            var root = Path.Combine(scratch, "prepared");
            var descriptor = await PreparedCanonicalNavigationBuilder.BuildAsync(root, loaded, package.Id, digest, inputs, locale);
            Assert(descriptor.IsBaseGameOnly && descriptor.PackageId == package.Id && descriptor.SourceStoreSha256 == digest,
                "Publication binds exact base package and source digest.");
            var pointerPath = Path.Combine(root, PreparedCanonicalNavigationStore.PublicationFileName);
            var firstPointer = await File.ReadAllBytesAsync(pointerPath);
            using var reader = await PreparedCanonicalNavigationStore.OpenAsync(root, package.Id, locale);
            Assert(reader.Statistics.PageReads == 0 && reader.Statistics.RowsMaterialized == 0,
                "Opening reads metadata without materializing any selector rows.");
            var stages = new List<CanonicalRuntimeStageMeasurement>();
            using var observation = CanonicalRuntimeDiagnostics.Observe(stages.Add);
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => reader.ReadLevelAsync(KnowledgeKind.Actor)));
            Assert(reader.Statistics.PageReads == 1 && reader.Statistics.PageCacheHits == 11,
                "Concurrent requests read and authenticate a requested page once.");
            Assert(concurrent.All(page => JsonSerializer.Serialize(page) == JsonSerializer.Serialize(concurrent[0])),
                "Concurrent readers get identical ordered page contracts.");
            Assert(concurrent.All(page => ReferenceEquals(page.Result, concurrent[0].Result)),
                "Warm navigation shares decoded immutable contracts without reparsing JSON.");
            var materializedBeforeRepeat = reader.Statistics.RowsMaterialized;
            _ = await reader.ReadLevelAsync(KnowledgeKind.Actor);
            Assert(reader.Statistics.RowsMaterialized == materializedBeforeRepeat,
                "Warm reads do not rematerialize rows.");
            Assert(stages.Count == 0, "Prepared reads perform no catalog validation, assertion indexing or graph construction.");

            CanonicalSelectorSelection? selected = null;
            var sawContinuation = false;
            foreach (var kind in Enum.GetValues<KnowledgeKind>())
            {
                var input = inputs[kind];
                var pending = new Queue<CanonicalNavigationPathId?>();
                var seen = new HashSet<CanonicalNavigationPathId>();
                pending.Enqueue(null);
                while (pending.TryDequeue(out var path))
                {
                    var actual = await reader.ReadLevelAsync(kind, path);
                    if (!seen.Add(actual.Result.CurrentPathId)) continue;
                    var expected = CanonicalSelectorProjectionEngine.Query(input, new(input.CatalogRevisionId,
                        input.CatalogCompositionId, kind, input.Policy.Id, input.Policy.ExactVersion,
                        path, null, kind != KnowledgeKind.Location, false, locale));
                    var children = actual.Result.ImmediateChildren.ToBuilder();
                    var cursor = actual.ContinuationCursor;
                    while (cursor is not null)
                    {
                        sawContinuation = true;
                        var next = await reader.ReadLevelAsync(kind, actual.Result.CurrentPathId, cursor);
                        children.AddRange(next.Result.ImmediateChildren);
                        cursor = next.ContinuationCursor;
                    }
                    Assert(JsonSerializer.Serialize(actual.Result with { ImmediateChildren = children.ToImmutable() }) ==
                        JsonSerializer.Serialize(expected), "Every persisted path preserves the projector's exact contract and evidence.");
                    foreach (var child in children)
                    {
                        pending.Enqueue(child.PathId);
                        if (child.IsSelectable && child.KnowledgeRecordId is { } record)
                            selected ??= new(CanonicalSelectorSelectionKind.CanonicalRecord, kind, descriptor.CatalogRevisionId,
                                descriptor.CatalogCompositionId, input.Policy.Id, input.Policy.ExactVersion,
                                child.PathId, record, null);
                    }
                }
            }
            Assert(sawContinuation, "A genuinely populated synthetic level exercises the 64-row continuation boundary.");
            Assert(selected is not null, "Fixture exercises direct selection of a real canonical leaf.");
            var beforeSelection = reader.Statistics;
            Assert(await reader.ValidateSelectionAsync(selected!), "Exact selected path and record validate through the directory.");
            Assert(reader.Statistics == beforeSelection, "Selection validation does not read descendant pages or materialize rows.");
            var nodeBefore = reader.Statistics.RowsMaterialized;
            var selectedNode = await reader.ReadNodeAsync(selected!.KnowledgeKind, selected.SelectedPathId!.Value);
            Assert(selectedNode.KnowledgeRecordId == selected.KnowledgeRecordId &&
                reader.Statistics.RowsMaterialized == nodeBefore + 1,
                "A selected label/evidence lookup decodes one node without any child-level rows.");
            var sameNode = await reader.ReadNodeAsync(selected.KnowledgeKind, selected.SelectedPathId.Value);
            Assert(ReferenceEquals(selectedNode, sameNode) && reader.Statistics.RowsMaterialized == nodeBefore + 1,
                "Repeated selected-node lookups reuse the same decoded immutable node.");
            var forged = new CanonicalSelectorSelection(CanonicalSelectorSelectionKind.CanonicalRecord,
                selected!.KnowledgeKind, selected.CatalogRevisionId, selected.CatalogCompositionId,
                selected.ProjectionPolicyId, selected.ProjectionPolicyVersion, selected.SelectedPathId,
                package.Payload.KnowledgeRecords.First(record => record.Id != selected.KnowledgeRecordId).Id, null);
            Assert(!await reader.ValidateSelectionAsync(forged), "A different record paired with a valid path is rejected.");
            var other = new CanonicalSelectorSelection(CanonicalSelectorSelectionKind.OtherContext,
                KnowledgeKind.Location, descriptor.CatalogRevisionId, composition,
                inputs[KnowledgeKind.Location].Policy.Id, inputs[KnowledgeKind.Location].Policy.ExactVersion,
                null, null, "unresolved-test-context");
            Assert(await reader.ValidateSelectionAsync(other), "Other stays unresolved context with no canonical identity.");
            Assert(reader.Statistics.CachedBytes <= reader.Statistics.MaximumCachedBytes &&
                reader.Statistics.MaximumCachedBytes == 32L * 1024 * 1024, "Retained byte cache respects the fixed memory budget.");
            await Reject<ArgumentException>(() => reader.ReadLevelAsync(KnowledgeKind.Location, cursor: "forged"),
                "Forged continuation cursor was accepted.");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Reject<OperationCanceledException>(() => reader.ReadLevelAsync(KnowledgeKind.Location,
                    cancellationToken: cancelled.Token), "Cancelled navigation proceeded.");
                await Reject<OperationCanceledException>(() => PreparedCanonicalNavigationBuilder.BuildAsync(root,
                    loaded, package.Id, digest, inputs, locale, cancelled.Token), "Cancelled preparation published.");
            }
            Assert(firstPointer.SequenceEqual(await File.ReadAllBytesAsync(pointerPath)), "Cancelled preparation preserves publication.");
            Directory.CreateDirectory(Path.Combine(root, ".preparing-interrupted"));
            using (var reopened = await PreparedCanonicalNavigationStore.OpenAsync(root, package.Id, locale))
                Assert(reopened.GenerationId == reader.GenerationId, "Unpublished interrupted staging cannot replace a generation.");
            await Reject<InvalidDataException>(() => PreparedCanonicalNavigationStore.OpenAsync(root, package.Id,
                new("fr-FR", [])), "A different locale reused the prepared generation.");
            var otherPackage = CanonicalCatalogPackageImportChecks.CreateProjectionPackage("other-build", 0x50);
            await Reject<InvalidDataException>(() => PreparedCanonicalNavigationStore.OpenAsync(root, otherPackage.Id, locale),
                "A different package reused the prepared generation.");
            await Reject<InvalidDataException>(() => PreparedCanonicalNavigationBuilder.BuildAsync(
                Path.Combine(scratch, "forged"), new(loaded.Snapshot, []), package.Id, digest, inputs, locale),
                "A publicly constructed load result minted a trusted preparation receipt.");

            await PreparedCanonicalNavigationBuilder.BuildAsync(root, loaded, package.Id, digest,
                Inputs(CanonicalSelectorProjectionPolicy.V2), locale);
            using var replacement = await PreparedCanonicalNavigationStore.OpenAsync(root, package.Id, locale);
            Assert(replacement.GenerationId != reader.GenerationId && !await replacement.ValidateSelectionAsync(selected),
                "A replacement policy publishes a new generation and rejects old selection coordinates.");
            Assert(await reader.ValidateSelectionAsync(selected), "An outstanding old reader retains its immutable generation.");
            Assert(File.Exists(Path.Combine(root, "previous.receipt")), "Publication preserves a rollback receipt.");
            var retiring = await PreparedCanonicalNavigationStore.OpenAsync(root, package.Id, locale);
            var outstanding = Enumerable.Range(0, 12).Select(async iteration =>
            {
                try { _ = await retiring.ReadLevelAsync(KnowledgeKind.Item); return true; }
                catch (ObjectDisposedException) { return false; }
            }).ToArray();
            retiring.Dispose();
            _ = await Task.WhenAll(outstanding);
            Assert(retiring.Statistics.CachedBytes == 0,
                "Reload disposal safely retires concurrent reads without repopulating the released cache.");

            var replacementDirectory = Path.Combine(root, replacement.GenerationId, "directory.json");
            using var directory = JsonDocument.Parse(await File.ReadAllBytesAsync(replacementDirectory));
            var actorPath = directory.RootElement.GetProperty("paths").EnumerateArray().First(value =>
                value.GetProperty("kind").GetInt32() == (int)KnowledgeKind.Actor &&
                value.GetProperty("parentPathId").ValueKind == JsonValueKind.Null);
            var pageId = actorPath.GetProperty("pageIds")[0].GetString()!;
            var pagePath = Path.Combine(root, replacement.GenerationId, pageId + ".page.json");
            var pageBytes = await File.ReadAllBytesAsync(pagePath);
            pageBytes[^1] ^= 1;
            await File.WriteAllBytesAsync(pagePath, pageBytes);
            await Reject<InvalidDataException>(() => replacement.ReadLevelAsync(KnowledgeKind.Actor),
                "A corrupt page reached presentation.");
            Assert(replacement.IsInvalid && replacement.Statistics.CachedBytes == 0,
                "Corruption invalidates the reader and discards its cache.");
            await Reject<InvalidDataException>(() => replacement.ReadLevelAsync(KnowledgeKind.Item),
                "A failed generation remained partially usable.");
            var finalPointer = await File.ReadAllBytesAsync(pointerPath);
            finalPointer[^1] ^= 1;
            await File.WriteAllBytesAsync(pointerPath, finalPointer);
            await Reject<CryptographicException>(() => PreparedCanonicalNavigationStore.OpenAsync(root, package.Id, locale),
                "An altered user-protected receipt was accepted.");
            Assert(digest == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(storePath))).ToLowerInvariant(),
                "Preparation and navigation leave the authoritative source store byte-identical.");
            reader.Dispose();
            await Reject<ObjectDisposedException>(() => reader.ReadLevelAsync(KnowledgeKind.Location),
                "A released generation remained queryable.");
            Console.WriteLine($"Prepared canonical navigation: {checks} focused checks passed.");
            return checks;
        }
        finally
        {
            // This directory was created uniquely by this test and contains only synthetic fixtures.
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static CanonicalCatalogPackage CreatePagedFixture()
    {
        var seed = CanonicalCatalogPackageImportChecks.CreateProjectionPackage("prepared-test", 0x4f);
        var template = seed.Payload.KnowledgeRecords.Single(record => record.Kind == KnowledgeKind.Actor);
        var artifact = seed.Payload.Artifacts.Single();
        var records = seed.Payload.KnowledgeRecords.ToBuilder();
        var terminology = seed.Payload.TerminologyAssertions.ToBuilder();
        var roles = seed.Payload.SemanticClassificationAssertions.ToBuilder();
        var receipts = seed.Payload.FileEvidenceReceipts.ToBuilder();
        var bindings = seed.Payload.EvidenceBindings.ToBuilder();
        void Evidence(CanonicalKnowledgeRecord record, string path, EvidenceClaimKind kind, EvidenceClaimContentId? claim)
        {
            var receipt = new FileEvidenceReceipt(record.SourceRevisionId, artifact.Id, artifact.Digest,
                "grid.synthetic.parser", "1", record.NativeIdentity.ExactRepresentation, path, null, null, null,
                new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));
            var receiptId = EvidenceReceiptId.DeriveV1(receipt);
            receipts.Add(new(receiptId, receipt));
            var bindingId = EvidenceBindingId.DeriveV2(receiptId, kind, record.Id, record.SourceRevisionId, path, claim);
            bindings.Add(new(bindingId, receiptId, kind, record.Id, record.SourceRevisionId, path, claim));
        }
        for (var ordinal = 0; ordinal < 75; ordinal++)
        {
            var native = SourceNativeIdentifier.FromExactUtf8("grid.synthetic.records", "Actor", $"paged-actor-{ordinal:D3}");
            var nativeId = NativeRecordIdentityId.DeriveV1(template.GameId, native);
            var id = KnowledgeRecordId.DeriveV1(template.GameId, template.GameVersion, null,
                template.SourceRevisionId, KnowledgeKind.Actor, nativeId);
            var record = new CanonicalKnowledgeRecord(id, template.GameId, template.GameVersion, null,
                template.SourceRevisionId, KnowledgeKind.Actor, nativeId, native);
            records.Add(record);
            Evidence(record, "/identity", EvidenceClaimKind.KnowledgeIdentity, null);
            // Equal labels deliberately retain distinct canonical identities across pages.
            var term = new TerminologyAssertion(id, record.SourceRevisionId, TerminologyAssertionRole.PrimaryName,
                $"Synthetic Actor {ordinal / 2:D3}", "/name", "en");
            terminology.Add(term);
            Evidence(record, "/name", EvidenceClaimKind.Terminology, EvidenceClaimContentId.DeriveV1(term));
            var roleId = CanonicalSemanticClassificationAssertionId.DeriveV1(id, record.SourceRevisionId,
                CanonicalProjectionSemantics.ActorNpc, "grid.actor-role", "1", "grid.synthetic.actor-role", "1", "/role");
            var role = new CanonicalSemanticClassificationAssertion(roleId, id, record.SourceRevisionId,
                CanonicalProjectionSemantics.ActorNpc, "grid.actor-role", "1", "grid.synthetic.actor-role", "1", "/role");
            roles.Add(role);
            Evidence(record, "/role", EvidenceClaimKind.SemanticClassification, EvidenceClaimContentId.DeriveV1(role));
        }
        var source = seed.Payload;
        var payload = new CanonicalCatalogPayload(source.EffectiveCoverage, source.AdapterDescriptors, source.Sources,
            source.Artifacts, source.SourceRevisions, records.ToImmutable(), terminology.ToImmutable(),
            source.RelationshipAssertions, receipts.ToImmutable(), source.ReferenceEvidenceReceipts,
            bindings.ToImmutable(), source.CorrelationEnvelopes, source.UnresolvedSourceAssertions,
            source.AcquisitionReceipts, source.ArtifactAcquisitionBindings)
        {
            SourceNativeLocationTypeAssertions = source.SourceNativeLocationTypeAssertions,
            LocationSemanticClassificationAssertions = source.LocationSemanticClassificationAssertions,
            RecordLifecycleAssertions = source.RecordLifecycleAssertions,
            CorrelatedRelationshipEnvelopes = source.CorrelatedRelationshipEnvelopes,
            LocationCoverageReports = source.LocationCoverageReports,
            SemanticClassificationAssertions = roles.ToImmutable(),
            RecordContributionAssertions = source.RecordContributionAssertions,
            OrganizationalValueAssertions = source.OrganizationalValueAssertions,
            InstructionAssertions = source.InstructionAssertions,
            InstructionEvidenceBindings = source.InstructionEvidenceBindings,
            InstructionConflictGroups = source.InstructionConflictGroups,
            CrossSourceAssertions = source.CrossSourceAssertions,
            CrossSourceTargetLinkClaims = source.CrossSourceTargetLinkClaims,
            UnresolvedCrossSourceClaimContents = source.UnresolvedCrossSourceClaimContents,
            UnresolvedCrossSourceEvidenceBindings = source.UnresolvedCrossSourceEvidenceBindings,
            UnresolvedCrossSourceAssertions = source.UnresolvedCrossSourceAssertions,
        };
        return CanonicalCatalogPackageKernel.CreateV5(seed.Manifest.PackageKind, seed.Manifest.GameScope,
            seed.Manifest.ModScope, seed.Manifest.RequiredBasePackageIds, seed.Manifest.CompositionPolicyVersion,
            payload, seed.ValidationSummary, seed.Manifest.BuildProvenance);
    }
}
