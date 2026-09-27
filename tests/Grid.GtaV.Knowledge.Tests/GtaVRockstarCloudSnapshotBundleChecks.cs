using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVRockstarCloudSnapshotBundleChecks
{
    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "grid-gta-cloud-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = ImmutableArray.Create("Exact_Case_One", "Exact_Ünicode_Two");
            var descriptor = DescriptorBytes();
            var valid = Path.Combine(root, "valid");
            WriteTargetBundle(valid, expected);
            var first = GtaVRockstarCloudSnapshotBundleLoader.Load(valid, descriptor, expected);
            var second = GtaVRockstarCloudSnapshotBundleLoader.Load(valid, descriptor, expected.Reverse().ToImmutableArray());
            Assert(first.EnvelopeArtifact == second.EnvelopeArtifact &&
                   first.AcquisitionReceipt.Id == second.AcquisitionReceipt.Id &&
                   first.RequestSetDigest == second.RequestSetDigest,
                "Identical bundle inputs produce stable artifact, acquisition, and request-set identities.");
            Assert(first.ProviderSource.Kind == KnowledgeSourceKind.OfficialProvider &&
                   first.FrozenEnvelopeArtifact.DeclaredFormat == GtaVRockstarCloudSnapshotBundleLoader.SnapshotFormat &&
                   first.AcquisitionBinding.ArtifactId == first.EnvelopeArtifact.Id,
                "The verified snapshot projects one OfficialProvider source and one acquisition-bound envelope artifact.");
            Assert(first.Index.Objects.Length == 2 && first.Index.PhysicalResponseParseCount == 2 &&
                   first.Index.EquivalentDuplicateCount == 0 && first.Index.DistinctExactFmnmCount == 2,
                "Every response is indexed exactly once at provider-object grain.");
            var exact = first.Index.FindExactFmnm("Exact_Case_One").Single();
            Assert(exact.ExactTitle == "A Rockstar Title" &&
                   exact.ExactActivityFamilyIdentity == "mission" &&
                   exact.ExactActivityFamilyLabel == "Missions" && exact.IsOnline == true,
                "The index exposes exact title, activity family, and Online fields without transformation.");
            Assert(first.Index.FindExactFmnm("exact_case_one").IsEmpty &&
                   first.Index.FindExactFmnm("Exact_Unicode_Two").IsEmpty,
                "fmnm lookup is ordinal and performs no case or Unicode normalization.");
            Assert(exact.TitleFieldPath == "/responses/0/rawBodyBase64#json/jobs/0/title" &&
                   exact.FmnmFieldPath == "/responses/0/rawBodyBase64#json/jobs/0/fmnm",
                "Indexed evidence locators retain exact envelope, response, object, and field coordinates.");

            var origin = CreateMissionOrigin(expected.Add("exact_case_one"));
            var cloudAdapter = new GtaVCloudJobHeaderSecondaryAssertionAdapter(
                ContentDigest.ComputeSha256("cloud-job-adapter-test"u8));
            var cloudResult = cloudAdapter.Extract(
                origin,
                KnowledgeSourceScope.BaseGame(
                    ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                    SourceNativeVersion.FromExactUtf8(
                        "valve.steam.app.3240220.build-id", "25261616",
                        "valve.steam.build-id.exact-utf8", 1)),
                first);
            var exactTargets = origin.KnowledgeRecords
                .Where(value => expected.Contains(value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal))
                .Select(value => value.Id).ToHashSet();
            var mismatchTarget = origin.KnowledgeRecords.Single(value =>
                value.NativeIdentity.ExactRepresentation == "exact_case_one").Id;
            Assert(cloudResult.Metrics.ExactMatchedTargetRecordCount == 2 &&
                   cloudResult.Metrics.UnmatchedTargetRecordCount == 1 &&
                   cloudResult.Batch.TerminologyAssertions.Length == 2 &&
                   cloudResult.Batch.SemanticClassifications.Length == 2 &&
                   cloudResult.Batch.OrganizationalValues.Length == 2,
                "The batch adapter enriches the complete exact-match set without fabricating a case-folded match.");
            Assert(cloudResult.Batch.TerminologyAssertions.All(value => exactTargets.Contains(value.KnowledgeRecordId)) &&
                   cloudResult.Batch.TerminologyAssertions.All(value => value.KnowledgeRecordId != mismatchTarget) &&
                   cloudResult.Batch.ReferenceEvidenceReceipts.Length > 0 &&
                   cloudResult.Batch.CrossSourceAssertions.Length == 6,
                "Verified provider objects add only evidence-bound title/family/Online assertions to existing IDs.");
            var acquisition = CreateAcquisitionClosure(origin, first);
            var enriched = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
                origin, cloudResult.Batch, acquisition.Receipts, acquisition.Bindings);
            Assert(enriched.KnowledgeRecords.Select(value => value.Id)
                       .SequenceEqual(origin.KnowledgeRecords.Select(value => value.Id)) &&
                   enriched.KnowledgeRecords.Select(value => value.SourceRevisionId)
                       .SequenceEqual(origin.KnowledgeRecords.Select(value => value.SourceRevisionId)),
                "Provider enrichment preserves every established MissionQuest identity and origin revision.");
            Assert(enriched.ReferenceEvidenceReceipts.All(value =>
                       value.Receipt.Verification == EvidenceVerificationKind.ReferenceVerified) &&
                   enriched.CrossSourceAssertions.All(value =>
                       value.TargetLinkKind == CrossSourceTargetLinkKind.VersionedExactMapping),
                "Cloud assertions retain REFERENCE_VERIFIED provider evidence and exact-mapping envelopes.");
            var firstProviderObject = first.Index.FindExactFmnm("Exact_Case_One").Single();
            var firstFamily = enriched.OrganizationalValueAssertions.Single(value =>
                value.KnowledgeRecordId == origin.KnowledgeRecords.Single(record =>
                    record.NativeIdentity.ExactRepresentation == "Exact_Case_One").Id);
            var firstFamilyContent = EvidenceClaimContentId.DeriveV1(firstFamily);
            var firstFamilyBindings = enriched.EvidenceBindings.Where(value =>
                    value.ClaimKind == EvidenceClaimKind.OrganizationalValue &&
                    value.ClaimContentId == firstFamilyContent)
                .OrderBy(value => value.ClaimLocator, StringComparer.Ordinal)
                .ToImmutableArray();
            Assert(firstFamily.SourceFieldPath.EndsWith("/activityFamily", StringComparison.Ordinal) &&
                   firstFamilyBindings.Length == 2 &&
                   firstFamilyBindings.Select(value => value.ClaimLocator).SequenceEqual(
                       new[]
                       {
                           firstProviderObject.ActivityFamilyIdentityFieldPath!,
                           firstProviderObject.ActivityFamilyLabelFieldPath!,
                       }.Order(StringComparer.Ordinal),
                       StringComparer.Ordinal),
                "A composite activity-family assertion is independently bound to its exact identity and label fields.");

            var package = CanonicalCatalogPackageKernel.CreateV6(
                CatalogPackageKind.BaseGameCatalog,
                new CatalogGameScope(
                    ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                    origin.KnowledgeRecords[0].GameVersion,
                    enriched.Artifacts.Select(value => value.Id).ToImmutableArray()),
                null, [], "grid.catalog-composition.v1", enriched,
                new CatalogValidationSummary(
                    CatalogValidationStatus.Candidate,
                    "grid.gta-v.cloud-job.structural", "1",
                    ContentDigest.ComputeSha256("candidate-cloud-job"u8)),
                new CatalogBuildProvenance(
                    CatalogBuildProvenance.CurrentSchemaVersion,
                    "grid.gta-v.cloud-job.tests", "1", new string('a', 40),
                    [new CatalogCommittedBuildInput(
                        "src/Grid.GtaV.Knowledge/GtaVCloudJobHeaderSecondaryAssertionAdapter.cs",
                        new string('b', 40))]));
            var packageVerification = CanonicalCatalogPackageKernel.Verify(package);
            Assert(packageVerification.IsStructurallyValid,
                "The exact provider assertions produce a structurally valid v6 Candidate package: " +
                string.Join("; ", packageVerification.Issues));

            var storePath = Path.Combine(root, "cloud-store.v5.json");
            var store = new JsonCanonicalKnowledgeCatalogStore(storePath);
            var imported = store.ImportPackageAsync(0, package).GetAwaiter().GetResult();
            var retry = store.ImportPackageAsync(imported.Revision, package).GetAwaiter().GetResult();
            var reloaded = new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync().GetAwaiter().GetResult();
            Assert(imported.Status == CanonicalCatalogImportStatus.Imported &&
                   retry.Status == CanonicalCatalogImportStatus.Unchanged &&
                   reloaded.IsValid &&
                   reloaded.Snapshot.KnowledgeRecords.Select(value => value.Id)
                       .SequenceEqual(origin.KnowledgeRecords.Select(value => value.Id)) &&
                   reloaded.Snapshot.ReferenceEvidenceReceipts.Length == enriched.ReferenceEvidenceReceipts.Length,
                "Store-v5 import, exact retry, and reload retain identities and provider evidence closure. " +
                $"import={imported.Status}; retry={retry.Status}; valid={reloaded.IsValid}; " +
                $"records={reloaded.Snapshot.KnowledgeRecords.Length}/{origin.KnowledgeRecords.Length}; " +
                $"referenceReceipts={reloaded.Snapshot.ReferenceEvidenceReceipts.Length}/{enriched.ReferenceEvidenceReceipts.Length}; " +
                $"importDetail={imported.Detail}; retryDetail={retry.Detail}; " +
                $"issues={string.Join(" | ", reloaded.Issues)}");

            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var packageJson = JsonSerializer.Serialize(package, jsonOptions);
            var exactTitlePath = first.Index.FindExactFmnm("Exact_Case_One").Single().TitleFieldPath!;
            var tamperedPackage = JsonSerializer.Deserialize<CanonicalCatalogPackage>(
                packageJson.Replace(exactTitlePath, exactTitlePath + "/tampered", StringComparison.Ordinal),
                jsonOptions) ?? throw new InvalidDataException("Tampered package did not deserialize.");
            Assert(!CanonicalCatalogPackageKernel.Verify(tamperedPackage).IsStructurallyValid,
                "Changing an exact provider field locator invalidates the package evidence closure.");
            foreach (var familyPath in new[]
                     {
                         firstProviderObject.ActivityFamilyIdentityFieldPath!,
                         firstProviderObject.ActivityFamilyLabelFieldPath!,
                     })
            {
                var familyTamper = JsonSerializer.Deserialize<CanonicalCatalogPackage>(
                    packageJson.Replace(familyPath, familyPath + "/tampered", StringComparison.Ordinal),
                    jsonOptions) ?? throw new InvalidDataException("Family-tampered package did not deserialize.");
                Assert(!CanonicalCatalogPackageKernel.Verify(familyTamper).IsStructurallyValid,
                    "Changing either exact activity-family coordinate invalidates package evidence closure.");
            }

            var alteredDescriptor = Canonical(new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["activityFamilyIdentityPointer"] = "/activityFamily/id",
                ["activityFamilyLabelPointer"] = "/activityFamily/label",
                ["exactFormatVersion"] = "1",
                ["fmnmPointer"] = "/fmnm",
                ["formatId"] = GtaVRockstarCloudSnapshotBundleLoader.SnapshotFormatId,
                ["nativeRevisionPointer"] = "/revision",
                ["objectsPointer"] = "/items",
                ["onlineScopePointer"] = "/online",
                ["providerObjectIdentityPointer"] = "/contentId",
                ["schemaId"] = GtaVRockstarCloudSnapshotBundleLoader.SchemaId,
                ["schemaVersion"] = 1,
                ["titlePointer"] = "/title",
            });
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(valid, alteredDescriptor, expected),
                "A bundle cannot self-authorize changed schema pointers.");
            checks++;

            var wrongSet = ImmutableArray.Create("Exact_Case_One", "Different");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(valid, descriptor, wrongSet),
                "Targeted snapshots fail closed when the expected request set changes.");
            checks++;

            var personalized = Path.Combine(root, "personalized");
            WriteTargetBundle(personalized, expected, visibilityScope: "favorites");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(personalized, descriptor, expected),
                "Personalized provider scope is rejected.");
            checks++;

            var wrongBuild = Path.Combine(root, "wrong-build");
            WriteTargetBundle(wrongBuild, expected, steamBuildId: "99999999");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(wrongBuild, descriptor, expected),
                "A snapshot for another Steam build is rejected.");
            checks++;

            var wrongLocale = Path.Combine(root, "wrong-locale");
            WriteTargetBundle(wrongLocale, expected, locale: "fr-FR");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(wrongLocale, descriptor, expected),
                "A snapshot for an undeclared localization scope is rejected.");
            checks++;

            var failedResponse = Path.Combine(root, "failed-response");
            WriteTargetBundle(failedResponse, expected, responseStatus: 503);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(failedResponse, descriptor, expected),
                "Non-success provider responses are rejected.");
            checks++;

            var secret = Path.Combine(root, "secret");
            WriteTargetBundle(secret, expected, addForbiddenBodyProperty: true);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(secret, descriptor, expected),
                "Credential and account properties are rejected even inside encoded response bodies.");
            checks++;

            var conflict = Path.Combine(root, "conflict");
            WriteTargetBundle(conflict, expected, addConflictingDuplicate: true);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(conflict, descriptor, expected),
                "One provider identity/revision cannot carry contradictory content.");
            checks++;

            var equivalent = Path.Combine(root, "equivalent-duplicate");
            WriteTargetBundle(equivalent, expected, addEquivalentDuplicate: true);
            var equivalentBundle = GtaVRockstarCloudSnapshotBundleLoader.Load(equivalent, descriptor, expected);
            var equivalentResult = cloudAdapter.Extract(
                origin,
                KnowledgeSourceScope.BaseGame(
                    ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                    SourceNativeVersion.FromExactUtf8(
                        "valve.steam.app.3240220.build-id", "25261616",
                        "valve.steam.build-id.exact-utf8", 1)),
                equivalentBundle);
            Assert(equivalentBundle.Index.Objects.Length == 2 &&
                   equivalentBundle.Index.EquivalentDuplicateCount == 1 &&
                   equivalentResult.Metrics.EquivalentDuplicateCount == 1 &&
                   equivalentResult.Batch.TerminologyAssertions.Length == 2,
                "Equivalent provider duplicates are counted but cannot duplicate semantic assertions.");

            var divergentExactTarget = Path.Combine(root, "divergent-exact-target");
            WriteTargetBundle(divergentExactTarget, expected, firstObjectFmnmOverride: "Different_Explicit_Target");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(divergentExactTarget, descriptor, expected),
                "An exact-target response cannot smuggle an explicit bridge for a different target.");
            checks++;

            var unsafeCoordinate = Path.Combine(root, "unsafe-coordinate");
            WriteTargetBundle(unsafeCoordinate, expected,
                firstRequestCoordinate: "https://provider.invalid/jobs?access_token=secret");
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(unsafeCoordinate, descriptor, expected),
                "Raw URLs, query strings, and credential-shaped request coordinates are rejected.");
            checks++;

            var titleConflict = Path.Combine(root, "title-conflict");
            WriteTargetBundle(titleConflict, expected, addDifferentIdentityConflict: true);
            var titleConflictBundle = GtaVRockstarCloudSnapshotBundleLoader.Load(titleConflict, descriptor, expected);
            var titleConflictResult = cloudAdapter.Extract(
                origin,
                KnowledgeSourceScope.BaseGame(
                    ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                    SourceNativeVersion.FromExactUtf8(
                        "valve.steam.app.3240220.build-id", "25261616",
                        "valve.steam.build-id.exact-utf8", 1)),
                titleConflictBundle);
            var firstTarget = origin.KnowledgeRecords.Single(value =>
                value.NativeIdentity.ExactRepresentation == expected[0]).Id;
            Assert(titleConflictResult.Metrics.TitleConflictTargetRecordCount == 1 &&
                   titleConflictResult.Batch.TerminologyAssertions.Count(value =>
                       value.KnowledgeRecordId == firstTarget) == 2,
                "Different provider identities preserve conflicting exact titles without selecting a winner.");

            var extra = Path.Combine(root, "extra");
            WriteTargetBundle(extra, expected);
            File.WriteAllText(Path.Combine(extra, "unexpected.txt"), "unexpected", Encoding.UTF8);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(extra, descriptor, expected),
                "The bundle directory rejects undeclared files.");
            checks++;

            var missing = Path.Combine(root, "missing-target");
            WriteTargetBundle(missing, expected[..1]);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(missing, descriptor, expected),
                "Exact-target snapshots require one terminal response for every expected fmnm.");
            checks++;

            var reorderedTargets = Path.Combine(root, "reordered-targets");
            WriteTargetBundle(reorderedTargets, expected, reverseResponseOrder: true);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(reorderedTargets, descriptor, expected),
                "Re-receipted exact-target responses in a different ordinal order fail closed.");
            checks++;

            var opaqueUnmatched = Path.Combine(root, "opaque-unmatched");
            WriteTargetBundle(opaqueUnmatched, expected, firstObjectWithoutSemanticFields: true);
            var opaqueBundle = GtaVRockstarCloudSnapshotBundleLoader.Load(opaqueUnmatched, descriptor, expected);
            AssertFails(() => cloudAdapter.Extract(
                    origin,
                    KnowledgeSourceScope.BaseGame(
                        ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                        SourceNativeVersion.FromExactUtf8(
                            "valve.steam.app.3240220.build-id", "25261616",
                            "valve.steam.build-id.exact-utf8", 1)),
                    opaqueBundle),
                "An unmatched opaque provider object fails closed when schema v1 cannot preserve a typed unresolved claim.");
            checks++;

            var unsafeMetadata = new (string Name, Action<string> Write)[]
            {
                ("cursor", path => WriteTargetBundle(path, expected, firstCursor: "Bearer secret")),
                ("next-cursor", path => WriteTargetBundle(path, expected, firstNextCursor: "https://provider.invalid/page")),
                ("provider-revision", path => WriteTargetBundle(path, expected, firstProviderRevision: "C:\\Users\\secret.txt")),
                ("etag", path => WriteTargetBundle(path, expected, firstEtag: "cookie=session-secret")),
            };
            foreach (var mutation in unsafeMetadata)
            {
                var path = Path.Combine(root, "unsafe-" + mutation.Name);
                mutation.Write(path);
                AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(path, descriptor, expected),
                    $"Secret- or path-bearing {mutation.Name} metadata fails closed.");
                checks++;
            }

            var global = Path.Combine(root, "global");
            WriteGlobalBundle(global, expected, repeatedCursor: false);
            var globalBundle = GtaVRockstarCloudSnapshotBundleLoader.Load(global, descriptor, expected);
            Assert(globalBundle.ScopeMode == GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry &&
                   globalBundle.Responses.Length == 2 && globalBundle.Responses[^1].Terminal,
                "A stable, terminal global pagination chain is accepted.");

            var loop = Path.Combine(root, "cursor-loop");
            WriteGlobalBundle(loop, expected, repeatedCursor: true);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(loop, descriptor, expected),
                "Repeated pagination cursors fail closed.");
            checks++;

            var tampered = Path.Combine(root, "tampered");
            WriteTargetBundle(tampered, expected);
            var envelopePath = Path.Combine(tampered, GtaVRockstarCloudSnapshotBundleLoader.EnvelopeFileName);
            var bytes = File.ReadAllBytes(envelopePath);
            bytes[^1] ^= 1;
            File.WriteAllBytes(envelopePath, bytes);
            AssertFails(() => GtaVRockstarCloudSnapshotBundleLoader.Load(tampered, descriptor, expected),
                "Envelope byte tampering is rejected before indexing.");
            checks++;

            Console.WriteLine("PASS  GTA V Rockstar cloud snapshot bundle validation and immutable indexing.");
            return checks;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteTargetBundle(
        string directory,
        ImmutableArray<string> requested,
        string visibilityScope = "global-authorized-nonpersonalized",
        bool addForbiddenBodyProperty = false,
        bool addConflictingDuplicate = false,
        bool addEquivalentDuplicate = false,
        bool addDifferentIdentityConflict = false,
        string? firstObjectFmnmOverride = null,
        string? firstRequestCoordinate = null,
        string steamBuildId = "25261616",
        string locale = "en-US",
        int responseStatus = 200,
        bool reverseResponseOrder = false,
        bool firstObjectWithoutSemanticFields = false,
        string? firstCursor = null,
        string? firstNextCursor = null,
        string? firstProviderRevision = null,
        string? firstEtag = null)
    {
        var responses = new List<ResponseFixture>();
        var responseOrder = reverseResponseOrder ? requested.Reverse().ToImmutableArray() : requested;
        for (var index = 0; index < responseOrder.Length; index++)
        {
            var requestedFmnm = responseOrder[index];
            var objects = new List<object?>
            {
                Job(
                    "provider-" + index,
                    "revision-1",
                    index == 0 && firstObjectFmnmOverride is not null
                        ? firstObjectFmnmOverride
                        : requestedFmnm,
                    index == 0 ? "A Rockstar Title" : "Ünicode Rockstar Title",
                    "mission",
                    "Missions",
                    true,
                    addForbiddenBodyProperty && index == 0),
            };
            if (firstObjectWithoutSemanticFields && index == 0)
            {
                objects.Clear();
                objects.Add(OpaqueJob("provider-opaque", "revision-1"));
            }
            if (addConflictingDuplicate && index == 0)
                objects.Add(Job("provider-0", "revision-1", requestedFmnm, "Contradictory Title", "mission", "Missions", true, false));
            if (addEquivalentDuplicate && index == 0)
                objects.Add(Job("provider-0", "revision-1", requestedFmnm, "A Rockstar Title", "mission", "Missions", true, false));
            if (addDifferentIdentityConflict && index == 0)
                objects.Add(Job("provider-conflict", "revision-1", requestedFmnm, "A Different Rockstar Title", "mission", "Missions", true, false));
            responses.Add(new ResponseFixture(
                index,
                index == 0 && firstRequestCoordinate is not null
                    ? firstRequestCoordinate
                    : "job-by-fmnm/" + index,
                requestedFmnm,
                index == 0 ? firstCursor : null,
                index == 0 ? firstNextCursor : null,
                Terminal: true,
                Body(objects),
                responseStatus,
                index == 0 && firstProviderRevision is not null
                    ? firstProviderRevision
                    : "registry-2026-09-26",
                index == 0 ? firstEtag : null));
        }
        WriteBundle(directory, "exact-target-set", visibilityScope, requested, responses, steamBuildId, locale);
    }

    private static void WriteGlobalBundle(string directory, ImmutableArray<string> expected, bool repeatedCursor)
    {
        var responses = new List<ResponseFixture>
        {
            new(0, "jobs/page/0", null, repeatedCursor ? "repeat" : "page-0", "page-1", false,
                Body([Job("provider-0", "revision-1", expected[0], "A Rockstar Title", "mission", "Missions", true, false)])),
            new(1, "jobs/page/1", null, repeatedCursor ? "repeat" : "page-1", null, true,
                Body([Job("provider-1", "revision-1", expected[1], "Ünicode Rockstar Title", "mission", "Missions", true, false)])),
        };
        WriteBundle(directory, "global-registry", "global-public", expected, responses);
    }

    private static void WriteBundle(
        string directory,
        string scopeMode,
        string visibilityScope,
        ImmutableArray<string> expected,
        IReadOnlyList<ResponseFixture> responses,
        string steamBuildId = "25261616",
        string locale = "en-US")
    {
        Directory.CreateDirectory(directory);
        var requestSetDigest = Sha256(Canonical(expected.Order(StringComparer.Ordinal).Cast<object?>().ToArray()));
        var responseValues = responses.Select(ResponseEnvelope).ToArray();
        const string captured = "2026-09-26T20:00:00Z";
        var envelope = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authenticationScope"] = visibilityScope == "global-public" ? "none" : "nonpersonalized-provider-session",
            ["capturedAtUtc"] = captured,
            ["endpointTemplateId"] = "rockstar.games.cloud.jobs.v1",
            ["endpointTemplateVersion"] = "1",
            ["gameId"] = "game.grandtheftautov-enhanced",
            ["locale"] = locale,
            ["platform"] = "pc",
            ["providerRegistryRevision"] = "registry-2026-09-26",
            ["requestSetSha256"] = requestSetDigest,
            ["responses"] = responseValues,
            ["schemaId"] = GtaVRockstarCloudSnapshotBundleLoader.SnapshotSchemaId,
            ["schemaVersion"] = 1,
            ["scopeMode"] = scopeMode,
            ["sourceFamilyId"] = GtaVRockstarCloudSnapshotBundleLoader.SourceFamilyId,
            ["steamAppId"] = "3240220",
            ["steamBuildId"] = steamBuildId,
            ["visibilityScope"] = visibilityScope,
        };
        var envelopeBytes = Canonical(envelope);
        var descriptorBytes = DescriptorBytes();
        var receiptResponses = responses.Select(ResponseReceipt).ToArray();
        var receipt = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authenticationScope"] = envelope["authenticationScope"],
            ["captureTool"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["methodId"] = "grid.rockstar-cloud.snapshot.capture",
                ["methodVersion"] = "1",
                ["toolArtifactSha256"] = Sha256(Encoding.UTF8.GetBytes("fixture-capture-tool")),
                ["toolId"] = "grid.test.rockstar-cloud-capture",
                ["toolVersion"] = "1",
            },
            ["capturedAtUtc"] = captured,
            ["endpointTemplateId"] = envelope["endpointTemplateId"],
            ["endpointTemplateVersion"] = envelope["endpointTemplateVersion"],
            ["envelopeByteLength"] = envelopeBytes.LongLength,
            ["envelopeSha256"] = Sha256(envelopeBytes),
            ["gameId"] = envelope["gameId"],
            ["locale"] = envelope["locale"],
            ["platform"] = envelope["platform"],
            ["providerRegistryRevision"] = envelope["providerRegistryRevision"],
            ["requestSetSha256"] = requestSetDigest,
            ["responseCount"] = responses.Count,
            ["responses"] = receiptResponses,
            ["schemaDescriptorByteLength"] = descriptorBytes.LongLength,
            ["schemaDescriptorSha256"] = Sha256(descriptorBytes),
            ["schemaId"] = GtaVRockstarCloudSnapshotBundleLoader.ReceiptSchemaId,
            ["schemaVersion"] = 1,
            ["scopeMode"] = scopeMode,
            ["sourceFamilyId"] = GtaVRockstarCloudSnapshotBundleLoader.SourceFamilyId,
            ["steamAppId"] = "3240220",
            ["steamBuildId"] = "25261616",
            ["visibilityScope"] = visibilityScope,
        };
        File.WriteAllBytes(Path.Combine(directory, GtaVRockstarCloudSnapshotBundleLoader.EnvelopeFileName), envelopeBytes);
        File.WriteAllBytes(Path.Combine(directory, GtaVRockstarCloudSnapshotBundleLoader.ReceiptFileName), Canonical(receipt));
        File.WriteAllBytes(Path.Combine(directory, GtaVRockstarCloudSnapshotBundleLoader.SchemaFileName), descriptorBytes);
    }

    private static SortedDictionary<string, object?> ResponseEnvelope(ResponseFixture value)
    {
        var raw = value.Body;
        var result = ResponseReceipt(value);
        result["rawBodyBase64"] = Convert.ToBase64String(raw);
        return result;
    }

    private static SortedDictionary<string, object?> ResponseReceipt(ResponseFixture value) =>
        new(StringComparer.Ordinal)
        {
            ["cursor"] = value.Cursor,
            ["etag"] = value.ETag ?? "\"etag-" + value.Ordinal + "\"",
            ["nextCursor"] = value.NextCursor,
            ["ordinal"] = value.Ordinal,
            ["providerRevision"] = value.ProviderRevision,
            ["rawBodyByteLength"] = value.Body.LongLength,
            ["rawBodySha256"] = Sha256(value.Body),
            ["requestCoordinate"] = value.RequestCoordinate,
            ["requestedFmnm"] = value.RequestedFmnm,
            ["statusCode"] = value.StatusCode,
            ["terminal"] = value.Terminal,
        };

    private static byte[] Body(IEnumerable<object?> jobs) => Canonical(
        new SortedDictionary<string, object?>(StringComparer.Ordinal) { ["jobs"] = jobs.ToArray() });

    private static SortedDictionary<string, object?> Job(
        string id,
        string revision,
        string fmnm,
        string title,
        string familyId,
        string familyLabel,
        bool online,
        bool addSecret)
    {
        var value = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["activityFamily"] = new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = familyId,
                ["label"] = familyLabel,
            },
            ["contentId"] = id,
            ["fmnm"] = fmnm,
            ["online"] = online,
            ["revision"] = revision,
            ["title"] = title,
        };
        if (addSecret) value["accessToken"] = "forbidden";
        return value;
    }

    private static SortedDictionary<string, object?> OpaqueJob(string id, string revision) =>
        new(StringComparer.Ordinal)
        {
            ["contentId"] = id,
            ["revision"] = revision,
        };

    private static byte[] DescriptorBytes() => Canonical(
        new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["activityFamilyIdentityPointer"] = "/activityFamily/id",
            ["activityFamilyLabelPointer"] = "/activityFamily/label",
            ["exactFormatVersion"] = "1",
            ["fmnmPointer"] = "/fmnm",
            ["formatId"] = GtaVRockstarCloudSnapshotBundleLoader.SnapshotFormatId,
            ["nativeRevisionPointer"] = "/revision",
            ["objectsPointer"] = "/jobs",
            ["onlineScopePointer"] = "/online",
            ["providerObjectIdentityPointer"] = "/contentId",
            ["schemaId"] = GtaVRockstarCloudSnapshotBundleLoader.SchemaId,
            ["schemaVersion"] = 1,
            ["titlePointer"] = "/title",
        });

    private static CanonicalCatalogPayload CreateMissionOrigin(ImmutableArray<string> fmnmValues)
    {
        var digest = ContentDigest.ComputeSha256("legacy-fmnm-adapter-test"u8);
        var adapter = new GtaVUgcMissionKnowledgeAdapter(digest);
        var artifacts = fmnmValues.Select((value, index) =>
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { mission = new { fmnm = value } }))
                .ToImmutableArray();
            var artifactDigest = ContentDigest.ComputeSha256(bytes.AsSpan());
            return new FrozenSourceArtifact(
                SourceArtifactId.DeriveV1(artifactDigest),
                artifactDigest,
                adapter.CreateSourceCoordinate($"update/update2.rpf!/common/data/ugc/cloud-fixture-{index}.ugc"),
                adapter.Format,
                bytes,
                new DateTimeOffset(2026, 9, 26, 20, 0, 0, TimeSpan.Zero));
        }).ToImmutableArray();
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id", "25261616",
            "valve.steam.build-id.exact-utf8", 1);
        var extraction = adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                gameVersion,
                null,
                null,
                adapter.Descriptor,
                artifacts))
            .GetAwaiter().GetResult();
        return GtaVKnowledgePackageProjection.CreatePayload(extraction);
    }

    private static AcquisitionClosure CreateAcquisitionClosure(
        CanonicalCatalogPayload origin,
        GtaVRockstarCloudSnapshotBundle snapshot)
    {
        var gameVersion = origin.KnowledgeRecords[0].GameVersion;
        var app = SourceNativeIdentifier.FromExactUtf8("valve.steam", "Application", "3240220");
        var method = new AcquisitionMethodCoordinate(
            "grid.gta-v.cloud-job.tests", "1", "fixture", "1",
            ContentDigest.ComputeSha256("cloud-acquisition-fixture"u8));
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        foreach (var artifact in origin.Artifacts)
        {
            var coordinate = SourceNativeIdentifier.FromExactUtf8(
                "grid.gta-v.cloud-job.tests", "FixtureContainer", artifact.Id.Value);
            var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "grid.gta-v.cloud-job.tests", "FixtureMember", artifact.Id.Value);
            var member = new SourceAcquisitionMember(memberCoordinate, 1, artifact.Digest, artifact.Id);
            var containerDigest = ContentDigest.ComputeSha256(Encoding.UTF8.GetBytes("container:" + artifact.Id.Value));
            var id = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, gameVersion, coordinate, 1, containerDigest, method, [member]);
            var receipt = new SourceAcquisitionReceipt(
                id, SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                app, gameVersion, coordinate, 1, containerDigest, method, [member]);
            receipts.Add(receipt);
            bindings.Add(new SourceArtifactAcquisitionBinding(
                artifact.Id, receipt.Id, memberCoordinate, 1, artifact.Digest));
        }
        receipts.Add(snapshot.AcquisitionReceipt);
        bindings.Add(snapshot.AcquisitionBinding);
        return new AcquisitionClosure(receipts.ToImmutable(), bindings.ToImmutable());
    }

    private sealed record AcquisitionClosure(
        ImmutableArray<SourceAcquisitionReceipt> Receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> Bindings);

    private static byte[] Canonical(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        using var document = JsonDocument.Parse(bytes);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidOperationException("Unsupported fixture JSON value.");
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AssertFails(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Check failed: " + message);
    }

    private sealed record ResponseFixture(
        int Ordinal,
        string RequestCoordinate,
        string? RequestedFmnm,
        string? Cursor,
        string? NextCursor,
        bool Terminal,
        byte[] Body,
        int StatusCode = 200,
        string ProviderRevision = "registry-2026-09-26",
        string? ETag = null);
}
