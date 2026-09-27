using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVWeaponsMetaKnowledgeChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        static async Task AssertThrowsAsync<TException>(Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
        }

        var adapterDigest = ContentDigest.ComputeSha256("gta-adapter-test-artifact"u8);
        var enhanced = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, adapterDigest);
        var legacy = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Legacy, adapterDigest);
        Assert(enhanced.Descriptor.SupportedGameIds.SequenceEqual(
                   [ProductionGridCatalogService.GrandTheftAutoVEnhancedId]) &&
               legacy.Descriptor.SupportedGameIds.SequenceEqual(
                   [ProductionGridCatalogService.GrandTheftAutoVLegacyId]) &&
               enhanced.Descriptor.AdapterId != legacy.Descriptor.AdapterId &&
               enhanced.Descriptor.RevisionId != legacy.Descriptor.RevisionId,
            "Legacy and Enhanced expose distinct exact adapter and game identities.");
        Assert(enhanced.Descriptor.RevisionId == KnowledgeAdapterRevisionId.DeriveV2(
                   enhanced.Descriptor.AdapterId,
                   enhanced.Descriptor.ExactAdapterVersion,
                   enhanced.Descriptor.AdapterContractVersion,
                   enhanced.Descriptor.MappingRulesVersion,
                   KnowledgeAdapterSemanticContractDigest.DeriveV1(
                       enhanced.Descriptor.SupportedGameIds,
                       enhanced.Descriptor.SupportedFormats,
                       enhanced.Descriptor.ResourceLimits)),
            "The Enhanced adapter revision is independently derivable from its exact descriptor coordinates.");
        var format = enhanced.Descriptor.SupportedFormats.Single();
        Assert(format.FormatId == GtaVWeaponsMetaKnowledgeAdapter.FormatId &&
               format.ExactFormatVersion == GtaVWeaponsMetaKnowledgeAdapter.ExactFormatVersion &&
               format.ContainerKinds.SequenceEqual(["loose-file", "rpf7-member"]) &&
               format.ResourceObjectTypes.SequenceEqual(["CWeaponInfoBlob"]) &&
               format.SupportedKnowledgeKinds.SequenceEqual([KnowledgeKind.Item]) &&
               !format.SupportsTerminology && format.SupportsRelationships && !format.SupportsHierarchy,
            "The adapter declares only its exact weapons.meta format profile and proven capabilities.");

        var observedAt = new DateTimeOffset(2026, 9, 24, 18, 30, 0, TimeSpan.Zero);
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id",
            "fixture-build-exact");
        var artifact = Frozen(enhanced, FixtureXml("WEAPON_Café—UPPER!"), observedAt);
        var discoveryRequest = new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            [artifact]);
        var extractionRequest = new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            [artifact]);

        var discoveryA = await enhanced.DiscoverAsync(discoveryRequest);
        var discoveryB = await enhanced.DiscoverAsync(discoveryRequest);
        Assert(discoveryA.SourceCandidates.Length == 1 && discoveryB.SourceCandidates.Length == 1 &&
               discoveryA.SourceCandidates[0].Source == discoveryB.SourceCandidates[0].Source &&
               discoveryA.SourceCandidates[0].ArtifactIds.SequenceEqual(discoveryB.SourceCandidates[0].ArtifactIds) &&
               discoveryA.UnsupportedArtifacts.SequenceEqual(discoveryB.UnsupportedArtifacts) &&
               discoveryA.Issues.SequenceEqual(discoveryB.Issues) &&
               discoveryA.CoverageState == KnowledgeCoverageState.Partial &&
               discoveryB.CoverageState == KnowledgeCoverageState.Partial,
            "Frozen-source discovery is deterministic and explicitly partial.");

        var secondArtifact = Frozen(
            enhanced,
            FixtureXml("WEAPON_SECOND_SOURCE"),
            observedAt,
            "update.rpf!/data/ai/weapons.meta");
        var forwardArtifacts = new[] { artifact, secondArtifact }.ToImmutableArray();
        var reverseArtifacts = new[] { secondArtifact, artifact }.ToImmutableArray();
        var forwardDiscovery = await enhanced.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            forwardArtifacts));
        var reverseDiscovery = await enhanced.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            reverseArtifacts));
        var forwardExtraction = await enhanced.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            forwardArtifacts));
        var reverseExtraction = await enhanced.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            reverseArtifacts));
        Assert(forwardDiscovery.SourceCandidates.Select(value => value.Source.Id)
                   .SequenceEqual(reverseDiscovery.SourceCandidates.Select(value => value.Source.Id)) &&
               forwardDiscovery.SourceCandidates.SelectMany(value => value.ArtifactIds)
                   .SequenceEqual(reverseDiscovery.SourceCandidates.SelectMany(value => value.ArtifactIds)) &&
               CanonicalCatalogPackageKernel.ComputePayloadDigest(
                   GtaVKnowledgePackageProjection.CreatePayload(forwardExtraction)) ==
               CanonicalCatalogPackageKernel.ComputePayloadDigest(
                   GtaVKnowledgePackageProjection.CreatePayload(reverseExtraction)),
            "Discovery and extraction are independent of frozen-artifact enumeration order.");

        var extractionA = await enhanced.ExtractAsync(extractionRequest);
        var extractionB = await enhanced.ExtractAsync(extractionRequest);
        var payloadA = GtaVKnowledgePackageProjection.CreatePayload(extractionA);
        var payloadB = GtaVKnowledgePackageProjection.CreatePayload(extractionB);
        Assert(CanonicalCatalogPackageKernel.ComputePayloadDigest(payloadA) ==
               CanonicalCatalogPackageKernel.ComputePayloadDigest(payloadB),
            "Repeated extraction of identical frozen bytes has one semantic payload identity.");
        var boundRevision = payloadA.SourceRevisions.Single();
        Assert(boundRevision.SourceScope.GameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId &&
               boundRevision.SourceScope.ExactGameVersion == gameVersion &&
               boundRevision.SourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame &&
               boundRevision.SourceScope.ExactModIdentity is null &&
               boundRevision.SourceScope.ExactModVersion is null &&
               boundRevision.AdapterRevisionId == enhanced.Descriptor.RevisionId &&
               boundRevision.Revision.Id.Value.StartsWith("grid.catalog-source-revision.v2.sha256.", StringComparison.Ordinal),
            "Extraction binds the exact Enhanced build, base-game scope, and adapter revision.");

        Assert(payloadA.KnowledgeRecords.Length == 3 &&
               payloadA.KnowledgeRecords.All(value => value.Kind == KnowledgeKind.Item) &&
               payloadA.KnowledgeRecords.Any(value =>
                   value.NativeIdentity.ExactRepresentation == "WEAPON_Café—UPPER!") &&
               payloadA.KnowledgeRecords
                   .Select(value => value.NativeIdentity.ExactRepresentation)
                   .Distinct(StringComparer.Ordinal)
                   .Count() == 3,
            "Source-native record identifiers remain verbatim and only proven Item records are emitted.");
        Assert(payloadA.TerminologyAssertions.IsEmpty &&
               payloadA.KnowledgeRecords.Any(value => value.NativeIdentity.ExactRepresentation == "WEAPON_Café—UPPER!") &&
               payloadA.EvidenceBindings.All(value => value.ClaimKind != EvidenceClaimKind.Terminology),
            "Name and HumanNameHash fields do not become terminology; nameless records retain their native identifiers.");

        Assert(payloadA.FileEvidenceReceipts.All(value =>
                   value.Receipt.Verification == EvidenceVerificationKind.FileVerified &&
                   value.Receipt.SourceArtifactId == artifact.Id &&
                   value.Receipt.ArtifactDigest == artifact.Digest &&
                   value.Receipt.ParserId == GtaVWeaponsMetaKnowledgeAdapter.ParserId &&
                   value.Receipt.ParserVersion == GtaVWeaponsMetaKnowledgeAdapter.ParserVersion &&
                   value.Receipt.ObservedAtUtc == observedAt &&
                   value.Receipt.SourceFieldPath.StartsWith(
                       "rpf7-member:common.rpf!/data/ai/weapons.meta#/CWeaponInfoBlob[1]",
                       StringComparison.Ordinal)) &&
               payloadA.ReferenceEvidenceReceipts.IsEmpty,
            "Every receipt retains the exact frozen member, digest, parser, field locator, observation time, and FILE_VERIFIED class.");
        Assert(payloadA.EvidenceBindings.All(binding =>
                   payloadA.FileEvidenceReceipts.Any(receipt =>
                       receipt.Id == binding.EvidenceReceiptId &&
                       receipt.Receipt.SourceFieldPath == binding.ClaimLocator)) &&
               payloadA.KnowledgeRecords.All(record => payloadA.EvidenceBindings.Any(binding =>
                   binding.KnowledgeRecordId == record.Id &&
                   binding.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                   binding.ClaimContentId is null)),
            "Identity evidence is bound to each exact record and receipt locator.");
        Assert(payloadA.RelationshipAssertions.Length == 2 &&
               payloadA.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Resolved) == 1 &&
               payloadA.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Unresolved) == 1 &&
               payloadA.RelationshipAssertions.Any(value => value.SourceNativeTarget.ExactRepresentation == "AMMO_MISSING") &&
               payloadA.RelationshipAssertions.All(relationship => payloadA.EvidenceBindings.Any(binding =>
                   binding.ClaimKind == EvidenceClaimKind.Relationship &&
                   binding.KnowledgeRecordId == relationship.SubjectKnowledgeRecordId &&
                   binding.ClaimLocator == relationship.SourceFieldPath &&
                   binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(relationship))),
            "Resolved and unresolved ammo relationships preserve native targets and exact claim evidence.");

        var changedArtifact = Frozen(enhanced, FixtureXml("WEAPON_CHANGED"), observedAt);
        var changed = await enhanced.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            [changedArtifact]));
        var changedPayload = GtaVKnowledgePackageProjection.CreatePayload(changed);
        Assert(changedArtifact.Id != artifact.Id &&
               changedPayload.SourceRevisions.Single().Revision.Id != boundRevision.Revision.Id &&
               !changedPayload.KnowledgeRecords.Select(value => value.Id)
                   .Intersect(payloadA.KnowledgeRecords.Select(value => value.Id)).Any(),
            "Changed bytes change the artifact, adapter-bound source revision, and revision-scoped knowledge identities.");

        var package = CanonicalCatalogPackageKernel.CreateV2(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                gameVersion,
                [artifact.Id]),
            null,
            [],
            "grid.catalog-composition.v1",
            payloadA,
            new CatalogValidationSummary(
                CatalogValidationStatus.Candidate,
                "grid.gta-v-enhanced.canary.structural",
                "1",
                ContentDigest.ComputeSha256("candidate-not-qcs"u8)),
            new CatalogBuildProvenance("grid.gta-v-enhanced.canary.tests", "1", "fixture-commit"));
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var roundTrip = JsonSerializer.Deserialize<CanonicalCatalogPackage>(
            JsonSerializer.Serialize(package, jsonOptions),
            jsonOptions);
        Assert(verification.IsStructurallyValid && roundTrip is not null &&
               roundTrip.Id == package.Id &&
               CanonicalCatalogPackageKernel.Verify(roundTrip).IsStructurallyValid &&
               package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate,
            "A Candidate canary package round-trips and verifies structurally without claiming QCS approval.");

        var unsupportedFormat = new KnowledgeFormatCoordinate("rockstar.gta-v.ymap", "1");
        var unsupportedBytes = Encoding.UTF8.GetBytes("unsupported").ToImmutableArray();
        var unsupportedDigest = ContentDigest.ComputeSha256(unsupportedBytes.AsSpan());
        var unsupportedArtifact = new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(unsupportedDigest),
            unsupportedDigest,
            enhanced.CreateSourceCoordinate("common.rpf!/data/ai/weapons.meta"),
            unsupportedFormat,
            unsupportedBytes,
            observedAt);
        var unsupportedDiscovery = await enhanced.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            [unsupportedArtifact]));
        Assert(unsupportedDiscovery.CoverageState == KnowledgeCoverageState.Unsupported &&
               unsupportedDiscovery.SourceCandidates.IsEmpty &&
               unsupportedDiscovery.UnsupportedArtifacts.Length == 1,
            "Unsupported exact GTA format tuples fail closed without usable discovery candidates.");
        await AssertThrowsAsync<ArgumentException>(() => Task.FromResult(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            [unsupportedArtifact])));
        checks++;

        var malformedBytes = Encoding.UTF8.GetBytes("<CMapData><name>not-weapons</name></CMapData>").ToImmutableArray();
        var malformedDigest = ContentDigest.ComputeSha256(malformedBytes.AsSpan());
        var malformed = new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(malformedDigest),
            malformedDigest,
            enhanced.CreateSourceCoordinate("common.rpf!/data/ai/weapons.meta"),
            GtaVWeaponsMetaKnowledgeAdapter.Format,
            malformedBytes,
            observedAt);
        var malformedExtraction = await enhanced.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            gameVersion,
            null,
            null,
            enhanced.Descriptor,
            [malformed]));
        Assert(malformedExtraction.CoverageState == KnowledgeCoverageState.Unsupported &&
               malformedExtraction.CanonicalRegistrations.IsEmpty &&
               malformedExtraction.CorrelationCandidates.IsEmpty &&
               malformedExtraction.UnresolvedSourceAssertions.IsEmpty,
            "Malformed or ambiguous supported-format bytes fail closed without canonical output.");
        foreach (var extension in new[] { ".rpf", ".gxt2", ".ymap", ".ytyp" })
        {
            try
            {
                _ = enhanced.CreateSourceCoordinate("common.rpf!/data/fixture" + extension);
                throw new InvalidOperationException("Unsupported GTA coordinate was accepted.");
            }
            catch (ArgumentException)
            {
                checks++;
            }
        }

        var publicSurface = typeof(GtaVWeaponsMetaKnowledgeAdapter)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .SelectMany(member => member switch
            {
                MethodInfo method => method.GetParameters().Select(value => value.ParameterType)
                    .Append(method.ReturnType),
                ConstructorInfo constructor => constructor.GetParameters().Select(value => value.ParameterType),
                PropertyInfo property => [property.PropertyType],
                _ => [],
            })
            .ToArray();
        var forbidden = new[]
        {
            "Account", "InstallationId", "ProfileId", "ModId", "LoadOrder", "Ticket", "Other",
            "ICanonicalKnowledgeCatalogStore",
        };
        Assert(publicSurface.All(type => forbidden.All(value =>
                   !type.Name.Contains(value, StringComparison.OrdinalIgnoreCase))) &&
               typeof(GtaVWeaponsMetaKnowledgeAdapter).GetMethods().All(method =>
                   method.Name is not "AppendAsync" and not "RegisterAsync"),
            "The adapter public surface has no account/profile/ticket/Other inputs or canonical-store append authority.");
        Assert(payloadA.Sources.Single().NativeIdentity.ExactRepresentation ==
                   "common.rpf!/data/ai/weapons.meta" &&
               payloadA.FileEvidenceReceipts.All(value =>
                   !value.Receipt.NativeRecordLocator.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)),
            "Local paths and machine context cannot enter canonical identities or evidence locators.");

        Console.WriteLine("PASS  GTA V weapons.meta universal adapter and canary package.");
        return checks;
    }

    private static FrozenSourceArtifact Frozen(
        GtaVWeaponsMetaKnowledgeAdapter adapter,
        string xml,
        DateTimeOffset observedAtUtc,
        string coordinate = "common.rpf!/data/ai/weapons.meta")
    {
        var bytes = Encoding.UTF8.GetBytes(xml).ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(bytes.AsSpan());
        return new(
            SourceArtifactId.DeriveV1(digest),
            digest,
            adapter.CreateSourceCoordinate(coordinate),
            GtaVWeaponsMetaKnowledgeAdapter.Format,
            bytes,
            observedAtUtc);
    }

    private static string FixtureXml(string weaponName) => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <CWeaponInfoBlob>
          <Infos>
            <Item type="CAmmoInfo">
              <Name>AMMO_PRESENT</Name>
            </Item>
            <Item type="CWeaponInfo">
              <Name>{{weaponName}}</Name>
              <HumanNameHash>WT_NOT_TERMINOLOGY</HumanNameHash>
              <AmmoInfo ref="AMMO_PRESENT" />
            </Item>
            <Item type="CWeaponInfo">
              <Name>WEAPON_IDENTIFIER_ONLY</Name>
              <HumanNameHash>WT_STILL_NOT_TERMINOLOGY</HumanNameHash>
              <AmmoInfo ref="AMMO_MISSING" />
            </Item>
          </Infos>
        </CWeaponInfoBlob>
        """;
}
