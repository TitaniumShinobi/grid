using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public enum GameManagementActionKind
{
    ConnectAnotherInstallation,
    RefreshExternalObservations,
    AuthorizeRequiredRoots,
    AuthorizeProfilesRoot,
    AuthorizeModsRoot,
    AuthorizeGameDirectory,
    AuthorizeGameData,
    AuthorizeOverwrite,
    AuthorizeGameSettings,
    DisconnectFromGrid,
}

public sealed record GameManagementRequest(GameManagementActionKind Kind, GameId GameId, InstallationId? InstallationId);

public sealed partial class GameManagementDialog : ContentDialog
{
    private readonly ManagedGame game;
    private readonly ManagedInstallation[] installations;

    public GameManagementDialog(ManagedGame game, InstallationId? selectedInstallationId, bool canConnectAnother)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        InitializeComponent();
        GameNameText.Text = game.Name;
        installations = game.Installations
            .Where(value => value.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference)
            .ToArray();
        InstallationSelector.ItemsSource = installations;
        InstallationSelector.SelectedItem = installations.FirstOrDefault(value => value.Id == selectedInstallationId) ?? installations.FirstOrDefault();
        InstallationSelectorPanel.Visibility = installations.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = installations.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        InstallationActions.Visibility = installations.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ConnectAnotherButton.Visibility = canConnectAnother ? Visibility.Visible : Visibility.Collapsed;
        UpdateInstallationStatus();
    }

    public GameManagementRequest? RequestedAction { get; private set; }

    private ManagedInstallation? SelectedInstallation => InstallationSelector.SelectedItem as ManagedInstallation;

    private void OnInstallationChanged(object sender, SelectionChangedEventArgs e) => UpdateInstallationStatus();

    private void UpdateInstallationStatus()
    {
        if (SelectedInstallation is not { } installation)
        {
            Mo2AuthorizationActions.Visibility = Visibility.Collapsed;
            return;
        }

        InstallationStatusText.Text = $"{installation.Name} · {installation.Metadata.Availability}\n{installation.Metadata.StatusDetail}";
        Mo2AuthorizationActions.Visibility = installation.AdapterId.Value.Equals("adapter.mod-organizer-2", StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnActionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var kind = tag switch
        {
            "connect" => GameManagementActionKind.ConnectAnotherInstallation,
            "refresh" => GameManagementActionKind.RefreshExternalObservations,
            "authorize-required" => GameManagementActionKind.AuthorizeRequiredRoots,
            "authorize-profiles" => GameManagementActionKind.AuthorizeProfilesRoot,
            "authorize-mods" => GameManagementActionKind.AuthorizeModsRoot,
            "authorize-game-directory" => GameManagementActionKind.AuthorizeGameDirectory,
            "authorize-game-data" => GameManagementActionKind.AuthorizeGameData,
            "authorize-overwrite" => GameManagementActionKind.AuthorizeOverwrite,
            "authorize-settings" => GameManagementActionKind.AuthorizeGameSettings,
            "disconnect" => GameManagementActionKind.DisconnectFromGrid,
            _ => throw new InvalidOperationException("Unknown game-management action."),
        };
        RequestedAction = new(kind, game.Id, kind == GameManagementActionKind.ConnectAnotherInstallation ? null : SelectedInstallation?.Id);
        Hide();
    }
}
