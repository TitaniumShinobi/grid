using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class HomePage : Page
{
    private Action<GameId>? openGame;
    private Action? addGame;

    public HomePage() => InitializeComponent();

    public void BindContext(GridCatalogSnapshot catalog, bool isDevelopmentDemo, Action<GameId> openGame, Action addGame)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.openGame = openGame ?? throw new ArgumentNullException(nameof(openGame));
        this.addGame = addGame ?? throw new ArgumentNullException(nameof(addGame));
        var games = catalog.Games
            .Where(game => !game.Installations.IsEmpty)
            .Select(game => new ConnectedGameRow(
                game.Id,
                game.Name,
                game.Installations.Length == 1 ? "1 connected installation" : $"{game.Installations.Length} connected installations"))
            .ToArray();
        GamesGrid.ItemsSource = games;
        GamesGrid.Visibility = games.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = games.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        GameCountText.Text = games.Length == 1 ? "1 GAME" : $"{games.Length} GAMES";
        DevelopmentModeBadge.Visibility = isDevelopmentDemo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAddGameClicked(object sender, RoutedEventArgs e) => addGame?.Invoke();

    private void OnGameClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConnectedGameRow row)
        {
            openGame?.Invoke(row.Id);
        }
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 720;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(AddGameButton, narrow ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(AddGameButton, narrow ? 0 : 1);
        AddGameButton.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(DevelopmentModeBadge, narrow ? 2 : 1);
    }

    private sealed record ConnectedGameRow(GameId Id, string Name, string InstallationSummary)
    {
        public string AutomationName => $"{Name}, connected game";
    }
}
