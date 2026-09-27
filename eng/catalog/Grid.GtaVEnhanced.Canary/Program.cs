using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Enrichment.Knowledge;
using Grid.GtaV.Knowledge;

try
{
    var arguments = ParseArguments(args);
    var gameRoot = Path.GetFullPath(RequireArgument(arguments, "game-root"));
    var acquisitionReceiptPath = Path.GetFullPath(RequireArgument(arguments, "acquisition-receipt"));
    var fiveFuryWheel = Path.GetFullPath(RequireArgument(arguments, "fivefury-wheel"));
    var uvExecutable = Path.GetFullPath(RequireArgument(arguments, "uv-executable"));
    var outputDirectory = Path.GetFullPath(RequireArgument(arguments, "output"));
    var historicalLibraryPath = Path.GetFullPath(RequireArgument(arguments, "historical-library"));
    var actorAcquisitionReceiptPath = Path.GetFullPath(RequireArgument(arguments, "actor-acquisition-receipt"));
    var actorCorpusIndexPath = Path.GetFullPath(RequireArgument(arguments, "actor-corpus-index"));
    var spatialAcquisitionReceiptPath = Path.GetFullPath(RequireArgument(arguments, "spatial-acquisition-receipt"));
    var spatialCorpusIndexPath = Path.GetFullPath(RequireArgument(arguments, "spatial-corpus-index"));
    var cloudSnapshotBundlePath = OptionalArgument(arguments, "rockstar-cloud-snapshot-bundle");
    if (cloudSnapshotBundlePath is not null) cloudSnapshotBundlePath = Path.GetFullPath(cloudSnapshotBundlePath);
    var allowedArguments = new HashSet<string>(StringComparer.Ordinal)
    {
        "game-root", "acquisition-receipt", "fivefury-wheel", "uv-executable",
        "historical-library", "output", "rockstar-cloud-snapshot-bundle",
        "actor-acquisition-receipt", "actor-corpus-index",
        "spatial-acquisition-receipt", "spatial-corpus-index",
    };
    Require(arguments.Keys.All(allowedArguments.Contains), "The canary received an unsupported command-line option.");

    var repositoryRoot = GitBuildProvenanceResolver.FindRepositoryRoot();
    var sourceFamilyManifestPath = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_source_families.v2.json");
    var registrationSourceRegistryPath = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_registration_sources.v5.json");
    var actorSourceFamilyManifestPath = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_actor_source_families.v1.json");
    var spatialSourceFamilyManifestPath = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_spatial_source_families.v1.json");
    var registrationSourceRegistry = GtaRegistrationSourceRegistry.Load(
        registrationSourceRegistryPath,
        sourceFamilyManifestPath,
        actorSourceFamilyManifestPath,
        spatialSourceFamilyManifestPath);
    await VerifyAcquisitionAsync(
        uvExecutable,
        fiveFuryWheel,
        gameRoot,
        acquisitionReceiptPath,
        repositoryRoot).ConfigureAwait(false);
    await VerifyActorAcquisitionAsync(
        uvExecutable,
        fiveFuryWheel,
        gameRoot,
        actorAcquisitionReceiptPath,
        repositoryRoot).ConfigureAwait(false);
    await VerifySpatialAcquisitionAsync(
        uvExecutable,
        fiveFuryWheel,
        gameRoot,
        spatialAcquisitionReceiptPath,
        repositoryRoot).ConfigureAwait(false);
    var acquisition = await GtaAcquisitionReceiptLoader.LoadAsync(
        acquisitionReceiptPath,
        gameRoot).ConfigureAwait(false);
    var semanticAcquisition = GtaAcquisitionSemanticProjection.Create(acquisition, sourceFamilyManifestPath);
    var actorAcquisition = await GtaActorAcquisitionReceiptLoader.LoadAsync(
        actorAcquisitionReceiptPath,
        actorCorpusIndexPath,
        actorSourceFamilyManifestPath).ConfigureAwait(false);
    Require(actorAcquisition.GameVersion == acquisition.GameVersion,
        "Mounted Actor and local source acquisitions must bind the same exact build.");
    var actorCorpusIndex = GtaVActorCorpusIndex.Load(
        actorAcquisition.CorpusIndexBytes.AsSpan(),
        actorAcquisition.CorpusArtifacts,
        actorAcquisition.CorpusIndexReceiptBinding);
    var spatialAcquisition = await GtaSpatialAcquisitionReceiptLoader.LoadAsync(
        spatialAcquisitionReceiptPath,
        spatialCorpusIndexPath,
        spatialSourceFamilyManifestPath).ConfigureAwait(false);
    Require(spatialAcquisition.GameVersion == acquisition.GameVersion,
        "Mounted spatial and local source acquisitions must bind the same exact build.");
    var spatialCorpusIndex = GtaVSpatialCorpusIndex.Load(
        spatialAcquisition.CorpusIndexBytes.AsSpan(),
        spatialAcquisition.SemanticArtifacts,
        spatialAcquisition.CorpusIndexReceiptBinding);

    var adapterAssemblyBytes = await File.ReadAllBytesAsync(
        typeof(GtaVWeaponsMetaKnowledgeAdapter).Assembly.Location).ConfigureAwait(false);
    var adapterDigest = ContentDigest.ComputeSha256(adapterAssemblyBytes);
    var enrichmentAssemblyBytes = await File.ReadAllBytesAsync(
        typeof(GtaVPopulationZonesKnowledgeAdapter).Assembly.Location).ConfigureAwait(false);
    var enrichmentAdapterDigest = ContentDigest.ComputeSha256(enrichmentAssemblyBytes);
    var adapterBuildReceipts = ImmutableArray.Create(
        CreateAdapterBuildProvenanceReceipt(
            typeof(GtaVWeaponsMetaKnowledgeAdapter).Assembly,
            adapterDigest),
        CreateAdapterBuildProvenanceReceipt(
            typeof(GtaVPopulationZonesKnowledgeAdapter).Assembly,
            enrichmentAdapterDigest));
    var weapons = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, adapterDigest);
    var locations = new GtaVMapZonesKnowledgeAdapter(adapterDigest);
    var populationZones = new GtaVPopulationZonesKnowledgeAdapter(enrichmentAdapterDigest);
    var ambientPedRoles = new GtaVAmbientPedRoleSecondaryAssertionAdapter(enrichmentAdapterDigest);
    var missions = new GtaVUgcMissionKnowledgeAdapter(adapterDigest);
    var onlineActivities = new GtaVOnlineActivityRegistryKnowledgeAdapter(adapterDigest);
    var actors = new GtaVGen9PedsKnowledgeAdapter(adapterDigest);
    var residentActors = new GtaVResidentPedsKnowledgeAdapter(adapterDigest, actorCorpusIndex);
    var mountedDlcActors = new GtaVMountedDlcPedsKnowledgeAdapter(adapterDigest, actorCorpusIndex);
    var mountedSpatial = new GtaVMountedSpatialKnowledgeAdapter(adapterDigest, spatialCorpusIndex);

    var weaponsArtifact = CreateFrozen(
        acquisition,
        "common.rpf!/data/ai/weapons.meta",
        weapons.CreateSourceCoordinate,
        GtaVWeaponsMetaKnowledgeAdapter.Format);
    var locationArtifact = CreateFrozen(
        acquisition,
        "common.rpf!/data/levels/gta5/mapzones.xml",
        locations.CreateSourceCoordinate,
        locations.Format);
    var populationZoneArtifact = CreateFrozen(
        acquisition,
        GtaVPopulationZonesKnowledgeAdapter.Coordinate,
        _ => populationZones.CreateSourceCoordinate(),
        populationZones.Format);
    var actorArtifact = CreateFrozen(
        acquisition,
        "update/update.rpf!/common/data/gen9_exclusive_assets_peds.meta",
        actors.CreateSourceCoordinate,
        actors.Format);
    var ambientPedArtifact = CreateFrozen(
        acquisition,
        GtaVAmbientPedRoleSecondaryAssertionAdapter.Coordinate,
        _ => ambientPedRoles.CreateSourceCoordinate(),
        ambientPedRoles.AmbientFormat);
    var baseLanguageRpfArtifact = CreateFrozen(
        acquisition,
        "x64b.rpf!/data/lang/american_rel.rpf",
        coordinate => CreateSecondaryCoordinate(coordinate, "Rpf7Container"),
        new KnowledgeFormatCoordinate(
            GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatId,
            GtaVWeaponsSecondaryAssertionAdapter.Rpf7FormatVersion));
    var baseGxt2Artifact = CreateFrozen(
        acquisition,
        "x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2",
        coordinate => CreateSecondaryCoordinate(coordinate, "GXT2"),
        new KnowledgeFormatCoordinate(
            GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatId,
            GtaVWeaponsSecondaryAssertionAdapter.Gxt2FormatVersion));
    var patchLanguageRpfArtifact = CreateFrozen(
        acquisition,
        "update/update.rpf!/x64/patch/data/lang/american_rel.rpf",
        coordinate => CreateSecondaryCoordinate(coordinate, "Rpf7Container"),
        new KnowledgeFormatCoordinate("rockstar.rpf7-container", "1"));
    var patchGxt2Artifact = CreateFrozen(
        acquisition,
        "update/update.rpf!/x64/patch/data/lang/american_rel.rpf!/global.gxt2",
        coordinate => CreateSecondaryCoordinate(coordinate, "GXT2"),
        new KnowledgeFormatCoordinate("rockstar.gta-v.gxt2-binary", "1"));
    var hudGfxArtifact = CreateFrozen(
        acquisition,
        "update/update.rpf!/x64/data/cdimages/scaleform_generic.rpf!/hud.gfx",
        coordinate => CreateSecondaryCoordinate(coordinate, "ScaleformGfxV8"),
        new KnowledgeFormatCoordinate("rockstar.scaleform.gfx-v8", "1"));
    var missionArtifacts = acquisition.Members.Values
        .Where(value => value.Coordinate.StartsWith(
            "update/update2.rpf!/common/data/ugc/",
            StringComparison.Ordinal))
        .OrderBy(value => value.Coordinate, StringComparer.Ordinal)
        .Select(value => CreateFrozen(acquisition, value.Coordinate, missions.CreateSourceCoordinate, missions.Format))
        .ToImmutableArray();
    Require(missionArtifacts.Length == 1052, "The exact Enhanced build must expose 1,052 acquired UGC members.");
    var commonActivityArtifacts = acquisition.Members.Values
        .Where(value => value.Coordinate.StartsWith("common.rpf!/data/ugc/", StringComparison.Ordinal))
        .OrderBy(value => value.Coordinate, StringComparer.Ordinal)
        .Select(value => CreateFrozen(acquisition, value.Coordinate, onlineActivities.CreateSourceCoordinate, onlineActivities.Format))
        .ToImmutableArray();
    Require(commonActivityArtifacts.Length == 40,
        "The exact Enhanced build must expose 40 acquired common Online activity registry members.");
    var onlineActivityArtifacts = commonActivityArtifacts.AddRange(missionArtifacts);
    var residentActorArtifact = actorAcquisition.Artifacts[actorCorpusIndex.Resident.Artifact.SourceCoordinate];
    var mountedDlcActorArtifacts = actorCorpusIndex.DlcPacks
        .Select(value => actorAcquisition.Artifacts[value.Artifact.SourceCoordinate])
        .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToImmutableArray();
    Require(mountedDlcActorArtifacts.Length == 25,
        "The exact Enhanced build must expose 25 mounted DLC ped metadata artifacts.");
    var mountedActorClosureArtifacts = new[] { actorCorpusIndex.DlcListArtifact.SourceCoordinate }
        .Concat(actorCorpusIndex.DlcPacks.SelectMany(value => new[]
        {
            value.Artifact.SourceCoordinate,
            value.SetupArtifact.SourceCoordinate,
            value.ContentArtifact.SourceCoordinate,
        }))
        .Select(value => actorAcquisition.Artifacts[value])
        .DistinctBy(value => value.Id)
        .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToImmutableArray();
    Require(mountedActorClosureArtifacts.Length == 76,
        "The mounted Actor assertion pass requires the exact DLC list and 25 setup/content/peds artifact triples.");

    var corpusArtifacts = ImmutableArray.CreateBuilder<FrozenSourceArtifact>(onlineActivityArtifacts.Length + 10);
    corpusArtifacts.Add(weaponsArtifact);
    corpusArtifacts.Add(locationArtifact);
    corpusArtifacts.Add(populationZoneArtifact);
    corpusArtifacts.Add(actorArtifact);
    corpusArtifacts.Add(ambientPedArtifact);
    corpusArtifacts.Add(baseLanguageRpfArtifact);
    corpusArtifacts.Add(baseGxt2Artifact);
    corpusArtifacts.Add(patchLanguageRpfArtifact);
    corpusArtifacts.Add(patchGxt2Artifact);
    corpusArtifacts.Add(hudGfxArtifact);
    corpusArtifacts.AddRange(missionArtifacts);
    corpusArtifacts.AddRange(commonActivityArtifacts);
    var sourceCorpus = new GtaVSupportedSourceCorpusIndex(corpusArtifacts.ToImmutable());
    var enrichmentCorpus = new GtaVEnrichmentSourceCorpusIndex(sourceCorpus);
    _ = sourceCorpus.GetGxt2(patchGxt2Artifact, GtaVPopulationZoneTerminologyAdapter.MaximumGxt2Bytes);

    // The bootstrap instances above supply exact coordinate/format factories only. Registration
    // itself uses one immutable corpus so every supported artifact representation is parsed once
    // and queried by every applicable adapter.
    weapons = new GtaVWeaponsMetaKnowledgeAdapter(GtaVKnowledgeEdition.Enhanced, adapterDigest, sourceCorpus);
    locations = new GtaVMapZonesKnowledgeAdapter(adapterDigest, sourceCorpus);
    populationZones = new GtaVPopulationZonesKnowledgeAdapter(enrichmentAdapterDigest, enrichmentCorpus);
    ambientPedRoles = new GtaVAmbientPedRoleSecondaryAssertionAdapter(enrichmentAdapterDigest, enrichmentCorpus);
    missions = new GtaVUgcMissionKnowledgeAdapter(adapterDigest, sourceCorpus);
    onlineActivities = new GtaVOnlineActivityRegistryKnowledgeAdapter(adapterDigest, sourceCorpus);
    actors = new GtaVGen9PedsKnowledgeAdapter(adapterDigest, sourceCorpus);

    var weaponsResult = await DiscoverAndExtractAsync(weapons, [weaponsArtifact], acquisition.GameVersion).ConfigureAwait(false);
    var locationResult = await DiscoverAndExtractAsync(locations, [locationArtifact], acquisition.GameVersion).ConfigureAwait(false);
    var populationZoneResult = await DiscoverAndExtractAsync(
        populationZones, [populationZoneArtifact], acquisition.GameVersion).ConfigureAwait(false);
    var missionBatch = await DiscoverAndExtractWithDiscoveryAsync(
        missions, missionArtifacts, acquisition.GameVersion).ConfigureAwait(false);
    var missionResult = missionBatch.Extraction;
    var onlineActivityBatch = await DiscoverAndExtractWithDiscoveryAsync(
        onlineActivities, onlineActivityArtifacts, acquisition.GameVersion).ConfigureAwait(false);
    var onlineActivityResult = onlineActivityBatch.Extraction;
    var missionCoverageLedger = RegistrationMissionQuestSourceLedger.Create(
        onlineActivityArtifacts,
        missionBatch.Discovery,
        onlineActivityBatch.Discovery);
    var actorResult = await DiscoverAndExtractAsync(actors, [actorArtifact], acquisition.GameVersion).ConfigureAwait(false);
    var residentActorResult = await DiscoverAndExtractAsync(
        residentActors, [residentActorArtifact], acquisition.GameVersion).ConfigureAwait(false);
    var mountedDlcActorResult = await DiscoverAndExtractAsync(
        mountedDlcActors, mountedDlcActorArtifacts, acquisition.GameVersion).ConfigureAwait(false);
    var mountedSpatialResult = await DiscoverAndExtractAsync(
        mountedSpatial, spatialAcquisition.SemanticArtifacts, acquisition.GameVersion).ConfigureAwait(false);
    var mountedSpatialMetrics = mountedSpatial.LastMetrics ??
        throw new InvalidDataException("Mounted spatial extraction did not expose deterministic coverage metrics.");
    var extractions = ImmutableArray.Create(
        weaponsResult, locationResult, populationZoneResult, missionResult, onlineActivityResult, actorResult,
        residentActorResult, mountedDlcActorResult, mountedSpatialResult);

    // Keep the complete verified semantic binding set available while secondary adapters
    // add their already-acquired localization and relationship artifacts. The package
    // projection narrows this set to the artifacts present at each deterministic stage.
    var baseAcquisitionBindings = semanticAcquisition.Bindings;
    var baseBoundArtifactIds = baseAcquisitionBindings
        .Select(value => value.ArtifactId)
        .ToHashSet();
    var actorSemanticAcquisition = GtaActorAcquisitionReceiptLoader.NarrowTo(
        actorAcquisition,
        new[] { residentActorArtifact.Id }
            .Concat(mountedActorClosureArtifacts.Select(value => value.Id))
            // The historical acquisition already receipts one mounted ped artifact. A
            // content-addressed artifact has exactly one acquisition binding in a package;
            // retain that established binding instead of adding a second receipt path for
            // identical bytes acquired again by the Actor corpus pass.
            .Where(value => !baseBoundArtifactIds.Contains(value)));
    var alreadyBoundArtifactIds = baseBoundArtifactIds
        .Concat(actorSemanticAcquisition.Bindings.Select(value => value.ArtifactId))
        .ToHashSet();
    var spatialSemanticAcquisition = GtaSpatialAcquisitionReceiptLoader.NarrowTo(
        spatialAcquisition,
        spatialAcquisition.SemanticArtifacts
            .Select(value => value.Id)
            .Where(value => !alreadyBoundArtifactIds.Contains(value)));
    var allAcquisitionReceipts = semanticAcquisition.Receipts
        .AddRange(actorSemanticAcquisition.Receipts)
        .AddRange(spatialSemanticAcquisition.Receipts)
        .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
        .ToImmutableArray();
    var allAcquisitionBindings = baseAcquisitionBindings
        .AddRange(actorSemanticAcquisition.Bindings)
        .AddRange(spatialSemanticAcquisition.Bindings)
        .OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal)
        .ToImmutableArray();
    var primaryArtifactIds = extractions
        .SelectMany(value => value.CanonicalRegistrations)
        .SelectMany(value => value.Registration.Artifacts)
        .Select(value => value.Id)
        .Distinct()
        .OrderBy(value => value.Value, StringComparer.Ordinal)
        .ToImmutableArray();
    var missingPrimaryBindings = primaryArtifactIds
        .Where(value => allAcquisitionBindings.Count(binding => binding.ArtifactId == value) != 1)
        .Select(value => $"{value.Value}:{allAcquisitionBindings.Count(binding => binding.ArtifactId == value)}")
        .ToArray();
    Require(missingPrimaryBindings.Length == 0,
        "Primary registration artifacts require one acquisition binding each: " +
        string.Join(",", missingPrimaryBindings));
    var originPayload = GtaVKnowledgePackageProjection.CreatePayload(
        extractions,
        allAcquisitionReceipts,
        allAcquisitionBindings);
    Console.WriteLine("RegistrationStage=PrimaryPayload");
    var sourceScope = KnowledgeSourceScope.BaseGame(
        ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
        acquisition.GameVersion);
    var secondaryAdapter = new GtaVWeaponsSecondaryAssertionAdapter(adapterDigest, sourceCorpus);
    var secondary = secondaryAdapter.Extract(
        originPayload,
        sourceScope,
        weaponsArtifact,
        baseLanguageRpfArtifact,
        baseGxt2Artifact);
    var weaponEnrichedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
        originPayload,
        secondary,
        allAcquisitionReceipts,
        allAcquisitionBindings);
    Console.WriteLine("RegistrationStage=WeaponTerminology");
    var weaponsOrganizationAdapter = new GtaVWeaponsOrganizationSecondaryAssertionAdapter(adapterDigest, sourceCorpus);
    var weaponsOrganization = weaponsOrganizationAdapter.Extract(
        weaponEnrichedPayload,
        sourceScope,
        weaponsArtifact);
    var weaponOrganizedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
        weaponEnrichedPayload,
        weaponsOrganization,
        allAcquisitionReceipts,
        allAcquisitionBindings);
    Console.WriteLine("RegistrationStage=WeaponOrganization");
    var actorSecondaryAdapter = new GtaVActorDlcSecondaryAssertionAdapter(adapterDigest, sourceCorpus);
    var actorSecondary = actorSecondaryAdapter.Extract(
        weaponOrganizedPayload,
        sourceScope,
        actorArtifact);
    var actorEnrichedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
        weaponOrganizedPayload,
        actorSecondary,
        allAcquisitionReceipts,
        allAcquisitionBindings);
    Console.WriteLine("RegistrationStage=HistoricalActorDlc");
    var populationTerminologyAdapter = new GtaVPopulationZoneTerminologyAdapter(enrichmentAdapterDigest, enrichmentCorpus);
    var populationTerminology = populationTerminologyAdapter.Extract(
        actorEnrichedPayload,
        sourceScope,
        populationZoneArtifact,
        baseLanguageRpfArtifact,
        baseGxt2Artifact);
    var populationEnrichedPayload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
        actorEnrichedPayload,
        populationTerminology,
        allAcquisitionReceipts,
        allAcquisitionBindings);
    Console.WriteLine("RegistrationStage=PopulationTerminology");
    var ambientActorRoles = ambientPedRoles.Extract(
        populationEnrichedPayload,
        sourceScope,
        actorArtifact,
        ambientPedArtifact);
    var localPayloadBeforeMountedActorAssertions = GtaVEnrichmentCoverageProjection.ApplyConsolidatedLocationCoverage(
        GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            populationEnrichedPayload,
            ambientActorRoles,
            allAcquisitionReceipts,
            allAcquisitionBindings),
        sourceScope);
    Console.WriteLine("RegistrationStage=AmbientActorRoles");
    var mountedActorAdapter = new GtaVMountedActorSecondaryAssertionAdapter(adapterDigest, actorCorpusIndex);
    var mountedActorResult = mountedActorAdapter.Extract(
        localPayloadBeforeMountedActorAssertions,
        sourceScope,
        residentActorArtifact,
        mountedActorClosureArtifacts,
        actorArtifact);
    var localPayload = mountedActorResult.Batches.Aggregate(
        localPayloadBeforeMountedActorAssertions,
        (current, batch) => GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            current, batch, allAcquisitionReceipts, allAcquisitionBindings));
    Console.WriteLine("RegistrationStage=MountedActorAssertions");
    GtaVRockstarCloudSnapshotBundle? cloudSnapshot = null;
    GtaVCloudJobHeaderSecondaryAssertionResult? cloudResult = null;
    RegistrationCloudMissionCoverage? cloudCoverage = null;
    object? referenceSourceIndexReport = null;
    var payload = localPayload;
    if (cloudSnapshotBundlePath is not null)
    {
        var establishedFmnm = localPayload.KnowledgeRecords
            .Where(value => value.Kind == KnowledgeKind.MissionQuest &&
                            string.Equals(value.NativeIdentity.Namespace,
                                "rockstar.gta-v.enhanced.ugc-mission", StringComparison.Ordinal) &&
                            string.Equals(value.NativeIdentity.ObjectType,
                                "UGCMissionFmnm", StringComparison.Ordinal))
            .Select(value => value.NativeIdentity.ExactRepresentation)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        Require(establishedFmnm.Length == 987 &&
                establishedFmnm.Distinct(StringComparer.Ordinal).Count() == establishedFmnm.Length,
            "Cloud registration requires the exact 987 established fmnm identities.");
        cloudSnapshot = GtaVRockstarCloudSnapshotBundleLoader.Load(
            cloudSnapshotBundlePath,
            registrationSourceRegistry.RockstarCloudJobs.ApprovedSchemaDescriptorBytes,
            establishedFmnm);
        referenceSourceIndexReport = RegistrationReferenceSourceIndexReport.Create(
            cloudSnapshot, registrationSourceRegistry);
        var cloudAdapter = new GtaVCloudJobHeaderSecondaryAssertionAdapter(adapterDigest);
        cloudResult = cloudAdapter.Extract(localPayload, sourceScope, cloudSnapshot);
        payload = GtaVKnowledgePackageProjection.AddSecondaryAssertions(
            localPayload,
            cloudResult.Batch,
            allAcquisitionReceipts.Add(cloudSnapshot.AcquisitionReceipt),
            allAcquisitionBindings.Add(cloudSnapshot.AcquisitionBinding));
        cloudCoverage = RegistrationCloudMissionCoverage.Create(cloudSnapshot, cloudResult);
    }
    Require(payload.KnowledgeRecords.SequenceEqual(originPayload.KnowledgeRecords),
        "Secondary enrichment must not regenerate or reorder any established canonical record identity.");
    ValidateExpectedCanary(
        payload, adapterDigest, enrichmentAdapterDigest, mountedActorResult,
        mountedSpatialMetrics, cloudResult);
    var git = GitBuildProvenanceResolver.ResolveCandidate();

    var validation = new CatalogValidationSummary(
        CatalogValidationStatus.Candidate,
        "grid.gta-v-enhanced.four-kind-canary.structural",
        "1",
        ContentDigest.ComputeSha256(Encoding.UTF8.GetBytes("candidate:not-qcs-evaluated:four-kind:v1")));
    var buildProvenance = git.Provenance.IsDevelopment
        ? git.Provenance
        : git.Provenance.WithAdapterBuildReceipts(adapterBuildReceipts);
    var package = CanonicalCatalogPackageKernel.CreateV7(
        CatalogPackageKind.BaseGameCatalog,
        new CatalogGameScope(
            ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            acquisition.GameVersion,
            payload.Artifacts.Select(value => value.Id).ToImmutableArray()),
        null,
        [],
        "grid.catalog-composition.v1",
        payload,
        validation,
        buildProvenance);
    var verification = CanonicalCatalogPackageKernel.Verify(package);
    Require(verification.IsStructurallyValid,
        "The four-kind canary package failed structural verification: " + string.Join("; ", verification.Issues));

    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    var packageJson = JsonSerializer.Serialize(package, options);
    var reloaded = JsonSerializer.Deserialize<CanonicalCatalogPackage>(packageJson, options) ??
        throw new InvalidDataException("The serialized canary package could not be reloaded.");
    var reloadedVerification = CanonicalCatalogPackageKernel.Verify(reloaded);
    Require(reloadedVerification.IsStructurallyValid && reloaded.Id == package.Id,
        "The reloaded canary package failed independent structural verification.");
    object coverageReport = RegistrationCoverageReport.Create(
        package,
        missionCoverageLedger,
        sourceFamilyManifestPath,
        actorSourceFamilyManifestPath,
        spatialSourceFamilyManifestPath,
        registrationSourceRegistry);
    if (cloudCoverage is not null && cloudSnapshot is not null)
    {
        coverageReport = RegistrationCloudMissionCoverageProjection.Apply(
            coverageReport, registrationSourceRegistry, cloudCoverage);
    }
    var sourceIndexReport = RegistrationSourceIndexReport.Create(acquisition, sourceFamilyManifestPath);

    var historicalStore = new JsonCanonicalKnowledgeCatalogStore(historicalLibraryPath);
    var historicalLibrary = await historicalStore.LoadAsync().ConfigureAwait(false);
    Require(historicalLibrary.IsValid, "The immutable historical v1 shared library is invalid.");
    ValidateHistoricalV1(historicalLibrary.Snapshot);
    var historicalChains = CreateEvidenceChains(
        historicalLibrary.Snapshot,
        KnowledgeAdapterRevisionId.LegacyAlgorithmVersion);

    EnsureOutputDirectory(outputDirectory);
    // Persist the already structurally verified immutable package before attempting the
    // append-only store import. A rejected import can then be audited without weakening
    // the store or rebuilding source inputs through a separate diagnostic path.
    await WriteNewAsync(Path.Combine(outputDirectory, "canary-package.v7.json"), packageJson).ConfigureAwait(false);
    var storePath = Path.Combine(outputDirectory, "shared-canonical-library.v5.json");
    File.Copy(historicalLibraryPath, storePath, overwrite: false);
    var store = new JsonCanonicalKnowledgeCatalogStore(storePath);
    var import = await store.ImportPackageAsync(historicalLibrary.Snapshot.Revision, package).ConfigureAwait(false);
    Require(import.Status == CanonicalCatalogImportStatus.Imported,
        $"The canary package was not atomically imported ({import.Status}: {import.Detail}).");
    var importedLibrary = await store.LoadAsync().ConfigureAwait(false);
    Require(importedLibrary.IsValid,
        "The imported shared canonical library is invalid: " + string.Join("; ", importedLibrary.Issues));
    var retry = await store.ImportPackageAsync(import.Revision, package).ConfigureAwait(false);
    Require(retry.Status == CanonicalCatalogImportStatus.Unchanged,
        $"An exact package retry was not idempotent ({retry.Status}: {retry.Detail}).");
    var library = await store.LoadAsync().ConfigureAwait(false);
    Require(library.IsValid, "The reloaded shared canonical library is invalid.");
    Require(library.Snapshot.FindImportedPackage(package.Id)?.Id == package.Id,
        "The imported package is not historically queryable.");
    foreach (var kind in Enum.GetValues<KnowledgeKind>())
        Require(library.Snapshot.KnowledgeRecords.Any(value => value.Kind == kind),
            $"The shared canonical library does not contain {kind} records.");
    foreach (var revisionId in package.Manifest.SourceRevisionIds)
        Require(library.Snapshot.FindSourceRevision(revisionId) is not null,
            "A historical package source revision is not queryable from the shared library.");
    ValidateHistoricalV1(library.Snapshot);
    var currentChains = CreateEvidenceChains(
        library.Snapshot,
        KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);

    var report = CreateReport(
        package,
        payload,
        verification,
        library.Snapshot,
        git.HeadCommit,
        adapterDigest,
        enrichmentAdapterDigest,
        historicalChains,
        currentChains);
    await WriteNewAsync(
        Path.Combine(outputDirectory, "canary-report.v3.json"),
        JsonSerializer.Serialize(report, options)).ConfigureAwait(false);
    await WriteNewAsync(
        Path.Combine(outputDirectory, "registration-coverage.v1.json"),
        JsonSerializer.Serialize(coverageReport, options)).ConfigureAwait(false);
    await WriteNewAsync(
        Path.Combine(outputDirectory, "registration-source-index.v1.json"),
        JsonSerializer.Serialize(sourceIndexReport, options)).ConfigureAwait(false);
    if (referenceSourceIndexReport is not null)
    {
        await WriteNewAsync(
            Path.Combine(outputDirectory, "registration-reference-index.v1.json"),
            JsonSerializer.Serialize(referenceSourceIndexReport, options)).ConfigureAwait(false);
    }

    Console.WriteLine($"PackageId={package.Id.Value}");
    Console.WriteLine($"CatalogRevisionId={package.Manifest.CatalogRevisionId.Value}");
    Console.WriteLine($"PayloadDigest={package.Manifest.PayloadDigest.Value}");
    Console.WriteLine($"Locations={payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Location)}");
    Console.WriteLine($"MissionQuests={payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.MissionQuest)}");
    Console.WriteLine($"Items={payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Item)}");
    Console.WriteLine($"Actors={payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Actor)}");
    Console.WriteLine($"MountedActorResidentRecords={mountedActorResult.Metrics.ResidentRecordCount}");
    Console.WriteLine($"MountedActorDlcRecords={mountedActorResult.Metrics.DlcRecordCount}");
    Console.WriteLine($"MountedActorNpcClassifications={mountedActorResult.Metrics.NpcClassificationCount}");
    Console.WriteLine($"MountedActorPlayerCharacterClassifications={mountedActorResult.Metrics.PlayerCharacterClassificationCount}");
    Console.WriteLine($"MountedActorGen9Correlations={mountedActorResult.Metrics.Gen9ResidentCorrelationCount}");
    Console.WriteLine($"MountedSpatialArchetypes={mountedSpatialMetrics.ArchetypeCount}");
    Console.WriteLine($"MountedSpatialRooms={mountedSpatialMetrics.RoomCount}");
    Console.WriteLine($"MountedSpatialInstances={mountedSpatialMetrics.InstanceCount}");
    Console.WriteLine($"MountedSpatialContainedBy={mountedSpatialMetrics.ContainedByCount}");
    Console.WriteLine($"MountedSpatialResolvedInstanceOf={mountedSpatialMetrics.ResolvedInstanceOfCount}");
    Console.WriteLine($"MountedSpatialUnresolvedInstanceOf={mountedSpatialMetrics.UnresolvedInstanceOfCount}");
    Console.WriteLine($"MountedSpatialConnectsTo={mountedSpatialMetrics.ConnectsToCount}");
    Console.WriteLine($"LibraryRevision={library.Snapshot.Revision}");
    Console.WriteLine($"ValidationStatus={package.Manifest.ValidationStatus}");
    Console.WriteLine($"StructurallyValid={verification.IsStructurallyValid}");
    Console.WriteLine("RegistrationCoverage=registration-coverage.v1.json");
    Console.WriteLine("RegistrationSourceIndex=registration-source-index.v1.json");
    if (referenceSourceIndexReport is not null)
    {
        Console.WriteLine("RegistrationReferenceIndex=registration-reference-index.v1.json");
        Console.WriteLine($"RockstarCloudMatchedTargets={cloudResult!.Metrics.ExactMatchedTargetRecordCount}");
        Console.WriteLine($"RockstarCloudTitledTargets={cloudResult.Metrics.TitledTargetRecordCount}");
        Console.WriteLine($"RockstarCloudFamilyOrganizedTargets={cloudResult.Metrics.FamilyOrganizedTargetRecordCount}");
        Console.WriteLine($"RockstarCloudUnmatchedTargets={cloudResult.Metrics.UnmatchedTargetRecordCount}");
    }
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static FrozenSourceArtifact CreateFrozen(
    ValidatedGtaAcquisition acquisition,
    string coordinate,
    Func<string, SourceNativeIdentifier> createCoordinate,
    KnowledgeFormatCoordinate format)
{
    if (!acquisition.Members.TryGetValue(coordinate, out var member))
        throw new InvalidDataException($"Required acquired member is absent: {coordinate}");
    return new FrozenSourceArtifact(
        member.ArtifactId,
        member.Digest,
        createCoordinate(coordinate),
        format,
        member.Bytes,
        acquisition.ObservedAtUtc);
}

static CatalogAdapterBuildProvenanceReceipt CreateAdapterBuildProvenanceReceipt(
    Assembly assembly,
    ContentDigest adapterArtifactDigest)
{
    var assemblyPath = assembly.Location;
    if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        throw new InvalidDataException("The exact adapter assembly path is unavailable.");
    if (ContentDigest.ComputeSha256(File.ReadAllBytes(assemblyPath)) != adapterArtifactDigest)
        throw new InvalidDataException("The adapter assembly digest changed while build provenance was captured.");

    var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
    if (!File.Exists(pdbPath))
        throw new InvalidDataException("The exact portable PDB for the adapter assembly is unavailable.");
    var pdbBytes = File.ReadAllBytes(pdbPath);
    var portablePdb = ReadPortablePdbBuildMetadata(pdbBytes);
    var compilerVersion = ReadCompilationOption(portablePdb.CompilationOptions, "compiler-version");
    var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .GroupBy(value => value.Key, StringComparer.Ordinal)
        .ToDictionary(
            value => value.Key,
            value => value.Single().Value ?? throw new InvalidDataException(
                $"Adapter assembly metadata {value.Key} is null."),
            StringComparer.Ordinal);

    string RequireMetadata(string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Adapter assembly metadata {key} is absent.");

    var deterministicText = RequireMetadata("Grid.Build.Deterministic");
    if (!bool.TryParse(deterministicText, out var deterministicBuild) || !deterministicBuild)
        throw new InvalidDataException("Reviewed adapter assemblies must declare a deterministic build.");
    var sourceLinkDigest = portablePdb.SourceLink is null
        ? (ContentDigest?)null
        : ContentDigest.ComputeSha256(portablePdb.SourceLink);

    return new CatalogAdapterBuildProvenanceReceipt(
        CatalogAdapterBuildProvenanceReceipt.CurrentSchemaVersion,
        adapterArtifactDigest,
        assembly.GetName().Name ?? throw new InvalidDataException("Adapter assembly name is absent."),
        assembly.GetName().Version?.ToString() ?? throw new InvalidDataException("Adapter assembly version is absent."),
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            throw new InvalidDataException("Adapter assembly informational version is absent."),
        assembly.ManifestModule.ModuleVersionId.ToString("D"),
        assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ??
            throw new InvalidDataException("Adapter target framework is absent."),
        assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ??
            throw new InvalidDataException("Adapter build configuration is absent."),
        RequireMetadata("Grid.Build.Platform"),
        RequireMetadata("Grid.Build.CompilerId"),
        compilerVersion,
        RequireMetadata("Grid.Build.SdkId"),
        RequireMetadata("Grid.Build.SdkVersion"),
        deterministicBuild,
        ContentDigest.ComputeSha256(pdbBytes),
        sourceLinkDigest is null ? CatalogBuildMetadataPresence.Absent : CatalogBuildMetadataPresence.Present,
        sourceLinkDigest,
        ContentDigest.ComputeSha256(portablePdb.CompilationOptions),
        ContentDigest.ComputeSha256(portablePdb.CompilationReferences));
}

static (byte[]? SourceLink, byte[] CompilationOptions, byte[] CompilationReferences)
    ReadPortablePdbBuildMetadata(byte[] pdbBytes)
{
    var sourceLinkKind = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
    var compilationOptionsKind = new Guid("b5feec05-8cd0-4a83-96da-466284bb4bd8");
    var compilationReferencesKind = new Guid("7e4d4708-096e-4c5c-aeda-cb10ba6a740d");
    using var stream = new MemoryStream(pdbBytes, writable: false);
    using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
    var reader = provider.GetMetadataReader();
    var blobs = reader.GetCustomDebugInformation(MetadataTokens.EntityHandle(TableIndex.Module, 1))
        .Select(handle => reader.GetCustomDebugInformation(handle))
        .Select(value => (Kind: reader.GetGuid(value.Kind), Bytes: reader.GetBlobBytes(value.Value)))
        .ToImmutableArray();

    byte[]? SingleOptional(Guid kind, string name)
    {
        var values = blobs.Where(value => value.Kind == kind).Select(value => value.Bytes).ToArray();
        if (values.Length > 1)
            throw new InvalidDataException($"Portable PDB contains duplicate {name} metadata.");
        return values.SingleOrDefault();
    }

    byte[] SingleRequired(Guid kind, string name) =>
        SingleOptional(kind, name) ??
        throw new InvalidDataException($"Portable PDB does not contain required {name} metadata.");

    return (
        SingleOptional(sourceLinkKind, "SourceLink"),
        SingleRequired(compilationOptionsKind, "compilation-options"),
        SingleRequired(compilationReferencesKind, "compilation-references"));
}

static string ReadCompilationOption(byte[] bytes, string requestedKey)
{
    var values = Encoding.UTF8.GetString(bytes).Split('\0');
    if (values.Length > 0 && values[^1].Length == 0)
        values = values[..^1];
    if (values.Length % 2 != 0)
        throw new InvalidDataException("Portable PDB compilation options are malformed.");
    var matches = Enumerable.Range(0, values.Length / 2)
        .Where(index => string.Equals(values[index * 2], requestedKey, StringComparison.Ordinal))
        .Select(index => values[(index * 2) + 1])
        .ToArray();
    return matches.Length == 1 && !string.IsNullOrWhiteSpace(matches[0])
        ? matches[0]
        : throw new InvalidDataException($"Portable PDB compilation option {requestedKey} is absent or ambiguous.");
}

static async Task<KnowledgeExtractionResult> DiscoverAndExtractAsync(
    IGameKnowledgeAdapter adapter,
    ImmutableArray<FrozenSourceArtifact> artifacts,
    SourceNativeVersion gameVersion)
{
    var batch = await DiscoverAndExtractWithDiscoveryAsync(adapter, artifacts, gameVersion).ConfigureAwait(false);
    return batch.Extraction;
}

static async Task<(SourceDiscoveryResult Discovery, KnowledgeExtractionResult Extraction)>
    DiscoverAndExtractWithDiscoveryAsync(
        IGameKnowledgeAdapter adapter,
        ImmutableArray<FrozenSourceArtifact> artifacts,
        SourceNativeVersion gameVersion)
{
    var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId;
    var discovery = await adapter.DiscoverAsync(new PreproductionSourceDiscoveryRequest(
        gameId, gameVersion, null, null, artifacts)).ConfigureAwait(false);
    Require(discovery.CoverageState != KnowledgeCoverageState.Unsupported && !discovery.SourceCandidates.IsEmpty,
        $"Adapter {adapter.Descriptor.AdapterId.Value} discovered no supported source.");
    var extraction = await adapter.ExtractAsync(new PreproductionKnowledgeExtractionRequest(
        gameId, gameVersion, null, null, adapter.Descriptor, artifacts)).ConfigureAwait(false);
    Require(extraction.CoverageState != KnowledgeCoverageState.Unsupported && !extraction.CanonicalRegistrations.IsEmpty,
        $"Adapter {adapter.Descriptor.AdapterId.Value} produced no canonical registrations.");
    return (discovery, extraction);
}

static void ValidateExpectedCanary(
    CanonicalCatalogPayload payload,
    ContentDigest adapterDigest,
    ContentDigest enrichmentAdapterDigest,
    GtaVMountedActorSecondaryAssertionResult mountedActorResult,
    GtaVMountedSpatialMetrics mountedSpatial,
    GtaVCloudJobHeaderSecondaryAssertionResult? cloudResult)
{
    var mountedActor = mountedActorResult.Metrics;
    var cloud = cloudResult?.Batch;
    var expectedAdapterCount = 15 + (cloud is null ? 0 : 1);
    Require(payload.AdapterDescriptors.Length == expectedAdapterCount &&
            payload.AdapterDescriptors.All(value =>
                value.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion &&
                value.Revision.SemanticContractDigest is not null),
        "Every reviewed GTA adapter must use the additive semantic v2 identity contract.");
    Require(payload.AdapterDescriptors.All(value =>
                value.AdapterArtifactDigest ==
                    (value.AdapterId.Value.Contains("population-zone", StringComparison.Ordinal) ||
                     value.AdapterId.Value.Contains("ambient-ped", StringComparison.Ordinal)
                        ? enrichmentAdapterDigest
                        : adapterDigest)),
        "Every v2 adapter descriptor must retain the exact assembly digest that produced it.");

    var locationCount = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Location);
    var mapZoneCount = payload.KnowledgeRecords.Count(value =>
        value.Kind == KnowledgeKind.Location &&
        string.Equals(value.NativeIdentity.Namespace, "rockstar.gta-v.enhanced.mapzones", StringComparison.Ordinal));
    var populationZoneCount = payload.KnowledgeRecords.Count(value =>
        value.Kind == KnowledgeKind.Location &&
        string.Equals(value.NativeIdentity.Namespace, "rockstar.gta-v.enhanced.population-zones", StringComparison.Ordinal));
    var mountedSpatialRecordCount = mountedSpatial.ArchetypeCount + mountedSpatial.RoomCount + mountedSpatial.InstanceCount;
    Require(locationCount == 138 + mountedSpatialRecordCount,
        $"The exact source revisions did not preserve 41 map zones and 97 population zones while adding every " +
        $"mounted MLO archetype, room, and instance (actual total {locationCount}, map-zone {mapZoneCount}, " +
        $"population-zone {populationZoneCount}, mounted {mountedSpatialRecordCount}).");
    Require(payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.MissionQuest) == 1057,
        "The exact source revisions did not preserve 987 fmnm identities and add 70 exact Online activity registry records.");
    Require(payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Item) == 102,
        "The exact source revision did not preserve 102 Item records.");
    Require(payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Actor) == 1120 &&
            mountedActor.ResidentRecordCount == 683 && mountedActor.DlcRecordCount == 435,
        "The mounted Actor corpus did not preserve two historical records and add 683 resident plus 435 DLC records.");
    var recordsById = payload.KnowledgeRecords.ToDictionary(value => value.Id);
    var cloudTitleCount = cloud?.TerminologyAssertions.Length ?? 0;
    Require(payload.TerminologyAssertions.Length == 219 + cloudTitleCount &&
            payload.TerminologyAssertions.All(value => value.KnowledgeRecordId != default) &&
            payload.TerminologyAssertions.Count(value =>
                recordsById[value.KnowledgeRecordId].Kind == KnowledgeKind.Item) == 53 &&
            payload.TerminologyAssertions.Count(value =>
                recordsById[value.KnowledgeRecordId].Kind == KnowledgeKind.Location &&
                value.Role == TerminologyAssertionRole.PrimaryName &&
                string.Equals(value.LanguageTag, "en-US", StringComparison.Ordinal)) == 96 &&
            payload.TerminologyAssertions.Count(value =>
                recordsById[value.KnowledgeRecordId].Kind == KnowledgeKind.MissionQuest &&
                value.Role == TerminologyAssertionRole.PrimaryName) == 70 + cloudTitleCount &&
            payload.TerminologyAssertions.Count(value =>
                recordsById[value.KnowledgeRecordId].Kind == KnowledgeKind.MissionQuest &&
                value.Role == TerminologyAssertionRole.PrimaryName &&
                string.Equals(value.LanguageTag, "und", StringComparison.Ordinal)) == 70 &&
            payload.TerminologyAssertions.All(value =>
                recordsById[value.KnowledgeRecordId].Kind is KnowledgeKind.Item or KnowledgeKind.Location or KnowledgeKind.MissionQuest),
        "The exact evidence must preserve 149 prior names, 70 FILE titles, and every admitted provider title.");
    var mountedSpatialRelationshipCount = mountedSpatial.ContainedByCount +
        mountedSpatial.ResolvedInstanceOfCount + mountedSpatial.UnresolvedInstanceOfCount +
        mountedSpatial.ConnectsToCount;
    Require(payload.RelationshipAssertions.Length == 91 + mountedSpatialRelationshipCount &&
            payload.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Resolved) ==
                27 + mountedSpatial.ContainedByCount + mountedSpatial.ResolvedInstanceOfCount +
                mountedSpatial.ConnectsToCount &&
            payload.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Unresolved) ==
                64 + mountedSpatial.UnresolvedInstanceOfCount,
        "The existing weapons and mounted spatial relationship semantics changed.");
    Require(payload.UnresolvedSourceAssertions.Length == 60,
        "The exact UGC source revision did not retain 60 unresolved mission resources.");
    Require(payload.ReferenceEvidenceReceipts.Length == (cloud?.ReferenceEvidenceReceipts.Length ?? 0),
        "REFERENCE_VERIFIED evidence must be absent locally or exactly closed by the verified cloud batch.");
    var cloudClassificationCount = cloud?.SemanticClassifications.Length ?? 0;
    Require(payload.SemanticClassificationAssertions.Count(value =>
                value.RoleId == CanonicalProjectionSemantics.ItemWeapons) == 91 &&
            payload.SemanticClassificationAssertions.Count(value =>
                value.RoleId == CanonicalProjectionSemantics.ActorNpc) == 1117 &&
            payload.SemanticClassificationAssertions.Count(value =>
                value.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter) == 3 &&
            payload.SemanticClassificationAssertions.Count(value =>
                value.RoleId == CanonicalProjectionSemantics.MissionOnline) == 70 + cloudClassificationCount &&
            payload.SemanticClassificationAssertions.Length == 1281 + cloudClassificationCount &&
            mountedActor.PlayerCharacterClassificationCount == 3 &&
            mountedActor.NpcClassificationCount == 1115 &&
            mountedActor.UnclassifiedRecordCount == 0,
        "The exact evidence must preserve local classifications and every admitted provider Online classification.");
    var cloudOrganizationCount = cloud?.OrganizationalValues.Length ?? 0;
    Require(payload.OrganizationalValueAssertions.Length == 1014 + cloudOrganizationCount &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode) == 451 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode) == 91 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.GroupDimension) == 62 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.SlotDimension) == 63 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.NavigateOrderEntryDimension) == 112 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.NavigateOrderNumberDimension) == 112 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.BestOrderEntryDimension) == 54 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == GtaVWeaponsOrganizationSecondaryAssertionAdapter.BestOrderNumberDimension) == 54 &&
            payload.OrganizationalValueAssertions.Count(value =>
                value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode) ==
                    15 + cloudOrganizationCount &&
            payload.OrganizationalValueAssertions.Where(value =>
                value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode)
                .All(value => value.VerbatimDisplayValue is not null) &&
            payload.OrganizationalValueAssertions.Where(value =>
                value.DimensionId != CanonicalProjectionSemantics.ActorDlcDimensionNode &&
                value.DimensionId != CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode)
                .All(value => value.VerbatimDisplayValue is null) &&
            mountedActor.DlcOrganizationalValueCount == 435,
        "The exact organization fields must preserve local values and every admitted provider family.");
    Require(payload.CrossSourceAssertions.Length == 2359 + (cloud?.CrossSourceAssertions.Length ?? 0) &&
            payload.CrossSourceTargetLinkClaims.Length == 2054 + (cloud?.TargetLinkClaims.Length ?? 0),
        "Every secondary claim requires an exact envelope and exact origin target link.");
    Require(payload.CorrelationEnvelopes.Length == 2 &&
            mountedActor.Gen9ResidentCorrelationCount == 2 &&
            payload.CorrelationEnvelopes.All(value =>
                value.Record.Outcome == CorrelationOutcome.Correlated &&
                value.Record.MemberIds.Length == 2),
        "The two historical Gen9 Actors must correlate without identity replacement to resident records.");
    Require(payload.UnresolvedCrossSourceClaimContents.Length ==
                (cloud?.UnresolvedCrossSourceClaimContents.Length ?? 0) &&
            payload.UnresolvedCrossSourceEvidenceBindings.Length ==
                (cloud?.UnresolvedCrossSourceEvidenceBindings.Length ?? 0) &&
            payload.UnresolvedCrossSourceAssertions.Length ==
                (cloud?.UnresolvedCrossSourceAssertions.Length ?? 0),
        "Unmatched or ambiguous cloud objects must remain exact unresolved cross-source assertions.");
    Require(payload.SourceNativeLocationTypeAssertions.Count(value =>
                string.Equals(value.ExactNativeType.ExactRepresentation, "CMapZone", StringComparison.Ordinal)) == 41 &&
            payload.SourceNativeLocationTypeAssertions.Count(value =>
                string.Equals(value.ExactNativeType.ExactRepresentation, "zone", StringComparison.Ordinal)) == 97 &&
            payload.SourceNativeLocationTypeAssertions.Length == 138 + mountedSpatialRecordCount,
        "The exact Location sources did not preserve the area-zone native types and add every mounted spatial native type.");
    Require(payload.LocationSemanticClassificationAssertions.Length ==
                138 + mountedSpatial.ArchetypeCount + mountedSpatial.RoomCount + (2 * mountedSpatial.InstanceCount) &&
            payload.LocationSemanticClassificationAssertions.Count(value =>
                value.RoleId == LocationSemanticRoles.AreaZone) == 138 &&
            payload.LocationSemanticClassificationAssertions.Count(value =>
                value.RoleId == LocationSemanticRoles.Room) == mountedSpatial.RoomCount &&
            payload.LocationSemanticClassificationAssertions.Count(value =>
                value.RoleId == LocationSemanticRoles.Instance) == mountedSpatial.InstanceCount &&
            payload.LocationSemanticClassificationAssertions.Count(value =>
                value.RoleId == LocationSemanticRoles.Interior) ==
                mountedSpatial.ArchetypeCount + mountedSpatial.InstanceCount,
        "The exact mounted spatial source revisions did not produce the expected independently evidenced roles.");
    Require(payload.LocationCoverageReports is [{ Status: LocationCoverageStatus.Partial }] &&
            payload.LocationCoverageReports[0].Terminology is
                { PrimaryNamedRecordCount: 96 } &&
            payload.LocationCoverageReports[0].Terminology.TotalRecordCount == locationCount &&
            payload.LocationCoverageReports[0].Terminology.IdentifierOnlyRecordCount == locationCount - 96 &&
            payload.LocationCoverageReports[0].SourceFamilies.Length == 5 &&
            payload.LocationCoverageReports[0].Hierarchy.SourceProvidedEdgeCount ==
                mountedSpatial.ContainedByCount + mountedSpatial.ResolvedInstanceOfCount +
                mountedSpatial.UnresolvedInstanceOfCount &&
            !payload.LocationCoverageReports[0].Hierarchy.NotProvidedBySource,
        "The GTA V Enhanced Location canary must remain Partial while truthfully adding mounted spatial hierarchy.");
}

static void ValidateHistoricalV1(CanonicalKnowledgeCatalogSnapshot snapshot)
{
    const string HistoricalPackageId =
        "grid.catalog-package.v6.sha256.871d56f389891b70ecfddb5698cb5d67679b6428e2856f9a8380fa21e6ff6d32";
    Require(snapshot.FindImportedPackage(new CatalogPackageId(HistoricalPackageId)) is not null,
        "The pinned historical v1 package is absent from the shared library.");
    var v1Descriptors = snapshot.AdapterDescriptors
        .Where(value => value.RevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.LegacyAlgorithmVersion)
        .ToImmutableArray();
    Require(v1Descriptors.Length == 9 && v1Descriptors.All(value => value.Revision.SemanticContractDigest is null),
        "The nine historical adapter revisions must remain exact v1 coordinates.");
    var enrichmentAdapterIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "grid.gta-v.enhanced.population-zones",
        "grid.gta-v.enhanced.population-zone-gxt2-secondary",
        "grid.gta-v.enhanced.ambient-ped-npc-secondary",
    };
    var historicalV1RevisionIds = snapshot.AdapterBoundSourceRevisions
        .Where(value => value.AdapterRevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.LegacyAlgorithmVersion)
        .Select(value => value.Revision.Id)
        .ToHashSet();
    var establishedRevisionIds = snapshot.AdapterBoundSourceRevisions
        .Where(value => value.AdapterRevisionId.AlgorithmVersion == KnowledgeAdapterRevisionId.LegacyAlgorithmVersion &&
                        !enrichmentAdapterIds.Contains(v1Descriptors.Single(
                            descriptor => descriptor.RevisionId == value.AdapterRevisionId).AdapterId.Value))
        .Select(value => value.Revision.Id)
        .ToImmutableArray();
    var establishedRecords = snapshot.KnowledgeRecords
        .Where(value => historicalV1RevisionIds.Contains(value.SourceRevisionId) &&
                        !string.Equals(value.NativeIdentity.Namespace,
                            "rockstar.gta-v.enhanced.population-zones", StringComparison.Ordinal))
        .ToImmutableArray();
    Require(establishedRecords.Length == 1_132 &&
            Fingerprint(establishedRecords.Select(value => value.Id.Value)) ==
                "a0cccd05ff5fe4c623b644011840b26f2ce63f855f548f61df7c3b97b62628be",
        "The 1,132 historical v1 canonical records changed or disappeared.");
    Require(establishedRevisionIds.Length == 1_052 &&
            Fingerprint(establishedRevisionIds.Select(value => value.Value)) ==
                "f84dcf947954dabc091b43d61aeda660bdbcbc3d660e0384196763c2bdc5a1f0",
        "The 1,052 historical v1 source revisions changed or disappeared.");
    Require(v1Descriptors.Count(value => !enrichmentAdapterIds.Contains(value.AdapterId.Value)) == 6 &&
            Fingerprint(v1Descriptors
                .Where(value => !enrichmentAdapterIds.Contains(value.AdapterId.Value))
                .Select(value => value.RevisionId.Value)) ==
                "026e34e89427503ed19f7dd55d965dcdc94769657167a75b360be2109ec5f4a7",
        "The six historical v1 adapter identities changed or disappeared.");
}

static string Fingerprint(IEnumerable<string> values)
{
    var ordered = values.OrderBy(value => value, StringComparer.Ordinal);
    var bytes = Encoding.UTF8.GetBytes(string.Join('\n', ordered));
    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

static SourceNativeIdentifier CreateSecondaryCoordinate(string coordinate, string objectType) =>
    SourceNativeIdentifier.FromExactUtf8(
        "rockstar.gta-v.enhanced.resource-coordinate",
        objectType,
        coordinate,
        "grid.gta-v.resource-coordinate.exact-utf8",
        1);

static object CreateReport(
    CanonicalCatalogPackage package,
    CanonicalCatalogPayload payload,
    CatalogPackageVerificationResult verification,
    CanonicalKnowledgeCatalogSnapshot library,
    string sourceCommit,
    ContentDigest adapterAssemblyDigest,
    ContentDigest enrichmentAdapterAssemblyDigest,
    ImmutableArray<EvidenceChainReport> historicalV1EvidenceChains,
    ImmutableArray<EvidenceChainReport> currentV2EvidenceChains) => new
{
    schemaVersion = 3,
    gameId = package.Manifest.GameScope.GameId.Value,
    gameBuild = package.Manifest.GameScope.ExactGameVersion?.ExactRepresentation,
    sourceCommit,
    adapterAssemblyDigest = adapterAssemblyDigest.HexValue,
    enrichmentAdapterAssemblyDigest = enrichmentAdapterAssemblyDigest.HexValue,
    adapterBuildReceipts = package.Manifest.BuildProvenance.AdapterBuildReceipts.Select(value => new
    {
        adapterArtifactDigest = value.AdapterArtifactDigest.HexValue,
        value.AssemblyName,
        value.ExactAssemblyVersion,
        value.AssemblyInformationalVersion,
        value.ModuleVersionId,
        value.TargetFramework,
        value.BuildConfiguration,
        value.BuildPlatform,
        value.CompilerId,
        value.ExactCompilerVersion,
        value.SdkId,
        value.ExactSdkVersion,
        value.DeterministicBuild,
        portablePdbDigest = value.PortablePdbDigest.HexValue,
        sourceLinkPresence = value.SourceLinkPresence.ToString(),
        sourceLinkContentDigest = value.SourceLinkContentDigest?.HexValue,
        compilationOptionsContentDigest = value.CompilationOptionsContentDigest.HexValue,
        compilationReferencesContentDigest = value.CompilationReferencesContentDigest.HexValue,
    }).ToArray(),
    acquisitionReceiptIds = payload.AcquisitionReceipts.Select(value => value.Id.Value).ToArray(),
    adapterRevisionIds = package.Manifest.AdapterRevisionIds.Select(value => value.Value).ToArray(),
    sourceRevisionIds = package.Manifest.SourceRevisionIds.Select(value => value.Value).ToArray(),
    payloadDigest = package.Manifest.PayloadDigest.Value,
    catalogRevisionId = package.Manifest.CatalogRevisionId.Value,
    packageId = package.Id.Value,
    coverage = package.Manifest.EffectiveCoverage.ToString(),
    validationStatus = package.Manifest.ValidationStatus.ToString(),
    structurallyValid = verification.IsStructurallyValid,
    structuralIssues = verification.Issues,
    sharedLibraryRevision = library.Revision,
    sharedLibraryPackageId = library.FindImportedPackage(package.Id)?.Id.Value,
    historicalV1EvidenceChains,
    currentV2EvidenceChains,
    locationCoverage = payload.LocationCoverageReports.Select(value => new
    {
        locationCoverageReportId = value.Id.Value,
        manifestId = value.Manifest.Id.Value,
        status = value.Status.ToString(),
        manifestClosed = value.Manifest.IsClosed,
        qcsStatus = value.Manifest.QcsValidation.Status.ToString(),
        sourceFamilies = value.SourceFamilies.Select(family => new
        {
            sourceFamilyId = family.SourceFamilyId.Value,
            discovered = family.DiscoveredObjectCount,
            acquired = family.AcquiredObjectCount,
            parsed = family.ParsedObjectCount,
            emitted = family.EmittedLocationRecordIds.Length,
            unresolved = family.UnresolvedSourceAssertionIds.Length,
            excluded = family.EvidenceBackedExclusions.Length,
            unsupported = family.UnsupportedObjectCount,
            parserErrors = family.ParserErrorCount,
            missingArtifacts = family.MissingArtifactCount,
            ambiguousClassifications = family.AmbiguousClassificationCount,
        }).ToArray(),
        nativeTypes = value.SemanticCategories
            .Where(category => category.ExactNativeType is not null)
            .Select(category => category.ExactNativeType!.ExactRepresentation)
            .ToArray(),
        semanticRoles = value.SemanticCategories
            .Where(category => category.SemanticRoleId is not null)
            .Select(category => category.SemanticRoleId!.Value.Value)
            .ToArray(),
        terminology = value.Terminology,
        hierarchy = value.Hierarchy,
    }).ToArray(),
    counts = new
    {
        records = payload.KnowledgeRecords.Length,
        locations = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Location),
        missionQuests = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.MissionQuest),
        items = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Item),
        actors = payload.KnowledgeRecords.Count(value => value.Kind == KnowledgeKind.Actor),
        terminology = payload.TerminologyAssertions.Length,
        relationships = payload.RelationshipAssertions.Length,
        unresolvedRelationships = payload.RelationshipAssertions.Count(value => value.Resolution == CanonicalResolutionState.Unresolved),
        unresolvedSourceAssertions = payload.UnresolvedSourceAssertions.Length,
        fileEvidence = payload.FileEvidenceReceipts.Length,
        referenceEvidence = payload.ReferenceEvidenceReceipts.Length,
        evidenceBindings = payload.EvidenceBindings.Length,
        sourceRevisions = payload.SourceRevisions.Length,
        artifacts = payload.Artifacts.Length,
        locationNativeTypeAssertions = payload.SourceNativeLocationTypeAssertions.Length,
        locationSemanticClassifications = payload.LocationSemanticClassificationAssertions.Length,
        locationCoverageReports = payload.LocationCoverageReports.Length,
        locationCoverageSourceFamilies = payload.LocationCoverageReports.Sum(value => value.SourceFamilies.Length),
        locationContainedByRelationships = payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.ContainedBy),
        locationInstanceOfRelationships = payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.InstanceOf),
        locationConnectsToRelationships = payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.ConnectsTo),
        locationResolvedInstanceOfRelationships = payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.InstanceOf &&
            value.Resolution == CanonicalResolutionState.Resolved),
        locationUnresolvedInstanceOfRelationships = payload.RelationshipAssertions.Count(value =>
            value.SemanticId == LocationRelationshipSemantics.InstanceOf &&
            value.Resolution == CanonicalResolutionState.Unresolved),
        itemSemanticClassifications = payload.SemanticClassificationAssertions.Count(value =>
            value.RoleId == CanonicalProjectionSemantics.ItemWeapons),
        actorSemanticClassifications = payload.SemanticClassificationAssertions.Count(value =>
            value.RoleId == CanonicalProjectionSemantics.ActorNpc ||
            value.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter),
        actorNpcClassifications = payload.SemanticClassificationAssertions.Count(value =>
            value.RoleId == CanonicalProjectionSemantics.ActorNpc),
        actorPlayerCharacterClassifications = payload.SemanticClassificationAssertions.Count(value =>
            value.RoleId == CanonicalProjectionSemantics.ActorPlayerCharacter),
        actorDlcOrganizationalValues = payload.OrganizationalValueAssertions.Count(value =>
            value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode),
        weaponOrganizationalValues = payload.OrganizationalValueAssertions.Count(value =>
            value.DimensionId != CanonicalProjectionSemantics.ActorDlcDimensionNode &&
            value.DimensionId != CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode),
        missionActivityFamilyOrganizationalValues = payload.OrganizationalValueAssertions.Count(value =>
            value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode),
        crossSourceAssertions = payload.CrossSourceAssertions.Length,
        crossSourceTargetLinks = payload.CrossSourceTargetLinkClaims.Length,
        correlations = payload.CorrelationEnvelopes.Length,
    },
    records = payload.KnowledgeRecords.Select(value => new
    {
        knowledgeRecordId = value.Id.Value,
        kind = value.Kind.ToString(),
        nativeObjectType = value.NativeIdentity.ObjectType,
        nativeIdentifier = value.NativeIdentity.ExactRepresentation,
        sourceRevisionId = value.SourceRevisionId.Value,
    }).ToArray(),
};

static ImmutableArray<EvidenceChainReport> CreateEvidenceChains(
    CanonicalKnowledgeCatalogSnapshot snapshot,
    int adapterIdentityAlgorithmVersion)
{
    var revisionAlgorithms = snapshot.AdapterBoundSourceRevisions.ToDictionary(
        value => value.Revision.Id,
        value => value.AdapterRevisionId.AlgorithmVersion);
    var fileEvidence = snapshot.FileEvidenceReceipts.ToDictionary(value => value.Id);
    var acquisitionBindings = snapshot.ArtifactAcquisitionBindings
        .GroupBy(value => value.ArtifactId)
        .ToDictionary(
            value => value.Key,
            value => value.OrderBy(item => item.MemberCoordinate.ExactRepresentation, StringComparer.Ordinal)
                .ThenBy(item => item.AcquisitionReceiptId.Value, StringComparer.Ordinal)
                .ToImmutableArray());
    var acquisitionReceipts = snapshot.AcquisitionReceipts.ToDictionary(value => value.Id);
    var chains = ImmutableArray.CreateBuilder<EvidenceChainReport>(4);
    foreach (var kind in Enum.GetValues<KnowledgeKind>())
    {
        var record = snapshot.KnowledgeRecords
            .Where(value => value.Kind == kind &&
                            revisionAlgorithms.GetValueOrDefault(value.SourceRevisionId) == adapterIdentityAlgorithmVersion)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidDataException(
                $"No {kind} record exists for adapter identity v{adapterIdentityAlgorithmVersion}.");
        var binding = snapshot.EvidenceBindings
            .Where(value => value.KnowledgeRecordId == record.Id &&
                            value.SourceRevisionId == record.SourceRevisionId &&
                            value.ClaimKind == EvidenceClaimKind.KnowledgeIdentity &&
                            value.ClaimContentId is null)
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .FirstOrDefault() ?? throw new InvalidDataException(
                $"The representative {kind} record has no exact identity evidence binding.");
        Require(fileEvidence.TryGetValue(binding.EvidenceReceiptId, out var catalogReceipt),
            $"The representative {kind} identity binding does not resolve to FILE_VERIFIED evidence.");
        var receipt = catalogReceipt!.Receipt;
        Require(receipt.Verification == EvidenceVerificationKind.FileVerified &&
                receipt.SourceRevisionId == record.SourceRevisionId &&
                string.Equals(binding.ClaimLocator, receipt.SourceFieldPath, StringComparison.Ordinal),
            $"The representative {kind} FILE_VERIFIED receipt coordinates do not match its binding.");
        var sourceRevision = snapshot.FindSourceRevision(record.SourceRevisionId);
        Require(sourceRevision is not null,
            $"The representative {kind} source revision is not historically queryable.");
        Require(acquisitionBindings.TryGetValue(receipt.SourceArtifactId, out var candidateAcquisitionBindings),
            $"The representative {kind} artifact is not acquisition-bound.");
        var matchingAcquisitionBindings = candidateAcquisitionBindings!
            .Where(value =>
            {
                var coordinatePrefix =
                    $"rpf7-member:{value.MemberCoordinate.ExactRepresentation}#";
                return receipt.NativeRecordLocator.StartsWith(coordinatePrefix, StringComparison.Ordinal) &&
                       receipt.SourceFieldPath.StartsWith(coordinatePrefix, StringComparison.Ordinal);
            })
            .ToImmutableArray();
        var matchingCoordinateGroups = matchingAcquisitionBindings
            .GroupBy(value => value.MemberCoordinate)
            .ToImmutableArray();
        Require(matchingCoordinateGroups.Length == 1,
            $"The representative {kind} FILE_VERIFIED receipt does not identify one exact acquired member coordinate.");
        var acquisitionBinding = matchingCoordinateGroups[0]
            .OrderBy(value => value.AcquisitionReceiptId.Value, StringComparer.Ordinal)
            .First();
        Require(acquisitionReceipts.TryGetValue(acquisitionBinding!.AcquisitionReceiptId, out var acquisitionReceipt),
            $"The representative {kind} acquisition receipt is absent.");
        var member = acquisitionReceipt!.Members.SingleOrDefault(value =>
            value.ArtifactId == receipt.SourceArtifactId &&
            value.MemberCoordinate == acquisitionBinding.MemberCoordinate);
        Require(member is not null &&
                member.Digest == receipt.ArtifactDigest &&
                member.Digest == acquisitionBinding.MemberDigest &&
                member.ByteLength == acquisitionBinding.MemberByteLength,
            $"The representative {kind} artifact/member/acquisition digests or lengths do not close.");
        chains.Add(new EvidenceChainReport(
            kind.ToString(),
            record.Id.Value,
            binding.Id.Value,
            catalogReceipt.Id.Value,
            EvidenceVerificationKind.FileVerified.ToString(),
            record.SourceRevisionId.Value,
            receipt.SourceArtifactId.Value,
            receipt.NativeRecordLocator,
            receipt.SourceFieldPath,
            acquisitionBinding.MemberCoordinate.ExactRepresentation,
            acquisitionBinding.MemberByteLength,
            acquisitionBinding.MemberDigest.HexValue,
            acquisitionReceipt.Id.Value));
    }
    return chains.ToImmutable();
}

static async Task VerifyAcquisitionAsync(
    string uvExecutable,
    string fiveFuryWheel,
    string gameRoot,
    string receiptPath,
    string repositoryRoot)
{
    var script = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_acquire.py");
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = uvExecutable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
    process.StartInfo.Environment["UV_CACHE_DIR"] = Path.Combine(
        Path.GetTempPath(),
        "grid-gta-enhanced-canary-uv-cache");
    foreach (var argument in new[]
             {
                 "run", "--offline", "--python", "3.11", "--with", fiveFuryWheel, "python", script,
                 "verify", "--game-root", gameRoot, "--fivefury-wheel", fiveFuryWheel,
                 "--receipt", receiptPath,
             })
        process.StartInfo.ArgumentList.Add(argument);
    Require(process.Start(), "The acquisition verifier could not be started.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    Require(process.ExitCode == 0,
        $"Independent container-to-member acquisition verification failed: {error.Trim()} {output.Trim()}".Trim());
}

static async Task VerifyActorAcquisitionAsync(
    string uvExecutable,
    string fiveFuryWheel,
    string gameRoot,
    string receiptPath,
    string repositoryRoot)
{
    var script = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_actor_acquire.py");
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = uvExecutable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
    process.StartInfo.Environment["UV_CACHE_DIR"] = Path.Combine(
        Path.GetTempPath(), "grid-gta-enhanced-canary-uv-cache");
    foreach (var argument in new[]
             {
                 "run", "--offline", "--python", "3.11", "--with", fiveFuryWheel, "python", script,
                 "verify", "--game-root", gameRoot, "--fivefury-wheel", fiveFuryWheel,
                 "--receipt", receiptPath,
             })
        process.StartInfo.ArgumentList.Add(argument);
    Require(process.Start(), "The mounted Actor acquisition verifier could not be started.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    Require(process.ExitCode == 0,
        $"Independent mounted Actor acquisition verification failed: {error.Trim()} {output.Trim()}".Trim());
}

static async Task VerifySpatialAcquisitionAsync(
    string uvExecutable,
    string fiveFuryWheel,
    string gameRoot,
    string receiptPath,
    string repositoryRoot)
{
    var script = Path.Combine(
        repositoryRoot,
        "scripts", "games", "grandtheftautov", "catalog", "gta_v_enhanced_spatial_acquire.py");
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo
        {
            FileName = uvExecutable,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        },
    };
    process.StartInfo.Environment["UV_CACHE_DIR"] = Path.Combine(
        Path.GetTempPath(), "grid-gta-enhanced-canary-uv-cache");
    foreach (var argument in new[]
             {
                 "run", "--offline", "--python", "3.11", "--with", fiveFuryWheel, "python", script,
                 "verify", "--game-root", gameRoot, "--fivefury-wheel", fiveFuryWheel,
                 "--receipt", receiptPath,
             })
        process.StartInfo.ArgumentList.Add(argument);
    Require(process.Start(), "The mounted spatial acquisition verifier could not be started.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().ConfigureAwait(false);
    var output = await standardOutput.ConfigureAwait(false);
    var error = await standardError.ConfigureAwait(false);
    Require(process.ExitCode == 0,
        $"Independent mounted spatial acquisition verification failed: {error.Trim()} {output.Trim()}".Trim());
}

static Dictionary<string, string> ParseArguments(string[] values)
{
    if (values.Length == 0 || values.Length % 2 != 0)
        throw new ArgumentException(
            "Expected paired options: --game-root, --acquisition-receipt, --fivefury-wheel, --uv-executable, " +
            "--historical-library, --actor-acquisition-receipt, --actor-corpus-index, " +
            "--spatial-acquisition-receipt, --spatial-corpus-index, --output, " +
            "and optional --rockstar-cloud-snapshot-bundle.");
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2)
    {
        var option = values[index];
        if (!option.StartsWith("--", StringComparison.Ordinal) || option.Length == 2 ||
            !result.TryAdd(option[2..], values[index + 1]))
            throw new ArgumentException($"Invalid or duplicate option '{option}'.");
    }
    return result;
}

static string RequireArgument(IReadOnlyDictionary<string, string> values, string name)
{
    if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        throw new ArgumentException($"Missing required --{name} value.");
    return value;
}

static string? OptionalArgument(IReadOnlyDictionary<string, string> values, string name) =>
    values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

static void EnsureOutputDirectory(string outputDirectory)
{
    if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        throw new IOException("Output directory must be absent or empty.");
    Directory.CreateDirectory(outputDirectory);
}

static async Task WriteNewAsync(string path, string content)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await using var writer = new StreamWriter(stream, new UTF8Encoding(false, true));
    await writer.WriteAsync(content).ConfigureAwait(false);
}

internal sealed record EvidenceChainReport(
    string KnowledgeKind,
    string KnowledgeRecordId,
    string EvidenceBindingId,
    string EvidenceReceiptId,
    string EvidenceClass,
    string SourceRevisionId,
    string SourceArtifactId,
    string NativeRecordLocator,
    string SourceFieldPath,
    string AcquisitionMemberCoordinate,
    long AcquisitionMemberByteLength,
    string AcquisitionMemberSha256,
    string AcquisitionReceiptId);
