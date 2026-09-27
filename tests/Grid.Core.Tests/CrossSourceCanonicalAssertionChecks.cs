using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CrossSourceCanonicalAssertionChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        var fixture = CreateFixture();
        var verification = CanonicalCatalogPackageKernel.Verify(fixture.Package);
        Assert(verification.IsStructurallyValid, "Schema-v6 cross-source package verifies structurally.");
        Assert(fixture.Package.Manifest.PackageSchemaVersion == CatalogPackageManifest.CrossSourceAssertionSchemaVersion,
            "Cross-source assertions use additive package schema v6.");
        Assert(fixture.Package.Payload.CrossSourceAssertions.Length == 5,
            "Neutral GTA-shaped fixture covers terminology, classification, DLC organization, and Location terminology.");
        Assert(fixture.Package.Payload.UnresolvedCrossSourceAssertions.Length == 1 &&
               fixture.Package.Payload.UnresolvedCrossSourceClaimContents.Single().Terminology?.VerbatimValue ==
                   "Exact unlinked GXT2 wording",
            "An unlinked GXT2-shaped fact remains targetless, typed, and verbatim instead of being dropped or falsely targeted.");
        var packageRoundTrip = JsonSerializer.Deserialize<CanonicalCatalogPackage>(
            JsonSerializer.Serialize(fixture.Package));
        Assert(packageRoundTrip is not null &&
               CanonicalCatalogPackageKernel.Verify(packageRoundTrip).IsStructurallyValid &&
               packageRoundTrip.Payload.UnresolvedCrossSourceClaimContents.Single().Terminology?.VerbatimValue ==
                   "Exact unlinked GXT2 wording",
            "Schema-v6 JSON round-trip preserves and reverifies exact targetless typed content.");
        var unresolvedClaim = fixture.Package.Payload.UnresolvedCrossSourceClaimContents.Single();
        var unresolvedTermBody = unresolvedClaim.Terminology!;
        var changedUnresolvedBody = new UnresolvedCrossSourceTerminologyClaim(
            unresolvedTermBody.Role, "Changed exact wording", unresolvedTermBody.SourceFieldPath,
            unresolvedTermBody.LanguageTag, unresolvedTermBody.NativeStringIdentifier);
        var changedUnresolvedClaimId = UnresolvedCrossSourceClaimContentId.DeriveV1(
            unresolvedClaim.AssertingSourceRevisionId, unresolvedClaim.ExactAssertingSubject,
            unresolvedClaim.AssertionKind, changedUnresolvedBody, null, null, null, null, null, null, null, null);
        Assert(changedUnresolvedClaimId != unresolvedClaim.Id,
            "Changing targetless assertion content changes its exact typed claim identity.");
        Assert(Throws<EncoderFallbackException>(() => UnresolvedCrossSourceClaimContentId.DeriveV1(
                unresolvedClaim.AssertingSourceRevisionId, unresolvedClaim.ExactAssertingSubject,
                unresolvedClaim.AssertionKind,
                new UnresolvedCrossSourceTerminologyClaim(
                    unresolvedTermBody.Role, "\uD800", unresolvedTermBody.SourceFieldPath,
                    unresolvedTermBody.LanguageTag, unresolvedTermBody.NativeStringIdentifier),
                null, null, null, null, null, null, null, null)),
            "Malformed Unicode is rejected by targetless cross-source identity serialization.");
        Assert(fixture.Package.Payload.KnowledgeRecords.Single(value => value.Id == fixture.Item.Id) == fixture.Item,
            "Secondary facts do not rewrite the target record or its immutable identity.");
        Assert(fixture.Package.Payload.LocationCoverageReports.Single().Id ==
               fixture.OriginPackage.Payload.LocationCoverageReports.Single().Id,
            "Secondary Location facts do not rewrite the target origin source-family coverage report.");
        Assert(fixture.Item.SourceRevisionId == fixture.OriginRevisionId &&
               fixture.Term.SourceRevisionId == fixture.AssertingRevisionId,
            "Target origin and secondary assertion provenance remain separate.");

        var sameEnvelope = RecreateEnvelope(fixture.TermEnvelope);
        Assert(sameEnvelope.Id == fixture.TermEnvelope.Id,
            "Identical cross-source inputs reproduce the same envelope identity.");
        var methodMutation = RecreateEnvelope(fixture.TermEnvelope,
            targetLinkMethodVersion: fixture.TermEnvelope.TargetLinkMethodVersion + ".2");
        Assert(methodMutation.Id != fixture.TermEnvelope.Id,
            "Changing the exact link method version changes envelope identity.");
        var changedTerm = new TerminologyAssertion(
            fixture.Item.Id, fixture.AssertingRevisionId, fixture.Term.Role,
            fixture.Term.VerbatimValue + " changed", fixture.Term.SourceFieldPath,
            fixture.Term.LanguageTag, fixture.Term.NativeStringIdentifier);
        Assert(EvidenceClaimContentId.DeriveV1(changedTerm) != fixture.TermEnvelope.UnderlyingClaimContentId,
            "Changing assertion content changes its exact claim identity.");

        var secondRevision = fixture.Package.Payload.SourceRevisions.Single(value =>
            value.Revision.Id == fixture.AssertingRevisionId).Revision with
        {
            Id = CatalogSourceRevisionId.DeriveV2(
                fixture.Package.Payload.SourceRevisions.Single(value => value.Revision.Id == fixture.AssertingRevisionId).Revision.SourceId,
                new SourceNativeVersion("grid.test.cross-source", "revision-2", [2], "grid.test.exact-version", 1),
                fixture.Package.Payload.SourceRevisions.Single(value => value.Revision.Id == fixture.AssertingRevisionId).Revision.ArtifactIds,
                fixture.Package.Payload.SourceRevisions.Single(value => value.Revision.Id == fixture.AssertingRevisionId).AdapterRevisionId),
        };
        var revisionMutationId = CrossSourceCanonicalAssertionId.DeriveV1(
            fixture.TermEnvelope.TargetKnowledgeRecordId,
            fixture.TermEnvelope.TargetOriginSourceRevisionId,
            fixture.TermEnvelope.TargetNativeRecordIdentityId,
            secondRevision.Id,
            fixture.TermEnvelope.AssertionKind,
            fixture.TermEnvelope.UnderlyingClaimContentId,
            fixture.TermEnvelope.TargetLinkClaimId,
            fixture.TermEnvelope.TargetLinkKind,
            fixture.TermEnvelope.ExactLinkKey,
            fixture.TermEnvelope.TargetLinkMethodId,
            fixture.TermEnvelope.TargetLinkMethodVersion,
            fixture.TermEnvelope.SupportingEvidenceReceiptIds,
            fixture.TermEnvelope.SupportingEvidenceBindingIds,
            fixture.TermEnvelope.SupportingInstructionEvidenceBindingIds,
            fixture.TermEnvelope.CorrelationRecordIds);
        Assert(revisionMutationId != fixture.TermEnvelope.Id,
            "Changing the asserting revision changes envelope identity.");

        var reordered = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            terminologyAssertions: fixture.Package.Payload.TerminologyAssertions.Reverse().ToImmutableArray(),
            fileEvidenceReceipts: fixture.Package.Payload.FileEvidenceReceipts.Reverse().ToImmutableArray(),
            evidenceBindings: fixture.Package.Payload.EvidenceBindings.Reverse().ToImmutableArray(),
            crossSourceAssertions: fixture.Package.Payload.CrossSourceAssertions.Reverse().ToImmutableArray());
        var reorderedPackage = CanonicalCatalogPackageKernel.CreateV6(
            fixture.Package.Manifest.PackageKind,
            fixture.Package.Manifest.GameScope,
            fixture.Package.Manifest.ModScope,
            fixture.Package.Manifest.RequiredBasePackageIds,
            fixture.Package.Manifest.CompositionPolicyVersion,
            reordered,
            fixture.Package.ValidationSummary,
            fixture.Package.Manifest.BuildProvenance);
        Assert(reorderedPackage.Id == fixture.Package.Id &&
               reorderedPackage.Manifest.PayloadDigest == fixture.Package.Manifest.PayloadDigest,
            "Schema-v6 package identity is independent of input enumeration order.");

        var changedBuildInput = new CatalogBuildProvenance(
            CatalogBuildProvenance.CurrentSchemaVersion,
            fixture.Package.Manifest.BuildProvenance.BuildSystemId,
            fixture.Package.Manifest.BuildProvenance.ExactBuildSystemVersion,
            fixture.Package.Manifest.BuildProvenance.SourceCommit,
            fixture.Package.Manifest.BuildProvenance.CommittedBuildInputs
                .Select((value, index) => index == 0
                    ? new CatalogCommittedBuildInput(value.RepositoryRelativePath, new string('c', 40))
                    : value)
                .ToImmutableArray());
        var changedBuildPackage = CanonicalCatalogPackageKernel.CreateV6(
            fixture.Package.Manifest.PackageKind,
            fixture.Package.Manifest.GameScope,
            fixture.Package.Manifest.ModScope,
            fixture.Package.Manifest.RequiredBasePackageIds,
            fixture.Package.Manifest.CompositionPolicyVersion,
            fixture.Package.Payload,
            fixture.Package.ValidationSummary,
            changedBuildInput);
        Assert(changedBuildPackage.Id != fixture.Package.Id,
            "Schema-v6 package identity commits the exact reviewed build-input closure.");

        var developmentInputs = ImmutableArray.Create(
            new CatalogDevelopmentBuildInput(
                "src/Grid.Core/Models/CanonicalKnowledgePackageContracts.cs",
                ContentDigest.ComputeSha256("development-contract-bytes"u8),
                CatalogDevelopmentBuildInputState.TrackedWorktreeModified),
            new CatalogDevelopmentBuildInput(
                "src/Grid.Core/Models/CanonicalCrossSourceAssertionModels.cs",
                ContentDigest.ComputeSha256("development-cross-source-bytes"u8),
                CatalogDevelopmentBuildInputState.Untracked));
        var developmentProvenance = CatalogBuildProvenance.CreateDevelopment(
            fixture.Package.Manifest.BuildProvenance.BuildSystemId,
            fixture.Package.Manifest.BuildProvenance.ExactBuildSystemVersion,
            fixture.Package.Manifest.BuildProvenance.SourceCommit,
            developmentInputs);
        var developmentPackage = CanonicalCatalogPackageKernel.CreateV6(
            fixture.Package.Manifest.PackageKind,
            fixture.Package.Manifest.GameScope,
            fixture.Package.Manifest.ModScope,
            fixture.Package.Manifest.RequiredBasePackageIds,
            fixture.Package.Manifest.CompositionPolicyVersion,
            fixture.Package.Payload,
            fixture.Package.ValidationSummary,
            developmentProvenance);
        var repeatedDevelopmentPackage = CanonicalCatalogPackageKernel.CreateV6(
            fixture.Package.Manifest.PackageKind,
            fixture.Package.Manifest.GameScope,
            fixture.Package.Manifest.ModScope,
            fixture.Package.Manifest.RequiredBasePackageIds,
            fixture.Package.Manifest.CompositionPolicyVersion,
            fixture.Package.Payload,
            fixture.Package.ValidationSummary,
            CatalogBuildProvenance.CreateDevelopment(
                developmentProvenance.BuildSystemId,
                developmentProvenance.ExactBuildSystemVersion,
                developmentProvenance.SourceCommit,
                developmentInputs.Reverse().ToImmutableArray()));
        Assert(CanonicalCatalogPackageKernel.Verify(developmentPackage).IsStructurallyValid &&
               developmentPackage.Manifest.ValidationStatus == CatalogValidationStatus.Candidate &&
               developmentPackage.Manifest.BuildProvenance.IsDevelopment &&
               repeatedDevelopmentPackage.Id == developmentPackage.Id,
            "A truthful dirty-worktree schema-v6 Candidate is structurally verifiable and deterministic across input order.");
        var developmentRoundTrip = JsonSerializer.Deserialize<CanonicalCatalogPackage>(
            JsonSerializer.Serialize(developmentPackage));
        Assert(developmentRoundTrip is not null && developmentRoundTrip.Id == developmentPackage.Id &&
               CanonicalCatalogPackageKernel.Verify(developmentRoundTrip).IsStructurallyValid &&
               developmentRoundTrip.Manifest.BuildProvenance.DevelopmentBuildInputs.SequenceEqual(
                   developmentProvenance.DevelopmentBuildInputs),
            "Development provenance round-trips without upgrading or pretending its inputs are committed.");

        var changedDevelopmentProvenance = CatalogBuildProvenance.CreateDevelopment(
            developmentProvenance.BuildSystemId,
            developmentProvenance.ExactBuildSystemVersion,
            developmentProvenance.SourceCommit,
            developmentInputs.SetItem(0, new CatalogDevelopmentBuildInput(
                developmentInputs[0].RepositoryRelativePath,
                ContentDigest.ComputeSha256("changed-development-contract-bytes"u8),
                CatalogDevelopmentBuildInputState.TrackedIndexAndWorktreeModified)));
        var changedDevelopmentPackage = CanonicalCatalogPackageKernel.CreateV6(
            fixture.Package.Manifest.PackageKind,
            fixture.Package.Manifest.GameScope,
            fixture.Package.Manifest.ModScope,
            fixture.Package.Manifest.RequiredBasePackageIds,
            fixture.Package.Manifest.CompositionPolicyVersion,
            fixture.Package.Payload,
            fixture.Package.ValidationSummary,
            changedDevelopmentProvenance);
        Assert(changedDevelopmentPackage.Id != developmentPackage.Id,
            "Changing exact development bytes or dirty state changes the schema-v6 package identity.");

        var passedValidation = new CatalogValidationSummary(
            CatalogValidationStatus.Passed,
            fixture.Package.ValidationSummary.PolicyId,
            fixture.Package.ValidationSummary.ExactPolicyVersion,
            fixture.Package.ValidationSummary.ResultDigest);
        Assert(Throws<ArgumentException>(() => CanonicalCatalogPackageKernel.CreateV6(
                fixture.Package.Manifest.PackageKind,
                fixture.Package.Manifest.GameScope,
                fixture.Package.Manifest.ModScope,
                fixture.Package.Manifest.RequiredBasePackageIds,
                fixture.Package.Manifest.CompositionPolicyVersion,
                fixture.Package.Payload,
                passedValidation,
                developmentProvenance)),
            "Development provenance cannot be labeled Passed or become publication-approved.");
        Assert(Throws<ArgumentException>(() => CatalogBuildProvenance.CreateDevelopment(
                developmentProvenance.BuildSystemId,
                developmentProvenance.ExactBuildSystemVersion,
                "HEAD",
                developmentInputs)),
            "Development provenance rejects a symbolic or caller-invented commit in place of exact HEAD.");

        var developmentStorePath = Path.Combine(
            Path.GetTempPath(), "grid-development-provenance-store-" + Guid.NewGuid().ToString("N"), "catalog.json");
        try
        {
            var developmentStore = new JsonCanonicalKnowledgeCatalogStore(developmentStorePath);
            var developmentImport = await developmentStore.ImportPackageAsync(0, developmentPackage);
            var developmentRetry = await developmentStore.ImportPackageAsync(
                developmentImport.Revision, developmentPackage);
            var developmentReload = await developmentStore.LoadAsync();
            Assert(developmentImport.Status == CanonicalCatalogImportStatus.Imported &&
                   developmentRetry.Status == CanonicalCatalogImportStatus.Unchanged &&
                   developmentReload.IsValid &&
                   developmentReload.Snapshot.FindImportedPackage(developmentPackage.Id)?.Manifest
                       .BuildProvenance.IsDevelopment == true,
                "A structurally valid development Candidate imports atomically, retries idempotently, and remains explicitly developmental after reload.");
        }
        finally
        {
            var developmentStoreDirectory = Path.GetDirectoryName(developmentStorePath)!;
            if (Directory.Exists(developmentStoreDirectory)) Directory.Delete(developmentStoreDirectory, true);
        }

        var missingTargetPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            knowledgeRecords: fixture.Package.Payload.KnowledgeRecords.Where(value => value.Id != fixture.Item.Id).ToImmutableArray());
        Assert(!VerifyTampered(fixture.Package, missingTargetPayload), "An absent cross-source target fails closed.");
        var missingRevisionPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            sourceRevisions: fixture.Package.Payload.SourceRevisions.Where(value => value.Revision.Id != fixture.AssertingRevisionId).ToImmutableArray());
        Assert(!VerifyTampered(fixture.Package, missingRevisionPayload), "An absent asserting revision fails closed.");

        var borrowed = RecreateEnvelope(
            fixture.TermEnvelope,
            receipts: [fixture.OriginIdentityBinding.EvidenceReceiptId],
            bindings: [fixture.OriginIdentityBinding.Id]);
        var borrowedPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            crossSourceAssertions: fixture.Package.Payload.CrossSourceAssertions
                .Where(value => value.Id != fixture.TermEnvelope.Id).Append(borrowed).ToImmutableArray());
        Assert(!VerifyTampered(fixture.Package, borrowedPayload),
            "Target-origin identity evidence cannot be borrowed as secondary claim evidence.");

        var nativeEnvelope = fixture.Package.Payload.CrossSourceAssertions.Single(value =>
            value.AssertionKind == CrossSourceCanonicalAssertionKind.SemanticClassification);
        var wrongNativeLink = RecreateEnvelope(nativeEnvelope,
            exactLinkKey: SourceNativeIdentifier.FromExactUtf8("grid.test.cross-source", "record", "WRONG_TARGET"));
        var wrongLinkPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            crossSourceAssertions: fixture.Package.Payload.CrossSourceAssertions
                .Where(value => value.Id != nativeEnvelope.Id).Append(wrongNativeLink).ToImmutableArray());
        Assert(!VerifyTampered(fixture.Package, wrongLinkPayload), "A wrong exact native linkage fails closed.");
        var wrongLabelLink = RecreateEnvelope(fixture.TermEnvelope,
            exactLinkKey: SourceNativeIdentifier.FromExactUtf8("grid.test.gxt2", "label-key", "WRONG_LABEL"));
        var wrongLabelPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            crossSourceAssertions: fixture.Package.Payload.CrossSourceAssertions
                .Where(value => value.Id != fixture.TermEnvelope.Id).Append(wrongLabelLink).ToImmutableArray());
        Assert(!VerifyTampered(fixture.Package, wrongLabelPayload), "A wrong exact label/hash linkage fails closed.");
        Assert(!VerifyTampered(fixture.Package, CreateWrongExistingTargetPayload(fixture)),
            "A fully rederived envelope for an existing but unrelated target fails its target-side evidence coordinate.");
        Assert(!Enum.GetNames<CrossSourceTargetLinkKind>().Any(value =>
                value.Contains("fuzzy", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("model", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("similar", StringComparison.OrdinalIgnoreCase)),
            "The linkage contract exposes no fuzzy, name-similarity, or model link kind.");

        var unresolved = fixture.UnresolvedAssertion;
        var borrowedCorrelationRecord = new CorrelationRecord(
            [fixture.Item.Id, fixture.Actor.Id], unresolved.AttemptedLinkMethodId,
            unresolved.AttemptedLinkMethodVersion, CorrelationOutcome.Unresolved);
        var borrowedCorrelationId = CorrelationRecordId.DeriveV1(borrowedCorrelationRecord, []);
        var borrowedCorrelation = new CanonicalCorrelationEnvelope(
            borrowedCorrelationId, borrowedCorrelationRecord, []);
        var borrowedUnresolvedId = UnresolvedCrossSourceAssertionId.DeriveV1(
            unresolved.ClaimContentId, unresolved.AssertingSourceRevisionId,
            unresolved.AdapterRevisionId, CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation,
            unresolved.AttemptedLinkMethodId, unresolved.AttemptedLinkMethodVersion,
            unresolved.Outcome, unresolved.ReasonCode, [fixture.Item.Id, fixture.Actor.Id],
            unresolved.SupportingEvidenceReceiptIds, unresolved.SupportingEvidenceBindingIds,
            [borrowedCorrelation.Id]);
        var borrowedUnresolved = new UnresolvedCrossSourceAssertion(
            borrowedUnresolvedId, unresolved.ClaimContentId, unresolved.AssertingSourceRevisionId,
            unresolved.AdapterRevisionId, CrossSourceTargetLinkKind.VerifiedDeterministicCorrelation,
            unresolved.AttemptedLinkMethodId, unresolved.AttemptedLinkMethodVersion,
            unresolved.Outcome, unresolved.ReasonCode, [fixture.Item.Id, fixture.Actor.Id],
            unresolved.SupportingEvidenceReceiptIds, unresolved.SupportingEvidenceBindingIds,
            [borrowedCorrelation.Id]);
        var borrowedCorrelationPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            fixture.Package.Payload,
            correlationEnvelopes: fixture.Package.Payload.CorrelationEnvelopes.Add(borrowedCorrelation),
            unresolvedCrossSourceAssertions: [borrowedUnresolved]);
        Assert(!VerifyRebuilt(fixture.Package, borrowedCorrelationPayload),
            "An unrelated evidence-free correlation cannot be borrowed as an unresolved target-link attempt.");

        var composition = new CatalogCompositionId("composition.cross-source.test");
        var applicability = new CanonicalApplicabilityProjection(
            composition,
            fixture.Package.Payload.KnowledgeRecords.Select(value => value.Id).ToImmutableArray(),
            [],
            "1");
        var input = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
            fixture.Package, composition, CanonicalSelectorProjectionPolicy.V1, applicability);
        var itemTerms = input.TerminologyAssertions.Where(value => value.KnowledgeRecordId == fixture.Item.Id).ToImmutableArray();
        Assert(itemTerms.Any(value => value.VerbatimValue == fixture.Term.VerbatimValue &&
                                      value.SourceRevisionId == fixture.AssertingRevisionId),
            "Verified projection exposes exact secondary terminology with its asserting provenance.");
        Assert(input.TerminologyAssertions.All(value => value.VerbatimValue != "Exact unlinked GXT2 wording"),
            "Targetless unresolved terminology cannot enter selector projection as a fact about a canonical record.");

        var conflicting = fixture.Package.Payload.TerminologyAssertions.Where(value =>
            value.KnowledgeRecordId == fixture.Item.Id && value.Role == TerminologyAssertionRole.PrimaryName).ToImmutableArray();
        Assert(conflicting.Length == 2 && conflicting.Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).Count() == 2,
            "Conflicting secondary terminology survives independently without a synthesized winner.");

        var temp = Path.Combine(Path.GetTempPath(), "grid-cross-source-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new JsonCanonicalKnowledgeCatalogStore(Path.Combine(temp, "catalog.json"));
            var originImport = await store.ImportPackageAsync(0, fixture.OriginPackage);
            var import = await store.ImportPackageAsync(originImport.Revision, fixture.Package);
            Assert(originImport.Status == CanonicalCatalogImportStatus.Imported &&
                   import.Status == CanonicalCatalogImportStatus.Imported && import.ImportedSourceRevisionCount == 1,
                $"The append-only store appends only the later asserting revision without reopening target origin content " +
                $"(origin={originImport.Status}:{originImport.Detail}; secondary={import.Status}:{import.Detail}; " +
                $"count={import.ImportedSourceRevisionCount}).");
            var retry = await store.ImportPackageAsync(import.Revision, fixture.Package);
            Assert(retry.Status == CanonicalCatalogImportStatus.Unchanged,
                "Duplicate cross-source package registration is idempotent.");
            var reloaded = await store.LoadAsync();
            var target = reloaded.Snapshot.FindKnowledgeRecord(fixture.Item.Id);
            var persistedEnvelope = reloaded.Snapshot.FindCrossSourceAssertion(fixture.TermEnvelope.Id);
            var persistedUnresolved = reloaded.Snapshot.FindUnresolvedCrossSourceAssertion(fixture.UnresolvedAssertion.Id);
            Assert(reloaded.IsValid && target?.SourceRevisionId == fixture.OriginRevisionId &&
                   persistedEnvelope?.AssertingSourceRevisionId == fixture.AssertingRevisionId &&
                   persistedUnresolved?.AssertingSourceRevisionId == fixture.AssertingRevisionId,
                "Store reload preserves origin provenance separately from secondary assertion provenance.");
            Assert(reloaded.Snapshot.FindSourceRevision(fixture.OriginRevisionId) is not null &&
                   reloaded.Snapshot.FindSourceRevision(fixture.AssertingRevisionId) is not null &&
                   reloaded.Snapshot.FindImportedPackage(fixture.OriginPackage.Id) is not null &&
                   reloaded.Snapshot.FindImportedPackage(fixture.Package.Id) is not null &&
                   fixture.TermEnvelope.SupportingEvidenceReceiptIds.All(id =>
                       reloaded.Snapshot.FindEvidenceReceipt(id) is not null) &&
                   fixture.TermEnvelope.SupportingEvidenceBindingIds.All(id =>
                       reloaded.Snapshot.FindEvidenceBinding(id) is not null),
                "Historical source revisions, packages, and exact provenance remain addressable after reload.");

            var persistedProjectionInput = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                reloaded, fixture.Package.Id, composition, CanonicalSelectorProjectionPolicy.V1, applicability);
            Assert(persistedProjectionInput.TerminologyAssertions.Any(value =>
                    value.KnowledgeRecordId == fixture.Item.Id &&
                    value.VerbatimValue == fixture.Term.VerbatimValue &&
                    value.SourceRevisionId == fixture.AssertingRevisionId),
                "A validated shared-catalog reload exposes an exact package-pinned projection input without extraction.");
            Assert(Throws<InvalidDataException>(() => CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                    new CanonicalCatalogLoadResult(reloaded.Snapshot, ["tampered snapshot"]),
                    fixture.Package.Id, composition, CanonicalSelectorProjectionPolicy.V1, applicability)),
                "An invalid shared-catalog load cannot become a runtime projection input.");
            Assert(Throws<InvalidDataException>(() => CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                    reloaded,
                    new CatalogPackageId("grid.catalog-package.v6.sha256." + new string('0', 64)),
                    composition, CanonicalSelectorProjectionPolicy.V1, applicability)),
                "A package absent from the validated shared catalog cannot become a runtime projection input.");

            var stateBeforeRejectedImport = await File.ReadAllBytesAsync(Path.Combine(temp, "catalog.json"));
            var tamperedPackage = fixture.Package with
            {
                Payload = CanonicalCatalogPackageImportChecks.CopyPayload(
                    fixture.Package.Payload,
                    knowledgeRecords: fixture.Package.Payload.KnowledgeRecords
                        .Where(value => value.Id != fixture.Item.Id).ToImmutableArray()),
            };
            var rejectedImport = await store.ImportPackageAsync(import.Revision, tamperedPackage);
            var afterRejectedImport = await store.LoadAsync();
            Assert(rejectedImport.Status == CanonicalCatalogImportStatus.Invalid &&
                   afterRejectedImport.IsValid && afterRejectedImport.Snapshot.Revision == import.Revision &&
                   stateBeforeRejectedImport.SequenceEqual(
                       await File.ReadAllBytesAsync(Path.Combine(temp, "catalog.json"))),
                "A tampered schema-v6 package is rejected atomically without changing persisted canonical state.");

            var json = await File.ReadAllTextAsync(Path.Combine(temp, "catalog.json"));
            Assert(json.Contains("\"schemaVersion\": 5", StringComparison.Ordinal),
                "Cross-source persistence uses additive store schema v5.");

            var downgradePath = Path.Combine(temp, "catalog-invalid-v4.json");
            var downgradeJson = JsonNode.Parse(json)!.AsObject();
            downgradeJson["schemaVersion"] = 4;
            await File.WriteAllTextAsync(downgradePath, downgradeJson.ToJsonString());
            var downgradeLoad = await new JsonCanonicalKnowledgeCatalogStore(downgradePath).LoadAsync();
            Assert(!downgradeLoad.IsValid,
                "A legacy schema cannot silently discard nonempty cross-source state.");

            var incompletePath = Path.Combine(temp, "catalog-incomplete-v5.json");
            var incompleteJson = JsonNode.Parse(json)!.AsObject();
            incompleteJson.Remove("unresolvedCrossSourceEvidenceBindings");
            await File.WriteAllTextAsync(incompletePath, incompleteJson.ToJsonString());
            var incompleteLoad = await new JsonCanonicalKnowledgeCatalogStore(incompletePath).LoadAsync();
            Assert(!incompleteLoad.IsValid,
                "Schema-v5 persistence fails closed when an unresolved cross-source collection is absent.");

            var legacyPath = Path.Combine(temp, "catalog-v4.json");
            var legacyStore = new JsonCanonicalKnowledgeCatalogStore(legacyPath);
            var legacyImport = await legacyStore.ImportPackageAsync(0, fixture.OriginPackage);
            var legacyJson = JsonNode.Parse(await File.ReadAllTextAsync(legacyPath))!.AsObject();
            legacyJson["schemaVersion"] = 4;
            legacyJson.Remove("crossSourceAssertions");
            legacyJson.Remove("crossSourceTargetLinkClaims");
            legacyJson.Remove("unresolvedCrossSourceClaimContents");
            legacyJson.Remove("unresolvedCrossSourceEvidenceBindings");
            legacyJson.Remove("unresolvedCrossSourceAssertions");
            await File.WriteAllTextAsync(legacyPath, legacyJson.ToJsonString());
            var legacyReload = await new JsonCanonicalKnowledgeCatalogStore(legacyPath).LoadAsync();
            Assert(legacyImport.Status == CanonicalCatalogImportStatus.Imported &&
                   legacyReload.IsValid && legacyReload.Snapshot.CrossSourceAssertions.IsEmpty,
                "A genuine schema-v4 store remains readable without reinterpretation.");
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }

        return checks;

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        static bool Throws<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return true; }
            return false;
        }
    }

    private static Fixture CreateFixture()
    {
        var seed = CanonicalCatalogPackageImportChecks.CreateProjectionPackage("cross-source-origin", 0x61);
        var payload = seed.Payload;
        var item = payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Item);
        var actor = payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Actor);
        var location = payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Location);
        var originBinding = payload.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == item.Id && value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity);
        var descriptor = payload.AdapterDescriptors.Single();
        var format = descriptor.SupportedFormats.Single();
        var originArtifact = payload.Artifacts.Single();
        var artifactDigest = ContentDigest.ComputeSha256("neutral-gxt2-and-metadata"u8);
        var artifact = new SourceArtifactRecord(SourceArtifactId.DeriveV1(artifactDigest), artifactDigest);
        var sourceNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.cross-source", "authoritative-dataset", "neutral-secondary-source");
        var source = new CatalogSourceRecord(
            CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, sourceNative),
            KnowledgeSourceKind.LocalGameDistribution,
            sourceNative);
        var nativeRevision = new SourceNativeVersion(
            "grid.test.cross-source", "revision-1", [1], "grid.test.exact-version", 1);
        var revisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id, nativeRevision, [originArtifact.Id, artifact.Id], descriptor.RevisionId);
        var revision = new CatalogSourceRevisionRecord(
            revisionId, source.Id, nativeRevision, [originArtifact.Id, artifact.Id]);
        var boundRevision = new AdapterBoundCatalogSourceRevisionRecord(
            revision,
            descriptor.RevisionId,
            payload.SourceRevisions.Single().SourceScope,
            [
                new SourceArtifactFormatBinding(
                    originArtifact.Id,
                    new KnowledgeFormatCoordinate(format.FormatId, format.ExactFormatVersion)),
                new SourceArtifactFormatBinding(
                    artifact.Id,
                    new KnowledgeFormatCoordinate(format.FormatId, format.ExactFormatVersion)),
            ]);

        var linkKey = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.gxt2", "label-key", "WT_NEUTRAL_ITEM");
        var term = new TerminologyAssertion(
            item.Id, revisionId, TerminologyAssertionRole.PrimaryName,
            "  Exact secondary name — unchanged  ", "entries[WT_NEUTRAL_ITEM].value", "en", linkKey);
        var conflictingTerm = new TerminologyAssertion(
            item.Id, revisionId, TerminologyAssertionRole.PrimaryName,
            "Independent exact source wording", "entries[WT_NEUTRAL_ITEM].alternate", "en", linkKey);
        var locationTerm = new TerminologyAssertion(
            location.Id, revisionId, TerminologyAssertionRole.PrimaryName,
            "Exact secondary location", "locations[LOC_NEUTRAL].value", "en", location.NativeIdentity);
        var itemClassification = Classification(item, revisionId, CanonicalProjectionSemantics.ItemWeapons, "records[item].class");
        var dlcValue = SourceNativeIdentifier.FromExactUtf8("grid.test.dlc", "membership", "DLC_NEUTRAL");
        var actorDlcId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            actor.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
            dlcValue, "DLC_NEUTRAL", "grid.test.exact-dlc-membership", "1", "records[actor].dlc");
        var actorDlc = new CanonicalOrganizationalValueAssertion(
            actorDlcId, actor.Id, revisionId, CanonicalProjectionSemantics.ActorDlcDimensionNode,
            dlcValue, "DLC_NEUTRAL", "grid.test.exact-dlc-membership", "1", "records[actor].dlc");

        var receipts = payload.FileEvidenceReceipts.ToBuilder();
        var bindings = payload.EvidenceBindings.ToBuilder();
        var targetLinkClaims = ImmutableArray.CreateBuilder<CrossSourceTargetLinkClaim>();
        (CatalogFileEvidenceReceipt Receipt, EvidenceBinding Binding) AddClaim(
            CanonicalKnowledgeRecord target, string fieldPath, EvidenceClaimKind kind,
            EvidenceClaimContentId claimId, SourceNativeIdentifier nativeObject)
        {
            var value = new FileEvidenceReceipt(
                revisionId, artifact.Id, artifact.Digest,
                "grid.test.exact-secondary-parser", "1", nativeObject.ExactRepresentation, fieldPath,
                null, null, null, new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero), nativeObject);
            var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
            var binding = new EvidenceBinding(
                EvidenceBindingId.DeriveV2(receipt.Id, kind, target.Id, revisionId, fieldPath, claimId),
                receipt.Id, kind, target.Id, revisionId, fieldPath, claimId);
            receipts.Add(receipt);
            bindings.Add(binding);
            return (receipt, binding);
        }

        CatalogFileEvidenceReceipt AddTargetCoordinateReceipt(
            CanonicalKnowledgeRecord target,
            string fieldPath)
        {
            var value = new FileEvidenceReceipt(
                revisionId, originArtifact.Id, originArtifact.Digest,
                "grid.test.exact-secondary-parser", "1", target.NativeIdentity.ExactRepresentation, fieldPath,
                null, null, null, new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero), target.NativeIdentity);
            var receipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV2(value), value);
            if (receipts.All(existing => existing.Id != receipt.Id)) receipts.Add(receipt);
            return receipt;
        }

        var termEvidence = AddClaim(item, term.SourceFieldPath, EvidenceClaimKind.Terminology,
            EvidenceClaimContentId.DeriveV1(term), linkKey);
        var conflictEvidence = AddClaim(item, conflictingTerm.SourceFieldPath, EvidenceClaimKind.Terminology,
            EvidenceClaimContentId.DeriveV1(conflictingTerm), linkKey);
        var locationEvidence = AddClaim(location, locationTerm.SourceFieldPath, EvidenceClaimKind.Terminology,
            EvidenceClaimContentId.DeriveV1(locationTerm), location.NativeIdentity);
        var classificationEvidence = AddClaim(item, itemClassification.SourceFieldPath,
            EvidenceClaimKind.SemanticClassification, EvidenceClaimContentId.DeriveV1(itemClassification), item.NativeIdentity);
        var dlcEvidence = AddClaim(actor, actorDlc.SourceFieldPath,
            EvidenceClaimKind.OrganizationalValue, EvidenceClaimContentId.DeriveV1(actorDlc), actor.NativeIdentity);

        EvidenceBinding OriginFor(CanonicalKnowledgeRecord record) => payload.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == record.Id && value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity);
        CrossSourceCanonicalAssertion Envelope(
            CanonicalKnowledgeRecord target,
            CrossSourceCanonicalAssertionKind kind,
            EvidenceClaimContentId claim,
            CrossSourceTargetLinkKind linkKind,
            SourceNativeIdentifier exactLink,
            (CatalogFileEvidenceReceipt Receipt, EvidenceBinding Binding) claimEvidence,
            string method)
        {
            var origin = OriginFor(target);
            var exactTargetCoordinate = linkKind is CrossSourceTargetLinkKind.ExactLabelOrHashKey
                ? exactLink
                : target.NativeIdentity;
            var targetFieldPath = $"mapping[{target.Id.Value}].target-coordinate.{targetLinkClaims.Count}";
            var targetReceipt = AddTargetCoordinateReceipt(target, targetFieldPath);
            var linkClaimId = CrossSourceTargetLinkClaimId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                exactTargetCoordinate, originArtifact.Id, targetFieldPath,
                revisionId, exactLink, artifact.Id, claimEvidence.Receipt.Receipt.SourceFieldPath,
                linkKind, method, "1");
            var linkClaim = new CrossSourceTargetLinkClaim(
                linkClaimId, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId,
                exactTargetCoordinate, originArtifact.Id, targetFieldPath,
                revisionId, exactLink, artifact.Id, claimEvidence.Receipt.Receipt.SourceFieldPath,
                linkKind, method, "1");
            targetLinkClaims.Add(linkClaim);
            var linkClaimContentId = EvidenceClaimContentId.DeriveV1(linkClaim);
            var targetLinkBinding = new EvidenceBinding(
                EvidenceBindingId.DeriveV2(
                    targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                    target.Id, revisionId, targetFieldPath, linkClaimContentId),
                targetReceipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                target.Id, revisionId, targetFieldPath, linkClaimContentId);
            var assertingLinkBinding = new EvidenceBinding(
                EvidenceBindingId.DeriveV2(
                    claimEvidence.Receipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                    target.Id, revisionId, linkClaim.AssertingCoordinateFieldPath, linkClaimContentId),
                claimEvidence.Receipt.Id, EvidenceClaimKind.CrossSourceTargetLink,
                target.Id, revisionId, linkClaim.AssertingCoordinateFieldPath, linkClaimContentId);
            bindings.Add(targetLinkBinding);
            bindings.Add(assertingLinkBinding);
            var receiptIds = ImmutableArray.Create(
                origin.EvidenceReceiptId, targetReceipt.Id, claimEvidence.Receipt.Id);
            var bindingIds = ImmutableArray.Create(
                origin.Id, claimEvidence.Binding.Id, targetLinkBinding.Id, assertingLinkBinding.Id);
            var id = CrossSourceCanonicalAssertionId.DeriveV1(
                target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revisionId,
                kind, claim, linkClaim.Id, linkKind, exactLink, method, "1", receiptIds, bindingIds, [], []);
            return new(id, target.Id, target.SourceRevisionId, target.NativeRecordIdentityId, revisionId,
                kind, claim, linkClaim.Id, linkKind, exactLink, method, "1", receiptIds, bindingIds, [], []);
        }

        var termEnvelope = Envelope(item, CrossSourceCanonicalAssertionKind.Terminology,
            EvidenceClaimContentId.DeriveV1(term), CrossSourceTargetLinkKind.ExactLabelOrHashKey,
            linkKey, termEvidence, "grid.test.exact-label-key-link");
        var envelopes = ImmutableArray.Create(
            termEnvelope,
            Envelope(item, CrossSourceCanonicalAssertionKind.Terminology,
                EvidenceClaimContentId.DeriveV1(conflictingTerm), CrossSourceTargetLinkKind.ExactLabelOrHashKey,
                linkKey, conflictEvidence, "grid.test.exact-label-key-link"),
            Envelope(item, CrossSourceCanonicalAssertionKind.SemanticClassification,
                EvidenceClaimContentId.DeriveV1(itemClassification), CrossSourceTargetLinkKind.ExactSourceNativeIdentity,
                item.NativeIdentity, classificationEvidence, "grid.test.exact-native-id-link"),
            Envelope(actor, CrossSourceCanonicalAssertionKind.OrganizationalValue,
                EvidenceClaimContentId.DeriveV1(actorDlc), CrossSourceTargetLinkKind.ExactSourceNativeIdentity,
                actor.NativeIdentity, dlcEvidence, "grid.test.exact-native-id-link"),
            Envelope(location, CrossSourceCanonicalAssertionKind.Terminology,
                EvidenceClaimContentId.DeriveV1(locationTerm), CrossSourceTargetLinkKind.ExactSourceNativeIdentity,
                location.NativeIdentity, locationEvidence, "grid.test.exact-native-id-link"));

        var unresolvedSubject = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.gxt2", "label-key", "WT_UNLINKED");
        var unresolvedTerm = new UnresolvedCrossSourceTerminologyClaim(
            TerminologyAssertionRole.PrimaryName, "Exact unlinked GXT2 wording",
            "entries[WT_UNLINKED].value", "en", unresolvedSubject);
        var unresolvedClaimId = UnresolvedCrossSourceClaimContentId.DeriveV1(
            revisionId, unresolvedSubject, CrossSourceCanonicalAssertionKind.Terminology,
            unresolvedTerm, null, null, null, null, null, null, null, null);
        var unresolvedClaim = new UnresolvedCrossSourceClaimContent(
            unresolvedClaimId, revisionId, unresolvedSubject,
            CrossSourceCanonicalAssertionKind.Terminology,
            unresolvedTerm, null, null, null, null, null, null, null, null);
        var unresolvedReceiptValue = new FileEvidenceReceipt(
            revisionId, artifact.Id, artifact.Digest,
            "grid.test.exact-secondary-parser", "1", unresolvedSubject.ExactRepresentation,
            unresolvedClaim.SourceFieldPath, null, null, null,
            new DateTimeOffset(2026, 9, 25, 15, 0, 0, TimeSpan.Zero), unresolvedSubject);
        var unresolvedReceipt = new CatalogFileEvidenceReceipt(
            EvidenceReceiptId.DeriveV2(unresolvedReceiptValue), unresolvedReceiptValue);
        receipts.Add(unresolvedReceipt);
        var unresolvedBindingId = UnresolvedCrossSourceEvidenceBindingId.DeriveV1(
            unresolvedReceipt.Id, revisionId, unresolvedClaim.Id,
            null,
            unresolvedClaim.AssertionKind, unresolvedClaim.SourceFieldPath,
            EvidenceVerificationKind.FileVerified);
        var unresolvedBinding = new UnresolvedCrossSourceEvidenceBinding(
            unresolvedBindingId, unresolvedReceipt.Id, revisionId, unresolvedClaim.Id,
            null,
            unresolvedClaim.AssertionKind, unresolvedClaim.SourceFieldPath,
            EvidenceVerificationKind.FileVerified);
        var unresolvedId = UnresolvedCrossSourceAssertionId.DeriveV1(
            unresolvedClaim.Id, revisionId, descriptor.RevisionId,
            CrossSourceTargetLinkKind.ExactLabelOrHashKey,
            "grid.test.exact-label-key-link", "1", CorrelationOutcome.Unresolved,
            "NO_EXACT_TARGET_LINK", [], [unresolvedReceipt.Id], [unresolvedBinding.Id], []);
        var unresolved = new UnresolvedCrossSourceAssertion(
            unresolvedId, unresolvedClaim.Id, revisionId, descriptor.RevisionId,
            CrossSourceTargetLinkKind.ExactLabelOrHashKey,
            "grid.test.exact-label-key-link", "1", CorrelationOutcome.Unresolved,
            "NO_EXACT_TARGET_LINK", [], [unresolvedReceipt.Id], [unresolvedBinding.Id], []);

        var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.cross-source", "container", "neutral-secondary.container");
        var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.cross-source", "member", "neutral-secondary.container!/facts.bin");
        var member = new SourceAcquisitionMember(memberCoordinate, 32, artifact.Digest, artifact.Id);
        var method = new AcquisitionMethodCoordinate(
            "grid.test.cross-source-acquisition", "1", "grid.test.fixture", "1",
            ContentDigest.ComputeSha256("cross-source-acquisition-tool"u8));
        var containerDigest = ContentDigest.ComputeSha256("neutral-secondary-container"u8);
        var acquisitionId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion, item.GameId, null, null,
            containerCoordinate, 64, containerDigest, method, [member]);
        var acquisition = new SourceAcquisitionReceipt(
            acquisitionId, SourceAcquisitionReceipt.CurrentSchemaVersion, item.GameId, null, null,
            containerCoordinate, 64, containerDigest, method, [member]);
        var acquisitionBinding = new SourceArtifactAcquisitionBinding(
            artifact.Id, acquisition.Id, memberCoordinate, 32, artifact.Digest);

        var report = payload.LocationCoverageReports.Single();
        var extendedPayload = CanonicalCatalogPackageImportChecks.CopyPayload(
            payload,
            sources: payload.Sources.Add(source),
            artifacts: payload.Artifacts.Add(artifact),
            sourceRevisions: payload.SourceRevisions.Add(boundRevision),
            terminologyAssertions: payload.TerminologyAssertions.Add(term).Add(conflictingTerm).Add(locationTerm),
            fileEvidenceReceipts: receipts.ToImmutable(),
            evidenceBindings: bindings.ToImmutable(),
            semanticClassifications: payload.SemanticClassificationAssertions.Add(itemClassification),
            organizationalValues: payload.OrganizationalValueAssertions.Add(actorDlc),
            acquisitionReceipts: payload.AcquisitionReceipts.Add(acquisition),
            acquisitionBindings: payload.ArtifactAcquisitionBindings.Add(acquisitionBinding),
            crossSourceAssertions: envelopes,
            crossSourceTargetLinkClaims: targetLinkClaims.ToImmutable(),
            unresolvedCrossSourceClaimContents: [unresolvedClaim],
            unresolvedCrossSourceEvidenceBindings: [unresolvedBinding],
            unresolvedCrossSourceAssertions: [unresolved]) with { LocationCoverageReports = [report] };
        var gameScope = new CatalogGameScope(
            seed.Manifest.GameScope.GameId,
            seed.Manifest.GameScope.ExactGameVersion,
            seed.Manifest.GameScope.ArtifactFingerprints.Add(artifact.Id));
        var package = CanonicalCatalogPackageKernel.CreateV6(
            seed.Manifest.PackageKind, gameScope, seed.Manifest.ModScope,
            seed.Manifest.RequiredBasePackageIds, seed.Manifest.CompositionPolicyVersion,
            extendedPayload, seed.ValidationSummary, seed.Manifest.BuildProvenance);
        return new(seed, package, item, actor, term, termEnvelope, unresolved,
            originBinding, item.SourceRevisionId, revisionId);
    }

    private static CanonicalCatalogPayload CreateWrongExistingTargetPayload(Fixture fixture)
    {
        var payload = fixture.Package.Payload;
        var sourceEnvelope = fixture.TermEnvelope;
        var sourceLinkClaim = payload.CrossSourceTargetLinkClaims.Single(value =>
            value.Id == sourceEnvelope.TargetLinkClaimId);
        var actor = fixture.Actor;
        var maliciousTerm = new TerminologyAssertion(
            actor.Id, fixture.AssertingRevisionId, fixture.Term.Role, fixture.Term.VerbatimValue,
            fixture.Term.SourceFieldPath, fixture.Term.LanguageTag, fixture.Term.NativeStringIdentifier);
        var maliciousTermClaim = EvidenceClaimContentId.DeriveV1(maliciousTerm);
        var sourceTermBinding = payload.EvidenceBindings.Single(value =>
            sourceEnvelope.SupportingEvidenceBindingIds.Contains(value.Id) &&
            value.ClaimKind == EvidenceClaimKind.Terminology);
        var maliciousTermBinding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                sourceTermBinding.EvidenceReceiptId, EvidenceClaimKind.Terminology, actor.Id,
                fixture.AssertingRevisionId, maliciousTerm.SourceFieldPath, maliciousTermClaim),
            sourceTermBinding.EvidenceReceiptId, EvidenceClaimKind.Terminology, actor.Id,
            fixture.AssertingRevisionId, maliciousTerm.SourceFieldPath, maliciousTermClaim);
        var maliciousLinkClaimId = CrossSourceTargetLinkClaimId.DeriveV1(
            actor.Id, actor.SourceRevisionId, actor.NativeRecordIdentityId,
            sourceLinkClaim.ExactTargetCoordinate,
            sourceLinkClaim.TargetCoordinateArtifactId, sourceLinkClaim.TargetCoordinateFieldPath,
            fixture.AssertingRevisionId, sourceLinkClaim.ExactAssertingCoordinate,
            sourceLinkClaim.AssertingCoordinateArtifactId, sourceLinkClaim.AssertingCoordinateFieldPath,
            sourceLinkClaim.LinkKind, sourceLinkClaim.MethodId, sourceLinkClaim.MethodVersion);
        var maliciousLinkClaim = new CrossSourceTargetLinkClaim(
            maliciousLinkClaimId, actor.Id, actor.SourceRevisionId, actor.NativeRecordIdentityId,
            sourceLinkClaim.ExactTargetCoordinate,
            sourceLinkClaim.TargetCoordinateArtifactId, sourceLinkClaim.TargetCoordinateFieldPath,
            fixture.AssertingRevisionId, sourceLinkClaim.ExactAssertingCoordinate,
            sourceLinkClaim.AssertingCoordinateArtifactId, sourceLinkClaim.AssertingCoordinateFieldPath,
            sourceLinkClaim.LinkKind, sourceLinkClaim.MethodId, sourceLinkClaim.MethodVersion);
        var maliciousLinkContent = EvidenceClaimContentId.DeriveV1(maliciousLinkClaim);
        var linkBindings = payload.EvidenceBindings.Where(value =>
                sourceEnvelope.SupportingEvidenceBindingIds.Contains(value.Id) &&
                value.ClaimKind == EvidenceClaimKind.CrossSourceTargetLink)
            .Select(value => new EvidenceBinding(
                EvidenceBindingId.DeriveV2(
                    value.EvidenceReceiptId, EvidenceClaimKind.CrossSourceTargetLink, actor.Id,
                    fixture.AssertingRevisionId, value.ClaimLocator, maliciousLinkContent),
                value.EvidenceReceiptId, EvidenceClaimKind.CrossSourceTargetLink, actor.Id,
                fixture.AssertingRevisionId, value.ClaimLocator, maliciousLinkContent))
            .ToImmutableArray();
        var actorOrigin = payload.EvidenceBindings.Single(value =>
            value.KnowledgeRecordId == actor.Id && value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity);
        var bindingIds = ImmutableArray.Create(actorOrigin.Id, maliciousTermBinding.Id)
            .AddRange(linkBindings.Select(value => value.Id));
        var receiptIds = bindingIds.Select(id =>
                id == actorOrigin.Id ? actorOrigin.EvidenceReceiptId :
                id == maliciousTermBinding.Id ? maliciousTermBinding.EvidenceReceiptId :
                linkBindings.Single(value => value.Id == id).EvidenceReceiptId)
            .Distinct().ToImmutableArray();
        var maliciousEnvelopeId = CrossSourceCanonicalAssertionId.DeriveV1(
            actor.Id, actor.SourceRevisionId, actor.NativeRecordIdentityId, fixture.AssertingRevisionId,
            CrossSourceCanonicalAssertionKind.Terminology, maliciousTermClaim, maliciousLinkClaim.Id,
            sourceEnvelope.TargetLinkKind, sourceEnvelope.ExactLinkKey,
            sourceEnvelope.TargetLinkMethodId, sourceEnvelope.TargetLinkMethodVersion,
            receiptIds, bindingIds, [], []);
        var maliciousEnvelope = new CrossSourceCanonicalAssertion(
            maliciousEnvelopeId, actor.Id, actor.SourceRevisionId, actor.NativeRecordIdentityId,
            fixture.AssertingRevisionId, CrossSourceCanonicalAssertionKind.Terminology,
            maliciousTermClaim, maliciousLinkClaim.Id, sourceEnvelope.TargetLinkKind,
            sourceEnvelope.ExactLinkKey, sourceEnvelope.TargetLinkMethodId,
            sourceEnvelope.TargetLinkMethodVersion, receiptIds, bindingIds, [], []);
        return CanonicalCatalogPackageImportChecks.CopyPayload(
            payload,
            terminologyAssertions: payload.TerminologyAssertions.Add(maliciousTerm),
            evidenceBindings: payload.EvidenceBindings.Add(maliciousTermBinding).AddRange(linkBindings),
            crossSourceAssertions: payload.CrossSourceAssertions.Add(maliciousEnvelope),
            crossSourceTargetLinkClaims: payload.CrossSourceTargetLinkClaims.Add(maliciousLinkClaim));
    }

    private static CanonicalSemanticClassificationAssertion Classification(
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId revisionId,
        CanonicalSemanticRoleId role,
        string path)
    {
        var id = CanonicalSemanticClassificationAssertionId.DeriveV1(
            record.Id, revisionId, role, "grid.test.exact-classification", "1",
            "grid.test.exact-classification", "1", path);
        return new(id, record.Id, revisionId, role, "grid.test.exact-classification", "1",
            "grid.test.exact-classification", "1", path);
    }

    private static CrossSourceCanonicalAssertion RecreateEnvelope(
        CrossSourceCanonicalAssertion source,
        SourceNativeIdentifier? exactLinkKey = null,
        string? targetLinkMethodVersion = null,
        ImmutableArray<EvidenceReceiptId>? receipts = null,
        ImmutableArray<EvidenceBindingId>? bindings = null)
    {
        var linkKey = exactLinkKey ?? source.ExactLinkKey;
        var version = targetLinkMethodVersion ?? source.TargetLinkMethodVersion;
        var receiptIds = receipts ?? source.SupportingEvidenceReceiptIds;
        var bindingIds = bindings ?? source.SupportingEvidenceBindingIds;
        var id = CrossSourceCanonicalAssertionId.DeriveV1(
            source.TargetKnowledgeRecordId, source.TargetOriginSourceRevisionId,
            source.TargetNativeRecordIdentityId, source.AssertingSourceRevisionId,
            source.AssertionKind, source.UnderlyingClaimContentId, source.TargetLinkClaimId, source.TargetLinkKind,
            linkKey, source.TargetLinkMethodId, version, receiptIds, bindingIds,
            source.SupportingInstructionEvidenceBindingIds, source.CorrelationRecordIds);
        return new(id, source.TargetKnowledgeRecordId, source.TargetOriginSourceRevisionId,
            source.TargetNativeRecordIdentityId, source.AssertingSourceRevisionId,
            source.AssertionKind, source.UnderlyingClaimContentId, source.TargetLinkClaimId, source.TargetLinkKind,
            linkKey, source.TargetLinkMethodId, version, receiptIds, bindingIds,
            source.SupportingInstructionEvidenceBindingIds, source.CorrelationRecordIds);
    }

    private static bool VerifyTampered(CanonicalCatalogPackage package, CanonicalCatalogPayload payload) =>
        CanonicalCatalogPackageKernel.Verify(package with { Payload = payload }).IsStructurallyValid;

    private static bool VerifyRebuilt(CanonicalCatalogPackage source, CanonicalCatalogPayload payload)
    {
        try
        {
            var rebuilt = CanonicalCatalogPackageKernel.CreateV6(
                source.Manifest.PackageKind, source.Manifest.GameScope, source.Manifest.ModScope,
                source.Manifest.RequiredBasePackageIds, source.Manifest.CompositionPolicyVersion,
                payload, source.ValidationSummary, source.Manifest.BuildProvenance);
            return CanonicalCatalogPackageKernel.Verify(rebuilt).IsStructurallyValid;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private sealed record Fixture(
        CanonicalCatalogPackage OriginPackage,
        CanonicalCatalogPackage Package,
        CanonicalKnowledgeRecord Item,
        CanonicalKnowledgeRecord Actor,
        TerminologyAssertion Term,
        CrossSourceCanonicalAssertion TermEnvelope,
        UnresolvedCrossSourceAssertion UnresolvedAssertion,
        EvidenceBinding OriginIdentityBinding,
        CatalogSourceRevisionId OriginRevisionId,
        CatalogSourceRevisionId AssertingRevisionId);
}
