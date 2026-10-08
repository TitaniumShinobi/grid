using Grid.App.Composition;
using Grid.App.Services;
using Grid.Core.Startup;
using Microsoft.UI.Xaml;

namespace Grid.App;

public partial class App : Application
{
    private MainWindow? window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var isolatedTestMode = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GRID_DATA_ROOT"))
            ? null
            : Environment.GetEnvironmentVariable("GRID_TEST_APP_MODE");
        var mode = GridLaunchOptions.Parse(args.Arguments, Environment.GetCommandLineArgs().Skip(1), isolatedTestMode);
        var root = mode == GridApplicationMode.Demo
            ? GridCompositionRoot.CreateDemo()
            : GridCompositionRoot.CreateProductionShell();
        StartupInstrumentation.Begin(enabled: mode != GridApplicationMode.Demo, isDemoMode: mode == GridApplicationMode.Demo);
        StartupInstrumentation.Current?.BeginStage(
            StartupStageId.ShellMainWindow,
            StartupWorkClassification.StartupRequired,
            "Application launch and main window construction.");
        StartupInstrumentation.Current?.BeginStage(
            StartupStageId.ShellInitialization,
            StartupWorkClassification.StartupRequired,
            "Shell initialization envelope.");
        window = new MainWindow(root);
        window.Activate();
    }
}
