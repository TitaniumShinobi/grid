using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Deterministic fixtures for the explicit Demo composition only. Production must never
/// wrap, persist, or merge this catalog with connected installation references.
/// </summary>
public sealed class MockGridCatalogService : IGridCatalogService
{
    private static readonly GameId SkyrimGameId = new("game.skyrim-special-edition");
    private static readonly GameId GtaGameId = new("game.grand-theft-auto-v");
    private static readonly GameAdapterId Mo2AdapterId = new("adapter.mock.mod-organizer-2");
    private static readonly GameAdapterId ConnectedMo2AdapterId = new("adapter.mod-organizer-2");
    private static readonly GameAdapterId DeploymentAdapterId = new("adapter.mock.deployment");
    private static readonly GameAdapterId GtaAdapterId = new("adapter.mock.gta-preview");
    private static readonly GridCatalogSnapshot Catalog = CreateCatalog();

    public Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Catalog);
    }

    private static GridCatalogSnapshot CreateCatalog()
    {
        var fullSkyrimCapabilities = new WorkspaceCapabilities(
            WorkspaceFeature.Profiles |
            WorkspaceFeature.ModList |
            WorkspaceFeature.Health |
            WorkspaceFeature.Tools |
            WorkspaceFeature.LaunchTargets,
            EnvironmentTabCapability.Plugins |
            EnvironmentTabCapability.Archives |
            EnvironmentTabCapability.Data |
            EnvironmentTabCapability.Saves |
            EnvironmentTabCapability.Downloads |
            EnvironmentTabCapability.Conflicts |
            EnvironmentTabCapability.Outputs |
            EnvironmentTabCapability.Activity);

        var deploymentCapabilities = new WorkspaceCapabilities(
            WorkspaceFeature.Profiles |
            WorkspaceFeature.ModList |
            WorkspaceFeature.Health |
            WorkspaceFeature.Tools |
            WorkspaceFeature.LaunchTargets,
            EnvironmentTabCapability.Data |
            EnvironmentTabCapability.Saves |
            EnvironmentTabCapability.Downloads |
            EnvironmentTabCapability.Conflicts |
            EnvironmentTabCapability.Activity);

        var undefeatedId = new InstallationId("installation.skyrim.undefeated");
        var nefarammId = new InstallationId("installation.skyrim.nefaram");
        var customId = new InstallationId("installation.skyrim.custom");
        var skyrimToolCatalog = CreateSkyrimToolCatalog();
        var gtaToolCatalog = CreateGtaToolCatalog();

        var undefeated = new ManagedInstallation(
            undefeatedId,
            SkyrimGameId,
            Mo2AdapterId,
            "UNDEFEATED",
            InstallationKind.External,
            WorkspaceAccessMode.ReadOnly,
            fullSkyrimCapabilities,
            ImmutableArray.Create(
                new Profile(
                    new ProfileId("profile.undefeated.default"),
                    undefeatedId,
                    "Default",
                    ImmutableArray.Create(
                        MockSeparator("mod.undefeated.separator.core", "CORE & FRAMEWORKS", 0),
                        MockMod("mod.undefeated.address-library", "Address Library", "11.0.0", true, 1, "Framework"),
                        MockMod("mod.undefeated.skse", "SKSE Runtime", "2.2.6", true, 2, "Framework"),
                        MockMod("mod.undefeated.skyui", "SkyUI", "5.2", true, 3, "Interface", "5.3-mock", ModUpdateState.UpdateAvailable, ModConflictState.Overwrites),
                        MockSeparator("mod.undefeated.separator.combat", "COMBAT", 4),
                        MockMod("mod.undefeated.mco", "Modern Combat Overhaul", "1.6.0", true, 5, "Combat", null, ModUpdateState.Current, ModConflictState.Mixed),
                        MockMod("mod.undefeated.combat-output", "Generated Combat Output", "2026.08", true, 6, "Generated", null, ModUpdateState.Current, ModConflictState.Overwritten, HealthLevel.Advisory),
                        MockSeparator("mod.undefeated.separator.visuals", "VISUALS", 7),
                        MockMod("mod.undefeated.community-shaders", "Community Shaders", "0.8.7", true, 8, "Visuals", "0.8.8-mock", ModUpdateState.UpdateAvailable),
                        MockMod("mod.undefeated.high-poly", "High Poly Project", "4.0", false, 9, "Visuals", null, ModUpdateState.Ignored, ModConflictState.Overwritten)),
                    ImmutableArray.Create(
                        new PluginEntry(new PluginId("plugin.undefeated.skyrim"), "Skyrim.esm", true, 0, HealthLevel.Healthy),
                        new PluginEntry(new PluginId("plugin.undefeated.update"), "Update.esm", true, 1, HealthLevel.Healthy)),
                    AdvisoryHealth("Launch-ready with advisories", "mock.skse-review", "SKSE compatibility review", "One represented DLL awaits compatibility confirmation."),
                    ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
                    ProfileLifecycleState.Available,
                    CreateFullEnvironment(
                        "undefeated.default",
                        new ModId("mod.undefeated.skyui"),
                        new ModId("mod.undefeated.combat-output"),
                        includeDownloads: false),
                    new LaunchTargetId("launch.skyrim.skse"),
                    new ProfileLaunchReadiness(
                        ProfileValidationState.Advisory,
                        PendingChangeState.None,
                        PendingChangeState.Pending,
                        OnlineCleanVerificationState.NotApplicable)),
                new Profile(
                    new ProfileId("profile.undefeated.performance"),
                    undefeatedId,
                    "Performance",
                    ImmutableArray.Create(
                        MockSeparator("mod.undefeated.performance.separator.core", "CORE", 0),
                        MockMod("mod.undefeated.performance.address-library", "Address Library", "11.0.0", true, 1, "Framework"),
                        MockMod("mod.undefeated.performance.skyui", "SkyUI", "5.2", true, 2, "Interface", null, ModUpdateState.Current, ModConflictState.Overwrites),
                        MockSeparator("mod.undefeated.performance.separator.tuning", "PERFORMANCE TUNING", 3),
                        MockMod("mod.undefeated.performance.upscaler", "Skyrim Upscaler", "1.1", true, 4, "Performance"),
                        MockMod("mod.undefeated.performance.visuals", "High-cost Visuals", "1.0", false, 5, "Visuals", null, ModUpdateState.Ignored, ModConflictState.Overwritten)),
                    ImmutableArray.Create(
                        new PluginEntry(new PluginId("plugin.undefeated.performance.skyrim"), "Skyrim.esm", true, 0, HealthLevel.Healthy),
                        new PluginEntry(new PluginId("plugin.undefeated.performance.update"), "Update.esm", true, 1, HealthLevel.Healthy)),
                    Healthy("Profile represented healthy"),
                    ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
                    ProfileLifecycleState.Available,
                    CreateFullEnvironment(
                        "undefeated.performance",
                        new ModId("mod.undefeated.performance.skyui"),
                        new ModId("mod.undefeated.performance.visuals"),
                        includeDownloads: true),
                    new LaunchTargetId("launch.skyrim.game"),
                    new ProfileLaunchReadiness(
                        ProfileValidationState.Valid,
                        PendingChangeState.None,
                        PendingChangeState.None,
                        OnlineCleanVerificationState.NotApplicable)),
                new Profile(
                    new ProfileId("profile.undefeated.legacy-benchmark"),
                    undefeatedId,
                    "Legacy Benchmark",
                    ImmutableArray<ModEntry>.Empty,
                    ImmutableArray<PluginEntry>.Empty,
                    new HealthSummary(HealthLevel.Unknown, "Archived mock profile", ImmutableArray<Advisory>.Empty),
                    ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
                    ProfileLifecycleState.Archived,
                    ImmutableArray<EnvironmentEntry>.Empty,
                    null,
                    new ProfileLaunchReadiness(
                        ProfileValidationState.Unknown,
                        PendingChangeState.Unknown,
                        PendingChangeState.Unknown,
                        OnlineCleanVerificationState.NotApplicable))),
            CreateMo2ToolConfigurations(includeAllBuildTools: true),
            CreateMo2LaunchConfigurations(includeAllBuildTools: true),
            AdvisoryHealth("Launch-ready with advisories", "mock.installation-review", "Installation review available", "Two represented advisories remain read-only."),
            ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
            new InstallationMetadata(
                @"X:\MockExternal\UNDEFEATED",
                InstallationAvailability.Available,
                "Represented external portable MO2 installation is available to the mock catalog.",
                InstallationProvenanceKind.Mock));

        var nefaramm = new ManagedInstallation(
            nefarammId,
            SkyrimGameId,
            Mo2AdapterId,
            "NEFARAM",
            InstallationKind.External,
            WorkspaceAccessMode.ReadOnly,
            fullSkyrimCapabilities,
            ImmutableArray.Create(
                new Profile(
                    new ProfileId("profile.nefaram.default"),
                    nefarammId,
                    "Default",
                    ImmutableArray.Create(
                        MockSeparator("mod.nefaram.separator.core", "FOUNDATIONS", 0),
                        MockMod("mod.nefaram.address-library", "Address Library", "11.0.0", true, 1, "Framework"),
                        MockMod("mod.nefaram.interface", "Interface Foundation", "2.4", true, 2, "Interface", "2.5-mock", ModUpdateState.UpdateAvailable),
                        MockSeparator("mod.nefaram.separator.gameplay", "GAMEPLAY", 3),
                        MockMod("mod.nefaram.survival", "Survival Foundation", "3.1", true, 4, "Gameplay", null, ModUpdateState.Current, ModConflictState.Mixed),
                        MockMod("mod.nefaram.weather", "Northern Weather", "1.8", true, 5, "Visuals", null, ModUpdateState.Current, ModConflictState.Overwrites)),
                    ImmutableArray.Create(
                        new PluginEntry(new PluginId("plugin.nefaram.skyrim"), "Skyrim.esm", true, 0, HealthLevel.Healthy),
                        new PluginEntry(new PluginId("plugin.nefaram.update"), "Update.esm", true, 1, HealthLevel.Healthy)),
                    Healthy("Profile represented healthy"),
                    ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
                    ProfileLifecycleState.Available,
                    CreateFullEnvironment(
                        "nefaram.default",
                        new ModId("mod.nefaram.interface"),
                        new ModId("mod.nefaram.survival"),
                        includeDownloads: false),
                    new LaunchTargetId("launch.skyrim.skse"),
                    new ProfileLaunchReadiness(
                        ProfileValidationState.Valid,
                        PendingChangeState.None,
                        PendingChangeState.None,
                        OnlineCleanVerificationState.NotApplicable))),
            CreateMo2ToolConfigurations(includeAllBuildTools: false),
            CreateMo2LaunchConfigurations(includeAllBuildTools: false),
            Healthy("Installation represented healthy"),
            ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
            new InstallationMetadata(
                @"X:\MockExternal\NEFARAM",
                InstallationAvailability.Available,
                "Represented external portable MO2 installation is available to the mock catalog.",
                InstallationProvenanceKind.Mock));

        var custom = new ManagedInstallation(
            customId,
            SkyrimGameId,
            DeploymentAdapterId,
            "Custom",
            InstallationKind.External,
            WorkspaceAccessMode.ReadOnly,
            deploymentCapabilities,
            ImmutableArray.Create(
                new Profile(
                    new ProfileId("profile.custom.default"),
                    customId,
                    "Default",
                    ImmutableArray.Create(
                        MockMod("mod.custom.example", "Example Mod", "1.0", true, 0, "Uncategorized", null, ModUpdateState.Unknown, ModConflictState.Unknown, HealthLevel.Unknown)),
                    ImmutableArray<PluginEntry>.Empty,
                    new HealthSummary(HealthLevel.Unknown, "Awaiting a future adapter", ImmutableArray<Advisory>.Empty),
                    ProfileFeature.Saves,
                    ProfileLifecycleState.Available,
                    ImmutableArray<EnvironmentEntry>.Empty,
                    new LaunchTargetId("launch.skyrim.game"),
                    new ProfileLaunchReadiness(
                        ProfileValidationState.Unknown,
                        PendingChangeState.Unknown,
                        PendingChangeState.Unknown,
                        OnlineCleanVerificationState.NotApplicable))),
            ImmutableArray.Create(
                new ToolConfiguration(
                    new ToolId("tool.skyrim.deployment"),
                    AvailabilityState.PreviewOnly,
                    null,
                    MockCommand(ConfiguredPathAnchor.ManagerRoot, "deployment\\GridDeploymentPreview.exe", "deployment", EnvironmentPolicy.AdapterManaged))),
            ImmutableArray.Create(
                GameLaunchConfiguration("launch.skyrim.game", "SkyrimSE.exe", []),
                ToolLaunchConfiguration("launch.skyrim.deployment", []),
                InternalLaunchConfiguration("launch.skyrim.diagnostics")),
            new HealthSummary(HealthLevel.Unknown, "Read-only mock installation", ImmutableArray<Advisory>.Empty),
            ProfileFeature.Saves,
            new InstallationMetadata(
                null,
                InstallationAvailability.Missing,
                "No authoritative location is represented for this missing external installation.",
                InstallationProvenanceKind.Mock));

        var skyrim = new ManagedGame(
            SkyrimGameId,
            "Skyrim Special Edition",
            fullSkyrimCapabilities,
            ImmutableArray.Create(
                new GameAdapterIdentity(Mo2AdapterId, "Mod Organizer 2"),
                new GameAdapterIdentity(ConnectedMo2AdapterId, "Mod Organizer 2 · Connected reference"),
                new GameAdapterIdentity(DeploymentAdapterId, "Deployment Preview")),
            ImmutableArray.Create(undefeated, nefaramm, custom),
            skyrimToolCatalog,
            AdvisoryHealth("Two represented advisories", "mock.skyrim-health", "Review represented Skyrim state", "Mock health evidence is available for review."));

        var gta = new ManagedGame(
            GtaGameId,
            "Grand Theft Auto V",
            new WorkspaceCapabilities(
                WorkspaceFeature.Health |
                WorkspaceFeature.Tools |
                WorkspaceFeature.LaunchTargets,
                EnvironmentTabCapability.Activity),
            ImmutableArray.Create(new GameAdapterIdentity(GtaAdapterId, "GTA Preview")),
            ImmutableArray<ManagedInstallation>.Empty,
            gtaToolCatalog,
            new HealthSummary(HealthLevel.Unknown, "No runtime adapter connected", ImmutableArray<Advisory>.Empty));

        return new GridCatalogSnapshot(
            "mock-catalog-v4",
            CatalogSourceKind.Mock,
            [skyrim, gta]);
    }

    private static GameToolCatalog CreateSkyrimToolCatalog()
    {
        var mo2Only = ImmutableArray.Create(Mo2AdapterId);
        var deploymentOnly = ImmutableArray.Create(DeploymentAdapterId);
        var allSkyrimAdapters = ImmutableArray.Create(Mo2AdapterId, DeploymentAdapterId);
        var standard = StandardSafetyPolicy();

        return new GameToolCatalog(
            ImmutableArray.Create(
                new ToolDefinition(new ToolId("tool.skyrim.mo2"), "Mod Organizer 2", "Represented portable manager utility.", ToolKind.ExternalUtility, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.bodyslide"), "BodySlide", "Represented body and outfit generation utility.", ToolKind.ExternalUtility, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.sseedit"), "SSEEdit", "Represented plugin inspection utility shared by multiple targets.", ToolKind.ExternalUtility, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.loot"), "LOOT", "Represented load-order review utility.", ToolKind.ExternalUtility, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.pandora"), "Pandora", "Represented behavior build tool.", ToolKind.BuildTool, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.synthesis"), "Synthesis", "Represented patch build tool.", ToolKind.BuildTool, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.texgen"), "TexGen", "Represented terrain-texture build tool.", ToolKind.BuildTool, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.dyndolod"), "DynDOLOD", "Represented distant-object build tool.", ToolKind.BuildTool, mo2Only),
                new ToolDefinition(new ToolId("tool.skyrim.deployment"), "Deployment Preview", "Represented deployment-aware utility.", ToolKind.BuildTool, deploymentOnly)),
            ImmutableArray.Create(
                Target("launch.skyrim.game", "Skyrim Special Edition", LaunchTargetKind.Game, allSkyrimAdapters, standard),
                Target("launch.skyrim.skse", "SKSE", LaunchTargetKind.Game, mo2Only, standard),
                ToolTarget("launch.skyrim.mo2", "Mod Organizer 2", "tool.skyrim.mo2", mo2Only, standard),
                ToolTarget("launch.skyrim.bodyslide", "BodySlide", "tool.skyrim.bodyslide", mo2Only, standard),
                ToolTarget("launch.skyrim.sseedit", "SSEEdit", "tool.skyrim.sseedit", mo2Only, standard),
                ToolTarget("launch.skyrim.sseedit-view", "SSEEdit · View-only mock", "tool.skyrim.sseedit", mo2Only, standard),
                ToolTarget("launch.skyrim.loot", "LOOT", "tool.skyrim.loot", mo2Only, standard),
                ToolTarget("launch.skyrim.pandora", "Pandora", "tool.skyrim.pandora", mo2Only, standard),
                ToolTarget("launch.skyrim.synthesis", "Synthesis", "tool.skyrim.synthesis", mo2Only, standard),
                ToolTarget("launch.skyrim.texgen", "TexGen", "tool.skyrim.texgen", mo2Only, standard),
                ToolTarget("launch.skyrim.dyndolod", "DynDOLOD", "tool.skyrim.dyndolod", mo2Only, standard),
                ToolTarget("launch.skyrim.deployment", "Deployment Preview", "tool.skyrim.deployment", deploymentOnly, standard),
                new LaunchTargetDefinition(
                    new LaunchTargetId("launch.skyrim.diagnostics"),
                    "Grid diagnostics",
                    LaunchTargetKind.GridInternal,
                    null,
                    GridInternalRoute.Diagnostics,
                    allSkyrimAdapters,
                    InternalSafetyPolicy())));
    }

    private static GameToolCatalog CreateGtaToolCatalog()
    {
        var gtaAdapters = ImmutableArray.Create(GtaAdapterId);
        var standard = StandardSafetyPolicy();
        var online = standard with { RequiresOnlineCleanVerification = true };

        return new GameToolCatalog(
            ImmutableArray.Create(
                new ToolDefinition(new ToolId("tool.gta.deployment"), "GTA deployment tools", "Future adapter-owned deployment tooling.", ToolKind.BuildTool, gtaAdapters),
                new ToolDefinition(new ToolId("tool.gta.openiv"), "OpenIV", "Future represented Story Mode utility.", ToolKind.ExternalUtility, gtaAdapters)),
            ImmutableArray.Create(
                Target("launch.gta.story", "GTA V · Story Mode", LaunchTargetKind.Game, gtaAdapters, standard),
                Target("launch.gta.online-verified", "GTA Online · verification required", LaunchTargetKind.Game, gtaAdapters, online),
                ToolTarget("launch.gta.deployment", "GTA deployment tools", "tool.gta.deployment", gtaAdapters, standard),
                ToolTarget("launch.gta.openiv", "OpenIV · Story Mode tooling", "tool.gta.openiv", gtaAdapters, standard),
                new LaunchTargetDefinition(
                    new LaunchTargetId("launch.gta.diagnostics"),
                    "Grid GTA diagnostics",
                    LaunchTargetKind.GridInternal,
                    null,
                    GridInternalRoute.Diagnostics,
                    gtaAdapters,
                    InternalSafetyPolicy())));
    }

    private static ImmutableArray<ToolConfiguration> CreateMo2ToolConfigurations(bool includeAllBuildTools)
    {
        var configurations = new List<ToolConfiguration>
        {
            PreviewTool("tool.skyrim.mo2", ConfiguredPathAnchor.ManagerRoot, "ModOrganizer.exe", "portable", EnvironmentPolicy.AdapterManaged),
            PreviewTool("tool.skyrim.bodyslide", ConfiguredPathAnchor.GameRoot, "Data\\CalienteTools\\BodySlide\\BodySlide x64.exe", "Data\\CalienteTools\\BodySlide", EnvironmentPolicy.AdapterManaged),
            PreviewTool("tool.skyrim.sseedit", ConfiguredPathAnchor.ManagerRoot, "tools\\SSEEdit\\SSEEdit.exe", "tools\\SSEEdit", EnvironmentPolicy.RestrictedInherited),
            PreviewTool("tool.skyrim.loot", ConfiguredPathAnchor.ManagerRoot, "tools\\LOOT\\LOOT.exe", "tools\\LOOT", EnvironmentPolicy.RestrictedInherited),
        };

        if (includeAllBuildTools)
        {
            configurations.Add(PreviewTool("tool.skyrim.pandora", ConfiguredPathAnchor.GameRoot, "Data\\Pandora_Engine\\Pandora Behaviour Engine+.exe", "Data\\Pandora_Engine", EnvironmentPolicy.AdapterManaged));
            configurations.Add(PreviewTool("tool.skyrim.synthesis", ConfiguredPathAnchor.ManagerRoot, "tools\\Synthesis\\Synthesis.exe", "tools\\Synthesis", EnvironmentPolicy.AdapterManaged));
            configurations.Add(PreviewTool("tool.skyrim.texgen", ConfiguredPathAnchor.ManagerRoot, "tools\\DynDOLOD\\TexGenx64.exe", "tools\\DynDOLOD", EnvironmentPolicy.AdapterManaged));
            configurations.Add(new ToolConfiguration(
                new ToolId("tool.skyrim.dyndolod"),
                AvailabilityState.Unavailable,
                "Represented DynDOLOD executable configuration requires review.",
                null));
        }

        return configurations.ToImmutableArray();
    }

    private static ImmutableArray<LaunchTargetConfiguration> CreateMo2LaunchConfigurations(bool includeAllBuildTools)
    {
        var configurations = new List<LaunchTargetConfiguration>
        {
            GameLaunchConfiguration("launch.skyrim.game", "SkyrimSE.exe", []),
            GameLaunchConfiguration("launch.skyrim.skse", "skse64_loader.exe", [Argument("--mock-profile"), Argument("{ProfileName}")]),
            ToolLaunchConfiguration("launch.skyrim.mo2", [Argument("--mock-profile"), Argument("{ProfileName}")]),
            ToolLaunchConfiguration("launch.skyrim.bodyslide", [Argument("--mock-preview")]),
            ToolLaunchConfiguration("launch.skyrim.sseedit", [Argument("-IKnowWhatImDoing")]),
            ToolLaunchConfiguration("launch.skyrim.sseedit-view", [Argument("--view-only-mock")]),
            ToolLaunchConfiguration("launch.skyrim.loot", [Argument("--game=SkyrimSE"), Argument("--mock-preview")]),
            InternalLaunchConfiguration("launch.skyrim.diagnostics"),
        };

        if (includeAllBuildTools)
        {
            configurations.Add(ToolLaunchConfiguration("launch.skyrim.pandora", [Argument("--mock-build-preview")]));
            configurations.Add(ToolLaunchConfiguration("launch.skyrim.synthesis", [Argument("--mock-pipeline")]));
            configurations.Add(ToolLaunchConfiguration("launch.skyrim.texgen", [Argument("--mock-preview")]));
            configurations.Add(new LaunchTargetConfiguration(
                new LaunchTargetId("launch.skyrim.dyndolod"),
                AvailabilityState.Unavailable,
                "Represented DynDOLOD configuration is unavailable until reviewed.",
                null,
                ImmutableArray<CommandArgument>.Empty));
        }

        return configurations.ToImmutableArray();
    }

    private static LaunchTargetDefinition Target(
        string id,
        string name,
        LaunchTargetKind kind,
        ImmutableArray<GameAdapterId> adapters,
        LaunchSafetyPolicy policy) =>
        new(new LaunchTargetId(id), name, kind, null, null, adapters, policy);

    private static LaunchTargetDefinition ToolTarget(
        string id,
        string name,
        string toolId,
        ImmutableArray<GameAdapterId> adapters,
        LaunchSafetyPolicy policy) =>
        new(new LaunchTargetId(id), name, LaunchTargetKind.Tool, new ToolId(toolId), null, adapters, policy);

    private static ToolConfiguration PreviewTool(
        string toolId,
        ConfiguredPathAnchor anchor,
        string executable,
        string workingDirectory,
        EnvironmentPolicy environmentPolicy) =>
        new(new ToolId(toolId), AvailabilityState.PreviewOnly, null, MockCommand(anchor, executable, workingDirectory, environmentPolicy));

    private static LaunchTargetConfiguration GameLaunchConfiguration(
        string targetId,
        string executable,
        ImmutableArray<CommandArgument> arguments) =>
        new(
            new LaunchTargetId(targetId),
            AvailabilityState.PreviewOnly,
            null,
            MockCommand(ConfiguredPathAnchor.GameRoot, executable, "Data", EnvironmentPolicy.AdapterManaged),
            arguments);

    private static LaunchTargetConfiguration ToolLaunchConfiguration(
        string targetId,
        ImmutableArray<CommandArgument> arguments) =>
        new(new LaunchTargetId(targetId), AvailabilityState.PreviewOnly, null, null, arguments);

    private static LaunchTargetConfiguration InternalLaunchConfiguration(string targetId) =>
        new(
            new LaunchTargetId(targetId),
            AvailabilityState.PreviewOnly,
            null,
            null,
            ImmutableArray<CommandArgument>.Empty);

    private static ExecutableConfiguration MockCommand(
        ConfiguredPathAnchor anchor,
        string executable,
        string workingDirectory,
        EnvironmentPolicy policy) =>
        new(new ConfiguredPath(anchor, executable), new ConfiguredPath(anchor, workingDirectory), policy);

    private static CommandArgument Argument(string value) => new(value);

    private static LaunchSafetyPolicy StandardSafetyPolicy() =>
        new(true, true, true, false, true, true);

    private static LaunchSafetyPolicy InternalSafetyPolicy() =>
        new(false, false, false, false, false, false);

    private static ModEntry MockMod(
        string id,
        string name,
        string version,
        bool isEnabled,
        int priority,
        string category,
        string? availableVersion = null,
        ModUpdateState updateState = ModUpdateState.Current,
        ModConflictState conflictState = ModConflictState.None,
        HealthLevel health = HealthLevel.Healthy) =>
        new(
            new ModId(id),
            name,
            version,
            "Deterministic mock catalog",
            isEnabled,
            priority,
            health,
            ModEntryKind.Mod,
            category,
            availableVersion,
            updateState,
            conflictState);

    private static ModEntry MockSeparator(string id, string name, int priority) =>
        new(
            new ModId(id),
            name,
            string.Empty,
            string.Empty,
            false,
            priority,
            HealthLevel.Unknown,
            ModEntryKind.Separator,
            string.Empty,
            null,
            ModUpdateState.Unknown,
            ModConflictState.None);

    private static ImmutableArray<EnvironmentEntry> CreateFullEnvironment(
        string prefix,
        ModId primaryModId,
        ModId secondaryModId,
        bool includeDownloads)
    {
        var entries = new List<EnvironmentEntry>
        {
            MockEnvironment(prefix, "archive.core", EnvironmentTabCapability.Archives, "Core Assets.bsa", "Represented archive registered by the mock profile.", "Loaded · mock", HealthLevel.Healthy, primaryModId),
            MockEnvironment(prefix, "archive.interface", EnvironmentTabCapability.Archives, "Interface Assets.bsa", "Represented archive ordering evidence only.", "After Core Assets · mock", HealthLevel.Healthy, secondaryModId),
            MockEnvironment(prefix, "data.primary", EnvironmentTabCapability.Data, "meshes\\actors\\character", "Represented winning provider: selected mock mod.", "Virtual data · mock", HealthLevel.Advisory, primaryModId),
            MockEnvironment(prefix, "data.secondary", EnvironmentTabCapability.Data, "scripts\\GridMock.pex", "Synthetic data-tree entry; no file was enumerated.", "Represented only", HealthLevel.Healthy, secondaryModId),
            MockEnvironment(prefix, "save.recent", EnvironmentTabCapability.Saves, "Whiterun Gate · 12h 44m", "Represented profile-local save metadata.", "Compatible · mock", HealthLevel.Healthy),
            MockEnvironment(prefix, "conflict.primary", EnvironmentTabCapability.Conflicts, "Interface asset overlap", "Two represented providers target the same mock path.", "Review winner · mock", HealthLevel.Advisory, primaryModId, secondaryModId),
            MockEnvironment(prefix, "conflict.secondary", EnvironmentTabCapability.Conflicts, "Generated output overwrite", "Represented output intentionally wins this mock conflict.", "Expected · mock", HealthLevel.Healthy, secondaryModId),
            MockEnvironment(prefix, "output.generated", EnvironmentTabCapability.Outputs, "Generated behavior output", "Synthetic generated-output registration for this profile.", "Current · mock", HealthLevel.Healthy, secondaryModId),
            MockEnvironment(prefix, "activity.open", EnvironmentTabCapability.Activity, "Profile opened", "Deterministic activity entry at 2026-08-25 09:00.", "Read-only mock event", HealthLevel.Healthy),
            MockEnvironment(prefix, "activity.review", EnvironmentTabCapability.Activity, "Conflict review represented", "No files were read or changed by this activity entry.", "Read-only mock event", HealthLevel.Advisory, primaryModId),
        };

        if (includeDownloads)
        {
            entries.Add(MockEnvironment(
                prefix,
                "download.staged",
                EnvironmentTabCapability.Downloads,
                "Optional Performance Patch.7z",
                "Represented download metadata; no archive exists in Grid.",
                "Ready for future review · mock",
                HealthLevel.Unknown));
        }

        return entries.ToImmutableArray();
    }

    private static EnvironmentEntry MockEnvironment(
        string prefix,
        string suffix,
        EnvironmentTabCapability tab,
        string name,
        string detail,
        string status,
        HealthLevel health,
        params ModId[] relatedModIds) =>
        new(
            new EnvironmentEntryId($"environment.{prefix}.{suffix}"),
            tab,
            name,
            detail,
            status,
            health,
            relatedModIds.ToImmutableArray());

    private static HealthSummary Healthy(string label) =>
        new(HealthLevel.Healthy, label, ImmutableArray<Advisory>.Empty);

    private static HealthSummary AdvisoryHealth(string label, string code, string title, string detail) =>
        new(
            HealthLevel.Advisory,
            label,
            ImmutableArray.Create(new Advisory(code, title, detail, HealthLevel.Advisory)));
}
