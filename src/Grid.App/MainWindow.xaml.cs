using System.Collections.ObjectModel;
using Grid.App.Composition;
using Grid.App.Services;
using Grid.App.Views;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;
using Grid.Mo2.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinRT.Interop;

namespace Grid.App;

public sealed partial class MainWindow : Window
{
    private const double ShellPanelContentInset = 5;
    private static readonly GameAdapterId ConnectedMo2AdapterId = new("adapter.mod-organizer-2");
    private readonly GridCompositionRoot composition;
    private readonly IGridCatalogService catalogService;
    private readonly IInstallationPathPicker pathPicker;
    private readonly Dictionary<GameId, Button> gameItems = [];
    private readonly List<Button> dynamicGameItems = [];
    private readonly List<ShellLocation> navigationHistory = [];
    private readonly List<EditorTabRecord> editorTabs = [];
    private readonly ObservableCollection<GridNotification> notifications = [];
    private ToolTargetPresentation[] launchTargetItems = [];
    private readonly string[] searchCatalog =
    [
        "Mod Sites · Nexus", "Mod Sites · LoversLab", "Mod Sites · Reddit", "Mod Sites · Add site…",
        "Tools · xLODGenx64", "Tools · LOOT", "Tools · Pandora Behavior Engine+", "Tools · Add tool…",
        "Managers · Mod Organizer 2", "Managers · Vortex", "Managers · Add manager…",
        "Publishers · Wabbajack", "Publishers · Add publisher…",
    ];
    private CancellationTokenSource? lifetime;
    private readonly DispatcherTimer vortexCatalogTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool vortexCatalogRefreshRunning;
    private GridApplicationSession? session;
    private bool suppressSelectors;
    private bool restoringNavigation;
    private bool suppressEditorTabSelection;
    private bool taskboardExpanded;
    private GridTaskboardPhase taskboardFocus = GridTaskboardPhase.Ledger;
    private bool firstRunCompleted = true;
    private bool leftPanelRequested;
    private UIElement? activeSidePanel;
    private GameId? activeGameSidePanelId;
    private bool bottomPanelRequested;
    private bool bottomPanelMaximized;
    private int navigationIndex = -1;
    private double requestedLeftPanelWidth = 286;
    private double requestedBottomPanelHeight = 220;
    private int unreadNotificationCount;
    private string connectionStatusDetail = "No manager connected";
    private ShellSurface activeSurface = ShellSurface.Home;
    private bool UsesAssistantDockBudget => session is { Assistant: { IsExpanded: true, IsFullScreen: false } };
    private bool AssistantOwnsWorkbench =>
        session?.Assistant is { IsExpanded: true } assistant &&
        (assistant.IsFullScreen ||
         ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, true, assistant.RequestedPanelWidth)
             .AssistantPresentation == AssistantPresentationMode.Solo);

    public MainWindow(GridCompositionRoot composition)
    {
        this.composition = composition ?? throw new ArgumentNullException(nameof(composition));
        catalogService = composition.CatalogService;
        InitializeComponent();
        NotificationList.ItemsSource = notifications;
        pathPicker = new WindowsInstallationPathPicker(this);
        GameSortSelector.ItemsSource = new[] { "Last updated", "Alphabetical", "Creation time / day" };
        GameSortSelector.SelectedIndex = 0;
        GlobalSearchBox.PlaceholderText = "Home";
        TerminalOutputText.Text = "Grid local terminal · deterministic commands only\nType 'help' to list available commands.\n";
        OutputText.Text = "Grid output\nNo operation is running. External tool output appears only after an authorized route records it.";
        ConfigureWindow();
        ShellContentGrid.SizeChanged += OnShellSizeChanged;
        MainWorkspacePanel.SizeChanged += OnMainWorkspacePanelSizeChanged;
        ShellContentGrid.Loaded += OnLoaded;
        Closed += OnClosed;
        vortexCatalogTimer.Tick += OnVortexCatalogTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShellContentGrid.Loaded -= OnLoaded;
        await LoadCatalogAsync();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        lifetime?.Cancel();
        vortexCatalogTimer.Stop();
        lifetime?.Dispose();
        session?.Dispose();
    }

    private async Task LoadCatalogAsync()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = new CancellationTokenSource();
        ShowLoading();
        try
        {
            string? taskHistoryIssue = null;
            var catalog = await catalogService.GetCatalogAsync(lifetime.Token);
            var restoredSelection = composition.WorkspaceSelectionStore?.Load();
            firstRunCompleted = composition.IsDemo || composition.FirstRunStateStore?.IsComplete() == true;
            session?.Dispose();
            session = new GridApplicationSession(
                catalog,
                composition.HistoryStore,
                composition.Mo2ToolOutputQueryService,
                composition.Mo2ResolvedStateService,
                composition.FidelityAuditService,
                composition.WorkspaceLaunchService,
                assistantClasses: AssistantClassCatalogLoader.Load(),
                assistantTools: AssistantToolCatalogLoader.Load(),
                assistantExecutionService: composition.AssistantRequestExecutionService,
                initialSelection: restoredSelection,
                offlineAlertIndexStore: composition.OfflineAlertIndexStore);
            await session.History.LoadAsync(lifetime.Token);
            try
            {
                await session.Assistant.LoadPersistedTasksAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                taskHistoryIssue = "Saved investigation tasks could not be restored. Connected games remain available and no external changes were made.";
            }
            editorTabs.Clear();
            EditorTabView.TabItems.Clear();
            navigationHistory.Clear();
            navigationIndex = -1;
            PersistWorkspaceSelection();
            PopulateGameItems(catalog);
            BindAssistant();
            ShowShell();
            if (taskHistoryIssue is not null)
            {
                RaiseNotification("Investigation history unavailable", taskHistoryIssue, InfoBarSeverity.Warning);
            }
            SelectBottomSurface(BottomSurface.Terminal);
            leftPanelRequested = false;
            bottomPanelRequested = false;
            session.Assistant.Collapse();
            ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(
                WorkspaceHost.ActualWidth,
                UsesAssistantDockBudget,
                session.Assistant.RequestedPanelWidth).Band);
            UpdateAssistantLayout();
            if (!composition.IsDemo && !firstRunCompleted)
            {
                NavigateSurface(ShellSurface.Welcome, record: false);
            }
            else
            {
                NavigateSurface(ShellSurface.Home, record: false);
            }
            if (navigationHistory.Count == 0) RecordNavigation(CreateLocation(activeSurface));
            if (composition.VortexConnectionStore is not null) vortexCatalogTimer.Start();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            ShowError("Connected games could not be loaded. Grid made no external changes.");
        }
    }

    private void PopulateGameItems(GridCatalogSnapshot catalog)
    {
        GamesList.Children.Clear();
        GameRailItems.Children.Clear();
        dynamicGameItems.Clear();
        gameItems.Clear();

        var connectedGames = SortGames(catalog.Games.Where(value => !value.Installations.IsEmpty));
        foreach (var game in connectedGames)
        {
            var fallbackMark = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(game.Name) ? "G" : game.Name[..1].ToUpperInvariant(),
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var item = new Button
            {
                Content = fallbackMark,
                Tag = game.Id,
                Width = 38,
                Height = 38,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Transparent),
                CornerRadius = new CornerRadius(19),
            };
            item.Click += OnGameItemClicked;
            AutomationProperties.SetName(item, $"{game.Name}, connected game");
            AutomationProperties.SetHelpText(item, $"Open {game.Name}. Profiles are available inside its workspace.");
            ToolTipService.SetToolTip(item, $"Open {game.Name}");
            GameRailItems.Children.Add(item);
            dynamicGameItems.Add(item);
            gameItems.Add(game.Id, item);
        }
        EmptyGamesButton.Visibility = dynamicGameItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IEnumerable<ManagedGame> SortGames(IEnumerable<ManagedGame> games)
    {
        var values = games.ToArray();
        return GameSortSelector.SelectedIndex switch
        {
            1 => values.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => values.Reverse(),
            _ => values,
        };
    }

    private void OnGameItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameId id } item) return;
        if (leftPanelRequested && LeftSidebar.Visibility == Visibility.Visible &&
            ReferenceEquals(activeSidePanel, GameSnapshotPanel) && activeGameSidePanelId == id)
        {
            HideLeftPanel();
            item.Focus(FocusState.Programmatic);
            return;
        }

        OpenGame(id);
    }

    private void OnGameSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (session is not null) PopulateGameItems(session.Catalog);
    }

    private void NavigateSurface(ShellSurface surface, GameId? gameId = null, bool record = true, bool synchronizeTab = true)
    {
        if (session is null) return;
        switch (surface)
        {
            case ShellSurface.Welcome:
                activeSurface = ShellSurface.Welcome;
                session.Shell.NavigateHome();
                break;
            case ShellSurface.Home:
            case ShellSurface.Games:
                activeSurface = surface;
                session.Shell.NavigateHome();
                break;
            case ShellSurface.Workstation:
                var target = gameId ?? session.Shell.CurrentSelection.GameId ??
                    session.Catalog.Games.FirstOrDefault(game => !game.Installations.IsEmpty)?.Id;
                if (target is not GameId selectedGame)
                {
                    activeSurface = ShellSurface.Games;
                    session.Shell.NavigateHome();
                    _ = ShowMessageAsync("No workstation available", "Connect a supported game installation before opening Workstation.");
                    surface = ShellSurface.Games;
                    break;
                }
                activeSurface = ShellSurface.Workstation;
                gameId = selectedGame;
                session.Shell.NavigateGame(selectedGame);
                PersistWorkspaceSelection();
                break;
            case ShellSurface.Activities:
                activeSurface = ShellSurface.Activities;
                session.Shell.NavigateHistory();
                break;
            case ShellSurface.Settings:
                activeSurface = ShellSurface.Settings;
                session.Shell.NavigateSettings();
                break;
        }
        if (synchronizeTab) OpenOrFocusEditorTab(activeSurface);
        if (record) RecordNavigation(CreateLocation(activeSurface));
        RenderCurrentRoute();
    }

    private void RecordNavigation(ShellLocation location)
    {
        if (restoringNavigation) return;
        if (navigationIndex >= 0 && navigationHistory[navigationIndex] == location)
        {
            UpdateHistoryButtons();
            return;
        }
        if (navigationIndex + 1 < navigationHistory.Count)
            navigationHistory.RemoveRange(navigationIndex + 1, navigationHistory.Count - navigationIndex - 1);
        navigationHistory.Add(location);
        navigationIndex = navigationHistory.Count - 1;
        UpdateHistoryButtons();
    }

    private void UpdateHistoryButtons()
    {
        BackButton.IsEnabled = navigationIndex > 0;
        ForwardButton.IsEnabled = navigationIndex >= 0 && navigationIndex < navigationHistory.Count - 1;
    }

    private void NavigateHistoryOffset(int offset)
    {
        var target = navigationIndex + offset;
        if (target < 0 || target >= navigationHistory.Count) return;
        navigationIndex = target;
        restoringNavigation = true;
        try
        {
            RestoreLocation(navigationHistory[target]);
            NavigateSurface(navigationHistory[target].Surface, navigationHistory[target].Selection.GameId, false);
        }
        finally { restoringNavigation = false; }
        UpdateHistoryButtons();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e) => NavigateHistoryOffset(-1);
    private void OnForwardClicked(object sender, RoutedEventArgs e) => NavigateHistoryOffset(1);
    private void OnHomeBrandClicked(object sender, RoutedEventArgs e)
    {
        ShowLeftPanel();
        ShowSidePanel(HomeSidePanel, "HOME");
        NavigateSurface(ShellSurface.Home);
    }

    private void OnHomeActivityClicked(object sender, RoutedEventArgs e) => OnHomeBrandClicked(sender, e);
    private void OnActivitiesClicked(object sender, RoutedEventArgs e)
    {
        ToggleSidePanel(ActivityPanel, "ACTIVITY", RefreshActivitySurface);
    }

    private void OnSeeMoreActivityClicked(object sender, RoutedEventArgs e) => OpenTaskboard(GridTaskboardPhase.Ledger);
    private void OnActivityQueueScoreClicked(object sender, RoutedEventArgs e) => OpenTaskboard(GridTaskboardPhase.Queue);
    private void OnActivityProgressScoreClicked(object sender, RoutedEventArgs e) => OpenTaskboard(GridTaskboardPhase.InProgress);
    private void OnActivityReadyScoreClicked(object sender, RoutedEventArgs e) => OpenTaskboard(GridTaskboardPhase.Ready);
    private void OnActivityLedgerScoreClicked(object sender, RoutedEventArgs e) => OpenTaskboard(GridTaskboardPhase.Ledger);

    private void OpenTaskboard(GridTaskboardPhase focus)
    {
        if (session is null) return;
        if (bottomPanelMaximized) RestoreBottomPanelLayout();
        taskboardFocus = focus;
        taskboardExpanded = true;
        RenderTaskboard();
    }

    private void CloseTaskboard()
    {
        taskboardExpanded = false;
        TaskboardShellOverlay.Visibility = Visibility.Collapsed;
        RenderCurrentRoute();
    }

    private void RenderTaskboard()
    {
        if (session is null) return;

        // Activity Taskboard is a shell takeover: only the persistent top bar and footer remain outside it.
        // The normal rail, left snapshot panel, editor tabs/content, bottom tools, and assistant stay mounted
        // underneath so closing the board returns to the exact prior surface without destroying context.
        TaskboardOverlayView.BindContext(
            TaskboardProjection.Create(session.Assistant.Snapshot()),
            CloseTaskboard,
            BeginActivityTaskDraft,
            FocusActivityAttachment,
            OpenActivityTask);
        if (taskboardFocus == GridTaskboardPhase.Ledger)
            TaskboardOverlayView.FocusLedger();

        TaskboardShellOverlay.Visibility = Visibility.Visible;
        GlobalSearchBox.PlaceholderText = "activity";
        RefreshActivitySurface();
    }

    private void BeginActivityTaskDraft(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        AssistantPanelView.BeginTaskDraft(text);
        session?.Assistant.Expand();
        RefreshAssistantContext();
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void FocusActivityAttachment()
    {
        session?.Assistant.Expand();
        RefreshAssistantContext();
        AssistantPanelView.FocusPrimaryAction();
    }

    private void OnActivityComposeClicked(object sender, RoutedEventArgs e)
    {
        var text = ActivityComposerText.Text.Trim();
        if (text.Length == 0) return;
        BeginActivityTaskDraft(text);
        ActivityComposerText.Text = string.Empty;
    }

    private void OnActivityAttachClicked(object sender, RoutedEventArgs e) => FocusActivityAttachment();

    private void RefreshActivitySurface()
    {
        if (session is null) return;
        var snapshot = TaskboardProjection.Create(session.Assistant.Snapshot());
        ActivityQueueScore.Content = snapshot.QueueCount.ToString();
        ActivityProgressScore.Content = snapshot.InProgressCount.ToString();
        ActivityReadyScore.Content = snapshot.ReadyCount.ToString();
        ActivityLedgerScore.Content = snapshot.LedgerCount.ToString();

        ActivityLedgerItems.Children.Clear();
        var entries = ActivityFeedProjection.CreateRecent(snapshot, session.History.Entries);
        if (entries.Length == 0)
        {
            ActivityLedgerItems.Children.Add(new TextBlock
            {
                Text = "No recorded activity for this context.",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var entry in entries)
        {
            var stack = new StackPanel { Spacing = 2 };
            stack.Children.Add(new TextBlock { Text = entry.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            stack.Children.Add(new TextBlock
            {
                Text = $"{entry.Status} · {entry.OccurredAtUtc.ToLocalTime():g}",
                FontSize = 10,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var content = new Border
            {
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(5),
                Background = (Brush)Application.Current.Resources["ShellPanelBrush"],
                Child = stack,
            };
            ToolTipService.SetToolTip(content, entry.Detail);
            if (entry.TaskId is not string taskId)
            {
                ActivityLedgerItems.Children.Add(content);
                continue;
            }

            var button = new Button
            {
                Tag = taskId,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Content = content,
            };
            AutomationProperties.SetName(button, $"Open recent task {entry.Title}");
            button.Click += OnActivityTaskClicked;
            ActivityLedgerItems.Children.Add(button);
        }
    }

    private void OnActivityTaskClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string taskId }) OpenActivityTask(taskId);
    }

    private void OpenActivityTask(string taskId)
    {
        if (session is null) return;
        session.Assistant.OpenTask(taskId);
        session.Assistant.Expand();
        taskboardExpanded = false;
        TaskboardShellOverlay.Visibility = Visibility.Collapsed;
        RefreshAssistantContext();
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(
            WorkspaceHost.ActualWidth,
            UsesAssistantDockBudget,
            session.Assistant.RequestedPanelWidth).Band);
    }
    private void OnSettingsClicked(object sender, RoutedEventArgs e) => NavigateSurface(ShellSurface.Settings);
    private void OnWelcomeClicked(object sender, RoutedEventArgs e) => NavigateSurface(ShellSurface.Welcome);
    private void OnAddGameClicked(object sender, RoutedEventArgs e) => _ = BeginAddGameAsync();

    private void OnLibraryActivityClicked(object sender, RoutedEventArgs e)
    {
        ToggleSidePanel(LibraryPanel, "EXPLORER", () =>
        {
            RefreshInstanceExplorer();
            LibraryPanel.Focus(FocusState.Programmatic);
        });
    }

    private void RefreshInstanceExplorer()
    {
        InstanceExplorerTree.RootNodes.Clear();
        ExplorerSelectionText.Text = string.Empty;
        ExplorerRootText.Text = "No connected profile";

        if (session?.Shell.CurrentSelection is not WorkspaceSelection selection ||
            selection.GameId is not GameId gameId ||
            selection.InstallationId is not InstallationId installationId)
        {
            return;
        }

        var game = session.Catalog.Games.FirstOrDefault(value => value.Id == gameId);
        var installation = game?.Installations.FirstOrDefault(value => value.Id == installationId);
        var profile = selection.ProfileId is ProfileId profileId
            ? installation?.Profiles.FirstOrDefault(value => value.Id == profileId)
            : null;
        var instancePath = installation?.Metadata.LocationDisplay;

        ExplorerRootText.Text = profile?.Name ?? installation?.Name ?? game?.Name ?? "Connected profile";

        if (string.IsNullOrWhiteSpace(instancePath) || !Directory.Exists(instancePath))
        {
            AddExplorerUnavailableRoot(
                installation?.Name ?? "Manager",
                instancePath,
                "Connected manager path unavailable");
            return;
        }

        // GRID Explorer is contextual, not an arbitrary machine root. Every top-level
        // node represents a known game/profile concern and expands lazily beneath it.
        AddExplorerRoot("Manager", instancePath, "MO2 instance");

        AddConventionalExplorerRoot("Mods", Path.Combine(instancePath, "mods"));
        AddConventionalExplorerRoot("Downloads", Path.Combine(instancePath, "downloads"));
        AddConventionalExplorerRoot("Overwrite", Path.Combine(instancePath, "overwrite"));

        var profilesRoot = Path.Combine(instancePath, "profiles");
        if (profile is not null)
        {
            AddConventionalExplorerRoot("Profile", Path.Combine(profilesRoot, profile.Name));
            AddConventionalExplorerRoot("Saves", Path.Combine(profilesRoot, profile.Name, "saves"));
        }
        else
        {
            AddConventionalExplorerRoot("Profiles", profilesRoot);
        }

        // Tools are intentionally omitted in Explorer v2. The observed-executable
        // summary does not expose filesystem paths. GRID will add the Tools root only
        // when it is bound to the authoritative executable configuration/catalog source.
    }

    private void AddConventionalExplorerRoot(string label, string path)
    {
        if (!Directory.Exists(path)) return;
        AddExplorerRoot(label, path, path);
    }

    private void AddExplorerRoot(string label, string path, string detail)
    {
        var node = CreateExplorerDirectoryNode(path, label, detail);
        InstanceExplorerTree.RootNodes.Add(node);
    }

    private void AddExplorerUnavailableRoot(string label, string? path, string detail)
    {
        InstanceExplorerTree.RootNodes.Add(new TreeViewNode
        {
            Content = new FileSystemExplorerItem(path ?? string.Empty, label, false, detail),
        });
    }

    private static TreeViewNode CreateExplorerDirectoryNode(string path, string label, string? detail = null)
    {
        var node = new TreeViewNode
        {
            Content = new FileSystemExplorerItem(path, label, true, detail ?? path),
            HasUnrealizedChildren = HasExplorerChildren(path),
        };
        return node;
    }

    private static bool HasExplorerChildren(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Take(1).Any();
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void PopulateExplorerChildren(TreeViewNode parent, string path)
    {
        parent.Children.Clear();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path)
                         .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                         .Take(256))
            {
                parent.Children.Add(CreateExplorerDirectoryNode(
                    directory,
                    Path.GetFileName(directory),
                    directory));
            }

            foreach (var file in Directory.EnumerateFiles(path)
                         .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                         .Take(256))
            {
                parent.Children.Add(new TreeViewNode
                {
                    Content = new FileSystemExplorerItem(
                        file,
                        Path.GetFileName(file),
                        false,
                        file),
                });
            }
        }
        catch (UnauthorizedAccessException)
        {
            parent.Children.Add(new TreeViewNode
            {
                Content = new FileSystemExplorerItem(path, "Access unavailable", false, path),
            });
        }
        catch (IOException)
        {
            parent.Children.Add(new TreeViewNode
            {
                Content = new FileSystemExplorerItem(path, "Path unavailable", false, path),
            });
        }
    }

    private void OnExplorerNodeExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not FileSystemExplorerItem { IsDirectory: true } item) return;
        if (!args.Node.HasUnrealizedChildren || string.IsNullOrWhiteSpace(item.Path)) return;
        PopulateExplorerChildren(args.Node, item.Path);
        args.Node.HasUnrealizedChildren = false;
    }

    private void OnExplorerSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (args.AddedItems.FirstOrDefault() is not TreeViewNode node ||
            node.Content is not FileSystemExplorerItem item)
        {
            ExplorerSelectionText.Text = string.Empty;
            return;
        }

        ExplorerSelectionText.Text = string.IsNullOrWhiteSpace(item.Path)
            ? item.Detail ?? item.Name
            : item.Path;
    }

    private void OnCollapseExplorerClicked(object sender, RoutedEventArgs e)
    {
        foreach (var node in InstanceExplorerTree.RootNodes)
        {
            CollapseExplorerNode(node);
        }
    }

    private static void CollapseExplorerNode(TreeViewNode node)
    {
        node.IsExpanded = false;
        foreach (var child in node.Children)
        {
            CollapseExplorerNode(child);
        }
    }

    private void OnRefreshExplorerClicked(object sender, RoutedEventArgs e) => RefreshInstanceExplorer();

    private void OnSearchActivityClicked(object sender, RoutedEventArgs e)
    {
        ToggleSidePanel(SearchPanel, "SEARCH", () => SideSearchBox.Focus(FocusState.Programmatic));
    }

    private void ToggleSidePanel(UIElement panel, string title, Action? onOpened = null)
    {
        if (leftPanelRequested && LeftSidebar.Visibility == Visibility.Visible && ReferenceEquals(activeSidePanel, panel))
        {
            HideLeftPanel();
            return;
        }

        ShowSidePanel(panel, title);
        ShowLeftPanel();
        onOpened?.Invoke();
    }

    private void ShowSidePanel(UIElement panel, string title, GameId? gameId = null)
    {
        activeSidePanel = panel;
        activeGameSidePanelId = ReferenceEquals(panel, GameSnapshotPanel) ? gameId : null;
        SidePanelTitle.Text = title;
        foreach (var candidate in new UIElement[] { HomeSidePanel, GameSnapshotPanel, LibraryPanel, SearchPanel, ActivityPanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideLeftPanel()
    {
        leftPanelRequested = false;
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void ShowLeftPanel()
    {
        leftPanelRequested = true;
        LeftPanelToggleButton.IsChecked = true;
        LeftPanelMenuItem.IsChecked = true;
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void OnFocusSearchClicked(object sender, RoutedEventArgs e) => GlobalSearchBox.Focus(FocusState.Programmatic);

    private void OnGlobalSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var term = sender.Text.Trim();
        var games = session?.Catalog.Games.Where(game => !game.Installations.IsEmpty)
            .Select(game => $"Games · {game.Name}") ?? [];
        sender.ItemsSource = searchCatalog.Concat(games)
            .Where(value => term.Length == 0 || value.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            .Take(12).ToArray();
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string value) sender.Text = value;
    }

    private void OnGlobalSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = (args.ChosenSuggestion as string ?? args.QueryText).Trim();
        if (string.IsNullOrWhiteSpace(query)) return;
        var game = session?.Catalog.Games.FirstOrDefault(value =>
            query.Contains(value.Name, StringComparison.CurrentCultureIgnoreCase));
        if (game is not null && !game.Installations.IsEmpty)
        {
            OpenGame(game.Id);
            return;
        }
        if (query.StartsWith("Managers", StringComparison.OrdinalIgnoreCase))
        {
            _ = ShowManagerBoundaryAsync(query);
            return;
        }
        AppendTerminal($"> search {query}\nGrid indexed the requested category. Provider-backed browsing is unavailable in this local build.\n");
        ShowBottomPanel();
    }

    private void OnLeftPanelToggleClicked(object sender, RoutedEventArgs e)
    {
        leftPanelRequested = sender switch
        {
            ToggleButton toggle => toggle.IsChecked == true,
            ToggleMenuFlyoutItem item => item.IsChecked,
            _ => !leftPanelRequested,
        };
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void OnBottomPanelMaximizeClicked(object sender, RoutedEventArgs e)
    {
        bottomPanelMaximized = sender is ToggleButton toggle ? toggle.IsChecked == true : !bottomPanelMaximized;
        if (bottomPanelMaximized)
        {
            bottomPanelRequested = true;
            ApplyBottomPanelMaximizedLayout();
        }
        else
        {
            RestoreBottomPanelLayout();
        }
    }

    private void ApplyBottomPanelMaximizedLayout()
    {
        TaskboardShellOverlay.Visibility = Visibility.Collapsed;

        // Mirror assistant full view: Console owns the center workspace column only.
        // Left and right panels retain their independent shell state and toggles.
        MainWorkspacePanel.Visibility = Visibility.Collapsed;
        BottomPanelFullView.Visibility = Visibility.Visible;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(BottomPanelFullView, 4);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(BottomPanelFullView, 1);
        SynchronizeFullBottomPanel();

        BottomPanelMaximizeButton.IsChecked = true;
        BottomPanelToggleButton.IsChecked = true;
        BottomPanelMenuItem.IsChecked = true;
        UpdateLayoutToggleGlyphs();
    }

    private void RestoreBottomPanelLayout()
    {
        bottomPanelMaximized = false;
        BottomPanelMaximizeButton.IsChecked = false;
        BottomPanelFullView.Visibility = Visibility.Collapsed;
        MainWorkspacePanel.Visibility = Visibility.Visible;
        RenderCurrentRoute();
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
        UpdateAssistantLayout();
        ApplyBottomPanelVisibility();
    }

    private void SynchronizeFullBottomPanel()
    {
        FullTerminalOutputText.Text = TerminalOutputText.Text;
        FullOutputText.Text = OutputText.Text;
        FullProblemsDetailText.Text = ProblemsDetailText.Text;
        FullTerminalSurface.Visibility = TerminalSurface.Visibility;
        FullOutputSurface.Visibility = OutputSurface.Visibility;
        FullProblemsSurface.Visibility = ProblemsSurface.Visibility;
        FullTerminalTabButton.IsChecked = TerminalTabButton.IsChecked;
        FullOutputTabButton.IsChecked = OutputTabButton.IsChecked;
        FullProblemsTabButton.IsChecked = ProblemsTabButton.IsChecked;
    }

    private void OnFullTerminalTabClicked(object sender, RoutedEventArgs e)
    {
        SelectBottomSurface(BottomSurface.Terminal);
        SynchronizeFullBottomPanel();
    }

    private void OnFullOutputTabClicked(object sender, RoutedEventArgs e)
    {
        SelectBottomSurface(BottomSurface.Output);
        SynchronizeFullBottomPanel();
    }

    private void OnFullProblemsTabClicked(object sender, RoutedEventArgs e)
    {
        SelectBottomSurface(BottomSurface.Problems);
        SynchronizeFullBottomPanel();
    }

    private void OnRestoreBottomPanelClicked(object sender, RoutedEventArgs e) => RestoreBottomPanelLayout();

    private void OnFullTerminalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var command = FullTerminalCommandBox.Text.Trim();
        FullTerminalCommandBox.Text = string.Empty;
        ExecuteLocalTerminalCommand(command);
        SynchronizeFullBottomPanel();
        FullTerminalScrollViewer.ChangeView(null, double.MaxValue, null, true);
        e.Handled = true;
    }

    private void OnBottomPanelToggleClicked(object sender, RoutedEventArgs e)
    {
        bottomPanelRequested = sender switch
        {
            ToggleButton toggle => toggle.IsChecked == true,
            ToggleMenuFlyoutItem item => item.IsChecked,
            _ => !bottomPanelRequested,
        };
        ApplyBottomPanelVisibility();
    }

    private void ApplyBottomPanelVisibility()
    {
        if (bottomPanelMaximized)
        {
            if (!bottomPanelRequested)
            {
                RestoreBottomPanelLayout();
                return;
            }
            ApplyBottomPanelMaximizedLayout();
            return;
        }
        var visible = bottomPanelRequested && WorkspaceHost.ActualHeight >= 500;
        BottomPanelRow.Height = new GridLength(visible ? requestedBottomPanelHeight : 0);
        BottomSplitterRow.Height = new GridLength(visible ? ResponsiveLayoutPolicy.PaneDividerThickness : 0);
        BottomPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        BottomSplitter.Visibility = BottomPanel.Visibility;
        BottomPanelToggleButton.IsChecked = visible;
        BottomPanelMenuItem.IsChecked = bottomPanelRequested;
        UpdateLayoutToggleGlyphs();
    }

    private void ShowBottomPanel()
    {
        bottomPanelRequested = true;
        ApplyBottomPanelVisibility();
    }

    private void ApplyResponsiveShell(ResponsiveLayoutBand band)
    {
        var showLeft =
            leftPanelRequested &&
            !AssistantOwnsWorkbench;
        LeftPanelColumn.Width = new GridLength(showLeft ? requestedLeftPanelWidth : 0);
        LeftSplitterColumn.Width = new GridLength(showLeft ? ResponsiveLayoutPolicy.PaneDividerThickness : 0);
        LeftSidebar.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        LeftSplitter.Visibility = LeftSidebar.Visibility;
        LeftPanelToggleButton.IsChecked = showLeft;
        LeftPanelMenuItem.IsChecked = leftPanelRequested;
        MediationButton.Foreground = showLeft
            ? (Brush)Application.Current.Resources["ShellSuccessBrush"]
            : (Brush)Application.Current.Resources["MutedTextBrush"];
        ApplyBottomPanelVisibility();
        if (bottomPanelMaximized) ApplyBottomPanelMaximizedLayout();
    }

    private void UpdateLayoutToggleGlyphs()
    {
        LeftPanelToggleFill.Visibility = LeftPanelToggleButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        BottomPanelToggleFill.Visibility = BottomPanelToggleButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        AssistantToggleFill.Visibility = AssistantToggleButton.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLeftSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        requestedLeftPanelWidth = Math.Clamp(requestedLeftPanelWidth + e.HorizontalChange, 220, 420);
        LeftPanelColumn.Width = new GridLength(requestedLeftPanelWidth);
    }

    private void OnLeftSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Home) requestedLeftPanelWidth = 286;
        else if (e.Key == VirtualKey.Left) requestedLeftPanelWidth -= 24;
        else if (e.Key == VirtualKey.Right) requestedLeftPanelWidth += 24;
        else return;
        requestedLeftPanelWidth = Math.Clamp(requestedLeftPanelWidth, 220, 420);
        LeftPanelColumn.Width = new GridLength(requestedLeftPanelWidth);
        e.Handled = true;
    }

    private void OnBottomSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        requestedBottomPanelHeight = Math.Clamp(requestedBottomPanelHeight - e.VerticalChange, 120, Math.Max(120, WorkspaceHost.ActualHeight - 180));
        BottomPanelRow.Height = new GridLength(requestedBottomPanelHeight);
    }

    private void OnBottomSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Home) requestedBottomPanelHeight = 220;
        else if (e.Key == VirtualKey.Up) requestedBottomPanelHeight += 24;
        else if (e.Key == VirtualKey.Down) requestedBottomPanelHeight -= 24;
        else return;
        requestedBottomPanelHeight = Math.Clamp(requestedBottomPanelHeight, 120, Math.Max(120, WorkspaceHost.ActualHeight - 180));
        BottomPanelRow.Height = new GridLength(requestedBottomPanelHeight);
        e.Handled = true;
    }

    private void OnResetLayoutClicked(object sender, RoutedEventArgs e)
    {
        requestedLeftPanelWidth = 286;
        requestedBottomPanelHeight = 220;
        session?.Assistant.Resize(AssistantSessionState.DefaultPanelWidth);
        leftPanelRequested = bottomPanelRequested = true;
        ShowLeftPanel();
        ShowBottomPanel();
        if (session is not null) { session.Assistant.Expand(); UpdateAssistantLayout(); }
    }

    private void OnCompactDensityClicked(object sender, RoutedEventArgs e)
    {
        var compact = sender is ToggleMenuFlyoutItem { IsChecked: true };
        requestedLeftPanelWidth = compact ? 286 : 320;
        requestedBottomPanelHeight = compact ? 220 : 260;
        ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void OnTerminalTabClicked(object sender, RoutedEventArgs e) => SelectBottomSurface(BottomSurface.Terminal);
    private void OnOutputTabClicked(object sender, RoutedEventArgs e) => SelectBottomSurface(BottomSurface.Output);
    private void OnProblemsTabClicked(object sender, RoutedEventArgs e) => SelectBottomSurface(BottomSurface.Problems);

    private void SelectBottomSurface(BottomSurface surface)
    {
        ShowBottomPanel();
        TerminalSurface.Visibility = surface == BottomSurface.Terminal ? Visibility.Visible : Visibility.Collapsed;
        OutputSurface.Visibility = surface == BottomSurface.Output ? Visibility.Visible : Visibility.Collapsed;
        ProblemsSurface.Visibility = surface == BottomSurface.Problems ? Visibility.Visible : Visibility.Collapsed;
        TerminalTabButton.IsChecked = surface == BottomSurface.Terminal;
        OutputTabButton.IsChecked = surface == BottomSurface.Output;
        ProblemsTabButton.IsChecked = surface == BottomSurface.Problems;
        foreach (var tab in new[] { TerminalTabButton, OutputTabButton, ProblemsTabButton })
        {
            var selected = tab.IsChecked == true;
            tab.Foreground = selected ? (Brush)Application.Current.Resources["ShellAccentBrush"] :
                new SolidColorBrush(Windows.UI.Color.FromArgb(255, 204, 204, 204));
            tab.BorderBrush = selected ? (Brush)Application.Current.Resources["ShellAccentBrush"] : new SolidColorBrush(Colors.Transparent);
            tab.BorderThickness = selected ? new Thickness(0, 0, 0, 2) : new Thickness(0);
        }
        if (bottomPanelMaximized) SynchronizeFullBottomPanel();
    }

    private void OnTerminalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var command = TerminalCommandBox.Text.Trim();
        TerminalCommandBox.Text = string.Empty;
        ExecuteLocalTerminalCommand(command);
        e.Handled = true;
    }

    private void ExecuteLocalTerminalCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        AppendTerminal($"PS GRID> {command}\n");
        switch (command.ToLowerInvariant())
        {
            case "help":
                AppendTerminal("Available: help, clear, status, games, history, health\nCommands inspect Grid's in-memory state only; no arbitrary shell execution is exposed.\n");
                break;
            case "clear":
                TerminalOutputText.Text = string.Empty;
                break;
            case "status":
                AppendTerminal($"Surface: {activeSurface}\nManager: {connectionStatusDetail}\nCorrective actions: none recorded\n");
                break;
            case "games":
                var games = session?.Catalog.Games.Where(game => !game.Installations.IsEmpty).Select(game => game.Name).ToArray() ?? [];
                AppendTerminal(games.Length == 0 ? "No connected games.\n" : string.Join("\n", games) + "\n");
                break;
            case "history":
                AppendTerminal($"Recorded actions: {session?.History.Entries.Length ?? 0}\n");
                break;
            case "health":
                AppendTerminal("Read-only health surface ready. No diagnostic case is active; Grid will not invent findings.\n");
                break;
            default:
                AppendTerminal($"Unknown Grid-local command: {command}. Type 'help'.\n");
                break;
        }
    }

    private void AppendTerminal(string text)
    {
        TerminalOutputText.Text += text;
        TerminalScrollViewer.ChangeView(null, double.MaxValue, null, true);
        if (bottomPanelMaximized) SynchronizeFullBottomPanel();
    }

    private void OnClearTerminalClicked(object sender, RoutedEventArgs e)
    {
        TerminalOutputText.Text = "Grid local terminal · deterministic commands only\n";
        if (bottomPanelMaximized) SynchronizeFullBottomPanel();
    }

    private async void OnOpenExplorerClicked(object sender, RoutedEventArgs e) => await ShowMessageAsync(
        "Explorer boundary",
        "Grid has not been granted an external folder-launch route for this context. The selected manager remains the authority for opening its files.");

    private async void OnOpenManagerInstanceClicked(object sender, RoutedEventArgs e)
    {
        var requested = (sender as FrameworkElement)?.Tag?.ToString() ?? "selected manager";
        await ShowManagerBoundaryAsync(requested);
    }

    private Task ShowManagerBoundaryAsync(string requested) => ShowMessageAsync(
        $"Open in {requested}",
        "The manager instance is visible as read-only context, but this build has no verified external activation bridge. Grid did not launch or change anything.");

    private async void OnRefreshMenuClicked(object sender, RoutedEventArgs e)
    {
        if (session?.Shell.CurrentSelection.InstallationId is InstallationId id)
            await RefreshInstallationAsync(id);
        else
            await ShowMessageAsync("Nothing to refresh", "Open a connected game workstation to refresh its read-only observations.");
    }

    private async void OnMediationClicked(object sender, RoutedEventArgs e)
    {
        if (activeSurface == ShellSurface.Workstation &&
            session?.Assistant.GetActionableRepairTask(
                session.Shell.CurrentSelection.InstallationId,
                session.Shell.CurrentSelection.ProfileId) is { } repairTask)
        {
            session.Assistant.OpenTask(repairTask.Summary.Id);
            session.Assistant.Expand();
            RefreshAssistantContext();
            UpdateAssistantLayout(true);
            return;
        }

        SelectBottomSurface(BottomSurface.Problems);
        await ShowMessageAsync("No corrective actions", "Grid has no evidence-backed corrective action to review for this surface. Findings will never be fabricated.");
    }

    private void OnNotificationsClicked(object sender, RoutedEventArgs e)
    {
        unreadNotificationCount = 0;
        UpdateNotificationPresentation();
    }

    private void OnClearNotificationsClicked(object sender, RoutedEventArgs e)
    {
        notifications.Clear();
        unreadNotificationCount = 0;
        UpdateNotificationPresentation();
    }

    private void RaiseNotification(string title, string message, InfoBarSeverity severity)
    {
        if (notifications.Any(item => item.Title == title && item.Message == message))
        {
            return;
        }

        var glyph = severity switch
        {
            InfoBarSeverity.Success => "\uE73E",
            InfoBarSeverity.Warning => "\uE7BA",
            InfoBarSeverity.Error => "\uEA39",
            _ => "\uE946",
        };
        notifications.Insert(0, new(title, message, DateTimeOffset.Now.ToString("t"), glyph));
        while (notifications.Count > 50)
        {
            notifications.RemoveAt(notifications.Count - 1);
        }

        unreadNotificationCount = Math.Min(99, unreadNotificationCount + 1);
        UpdateNotificationPresentation();
    }

    private void UpdateNotificationPresentation()
    {
        var hasNotifications = notifications.Count > 0;
        NotificationList.Visibility = hasNotifications ? Visibility.Visible : Visibility.Collapsed;
        NoNotificationsText.Visibility = hasNotifications ? Visibility.Collapsed : Visibility.Visible;
        NotificationBadge.Visibility = unreadNotificationCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotificationCountText.Text = unreadNotificationCount > 9 ? "9+" : unreadNotificationCount.ToString();
        AutomationProperties.SetHelpText(
            NotificationsButton,
            unreadNotificationCount == 0
                ? $"{notifications.Count} notification{(notifications.Count == 1 ? string.Empty : "s")}, none unread"
                : $"{unreadNotificationCount} unread notification{(unreadNotificationCount == 1 ? string.Empty : "s")}");
    }

    private async void OnHelpClicked(object sender, RoutedEventArgs e) => await ShowMessageAsync(
        "Safety boundaries",
        "Grid separates read-only observation from proposed, approved, snapshotted, verified, and recoverable actions. This build exposes no arbitrary shell or autonomous mutation route.");

    private async void OnAboutClicked(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unavailable";
        await ShowMessageAsync("About Grid", $"Grid · deterministic game and mod workspace · version {version}");
    }

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();

    private void RenderCurrentRoute()
    {
        if (session is null)
        {
            return;
        }
        session.SynchronizeContext();
        if (taskboardExpanded)
        {
            RenderTaskboard();
            return;
        }

        if (activeSurface == ShellSurface.Welcome)
        {
            ContextBar.Visibility = Visibility.Collapsed;
            NavigateFrame(typeof(WelcomePage));
            if (ContentFrame.Content is WelcomePage welcome)
            {
                welcome.BindContext(session.Catalog, () => _ = BeginAddGameAsync(), OpenSetupFromWelcome,
                    CompleteFirstRunSetup, SkipFirstRunSetup);
            }
            RefreshAssistantContext();
            UpdateShellPresentation();
            return;
        }

        if (session.Shell.CurrentRoute == ShellRoute.GameWorkspace)
        {
            activeSurface = ShellSurface.Workstation;
            RenderGameWorkspace();
            UpdateShellPresentation();
            return;
        }

        ContextBar.Visibility = Visibility.Collapsed;
        activeSurface = session.Shell.CurrentRoute switch
        {
            ShellRoute.History => ShellSurface.Activities,
            ShellRoute.Settings => ShellSurface.Settings,
            _ when activeSurface == ShellSurface.Games => ShellSurface.Games,
            _ => ShellSurface.Home,
        };
        var destination = session.Shell.CurrentRoute switch
        {
            ShellRoute.History => typeof(HistoryPage),
            ShellRoute.Settings => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        NavigateFrame(destination);
        if (ContentFrame.Content is HomePage home)
        {
            home.BindContext(session.Catalog, composition.IsDemo, OpenGame, () => _ = BeginAddGameAsync());
        }
        else if (ContentFrame.Content is HistoryPage history)
        {
            history.BindContext(session.History);
        }
        else if (ContentFrame.Content is SettingsPage settings)
        {
            settings.BindContext(session.Catalog, session.FidelityAudit?.Snapshot,
                session.ExternalLaunch?.CurrentSession, composition.Mode, null, session.History.Issue,
                session.Assistant.ProviderAvailability, composition.SourceAcquisitionPreferencesStore, composition.NexusCredentialStore);
        }
        RefreshAssistantContext();
        RefreshActivitySurface();
        UpdateShellPresentation();
    }

    private void UpdateShellPresentation()
    {
        if (session is null) return;
        var selectedGameId = session.Shell.CurrentSelection.GameId;
        var selectedGame = selectedGameId is GameId gameId
            ? session.Catalog.Games.FirstOrDefault(game => game.Id == gameId) : null;
        var pageTitle = activeSurface switch
        {
            ShellSurface.Welcome => "Welcome",
            ShellSurface.Home => "Home",
            ShellSurface.Games => "Games",
            ShellSurface.Workstation => selectedGame?.Name ?? "Workstation",
            ShellSurface.Activities => "Activities",
            ShellSurface.Settings => "Settings",
            _ => "Home",
        };
        var selectedInstallation = selectedGame?.Installations
            .FirstOrDefault(value => value.Id == session.Shell.CurrentSelection.InstallationId);
        var selectedProfile = selectedInstallation?.Profiles
            .FirstOrDefault(value => value.Id == session.Shell.CurrentSelection.ProfileId);
        var contextTitle = activeSurface == ShellSurface.Workstation
            ? selectedProfile?.Name ?? selectedGame?.Name ?? pageTitle
            : pageTitle;
        GlobalSearchBox.PlaceholderText = taskboardExpanded ? "activity" : contextTitle;
        foreach (var pair in gameItems)
        {
            var selected = pair.Key == selectedGameId && activeSurface == ShellSurface.Workstation;
            pair.Value.Background = selected ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 4, 57, 94)) : new SolidColorBrush(Colors.Transparent);
            pair.Value.BorderBrush = selected ? (Brush)Application.Current.Resources["ShellAccentBrush"] : new SolidColorBrush(Colors.Transparent);
        }

        var installation = selectedGame?.Installations.FirstOrDefault(value => value.Id == session.Shell.CurrentSelection.InstallationId);
        if (installation is null)
        {
            connectionStatusDetail = "No manager connected";
            ConnectionDot.Fill = (Brush)Application.Current.Resources["MutedTextBrush"];
        }
        else
        {
            connectionStatusDetail = $"{installation.Name} · {installation.Metadata.Availability}";
            var available = installation.Metadata.Availability == InstallationAvailability.Available;
            ConnectionDot.Fill = available ? (Brush)Application.Current.Resources["ShellSuccessBrush"] :
                (Brush)Application.Current.Resources["ShellWarningBrush"];
        }
        ToolTipService.SetToolTip(ConnectionStatusButton, connectionStatusDetail);
        AutomationProperties.SetHelpText(ConnectionStatusButton, connectionStatusDetail);

        BindOfflineAlertPresentation();
        var repairTask = activeSurface == ShellSurface.Workstation
            ? session.Assistant.GetActionableRepairTask(
                session.Shell.CurrentSelection.InstallationId,
                session.Shell.CurrentSelection.ProfileId)
            : null;
        MediationButton.Content = repairTask is null ? "No corrective actions" : "Review 1 corrective action";
        ToolTipService.SetToolTip(MediationButton,
            repairTask?.RepairAvailability?.Detail ?? "No evidence-backed corrective action is available for this installation and profile.");
    }

    private void BindOfflineAlertPresentation()
    {
        var index = activeSurface == ShellSurface.Workstation ? session?.OfflineAlerts?.Snapshot : null;
        var unresolved = index?.Alerts
            .Where(alert => alert.State is OfflineAlertState.Active or OfflineAlertState.Acknowledged)
            .ToArray() ?? [];
        var discrepancies = unresolved.Count(alert => alert.IsBlocking || alert.Severity == OfflineAlertSeverity.Error);
        var concerns = unresolved.Length - discrepancies;
        DiscrepancyCountText.Text = $"\u00D7{discrepancies}";
        ConcernCountText.Text = $"!{concerns}";
        ToolTipService.SetToolTip(DiscrepancyCountText, $"{discrepancies} current blocking or error alert{(discrepancies == 1 ? string.Empty : "s")}");
        ToolTipService.SetToolTip(ConcernCountText, $"{concerns} current warning or advisory alert{(concerns == 1 ? string.Empty : "s")}");

        if (index is null)
        {
            ProblemsDetailText.Text = "No normalized offline alert index is available for this surface.";
            return;
        }

        var stale = index.Alerts.Count(alert => alert.State == OfflineAlertState.Stale);
        var accounted = index.Alerts.Count(alert => alert.State == OfflineAlertState.AccountedFor);
        ProblemsDetailText.Text = unresolved.Length == 0 && stale == 0
            ? "The latest normalized offline index contains no unresolved alerts."
            : $"{discrepancies} discrepancies · {concerns} concerns · {accounted} accounted for · {stale} stale.";

        foreach (var alert in index.Alerts.Where(alert => alert.State == OfflineAlertState.Active).Take(50))
        {
            var severity = alert.Severity switch
            {
                OfflineAlertSeverity.Error => InfoBarSeverity.Error,
                OfflineAlertSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            };
            RaiseNotification(alert.Title, alert.Detail, severity);
        }
    }

    private void OpenGame(GameId id)
    {
        if (session is null)
        {
            return;
        }
        session.Shell.NavigateGame(id);
        PersistWorkspaceSelection();
        activeSurface = ShellSurface.Workstation;
        OpenOrFocusEditorTab(ShellSurface.Workstation);
        RecordNavigation(CreateLocation(ShellSurface.Workstation));
        RenderCurrentRoute();
        ShowLeftPanel();
        ShowSidePanel(GameSnapshotPanel, "GAME SNAPSHOT", id);
        if (gameItems.TryGetValue(id, out var item)) item.Focus(FocusState.Programmatic);
    }

    private void RenderGameWorkspace()
    {
        if (session?.Shell.CurrentSelection.GameId is not GameId gameId)
        {
            session?.Shell.NavigateHome();
            RenderCurrentRoute();
            return;
        }
        var game = session.Catalog.Games.FirstOrDefault(value => value.Id == gameId);
        if (game is null)
        {
            session.Shell.NavigateHome();
            RenderCurrentRoute();
            return;
        }
        var selection = session.Shell.CurrentSelection;
        var installation = selection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(value => value.Id == installationId) : null;
        var profile = installation is not null && selection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(value => value.Id == profileId) : null;
        SnapshotGameName.Text = game.Name;
        SnapshotInstanceText.Text = installation is null
            ? "No instance selected"
            : $"{installation.Name} · {installation.Metadata.Availability}";
        SnapshotProfileText.Text = profile?.Name ?? "No profile selected";
        session.SynchronizeContext();
        BindContextBar(game, installation, profile);
        NavigateFrame(typeof(GameWorkspacePage));
        if (ContentFrame.Content is GameWorkspacePage workspace)
        {
            workspace.BindContext(
                game, installation, profile, session.Catalog.SourceKind, session.Workspace,
                session.Environment, session.ToolOutputs, session.OfflineAlerts,
                composition.Mo2ToolOutputQueryService as Mo2WorkspaceToolOutputQueryService,
                composition.WorkspacePresentationStore,
                composition.Mo2ModStateMutationService,
                OnWorkspaceCatalogChanged, RefreshConnectedWorkspaceCatalogAsync,
                RefreshAssistantContext, RaiseNotification, composition.IsDemo);
        }
        if (installation?.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference && profile is not null &&
            session.ToolOutputs is { Snapshot: null, IsRefreshing: false })
        {
            _ = RefreshToolOutputsAsync();
        }
        RefreshAssistantContext();
    }

    private void BindContextBar(ManagedGame game, ManagedInstallation? installation, Profile? profile)
    {
        ContextBar.Visibility = Visibility.Visible;
        ContextIdentityPanel.Visibility = Visibility.Collapsed;
        InstallationContextPanel.Visibility = Visibility.Collapsed;
        ProfileContextPanel.Visibility = Visibility.Collapsed;
        GameContextName.Text = profile?.Name ?? game.Name;
        var activeModCount = profile?.Mods.Count(mod => mod.Kind == ModEntryKind.Mod && mod.IsEnabled) ?? 0;
        InstallationSummaryText.Text = profile is null
            ? installation is null ? "No installation selected" : $"{installation.Name} · {installation.Metadata.Availability}"
            : $"Active {activeModCount}";
        suppressSelectors = true;
        try
        {
            InstallationSelector.ItemsSource = game.Installations;
            InstallationSelector.SelectedItem = installation;
            var profiles = installation?.Metadata.Availability == InstallationAvailability.Available
                ? installation.Profiles.Where(value => value.Lifecycle == ProfileLifecycleState.Available).ToArray() : [];
            ProfileSelector.ItemsSource = profiles;
            ProfileSelector.SelectedItem = profile;
            ProfileSelector.IsEnabled = profiles.Length > 0;
            ProfileSelector.PlaceholderText = profiles.Length == 0 ? "No observed profiles" : "Select profile";
            BindTools(installation);
        }
        finally
        {
            suppressSelectors = false;
        }
        ApplyContextLayout(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
            session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
    }

    private void BindTools(ManagedInstallation? installation)
    {
        if (session is null)
        {
            return;
        }
        if (installation?.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference)
        {
            var items = new[] { ToolTargetPresentation.ForManagement() }
                .Concat((session.ToolOutputs?.Snapshot?.Executables ?? [])
                    .Select(ToolTargetPresentation.ForObserved)).ToArray();
            launchTargetItems = items;
            LaunchTargetSelector.ItemsSource = items;
            LaunchTargetSelector.SelectedItem = session.ToolOutputs?.SelectedExecutableId is ObservedExecutableId id
                ? items.FirstOrDefault(value => value.Executable?.Id == id) : null;
            LaunchTargetSelector.IsEnabled = true;
            LaunchTargetSelector.PlaceholderText = items.Length == 1 ? "Manage or select a tool" : "Select tool";
            UpdateLaunchTargetButtonPresentation();
            RunButton.IsEnabled = false;
            AutomationProperties.SetHelpText(RunButton, "Launching is not implemented in this shell stage.");
            return;
        }
        var targets = new[] { ToolTargetPresentation.ForManagement() }
            .Concat(composition.IsDemo ? session.LaunchTargets.GetTargets().Select(ToolTargetPresentation.ForDemo) : []).ToArray();
        launchTargetItems = targets;
        LaunchTargetSelector.ItemsSource = targets;
        LaunchTargetSelector.SelectedItem = targets.FirstOrDefault(value => value.LaunchTarget?.Definition.Id == session.LaunchTargets.SelectedTargetId);
        LaunchTargetSelector.IsEnabled = true;
        LaunchTargetSelector.PlaceholderText = targets.Length == 1 ? "Manage" : "Select target";
        UpdateLaunchTargetButtonPresentation();
        RunButton.IsEnabled = false;
    }

    private void OnLaunchTargetFlyoutOpening(object sender, object e)
    {
        LaunchTargetFlyout.Items.Clear();
        foreach (var target in launchTargetItems)
        {
            var item = new MenuFlyoutItem
            {
                Text = target.Name,
                Tag = target,
                MinWidth = Math.Max(0, LaunchTargetButton.ActualWidth - 12),
            };
            ToolTipService.SetToolTip(item, target.PresentationStatus);
            item.Click += OnLaunchTargetMenuItemClicked;
            LaunchTargetFlyout.Items.Add(item);
        }
    }

    private void OnLaunchTargetMenuItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ToolTargetPresentation target })
        {
            LaunchTargetSelector.SelectedItem = target;
        }
    }

    private async void OnRefreshEnvironmentClicked(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.Content is not GameWorkspacePage workspace)
        {
            return;
        }

        RefreshEnvironmentButton.IsEnabled = false;
        try
        {
            await workspace.RefreshEnvironmentAsync();
        }
        finally
        {
            RefreshEnvironmentButton.IsEnabled = true;
        }
    }

    private void UpdateLaunchTargetButtonPresentation()
    {
        LaunchTargetButton.IsEnabled = LaunchTargetSelector.IsEnabled;
        LaunchTargetButtonText.Text = (LaunchTargetSelector.SelectedItem as ToolTargetPresentation)?.Name
            ?? LaunchTargetSelector.PlaceholderText
            ?? "Unavailable";
    }

    private void OnInstallationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectors || session is null) return;
        session.Shell.SelectInstallation((InstallationSelector.SelectedItem as ManagedInstallation)?.Id);
        PersistWorkspaceSelection();
        OpenOrFocusEditorTab(ShellSurface.Workstation);
        RecordNavigation(CreateLocation(ShellSurface.Workstation));
        RenderGameWorkspace();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressSelectors || session is null) return;
        session.Shell.SelectProfile((ProfileSelector.SelectedItem as Profile)?.Id);
        PersistWorkspaceSelection();
        OpenOrFocusEditorTab(ShellSurface.Workstation);
        RecordNavigation(CreateLocation(ShellSurface.Workstation));
        RenderGameWorkspace();
    }

    private async void OnLaunchTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLaunchTargetButtonPresentation();
        if (suppressSelectors || session is null) return;
        var item = LaunchTargetSelector.SelectedItem as ToolTargetPresentation;
        if (item?.IsManagementAction == true)
        {
            suppressSelectors = true;
            LaunchTargetSelector.SelectedItem = null;
            suppressSelectors = false;
            UpdateLaunchTargetButtonPresentation();
            await ShowManageGameAsync();
            return;
        }
        if (item?.Executable is { } executable) session.ToolOutputs?.SelectExecutable(executable.Id);
        else session.LaunchTargets.SelectTarget(item?.LaunchTarget?.Definition.Id);
        RefreshAssistantContext();
    }

    private async void OnManageGameClicked(object sender, RoutedEventArgs e)
    {
        if (session?.Shell.CurrentSelection.GameId is not GameId)
        {
            await ShowMessageAsync("No game selected", "Open a connected game workstation before managing its Grid connection.");
            return;
        }
        await ShowManageGameAsync();
    }

    private async Task ShowManageGameAsync()
    {
        if (session?.Shell.CurrentSelection.GameId is not GameId gameId) return;
        var game = session.Catalog.Games.First(value => value.Id == gameId);
        var dialog = new GameManagementDialog(game, session.Shell.CurrentSelection.InstallationId, CanAddMo2(gameId)) { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
        if (dialog.RequestedAction is not { } request) return;
        try
        {
            switch (request.Kind)
            {
                case GameManagementActionKind.ConnectAnotherInstallation: await BeginAddGameAsync(gameId); break;
                case GameManagementActionKind.RefreshExternalObservations: await RefreshInstallationAsync(request.InstallationId!.Value); break;
                case GameManagementActionKind.AuthorizeRequiredRoots: await AuthorizeRequiredRootsAsync(request.InstallationId!.Value); break;
                case GameManagementActionKind.AuthorizeProfilesRoot: await AuthorizeProfilesAsync(request.InstallationId!.Value); break;
                case GameManagementActionKind.AuthorizeModsRoot: await AuthorizeModsAsync(request.InstallationId!.Value); break;
                case GameManagementActionKind.AuthorizeGameDirectory: await AuthorizeContentAsync(request.InstallationId!.Value, Mo2ContentRootKind.GameDirectory); break;
                case GameManagementActionKind.AuthorizeGameData: await AuthorizeContentAsync(request.InstallationId!.Value, Mo2ContentRootKind.GameData); break;
                case GameManagementActionKind.AuthorizeOverwrite: await AuthorizeContentAsync(request.InstallationId!.Value, Mo2ContentRootKind.Overwrite); break;
                case GameManagementActionKind.AuthorizeGameSettings: await AuthorizeContentAsync(request.InstallationId!.Value, Mo2ContentRootKind.GlobalGameSettings); break;
                case GameManagementActionKind.DisconnectFromGrid: await ConfirmDisconnectAsync(request.InstallationId!.Value); break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await ShowMessageAsync("Action unavailable", exception.Message);
        }
    }

    private void PersistWorkspaceSelection()
    {
        if (session is null)
        {
            return;
        }

        _ = composition.WorkspaceSelectionStore?.Save(session.Shell.CurrentSelection);
    }

    private ShellLocation CreateLocation(ShellSurface surface)
    {
        var selection = surface == ShellSurface.Workstation && session is not null
            ? session.Shell.CurrentSelection
            : WorkspaceSelection.Empty;
        return new ShellLocation(surface, selection, CreateTabKey(surface, selection));
    }

    private void RestoreLocation(ShellLocation location)
    {
        if (session is null || location.Surface != ShellSurface.Workstation || location.Selection.GameId is not GameId gameId)
        {
            return;
        }

        session.Shell.NavigateGame(gameId);
        if (location.Selection.InstallationId is InstallationId installationId)
        {
            session.Shell.SelectInstallation(installationId);
        }
        if (location.Selection.ProfileId is ProfileId profileId)
        {
            session.Shell.SelectProfile(profileId);
        }
        PersistWorkspaceSelection();
    }

    private void OpenOrFocusEditorTab(ShellSurface surface)
    {
        if (session is null) return;
        var selection = surface == ShellSurface.Workstation ? session.Shell.CurrentSelection : WorkspaceSelection.Empty;
        var key = CreateTabKey(surface, selection);
        var existing = editorTabs.FirstOrDefault(tab => tab.Key.Equals(key, StringComparison.Ordinal));
        var tab = existing ?? new EditorTabRecord(key, surface, CreateTabTitle(surface, selection), selection);
        if (existing is null)
        {
            editorTabs.Add(tab);
            var item = new TabViewItem
            {
                Header = tab.Title,
                Tag = tab,
                IsClosable = true,
                Style = (Style)Application.Current.Resources["ShellTabViewItemStyle"],
            };
            AutomationProperties.SetName(item, $"{tab.Title} tab");
            EditorTabView.TabItems.Add(item);
        }

        var target = EditorTabView.TabItems
            .OfType<TabViewItem>()
            .FirstOrDefault(item => item.Tag is EditorTabRecord record && record.Key.Equals(key, StringComparison.Ordinal));
        if (target is null) return;
        suppressEditorTabSelection = true;
        try { EditorTabView.SelectedItem = target; }
        finally { suppressEditorTabSelection = false; }
    }

    private string CreateTabKey(ShellSurface surface, WorkspaceSelection selection) => surface switch
    {
        ShellSurface.Welcome => "welcome",
        ShellSurface.Home or ShellSurface.Games => "home",
        ShellSurface.Activities => "activities",
        ShellSurface.Settings => "settings",
        ShellSurface.Workstation => $"workstation:{selection.GameId?.Value ?? "none"}:{selection.InstallationId?.Value ?? "none"}:{selection.ProfileId?.Value ?? "none"}",
        _ => surface.ToString().ToLowerInvariant(),
    };

    private string CreateTabTitle(ShellSurface surface, WorkspaceSelection selection)
    {
        if (session is null) return surface.ToString();
        if (surface == ShellSurface.Welcome) return "Welcome";
        if (surface is ShellSurface.Home or ShellSurface.Games) return "Home";
        if (surface == ShellSurface.Activities) return "Activities";
        if (surface == ShellSurface.Settings) return "Settings";
        if (surface != ShellSurface.Workstation) return surface.ToString();

        var game = selection.GameId is GameId gameId
            ? session.Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId)
            : null;
        var installation = game is not null && selection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(candidate => candidate.Id == installationId)
            : null;
        var profile = installation is not null && selection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(candidate => candidate.Id == profileId)
            : null;
        return profile?.Name ?? installation?.Name ?? game?.Name ?? "Workstation";
    }

    private void OnEditorTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressEditorTabSelection || session is null || EditorTabView.SelectedItem is not TabViewItem { Tag: EditorTabRecord tab })
        {
            return;
        }

        RestoreLocation(new ShellLocation(tab.Surface, tab.Selection, tab.Key));
        NavigateSurface(tab.Surface, tab.Selection.GameId, record: false, synchronizeTab: false);
        RecordNavigation(CreateLocation(tab.Surface));
    }

    private void OnEditorTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Tab.Tag is not EditorTabRecord tab) return;
        editorTabs.RemoveAll(candidate => candidate.Key.Equals(tab.Key, StringComparison.Ordinal));
        sender.TabItems.Remove(args.Tab);
        if (editorTabs.Count == 0)
        {
            NavigateSurface(ShellSurface.Home);
        }
    }

    private void OpenSetupFromWelcome(GameId gameId, InstallationId installationId, ProfileId? profileId)
    {
        if (session is null) return;
        session.Shell.NavigateGame(gameId);
        session.Shell.SelectInstallation(installationId);
        if (profileId is ProfileId selectedProfileId)
        {
            session.Shell.SelectProfile(selectedProfileId);
        }
        PersistWorkspaceSelection();
        activeSurface = ShellSurface.Workstation;
        OpenOrFocusEditorTab(ShellSurface.Workstation);
        RecordNavigation(CreateLocation(ShellSurface.Workstation));
        RenderCurrentRoute();
        ShowLeftPanel();
        ShowSidePanel(GameSnapshotPanel, "GAME SNAPSHOT", gameId);
    }

    private void CompleteFirstRunSetup()
    {
        if (composition.FirstRunStateStore is null || !composition.FirstRunStateStore.MarkComplete())
        {
            _ = ShowMessageAsync("Setup could not be saved", "GRID could not persist first-run completion. No external game or manager state was changed.");
            return;
        }

        firstRunCompleted = true;
        NavigateSurface(ShellSurface.Home);
    }

    private void SkipFirstRunSetup() => NavigateSurface(ShellSurface.Home);

    private void OnRunClicked(object sender, RoutedEventArgs e) { }

    private async Task BeginAddGameAsync(GameId? requestedGame = null)
    {
        if (session is null || composition.IsDemo)
        {
            return;
        }
        if ((requestedGame is null || requestedGame == ProductionGridCatalogService.GrandTheftAutoVId) &&
            composition.ProviderDiscoveryService is not null && composition.GameRegistrationStore is not null)
        {
            var registrationDialog = new GameRegistrationDialog(
                composition.ProviderDiscoveryService, composition.GameRegistrationStore, pathPicker, composition.VortexConnectionStore)
            { XamlRoot = Content.XamlRoot };
            await registrationDialog.ShowAsync();
            if (registrationDialog.Registered.Count > 0)
            {
                var catalog = await catalogService.GetCatalogAsync(lifetime?.Token ?? default);
                session.Shell.ReplaceCatalog(catalog);
                PopulateGameItems(catalog);
                var registered = registrationDialog.Registered[0];
                session.Shell.NavigateGame(registered.GameId);
                session.Shell.SelectInstallation(registered.InstallationId);
                session.SynchronizeContext();
                PersistWorkspaceSelection();
                await RecordHistoryAsync(HistoryEventKind.InstallationConnected, HistoryEventStatus.Succeeded,
                    "Game installation registered", $"Grid registered {registrationDialog.Registered.Count} reviewed installation(s).",
                    registered.GameId, registered.InstallationId);
                activeSurface = ShellSurface.Workstation;
                OpenOrFocusEditorTab(ShellSurface.Workstation);
                RenderCurrentRoute();
                ShowLeftPanel();
                ShowSidePanel(GameSnapshotPanel, "GAME SNAPSHOT", registered.GameId);
            }
            return;
        }
        var gameId = requestedGame ?? session.Catalog.Games.FirstOrDefault(game => CanAddMo2(game.Id))?.Id;
        if (gameId is not GameId id || composition.Mo2Validator is null || composition.Mo2OnboardingCoordinator is null ||
            composition.Mo2DiscoveryOptions is null)
        {
            await ShowMessageAsync("No supported adapter", "No installation adapter is currently available.");
            return;
        }
        var dialog = new ExistingMo2ConnectionDialog(
            composition.Mo2Validator, composition.Mo2OnboardingCoordinator, pathPicker,
            composition.Mo2DiscoveryOptions with { ExpectedGameId = id }, ConnectedMo2AdapterId)
        { XamlRoot = Content.XamlRoot };
        try
        {
            await dialog.ShowAsync();
            if (dialog.ConnectedReference is { } reference)
            {
                await ReloadAfterConnectionAsync(reference);
            }
        }
        finally
        {
            RenderCurrentRoute();
        }
    }

    private bool CanAddMo2(GameId id) => !composition.IsDemo && session?.Catalog.Games
        .FirstOrDefault(game => game.Id == id)?.Adapters.Any(adapter => adapter.Id == ConnectedMo2AdapterId) == true;

    private async Task ReloadAfterConnectionAsync(Mo2InstallationReference reference)
    {
        if (session is null) return;
        var catalog = await catalogService.GetCatalogAsync(lifetime?.Token ?? default);
        session.Shell.ReplaceCatalog(catalog);
        PopulateGameItems(catalog);
        session.Shell.NavigateGame(reference.GameId);
        session.Shell.SelectInstallation(reference.InstallationId);
        session.SynchronizeContext();
        PersistWorkspaceSelection();
        await RecordHistoryAsync(HistoryEventKind.InstallationConnected, HistoryEventStatus.Succeeded,
            "Game installation connected", "Grid saved a reconnectable external installation reference.", reference.GameId, reference.InstallationId);
        await ResumeSessionAuthorizationAsync(reference);


        // Connection success is an intentional one-time transition: reveal the game snapshot
        // and the newly connected profile workstation, while Chat and Console stay closed.
        activeSurface = ShellSurface.Workstation;
        OpenOrFocusEditorTab(ShellSurface.Workstation);
        bottomPanelRequested = false;
        session.Assistant.Collapse();
        RenderCurrentRoute();
        ShowLeftPanel();
        ShowSidePanel(GameSnapshotPanel, "GAME SNAPSHOT", reference.GameId);
        ApplyBottomPanelVisibility();
        UpdateAssistantLayout();
    }

    private async Task ResumeSessionAuthorizationAsync(Mo2InstallationReference reference)
    {
        if (composition.Mo2OnboardingCoordinator is null) return;

        var cancellationToken = lifetime?.Token ?? default;
        var state = await composition.Mo2OnboardingCoordinator.ResumeAsync(reference, cancellationToken);

        while (state.Phase == Mo2OnboardingPhase.Authorize && !state.RequiredAuthorizations.IsEmpty)
        {
            var requirement = state.RequiredAuthorizations[0];
            var confirmation = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "Authorize read-only session access",
                Content = $"GRID needs read-only access to this exact path for the current application session:\n\n{requirement.Label}\n{requirement.ExactPath}\n\nGRID will not modify the selected path.",
                PrimaryButtonText = "Choose exact path",
                CloseButtonText = "Not now",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            var selected = await pathPicker.PickDirectoryAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(selected)) return;

            try
            {
                state = await composition.Mo2OnboardingCoordinator.AuthorizeRequiredAsync(
                    requirement.Label, selected, cancellationToken);
            }
            catch (UnauthorizedAccessException exception)
            {
                await ShowMessageAsync("Exact path required", exception.Message);
                continue;
            }

            await RecordAuthorizationAsync(reference.InstallationId, $"{requirement.Label} authorized for this session");
        }

        if (state.Phase != Mo2OnboardingPhase.SelectProfile) return;

        await RefreshInstallationAsync(reference.InstallationId);

        if (session?.Shell.CurrentSelection.ProfileId is not ProfileId) return;

        if (!firstRunCompleted && composition.FirstRunStateStore?.MarkComplete() == true)
            firstRunCompleted = true;
    }
    private async Task RefreshInstallationAsync(InstallationId installationId)
    {
        if (session is null) return;
        var catalog = await catalogService.GetCatalogAsync(lifetime?.Token ?? default);
        session.Shell.ReplaceCatalog(catalog);
        PopulateGameItems(catalog);
        session.SynchronizeContext();
        PersistWorkspaceSelection();
        await RecordHistoryAsync(HistoryEventKind.ExternalObservationRefreshed, HistoryEventStatus.Succeeded,
            "External observations refreshed", "Grid refreshed read-only evidence for the selected installation.",
            session.Shell.CurrentSelection.GameId, installationId);
        RenderCurrentRoute();
    }

    private ManagedInstallation FindInstallation(InstallationId id) => session!.Catalog.Games.SelectMany(game => game.Installations)
        .First(value => value.Id == id);

    private async Task ConfirmDisconnectAsync(InstallationId installationId)
    {
        var confirmation = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Disconnect from Grid?",
            Content = "Only Grid's local reference will be removed. MO2, profiles, mods, downloads, saves, tools, outputs, and the game remain unchanged.",
            PrimaryButtonText = "Disconnect from Grid",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        var installation = FindInstallation(installationId);
        if (installation.AdapterId == ConnectedMo2AdapterId)
        {
            if (installation.Metadata.ReferenceId is not InstallationReferenceId referenceId || composition.Mo2OnboardingCoordinator is null)
                throw new InvalidOperationException("The connected reference is unavailable.");
            var result = await composition.Mo2OnboardingCoordinator.DisconnectAsync(new(referenceId, installationId), lifetime?.Token ?? default);
            if (!result.Succeeded) throw new InvalidOperationException(result.Issues.FirstOrDefault()?.Message ?? "Disconnect failed.");
        }
        else if (composition.GameRegistrationStore is null || !await composition.GameRegistrationStore.RemoveAsync(installationId, lifetime?.Token ?? default))
        {
            throw new InvalidOperationException("The registered game reference is unavailable.");
        }
        var gameId = session!.Shell.CurrentSelection.GameId;
        var catalog = await catalogService.GetCatalogAsync(lifetime?.Token ?? default);
        session.Shell.ReplaceCatalog(catalog);
        PopulateGameItems(catalog);
        session.Shell.NavigateHome();
        session.SynchronizeContext();
        PersistWorkspaceSelection();
        await RecordHistoryAsync(HistoryEventKind.InstallationDisconnected, HistoryEventStatus.Succeeded,
            "Game installation disconnected", "Grid removed only its local connection reference.", gameId, installationId);
        RenderCurrentRoute();
    }

    private async Task AuthorizeProfilesAsync(InstallationId installationId)
    {
        var selected = await pathPicker.PickDirectoryAsync(lifetime?.Token ?? default);
        if (string.IsNullOrWhiteSpace(selected)) throw new OperationCanceledException();
        var installation = FindInstallation(installationId);
        var referenceId = installation.Metadata.ReferenceId ?? throw new InvalidOperationException("Reference unavailable.");
        composition.Mo2ProfileAuthorization?.AuthorizeProfilesRoot(referenceId, selected);
        composition.Mo2OnboardingCoordinator?.RegisterExactAuthorization(referenceId, selected);
        await RecordAuthorizationAsync(installationId, "Profiles root authorized for this session");
        await RefreshInstallationAsync(installationId);
    }

    private async Task AuthorizeRequiredRootsAsync(InstallationId installationId)
    {
        if (composition.Mo2OnboardingCoordinator is null || composition.Mo2ReferenceStore is null)
            throw new InvalidOperationException("The MO2 authorization service is unavailable.");

        var installation = FindInstallation(installationId);
        var referenceId = installation.Metadata.ReferenceId ?? throw new InvalidOperationException("Reference unavailable.");
        var references = await composition.Mo2ReferenceStore.LoadAsync(lifetime?.Token ?? default);
        var reference = references.References.SingleOrDefault(value => value.Id == referenceId && value.InstallationId == installationId)
            ?? throw new InvalidOperationException("The persisted MO2 reference no longer matches this installation.");
        var state = await composition.Mo2OnboardingCoordinator.ResumeAsync(reference, lifetime?.Token ?? default);
        var configuredRequirements = state.RequiredAuthorizations;
        var authorizedAny = false;

        if (!configuredRequirements.IsEmpty && await ConfirmExactReadRootsAsync(
            "Authorize configured MO2 roots?",
            configuredRequirements.Select(value => (value.Label, value.ExactPath))))
        {
            foreach (var requirement in configuredRequirements)
            {
                state = await composition.Mo2OnboardingCoordinator.AuthorizeRequiredAsync(
                    requirement.Label, requirement.ExactPath, lifetime?.Token ?? default);
            }
            authorizedAny = true;
            await RecordAuthorizationAsync(installationId, $"{configuredRequirements.Length} configured root(s) authorized for this session");
            await RefreshInstallationAsync(installationId);
        }

        if (session?.Shell.CurrentSelection is { GameId: GameId selectedGameId, ProfileId: ProfileId selectedProfileId } &&
            session.Shell.CurrentSelection.InstallationId == installationId && composition.Mo2ResolvedStateService is not null)
        {
            var refreshedInstallation = FindInstallation(installationId);
            var profile = refreshedInstallation.Profiles.SingleOrDefault(value => value.Id == selectedProfileId);
            if (profile is not null)
            {
                var context = new WorkspaceEnvironmentContext(
                    selectedGameId, installationId, selectedProfileId, session.Catalog.Revision,
                    profile.Observation?.Inventory?.Fingerprint);
                var contentRequirements = await composition.Mo2ResolvedStateService.GetAuthorizationRequirementsAsync(
                    context, lifetime?.Token ?? default);
                if (!contentRequirements.IsEmpty && await ConfirmExactReadRootsAsync(
                    "Authorize resolved content roots?",
                    contentRequirements.Select(value => (value.Label, value.ExpectedPath))))
                {
                    foreach (var requirement in contentRequirements)
                    {
                        if (!await composition.Mo2ResolvedStateService.AuthorizeExactRootAsync(
                            context, requirement.Kind, requirement.ExpectedPath, lifetime?.Token ?? default))
                            throw new InvalidOperationException($"The required {requirement.Label} changed during authorization review.");
                        composition.Mo2OnboardingCoordinator.RegisterExactAuthorization(referenceId, requirement.ExpectedPath);
                    }
                    authorizedAny = true;
                    await RecordAuthorizationAsync(installationId, $"{contentRequirements.Length} content root(s) authorized for this session");
                    await RefreshInstallationAsync(installationId);
                }
            }
        }

        if (!authorizedAny)
            await ShowMessageAsync("No authorization added", "No currently required exact read root was approved. Grid made no external change.");
    }

    private async Task<bool> ConfirmExactReadRootsAsync(string title, IEnumerable<(string Label, string Path)> roots)
    {
        var exactRoots = roots.ToArray();
        if (exactRoots.Length == 0) return false;
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Grid will read only these exact paths for this application session. This grants no repair or mutation authority.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var root in exactRoots)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{root.Label}\n{root.Path}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }
        var confirmation = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = "Authorize listed roots",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await confirmation.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task AuthorizeModsAsync(InstallationId installationId)
    {
        var selected = await pathPicker.PickDirectoryAsync(lifetime?.Token ?? default);
        if (string.IsNullOrWhiteSpace(selected)) throw new OperationCanceledException();
        var installation = FindInstallation(installationId);
        var referenceId = installation.Metadata.ReferenceId ?? throw new InvalidOperationException("Reference unavailable.");
        composition.Mo2ModsAuthorization?.AuthorizeModsRoot(referenceId, selected);
        composition.Mo2OnboardingCoordinator?.RegisterExactAuthorization(referenceId, selected);
        await RecordAuthorizationAsync(installationId, "Mods root authorized for this session");
        await RefreshInstallationAsync(installationId);
    }

    private async Task AuthorizeContentAsync(InstallationId installationId, Mo2ContentRootKind kind)
    {
        if (session?.Shell.CurrentSelection is not { GameId: GameId gameId, ProfileId: ProfileId profileId } ||
            session.Shell.CurrentSelection.InstallationId != installationId || composition.Mo2ResolvedStateService is null)
            throw new InvalidOperationException("Select an observed profile before authorizing this content root.");
        var selected = await pathPicker.PickDirectoryAsync(lifetime?.Token ?? default);
        if (string.IsNullOrWhiteSpace(selected)) throw new OperationCanceledException();
        var profile = FindInstallation(installationId).Profiles.First(value => value.Id == profileId);
        var context = new WorkspaceEnvironmentContext(gameId, installationId, profileId, session.Catalog.Revision, profile.Observation?.Inventory?.Fingerprint);
        if (!await composition.Mo2ResolvedStateService.AuthorizeExactRootAsync(context, kind, selected, lifetime?.Token ?? default))
            throw new InvalidOperationException("The selected directory did not exactly match the required configured root.");
        if (FindInstallation(installationId).Metadata.ReferenceId is InstallationReferenceId referenceId)
            composition.Mo2OnboardingCoordinator?.RegisterExactAuthorization(referenceId, selected);
        await RecordAuthorizationAsync(installationId, $"{kind} authorized for this session");
        await RefreshInstallationAsync(installationId);
    }

    private Task RecordAuthorizationAsync(InstallationId installationId, string title) => RecordHistoryAsync(
        HistoryEventKind.SessionAuthorizationGranted, HistoryEventStatus.Succeeded, title,
        "The exact selected root was authorized for read-only observation in this application session.",
        session?.Shell.CurrentSelection.GameId, installationId);

    private async Task RefreshToolOutputsAsync()
    {
        if (session?.ToolOutputs is null) return;
        var result = await session.ToolOutputs.RefreshAsync(false, lifetime?.Token ?? default);
        session.SynchronizeContext();
        var selection = session.Shell.CurrentSelection;
        var selectedProfile = session.Catalog.Games
            .FirstOrDefault(game => game.Id == selection.GameId)?.Installations
            .FirstOrDefault(installation => installation.Id == selection.InstallationId)?.Profiles
            .FirstOrDefault(profile => profile.Id == selection.ProfileId);
        session.OfflineAlerts?.Refresh(result, session.FidelityAudit?.LastResult, selectedProfile, session.Environment?.LastResult);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (session?.Shell.CurrentSelection.GameId is not GameId gameId) return;
            var installation = session.Catalog.Games.FirstOrDefault(game => game.Id == gameId)?.Installations
                .FirstOrDefault(value => value.Id == session.Shell.CurrentSelection.InstallationId);
            BindTools(installation);
            BindOfflineAlertPresentation();
        });
    }

    private async Task RecordHistoryAsync(HistoryEventKind kind, HistoryEventStatus status, string title, string detail,
        GameId? gameId, InstallationId? installationId)
    {
        if (session is null) return;
        await session.History.RecordAsync(new(
            new HistoryEntryId($"history.{Guid.NewGuid():N}"), DateTimeOffset.UtcNow, HistoryActor.User, kind, status,
            gameId, installationId, session.Shell.CurrentSelection.ProfileId, title, detail), lifetime?.Token ?? default);
    }

    private async Task ShowMessageAsync(string title, string message) => await new ContentDialog
    {
        XamlRoot = Content.XamlRoot,
        Title = title,
        Content = message,
        CloseButtonText = "Close",
    }.ShowAsync();

    private void OnWorkspaceCatalogChanged() => RenderGameWorkspace();

    private async void OnVortexCatalogTimerTick(object? sender, object e)
    {
        if (vortexCatalogRefreshRunning || session is null || lifetime?.IsCancellationRequested != false) return;
        vortexCatalogRefreshRunning = true;
        try
        {
            var catalog = await catalogService.GetCatalogAsync(lifetime.Token);
            if (catalog.Revision == session.Catalog.Revision) return;
            session.Shell.ReplaceCatalog(catalog);
            session.SynchronizeContext();
            PopulateGameItems(catalog);
            PersistWorkspaceSelection();
            RenderCurrentRoute();
        }
        catch (OperationCanceledException) when (lifetime?.IsCancellationRequested != false) { }
        catch { }
        finally { vortexCatalogRefreshRunning = false; }
    }

    private async Task RefreshConnectedWorkspaceCatalogAsync()
    {
        if (session is null) return;
        var catalog = await catalogService.GetCatalogAsync(lifetime?.Token ?? default);
        session.Shell.ReplaceCatalog(catalog);
        session.SynchronizeContext();
        PopulateGameItems(catalog);
        PersistWorkspaceSelection();
        RenderCurrentRoute();
    }

    private void BindAssistant()
    {
        if (session is null) return;
        session.Assistant.Expand();
        AssistantPanelView.BindState(session.Assistant, new WindowsEvidenceFilePicker(this), OnAssistantStateChanged, composition.SourceAcquisitionPreferencesStore, composition.NexusRecoverySourceDownloader, lifetime?.Token ?? default);
        UpdateEditHistoryCommands();
    }

    private void OnAssistantStateChanged()
    {
        UpdateAssistantLayout();
        UpdateEditHistoryCommands();
        RefreshActivitySurface();
        if (taskboardExpanded) RenderTaskboard();
    }

    private void UpdateEditHistoryCommands()
    {
        var task = session?.Assistant.Snapshot().ActiveTask;
        var states = task?.ActionStates;
        var canUndo = states?.FirstOrDefault(item => item.Action == AssistantCaseAction.RollBack)?.IsEnabled == true;
        var canRedo = task?.RepairAvailability?.HistoryState == "Undone" &&
            states?.FirstOrDefault(item => item.Action == AssistantCaseAction.ApplyRepair)?.IsEnabled == true;
        UndoRepairMenuItem.IsEnabled = CompactUndoRepairMenuItem.IsEnabled = canUndo;
        RedoRepairMenuItem.IsEnabled = CompactRedoRepairMenuItem.IsEnabled = canRedo;
        ToolTipService.SetToolTip(UndoRepairMenuItem,
            states?.FirstOrDefault(item => item.Action == AssistantCaseAction.RollBack)?.Detail ?? "No verified rollback receipt exists.");
        ToolTipService.SetToolTip(RedoRepairMenuItem,
            states?.FirstOrDefault(item => item.Action == AssistantCaseAction.ApplyRepair)?.Detail ?? "No exact repair is available to reapply.");
    }

    private async void OnUndoRepairClicked(object sender, RoutedEventArgs e) =>
        await ExecuteAssistantHistoryActionAsync(AssistantCaseAction.RollBack);

    private async void OnRedoRepairClicked(object sender, RoutedEventArgs e) =>
        await ExecuteAssistantHistoryActionAsync(AssistantCaseAction.ApplyRepair);

    private async Task ExecuteAssistantHistoryActionAsync(AssistantCaseAction action)
    {
        if (session is null) return;
        try
        {
            await session.Assistant.ExecuteActiveActionAsync(action, null, RefreshAssistantContext, lifetime?.Token ?? default);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            await ShowMessageAsync(action == AssistantCaseAction.RollBack ? "Undo repair" : "Redo repair", exception.Message);
        }
        RefreshAssistantContext();
    }

    private void RefreshAssistantContext()
    {
        if (session is null) return;
        session.SynchronizeContext();
        AssistantPanelView.RefreshContext();
        UpdateAssistantLayout();
        UpdateEditHistoryCommands();
        UpdateShellPresentation();
        RefreshActivitySurface();
        if (taskboardExpanded) RenderTaskboard();
    }

    private void OnAssistantToggleClicked(object sender, RoutedEventArgs e)
    {
        if (session is null) return;
        var requested = sender switch
        {
            ToggleButton toggle => toggle.IsChecked == true,
            ToggleMenuFlyoutItem item => item.IsChecked,
            _ => !session.Assistant.IsExpanded,
        };
        if (requested) session.Assistant.Expand(); else session.Assistant.Collapse();
        UpdateAssistantLayout(true);
    }

    private void CloseAssistant()
    {
        session?.Assistant.Collapse();
        UpdateAssistantLayout();
        AssistantToggleButton.Focus(FocusState.Programmatic);
    }

    private void UpdateAssistantLayout(bool focusPanel = false)
    {
        if (session is null) return;
        AssistantPanelView.RefreshContext();
        if (session.Assistant.IsExpanded && session.Assistant.IsFullScreen)
        {
            // One open workbench panel owns the complete region after the activity rail.
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(AssistantPanelShell, 4);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(AssistantPanelShell, 3);
            AssistantPanelShell.Margin = new Thickness(0, 3.25, 0, 3.25);
            AssistantPanelShell.Visibility = Visibility.Visible;
            MainWorkspacePanel.Visibility = Visibility.Collapsed;
            AssistantSplitter.Visibility = Visibility.Collapsed;
            AssistantSplitterColumn.Width = new GridLength(0);
            AssistantPanelColumn.Width = new GridLength(0);
            AssistantToggleButton.IsChecked = true;
            RightPanelMenuItem.IsChecked = true;
            AutomationProperties.SetName(AssistantToggleButton, "Hide Grid Assistant");
            ApplyContextLayout(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, false, 0).Band);
            ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, false, 0).Band);
            if (focusPanel) AssistantPanelView.FocusPrimaryAction();
            return;
        }

        AssistantPanelShell.Margin = new Thickness(0, 3.25, 0, 3.25);
        var expanded = session.Assistant.IsExpanded;
        var decision = ResponsiveLayoutPolicy.Evaluate(
            WorkspaceHost.ActualWidth,
            expanded,
            session.Assistant.RequestedPanelWidth);
        var docked = expanded && decision.AssistantPresentation == AssistantPresentationMode.Docked;
        var solo = expanded && decision.AssistantPresentation == AssistantPresentationMode.Solo;

        Microsoft.UI.Xaml.Controls.Grid.SetColumn(AssistantPanelShell, solo ? 4 : 6);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(AssistantPanelShell, solo ? 3 : 1);

        AssistantPanelColumn.Width = new GridLength(docked ? decision.AssistantWidth : 0);
        AssistantSplitterColumn.Width = new GridLength(docked ? ResponsiveLayoutPolicy.PaneDividerThickness : 0);
        AssistantPanelShell.Visibility = docked || solo ? Visibility.Visible : Visibility.Collapsed;
        AssistantSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        MainWorkspacePanel.Visibility = solo || bottomPanelMaximized ? Visibility.Collapsed : Visibility.Visible;
        AssistantToggleButton.IsChecked = session.Assistant.IsExpanded;
        RightPanelMenuItem.IsChecked = session.Assistant.IsExpanded;
        AutomationProperties.SetName(AssistantToggleButton, session.Assistant.IsExpanded ? "Hide Grid Assistant" : "Show Grid Assistant");
        AutomationProperties.SetHelpText(
            AssistantToggleButton,
            $"expanded={session.Assistant.IsExpanded};fullscreen={session.Assistant.IsFullScreen};presentation={decision.AssistantPresentation};" +
            $"docked={docked};solo={solo};dockedWidth={AssistantPanelColumn.Width.Value:0.##};" +
            $"panelVisibility={AssistantPanelShell.Visibility};workspaceVisibility={MainWorkspacePanel.Visibility}");
        ApplyContextLayout(decision.Band);
        ApplyResponsiveShell(decision.Band);
        if (focusPanel && session.Assistant.IsExpanded)
        {
            AssistantPanelView.FocusPrimaryAction();
        }
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compactMenu = e.NewSize.Width <= e.NewSize.Height;
        MainMenuBar.Visibility = compactMenu ? Visibility.Collapsed : Visibility.Visible;
        CompactMenuButton.Visibility = compactMenu ? Visibility.Visible : Visibility.Collapsed;
        OpenInLabel.Visibility = compactMenu ? Visibility.Collapsed : Visibility.Visible;
        OpenInButton.Width = compactMenu ? 32 : 110;
        OpenInButton.Padding = compactMenu ? new Thickness(0) : new Thickness(7, 0, 7, 0);
        GlobalSearchPanel.Width = e.NewSize.Width < 820 ? 220 : compactMenu ? 280 : 300;
        UpdateAssistantLayout();
    }

    private void OnMainWorkspacePanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ContextBar.Visibility == Visibility.Visible)
        {
            ApplyContextLayout(ResponsiveLayoutPolicy.Evaluate(
                WorkspaceHost.ActualWidth,
                UsesAssistantDockBudget,
                session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
        }
    }

    private void OnShellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && session?.Assistant.IsFullScreen == true)
        {
            session.Assistant.ToggleFullScreen(); UpdateAssistantLayout(true); e.Handled = true; return;
        }
        if (e.Key == VirtualKey.Escape && session?.Assistant.IsExpanded == true)
        {
            CloseAssistant(); e.Handled = true; return;
        }
        var control = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (control && shift && e.Key == VirtualKey.O && session is not null)
        {
            session.Assistant.Toggle(); UpdateAssistantLayout(true); e.Handled = true;
            return;
        }
        if (control && e.Key == VirtualKey.B)
        {
            leftPanelRequested = !leftPanelRequested;
            ApplyResponsiveShell(ResponsiveLayoutPolicy.Evaluate(WorkspaceHost.ActualWidth, UsesAssistantDockBudget,
                session?.Assistant.RequestedPanelWidth ?? AssistantSessionState.DefaultPanelWidth).Band);
            e.Handled = true;
            return;
        }
        if (control && e.Key == VirtualKey.J)
        {
            bottomPanelRequested = !bottomPanelRequested;
            ApplyBottomPanelVisibility();
            e.Handled = true;
            return;
        }
        if (control && e.Key == VirtualKey.P)
        {
            GlobalSearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void OnAssistantSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (session?.Assistant.IsExpanded != true) return;
        session.Assistant.Resize(session.Assistant.RequestedPanelWidth - e.HorizontalChange);
        UpdateAssistantLayout();
    }

    private void OnAssistantSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (session?.Assistant.IsExpanded != true) return;
        if (e.Key == VirtualKey.Home)
        {
            session.Assistant.Resize(AssistantSessionState.DefaultPanelWidth); UpdateAssistantLayout(); e.Handled = true; return;
        }
        if (e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            session.Assistant.Resize(session.Assistant.RequestedPanelWidth + (e.Key == VirtualKey.Left ? 24 : -24));
            UpdateAssistantLayout(); e.Handled = true;
        }
    }

    private void ApplyContextLayout(ResponsiveLayoutBand band)
    {
        static void Place(FrameworkElement element, int row, int column, int span = 1)
        {
            Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
            Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, span);
        }
        Place(TargetContextPanel, 0, 0);
        Place(RunButton, 0, 1);

        var availableWidth = Math.Max(0, MainWorkspacePanel.ActualWidth - (ShellPanelContentInset * 2));
        if (availableWidth > 0)
        {
            ContextLayout.Width = band == ResponsiveLayoutBand.Wide
                ? Math.Max(360, (availableWidth - 8) / 2)
                : availableWidth;
        }
    }

    private void NavigateFrame(Type destination)
    {
        if (ContentFrame.CurrentSourcePageType != destination) ContentFrame.Navigate(destination);
    }

    private void ShowLoading() { LoadingState.Visibility = Visibility.Visible; ErrorState.Visibility = Visibility.Collapsed; }
    private void ShowShell() { LoadingState.Visibility = Visibility.Collapsed; ErrorState.Visibility = Visibility.Collapsed; }
    private void ShowError(string message) { LoadingState.Visibility = Visibility.Collapsed; ErrorState.Visibility = Visibility.Visible; ErrorMessage.Text = message; }
    private async void OnRetryClicked(object sender, RoutedEventArgs e) => await LoadCatalogAsync();

    private void ConfigureWindow()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(handle));
        TemporaryApplicationIdentity.Apply(appWindow);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1580, 940));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        appWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 204, 204, 204);
        appWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 45, 45, 45);
    }

    private enum ShellSurface { Welcome, Home, Games, Workstation, Activities, Settings }
    private sealed record GridNotification(string Title, string Message, string Timestamp, string Glyph);
    private sealed record FileSystemExplorerItem(string Path, string Name, bool IsDirectory, string? Detail = null)
    {
        public override string ToString() => Name;
    }

    private enum BottomSurface { Terminal, Output, Problems }
    private readonly record struct ShellLocation(ShellSurface Surface, WorkspaceSelection Selection, string TabKey);
    private sealed record EditorTabRecord(string Key, ShellSurface Surface, string Title, WorkspaceSelection Selection);

    private sealed record ToolTargetPresentation(string Name, string PresentationStatus, ResolvedLaunchTarget? LaunchTarget, ObservedExecutableSummary? Executable, bool IsManagementAction = false)
    {
        public static ToolTargetPresentation ForManagement() => new("Manage…", "Manager connection and installations", null, null, true);
        public static ToolTargetPresentation ForDemo(ResolvedLaunchTarget target) => new(target.Definition.Name, "Development fixture", target, null);
        public static ToolTargetPresentation ForObserved(ObservedExecutableSummary executable) =>
            new(executable.Title, $"Read-only MO2 configuration · {executable.Availability}", null, executable);
    }
}
