using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalKnowledgeCatalogStoreChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Check failed: {message}");
            checks++;
        }

        var fixtureRoot = Path.Combine(Path.GetTempPath(), "grid-canonical-catalog-" + Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(fixtureRoot, "catalog", "canonical-knowledge.v1.json");

        try
        {
            var first = CreateRegistration("revision-1", '1', "  Source Name V1—exact  \r\n");
            var second = CreateRegistration("revision-2", '2', "Source Name V2");
            var store = new JsonCanonicalKnowledgeCatalogStore(storePath);

            var empty = await store.LoadAsync();
            Assert(empty.IsValid && empty.Snapshot == CanonicalKnowledgeCatalogSnapshot.Empty,
                "A missing store loads as the immutable empty canonical snapshot.");

            var appended = await store.AppendAsync(0, first.Registration);
            Assert(appended.Status == CanonicalCatalogAppendStatus.Appended && appended.Revision == 1,
                "The first complete canonical source revision appends at revision one.");

            var firstLoad = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
            Assert(firstLoad.IsValid && firstLoad.Snapshot.Revision == 1,
                "A separately constructed store reads the committed snapshot.");
            Assert(firstLoad.Snapshot.FindSource(first.Registration.Source.Id) == first.Registration.Source &&
                   firstLoad.Snapshot.FindArtifact(first.Registration.Artifacts.Single().Id) == first.Registration.Artifacts.Single(),
                "Historical source and artifact lookups use their canonical identities.");
            Assert(firstLoad.Snapshot.FindKnowledgeRecord(first.Record.Id) == first.Record,
                "Historical knowledge lookup returns the exact immutable canonical record.");
            Assert(firstLoad.Snapshot.FindSourceRevision(first.Revision.Id) is not null,
                "Historical source-revision lookup is keyed by canonical revision identity.");
            Assert(firstLoad.Snapshot.TerminologyAssertions.Single().VerbatimValue == "  Source Name V1—exact  \r\n",
                "Persistence preserves source terminology byte-for-byte at the string boundary.");

            var fileLookup = firstLoad.Snapshot.FindEvidenceReceipt(first.IdentityFileEvidence.Id);
            var referenceLookup = firstLoad.Snapshot.FindEvidenceReceipt(first.TerminologyReferenceEvidence.Id);
            Assert(fileLookup is { Verification: EvidenceVerificationKind.FileVerified, FileReceipt: not null, ReferenceReceipt: null },
                "Historical evidence lookup retains the structurally distinct file receipt.");
            Assert(referenceLookup is { Verification: EvidenceVerificationKind.ReferenceVerified, FileReceipt: null, ReferenceReceipt: not null },
                "Historical evidence lookup retains the structurally distinct reference receipt.");
            Assert(firstLoad.Snapshot.FindEvidenceBinding(first.IdentityBinding.Id) == first.IdentityBinding,
                "Claim-level evidence bindings have stable historical lookup identities.");

            var repeated = await store.AppendAsync(0, first.Registration);
            Assert(repeated.Status == CanonicalCatalogAppendStatus.Unchanged && repeated.Revision == 1,
                "Retrying the identical source revision is idempotent even with a stale expected snapshot revision.");

            var changedTerm = first.Registration with
            {
                TerminologyAssertions =
                [
                    new TerminologyAssertion(
                        first.Record.Id,
                        first.Revision.Id,
                        TerminologyAssertionRole.PrimaryName,
                        "different immutable assertion",
                        "records[0].name",
                        "en"),
                ],
            };
            var immutableConflict = await store.AppendAsync(1, changedTerm);
            Assert(immutableConflict.Status == CanonicalCatalogAppendStatus.Invalid,
                "A registered source revision cannot be reopened with changed immutable assertions.");

            var missingClaimEvidence = second.Registration with
            {
                EvidenceBindings = second.Registration.EvidenceBindings
                    .Where(value => value.ClaimKind != EvidenceClaimKind.Terminology)
                    .ToImmutableArray(),
            };
            var rejectedWithoutEvidence = await store.AppendAsync(1, missingClaimEvidence);
            Assert(rejectedWithoutEvidence.Status == CanonicalCatalogAppendStatus.Invalid,
                "A canonical terminology assertion cannot be persisted without claim-level authoritative evidence.");

            var secondTerm = second.Registration.TerminologyAssertions.Single();
            var secondRelationship = second.Registration.RelationshipAssertions.Single();
            var idReceiptForTerminology = CreateBinding(
                second.IdentityFileEvidence.Id,
                EvidenceClaimKind.Terminology,
                second.Record.Id,
                second.Revision.Id,
                "records[0].id",
                EvidenceClaimContentId.DeriveV1(secondTerm));
            var terminologyUsingIdReceipt = ReplaceBinding(
                second.Registration,
                EvidenceClaimKind.Terminology,
                idReceiptForTerminology);
            Assert((await store.AppendAsync(1, terminologyUsingIdReceipt)).Status == CanonicalCatalogAppendStatus.Invalid,
                "An ID-field receipt cannot authorize a name-field terminology assertion.");

            var idReceiptForRelationship = CreateBinding(
                second.IdentityFileEvidence.Id,
                EvidenceClaimKind.Relationship,
                second.Record.Id,
                second.Revision.Id,
                "records[0].id",
                EvidenceClaimContentId.DeriveV1(secondRelationship));
            var relationshipUsingIdReceipt = ReplaceBinding(
                second.Registration,
                EvidenceClaimKind.Relationship,
                idReceiptForRelationship);
            Assert((await store.AppendAsync(1, relationshipUsingIdReceipt)).Status == CanonicalCatalogAppendStatus.Invalid,
                "An ID-field receipt cannot authorize a parent relationship assertion.");

            var falseLocatorBinding = CreateBinding(
                second.TerminologyReferenceEvidence.Id,
                EvidenceClaimKind.Terminology,
                second.Record.Id,
                second.Revision.Id,
                "records[0].not-the-name-field",
                EvidenceClaimContentId.DeriveV1(secondTerm));
            var falseLocatorRegistration = ReplaceBinding(
                second.Registration,
                EvidenceClaimKind.Terminology,
                falseLocatorBinding);
            Assert((await store.AppendAsync(1, falseLocatorRegistration)).Status == CanonicalCatalogAppendStatus.Invalid,
                "A binding locator must exactly equal its referenced receipt's actual field path.");

            var additionalTerm = new TerminologyAssertion(
                second.Record.Id,
                second.Revision.Id,
                TerminologyAssertionRole.Alias,
                "A second exact source assertion",
                secondTerm.SourceFieldPath,
                secondTerm.LanguageTag);
            var twoTermsOneBinding = second.Registration with
            {
                TerminologyAssertions = [secondTerm, additionalTerm],
            };
            Assert((await store.AppendAsync(1, twoTermsOneBinding)).Status == CanonicalCatalogAppendStatus.Invalid,
                "Two distinct terminology assertions cannot share one generic evidence binding.");

            var additionalTermBinding = CreateBinding(
                second.TerminologyReferenceEvidence.Id,
                EvidenceClaimKind.Terminology,
                second.Record.Id,
                second.Revision.Id,
                secondTerm.SourceFieldPath,
                EvidenceClaimContentId.DeriveV1(additionalTerm));
            var independentlyBoundTerms = twoTermsOneBinding with
            {
                EvidenceBindings = twoTermsOneBinding.EvidenceBindings.Add(additionalTermBinding),
            };
            var independentlyBoundPath = Path.Combine(fixtureRoot, "exact-multiple-claims", "catalog.json");
            var independentlyBoundStore = new JsonCanonicalKnowledgeCatalogStore(independentlyBoundPath);
            Assert((await independentlyBoundStore.AppendAsync(0, independentlyBoundTerms)).Status == CanonicalCatalogAppendStatus.Appended &&
                   (await independentlyBoundStore.LoadAsync()).Snapshot.TerminologyAssertions.Length == 2,
                "One receipt can support multiple claims only through separate exact-content bindings.");

            var crossRevisionIdentityBinding = CreateBinding(
                first.IdentityFileEvidence.Id,
                EvidenceClaimKind.KnowledgeIdentity,
                second.Record.Id,
                second.Revision.Id,
                "records[0].id");
            var crossRevisionEvidence = second.Registration with
            {
                EvidenceBindings = second.Registration.EvidenceBindings
                    .Select(value => value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity
                        ? crossRevisionIdentityBinding
                        : value)
                    .ToImmutableArray(),
            };
            var rejectedCrossRevisionEvidence = await store.AppendAsync(1, crossRevisionEvidence);
            Assert(rejectedCrossRevisionEvidence.Status == CanonicalCatalogAppendStatus.Invalid,
                "Claim bindings cannot reuse evidence from a different immutable source revision.");

            var staleAppend = await store.AppendAsync(0, second.Registration);
            Assert(staleAppend.Status == CanonicalCatalogAppendStatus.Conflict && staleAppend.Revision == 1,
                "Optimistic snapshot revision checks prevent lost canonical updates.");

            var secondAppend = await store.AppendAsync(1, second.Registration);
            Assert(secondAppend.Status == CanonicalCatalogAppendStatus.Appended && secondAppend.Revision == 2,
                "A new immutable source revision appends after the current snapshot revision.");

            var finalLoad = await store.LoadAsync();
            Assert(finalLoad.IsValid && finalLoad.Snapshot.Revision == 2,
                "The latest read returns one snapshot-consistent catalog revision.");
            Assert(finalLoad.Snapshot.Sources.Length == 1 && finalLoad.Snapshot.SourceRevisions.Length == 2,
                "Shared source identity deduplicates while distinct source revisions remain historical records.");
            Assert(finalLoad.Snapshot.KnowledgeRecords.Length == 2 &&
                   finalLoad.Snapshot.FindKnowledgeRecord(first.Record.Id) is not null &&
                   finalLoad.Snapshot.FindKnowledgeRecord(second.Record.Id) is not null,
                "A later source revision preserves both old and new canonical knowledge identities.");
            var historicalTerms = finalLoad.Snapshot.TerminologyAssertions
                .Select(value => value.VerbatimValue)
                .ToImmutableHashSet(StringComparer.Ordinal);
            Assert(historicalTerms.Count == 2 &&
                   historicalTerms.Contains("  Source Name V1—exact  \r\n") &&
                   historicalTerms.Contains("Source Name V2"),
                "Disagreeing source-revision terminology remains independently persisted.");
            Assert(firstLoad.Snapshot.Revision == 1 && firstLoad.Snapshot.FindKnowledgeRecord(second.Record.Id) is null,
                "A previously returned snapshot remains unchanged after a later append.");
            Assert(finalLoad.Snapshot.RelationshipAssertions.All(value =>
                    value.Resolution == CanonicalResolutionState.Unresolved && value.SourceNativeTarget is not null),
                "Unresolved native relationship targets persist without invented display terminology.");

            var identicalConcurrentPath = Path.Combine(fixtureRoot, "concurrent-identical", "catalog.json");
            var identicalConcurrent = await Task.WhenAll(
                new JsonCanonicalKnowledgeCatalogStore(identicalConcurrentPath).AppendAsync(0, first.Registration),
                new JsonCanonicalKnowledgeCatalogStore(identicalConcurrentPath).AppendAsync(0, first.Registration));
            Assert(identicalConcurrent.Select(value => value.Status).Order().SequenceEqual(
                    [CanonicalCatalogAppendStatus.Appended, CanonicalCatalogAppendStatus.Unchanged]),
                "Identical concurrent tasks produce exactly one append and one unchanged result.");

            var distinctConcurrentPath = Path.Combine(fixtureRoot, "concurrent-distinct", "catalog.json");
            var distinctConcurrent = await Task.WhenAll(
                new JsonCanonicalKnowledgeCatalogStore(distinctConcurrentPath).AppendAsync(0, first.Registration),
                new JsonCanonicalKnowledgeCatalogStore(distinctConcurrentPath).AppendAsync(0, second.Registration));
            Assert(distinctConcurrent.Select(value => value.Status).Order().SequenceEqual(
                    [CanonicalCatalogAppendStatus.Appended, CanonicalCatalogAppendStatus.Conflict]),
                "Distinct concurrent tasks against one expected revision produce one append and one conflict.");

            var laterFileObservation = new FileEvidenceReceipt(
                first.IdentityFileEvidence.Receipt.SourceRevisionId,
                first.IdentityFileEvidence.Receipt.SourceArtifactId,
                first.IdentityFileEvidence.Receipt.ArtifactDigest,
                first.IdentityFileEvidence.Receipt.ParserId,
                first.IdentityFileEvidence.Receipt.ParserVersion,
                first.IdentityFileEvidence.Receipt.NativeRecordLocator,
                first.IdentityFileEvidence.Receipt.SourceFieldPath,
                first.IdentityFileEvidence.Receipt.ByteOffset,
                first.IdentityFileEvidence.Receipt.ByteLength,
                first.IdentityFileEvidence.Receipt.InterpretedBytesDigest,
                first.IdentityFileEvidence.Receipt.ObservedAtUtc.AddDays(1));
            Assert(EvidenceReceiptId.DeriveV1(laterFileObservation) == first.IdentityFileEvidence.Id,
                "Observation timestamps do not alter deterministic file-evidence coordinates.");
            var laterReferenceRetrieval = new ReferenceEvidenceReceipt(
                first.TerminologyReferenceEvidence.Receipt.ProviderCatalogSourceId,
                first.TerminologyReferenceEvidence.Receipt.SourceRevisionId,
                first.TerminologyReferenceEvidence.Receipt.ResponseArtifactId,
                first.TerminologyReferenceEvidence.Receipt.ResponseContentDigest,
                first.TerminologyReferenceEvidence.Receipt.NativeObjectIdentity,
                first.TerminologyReferenceEvidence.Receipt.NativeRevisionIdentity,
                first.TerminologyReferenceEvidence.Receipt.ResponseFieldPath,
                first.TerminologyReferenceEvidence.Receipt.RetrievedAtUtc.AddDays(1));
            Assert(EvidenceReceiptId.DeriveV1(laterReferenceRetrieval) == first.TerminologyReferenceEvidence.Id,
                "Retrieval timestamps do not alter deterministic reference-evidence coordinates.");

            var registrationSurface = typeof(CanonicalCatalogRegistration).GetProperties();
            Assert(registrationSurface.All(property =>
                    !property.Name.Contains("Other", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Contains("Ticket", StringComparison.OrdinalIgnoreCase) &&
                    !property.PropertyType.Name.Contains("Other", StringComparison.OrdinalIgnoreCase) &&
                    !property.PropertyType.Name.Contains("Ticket", StringComparison.OrdinalIgnoreCase)),
                "Shared catalog writes expose no Other context, ticket identity, or ticket-user text contract.");

            var validSnapshotJson = await File.ReadAllTextAsync(storePath);
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };

            var legacyDocument = JsonNode.Parse(validSnapshotJson)!.AsObject();
            legacyDocument["schemaVersion"] = 1;
            legacyDocument.Remove("adapterDescriptors");
            legacyDocument.Remove("adapterBoundSourceRevisions");
            legacyDocument.Remove("correlationEnvelopes");
            legacyDocument.Remove("unresolvedSourceAssertions");
            legacyDocument.Remove("acquisitionReceipts");
            legacyDocument.Remove("artifactAcquisitionBindings");
            legacyDocument.Remove("importedPackages");
            var legacyPath = Path.Combine(fixtureRoot, "legacy-v1", "catalog.json");
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            await File.WriteAllTextAsync(legacyPath, legacyDocument.ToJsonString(jsonOptions));
            var legacyLoad = await new JsonCanonicalKnowledgeCatalogStore(legacyPath).LoadAsync();
            Assert(legacyLoad.IsValid && legacyLoad.Snapshot.Revision == 2 &&
                   legacyLoad.Snapshot.FindKnowledgeRecord(first.Record.Id) is not null &&
                   legacyLoad.Snapshot.FindKnowledgeRecord(second.Record.Id) is not null &&
                   legacyLoad.Snapshot.AdapterBoundSourceRevisions.IsEmpty &&
                   legacyLoad.Snapshot.ImportedPackages.IsEmpty,
                "Schema-v1 snapshots remain readable as immutable v1 history without reinterpretation.");

            var provenanceTamper = JsonNode.Parse(validSnapshotJson)!.AsObject();
            var maliciousBinding = CreateBinding(
                first.IdentityFileEvidence.Id,
                EvidenceClaimKind.Terminology,
                first.Record.Id,
                first.Revision.Id,
                first.IdentityFileEvidence.Receipt.SourceFieldPath,
                EvidenceClaimContentId.DeriveV1(first.Registration.TerminologyAssertions.Single()));
            var persistedBindings = provenanceTamper["evidenceBindings"]!.AsArray();
            var terminologyBindingIndex = persistedBindings
                .Select((value, index) => (value, index))
                .Single(value =>
                    value.value!["claimKind"]!.GetValue<int>() == (int)EvidenceClaimKind.Terminology &&
                    string.Equals(
                        value.value["knowledgeRecordId"]!["value"]!.GetValue<string>(),
                        first.Record.Id.Value,
                        StringComparison.Ordinal))
                .index;
            persistedBindings[terminologyBindingIndex] = JsonSerializer.SerializeToNode(maliciousBinding, jsonOptions);
            await File.WriteAllTextAsync(storePath, provenanceTamper.ToJsonString(jsonOptions));
            Assert(!(await store.LoadAsync()).IsValid,
                "Reload rejects a self-consistent binding that borrows ID-field evidence for exact terminology content.");
            await File.WriteAllTextAsync(storePath, validSnapshotJson);

            var falsePersistedLocatorTamper = JsonNode.Parse(validSnapshotJson)!.AsObject();
            var falsePersistedLocatorBinding = CreateBinding(
                first.TerminologyReferenceEvidence.Id,
                EvidenceClaimKind.Terminology,
                first.Record.Id,
                first.Revision.Id,
                "records[0].false-field",
                EvidenceClaimContentId.DeriveV1(first.Registration.TerminologyAssertions.Single()));
            falsePersistedLocatorTamper["evidenceBindings"]!.AsArray()[terminologyBindingIndex] =
                JsonSerializer.SerializeToNode(falsePersistedLocatorBinding, jsonOptions);
            await File.WriteAllTextAsync(storePath, falsePersistedLocatorTamper.ToJsonString(jsonOptions));
            Assert(!(await store.LoadAsync()).IsValid,
                "Reload rejects a binding whose locator differs from its receipt's actual field path.");
            await File.WriteAllTextAsync(storePath, validSnapshotJson);

            var revisionTamper = JsonNode.Parse(validSnapshotJson)!.AsObject();
            revisionTamper["revision"] = 99;
            await File.WriteAllTextAsync(storePath, revisionTamper.ToJsonString(jsonOptions));
            Assert(!(await store.LoadAsync()).IsValid &&
                   JsonNode.Parse(await File.ReadAllTextAsync(storePath))!["revision"]!.GetValue<long>() == 99,
                "Revision-counter tampering fails closed and is not silently repaired during load.");
            await File.WriteAllTextAsync(storePath, validSnapshotJson);

            var orphanSourceNative = SourceNativeIdentifier.FromExactUtf8(
                "provider.fixture",
                "dataset",
                "provider.fixture:orphan-source");
            var orphanSource = new CatalogSourceRecord(
                CatalogSourceId.DeriveV1(KnowledgeSourceKind.ReferenceProvider, orphanSourceNative),
                KnowledgeSourceKind.ReferenceProvider,
                orphanSourceNative);
            var orphanSourceTamper = JsonNode.Parse(validSnapshotJson)!.AsObject();
            orphanSourceTamper["sources"]!.AsArray().Add(JsonSerializer.SerializeToNode(orphanSource, jsonOptions));
            await File.WriteAllTextAsync(storePath, orphanSourceTamper.ToJsonString(jsonOptions));
            Assert(!(await store.LoadAsync()).IsValid,
                "A valid but unreachable persisted source fails snapshot closure validation.");
            await File.WriteAllTextAsync(storePath, validSnapshotJson);

            var orphanDigest = new ContentDigest(ContentDigest.Sha256Algorithm, new string('f', 64));
            var orphanArtifact = new SourceArtifactRecord(SourceArtifactId.DeriveV1(orphanDigest), orphanDigest);
            var orphanArtifactTamper = JsonNode.Parse(validSnapshotJson)!.AsObject();
            orphanArtifactTamper["artifacts"]!.AsArray().Add(JsonSerializer.SerializeToNode(orphanArtifact, jsonOptions));
            await File.WriteAllTextAsync(storePath, orphanArtifactTamper.ToJsonString(jsonOptions));
            Assert(!(await store.LoadAsync()).IsValid,
                "A valid but unreachable persisted artifact fails snapshot closure validation.");
            await File.WriteAllTextAsync(storePath, validSnapshotJson);

            var restoredMultiRevision = await store.LoadAsync();
            Assert(restoredMultiRevision.IsValid && restoredMultiRevision.Snapshot.Revision == 2 &&
                   restoredMultiRevision.Snapshot.FindKnowledgeRecord(first.Record.Id) is not null &&
                   restoredMultiRevision.Snapshot.FindKnowledgeRecord(second.Record.Id) is not null,
                "A valid multi-revision snapshot reloads with all historical canonical lookups preserved.");

            var maximumStoreBytesField = typeof(JsonCanonicalKnowledgeCatalogStore).GetField(
                "MaximumStoreBytes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert(maximumStoreBytesField?.IsLiteral == true &&
                   maximumStoreBytesField.GetRawConstantValue() is long maximumStoreBytes &&
                   maximumStoreBytes == 512L * 1024 * 1024,
                "The game-neutral catalog-store read ceiling is exactly 512 MiB.");
            var oversizedStorePath = Path.Combine(fixtureRoot, "catalog", "oversized-canonical-knowledge.v5.json");
            await using (var oversized = new FileStream(
                             oversizedStorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                oversized.SetLength(512L * 1024 * 1024 + 1);
            var oversizedLoad = await new JsonCanonicalKnowledgeCatalogStore(oversizedStorePath).LoadAsync();
            Assert(!oversizedLoad.IsValid &&
                   oversizedLoad.Snapshot == CanonicalKnowledgeCatalogSnapshot.Empty,
                "A store one byte above the 512-MiB ceiling still fails closed before JSON materialization.");

            await File.WriteAllTextAsync(storePath, "{\"schemaVersion\":1,\"revision\":2}");
            var corruptLoad = await store.LoadAsync();
            Assert(!corruptLoad.IsValid && corruptLoad.Snapshot == CanonicalKnowledgeCatalogSnapshot.Empty,
                "An incomplete persisted document is rejected instead of yielding a partial snapshot.");
            var protectedAppend = await store.AppendAsync(2, second.Registration);
            Assert(protectedAppend.Status == CanonicalCatalogAppendStatus.Failed,
                "An invalid existing store is never overwritten by an append.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }

        return checks;
    }

    private static RegistrationFixture CreateRegistration(string revisionValue, char digestCharacter, string terminology)
    {
        var sourceNativeIdentity = SourceNativeIdentifier.FromExactUtf8(
            "provider.fixture",
            "dataset",
            "provider.fixture:canonical-dataset");
        var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.OfficialProvider, sourceNativeIdentity);
        var source = new CatalogSourceRecord(sourceId, KnowledgeSourceKind.OfficialProvider, sourceNativeIdentity);

        var digest = new ContentDigest(ContentDigest.Sha256Algorithm, new string(digestCharacter, 64));
        var artifactId = SourceArtifactId.DeriveV1(digest);
        var artifact = new SourceArtifactRecord(artifactId, digest);
        var nativeRevision = SourceNativeVersion.FromExactUtf8("provider.fixture.revision", revisionValue);
        var revisionId = CatalogSourceRevisionId.DeriveV1(sourceId, nativeRevision, [artifactId]);
        var revision = new CatalogSourceRevisionRecord(revisionId, sourceId, nativeRevision, [artifactId]);

        var gameId = new GameId("game.fixture");
        var nativeRecord = SourceNativeIdentifier.FromExactUtf8(
            "provider.fixture.records",
            "item",
            "record:item:42");
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(gameId, nativeRecord);
        var gameVersion = SourceNativeVersion.FromExactUtf8("game.fixture.build", "1.0.0");
        var recordId = KnowledgeRecordId.DeriveV1(
            gameId,
            gameVersion,
            nativeRevision,
            revisionId,
            KnowledgeKind.Item,
            nativeRecordId);
        var record = new CanonicalKnowledgeRecord(
            recordId,
            gameId,
            gameVersion,
            nativeRevision,
            revisionId,
            KnowledgeKind.Item,
            nativeRecordId,
            nativeRecord);

        var term = new TerminologyAssertion(
            recordId,
            revisionId,
            TerminologyAssertionRole.PrimaryName,
            terminology,
            "records[0].name",
            "en");
        var relationship = new RelationshipAssertion(
            recordId,
            revisionId,
            new RelationshipSemanticId("grid.relationship.contained-by"),
            "provider-parent",
            "records[0].parentId",
            SourceNativeIdentifier.FromExactUtf8("provider.fixture.records", "location", "record:location:7"));

        var observedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var identityReceipt = new FileEvidenceReceipt(
            revisionId,
            artifactId,
            digest,
            "fixture.parser",
            "1",
            "records[0]",
            "records[0].id",
            0,
            8,
            null,
            observedAt);
        var identityEvidence = new CatalogFileEvidenceReceipt(
            EvidenceReceiptId.DeriveV1(identityReceipt),
            identityReceipt);

        var relationshipReceipt = new FileEvidenceReceipt(
            revisionId,
            artifactId,
            digest,
            "fixture.parser",
            "1",
            "records[0]",
            "records[0].parentId",
            24,
            8,
            null,
            observedAt);
        var relationshipEvidence = new CatalogFileEvidenceReceipt(
            EvidenceReceiptId.DeriveV1(relationshipReceipt),
            relationshipReceipt);

        var terminologyReceipt = new ReferenceEvidenceReceipt(
            sourceId,
            revisionId,
            artifactId,
            digest,
            nativeRecord,
            nativeRevision,
            "records[0].name",
            observedAt);
        var terminologyEvidence = new CatalogReferenceEvidenceReceipt(
            EvidenceReceiptId.DeriveV1(terminologyReceipt),
            terminologyReceipt);

        var identityBinding = CreateBinding(
            identityEvidence.Id,
            EvidenceClaimKind.KnowledgeIdentity,
            recordId,
            revisionId,
            "records[0].id");
        var terminologyBinding = CreateBinding(
            terminologyEvidence.Id,
            EvidenceClaimKind.Terminology,
            recordId,
            revisionId,
            "records[0].name",
            EvidenceClaimContentId.DeriveV1(term));
        var relationshipBinding = CreateBinding(
            relationshipEvidence.Id,
            EvidenceClaimKind.Relationship,
            recordId,
            revisionId,
            "records[0].parentId",
            EvidenceClaimContentId.DeriveV1(relationship));

        var registration = new CanonicalCatalogRegistration(
            source,
            [artifact],
            revision,
            [record],
            [term],
            [relationship],
            [identityEvidence, relationshipEvidence],
            [terminologyEvidence],
            [identityBinding, terminologyBinding, relationshipBinding]);

        return new(
            registration,
            revision,
            record,
            identityEvidence,
            terminologyEvidence,
            identityBinding);
    }

    private static EvidenceBinding CreateBinding(
        EvidenceReceiptId receiptId,
        EvidenceClaimKind claimKind,
        KnowledgeRecordId recordId,
        CatalogSourceRevisionId revisionId,
        string locator,
        EvidenceClaimContentId? claimContentId = null) =>
        new(
            EvidenceBindingId.DeriveV2(receiptId, claimKind, recordId, revisionId, locator, claimContentId),
            receiptId,
            claimKind,
            recordId,
            revisionId,
            locator,
            claimContentId);

    private static CanonicalCatalogRegistration ReplaceBinding(
        CanonicalCatalogRegistration registration,
        EvidenceClaimKind claimKind,
        EvidenceBinding replacement) =>
        registration with
        {
            EvidenceBindings = registration.EvidenceBindings
                .Select(value => value.ClaimKind == claimKind ? replacement : value)
                .ToImmutableArray(),
        };

    private sealed record RegistrationFixture(
        CanonicalCatalogRegistration Registration,
        CatalogSourceRevisionRecord Revision,
        CanonicalKnowledgeRecord Record,
        CatalogFileEvidenceReceipt IdentityFileEvidence,
        CatalogReferenceEvidenceReceipt TerminologyReferenceEvidence,
        EvidenceBinding IdentityBinding);
}
