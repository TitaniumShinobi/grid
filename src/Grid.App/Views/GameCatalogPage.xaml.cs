using System.Collections.ObjectModel;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class GameCatalogPage : Page
{
    private readonly ObservableCollection<CatalogEntry> visibleEntries = [];
    private readonly HashSet<GameId> detectedGames = [];
    private GridCatalogSnapshot? catalog;
    private Action<GameId>? addGame;
    private Action? addProfile;
    private Func<CancellationToken, Task<IReadOnlyList<GameId>>>? findGames;
    private CancellationTokenSource? discoveryCancellation;
    private bool autoDiscoverPending;

    public GameCatalogPage()
    {
        InitializeComponent();
        CatalogList.ItemsSource = visibleEntries;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void BindContext(
        GridCatalogSnapshot catalog,
        Action<GameId> addGame,
        Action addProfile,
        Func<CancellationToken, Task<IReadOnlyList<GameId>>> findGames,
        bool autoDiscover = false)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.addGame = addGame ?? throw new ArgumentNullException(nameof(addGame));
        this.addProfile = addProfile ?? throw new ArgumentNullException(nameof(addProfile));
        this.findGames = findGames ?? throw new ArgumentNullException(nameof(findGames));
        autoDiscoverPending |= autoDiscover;
        ApplyFilter();
        if (autoDiscoverPending && IsLoaded) _ = DiscoverAsync();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (autoDiscoverPending) _ = DiscoverAsync();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (autoDiscoverPending) _ = DiscoverAsync();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
    private void OnAddProfileClicked(object sender, RoutedEventArgs e) => addProfile?.Invoke();
    private async void OnFindGamesClicked(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private void OnGameButtonClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CatalogEntry entry }) addGame?.Invoke(entry.Id);
    }

    private async Task DiscoverAsync()
    {
        if (findGames is null) return;
        autoDiscoverPending = false;
        discoveryCancellation?.Cancel();
        discoveryCancellation?.Dispose();
        discoveryCancellation = new CancellationTokenSource();
        FindGamesButton.IsEnabled = false;
        try
        {
            var found = await findGames(discoveryCancellation.Token);
            detectedGames.Clear();
            foreach (var gameId in found) detectedGames.Add(gameId);
            ApplyFilter();
            StatusBar.Title = "Game discovery complete";
            StatusBar.Message = $"{detectedGames.Count} supported local game{(detectedGames.Count == 1 ? string.Empty : "s")} detected. Select a game to review its installation.";
            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.IsOpen = true;
        }
        catch (OperationCanceledException) when (discoveryCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusBar.Title = "Game discovery unavailable";
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
        finally { FindGamesButton.IsEnabled = true; }
    }

    private void ApplyFilter()
    {
        if (catalog is null || CatalogList is null) return;
        var search = SearchBox?.Text?.Trim() ?? string.Empty;
        var filter = (FilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        var entries = catalog.Games
            .Where(game => search.Length == 0 || game.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .Where(game => filter switch
            {
                "connected" => !game.Installations.IsEmpty,
                "available" => game.Installations.IsEmpty,
                "detected" => detectedGames.Contains(game.Id),
                _ => true,
            })
            .OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(game => new CatalogEntry(
                game.Id,
                game.Name,
                !game.Installations.IsEmpty
                    ? $"{game.Installations.Length} connected installation{(game.Installations.Length == 1 ? string.Empty : "s")}"
                    : detectedGames.Contains(game.Id) ? "Local installation detected" : "Supported game",
                !game.Installations.IsEmpty ? "CONNECTED" : detectedGames.Contains(game.Id) ? "DETECTED" : "SUPPORTED"))
            .ToArray();
        visibleEntries.Clear();
        foreach (var entry in entries) visibleEntries.Add(entry);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        discoveryCancellation?.Cancel();
        discoveryCancellation?.Dispose();
        discoveryCancellation = null;
    }

    public sealed record CatalogEntry(GameId Id, string Name, string Detail, string Status);
}
