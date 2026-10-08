using System.Collections.ObjectModel;
using Grid.App.Services;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public abstract record AddGameDecision;
public sealed record CreateNewGameProfileDecision(ProviderGameCandidate Candidate, IReadOnlyList<string> ManagerProviderIds) : AddGameDecision;
public sealed record UseExistingGameProfileDecision(ResolvedProfileEnvironment Profile) : AddGameDecision;

public sealed partial class GameRegistrationDialog : ContentDialog
{
    private readonly ManagedGame game;
    private readonly GridCatalogSnapshot catalog;
    private readonly GridProviderDiscoveryService discovery;
    private readonly GridExistingProfileDiscoveryService existingProfiles;
    private readonly IInstallationPathPicker picker;
    private readonly ObservableCollection<CandidateRow> visibleCandidates = [];
    private readonly ObservableCollection<ExistingProfileRow> visibleProfiles = [];
    private readonly List<ProviderGameCandidate> candidates = [];
    private ProviderDiscoverySnapshot snapshot = new([], [], string.Empty);
    private CancellationTokenSource? profileDiscoveryCancellation;
    private bool busy;
    private bool discoveringProfiles;

    public GameRegistrationDialog(ManagedGame game, GridCatalogSnapshot catalog, GridProviderDiscoveryService discovery, GridExistingProfileDiscoveryService existingProfiles, IInstallationPathPicker picker)
    {
        this.game = game ?? throw new ArgumentNullException(nameof(game));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.existingProfiles = existingProfiles ?? throw new ArgumentNullException(nameof(existingProfiles));
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        InitializeComponent();
        SelectedGameName.Text = game.Name;
        InstallationCandidates.ItemsSource = visibleCandidates;
        ExistingProfiles.ItemsSource = visibleProfiles;
        Loaded += OnLoaded;
        Closed += OnClosed;
        IsPrimaryButtonEnabled = false;
    }

    public bool AutoDiscoverOnOpen { get; set; } = true;
    public AddGameDecision? Decision { get; private set; }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        profileDiscoveryCancellation?.Cancel();
        profileDiscoveryCancellation?.Dispose();
        profileDiscoveryCancellation = null;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (AutoDiscoverOnOpen) await ScanAsync();
        else UpdateReview();
    }

    private async void OnScanClicked(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        SetBusy(true);
        try
        {
            snapshot = await discovery.DiscoverAsync();
            candidates.RemoveAll(value => !value.ProviderId.Equals("manual", StringComparison.OrdinalIgnoreCase));
            candidates.AddRange(snapshot.Games.Where(value => value.GameId.Equals(game.Id.Value, StringComparison.Ordinal)));
            BindCandidates();
            ShowStatus("Game discovery complete", $"{visibleCandidates.Count} validated local installation candidate{(visibleCandidates.Count == 1 ? string.Empty : "s")} found for {game.Name}.", InfoBarSeverity.Success);
        }
        catch (Exception exception)
        {
            ShowStatus("Game discovery unavailable", exception.Message, InfoBarSeverity.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void OnBrowseClicked(object sender, RoutedEventArgs e)
    {
        var root = await picker.PickDirectoryAsync();
        if (string.IsNullOrWhiteSpace(root)) return;
        SetBusy(true);
        try
        {
            var candidate = await discovery.ResolveManualGameAsync(game.Id, game.Name, root);
            candidates.RemoveAll(value => value.GameId.Equals(candidate.GameId, StringComparison.Ordinal) && value.InstallRoot.Equals(candidate.InstallRoot, StringComparison.OrdinalIgnoreCase));
            candidates.Add(candidate);
            BindCandidates();
            InstallationCandidates.SelectedItem = visibleCandidates.First(value => value.Candidate == candidate);
            StatusBar.IsOpen = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            ShowStatus("Game directory not recognized", exception.Message, InfoBarSeverity.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void OnInstallationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateReview();
        await DiscoverProfilesForSelectionAsync();
    }

    private void BindCandidates()
    {
        visibleCandidates.Clear();
        foreach (var candidate in candidates.Where(value => value.GameId.Equals(game.Id.Value, StringComparison.Ordinal)).DistinctBy(value => Path.GetFullPath(value.InstallRoot), StringComparer.OrdinalIgnoreCase).OrderBy(value => value.InstallRoot, StringComparer.OrdinalIgnoreCase))
            visibleCandidates.Add(new(candidate));
        if (visibleCandidates.Count == 1) InstallationCandidates.SelectedIndex = 0;
        UpdateReview();
    }

    private async Task DiscoverProfilesForSelectionAsync()
    {
        profileDiscoveryCancellation?.Cancel();
        profileDiscoveryCancellation?.Dispose();
        profileDiscoveryCancellation = null;
        visibleProfiles.Clear();
        ExistingProfilesPanel.Visibility = Visibility.Collapsed;
        if (InstallationCandidates.SelectedItem is not CandidateRow selected) return;

        var cancellation = new CancellationTokenSource();
        profileDiscoveryCancellation = cancellation;
        discoveringProfiles = true;
        ProfileDiscoveryProgress.IsActive = true;
        ProfileDiscoveryProgress.Visibility = Visibility.Visible;
        ExistingProfilesPanel.Visibility = Visibility.Visible;
        ExistingProfilesHeading.Text = "Looking for existing profiles...";
        UpdateReview();

        try
        {
            var result = await existingProfiles.DiscoverAsync(new(game.Id, game.Name, selected.Candidate.InstallRoot, snapshot.Managers), catalog, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            foreach (var profile in result.Profiles) visibleProfiles.Add(new(profile));
            ExistingProfilesHeading.Text = visibleProfiles.Count switch
            {
                0 => "No matching existing profiles discovered",
                1 => "GRID discovered 1 existing profile",
                _ => $"GRID discovered {visibleProfiles.Count:N0} existing profiles",
            };
            if (result.Issues.Length > 0)
                ShowStatus("Some profile discovery evidence was unavailable", string.Join(" ", result.Issues.Select(issue => issue.Message)), InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            ExistingProfilesHeading.Text = "Existing profile discovery unavailable";
            ShowStatus("Existing profile discovery unavailable", $"{exception.Message} You can still create a new profile.", InfoBarSeverity.Warning);
        }
        finally
        {
            if (ReferenceEquals(profileDiscoveryCancellation, cancellation))
            {
                discoveringProfiles = false;
                ProfileDiscoveryProgress.IsActive = false;
                ProfileDiscoveryProgress.Visibility = Visibility.Collapsed;
                UpdateReview();
            }
        }
    }

    private void UpdateReview()
    {
        var candidate = (InstallationCandidates.SelectedItem as CandidateRow)?.Candidate;
        IsPrimaryButtonEnabled = candidate is not null && !busy && !discoveringProfiles;
        ReviewText.Text = candidate is null ? $"Detect {game.Name} or browse to its installation directory." : $"{game.Name} - {candidate.Edition}\n{candidate.InstallRoot}\nCreate new profile: {game.Name}";
    }

    private void OnCreateNewClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        if (busy || discoveringProfiles || InstallationCandidates.SelectedItem is not CandidateRow selected) return;
        Decision = new CreateNewGameProfileDecision(selected.Candidate, snapshot.Managers.Select(manager => manager.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Hide();
    }

    private void OnUseExistingClicked(object sender, RoutedEventArgs e)
    {
        if (busy || discoveringProfiles || sender is not FrameworkElement { DataContext: ExistingProfileRow row }) return;
        Decision = new UseExistingGameProfileDecision(row.Profile);
        Hide();
    }

    private void SetBusy(bool value)
    {
        busy = value;
        DiscoveryProgress.IsActive = value;
        DiscoveryProgress.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        InstallationCandidates.IsEnabled = !value;
        UpdateReview();
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    public sealed record CandidateRow(ProviderGameCandidate Candidate)
    {
        public string Title => Candidate.Edition;
        public string InstallRoot => Candidate.InstallRoot;
        public string Evidence => $"{Candidate.ProviderId} - {Candidate.Status}";
    }

    public sealed record ExistingProfileRow(ResolvedProfileEnvironment Profile)
    {
        public string ProfileName => Profile.ProfileName;
        public string ManagerName => Profile.ManagerName;
        public string Evidence => $"{Profile.EnabledModCount:N0} enabled mods | {Profile.ActivePluginCount:N0} active plugins";
        public string UseLabel => $"Use {Profile.ProfileName}";
    }
}
