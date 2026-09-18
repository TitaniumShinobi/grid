using Grid.App.Composition;
using Grid.App.Services;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class SettingsPage : Page
{
    private LocalSourceAcquisitionPreferencesStore? sourceAcquisitionPreferencesStore;
    private INexusCredentialStore? nexusCredentialStore;
    private bool bindingPreferences;

    public SettingsPage()
    {
        bindingPreferences = true;
        InitializeComponent();
        bindingPreferences = false;
    }

    public void BindContext(
        GridCatalogSnapshot catalog,
        FidelityAuditSnapshot? audit = null,
        ExternalLaunchSession? launch = null,
        GridApplicationMode applicationMode = GridApplicationMode.Production,
        Action<GridApplicationMode>? restartApplication = null,
        string? historyIssue = null,
        AssistantProviderAvailability assistantProvider = AssistantProviderAvailability.NotConfigured,
        LocalSourceAcquisitionPreferencesStore? acquisitionPreferencesStore = null,
        INexusCredentialStore? credentialStore = null)
    {
        sourceAcquisitionPreferencesStore = acquisitionPreferencesStore;
        nexusCredentialStore = credentialStore;
        bindingPreferences = true;
        AutomaticSourceDownloadsToggle.IsOn = acquisitionPreferencesStore?.Load().AutomaticallyDownloadVerifiedSources == true;
        bindingPreferences = false;
        UpdateAutomaticSourceDownloadStatus(saved: null);
        UpdateNexusCredentialStatus();
        var isDemo = applicationMode == GridApplicationMode.Demo;
        CatalogSourceText.Text = $"Source · {catalog.SourceKind}";
        CatalogRevisionText.Text = $"Revision · {catalog.Revision}";
        var installations = catalog.Games.Sum(game => game.Installations.Length);
        var profiles = catalog.Games.Sum(game => game.Installations.Sum(installation => installation.Profiles.Length));
        CatalogCountsText.Text = $"{catalog.Games.Length} game definition(s) · {installations} installation(s) · {profiles} profile observation(s)";

        var connected = catalog.Games.SelectMany(game => game.Installations)
            .Where(installation => installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference).ToArray();
        AdapterStateText.Text = isDemo
            ? "External adapters are not composed in the isolated development harness."
            : connected.Length == 0
                ? "No external installation is connected. Add a game from Home to begin onboarding."
                : "Connected references are represented through the read-only adapter boundary.";
        ReferenceCountText.Text = connected.Length switch
        {
            0 => "No persisted external-installation references",
            1 => "1 persisted external-installation reference",
            _ => $"{connected.Length} persisted external-installation references",
        };

        var observedProfiles = connected.SelectMany(installation => installation.Profiles)
            .Where(profile => profile.Observation is not null).ToArray();
        var complete = observedProfiles.Count(profile => profile.Observation?.Status == ProfileObservationStatus.Complete);
        var active = observedProfiles.Count(profile => profile.Observation?.ManagerState == ManagerProfileState.Active);
        var inventories = observedProfiles.Count(profile => profile.Observation?.Inventory is not null);
        var warnings = observedProfiles.Sum(profile => profile.Observation?.Inventory?.WarningCount ?? 0);
        ObservedProfilesText.Text = observedProfiles.Length == 0
            ? "No external profile snapshot is available."
            : $"Profiles · {observedProfiles.Length} observed · {complete} complete · {active} active in the external manager · {inventories} inventories · {warnings} warning(s)";

        var toolOutputs = observedProfiles.Select(profile => profile.Observation?.ToolOutputs)
            .OfType<ToolOutputObservationSummary>().ToArray();
        ObservedToolsOutputsText.Text = toolOutputs.Length == 0
            ? "No executable or generated-output observation is retained for the current session."
            : $"Executables · {toolOutputs.Max(summary => summary.ExecutableCount)} observed · Outputs · {toolOutputs.Sum(summary => summary.OutputCount)} observed · Warnings · {toolOutputs.Sum(summary => summary.WarningCount)}";
        FidelityStateText.Text = audit is null ? "Fidelity audit · No current result"
            : $"Fidelity audit · {audit.Readiness} · {audit.DiscrepancyCount} discrepancy item(s) · {audit.ObservedAtUtc:u}";
        LaunchStateText.Text = launch is null ? "External launch · No active session"
            : $"External launch · {launch.Status} · {launch.Events.Length} lifecycle event(s)";
        HistoryIssueText.Text = historyIssue is null ? "History store · No reported issue" : $"History store · {historyIssue}";
        AssistantProviderText.Text = $"Assistant provider · {assistantProvider}";
    }

    private void OnSaveNexusCredentialClicked(object sender, RoutedEventArgs e)
    {
        var saved = nexusCredentialStore?.SaveApiKey(NexusApiKeyBox.Password) == true;
        NexusApiKeyBox.Password = string.Empty;
        NexusCredentialStatusText.Text = saved
            ? "Nexus connection saved securely for this Windows user."
            : "The Nexus API key was not saved; enter the key shown in your Nexus account settings.";
    }

    private void OnRemoveNexusCredentialClicked(object sender, RoutedEventArgs e)
    {
        var removed = nexusCredentialStore?.Remove() == true;
        NexusApiKeyBox.Password = string.Empty;
        NexusCredentialStatusText.Text = removed ? "Nexus connection removed." : "The Nexus connection could not be removed.";
    }

    private void UpdateNexusCredentialStatus() => NexusCredentialStatusText.Text = nexusCredentialStore?.IsConfigured == true
        ? "Nexus connection is configured. The key remains in Windows Credential Locker."
        : "Nexus is not connected. Exact official links remain available without a key.";

    private void OnAutomaticSourceDownloadsToggled(object sender, RoutedEventArgs e)
    {
        if (bindingPreferences) return;
        var saved = sourceAcquisitionPreferencesStore?.Save(new SourceAcquisitionPreferences(AutomaticSourceDownloadsToggle.IsOn)) == true;
        UpdateAutomaticSourceDownloadStatus(saved);
    }

    private void UpdateAutomaticSourceDownloadStatus(bool? saved)
    {
        AutomaticSourceDownloadsStatusText.Text = AutomaticSourceDownloadsToggle.IsOn
            ? "Automatic acquisition is on. Exact sources will be downloaded when the connected provider account permits unattended downloads; all remaining sources will be shown as direct links."
            : "Automatic acquisition is off. Grid will show every exact official source link for you to download through the mod manager.";
        if (saved == false) AutomaticSourceDownloadsStatusText.Text += " The preference could not be saved.";
    }
}
