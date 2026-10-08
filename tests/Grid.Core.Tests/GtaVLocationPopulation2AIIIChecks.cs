using System.Collections.Immutable;

using System.Text.Json;

using Grid.Core.Models;

using Grid.Core.Services;

using Grid.GtaV.Knowledge;



/// <summary>GRID 2.A.III — GTA V Enhanced Location population via MDBO production path (candidate-only).</summary>

internal static class GtaVLocationPopulation2AIIIChecks

{

    public static int Run() =>

        RunAsync(LocateRepositoryRoot(), Path.Combine(LocateRepositoryRoot(), ".tmp", "grid-contract2-registration-20261003",

            "proof", "location-population-2a-iii-" + Guid.NewGuid().ToString("N"))).GetAwaiter().GetResult();



    public static async Task<int> RunAsync(string repositoryRoot, string outputRoot)

    {

        repositoryRoot = Path.GetFullPath(repositoryRoot);

        outputRoot = Path.GetFullPath(outputRoot);

        var allowed = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp/grid-contract2-registration-20261003/proof"));

        if (outputRoot != allowed && !outputRoot.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))

            throw new ArgumentException("Location population proof output must remain inside the approved Contract 2 proof directory.");

        Directory.CreateDirectory(outputRoot);



        var checks = 0;

        void Check(bool condition, string reason)

        {

            if (!condition) throw new InvalidOperationException("GTA V Location population 2.A.III: " + reason);

            checks++;

        }



        var references = Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references");

        var index = GtaVLocationHierarchyCorpusIndex.LoadFromRepository(references);
        var losSantosWiki = File.ReadAllBytes(Path.Combine(references, "los-santos-419777.normalized.txt"));
        var grandSenoraWiki = File.ReadAllBytes(Path.Combine(references, "grand-senora-desert-395939.normalized.txt"));
        Check(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantosWiki, "Downtown Los Santos", "divided-neighbourhoods", "Vespucci", out _),
            "False join-index parent Vespucci is rejected against Downtown Los Santos upstream section subject.");
        Check(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenoraWiki, "Grand Senora Desert", "town-located-inside-desert", "Grand Senora", out _),
            "Shortened desert parent Grand Senora must not locate upstream containment or correlate an edge.");
        Check(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenoraWiki, "Grand Senora Desert", "town-located-inside-desert", "Grand", out _),
            "Prefix desert parent Grand must not locate upstream containment or correlate an edge.");
        Check(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenoraWiki, "Grand Senora Desert", "town-located-inside-desert", "Grand Senora Desert", out var sandyShoresUpstream)
                && sandyShoresUpstream.SequenceEqual(ImmutableArray.Create("Sandy Shores"), StringComparer.Ordinal),
            "Complete desert parent Grand Senora Desert locates Sandy Shores for join-index correlation.");

        var (activeCatalogStorePath, activePackage) = await LoadActiveGtaCatalogAsync();

        Check(activePackage.Manifest.GameScope.GameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId,

            "Active catalog binding targets GTA V Enhanced.");

        var baselinePayload = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(activePackage.Payload);

        var baselinePackage = activePackage;



        var profileId = new ProfileId("profile.location-population-2a-iii");

        var sources = GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index);

        Check(sources.Length == 8, "Admitted hierarchy sources include wiki snapshots and native zone table.");

        Check(sources.All(s => s.Source.Kind == KnowledgeSourceKind.ReferenceProvider),

            "All admitted Location hierarchy sources are reference-verified providers.");

        Check(sources.All(s => !s.Source.Uri.Contains("grid.thewreck.org", StringComparison.OrdinalIgnoreCase)),

            "No opaque GRID-hosted containment source is admitted as relationship evidence.");



        var productionAdapter = new GtaVLocationHierarchyRegistrationEvidenceAdapter(

            baselinePayload, index, profileId, baselinePackage.Id);

        CanonicalRelationshipRegistrationProductionBindings.EnsureProductionEvidenceAdapter(productionAdapter);

        Check(productionAdapter.Id == GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId,

            "Production evidence adapter coordinates match the pinned registration manifest.");



        var registrationCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(

            productionAdapter, sources, GtaVLocationHierarchyRegistrationRules.Create(), [baselinePackage]);

        Check(registrationCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished,

            "MDBO composer candidate remains NOT_PUBLISHED.");

        var correlated = registrationCandidate.Relationships

            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();

        var unresolvedRulings = registrationCandidate.Rulings

            .Where(r => r.Stage == "relationship" && r.Outcome == RegistrationOutcome.Unresolved).ToArray();

        var rejectedRulings = registrationCandidate.Rulings

            .Where(r => r.Stage == "relationship" && r.Outcome is RegistrationOutcome.Rejected or RegistrationOutcome.Ambiguous).ToArray();

        Check(correlated.Length == index.ExpectedRelationshipCount,

            "Correlated hierarchy edges match the pinned relationship budget.");

        Check(registrationCandidate.Entities.Length == 36,

            "Verified Contract 2 candidate admits thirty-six Location entities.");

        Check(registrationCandidate.Entities.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() ==

              registrationCandidate.Entities.Length,

            "No duplicate canonical entity identities.");

        Check(registrationCandidate.Entities.All(e => e.Selector == "Location"),

            "Every populated Location entity uses the Location selector.");



        var evidenceById = registrationCandidate.Input.Evidence.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);

        var relationshipClaims = registrationCandidate.Input.Evidence.Relationships.ToDictionary(r => r.Id, StringComparer.Ordinal);

        Check(correlated.All(r =>
                !string.IsNullOrWhiteSpace(r.SourceId) &&
                !string.IsNullOrWhiteSpace(r.SourceNativeRelationshipType) &&
                !string.IsNullOrWhiteSpace(r.SourceFieldPath) &&
                r.EvidenceIds.Length > 0 &&
                !string.IsNullOrWhiteSpace(r.ProvenanceReason) &&
                relationshipClaims.TryGetValue(r.Id, out var claim) &&
                claim.SubjectKey != claim.TargetKey &&
                string.Equals(claim.SubjectKey, registrationCandidate.Entities.Single(e => e.Id == r.SubjectId).SourceKeys[0], StringComparison.Ordinal) &&
                string.Equals(claim.TargetKey, registrationCandidate.Entities.Single(e => e.Id == r.TargetId).SourceKeys[0], StringComparison.Ordinal) &&
                string.Equals(claim.SourceId, r.SourceId, StringComparison.Ordinal) &&
                string.Equals(claim.SourceFieldPath, r.SourceFieldPath, StringComparison.Ordinal) &&
                string.Equals(claim.SourceNativeRelationshipType, r.SourceNativeRelationshipType, StringComparison.Ordinal) &&
                r.EvidenceIds.Any(id =>
                    evidenceById[id].Locator == r.SourceFieldPath &&
                    evidenceById[id].SourceId == r.SourceId) &&
                NativeBridgeEvidenceClosure(registrationCandidate, r, evidenceById, index)),
            "Every correlated edge is claim-exact; native-bridged endpoints close over the zone table.");

        Check(index.ResolveForRegistration(baselinePayload).UnresolvedEdges.IsEmpty,

            "Pinned active catalog resolves every hierarchy endpoint without UNRESOLVED rulings.");

        Check(CrossSourceRelationshipEvidenceSubstitutionRejected(),

            "Cross-source relationship evidence substitution is rejected by the registration engine.");

        Check(await UnresolvedEndpointYieldsRulingAsync(index, baselinePayload, baselinePackage, sources, profileId),

            "Missing hierarchy endpoints yield UNRESOLVED rulings without aborting registration.");

        var registrationRows = index.ResolveForRegistration(baselinePayload).CorrelatedRows;
        var hierarchyRows = index.Resolve(baselinePayload);

        Check(registrationCandidate.Entities.Length > 0 &&
              registrationCandidate.Entities.Length >= hierarchyRows
                  .SelectMany(r => new[] { r.Child.EntityKey, r.Parent.EntityKey })
                  .Distinct(StringComparer.Ordinal)
                  .Count(),
            "Entity admissions cover every resolved hierarchy endpoint identity.");



        var navContext = new RegistrationNavigationContext(

            ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value,

            profileId.Value,

            "en-US");

        var prepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(

            Path.Combine(outputRoot, "prepared-registration"), registrationCandidate, navContext, [baselinePackage]);

        using var reader = await CanonicalRegistrationPreparedReader.OpenAsync(

            prepared.DirectoryPath, prepared.Digest, navContext);

        Check(reader.Descriptor.PublicationState == CanonicalRegistrationEncoding.NotPublished,

            "Graph-derived prepared navigation remains NOT_PUBLISHED.");

        Check(reader.Descriptor.Paths.Length == ExpectedPreparedPaths && reader.Descriptor.Views.Length > 0,

            "Prepared navigation materializes selector roots, four frame levels, Miscellaneous and thirty-nine entity paths.");

        var levelTree = await VerifyLevelSlotShapeAsync(registrationCandidate, reader, baselinePackage, Check);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "level-slot-prepared-tree.txt"), levelTree);
        Console.WriteLine(levelTree);



        var fixtureRoot = Path.Combine(outputRoot, "mdobo-refresh");

        Directory.CreateDirectory(fixtureRoot);

        var storePath = Path.Combine(fixtureRoot, "catalog.json");

        var store = new JsonCanonicalKnowledgeCatalogStore(storePath);

        var (refreshBaselinePackage, _) =

            GtaVLocationHierarchyRegistrationProofFixtures.CreateImportedBaselineWithoutHierarchy(repositoryRoot);

        var import = await store.ImportPackageAsync(0, refreshBaselinePackage);

        Check(import.Status == CanonicalCatalogImportStatus.Imported,

            "Hierarchy-free baseline imports into isolated catalog store for refresh author.");

        var catalogBefore = await store.LoadAsync();

        var storeRevisionBefore = catalogBefore.Snapshot.Revision;

        var refreshOriginPackage = catalogBefore.Snapshot.FindImportedPackage(refreshBaselinePackage.Id)

            ?? throw new InvalidOperationException("Refresh baseline package is absent from the isolated store.");

        var refreshBaselinePayload = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(refreshOriginPackage.Payload);

        var auditedCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(

            new GtaVLocationHierarchyRegistrationEvidenceAdapter(refreshBaselinePayload, index, profileId, refreshOriginPackage.Id),

            sources,

            GtaVLocationHierarchyRegistrationRules.Create(),

            [refreshOriginPackage]);

        var auditedCorrelated = auditedCandidate.Relationships

            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();

        Check(auditedCandidate.Entities.Length == 36 && auditedCorrelated.Length == index.ExpectedRelationshipCount,

            "Refresh baseline reproduces the verified thirty-six entity and thirty-six edge registration candidate.");

        var auditedPrepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(

            Path.Combine(fixtureRoot, "audited-prepared-registration"),

            auditedCandidate,

            navContext,

            [refreshOriginPackage]);

        using var auditedReader = await CanonicalRegistrationPreparedReader.OpenAsync(

            auditedPrepared.DirectoryPath, auditedPrepared.Digest, navContext);



        var observationContributor = new GtaVEnhancedRegistrationKnowledgeRefreshContributor();

        var mdoboContributor = new CanonicalRegistrationMdboKnowledgeRefreshContributor(

            observationContributor, new GtaVRegistrationMdboRefreshAuthor());

        var refreshContext = new RegistrationRefreshContext(

            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,

            new InstallationId("installation.location-population-2a-iii"),

            profileId,

            refreshBaselinePackage.Id,

            storePath,

            Path.Combine(fixtureRoot, "prepared-orchestrator"),

            Path.Combine(fixtureRoot, "binding.json"),

            Path.Combine(fixtureRoot, "receipts"),

            null,

            false,

            new RegistrationRefreshResourcePaths(

                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/vinewood-411759.normalized.txt"),

                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/cfx-zones-ad60ae80.md")));

        var observation = observationContributor.Observe(refreshContext);

        var contribution = await mdoboContributor.TryRebuildAsync(refreshContext, observation, catalogBefore, null, default);

        Check(contribution is not null, "GtaVRegistrationMdboRefreshAuthor produced a rebuild contribution.");

        Check(contribution!.CandidateReadyWithoutImport,

            "CanonicalRegistrationMdboKnowledgeRefreshContributor terminates at CandidateReadyWithoutImport.");

        Check(contribution.Package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate,

            "Refresh contribution package remains Candidate validation status.");

        Check(contribution.AdmittedLocationRelationships == index.ExpectedRelationshipCount &&

              contribution.RejectedLocationRelationships == 0,

            "Refresh contribution accounting reflects thirty-six admitted hierarchy relationships without stale rejections.");

        var importablePayload = contribution.Package.Payload;
        var candidateEntityIds = auditedCandidate.Entities.Select(e => e.Id).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        static string RelationshipEndpointKey(RegisteredCanonicalRelationship relationship) =>
            relationship.SourceFieldPath + "\0" + relationship.SourceNativeRelationshipType + "\0" +
            relationship.SubjectId + "\0" + relationship.TargetId;
        var candidateRelationshipEndpointKeys = auditedCorrelated
            .Select(RelationshipEndpointKey)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
        var packageCandidateRecordIds = importablePayload.KnowledgeRecords
            .Select(r => r.Id.Value)
            .Where(id => candidateEntityIds.Contains(id, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
        Check(packageCandidateRecordIds.SequenceEqual(candidateEntityIds, StringComparer.Ordinal),
            "Importable package preserves the literal thirty-six candidate entity ids as knowledge record ids.");
        Check(auditedCandidate.Entities
                .Where(e => string.Equals(e.NativeNamespace, GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace, StringComparison.Ordinal))
                .All(e => KnowledgeRecordId.IsRegistrationEntityBacked(e.Id)),
            "Reference geography entities remain registration-backed ids without knowledge-record reminting.");
        var hierarchyRelationships = importablePayload.RelationshipAssertions
            .Where(r => r.SemanticId == LocationRelationshipSemantics.ContainedBy &&
                        r.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal))
            .ToArray();
        Check(hierarchyRelationships.Length == 36 && index.ExpectedRelationshipCount == 36,
            "Importable refresh package carries thirty-six hierarchy relationships.");
        var packageRelationshipEndpointKeys = hierarchyRelationships
            .Select(r => r.SourceFieldPath + "\0" + r.SourceNativeRelationshipType + "\0" +
                         r.SubjectKnowledgeRecordId.Value + "\0" + r.ResolvedTargetKnowledgeRecordId!.Value.Value)
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
        Check(packageRelationshipEndpointKeys.SequenceEqual(candidateRelationshipEndpointKeys, StringComparer.Ordinal),
            "Importable refresh package relationship keys and endpoints match the audited registration candidate literally.");
        Check(auditedCorrelated.All(relationship =>
                hierarchyRelationships.Any(assertion =>
                    string.Equals(assertion.SourceFieldPath, relationship.SourceFieldPath, StringComparison.Ordinal) &&
                    string.Equals(assertion.SourceNativeRelationshipType, relationship.SourceNativeRelationshipType, StringComparison.Ordinal) &&
                    string.Equals(assertion.SubjectKnowledgeRecordId.Value, relationship.SubjectId, StringComparison.Ordinal) &&
                    string.Equals(assertion.ResolvedTargetKnowledgeRecordId!.Value.Value, relationship.TargetId, StringComparison.Ordinal))),
            "Every hierarchy relationship assertion preserves literal SubjectId and TargetId registration keys.");
        static bool HasPinnedRevision(CanonicalCatalogPayload payload, SourceArtifactId artifactId, string revision) =>
            payload.ReferenceEvidenceReceipts.Any(receipt =>
                receipt.Receipt.ResponseArtifactId == artifactId &&
                receipt.Receipt.NativeRevisionIdentity is { } nativeRevision &&
                string.Equals(nativeRevision.ExactRepresentation, revision, StringComparison.Ordinal));
        Check(HasPinnedRevision(importablePayload, index.GrandSenoraWiki.Id, "395939"),
            "Grand Senora Desert reference receipts carry pinned revision 395939.");
        Check(HasPinnedRevision(importablePayload, index.EastLosSantosWiki.Id, "409804"),
            "East Los Santos reference receipts carry pinned revision 409804.");
        Check(HasPinnedRevision(importablePayload, index.BlaineCountyWiki.Id, "406414"),
            "Blaine County reference receipts carry pinned revision 406414.");
        Check(HasPinnedRevision(importablePayload, index.SanAndreasWiki.Id, "416219"),
            "San Andreas reference receipts carry pinned revision 416219.");
        Check(HasPinnedRevision(importablePayload, index.VespucciWiki.Id, "396784"),
            "Vespucci reference receipts carry pinned revision 396784.");
        Check(!importablePayload.ReferenceEvidenceReceipts.Any(receipt =>
                receipt.Receipt.ResponseArtifactId != index.NativeTable.Id &&
                receipt.Receipt.NativeRevisionIdentity is { } nativeRevision &&
                string.Equals(nativeRevision.ExactRepresentation, GtaVLocationHierarchyCorpusIndex.CfxRevision, StringComparison.Ordinal)),
            "Wiki geography sources must not fall back to the CFX revision token.");
        Check(auditedCorrelated.All(r =>
                importablePayload.ReferenceEvidenceReceipts.Any(receipt =>
                    receipt.Receipt.ResponseArtifactId.Value == r.SourceId &&
                    receipt.Receipt.ResponseFieldPath == r.SourceFieldPath)),
            "Every correlated edge retains upstream reference provenance in the importable package.");
        var referenceRecord = importablePayload.KnowledgeRecords.First(r =>
            KnowledgeRecordId.IsRegistrationEntityBacked(r.Id.Value) &&
            string.Equals(r.NativeIdentity.Namespace, GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace, StringComparison.Ordinal));
        Check(string.Equals(
                referenceRecord.Id.Value,
                KnowledgeRecordId.DeriveRegistrationBackedLocationEntityId(referenceRecord),
                StringComparison.Ordinal),
            "Registration-backed location record id matches canonical identity rederivation.");
        var adversarialRegistrationEntityId = KnowledgeRecordId.RegistrationEntityPrefix + new string('f', 64);
        var tamperedRecord = referenceRecord with { Id = new KnowledgeRecordId(adversarialRegistrationEntityId) };
        Check(!KnowledgeRecordId.MatchesPackageIdentity(tamperedRecord),
            "Adversarial registration entity id with valid prefix and hex is rejected.");
        var tamperedPayload = importablePayload with
        {
            KnowledgeRecords = importablePayload.KnowledgeRecords
                .Select(record => record.Id == referenceRecord.Id ? tamperedRecord : record)
                .ToImmutableArray(),
        };
        var tamperedPackage = contribution.Package with { Payload = tamperedPayload };
        Check(!CanonicalCatalogPackageKernel.Verify(tamperedPackage).IsStructurallyValid,
            "Package kernel rejects tampered registration-backed knowledge record ids.");
        Check(auditedReader.Descriptor.Paths.Length == ExpectedPreparedPaths,
            "Prepared navigation from the verified refresh-baseline candidate materializes the level-slotted path set.");

        var repStorePath = Path.Combine(fixtureRoot, "package-import-store.json");
        var repStore = new JsonCanonicalKnowledgeCatalogStore(repStorePath);
        var repImport = await repStore.ImportPackageAsync(0, contribution.Package);
        Check(repImport.Status == CanonicalCatalogImportStatus.Imported,
            "Importable refresh package imports into an isolated catalog store for reprepare verification.");
        var repCatalog = await repStore.LoadAsync();
        var importedPackage = repCatalog.Snapshot.FindImportedPackage(contribution.Package.Id)
            ?? throw new InvalidOperationException("Imported refresh package is absent from the store.");
        var repBaseline = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(importedPackage.Payload);
        var repCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            new GtaVLocationHierarchyRegistrationEvidenceAdapter(repBaseline, index, profileId, importedPackage.Id),
            sources,
            GtaVLocationHierarchyRegistrationRules.Create(),
            [refreshBaselinePackage, importedPackage]);
        var repCorrelated = repCandidate.Relationships
            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();
        Check(repCandidate.Entities.Select(e => e.Id).OrderBy(v => v, StringComparer.Ordinal)
                .SequenceEqual(candidateEntityIds, StringComparer.Ordinal),
            "Re-registration after package import preserves the exact candidate entity ID set.");
        Check(repCorrelated.Select(RelationshipEndpointKey).OrderBy(v => v, StringComparer.Ordinal)
                .SequenceEqual(candidateRelationshipEndpointKeys, StringComparer.Ordinal),
            "Re-registration after package import preserves exact relationship keys and endpoints.");
        var repPrepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(
            Path.Combine(outputRoot, "reprepared-registration"),
            repCandidate,
            navContext,
            [refreshBaselinePackage, importedPackage]);
        using var repReader = await CanonicalRegistrationPreparedReader.OpenAsync(
            repPrepared.DirectoryPath, repPrepared.Digest, navContext);
        Check(repReader.Descriptor.Paths.Length == ExpectedPreparedPaths,
            "Reprepared navigation after package import retains the level-slotted path set.");
        Check(PathProvenanceSignatures(auditedReader.Descriptor).OrderBy(v => v, StringComparer.Ordinal)
                .SequenceEqual(PathProvenanceSignatures(repReader.Descriptor).OrderBy(v => v, StringComparer.Ordinal), StringComparer.Ordinal),
            "Prepared path entity and parent-entity provenance match after package import and reprepare.");



        var orchestrator = new CanonicalRegistrationRefreshOrchestrator([mdoboContributor], new CanonicalTerminologyLocalePreference("en-US", []));

        var refresh = await orchestrator.RefreshAsync(refreshContext, null, default);

        Check(refresh.Status == RegistrationRefreshStatus.Completed &&

              refresh.Mode == RegistrationRefreshMode.KnowledgeRebuild &&

              refresh.PublishedPackageId is null,

            "Refresh orchestrator completes without catalog import or runtime publication.");

        var catalogAfter = await store.LoadAsync();

        Check(catalogAfter.Snapshot.Revision == storeRevisionBefore,

            "Candidate-ready refresh does not mutate catalog store revision.");



        var paths = BuildRepresentativePaths(registrationCandidate, correlated);

        var sandyEntityId = registrationCandidate.Entities
            .Single(e => e.Names.Any(n => !n.IsAlias && n.Locale == "en-US" && n.Value == "Sandy Shores")).Id;
        var sandyParentIds = correlated.Where(r => r.SubjectId == sandyEntityId).Select(r => r.TargetId).Distinct(StringComparer.Ordinal).ToArray();
        Check(sandyParentIds.Length == 2, "Sandy Shores correlates under Grand Senora Desert and Blaine County.");
        var sandyPathParents = paths
            .Select(p => (Path: (string)p.GetType().GetProperty("path")!.GetValue(p)!, ParentId: (string?)p.GetType().GetProperty("parentEntityId")!.GetValue(p)))
            .Where(t => t.Path.EndsWith("Sandy Shores", StringComparison.Ordinal))
            .Select(t => t.ParentId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Check(sandyPathParents.Length == 2 && sandyPathParents.All(id => sandyParentIds.Contains(id!, StringComparer.Ordinal)),
            "Representative paths preserve distinct parent-edge provenance for Sandy Shores.");

        var sourceCoverage = BuildSourceCoverage(index, registrationRows, correlated, registrationCandidate);

        var provenanceComplete = index.ResolveForRegistration(baselinePayload).CorrelatedRows.All(r =>
            r.EvidenceArtifact.SourceCoordinate.ExactRepresentation is GtaVLocationHierarchyCorpusIndex.WikiCoordinate
                or GtaVLocationHierarchyCorpusIndex.LosSantosCoordinate
                or GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate
                or GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate
                or GtaVLocationHierarchyCorpusIndex.BlaineCountyCoordinate
                or GtaVLocationHierarchyCorpusIndex.SanAndreasCoordinate
                or GtaVLocationHierarchyCorpusIndex.VespucciCoordinate);

        var candidateIntegrityReady = correlated.Length == index.ExpectedRelationshipCount &&

                                      unresolvedRulings.Length == 0 &&

                                      rejectedRulings.Length == 0 &&

                                      provenanceComplete &&

                                      registrationCandidate.PublicationState == CanonicalRegistrationEncoding.NotPublished;

        var geographyPublicationReady = index.GeographyPublicationReady;
        var geographyBridgeBlocked = index.GeographyBridgeBlockedRows;
        Check(candidateIntegrityReady, "Pinned candidate integrity remains ready at thirty-six correlated edges without import.");

        Check(geographyPublicationReady, "Geography publication is ready when join-index rows correlate as reference geography without ambiguous native bridge blockers.");

        Check(geographyBridgeBlocked.IsEmpty,
            "Geography join index has no ambiguous native-bridge blockers.");

        Check(index.ResolveForRegistration(baselinePayload).CorrelatedRows.Any(r =>
                r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate),
            "East Los Santos reference geography containment edges correlate from the join index.");

        Check(index.ResolveForRegistration(baselinePayload).CorrelatedRows.Any(r =>
                r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.BlaineCountyCoordinate),
            "Blaine County reference geography containment edges correlate from the join index.");

        Check(index.ResolveForRegistration(baselinePayload).CorrelatedRows.Any(r =>
                r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.SanAndreasCoordinate),
            "San Andreas county reference geography containment edges correlate from the join index.");

        Check(index.ResolveForRegistration(baselinePayload).CorrelatedRows.Any(r =>
                r.ParentName == "Los Santos" && r.ChildName == "Davis"),
            "Los Santos city reference parent admits Davis with native DAVIS bridge.");

        (string Parent, string Child, string Type, string Coordinate)[] leadSentenceEdges =
        [
            ("Los Santos", "Vinewood", "reference.district-in-city", GtaVLocationHierarchyCorpusIndex.WikiCoordinate),
            ("Los Santos", "Downtown", "reference.district-area-of-city", GtaVLocationHierarchyCorpusIndex.LosSantosCoordinate),
            ("Los Santos", "East Los Santos", "reference.sector-located-in-city", GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate),
            ("Blaine County", "Grand Senora Desert", "reference.desert-located-in-county", GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate),
            ("Los Santos", "Vespucci", "reference.neighborhood-in-city", GtaVLocationHierarchyCorpusIndex.VespucciCoordinate),
        ];
        Check(leadSentenceEdges.All(edge => registrationRows.Count(r => r.ParentName == edge.Parent && r.ChildName == edge.Child &&
                r.SourceNativeRelationshipType == edge.Type && r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == edge.Coordinate) == 1),
            "Lead-sentence containment edges correlate once each from their own pinned snapshot.");
        var eastLosSantosWiki = File.ReadAllBytes(Path.Combine(references, "east-los-santos-409804.normalized.txt"));
        Check(!GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantosWiki, "Downtown Los Santos", "financial-center-of-city", "San Andreas", out _) &&
              !GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(eastLosSantosWiki, "East Los Santos", "sector-located-in-city", "Los", out _) &&
              !GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenoraWiki, "Grand Senora Desert", "desert-located-in-county", "San Andreas", out _) &&
              !GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(losSantosWiki, "Rockford Hills", "financial-center-of-city", "Los Santos", out _),
            "Lead-sentence grammars reject parents the sentence does not name and sections without the sentence.");
        Check(GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(eastLosSantosWiki, "East Los Santos", "sector-located-in-city", "Los Santos", out var elsChild) &&
              elsChild.SequenceEqual(["East Los Santos"]) &&
              GtaVLocationHierarchyCorpusIndex.LocateUpstreamContainmentAssertion(grandSenoraWiki, "Grand Senora Desert", "desert-located-in-county", "Blaine County", out var gsdChild) &&
              gsdChild.SequenceEqual(["Grand Senora Desert"]),
            "Lead-sentence grammars locate the exact subject child for the named container.");



        var report = new

        {

            gridPhase = "2.A.III",

            gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value,

            activeCatalogStorePath,

            pinnedPackageId = baselinePackage.Id.Value,

            checks,

            result = "PASS",

            publicationState = CanonicalRegistrationEncoding.NotPublished,

            candidateReadyWithoutImport = true,

            counts = new

            {

                entitiesAdmitted = registrationCandidate.Entities.Length,

                relationshipsCorrelated = correlated.Length,

                relationshipsUnresolved = unresolvedRulings.Length,

                relationshipsRejected = rejectedRulings.Length,

                expectedRelationships = index.ExpectedRelationshipCount,

                preparedPaths = reader.Descriptor.Paths.Length,

                preparedViews = reader.Descriptor.Views.Length,

            },

            representativeHierarchyPaths = paths,

            unresolvedGaps = unresolvedRulings.Select(r => new

            {

                claimId = r.ClaimId,

                stage = r.Stage,

                outcome = r.Outcome.ToString(),

                reason = r.Reason,

            }).ToArray(),

            rejectedRulings = rejectedRulings

                .Select(r => new { r.ClaimId, r.Stage, r.Outcome, r.Reason })

                .ToArray(),

            sourceCoverage,

            productionPath = new[]

            {

                "MDBO capability registry",

                "CanonicalRelationshipRegistrationMdboComposer",

                "GtaVLocationHierarchyRegistrationEvidenceAdapter",

                "CanonicalRegistrationCandidate",

                "GtaVRegistrationMdboRefreshAuthor",

                "CanonicalRegistrationMdboKnowledgeRefreshContributor",

                "CandidateReadyWithoutImport",

            },

            candidateIntegrityReady = candidateIntegrityReady ? "YES" : "NO",

            candidateIntegrityRationale = candidateIntegrityReady

                ? "All thirty-six evidence-backed containment edges correlate with claim-exact provenance receipts (matching SourceId and SourceFieldPath), reference geography for higher-order labels, native-table closure where uniquely proven, and upstream wiki coordinates; prepared navigation projects from the candidate graph; orchestration stops at CandidateReadyWithoutImport without import or binding publication."

                : $"Candidate-integrity readiness withheld: correlated={correlated.Length}/{index.ExpectedRelationshipCount}, unresolved={unresolvedRulings.Length}, rejected={rejectedRulings.Length}, provenanceComplete={provenanceComplete}.",

            geographyPublicationReady = geographyPublicationReady ? "YES" : "NO",

            geographyPublicationRationale = geographyPublicationReady

                ? "All join-index geography rows locate upstream containment and correlate as reference-verified edges; native zone mapping remains UNRESOLVED only for the pinned higher-order labels without unique CFX bridges."

                : "Geography publication withheld while join-index rows remain ambiguous-native-blocked.",

            unresolvedNativeMappingEnglishNames = GtaVLocationHierarchyCorpusIndex.ReferenceOnlyNativeMappingEnglishNames.OrderBy(v => v, StringComparer.Ordinal).ToArray(),

            geographyBridgeBlockedRows = geographyBridgeBlocked.Select(b => new

            {

                sourceKey = b.SourceKey,

                parentName = b.ParentName,

                sectionPath = b.SectionPath,

                grammar = b.Grammar,

                childCount = b.ChildNames.Length,

                ambiguousNativeBridgeNames = b.AmbiguousNativeBridgeNames.ToArray(),

            }).ToArray(),

        };



        await File.WriteAllBytesAsync(

            Path.Combine(outputRoot, "location-population-2a-iii-report.json"),

            CanonicalRegistrationEncoding.Bytes(report));



        Console.WriteLine(

            $"PASS: {checks} GTA V Enhanced Location population 2.A.III checks; entities={registrationCandidate.Entities.Length}; " +

            $"edges={correlated.Length}/{index.ExpectedRelationshipCount}; unresolved={unresolvedRulings.Length}; NOT_PUBLISHED.");

        return checks;

    }

    /// <summary>Six selector roots + the evidence-backed San Andreas frame + one Miscellaneous + 39 entity paths.</summary>
    private const int ExpectedPreparedPaths = 47;

    private static readonly string[] LevelLabels =
        ["Location", "World", "Continent", "Country", "State", "County/Region", "City", "Town/Neighborhood", "Street", "Structure", "Room"];

    private static async Task<string> VerifyLevelSlotShapeAsync(
        CanonicalRegistrationCandidate candidate,
        CanonicalRegistrationPreparedReader reader,
        CanonicalCatalogPackage package,
        Action<bool, string> check)
    {
        var byName = candidate.Entities.ToDictionary(e => e.Names.Single(n => !n.IsAlias && n.Locale == "en-US").Value, StringComparer.Ordinal);
        var checklist = CanonicalRegistrationChecklist.Load();
        decimal Level(RegisteredCanonicalEntity entity) => checklist.SemanticLevel(entity.Anchor, entity.SemanticLevel);
        string[] counties = ["Blaine County", "Grand Senora Desert", "Los Santos County"];
        string[] cities = ["Davis", "Los Santos"];
        string[] subNeighborhoods =
        [
            "Burton", "Downtown Vinewood", "East Vinewood", "Legion Square", "Mission Row", "Pillbox Hill",
            "Textile City", "Vespucci Beach", "Vespucci Canals", "Vinewood Hills", "West Vinewood",
        ];
        check(counties.All(n => byName[n].Anchor == GtaVLocationHierarchyRegistrationRules.CountyRegionAnchor),
            "Blaine County, Los Santos County and Grand Senora Desert register at L5 County/Region.");
        check(cities.All(n => byName[n].Anchor == GtaVLocationHierarchyRegistrationRules.CityAnchor),
            "Los Santos and Davis register at L6 City from municipality and City-of evidence.");
        check(byName.Where(p => !counties.Contains(p.Key) && !cities.Contains(p.Key))
                .All(p => p.Value.Anchor == GtaVLocationHierarchyRegistrationRules.NeighborhoodAnchor),
            "Remaining thirty-one entities register on the L7 Town/Neighborhood anchor.");
        check(subNeighborhoods.All(n => byName[n].SemanticLevel == GtaVLocationHierarchyRegistrationRules.SubNeighborhoodLevel && Level(byName[n]) == 7.5m) &&
              byName.Where(p => !counties.Contains(p.Key) && !cities.Contains(p.Key) && !subNeighborhoods.Contains(p.Key))
                  .All(p => p.Value.SemanticLevel is null && Level(p.Value) == 7m) &&
              byName.Count(p => p.Value.SemanticLevel is not null) == subNeighborhoods.Length,
            "Eleven district-subdivision neighbourhoods register at L7.5; twenty remain integer L7.");
        check(Level(byName["Vespucci"]) == 7m && Level(byName["Rockford Hills"]) == 7m && Level(byName["Downtown"]) == 7m,
            "Subdivided districts Vespucci, Rockford Hills and Downtown stay at L7 (sub-level is per child, not inherited).");
        check(subNeighborhoods.All(n => byName[n].EvidenceIds.Any(id => id.StartsWith("evidence:relationship:", StringComparison.Ordinal))),
            "Every L7.5 assignment is backed by its own subdivision sentence evidence.");
        check(candidate.Input.Evidence.Evidence.Any(e => e.Id == "evidence:level:Southern San Andreas / Blaine County" &&
                byName["Blaine County"].EvidenceIds.Contains(e.Id)),
            "Blaine County county level is evidence-backed by the San Andreas county section.");
        check(byName["Downtown"].Names.Any(n => n.IsAlias && n.Value == "Downtown Los Santos"),
            "Downtown retains its source section identity Downtown Los Santos.");

        var rows = new List<(RegistrationNavigationRow Row, int Depth)>();
        var root = reader.Descriptor.Views.Single(v => v.Selector == "Location" && v.Dimension is null).RootPathId;
        var text = new System.Text.StringBuilder();
        async Task Walk(string pathId, int depth)
        {
            string? cursor = null;
            do
            {
                var page = await reader.GetChildrenAsync(pathId, cursor);
                foreach (var child in page.Children)
                {
                    rows.Add((child, depth));
                    var level = child.EntityId is { } id
                        ? $" [L{Level(candidate.Entities.Single(e => e.Id == id))}]"
                        : child.Label == CanonicalRegistrationPreparationBuilder.MiscellaneousLabel ? " (presentation)" : " [frame]";
                    text.Append(new string(' ', depth * 3)).Append("└─ ").Append(child.Label).Append(level).AppendLine();
                    await Walk(child.PathId, depth + 1);
                }
                cursor = page.ContinuationCursor;
            } while (cursor is not null);
        }
        text.AppendLine("Location [L0]");
        await Walk(root, 0);
        string[] Children(string? parentLabel, int depth) => rows
            .Where(r => r.Depth == depth && (parentLabel is null || rows.Any(p => p.Row.PathId == r.Row.ParentPathId && p.Row.Label == parentLabel)))
            .Select(r => r.Row.Label).ToArray();
        check(candidate.Input.RuleSet.LevelFrame is [{ Label: "San Andreas", EvidenceIds.Length: > 0 }],
            "The only declared level frame is the evidence-backed San Andreas state; unsourced world/continent/country levels are skipped.");
        check(Children(null, 0).SequenceEqual(["San Andreas"]) && !rows.Any(r => r.Row.Label is "Earth" or "North America" or "United States"),
            "Prepared root composes San Andreas directly without synthetic Earth/North America/United States frames.");
        check(Children("San Andreas", 1).SequenceEqual(["Blaine County", "Los Santos County", "Miscellaneous"]),
            "San Andreas lists L5 counties followed by Miscellaneous.");
        check(Children("Miscellaneous", 2).SequenceEqual(["Rockford Hills"]),
            "Only Rockford Hills, which lacks any pinned containment sentence, remains under Miscellaneous.");
        check(Children("Vespucci", 4).SequenceEqual(["Vespucci Beach", "Vespucci Canals"]),
            "Vespucci keeps Vespucci Beach and Vespucci Canals beneath it under Los Santos.");
        check(Children("Los Santos County", 2).SequenceEqual(["Chumash", "Davis", "Los Santos"]),
            "Los Santos County composes city and town children.");
        check(Children("Los Santos", 3).SequenceEqual(["Davis", "Downtown Los Santos", "East Los Santos", "Vespucci", "Vinewood"]),
            "Los Santos composes Davis, Downtown Los Santos, East Los Santos, Vespucci and Vinewood from pinned sentences.");
        check(Children("Blaine County", 2).Contains("Grand Senora Desert") && Children("Grand Senora Desert", 3).SequenceEqual(["Sandy Shores"]),
            "Grand Senora Desert sits under Blaine County and keeps Sandy Shores.");
        check(rows.Count(r => r.Row.Label == "Sandy Shores") == 2 && rows.Count(r => r.Row.Label == "East Vinewood") == 2,
            "Multi-parent Sandy Shores and East Vinewood retain both parent paths.");
        check(!rows.Any(r => LevelLabels.Contains(r.Row.Label, StringComparer.Ordinal)),
            "No synthetic level placeholder labels (World, County/Region, ...) appear as rows.");
        check(rows.Where(r => r.Row.EntityId is null).All(r => !r.Row.Selectable),
            "Frame and Miscellaneous nodes are not selectable.");
        check(rows.Count(r => r.Row.EntityId is not null) == 39 && rows.Select(r => r.Row.EntityId).OfType<string>().Distinct().Count() == 36,
            "Thirty-nine entity paths cover all thirty-six entities.");

        var descriptor = new PreparedCanonicalGenerationDescriptor(1, package.Id, package.Manifest.CatalogRevisionId,
            new CatalogCompositionId("composition.level-slot-2a-iii"), 1, package.Manifest.GameScope.GameId,
            CatalogValidationStatus.Candidate, null, null, null, new string('f', 64),
            new CanonicalTerminologyLocalePreference("en-US", []), []);
        var composer = new RegistrationPreparedNavigationComposer(reader, descriptor);
        var rootPage = (await composer.QueryAsync(null, null, CancellationToken.None))!.Result;
        check(rootPage.CurrentNode.DisplayAnchor == "Location" && rootPage.ImmediateChildren.Select(c => c.DisplayAnchor).SequenceEqual(["San Andreas"]),
            "Composer root is the prepared Location page whose only child is San Andreas (no synthetic World/Earth).");
        var sanAndreas = (await composer.QueryAsync(rootPage.ImmediateChildren.Single().PathId, null, CancellationToken.None))!.Result;
        check(sanAndreas.ImmediateChildren.Select(c => c.DisplayAnchor).SequenceEqual(["Blaine County", "Los Santos County", "Miscellaneous"]),
            "Composer descends San Andreas to its populated county level.");
        var cursorPage = sanAndreas;
        foreach (var label in new[] { "Los Santos County", "Los Santos" })
            cursorPage = (await composer.QueryAsync(cursorPage.ImmediateChildren.Single(c => c.DisplayAnchor == label).PathId, null, CancellationToken.None))!.Result;
        check(cursorPage.ImmediateChildren.Select(c => c.DisplayAnchor).SequenceEqual(["Davis", "Downtown Los Santos", "East Los Santos", "Vespucci", "Vinewood"]),
            "Composer lists Los Santos districts at the town/neighborhood level.");
        var downtown = cursorPage.ImmediateChildren.Single(c => c.DisplayAnchor == "Downtown Los Santos");
        check(downtown.IsSelectable && composer.ValidateSelection(downtown.PathId, downtown.KnowledgeRecordId!.Value) &&
              !composer.ValidateSelection(sanAndreas.CurrentPathId, downtown.KnowledgeRecordId!.Value),
            "Composer validates Downtown Los Santos selection and rejects frame-path selection.");
        var misc = (await composer.QueryAsync(sanAndreas.ImmediateChildren.Single(c => c.DisplayAnchor == "Miscellaneous").PathId, null, CancellationToken.None))!.Result;
        check(misc.ImmediateChildren.Select(c => c.DisplayAnchor).SequenceEqual(["Rockford Hills"]),
            "Composer Miscellaneous contains only Rockford Hills.");
        var vespucci = (await composer.QueryAsync(cursorPage.ImmediateChildren.Single(c => c.DisplayAnchor == "Vespucci").PathId, null, CancellationToken.None))!.Result;
        check(vespucci.ImmediateChildren.Select(c => c.DisplayAnchor).SequenceEqual(["Vespucci Beach", "Vespucci Canals"]) &&
              vespucci.ImmediateChildren.All(c => c.IsSelectable),
            "Composer descends Los Santos > Vespucci to selectable L7.5 Vespucci Beach and Vespucci Canals.");
        return text.ToString();
    }

    private static IEnumerable<string> PathProvenanceSignatures(RegistrationPreparedDescriptor descriptor)
    {
        var byId = descriptor.Paths.ToDictionary(path => path.PathId, StringComparer.Ordinal);
        string? ParentEntityId(string? parentPathId) =>
            parentPathId is null ? null : byId[parentPathId].EntityId;
        return descriptor.Paths.Select(path =>
            (path.EntityId ?? string.Empty) + "\0" + (ParentEntityId(path.ParentPathId) ?? string.Empty));
    }

    private static async Task<(string StorePath, CanonicalCatalogPackage Package)> LoadActiveGtaCatalogAsync()

    {

        var dataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");

        if (string.IsNullOrWhiteSpace(dataRoot))

            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid");

        dataRoot = Path.GetFullPath(dataRoot);

        var catalogDir = Path.Combine(dataRoot, "catalogs");

        var storePath = Path.Combine(catalogDir, "shared-canonical-library.v5.json");

        var bindingPath = Path.Combine(catalogDir, "canonical-runtime-binding.v1.json");

        if (!File.Exists(storePath) || !File.Exists(bindingPath))

            throw new InvalidOperationException("Active GTA V Enhanced catalog store is unavailable under Grid data root: " + catalogDir);

        using var bindingJson = JsonDocument.Parse(await File.ReadAllTextAsync(bindingPath));

        var packageIdValue = bindingJson.RootElement.GetProperty("packageId").GetString()

            ?? throw new InvalidOperationException("Active catalog binding is missing packageId.");

        var packageId = new CatalogPackageId(packageIdValue);

        var loaded = await new JsonCanonicalKnowledgeCatalogStore(storePath).LoadAsync();

        if (!loaded.IsValid)

            throw new InvalidOperationException("Active catalog store failed validation.");

        var package = loaded.Snapshot.FindImportedPackage(packageId)

            ?? throw new InvalidOperationException("Pinned package is absent from the active catalog store.");

        return (storePath, package);

    }



    private static object[] BuildRepresentativePaths(

        CanonicalRegistrationCandidate candidate,

        RegisteredCanonicalRelationship[] correlated)

    {

        var names = candidate.Entities.ToDictionary(

            e => e.Id,

            e => e.Names.First(n => !n.IsAlias && n.Locale == "en-US").Value,

            StringComparer.Ordinal);

        var childIds = correlated.Select(r => r.SubjectId).ToHashSet(StringComparer.Ordinal);

        var roots = candidate.Entities.Select(e => e.Id)

            .Where(id => !childIds.Contains(id))

            .OrderBy(id => id, StringComparer.Ordinal)

            .ToArray();

        var paths = new List<object>();

        foreach (var root in roots)

            AppendPath(root, null, [], names, correlated, paths);

        return paths.ToArray();

    }



    private static void AppendPath(

        string entityId,

        string? parentEntityId,

        List<string> prefix,

        IReadOnlyDictionary<string, string> names,

        RegisteredCanonicalRelationship[] correlated,

        List<object> paths)

    {

        var label = names[entityId];

        var segment = prefix.Concat([label]).ToArray();

        RegisteredCanonicalRelationship? edge = parentEntityId is null

            ? correlated.FirstOrDefault(r => r.SubjectId == entityId)

            : correlated.FirstOrDefault(r => r.SubjectId == entityId && r.TargetId == parentEntityId);

        paths.Add(new

        {

            path = string.Join(" > ", segment),

            childEntityId = entityId,

            parentEntityId = edge?.TargetId ?? parentEntityId,

            semantic = edge?.Semantic,

            sourceNativeRelationshipType = edge?.SourceNativeRelationshipType,

            sourceFieldPath = edge?.SourceFieldPath,

        });

        foreach (var child in correlated.Where(r => r.TargetId == entityId).Select(r => r.SubjectId).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))

            AppendPath(child, entityId, segment.ToList(), names, correlated, paths);

    }



    private static object BuildSourceCoverage(

        GtaVLocationHierarchyCorpusIndex index,

        ImmutableArray<GtaVLocationHierarchyRow> rows,

        RegisteredCanonicalRelationship[] correlated,

        CanonicalRegistrationCandidate candidate)

    {

        var wikiEdges = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.WikiCoordinate);

        var losSantosEdges = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.LosSantosCoordinate);

        var grandSenoraEdges = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate);

        var nativeBridgeEvidence = candidate.Input.Evidence.Evidence

            .Count(e => e.SourceId == candidate.Input.Sources.Single(s => s.Uri == GtaVLocationHierarchyCorpusIndex.CfxCoordinate).Id);

        return new
        {

            wikiEnumeration = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.WikiCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.WikiDigest,

                relationshipRows = wikiEdges,

                relationshipTypes = rows.Where(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.WikiCoordinate)

                    .Select(r => r.SourceNativeRelationshipType).Distinct(StringComparer.Ordinal).ToArray(),

            },

            nativeZoneTable = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.CfxCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.CfxDigest,

                role = "unique English name to native zone code bridge for endpoint identity and relationship closure",

                nameCodeCount = rows.SelectMany(r => new[] { r.ParentName, r.ChildName }).Distinct(StringComparer.Ordinal).Count(),

                evidenceReceiptsInCandidate = nativeBridgeEvidence,

            },

            losSantosContainment = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.LosSantosCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.LosSantosDigest,

                joinIndexDigest = GtaVLocationHierarchyCorpusIndex.JoinIndexDigest,

                joinStatus = "join-indexed",

                relationshipRows = losSantosEdges,

                relationshipTypes = rows.Where(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.LosSantosCoordinate)

                    .Select(r => r.SourceNativeRelationshipType).Distinct(StringComparer.Ordinal).ToArray(),

            },

            grandSenoraDesertContainment = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.GrandSenoraDigest,

                joinStatus = "join-indexed",

                upstreamParentIdentityRule = "section subject must equal complete parent name (Grand Senora Desert); shortened parents unresolved",

                relationshipRows = grandSenoraEdges,

                relationshipTypes = rows.Where(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.GrandSenoraCoordinate)

                    .Select(r => r.SourceNativeRelationshipType).Distinct(StringComparer.Ordinal).ToArray(),

            },

            eastLosSantosSnapshot = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.EastLosSantosDigest,

                joinStatus = "join-indexed-reference-geography",

                relationshipRows = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.EastLosSantosCoordinate),

            },

            blaineCountySnapshot = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.BlaineCountyCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.BlaineCountyDigest,

                joinStatus = "join-indexed-reference-geography",

                relationshipRows = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.BlaineCountyCoordinate),

            },

            sanAndreasSnapshot = new

            {

                coordinate = GtaVLocationHierarchyCorpusIndex.SanAndreasCoordinate,

                digest = GtaVLocationHierarchyCorpusIndex.SanAndreasDigest,

                joinStatus = "join-indexed-reference-geography",

                relationshipRows = rows.Count(r => r.EvidenceArtifact.SourceCoordinate.ExactRepresentation == GtaVLocationHierarchyCorpusIndex.SanAndreasCoordinate),

            },

            geographyBridgeBlockedSourceKeys = index.GeographyBridgeBlockedRows

                .Select(b => b.SourceKey).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray(),

            correlatedEdgesBySourceId = correlated.GroupBy(r => r.SourceId ?? string.Empty, StringComparer.Ordinal)

                .OrderBy(g => g.Key, StringComparer.Ordinal)

                .Select(g => new { sourceId = g.Key, count = g.Count() })

                .ToArray(),

        };

    }



    private static bool NativeBridgeEvidenceClosure(
        CanonicalRegistrationCandidate candidate,
        RegisteredCanonicalRelationship relationship,
        IReadOnlyDictionary<string, RegistrationEvidence> evidenceById,
        GtaVLocationHierarchyCorpusIndex index)
    {
        var nativeSourceId = candidate.Input.Sources.Single(s => s.Uri == GtaVLocationHierarchyCorpusIndex.CfxCoordinate).Id;
        var subject = candidate.Entities.Single(e => e.Id == relationship.SubjectId);
        var target = candidate.Entities.Single(e => e.Id == relationship.TargetId);
        var subjectNative = !string.Equals(subject.NativeNamespace, GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace, StringComparison.Ordinal);
        var targetNative = !string.Equals(target.NativeNamespace, GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace, StringComparison.Ordinal);
        var nativeBridgeCount = relationship.EvidenceIds.Count(id => evidenceById[id].SourceId == nativeSourceId);
        if (subjectNative && targetNative) return nativeBridgeCount >= 2;
        if (subjectNative || targetNative) return nativeBridgeCount >= 1;
        return nativeBridgeCount == 0;
    }

    private static bool CrossSourceRelationshipEvidenceSubstitutionRejected()
    {
        var bytesA = new byte[] { 0x01 };
        var bytesB = new byte[] { 0x02 };
        var sourceA = new RegistrationSource("source-a", "fixture://source-a", KnowledgeSourceKind.ReferenceProvider,
            CanonicalRegistrationEncoding.Digest(bytesA), "fixture.substitution", "1", "scenario-rows");
        var sourceB = new RegistrationSource("source-b", "fixture://source-b", KnowledgeSourceKind.ReferenceProvider,
            CanonicalRegistrationEncoding.Digest(bytesB), "fixture.substitution", "1", "scenario-rows");
        var childEvidence = new RegistrationEvidence("evidence:child", sourceA.Id, "/rows/child", EvidenceVerificationKind.ReferenceVerified);
        var parentEvidence = new RegistrationEvidence("evidence:parent", sourceA.Id, "/rows/parent", EvidenceVerificationKind.ReferenceVerified);
        var substitutedRelationshipEvidence = new RegistrationEvidence("evidence:relationship", sourceB.Id, "/rows/child/parent/parent", EvidenceVerificationKind.ReferenceVerified);
        var entities = new[]
        {
            new RegistrationEntityClaim("child", sourceA.Id, "fixture.native", "native-child", "Location", "game.fixture", "native:Location/world", RegistrationEntityKind.Entity,
                [new("Child", "en-US", false, [childEvidence.Id])], [new("game.fixture", "profile.a", [childEvidence.Id])],
                [new("Game", "fixture.native", "native-child", [childEvidence.Id])], [childEvidence.Id]),
            new RegistrationEntityClaim("parent", sourceA.Id, "fixture.native", "native-parent", "Location", "game.fixture", "native:Location/world/continent", RegistrationEntityKind.Category,
                [new("Parent", "en-US", false, [parentEvidence.Id])], [new("game.fixture", "profile.a", [parentEvidence.Id])],
                [new("Game", "fixture.native", "native-parent", [parentEvidence.Id])], [parentEvidence.Id]),
        };
        var relationships = new[]
        {
            new RegistrationRelationshipClaim("edge:substitution", "child", "parent", "contained-by",
                [substitutedRelationshipEvidence.Id, childEvidence.Id, parentEvidence.Id],
                sourceA.Id, "fixture.contained-by", "/rows/child/parent/parent"),
        };
        var rules = new RegistrationRuleSet("substitution.rules.v1", [
            new("map:Location/world", "scenario-rows", "Location", "native:Location/world", "Location/world"),
            new("map:Location/world/continent", "scenario-rows", "Location", "native:Location/world/continent", "Location/world/continent"),
        ]);
        var candidate = CanonicalRegistrationEngine.RegisterAsync(
            new SubstitutionEvidenceAdapter(childEvidence, parentEvidence, substitutedRelationshipEvidence, entities, relationships),
            [new RegistrationSourceArtifact(sourceA, bytesA), new RegistrationSourceArtifact(sourceB, bytesB)],
            rules).GetAwaiter().GetResult();
        return candidate.Rulings.Any(r => r.Stage == "relationship" && r.Reason == "relationship-source-mismatch");
    }

    private sealed class SubstitutionEvidenceAdapter(
        RegistrationEvidence childEvidence,
        RegistrationEvidence parentEvidence,
        RegistrationEvidence substitutedRelationshipEvidence,
        RegistrationEntityClaim[] entities,
        RegistrationRelationshipClaim[] relationships) : IRegistrationEvidenceAdapter
    {
        public string Id => "fixture.substitution";
        public string Version => "1";
        public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RegistrationEvidenceSet(
                [childEvidence, parentEvidence, substitutedRelationshipEvidence],
                entities,
                relationships,
                []));
    }

    private static async Task<bool> UnresolvedEndpointYieldsRulingAsync(
        GtaVLocationHierarchyCorpusIndex index,
        CanonicalCatalogPayload baselinePayload,
        CanonicalCatalogPackage baselinePackage,
        RegistrationSourceArtifact[] sources,
        ProfileId profileId)
    {
        var sampleChild = index.Resolve(baselinePayload).First().Child.CatalogRecord!.Id;
        var degradedPayload = baselinePayload with
        {
            KnowledgeRecords = baselinePayload.KnowledgeRecords.Where(r => r.Id != sampleChild).ToImmutableArray(),
        };
        var degradedResolution = index.ResolveForRegistration(degradedPayload);
        if (degradedResolution.UnresolvedEdges.IsEmpty)
            return false;
        var degradedCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            new GtaVLocationHierarchyRegistrationEvidenceAdapter(degradedPayload, index, profileId, baselinePackage.Id),
            sources,
            GtaVLocationHierarchyRegistrationRules.Create(),
            [baselinePackage]);
        return degradedCandidate.Rulings.Any(r =>
            r.Stage == "relationship" &&
            r.Outcome == RegistrationOutcome.Unresolved &&
            r.Reason == "relationship-endpoint-unresolved");
    }

    public static async Task<int> RunLivePublicationAsync(string repositoryRoot, string outputRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        var allowed = Path.GetFullPath(Path.Combine(repositoryRoot, ".tmp/grid-contract2-registration-20261003/proof"));
        if (outputRoot != allowed && !outputRoot.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Live publication proof output must remain inside the approved Contract 2 proof directory.");
        Directory.CreateDirectory(outputRoot);

        var checks = 0;
        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("GTA V Location population 2.A.III LIVE: " + reason);
            checks++;
        }

        var dataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid");
        dataRoot = Path.GetFullPath(dataRoot);
        var catalogsRoot = Path.Combine(dataRoot, "catalogs");
        var storePath = Path.Combine(catalogsRoot, "shared-canonical-library.v5.json");
        var bindingPath = Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json");
        var preparedRoot = Path.Combine(catalogsRoot, "prepared-canonical");
        var receiptDir = Path.Combine(dataRoot, "evidence", "registration-refresh");

        Check(File.Exists(storePath) && File.Exists(bindingPath), "Live catalog store and runtime binding exist under GRID data root.");

        var index = GtaVLocationHierarchyCorpusIndex.LoadFromRepository(
            Path.Combine(repositoryRoot, "scripts", "games", "grandtheftautov", "catalog", "references"));
        var (activeCatalogStorePath, baselinePackage) = await LoadActiveGtaCatalogAsync();
        Check(string.Equals(activeCatalogStorePath, storePath, StringComparison.OrdinalIgnoreCase),
            "Active catalog path resolves to the live GRID data root store.");

        static int CountReferenceHierarchyEdges(CanonicalCatalogPackage package) =>
            package.Payload.RelationshipAssertions.Count(r =>
                r.SemanticId == LocationRelationshipSemantics.ContainedBy &&
                r.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal) &&
                r.ResolvedTargetKnowledgeRecordId is not null);

        var beforeHierarchyEdges = CountReferenceHierarchyEdges(baselinePackage);
        Check(beforeHierarchyEdges != 12,
            "Pinned package must not remain on the stale twelve-edge SecondaryAssertion-only hierarchy.");
        Check(beforeHierarchyEdges < index.ExpectedRelationshipCount,
            "Live publication requires the pinned package to lack the full thirty-six-edge registration hierarchy.");

        var profileId = new ProfileId("profile.location-population-2a-iii-live");
        var installationId = new InstallationId("installation.location-population-2a-iii-live");
        var observationContributor = new GtaVEnhancedRegistrationKnowledgeRefreshContributor();
        var mdoboContributor = new CanonicalRegistrationMdboKnowledgeRefreshContributor(
            observationContributor, new GtaVRegistrationMdboRefreshAuthor());
        var refreshContext = new RegistrationRefreshContext(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            installationId,
            profileId,
            baselinePackage.Id,
            storePath,
            preparedRoot,
            bindingPath,
            receiptDir,
            null,
            true,
            new RegistrationRefreshResourcePaths(
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/vinewood-411759.normalized.txt"),
                Path.Combine(repositoryRoot, "scripts/games/grandtheftautov/catalog/references/cfx-zones-ad60ae80.md")));

        var orchestrator = new CanonicalRegistrationRefreshOrchestrator([mdoboContributor], new CanonicalTerminologyLocalePreference("en-US", []));
        var refresh = await orchestrator.RefreshAsync(refreshContext, null, default);
        Check(refresh.Status == RegistrationRefreshStatus.Completed, "Live registration refresh completed: " + refresh.Detail);
        Check(refresh.Mode == RegistrationRefreshMode.KnowledgeRebuild, "Live refresh performed KnowledgeRebuild import and publication.");
        Check(refresh.PublishedPackageId is not null, "Live refresh published a new package id.");
        var publishedPackageId = refresh.PublishedPackageId!.Value;
        Check(refresh.AdmittedLocationRelationships == index.ExpectedRelationshipCount,
            "Live refresh admitted thirty-six Location hierarchy relationships.");
        Check(refresh.BindingRollbackPath is not null && File.Exists(refresh.BindingRollbackPath),
            "Live publication retained a binding rollback receipt.");

        var store = new JsonCanonicalKnowledgeCatalogStore(storePath);
        var catalogAfter = await store.LoadAsync();
        Check(catalogAfter.IsValid, "Live catalog store validates after publication.");
        var published = catalogAfter.Snapshot.FindImportedPackage(publishedPackageId)
            ?? throw new InvalidOperationException("Published package missing from live store.");
        Check(CountReferenceHierarchyEdges(published) == index.ExpectedRelationshipCount,
            "Published package carries thirty-six reference hierarchy relationship assertions.");
        Check(published.Payload.KnowledgeRecords.Count(r => r.Kind == KnowledgeKind.Location) >= 36,
            "Published package retains at least thirty-six Location knowledge records.");

        using var bindingJson = JsonDocument.Parse(await File.ReadAllTextAsync(bindingPath));
        Check(string.Equals(bindingJson.RootElement.GetProperty("packageId").GetString(), publishedPackageId.Value, StringComparison.Ordinal),
            "Runtime binding packageId matches the published package.");
        Check(bindingJson.RootElement.GetProperty("allowCandidatePackages").GetBoolean(),
            "Runtime binding allows candidate packages after live publication.");

        Check(CountReferenceHierarchyEdges(published) == index.ExpectedRelationshipCount,
            "HierarchyRelationshipsPresent confirms thirty-six reference edges on the published package.");
        Check(index.ExpectedPackageAssertionRelationshipCount == 36,
            "ExpectedPackageAssertionRelationshipCount is thirty-six on the corpus index.");

        var sources = GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index);
        var repBaseline = GtaVLocationHierarchyRegistrationRefreshSupport.StripPriorReferenceHierarchy(published.Payload);
        var repCandidate = await CanonicalRelationshipRegistrationMdboComposer.RegisterCandidateAsync(
            new GtaVLocationHierarchyRegistrationEvidenceAdapter(repBaseline, index, profileId, published.Id),
            sources,
            GtaVLocationHierarchyRegistrationRules.Create(),
            [baselinePackage, published]);
        var repCorrelated = repCandidate.Relationships.Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated).ToArray();
        Check(repCandidate.Entities.Length == 36 && repCorrelated.Length == index.ExpectedRelationshipCount,
            "Post-publication registration reproduces thirty-six entities and thirty-six correlated edges.");

        Check(File.Exists(Path.Combine(preparedRoot, PreparedCanonicalNavigationStore.PublicationFileName)),
            "Prepared navigation publication receipt exists after live publication.");
        using (var preparedStore = await PreparedCanonicalNavigationStore.OpenAsync(
            preparedRoot,
            publishedPackageId,
            new CanonicalTerminologyLocalePreference("en-US", [])))
        {
            Check(preparedStore.Descriptor.PackageId == publishedPackageId,
                "Runtime prepared navigation receipt targets the published package.");
        }

        var navContext = new RegistrationNavigationContext(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value,
            profileId.Value,
            "en-US");
        var livePrepared = await CanonicalRelationshipRegistrationMdboComposer.PrepareNavigationAsync(
            Path.Combine(outputRoot, "live-prepared-registration"),
            repCandidate,
            navContext,
            [published]);
        using var livePreparedReader = await CanonicalRegistrationPreparedReader.OpenAsync(
            livePrepared.DirectoryPath, livePrepared.Digest, navContext);
        Check(livePreparedReader.Descriptor.Paths.Length == ExpectedPreparedPaths,
            "Registration-prepared Location navigation retains the level-slotted path set after live publication.");
        var preparedPathCount = livePreparedReader.Descriptor.Paths.Length;

        var sandyEntityId = repCandidate.Entities
            .Single(e => e.Names.Any(n => !n.IsAlias && n.Locale == "en-US" && n.Value == "Sandy Shores")).Id;
        var sandyParentIds = repCorrelated.Where(r => r.SubjectId == sandyEntityId).Select(r => r.TargetId).Distinct(StringComparer.Ordinal).ToArray();
        Check(sandyParentIds.Length == 2, "Sandy Shores retains dual parent edges after live publication.");

        var report = new
        {
            gridPhase = "2.A.III",
            publicationMode = "LIVE",
            gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value,
            gridDataRoot = dataRoot,
            activeCatalogStorePath = storePath,
            bindingStorePath = bindingPath,
            previousPackageId = baselinePackage.Id.Value,
            publishedPackageId = publishedPackageId.Value,
            bindingRollbackPath = refresh.BindingRollbackPath,
            catalogRollbackPath = refresh.CatalogRollbackPath,
            refreshReceiptPath = refresh.ReceiptPath,
            checks,
            result = "PASS",
            counts = new
            {
                entitiesAdmitted = repCandidate.Entities.Length,
                relationshipsCorrelated = repCorrelated.Length,
                expectedRelationships = index.ExpectedRelationshipCount,
                preparedPaths = preparedPathCount,
                beforeHierarchyEdgesOnPinned = beforeHierarchyEdges,
                publishedHierarchyEdges = CountReferenceHierarchyEdges(published),
            },
            sandyShoresParentEntityIds = sandyParentIds,
            hierarchyRelationshipsPresent = true,
            expectedPackageAssertionRelationshipCount = index.ExpectedPackageAssertionRelationshipCount,
        };
        await File.WriteAllBytesAsync(
            Path.Combine(outputRoot, "location-population-2a-iii-live-report.json"),
            CanonicalRegistrationEncoding.Bytes(report));

        Console.WriteLine(
            $"PASS LIVE: {checks} checks; published={refresh.PublishedPackageId!.Value}; " +
            $"edges={repCorrelated.Length}; paths={preparedPathCount}; rollback={refresh.BindingRollbackPath}");
        return checks;
    }

    private static string LocateRepositoryRoot()

    {

        var fromEnvironment = Environment.GetEnvironmentVariable("GRID_REPOSITORY_ROOT");

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(Path.Combine(fromEnvironment, ".git")))

            return Path.GetFullPath(fromEnvironment);

        for (var directory = AppContext.BaseDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)!)

        {

            if (Directory.Exists(Path.Combine(directory, ".git")))

                return directory;

        }



        throw new InvalidOperationException("Repository root is unavailable for Location population 2.A.III checks.");

    }

}


