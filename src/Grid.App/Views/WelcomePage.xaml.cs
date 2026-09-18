using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class WelcomePage : Page
{
    private Action? findSetups;
    private Action<GameId, InstallationId, ProfileId?>? openSetup;
    private Action? finishSetup;
    private Action? skipSetup;

    public WelcomePage() => InitializeComponent();

    public void BindContext(
        GridCatalogSnapshot catalog,
        Action findSetups,
        Action<GameId, InstallationId, ProfileId?> openSetup,
        Action finishSetup,
        Action skipSetup)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        this.findSetups = findSetups ?? throw new ArgumentNullException(nameof(findSetups));
        this.openSetup = openSetup ?? throw new ArgumentNullException(nameof(openSetup));
        this.finishSetup = finishSetup ?? throw new ArgumentNullException(nameof(finishSetup));
        this.skipSetup = skipSetup ?? throw new ArgumentNullException(nameof(skipSetup));

        SetupList.Children.Clear();
        var rowBuilder = new List<SetupRow>();
        foreach (var game in catalog.Games)
        {
            foreach (var installation in game.Installations)
            {
                var availableProfiles = installation.Profiles
                    .Where(profile => profile.Lifecycle == ProfileLifecycleState.Available)
                    .ToArray();
                if (availableProfiles.Length > 0)
                {
                    rowBuilder.AddRange(availableProfiles.Select(profile => new SetupRow(game, installation, profile)));
                }
                else if (installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference)
                {
                    rowBuilder.Add(new SetupRow(game, installation, null));
                }
            }
        }
        var rows = rowBuilder.ToArray();

        foreach (var row in rows)
        {
            SetupList.Children.Add(CreateSetupCard(row));
        }

        NoSetupsState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupCountText.Text = rows.Length == 1 ? "1 SETUP" : $"{rows.Length} SETUPS";
        FinishSetupButton.IsEnabled = rows.Any(row => row.Profile is not null);
    }

    private Border CreateSetupCard(SetupRow row)
    {
        var title = new TextBlock
        {
            Text = row.Profile?.Name ?? row.Installation.Name,
            FontSize = 19,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        var provenance = new TextBlock
        {
            Text = $"{row.Game.Name} · {ManagerName(row.Installation)}",
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var detail = new TextBlock
        {
            Text = row.Profile is null
                ? $"{row.Installation.Name} is connected, but no observed profile is ready yet."
                : $"Backing instance: {row.Installation.Name} · Existing profile · {row.Installation.Metadata.Availability}",
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["MutedTextBrush"],
            TextWrapping = TextWrapping.Wrap,
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(title);
        stack.Children.Add(provenance);
        stack.Children.Add(detail);

        var button = new Button
        {
            Content = row.Profile is null ? "Open game" : "Open setup",
            Tag = row,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 96,
        };
        AutomationProperties.SetName(button, row.Profile is null
            ? $"Open {row.Installation.Name}"
            : $"Open setup {row.Profile.Name}");
        button.Click += OnOpenSetupClicked;

        var grid = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(stack);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(button, 1);
        grid.Children.Add(button);

        return new Border
        {
            Padding = new Thickness(18),
            Style = (Style)Application.Current.Resources["CardStyle"],
            Child = grid,
        };
    }

    private static string ManagerName(ManagedInstallation installation) =>
        installation.AdapterId.Value.Equals("adapter.mod-organizer-2", StringComparison.OrdinalIgnoreCase)
            ? "Mod Organizer 2"
            : installation.AdapterId.Value;

    private void OnFindSetupsClicked(object sender, RoutedEventArgs e) => findSetups?.Invoke();

    private void OnOpenSetupClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SetupRow row })
        {
            openSetup?.Invoke(row.Game.Id, row.Installation.Id, row.Profile?.Id);
        }
    }

    private void OnFinishSetupClicked(object sender, RoutedEventArgs e) => finishSetup?.Invoke();

    private void OnSkipClicked(object sender, RoutedEventArgs e) => skipSetup?.Invoke();

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 840;
        StepRail.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(MainContent, narrow ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(MainContent, narrow ? 2 : 1);
    }

    private sealed record SetupRow(ManagedGame Game, ManagedInstallation Installation, Profile? Profile);
}
