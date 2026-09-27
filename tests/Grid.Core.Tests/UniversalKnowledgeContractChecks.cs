using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class UniversalKnowledgeContractChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;

        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException($"Check failed: {message}");
            checks++;
        }

        void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                checks++;
                return;
            }

            throw new InvalidOperationException($"Check failed: {message}");
        }

        var adapter = new ConformanceFixtureAdapter();
        var bytes = Encoding.UTF8.GetBytes(ConformanceFixtureAdapter.ExactFixtureContent).ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(bytes.AsSpan());
        var artifactId = SourceArtifactId.DeriveV1(digest);
        var artifact = new FrozenSourceArtifact(
            artifactId,
            digest,
            SourceNativeIdentifier.FromExactUtf8(
                "grid.conformance.fixture",
                "frozen-resource-coordinate",
                "fixture/content/catalog.fixture"),
            ConformanceFixtureAdapter.Format,
            bytes);
        var gameVersion = SourceNativeVersion.FromExactUtf8("grid.conformance.game-build", "build-7-exact");
        var discoveryRequest = new PreproductionSourceDiscoveryRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            null,
            null,
            [artifact]);
        var extractionRequest = new PreproductionKnowledgeExtractionRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            null,
            null,
            adapter.Descriptor,
            [artifact]);

        var discoveryA = await adapter.DiscoverAsync(discoveryRequest);
        var discoveryB = await adapter.DiscoverAsync(discoveryRequest);
        var extractionA = await adapter.ExtractAsync(extractionRequest);
        var extractionB = await adapter.ExtractAsync(extractionRequest);
        Assert(discoveryA.SourceCandidates.Length == 1 && discoveryB.SourceCandidates.Length == 1 &&
               discoveryA.SourceCandidates[0].Source == discoveryB.SourceCandidates[0].Source &&
               discoveryA.SourceCandidates[0].NativeRevision == discoveryB.SourceCandidates[0].NativeRevision &&
               discoveryA.SourceCandidates[0].ArtifactFormats.SequenceEqual(discoveryB.SourceCandidates[0].ArtifactFormats) &&
               discoveryA.SourceCandidates[0].ArtifactIds.SequenceEqual(discoveryB.SourceCandidates[0].ArtifactIds) &&
               discoveryA.CoverageState == KnowledgeCoverageState.Partial,
            "Game-neutral discovery is deterministic and identifies sources without emitting knowledge.");
        Assert(extractionA.CanonicalRegistrations.Length == 1 &&
               extractionA.CanonicalRegistrations[0].AdapterRevisionId == adapter.Descriptor.RevisionId &&
               extractionA.CoverageState == KnowledgeCoverageState.Partial,
            "Game-neutral extraction emits adapter-bound canonical registrations with explicit partial coverage.");

        var payloadA = ToPayload(extractionA);
        var payloadB = ToPayload(extractionB);
        Assert(CanonicalCatalogPackageKernel.ComputePayloadDigest(payloadA) ==
               CanonicalCatalogPackageKernel.ComputePayloadDigest(payloadB),
            "Repeated extraction from the same frozen inputs produces one semantic payload digest.");

        var sourceRevision = payloadA.SourceRevisions.Single();
        Assert(sourceRevision.Revision.Id.Value.StartsWith("grid.catalog-source-revision.v2.sha256.", StringComparison.Ordinal) &&
               sourceRevision.AdapterRevisionId == adapter.Descriptor.RevisionId,
            "Adapter-bound source revisions use the additive v2 identity without reinterpreting v1 revisions.");
        var changedAdapterRevision = KnowledgeAdapterRevisionId.DeriveV1(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            ContentDigest.ComputeSha256("different-adapter-binary"u8),
            adapter.Descriptor.AdapterContractVersion,
            adapter.Descriptor.MappingRulesVersion);
        Assert(CatalogSourceRevisionId.DeriveV2(
                   sourceRevision.Revision.SourceId,
                   sourceRevision.Revision.NativeRevision,
                   sourceRevision.Revision.ArtifactIds,
                   changedAdapterRevision) != sourceRevision.Revision.Id,
            "Changing the adapter revision changes an otherwise identical v2 source revision identity.");
        Assert(KnowledgeAdapterRevisionId.DeriveV1(
                   adapter.Descriptor.AdapterId,
                   adapter.Descriptor.ExactAdapterVersion,
                   adapter.Descriptor.AdapterArtifactDigest,
                   adapter.Descriptor.AdapterContractVersion,
                   adapter.Descriptor.MappingRulesVersion) == adapter.Descriptor.RevisionId,
            "Adapter revision derivation is deterministic from its exact declared coordinate.");
        Assert(KnowledgeAdapterRevisionId.DeriveV1(
                   adapter.Descriptor.AdapterId,
                   adapter.Descriptor.ExactAdapterVersion,
                   adapter.Descriptor.AdapterArtifactDigest,
                   adapter.Descriptor.AdapterContractVersion,
                   "fixture.mapping.v2") != adapter.Descriptor.RevisionId,
            "Mapping-rule changes produce a different adapter revision identity.");

        var v2ArtifactA = ContentDigest.ComputeSha256("same-semantics-build-a"u8);
        var v2ArtifactB = ContentDigest.ComputeSha256("same-semantics-build-b"u8);
        var v2A = new GameKnowledgeAdapterDescriptor(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            v2ArtifactA,
            adapter.Descriptor.AdapterContractVersion,
            adapter.Descriptor.MappingRulesVersion,
            adapter.Descriptor.SupportedGameIds,
            adapter.Descriptor.SupportedFormats,
            adapter.Descriptor.ResourceLimits,
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
        var v2B = new GameKnowledgeAdapterDescriptor(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            v2ArtifactB,
            adapter.Descriptor.AdapterContractVersion,
            adapter.Descriptor.MappingRulesVersion,
            adapter.Descriptor.SupportedGameIds,
            adapter.Descriptor.SupportedFormats,
            adapter.Descriptor.ResourceLimits,
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
        Assert(v2A.RevisionId == v2B.RevisionId &&
               v2A.AdapterArtifactDigest != v2B.AdapterArtifactDigest &&
               v2A.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion,
            "V2 adapter identity is stable across incidental build bytes while both exact artifact digests remain truthful.");
        var v2ChangedMapping = new GameKnowledgeAdapterDescriptor(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            v2ArtifactA,
            adapter.Descriptor.AdapterContractVersion,
            "fixture.mapping.semantic-change.v2",
            adapter.Descriptor.SupportedGameIds,
            adapter.Descriptor.SupportedFormats,
            adapter.Descriptor.ResourceLimits,
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
        var v2ChangedLimits = new GameKnowledgeAdapterDescriptor(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            v2ArtifactA,
            adapter.Descriptor.AdapterContractVersion,
            adapter.Descriptor.MappingRulesVersion,
            adapter.Descriptor.SupportedGameIds,
            adapter.Descriptor.SupportedFormats,
            new KnowledgeAdapterResourceLimits(
                adapter.Descriptor.ResourceLimits.MaximumArtifactBytes + 1,
                adapter.Descriptor.ResourceLimits.MaximumArtifacts,
                adapter.Descriptor.ResourceLimits.MaximumKnowledgeRecords,
                adapter.Descriptor.ResourceLimits.MaximumRelationships),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
        Assert(v2ChangedMapping.RevisionId != v2A.RevisionId && v2ChangedLimits.RevisionId != v2A.RevisionId,
            "V2 mapping-rule and declared resource-contract changes alter adapter identity.");
        Assert(adapter.Descriptor.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.LegacyAlgorithmVersion &&
               adapter.Descriptor.RevisionId != v2A.RevisionId &&
               adapter.Descriptor.Revision.SemanticContractDigest is null &&
               v2A.Revision.SemanticContractDigest is not null,
            "Historical v1 and additive v2 adapter coordinates remain explicitly distinct and cannot collide.");

        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Passed,
            "grid.conformance.policy",
            "1",
            ContentDigest.ComputeSha256("fixture-validation-result"u8));
        var package = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(ConformanceFixtureAdapter.GameId, gameVersion, [artifactId]),
            null,
            [],
            "grid.composition.v1",
            payloadA,
            validation,
            new CatalogBuildProvenance("grid.conformance", "1", "fixture-commit"));
        var verification = CanonicalCatalogPackageKernel.Verify(package);

        var acquisitionMember = new SourceAcquisitionMember(
            artifact.SourceCoordinate,
            artifact.ExactBytes.Length,
            artifact.Digest,
            artifact.Id);
        var acquisitionMethod = new AcquisitionMethodCoordinate(
            "grid.test.acquisition",
            "1",
            "grid.test.tool",
            "1",
            ContentDigest.ComputeSha256("fixture-tool"u8));
        var applicationIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.distribution",
            "application-id",
            "fixture-app");
        var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.container",
            "container-coordinate",
            "fixture.rpf");
        var containerDigest = ContentDigest.ComputeSha256("fixture-container"u8);
        var acquisitionReceiptId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ConformanceFixtureAdapter.GameId,
            applicationIdentity,
            gameVersion,
            containerCoordinate,
            17,
            containerDigest,
            acquisitionMethod,
            [acquisitionMember]);
        var acquisitionReceipt = new SourceAcquisitionReceipt(
            acquisitionReceiptId,
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ConformanceFixtureAdapter.GameId,
            applicationIdentity,
            gameVersion,
            containerCoordinate,
            17,
            containerDigest,
            acquisitionMethod,
            [acquisitionMember]);
        var acquisitionBinding = new SourceArtifactAcquisitionBinding(
            artifact.Id,
            acquisitionReceipt.Id,
            artifact.SourceCoordinate,
            artifact.ExactBytes.Length,
            artifact.Digest);
        var payloadV3 = WithAcquisition(payloadA, [acquisitionReceipt], [acquisitionBinding]);
        var buildProvenanceV2 = new CatalogBuildProvenance(
            CatalogBuildProvenance.CurrentSchemaVersion,
            "grid.conformance",
            "1",
            new string('a', 40),
            [new CatalogCommittedBuildInput("src/Grid.Core/Grid.Core.csproj", new string('b', 40))]);
        var adapterBuildReceipt = CreateAdapterBuildReceipt(adapter.Descriptor.AdapterArtifactDigest);
        var reviewedBuildProvenance = buildProvenanceV2.WithAdapterBuildReceipts([adapterBuildReceipt]);
        var reviewedBuildJson = System.Text.Json.JsonSerializer.Serialize(reviewedBuildProvenance);
        var reviewedBuildReloaded = System.Text.Json.JsonSerializer.Deserialize<CatalogBuildProvenance>(reviewedBuildJson);
        Assert(reviewedBuildReloaded is not null &&
               reviewedBuildReloaded.ProvenanceSchemaVersion == CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion &&
               reviewedBuildReloaded.AdapterBuildReceipts.SequenceEqual(reviewedBuildProvenance.AdapterBuildReceipts) &&
               reviewedBuildReloaded.AdapterBuildReceipts.Single().AdapterArtifactDigest ==
                   adapter.Descriptor.AdapterArtifactDigest,
            "Reviewed build provenance round-trips exact adapter DLL, MVID, compiler/SDK, PDB, and explicit SourceLink coordinates.");
        AssertThrows<ArgumentException>(() => _ = new CatalogAdapterBuildProvenanceReceipt(
                adapterBuildReceipt.ReceiptSchemaVersion,
                adapterBuildReceipt.AdapterArtifactDigest,
                adapterBuildReceipt.AssemblyName,
                adapterBuildReceipt.ExactAssemblyVersion,
                adapterBuildReceipt.AssemblyInformationalVersion,
                adapterBuildReceipt.ModuleVersionId,
                adapterBuildReceipt.TargetFramework,
                adapterBuildReceipt.BuildConfiguration,
                adapterBuildReceipt.BuildPlatform,
                adapterBuildReceipt.CompilerId,
                adapterBuildReceipt.ExactCompilerVersion,
                adapterBuildReceipt.SdkId,
                adapterBuildReceipt.ExactSdkVersion,
                adapterBuildReceipt.DeterministicBuild,
                adapterBuildReceipt.PortablePdbDigest,
                CatalogBuildMetadataPresence.Absent,
                ContentDigest.ComputeSha256("unexpected-sourcelink"u8),
                adapterBuildReceipt.CompilationOptionsContentDigest,
                adapterBuildReceipt.CompilationReferencesContentDigest),
            "SourceLink absence cannot conceal a supplied metadata digest.");
        Assert(v2A.RevisionId == new GameKnowledgeAdapterDescriptor(
                   v2A.AdapterId,
                   v2A.ExactAdapterVersion,
                   ContentDigest.ComputeSha256("different-reviewed-dll"u8),
                   v2A.AdapterContractVersion,
                   v2A.MappingRulesVersion,
                   v2A.SupportedGameIds,
                   v2A.SupportedFormats,
                   v2A.ResourceLimits,
                   KnowledgeAdapterRevisionId.CurrentAlgorithmVersion).RevisionId,
            "Reviewed build coordinates remain provenance-only and cannot churn semantic v2 adapter identity.");
        var packageV3 = CanonicalCatalogPackageKernel.CreateV3(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(ConformanceFixtureAdapter.GameId, gameVersion, [artifactId]),
            null,
            [],
            "grid.composition.v1",
            payloadV3,
            validation,
            buildProvenanceV2);
        Assert(CanonicalCatalogPackageKernel.Verify(packageV3).IsStructurallyValid &&
               packageV3.Manifest.PackageSchemaVersion == 3 &&
               packageV3.Manifest.PayloadDigest.Value.StartsWith("grid.catalog-payload.v3.sha256.", StringComparison.Ordinal) &&
               packageV3.Manifest.CatalogRevisionId.Value.StartsWith("grid.catalog-revision.v3.sha256.", StringComparison.Ordinal) &&
               packageV3.Id.Value.StartsWith("grid.catalog-package.v3.sha256.", StringComparison.Ordinal),
            "Schema-v3 packages bind acquisition and committed-build provenance with explicit v3 identities.");
        var packageV3Json = System.Text.Json.JsonSerializer.Serialize(packageV3);
        var reloadedPackageV3 = System.Text.Json.JsonSerializer.Deserialize<CanonicalCatalogPackage>(packageV3Json);
        Assert(reloadedPackageV3 is not null &&
               reloadedPackageV3.Id == packageV3.Id &&
               CanonicalCatalogPackageKernel.Verify(reloadedPackageV3).IsStructurallyValid,
            "Schema-v3 acquisition and build provenance round-trip without reinterpretation.");
        var wrongCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "grid.test.member",
            "member-coordinate",
            "wrong/member.meta");
        var invalidBindingPayload = WithAcquisition(
            payloadA,
            [acquisitionReceipt],
            [new SourceArtifactAcquisitionBinding(
                artifact.Id,
                acquisitionReceipt.Id,
                wrongCoordinate,
                artifact.ExactBytes.Length,
                artifact.Digest)]);
        Assert(!CanonicalCatalogPackageKernel.Verify(packageV3 with { Payload = invalidBindingPayload }).IsStructurallyValid,
            "Schema-v3 verification rejects an artifact binding to the wrong acquired member coordinate.");
        AssertThrows<ArgumentException>(() => _ = new SourceAcquisitionReceipt(
                acquisitionReceipt.Id,
                acquisitionReceipt.ReceiptSchemaVersion,
                acquisitionReceipt.GameId,
                acquisitionReceipt.DistributionApplicationIdentity,
                acquisitionReceipt.DistributionBuildVersion,
                acquisitionReceipt.ContainerCoordinate,
                acquisitionReceipt.ContainerByteLength,
                ContentDigest.ComputeSha256("false-container"u8),
                acquisitionReceipt.AcquisitionMethod,
                acquisitionReceipt.Members),
            "Acquisition receipts reject a substituted container digest under the original receipt identity.");
        var differentContainerDigest = ContentDigest.ComputeSha256("different-container"u8);
        var differentReceiptId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ConformanceFixtureAdapter.GameId,
            applicationIdentity,
            gameVersion,
            containerCoordinate,
            17,
            differentContainerDigest,
            acquisitionMethod,
            [acquisitionMember]);
        var differentReceipt = new SourceAcquisitionReceipt(
            differentReceiptId,
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ConformanceFixtureAdapter.GameId,
            applicationIdentity,
            gameVersion,
            containerCoordinate,
            17,
            differentContainerDigest,
            acquisitionMethod,
            [acquisitionMember]);
        var differentAcquisitionPayload = WithAcquisition(
            payloadA,
            [differentReceipt],
            [new SourceArtifactAcquisitionBinding(
                artifact.Id,
                differentReceipt.Id,
                artifact.SourceCoordinate,
                artifact.ExactBytes.Length,
                artifact.Digest)]);
        Assert(acquisitionReceiptId == SourceAcquisitionReceiptId.DeriveV1(
                   SourceAcquisitionReceipt.CurrentSchemaVersion,
                   ConformanceFixtureAdapter.GameId,
                   applicationIdentity,
                   gameVersion,
                   containerCoordinate,
                   17,
                   containerDigest,
                   acquisitionMethod,
                   [acquisitionMember]) &&
               acquisitionReceiptId != differentReceiptId &&
               CanonicalCatalogPackageKernel.ComputePayloadDigestV3(payloadV3) !=
               CanonicalCatalogPackageKernel.ComputePayloadDigestV3(differentAcquisitionPayload),
            "Acquisition receipt identity is deterministic and container-digest changes alter v3 semantic identity.");
        AssertThrows<ArgumentException>(() => _ = new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.conformance",
                "1",
                "caller-supplied",
                [new CatalogCommittedBuildInput("src/Grid.Core/Grid.Core.csproj", new string('b', 40))]),
            "Schema-v3 build provenance rejects arbitrary non-commit caller text.");
        Assert(adapter.Descriptor.RevisionId.Value ==
               "grid.knowledge-adapter-revision.v1.sha256.7dfadfc6853d8550f798ec14791f8ada6555e212b5adb7e2dad901cdef55a4fb",
            "Knowledge adapter revision v1 matches its locked golden vector.");
        Assert(sourceRevision.Revision.Id.Value ==
               "grid.catalog-source-revision.v2.sha256.347d63a1c8bab1e57e179cd6707170b585ef622c5cb3c0d09dc2f5074095c2f3",
            "Adapter-bound source revision v2 matches its locked golden vector.");
        const string legacyPayloadVector =
            "grid.catalog-payload.v1.sha256.2bc13869d5a7e96b3678631fa62d51036fdbae4e94e24996cf8e91faafe0dfd1";
        const string legacyCatalogRevisionVector =
            "grid.catalog-revision.v1.sha256.fd128b2d1ff22de1d9497ed9c83dcbdc8865f3c42e34781bb52b87e282dd8fb2";
        const string legacyPackageVector =
            "grid.catalog-package.v1.sha256.b9fbed885f78914c04cabc6e0bf9af370bb3c5de6defb3a98bb7dcb40741fa87";
        Assert(new CatalogPayloadDigest(legacyPayloadVector).Value == legacyPayloadVector &&
               new CatalogPackageId(legacyPackageVector).Value == legacyPackageVector,
            "Legacy Slice 4A payload and package identity vectors remain readable without reinterpretation.");
        Assert(CatalogRevisionId.DeriveV1(
                   CatalogPackageKind.BaseGameCatalog,
                   package.Manifest.GameScope,
                   null,
                   [adapter.Descriptor.RevisionId],
                   [sourceRevision.Revision.Id],
                   [],
                   "grid.composition.v1",
                   new CatalogPayloadDigest(legacyPayloadVector)).Value == legacyCatalogRevisionVector,
            "The legacy catalog-revision v1 derivation remains byte-for-byte unchanged.");
        Assert(package.Manifest.PayloadDigest.Value.StartsWith("grid.catalog-payload.v2.sha256.", StringComparison.Ordinal) &&
               package.Manifest.CatalogRevisionId.Value.StartsWith("grid.catalog-revision.v2.sha256.", StringComparison.Ordinal) &&
               package.Id.Value.StartsWith("grid.catalog-package.v2.sha256.", StringComparison.Ordinal),
            "Scope, coverage, adapter-coordinate, and format-bound package identities use explicit v2 algorithms.");
        Assert(package.Manifest.PayloadDigest.Value ==
               "grid.catalog-payload.v2.sha256.21b2dcfbc558a2a044ba909b2224fde2cd82ade2dbcd7ab5600758678bb02bbb" &&
               package.Manifest.CatalogRevisionId.Value ==
               "grid.catalog-revision.v2.sha256.f82070fc69c83e70ced0b100c856a707d8ead3407f308392f61bf14d65754949" &&
               package.Id.Value ==
               "grid.catalog-package.v2.sha256.863b0d793262f91043c478c62589ef395e6318d74483268df38f674908cb8777",
            "The corrected v2 payload, catalog, and package identities match their locked golden vectors.");
        Assert(verification.IsStructurallyValid, "A closed deterministic base-game package verifies successfully.");
        Assert(package.Manifest.PayloadDigest == CanonicalCatalogPackageKernel.ComputePayloadDigest(payloadA) &&
               package.Manifest.SourceRevisionIds.SequenceEqual([sourceRevision.Revision.Id]) &&
               package.Manifest.AdapterRevisionIds.SequenceEqual([adapter.Descriptor.RevisionId]),
            "The package manifest pins exact payload, source-revision, and adapter-revision identities.");

        var reorderedPayload = ReversePayload(payloadA);
        Assert(CanonicalCatalogPackageKernel.ComputePayloadDigest(reorderedPayload) == package.Manifest.PayloadDigest,
            "Canonical payload identity is invariant to input collection enumeration order.");

        var laterObservationPayload = WithObservationTime(payloadA, DateTimeOffset.UnixEpoch.AddYears(25));
        Assert(CanonicalCatalogPackageKernel.ComputePayloadDigest(laterObservationPayload) == package.Manifest.PayloadDigest,
            "Wall-clock evidence observation time does not alter the semantic catalog identity.");

        var changedTerm = payloadA.TerminologyAssertions.Single() with { };
        changedTerm = new TerminologyAssertion(
            changedTerm.KnowledgeRecordId,
            changedTerm.SourceRevisionId,
            changedTerm.Role,
            changedTerm.VerbatimValue + "!",
            changedTerm.SourceFieldPath,
            changedTerm.LanguageTag,
            changedTerm.NativeStringIdentifier);
        var alteredPayload = CopyPayload(payloadA, terminology: [changedTerm]);
        Assert(CanonicalCatalogPackageKernel.ComputePayloadDigest(alteredPayload) != package.Manifest.PayloadDigest,
            "Changing verbatim assertion content changes the canonical payload identity.");
        Assert(!CanonicalCatalogPackageKernel.Verify(package with { Payload = alteredPayload }).IsStructurallyValid,
            "Package verification rejects payload tampering and stale exact-claim evidence.");

        var differentManifest = new CatalogPackageManifest(
            package.Manifest.PackageSchemaVersion,
            package.Manifest.CatalogRevisionId,
            package.Manifest.PackageKind,
            package.Manifest.GameScope,
            package.Manifest.ModScope,
            package.Manifest.EffectiveCoverage,
            package.Manifest.AdapterRevisions,
            package.Manifest.SourceRevisionIds,
            package.Manifest.RequiredBasePackageIds,
            package.Manifest.CompositionPolicyVersion + ".changed",
            package.Manifest.PayloadDigest,
            package.Manifest.ValidationStatus,
            package.Manifest.ValidationPolicyId,
            package.Manifest.ValidationPolicyVersion,
            package.Manifest.ValidationResultDigest,
            package.Manifest.BuildProvenance);
        Assert(!CanonicalCatalogPackageKernel.Verify(package with { Manifest = differentManifest }).IsStructurallyValid,
            "Package verification rejects a manifest whose catalog and package identities were not recomputed.");

        var registration = extractionA.CanonicalRegistrations.Single().Registration;
        Assert(registration.TerminologyAssertions.Single().VerbatimValue == "  Café—North  " &&
               registration.KnowledgeRecords.Length == 2 &&
               extractionA.UnresolvedSourceAssertions.Single().CandidateKind is null,
            "Conformance extraction preserves exact source terminology and leaves unclassified native data unresolved.");
        Assert(registration.EvidenceBindings.Any(value => value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity) &&
               registration.EvidenceBindings.Any(value => value.ClaimKind == EvidenceClaimKind.Terminology) &&
               registration.EvidenceBindings.Any(value => value.ClaimKind == EvidenceClaimKind.Relationship),
            "The fixture independently binds identity, terminology, and relationship claims.");
        Assert(registration.TerminologyAssertions.All(assertion => registration.EvidenceBindings.Any(binding =>
                   binding.ClaimKind == EvidenceClaimKind.Terminology &&
                   binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                   binding.ClaimLocator == assertion.SourceFieldPath)) &&
               registration.RelationshipAssertions.All(assertion => registration.EvidenceBindings.Any(binding =>
                   binding.ClaimKind == EvidenceClaimKind.Relationship &&
                   binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion) &&
                   binding.ClaimLocator == assertion.SourceFieldPath)),
            "All source assertions carry exact claim-level FILE_VERIFIED evidence.");

        var unsupportedBytes = Encoding.UTF8.GetBytes("unsupported").ToImmutableArray();
        var unsupportedDigest = ContentDigest.ComputeSha256(unsupportedBytes.AsSpan());
        var unsupportedArtifact = new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(unsupportedDigest),
            unsupportedDigest,
            artifact.SourceCoordinate,
            ConformanceFixtureAdapter.Format,
            unsupportedBytes);
        var unsupported = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            null,
            null,
            adapter.Descriptor,
            [unsupportedArtifact]));
        Assert(unsupported.CoverageState == KnowledgeCoverageState.Unsupported &&
               unsupported.CanonicalRegistrations.IsEmpty &&
               unsupported.CorrelationCandidates.IsEmpty &&
               unsupported.UnresolvedSourceAssertions.IsEmpty,
            "Unsupported formats fail closed without emitting canonical knowledge.");

        AssertThrows<ArgumentException>(() => _ = new SourceDiscoveryResult(
                discoveryA.SourceCandidates,
                [],
                KnowledgeCoverageState.Unsupported,
                [new KnowledgeBuildIssue("fixture.unsupported", "Diagnostic only.")]),
            "Unsupported discovery cannot expose source candidates suitable for extraction.");
        AssertThrows<ArgumentException>(() => _ = new KnowledgeExtractionResult(
                extractionA.CanonicalRegistrations,
                extractionA.CorrelationCandidates,
                extractionA.UnresolvedSourceAssertions,
                KnowledgeCoverageState.Unsupported,
                [new KnowledgeBuildIssue("fixture.unsupported", "Diagnostic only.")]),
            "Unsupported extraction cannot expose registrations, correlations, or unresolved canonical assertions.");

        var completePayload = CopyPayload(payloadA, coverage: KnowledgeCoverageState.Complete);
        var completePackage = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.BaseGameCatalog,
            package.Manifest.GameScope,
            null,
            [],
            package.Manifest.CompositionPolicyVersion,
            completePayload,
            validation,
            package.Manifest.BuildProvenance);
        Assert(package.Manifest.EffectiveCoverage == KnowledgeCoverageState.Partial &&
               completePackage.Manifest.EffectiveCoverage == KnowledgeCoverageState.Complete &&
               package.Manifest.PayloadDigest != completePackage.Manifest.PayloadDigest &&
               package.Manifest.CatalogRevisionId != completePackage.Manifest.CatalogRevisionId &&
               package.Id != completePackage.Id,
            "Partial and complete coverage are explicit and cannot share payload, catalog, or package identity.");
        var falseCoverageManifest = new CatalogPackageManifest(
            package.Manifest.PackageSchemaVersion,
            package.Manifest.CatalogRevisionId,
            package.Manifest.PackageKind,
            package.Manifest.GameScope,
            package.Manifest.ModScope,
            KnowledgeCoverageState.Complete,
            package.Manifest.AdapterRevisions,
            package.Manifest.SourceRevisionIds,
            package.Manifest.RequiredBasePackageIds,
            package.Manifest.CompositionPolicyVersion,
            package.Manifest.PayloadDigest,
            package.Manifest.ValidationStatus,
            package.Manifest.ValidationPolicyId,
            package.Manifest.ValidationPolicyVersion,
            package.Manifest.ValidationResultDigest,
            package.Manifest.BuildProvenance);
        Assert(!CanonicalCatalogPackageKernel.Verify(package with { Manifest = falseCoverageManifest }).IsStructurallyValid,
            "Verification rejects a manifest that relabels partial payload coverage as complete.");

        var rejectedValidation = new CatalogValidationSummary(
            CatalogValidationStatus.Rejected,
            validation.PolicyId,
            validation.ExactPolicyVersion,
            ContentDigest.ComputeSha256("fixture-rejected-validation-result"u8));
        var rejectedPackage = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.BaseGameCatalog,
            package.Manifest.GameScope,
            null,
            [],
            package.Manifest.CompositionPolicyVersion,
            payloadA,
            rejectedValidation,
            package.Manifest.BuildProvenance);
        var rejectedVerification = CanonicalCatalogPackageKernel.Verify(rejectedPackage);
        Assert(rejectedVerification.IsStructurallyValid &&
               rejectedPackage.Manifest.ValidationStatus == CatalogValidationStatus.Rejected &&
               typeof(CatalogPackageVerificationResult).GetProperty("IsPublishable") is null &&
               typeof(CatalogPackageVerificationResult).GetProperty("IsApproved") is null,
            "Structural validity does not grant publication/QCS approval to a Rejected package.");

        var modAIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.conformance.mod",
            "provider-mod-id",
            "mod-A");
        var modBIdentity = SourceNativeIdentifier.FromExactUtf8(
            "grid.conformance.mod",
            "provider-mod-id",
            "mod-B");
        var modVersion1 = SourceNativeVersion.FromExactUtf8("grid.conformance.mod-version", "1-exact");
        var modVersion2 = SourceNativeVersion.FromExactUtf8("grid.conformance.mod-version", "2-exact");
        var modAExtraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            modAIdentity,
            modVersion1,
            adapter.Descriptor,
            [artifact]));
        var modAPayload = ToPayload(modAExtraction);
        var modAPackage = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.ModCatalogExtension,
            package.Manifest.GameScope,
            new CatalogModScope(modAIdentity, modVersion1, [artifactId]),
            [package.Id],
            package.Manifest.CompositionPolicyVersion,
            modAPayload,
            validation,
            package.Manifest.BuildProvenance);
        Assert(modAPackage.Payload.SourceRevisions.All(value =>
                   value.SourceScope.ScopeKind == KnowledgeSourceScopeKind.ModExtension &&
                   value.SourceScope.ExactModIdentity == modAIdentity &&
                   value.SourceScope.ExactModVersion == modVersion1) &&
               CanonicalCatalogPackageKernel.Verify(modAPackage).IsStructurallyValid,
            "A mod package is structurally valid only with its exact immutable mod scope binding.");
        AssertThrows<InvalidDataException>(() => _ = CanonicalCatalogPackageKernel.CreateV2(
                CatalogPackageKind.ModCatalogExtension,
                package.Manifest.GameScope,
                new CatalogModScope(modAIdentity, modVersion1, [artifactId]),
                [package.Id],
                package.Manifest.CompositionPolicyVersion,
                payloadA,
                validation,
                package.Manifest.BuildProvenance),
            "A base-bound payload cannot be relabeled as a mod extension.");
        AssertThrows<InvalidDataException>(() => _ = CanonicalCatalogPackageKernel.CreateV2(
                CatalogPackageKind.ModCatalogExtension,
                package.Manifest.GameScope,
                new CatalogModScope(modBIdentity, modVersion1, [artifactId]),
                [package.Id],
                package.Manifest.CompositionPolicyVersion,
                modAPayload,
                validation,
                package.Manifest.BuildProvenance),
            "Mod-A content cannot verify under a mod-B manifest.");

        var modBPayload = ToPayload(await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            modBIdentity,
            modVersion1,
            adapter.Descriptor,
            [artifact])));
        var modBPackage = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.ModCatalogExtension,
            package.Manifest.GameScope,
            new CatalogModScope(modBIdentity, modVersion1, [artifactId]),
            [package.Id],
            package.Manifest.CompositionPolicyVersion,
            modBPayload,
            validation,
            package.Manifest.BuildProvenance);
        var modVersion2Payload = ToPayload(await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ConformanceFixtureAdapter.GameId,
            gameVersion,
            modAIdentity,
            modVersion2,
            adapter.Descriptor,
            [artifact])));
        var modVersion2Package = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.ModCatalogExtension,
            package.Manifest.GameScope,
            new CatalogModScope(modAIdentity, modVersion2, [artifactId]),
            [package.Id],
            package.Manifest.CompositionPolicyVersion,
            modVersion2Payload,
            validation,
            package.Manifest.BuildProvenance);
        Assert(modAPackage.Manifest.PayloadDigest != modBPackage.Manifest.PayloadDigest &&
               modAPackage.Manifest.CatalogRevisionId != modBPackage.Manifest.CatalogRevisionId &&
               modAPackage.Id != modBPackage.Id,
            "Changing exact mod identity changes payload, catalog, and package identity.");
        Assert(modAPackage.Manifest.PayloadDigest != modVersion2Package.Manifest.PayloadDigest &&
               modAPackage.Manifest.CatalogRevisionId != modVersion2Package.Manifest.CatalogRevisionId &&
               modAPackage.Id != modVersion2Package.Id,
            "Changing exact mod version changes payload, catalog, and package identity.");

        var orphanFileReceipt = CreateFileReceipt(
            sourceRevision.Revision.Id,
            artifact,
            "fixture-node[orphan-file]",
            "fixture-node[orphan-file]/unused");
        var orphanFilePayload = CopyPayload(
            payloadA,
            fileEvidence: payloadA.FileEvidenceReceipts.Add(orphanFileReceipt));
        AssertThrows<InvalidDataException>(() => _ = CanonicalCatalogPackageKernel.CreateV2(
                CatalogPackageKind.BaseGameCatalog,
                package.Manifest.GameScope,
                null,
                [],
                package.Manifest.CompositionPolicyVersion,
                orphanFilePayload,
                validation,
                package.Manifest.BuildProvenance),
            "An orphan FILE_VERIFIED receipt cannot enter a structurally valid package.");

        var orphanReference = new ReferenceEvidenceReceipt(
            payloadA.Sources.Single().Id,
            sourceRevision.Revision.Id,
            artifact.Id,
            artifact.Digest,
            SourceNativeIdentifier.FromExactUtf8("grid.conformance.reference", "object", "orphan-reference"),
            null,
            "response/orphan-reference/unused",
            DateTimeOffset.UnixEpoch);
        var orphanReferencePayload = CopyPayload(
            payloadA,
            referenceEvidence: payloadA.ReferenceEvidenceReceipts.Add(
                new CatalogReferenceEvidenceReceipt(EvidenceReceiptId.DeriveV1(orphanReference), orphanReference)));
        AssertThrows<InvalidDataException>(() => _ = CanonicalCatalogPackageKernel.CreateV2(
                CatalogPackageKind.BaseGameCatalog,
                package.Manifest.GameScope,
                null,
                [],
                package.Manifest.CompositionPolicyVersion,
                orphanReferencePayload,
                validation,
                package.Manifest.BuildProvenance),
            "An orphan REFERENCE_VERIFIED receipt cannot enter a structurally valid package.");

        var changedDescriptors = new[]
        {
            MutateDescriptor(adapter.Descriptor, exactAdapterVersion: "1.1-exact"),
            MutateDescriptor(adapter.Descriptor, adapterArtifactDigest: ContentDigest.ComputeSha256("changed-adapter"u8)),
            MutateDescriptor(adapter.Descriptor, adapterContractVersion: 2),
            MutateDescriptor(adapter.Descriptor, mappingRulesVersion: "fixture.mapping.v2"),
        };
        Assert(changedDescriptors.All(value => value.RevisionId != adapter.Descriptor.RevisionId),
            "Every adapter version/artifact/contract/mapping coordinate mutation produces a different revision ID.");
        foreach (var changedDescriptor in changedDescriptors)
        {
            var tamperedAdapterPayload = CopyPayload(payloadA, adapterDescriptors: [changedDescriptor]);
            Assert(!CanonicalCatalogPackageKernel.Verify(package with { Payload = tamperedAdapterPayload }).IsStructurallyValid,
                "Package verification rejects adapter-coordinate mutation against dependent source revisions.");
            AssertThrows<InvalidDataException>(() => _ = CanonicalCatalogPackageKernel.CreateV2(
                    CatalogPackageKind.BaseGameCatalog,
                    package.Manifest.GameScope,
                    null,
                    [],
                    package.Manifest.CompositionPolicyVersion,
                    tamperedAdapterPayload,
                    validation,
                    package.Manifest.BuildProvenance),
                "Recomputed packaging rejects an adapter-coordinate mutation with stale dependent source revisions.");
        }
        AssertThrows<ArgumentException>(() => _ = new KnowledgeAdapterRevisionCoordinate(
                adapter.Descriptor.RevisionId,
                adapter.Descriptor.AdapterId,
                "tampered-version",
                adapter.Descriptor.AdapterArtifactDigest,
                adapter.Descriptor.AdapterContractVersion,
                adapter.Descriptor.MappingRulesVersion),
            "An opaque adapter revision ID cannot be paired with tampered exact coordinates.");

        Assert(discoveryA.SourceCandidates.Single().ArtifactFormats.Single().Format == ConformanceFixtureAdapter.Format &&
               payloadA.SourceRevisions.Single().ArtifactFormats.Single().Format == ConformanceFixtureAdapter.Format,
            "The exact declared format ID/version tuple survives discovery, extraction, and package provenance.");
        AssertThrows<ArgumentException>(() => _ = new KnowledgeFormatCoordinate(ConformanceFixtureAdapter.FormatId, ""),
            "Missing exact format version is rejected.");
        AssertThrows<ArgumentException>(() => _ = new PreproductionKnowledgeExtractionRequest(
                ConformanceFixtureAdapter.GameId,
                gameVersion,
                modAIdentity,
                null,
                adapter.Descriptor,
                [artifact]),
            "A mod extraction scope cannot omit its exact mod version.");
        var mismatchedFormatArtifact = new FrozenSourceArtifact(
            artifact.Id,
            artifact.Digest,
            artifact.SourceCoordinate,
            new KnowledgeFormatCoordinate(ConformanceFixtureAdapter.FormatId, "2"),
            artifact.ExactBytes);
        AssertThrows<ArgumentException>(() => _ = new PreproductionKnowledgeExtractionRequest(
                ConformanceFixtureAdapter.GameId,
                gameVersion,
                null,
                null,
                adapter.Descriptor,
                [mismatchedFormatArtifact]),
            "An undeclared exact format-version tuple cannot enter extraction.");
        var dualVersionDescriptor = new GameKnowledgeAdapterDescriptor(
            adapter.Descriptor.AdapterId,
            adapter.Descriptor.ExactAdapterVersion,
            adapter.Descriptor.AdapterArtifactDigest,
            adapter.Descriptor.AdapterContractVersion,
            adapter.Descriptor.MappingRulesVersion,
            adapter.Descriptor.SupportedGameIds,
            [
                adapter.Descriptor.SupportedFormats.Single(),
                new SupportedKnowledgeFormat(
                    ConformanceFixtureAdapter.FormatId,
                    "2",
                    ["frozen-bytes"],
                    ["fixture-node"],
                    [KnowledgeKind.Location],
                    true,
                    true,
                    true),
            ],
            adapter.Descriptor.ResourceLimits);
        AssertThrows<ArgumentException>(() => _ = new PreproductionKnowledgeExtractionRequest(
                ConformanceFixtureAdapter.GameId,
                gameVersion,
                null,
                null,
                dualVersionDescriptor,
                [artifact, mismatchedFormatArtifact]),
            "The same artifact cannot enter extraction under ambiguous format-version declarations.");

        AssertThrows<ArgumentException>(() => _ = new FrozenSourceArtifact(
                artifactId,
                digest,
                artifact.SourceCoordinate,
                artifact.DeclaredFormat,
                Encoding.UTF8.GetBytes("tampered").ToImmutableArray()),
            "Frozen artifact contracts reject bytes that do not match their exact digest.");
        AssertThrows<ArgumentException>(() => _ = new CatalogGameScope(
                ConformanceFixtureAdapter.GameId,
                gameVersion,
                [default]),
            "Package scopes reject default canonical artifact identities.");
        AssertThrows<ArgumentException>(() => _ = new CatalogArchiveDigest(default),
            "Archive digest contracts reject an uninitialized content digest.");
        AssertThrows<EncoderFallbackException>(() => _ = new KnowledgeAdapterId("adapter.\uD800"),
            "Adapter identity text rejects malformed UTF-16.");
        AssertThrows<ArgumentException>(() => _ = new CatalogPackageManifest(
                CatalogPackageManifest.CurrentSchemaVersion,
                package.Manifest.CatalogRevisionId,
                CatalogPackageKind.ModCatalogExtension,
                package.Manifest.GameScope,
                null,
                package.Manifest.EffectiveCoverage,
                package.Manifest.AdapterRevisions,
                package.Manifest.SourceRevisionIds,
                [],
                package.Manifest.CompositionPolicyVersion,
                package.Manifest.PayloadDigest,
                package.Manifest.ValidationStatus,
                package.Manifest.ValidationPolicyId,
                package.Manifest.ValidationPolicyVersion,
                package.Manifest.ValidationResultDigest,
                package.Manifest.BuildProvenance),
            "A mod extension cannot omit its exact mod scope and required base package identity.");

        var adapterApiTypes = typeof(IGameKnowledgeAdapter).GetMembers()
            .SelectMany(member => member switch
            {
                System.Reflection.MethodInfo method => method.GetParameters().Select(value => value.ParameterType)
                    .Append(method.ReturnType),
                System.Reflection.PropertyInfo property => [property.PropertyType],
                _ => [],
            })
            .ToImmutableArray();
        Assert(!adapterApiTypes.Contains(typeof(ICanonicalKnowledgeCatalogStore)) &&
               typeof(FrozenSourceArtifact).GetProperties().All(value =>
                   !value.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)),
            "The adapter consumes frozen content and has no store or local-path authority.");
        var forbiddenScopeTypes = new[] { typeof(InstallationId), typeof(ProfileId), typeof(ModId) };
        var identityFactoryParameters = typeof(KnowledgeAdapterRevisionId).GetMethods()
            .Where(value => value.Name.StartsWith("Derive", StringComparison.Ordinal))
            .SelectMany(value => value.GetParameters())
            .Select(value => value.ParameterType)
            .Concat(typeof(CatalogSourceRevisionId).GetMethods()
                .Where(value => value.Name.StartsWith("Derive", StringComparison.Ordinal))
                .SelectMany(value => value.GetParameters())
                .Select(value => value.ParameterType));
        Assert(!identityFactoryParameters.Any(forbiddenScopeTypes.Contains),
            "Account/profile/install-local identity types cannot enter adapter or source-revision derivation.");

        return checks;
    }

    private static CanonicalCatalogPayload ToPayload(KnowledgeExtractionResult result)
    {
        var registrations = result.CanonicalRegistrations.Select(value => value.Registration).ToArray();
        return new CanonicalCatalogPayload(
            result.CoverageState,
            result.CanonicalRegistrations.Select(value => value.AdapterDescriptor)
                .DistinctBy(value => value.RevisionId).ToImmutableArray(),
            registrations.Select(value => value.Source).ToImmutableArray(),
            registrations.SelectMany(value => value.Artifacts).ToImmutableArray(),
            result.CanonicalRegistrations.Select(value => new AdapterBoundCatalogSourceRevisionRecord(
                value.Registration.SourceRevision,
                value.AdapterRevisionId,
                value.SourceScope,
                value.ArtifactFormats)).ToImmutableArray(),
            registrations.SelectMany(value => value.KnowledgeRecords).ToImmutableArray(),
            registrations.SelectMany(value => value.TerminologyAssertions).ToImmutableArray(),
            registrations.SelectMany(value => value.RelationshipAssertions).ToImmutableArray(),
            registrations.SelectMany(value => value.FileEvidenceReceipts).ToImmutableArray(),
            registrations.SelectMany(value => value.ReferenceEvidenceReceipts).ToImmutableArray(),
            registrations.SelectMany(value => value.EvidenceBindings).ToImmutableArray(),
            result.CorrelationCandidates,
            result.UnresolvedSourceAssertions);
    }

    private static CanonicalCatalogPayload ReversePayload(CanonicalCatalogPayload value) => new(
        value.EffectiveCoverage,
        value.AdapterDescriptors.Reverse().ToImmutableArray(),
        value.Sources.Reverse().ToImmutableArray(),
        value.Artifacts.Reverse().ToImmutableArray(),
        value.SourceRevisions.Reverse().ToImmutableArray(),
        value.KnowledgeRecords.Reverse().ToImmutableArray(),
        value.TerminologyAssertions.Reverse().ToImmutableArray(),
        value.RelationshipAssertions.Reverse().ToImmutableArray(),
        value.FileEvidenceReceipts.Reverse().ToImmutableArray(),
        value.ReferenceEvidenceReceipts.Reverse().ToImmutableArray(),
        value.EvidenceBindings.Reverse().ToImmutableArray(),
        value.CorrelationEnvelopes.Reverse().ToImmutableArray(),
        value.UnresolvedSourceAssertions.Reverse().ToImmutableArray());

    private static CanonicalCatalogPayload WithObservationTime(
        CanonicalCatalogPayload value,
        DateTimeOffset observedAtUtc) => CopyPayload(
            value,
            fileEvidence: value.FileEvidenceReceipts.Select(item =>
            {
                var receipt = item.Receipt;
                return new CatalogFileEvidenceReceipt(item.Id, new FileEvidenceReceipt(
                    receipt.SourceRevisionId,
                    receipt.SourceArtifactId,
                    receipt.ArtifactDigest,
                    receipt.ParserId,
                    receipt.ParserVersion,
                    receipt.NativeRecordLocator,
                    receipt.SourceFieldPath,
                    receipt.ByteOffset,
                    receipt.ByteLength,
                    receipt.InterpretedBytesDigest,
                    observedAtUtc));
            }).ToImmutableArray());

    private static CanonicalCatalogPayload CopyPayload(
        CanonicalCatalogPayload value,
        ImmutableArray<TerminologyAssertion>? terminology = null,
        ImmutableArray<CatalogFileEvidenceReceipt>? fileEvidence = null,
        ImmutableArray<CatalogReferenceEvidenceReceipt>? referenceEvidence = null,
        KnowledgeCoverageState? coverage = null,
        ImmutableArray<GameKnowledgeAdapterDescriptor>? adapterDescriptors = null,
        ImmutableArray<AdapterBoundCatalogSourceRevisionRecord>? sourceRevisions = null) => new(
            coverage ?? value.EffectiveCoverage,
            adapterDescriptors ?? value.AdapterDescriptors,
            value.Sources,
            value.Artifacts,
            sourceRevisions ?? value.SourceRevisions,
            value.KnowledgeRecords,
            terminology ?? value.TerminologyAssertions,
            value.RelationshipAssertions,
            fileEvidence ?? value.FileEvidenceReceipts,
            referenceEvidence ?? value.ReferenceEvidenceReceipts,
            value.EvidenceBindings,
            value.CorrelationEnvelopes,
            value.UnresolvedSourceAssertions);

    private static CatalogFileEvidenceReceipt CreateFileReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        string recordLocator,
        string fieldPath)
    {
        var receipt = new FileEvidenceReceipt(
            revisionId,
            artifact.Id,
            artifact.Digest,
            "grid.conformance.fixture-parser",
            "1",
            recordLocator,
            fieldPath,
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch);
        return new(EvidenceReceiptId.DeriveV1(receipt), receipt);
    }

    private static CatalogAdapterBuildProvenanceReceipt CreateAdapterBuildReceipt(ContentDigest artifactDigest) =>
        new(
            CatalogAdapterBuildProvenanceReceipt.CurrentSchemaVersion,
            artifactDigest,
            "Grid.Conformance.Adapter",
            "1.0.0.0",
            "1.0.0+fixture",
            "11111111-2222-3333-4444-555555555555",
            ".NETCoreApp,Version=v9.0",
            "Debug",
            "x64",
            "Microsoft.CodeAnalysis.CSharp",
            "4.14.0-fixture",
            "Microsoft.NET.Sdk",
            "9.0.301",
            true,
            ContentDigest.ComputeSha256("fixture-pdb"u8),
            CatalogBuildMetadataPresence.Absent,
            null,
            ContentDigest.ComputeSha256("fixture-compilation-options"u8),
            ContentDigest.ComputeSha256("fixture-compilation-references"u8));

    private static CanonicalCatalogPayload WithAcquisition(
        CanonicalCatalogPayload value,
        ImmutableArray<SourceAcquisitionReceipt> receipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> bindings) => new(
        value.EffectiveCoverage,
        value.AdapterDescriptors,
        value.Sources,
        value.Artifacts,
        value.SourceRevisions,
        value.KnowledgeRecords,
        value.TerminologyAssertions,
        value.RelationshipAssertions,
        value.FileEvidenceReceipts,
        value.ReferenceEvidenceReceipts,
        value.EvidenceBindings,
        value.CorrelationEnvelopes,
        value.UnresolvedSourceAssertions,
        receipts,
        bindings);

    private static GameKnowledgeAdapterDescriptor MutateDescriptor(
        GameKnowledgeAdapterDescriptor value,
        string? exactAdapterVersion = null,
        ContentDigest? adapterArtifactDigest = null,
        int? adapterContractVersion = null,
        string? mappingRulesVersion = null) => new(
            value.AdapterId,
            exactAdapterVersion ?? value.ExactAdapterVersion,
            adapterArtifactDigest ?? value.AdapterArtifactDigest,
            adapterContractVersion ?? value.AdapterContractVersion,
            mappingRulesVersion ?? value.MappingRulesVersion,
            value.SupportedGameIds,
            value.SupportedFormats,
            value.ResourceLimits);

    private sealed class ConformanceFixtureAdapter : IGameKnowledgeAdapter
    {
        public const string FormatId = "grid.conformance.fixture-kv";
        public const string FormatVersion = "1";
        public static readonly KnowledgeFormatCoordinate Format = new(FormatId, FormatVersion);
        public const string ExactFixtureContent = "id=child\nname=  Café—North  \nparent=parent\nid=parent\nunknown=opaque-42\n";
        public static readonly GameId GameId = new("game.conformance.fixture");

        public ConformanceFixtureAdapter()
        {
            Descriptor = new GameKnowledgeAdapterDescriptor(
                new KnowledgeAdapterId("grid.adapter.conformance-fixture"),
                "1.0-exact",
                ContentDigest.ComputeSha256("grid-conformance-fixture-adapter"u8),
                1,
                "fixture.mapping.v1",
                [GameId],
                [new SupportedKnowledgeFormat(
                    FormatId,
                    "1",
                    ["frozen-bytes"],
                    ["fixture-node"],
                    [KnowledgeKind.Location],
                    supportsTerminology: true,
                    supportsRelationships: true,
                    supportsHierarchy: true)],
                new KnowledgeAdapterResourceLimits(4096, 1, 8, 8));
        }

        public GameKnowledgeAdapterDescriptor Descriptor { get; }

        public Task<SourceDiscoveryResult> DiscoverAsync(
            PreproductionSourceDiscoveryRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.GameId != GameId || request.Artifacts.Any(value => value.DeclaredFormat != Format))
                return Task.FromResult(new SourceDiscoveryResult(
                    [],
                    request.Artifacts.Select(value => new UnsupportedKnowledgeArtifact(value.Id, "fixture.unsupported"))
                        .ToImmutableArray(),
                    KnowledgeCoverageState.Unsupported,
                    [new KnowledgeBuildIssue("fixture.unsupported", "The frozen artifact is outside the fixture declaration.")]));

            var source = Source(request.Artifacts.Single());
            return Task.FromResult(new SourceDiscoveryResult(
                [new DiscoveredKnowledgeSource(
                    source,
                    NativeRevision(request.Artifacts.Single()),
                    [request.Artifacts.Single().FormatBinding])],
                [],
                KnowledgeCoverageState.Partial,
                []));
        }

        public Task<KnowledgeExtractionResult> ExtractAsync(
            PreproductionKnowledgeExtractionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.AdapterRevisionId != Descriptor.RevisionId || request.GameId != GameId ||
                request.Artifacts.Length != 1 || request.Artifacts[0].DeclaredFormat != Format ||
                !request.Artifacts[0].ExactBytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(ExactFixtureContent)))
                return Task.FromResult(new KnowledgeExtractionResult(
                    [], [], [], KnowledgeCoverageState.Unsupported,
                    [new KnowledgeBuildIssue("fixture.unsupported", "Input is not the declared conformance fixture.")]));

            var artifact = request.Artifacts.Single();
            var source = Source(artifact);
            var revision = NativeRevision(artifact);
            var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, revision, [artifact.Id], Descriptor.RevisionId);
            var sourceRevision = new CatalogSourceRevisionRecord(revisionId, source.Id, revision, [artifact.Id]);
            var child = Record("child", request, revisionId);
            var parent = Record("parent", request, revisionId);
            var terminology = new TerminologyAssertion(
                child.Id,
                revisionId,
                TerminologyAssertionRole.PrimaryName,
                "  Café—North  ",
                "fixture-node[child]/name",
                "und");
            var relationship = new RelationshipAssertion(
                child.Id,
                revisionId,
                new RelationshipSemanticId("grid.relationship.parent"),
                "parent",
                "fixture-node[child]/parent",
                parent.NativeIdentity,
                parent.Id);

            var receipts = new[]
            {
                Receipt(revisionId, artifact, "fixture-node[child]", "fixture-node[child]/id"),
                Receipt(revisionId, artifact, "fixture-node[child]", terminology.SourceFieldPath),
                Receipt(revisionId, artifact, "fixture-node[child]", relationship.SourceFieldPath),
                Receipt(revisionId, artifact, "fixture-node[parent]", "fixture-node[parent]/id"),
                Receipt(revisionId, artifact, "fixture-node[opaque-42]", "fixture-node[opaque-42]/kind"),
            };
            var bindings = new[]
            {
                Binding(receipts[0], EvidenceClaimKind.KnowledgeIdentity, child.Id, revisionId, null),
                Binding(receipts[1], EvidenceClaimKind.Terminology, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(terminology)),
                Binding(receipts[2], EvidenceClaimKind.Relationship, child.Id, revisionId, EvidenceClaimContentId.DeriveV1(relationship)),
                Binding(receipts[3], EvidenceClaimKind.KnowledgeIdentity, parent.Id, revisionId, null),
            };
            var registration = new CanonicalCatalogRegistration(
                source,
                [new SourceArtifactRecord(artifact.Id, artifact.Digest)],
                sourceRevision,
                [child, parent],
                [terminology],
                [relationship],
                receipts.ToImmutableArray(),
                [],
                bindings.ToImmutableArray());
            var unresolvedNative = SourceNativeIdentifier.FromExactUtf8(
                "grid.conformance.fixture.record",
                "opaque",
                "opaque-42");
            var unresolvedEvidence = receipts[4].Id;
            var unresolvedId = UnresolvedSourceAssertionId.DeriveV1(
                GameId,
                revisionId,
                Descriptor.RevisionId,
                unresolvedNative,
                null,
                "fixture.unclassified",
                [unresolvedEvidence]);
            var unresolved = new UnresolvedSourceAssertion(
                unresolvedId,
                GameId,
                revisionId,
                Descriptor.RevisionId,
                unresolvedNative,
                null,
                "fixture.unclassified",
                [unresolvedEvidence]);
            var correlationRecord = new CorrelationRecord(
                [child.Id, parent.Id],
                "fixture.relationship-distinction",
                "1",
                CorrelationOutcome.Rejected);
            var correlationId = CorrelationRecordId.DeriveV1(correlationRecord, [receipts[0].Id, receipts[3].Id]);
            return Task.FromResult(new KnowledgeExtractionResult(
                [new AdapterBoundCanonicalCatalogRegistration(
                    registration,
                    Descriptor,
                    request.SourceScope,
                    [artifact.FormatBinding])],
                [new CanonicalCorrelationEnvelope(correlationId, correlationRecord, [receipts[0].Id, receipts[3].Id])],
                [unresolved],
                KnowledgeCoverageState.Partial,
                []));
        }

        private static CatalogSourceRecord Source(FrozenSourceArtifact artifact)
        {
            var native = SourceNativeIdentifier.FromExactUtf8(
                "grid.conformance.fixture.source",
                "frozen-source-set",
                $"{GameId.Value}:{artifact.DeclaredFormat.FormatId}");
            return new(
                CatalogSourceId.DeriveV1(KnowledgeSourceKind.FrozenRepositoryDataset, native),
                KnowledgeSourceKind.FrozenRepositoryDataset,
                native);
        }

        private static SourceNativeVersion NativeRevision(FrozenSourceArtifact artifact) =>
            SourceNativeVersion.FromExactUtf8("grid.conformance.fixture.source-revision", artifact.Digest.HexValue);

        private static CanonicalKnowledgeRecord Record(
            string nativeValue,
            PreproductionKnowledgeExtractionRequest request,
            CatalogSourceRevisionId revisionId)
        {
            var native = SourceNativeIdentifier.FromExactUtf8(
                "grid.conformance.fixture.record",
                "fixture-node",
                nativeValue);
            var nativeId = NativeRecordIdentityId.DeriveV1(GameId, native);
            var id = KnowledgeRecordId.DeriveV1(
                GameId,
                request.ExactGameVersion,
                request.ExactModVersion,
                revisionId,
                KnowledgeKind.Location,
                nativeId);
            return new(id, GameId, request.ExactGameVersion, request.ExactModVersion, revisionId,
                KnowledgeKind.Location, nativeId, native);
        }

        private static CatalogFileEvidenceReceipt Receipt(
            CatalogSourceRevisionId revisionId,
            FrozenSourceArtifact artifact,
            string recordLocator,
            string fieldPath)
        {
            var receipt = new FileEvidenceReceipt(
                revisionId,
                artifact.Id,
                artifact.Digest,
                "grid.conformance.fixture-parser",
                "1",
                recordLocator,
                fieldPath,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch);
            return new(EvidenceReceiptId.DeriveV1(receipt), receipt);
        }

        private static EvidenceBinding Binding(
            CatalogFileEvidenceReceipt receipt,
            EvidenceClaimKind claimKind,
            KnowledgeRecordId recordId,
            CatalogSourceRevisionId revisionId,
            EvidenceClaimContentId? claimContentId)
        {
            var id = EvidenceBindingId.DeriveV2(
                receipt.Id,
                claimKind,
                recordId,
                revisionId,
                receipt.Receipt.SourceFieldPath,
                claimContentId);
            return new(id, receipt.Id, claimKind, recordId, revisionId,
                receipt.Receipt.SourceFieldPath, claimContentId);
        }
    }
}
