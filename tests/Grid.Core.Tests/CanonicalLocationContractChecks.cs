using System.Collections.Immutable;
using Grid.Core.Models;

internal static class CanonicalLocationContractChecks
{
    public static int Run()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException($"Location contract check failed: {message}");
        }

        static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }

            throw new InvalidOperationException($"Location contract check failed: {message}");
        }

        var gameId = new GameId("game.fixture.location");
        var gameVersion = SourceNativeVersion.FromExactUtf8("fixture.game-build", "42");
        var artifactDigest = ContentDigest.ComputeSha256("location-fixture"u8);
        var artifactId = SourceArtifactId.DeriveV1(artifactDigest);
        var sourceNative = SourceNativeIdentifier.FromExactUtf8("fixture.location", "source", "locations.xml");
        var sourceId = CatalogSourceId.DeriveV1(KnowledgeSourceKind.LocalGameDistribution, sourceNative);
        var revisionId = CatalogSourceRevisionId.DeriveV1(
            sourceId,
            SourceNativeVersion.FromExactUtf8("fixture.location.revision", "1"),
            [artifactId]);
        var recordA = CreateRecord(gameId, gameVersion, revisionId, "ZONE_A");
        var recordB = CreateRecord(gameId, gameVersion, revisionId, "ZONE_B");
        var nativeType = SourceNativeIdentifier.FromExactUtf8(
            "fixture.location.native-type",
            "location-record-type",
            " CMapZone ");

        var nativeTypeId = SourceNativeLocationTypeAssertionId.DeriveV1(
            recordA.Id,
            revisionId,
            nativeType,
            "/Zones/Item[1]");
        var nativeTypeAssertion = new SourceNativeLocationTypeAssertion(
            nativeTypeId,
            recordA.Id,
            revisionId,
            nativeType,
            "/Zones/Item[1]");
        Assert(nativeTypeAssertion.ExactNativeType.ExactRepresentation == " CMapZone ",
            "Source-native type text remains exact and unnormalized.");
        Assert(SourceNativeLocationTypeAssertionId.DeriveV1(recordA.Id, revisionId, nativeType, "/Zones/Item[1]") == nativeTypeId,
            "Native-type assertion identity is deterministic.");
        Assert(SourceNativeLocationTypeAssertionId.DeriveV1(recordA.Id, revisionId, nativeType, "/Zones/Item[2]") != nativeTypeId,
            "Changing a native-type claim field changes its assertion identity.");

        var classificationId = LocationSemanticClassificationAssertionId.DeriveV1(
            recordA.Id,
            revisionId,
            nativeTypeId,
            LocationSemanticRoles.AreaZone,
            LocationSemanticRoles.CurrentVocabularyVersion,
            "grid.fixture.location-map",
            "1",
            "/Zones/Item[1]");
        var classification = new LocationSemanticClassificationAssertion(
            classificationId,
            recordA.Id,
            revisionId,
            nativeTypeId,
            LocationSemanticRoles.AreaZone,
            LocationSemanticRoles.CurrentVocabularyVersion,
            "grid.fixture.location-map",
            "1",
            "/Zones/Item[1]");
        var changedRoleId = LocationSemanticClassificationAssertionId.DeriveV1(
            recordA.Id,
            revisionId,
            nativeTypeId,
            LocationSemanticRoles.Region,
            LocationSemanticRoles.CurrentVocabularyVersion,
            "grid.fixture.location-map",
            "1",
            "/Zones/Item[1]");
        Assert(changedRoleId != classificationId,
            "Semantic-role changes alter classification-claim identity without altering the knowledge record.");
        Assert(classification.KnowledgeRecordId == recordA.Id && recordA.Kind == KnowledgeKind.Location,
            "Classification is an independent assertion and does not redefine Location identity.");

        var futureRole = new LocationSemanticRoleId("future.game.location.role.sky-island");
        Assert(futureRole.Value == "future.game.location.role.sky-island",
            "Open semantic-role identifiers accept future game-neutral roles without schema changes.");
        Assert(typeof(LocationSemanticClassificationAssertion).GetProperties().All(value =>
                !value.Name.Contains("Other", StringComparison.OrdinalIgnoreCase)),
            "Location classification has no guessed other-role or ticket Other field.");

        var nativeTypeClaimId = EvidenceClaimContentId.DeriveV1(nativeTypeAssertion);
        var classificationClaimId = EvidenceClaimContentId.DeriveV1(classification);
        Assert(nativeTypeClaimId != classificationClaimId,
            "Native-type and semantic-classification evidence claims remain structurally distinct.");
        Assert(EvidenceClaimContentId.DeriveV1(new LocationSemanticClassificationAssertion(
                changedRoleId,
                recordA.Id,
                revisionId,
                nativeTypeId,
                LocationSemanticRoles.Region,
                LocationSemanticRoles.CurrentVocabularyVersion,
                "grid.fixture.location-map",
                "1",
                "/Zones/Item[1]")) != classificationClaimId,
            "Changing classification content changes its exact evidence claim identity.");

        var fileReceipt = new FileEvidenceReceipt(
            revisionId,
            artifactId,
            artifactDigest,
            "fixture-location-parser",
            "1",
            "Zones/Item[1]",
            "/Zones/Item[1]",
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch);
        var receiptId = EvidenceReceiptId.DeriveV1(fileReceipt);
        var nativeTypeBindingId = EvidenceBindingId.DeriveV2(
            receiptId,
            EvidenceClaimKind.LocationNativeType,
            recordA.Id,
            revisionId,
            fileReceipt.SourceFieldPath,
            nativeTypeClaimId);
        var classificationBindingId = EvidenceBindingId.DeriveV2(
            receiptId,
            EvidenceClaimKind.LocationSemanticClassification,
            recordA.Id,
            revisionId,
            fileReceipt.SourceFieldPath,
            classificationClaimId);
        Assert(nativeTypeBindingId != classificationBindingId,
            "One receipt supports native-type and role claims only through independent exact bindings.");

        var exclusionIdentity = SourceNativeIdentifier.FromExactUtf8(
            "fixture.location.record",
            "excluded-candidate",
            "NOT_A_PLAYER_PLACE");
        var exclusionId = LocationCoverageExclusionId.DeriveV1(
            revisionId,
            exclusionIdentity,
            "not-player-addressable",
            "fixture.location.exclusion-rule",
            "1",
            fileReceipt.SourceFieldPath,
            [receiptId]);
        var exclusion = new LocationCoverageExclusion(
            exclusionId,
            revisionId,
            exclusionIdentity,
            "not-player-addressable",
            "fixture.location.exclusion-rule",
            "1",
            fileReceipt.SourceFieldPath,
            [receiptId]);
        Assert(exclusion.Id == exclusionId &&
               exclusion.SourceRevisionId == revisionId &&
               exclusion.SourceFieldPath == fileReceipt.SourceFieldPath,
            "A coverage exclusion retains an exact deterministic source-revision and field coordinate.");
        Assert(LocationCoverageExclusionId.DeriveV1(
                revisionId,
                exclusionIdentity,
                "not-player-addressable",
                "fixture.location.exclusion-rule",
                "2",
                fileReceipt.SourceFieldPath,
                [receiptId]) != exclusionId,
            "Changing the versioned exclusion rule changes the exact exclusion claim identity.");
        Assert(LocationCoverageExclusionId.DeriveV1(
                revisionId,
                exclusionIdentity,
                "not-player-addressable",
                "fixture.location.exclusion-rule",
                "1",
                "/Zones/Item[2]",
                [receiptId]) != exclusionId,
            "Changing the exclusion evidence locator changes the exact exclusion claim identity.");
        AssertThrows<ArgumentException>(() => _ = new LocationCoverageExclusion(
                exclusionId,
                revisionId,
                exclusionIdentity,
                "not-player-addressable",
                "fixture.location.exclusion-rule",
                "2",
                fileReceipt.SourceFieldPath,
                [receiptId]),
            "An exclusion cannot borrow an identity derived for a different rule version.");

        var lifecycleId = CanonicalRecordLifecycleAssertionId.DeriveV1(
            recordA.Id,
            revisionId,
            CanonicalRecordLifecycleState.SourceAssertedDeleted,
            "isDeleted",
            "/Zones/Item[1]/isDeleted");
        var lifecycle = new CanonicalRecordLifecycleAssertion(
            lifecycleId,
            recordA.Id,
            revisionId,
            CanonicalRecordLifecycleState.SourceAssertedDeleted,
            "isDeleted",
            "/Zones/Item[1]/isDeleted");
        Assert(EvidenceClaimContentId.DeriveV1(lifecycle).Value.Contains(".evidence-claim-content.v1.", StringComparison.Ordinal),
            "Source-asserted lifecycle state has an exact evidence claim identity.");

        var parentAtoB = Relationship(recordA, recordB, revisionId, LocationRelationshipSemantics.ContainedBy, "parent-a-b");
        var parentBtoA = Relationship(recordB, recordA, revisionId, LocationRelationshipSemantics.InteriorOf, "parent-b-a");
        Assert(LocationRelationshipSemantics.IsStrictHierarchy(LocationRelationshipSemantics.ContainedBy) &&
               !LocationRelationshipSemantics.IsStrictHierarchy(LocationRelationshipSemantics.SpatialMemberOf),
            "Strict containment is distinct from spatial membership and transitions.");
        CanonicalLocationContractValidator.ValidateStrictHierarchyAcyclic([parentAtoB]);
        AssertThrows<ArgumentException>(
            () => CanonicalLocationContractValidator.ValidateStrictHierarchyAcyclic([parentAtoB, parentBtoA]),
            "Strict hierarchy cycles fail closed within one source revision.");

        var correlationRecord = new CorrelationRecord(
            [recordA.Id, recordB.Id],
            "fixture.exact-native-link",
            "1",
            CorrelationOutcome.Correlated);
        var correlationRecordId = CorrelationRecordId.DeriveV1(correlationRecord, [receiptId]);
        var correlatedRelationshipId = CorrelatedRelationshipEnvelopeId.DeriveV1(
            recordA.Id,
            LocationRelationshipSemantics.SpatialMemberOf,
            [recordB.Id],
            [EvidenceClaimContentId.DeriveV1(parentAtoB)],
            [correlationRecordId],
            "fixture.correlation",
            "1",
            CorrelationOutcome.Correlated);
        var correlatedRelationship = new CorrelatedRelationshipEnvelope(
            correlatedRelationshipId,
            recordA.Id,
            LocationRelationshipSemantics.SpatialMemberOf,
            [recordB.Id],
            [EvidenceClaimContentId.DeriveV1(parentAtoB)],
            [correlationRecordId],
            "fixture.correlation",
            "1",
            CorrelationOutcome.Correlated);
        Assert(typeof(CorrelatedRelationshipEnvelope).GetProperty("Verification") is null &&
               correlatedRelationship.CandidateTargetKnowledgeRecordIds.SequenceEqual([recordB.Id]),
            "Correlation-derived relationships retain inputs without masquerading as FILE/REFERENCE evidence.");

        var adapterRevisionId = KnowledgeAdapterRevisionId.DeriveV1(
            new KnowledgeAdapterId("fixture.location-adapter"),
            "1",
            ContentDigest.ComputeSha256("adapter"u8),
            1,
            "1");
        var familyId = new LocationSourceFamilyId("fixture.map-zones");
        var familyDeclaration = new LocationSourceFamilyDeclaration(
            familyId,
            new KnowledgeFormatCoordinate("fixture.map-zones.xml", "1"),
            adapterRevisionId,
            true,
            [artifactId],
            [revisionId],
            [LocationSemanticRoles.AreaZone]);
        var candidateValidation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate,
            "fixture.location-coverage",
            "1",
            ContentDigest.ComputeSha256("candidate"u8));
        var scope = KnowledgeSourceScope.BaseGame(gameId, gameVersion);
        var openManifestId = LocationCoverageManifestId.DeriveV1(
            scope,
            "1",
            false,
            candidateValidation,
            [familyDeclaration]);
        var openManifest = new LocationCoverageManifest(
            openManifestId,
            scope,
            "1",
            false,
            candidateValidation,
            [familyDeclaration]);
        var closedFamily = new LocationSourceFamilyCoverage(
            familyId,
            [artifactId],
            [revisionId],
            2,
            2,
            2,
            [recordB.Id, recordA.Id],
            [],
            [],
            0,
            0,
            0,
            0);
        var terminologyCoverage = new LocationTerminologyCoverage(2, 0, 0, 2, 0);
        var hierarchyCoverage = new LocationHierarchyCoverage(0, 0, 0, 0, true);
        var categories = ImmutableArray.Create(new LocationSemanticCategoryCoverage(
            nativeType,
            LocationSemanticRoles.AreaZone,
            [recordA.Id, recordB.Id],
            0));
        var explicitlyUnclassified = new LocationSemanticCategoryCoverage(
            null,
            null,
            [recordA.Id, recordB.Id],
            2);
        Assert(explicitlyUnclassified.IsExplicitlyUnclassified &&
               explicitlyUnclassified.RecordIds.Length == 2 &&
               explicitlyUnclassified.RecordIds.Contains(recordA.Id) &&
               explicitlyUnclassified.RecordIds.Contains(recordB.Id),
            "Location coverage can explicitly retain records with neither a source-native type nor a defensible universal role.");
        AssertThrows<ArgumentException>(() => _ = new LocationSemanticCategoryCoverage(
                null,
                null,
                [recordA.Id, recordB.Id],
                1),
            "Explicit unclassified coverage must account for every listed record without guessing a category.");
        var partialReport = LocationCoverageReport.Create(
            openManifest,
            [closedFamily],
            categories,
            terminologyCoverage,
            hierarchyCoverage,
            [],
            []);
        Assert(partialReport.Status == LocationCoverageStatus.Partial,
            "Captured records remain Partial while their exact source manifest is not closed and QCS-passed.");
        Assert(LocationCoverageReport.Create(
                openManifest,
                [closedFamily],
                categories,
                terminologyCoverage,
                hierarchyCoverage,
                [],
                []).Id == partialReport.Id,
            "Location coverage report identity is deterministic across repeated construction.");

        var passedValidation = new CatalogValidationSummary(
            CatalogValidationStatus.Passed,
            "fixture.location-coverage",
            "1",
            ContentDigest.ComputeSha256("passed"u8));
        var closedManifestId = LocationCoverageManifestId.DeriveV1(
            scope,
            "1",
            true,
            passedValidation,
            [familyDeclaration]);
        var closedManifest = new LocationCoverageManifest(
            closedManifestId,
            scope,
            "1",
            true,
            passedValidation,
            [familyDeclaration]);
        var completeReport = LocationCoverageReport.Create(
            closedManifest,
            [closedFamily],
            categories,
            terminologyCoverage,
            hierarchyCoverage,
            [],
            []);
        Assert(completeReport.Status == LocationCoverageStatus.CompleteForRegisteredSources,
            "Only a closed, QCS-passed, mechanically reconciled manifest is CompleteForRegisteredSources.");
        var gappedFamily = new LocationSourceFamilyCoverage(
            familyId,
            [artifactId],
            [revisionId],
            2,
            2,
            2,
            [recordA.Id, recordB.Id],
            [],
            [],
            0,
            1,
            0,
            0);
        Assert(LocationCoverageReport.Create(
                closedManifest,
                [gappedFamily],
                categories,
                terminologyCoverage,
                hierarchyCoverage,
                [],
                []).Status == LocationCoverageStatus.Partial,
            "Parser errors mechanically prevent a completion claim even under a closed manifest.");
        AssertThrows<ArgumentException>(() => _ = new LocationCoverageReport(
                partialReport.Id,
                openManifest,
                LocationCoverageStatus.CompleteForRegisteredSources,
                partialReport.SourceFamilies,
                partialReport.SemanticCategories,
                partialReport.Terminology,
                partialReport.Hierarchy,
                partialReport.Relationships,
                partialReport.Unresolved),
            "Callers cannot relabel Partial coverage as complete.");

        var unsupportedManifestId = LocationCoverageManifestId.DeriveV1(
            scope,
            "unsupported-1",
            false,
            candidateValidation,
            []);
        var unsupportedManifest = new LocationCoverageManifest(
            unsupportedManifestId,
            scope,
            "unsupported-1",
            false,
            candidateValidation,
            []);
        var unsupportedReport = LocationCoverageReport.Create(
            unsupportedManifest,
            [],
            [],
            new LocationTerminologyCoverage(0, 0, 0, 0, 0),
            new LocationHierarchyCoverage(0, 0, 0, 0, true),
            [],
            []);
        Assert(unsupportedReport.Status == LocationCoverageStatus.Unsupported,
            "No applicable registered source family and no output yields Unsupported deterministically.");

        var selectorQuery = new LocationSelectorQuery(
            new CatalogCompositionId("grid.fixture.composition"),
            null,
            [futureRole, LocationSemanticRoles.AreaZone],
            "  Exact Search  ",
            true,
            false);
        Assert(selectorQuery.SearchText == "  Exact Search  " &&
               selectorQuery.LocationSemanticRoleIds.SequenceEqual(
                   selectorQuery.LocationSemanticRoleIds.OrderBy(value => value.Value, StringComparer.Ordinal)),
            "Selector search text remains exact while role filters are ordered deterministically.");
        var prohibitedNames = new[] { "Account", "Profile", "Installation", "Other", "Ticket" };
        var locationContractTypes = new[]
        {
            typeof(SourceNativeLocationTypeAssertion),
            typeof(LocationSemanticClassificationAssertion),
            typeof(LocationCoverageManifest),
            typeof(LocationCoverageReport),
            typeof(LocationSelectorQuery),
            typeof(LocationSelectorResult),
        };
        Assert(locationContractTypes.SelectMany(value => value.GetProperties())
                .All(property => prohibitedNames.All(name =>
                    !property.Name.Contains(name, StringComparison.OrdinalIgnoreCase))),
            "Canonical Location and selector contracts contain no account/profile/install/ticket/Other vocabulary inputs.");

        var registration = new CanonicalCatalogRegistration(
            new CatalogSourceRecord(sourceId, KnowledgeSourceKind.LocalGameDistribution, sourceNative),
            [new SourceArtifactRecord(artifactId, artifactDigest)],
            new CatalogSourceRevisionRecord(revisionId, sourceId, null, [artifactId]),
            [recordA],
            [],
            [],
            [new CatalogFileEvidenceReceipt(receiptId, fileReceipt)],
            [],
            [])
        {
            SourceNativeLocationTypeAssertions = [nativeTypeAssertion],
            LocationSemanticClassificationAssertions = [classification],
            RecordLifecycleAssertions = [lifecycle],
        };
        Assert(registration.SourceNativeLocationTypeAssertions.Single().Id == nativeTypeId &&
               registration.LocationSemanticClassificationAssertions.Single().Id == classificationId &&
               registration.RecordLifecycleAssertions.Single().Id == lifecycleId,
            "Canonical registration carries additive Location assertions without changing existing registration construction.");

        Console.WriteLine($"PASS  Universal canonical Location contract ({checks} checks).");
        return checks;
    }

    private static CanonicalKnowledgeRecord CreateRecord(
        GameId gameId,
        SourceNativeVersion gameVersion,
        CatalogSourceRevisionId revisionId,
        string exactIdentity)
    {
        var native = SourceNativeIdentifier.FromExactUtf8("fixture.location.record", "CMapZone", exactIdentity);
        var nativeId = NativeRecordIdentityId.DeriveV1(gameId, native);
        var id = KnowledgeRecordId.DeriveV1(
            gameId,
            gameVersion,
            null,
            revisionId,
            KnowledgeKind.Location,
            nativeId);
        return new(id, gameId, gameVersion, null, revisionId, KnowledgeKind.Location, nativeId, native);
    }

    private static RelationshipAssertion Relationship(
        CanonicalKnowledgeRecord subject,
        CanonicalKnowledgeRecord target,
        CatalogSourceRevisionId revisionId,
        RelationshipSemanticId semanticId,
        string sourceNativeType) =>
        new(
            subject.Id,
            revisionId,
            semanticId,
            sourceNativeType,
            $"/{sourceNativeType}",
            target.NativeIdentity,
            target.Id);
}
