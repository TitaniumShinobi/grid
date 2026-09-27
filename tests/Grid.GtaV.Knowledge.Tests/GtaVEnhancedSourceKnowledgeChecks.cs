using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal static class GtaVEnhancedSourceKnowledgeChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var digest = ContentDigest.ComputeSha256("gta-four-kind-adapters-v1"u8);
        var map = new GtaVMapZonesKnowledgeAdapter(digest);
        var missions = new GtaVUgcMissionKnowledgeAdapter(digest);
        var peds = new GtaVGen9PedsKnowledgeAdapter(digest);
        var adapters = new IGameKnowledgeAdapter[] { map, missions, peds };

        Assert(adapters.All(value => value.Descriptor.SupportedGameIds.SequenceEqual(
                   [ProductionGridCatalogService.GrandTheftAutoVEnhancedId])) &&
               adapters.Select(value => value.Descriptor.RevisionId).Distinct().Count() == 3 &&
               adapters.All(value => value.Descriptor.RevisionId == KnowledgeAdapterRevisionId.DeriveV2(
                   value.Descriptor.AdapterId,
                   value.Descriptor.ExactAdapterVersion,
                   value.Descriptor.AdapterContractVersion,
                   value.Descriptor.MappingRulesVersion,
                   KnowledgeAdapterSemanticContractDigest.DeriveV1(
                       value.Descriptor.SupportedGameIds,
                       value.Descriptor.SupportedFormats,
                       value.Descriptor.ResourceLimits))),
            "All three adapters are Enhanced-only and expose independently derivable revisions.");

        Assert(map.Descriptor.SupportedFormats.Single().FormatId == GtaVMapZonesKnowledgeAdapter.FormatId &&
               map.Descriptor.SupportedFormats.Single().ExactFormatVersion == "1" &&
               map.Descriptor.SupportedFormats.Single().SupportedKnowledgeKinds.SequenceEqual([KnowledgeKind.Location]) &&
               missions.Descriptor.SupportedFormats.Single().FormatId == GtaVUgcMissionKnowledgeAdapter.FormatId &&
               missions.Descriptor.SupportedFormats.Single().SupportedKnowledgeKinds.SequenceEqual([KnowledgeKind.MissionQuest]) &&
               peds.Descriptor.SupportedFormats.Single().FormatId == GtaVGen9PedsKnowledgeAdapter.FormatId &&
               peds.Descriptor.SupportedFormats.Single().SupportedKnowledgeKinds.SequenceEqual([KnowledgeKind.Actor]) &&
               adapters.All(value => value.Descriptor.SupportedFormats.Single().ContainerKinds.SequenceEqual(["rpf7-member"]) &&
                   !value.Descriptor.SupportedFormats.Single().SupportsTerminology &&
                   !value.Descriptor.SupportedFormats.Single().SupportsRelationships &&
                   !value.Descriptor.SupportedFormats.Single().SupportsHierarchy),
            "Every adapter declares only its exact v1 member format and proven capability.");

        var version = SourceNativeVersion.FromExactUtf8("valve.steam.app.3240220.build-id", "25261616");
        var observedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var mapArtifact = Frozen(
            map.CreateSourceCoordinate("common.rpf!/data/levels/gta5/mapzones.xml"),
            map.Format,
            MapFixture,
            observedAt);
        var missionArtifact = Frozen(
            missions.CreateSourceCoordinate("update/update2.rpf!/common/data/ugc/mission-one.ugc"),
            missions.Format,
            MissionFixture,
            observedAt);
        var unresolvedMissionArtifact = Frozen(
            missions.CreateSourceCoordinate("update/update2.rpf!/common/data/ugc/mission-unresolved.ugc"),
            missions.Format,
            UnresolvedMissionFixture,
            observedAt);
        var pedArtifact = Frozen(
            peds.CreateSourceCoordinate("update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta"),
            peds.Format,
            PedsFixture,
            observedAt);

        var mapExtraction = await Extract(map, version, [mapArtifact]);
        var missionExtraction = await Extract(missions, version, [missionArtifact, unresolvedMissionArtifact]);
        var pedExtraction = await Extract(peds, version, [pedArtifact]);
        var extractions = new[] { mapExtraction, missionExtraction, pedExtraction };

        Assert(mapExtraction.CanonicalRegistrations.Single().Registration.KnowledgeRecords.Length == 2 &&
               mapExtraction.CanonicalRegistrations.Single().Registration.KnowledgeRecords.All(value =>
                   value.Kind == KnowledgeKind.Location) &&
               mapExtraction.CanonicalRegistrations.Single().Registration.KnowledgeRecords
                   .Select(value => value.NativeIdentity.ExactRepresentation)
                   .SequenceEqual(["Zone_Café—UPPER!", "Zone_SECOND"]),
            "Map-zone Name values survive verbatim as Location identities.");
        var mapRegistration = mapExtraction.CanonicalRegistrations.Single().Registration;
        Assert(mapRegistration.KnowledgeRecords.All(value =>
                   value.Id == KnowledgeRecordId.DeriveV1(
                       value.GameId,
                       value.GameVersion,
                       value.ModVersion,
                       value.SourceRevisionId,
                       value.Kind,
                       value.NativeRecordIdentityId)) &&
               mapRegistration.TerminologyAssertions.IsEmpty &&
               mapRegistration.RelationshipAssertions.IsEmpty,
            "Adding Location classification does not alter canonical record identity or invent terminology and hierarchy.");
        Assert(mapRegistration.SourceNativeLocationTypeAssertions.Length == 2 &&
               mapRegistration.SourceNativeLocationTypeAssertions.All(value =>
                   value.ExactNativeType.ExactRepresentation == GtaVMapZonesKnowledgeAdapter.NativeLocationType &&
                   value.SourceFieldPath.Contains("#/CMapZonesContainer[1]/Zones[1]/Item[", StringComparison.Ordinal) &&
                   SourceNativeLocationTypeAssertionId.DeriveV1(
                       value.KnowledgeRecordId,
                       value.SourceRevisionId,
                       value.ExactNativeType,
                       value.SourceFieldPath) == value.Id) &&
               mapRegistration.KnowledgeRecords.All(record =>
                   mapRegistration.SourceNativeLocationTypeAssertions.Count(value =>
                       value.KnowledgeRecordId == record.Id) == 1),
            "Every map-zone Location retains the exact CMapZone native type as a separately identified source claim.");
        Assert(mapRegistration.LocationSemanticClassificationAssertions.Length == 2 &&
               mapRegistration.LocationSemanticClassificationAssertions.All(value =>
                   value.RoleId.Value == GtaVMapZonesKnowledgeAdapter.AreaZoneSemanticRole &&
                   value.VocabularyVersion == new LocationSemanticVocabularyVersion(1) &&
                   value.ClassificationMethodId == GtaVMapZonesKnowledgeAdapter.ClassificationMethod &&
                   value.ClassificationMethodVersion == GtaVMapZonesKnowledgeAdapter.ClassificationMethodVersion &&
                   value.SourceNativeTypeAssertionId is not null &&
                   mapRegistration.SourceNativeLocationTypeAssertions.Any(nativeType =>
                       nativeType.Id == value.SourceNativeTypeAssertionId &&
                       nativeType.KnowledgeRecordId == value.KnowledgeRecordId)),
            "Every map-zone Location has the independently evidenced universal area-zone classification and no guessed topology.");
        Assert(mapRegistration.FileEvidenceReceipts.Length == 4 &&
               mapRegistration.EvidenceBindings.Length == 6 &&
               mapRegistration.EvidenceBindings.Count(value =>
                   value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity) == 2 &&
               mapRegistration.EvidenceBindings.Count(value =>
                   value.ClaimKind == EvidenceClaimKind.LocationNativeType) == 2 &&
               mapRegistration.EvidenceBindings.Count(value =>
                   value.ClaimKind == EvidenceClaimKind.LocationSemanticClassification) == 2 &&
               mapRegistration.EvidenceBindings.Where(value =>
                       value.ClaimKind is EvidenceClaimKind.LocationNativeType or
                           EvidenceClaimKind.LocationSemanticClassification)
                   .All(binding =>
                       binding.ClaimContentId is not null &&
                       mapRegistration.FileEvidenceReceipts.Any(receipt =>
                           receipt.Id == binding.EvidenceReceiptId &&
                           receipt.Receipt.SourceFieldPath == binding.ClaimLocator)),
            "Map-zone type and role claims have independent content-bound FILE_VERIFIED bindings at the exact Item locator.");
        Assert(mapRegistration.SourceNativeLocationTypeAssertions.All(assertion =>
                   mapRegistration.EvidenceBindings.Any(binding =>
                       binding.ClaimKind == EvidenceClaimKind.LocationNativeType &&
                       binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
                       binding.ClaimLocator == assertion.SourceFieldPath &&
                       binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion))) &&
               mapRegistration.LocationSemanticClassificationAssertions.All(assertion =>
                   mapRegistration.EvidenceBindings.Any(binding =>
                       binding.ClaimKind == EvidenceClaimKind.LocationSemanticClassification &&
                       binding.KnowledgeRecordId == assertion.KnowledgeRecordId &&
                       binding.ClaimLocator == assertion.SourceFieldPath &&
                       binding.ClaimContentId == EvidenceClaimContentId.DeriveV1(assertion))),
            "Map-zone evidence bindings target the exact native-type and semantic-classification claim identities.");
        var mapCoverage = mapExtraction.LocationCoverageReports.Single();
        var mapFamilyCoverage = mapCoverage.SourceFamilies.Single();
        Assert(mapCoverage.Status == LocationCoverageStatus.Partial &&
               !mapCoverage.Manifest.IsClosed &&
               mapCoverage.Manifest.QcsValidation.Status == CatalogValidationStatus.Candidate &&
               mapCoverage.Manifest.SourceFamilies.Single().SourceFamilyId.Value ==
                   GtaVMapZonesKnowledgeAdapter.CoverageSourceFamily &&
               mapCoverage.Manifest.SourceFamilies.Single().ExpectedSemanticRoles.SequenceEqual(
                   [LocationSemanticRoles.AreaZone]) &&
               mapFamilyCoverage.DiscoveredObjectCount == 2 &&
               mapFamilyCoverage.AcquiredObjectCount == 2 &&
               mapFamilyCoverage.ParsedObjectCount == 2 &&
               mapFamilyCoverage.EmittedLocationRecordIds.Length == 2 &&
               mapFamilyCoverage.UnsupportedObjectCount == 0 &&
               mapFamilyCoverage.ParserErrorCount == 0 &&
               mapFamilyCoverage.MissingArtifactCount == 0 &&
               mapFamilyCoverage.AmbiguousClassificationCount == 0,
            "Map-zone extraction reports one exact open source family as Partial rather than claiming Location completion.");
        Assert(mapCoverage.SemanticCategories.Single().ExactNativeType?.ExactRepresentation ==
                   GtaVMapZonesKnowledgeAdapter.NativeLocationType &&
               mapCoverage.SemanticCategories.Single().SemanticRoleId?.Value ==
                   GtaVMapZonesKnowledgeAdapter.AreaZoneSemanticRole &&
               mapCoverage.Terminology.TotalRecordCount == 2 &&
               mapCoverage.Terminology.PrimaryNamedRecordCount == 0 &&
               mapCoverage.Terminology.IdentifierOnlyRecordCount == 2 &&
               mapCoverage.Hierarchy.NotProvidedBySource &&
               mapCoverage.Hierarchy.SourceProvidedEdgeCount == 0 &&
               mapCoverage.Relationships.IsEmpty &&
               mapCoverage.Unresolved.IsEmpty,
            "The map-zone ledger states area-zone-only, zero terminology, and no source-provided hierarchy or relationships.");
        var mapPayload = GtaVKnowledgePackageProjection.CreatePayload(mapExtraction);
        Assert(mapPayload.SourceNativeLocationTypeAssertions.Length == 2 &&
               mapPayload.LocationSemanticClassificationAssertions.Length == 2 &&
               mapPayload.LocationCoverageReports.Length == 1 &&
               mapPayload.LocationCoverageReports[0].Id == mapCoverage.Id,
            "Package projection retains the exact map-zone Location claims and coverage report.");
        Assert(missionExtraction.CanonicalRegistrations.Sum(value => value.Registration.KnowledgeRecords.Length) == 1 &&
               missionExtraction.CanonicalRegistrations.SelectMany(value => value.Registration.KnowledgeRecords)
                   .Single().NativeIdentity.ExactRepresentation == "FMNM_Café—Exact!" &&
               missionExtraction.UnresolvedSourceAssertions.Length == 1 &&
               missionExtraction.UnresolvedSourceAssertions[0].CandidateKind == KnowledgeKind.MissionQuest &&
               missionExtraction.UnresolvedSourceAssertions[0].NativeIdentity.ExactRepresentation ==
                   "update/update2.rpf!/common/data/ugc/mission-unresolved.ugc",
            "UGC fmnm is verbatim MissionQuest identity while a mission lacking fmnm remains evidence-backed unresolved.");
        var pedRegistration = pedExtraction.CanonicalRegistrations.Single().Registration;
        Assert(pedRegistration.KnowledgeRecords.Length == 2 &&
               pedRegistration.KnowledgeRecords.All(value => value.Kind == KnowledgeKind.Actor) &&
               pedRegistration.KnowledgeRecords.Select(value => value.NativeIdentity.ExactRepresentation)
                   .SequenceEqual(["A_Café—ONE", "A_SECOND"]) &&
               pedRegistration.FileEvidenceReceipts.Length == 3 &&
               pedRegistration.EvidenceBindings.Length == 3,
            "Repeated ped occurrences coalesce to two exact Actor identities while retaining all three source occurrences as evidence.");

        Assert(extractions.SelectMany(value => value.CanonicalRegistrations)
                   .All(value => value.SourceScope.GameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId &&
                       value.SourceScope.ExactGameVersion == version &&
                       value.SourceScope.ScopeKind == KnowledgeSourceScopeKind.BaseGame &&
                       value.Registration.SourceRevision.Id.Value.StartsWith(
                           "grid.catalog-source-revision.v2.sha256.", StringComparison.Ordinal)) &&
               extractions.All(value => !value.CanonicalRegistrations
                   .SelectMany(registration => registration.Registration.TerminologyAssertions).Any()),
            "All records bind the exact Enhanced build/base scope and emit no terminology.");

        var allRegistrations = extractions.SelectMany(value => value.CanonicalRegistrations).ToArray();
        Assert(allRegistrations.SelectMany(value => value.Registration.FileEvidenceReceipts).All(value =>
                   value.Receipt.Verification == EvidenceVerificationKind.FileVerified &&
                   value.Receipt.SourceFieldPath.StartsWith("rpf7-member:", StringComparison.Ordinal) &&
                   value.Receipt.ObservedAtUtc == observedAt) &&
               !allRegistrations.SelectMany(value => value.Registration.ReferenceEvidenceReceipts).Any() &&
               allRegistrations.SelectMany(value => value.Registration.EvidenceBindings).All(binding =>
                   allRegistrations.SelectMany(value => value.Registration.FileEvidenceReceipts).Any(receipt =>
                       receipt.Id == binding.EvidenceReceiptId &&
                       receipt.Receipt.SourceFieldPath == binding.ClaimLocator)),
            "Every canonical claim has an exact locator-matched FILE_VERIFIED binding and no reference evidence.");
        var unresolvedReceiptId = missionExtraction.UnresolvedSourceAssertions.Single().SupportingEvidenceReceiptIds.Single();
        Assert(missionExtraction.CanonicalRegistrations.SelectMany(value => value.Registration.FileEvidenceReceipts)
                   .Single(value => value.Id == unresolvedReceiptId).Receipt.SourceFieldPath.EndsWith("#/mission", StringComparison.Ordinal) &&
               missionExtraction.CanonicalRegistrations.SelectMany(value => value.Registration.EvidenceBindings)
                   .All(value => value.EvidenceReceiptId != unresolvedReceiptId),
            "A missing fmnm is proven from the mission object without fabricating a canonical claim binding.");

        var mapAgain = await Extract(map, version, [mapArtifact]);
        Assert(PayloadDigest(mapExtraction) == PayloadDigest(mapAgain),
            "Repeated extraction of identical map-zone bytes has one semantic identity.");
        var reorderedMission = await Extract(missions, version, [unresolvedMissionArtifact, missionArtifact]);
        Assert(PayloadDigest(missionExtraction) == PayloadDigest(reorderedMission),
            "UGC extraction is independent of frozen-artifact enumeration order.");

        var changedMap = Frozen(
            map.CreateSourceCoordinate("common.rpf!/data/levels/gta5/mapzones.xml"),
            map.Format,
            MapFixture.Replace("Zone_SECOND", "Zone_CHANGED", StringComparison.Ordinal),
            observedAt);
        var changedExtraction = await Extract(map, version, [changedMap]);
        Assert(changedMap.Id != mapArtifact.Id &&
               changedExtraction.CanonicalRegistrations.Single().Registration.SourceRevision.Id !=
                   mapExtraction.CanonicalRegistrations.Single().Registration.SourceRevision.Id &&
               PayloadDigest(changedExtraction) != PayloadDigest(mapExtraction),
            "Changed bytes change the artifact, adapter-bound source revision, and semantic payload.");

        var unsupportedMission = Frozen(
            missions.CreateSourceCoordinate("update/update2.rpf!/common/data/ugc/non-mission.ugc"),
            missions.Format,
            "{\"ugcMetadata\":{\"kind\":\"not-a-mission\"}}",
            observedAt);
        var unsupportedResult = await Extract(missions, version, [unsupportedMission]);
        Assert(unsupportedResult.CoverageState == KnowledgeCoverageState.Unsupported &&
               unsupportedResult.CanonicalRegistrations.IsEmpty &&
               unsupportedResult.UnresolvedSourceAssertions.IsEmpty,
            "A UGC document without a mission object fails closed without canonical output.");

        var malformedMap = Frozen(
            map.CreateSourceCoordinate("common.rpf!/data/levels/gta5/mapzones.xml"),
            map.Format,
            "<CMapZonesContainer><Zones><Item><Name>NO_EVIDENCE_SHAPE</Name></Item></Zones></CMapZonesContainer>",
            observedAt);
        var malformedResult = await Extract(map, version, [malformedMap]);
        Assert(malformedResult.CoverageState == KnowledgeCoverageState.Unsupported &&
               malformedResult.CanonicalRegistrations.IsEmpty,
            "Incomplete map-zone structure fails closed rather than classifying an unproven Location.");

        Assert(adapters.All(adapter => adapter.GetType().GetMethods().All(method =>
                   method.Name is not "AppendAsync" and not "RegisterAsync")) &&
               adapters.All(adapter => adapter.GetType().GetConstructors()
                   .SelectMany(value => value.GetParameters())
                   .All(value => !value.ParameterType.Name.Contains("Store", StringComparison.OrdinalIgnoreCase))),
            "The new adapters have no canonical-store append authority.");

        var extraDigest = ContentDigest.ComputeSha256("unsupported-ugc-member"u8);
        var acquiredMembers = ImmutableArray.Create(
            new SourceAcquisitionMember(
                SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.rpf-member-coordinate",
                    "Rpf7Member",
                    mapArtifact.SourceCoordinate.ExactRepresentation),
                mapArtifact.ExactBytes.Length,
                mapArtifact.Digest,
                mapArtifact.Id),
            new SourceAcquisitionMember(
                SourceNativeIdentifier.FromExactUtf8(
                    "rockstar.gta-v.enhanced.rpf-member-coordinate",
                    "Rpf7Member",
                    "common.rpf!/unsupported/member.bin"),
                22,
                extraDigest,
                SourceArtifactId.DeriveV1(extraDigest)));
        var acquisitionMethod = new AcquisitionMethodCoordinate(
            "grid.test.acquisition",
            "1",
            "grid.test.tool",
            "1",
            digest);
        var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.container-coordinate",
            "Rpf7Container",
            "common.rpf");
        var containerDigest = ContentDigest.ComputeSha256("container"u8);
        var applicationIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam",
            "Application",
            "3240220");
        var acquisitionId = SourceAcquisitionReceiptId.DeriveV1(
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            applicationIdentity,
            version,
            containerCoordinate,
            9,
            containerDigest,
            acquisitionMethod,
            acquiredMembers);
        var acquisitionReceipt = new SourceAcquisitionReceipt(
            acquisitionId,
            SourceAcquisitionReceipt.CurrentSchemaVersion,
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            applicationIdentity,
            version,
            containerCoordinate,
            9,
            containerDigest,
            acquisitionMethod,
            acquiredMembers);
        var rawBindings = acquiredMembers.Select(value => new SourceArtifactAcquisitionBinding(
            value.ArtifactId,
            acquisitionReceipt.Id,
            value.MemberCoordinate,
            value.ByteLength,
            value.Digest)).ToImmutableArray();
        var narrowedPayload = GtaVKnowledgePackageProjection.CreatePayload(
            [mapExtraction],
            [acquisitionReceipt],
            rawBindings);
        Assert(narrowedPayload.AcquisitionReceipts.Length == 1 &&
               narrowedPayload.AcquisitionReceipts[0].Members.Length == 1 &&
               narrowedPayload.AcquisitionReceipts[0].Members[0].ArtifactId == mapArtifact.Id &&
               narrowedPayload.AcquisitionReceipts[0].Id != acquisitionReceipt.Id &&
               narrowedPayload.ArtifactAcquisitionBindings.Length == 1 &&
               narrowedPayload.ArtifactAcquisitionBindings[0].AcquisitionReceiptId ==
                   narrowedPayload.AcquisitionReceipts[0].Id,
            "Package projection narrows validated container receipts to emitted artifacts without retaining orphan members.");
        var locationPackage = CanonicalCatalogPackageKernel.CreateV4(
            CatalogPackageKind.BaseGameCatalog,
            new CatalogGameScope(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                version,
                [mapArtifact.Id]),
            null,
            [],
            "grid.catalog-composition.v1",
            narrowedPayload,
            new CatalogValidationSummary(
                CatalogValidationStatus.Candidate,
                "grid.gta-v-enhanced.location-canary.structural",
                "1",
                ContentDigest.ComputeSha256("candidate-not-qcs-approved"u8)),
            new CatalogBuildProvenance(
                CatalogBuildProvenance.CurrentSchemaVersion,
                "grid.gta-v-enhanced.location-canary.tests",
                "1",
                new string('a', 40),
                [new CatalogCommittedBuildInput(
                    "src/Grid.GtaV.Knowledge/GtaVMapZonesKnowledgeAdapter.cs",
                    new string('b', 40))]));
        Assert(locationPackage.Manifest.PackageSchemaVersion == CatalogPackageManifest.LocationContractSchemaVersion &&
               locationPackage.Manifest.ValidationStatus == CatalogValidationStatus.Candidate &&
               CanonicalCatalogPackageKernel.Verify(locationPackage).IsStructurallyValid,
            "The exact map-zone claims and Partial coverage ledger form a structurally valid schema-v4 Candidate package without approval semantics.");
        var missingCoverageVerification = CanonicalCatalogPackageKernel.Verify(locationPackage with
        {
            Payload = narrowedPayload with { LocationCoverageReports = [] },
        });
        Assert(!missingCoverageVerification.IsStructurallyValid &&
               missingCoverageVerification.Issues.Any(value => value.Contains("coverage report", StringComparison.Ordinal)),
            "A schema-v4 package cannot omit exact Location coverage.");
        var classificationBindingIds = narrowedPayload.EvidenceBindings
            .Where(value => value.ClaimKind == EvidenceClaimKind.LocationSemanticClassification)
            .Select(value => value.Id)
            .ToHashSet();
        var missingClassificationEvidence = CanonicalCatalogPackageKernel.Verify(locationPackage with
        {
            Payload = new CanonicalCatalogPayload(
                narrowedPayload.EffectiveCoverage,
                narrowedPayload.AdapterDescriptors,
                narrowedPayload.Sources,
                narrowedPayload.Artifacts,
                narrowedPayload.SourceRevisions,
                narrowedPayload.KnowledgeRecords,
                narrowedPayload.TerminologyAssertions,
                narrowedPayload.RelationshipAssertions,
                narrowedPayload.FileEvidenceReceipts,
                narrowedPayload.ReferenceEvidenceReceipts,
                narrowedPayload.EvidenceBindings
                    .Where(value => !classificationBindingIds.Contains(value.Id))
                    .ToImmutableArray(),
                narrowedPayload.CorrelationEnvelopes,
                narrowedPayload.UnresolvedSourceAssertions,
                narrowedPayload.AcquisitionReceipts,
                narrowedPayload.ArtifactAcquisitionBindings)
            {
                SourceNativeLocationTypeAssertions = narrowedPayload.SourceNativeLocationTypeAssertions,
                LocationSemanticClassificationAssertions = narrowedPayload.LocationSemanticClassificationAssertions,
                RecordLifecycleAssertions = narrowedPayload.RecordLifecycleAssertions,
                CorrelatedRelationshipEnvelopes = narrowedPayload.CorrelatedRelationshipEnvelopes,
                LocationCoverageReports = narrowedPayload.LocationCoverageReports,
            },
        });
        Assert(!missingClassificationEvidence.IsStructurallyValid &&
               missingClassificationEvidence.Issues.Any(value =>
                   value.Contains("semantic-classification", StringComparison.Ordinal)),
            "Location semantic classifications fail closed without their exact claim-level evidence bindings.");

        var storeRoot = Path.Combine(Path.GetTempPath(), $"grid-location-store-{Guid.NewGuid():N}");
        try
        {
            var storePath = Path.Combine(storeRoot, "canonical-catalog.json");
            var store = new JsonCanonicalKnowledgeCatalogStore(storePath);
            var imported = await store.ImportPackageAsync(0, locationPackage);
            var reloaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();
            Assert(imported.Status == CanonicalCatalogImportStatus.Imported && reloaded.IsValid &&
                   reloaded.Snapshot.FindImportedPackage(locationPackage.Id) is not null &&
                   narrowedPayload.SourceNativeLocationTypeAssertions.All(value =>
                       reloaded.Snapshot.FindLocationNativeType(value.Id) is not null) &&
                   narrowedPayload.LocationSemanticClassificationAssertions.All(value =>
                       reloaded.Snapshot.FindLocationClassification(value.Id) is not null) &&
                   reloaded.Snapshot.FindLocationCoverageReport(
                       narrowedPayload.LocationCoverageReports.Single().Id) is not null,
                "Schema-v4 Location claims and coverage import atomically and remain historically queryable in the schema-v3 shared store.");
            var retry = await store.ImportPackageAsync(reloaded.Snapshot.Revision, locationPackage);
            Assert(retry.Status == CanonicalCatalogImportStatus.Unchanged,
                "An exact schema-v4 Location package retry remains idempotent.");
        }
        finally
        {
            if (Directory.Exists(storeRoot)) Directory.Delete(storeRoot, recursive: true);
        }

        var compositionId = new CatalogCompositionId("grid.catalog-composition.fixture.gta-v-enhanced");
        var locationQuery = new LocationSelectorQuery(
            compositionId,
            null,
            [LocationSemanticRoles.AreaZone],
            null,
            includeIdentifierOnly: true,
            includeInapplicableForInspection: false);
        var projectionA = CanonicalLocationSelectorProjector.Project(
            locationPackage,
            locationQuery,
            narrowedPayload.LocationCoverageReports.Single().Id);
        var projectionB = CanonicalLocationSelectorProjector.Project(
            locationPackage,
            locationQuery,
            narrowedPayload.LocationCoverageReports.Single().Id);
        Assert(projectionA.Options.Select(value => value.KnowledgeRecordId)
                   .SequenceEqual(projectionB.Options.Select(value => value.KnowledgeRecordId)) &&
               projectionA.Options.Length == mapRegistration.KnowledgeRecords.Length &&
               projectionA.Options.All(value => value.IsIdentifierOnly &&
                   value.ExactTerminologyAssertions.IsEmpty &&
                   value.SemanticClassifications.Any(item => item.RoleId == LocationSemanticRoles.AreaZone)),
            "The package-only Location query is deterministic and exposes identifier-only area zones without invented terminology.");
        var policyOrder = projectionA.Options.Reverse().ToImmutableArray();
        var policyOrderedResult = new LocationSelectorResult(
            projectionA.CatalogRevisionId,
            projectionA.CatalogCompositionId,
            projectionA.LocationCoverageReportId,
            projectionA.LocationPresentationPolicyId,
            projectionA.ExactPresentationPolicyVersion,
            policyOrder);
        Assert(policyOrderedResult.Options.Select(value => value.KnowledgeRecordId)
                .SequenceEqual(policyOrder.Select(value => value.KnowledgeRecordId)),
            "Location selector results preserve the ordering established by their pinned presentation policy.");

        var mapFamily = mapCoverage.SourceFamilies.Single();
        LocationCoverageReport SplitReport(KnowledgeRecordId recordId)
        {
            var splitFamily = new LocationSourceFamilyCoverage(
                mapFamily.SourceFamilyId,
                mapFamily.ArtifactIds,
                mapFamily.SourceRevisionIds,
                1,
                1,
                1,
                [recordId],
                [],
                [],
                0,
                0,
                0,
                0);
            var category = mapCoverage.SemanticCategories.Single();
            return LocationCoverageReport.Create(
                mapCoverage.Manifest,
                [splitFamily],
                [new LocationSemanticCategoryCoverage(
                    category.ExactNativeType,
                    category.SemanticRoleId,
                    [recordId],
                    0)],
                new LocationTerminologyCoverage(1, 0, 0, 1, 0),
                new LocationHierarchyCoverage(0, 0, 0, 0, true),
                [],
                []);
        }
        var splitReports = mapRegistration.KnowledgeRecords
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .Select(value => SplitReport(value.Id))
            .ToImmutableArray();
        var splitPayload = narrowedPayload with { LocationCoverageReports = splitReports };
        var splitPackage = CanonicalCatalogPackageKernel.CreateV4(
            locationPackage.Manifest.PackageKind,
            locationPackage.Manifest.GameScope,
            locationPackage.Manifest.ModScope,
            locationPackage.Manifest.RequiredBasePackageIds,
            locationPackage.Manifest.CompositionPolicyVersion,
            splitPayload,
            locationPackage.ValidationSummary,
            locationPackage.Manifest.BuildProvenance);
        Assert(CanonicalCatalogPackageKernel.Verify(splitPackage).IsStructurallyValid,
            "Multiple exact Location coverage reports remain structurally valid when every record is covered once.");
        var scopedProjection = CanonicalLocationSelectorProjector.Project(
            splitPackage,
            locationQuery,
            splitReports[0].Id);
        Assert(scopedProjection.Options.Select(value => value.KnowledgeRecordId)
                .SequenceEqual(splitReports[0].SourceFamilies.Single().EmittedLocationRecordIds),
            "A Location query returns only records accounted for by its selected coverage report.");

        var orderedMapRecords = mapRegistration.KnowledgeRecords
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var correlationEvidence = mapRegistration.FileEvidenceReceipts[0].Id;
        var ambiguousCorrelation = new CorrelationRecord(
            orderedMapRecords.Select(value => value.Id).ToImmutableArray(),
            "grid.tests.location.hierarchy-ambiguity",
            "1",
            CorrelationOutcome.Ambiguous);
        var ambiguousCorrelationId = CorrelationRecordId.DeriveV1(
            ambiguousCorrelation,
            [correlationEvidence]);
        var correlationEnvelope = new CanonicalCorrelationEnvelope(
            ambiguousCorrelationId,
            ambiguousCorrelation,
            [correlationEvidence]);
        var correlatedRelationshipId = CorrelatedRelationshipEnvelopeId.DeriveV1(
            orderedMapRecords[0].Id,
            LocationRelationshipSemantics.ContainedBy,
            orderedMapRecords.Select(value => value.Id).ToImmutableArray(),
            [],
            [ambiguousCorrelationId],
            "grid.tests.location.hierarchy-ambiguity",
            "1",
            CorrelationOutcome.Ambiguous);
        var correlatedRelationship = new CorrelatedRelationshipEnvelope(
            correlatedRelationshipId,
            orderedMapRecords[0].Id,
            LocationRelationshipSemantics.ContainedBy,
            orderedMapRecords.Select(value => value.Id).ToImmutableArray(),
            [],
            [ambiguousCorrelationId],
            "grid.tests.location.hierarchy-ambiguity",
            "1",
            CorrelationOutcome.Ambiguous);
        var ambiguousCoverage = LocationCoverageReport.Create(
            mapCoverage.Manifest,
            mapCoverage.SourceFamilies,
            mapCoverage.SemanticCategories,
            mapCoverage.Terminology,
            new LocationHierarchyCoverage(0, 0, 0, 1, true),
            mapCoverage.Relationships,
            mapCoverage.Unresolved);
        var ambiguousPayload = new CanonicalCatalogPayload(
            narrowedPayload.EffectiveCoverage,
            narrowedPayload.AdapterDescriptors,
            narrowedPayload.Sources,
            narrowedPayload.Artifacts,
            narrowedPayload.SourceRevisions,
            narrowedPayload.KnowledgeRecords,
            narrowedPayload.TerminologyAssertions,
            narrowedPayload.RelationshipAssertions,
            narrowedPayload.FileEvidenceReceipts,
            narrowedPayload.ReferenceEvidenceReceipts,
            narrowedPayload.EvidenceBindings,
            [correlationEnvelope],
            narrowedPayload.UnresolvedSourceAssertions,
            narrowedPayload.AcquisitionReceipts,
            narrowedPayload.ArtifactAcquisitionBindings)
        {
            SourceNativeLocationTypeAssertions = narrowedPayload.SourceNativeLocationTypeAssertions,
            LocationSemanticClassificationAssertions = narrowedPayload.LocationSemanticClassificationAssertions,
            RecordLifecycleAssertions = narrowedPayload.RecordLifecycleAssertions,
            CorrelatedRelationshipEnvelopes = [correlatedRelationship],
            LocationCoverageReports = [ambiguousCoverage],
        };
        var ambiguousPackage = CanonicalCatalogPackageKernel.CreateV4(
            locationPackage.Manifest.PackageKind,
            locationPackage.Manifest.GameScope,
            locationPackage.Manifest.ModScope,
            locationPackage.Manifest.RequiredBasePackageIds,
            locationPackage.Manifest.CompositionPolicyVersion,
            ambiguousPayload,
            locationPackage.ValidationSummary,
            locationPackage.Manifest.BuildProvenance);
        Assert(CanonicalCatalogPackageKernel.Verify(ambiguousPackage).IsStructurallyValid,
            "An ambiguous correlated hierarchy remains a structurally valid independent correlation outcome.");
        var ambiguousProjection = CanonicalLocationSelectorProjector.Project(
            ambiguousPackage,
            locationQuery,
            ambiguousCoverage.Id);
        var ambiguousOption = ambiguousProjection.Options.Single(value =>
            value.KnowledgeRecordId == orderedMapRecords[0].Id);
        Assert(ambiguousOption.CorrelatedRelationshipEnvelopeIds.SequenceEqual([correlatedRelationshipId]) &&
               ambiguousOption.ConflictState.HasFlag(LocationConflictState.Hierarchy) &&
               ambiguousOption.ResolutionState == CanonicalResolutionState.Unresolved,
            "Location queries expose independent hierarchy-correlation ambiguity without selecting a parent or upgrading evidence.");
        var hiddenIdentifiers = CanonicalLocationSelectorProjector.Project(
            locationPackage,
            new LocationSelectorQuery(compositionId, null, [], null, false, false),
            narrowedPayload.LocationCoverageReports.Single().Id);
        Assert(hiddenIdentifiers.Options.IsEmpty,
            "Identifier-only Locations are omitted unless the caller explicitly includes them; no fallback name is synthesized.");
        var projectorInputs = typeof(CanonicalLocationSelectorProjector).GetMethods()
            .SelectMany(value => value.GetParameters())
            .Select(value => value.ParameterType)
            .ToImmutableArray();
        Assert(!projectorInputs.Contains(typeof(InstallationId)) &&
               !projectorInputs.Contains(typeof(ProfileId)) &&
               !projectorInputs.Contains(typeof(TicketId)),
            "Canonical Location projection accepts no account, installation, profile, ticket, or Other context.");

        Console.WriteLine("PASS  GTA V Enhanced Location, MissionQuest, and Actor universal adapters.");
        return checks;
    }

    private static async Task<KnowledgeExtractionResult> Extract(
        IGameKnowledgeAdapter adapter,
        SourceNativeVersion version,
        ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        var discovery = await adapter.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version,
            null,
            null,
            artifacts));
        if (discovery.CoverageState != KnowledgeCoverageState.Unsupported && discovery.SourceCandidates.IsEmpty)
            throw new InvalidOperationException("Supported discovery did not emit a source candidate.");
        return await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            version,
            null,
            null,
            adapter.Descriptor,
            artifacts));
    }

    private static CatalogPayloadDigest PayloadDigest(KnowledgeExtractionResult extraction) =>
        CanonicalCatalogPackageKernel.ComputePayloadDigestV4(GtaVKnowledgePackageProjection.CreatePayload(extraction));

    private static FrozenSourceArtifact Frozen(
        SourceNativeIdentifier coordinate,
        KnowledgeFormatCoordinate format,
        string content,
        DateTimeOffset observedAtUtc)
    {
        var bytes = Encoding.UTF8.GetBytes(content).ToImmutableArray();
        var digest = ContentDigest.ComputeSha256(bytes.AsSpan());
        return new FrozenSourceArtifact(
            SourceArtifactId.DeriveV1(digest),
            digest,
            coordinate,
            format,
            bytes,
            observedAtUtc);
    }

    private const string MapFixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <CMapZonesContainer>
          <Zones>
            <Item><Name>Zone_Café—UPPER!</Name><ZoneAreas><Item><Point>1</Point></Item></ZoneAreas><BoundBox><Min>0</Min></BoundBox></Item>
            <Item><Name>Zone_SECOND</Name><ZoneAreas><Item><Point>2</Point></Item></ZoneAreas><BoundBox><Min>1</Min></BoundBox></Item>
          </Zones>
        </CMapZonesContainer>
        """;

    private const string MissionFixture = """
        {"mission":{"fmnm":"FMNM_Café—Exact!","other":"source data"}}
        """;

    private const string UnresolvedMissionFixture = """
        {"mission":{"other":"fmnm is absent"}}
        """;

    private const string PedsFixture = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Gen9ExclusiveAssetsDataPeds>
          <PedData>
            <Item><PedModelName>A_Café—ONE</PedModelName><dlcName>DLC_A</dlcName></Item>
            <Item><PedModelName>A_Café—ONE</PedModelName><dlcName>DLC_B</dlcName></Item>
            <Item><PedModelName>A_SECOND</PedModelName><dlcName>DLC_C</dlcName></Item>
          </PedData>
        </Gen9ExclusiveAssetsDataPeds>
        """;
}
