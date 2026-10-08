using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class WelcomePage : Page
{
    private Action? browseCatalog;
    private Action? findGames;
    private Action? continueHome;

    public WelcomePage() => InitializeComponent();

    public void BindContext(Action browseCatalog, Action findGames, Action continueHome)
    {
        this.browseCatalog = browseCatalog ?? throw new ArgumentNullException(nameof(browseCatalog));
        this.findGames = findGames ?? throw new ArgumentNullException(nameof(findGames));
        this.continueHome = continueHome ?? throw new ArgumentNullException(nameof(continueHome));
    }

    private void OnBrowseCatalogClicked(object sender, RoutedEventArgs e) => browseCatalog?.Invoke();
    private void OnFindGamesClicked(object sender, RoutedEventArgs e) => findGames?.Invoke();
    private void OnContinueHomeClicked(object sender, RoutedEventArgs e) => continueHome?.Invoke();
}
