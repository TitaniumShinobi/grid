using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalCatalogPackageImportChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Check failed: {message}");
            checks++;
        }
        void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T) { checks++; return; }
            throw new InvalidOperationException($"Check failed: {message}");
        }

        var fixtureRoot = Path.Combine(Path.GetTempPath(), "grid-catalog-package-import-" + Guid.NewGuid().ToString("N"));
        var storePath = Path.Combine(fixtureRoot, "catalog.v2.json");
        try
        {
            var first = CreatePackage("build-1", 0x31);
            var second = CreatePackage("build-2", 0x32);
            var store = new JsonCanonicalKnowledgeCatalogStore(storePath);

            var imported = await store.ImportPackageAsync(0, first);
            Assert(imported is { Status: CanonicalCatalogImportStatus.Imported, Revision: 1, ImportedSourceRevisionCount: 1 },
                "A structurally verified package imports all of its source revisions in one atomic commit.");

            var loaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
            Assert(loaded.IsValid && loaded.Snapshot.Revision == 1 &&
                   loaded.Snapshot.FindImportedPackage(first.Id)?.Id == first.Id,
                "Schema-v2 reload preserves the exact imported package and revision.");
            Assert(Enum.GetValues<KnowledgeKind>().All(kind =>
                    loaded.Snapshot.KnowledgeRecords.Any(record => record.Kind == kind)),
                "The shared canonical library historically indexes all four game-neutral knowledge kinds.");
            Assert(first.Payload.KnowledgeRecords.All(record =>
                    loaded.Snapshot.FindKnowledgeRecord(record.Id) == record) &&
                   loaded.Snapshot.FindAdapterBoundSourceRevision(first.Payload.SourceRevisions.Single().Revision.Id) is not null &&
                   loaded.Snapshot.FindAdapterRevision(first.Payload.AdapterDescriptors.Single().RevisionId) is not null,
                "Imported canonical identities and adapter-bound source-revision coordinates remain queryable.");
            Assert(loaded.Snapshot.FindAcquisitionReceipt(first.Payload.AcquisitionReceipts.Single().Id) is not null &&
                   loaded.Snapshot.FindArtifactAcquisitionBindings(first.Payload.Artifacts.Single().Id).Length == 1,
                "Acquisition provenance remains historically queryable by receipt and artifact identity.");

            var legacyAppend = await store.AppendAsync(1, CreateLegacyRegistration());
            Assert(legacyAppend is { Status: CanonicalCatalogAppendStatus.Appended, Revision: 2 } &&
                   (await store.LoadAsync()).Snapshot.FindImportedPackage(first.Id) is not null,
                "A schema-v1 append remains supported after package import without discarding schema-v2 package history.");

            var retry = await store.ImportPackageAsync(0, first);
            Assert(retry is { Status: CanonicalCatalogImportStatus.Unchanged, Revision: 2, ImportedSourceRevisionCount: 0 },
                "An exact package retry is idempotent even when its expected revision is stale.");

            var staleDistinct = await store.ImportPackageAsync(0, second);
            Assert(staleDistinct.Status == CanonicalCatalogImportStatus.Conflict && staleDistinct.Revision == 2,
                "A distinct package prepared against a stale shared-library revision conflicts without partial import.");

            var tamperedValidation = new CatalogValidationSummary(
                first.ValidationSummary.Status,
                first.ValidationSummary.PolicyId,
                first.ValidationSummary.ExactPolicyVersion,
                ContentDigest.ComputeSha256([0x7f]));
            var tampered = first with { ValidationSummary = tamperedValidation };
            var rejected = await store.ImportPackageAsync(2, tampered);
            Assert(rejected.Status == CanonicalCatalogImportStatus.Invalid &&
                   (await store.LoadAsync()).Snapshot.Revision == 2,
                "Structural tampering is rejected before any shared-library state is written.");

            var later = await store.ImportPackageAsync(2, second);
            Assert(later is { Status: CanonicalCatalogImportStatus.Imported, Revision: 3, ImportedSourceRevisionCount: 1 },
                "A later structurally verified package appends its new source revision.");
            var historical = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
            Assert(historical.IsValid && historical.Snapshot.FindImportedPackage(first.Id) is not null &&
                   historical.Snapshot.FindImportedPackage(second.Id) is not null &&
                   first.Payload.KnowledgeRecords.All(record => historical.Snapshot.FindKnowledgeRecord(record.Id) is not null),
                "A later package import preserves historical package, revision, and record lookups.");

            var persisted = await File.ReadAllTextAsync(storePath);
            Assert(persisted.Contains("\"schemaVersion\": 5", StringComparison.Ordinal),
                "Package import persists through the additive schema-v5 cross-source document boundary.");
            var schemaV2 = JsonNode.Parse(persisted)!.AsObject();
            schemaV2["schemaVersion"] = 2;
            schemaV2.Remove("sourceNativeLocationTypeAssertions");
            schemaV2.Remove("locationSemanticClassificationAssertions");
            schemaV2.Remove("recordLifecycleAssertions");
            schemaV2.Remove("correlatedRelationshipEnvelopes");
            schemaV2.Remove("locationCoverageReports");
            var schemaV2Path = Path.Combine(fixtureRoot, "canonical-catalog-v2.json");
            await File.WriteAllTextAsync(schemaV2Path, schemaV2.ToJsonString());
            var schemaV2Load = await new JsonCanonicalKnowledgeCatalogStore(schemaV2Path).LoadAsync();
            Assert(schemaV2Load.IsValid && schemaV2Load.Snapshot.LocationCoverageReports.IsEmpty &&
                   schemaV2Load.Snapshot.ImportedPackages.Length == 2,
                "Schema-v2 shared-library history remains readable without reinterpretation as frozen Location coverage.");

            var projectionPackage = CreateProjectionPackage("build-3", 0x33);
            CanonicalCatalogPackage RebuildProjection(CanonicalCatalogPayload payload) =>
                CanonicalCatalogPackageKernel.CreateV5(
                    projectionPackage.Manifest.PackageKind,
                    projectionPackage.Manifest.GameScope,
                    projectionPackage.Manifest.ModScope,
                    projectionPackage.Manifest.RequiredBasePackageIds,
                    projectionPackage.Manifest.CompositionPolicyVersion,
                    payload,
                    projectionPackage.ValidationSummary,
                    projectionPackage.Manifest.BuildProvenance);
            Assert(projectionPackage.Manifest.PackageSchemaVersion == CatalogPackageManifest.ProjectionContractSchemaVersion &&
                   projectionPackage.Manifest.PayloadDigest.Value.StartsWith("grid.catalog-payload.v5.sha256.", StringComparison.Ordinal) &&
                   projectionPackage.Manifest.CatalogRevisionId.Value.StartsWith("grid.catalog-revision.v5.sha256.", StringComparison.Ordinal) &&
                   projectionPackage.Id.Value.StartsWith("grid.catalog-package.v5.sha256.", StringComparison.Ordinal) &&
                   CanonicalCatalogPackageKernel.Verify(projectionPackage).IsStructurallyValid,
                "Schema-v5 identities bind exact projection and Instructions claim content in a structurally verified package.");
            var projectionImport = await store.ImportPackageAsync(3, projectionPackage);
            Assert(projectionImport is { Status: CanonicalCatalogImportStatus.Imported, Revision: 4, ImportedSourceRevisionCount: 1 },
                "A structurally verified schema-v5 package imports atomically through the schema-v5 additive store boundary.");
            var projectionRetry = await store.ImportPackageAsync(0, projectionPackage);
            Assert(projectionRetry is { Status: CanonicalCatalogImportStatus.Unchanged, Revision: 4 },
                "An exact schema-v5 package retry remains idempotent.");
            var projectionLoaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
            var semantic = projectionPackage.Payload.SemanticClassificationAssertions.Single(value =>
                value.RoleId == CanonicalProjectionSemantics.ItemWeapons);
            var contribution = projectionPackage.Payload.RecordContributionAssertions.Single();
            var organizational = projectionPackage.Payload.OrganizationalValueAssertions.Single();
            var instruction = projectionPackage.Payload.InstructionAssertions[0];
            var instructionBinding = projectionPackage.Payload.InstructionEvidenceBindings.Single(value =>
                value.InstructionAssertionId == instruction.Id);
            Assert(projectionLoaded.IsValid &&
                   projectionLoaded.Snapshot.FindSemanticClassification(semantic.Id)?.Id == semantic.Id &&
                   projectionLoaded.Snapshot.FindRecordContribution(contribution.Id)?.Id == contribution.Id &&
                   projectionLoaded.Snapshot.FindOrganizationalValue(organizational.Id)?.Id == organizational.Id &&
                   projectionLoaded.Snapshot.FindInstructionAssertion(instruction.Id)?.Content.VerbatimText ==
                   instruction.Content.VerbatimText &&
                   projectionLoaded.Snapshot.FindInstructionEvidenceBinding(instructionBinding.Id)?.Id == instructionBinding.Id,
                "Schema-v4 reload preserves exact projection and source-backed Instructions claims for historical lookup.");
            var projectionPersisted = await File.ReadAllTextAsync(storePath);
            var wrongObjectDocument = JsonNode.Parse(projectionPersisted)!.AsObject();
            wrongObjectDocument["instructionAssertions"]!.AsArray()[0]!["sourceLocator"]!["nativeObjectIdentity"]!["exactRepresentation"] =
                "WRONG_OBJECT";
            var wrongObjectPath = Path.Combine(fixtureRoot, "catalog-wrong-instruction-object.json");
            await File.WriteAllTextAsync(wrongObjectPath, wrongObjectDocument.ToJsonString());
            var wrongObjectBeforeLoad = await File.ReadAllTextAsync(wrongObjectPath);
            var wrongObjectLoad = await new JsonCanonicalKnowledgeCatalogStore(wrongObjectPath).LoadAsync();
            Assert(!wrongObjectLoad.IsValid &&
                   await File.ReadAllTextAsync(wrongObjectPath) == wrongObjectBeforeLoad,
                "Store reload rejects and never repairs a persisted Instruction with a substituted native object.");
            var wrongRevisionDocument = JsonNode.Parse(projectionPersisted)!.AsObject();
            wrongRevisionDocument["instructionAssertions"]!.AsArray()[0]!["sourceRevisionId"] =
                first.Payload.SourceRevisions.Single().Revision.Id.Value;
            var wrongRevisionPath = Path.Combine(fixtureRoot, "catalog-wrong-instruction-revision.json");
            await File.WriteAllTextAsync(wrongRevisionPath, wrongRevisionDocument.ToJsonString());
            var wrongRevisionBeforeLoad = await File.ReadAllTextAsync(wrongRevisionPath);
            var wrongRevisionLoad = await new JsonCanonicalKnowledgeCatalogStore(wrongRevisionPath).LoadAsync();
            Assert(!wrongRevisionLoad.IsValid &&
                   await File.ReadAllTextAsync(wrongRevisionPath) == wrongRevisionBeforeLoad,
                "Store reload rejects and never repairs a persisted Instruction with a substituted source revision.");
            var noClassificationEvidence = projectionPackage with
            {
                Payload = CopyPayload(
                    projectionPackage.Payload,
                    evidenceBindings: projectionPackage.Payload.EvidenceBindings.Where(value =>
                        value.ClaimKind != EvidenceClaimKind.SemanticClassification).ToImmutableArray()),
            };
            Assert(!CanonicalCatalogPackageKernel.Verify(noClassificationEvidence).IsStructurallyValid,
                "Projection classifications cannot survive package verification without exact claim evidence.");
            var projectionComposition = new CatalogCompositionId("composition.package-import-projection");
            var projectionApplicability = new CanonicalApplicabilityProjection(
                projectionComposition,
                projectionPackage.Payload.KnowledgeRecords.Select(value => value.Id).ToImmutableArray(),
                [],
                "1");
            AssertThrows<InvalidDataException>(() => CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                    noClassificationEvidence,
                    projectionComposition,
                    CanonicalSelectorProjectionPolicy.V1,
                    projectionApplicability),
                "Projection cannot consume a package whose assertion evidence closure was tampered.");

            foreach (var (record, role) in new[]
                     {
                         (projectionPackage.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.MissionQuest),
                             CanonicalProjectionSemantics.MissionOnline),
                         (projectionPackage.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Item),
                             CanonicalProjectionSemantics.ItemMagic),
                         (projectionPackage.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Actor),
                             CanonicalProjectionSemantics.ActorPlayerCharacter),
                     })
            {
                var falsePath = $"tamper/{record.Kind}/classification";
                var falseId = CanonicalSemanticClassificationAssertionId.DeriveV1(
                    record.Id, record.SourceRevisionId, role, "grid.tamper", "1", "grid.tamper", "1", falsePath);
                var falseAssertion = new CanonicalSemanticClassificationAssertion(
                    falseId, record.Id, record.SourceRevisionId, role,
                    "grid.tamper", "1", "grid.tamper", "1", falsePath);
                AssertThrows<InvalidDataException>(() => RebuildProjection(CopyPayload(
                        projectionPackage.Payload,
                        semanticClassifications: projectionPackage.Payload.SemanticClassificationAssertions.Add(falseAssertion))),
                    $"An unbound {record.Kind} classification cannot enter a structurally verified package.");
            }

            var location = projectionPackage.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Location);
            var falseParent = SourceNativeIdentifier.FromExactUtf8("grid.tamper", "Location", "MISSING_PARENT");
            var falseHierarchy = new RelationshipAssertion(
                location.Id, location.SourceRevisionId, LocationRelationshipSemantics.ContainedBy,
                "contained-by", "tamper/location/parent", falseParent);
            AssertThrows<InvalidDataException>(() => RebuildProjection(CopyPayload(
                    projectionPackage.Payload,
                    relationshipAssertions: projectionPackage.Payload.RelationshipAssertions.Add(falseHierarchy))),
                "An unbound Location hierarchy assertion cannot enter a verified projection package.");

            InstructionAssertion ReplaceInstructionLocator(InstructionSourceLocator locator)
            {
                var source = projectionPackage.Payload.InstructionAssertions[0];
                var id = InstructionAssertionId.DeriveV1(
                    source.SourceRevisionId, source.SourceScope, source.NativeIdentity, source.CategoryId,
                    source.Content.RetentionMode, source.Content.ContentDigest, locator, source.ExactLanguageTag,
                    source.CategoryMappingMethodId, source.CategoryMappingMethodVersion,
                    source.ApplicableRecordIds, source.ApplicablePackageIds);
                return new InstructionAssertion(
                    id, source.SourceRevisionId, source.SourceScope, source.NativeIdentity, source.CategoryId,
                    source.CategoryMappingMethodId, source.CategoryMappingMethodVersion, source.ExactLanguageTag,
                    source.Content, locator, source.ApplicableRecordIds, source.ApplicablePackageIds);
            }
            CanonicalCatalogPayload ReplaceInstruction(
                InstructionAssertion replacement,
                EvidenceReceiptId receiptId,
                EvidenceVerificationKind verification,
                ImmutableArray<CatalogFileEvidenceReceipt>? fileReceipts = null,
                ImmutableArray<CatalogReferenceEvidenceReceipt>? referenceReceipts = null)
            {
                var original = projectionPackage.Payload.InstructionAssertions[0];
                var claim = EvidenceClaimContentId.DeriveV1(replacement);
                var binding = new InstructionEvidenceBinding(
                    InstructionEvidenceBindingId.DeriveV3(
                        receiptId, replacement.Id, replacement.SourceRevisionId,
                        replacement.SourceLocator.ExactFieldPathOrFragment, claim, verification),
                    receiptId, replacement.Id, replacement.SourceRevisionId,
                    replacement.SourceLocator.ExactFieldPathOrFragment, claim, verification);
                return CopyPayload(
                    projectionPackage.Payload,
                    fileEvidenceReceipts: fileReceipts,
                    referenceEvidenceReceipts: referenceReceipts,
                    instructions: projectionPackage.Payload.InstructionAssertions
                        .Select(value => value.Id == original.Id ? replacement : value).ToImmutableArray(),
                    instructionBindings: projectionPackage.Payload.InstructionEvidenceBindings
                        .Where(value => value.InstructionAssertionId != original.Id).Append(binding).ToImmutableArray());
            }

            var originalInstruction = projectionPackage.Payload.InstructionAssertions[0];
            var originalInstructionBinding = projectionPackage.Payload.InstructionEvidenceBindings.Single(value =>
                value.InstructionAssertionId == originalInstruction.Id);
            var originalInstructionReceipt = projectionPackage.Payload.FileEvidenceReceipts.Single(value =>
                value.Id == originalInstructionBinding.EvidenceReceiptId);
            var originalNativeObject = originalInstruction.SourceLocator.NativeObjectIdentity;
            var changedBytes = originalNativeObject.IdentityBytes.ToArray();
            changedBytes[0] ^= 0xff;
            var nativeIdentityRelabels = new[]
            {
                new SourceNativeIdentifier(
                    originalNativeObject.Namespace + ".WRONG", originalNativeObject.ObjectType,
                    originalNativeObject.ExactRepresentation, originalNativeObject.IdentityBytes,
                    originalNativeObject.ComparisonMethodId, originalNativeObject.ComparisonMethodVersion),
                new SourceNativeIdentifier(
                    originalNativeObject.Namespace, originalNativeObject.ObjectType + ".WRONG",
                    originalNativeObject.ExactRepresentation, originalNativeObject.IdentityBytes,
                    originalNativeObject.ComparisonMethodId, originalNativeObject.ComparisonMethodVersion),
                new SourceNativeIdentifier(
                    originalNativeObject.Namespace, originalNativeObject.ObjectType,
                    originalNativeObject.ExactRepresentation, changedBytes.ToImmutableArray(),
                    originalNativeObject.ComparisonMethodId, originalNativeObject.ComparisonMethodVersion),
                new SourceNativeIdentifier(
                    originalNativeObject.Namespace, originalNativeObject.ObjectType,
                    originalNativeObject.ExactRepresentation, originalNativeObject.IdentityBytes,
                    originalNativeObject.ComparisonMethodId + ".WRONG", originalNativeObject.ComparisonMethodVersion),
                new SourceNativeIdentifier(
                    originalNativeObject.Namespace, originalNativeObject.ObjectType,
                    originalNativeObject.ExactRepresentation, originalNativeObject.IdentityBytes,
                    originalNativeObject.ComparisonMethodId, originalNativeObject.ComparisonMethodVersion + 1),
            };
            foreach (var relabeledIdentity in nativeIdentityRelabels)
            {
                var relabeled = ReplaceInstructionLocator(
                    originalInstruction.SourceLocator with { NativeObjectIdentity = relabeledIdentity });
                AssertThrows<InvalidDataException>(() => RebuildProjection(ReplaceInstruction(
                        relabeled,
                        originalInstructionReceipt.Id,
                        EvidenceVerificationKind.FileVerified)),
                    "FILE Instruction evidence rejects every complete native-object identity relabel even when exact representation is unchanged.");
            }
            var wrongArtifact = SourceArtifactId.DeriveV1(ContentDigest.ComputeSha256("wrong-artifact"u8));
            var instructionTamperLocators = new[]
            {
                new InstructionSourceLocator(
                    originalInstruction.SourceLocator.ArtifactId,
                    SourceNativeIdentifier.FromExactUtf8("grid.synthetic.instructions", "instruction", "WRONG_OBJECT"),
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment,
                    null),
                new InstructionSourceLocator(
                    wrongArtifact,
                    originalInstruction.SourceLocator.NativeObjectIdentity,
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment,
                    null),
                new InstructionSourceLocator(
                    originalInstruction.SourceLocator.ArtifactId,
                    originalInstruction.SourceLocator.NativeObjectIdentity,
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment,
                    SourceNativeVersion.FromExactUtf8("grid.synthetic.provider", "WRONG_REVISION")),
                new InstructionSourceLocator(
                    originalInstruction.SourceLocator.ArtifactId,
                    originalInstruction.SourceLocator.NativeObjectIdentity,
                    "WRONG_FIELD",
                    null),
            };
            foreach (var tamperedLocator in instructionTamperLocators)
            {
                var tamperedInstruction = ReplaceInstructionLocator(tamperedLocator);
                AssertThrows<InvalidDataException>(() => RebuildProjection(ReplaceInstruction(
                        tamperedInstruction,
                        originalInstructionReceipt.Id,
                        EvidenceVerificationKind.FileVerified)),
                    "An Instruction cannot borrow FILE_VERIFIED evidence for another artifact, object, revision, or field.");
            }
            AssertThrows<InvalidDataException>(() => RebuildProjection(ReplaceInstruction(
                    originalInstruction,
                    originalInstructionReceipt.Id,
                    EvidenceVerificationKind.ReferenceVerified)),
                "An Instruction cannot relabel FILE_VERIFIED evidence as REFERENCE_VERIFIED.");

            var providerSource = projectionPackage.Payload.Sources.Single();
            var referenceReceiptValue = new ReferenceEvidenceReceipt(
                providerSource.Id,
                originalInstruction.SourceRevisionId,
                originalInstruction.SourceLocator.ArtifactId!.Value,
                projectionPackage.Payload.Artifacts.Single().Digest,
                originalInstruction.SourceLocator.NativeObjectIdentity,
                originalInstruction.SourceLocator.ProviderObjectRevision,
                originalInstruction.SourceLocator.ExactFieldPathOrFragment,
                new DateTimeOffset(2026, 9, 25, 13, 0, 0, TimeSpan.Zero));
            var referenceReceipt = new CatalogReferenceEvidenceReceipt(
                EvidenceReceiptId.DeriveV1(referenceReceiptValue), referenceReceiptValue);
            var referencePayload = ReplaceInstruction(
                originalInstruction,
                referenceReceipt.Id,
                EvidenceVerificationKind.ReferenceVerified,
                projectionPackage.Payload.FileEvidenceReceipts.Where(value =>
                    value.Id != originalInstructionReceipt.Id).ToImmutableArray(),
                [referenceReceipt]);
            var referencePackage = RebuildProjection(referencePayload);
            Assert(CanonicalCatalogPackageKernel.Verify(referencePackage).IsStructurallyValid,
                "Exact REFERENCE_VERIFIED Instruction coordinates remain structurally valid and distinct from file proof.");
            AssertThrows<InvalidDataException>(() => RebuildProjection(ReplaceInstruction(
                    originalInstruction,
                    referenceReceipt.Id,
                    EvidenceVerificationKind.FileVerified,
                    projectionPackage.Payload.FileEvidenceReceipts.Where(value =>
                        value.Id != originalInstructionReceipt.Id).ToImmutableArray(),
                    [referenceReceipt])),
                "An Instruction cannot relabel REFERENCE_VERIFIED evidence as FILE_VERIFIED.");

            var originalClaim = EvidenceClaimContentId.DeriveV1(originalInstruction);
            var legacyFileReceiptValue = new FileEvidenceReceipt(
                originalInstructionReceipt.Receipt.SourceRevisionId,
                originalInstructionReceipt.Receipt.SourceArtifactId,
                originalInstructionReceipt.Receipt.ArtifactDigest,
                originalInstructionReceipt.Receipt.ParserId,
                originalInstructionReceipt.Receipt.ParserVersion,
                originalInstructionReceipt.Receipt.NativeRecordLocator,
                originalInstructionReceipt.Receipt.SourceFieldPath,
                originalInstructionReceipt.Receipt.ByteOffset,
                originalInstructionReceipt.Receipt.ByteLength,
                originalInstructionReceipt.Receipt.InterpretedBytesDigest,
                originalInstructionReceipt.Receipt.ObservedAtUtc);
            var legacyFileReceipt = new CatalogFileEvidenceReceipt(
                EvidenceReceiptId.DeriveV1(legacyFileReceiptValue), legacyFileReceiptValue);
            var legacyFileBinding = new InstructionEvidenceBinding(
                InstructionEvidenceBindingId.DeriveV1(
                    legacyFileReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim),
                legacyFileReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim);
            var legacyFilePackage = RebuildProjection(CopyPayload(
                projectionPackage.Payload,
                fileEvidenceReceipts: projectionPackage.Payload.FileEvidenceReceipts
                    .Where(value => value.Id != originalInstructionReceipt.Id).Append(legacyFileReceipt).ToImmutableArray(),
                instructionBindings: projectionPackage.Payload.InstructionEvidenceBindings
                    .Where(value => value.InstructionAssertionId != originalInstruction.Id).Append(legacyFileBinding).ToImmutableArray()));
            Assert(CanonicalCatalogPackageKernel.Verify(legacyFilePackage).IsStructurallyValid,
                "A genuine v1 FILE Instruction binding verifies under its original identity contract without upgrade.");
            var v2FileBinding = new InstructionEvidenceBinding(
                InstructionEvidenceBindingId.DeriveV2(
                    legacyFileReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim,
                    EvidenceVerificationKind.FileVerified),
                legacyFileReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim,
                EvidenceVerificationKind.FileVerified);
            var v2FilePackage = RebuildProjection(CopyPayload(
                projectionPackage.Payload,
                fileEvidenceReceipts: projectionPackage.Payload.FileEvidenceReceipts
                    .Where(value => value.Id != originalInstructionReceipt.Id).Append(legacyFileReceipt).ToImmutableArray(),
                instructionBindings: projectionPackage.Payload.InstructionEvidenceBindings
                    .Where(value => value.InstructionAssertionId != originalInstruction.Id).Append(v2FileBinding).ToImmutableArray()));
            Assert(CanonicalCatalogPackageKernel.Verify(v2FilePackage).IsStructurallyValid,
                "A genuine v2 FILE Instruction binding retains its evidence-class-aware historical semantics.");

            var legacyReferenceBinding = new InstructionEvidenceBinding(
                InstructionEvidenceBindingId.DeriveV1(
                    referenceReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                    originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim),
                referenceReceipt.Id, originalInstruction.Id, originalInstruction.SourceRevisionId,
                originalInstruction.SourceLocator.ExactFieldPathOrFragment, originalClaim);
            var legacyReferencePackage = RebuildProjection(CopyPayload(
                projectionPackage.Payload,
                fileEvidenceReceipts: projectionPackage.Payload.FileEvidenceReceipts
                    .Where(value => value.Id != originalInstructionReceipt.Id).ToImmutableArray(),
                referenceEvidenceReceipts: [referenceReceipt],
                instructionBindings: projectionPackage.Payload.InstructionEvidenceBindings
                    .Where(value => value.InstructionAssertionId != originalInstruction.Id).Append(legacyReferenceBinding).ToImmutableArray()));
            Assert(CanonicalCatalogPackageKernel.Verify(legacyReferencePackage).IsStructurallyValid,
                "A genuine v1 REFERENCE Instruction binding verifies under its original identity contract without upgrade.");

            foreach (var (description, legacyPackage, legacyBinding) in new[]
                     {
                         ("FILE", legacyFilePackage, legacyFileBinding),
                         ("REFERENCE", legacyReferencePackage, legacyReferenceBinding),
                     })
            {
                var legacyStorePath = Path.Combine(fixtureRoot, $"legacy-{description.ToLowerInvariant()}.json");
                var legacyStore = new JsonCanonicalKnowledgeCatalogStore(legacyStorePath);
                var legacyImport = await legacyStore.ImportPackageAsync(0, legacyPackage);
                var legacyLoad = await new JsonCanonicalKnowledgeCatalogStore(legacyStorePath).LoadAsync();
                var loadedBinding = legacyLoad.Snapshot.FindInstructionEvidenceBinding(legacyBinding.Id);
                Assert(legacyImport.Status == CanonicalCatalogImportStatus.Imported && legacyLoad.IsValid &&
                       loadedBinding?.Id == legacyBinding.Id &&
                       loadedBinding.Id.AlgorithmVersion == InstructionEvidenceBindingId.LegacyAlgorithmVersion &&
                       loadedBinding.Verification is null,
                    $"A v1 {description} Instruction binding survives package/import/store reload without reinterpretation.");
            }

            var instructionIds = projectionPackage.Payload.InstructionAssertions.Select(value => value.Id).ToImmutableArray();
            var conflictId = InstructionConflictGroupId.DeriveV1(
                instructionIds, "grid.synthetic.conflict", "1");
            var conflict = new InstructionConflictGroup(
                conflictId, instructionIds, "grid.synthetic.conflict", "1");
            var conflictPackage = RebuildProjection(CopyPayload(
                projectionPackage.Payload,
                instructionConflicts: [conflict]));
            var conflictMutation = await store.ImportPackageAsync(4, conflictPackage);
            var afterConflictAttempt = await store.LoadAsync();
            Assert(conflictMutation is { Status: CanonicalCatalogImportStatus.Invalid, Revision: 4 } &&
                   afterConflictAttempt.IsValid && afterConflictAttempt.Snapshot.Revision == 4 &&
                   afterConflictAttempt.Snapshot.InstructionConflictGroups.IsEmpty,
                "A distinct package cannot add conflict semantics to an existing source revision without advancing canonical history.");
            var conflictRetry = await store.ImportPackageAsync(0, projectionPackage);
            Assert(conflictRetry is { Status: CanonicalCatalogImportStatus.Unchanged, Revision: 4 },
                "Rejecting a semantic mutation preserves exact package retry idempotence.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, recursive: true);
        }

        return checks;
    }

    private static CanonicalCatalogRegistration CreateLegacyRegistration()
    {
        var gameId = new GameId("game.synthetic.legacy-append");
        var sourceNative = SourceNativeIdentifier.FromExactUtf8("grid.synthetic", "legacy-source", "legacy:source");
        var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, sourceNative);
        var source = new CatalogSourceRecord(sourceId, KnowledgeSourceKind.FrozenRepositoryDataset, sourceNative);
        var digest = ContentDigest.ComputeSha256([0x01, 0x02, 0x03]);
        var artifactId = SourceArtifactId.DeriveV1(digest);
        var artifact = new SourceArtifactRecord(artifactId, digest);
        var nativeRevision = SourceNativeVersion.FromExactUtf8("grid.synthetic.revision", "legacy-v1");
        var revisionId = CatalogSourceRevisionId.DeriveV1(sourceId, nativeRevision, [artifactId]);
        var revision = new CatalogSourceRevisionRecord(revisionId, sourceId, nativeRevision, [artifactId]);
        var nativeRecord = SourceNativeIdentifier.FromExactUtf8("grid.synthetic.records", "item", "legacy:item");
        var nativeRecordId = NativeRecordIdentityId.DeriveV1(gameId, nativeRecord);
        var recordId = KnowledgeRecordId.DeriveV1(gameId, null, null, revisionId, KnowledgeKind.Item, nativeRecordId);
        var record = new CanonicalKnowledgeRecord(
            recordId, gameId, null, null, revisionId, KnowledgeKind.Item, nativeRecordId, nativeRecord);
        var receipt = new FileEvidenceReceipt(
            revisionId,
            artifactId,
            digest,
            "grid.synthetic.legacy-parser",
            "1",
            "records[0]",
            "records[0].id",
            0,
            3,
            null,
            DateTimeOffset.UnixEpoch);
        var catalogReceipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt);
        var binding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                catalogReceipt.Id,
                EvidenceClaimKind.KnowledgeIdentity,
                recordId,
                revisionId,
                receipt.SourceFieldPath,
                null),
            catalogReceipt.Id,
            EvidenceClaimKind.KnowledgeIdentity,
            recordId,
            revisionId,
            receipt.SourceFieldPath,
            null);
        return new(source, [artifact], revision, [record], [], [], [catalogReceipt], [], [binding]);
    }

    private static CanonicalCatalogPackage CreatePackage(string exactBuild, byte digestSeed)
    {
        var gameId = new GameId("game.synthetic.four-kind");
        var gameVersion = SourceNativeVersion.FromExactUtf8("game.synthetic.build", exactBuild);
        var adapterDigest = ContentDigest.ComputeSha256([0x10, digestSeed]);
        var format = new SupportedKnowledgeFormat(
            "synthetic.four-kind",
            "1",
            ["frozen-member"],
            ["synthetic-records"],
            Enum.GetValues<KnowledgeKind>().ToImmutableArray(),
            false,
            false,
            false);
        var descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId("grid.synthetic.four-kind"),
            "1",
            adapterDigest,
            1,
            "1",
            [gameId],
            [format],
            new KnowledgeAdapterResourceLimits(4096, 1, 4, 1));

        var artifactDigest = ContentDigest.ComputeSha256([digestSeed, 0x41, 0x42, 0x43]);
        var artifactId = SourceArtifactId.DeriveV1(artifactDigest);
        var artifact = new SourceArtifactRecord(artifactId, artifactDigest);
        var sourceNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic",
            "frozen-source",
            $"source:{exactBuild}");
        var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, sourceNative);
        var source = new CatalogSourceRecord(sourceId, KnowledgeSourceKind.FrozenRepositoryDataset, sourceNative);
        var revisionId = CatalogSourceRevisionId.DeriveV2(sourceId, gameVersion, [artifactId], descriptor.RevisionId);
        var revision = new CatalogSourceRevisionRecord(revisionId, sourceId, gameVersion, [artifactId]);
        var scope = KnowledgeSourceScope.BaseGame(gameId, gameVersion);
        var adapterBoundRevision = new AdapterBoundCatalogSourceRevisionRecord(
            revision,
            descriptor.RevisionId,
            scope,
            [new SourceArtifactFormatBinding(artifactId, new KnowledgeFormatCoordinate(format.FormatId, format.ExactFormatVersion))]);

        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>();
        var receipts = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var observedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var index = 0;
        foreach (var kind in Enum.GetValues<KnowledgeKind>())
        {
            var native = SourceNativeIdentifier.FromExactUtf8(
                "grid.synthetic.records",
                kind.ToString(),
                $"{exactBuild}:{kind}");
            var nativeId = NativeRecordIdentityId.DeriveV1(gameId, native);
            var recordId = KnowledgeRecordId.DeriveV1(gameId, gameVersion, null, revisionId, kind, nativeId);
            var record = new CanonicalKnowledgeRecord(
                recordId, gameId, gameVersion, null, revisionId, kind, nativeId, native);
            records.Add(record);

            var fieldPath = $"records[{index}].id";
            var receipt = new FileEvidenceReceipt(
                revisionId,
                artifactId,
                artifactDigest,
                "grid.synthetic.parser",
                "1",
                $"records[{index}]",
                fieldPath,
                index * 4,
                4,
                null,
                observedAt);
            var catalogReceipt = new CatalogFileEvidenceReceipt(EvidenceReceiptId.DeriveV1(receipt), receipt);
            receipts.Add(catalogReceipt);
            bindings.Add(new EvidenceBinding(
                EvidenceBindingId.DeriveV2(
                    catalogReceipt.Id,
                    EvidenceClaimKind.KnowledgeIdentity,
                    recordId,
                    revisionId,
                    fieldPath,
                    null),
                catalogReceipt.Id,
                EvidenceClaimKind.KnowledgeIdentity,
                recordId,
                revisionId,
                fieldPath,
                null));
            index++;
        }

        var memberCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic.container",
            "member",
            $"synthetic.rpf!/records/{exactBuild}.bin");
        var member = new SourceAcquisitionMember(memberCoordinate, 4, artifactDigest, artifactId);
        var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic.container",
            "container",
            "synthetic.rpf");
        var acquisitionMethod = new AcquisitionMethodCoordinate(
            "grid.synthetic.acquire",
            "1",
            "grid.synthetic.tool",
            "1",
            ContentDigest.ComputeSha256([0x55]));
        var acquisitionId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            gameId,
            null,
            null,
            containerCoordinate,
            128,
            ContentDigest.ComputeSha256([0x60, digestSeed]),
            acquisitionMethod,
            [member]);
        var acquisition = new SourceAcquisitionReceipt(
            acquisitionId,
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            gameId,
            null,
            null,
            containerCoordinate,
            128,
            ContentDigest.ComputeSha256([0x60, digestSeed]),
            acquisitionMethod,
            [member]);
        var acquisitionBinding = new SourceArtifactAcquisitionBinding(
            artifactId,
            acquisitionId,
            memberCoordinate,
            4,
            artifactDigest);

        var payload = new CanonicalCatalogPayload(
            KnowledgeCoverageState.Partial,
            [descriptor],
            [source],
            [artifact],
            [adapterBoundRevision],
            records.ToImmutable(),
            [],
            [],
            receipts.ToImmutable(),
            [],
            bindings.ToImmutable(),
            [],
            [],
            [acquisition],
            [acquisitionBinding]);
        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate,
            "grid.synthetic.structural",
            "1",
            ContentDigest.ComputeSha256([0x70, digestSeed]));
        var provenance = new CatalogBuildProvenance(
            CatalogBuildProvenance.CurrentSchemaVersion,
            "grid.synthetic.builder",
            "1",
            new string('a', 40),
            [new CatalogCommittedBuildInput("src/Grid.Core/Grid.Core.csproj", new string('b', 40))]);
        return CanonicalCatalogPackageKernel.CreateV3(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(gameId, gameVersion, [artifactId]),
            null,
            [],
            "1",
            payload,
            validation,
            provenance);
    }

    internal static CanonicalCatalogPackage CreateProjectionPackage(string exactBuild, byte digestSeed)
    {
        var seed = CreatePackage(exactBuild, digestSeed);
        var location = seed.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Location);
        var mission = seed.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.MissionQuest);
        var item = seed.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Item);
        var actor = seed.Payload.KnowledgeRecords.Single(value => value.Kind == KnowledgeKind.Actor);
        var sourceRevisionId = item.SourceRevisionId;
        var artifact = seed.Payload.Artifacts.Single();
        var itemIdentityBinding = seed.Payload.EvidenceBindings.Single(value => value.KnowledgeRecordId == item.Id);
        var itemIdentityReceipt = seed.Payload.FileEvidenceReceipts.Single(value => value.Id == itemIdentityBinding.EvidenceReceiptId);
        var locationIdentityBinding = seed.Payload.EvidenceBindings.Single(value => value.KnowledgeRecordId == location.Id);
        var locationIdentityReceipt = seed.Payload.FileEvidenceReceipts.Single(value => value.Id == locationIdentityBinding.EvidenceReceiptId);
        var missionIdentityBinding = seed.Payload.EvidenceBindings.Single(value => value.KnowledgeRecordId == mission.Id);
        var missionIdentityReceipt = seed.Payload.FileEvidenceReceipts.Single(value => value.Id == missionIdentityBinding.EvidenceReceiptId);
        var actorIdentityBinding = seed.Payload.EvidenceBindings.Single(value => value.KnowledgeRecordId == actor.Id);
        var actorIdentityReceipt = seed.Payload.FileEvidenceReceipts.Single(value => value.Id == actorIdentityBinding.EvidenceReceiptId);
        CatalogFileEvidenceReceipt Receipt(string locator, string fieldPath)
        {
            var value = new FileEvidenceReceipt(
                sourceRevisionId, artifact.Id, artifact.Digest,
                "grid.synthetic.parser", "1", locator, fieldPath,
                null, null, null, new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero));
            return new(EvidenceReceiptId.DeriveV1(value), value);
        }

        CatalogFileEvidenceReceipt InstructionReceipt(
            SourceNativeIdentifier nativeObjectIdentity,
            string fieldPath)
        {
            var value = new FileEvidenceReceipt(
                sourceRevisionId, artifact.Id, artifact.Digest,
                "grid.synthetic.parser", "1", nativeObjectIdentity.ExactRepresentation, fieldPath,
                null, null, null, new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero),
                nativeObjectIdentity);
            return new(EvidenceReceiptId.DeriveV2(value), value);
        }

        const string classificationPath = "records[2].family";
        var classificationId = CanonicalSemanticClassificationAssertionId.DeriveV1(
            item.Id, sourceRevisionId, CanonicalProjectionSemantics.ItemWeapons,
            "grid.item-family", "1", "grid.synthetic.item-family", "1", classificationPath);
        var classification = new CanonicalSemanticClassificationAssertion(
            classificationId, item.Id, sourceRevisionId, CanonicalProjectionSemantics.ItemWeapons,
            "grid.item-family", "1", "grid.synthetic.item-family", "1", classificationPath);
        var classificationReceipt = Receipt("records[2]", classificationPath);
        var classificationClaim = EvidenceClaimContentId.DeriveV1(classification);
        var classificationBinding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                classificationReceipt.Id, EvidenceClaimKind.SemanticClassification,
                item.Id, sourceRevisionId, classificationPath, classificationClaim),
            classificationReceipt.Id, EvidenceClaimKind.SemanticClassification,
            item.Id, sourceRevisionId, classificationPath, classificationClaim);

        const string contributionPath = "records[2].contribution";
        var contributionId = CanonicalRecordContributionAssertionId.DeriveV1(
            item.Id, sourceRevisionId, CanonicalRecordContributionKind.Introduced,
            null, null, null, "grid.synthetic.contribution", "1", contributionPath);
        var contribution = new CanonicalRecordContributionAssertion(
            contributionId, item.Id, sourceRevisionId, CanonicalRecordContributionKind.Introduced,
            null, null, null, "grid.synthetic.contribution", "1", contributionPath);
        var contributionReceipt = Receipt("records[2]", contributionPath);
        var contributionClaim = EvidenceClaimContentId.DeriveV1(contribution);
        var contributionBinding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                contributionReceipt.Id, EvidenceClaimKind.RecordContribution,
                item.Id, sourceRevisionId, contributionPath, contributionClaim),
            contributionReceipt.Id, EvidenceClaimKind.RecordContribution,
            item.Id, sourceRevisionId, contributionPath, contributionClaim);

        const string actorRolePath = "records[3].role";
        var actorRoleId = CanonicalSemanticClassificationAssertionId.DeriveV1(
            actor.Id, sourceRevisionId, CanonicalProjectionSemantics.ActorNpc,
            "grid.actor-role", "1", "grid.synthetic.actor-role", "1", actorRolePath);
        var actorRole = new CanonicalSemanticClassificationAssertion(
            actorRoleId, actor.Id, sourceRevisionId, CanonicalProjectionSemantics.ActorNpc,
            "grid.actor-role", "1", "grid.synthetic.actor-role", "1", actorRolePath);
        var actorRoleReceipt = Receipt("records[3]", actorRolePath);
        var actorRoleClaim = EvidenceClaimContentId.DeriveV1(actorRole);
        var actorRoleBinding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                actorRoleReceipt.Id, EvidenceClaimKind.SemanticClassification,
                actor.Id, sourceRevisionId, actorRolePath, actorRoleClaim),
            actorRoleReceipt.Id, EvidenceClaimKind.SemanticClassification,
            actor.Id, sourceRevisionId, actorRolePath, actorRoleClaim);

        const string factionPath = "records[3].faction";
        var factionIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic.actor", "faction", "Faction.Native.Id");
        var factionId = CanonicalOrganizationalValueAssertionId.DeriveV1(
            actor.Id, sourceRevisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
            factionIdentity, null, "grid.synthetic.actor-faction", "1", factionPath);
        var faction = new CanonicalOrganizationalValueAssertion(
            factionId, actor.Id, sourceRevisionId, CanonicalProjectionSemantics.ActorFactionDimensionNode,
            factionIdentity, null, "grid.synthetic.actor-faction", "1", factionPath);
        var factionReceipt = Receipt("records[3]", factionPath);
        var factionClaim = EvidenceClaimContentId.DeriveV1(faction);
        var factionBinding = new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                factionReceipt.Id, EvidenceClaimKind.OrganizationalValue,
                actor.Id, sourceRevisionId, factionPath, factionClaim),
            factionReceipt.Id, EvidenceClaimKind.OrganizationalValue,
            actor.Id, sourceRevisionId, factionPath, factionClaim);

        const string instructionPath = "instructions[0].text";
        var instructionNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic.instructions", "instruction", "requirements:0");
        var instructionContent = InstructionContent.FromVerbatim("  Exact source instruction.\r\n");
        var instructionLocator = new InstructionSourceLocator(
            artifact.Id, instructionNative, instructionPath, null);
        var sourceScope = seed.Payload.SourceRevisions.Single().SourceScope;
        var instructionId = InstructionAssertionId.DeriveV1(
            sourceRevisionId, sourceScope, instructionNative, InstructionCategories.Requirements,
            instructionContent.RetentionMode, instructionContent.ContentDigest, instructionLocator,
            "en", "grid.synthetic.instruction-category", "1", [item.Id], []);
        var instruction = new InstructionAssertion(
            instructionId, sourceRevisionId, sourceScope, instructionNative,
            InstructionCategories.Requirements, "grid.synthetic.instruction-category", "1", "en",
            instructionContent, instructionLocator, [item.Id], []);
        var instructionReceipt = InstructionReceipt(instructionNative, instructionPath);
        var instructionClaim = EvidenceClaimContentId.DeriveV1(instruction);
        var instructionBinding = new InstructionEvidenceBinding(
            InstructionEvidenceBindingId.DeriveV3(
                instructionReceipt.Id, instruction.Id, sourceRevisionId, instructionPath, instructionClaim,
                EvidenceVerificationKind.FileVerified),
            instructionReceipt.Id, instruction.Id, sourceRevisionId, instructionPath, instructionClaim,
            EvidenceVerificationKind.FileVerified);

        const string secondInstructionPath = "instructions[1].text";
        var secondInstructionNative = SourceNativeIdentifier.FromExactUtf8(
            "grid.synthetic.instructions", "instruction", "requirements:1");
        var secondInstructionContent = InstructionContent.FromVerbatim("Second exact source instruction.");
        var secondInstructionLocator = new InstructionSourceLocator(
            artifact.Id, secondInstructionNative, secondInstructionPath, null);
        var secondInstructionId = InstructionAssertionId.DeriveV1(
            sourceRevisionId, sourceScope, secondInstructionNative, InstructionCategories.Requirements,
            secondInstructionContent.RetentionMode, secondInstructionContent.ContentDigest, secondInstructionLocator,
            "en", "grid.synthetic.instruction-category", "1", [item.Id], []);
        var secondInstruction = new InstructionAssertion(
            secondInstructionId, sourceRevisionId, sourceScope, secondInstructionNative,
            InstructionCategories.Requirements, "grid.synthetic.instruction-category", "1", "en",
            secondInstructionContent, secondInstructionLocator, [item.Id], []);
        var secondInstructionReceipt = InstructionReceipt(secondInstructionNative, secondInstructionPath);
        var secondInstructionClaim = EvidenceClaimContentId.DeriveV1(secondInstruction);
        var secondInstructionBinding = new InstructionEvidenceBinding(
            InstructionEvidenceBindingId.DeriveV3(
                secondInstructionReceipt.Id, secondInstruction.Id, sourceRevisionId,
                secondInstructionPath, secondInstructionClaim, EvidenceVerificationKind.FileVerified),
            secondInstructionReceipt.Id, secondInstruction.Id, sourceRevisionId,
            secondInstructionPath, secondInstructionClaim, EvidenceVerificationKind.FileVerified);

        var locationValidation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate, "grid.synthetic.location", "1",
            ContentDigest.ComputeSha256("synthetic-location"u8));
        var locationScope = seed.Payload.SourceRevisions.Single().SourceScope;
        var locationFamily = new LocationSourceFamilyDeclaration(
            new LocationSourceFamilyId("grid.synthetic.locations"),
            seed.Payload.SourceRevisions.Single().ArtifactFormats.Single().Format,
            seed.Payload.AdapterDescriptors.Single().RevisionId,
            true,
            [artifact.Id],
            [sourceRevisionId],
            []);
        var locationManifestId = LocationCoverageManifestId.DeriveV1(
            locationScope, "1", false, locationValidation, [locationFamily]);
        var locationManifest = new LocationCoverageManifest(
            locationManifestId, locationScope, "1", false, locationValidation, [locationFamily]);
        var locationFamilyCoverage = new LocationSourceFamilyCoverage(
            locationFamily.SourceFamilyId, [artifact.Id], [sourceRevisionId], 1, 1, 1,
            [location.Id], [], [], 0, 0, 0, 0);
        var locationReport = LocationCoverageReport.Create(
            locationManifest,
            [locationFamilyCoverage],
            [new LocationSemanticCategoryCoverage(null, null, [location.Id], 1)],
            new LocationTerminologyCoverage(1, 0, 0, 1, 0),
            new LocationHierarchyCoverage(0, 0, 0, 0, true),
            [],
            []);

        var payload = CopyPayload(
            seed.Payload,
            knowledgeRecords: [location, mission, item, actor],
            fileEvidenceReceipts:
            [
                locationIdentityReceipt, missionIdentityReceipt, itemIdentityReceipt, actorIdentityReceipt, classificationReceipt, contributionReceipt,
                actorRoleReceipt, factionReceipt, instructionReceipt, secondInstructionReceipt,
            ],
            evidenceBindings:
            [
                locationIdentityBinding, missionIdentityBinding, itemIdentityBinding, actorIdentityBinding, classificationBinding, contributionBinding,
                actorRoleBinding, factionBinding,
            ],
            semanticClassifications: [classification, actorRole],
            recordContributions: [contribution],
            organizationalValues: [faction],
            instructions: [instruction, secondInstruction],
            instructionBindings: [instructionBinding, secondInstructionBinding]);
        payload = payload with { LocationCoverageReports = [locationReport] };
        return CanonicalCatalogPackageKernel.CreateV5(
            CatalogPackageKind.BaseGameCatalog,
            seed.Manifest.GameScope,
            null,
            [],
            seed.Manifest.CompositionPolicyVersion,
            payload,
            seed.ValidationSummary,
            seed.Manifest.BuildProvenance);
    }

    internal static CanonicalCatalogPayload CopyPayload(
        CanonicalCatalogPayload source,
        ImmutableArray<GameKnowledgeAdapterDescriptor>? adapterDescriptors = null,
        ImmutableArray<CatalogSourceRecord>? sources = null,
        ImmutableArray<SourceArtifactRecord>? artifacts = null,
        ImmutableArray<AdapterBoundCatalogSourceRevisionRecord>? sourceRevisions = null,
        ImmutableArray<CanonicalKnowledgeRecord>? knowledgeRecords = null,
        ImmutableArray<TerminologyAssertion>? terminologyAssertions = null,
        ImmutableArray<RelationshipAssertion>? relationshipAssertions = null,
        ImmutableArray<CatalogFileEvidenceReceipt>? fileEvidenceReceipts = null,
        ImmutableArray<CatalogReferenceEvidenceReceipt>? referenceEvidenceReceipts = null,
        ImmutableArray<EvidenceBinding>? evidenceBindings = null,
        ImmutableArray<CanonicalCorrelationEnvelope>? correlationEnvelopes = null,
        ImmutableArray<CanonicalSemanticClassificationAssertion>? semanticClassifications = null,
        ImmutableArray<CanonicalRecordContributionAssertion>? recordContributions = null,
        ImmutableArray<CanonicalOrganizationalValueAssertion>? organizationalValues = null,
        ImmutableArray<InstructionAssertion>? instructions = null,
        ImmutableArray<InstructionEvidenceBinding>? instructionBindings = null,
        ImmutableArray<InstructionConflictGroup>? instructionConflicts = null,
        ImmutableArray<SourceAcquisitionReceipt>? acquisitionReceipts = null,
        ImmutableArray<SourceArtifactAcquisitionBinding>? acquisitionBindings = null,
        ImmutableArray<CrossSourceCanonicalAssertion>? crossSourceAssertions = null,
        ImmutableArray<CrossSourceTargetLinkClaim>? crossSourceTargetLinkClaims = null,
        ImmutableArray<UnresolvedCrossSourceClaimContent>? unresolvedCrossSourceClaimContents = null,
        ImmutableArray<UnresolvedCrossSourceEvidenceBinding>? unresolvedCrossSourceEvidenceBindings = null,
        ImmutableArray<UnresolvedCrossSourceAssertion>? unresolvedCrossSourceAssertions = null) =>
        new(
            source.EffectiveCoverage,
            adapterDescriptors ?? source.AdapterDescriptors,
            sources ?? source.Sources,
            artifacts ?? source.Artifacts,
            sourceRevisions ?? source.SourceRevisions,
            knowledgeRecords ?? source.KnowledgeRecords,
            terminologyAssertions ?? source.TerminologyAssertions,
            relationshipAssertions ?? source.RelationshipAssertions,
            fileEvidenceReceipts ?? source.FileEvidenceReceipts,
            referenceEvidenceReceipts ?? source.ReferenceEvidenceReceipts,
            evidenceBindings ?? source.EvidenceBindings,
            correlationEnvelopes ?? source.CorrelationEnvelopes,
            source.UnresolvedSourceAssertions,
            acquisitionReceipts ?? source.AcquisitionReceipts,
            acquisitionBindings ?? source.ArtifactAcquisitionBindings)
        {
            SourceNativeLocationTypeAssertions = source.SourceNativeLocationTypeAssertions,
            LocationSemanticClassificationAssertions = source.LocationSemanticClassificationAssertions,
            RecordLifecycleAssertions = source.RecordLifecycleAssertions,
            CorrelatedRelationshipEnvelopes = source.CorrelatedRelationshipEnvelopes,
            LocationCoverageReports = source.LocationCoverageReports,
            SemanticClassificationAssertions = semanticClassifications ?? source.SemanticClassificationAssertions,
            RecordContributionAssertions = recordContributions ?? source.RecordContributionAssertions,
            OrganizationalValueAssertions = organizationalValues ?? source.OrganizationalValueAssertions,
            InstructionAssertions = instructions ?? source.InstructionAssertions,
            InstructionEvidenceBindings = instructionBindings ?? source.InstructionEvidenceBindings,
            InstructionConflictGroups = instructionConflicts ?? source.InstructionConflictGroups,
            CrossSourceAssertions = crossSourceAssertions ?? source.CrossSourceAssertions,
            CrossSourceTargetLinkClaims = crossSourceTargetLinkClaims ?? source.CrossSourceTargetLinkClaims,
            UnresolvedCrossSourceClaimContents = unresolvedCrossSourceClaimContents ?? source.UnresolvedCrossSourceClaimContents,
            UnresolvedCrossSourceEvidenceBindings = unresolvedCrossSourceEvidenceBindings ?? source.UnresolvedCrossSourceEvidenceBindings,
            UnresolvedCrossSourceAssertions = unresolvedCrossSourceAssertions ?? source.UnresolvedCrossSourceAssertions,
        };
}
