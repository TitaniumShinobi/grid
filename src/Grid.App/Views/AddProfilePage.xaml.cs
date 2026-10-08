using Grid.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class AddProfilePage : Page
{
    private Func<CancellationToken, Task<ResolvedProfileEnvironmentResolution?>>? browse;
    private Func<ResolvedProfileEnvironment, CancellationToken, Task>? connect;
    private Action? cancel;
    private CancellationTokenSource? operation;
    private AddProfileReviewState<ResolvedProfileEnvironment> reviewState =
        AddProfileReviewState<ResolvedProfileEnvironment>.Empty;

    public AddProfilePage()
    {
        InitializeComponent();
        Unloaded += OnUnloaded;
    }

    public void BindContext(
        Func<CancellationToken, Task<ResolvedProfileEnvironmentResolution?>> browse,
        Func<ResolvedProfileEnvironment, CancellationToken, Task> connect,
        Action cancel)
    {
        this.browse = browse ?? throw new ArgumentNullException(nameof(browse));
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.cancel = cancel ?? throw new ArgumentNullException(nameof(cancel));
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        if (browse is null) return;
        ClearResolution();
        StatusBar.IsOpen = false;
        await RunAsync(async cancellationToken =>
        {
            var result = await browse(cancellationToken);
            if (result is null) return;
            reviewState = AddProfileReviewState<ResolvedProfileEnvironment>.FromChoices(result.Profiles);
            ProfileSelector.ItemsSource = reviewState.Choices;
            ProfileSelectionPanel.Visibility = reviewState.RequiresSelection ? Visibility.Visible : Visibility.Collapsed;
            ReviewCard.Visibility = reviewState.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (reviewState.Selected is not null)
                RenderProfile(reviewState.Selected);
        });
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileSelector.SelectedItem is ResolvedProfileEnvironment profile)
        {
            reviewState = reviewState.Select(profile);
            RenderProfile(profile);
        }
        else
        {
            reviewState = reviewState.Select(null);
            ConnectButton.IsEnabled = false;
        }
    }

    private void RenderProfile(ResolvedProfileEnvironment profile)
    {
        GameText.Text = profile.GameName;
        ManagerText.Text = profile.ManagerName;
        TopologyText.Text = profile.Topology;
        ProfileText.Text = profile.ProfileName;
        ApplicationText.Text = profile.ApplicationDirectory;
        EnvironmentText.Text = profile.InstanceDirectory;
        GameDirectoryText.Text = profile.GameDirectory;
        ModsText.Text = profile.ModsSummary;
        PluginsText.Text = profile.PluginsSummary;
        ConfigurationText.Text = profile.ConfigurationSummary;
        ObservationText.Text = profile.ObservationSummary;
        EvidenceText.Text = profile.EvidenceSummary;
        WarningsList.ItemsSource = profile.Warnings;
        WarningsPanel.Visibility = profile.Warnings.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.IsEnabled = true;
        StatusBar.IsOpen = false;
    }

    private async void OnConnectClicked(object sender, RoutedEventArgs e)
    {
        if (connect is null || reviewState.Selected is not { } selected) return;
        await RunAsync(async cancellationToken => await connect(selected, cancellationToken));
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => cancel?.Invoke();

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        operation?.Cancel();
        operation?.Dispose();
        operation = new CancellationTokenSource();
        SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ClearResolution();
            StatusBar.Title = "Profile environment not recognized";
            StatusBar.Message = exception.Message;
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.IsOpen = true;
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool value)
    {
        BusyIndicator.IsActive = value;
        BusyIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        BrowseButton.IsEnabled = !value;
        ProfileSelector.IsEnabled = !value;
        ConnectButton.IsEnabled = !value && reviewState.CanConnect;
    }

    private void ClearResolution()
    {
        reviewState = reviewState.Clear();
        ProfileSelector.SelectedItem = null;
        ProfileSelector.ItemsSource = null;
        ProfileSelectionPanel.Visibility = Visibility.Collapsed;
        WarningsList.ItemsSource = null;
        WarningsPanel.Visibility = Visibility.Collapsed;
        GameText.Text = string.Empty;
        ManagerText.Text = string.Empty;
        TopologyText.Text = string.Empty;
        ProfileText.Text = string.Empty;
        ApplicationText.Text = string.Empty;
        EnvironmentText.Text = string.Empty;
        GameDirectoryText.Text = string.Empty;
        ModsText.Text = string.Empty;
        PluginsText.Text = string.Empty;
        ConfigurationText.Text = string.Empty;
        ObservationText.Text = string.Empty;
        EvidenceText.Text = string.Empty;
        ReviewCard.Visibility = Visibility.Collapsed;
        ConnectButton.IsEnabled = false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        operation?.Dispose();
        operation = null;
    }
}
