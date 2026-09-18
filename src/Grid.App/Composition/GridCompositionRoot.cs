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
    GridProviderDiscoveryService? ProviderDiscoveryService = null)
{
    public bool IsDemo => Mode == GridApplicationMode.Demo;

    public static GridCompositionRoot CreateDemo() => new(
        GridApplicationMode.Demo,
        new MockGridCatalogService(),
        new InMemoryUserHistoryStore());

    public static GridCompositionRoot CreateProduction()
    {
        var fileSystem = new Mo2FileSystem();
        var pathCanonicalizer = new WindowsPathCanonicalizer();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var globalInstancesRoot = Path.Combine(localApplicationData, "ModOrganizer");
        var gridDataRoot = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(gridDataRoot))
        {
            gridDataRoot = Path.Combine(localApplicationData, "Grid");
        }
        else
        {
            gridDataRoot = Path.GetFullPath(gridDataRoot);
        }
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
        var registeredGames = new RegisteredGameCatalogService(new ProductionGridCatalogService(), gameRegistrationStore);
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

        return new(
            GridApplicationMode.Production,
            catalog,
            new LocalUserHistoryStore(Path.Combine(gridDataRoot, "history", "history.v1.json")),
            validator,
            onboarding,
            new(localApplicationData, systemDriveRoot, ProductionGridCatalogService.SkyrimSpecialEditionId),
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
            ProviderDiscoveryService: providerDiscoveryService);
    }
}
