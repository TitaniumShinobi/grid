using System.Collections.Immutable;
using System.Globalization;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.App.Services;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.App.Composition;

public sealed record GridCompositionRoot(
    GridApplicationMode Mode,
    IGridCatalogService CatalogService,
    IUserHistoryStore HistoryStore,
    IMo2InstallationValidator? Mo2Validator = null,
    Mo2OnboardingCoordinator? Mo2OnboardingCoordinator = null,
    Mo2DiscoveryOptions? Mo2DiscoveryOptions = null,
    IMo2SessionPathAuthorization? Mo2ProfileAuthorization = null,
    IMo2ModsPathAuthorization? Mo2ModsAuthorization = null,
    Mo2ResolvedStateService? Mo2ResolvedStateService = null,
    IWorkspaceToolOutputQueryService? Mo2ToolOutputQueryService = null,
    IMo2PeIconReader? Mo2ExecutableIconReader = null,
    IFidelityAuditService? FidelityAuditService = null,
    IWorkspaceLaunchService? WorkspaceLaunchService = null,
    IAssistantRequestExecutionService? AssistantRequestExecutionService = null,
    LocalWorkspaceSelectionStore? WorkspaceSelectionStore = null,
    LocalWorkspacePresentationStore? WorkspacePresentationStore = null,
    LocalSourceAcquisitionPreferencesStore? SourceAcquisitionPreferencesStore = null,
    INexusCredentialStore? NexusCredentialStore = null,
    NexusRecoverySourceDownloader? NexusRecoverySourceDownloader = null,
    LocalFirstRunStateStore? FirstRunStateStore = null,
    IMo2InstallationReferenceStore? Mo2ReferenceStore = null,
    Mo2ModStateMutationService? Mo2ModStateMutationService = null,
    IOfflineAlertIndexStore? OfflineAlertIndexStore = null,
    IGameInstallationRegistrationStore? GameRegistrationStore = null,
    IVortexInstallationConnectionStore? VortexConnectionStore = null,
    GridProviderDiscoveryService? ProviderDiscoveryService = null,
    GridProfileEnvironmentResolver? ProfileEnvironmentResolver = null,
    GridExistingProfileDiscoveryService? ExistingProfileDiscoveryService = null,
    IUserToolConfigurationStore? UserToolConfigurationStore = null,
    IInstalledToolKnowledgeStore? InstalledToolKnowledgeStore = null,
    IInstalledToolIdentityService? InstalledToolIdentityService = null,
    IUserToolLaunchService? UserToolLaunchService = null,
    CanonicalCatalogRuntimeService? CanonicalCatalogRuntimeService = null,
    CanonicalRegistrationRefreshService? CanonicalRegistrationRefreshService = null)
{
    public bool IsDemo => Mode == GridApplicationMode.Demo;

    public static GridCompositionRoot CreateDemo() => new(
        GridApplicationMode.Demo,
        new MockGridCatalogService(),
        new InMemoryUserHistoryStore());

    /// <summary>
    /// Creates the state-free Production shell used before authentication.
    /// Account-owned services must not be composed until a stable account
    /// identity is available.
    /// </summary>
    public static GridCompositionRoot CreateProductionShell() => new(
        GridApplicationMode.Production,
        new ProductionGridCatalogService(),
        new InMemoryUserHistoryStore());

    public static GridCompositionRoot CreateProduction(string stableAccountId)
    {
        var fileSystem = new Mo2FileSystem();
        var pathCanonicalizer = new WindowsPathCanonicalizer();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var globalInstancesRoot = Path.Combine(localApplicationData, "ModOrganizer");
        var baseDataRoot = GridAccountDataScope.ResolveBaseDataRoot();
        var gridDataRoot = GridAccountDataScope.Resolve(baseDataRoot, stableAccountId);
        var referenceStore = new Mo2InstallationReferenceStore(
            fileSystem,
            pathCanonicalizer,
            Path.Combine(gridDataRoot, "connections", "mo2-installations.v1.json"));
        var validator = new Mo2InstallationValidator(
            fileSystem,
            new Mo2IniReader(fileSystem),
            pathCanonicalizer,
            globalInstancesRoot);
        var discovery = new Mo2DiscoveryService(
            new WindowsMo2EvidenceSource(fileSystem, pathCanonicalizer),
            referenceStore,
            pathCanonicalizer);
        var connection = new Mo2ConnectionService(validator, referenceStore, pathCanonicalizer);
        var profileAuthorization = new Mo2SessionPathAuthorization(pathCanonicalizer);
        var profileSnapshots = new Mo2ProfileSnapshotService(
            fileSystem,
            new Mo2TextDecoder(),
            pathCanonicalizer,
            profileAuthorization);
        var modsAuthorization = new Mo2SessionModsPathAuthorization(pathCanonicalizer);
        var inventories = new Mo2ModInventoryService(
            fileSystem,
            new Mo2TextDecoder(),
            pathCanonicalizer,
            modsAuthorization);
        var contentAuthorization = new Mo2SessionContentPathAuthorization(pathCanonicalizer);
        var contentObserver = new Mo2ContentTreeObserver(pathCanonicalizer);
        var randomAccessFiles = new WindowsRandomAccessFileFactory();
        var resolvedCache = new Mo2ResolvedStateCache();
        var executableAuthorization = new Mo2SessionExecutablePathAuthorization(pathCanonicalizer);
        var onboarding = new Mo2OnboardingCoordinator(
            discovery,
            connection,
            _ => validator,
            profileAuthorization,
            modsAuthorization,
            contentAuthorization,
            executableAuthorization,
            pathCanonicalizer);
        var resolvedService = new Mo2ResolvedStateService(
            referenceStore,
            onboarding.CreateAuthorizedValidator,
            profileSnapshots,
            inventories,
            contentObserver,
            randomAccessFiles,
            new Mo2PluginHeaderParser(),
            new Mo2BsaIndexParser(),
            contentAuthorization,
            pathCanonicalizer,
            fileSystem,
            new Mo2TextDecoder(),
            resolvedCache,
            new Mo2VirtualDataResolver(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition"));
        var executableConfigurations = new Mo2ExecutableConfigurationService(
            fileSystem,
            pathCanonicalizer,
            new Mo2TextDecoder(),
            executableAuthorization);
        var outputSnapshots = new Mo2ToolOutputSnapshotCache(new Mo2ToolOutputService(
            contentObserver,
            randomAccessFiles,
            pathCanonicalizer,
            contentAuthorization));
        var toolOutputs = new Mo2WorkspaceToolOutputQueryService(
            referenceStore,
            onboarding.CreateAuthorizedValidator,
            profileSnapshots,
            inventories,
            executableConfigurations,
            outputSnapshots);
        var gameRegistrationStore = new JsonGameInstallationRegistrationStore(
            Path.Combine(gridDataRoot, "connections", "game-installations.v1.json"));
        var vortexConnectionStore = new JsonVortexInstallationConnectionStore(
            Path.Combine(gridDataRoot, "connections", "vortex-installations.v1.json"));
        var supportedGames = new VortexSupportedGameCatalogService(new ProductionGridCatalogService());
        var registeredGames = new RegisteredGameCatalogService(supportedGames, gameRegistrationStore);
        var vortexGames = new VortexCatalogService(registeredGames, vortexConnectionStore);
        var catalog = new Mo2CatalogService(
            vortexGames,
            referenceStore,
            onboarding.CreateAuthorizedValidator,
            profileSnapshots,
            inventories,
            resolvedCache,
            toolOutputs,
            enforceProductionIsolation: true);
        var requestExecution = new PowerShellAssistantRequestExecutionService(
            Path.Combine(AppContext.BaseDirectory, "RequestEngine"), gridDataRoot);
        var modStateMutation = new Mo2ModStateMutationService(
            referenceStore, Path.Combine(AppContext.BaseDirectory, "RequestEngine"), gridDataRoot);
        var workspaceSelectionStore = new LocalWorkspaceSelectionStore(
            Path.Combine(gridDataRoot, "workspace", "selection.v1.json"));
        var workspacePresentationStore = new LocalWorkspacePresentationStore(
            Path.Combine(gridDataRoot, "workspace", "presentation.v1.json"));
        var sourceAcquisitionPreferencesStore = new LocalSourceAcquisitionPreferencesStore(
            Path.Combine(gridDataRoot, "preferences", "source-acquisition.v1.json"));
        var nexusCredentialStore = new NexusCredentialStore();
        var nexusRecoverySourceDownloader = new NexusRecoverySourceDownloader(
            nexusCredentialStore, referenceStore, onboarding);
        var firstRunStateStore = new LocalFirstRunStateStore(
            Path.Combine(gridDataRoot, "setup", "first-run.v1.json"));
        var offlineAlertIndexStore = new LocalOfflineAlertIndexStore(
            Path.Combine(gridDataRoot, "alerts", "offline-index"));
        var providerDiscoveryService = new GridProviderDiscoveryService(
            Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts", "health", "Invoke-GridProviderDiscovery.ps1"),
            Path.Combine(gridDataRoot, "discovery", "provider-scan.v1.json"));
        var mo2DiscoveryOptions = new Mo2DiscoveryOptions(
            localApplicationData, systemDriveRoot, ProductionGridCatalogService.SkyrimSpecialEditionId);
        var profileEnvironmentResolver = new GridProfileEnvironmentResolver(
            new Mo2ProfileEnvironmentResolutionService(
                discovery,
                validator,
                profileSnapshots,
                inventories,
                profileAuthorization,
                modsAuthorization,
                pathCanonicalizer,
                fileSystem),
            mo2DiscoveryOptions);
        var existingProfileDiscoveryService = new GridExistingProfileDiscoveryService([
            new Mo2ExistingProfileDiscoveryAdapter(profileEnvironmentResolver, pathCanonicalizer),
        ]);
        var userToolConfigurationStore = new JsonUserToolConfigurationStore(
            Path.Combine(gridDataRoot, "connections", "tool-launch-configurations.v1.json"));
        var installedToolKnowledgeStore = new JsonInstalledToolKnowledgeStore(
            Path.Combine(gridDataRoot, "evidence", "tool-identity-resolutions.v1.json"));
        var installedToolIdentityService = new PowerShellInstalledToolIdentityService(
            Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts", "health", "Get-GridInstalledToolIdentity.ps1"));
        var userToolLaunchService = new WindowsUserToolLaunchService();
        var canonicalCatalogRuntimeService = CreateCanonicalCatalogRuntimeService(baseDataRoot);
        var uiCulture = CultureInfo.CurrentUICulture;
        var fallbackTags = uiCulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create("en");
        var terminologyLocale = new CanonicalTerminologyLocalePreference(uiCulture.Name, fallbackTags);
        var canonicalRegistrationRefreshService = new CanonicalRegistrationRefreshService(baseDataRoot, terminologyLocale);

        return new(
            GridApplicationMode.Production,
            catalog,
            new LocalUserHistoryStore(Path.Combine(gridDataRoot, "history", "history.v1.json")),
            validator,
            onboarding,
            mo2DiscoveryOptions,
            profileAuthorization,
            modsAuthorization,
            resolvedService,
            toolOutputs,
            new Mo2PeIconReader(fileSystem),
            AssistantRequestExecutionService: requestExecution,
            WorkspaceSelectionStore: workspaceSelectionStore,
            WorkspacePresentationStore: workspacePresentationStore,
            SourceAcquisitionPreferencesStore: sourceAcquisitionPreferencesStore,
            NexusCredentialStore: nexusCredentialStore,
            NexusRecoverySourceDownloader: nexusRecoverySourceDownloader,
            FirstRunStateStore: firstRunStateStore,
            Mo2ReferenceStore: referenceStore,
            Mo2ModStateMutationService: modStateMutation,
            OfflineAlertIndexStore: offlineAlertIndexStore,
            GameRegistrationStore: gameRegistrationStore,
            VortexConnectionStore: vortexConnectionStore,
            ProviderDiscoveryService: providerDiscoveryService,
            ProfileEnvironmentResolver: profileEnvironmentResolver,
            ExistingProfileDiscoveryService: existingProfileDiscoveryService,
            UserToolConfigurationStore: userToolConfigurationStore,
            InstalledToolKnowledgeStore: installedToolKnowledgeStore,
            InstalledToolIdentityService: installedToolIdentityService,
            UserToolLaunchService: userToolLaunchService,
            CanonicalCatalogRuntimeService: canonicalCatalogRuntimeService,
            CanonicalRegistrationRefreshService: canonicalRegistrationRefreshService);
    }

    private static CanonicalCatalogRuntimeService CreateCanonicalCatalogRuntimeService(string baseDataRoot)
    {
        // Canonical packages are shared machine-local knowledge, independent of account-owned state.
        var catalogsRoot = Path.Combine(baseDataRoot, "catalogs");
        Directory.CreateDirectory(catalogsRoot);
        var catalogStorePath = Environment.GetEnvironmentVariable("GRID_CANONICAL_CATALOG_PATH");
        if (string.IsNullOrWhiteSpace(catalogStorePath))
            catalogStorePath = Path.Combine(catalogsRoot, CanonicalCatalogRuntimeBindingStore.DefaultCatalogStoreFileName);
        else
            catalogStorePath = Path.GetFullPath(catalogStorePath);

        var binding = new CanonicalCatalogRuntimeBindingStore(
            Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json")).Load();
        var allowCandidate = ParseOptionalBooleanEnvironment("GRID_CANONICAL_ALLOW_CANDIDATE") ??
                             binding?.AllowCandidatePackages ??
                             false;
        CatalogPackageId? pinnedPackageId = null;
        var packageOverride = Environment.GetEnvironmentVariable("GRID_CANONICAL_PACKAGE_ID");
        if (!string.IsNullOrWhiteSpace(packageOverride))
            pinnedPackageId = new CatalogPackageId(packageOverride);
        else if (binding is not null)
            pinnedPackageId = binding.PackageId;

        var uiCulture = CultureInfo.CurrentUICulture;
        var fallbackTags = uiCulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? ImmutableArray<string>.Empty
            : ImmutableArray.Create("en");
        var terminologyLocale = new CanonicalTerminologyLocalePreference(uiCulture.Name, fallbackTags);
        return new CanonicalCatalogRuntimeService(
            catalogStorePath,
            terminologyLocale,
            allowCandidate,
            pinnedPackageId,
            runtimeBindingPath: Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json"));
    }

    private static bool? ParseOptionalBooleanEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Environment variable '{name}' must be 'True' or 'False'.");
    }
}
