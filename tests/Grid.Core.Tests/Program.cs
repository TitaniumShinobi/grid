using System.Collections.Immutable;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;
using MockLaunchTargetState = Grid.Core.Application.LaunchTargetSelectionState;
using MockWorkspaceSessionState = Grid.Core.Application.WorkspaceSessionState;

var checks = 0;
var service = new MockGridCatalogService();
var catalog = await service.GetCatalogAsync();

var productionService = new ProductionGridCatalogService();
var stageOneProductionCatalog = await productionService.GetCatalogAsync();
Assert(stageOneProductionCatalog.SourceKind == CatalogSourceKind.Adapter, "Production identifies adapter-backed catalog provenance.");
Assert(stageOneProductionCatalog.Games.Length == 2, "Production declares Skyrim and GTA onboarding definitions.");
Assert(stageOneProductionCatalog.Games.All(game => game.Installations.IsEmpty), "A clean production catalog contains zero installations.");
Assert(stageOneProductionCatalog.Games.SelectMany(game => game.Installations).All(installation =>
    installation.Metadata.Provenance != InstallationProvenanceKind.Mock), "Production contains no mock installation provenance.");
Assert(stageOneProductionCatalog.Games.Any(game => game.Id == ProductionGridCatalogService.GrandTheftAutoVId),
    "Production exposes the GTA registration target without fabricating an installation.");
Assert(stageOneProductionCatalog.Games.All(game => game.Installations.IsEmpty),
    "A clean production Home projection has no connected-game cards or fabricated status values.");

var registrationFixture = Path.Combine(Path.GetTempPath(), "grid-game-registration-" + Guid.NewGuid().ToString("N"));
try
{
    var gameRoot = Path.Combine(registrationFixture, "Grand Theft Auto V");
    Directory.CreateDirectory(gameRoot);
    var executable = Path.Combine(gameRoot, "GTA5.exe");
    await File.WriteAllTextAsync(executable, "fixture");
    var registrationStore = new JsonGameInstallationRegistrationStore(Path.Combine(registrationFixture, "connections", "game-installations.v1.json"));
    var firstRegistration = await registrationStore.RegisterAsync(
        ProductionGridCatalogService.GrandTheftAutoVId,
        ProductionGridCatalogService.ProviderDiscoveryAdapterId,
        "Grand Theft Auto V · Legacy", "Legacy", "steam", gameRoot, executable, ["vortex"]);
    var loadedRegistrations = await registrationStore.LoadAsync();
    Assert(loadedRegistrations.Issues.IsEmpty && loadedRegistrations.Registrations.Length == 1,
        "Reviewed game registration persists as one Grid-owned reference.");
    Assert(firstRegistration.ManagerProviderIds.SequenceEqual(["vortex"]),
        "Registration retains reviewed manager relationships.");
    await registrationStore.RegisterAsync(
        ProductionGridCatalogService.GrandTheftAutoVId,
        ProductionGridCatalogService.ProviderDiscoveryAdapterId,
        "Grand Theft Auto V · Legacy", "Legacy", "steam", gameRoot, executable, ["vortex"]);
    Assert((await registrationStore.LoadAsync()).Registrations.Length == 1,
        "Canonical game identity makes repeated registration idempotent.");
    var registeredCatalog = await new RegisteredGameCatalogService(new ProductionGridCatalogService(), registrationStore).GetCatalogAsync();
    var registeredGta = registeredCatalog.Games.Single(game => game.Id == ProductionGridCatalogService.GrandTheftAutoVId);
    Assert(registeredGta.Installations.Length == 1 && registeredGta.Installations[0].Metadata.Provenance == InstallationProvenanceKind.ConnectedReference,
        "Persisted GTA registration projects into the production catalog as an external read-only installation.");
    var vortexRoot = Path.Combine(registrationFixture, "Vortex staging");
    Directory.CreateDirectory(vortexRoot);
    Directory.CreateDirectory(Path.Combine(vortexRoot, "Menyoo 2.0"));
    var vortexStore = new JsonVortexInstallationConnectionStore(Path.Combine(registrationFixture, "connections", "vortex-installations.v1.json"));
    await vortexStore.ConnectAsync(firstRegistration.InstallationId, vortexRoot);
    var vortexCatalogService = new VortexCatalogService(new RegisteredGameCatalogService(new ProductionGridCatalogService(), registrationStore), vortexStore);
    var vortexCatalog = await vortexCatalogService.GetCatalogAsync();
    var vortexInstallation = vortexCatalog.Games.Single(game => game.Id == ProductionGridCatalogService.GrandTheftAutoVId).Installations.Single();
    Assert(vortexInstallation.AdapterId == VortexCatalogService.AdapterId && vortexInstallation.Profiles.Single().Mods.Length == 1,
        "A connected Vortex staging directory projects its current GTA mod inventory.");
    Assert(vortexInstallation.Profiles.Single().Mods.Single().Kind == ModEntryKind.UnlistedDirectory && !vortexInstallation.Profiles.Single().Mods.Single().IsEnabled,
        "A staging directory does not fabricate authoritative Vortex deployment state.");
    var firstVortexRevision = vortexCatalog.Revision;
    Directory.CreateDirectory(Path.Combine(vortexRoot, "Community Script Hook V .NET"));
    vortexCatalog = await vortexCatalogService.GetCatalogAsync();
    Assert(vortexCatalog.Revision != firstVortexRevision && vortexCatalog.Games.Single(game => game.Id == ProductionGridCatalogService.GrandTheftAutoVId).Installations.Single().Profiles.Single().Mods.Length == 2,
        "A later catalog observation includes Vortex mods added after initial registration.");
    Assert(await vortexStore.RemoveAsync(firstRegistration.InstallationId) && (await vortexStore.LoadAsync()).Connections.IsEmpty,
        "Disconnecting Vortex removes only Grid's Vortex connection record.");
    File.Delete(executable);
    registeredCatalog = await new RegisteredGameCatalogService(new ProductionGridCatalogService(), registrationStore).GetCatalogAsync();
    Assert(registeredCatalog.Games.Single(game => game.Id == ProductionGridCatalogService.GrandTheftAutoVId).Installations[0].Metadata.Availability == InstallationAvailability.Missing,
        "A missing registered executable remains visible with missing availability.");
    Assert(await registrationStore.RemoveAsync(firstRegistration.InstallationId) && (await registrationStore.LoadAsync()).Registrations.IsEmpty,
        "Disconnect removes only the selected Grid-owned game registration.");
}
finally
{
    if (Directory.Exists(registrationFixture)) Directory.Delete(registrationFixture, recursive: true);
}

Assert(catalog.SourceKind == CatalogSourceKind.Mock, "The catalog explicitly identifies mock data.");
Assert(catalog.Revision == "mock-catalog-v4", "The mock catalog revision is deterministic.");
Assert(catalog.Games.Length == 2, "The catalog contains Skyrim and GTA.");
Assert(catalog.Games.Select(game => game.Id).Distinct().Count() == catalog.Games.Length, "Game identifiers are unique.");

var skyrimId = new GameId("game.skyrim-special-edition");
var gtaId = new GameId("game.grand-theft-auto-v");
var undefeatedId = new InstallationId("installation.skyrim.undefeated");
var nefarammId = new InstallationId("installation.skyrim.nefaram");
var customId = new InstallationId("installation.skyrim.custom");
var skyrim = catalog.Games.Single(game => game.Id == skyrimId);
var gta = catalog.Games.Single(game => game.Id == gtaId);
var undefeated = skyrim.Installations.Single(installation => installation.Id == undefeatedId);
var nefaramm = skyrim.Installations.Single(installation => installation.Id == nefarammId);
var custom = skyrim.Installations.Single(installation => installation.Id == customId);

Assert(skyrim.Adapters.Length == 3, "Skyrim aggregates mock, connected-MO2, and deployment adapter identities.");
Assert(skyrim.Adapters.Any(adapter => adapter.Id == new GameAdapterId("adapter.mod-organizer-2")), "Skyrim registers the stable real MO2 adapter identity for connected references.");
Assert(skyrim.Installations.Length == 3, "Skyrim has distinct UNDEFEATED, NEFARAM, and Custom installations.");
Assert(skyrim.Installations.All(installation => installation.Kind == InstallationKind.External), "All represented Skyrim installations are external.");
Assert(skyrim.Installations.All(installation => installation.AccessMode == WorkspaceAccessMode.ReadOnly), "All represented installations are read-only.");
Assert(skyrim.Installations.All(installation => installation.Metadata.Provenance == InstallationProvenanceKind.Mock), "Deterministic fixture installations explicitly retain mock provenance.");
Assert(skyrim.Installations.All(installation => installation.Metadata.ReferenceId is null), "Mock installations never claim a persisted external reference identity.");
Assert(skyrim.Installations.Select(installation => installation.Id).Distinct().Count() == 3, "Curated installations have stable, distinct identities.");
Assert(undefeated.Profiles.Length == 3, "UNDEFEATED contains multiple internal profiles.");
Assert(undefeated.Profiles.All(profile => profile.InstallationId == undefeated.Id), "UNDEFEATED profiles retain their installation parent.");
Assert(undefeated.Profiles.Count(profile => profile.Lifecycle == ProfileLifecycleState.Archived) == 1, "UNDEFEATED includes a represented archived profile.");
Assert(undefeated.Profiles.Where(profile => profile.Lifecycle == ProfileLifecycleState.Available).All(profile => profile.Features.HasFlag(ProfileFeature.Saves)), "Available MO2 profiles advertise represented profile saves.");
Assert(undefeated.SupportedProfileFeatures.HasFlag(ProfileFeature.ConfigurationFiles), "The MO2 installation advertises represented profile INI support.");
Assert(undefeated.Metadata.Availability == InstallationAvailability.Available, "UNDEFEATED is an available represented external installation.");
Assert(custom.Metadata.Availability == InstallationAvailability.Missing && custom.Metadata.LocationDisplay is null, "Custom represents a missing external installation without an authoritative path.");
Assert(undefeated.Profiles.SelectMany(profile => profile.Mods).Any(), "Skyrim profiles contain deterministic mod entries.");
Assert(undefeated.Profiles.SelectMany(profile => profile.Plugins).Any(), "Plugin entries are supplied for a capable installation.");
Assert(undefeated.Profiles[0].Mods.Any(mod => mod.Kind == ModEntryKind.Separator), "The mock mod list represents explicit separators.");
Assert(undefeated.Profiles[0].Mods.Where(mod => mod.Kind == ModEntryKind.Mod).All(mod => !string.IsNullOrWhiteSpace(mod.Category)), "Represented mods include readable categories.");
Assert(undefeated.Profiles[0].EnvironmentEntries.Any(), "Capable Skyrim profiles include generic environment evidence.");
Assert(custom.Profiles.All(profile => profile.Plugins.IsEmpty), "Plugin absence is explicit for the deployment-preview installation.");
Assert(undefeated.ToolConfigurations.Any(tool => tool.Availability == AvailabilityState.PreviewOnly), "Configured mock tools expose preview-only availability.");
Assert(undefeated.ToolConfigurations.Any(tool => tool.Availability == AvailabilityState.Unavailable), "Unavailable tool configuration remains explicit.");
Assert(undefeated.LaunchTargetConfigurations.Any(target => target.Availability == AvailabilityState.PreviewOnly), "Configured mock launch targets remain preview-only.");
Assert(skyrim.ToolCatalog.Tools.Any() && skyrim.ToolCatalog.LaunchTargets.Any(), "Skyrim exposes an adapter-declared game tool catalog.");

Assert(skyrim.Capabilities.Supports(WorkspaceFeature.ModList), "Skyrim supports a mod list.");
Assert(skyrim.Capabilities.Supports(EnvironmentTabCapability.Plugins), "Skyrim supports the Plugins environment tab.");
Assert(gta.Capabilities.Supports(EnvironmentTabCapability.Activity), "GTA exposes a safe Activity capability.");
Assert(!gta.Capabilities.Supports(WorkspaceFeature.ModList), "GTA does not claim a mod-list capability.");
Assert(!gta.Capabilities.Supports(EnvironmentTabCapability.Plugins), "GTA does not claim a Plugins capability.");
Assert(gta.Installations.IsEmpty, "GTA has no fabricated installation or profile.");

AssertThrows<ArgumentException>(() => _ = new GameId(" "), "Blank stable identifiers are rejected.");
AssertThrows<ArgumentException>(() => _ = new InstallationReferenceId(" "), "Blank installation-reference identifiers are rejected.");
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("duplicate-games", CatalogSourceKind.Mock, catalog.Games.Add(skyrim)),
    "Duplicate game identifiers are rejected.");

var adapterlessGame = skyrim with { Adapters = ImmutableArray<GameAdapterIdentity>.Empty };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("missing-game-adapter", CatalogSourceKind.Mock, [adapterlessGame, gta]),
    "Managed games require at least one adapter identity.");

var unknownCapabilityGame = gta with
{
    Capabilities = new WorkspaceCapabilities((WorkspaceFeature)(1 << 20), EnvironmentTabCapability.Activity),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("unknown-capability", CatalogSourceKind.Mock, [skyrim, unknownCapabilityGame]),
    "Unknown capability flags are rejected.");

var wrongParentInstallation = undefeated with { GameId = gta.Id };
var wrongParentGame = skyrim with { Installations = skyrim.Installations.SetItem(0, wrongParentInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("wrong-installation-parent", CatalogSourceKind.Mock, [wrongParentGame, gta]),
    "Installation parent mismatches are rejected.");

var unknownAdapterInstallation = undefeated with { AdapterId = new GameAdapterId("adapter.missing") };
var unknownAdapterGame = skyrim with { Installations = skyrim.Installations.SetItem(0, unknownAdapterInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("unknown-adapter", CatalogSourceKind.Mock, [unknownAdapterGame, gta]),
    "Installations cannot reference an unregistered adapter.");

var referenceId = new InstallationReferenceId("reference.mo2.connected-test");
var observedAtUtc = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
var connectedProfiles = undefeated.Profiles
    .Select((profile, index) => profile with
    {
        Observation = new(
            ProfileObservationStatus.Complete,
            index == 0 ? ManagerProfileState.Active : ManagerProfileState.Inactive,
            observedAtUtc,
            $"snapshot.connected.{index + 1}",
            0,
            LocalSavesEnabled: true,
            LocalSettingsEnabled: true,
            [
                new("settings.ini", ProfileSourceAvailability.Read, ProfileSourceParseStatus.Parsed, 0),
                new("modlist.txt", ProfileSourceAvailability.Read, ProfileSourceParseStatus.Parsed, 0),
            ]),
    })
    .ToImmutableArray();
var connectedInstallation = undefeated with
{
    Profiles = connectedProfiles,
    Metadata = undefeated.Metadata with
    {
        Provenance = InstallationProvenanceKind.ConnectedReference,
        ReferenceId = referenceId,
        StatusDetail = "Connected external installation reference used only for Core provenance checks.",
    },
};
var connectedGame = skyrim with
{
    Installations = skyrim.Installations.SetItem(0, connectedInstallation),
};
var mixedCatalog = new GridCatalogSnapshot("mixed-provenance", CatalogSourceKind.Mixed, [connectedGame, gta]);
Assert(mixedCatalog.SourceKind == CatalogSourceKind.Mixed, "A catalog can explicitly represent mixed mock and connected-reference provenance.");
Assert(
    connectedInstallation.Profiles.All(profile => profile.Observation is not null) &&
    connectedInstallation.Profiles.Count(profile => profile.Observation!.ManagerState == ManagerProfileState.Active) == 1,
    "Connected profiles carry immutable observation summaries with one represented manager-active profile.");
Assert(
    skyrim.Installations.SelectMany(installation => installation.Profiles).All(profile => profile.Observation is null),
    "Deterministic mock profiles remain free of real adapter observations.");

var inactiveObservedProfile = connectedInstallation.Profiles[1];
var observationSelectionShell = new ShellNavigationState(mixedCatalog);
observationSelectionShell.SelectProfile(inactiveObservedProfile.Id);
Assert(
    observationSelectionShell.CurrentSelection.ProfileId == inactiveObservedProfile.Id &&
    inactiveObservedProfile.Observation!.ManagerState == ManagerProfileState.Inactive &&
    connectedInstallation.Profiles[0].Observation!.ManagerState == ManagerProfileState.Active,
    "Grid selection can select an inactive observed profile without rewriting manager-active evidence.");

var originalSources = connectedInstallation.Profiles[0].Observation!.Sources;
var replacedSources = originalSources.SetItem(0, originalSources[0] with { WarningCount = 1 });
Assert(
    originalSources[0].WarningCount == 0 && replacedSources[0].WarningCount == 1,
    "Profile observation source snapshots use immutable collections.");

var connectedProfileWithoutObservation = connectedInstallation.Profiles[0] with { Observation = null };
var connectedWithoutObservation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, connectedProfileWithoutObservation),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "connected-profile-without-observation",
        CatalogSourceKind.Mixed,
        [connectedGame with { Installations = connectedGame.Installations.SetItem(0, connectedWithoutObservation) }, gta]),
    "Connected profiles require observation summaries.");

var mockProfileWithObservation = undefeated.Profiles[0] with
{
    Observation = connectedInstallation.Profiles[0].Observation,
};
var mockInstallationWithObservation = undefeated with
{
    Profiles = undefeated.Profiles.SetItem(0, mockProfileWithObservation),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "mock-profile-with-observation",
        CatalogSourceKind.Mock,
        [skyrim with { Installations = skyrim.Installations.SetItem(0, mockInstallationWithObservation) }, gta]),
    "Mock profiles reject connected observation summaries.");

var invalidObservation = connectedInstallation.Profiles[0].Observation! with
{
    Status = (ProfileObservationStatus)99,
};
var invalidObservedProfile = connectedInstallation.Profiles[0] with { Observation = invalidObservation };
var invalidObservedInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, invalidObservedProfile),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "invalid-profile-observation-state",
        CatalogSourceKind.Mixed,
        [connectedGame with { Installations = connectedGame.Installations.SetItem(0, invalidObservedInstallation) }, gta]),
    "Unknown profile observation states are rejected.");

var invalidSourceObservation = connectedInstallation.Profiles[0].Observation! with
{
    Sources =
    [
        new("settings.ini", ProfileSourceAvailability.Read, ProfileSourceParseStatus.Parsed, 0),
        new("SETTINGS.INI", ProfileSourceAvailability.OptionalAbsent, ProfileSourceParseStatus.NotParsed, 0),
    ],
};
var invalidSourceProfile = connectedInstallation.Profiles[0] with { Observation = invalidSourceObservation };
var invalidSourceInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, invalidSourceProfile),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "duplicate-profile-observation-source",
        CatalogSourceKind.Mixed,
        [connectedGame with { Installations = connectedGame.Installations.SetItem(0, invalidSourceInstallation) }, gta]),
    "Profile observation source names are unique without case ambiguity.");

var invalidTimeObservation = connectedInstallation.Profiles[0].Observation! with
{
    ObservedAtUtc = observedAtUtc.ToOffset(TimeSpan.FromHours(1)),
};
var invalidTimeProfile = connectedInstallation.Profiles[0] with { Observation = invalidTimeObservation };
var invalidTimeInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, invalidTimeProfile),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "non-utc-profile-observation",
        CatalogSourceKind.Mixed,
        [connectedGame with { Installations = connectedGame.Installations.SetItem(0, invalidTimeInstallation) }, gta]),
    "Profile observation timestamps must be non-default UTC values.");

AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with { ManagerState = (ManagerProfileState)99 },
    connectedInstallation,
    connectedGame,
    gta,
    "invalid-manager-profile-state",
    "Unknown manager-profile states are rejected.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with { ObservedAtUtc = default },
    connectedInstallation,
    connectedGame,
    gta,
    "default-profile-observation-time",
    "Default profile observation timestamps are rejected.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with { SnapshotFingerprint = " " },
    connectedInstallation,
    connectedGame,
    gta,
    "empty-profile-observation-fingerprint",
    "Profile observations require a non-empty snapshot fingerprint.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with { WarningCount = -1 },
    connectedInstallation,
    connectedGame,
    gta,
    "negative-profile-observation-warning-count",
    "Profile observation warning counts cannot be negative.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with { Sources = default },
    connectedInstallation,
    connectedGame,
    gta,
    "uninitialized-profile-observation-sources",
    "Profile observation source collections must be initialized.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with
    {
        Sources = [new("settings.ini", (ProfileSourceAvailability)99, ProfileSourceParseStatus.Parsed, 0)],
    },
    connectedInstallation,
    connectedGame,
    gta,
    "invalid-profile-source-availability",
    "Unknown profile source availability states are rejected.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with
    {
        Sources = [new("settings.ini", ProfileSourceAvailability.Read, (ProfileSourceParseStatus)99, 0)],
    },
    connectedInstallation,
    connectedGame,
    gta,
    "invalid-profile-source-parse-state",
    "Unknown profile source parse states are rejected.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with
    {
        Sources = [new("settings.ini", ProfileSourceAvailability.Read, ProfileSourceParseStatus.Parsed, -1)],
    },
    connectedInstallation,
    connectedGame,
    gta,
    "negative-profile-source-warning-count",
    "Profile source warning counts cannot be negative.");

var duplicateActiveProfile = connectedInstallation.Profiles[1] with
{
    Observation = connectedInstallation.Profiles[1].Observation! with { ManagerState = ManagerProfileState.Active },
};
var duplicateActiveInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(1, duplicateActiveProfile),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "multiple-manager-active-profiles",
        CatalogSourceKind.Mixed,
        [connectedGame with { Installations = connectedGame.Installations.SetItem(0, duplicateActiveInstallation) }, gta]),
    "An installation cannot report more than one manager-active profile.");

var mockWithReference = undefeated with
{
    Metadata = undefeated.Metadata with { ReferenceId = referenceId },
};
var mockWithReferenceGame = skyrim with { Installations = skyrim.Installations.SetItem(0, mockWithReference) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("mock-with-reference", CatalogSourceKind.Mixed, [mockWithReferenceGame, gta]),
    "Mock installation provenance rejects persisted reference identities.");

var connectedWithoutReference = undefeated with
{
    Metadata = undefeated.Metadata with
    {
        Provenance = InstallationProvenanceKind.ConnectedReference,
        ReferenceId = null,
    },
};
var connectedWithoutReferenceGame = skyrim with { Installations = skyrim.Installations.SetItem(0, connectedWithoutReference) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("connected-without-reference", CatalogSourceKind.Mixed, [connectedWithoutReferenceGame, gta]),
    "Connected installation provenance requires a persisted reference identity.");

var mutableConnectedInstallation = connectedInstallation with { Kind = InstallationKind.Managed };
var mutableConnectedGame = skyrim with { Installations = skyrim.Installations.SetItem(0, mutableConnectedInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("mutable-connected-reference", CatalogSourceKind.Mixed, [mutableConnectedGame, gta]),
    "Connected installation references must remain external and read-only.");

var duplicateReferenceInstallation = connectedInstallation with
{
    Id = new InstallationId("installation.skyrim.duplicate-reference"),
    Profiles = ImmutableArray<Profile>.Empty,
    ToolConfigurations = ImmutableArray<ToolConfiguration>.Empty,
    LaunchTargetConfigurations = ImmutableArray<LaunchTargetConfiguration>.Empty,
    Capabilities = new WorkspaceCapabilities(WorkspaceFeature.None, EnvironmentTabCapability.None),
    SupportedProfileFeatures = ProfileFeature.None,
};
var duplicateReferenceGame = connectedGame with
{
    Installations = connectedGame.Installations.Add(duplicateReferenceInstallation),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("duplicate-reference", CatalogSourceKind.Mixed, [duplicateReferenceGame, gta]),
    "Persisted installation-reference identities are unique across the catalog.");

var connectedManagementShell = new ShellNavigationState(mixedCatalog);
var connectedManagement = new MockProfileManagementState(connectedManagementShell);
var connectedManagementCatalog = connectedManagement.Catalog;
var connectedCreate = connectedManagement.CreateProfile(connectedInstallation.Id, "Must remain read only");
Assert(
    !connectedCreate.Succeeded &&
    connectedCreate.Failure == ProfileManagementFailure.ProfilesUnsupported &&
    ReferenceEquals(connectedManagementCatalog, connectedManagement.Catalog),
    "Mock profile mutation rejects a selected connected reference without changing the catalog.");

var connectedWorkspace = new MockWorkspaceSessionState(connectedManagementShell);
connectedWorkspace.SelectMods([connectedInstallation.Profiles[0].Mods.First(mod => mod.Kind == ModEntryKind.Mod).Id]);
var connectedEnable = connectedWorkspace.SetSelectedEnabled(false);
Assert(
    !connectedEnable.Succeeded && connectedEnable.Failure == WorkspaceCommandFailure.ContextUnavailable,
    "Mock workspace mutation rejects a selected connected reference.");

var connectedLaunch = new MockLaunchTargetState(connectedManagementShell);
var connectedLaunchEdit = connectedLaunch.SaveConfiguration(
    new LaunchTargetId("launch.skyrim.skse"),
    new LaunchConfigurationDraft(
        ConfiguredPathAnchor.ManagerRoot,
        "skse_loader.exe",
        ConfiguredPathAnchor.ManagerRoot,
        "tools",
        EnvironmentPolicy.AdapterManaged,
        ImmutableArray<CommandArgument>.Empty));
Assert(
    !connectedLaunchEdit.Succeeded && connectedLaunchEdit.Failure == LaunchConfigurationFailure.ContextUnavailable,
    "Mock launch configuration rejects a selected connected reference.");

var pluginWithoutCapabilityProfile = custom.Profiles[0] with { Plugins = undefeated.Profiles[0].Plugins };
var pluginWithoutCapabilityInstallation = custom with { Profiles = custom.Profiles.SetItem(0, pluginWithoutCapabilityProfile) };
var pluginWithoutCapabilityGame = skyrim with { Installations = skyrim.Installations.SetItem(2, pluginWithoutCapabilityInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("plugins-without-capability", CatalogSourceKind.Mock, [pluginWithoutCapabilityGame, gta]),
    "Plugin data without a Plugins capability is rejected.");

var duplicatePriorityProfile = undefeated.Profiles[0] with
{
    Mods = undefeated.Profiles[0].Mods.SetItem(2, undefeated.Profiles[0].Mods[2] with { Priority = 1 }),
};
var duplicatePriorityInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, duplicatePriorityProfile) };
var duplicatePriorityGame = skyrim with { Installations = skyrim.Installations.SetItem(0, duplicatePriorityInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("duplicate-mod-priority", CatalogSourceKind.Mock, [duplicatePriorityGame, gta]),
    "Mod priority is unique within a profile.");

var actionableSeparatorProfile = undefeated.Profiles[0] with
{
    Mods = undefeated.Profiles[0].Mods.SetItem(0, undefeated.Profiles[0].Mods[0] with { IsEnabled = true }),
};
var actionableSeparatorInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, actionableSeparatorProfile) };
var actionableSeparatorGame = skyrim with { Installations = skyrim.Installations.SetItem(0, actionableSeparatorInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("actionable-separator", CatalogSourceKind.Mock, [actionableSeparatorGame, gta]),
    "Separators cannot contain actionable mod-package state.");

var invalidRelatedEntry = undefeated.Profiles[0].EnvironmentEntries[0] with
{
    RelatedModIds = ImmutableArray.Create(new ModId("mod.outside-profile")),
};
var invalidRelatedProfile = undefeated.Profiles[0] with
{
    EnvironmentEntries = undefeated.Profiles[0].EnvironmentEntries.SetItem(0, invalidRelatedEntry),
};
var invalidRelatedInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, invalidRelatedProfile) };
var invalidRelatedGame = skyrim with { Installations = skyrim.Installations.SetItem(0, invalidRelatedInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("cross-profile-environment-reference", CatalogSourceKind.Mock, [invalidRelatedGame, gta]),
    "Environment evidence cannot reference a mod outside its profile.");

var unsupportedEnvironmentEntry = undefeated.Profiles[0].EnvironmentEntries[0] with
{
    Tab = (EnvironmentTabCapability)(1 << 20),
};
var unsupportedEnvironmentProfile = undefeated.Profiles[0] with
{
    EnvironmentEntries = undefeated.Profiles[0].EnvironmentEntries.SetItem(0, unsupportedEnvironmentEntry),
};
var unsupportedEnvironmentInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, unsupportedEnvironmentProfile) };
var unsupportedEnvironmentGame = skyrim with { Installations = skyrim.Installations.SetItem(0, unsupportedEnvironmentInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("unsupported-environment-entry", CatalogSourceKind.Mock, [unsupportedEnvironmentGame, gta]),
    "Environment evidence is rejected when its tab capability is unsupported.");

var missingToolInstallation = undefeated with
{
    ToolConfigurations = undefeated.ToolConfigurations
        .Where(configuration => configuration.ToolId != new ToolId("tool.skyrim.mo2"))
        .ToImmutableArray(),
};
var missingToolGame = skyrim with { Installations = skyrim.Installations.SetItem(0, missingToolInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("missing-tool", CatalogSourceKind.Mock, [missingToolGame, gta]),
    "Tool launch targets must reference a tool in the same installation.");

var invalidKindTarget = skyrim.ToolCatalog.LaunchTargets[0] with { Kind = (LaunchTargetKind)99 };
var invalidKindGame = skyrim with
{
    ToolCatalog = skyrim.ToolCatalog with
    {
        LaunchTargets = skyrim.ToolCatalog.LaunchTargets.SetItem(0, invalidKindTarget),
    },
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("invalid-launch-kind", CatalogSourceKind.Mock, [invalidKindGame, gta]),
    "Unknown launch-target kinds are rejected.");

var duplicateToolDefinitionGame = skyrim with
{
    ToolCatalog = skyrim.ToolCatalog with
    {
        Tools = skyrim.ToolCatalog.Tools.Add(skyrim.ToolCatalog.Tools[0]),
    },
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("duplicate-tool-definition", CatalogSourceKind.Mock, [duplicateToolDefinitionGame, gta]),
    "Game tool-definition identifiers are unique.");

var traversalToolConfiguration = undefeated.ToolConfigurations[0] with
{
    Command = undefeated.ToolConfigurations[0].Command! with
    {
        Executable = new ConfiguredPath(ConfiguredPathAnchor.ManagerRoot, "..\\outside.exe"),
    },
};
var traversalInstallation = undefeated with
{
    ToolConfigurations = undefeated.ToolConfigurations.SetItem(0, traversalToolConfiguration),
};
var traversalGame = skyrim with { Installations = skyrim.Installations.SetItem(0, traversalInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("traversal-tool-path", CatalogSourceKind.Mock, [traversalGame, gta]),
    "Configured command paths reject traversal segments without probing the filesystem.");

var excessiveArguments = Enumerable.Range(0, 65)
    .Select(index => new CommandArgument($"argument-{index}"))
    .ToImmutableArray();
var excessiveArgumentConfiguration = undefeated.LaunchTargetConfigurations[0] with
{
    Arguments = excessiveArguments,
};
var excessiveArgumentInstallation = undefeated with
{
    LaunchTargetConfigurations = undefeated.LaunchTargetConfigurations.SetItem(0, excessiveArgumentConfiguration),
};
var excessiveArgumentGame = skyrim with { Installations = skyrim.Installations.SetItem(0, excessiveArgumentInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("excessive-target-arguments", CatalogSourceKind.Mock, [excessiveArgumentGame, gta]),
    "Structured launch arguments enforce the fixed per-target count limit.");

var duplicateNameProfile = undefeated.Profiles[1] with
{
    Id = new ProfileId("profile.undefeated.duplicate-name"),
    Name = "default",
};
var duplicateNameInstallation = undefeated with { Profiles = undefeated.Profiles.Add(duplicateNameProfile) };
var duplicateNameGame = skyrim with { Installations = skyrim.Installations.SetItem(0, duplicateNameInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("duplicate-profile-name", CatalogSourceKind.Mock, [duplicateNameGame, gta]),
    "Profile names are unique without regard to case within an installation.");

var paddedNameProfile = undefeated.Profiles[0] with { Name = " Default " };
var paddedNameInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, paddedNameProfile) };
var paddedNameGame = skyrim with { Installations = skyrim.Installations.SetItem(0, paddedNameInstallation) };
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("padded-profile-name", CatalogSourceKind.Mock, [paddedNameGame, gta]),
    "Catalog profile names must already be normalized.");

var unsupportedProfileFeature = undefeated.Profiles[0] with { Features = (ProfileFeature)(1 << 20) };
var unsupportedProfileFeatureInstallation = undefeated with
{
    Profiles = undefeated.Profiles.SetItem(0, unsupportedProfileFeature),
};
var unsupportedProfileFeatureGame = skyrim with
{
    Installations = skyrim.Installations.SetItem(0, unsupportedProfileFeatureInstallation),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot("unknown-profile-feature", CatalogSourceKind.Mock, [unsupportedProfileFeatureGame, gta]),
    "Unknown profile feature flags are rejected.");

var selection = new WorkspaceSelectionState(catalog);
AssertSelection(selection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[0].Id, "Initial selection chooses the first complete hierarchy.");

var restoredSelection = new WorkspaceSelectionState(
    catalog,
    new WorkspaceSelection(skyrim.Id, undefeated.Id, undefeated.Profiles[1].Id));
AssertSelection(restoredSelection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[1].Id,
    "Cold-start selection restores an exact available game, installation, and profile identity.");

var staleProfileSelection = new WorkspaceSelectionState(
    catalog,
    new WorkspaceSelection(skyrim.Id, undefeated.Id, new ProfileId("profile.missing")));
AssertSelection(staleProfileSelection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[0].Id,
    "Cold-start selection falls back deterministically when the persisted profile no longer exists.");

var staleInstallationSelection = new WorkspaceSelectionState(
    catalog,
    new WorkspaceSelection(skyrim.Id, new InstallationId("installation.missing"), null));
AssertSelection(staleInstallationSelection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[0].Id,
    "Cold-start selection falls back deterministically when the persisted installation no longer exists.");

selection.SelectProfile(undefeated.Profiles[1].Id);
AssertSelection(selection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[1].Id, "A valid profile transition is preserved.");

selection.SelectProfile(undefeated.Profiles.Single(profile => profile.Lifecycle == ProfileLifecycleState.Archived).Id);
AssertSelection(selection.Current, skyrim.Id, undefeated.Id, null, "An archived profile cannot become the active selection.");

selection.SelectInstallation(custom.Id);
AssertSelection(selection.Current, skyrim.Id, custom.Id, null, "A missing installation remains selected for diagnosis while clearing profile context.");

selection.SelectProfile(custom.Profiles[0].Id);
AssertSelection(selection.Current, skyrim.Id, custom.Id, null, "A profile in a missing installation cannot become active.");

selection.SelectInstallation(nefaramm.Id);
AssertSelection(selection.Current, skyrim.Id, nefaramm.Id, nefaramm.Profiles[0].Id, "Selecting an installation chooses its first profile.");

selection.SelectProfile(undefeated.Profiles[0].Id);
AssertSelection(selection.Current, skyrim.Id, nefaramm.Id, null, "A cross-installation profile clears only the profile selection.");

selection.SelectInstallation(new InstallationId("installation.missing"));
AssertSelection(selection.Current, skyrim.Id, null, null, "An invalid installation preserves the game and clears descendants.");

selection.SelectGame(gta.Id);
AssertSelection(selection.Current, gta.Id, null, null, "Selecting GTA safely produces a game-only selection.");

selection.SelectInstallation(undefeated.Id);
AssertSelection(selection.Current, gta.Id, null, null, "A cross-game installation cannot change the selected game.");

selection.SelectGame(new GameId("game.missing"));
AssertSelection(selection.Current, null, null, null, "An invalid game clears the complete selection.");

selection.SelectGame(skyrim.Id);
selection.SelectInstallation(nefaramm.Id);
var catalogWithoutNefaramm = new GridCatalogSnapshot(
    "mock-catalog-without-nefaram",
    CatalogSourceKind.Mock,
    [skyrim with { Installations = skyrim.Installations.Remove(nefaramm) }, gta]);
selection.ReplaceCatalog(catalogWithoutNefaramm);
AssertSelection(selection.Current, skyrim.Id, undefeated.Id, undefeated.Profiles[0].Id, "Catalog replacement falls back to the first valid descendants.");

selection.SelectInstallation(custom.Id);
var preserved = selection.Current;
selection.ReplaceCatalog(catalog);
Assert(selection.Current == preserved, "Catalog replacement preserves a still-valid hierarchy.");

var emptyCatalog = new GridCatalogSnapshot("empty", CatalogSourceKind.Mock, Array.Empty<ManagedGame>());
var emptySelection = new WorkspaceSelectionState(emptyCatalog);
Assert(emptySelection.Current == WorkspaceSelection.Empty, "An empty catalog produces an empty selection.");

var scaleInstallationId = new InstallationId("installation.skyrim.scale-test");
var scaleProfiles = Enumerable.Range(0, 128)
    .Select(index => custom.Profiles[0] with
    {
        Id = new ProfileId($"profile.scale-test.{index:D3}"),
        InstallationId = scaleInstallationId,
        Name = $"Scale Profile {index:D3}",
        Lifecycle = ProfileLifecycleState.Available,
    })
    .ToImmutableArray();
var scaleInstallation = custom with
{
    Id = scaleInstallationId,
    Name = "Scale Test",
    Profiles = scaleProfiles,
    Metadata = new InstallationMetadata(
        @"X:\MockExternal\ScaleTest",
        InstallationAvailability.Available,
        "Available scale-test installation."),
};
var scaleGame = skyrim with { Installations = ImmutableArray.Create(scaleInstallation) };
var scaleCatalog = new GridCatalogSnapshot("scale-test", CatalogSourceKind.Mock, [scaleGame]);
var scaleSelection = new WorkspaceSelectionState(scaleCatalog);
scaleSelection.SelectProfile(scaleProfiles[^1].Id);
Assert(scaleSelection.Current.ProfileId == scaleProfiles[^1].Id, "Selection supports an unbounded profile collection without a fixed limit.");

var managementShell = new ShellNavigationState(catalog);
var management = new MockProfileManagementState(managementShell);
var preFailureCatalog = management.Catalog;
var preFailureSelection = managementShell.CurrentSelection;
var duplicateNameResult = management.CreateProfile(undefeated.Id, "default");
Assert(!duplicateNameResult.Succeeded && duplicateNameResult.Failure == ProfileManagementFailure.DuplicateName, "Mock create rejects duplicate names without regard to case.");
Assert(ReferenceEquals(management.Catalog, preFailureCatalog) && managementShell.CurrentSelection == preFailureSelection, "A rejected mock operation does not change catalog or selection state.");

var invalidNameResult = management.CreateProfile(undefeated.Id, "   ");
Assert(!invalidNameResult.Succeeded && invalidNameResult.Failure == ProfileManagementFailure.InvalidName, "Mock create rejects an empty normalized name.");

var createResult = management.CreateProfile(undefeated.Id, "  Explorer  ");
Assert(createResult.Succeeded && createResult.AffectedProfileId is not null, "Mock create adds a profile with a stable generated identity.");
var createdProfile = management.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == createResult.AffectedProfileId);
Assert(createdProfile.Name == "Explorer", "Mock create stores the trimmed profile name.");
Assert(createdProfile.Mods.IsEmpty && createdProfile.Plugins.IsEmpty, "A newly created mock profile starts with empty represented contents.");
Assert(createdProfile.Features == undefeated.SupportedProfileFeatures, "A new profile inherits the installation's supported profile features.");
Assert(createdProfile.DefaultLaunchTargetId is not null, "A new profile receives the installation's first deterministic preview target as its default.");
Assert(managementShell.CurrentSelection.ProfileId == createdProfile.Id, "A newly created mock profile becomes active.");
var firstCreatedRevision = management.Catalog.Revision;

var performanceProfile = management.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == undefeated.Profiles[1].Id);
var duplicateResult = management.DuplicateProfile(performanceProfile.Id, "Performance Copy");
Assert(duplicateResult.Succeeded && duplicateResult.AffectedProfileId is not null, "Mock duplicate creates a distinct profile identity.");
var duplicatedProfile = management.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == duplicateResult.AffectedProfileId);
Assert(duplicatedProfile.Id != performanceProfile.Id && duplicatedProfile.Mods.SequenceEqual(performanceProfile.Mods), "Mock duplicate preserves represented contents but not identity.");
Assert(duplicatedProfile.Lifecycle == ProfileLifecycleState.Available && managementShell.CurrentSelection.ProfileId == duplicatedProfile.Id, "A duplicated archived or available source produces an active available profile.");

var renamedId = duplicatedProfile.Id;
var renameResult = management.RenameProfile(renamedId, "  Performance Tuned  ");
var renamedProfile = management.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == renamedId);
Assert(renameResult.Succeeded && renamedProfile.Name == "Performance Tuned" && renamedProfile.Id == renamedId, "Mock rename normalizes the name without changing stable identity.");

var archiveResult = management.ArchiveProfile(renamedId);
var archivedProfile = management.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == renamedId);
Assert(archiveResult.Succeeded && archivedProfile.Lifecycle == ProfileLifecycleState.Archived, "Mock archive retains the profile and marks it archived.");
Assert(managementShell.CurrentSelection.ProfileId == undefeated.Profiles[0].Id, "Archiving the active profile falls back to the first available profile.");

var activeDeleteCatalog = management.Catalog;
var activeDeleteResult = management.DeleteProfile(undefeated.Profiles[0].Id);
Assert(!activeDeleteResult.Succeeded && activeDeleteResult.Failure == ProfileManagementFailure.ActiveProfile, "Deleting the active profile is rejected.");
Assert(ReferenceEquals(management.Catalog, activeDeleteCatalog), "Rejected active-profile deletion leaves the catalog unchanged.");

var archivedDeleteResult = management.DeleteProfile(renamedId);
Assert(archivedDeleteResult.Succeeded && !management.Catalog.Games
    .SelectMany(game => game.Installations)
    .SelectMany(installation => installation.Profiles)
    .Any(profile => profile.Id == renamedId), "A non-active archived mock profile can be deleted after approval.");

var deterministicShell = new ShellNavigationState(catalog);
var deterministicManagement = new MockProfileManagementState(deterministicShell);
var deterministicCreate = deterministicManagement.CreateProfile(undefeated.Id, "Explorer");
Assert(deterministicCreate.AffectedProfileId == createResult.AffectedProfileId && deterministicManagement.Catalog.Revision == firstCreatedRevision, "Equivalent operation sequences generate deterministic IDs and revisions.");

var singleProfileInstallation = undefeated with
{
    Profiles = ImmutableArray.Create(undefeated.Profiles[0]),
};
var singleProfileGame = skyrim with { Installations = ImmutableArray.Create(singleProfileInstallation) };
var singleProfileCatalog = new GridCatalogSnapshot("single-profile", CatalogSourceKind.Mock, [singleProfileGame]);
var singleProfileManagement = new MockProfileManagementState(new ShellNavigationState(singleProfileCatalog));
var finalDeleteResult = singleProfileManagement.DeleteProfile(undefeated.Profiles[0].Id);
Assert(!finalDeleteResult.Succeeded && finalDeleteResult.Failure == ProfileManagementFailure.FinalProfile, "Deleting an installation's final profile is rejected.");

var finalAvailableInstallation = undefeated with
{
    Profiles = ImmutableArray.Create(undefeated.Profiles[0], undefeated.Profiles[2]),
};
var finalAvailableGame = skyrim with { Installations = ImmutableArray.Create(finalAvailableInstallation) };
var finalAvailableCatalog = new GridCatalogSnapshot("final-available-profile", CatalogSourceKind.Mock, [finalAvailableGame]);
var finalAvailableManagement = new MockProfileManagementState(new ShellNavigationState(finalAvailableCatalog));
var finalArchiveResult = finalAvailableManagement.ArchiveProfile(undefeated.Profiles[0].Id);
Assert(!finalArchiveResult.Succeeded && finalArchiveResult.Failure == ProfileManagementFailure.FinalAvailableProfile, "Archiving the final available profile is rejected.");

var unavailableShell = new ShellNavigationState(catalog);
unavailableShell.SelectInstallation(custom.Id);
var unavailableManagement = new MockProfileManagementState(unavailableShell);
var unavailableCatalogBefore = unavailableManagement.Catalog;
var unavailableCreateResult = unavailableManagement.CreateProfile(custom.Id, "Unavailable Profile");
Assert(!unavailableCreateResult.Succeeded && unavailableCreateResult.Failure == ProfileManagementFailure.InstallationUnavailable, "Mock management rejects operations against a missing external installation.");
Assert(ReferenceEquals(unavailableManagement.Catalog, unavailableCatalogBefore), "Unavailable-installation rejection leaves the catalog unchanged.");

var scaleShell = new ShellNavigationState(scaleCatalog);
var scaleManagement = new MockProfileManagementState(scaleShell);
var scaleCreateResult = scaleManagement.CreateProfile(scaleInstallationId, "Scale Profile 128");
Assert(scaleCreateResult.Succeeded && scaleManagement.Catalog.Games[0].Installations[0].Profiles.Length == 129, "Mock management adds profiles beyond a large collection without an engine limit.");

var launchShell = new ShellNavigationState(catalog);
var launchState = new MockLaunchTargetState(launchShell);
var skseTargetId = new LaunchTargetId("launch.skyrim.skse");
var sseEditTargetId = new LaunchTargetId("launch.skyrim.sseedit");
var sseEditViewTargetId = new LaunchTargetId("launch.skyrim.sseedit-view");
var diagnosticsTargetId = new LaunchTargetId("launch.skyrim.diagnostics");
var dynDoLodTargetId = new LaunchTargetId("launch.skyrim.dyndolod");

Assert(launchState.SelectedTargetId == skseTargetId, "Launch resolution selects the active profile's deterministic default target.");
var initialTargets = launchState.GetTargets();
Assert(initialTargets.Any(target => target.Definition.Kind == LaunchTargetKind.Game), "Resolved launch targets distinguish game routes.");
Assert(initialTargets.Any(target => target.CategoryLabel == "External utility"), "Resolved launch targets distinguish external utilities.");
Assert(initialTargets.Any(target => target.CategoryLabel == "Build tool"), "Resolved launch targets distinguish build tools.");
Assert(initialTargets.Any(target => target.Definition.Kind == LaunchTargetKind.GridInternal), "Resolved launch targets distinguish Grid internal routes.");

var initialPreview = launchState.CreatePreview();
Assert(initialPreview is not null && initialPreview.Target.Definition.Id == skseTargetId, "The selected default produces an immutable preview.");
Assert(initialPreview!.NoExecutionDisclosure.Contains("NO PROCESS STARTED", StringComparison.Ordinal), "Launch previews explicitly disclose that no process was started.");
Assert(initialPreview.Target.SafetyGates.Any(gate => gate.Kind == LaunchSafetyGateKind.PendingOutputs && gate.Disposition == LaunchGateDisposition.Warning), "Pending represented outputs produce a warning gate.");
Assert(initialPreview.Target.SafetyGates.Any(gate => gate.Kind == LaunchSafetyGateKind.Snapshot && gate.Disposition == LaunchGateDisposition.Recommendation), "External launch previews recommend a future snapshot.");
Assert(initialPreview.Target.SafetyGates.Any(gate => gate.Kind == LaunchSafetyGateKind.Approval && gate.Disposition == LaunchGateDisposition.RequiredAtExecution), "External launch previews preserve the future approval boundary.");

launchState.SelectTarget(sseEditTargetId);
var metacharacterArgument = new CommandArgument("; calc.exe --still-one-literal");
var saveLaunchConfiguration = launchState.SaveConfiguration(
    sseEditTargetId,
    new LaunchConfigurationDraft(
        ConfiguredPathAnchor.ManagerRoot,
        "tools\\SSEEdit\\SessionEdit.exe",
        ConfiguredPathAnchor.ManagerRoot,
        "tools\\SSEEdit",
        EnvironmentPolicy.CleanAllowlist,
        ImmutableArray.Create(metacharacterArgument)));
Assert(saveLaunchConfiguration.Succeeded, "A valid launch configuration updates the deterministic mock session.");
Assert(launchState.Catalog.Revision.EndsWith("launch-001", StringComparison.Ordinal), "The first launch configuration edit advances a deterministic session revision.");
var editedTargets = launchState.GetTargets();
var editedSseEdit = editedTargets.Single(target => target.Definition.Id == sseEditTargetId);
var editedSseEditView = editedTargets.Single(target => target.Definition.Id == sseEditViewTargetId);
Assert(editedSseEdit.Command!.Executable == editedSseEditView.Command!.Executable, "Targets referencing one utility share its edited executable configuration.");
Assert(editedSseEdit.Command.Arguments.SequenceEqual([metacharacterArgument]), "Shell metacharacters remain one literal structured argument without parsing.");
Assert(!editedSseEditView.Command.Arguments.SequenceEqual(editedSseEdit.Command.Arguments), "Target-specific arguments remain independent from the shared tool base.");

var catalogBeforeInvalidLaunchEdit = launchState.Catalog;
var invalidLaunchEdit = launchState.SaveConfiguration(
    sseEditTargetId,
    new LaunchConfigurationDraft(
        ConfiguredPathAnchor.ManagerRoot,
        "..\\outside.exe",
        ConfiguredPathAnchor.ManagerRoot,
        "tools\\SSEEdit",
        EnvironmentPolicy.CleanAllowlist,
        ImmutableArray<CommandArgument>.Empty));
Assert(!invalidLaunchEdit.Succeeded && invalidLaunchEdit.Failure == LaunchConfigurationFailure.InvalidExecutablePath, "Session configuration rejects traversal-shaped executable paths.");
Assert(ReferenceEquals(launchState.Catalog, catalogBeforeInvalidLaunchEdit), "A rejected launch edit leaves catalog state unchanged.");

launchState.SynchronizeContext();
Assert(launchState.SelectedTargetId == sseEditTargetId, "Explicit target selection is preserved while the profile context remains unchanged.");
var setDefaultResult = launchState.SetProfileDefault(sseEditTargetId);
Assert(setDefaultResult.Succeeded, "A previewable target can become the active profile's default.");
var defaultProfileAfterEdit = launchState.Catalog.Games[0].Installations[0].Profiles[0];
Assert(defaultProfileAfterEdit.DefaultLaunchTargetId == sseEditTargetId, "Profile default launch-target identity is stored in the immutable catalog.");

launchShell.SelectProfile(undefeated.Profiles[1].Id);
launchState.SynchronizeContext();
Assert(launchState.SelectedTargetId == new LaunchTargetId("launch.skyrim.game"), "Switching profiles resolves the newly active profile's own default.");
launchShell.SelectProfile(undefeated.Profiles[0].Id);
launchState.SynchronizeContext();
Assert(launchState.SelectedTargetId == sseEditTargetId, "Returning to a profile restores its updated default.");

launchState.SelectTarget(dynDoLodTargetId);
Assert(launchState.GetSelectedTarget()?.Availability == AvailabilityState.Unavailable && launchState.CreatePreview() is null, "Unavailable tools retain explanations and cannot fabricate an exact preview.");
launchState.SelectTarget(diagnosticsTargetId);
var diagnosticsPreview = launchState.CreatePreview();
Assert(diagnosticsPreview?.Target.Command?.InternalRoute == GridInternalRoute.Diagnostics, "Grid diagnostics resolves as an internal route without an executable.");

var freshLaunchState = new MockLaunchTargetState(new ShellNavigationState(catalog));
var freshSseEdit = freshLaunchState.GetTargets().Single(target => target.Definition.Id == sseEditTargetId);
Assert(freshSseEdit.Command!.Executable!.Value.RelativePath != "tools\\SSEEdit\\SessionEdit.exe", "Launch configuration changes reset with a fresh mock catalog session.");

var blockedProfile = undefeated.Profiles[0] with
{
    LaunchReadiness = undefeated.Profiles[0].LaunchReadiness with { ProfileState = ProfileValidationState.Invalid },
};
var blockedInstallation = undefeated with { Profiles = undefeated.Profiles.SetItem(0, blockedProfile) };
var blockedGame = skyrim with { Installations = skyrim.Installations.SetItem(0, blockedInstallation) };
var blockedCatalog = new GridCatalogSnapshot("blocked-launch-preview", CatalogSourceKind.Mock, [blockedGame, gta]);
var blockedLaunchState = new MockLaunchTargetState(new ShellNavigationState(blockedCatalog));
var blockedPreview = blockedLaunchState.CreatePreview();
Assert(blockedPreview is not null && blockedPreview.Target.CanPreview && blockedPreview.Target.IsBlockedForFutureExecution, "A resolvable command remains previewable while future execution is explicitly blocked.");

var gtaLaunchShell = new ShellNavigationState(catalog);
gtaLaunchShell.NavigateGame(gta.Id);
var gtaLaunchState = new MockLaunchTargetState(gtaLaunchShell);
var gtaLaunchTargets = gtaLaunchState.GetTargets();
Assert(gtaLaunchTargets.Length == gta.ToolCatalog.LaunchTargets.Length && gtaLaunchTargets.All(target => target.Availability == AvailabilityState.Unavailable), "GTA exposes its game-level adapter catalog without fabricating installation bindings.");
var gtaOnlineTarget = gtaLaunchTargets.Single(target => target.Definition.Id == new LaunchTargetId("launch.gta.online-verified"));
Assert(gtaOnlineTarget.SafetyGates.Any(gate => gate.Kind == LaunchSafetyGateKind.OnlineCleanVerification && gate.Disposition == LaunchGateDisposition.Blocking), "GTA Online always requires explicit clean-state verification and never treats profile purging as sufficient.");
Assert(gtaLaunchState.CreatePreview() is null, "A game without an exact installation command cannot claim a launch preview.");

var workspaceShell = new ShellNavigationState(catalog);
var workspace = new MockWorkspaceSessionState(workspaceShell);
var defaultProfile = undefeated.Profiles[0];
var skyUiId = new ModId("mod.undefeated.skyui");
var skseId = new ModId("mod.undefeated.skse");
var separatorId = new ModId("mod.undefeated.separator.core");
var highPolyId = new ModId("mod.undefeated.high-poly");

Assert(workspace.GetVisibleMods().SequenceEqual(defaultProfile.Mods.OrderBy(mod => mod.Priority)), "The default workspace projection preserves ascending represented Mod priority.");

workspace.SetQuery(ModListQuery.Default with { SearchText = "skyui" });
var searchedMods = workspace.GetVisibleMods();
Assert(searchedMods.Any(mod => mod.Id == skyUiId) && searchedMods.Any(mod => mod.Id == separatorId), "Case-insensitive search retains the matching mod and its priority-group separator.");
Assert(searchedMods.Count(mod => mod.Kind == ModEntryKind.Mod) == 1, "Search excludes unrelated represented mods.");

workspace.SetQuery(ModListQuery.Default with { EnabledFilter = ModEnabledFilter.Disabled });
Assert(workspace.GetVisibleMods().Where(mod => mod.Kind == ModEntryKind.Mod).All(mod => !mod.IsEnabled), "The disabled-state filter returns only disabled mods.");

workspace.SetQuery(ModListQuery.Default with { Category = "Visuals" });
Assert(workspace.GetVisibleMods().Where(mod => mod.Kind == ModEntryKind.Mod).All(mod => mod.Category == "Visuals"), "Category filtering is deterministic and case-aware by value.");

workspace.SetQuery(ModListQuery.Default with { ConflictsOnly = true });
Assert(workspace.GetVisibleMods().Where(mod => mod.Kind == ModEntryKind.Mod).All(mod => mod.ConflictState is not (ModConflictState.None or ModConflictState.Unknown)), "The represented-conflict filter omits non-conflicting and unknown rows.");

workspace.SetQuery(ModListQuery.Default with { UpdatesOnly = true });
Assert(workspace.GetVisibleMods().Count(mod => mod.Kind == ModEntryKind.Mod) == 2, "The represented-update filter exposes the two deterministic update fixtures.");

workspace.SetQuery(ModListQuery.Default with { SortColumn = ModSortColumn.Name });
var nameSortedMods = workspace.GetVisibleMods();
Assert(nameSortedMods.All(mod => mod.Kind == ModEntryKind.Mod), "Non-priority sorting omits separators whose grouping would be misleading.");
Assert(nameSortedMods.Select(mod => mod.Name).SequenceEqual(nameSortedMods.Select(mod => mod.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)), "Name sorting is stable and case-insensitive.");

workspace.SelectMods([skyUiId, separatorId, new ModId("mod.not-in-profile")]);
Assert(workspace.SelectedModIds.SetEquals([skyUiId]), "Workspace multi-selection intersects with actionable mods in the active profile.");

workspace.SetQuery(ModListQuery.Default);
workspace.SelectMods([skyUiId]);
var disableResult = workspace.SetSelectedEnabled(false);
var disabledSkyUi = workspace.Catalog.Games
    .Single(game => game.Id == skyrim.Id)
    .Installations.Single(installation => installation.Id == undefeated.Id)
    .Profiles.Single(profile => profile.Id == defaultProfile.Id)
    .Mods.Single(mod => mod.Id == skyUiId);
Assert(disableResult.Succeeded && !disabledSkyUi.IsEnabled, "Mock disable updates only the immutable in-memory catalog.");
Assert(workspace.Catalog.Revision.EndsWith("workspace-001", StringComparison.Ordinal), "The first mock workspace mutation advances a deterministic session revision.");

workspace.SelectMods([skseId]);
var idsBeforeMove = workspace.Catalog.Games[0].Installations[0].Profiles[0].Mods.Select(mod => mod.Id).ToHashSet();
var moveResult = workspace.MoveSelected(1);
var movedMods = workspace.Catalog.Games[0].Installations[0].Profiles[0].Mods.OrderBy(mod => mod.Priority).ToArray();
Assert(moveResult.Succeeded && movedMods.Single(mod => mod.Id == skseId).Priority == 3, "Move Down changes represented Mod priority by one position.");
Assert(movedMods.Select(mod => mod.Priority!.Value).SequenceEqual(Enumerable.Range(0, movedMods.Length)), "Priority changes recompute a contiguous non-negative ordering.");
Assert(idsBeforeMove.SetEquals(movedMods.Select(mod => mod.Id)), "Priority changes preserve every stable mod identity.");

workspace.SetQuery(ModListQuery.Default with { SearchText = "SKSE" });
workspace.SelectMods([skseId]);
var revisionBeforeRejectedMove = workspace.Catalog.Revision;
var filteredMoveResult = workspace.MoveSelected(-1);
Assert(!filteredMoveResult.Succeeded && filteredMoveResult.Failure == WorkspaceCommandFailure.ReorderUnavailable, "Reordering is rejected while the mod projection is filtered.");
Assert(workspace.Catalog.Revision == revisionBeforeRejectedMove, "A rejected filtered reorder does not mutate the catalog.");

workspace.SetQuery(ModListQuery.Default);
workspace.SelectMods([highPolyId]);
var boundaryMove = workspace.MoveSelected(1);
Assert(!boundaryMove.Succeeded && boundaryMove.Failure == WorkspaceCommandFailure.Boundary, "Reordering rejects the bottom represented Mod-priority boundary.");

var canonicalTabs = workspace.GetEnvironmentTabs();
var expectedTabs = new[]
{
    EnvironmentTabCapability.Plugins,
    EnvironmentTabCapability.Archives,
    EnvironmentTabCapability.Data,
    EnvironmentTabCapability.Saves,
    EnvironmentTabCapability.Downloads,
    EnvironmentTabCapability.Conflicts,
    EnvironmentTabCapability.Outputs,
    EnvironmentTabCapability.Activity,
};
Assert(canonicalTabs.Select(tab => tab.Capability).SequenceEqual(expectedTabs), "Capability-driven environment tabs use the canonical product order.");
Assert(defaultProfile.EnvironmentEntries.All(entry => entry.Tab != EnvironmentTabCapability.Downloads), "A supported Downloads tab can intentionally contain no represented entries.");

var gtaTabs = MockWorkspaceSessionState.CreateEnvironmentTabs(gta.Capabilities, ProfileFeature.None);
Assert(gtaTabs.Select(tab => tab.Capability).SequenceEqual([EnvironmentTabCapability.Activity]), "GTA capability projection exposes Activity without Skyrim-only tabs.");
Assert(!gtaTabs.Any(tab => tab.Capability == EnvironmentTabCapability.Plugins), "A non-plugin game does not receive a meaningless Plugins tab.");
Assert(defaultProfile.Mods.Any(mod => mod.Priority == 3) && defaultProfile.Plugins.Any(plugin => plugin.LoadOrder == 1), "Mod priority and Plugin load order remain separate typed fields.");

var localMetadata = new ModMetadataSummary(
    "1.0.0",
    "1.1.0",
    null,
    [7],
    ["Interface"],
    "skyrimspecialedition",
    3863,
    "skyui.7z",
    observedAtUtc.AddDays(-2),
    "Represented local notes",
    null,
    "Nexus",
    observedAtUtc.AddDays(-1),
    "cached",
    [new("version", "1.0.0", "1.0.0"), new("modid", "3863", "3863")]);
ModInventoryObservation Inventory(
    ModInventoryAuthority authority,
    ModReconciliationState reconciliation,
    int displayOrder,
    int? sourceOrder,
    string? marker,
    ModMetadataAvailability metadataAvailability,
    ModUpdateState updateState,
    ImmutableArray<string> warnings,
    ModMetadataSummary? metadata = null) =>
    new(
        authority,
        reconciliation,
        displayOrder,
        sourceOrder,
        marker,
        metadataAvailability,
        observedAtUtc,
        sourceOrder is null ? "mods directory" : "modlist.txt",
        $"inventory-{displayOrder}",
        warnings.Length,
        warnings,
        metadata,
        new(updateState, "LOCAL METADATA · NOT ONLINE VERIFIED", metadata?.ProviderTimestampUtc, NetworkChecked: false));

var inventorySeparatorId = new ModId("mod.connected.separator");
var inventoryMatchedId = new ModId("mod.connected.matched");
var inventoryMissingId = new ModId("mod.connected.missing");
var inventoryUnlistedId = new ModId("mod.connected.unlisted");
ImmutableArray<ModEntry> inventoryRows =
[
    new(
        inventorySeparatorId,
        "Core_separator",
        string.Empty,
        string.Empty,
        false,
        0,
        HealthLevel.Unknown,
        ModEntryKind.Separator,
        string.Empty,
        Inventory: Inventory(
            ModInventoryAuthority.ManagerAuthoritative,
            ModReconciliationState.Separator,
            0,
            0,
            "-",
            ModMetadataAvailability.Missing,
            ModUpdateState.Unknown,
            [])),
    new(
        inventoryMatchedId,
        "SkyUI",
        "1.0.0",
        "MO2 local metadata",
        true,
        1,
        HealthLevel.Unknown,
        ModEntryKind.Mod,
        "Interface",
        "1.1.0",
        ModUpdateState.UpdateAvailable,
        ModConflictState.Unknown,
        Inventory(
            ModInventoryAuthority.ManagerAuthoritative,
            ModReconciliationState.Matched,
            1,
            1,
            "+",
            ModMetadataAvailability.Available,
            ModUpdateState.UpdateAvailable,
            [],
            localMetadata)),
    new(
        inventoryMissingId,
        "Missing Mod",
        string.Empty,
        string.Empty,
        false,
        null,
        HealthLevel.Warning,
        ModEntryKind.Mod,
        string.Empty,
        null,
        ModUpdateState.Unknown,
        ModConflictState.Unknown,
        Inventory(
            ModInventoryAuthority.ManagerAuthoritative,
            ModReconciliationState.Missing,
            2,
            2,
            "-",
            ModMetadataAvailability.Missing,
            ModUpdateState.Unknown,
            ["The profile entry has no matching immediate child directory."])),
    new(
        inventoryUnlistedId,
        "Unexpected Local Folder",
        string.Empty,
        string.Empty,
        false,
        null,
        HealthLevel.Advisory,
        ModEntryKind.UnlistedDirectory,
        string.Empty,
        null,
        ModUpdateState.Unknown,
        ModConflictState.Unknown,
        Inventory(
            ModInventoryAuthority.GridDerived,
            ModReconciliationState.Unlisted,
            3,
            null,
            null,
            ModMetadataAvailability.Missing,
            ModUpdateState.Unknown,
            [])),
];
var connectedInventoryProfile = connectedInstallation.Profiles[0] with
{
    Mods = inventoryRows,
    EnvironmentEntries = [],
    Observation = connectedInstallation.Profiles[0].Observation! with
    {
        Inventory = new(
            ModInventoryObservationStatus.Partial,
            observedAtUtc,
            "connected-inventory-profile",
            1,
            3,
            1),
    },
};
var connectedInventoryInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, connectedInventoryProfile),
};
var connectedInventoryGame = connectedGame with
{
    Installations = connectedGame.Installations.SetItem(0, connectedInventoryInstallation),
};
var connectedInventoryCatalog = new GridCatalogSnapshot(
    "connected-inventory",
    CatalogSourceKind.Mixed,
    [connectedInventoryGame, gta]);
var connectedInventoryShell = new ShellNavigationState(connectedInventoryCatalog);
var connectedInventoryWorkspace = new MockWorkspaceSessionState(connectedInventoryShell);

Assert(
    connectedInventoryWorkspace.GetVisibleMods().Select(mod => mod.Id).SequenceEqual(inventoryRows.Select(mod => mod.Id)),
    "The authoritative inventory view preserves deterministic display order while nullable priorities remain unclaimed.");
Assert(
    connectedInventoryWorkspace.GetVisibleMods().Single(mod => mod.Id == inventoryUnlistedId).Priority is null,
    "A Grid-derived unlisted directory never receives a fabricated manager priority.");
Assert(
    connectedInventoryWorkspace.GetSeparatorDescriptors().Single().Id == inventorySeparatorId,
    "Separator navigation exposes stable IDs and manager-priority evidence.");

connectedInventoryWorkspace.SetQuery(ModListQuery.Default with
{
    Reconciliation = ModReconciliationState.Missing,
});
Assert(
    connectedInventoryWorkspace.GetVisibleMods().Any(mod => mod.Id == inventoryMissingId) &&
    connectedInventoryWorkspace.GetVisibleMods().All(mod => mod.Kind == ModEntryKind.Separator || mod.Inventory?.Reconciliation == ModReconciliationState.Missing),
    "Typed reconciliation filtering retains only matching inventory rows and their section separator.");

connectedInventoryWorkspace.SetQuery(ModListQuery.Default with { WarningsOnly = true });
Assert(
    connectedInventoryWorkspace.GetVisibleMods().Any(mod => mod.Id == inventoryMissingId) &&
    !connectedInventoryWorkspace.GetVisibleMods().Any(mod => mod.Id == inventoryMatchedId),
    "Warning filtering is driven by immutable inventory evidence.");

connectedInventoryWorkspace.SetQuery(ModListQuery.Default with { UpdateState = ModUpdateState.UpdateAvailable });
Assert(
    connectedInventoryWorkspace.GetVisibleMods().Any(mod => mod.Id == inventoryMatchedId) &&
    connectedInventoryWorkspace.GetVisibleMods().All(mod => mod.Kind == ModEntryKind.Separator || mod.UpdateState == ModUpdateState.UpdateAvailable),
    "Typed local-update filtering does not imply a network check.");

connectedInventoryWorkspace.SetQuery(ModListQuery.Default with { SearchText = "3863" });
Assert(
    !connectedInventoryWorkspace.GetVisibleMods().Any(mod => mod.Id == inventoryMatchedId),
    "Inventory search matches displayed mod names without surfacing metadata-only matches.");

connectedInventoryWorkspace.SetQuery(ModListQuery.Default with { SortColumn = ModSortColumn.Reconciliation });
Assert(
    connectedInventoryWorkspace.GetVisibleMods().All(mod => mod.Kind != ModEntryKind.Separator),
    "Alternate reconciliation sorting omits separators whose authoritative grouping would be misleading.");
Assert(
    connectedInventoryWorkspace.SelectSeparator(inventorySeparatorId) && connectedInventoryWorkspace.Query == ModListQuery.Default,
    "Separator navigation restores the authoritative unfiltered order deterministically.");

connectedInventoryWorkspace.SelectMods([inventoryMatchedId, inventoryUnlistedId]);
Assert(
    connectedInventoryWorkspace.SelectedModIds.SetEquals([inventoryMatchedId, inventoryUnlistedId]),
    "Inventory details selection accepts authoritative and Grid-derived non-separator rows.");
Assert(
    connectedInventoryWorkspace.SetSelectedEnabled(false).Failure == WorkspaceCommandFailure.ContextUnavailable,
    "Connected inventory remains protected from mock enablement even when rows are selected.");

var invalidUnlistedProfile = connectedInventoryProfile with
{
    Mods = inventoryRows.SetItem(3, inventoryRows[3] with { Priority = 9 }),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "invalid-unlisted-priority",
        CatalogSourceKind.Mixed,
        [connectedInventoryGame with
        {
            Installations = connectedInventoryGame.Installations.SetItem(
                0,
                connectedInventoryInstallation with
                {
                    Profiles = connectedInventoryInstallation.Profiles.SetItem(0, invalidUnlistedProfile),
                }),
        }, gta]),
    "Grid-derived unlisted rows cannot claim manager priority.");

var networkCheckedProfile = connectedInventoryProfile with
{
    Mods = inventoryRows.SetItem(1, inventoryRows[1] with
    {
        Inventory = inventoryRows[1].Inventory! with
        {
            UpdateEvidence = inventoryRows[1].Inventory!.UpdateEvidence with { NetworkChecked = true },
        },
    }),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "invalid-network-update-claim",
        CatalogSourceKind.Mixed,
        [connectedInventoryGame with
        {
            Installations = connectedInventoryGame.Installations.SetItem(
                0,
                connectedInventoryInstallation with
                {
                    Profiles = connectedInventoryInstallation.Profiles.SetItem(0, networkCheckedProfile),
                }),
        }, gta]),
    "Local MO2 metadata evidence cannot claim that Grid performed a network update check.");

var absoluteProvenanceProfile = connectedInventoryProfile with
{
    Mods = inventoryRows.SetItem(1, inventoryRows[1] with
    {
        Inventory = inventoryRows[1].Inventory! with { SourceName = @"C:\private\meta.ini" },
    }),
};
AssertThrows<ArgumentException>(
    () => _ = new GridCatalogSnapshot(
        "invalid-private-path-provenance",
        CatalogSourceKind.Mixed,
        [connectedInventoryGame with
        {
            Installations = connectedInventoryGame.Installations.SetItem(
                0,
                connectedInventoryInstallation with
                {
                    Profiles = connectedInventoryInstallation.Profiles.SetItem(0, absoluteProvenanceProfile),
                }),
        }, gta]),
    "Manager-neutral inventory provenance rejects private absolute paths.");

var connectedInventoryOperator = new MockOperatorSessionState(
    connectedInventoryWorkspace,
    new MockLaunchTargetState(connectedInventoryShell),
    new MockAiOperatorService());
var connectedOperatorContext = connectedInventoryOperator.SynchronizeContext();
Assert(
    connectedOperatorContext.Subjects.Any(subject => subject.Detail.Contains("authoritative read-only manager observation", StringComparison.Ordinal)) &&
    connectedOperatorContext.Subjects.Any(subject => subject.Detail.Contains("Grid-derived unlisted-directory observation", StringComparison.Ordinal)),
    "Operator context distinguishes manager-authoritative and Grid-derived inventory evidence.");
Assert(
    !connectedOperatorContext.AvailableActions.Any(action => action.Id.Value.StartsWith("action.workspace.", StringComparison.Ordinal)),
    "Operator context never offers mock mutation proposals for a connected inventory.");
Assert(Enum.IsDefined(ModUpdateState.Pinned) && Enum.IsDefined(ModUpdateState.Error), "The local update abstraction represents pinned and error states without inferring either.");

var operatorShell = new ShellNavigationState(catalog);
var operatorWorkspace = new MockWorkspaceSessionState(operatorShell);
var operatorLaunch = new MockLaunchTargetState(operatorShell);
var operatorState = new MockOperatorSessionState(operatorWorkspace, operatorLaunch, new MockAiOperatorService());
AssertThrows<ArgumentException>(
    () => _ = new MockOperatorSessionState(
        operatorWorkspace,
        new MockLaunchTargetState(new ShellNavigationState(catalog)),
        new MockAiOperatorService()),
    "Operator workspace and launch state must share one authoritative shell context.");
Assert(!operatorState.IsExpanded && operatorState.PanelWidth == MockOperatorSessionState.DefaultPanelWidth, "The operator starts collapsed with its deterministic session width.");
operatorState.ExpandPanel();
operatorState.ResizePanel(1000);
Assert(operatorState.IsExpanded && operatorState.PanelWidth == MockOperatorSessionState.MaximumPanelWidth, "Operator expansion and maximum width clamping are framework-neutral session state.");
operatorState.ResizePanel(1);
operatorState.CollapsePanel();
Assert(!operatorState.IsExpanded && operatorState.PanelWidth == MockOperatorSessionState.MinimumPanelWidth, "Operator collapse preserves the clamped expanded width for the session.");

var initialOperatorContext = operatorState.CurrentContext;
Assert(initialOperatorContext.GameName == skyrim.Name && initialOperatorContext.InstallationName == undefeated.Name && initialOperatorContext.ProfileName == defaultProfile.Name, "Operator context receives the selected game, installation, and profile hierarchy.");
Assert(initialOperatorContext.Subjects.IsEmpty && initialOperatorContext.OmittedSubjectCount == 0, "Operator context represents an empty workspace focus without fabrication.");
Assert(initialOperatorContext.Health.Advisories.Length <= MockOperatorSessionState.MaximumContextAdvisories && initialOperatorContext.RecentActivity.Length <= MockOperatorSessionState.MaximumRecentActivity, "Operator health and activity context is explicitly minimized.");
Assert(!initialOperatorContext.Permissions.CanApprove && !initialOperatorContext.Permissions.CanExecute, "Operator permissions make approval and execution unavailable.");
Assert(initialOperatorContext.AvailableActions.Any(action => action.Id == new DeterministicActionId("action.launch.preview-selected-target")), "Exact launch preview is exposed as a deterministic action descriptor.");

operatorLaunch.SelectTarget(diagnosticsTargetId);
var diagnosticsOperatorContext = operatorState.SynchronizeContext();
Assert(diagnosticsOperatorContext.LaunchTargetName == "Grid diagnostics" && diagnosticsOperatorContext.Fingerprint != initialOperatorContext.Fingerprint, "Launch-target selection propagates into operator context and its fingerprint.");
operatorLaunch.SelectTarget(skseTargetId);
operatorState.SynchronizeContext();

operatorWorkspace.SelectMods([skyUiId]);
var selectedModContext = operatorState.SynchronizeContext();
Assert(selectedModContext.Subjects.Any(subject => subject.Kind == OperatorSubjectKind.Mod && subject.StableId == skyUiId.Value), "Selected mod identity propagates into structured operator context.");
Assert(selectedModContext.Fingerprint != initialOperatorContext.Fingerprint, "Relevant selection changes produce a new context fingerprint.");
Assert(operatorState.SynchronizeContext().Fingerprint == selectedModContext.Fingerprint, "Unchanged structured context produces a stable fingerprint.");

operatorWorkspace.SelectEnvironmentTab(EnvironmentTabCapability.Plugins);
var selectedPluginId = defaultProfile.Plugins[0].Id;
operatorWorkspace.SelectPlugin(selectedPluginId);
var selectedPluginContext = operatorState.SynchronizeContext();
Assert(selectedPluginContext.Subjects.Any(subject => subject.Kind == OperatorSubjectKind.Plugin && subject.StableId == selectedPluginId.Value), "Selected plugin identity propagates independently from mod priority.");

var resolvedArchive = new ResolvedArchiveEntry(
    new ArchiveId("archive.resolved.core"),
    "Core Assets.bsa",
    ArchiveFormat.Bsa,
    105,
    ArchiveSupportStatus.Supported,
    ArchiveActivationState.Active,
    ArchiveActivationProvenance.EnabledPluginAssociation,
    selectedPluginId,
    "SkyUI",
    12,
    "archive-fingerprint",
    new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero),
    []);
operatorWorkspace.SelectResolvedArchive(resolvedArchive);
var resolvedArchiveContext = operatorState.SynchronizeContext();
Assert(
    resolvedArchiveContext.Subjects.Any(subject =>
        subject.Kind == OperatorSubjectKind.Archive &&
        subject.StableId == resolvedArchive.Id.Value &&
        subject.Name == resolvedArchive.Name),
    "Resolved archive selection propagates to minimized operator context without a path or filesystem authority.");

operatorWorkspace.SelectEnvironmentTab(EnvironmentTabCapability.Conflicts);
Assert(operatorWorkspace.SelectedPluginId is null, "Changing environment tabs clears an incompatible plugin selection.");
var conflictEntry = defaultProfile.EnvironmentEntries.First(entry => entry.Tab == EnvironmentTabCapability.Conflicts);
operatorWorkspace.SelectEnvironmentEntry(conflictEntry.Id);
var selectedConflictContext = operatorState.SynchronizeContext();
Assert(selectedConflictContext.Subjects.Any(subject => subject.Kind == OperatorSubjectKind.Conflict && subject.StableId == conflictEntry.Id.Value), "Selected conflict evidence propagates through a stable environment-entry identity.");

var manySubjectProfile = defaultProfile with
{
    Mods = Enumerable.Range(0, 12)
        .Select(index => defaultProfile.Mods.First(mod => mod.Kind == ModEntryKind.Mod) with
        {
            Id = new ModId($"mod.operator-context.{index:D2}"),
            Name = $"Operator Context Mod {index:D2}",
            Priority = index,
        })
        .ToImmutableArray(),
    EnvironmentEntries = ImmutableArray<EnvironmentEntry>.Empty,
};
var manySubjectInstallation = undefeated with { Profiles = ImmutableArray.Create(manySubjectProfile) };
var manySubjectGame = skyrim with { Installations = ImmutableArray.Create(manySubjectInstallation) };
var manySubjectCatalog = new GridCatalogSnapshot("operator-subject-limit", CatalogSourceKind.Mock, [manySubjectGame]);
var manySubjectShell = new ShellNavigationState(manySubjectCatalog);
var manySubjectWorkspace = new MockWorkspaceSessionState(manySubjectShell);
manySubjectWorkspace.SelectMods(manySubjectProfile.Mods.Select(mod => mod.Id));
var manySubjectState = new MockOperatorSessionState(
    manySubjectWorkspace,
    new MockLaunchTargetState(manySubjectShell),
    new MockAiOperatorService());
Assert(manySubjectState.CurrentContext.Subjects.Length == MockOperatorSessionState.MaximumContextSubjects && manySubjectState.CurrentContext.OmittedSubjectCount == 2, "Operator context bounds selected subjects and discloses the omitted count.");

var replacementShell = new ShellNavigationState(catalog);
var replacementWorkspace = new MockWorkspaceSessionState(replacementShell);
replacementWorkspace.SelectEnvironmentTab(EnvironmentTabCapability.Plugins);
replacementWorkspace.SelectPlugin(selectedPluginId);
var profileWithoutSelectedPlugin = defaultProfile with { Plugins = defaultProfile.Plugins.RemoveAt(0) };
var installationWithoutSelectedPlugin = undefeated with
{
    Profiles = undefeated.Profiles.SetItem(0, profileWithoutSelectedPlugin),
};
var gameWithoutSelectedPlugin = skyrim with
{
    Installations = skyrim.Installations.SetItem(0, installationWithoutSelectedPlugin),
};
replacementShell.ReplaceCatalog(new GridCatalogSnapshot(
    "operator-selection-replacement",
    CatalogSourceKind.Mock,
    [gameWithoutSelectedPlugin, gta]));
replacementWorkspace.SynchronizeContext();
Assert(replacementWorkspace.SelectedPluginId is null, "Catalog replacement safely clears a selected plugin that is no longer present.");

var transcriptBeforeMessage = operatorState.GetCurrentTranscript();
var explainResult = await operatorState.SubmitAsync("Explain this supplied mock context.");
var transcriptAfterMessage = operatorState.GetCurrentTranscript();
Assert(explainResult.Succeeded && transcriptAfterMessage.Messages.Length == transcriptBeforeMessage.Messages.Length + 2, "A mock operator submission appends one inert user message and one deterministic response.");
var explainResponse = transcriptAfterMessage.Messages[^1];
Assert(explainResponse.Statements.Select(statement => statement.Kind).ToHashSet().SetEquals([OperatorContentKind.Evidence, OperatorContentKind.Inference, OperatorContentKind.Warning]), "Mock responses visibly distinguish evidence, inference, and warning content.");

var proposalResult = await operatorState.SubmitAsync("Draft a typed action proposal.");
var proposal = operatorState.GetCurrentTranscript().Messages[^1].Proposal;
Assert(proposalResult.Succeeded && proposal is not null, "An explicit draft request can present a typed deterministic-action proposal.");
Assert(proposal!.ApprovalStatus.Contains("APPROVAL REQUIRED", StringComparison.Ordinal) && proposal.ExecutionStatus.Contains("EXECUTION UNAVAILABLE", StringComparison.Ordinal), "Proposal presentation preserves explicit approval and no-execution boundaries.");
Assert(proposal.ExpectedEffects.Any() && proposal.Exclusions.Any() && !string.IsNullOrWhiteSpace(proposal.Verification), "Proposal effects, exclusions, and verification come from its deterministic descriptor.");

var metacharacterText = "; rm -rf mock && calc.exe | still inert";
var metacharacterResult = await operatorState.SubmitAsync(metacharacterText);
var metacharacterMessage = operatorState.GetCurrentTranscript().Messages[^2];
Assert(metacharacterResult.Succeeded && metacharacterMessage.Body == metacharacterText, "Shell metacharacters remain inert transcript text without parsing or argument construction.");

operatorWorkspace.SelectMods([skseId]);
operatorState.SynchronizeContext();
Assert(operatorState.IsProposalStale(proposal), "A proposal becomes visibly stale when its bound selection fingerprint changes.");

var defaultContextTranscriptLength = operatorState.GetCurrentTranscript().Messages.Length;
operatorShell.SelectProfile(undefeated.Profiles[1].Id);
operatorState.SynchronizeContext();
Assert(operatorState.GetCurrentTranscript().Messages.Length == 1, "A newly selected profile receives a separate mock transcript.");
await operatorState.SubmitAsync("Review this profile context.");
operatorShell.SelectProfile(defaultProfile.Id);
operatorState.SynchronizeContext();
Assert(operatorState.GetCurrentTranscript().Messages.Length == defaultContextTranscriptLength, "Returning to a profile restores only that context's session history.");

var exportedBefore = operatorState.ExportSnapshot();
await operatorState.SubmitAsync("Explain the represented health evidence.");
Assert(exportedBefore.Transcripts.Single(transcript => transcript.ContextKey == operatorState.CurrentContext.Key).Messages.Length == defaultContextTranscriptLength, "Exported transcript snapshots remain immutable after later session activity.");
Assert(operatorState.GetAuditRecords().Select(record => record.Id).Distinct().Count() == operatorState.GetAuditRecords().Length, "Append-only operator audit records retain unique stable identities.");

using (var operatorCancellation = new CancellationTokenSource())
{
    operatorCancellation.Cancel();
    var beforeCancelledSubmission = operatorState.GetCurrentTranscript().Messages.Length;
    await AssertThrowsAsync<OperationCanceledException>(
        () => operatorState.SubmitAsync("Cancelled mock response.", operatorCancellation.Token),
        "Mock operator responses honor cancellation.");
    Assert(operatorState.GetCurrentTranscript().Messages.Length == beforeCancelledSubmission, "A cancelled operator response does not partially append transcript messages.");
}

var rejectingOperatorShell = new ShellNavigationState(catalog);
var rejectingOperatorState = new MockOperatorSessionState(
    new MockWorkspaceSessionState(rejectingOperatorShell),
    new MockLaunchTargetState(rejectingOperatorShell),
    new UnknownActionOperatorService());
await rejectingOperatorState.SubmitAsync("Draft an action proposal.");
var rejectedServiceMessage = rejectingOperatorState.GetCurrentTranscript().Messages[^1];
Assert(rejectedServiceMessage.Proposal is null && rejectedServiceMessage.Statements.Any(statement => statement.Title == "Rejected service output"), "Unknown service action IDs are rejected without creating a proposal.");
Assert(rejectingOperatorState.GetAuditRecords().Any(record => record.Kind == OperatorAuditEventKind.ServiceResponseRejected), "Rejected service output is retained in the append-only audit model.");

var gtaOperatorShell = new ShellNavigationState(catalog);
gtaOperatorShell.NavigateGame(gta.Id);
var gtaOperatorWorkspace = new MockWorkspaceSessionState(gtaOperatorShell);
var gtaOperatorState = new MockOperatorSessionState(
    gtaOperatorWorkspace,
    new MockLaunchTargetState(gtaOperatorShell),
    new MockAiOperatorService());
Assert(gtaOperatorState.CurrentContext.GameName == gta.Name && gtaOperatorState.CurrentContext.InstallationName is null && gtaOperatorState.CurrentContext.ProfileName is null, "GTA operator context remains game-only without a fabricated installation or profile.");
Assert(gtaOperatorState.CurrentContext.Subjects.IsEmpty && !gtaOperatorState.CurrentContext.Permissions.CanExecute, "Unavailable GTA context exposes no fabricated selected evidence or execution authority.");

workspaceShell.NavigateGame(gta.Id);
workspace.SynchronizeContext();
Assert(workspace.SelectedModIds.IsEmpty && workspace.SelectedPluginId is null && workspace.SelectedEnvironmentEntryId is null && workspace.GetEnvironmentTabs().IsEmpty, "Changing to a game-only context clears workspace evidence selection and exposes no fabricated profile tabs.");

var shell = new ShellNavigationState(catalog);
Assert(shell.CurrentRoute == ShellRoute.Home, "The shell starts on the global Home route.");
AssertSelection(shell.CurrentSelection, skyrim.Id, undefeated.Id, undefeated.Profiles[0].Id, "Home retains the deterministic initial workspace context.");

shell.NavigateGame(skyrim.Id);
Assert(shell.CurrentRoute == ShellRoute.GameWorkspace, "Selecting a managed game opens the game workspace route.");
shell.SelectInstallation(nefaramm.Id);
shell.SelectProfile(nefaramm.Profiles[0].Id);
var shellSkyrimContext = shell.CurrentSelection;
shell.NavigateHome();
shell.NavigateHistory();
shell.NavigateHistory();
shell.NavigateSettings();
Assert(shell.CurrentRoute == ShellRoute.Settings, "Global shell routes transition deterministically.");
Assert(shell.CurrentSelection == shellSkyrimContext, "Global shell routes preserve workspace selection.");

shell.NavigateGame(skyrim.Id);
Assert(shell.CurrentSelection == shellSkyrimContext, "Reopening the current game preserves installation and profile context.");

shell.NavigateGame(gta.Id);
Assert(shell.CurrentRoute == ShellRoute.GameWorkspace, "A game without installations still has a workspace route.");
AssertSelection(shell.CurrentSelection, gta.Id, null, null, "GTA navigation exposes an explicit game-only context.");

shell.NavigateGame(new GameId("game.invalid-shell-route"));
Assert(shell.CurrentRoute == ShellRoute.Home, "Invalid game navigation returns to Home.");
Assert(shell.CurrentSelection == WorkspaceSelection.Empty, "Invalid game navigation clears stale workspace context.");

shell.NavigateGame(skyrim.Id);
shell.ReplaceCatalog(emptyCatalog);
Assert(shell.CurrentRoute == ShellRoute.Home, "An emptied catalog returns an active workspace route to Home.");
Assert(shell.CurrentSelection == WorkspaceSelection.Empty, "An emptied catalog clears shell workspace context.");

var environmentContext = new WorkspaceEnvironmentContext(
    skyrim.Id,
    undefeated.Id,
    defaultProfile.Id,
    catalog.Revision,
    defaultProfile.Observation?.Inventory?.Fingerprint);
var environmentSummary = new ResolvedEnvironmentSummary(
    ResolvedEnvironmentStatus.Complete,
    new DateTimeOffset(2026, 8, 25, 14, 0, 0, TimeSpan.Zero),
    "resolved-environment-fingerprint",
    2,
    1,
    1,
    2,
    0);
var resolvedSnapshot = new ResolvedEnvironmentSnapshot(
    new ResolvedSnapshotId("resolved-snapshot-0001"),
    environmentContext,
    environmentSummary,
    [],
    false);
var environmentService = new TestWorkspaceEnvironmentQueryService(
    new ResolvedEnvironmentRefreshResult(
        ResolvedEnvironmentRefreshStatus.Completed,
        resolvedSnapshot,
        "Complete read-only observation.",
        []));
using var environmentState = new WorkspaceEnvironmentState(environmentService);
var noContextResult = await environmentState.RefreshAsync(false);
Assert(noContextResult.Status == ResolvedEnvironmentRefreshStatus.Unavailable, "Resolved environment refresh degrades safely without a selected profile context.");
environmentState.SetContext(environmentContext);
var environmentResult = await environmentState.RefreshAsync(false);
Assert(
    environmentResult.Status == ResolvedEnvironmentRefreshStatus.Completed &&
    environmentState.Snapshot?.Id == resolvedSnapshot.Id &&
    !environmentState.IsCurrentSnapshotStale,
    "Resolved environment state publishes a matching immutable snapshot.");
var pluginPage = await environmentState.QueryPluginsAsync(PluginQuery.Default);
Assert(
    pluginPage.SnapshotId == resolvedSnapshot.Id &&
    pluginPage.Items[0].LoadOrder == 0 &&
    pluginPage.Items[1].LoadOrder is null,
    "Paged plugin evidence preserves enabled-only load order and nullable unordered state.");
var dataPage = await environmentState.QueryDataAsync(VirtualDataQuery.Default);
var providerChain = await environmentState.GetProviderChainAsync(dataPage.Items[0].Id);
Assert(
    providerChain?.WinnerConfidence == ProviderWinnerConfidence.Established &&
    providerChain.Providers.Count(provider => provider.IsWinner) == 1,
    "Virtual Data paging resolves a snapshot-bound explainable provider chain.");

environmentState.SelectPlugin(pluginPage.Items[0].Id);
environmentState.SelectArchive(resolvedArchive.Id);
Assert(
    environmentState.SelectedPluginId is null &&
    environmentState.SelectedArchiveId == resolvedArchive.Id &&
    environmentState.SelectedVirtualPathId is null,
    "Resolved environment selections remain mutually exclusive across tabs.");
environmentService.NextResult = new ResolvedEnvironmentRefreshResult(
    ResolvedEnvironmentRefreshStatus.Failed,
    null,
    "Represented failure.",
    []);
await environmentState.RefreshAsync(true);
Assert(
    environmentState.Snapshot?.Id == resolvedSnapshot.Id && environmentState.IsCurrentSnapshotStale,
    "A failed reconstruction retains the prior immutable snapshot and marks it visibly stale.");

var lateEnvironmentService = new TestWorkspaceEnvironmentQueryService(null) { HoldRefresh = true };
using var lateEnvironmentState = new WorkspaceEnvironmentState(lateEnvironmentService);
lateEnvironmentState.SetContext(environmentContext);
var lateRefresh = lateEnvironmentState.RefreshAsync(false);
await lateEnvironmentService.RefreshStarted.Task;
lateEnvironmentState.SetContext(environmentContext with { ProfileId = nefaramm.Profiles[0].Id });
lateEnvironmentService.CompleteHeldRefresh(new ResolvedEnvironmentRefreshResult(
    ResolvedEnvironmentRefreshStatus.Completed,
    resolvedSnapshot,
    "Late old-context observation.",
    []));
await lateRefresh;
Assert(
    lateEnvironmentState.Snapshot is null &&
    lateEnvironmentState.Context?.ProfileId == nefaramm.Profiles[0].Id,
    "Late refresh completion cannot retarget a replacement profile context.");

var toolOutputContext = new WorkspaceToolOutputContext(
    connectedGame.Id,
    connectedInstallation.Id,
    connectedInstallation.Profiles[0].Id,
    referenceId,
    mixedCatalog.Revision,
    connectedInstallation.Profiles[0].Observation!.SnapshotFingerprint,
    connectedInstallation.Profiles[0].Observation!.Inventory?.Fingerprint);
var observedExecutable = new ObservedExecutableSummary(
    new ObservedExecutableId("observed-executable.fixture-skse"),
    "SKSE fixture",
    ObservedExecutableAvailability.Available,
    ObservedLocationTrust.ExpectedRoot,
    ObservedToolFamily.Skse,
    EvidenceConfidence.Corroborated,
    ObservedIconAvailability.Available,
    0,
    0,
    "observed-executable-fingerprint",
    ExternalObservationChange.FirstObservation);
var observedOutput = new GeneratedOutputSummary(
    new GeneratedOutputId("generated-output.fixture-overwrite"),
    "Overwrite",
    GeneratedOutputKind.Overwrite,
    GeneratedOutputLocationKind.Overwrite,
    GeneratedOutputAvailability.Available,
    GeneratedOutputEnabledState.NotApplicable,
    EvidenceConfidence.Confirmed,
    OutputFingerprintStrength.Structural,
    3,
    2,
    1024,
    0,
    "generated-output-fingerprint",
    ExternalObservationChange.FirstObservation,
    observedExecutable.Id,
    null);
var toolOutputSummary = new ToolOutputObservationSummary(
    ExternalObservationStatus.Complete,
    new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero),
    "tool-output-snapshot-fingerprint",
    1,
    1,
    0,
    false);
var toolOutputSnapshot = new ToolOutputObservationSnapshot(
    new ExternalObservationSnapshotId("external-observation-snapshot-0001"),
    toolOutputContext,
    toolOutputSummary,
    [observedExecutable],
    [observedOutput],
    [],
    false);
var toolOutputService = new TestWorkspaceToolOutputQueryService(
    new ToolOutputObservationRefreshResult(
        ExternalObservationRefreshStatus.Completed,
        toolOutputSnapshot,
        "Complete read-only tool and output observation.",
        []));
using var toolOutputState = new WorkspaceToolOutputState(toolOutputService);
var unavailableToolOutput = await toolOutputState.RefreshAsync(false);
Assert(
    unavailableToolOutput.Status == ExternalObservationRefreshStatus.Unavailable,
    "Tool/output refresh degrades safely without a selected connected profile context.");
toolOutputState.SetContext(toolOutputContext);
var toolOutputResult = await toolOutputState.RefreshAsync(false);
Assert(
    toolOutputResult.Status == ExternalObservationRefreshStatus.Completed &&
    toolOutputState.Snapshot?.Id == toolOutputSnapshot.Id &&
    toolOutputState.Progress?.Stage == ExternalObservationStage.Publishing &&
    !toolOutputState.IsCurrentSnapshotStale,
    "Tool/output state publishes only the matching immutable observation and progress.");
Assert(
    toolOutputState.SelectExecutable(observedExecutable.Id) &&
    toolOutputState.GetSelectedExecutable()?.Title == observedExecutable.Title &&
    toolOutputState.SelectedOutputId is null,
    "Observed executable selection uses a stable identity without exposing command material.");
Assert(
    toolOutputState.SelectOutput(observedOutput.Id) &&
    toolOutputState.GetSelectedOutput()?.Fingerprint == observedOutput.Fingerprint &&
    toolOutputState.SelectedExecutableId is null,
    "Generated-output selection is stable and mutually exclusive with executable selection.");
Assert(
    !toolOutputState.SelectOutput(new GeneratedOutputId("generated-output.unknown")) &&
    toolOutputState.SelectedOutputId == observedOutput.Id,
    "Unknown generated-output identities cannot silently replace the selected observation.");
toolOutputService.NextResult = new ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus.Failed,
    null,
    "Represented observation failure.",
    []);
await toolOutputState.RefreshAsync(true);
Assert(
    toolOutputState.Snapshot?.Id == toolOutputSnapshot.Id && toolOutputState.IsCurrentSnapshotStale,
    "A failed tool/output refresh retains the prior immutable observation as visibly stale.");

var lateToolOutputService = new TestWorkspaceToolOutputQueryService(null) { HoldRefresh = true };
using var lateToolOutputState = new WorkspaceToolOutputState(lateToolOutputService);
lateToolOutputState.SetContext(toolOutputContext);
var lateToolOutputRefresh = lateToolOutputState.RefreshAsync(false);
await lateToolOutputService.RefreshStarted.Task;
lateToolOutputState.SetContext(toolOutputContext with { ProfileId = connectedInstallation.Profiles[1].Id });
lateToolOutputService.CompleteHeldRefresh(new ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus.Completed,
    toolOutputSnapshot,
    "Late old-context observation.",
    []));
await lateToolOutputRefresh;
Assert(
    lateToolOutputState.Snapshot is null &&
    lateToolOutputState.Context?.ProfileId == connectedInstallation.Profiles[1].Id,
    "Late tool/output refresh completion cannot retarget a replacement profile context.");

AssertThrows<ArgumentException>(() => _ = new ObservedExecutableId(" "), "Blank observed-executable identities are rejected.");
AssertThrows<ArgumentException>(() => _ = new GeneratedOutputId(" "), "Blank generated-output identities are rejected.");
AssertThrows<ArgumentException>(() => _ = new ExternalObservationSnapshotId(" "), "Blank external-observation snapshot identities are rejected.");

var observedConnectedProfile = connectedInstallation.Profiles[0] with
{
    Observation = connectedInstallation.Profiles[0].Observation! with { ToolOutputs = toolOutputSummary },
};
var observedConnectedInstallation = connectedInstallation with
{
    Profiles = connectedInstallation.Profiles.SetItem(0, observedConnectedProfile),
    Metadata = connectedInstallation.Metadata with { ToolOutputObservation = toolOutputSummary },
};
_ = new GridCatalogSnapshot(
    "connected-tool-output-summary",
    CatalogSourceKind.Mixed,
    [connectedGame with { Installations = connectedGame.Installations.SetItem(0, observedConnectedInstallation) }, gta]);
Assert(true, "Connected installation and profile records accept valid manager-neutral tool/output summaries.");
AssertInvalidConnectedObservation(
    connectedInstallation.Profiles[0].Observation! with
    {
        ToolOutputs = toolOutputSummary with { ExecutableCount = -1 },
    },
    connectedInstallation,
    connectedGame,
    gta,
    "invalid-tool-output-count",
    "Connected profile tool/output summaries reject negative counts.");

var restoredApplicationSession = new GridApplicationSession(
    catalog,
    new TestUserHistoryStore(),
    initialSelection: new WorkspaceSelection(skyrim.Id, undefeated.Id, undefeated.Profiles[1].Id));
AssertSelection(restoredApplicationSession.Shell.CurrentSelection, skyrim.Id, undefeated.Id, undefeated.Profiles[1].Id,
    "A newly constructed application session honors a validated persisted workspace selection.");
restoredApplicationSession.Dispose();

var sessionHistoryStore = new TestUserHistoryStore();
var applicationSession = new GridApplicationSession(
    catalog,
    sessionHistoryStore,
    assistantClasses:
    [
        new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true),
        new("grid.class.crash-freeze", "Crash & Freeze", "grid.icon.crash", false),
    ]);
Assert(
    ReferenceEquals(applicationSession.Shell, applicationSession.Workspace.Shell) &&
    ReferenceEquals(applicationSession.Shell, applicationSession.LaunchTargets.Shell),
    "The application session guarantees one shared shell for neutral workspace and launch selection state.");
Assert(applicationSession.Shell.CurrentRoute == ShellRoute.Home, "The integrated application journey starts on Home.");
var homeContext = applicationSession.SynchronizeContext();
Assert(
    homeContext.Surface == ApplicationSurface.Home &&
    homeContext.GameId is null &&
    homeContext.ProfileId is null,
    "Home assistant context is explicitly global and never leaks a cached game or profile.");

applicationSession.Shell.NavigateGame(skyrim.Id);
applicationSession.Shell.SelectInstallation(undefeated.Id);
applicationSession.Shell.SelectProfile(defaultProfile.Id);
applicationSession.Workspace.SelectMods([skyUiId]);
applicationSession.Workspace.SelectEnvironmentTab(EnvironmentTabCapability.Conflicts);
applicationSession.Workspace.SelectEnvironmentEntry(conflictEntry.Id);
applicationSession.LaunchTargets.SelectTarget(skseTargetId);
var gameContext = applicationSession.SynchronizeContext();
Assert(
    gameContext.Surface == ApplicationSurface.GameWorkspace &&
    gameContext.GameId == skyrim.Id &&
    gameContext.InstallationId == undefeated.Id &&
    gameContext.ProfileId == defaultProfile.Id &&
    gameContext.ModIds.Contains(skyUiId) &&
    gameContext.EnvironmentEntryId == conflictEntry.Id &&
    gameContext.LaunchTargetId == skseTargetId,
    "Game context propagates stable hierarchy, mod, environment, and launch-target identities.");

var integratedSelection = applicationSession.Shell.CurrentSelection;
applicationSession.Shell.NavigateHistory();
Assert(applicationSession.Shell.CurrentSelection == integratedSelection, "History preserves cached workspace selection.");
var historyContext = applicationSession.SynchronizeContext();
Assert(historyContext.Surface == ApplicationSurface.History && historyContext.GameId is null,
    "History does not expose the cached game as active assistant context.");
applicationSession.Shell.NavigateSettings();
Assert(applicationSession.Shell.CurrentSelection == integratedSelection, "Settings preserves cached workspace selection.");
applicationSession.Shell.NavigateGame(skyrim.Id);
applicationSession.SynchronizeContext();
Assert(applicationSession.Shell.CurrentSelection == integratedSelection,
    "Returning to the same game restores its deterministic installation/profile selection.");

applicationSession.Assistant.Expand();
applicationSession.Assistant.SelectMode(AssistantOperatingMode.Assist);
var assistantSnapshot = applicationSession.Assistant.Snapshot();
Assert(
    assistantSnapshot.IsExpanded &&
    assistantSnapshot.Mode == AssistantOperatingMode.Assist &&
    assistantSnapshot.ProviderAvailability == AssistantProviderAvailability.NotConfigured &&
    assistantSnapshot.LifecycleStage == AssistantLifecycleStage.Idle,
    "Assistant modes are selectable without fabricating a provider or lifecycle progress.");
applicationSession.Assistant.SelectMode(AssistantOperatingMode.Auto);
Assert(applicationSession.Assistant.Snapshot().LifecycleStage == AssistantLifecycleStage.Idle,
    "Selecting Auto cannot advance the authoritative lifecycle.");

applicationSession.Assistant.ToggleForm();
var intakeSnapshot = applicationSession.Assistant.Snapshot();
Assert(
    intakeSnapshot.Surface == AssistantSurface.Home && intakeSnapshot.IsFormVisible &&
    intakeSnapshot.Draft.Scope == AssistantIntakeScope.Game && intakeSnapshot.Classes.Length == 2,
    "The assistant opens one GAME intake draft on Chat Home using injected Class registry data.");
applicationSession.Assistant.SelectClass("grid.class.installation-integrity");
applicationSession.Assistant.SetPlainText("Verbatim user evidence.");
foreach (var mod in intakeSnapshot.Mods.Take(2)) applicationSession.Assistant.SetModSelected(mod.Id, true);
var populatedDraft = applicationSession.Assistant.Snapshot().Draft;
var expectedStructuredModCount = intakeSnapshot.Draft.ModIds
    .Concat(intakeSnapshot.Mods.Take(2).Select(mod => mod.Id))
    .Distinct()
    .Count();
Assert(
    populatedDraft.ModIds.Length == expectedStructuredModCount &&
    populatedDraft.PlainText == "Verbatim user evidence." &&
    populatedDraft.DisplayTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 1 and <= 5 &&
    populatedDraft.DisplayTitle.Contains("Installation", StringComparison.Ordinal) && !populatedDraft.CanSubmit,
    "Structured multi-mod selections remain separate from verbatim composer text, produce a bounded deterministic title, and cannot fabricate a submission route.");
applicationSession.Assistant.SelectIntakeScope(AssistantIntakeScope.Grid);
Assert(
    applicationSession.Assistant.Snapshot().Draft.Readiness == AssistantDraftReadiness.RuntimeUnavailable,
    "GRID intake remains a visible unavailable boundary without an invented registry.");
applicationSession.Assistant.ShowHistory();
applicationSession.Assistant.ToggleForm();
Assert(
    applicationSession.Assistant.Snapshot().Surface == AssistantSurface.Home && applicationSession.Assistant.IsFormVisible,
    "Opening the intake form from History returns to Chat Home and preserves the draft.");
applicationSession.Assistant.ToggleFullScreen();
Assert(applicationSession.Assistant.IsFullScreen && applicationSession.Assistant.IsExpanded,
    "Chat full screen is authoritative assistant state and guarantees an expanded panel.");
applicationSession.Assistant.Collapse();
Assert(!applicationSession.Assistant.IsFullScreen && !applicationSession.Assistant.IsExpanded,
    "Closing Chat clears full-screen state so reopening returns to the docked panel.");

var suggestionAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [
        new("grid.class.installation-integrity", "CleanHouse Repair", "grid.icon.installation", true),
        new("grid.class.crash-freeze", "Crash & Freeze", "grid.icon.crash", true),
        new("grid.class.asset-mismatch", "Asset Mismatch", "grid.icon.asset", true),
    ],
    executionService: new TestAssistantRequestExecutionService());
suggestionAssistant.ApplySuggestion("installation");
var cleanHousePreset = suggestionAssistant.Snapshot().Draft;
Assert(
    cleanHousePreset.ClassId == "grid.class.installation-integrity" &&
    !string.IsNullOrWhiteSpace(cleanHousePreset.Problem) &&
    !string.IsNullOrWhiteSpace(cleanHousePreset.ExpectedBehavior) &&
    !string.IsNullOrWhiteSpace(cleanHousePreset.ReproductionOrLocation) &&
    !string.IsNullOrWhiteSpace(cleanHousePreset.DesiredOutcome),
    "The whole-profile CleanHouse suggestion populates every investigation field instead of only selecting a Class.");
suggestionAssistant.SetProblem("Preserve this user-authored problem.");
suggestionAssistant.ApplySuggestion("installation");
Assert(
    suggestionAssistant.Snapshot().Draft.Problem == "Preserve this user-authored problem.",
    "Applying a suggestion does not overwrite an existing user-authored field.");

var capabilityAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.outfits-bodies-physics", "Outfits, Bodies & Physics", "grid.icon.physics", true, "1.1.0",
        GameplayCapabilities: [new("grid.capability.equipment.multiple-rings", "Wear rings on multiple fingers",
            ["rings on more than one finger", "rings for more than one finger", "rings on multiple fingers", "wear more than one ring", "wear multiple rings", "multiple rings", "more than one ring"])])],
    executionService: new TestAssistantRequestExecutionService());
capabilityAssistant.ToggleForm();
capabilityAssistant.SelectClass("grid.class.outfits-bodies-physics");
capabilityAssistant.SetPlainText("Can this profile use rings for more than one finger?");
Assert(
    capabilityAssistant.Snapshot().Draft.CanSubmit &&
    capabilityAssistant.Snapshot().Draft.ClassId == "grid.class.outfits-bodies-physics" &&
    capabilityAssistant.Snapshot().Draft.CapabilityId == "grid.capability.equipment.multiple-rings",
    "An unambiguous catalog-owned intent phrase binds the exact registered gameplay capability without inventing mod selections.");
capabilityAssistant.SetPlainText("Diagnose a body mesh problem instead.");
Assert(
    capabilityAssistant.Snapshot().Draft.ClassId == "grid.class.outfits-bodies-physics" &&
    capabilityAssistant.Snapshot().Draft.CapabilityId is null,
    "Editing away an inferred phrase clears only the inferred capability while preserving a manually selected Class.");

var assistantExecution = new TestAssistantRequestExecutionService();
var executableAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true, "1.0.0", 1)],
    [new(new ToolId("grid.tool.mo2"), "Mod Organizer 2", AvailabilityState.Available, null)],
    assistantExecution,
    new FixedTimeProvider(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero)));
executableAssistant.ToggleForm();
executableAssistant.SelectClass("grid.class.installation-integrity");
var executableOptions = executableAssistant.Snapshot();
executableAssistant.SetModSelected(executableOptions.Mods[0].Id, true);
if (executableOptions.Tools.FirstOrDefault(tool => tool.Availability == AvailabilityState.Available) is { } availableTool)
    executableAssistant.SetToolSelected(availableTool.Id, true);
executableAssistant.SetPlainText("This remains a user claim until evidence supports it.");
executableAssistant.SetExpectedBehavior("The profile should have complete compatible assets.");
executableAssistant.SetReproductionOrLocation("The selected installation and profile.");
executableAssistant.SetDesiredOutcome("Produce a read-only evidence-backed diagnosis.");
Assert(executableAssistant.Snapshot().Draft.CanSubmit,
    "A complete registered request is submittable only when a real deterministic execution service is available.");
await executableAssistant.PrepareSubmissionAsync();
var authorizationSnapshot = executableAssistant.Snapshot();
Assert(
    authorizationSnapshot.Surface == AssistantSurface.Task &&
    authorizationSnapshot.PendingAuthorization is { MutationAuthorized: false } &&
    authorizationSnapshot.ActiveTask?.Transcript.Any(entry => entry.Kind == AssistantTranscriptKind.UserClaim &&
        entry.Text.Contains("Expected behavior: The profile should have complete compatible assets.", StringComparison.Ordinal) &&
        entry.Text.Contains("Reproduction or location: The selected installation and profile.", StringComparison.Ordinal) &&
        entry.Text.Contains("Desired outcome: Produce a read-only evidence-backed diagnosis.", StringComparison.Ordinal)) == true,
    "Submission prepares an exact non-mutating authorization review and renders every structured instruction as a user claim.");
await executableAssistant.ApproveSubmissionAsync();
var completedAssistant = executableAssistant.Snapshot();
Assert(
    completedAssistant.LifecycleStage == AssistantLifecycleStage.Completed &&
    completedAssistant.ActiveTask is { TerminalState: "EvidenceComplete", CaseId: "investigation-fixture-v1" } &&
    completedAssistant.Tasks.Length == 1 && completedAssistant.ActiveTask.Finding?.AffectedMods.IsEmpty == true,
    "Explicit authorization produces one evidence-bounded completed task without projecting selected mods into findings.");
Assert(
    completedAssistant.ActiveTask!.ActionStates.Single(state => state.Action == AssistantCaseAction.ReviewRepair).IsEnabled &&
    !completedAssistant.ActiveTask.ActionStates.Single(state => state.Action == AssistantCaseAction.ApplyRepair).IsEnabled,
    "A recovery plan is reviewable while Apply remains disabled until an exact repair specification exists.");
executableAssistant.ShowHistory();
executableAssistant.OpenTask(completedAssistant.Tasks[0].Id);
Assert(executableAssistant.Snapshot().ActiveTask?.Transcript.Any(entry => entry.Kind == AssistantTranscriptKind.Result) == true,
    "Completed deterministic tasks remain reviewable from chronological assistant history.");

var contextualRepairTask = new AssistantTaskRecord(
    new("repair-undefeated-plugin-state", "Review sealed plugin activation", new DateTimeOffset(2026, 9, 2, 13, 0, 0, TimeSpan.Zero), "RepairPlanned"),
    null,
    "repair-undefeated-plugin-state",
    "RepairPlanned",
    [],
    [],
    new(["Panties of SPID.esp"], [], "A sealed plugin activation batch is available.", "Review the exact batch.", []),
    false,
    RepairAvailability: new(
        "plugin-batch-fixture",
        new string('D', 64),
        true,
        false,
        "An exact sealed repair specification is ready for review.",
        HistoryState: "NotApplied",
        RepairKind: "PluginStateBatch",
        InstallationId: undefeated.Id,
        ProfileId: defaultProfile.Id));
var contextualRepairAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true)],
    executionService: new TestAssistantRequestExecutionService([contextualRepairTask]));
await contextualRepairAssistant.LoadPersistedTasksAsync();
Assert(
    contextualRepairAssistant.GetActionableRepairTask(undefeated.Id, defaultProfile.Id)?.Summary.Id == contextualRepairTask.Summary.Id,
    "Footer repair discovery exposes an exact sealed task for its matching installation and profile context.");
Assert(
    contextualRepairAssistant.GetActionableRepairTask(new InstallationId("installation.other"), defaultProfile.Id) is null &&
    contextualRepairAssistant.GetActionableRepairTask(undefeated.Id, null) is null,
    "Footer repair discovery rejects an installation/profile mismatch or incomplete context.");

var reopenedTask = contextualRepairTask with
{
    Summary = contextualRepairTask.Summary with { Id = "reopened-sealed-investigation", Status = "EvidenceComplete" },
    CaseId = "reopened-sealed-investigation",
    TerminalState = "EvidenceComplete",
    RepairAvailability = null,
};
var reopenedExecution = new TestAssistantRequestExecutionService([reopenedTask]);
var reopenedAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true)],
    executionService: reopenedExecution);
await reopenedAssistant.LoadPersistedTasksAsync();
reopenedAssistant.OpenTask(reopenedTask.Summary.Id);
var reopenedActions = reopenedAssistant.Snapshot().ActiveTask!.ActionStates;
Assert(
    reopenedActions.Single(state => state.Action == AssistantCaseAction.AttachEvidence).IsEnabled &&
    reopenedActions.Single(state => state.Action == AssistantCaseAction.CaptureCurrentState).IsEnabled &&
    reopenedActions.Single(state => state.Action == AssistantCaseAction.Diagnose).IsEnabled,
    "A reopened sealed investigation remains actionable even though its in-memory canonical request is not retained across sessions.");
await reopenedAssistant.ExecuteActiveActionAsync(AssistantCaseAction.CaptureCurrentState);
Assert(
    reopenedAssistant.Snapshot().LifecycleStage == AssistantLifecycleStage.AwaitingApproval &&
    reopenedAssistant.Snapshot().PendingAuthorization is { MutationAuthorized: false } &&
    reopenedExecution.LastPreparedAction == AssistantCaseAction.CaptureCurrentState &&
    reopenedExecution.LastPreparedActionParentRequest is null,
    "A reopened investigation reconstructs a successor from its sealed case and requires a fresh exact read authorization.");
reopenedAssistant.RejectAuthorization();
await reopenedAssistant.ExecuteActiveActionAsync(AssistantCaseAction.Diagnose);
Assert(
    reopenedAssistant.Snapshot().LifecycleStage == AssistantLifecycleStage.Completed &&
    reopenedExecution.LastExecutedAction == AssistantCaseAction.Diagnose,
    "A reopened investigation can run a deterministic case action without relying on lost in-memory draft state.");

var failedSuccessorExecution = new TestAssistantRequestExecutionService([reopenedTask])
{
    ExecuteFailure = new InvalidOperationException(
        "The deterministic request bridge refused the operation (AuthorizationSecretInvalid)."),
};
var failedSuccessorAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true)],
    executionService: failedSuccessorExecution,
    timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 9, 2, 14, 0, 0, TimeSpan.Zero)));
await failedSuccessorAssistant.LoadPersistedTasksAsync();
failedSuccessorAssistant.OpenTask(reopenedTask.Summary.Id);
await failedSuccessorAssistant.ExecuteActiveActionAsync(AssistantCaseAction.CaptureCurrentState);
await AssertThrowsAsync<InvalidOperationException>(
    () => failedSuccessorAssistant.ApproveSubmissionAsync(),
    "A successor handoff failure remains an explicit failed operation.");
var failedSuccessorSnapshot = failedSuccessorAssistant.Snapshot();
Assert(
    failedSuccessorSnapshot.LifecycleStage == AssistantLifecycleStage.Failed &&
    failedSuccessorSnapshot.ActiveTask?.Summary.Id == reopenedTask.Summary.Id &&
    failedSuccessorSnapshot.ActiveTask.Transcript.Any(entry =>
        entry.Kind == AssistantTranscriptKind.Failure &&
        entry.Text.Contains("AuthorizationSecretInvalid", StringComparison.Ordinal)) &&
    failedSuccessorSnapshot.Tasks.Count(task => task.Id == reopenedTask.Summary.Id) == 1,
    "A failed successor restores its sealed predecessor but retains the exact handoff failure in the visible task and activity state.");

var interruptedTask = reopenedTask with
{
    Summary = reopenedTask.Summary with { Id = "interrupted-unsealed-workspace", Status = "Interrupted" },
    CaseId = "workspace-interrupted",
    TerminalState = "Interrupted",
    Finding = null,
    Receipts = [],
};
var interruptedAssistant = new AssistantSessionState(
    gameContext,
    catalog,
    [new("grid.class.installation-integrity", "Installation Integrity", "grid.icon.installation", true)],
    executionService: new TestAssistantRequestExecutionService([interruptedTask]));
await interruptedAssistant.LoadPersistedTasksAsync();
interruptedAssistant.OpenTask(interruptedTask.Summary.Id);
var interruptedActions = interruptedAssistant.Snapshot().ActiveTask!.ActionStates;
Assert(
    !interruptedActions.Single(state => state.Action == AssistantCaseAction.AttachEvidence).IsVisible &&
    !interruptedActions.Single(state => state.Action == AssistantCaseAction.CaptureCurrentState).IsVisible &&
    !interruptedActions.Single(state => state.Action == AssistantCaseAction.Diagnose).IsVisible,
    "An interrupted unsealed workspace cannot masquerade as a sealed cross-session investigation.");

await applicationSession.History.RecordAsync(new(
    new HistoryEntryId("history.core.fixture"),
    new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero),
    HistoryActor.User,
    HistoryEventKind.InstallationConnected,
    HistoryEventStatus.Succeeded,
    skyrim.Id,
    undefeated.Id,
    defaultProfile.Id,
    "Installation connected",
    "Grid saved a reference."));
Assert(applicationSession.History.Entries.Length == 1 && sessionHistoryStore.Entries.Length == 1,
    "Genuine user outcomes append to the bounded history state.");

var emptyApplicationSession = new GridApplicationSession(emptyCatalog, new TestUserHistoryStore());
Assert(
    emptyApplicationSession.Shell.CurrentSelection == WorkspaceSelection.Empty &&
    emptyApplicationSession.SynchronizeContext().GameId is null,
    "The application session represents an empty catalog without fabricating hierarchy context.");

AssertThrows<ArgumentException>(() => _ = new FidelityAuditId(" "), "Blank fidelity-audit identities are rejected.");
AssertThrows<ArgumentException>(() => _ = new ExternalLaunchSessionId(" "), "Blank external-launch session identities are rejected.");
AssertThrows<ArgumentException>(() => _ = new LaunchApprovalId(" "), "Blank launch-approval identities are rejected.");
AssertThrows<ArgumentException>(() => _ = new LaunchEventId(" "), "Blank launch-event identities are rejected.");

var auditContext = new FidelityAuditContext(
    skyrim.Id,
    connectedInstallation.Id,
    connectedInstallation.Profiles[0].Id,
    referenceId,
    "audit-catalog-revision",
    "connection-fingerprint",
    "profile-fingerprint",
    "inventory-fingerprint",
    new ResolvedSnapshotId("resolved.audit"),
    "environment-fingerprint",
    toolOutputSnapshot.Id,
    toolOutputSnapshot.Summary.Fingerprint,
    observedExecutable.Id,
    observedExecutable.Fingerprint);
var warningAuditItem = new FidelityAuditItem(
    "audit.unsupported-archive",
    FidelityAuditArea.Archives,
    FidelityAuditItemStatus.Unsupported,
    FidelityAuditCriticality.LaunchRelevant,
    FidelityAuditDisposition.AcknowledgementRequired,
    "Unsupported archive evidence",
    "The unsupported archive remains visible and requires acknowledgement.");
var passAuditItem = new FidelityAuditItem(
    "audit.connection",
    FidelityAuditArea.Connection,
    FidelityAuditItemStatus.Pass,
    FidelityAuditCriticality.LaunchCritical,
    FidelityAuditDisposition.Satisfied,
    "Connection revalidated",
    "The connected reference matches its current canonical identity.");
var auditSnapshot = new FidelityAuditSnapshot(
    new FidelityAuditId("audit.fixture.001"),
    auditContext,
    observedAtUtc,
    "audit-fingerprint",
    FidelityAuditReadiness.RequiresAcknowledgement,
    [passAuditItem, warningAuditItem],
    "One launch-relevant unsupported condition requires acknowledgement.");
Assert(
    !auditSnapshot.HasBlockingItems && auditSnapshot.RequiresAcknowledgement && auditSnapshot.DiscrepancyCount == 1,
    "Fidelity-audit snapshots distinguish blocking evidence from launch-relevant acknowledgement evidence.");

var auditService = new TestFidelityAuditService(context => auditSnapshot with
{
    Context = context,
    Id = new FidelityAuditId($"audit.{context.ProfileId.Value}"),
    Fingerprint = $"audit-fingerprint.{context.SelectedExecutableId}",
});
using var auditState = new FidelityAuditState(auditService);
auditState.SetContext(auditContext);
var auditResult = await auditState.RunAsync();
Assert(
    auditResult.HasCurrentSnapshot && !auditState.IsCurrentSnapshotStale && auditState.Progress?.Stage == FidelityAuditStage.Publishing,
    "Fidelity-audit state publishes a current context-bound snapshot and typed progress.");
auditState.SetContext(auditContext with { CatalogRevision = "replacement-revision" });
Assert(
    auditState.IsCurrentSnapshotStale && auditState.Snapshot is not null,
    "Changing relevant evidence context retains the prior audit only as visibly stale.");
using var mismatchedAuditState = new FidelityAuditState(new TestFidelityAuditService(_ => auditSnapshot));
mismatchedAuditState.SetContext(auditContext with { CatalogRevision = "mismatched-service-context" });
var mismatchedAuditResult = await mismatchedAuditState.RunAsync();
Assert(
    mismatchedAuditResult.Status == FidelityAuditRunStatus.Failed && mismatchedAuditState.Snapshot is null,
    "Audit state rejects service evidence returned for a different context.");

var launchIntent = new ExternalLaunchIntent(
    auditContext.GameId,
    auditContext.InstallationId,
    auditContext.ProfileId,
    auditContext.ReferenceId,
    auditContext.SelectedExecutableId!.Value,
    auditContext.SelectedExecutableFingerprint!,
    auditSnapshot.Id,
    auditSnapshot.Fingerprint);
var launchService = new TestWorkspaceLaunchService();
using var workspaceLaunchState = new WorkspaceLaunchState(launchService);
workspaceLaunchState.SetIntent(launchIntent);
var preparationResult = await workspaceLaunchState.PrepareAsync(auditSnapshot);
Assert(
    preparationResult.Status == ExternalLaunchPreparationStatus.RequiresAcknowledgement && workspaceLaunchState.CanApprove,
    "Launch preparation preserves audit acknowledgement requirements without treating them as satisfied.");
AssertThrows<InvalidOperationException>(
    () => workspaceLaunchState.CreateApproval(acknowledgeWarningsAndUnsupported: false),
    "Launch-relevant warnings and unsupported evidence cannot be approved implicitly.");
var launchApproval = workspaceLaunchState.CreateApproval(acknowledgeWarningsAndUnsupported: true);
Assert(
    launchApproval.ExplicitUserAction && launchApproval.AuditFingerprint == auditSnapshot.Fingerprint,
    "Explicit approval is bound to the exact audited launch preparation.");
var launchResult = await workspaceLaunchState.LaunchAsync(launchApproval);
Assert(
    launchResult.Status == ExternalLaunchSessionStatus.Completed &&
    workspaceLaunchState.Events.Select(value => value.Sequence).SequenceEqual([1, 2, 3]) &&
    workspaceLaunchState.Events.All(value => !value.ConfiguredProcessLifecycleObserved),
    "Launch state keeps an append-only lifecycle and does not fabricate configured-process observation.");
AssertThrows<ArgumentException>(
    () => workspaceLaunchState.LaunchAsync(launchApproval with { PreparationFingerprint = "tampered" }).GetAwaiter().GetResult(),
    "A launch approval cannot be reused against a different preparation fingerprint.");

var stalePreparation = await workspaceLaunchState.PrepareAsync(auditSnapshot with { Fingerprint = "stale-audit" });
Assert(
    stalePreparation.Status == ExternalLaunchPreparationStatus.Stale,
    "A launch intent cannot silently retarget a changed fidelity audit.");

var productionConnectedInstallation = observedConnectedInstallation with
{
    Metadata = observedConnectedInstallation.Metadata with { ConnectionFingerprint = "connection-fingerprint" },
};
var productionConnectedGame = connectedGame with
{
    Installations = connectedGame.Installations.SetItem(0, productionConnectedInstallation),
};
var productionCatalog = new GridCatalogSnapshot(
    "production-session",
    CatalogSourceKind.Mixed,
    [productionConnectedGame, gta]);
var productionToolService = new TestWorkspaceToolOutputQueryService(new(
    ExternalObservationRefreshStatus.Completed,
    toolOutputSnapshot with
    {
        Context = toolOutputContext with { CatalogRevision = productionCatalog.Revision },
    },
    "Current tool observation.",
    []));
using var productionSession = new GridApplicationSession(
    productionCatalog,
    new TestUserHistoryStore(),
    productionToolService,
    fidelityAuditService: auditService,
    launchService: launchService);
productionSession.Shell.NavigateGame(productionConnectedGame.Id);
productionSession.Shell.SelectInstallation(productionConnectedInstallation.Id);
productionSession.Shell.SelectProfile(productionConnectedInstallation.Profiles[0].Id);
productionSession.SynchronizeContext();
await productionSession.ToolOutputs!.RefreshAsync(false);
productionSession.ToolOutputs.SelectExecutable(observedExecutable.Id);
productionSession.SynchronizeContext();
Assert(
    productionSession.FidelityAudit?.Context?.SelectedExecutableId == observedExecutable.Id,
    "The production composition root binds selected read-only executable evidence into audit context.");
await productionSession.FidelityAudit!.RunAsync();
productionSession.SynchronizeContext();
Assert(
    productionSession.ExternalLaunch?.Intent?.ExecutableId == observedExecutable.Id &&
    productionSession.ExternalLaunch.Intent.AuditId == productionSession.FidelityAudit.Snapshot!.Id,
    "Only a current matching fidelity audit creates an external launch intent.");

Assert(
    productionSession.Catalog.Games.Any(game => game.Id == productionConnectedGame.Id),
    "The production session exposes authoritative catalog entities directly without a dashboard DTO projection.");

var repeatedCatalog = await service.GetCatalogAsync();
Assert(
    catalog.Revision == repeatedCatalog.Revision &&
    catalog.Games.Select(game => game.Id).SequenceEqual(repeatedCatalog.Games.Select(game => game.Id)),
    "Repeated mock catalog reads are deterministic.");

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    await AssertThrowsAsync<OperationCanceledException>(
        () => service.GetCatalogAsync(cancellation.Token),
        "Catalog reads honor cancellation.");
}

Assert(
    !stageOneProductionCatalog.Games.SelectMany(game => game.Installations).Any(),
    "Normal Production Home contains no connected games until onboarding persists a real reference.");

var offlineAlertBuilder = new OfflineAlertIndexBuilder();
var noisyToolSnapshot = toolOutputSnapshot with
{
    Summary = toolOutputSnapshot.Summary with { WarningCount = 3 },
    Executables = [observedExecutable with { WarningCount = 1 }],
    Outputs = [observedOutput with { WarningCount = 1 }],
    Warnings = ["Fixture tool output warning."],
};
var noisyToolResult = new ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus.Completed,
    noisyToolSnapshot,
    "Current fixture output evidence.",
    []);
var currentAuditResult = new FidelityAuditResult(
    FidelityAuditRunStatus.Completed,
    auditSnapshot,
    "Current fixture fidelity evidence.");
var alertIndex = offlineAlertBuilder.Build(
    toolOutputContext,
    noisyToolResult,
    currentAuditResult,
    observedAtUtc: observedAtUtc);
Assert(
    alertIndex.SchemaVersion == 1 && alertIndex.Alerts.Length == 4 && alertIndex.ActiveCount == 4,
    "Offline alert indexing normalizes fidelity, tool-output, executable, and generated-output alerts into one snapshot.");
Assert(
    alertIndex.Alerts.All(alert => !string.IsNullOrWhiteSpace(alert.Id) && !string.IsNullOrWhiteSpace(alert.ContextFingerprint) && !alert.Provenance.IsEmpty),
    "Every offline alert retains a stable identity, selected-context fingerprint, and local provenance for instant display.");
var alertProfileBase = connectedInstallation.Profiles[0];
var alertModBase = alertProfileBase.Mods.First(mod => mod.Kind == ModEntryKind.Mod);
var alertModInventory = new ModInventoryObservation(
    ModInventoryAuthority.ManagerAuthoritative,
    ModReconciliationState.Matched,
    0,
    0,
    "+",
    ModMetadataAvailability.Available,
    observedAtUtc,
    "modlist.txt",
    "alert-mod-inventory-fingerprint",
    1,
    ["The mod metadata records a malformed provider value."],
    null,
    new ModUpdateEvidence(ModUpdateState.UpdateAvailable, "Cached provider metadata reports a newer version.", observedAtUtc, false));
var alertPluginBase = alertProfileBase.Plugins[0];
var alertPluginObservation = new PluginObservation(
    0,
    PluginFileExtension.Esp,
    false,
    false,
    PluginActivationProvenance.PluginsFileMarker,
    PluginOrderProvenance.LoadOrderFile,
    PluginFileAvailability.Present,
    alertModBase.Name,
    "alert-plugin-fingerprint",
    observedAtUtc,
    [],
    ["A declared master is disabled."]);
var alertProfile = alertProfileBase with
{
    Observation = alertProfileBase.Observation! with
    {
        Status = ProfileObservationStatus.Partial,
        WarningCount = 2,
        Sources = [new("modlist.txt", ProfileSourceAvailability.RequiredMissing, ProfileSourceParseStatus.NotParsed, 1)],
    },
    Mods = alertProfileBase.Mods.Select(mod => mod.Id == alertModBase.Id
        ? mod with { Inventory = alertModInventory, ConflictState = ModConflictState.Mixed }
        : mod).ToImmutableArray(),
    Plugins = alertProfileBase.Plugins.SetItem(0, alertPluginBase with { Observation = alertPluginObservation }),
};
var alertEnvironmentContext = new WorkspaceEnvironmentContext(
    toolOutputContext.GameId,
    toolOutputContext.InstallationId,
    toolOutputContext.ProfileId,
    toolOutputContext.CatalogRevision,
    toolOutputContext.InventoryFingerprint);
var alertEnvironmentSnapshot = new ResolvedEnvironmentSnapshot(
    new ResolvedSnapshotId("resolved-snapshot-alert-fixture"),
    alertEnvironmentContext,
    environmentSummary with { Fingerprint = "alert-environment-fingerprint", DiscrepancyCount = 1 },
    [new(new EnvironmentDiscrepancyId("environment-discrepancy-alert-fixture"), EnvironmentDiscrepancyKind.MissingEvidence,
        EnvironmentDiscrepancySeverity.Error, "Missing archive evidence", "One active archive could not be indexed.")],
    false);
var alertEnvironmentResult = new ResolvedEnvironmentRefreshResult(
    ResolvedEnvironmentRefreshStatus.Completed,
    alertEnvironmentSnapshot,
    "Current resolved-environment evidence.",
    []);
var comprehensiveAlertIndex = offlineAlertBuilder.Build(
    toolOutputContext,
    noisyToolResult,
    currentAuditResult,
    observedAtUtc: observedAtUtc,
    profile: alertProfile,
    resolvedEnvironment: alertEnvironmentResult);
Assert(
    comprehensiveAlertIndex.Alerts.Any(alert => alert.SourceKind == OfflineAlertSourceKind.Profile) &&
    comprehensiveAlertIndex.Alerts.Any(alert => alert.SourceIdentity == alertModBase.Id.Value && alert.Code == "grid.mod.inventory-warning") &&
    comprehensiveAlertIndex.Alerts.Any(alert => alert.SourceIdentity == alertModBase.Id.Value && alert.Code == "grid.mod.update-available") &&
    comprehensiveAlertIndex.Alerts.Any(alert => alert.SourceIdentity == alertModBase.Id.Value && alert.Code == "grid.mod.conflict") &&
    comprehensiveAlertIndex.Alerts.Any(alert => alert.SourceKind == OfflineAlertSourceKind.Plugin && alert.SourceIdentity == alertPluginBase.Id.Value) &&
    comprehensiveAlertIndex.Alerts.Single(alert => alert.SourceKind == OfflineAlertSourceKind.ResolvedEnvironment).IsBlocking,
    "Offline alert indexing unifies profile sources, per-mod warnings/updates/conflicts, plugin warnings, and resolved-environment discrepancies.");
var repeatedAlertIndex = offlineAlertBuilder.Build(
    toolOutputContext,
    noisyToolResult,
    currentAuditResult,
    alertIndex,
    observedAtUtc.AddMinutes(1));
Assert(
    repeatedAlertIndex.Fingerprint == alertIndex.Fingerprint &&
    repeatedAlertIndex.Alerts.Select(alert => alert.Id).SequenceEqual(alertIndex.Alerts.Select(alert => alert.Id)),
    "Repeated equivalent observations retain deterministic alert identities and semantic fingerprint.");
var quietTools = new ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus.Completed,
    toolOutputSnapshot,
    "Complete fixture source no longer reports warnings.",
    []);
var quietAudit = new FidelityAuditResult(
    FidelityAuditRunStatus.Completed,
    auditSnapshot with { Items = [passAuditItem], Readiness = FidelityAuditReadiness.Ready },
    "Complete fixture audit no longer reports discrepancies.");
var clearedAlerts = offlineAlertBuilder.Build(toolOutputContext, quietTools, quietAudit, alertIndex, observedAtUtc.AddMinutes(2));
Assert(
    clearedAlerts.Alerts.All(alert => alert.State == OfflineAlertState.Cleared),
    "An alert clears only when its owning source completes and no longer reports it.");
var unavailableTools = new ToolOutputObservationRefreshResult(
    ExternalObservationRefreshStatus.Unavailable,
    null,
    "Fixture source unavailable.",
    []);
var unavailableAudit = new FidelityAuditResult(FidelityAuditRunStatus.Unavailable, null, "Fixture audit unavailable.");
var staleAlerts = offlineAlertBuilder.Build(toolOutputContext, unavailableTools, unavailableAudit, alertIndex, observedAtUtc.AddMinutes(3));
Assert(
    staleAlerts.Alerts.All(alert => alert.State == OfflineAlertState.Stale),
    "Unavailable refreshes retain prior offline alerts as stale instead of silently clearing them.");

var offlineAlertStore = new TestOfflineAlertIndexStore();
var offlineAlertState = new OfflineAlertIndexState(offlineAlertStore, new FixedTimeProvider(observedAtUtc.AddMinutes(4)));
offlineAlertState.SetContext(toolOutputContext);
Assert(
    offlineAlertState.Snapshot is null && !offlineAlertState.IsFromPersistentCache,
    "A selected profile without prior alert evidence starts with an empty offline index.");
var persistedAlerts = offlineAlertState.Refresh(noisyToolResult, currentAuditResult)!;
Assert(
    offlineAlertStore.Saved?.Fingerprint == persistedAlerts.Fingerprint && offlineAlertBuilder.IsValid(persistedAlerts),
    "Refreshing offline alerts atomically publishes a validated persistable snapshot.");
var acknowledgedId = persistedAlerts.Alerts.First().Id;
Assert(
    offlineAlertState.SetDisposition(acknowledgedId, OfflineAlertState.Acknowledged, "Reviewed in the alert center.") &&
    offlineAlertState.Snapshot!.Alerts.Single(alert => alert.Id == acknowledgedId).State == OfflineAlertState.Acknowledged,
    "An operator can durably acknowledge an exact current alert without clearing its evidence.");
var accountedId = persistedAlerts.Alerts.Skip(1).First().Id;
Assert(
    !offlineAlertState.SetDisposition(accountedId, OfflineAlertState.AccountedFor) &&
    offlineAlertState.SetDisposition(accountedId, OfflineAlertState.AccountedFor, "Accepted until the upstream tool can regenerate its output."),
    "Accounting for an alert requires a bounded reason.");
var restoredAlertState = new OfflineAlertIndexState(offlineAlertStore, new FixedTimeProvider(observedAtUtc.AddMinutes(5)));
restoredAlertState.SetContext(toolOutputContext);
Assert(
    restoredAlertState.IsFromPersistentCache &&
    restoredAlertState.Snapshot?.Alerts.Single(alert => alert.Id == acknowledgedId).State == OfflineAlertState.Acknowledged &&
    restoredAlertState.Snapshot.Alerts.Single(alert => alert.Id == accountedId).DispositionReason is not null,
    "Exact profile alerts and their operator dispositions load instantly from local persistence.");
var refreshedDisposition = restoredAlertState.Refresh(noisyToolResult, currentAuditResult)!;
Assert(
    refreshedDisposition.Alerts.Single(alert => alert.Id == acknowledgedId).State == OfflineAlertState.Acknowledged &&
    refreshedDisposition.Alerts.Single(alert => alert.Id == accountedId).State == OfflineAlertState.AccountedFor,
    "Equivalent evidence retains acknowledged and accounted-for dispositions across refreshes.");
var changedAlertContext = toolOutputContext with { InventoryFingerprint = new string('9', 64) };
var driftedAlertState = new OfflineAlertIndexState(offlineAlertStore, new FixedTimeProvider(observedAtUtc.AddMinutes(6)));
driftedAlertState.SetContext(changedAlertContext);
Assert(
    driftedAlertState.IsCurrentSnapshotStale && driftedAlertState.Snapshot?.Alerts.All(alert => alert.State == OfflineAlertState.Stale) == true,
    "A changed profile inventory keeps cached alerts visible but explicitly stale.");
offlineAlertStore.Saved = refreshedDisposition with
{
    Alerts = refreshedDisposition.Alerts.SetItem(0, refreshedDisposition.Alerts[0] with { Title = "Tampered title" }),
};
var tamperedAlertState = new OfflineAlertIndexState(offlineAlertStore);
tamperedAlertState.SetContext(toolOutputContext);
Assert(
    tamperedAlertState.Snapshot is null && tamperedAlertState.PersistenceIssue?.Contains("integrity", StringComparison.OrdinalIgnoreCase) == true,
    "A tampered cached offline alert index is rejected instead of rendered as trusted evidence.");

Console.WriteLine($"All {checks} Grid.Core checks passed.");
return;

void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException($"Check failed: {message}");
    }

    checks++;
    Console.WriteLine($"PASS  {message}");
}

void AssertSelection(
    WorkspaceSelection actual,
    GameId? expectedGame,
    InstallationId? expectedInstallation,
    ProfileId? expectedProfile,
    string message) =>
    Assert(
        actual.GameId == expectedGame &&
        actual.InstallationId == expectedInstallation &&
        actual.ProfileId == expectedProfile,
        message);

void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        Assert(true, message);
        return;
    }

    throw new InvalidOperationException($"Check failed: {message}");
}

async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        Assert(true, message);
        return;
    }

    throw new InvalidOperationException($"Check failed: {message}");
}

void AssertInvalidConnectedObservation(
    ProfileObservationSummary observation,
    ManagedInstallation installation,
    ManagedGame game,
    ManagedGame otherGame,
    string revision,
    string message)
{
    var invalidProfile = installation.Profiles[0] with { Observation = observation };
    var invalidInstallation = installation with
    {
        Profiles = installation.Profiles.SetItem(0, invalidProfile),
    };
    AssertThrows<ArgumentException>(
        () => _ = new GridCatalogSnapshot(
            revision,
            CatalogSourceKind.Mixed,
            [game with { Installations = game.Installations.SetItem(0, invalidInstallation) }, otherGame]),
        message);
}

sealed class UnknownActionOperatorService : IAiOperatorService
{
    public Task<OperatorServiceResponse> RespondAsync(
        OperatorRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new OperatorServiceResponse(
            "The deliberately invalid test service returned an unknown action.",
            ImmutableArray<OperatorStatement>.Empty,
            new DeterministicActionId("action.not-in-supplied-context")));
    }
}

sealed class TestWorkspaceEnvironmentQueryService : IWorkspaceEnvironmentQueryService
{
    private TaskCompletionSource<ResolvedEnvironmentRefreshResult>? _heldRefresh;

    public TestWorkspaceEnvironmentQueryService(ResolvedEnvironmentRefreshResult? nextResult) =>
        NextResult = nextResult;

    public ResolvedEnvironmentRefreshResult? NextResult { get; set; }

    public bool HoldRefresh { get; set; }

    public TaskCompletionSource<bool> RefreshStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ResolvedEnvironmentRefreshResult> RefreshAsync(
        WorkspaceEnvironmentContext context,
        bool forceRefresh,
        IProgress<ResolvedEnvironmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ResolvedEnvironmentProgress(
            EnvironmentObservationStage.Validating,
            1,
            4,
            "Validating fixture context."));
        RefreshStarted.TrySetResult(true);
        if (HoldRefresh)
        {
            _heldRefresh = new TaskCompletionSource<ResolvedEnvironmentRefreshResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _heldRefresh.Task;
        }

        return Task.FromResult(NextResult ?? throw new InvalidOperationException("No fixture result is configured."));
    }

    public void CompleteHeldRefresh(ResolvedEnvironmentRefreshResult result) =>
        (_heldRefresh ?? throw new InvalidOperationException("No refresh is held.")).TrySetResult(result);

    public Task<EnvironmentPage<PluginEntry>> QueryPluginsAsync(
        ResolvedSnapshotId snapshotId,
        PluginQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentPage<PluginEntry>(
            [
                new PluginEntry(new PluginId("plugin.resolved.enabled"), "Enabled.esp", true, 0, HealthLevel.Healthy, 0),
                new PluginEntry(new PluginId("plugin.resolved.disabled"), "Disabled.esp", false, null, HealthLevel.Advisory, 1),
            ],
            0,
            2,
            query.PageSize,
            snapshotId));

    public Task<EnvironmentPage<ResolvedArchiveEntry>> QueryArchivesAsync(
        ResolvedSnapshotId snapshotId,
        ArchiveQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentPage<ResolvedArchiveEntry>([], 0, 0, query.PageSize, snapshotId));

    public Task<EnvironmentPage<VirtualDataEntry>> QueryDataAsync(
        ResolvedSnapshotId snapshotId,
        VirtualDataQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EnvironmentPage<VirtualDataEntry>(
            [new VirtualDataEntry(
                new VirtualPathId("virtual.path.mesh"),
                "meshes\\fixture.nif",
                2,
                new ProviderId("provider.overwrite"),
                "Overwrite",
                ProviderWinnerConfidence.Established,
                0)],
            0,
            1,
            query.PageSize,
            snapshotId));

    public Task<ProviderChain?> GetProviderChainAsync(
        ResolvedSnapshotId snapshotId,
        VirtualPathId virtualPathId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ProviderChain?>(new ProviderChain(
            snapshotId,
            virtualPathId,
            "meshes\\fixture.nif",
            [
                new VirtualFileProvider(
                    new ProviderId("provider.mod"),
                    VirtualProviderKind.ModLooseFile,
                    "Fixture Mod",
                    new ModId("mod.fixture"),
                    null,
                    1,
                    false,
                    "Higher-priority loose provider follows.",
                    "provider-mod-fingerprint",
                    []),
                new VirtualFileProvider(
                    new ProviderId("provider.overwrite"),
                    VirtualProviderKind.OverwriteLooseFile,
                    "Overwrite",
                    null,
                    null,
                    2,
                    true,
                    "MO2 overwrite is the highest loose provider.",
                    "provider-overwrite-fingerprint",
                    []),
            ],
            new ProviderId("provider.overwrite"),
            ProviderWinnerConfidence.Established,
            "The overwrite loose file wins the verified loose-provider chain.",
            []));
}

sealed class TestWorkspaceToolOutputQueryService : IWorkspaceToolOutputQueryService
{
    private TaskCompletionSource<ToolOutputObservationRefreshResult>? _heldRefresh;

    public TestWorkspaceToolOutputQueryService(ToolOutputObservationRefreshResult? nextResult) =>
        NextResult = nextResult;

    public ToolOutputObservationRefreshResult? NextResult { get; set; }

    public bool HoldRefresh { get; set; }

    public TaskCompletionSource<bool> RefreshStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ToolOutputObservationRefreshResult> RefreshAsync(
        WorkspaceToolOutputContext context,
        bool forceRefresh,
        IProgress<ExternalObservationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ExternalObservationProgress(
            ExternalObservationStage.Publishing,
            1,
            1,
            "Publishing fixture observation."));
        RefreshStarted.TrySetResult(true);
        if (HoldRefresh)
        {
            _heldRefresh = new TaskCompletionSource<ToolOutputObservationRefreshResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _heldRefresh.Task;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(NextResult ?? throw new InvalidOperationException("No fixture result is configured."));
    }

    public void CompleteHeldRefresh(ToolOutputObservationRefreshResult result) =>
        (_heldRefresh ?? throw new InvalidOperationException("No refresh is held.")).TrySetResult(result);
}

sealed class TestFidelityAuditService(
    Func<FidelityAuditContext, FidelityAuditSnapshot> createSnapshot) : IFidelityAuditService
{
    public Task<FidelityAuditResult> RunAsync(
        FidelityAuditContext context,
        IProgress<FidelityAuditProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(
            FidelityAuditStage.RevalidatingConnection,
            1,
            2,
            "Revalidating fixture evidence."));
        var snapshot = createSnapshot(context);
        progress?.Report(new(
            FidelityAuditStage.Publishing,
            2,
            2,
            "Publishing fixture audit."));
        return Task.FromResult(new FidelityAuditResult(
            FidelityAuditRunStatus.Completed,
            snapshot,
            "Fixture fidelity audit completed."));
    }
}

sealed class TestWorkspaceLaunchService : IWorkspaceLaunchService
{
    public Task<ExternalLaunchPreparationResult> PrepareAsync(
        ExternalLaunchIntent intent,
        FidelityAuditSnapshot audit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var blocking = audit.Items.Where(item => item.IsBlocking).ToImmutableArray();
        var acknowledgements = audit.Items.Where(item => item.RequiresAcknowledgement).ToImmutableArray();
        var status = !blocking.IsEmpty
            ? ExternalLaunchPreparationStatus.Blocked
            : !acknowledgements.IsEmpty
                ? ExternalLaunchPreparationStatus.RequiresAcknowledgement
                : ExternalLaunchPreparationStatus.Ready;
        var preparation = new ExternalLaunchPreparation(
            new ExternalLaunchSessionId($"launch-session.{intent.ExecutableId.Value}"),
            intent,
            $"preparation.{intent.AuditFingerprint}",
            new DateTimeOffset(2026, 8, 25, 15, 0, 0, TimeSpan.Zero),
            status,
            blocking,
            acknowledgements,
            "Fixture launch preparation.");
        return Task.FromResult(new ExternalLaunchPreparationResult(status, preparation, preparation.Detail));
    }

    public Task<ExternalLaunchResult> LaunchAsync(
        ExternalLaunchPreparation preparation,
        ExternalLaunchApproval approval,
        IProgress<ExternalLaunchEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExternalLaunchEvent[] launchEvents =
        [
            new(
                new LaunchEventId($"launch-event.{preparation.SessionId.Value}.001"),
                preparation.SessionId,
                1,
                new DateTimeOffset(2026, 8, 25, 15, 1, 0, TimeSpan.Zero),
                ExternalLaunchEventKind.LaunchRequested,
                "The user requested a fixture launch."),
            new(
                new LaunchEventId($"launch-event.{preparation.SessionId.Value}.002"),
                preparation.SessionId,
                2,
                new DateTimeOffset(2026, 8, 25, 15, 1, 1, TimeSpan.Zero),
                ExternalLaunchEventKind.ManagerProcessStarted,
                "The fixture manager invocation process started."),
            new(
                new LaunchEventId($"launch-event.{preparation.SessionId.Value}.003"),
                preparation.SessionId,
                3,
                new DateTimeOffset(2026, 8, 25, 15, 1, 2, TimeSpan.Zero),
                ExternalLaunchEventKind.ManagerInvocationExited,
                "The fixture manager invocation exited.",
                ExitCode: 0),
        ];
        foreach (var launchEvent in launchEvents)
        {
            progress?.Report(launchEvent);
        }

        var session = new ExternalLaunchSession(
            preparation.SessionId,
            preparation.Intent,
            ExternalLaunchSessionStatus.Completed,
            launchEvents[0].OccurredAtUtc,
            launchEvents[^1].OccurredAtUtc,
            launchEvents.ToImmutableArray(),
            "The fixture manager invocation completed. Configured-process lifecycle was not observed.");
        return Task.FromResult(new ExternalLaunchResult(
            ExternalLaunchSessionStatus.Completed,
            session,
            session.Detail));
    }
}

sealed class TestUserHistoryStore : IUserHistoryStore
{
    public ImmutableArray<HistoryEntry> Entries { get; private set; } = [];

    public Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HistoryLoadResult(Entries, null));
    }

    public Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entries = Entries.Insert(0, entry);
        return Task.CompletedTask;
    }
}

sealed class TestOfflineAlertIndexStore : IOfflineAlertIndexStore
{
    public OfflineAlertIndex? Saved { get; set; }

    public OfflineAlertIndex? Load(GameId gameId, InstallationId installationId, ProfileId profileId) => Saved;

    public void Save(OfflineAlertIndex index) => Saved = index;
}

sealed class TestAssistantRequestExecutionService : IAssistantRequestExecutionService
{
    private ImmutableArray<AssistantTaskRecord> tasks = [];

    public TestAssistantRequestExecutionService(IEnumerable<AssistantTaskRecord>? seedTasks = null)
    {
        tasks = seedTasks?.OrderBy(task => task.Summary.CreatedAtUtc).ToImmutableArray() ?? [];
    }

    public bool IsAvailable => true;
    public bool RequiresToolSelection => true;
    public AssistantCanonicalRequest? LastPreparedActionParentRequest { get; private set; }
    public AssistantCaseAction? LastPreparedAction { get; private set; }
    public AssistantCaseAction? LastExecutedAction { get; private set; }
    public Exception? ExecuteFailure { get; init; }

    public Task<ImmutableArray<AssistantTaskRecord>> LoadTasksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(tasks);

    public Task<AssistantAuthorizationReview> PrepareAsync(AssistantRequestDraft request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonical = new AssistantCanonicalRequest(
            "request-0123456789abcdef01234567",
            new string('A', 64),
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
            request);
        var scopes = request.Tools.Select(tool => new AssistantReadScope(
            tool.ToolId, tool.ProviderName, "FixtureAdapter", "ExistingOutputRead", "Available", ["C:\\fixture\\read-only"])).ToImmutableArray();
        return Task.FromResult(new AssistantAuthorizationReview(
            "authorization-0123456789abcdef01234567", "submission-fixture", canonical, new string('B', 64), scopes,
            new string('C', 64), false, "Read only the exact displayed paths; no mutation or launch is authorized."));
    }

    public Task<AssistantAuthorizationGrant> GrantAsync(AssistantAuthorizationReview authorization, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AssistantAuthorizationGrant(
            authorization.ReviewId, authorization.Request.RequestId, authorization.SubmissionId,
            "grant-0123456789abcdef01234567", "fixture-one-use-secret",
            new DateTimeOffset(2026, 9, 2, 12, 6, 0, TimeSpan.Zero), false));

    public Task<AssistantExecutionResult> ExecuteAsync(
        AssistantAuthorizationGrant grant,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ExecuteFailure is not null)
            return Task.FromException<AssistantExecutionResult>(ExecuteFailure);
        progress.Report(new(new DateTimeOffset(2026, 9, 2, 12, 1, 1, TimeSpan.Zero), "Collecting", "Reading fixture evidence."));
        return Task.FromResult(new AssistantExecutionResult(
            grant.SubmissionId,
            "investigation-fixture-v1",
            "EvidenceComplete",
            new([], [], "Registered read-only evidence was collected; no diagnosis is asserted.", "Review the sealed evidence.", []),
            [],
            new DateTimeOffset(2026, 9, 2, 12, 2, 0, TimeSpan.Zero),
            RepairAvailability: new(null, null, false, false, "Recovery requirements are available.", HasRecoveryPlan: true)));
    }

    public Task<AssistantExecutionResult> ResumeAsync(
        string taskId,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fixture has no interrupted task.");

    public Task<AssistantAuthorizationReview> PrepareActionAsync(
        AssistantCanonicalRequest? parentRequest,
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var task = tasks.Single(task => task.Summary.Id == taskId);
        LastPreparedActionParentRequest = parentRequest;
        LastPreparedAction = action;
        var draft = parentRequest?.Draft ?? new AssistantRequestDraft(
            new GameId("game.fixture"),
            new InstallationId("installation.fixture"),
            new ProfileId("profile.fixture"),
            [],
            [],
            task.Summary.Title,
            "1.0.0",
            task.Transcript.FirstOrDefault(entry => entry.Kind == AssistantTranscriptKind.UserClaim)?.Text ?? "Reopened sealed investigation.",
            task.Summary.Title,
            Attachments: attachments);
        return PrepareAsync(draft with { Attachments = attachments }, cancellationToken);
    }

    public Task<AssistantTaskRecord> ExecuteActionAsync(
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var existing = tasks.Single(task => task.Summary.Id == taskId);
        LastExecutedAction = action;
        progress.Report(new(DateTimeOffset.UtcNow, action.ToString(), "Fixture case action completed."));
        var required = action == AssistantCaseAction.RollBack
            ? new AssistantCapabilityRequired([], [], ["grid.health.remediation.rollback.execute"])
            : existing.Finding?.CapabilityRequired;
        var finding = existing.Finding is null ? null : existing.Finding with { CapabilityRequired = required };
        var updated = existing with { Finding = finding };
        tasks = tasks.Select(task => task.Summary.Id == taskId ? updated : task).ToImmutableArray();
        return Task.FromResult(updated);
    }
}

sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
