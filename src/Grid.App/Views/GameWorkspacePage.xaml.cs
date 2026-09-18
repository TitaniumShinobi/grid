using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.App.Services;
using Grid.Mo2.Models;
using Grid.Mo2.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Grid.App.Views;

public sealed partial class GameWorkspacePage : Page
{
    private const string NullCellValue = "—";
    // The page receives the width left after both NavigationView and the optional
    // assistant panel have taken their share. Stack early enough that neither pane
    // is reduced to a narrow strip when the assistant opens on a desktop display.
    private const double StackThreshold = 920;
    private const double LeftPaneMinimum = 380;
    private const double RightPaneMinimum = 320;
    private const double SplitterWidth = 4;
    private const double PageHorizontalPadding = 0;

    private readonly record struct FilterChoice<T>(string Label, T Value);
    private readonly record struct ScrollPosition(double HorizontalOffset, double VerticalOffset);
    private sealed record SeparatorChoice(string Label, ModId Id);
    private sealed record ModProjectionResult(ImmutableArray<ModEntry> Entries, ModRowViewModel[] Rows);

    private WorkspaceSessionState? _workspaceState;
    private WorkspaceEnvironmentState? _environmentState;
    private WorkspaceToolOutputState? _toolOutputState;
    private OfflineAlertIndexState? _offlineAlertState;
    private Mo2WorkspaceToolOutputQueryService? _toolOutputQueryService;
    private LocalWorkspacePresentationStore? _presentationStore;
    private Mo2ModStateMutationService? _modStateMutationService;
    private ManagedInstallation? _installation;
    private Profile? _profile;
    private Action? _catalogChanged;
    private Func<Task>? _catalogRefreshRequested;
    private Action? _contextChanged;
    private Action<string, string, InfoBarSeverity>? _notificationRaised;
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _environmentQueryCancellation;
    private bool _suppressUiEvents;
    private bool _isConnectedObservation;
    private bool _permitsDevelopmentCommands;
    private bool _modStateMutationRunning;
    private bool _isStacked;
    private double? _userLeftPaneWidth;
    private ImmutableArray<PluginEntry> _resolvedPlugins = [];
    private ImmutableArray<ResolvedArchiveEntry> _resolvedArchives = [];
    private ImmutableArray<VirtualDataEntry> _resolvedData = [];
    private readonly Dictionary<EnvironmentTabCapability, int> _environmentPageOffsets = [];
    private readonly Dictionary<EnvironmentTabCapability, int> _environmentPageTotals = [];
    private readonly HashSet<ModId> _collapsedSeparators = [];
    private readonly ObservableCollection<ModRowViewModel> _modRows = [];
    private const int CurrentModColumnLayoutVersion = 3;
    private static readonly double[] DefaultModColumnWidths = [76, 56, 72, 72, 86];
    private static readonly WorkspaceColumnLayout SharedModColumns = new(76, 56, 72, 72, 86);
    private static readonly WorkspaceColumnLayout SharedEnvironmentColumns = new(76, 62, 76, 0, 0);

    public GameWorkspacePage()
    {
        InitializeComponent();
        ModsList.ItemsSource = _modRows;
    }

    public void BindContext(
        ManagedGame game,
        ManagedInstallation? installation,
        Profile? profile,
        CatalogSourceKind sourceKind,
        WorkspaceSessionState workspaceState,
        WorkspaceEnvironmentState? environmentState,
        WorkspaceToolOutputState? toolOutputState,
        OfflineAlertIndexState? offlineAlertState,
        Mo2WorkspaceToolOutputQueryService? toolOutputQueryService,
        LocalWorkspacePresentationStore? presentationStore,
        Mo2ModStateMutationService? modStateMutationService,
        Action catalogChanged,
        Func<Task> catalogRefreshRequested,
        Action contextChanged,
        Action<string, string, InfoBarSeverity> notificationRaised,
        bool isDevelopmentDemo)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(workspaceState);
        ArgumentNullException.ThrowIfNull(catalogChanged);
        ArgumentNullException.ThrowIfNull(catalogRefreshRequested);
        ArgumentNullException.ThrowIfNull(contextChanged);
        ArgumentNullException.ThrowIfNull(notificationRaised);

        _installation = installation;
        _profile = profile;
        _workspaceState = workspaceState;
        _environmentState = environmentState;
        _toolOutputState = toolOutputState;
        _offlineAlertState = offlineAlertState;
        _toolOutputQueryService = toolOutputQueryService;
        _presentationStore = presentationStore;
        _modStateMutationService = modStateMutationService;
        if (profile is not null)
            _offlineAlertState?.Refresh(null, null, profile, environmentState?.LastResult);
        _isConnectedObservation = installation?.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference;
        _permitsDevelopmentCommands = false;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ModsList,
            _isConnectedObservation ? "MO2 mod list" : isDevelopmentDemo ? "Development mod list" : "Mod list unavailable");
        _catalogChanged = catalogChanged;
        _catalogRefreshRequested = catalogRefreshRequested;
        _contextChanged = contextChanged;
        _notificationRaised = notificationRaised;
        _workspaceState.SynchronizeContext();
        _collapsedSeparators.Clear();
        _suppressUiEvents = true;
        var restoredPresentation = RestorePresentationState();
        _suppressUiEvents = false;
        if (!restoredPresentation && profile is not null)
        {
            foreach (var separator in profile.Mods.Where(mod => mod.Kind == ModEntryKind.Separator))
            {
                _collapsedSeparators.Add(separator.Id);
            }
        }

        if (_isConnectedObservation && profile?.Observation?.WarningCount is > 0)
        {
            _notificationRaised(
                "MO2 profile observations",
                $"{profile.Observation.WarningCount} profile warning{(profile.Observation.WarningCount == 1 ? string.Empty : "s")} recorded for {profile.Name}.",
                InfoBarSeverity.Warning);
        }

        MockDisclosure.Title = _isConnectedObservation
            ? "MO2 INVENTORY · OBSERVED WITH EXPLICIT MOD TOGGLES"
            : isDevelopmentDemo ? "DEVELOPMENT DEMO · IN-MEMORY WORKSPACE" : "WORKSPACE EVIDENCE UNAVAILABLE";
        MockDisclosure.Message = _isConnectedObservation
            ? "Inventory and resolved environment rows remain typed point-in-time observations. An individual authoritative mod checkbox can change only that entry's modlist.txt marker after exact confirmation, backup, verification, and automatic rollback on failure. Grid-derived, separator, and foreign rows remain non-mutable."
            : isDevelopmentDemo
                ? "Rows are isolated development fixtures. They are never mixed with connected production state."
                : "No authoritative workspace evidence is available. Grid will not fabricate mods, plugins, archives, or load order.";

        ReadyState.Visibility = Visibility.Collapsed;
        UnavailableState.Visibility = Visibility.Collapsed;
        EmptyProfileState.Visibility = Visibility.Collapsed;
        NoModListState.Visibility = Visibility.Collapsed;

        if (installation is null)
        {
            UnavailableTitle.Text = "No managed installation available";
            UnavailableDetail.Text = $"{game.Name} remains a capability-aware preview surface. Grid is not fabricating an installation, profile, mod list, or Skyrim-only environment tabs.";
            UnavailableState.Visibility = Visibility.Visible;
            return;
        }

        if (installation.Metadata.Availability != InstallationAvailability.Available)
        {
            UnavailableTitle.Text = $"{installation.Metadata.Availability} external installation";
            UnavailableDetail.Text = $"{installation.Metadata.StatusDetail} Grid retains this installation for diagnosis and performs no external mutation.";
            UnavailableState.Visibility = Visibility.Visible;
            return;
        }

        if (profile is null)
        {
            EmptyProfileDetail.Text = installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference
                ? $"{installation.Name} is linked read only, but no current profile observation is available. Use Manage game to authorize the exact profile root if requested or refresh. Grid has not fabricated a profile."
                : $"{installation.Name} contains no available profile. The installation context remains selected and no fallback profile was fabricated.";
            EmptyProfileState.Visibility = Visibility.Visible;
            return;
        }

        if (!installation.Capabilities.Supports(WorkspaceFeature.ModList))
        {
            NoModListTitle.Text = _isConnectedObservation ? "Connected profile observed read only" : "Mod list unsupported";
            NoModListDetail.Text = _isConnectedObservation
                ? "Grid can select this observed MO2 profile, but this catalog exposes no normalized mod-list rows. No content was fabricated and no external files were changed."
                : "The selected adapter does not expose a mod-list capability. Grid will not fabricate Skyrim-style rows for this game.";
            ConnectedProfileObservation.IsOpen = _isConnectedObservation;
            ConnectedProfileObservation.Title = profile.Observation?.ManagerState == ManagerProfileState.Active
                ? "ACTIVE IN MO2"
                : "Not active in MO2";
            var observationStatus = profile.Observation?.Status.ToString() ?? "Unavailable";
            var warningCount = profile.Observation?.WarningCount ?? 0;
            ConnectedProfileObservation.Message = $"{(profile.Observation?.ManagerState == ManagerProfileState.Active ? "Also" : "Currently")} SELECTED IN GRID · {observationStatus} observation · {warningCount} warning{(warningCount == 1 ? string.Empty : "s")}.";
            NoModListState.Visibility = Visibility.Visible;
            return;
        }

        ReadyState.Visibility = Visibility.Visible;
        ResolvedEnvironmentControls.Visibility = _isConnectedObservation ? Visibility.Visible : Visibility.Collapsed;
        _permitsDevelopmentCommands = isDevelopmentDemo && sourceKind == CatalogSourceKind.Mock;
        MockCommandActions.Visibility = _permitsDevelopmentCommands ? Visibility.Visible : Visibility.Collapsed;
        EnableSelectedMenuItem.Visibility = _permitsDevelopmentCommands ? Visibility.Visible : Visibility.Collapsed;
        DisableSelectedMenuItem.Visibility = _permitsDevelopmentCommands ? Visibility.Visible : Visibility.Collapsed;
        MoveUpMenuItem.Visibility = _permitsDevelopmentCommands ? Visibility.Visible : Visibility.Collapsed;
        MoveDownMenuItem.Visibility = _permitsDevelopmentCommands ? Visibility.Visible : Visibility.Collapsed;
        EnableSelectedMenuItem.IsEnabled = _permitsDevelopmentCommands;
        DisableSelectedMenuItem.IsEnabled = _permitsDevelopmentCommands;
        MoveUpMenuItem.IsEnabled = _permitsDevelopmentCommands;
        MoveDownMenuItem.IsEnabled = _permitsDevelopmentCommands;
        ConflictFilterButton.IsEnabled = !_isConnectedObservation;
        ConflictFilterButton.Visibility = _isConnectedObservation ? Visibility.Collapsed : Visibility.Visible;
        ConflictFilterButton.Content = "Conflicts";
        UpdateInventoryViewStatus();
        ConfigureQueryControls(profile);
        _ = RefreshProjectionAsync(debounce: false);
        RefreshEnvironmentTabs();
        ApplyResponsiveLayout(ActualWidth);
    }

    private bool RestorePresentationState()
    {
        if (_profile is null || _presentationStore?.Load(_profile.Id.Value) is not { } state) return false;
        _userLeftPaneWidth = state.LeftPaneWidth;
        var restoredModColumns = state.ModColumnLayoutVersion >= CurrentModColumnLayoutVersion
            ? state.ModColumnWidths?.ToArray()
            : DefaultModColumnWidths;
        if (restoredModColumns is { Length: >= 5 })
        {
            restoredModColumns[1] = Math.Max(restoredModColumns[1], 56);
            restoredModColumns[2] = Math.Max(restoredModColumns[2], 72);
        }
        SharedModColumns.Restore(restoredModColumns);
        SharedEnvironmentColumns.Restore(state.EnvironmentColumnWidths);
        ModConflictHeaderColumn.Width = SharedModColumns.Second;
        ModFlagsHeaderColumn.Width = SharedModColumns.Third;
        ModPriorityHeaderColumn.Width = SharedModColumns.Fourth;
        ModVersionHeaderColumn.Width = SharedModColumns.Fifth;
        ModCategoryHeaderColumn.Width = SharedModColumns.Sixth;
        _collapsedSeparators.Clear();
        foreach (var value in state.CollapsedSeparatorIds)
        {
            if (!string.IsNullOrWhiteSpace(value)) _collapsedSeparators.Add(new ModId(value));
        }
        EnvironmentSearchBox.Text = state.EnvironmentSearch ?? string.Empty;
        if (Enum.TryParse<EnvironmentTabCapability>(state.SelectedEnvironmentTab, out var tab))
        {
            _workspaceState?.SelectEnvironmentTab(tab);
        }
        if (_workspaceState is not null)
        {
            _workspaceState.SetQuery(_workspaceState.Query with { SearchText = state.ModSearch ?? string.Empty });
        }
        return true;
    }

    private void SavePresentationState()
    {
        if (_profile is null || _presentationStore is null) return;
        _presentationStore.Save(_profile.Id.Value, new(
            ModSearchBox.Text ?? string.Empty,
            EnvironmentSearchBox.Text ?? string.Empty,
            _workspaceState?.SelectedEnvironmentTab.ToString() ?? EnvironmentTabCapability.Plugins.ToString(),
            _collapsedSeparators.Select(value => value.Value).OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            _userLeftPaneWidth,
            SharedModColumns.Values,
            SharedEnvironmentColumns.Values,
            CurrentModColumnLayoutVersion));
    }

    private void ConfigureQueryControls(Profile profile)
    {
        if (_workspaceState is null)
        {
            return;
        }

        var query = _workspaceState.Query;
        _suppressUiEvents = true;
        try
        {
            EnabledFilterSelector.ItemsSource = new[]
            {
                new FilterChoice<ModEnabledFilter>("All enablement", ModEnabledFilter.All),
                new FilterChoice<ModEnabledFilter>("Enabled", ModEnabledFilter.Enabled),
                new FilterChoice<ModEnabledFilter>("Disabled", ModEnabledFilter.Disabled),
            };
            SelectChoice(EnabledFilterSelector, query.EnabledFilter);

            var categories = profile.Mods
                .Where(mod => mod.Kind != ModEntryKind.Separator && !string.IsNullOrWhiteSpace(mod.Category))
                .Select(mod => mod.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(category => category, StringComparer.OrdinalIgnoreCase)
                .Select(category => new FilterChoice<string?>(category, category))
                .Prepend(new FilterChoice<string?>("All categories", null))
                .ToArray();
            CategoryFilterSelector.ItemsSource = categories;
            SelectChoice(CategoryFilterSelector, query.Category);

            SortSelector.ItemsSource = new[]
            {
                new FilterChoice<ModSortColumn>("Mod priority", ModSortColumn.Priority),
                new FilterChoice<ModSortColumn>("Name", ModSortColumn.Name),
                new FilterChoice<ModSortColumn>("Category", ModSortColumn.Category),
                new FilterChoice<ModSortColumn>(_isConnectedObservation ? "Local update evidence" : "Represented update", ModSortColumn.UpdateState),
                new FilterChoice<ModSortColumn>("Reconciliation", ModSortColumn.Reconciliation),
                new FilterChoice<ModSortColumn>("Installation time", ModSortColumn.InstallationTime),
            };
            SelectChoice(SortSelector, query.SortColumn);

            ReconciliationFilterSelector.ItemsSource = Enum.GetValues<ModReconciliationState>()
                .Select(state => new FilterChoice<ModReconciliationState?>(FormatEnumLabel(state), state))
                .Prepend(new FilterChoice<ModReconciliationState?>("All inventory states", null))
                .ToArray();
            SelectChoice(ReconciliationFilterSelector, query.Reconciliation);

            UpdateStateFilterSelector.ItemsSource = Enum.GetValues<ModUpdateState>()
                .Select(state => new FilterChoice<ModUpdateState?>(FormatEnumLabel(state), state))
                .Prepend(new FilterChoice<ModUpdateState?>("All update states", null))
                .ToArray();
            SelectChoice(UpdateStateFilterSelector, query.UpdateState);

            SeparatorSelector.ItemsSource = _workspaceState.GetSeparatorDescriptors()
                .OrderBy(separator => separator.DisplayOrder)
                .Select(separator => new SeparatorChoice(separator.Name, separator.Id))
                .ToArray();
            SeparatorSelector.SelectedItem = null;
            SeparatorSelector.IsEnabled = SeparatorSelector.Items.Count > 0;

            ModSearchBox.Text = query.SearchText;
            ConflictFilterButton.IsChecked = query.ConflictsOnly;
            WarningFilterButton.IsChecked = query.WarningsOnly;
            SortDirectionButton.Content = query.SortDirection == WorkspaceSortDirection.Ascending
                ? "Ascending"
                : "Descending";
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private static void SelectChoice<T>(ComboBox selector, T value)
    {
        selector.SelectedItem = selector.Items
            .OfType<FilterChoice<T>>()
            .FirstOrDefault(choice => EqualityComparer<T>.Default.Equals(choice.Value, value));
        if (selector.SelectedIndex < 0 && selector.Items.Count > 0)
        {
            selector.SelectedIndex = 0;
        }
    }

    private ModListQuery BuildQuery()
    {
        var current = _workspaceState?.Query ?? ModListQuery.Default;
        return new ModListQuery(
            ModSearchBox.Text,
            (EnabledFilterSelector.SelectedItem as FilterChoice<ModEnabledFilter>?)?.Value ?? current.EnabledFilter,
            (CategoryFilterSelector.SelectedItem as FilterChoice<string?>?)?.Value,
            ConflictFilterButton.IsChecked == true,
            false,
            (SortSelector.SelectedItem as FilterChoice<ModSortColumn>?)?.Value ?? current.SortColumn,
            current.SortDirection,
            (ReconciliationFilterSelector.SelectedItem as FilterChoice<ModReconciliationState?>?)?.Value,
            WarningFilterButton.IsChecked == true,
            (UpdateStateFilterSelector.SelectedItem as FilterChoice<ModUpdateState?>?)?.Value);
    }

    private static string FormatEnumLabel<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));

    private Dictionary<string, string> BuildOfflineAlertDetails() =>
        (_offlineAlertState?.Snapshot?.Alerts ?? [])
        .Where(alert => alert.State != OfflineAlertState.Cleared)
        .GroupBy(alert => alert.SourceIdentity, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => string.Join("\n", group.Take(8).Select(alert => $"[{alert.State}] {alert.Title}: {alert.Detail}")),
            StringComparer.Ordinal);

    private static string? GetAlertDetail(IReadOnlyDictionary<string, string> details, string sourceIdentity) =>
        details.TryGetValue(sourceIdentity, out var detail) ? detail : null;

    private async Task RefreshProjectionAsync(bool debounce, bool preserveWorkstationPosition = false)
    {
        if (_workspaceState is null || _profile is null)
        {
            return;
        }

        _projectionCancellation?.Cancel();
        _projectionCancellation?.Dispose();
        _projectionCancellation = new CancellationTokenSource();
        var cancellationToken = _projectionCancellation.Token;
        var modScrollPosition = preserveWorkstationPosition
            ? CaptureScrollPosition(ModsList)
            : null;

        try
        {
            if (debounce)
            {
                await Task.Delay(150, cancellationToken);
            }

            var query = BuildQuery();
            _workspaceState.SetQuery(query);
            var mods = _profile.Mods;
            var collapsedSeparators = _collapsedSeparators.ToHashSet();
            var offlineAlertDetails = BuildOfflineAlertDetails();
            var includeDerivedBoundary = _isConnectedObservation &&
                query.SortColumn == ModSortColumn.Priority &&
                query.SortDirection == WorkspaceSortDirection.Ascending;
            var projected = await Task.Run(
                () =>
                {
                    var entries = WorkspaceSessionState.ProjectMods(mods, query);
                    var rows = new List<ModRowViewModel>(entries.Length + 1);
                    var boundaryAdded = false;
                    ModId? currentSeparator = null;
                    var currentSectionCollapsed = false;
                    foreach (var entry in entries)
                    {
                        if (entry.Kind == ModEntryKind.Separator)
                        {
                            currentSeparator = entry.Id;
                            currentSectionCollapsed = string.IsNullOrWhiteSpace(query.SearchText) &&
                                collapsedSeparators.Contains(entry.Id);
                            rows.Add(new ModRowViewModel(entry, !currentSectionCollapsed, GetAlertDetail(offlineAlertDetails, entry.Id.Value)));
                            continue;
                        }

                        if (currentSectionCollapsed)
                        {
                            continue;
                        }

                        if (includeDerivedBoundary && !boundaryAdded &&
                            entry.Inventory?.Authority == ModInventoryAuthority.GridDerived)
                        {
                            rows.Add(ModRowViewModel.CreateDerivedBoundary());
                            boundaryAdded = true;
                        }

                        rows.Add(new ModRowViewModel(entry, true, GetAlertDetail(offlineAlertDetails, entry.Id.Value)));
                    }

                    return new ModProjectionResult(entries, rows.ToArray());
                },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyModProjection(projected, preserveWorkstationPosition, modScrollPosition);
        }
        catch (OperationCanceledException)
        {
            // A newer query owns the projection.
        }
    }

    private void ApplyModProjection(
        ModProjectionResult projected,
        bool preserveWorkstationPosition = false,
        ScrollPosition? modScrollPosition = null)
    {
        if (_workspaceState is null)
        {
            return;
        }

        var rows = projected.Rows;
        _suppressUiEvents = true;
        try
        {
            if (preserveWorkstationPosition)
            {
                ReconcileModRows(rows);
            }
            else
            {
                _modRows.Clear();
                foreach (var row in rows)
                {
                    _modRows.Add(row);
                }
            }

            foreach (var row in rows.Where(row => row.Entry is ModEntry entry &&
                _workspaceState.SelectedModIds.Contains(entry.Id)))
            {
                if (!ModsList.SelectedItems.Contains(row))
                {
                    ModsList.SelectedItems.Add(row);
                }
            }
        }
        finally
        {
            _suppressUiEvents = false;
        }

        var representedModCount = _profile?.Mods.Count(mod => mod.Kind != ModEntryKind.Separator) ??
            projected.Entries.Count(mod => mod.Kind != ModEntryKind.Separator);
        var enabledModCount = _profile?.Mods.Count(mod => mod.Kind != ModEntryKind.Separator && mod.IsEnabled) ?? 0;
        ModCountText.Text = _isConnectedObservation
            ? $"{representedModCount} mod{(representedModCount == 1 ? string.Empty : "s")} · {enabledModCount} active"
            : $"{representedModCount} represented mod{(representedModCount == 1 ? string.Empty : "s")} · mock evidence";
        var authoritativeOrder = _workspaceState.Query.SortColumn == ModSortColumn.Priority &&
            _workspaceState.Query.SortDirection == WorkspaceSortDirection.Ascending;
        UpdateInventoryViewStatus(authoritativeOrder);
        if (preserveWorkstationPosition)
        {
            RestoreScrollPosition(ModsList, modScrollPosition);
        }
        else
        {
            UpdateSelectionPresentation();
        }
    }

    private void ReconcileModRows(IReadOnlyList<ModRowViewModel> targetRows)
    {
        for (var targetIndex = 0; targetIndex < targetRows.Count; targetIndex++)
        {
            var target = targetRows[targetIndex];
            if (targetIndex < _modRows.Count && SameModRow(_modRows[targetIndex], target))
            {
                _modRows[targetIndex].SynchronizePresentation(target);
                continue;
            }

            var existingIndex = -1;
            for (var candidateIndex = targetIndex + 1; candidateIndex < _modRows.Count; candidateIndex++)
            {
                if (SameModRow(_modRows[candidateIndex], target))
                {
                    existingIndex = candidateIndex;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                while (existingIndex > targetIndex)
                {
                    _modRows.RemoveAt(targetIndex);
                    existingIndex--;
                }

                _modRows[targetIndex].SynchronizePresentation(target);
            }
            else
            {
                _modRows.Insert(targetIndex, target);
            }
        }

        while (_modRows.Count > targetRows.Count)
        {
            _modRows.RemoveAt(_modRows.Count - 1);
        }
    }

    private static bool SameModRow(ModRowViewModel left, ModRowViewModel right) =>
        left.Entry?.Id == right.Entry?.Id &&
        (left.Entry is not null || right.Entry is null);

    private static ScrollPosition? CaptureScrollPosition(DependencyObject root) =>
        FindDescendant<ScrollViewer>(root) is { } scrollViewer
            ? new(scrollViewer.HorizontalOffset, scrollViewer.VerticalOffset)
            : null;

    private void RestoreScrollPosition(DependencyObject root, ScrollPosition? position)
    {
        if (position is not { } target) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (root is UIElement element) element.UpdateLayout();
            FindDescendant<ScrollViewer>(root)?.ChangeView(
                target.HorizontalOffset,
                target.VerticalOffset,
                null,
                disableAnimation: true);
        });
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }

    private void UpdateInventoryViewStatus(bool authoritativeOrder = true)
    {
        if (!_isConnectedObservation)
        {
            InventoryViewStatus.IsOpen = false;
            return;
        }

        if (!authoritativeOrder)
        {
            InventoryViewStatus.IsOpen = true;
            InventoryViewStatus.Title = "VIEW SORTED · MO2 ORDER UNCHANGED";
            InventoryViewStatus.Message = "This is a temporary Grid view. Separators are omitted where grouping would mislead; no MO2 priority or file changed.";
            return;
        }

        var inventory = _profile?.Observation?.Inventory;
        InventoryViewStatus.IsOpen = inventory is null || inventory.WarningCount > 0;
        InventoryViewStatus.Title = inventory is null
            ? "Inventory observation unavailable"
            : $"{inventory.Status} MO2 inventory · {inventory.WarningCount} warning{(inventory.WarningCount == 1 ? string.Empty : "s")}";
        InventoryViewStatus.Message = inventory is null
            ? "Grid has no authoritative mod-directory reconciliation for this profile. No rows were invented."
            : $"{inventory.AuthoritativeEntryCount} authoritative rows · {inventory.DerivedEntryCount} Grid-derived rows · LOCAL METADATA · NOT ONLINE VERIFIED.";
    }

    private void RefreshEnvironmentTabs()
    {
        if (_workspaceState is null || _profile is null)
        {
            EnvironmentTabs.TabItemsSource = null;
            EnvironmentTabs.Visibility = Visibility.Collapsed;
            NoEnvironmentTabsState.IsOpen = true;
            return;
        }

        var tabs = _workspaceState.GetEnvironmentTabs()
            .Select(CreateEnvironmentTab)
            .ToArray();

        _suppressUiEvents = true;
        try
        {
            EnvironmentTabs.TabItemsSource = tabs;
            EnvironmentTabs.Visibility = tabs.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            NoEnvironmentTabsState.IsOpen = tabs.Length == 0;
            EnvironmentTabs.SelectedItem = tabs.FirstOrDefault(tab =>
                tab.Capability == _workspaceState.SelectedEnvironmentTab) ?? tabs.FirstOrDefault();
        }
        finally
        {
            _suppressUiEvents = false;
        }

        if (_isConnectedObservation && _environmentState?.Snapshot is not null)
        {
            _ = RefreshResolvedEnvironmentTabsAsync();
        }
    }

    private EnvironmentTabViewModel CreateEnvironmentTab(EnvironmentTabDescriptor descriptor)
    {
        if (_profile is null || _workspaceState is null)
        {
            return new EnvironmentTabViewModel(descriptor, string.Empty, []);
        }

        if (_isConnectedObservation && descriptor.Capability is
            EnvironmentTabCapability.Archives or EnvironmentTabCapability.Data)
        {
            return new EnvironmentTabViewModel(
                descriptor,
                _environmentState?.Snapshot is null
                    ? "Select Scan read only to build bounded evidence. No placeholder rows are fabricated."
                    : "Loading the current snapshot page…",
                [],
                emptyMessage: _environmentState?.Snapshot is null
                    ? "Not scanned for this profile. Select Scan read only above to build this view."
                    : "Loading the current profile snapshot…");
        }

        if (descriptor.Capability == EnvironmentTabCapability.Plugins)
        {
            var pluginRows = CreatePluginRows(_profile.Plugins, _isConnectedObservation, resolved: false);
            return new EnvironmentTabViewModel(
                descriptor,
                _isConnectedObservation
                    ? "Plugin load order is distinct from left-pane mod priority. Values are read-only MO2 observations and are not mutated by Grid."
                    : "Plugin load order is distinct from left-pane mod priority. Values are represented mock evidence only.",
                pluginRows,
                _workspaceState.SelectedPluginId,
                null);
        }

        var selectedIds = _workspaceState.SelectedModIds;
        var environmentRows = _profile.EnvironmentEntries
            .Where(entry => entry.Tab == descriptor.Capability)
            .OrderByDescending(entry => entry.RelatedModIds.Any(selectedIds.Contains))
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry =>
            {
                var related = entry.RelatedModIds.Any(selectedIds.Contains);
                var status = related ? $"{entry.Status} · Related to selected mod" : entry.Status;
                var column2 = descriptor.Capability == EnvironmentTabCapability.Saves
                    ? entry.Detail
                    : status;
                var column3 = descriptor.Capability == EnvironmentTabCapability.Downloads
                    ? entry.Detail
                    : entry.Health.ToString();
                return new EnvironmentRowViewModel(
                    entry.Name,
                    entry.Detail,
                    column2,
                    "HEALTH",
                    column3,
                    null,
                    entry.Id,
                    GeneratedOutputId: entry.ObservedOutputId,
                    Column4: descriptor.Capability == EnvironmentTabCapability.Saves ? status : string.Empty);
            })
            .ToArray();

        return new EnvironmentTabViewModel(
            descriptor,
            DescribeEnvironmentTab(descriptor.Capability),
            environmentRows,
            null,
            _workspaceState.SelectedEnvironmentEntryId);
    }

    private static string DescribeEnvironmentTab(EnvironmentTabCapability capability) => capability switch
    {
        EnvironmentTabCapability.Archives => "Read-only BSA index evidence. Payloads are never extracted or decompressed; unsupported formats remain explicit.",
        EnvironmentTabCapability.Data => "Paged effective Data evidence with explainable provider chains. Mixed loose/archive precedence remains uncertain when evidence is incomplete.",
        EnvironmentTabCapability.Saves => "Profile-specific represented saves. No save is opened, moved, or changed.",
        EnvironmentTabCapability.Downloads => "Represented download queue state. Networking is unavailable.",
        EnvironmentTabCapability.Conflicts => "Represented conflict evidence and mod relationships; results are not diagnostic facts.",
        EnvironmentTabCapability.Outputs => "Represented generated-output destinations. No files are created.",
        EnvironmentTabCapability.Activity => "Activity evidence associated with this profile context.",
        _ => "Capability-supported represented environment evidence.",
    };

    private void UpdateSelectionPresentation()
    {
        if (_workspaceState is null)
        {
            return;
        }

        var selectedRows = ModsList.SelectedItems
            .OfType<ModRowViewModel>()
            .Where(row => row.Entry is not null && row.Entry.Kind != ModEntryKind.Separator)
            .ToArray();
        var count = selectedRows.Length;
        SelectedModsText.Text = $"{count} selected";
        SelectedModDetailText.Text = count switch
        {
            0 => "No mods selected · environment evidence remains unfiltered",
            1 => $"Selected: {selectedRows[0].Name} · related evidence is promoted, not hidden",
            _ => $"{count} mods selected · related evidence is promoted, not hidden",
        };

        var hasSelection = count > 0 && !_isConnectedObservation;
        EnableSelectedButton.IsEnabled = hasSelection;
        DisableSelectedButton.IsEnabled = hasSelection;
        var canReorder = count == 1 &&
            !_isConnectedObservation &&
            _workspaceState.Query.IsUnfilteredPriorityView;
        MoveUpButton.IsEnabled = canReorder;
        MoveDownButton.IsEnabled = canReorder;
        UpdateSelectionInspector(selectedRows);
    }

    private async Task RefreshResolvedEnvironmentTabsAsync()
    {
        if (!_isConnectedObservation || _environmentState?.Snapshot is null || _workspaceState is null)
        {
            return;
        }

        _environmentQueryCancellation?.Cancel();
        _environmentQueryCancellation?.Dispose();
        _environmentQueryCancellation = new CancellationTokenSource();
        var token = _environmentQueryCancellation.Token;
        var search = EnvironmentSearchBox.Text?.Trim() ?? string.Empty;
        try
        {
            var pluginTask = _environmentState.QueryPluginsAsync(
                PluginQuery.Default with { SearchText = search, Offset = GetPageOffset(EnvironmentTabCapability.Plugins) }, token);
            var archiveTask = _environmentState.QueryArchivesAsync(
                ArchiveQuery.Default with { SearchText = search, Offset = GetPageOffset(EnvironmentTabCapability.Archives) }, token);
            var dataTask = _environmentState.QueryDataAsync(
                VirtualDataQuery.Default with { SearchText = search, Offset = GetPageOffset(EnvironmentTabCapability.Data) }, token);
            await Task.WhenAll(pluginTask, archiveTask, dataTask);
            var pluginPage = await pluginTask;
            var archivePage = await archiveTask;
            var dataPage = await dataTask;
            _resolvedPlugins = pluginPage.Items;
            _resolvedArchives = archivePage.Items;
            _resolvedData = dataPage.Items;
            _environmentPageTotals[EnvironmentTabCapability.Plugins] = pluginPage.TotalCount;
            _environmentPageTotals[EnvironmentTabCapability.Archives] = archivePage.TotalCount;
            _environmentPageTotals[EnvironmentTabCapability.Data] = dataPage.TotalCount;
            var descriptors = _workspaceState.GetEnvironmentTabs();
            var tabs = descriptors.Select(descriptor => descriptor.Capability switch
            {
                EnvironmentTabCapability.Plugins => CreateResolvedPluginsTab(descriptor),
                EnvironmentTabCapability.Archives => CreateResolvedArchivesTab(descriptor),
                EnvironmentTabCapability.Data => CreateResolvedDataTab(descriptor),
                _ => CreateEnvironmentTab(descriptor),
            }).ToArray();
            _suppressUiEvents = true;
            var selectedCapability = (EnvironmentTabs.SelectedItem as EnvironmentTabViewModel)?.Capability ??
                _workspaceState.SelectedEnvironmentTab;
            EnvironmentTabs.TabItemsSource = tabs;
            EnvironmentTabs.SelectedItem = tabs.FirstOrDefault(tab => tab.Capability == selectedCapability) ?? tabs.FirstOrDefault();
            UpdateEnvironmentPaging();
            _suppressUiEvents = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            EnvironmentObservationStatus.IsOpen = true;
            EnvironmentObservationStatus.Severity = InfoBarSeverity.Warning;
            EnvironmentObservationStatus.Title = "Snapshot query unavailable";
            EnvironmentObservationStatus.Message = $"The immutable snapshot page could not be queried ({exception.GetType().Name}). Refresh to reconstruct evidence.";
            _notificationRaised?.Invoke(
                EnvironmentObservationStatus.Title,
                EnvironmentObservationStatus.Message,
                EnvironmentObservationStatus.Severity);
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private EnvironmentTabViewModel CreateResolvedPluginsTab(EnvironmentTabDescriptor descriptor)
    {
        var rows = CreatePluginRows(_resolvedPlugins, connected: true, resolved: true);
        return new(descriptor,
            "Observed order is preserved and never corrected. Enabled-only load order is distinct from source priority; missing and disabled masters remain separate evidence.",
            rows,
            _environmentState?.SelectedPluginId,
            null);
    }

    private EnvironmentTabViewModel CreateResolvedArchivesTab(EnvironmentTabDescriptor descriptor)
    {
        var rows = _resolvedArchives.Select(archive => new EnvironmentRowViewModel(
            archive.Name,
            $"{archive.Format} v{archive.FormatVersion?.ToString() ?? NullCellValue} · provider {archive.SourceProvider} · {archive.MemberCount?.ToString("N0") ?? NullCellValue} indexed members",
            archive.Activation == ArchiveActivationState.Active ? "Active" : archive.Activation.ToString(),
            "INDEX",
            archive.SourceProvider,
            null,
            null,
            archive.Id,
            Column4: archive.MemberCount?.ToString("N0") ?? NullCellValue,
            CenterColumn2: true))
            .ToArray();
        return new(descriptor, DescribeEnvironmentTab(EnvironmentTabCapability.Archives), rows);
    }

    private EnvironmentTabViewModel CreateResolvedDataTab(EnvironmentTabDescriptor descriptor)
    {
        var rows = _resolvedData.Select(entry => new EnvironmentRowViewModel(
            entry.VirtualPath,
            $"{entry.ProviderCount} provider{(entry.ProviderCount == 1 ? string.Empty : "s")} · winner {entry.WinningProviderName ?? "not established"}",
            entry.WinningProviderName ?? NullCellValue,
            "PROVIDERS",
            FormatVirtualDataType(entry.VirtualPath),
            null,
            null,
            null,
            entry.Id,
            Column4: NullCellValue,
            Column5: NullCellValue))
            .ToArray();
        return new(descriptor, DescribeEnvironmentTab(EnvironmentTabCapability.Data), rows);
    }

    private static string FormatNullableFlag(bool? value) => value is null ? "unknown" : value.Value ? "yes" : "no";

    private static string FormatPluginFlags(PluginEntry plugin)
    {
        var flags = new List<string>();
        if (plugin.IsEnabled) flags.Add("Active");
        if (plugin.Observation?.HasMasterFlag == true) flags.Add("Master");
        if (plugin.Observation?.HasLightFlag == true || plugin.Observation?.Extension == PluginFileExtension.Esl) flags.Add("Light");
        if (plugin.Observation?.Warnings.Length > 0 || plugin.Health == HealthLevel.Warning) flags.Add("Warning");
        return flags.Count == 0 ? NullCellValue : string.Join(", ", flags);
    }

    private EnvironmentRowViewModel[] CreatePluginRows(
        IEnumerable<PluginEntry> plugins,
        bool connected,
        bool resolved)
    {
        var rows = new List<EnvironmentRowViewModel>();
        var offlineAlertDetails = BuildOfflineAlertDetails();
        var fullIndex = 0;
        var lightIndex = 0;
        foreach (var plugin in plugins.OrderBy(plugin => plugin.SourcePriority ?? plugin.LoadOrder ?? int.MaxValue))
        {
            var inferredCreationLight = plugin.Observation is null &&
                plugin.Name.StartsWith("cc", StringComparison.OrdinalIgnoreCase);
            var isLight = plugin.Observation?.HasLightFlag == true ||
                plugin.Observation?.Extension == PluginFileExtension.Esl ||
                inferredCreationLight;
            var modIndex = NullCellValue;
            if (plugin.IsEnabled)
            {
                modIndex = isLight
                    ? $"FE:{(lightIndex++).ToString("X3")}"
                    : fullIndex < 0xFE ? (fullIndex++).ToString("X2") : NullCellValue;
            }

            var detail = resolved
                ? $"{plugin.Observation?.Extension} · master={FormatNullableFlag(plugin.Observation?.HasMasterFlag)} · light={FormatNullableFlag(plugin.Observation?.HasLightFlag)} · provider {plugin.Observation?.SourceProvider ?? "unavailable"}"
                : inferredCreationLight
                    ? $"{plugin.Health} health · implicit Creation Club entry observed in MO2 loadorder.txt"
                    : connected
                        ? $"{plugin.Health} health · observed from MO2 profile sources"
                        : $"{plugin.Health} health · deterministic plugin fixture";
            if (GetAlertDetail(offlineAlertDetails, plugin.Id.Value) is { } offlineAlertDetail)
                detail += $"\n\nOffline alerts:\n{offlineAlertDetail}";
            rows.Add(new(
                plugin.Name,
                detail,
                resolved ? FormatPluginFlags(plugin) : plugin.IsEnabled ? "Active" : NullCellValue,
                "PRIORITY",
                (plugin.SourcePriority ?? plugin.LoadOrder)?.ToString() ?? NullCellValue,
                plugin.Id,
                null,
                Column4: modIndex,
                CenterColumn2: true,
                CenterColumn4: true));
        }
        return rows.ToArray();
    }

    private static string FormatVirtualDataType(string virtualPath)
    {
        var extension = Path.GetExtension(virtualPath);
        return string.IsNullOrWhiteSpace(extension) ? "Folder" : extension.TrimStart('.').ToUpperInvariant();
    }

    private static string MasterSummary(PluginEntry plugin)
    {
        var masters = plugin.Observation?.Masters ?? [];
        if (masters.IsEmpty) return "none observed";
        var missing = masters.Count(master => master.Status == PluginMasterStatus.Missing);
        var disabled = masters.Count(master => master.Status == PluginMasterStatus.PresentDisabled);
        return missing == 0 && disabled == 0 ? $"{masters.Length} present" : $"{missing} missing, {disabled} disabled";
    }

    public async Task RefreshEnvironmentAsync()
    {
        if (_environmentState is null)
        {
            return;
        }

        CancelEnvironmentButton.IsEnabled = true;
        EnvironmentProgressBar.Visibility = Visibility.Visible;
        EnvironmentObservationStatus.IsOpen = true;
        EnvironmentObservationStatus.Severity = InfoBarSeverity.Informational;
        EnvironmentObservationStatus.Title = "READ-ONLY INDEXING";
        EnvironmentObservationStatus.Message = "Observing authorized content with bounded readers. No process, mount, extraction, or write is performed.";
        try
        {
            var refreshTask = _environmentState.RefreshAsync(forceRefresh: true);
            while (!refreshTask.IsCompleted)
            {
                await Task.WhenAny(refreshTask, Task.Delay(100));
                if (_environmentState.Progress is { } current)
                {
                    EnvironmentObservationStatus.Message = $"{current.Stage} · {current.Status}";
                    EnvironmentProgressBar.IsIndeterminate = current.Fraction is null;
                    if (current.Fraction is double fraction)
                    {
                        EnvironmentProgressBar.Value = fraction * 100;
                    }
                }
            }

            var result = await refreshTask;
            _offlineAlertState?.Refresh(null, null, _profile, result);
            EnvironmentObservationStatus.Severity = result.Status switch
            {
                ResolvedEnvironmentRefreshStatus.Completed => InfoBarSeverity.Success,
                ResolvedEnvironmentRefreshStatus.Partial => InfoBarSeverity.Warning,
                ResolvedEnvironmentRefreshStatus.AuthorizationRequired => InfoBarSeverity.Warning,
                ResolvedEnvironmentRefreshStatus.Canceled => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Error,
            };
            EnvironmentObservationStatus.Title = result.Status.ToString().ToUpperInvariant();
            EnvironmentObservationStatus.Message = result.RequiredAuthorizations.IsEmpty
                ? result.Detail
                : $"{result.Detail} Required: {string.Join(", ", result.RequiredAuthorizations)}. Authorize exact roots through Manage game.";
            _notificationRaised?.Invoke(
                $"Environment scan · {EnvironmentObservationStatus.Title}",
                EnvironmentObservationStatus.Message,
                EnvironmentObservationStatus.Severity);
            if (result.HasCurrentSnapshot)
            {
                await RefreshResolvedEnvironmentTabsAsync();
                _catalogChanged?.Invoke();
            }
        }
        finally
        {
            EnvironmentProgressBar.Visibility = Visibility.Collapsed;
            CancelEnvironmentButton.IsEnabled = false;
        }
    }

    private void OnCancelEnvironmentClicked(object sender, RoutedEventArgs e) => _environmentState?.CancelRefresh();

    private void OnEnvironmentSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            SavePresentationState();
        }
        if (!_suppressUiEvents && _environmentState?.Snapshot is not null)
        {
            _environmentPageOffsets.Clear();
            _ = RefreshResolvedEnvironmentTabsAsync();
        }
    }

    private int GetPageOffset(EnvironmentTabCapability capability) =>
        _environmentPageOffsets.GetValueOrDefault(capability);

    private void UpdateEnvironmentPaging()
    {
        var capability = (EnvironmentTabs.SelectedItem as EnvironmentTabViewModel)?.Capability ?? EnvironmentTabCapability.None;
        var queryBacked = capability is EnvironmentTabCapability.Plugins or EnvironmentTabCapability.Archives or EnvironmentTabCapability.Data;
        var offset = GetPageOffset(capability);
        var total = _environmentPageTotals.GetValueOrDefault(capability);
        PreviousEnvironmentPageButton.IsEnabled = queryBacked && offset > 0;
        NextEnvironmentPageButton.IsEnabled = queryBacked && offset + 200 < total;
        EnvironmentPageText.Text = queryBacked
            ? $"{(total == 0 ? 0 : offset + 1):N0}–{Math.Min(offset + 200, total):N0} of {total:N0}"
            : "Current tab is catalog-backed";
    }

    private void OnPreviousEnvironmentPageClicked(object sender, RoutedEventArgs e) => ChangeEnvironmentPage(-200);

    private void OnNextEnvironmentPageClicked(object sender, RoutedEventArgs e) => ChangeEnvironmentPage(200);

    private void ChangeEnvironmentPage(int delta)
    {
        if (EnvironmentTabs.SelectedItem is not EnvironmentTabViewModel tab ||
            tab.Capability is not (EnvironmentTabCapability.Plugins or EnvironmentTabCapability.Archives or EnvironmentTabCapability.Data))
        {
            return;
        }

        var total = _environmentPageTotals.GetValueOrDefault(tab.Capability);
        _environmentPageOffsets[tab.Capability] = Math.Clamp(
            GetPageOffset(tab.Capability) + delta,
            0,
            Math.Max(0, ((Math.Max(0, total - 1)) / 200) * 200));
        _ = RefreshResolvedEnvironmentTabsAsync();
    }

    private void UpdateSelectionInspector(IReadOnlyList<ModRowViewModel> selectedRows)
    {
        InspectorWarningStatus.IsOpen = false;
        if (selectedRows.Count == 0)
        {
            InspectorSummaryText.Text = "No mod selected";
            InspectorAuthorityText.Text = "Select one inventory row to review its authority, reconciliation, metadata provenance, and warnings.";
            InspectorMetadataText.Text = string.Empty;
            InspectorUpdateText.Text = string.Empty;
            InspectorProvenanceText.Text = string.Empty;
            return;
        }

        if (selectedRows.Count > 1)
        {
            InspectorSummaryText.Text = $"{selectedRows.Count} rows selected";
            InspectorAuthorityText.Text = "Select one row for exact metadata and provenance. Multi-selection remains available for environment context only.";
            InspectorMetadataText.Text = string.Empty;
            InspectorUpdateText.Text = string.Empty;
            InspectorProvenanceText.Text = string.Empty;
            return;
        }

        var entry = selectedRows[0].Entry!;
        var inventory = entry.Inventory;
        InspectorSummaryText.Text = entry.Name;
        if (inventory is null)
        {
            InspectorAuthorityText.Text = "IN-MEMORY MOCK EVIDENCE · no external source observation";
            InspectorMetadataText.Text = $"Category: {entry.Category} · Installed version: {entry.Version}";
            InspectorUpdateText.Text = $"Represented update state: {entry.UpdateState}";
            InspectorProvenanceText.Text = $"Source label: {entry.Source}";
            return;
        }

        InspectorAuthorityText.Text =
            $"{FormatEnumLabel(inventory.Authority).ToUpperInvariant()} · {FormatEnumLabel(inventory.Reconciliation).ToUpperInvariant()} · " +
            $"source row {FormatOptionalNumber(inventory.SourceOrder)} · marker {inventory.SourceMarker ?? "unavailable"} · " +
            $"MO2 priority {FormatOptionalNumber(entry.Priority)}";

        var metadata = inventory.Metadata;
        if (metadata is null)
        {
            InspectorMetadataText.Text = $"Metadata: {FormatEnumLabel(inventory.MetadataAvailability)}. No value was invented.";
        }
        else
        {
            var category = metadata.CategoryNames.Length > 0
                ? string.Join(", ", metadata.CategoryNames)
                : metadata.CategoryIds.Length > 0
                    ? $"IDs {string.Join(", ", metadata.CategoryIds)}"
                    : "unavailable";
            var nexus = metadata.NexusModId is long nexusModId
                ? $"{metadata.NexusGameName ?? "game unavailable"} / {nexusModId}"
                : "unavailable";
            var rawValues = metadata.RawValues.Length == 0
                ? "No supported raw values"
                : string.Join(" · ", metadata.RawValues.Take(20).Select(value => $"{value.Key}={value.RawValue}")) +
                    (metadata.RawValues.Length > 20 ? $" · +{metadata.RawValues.Length - 20} more" : string.Empty);
            InspectorMetadataText.Text =
                $"Metadata: {FormatEnumLabel(inventory.MetadataAvailability)}\n" +
                $"Installed: {metadata.InstalledVersion ?? "unavailable"} · Newest cached: {metadata.NewestVersion ?? "unavailable"} · Categories: {category}\n" +
                $"Nexus identity: {nexus} · Installation file: {metadata.InstallationFile ?? "unavailable"} · Installed: {FormatTimestamp(metadata.InstallationTimeUtc)}\n" +
                $"Notes: {metadata.Notes ?? metadata.Comments ?? "unavailable"}\nRaw supported values: {rawValues}";
        }

        InspectorUpdateText.Text =
            $"LOCAL METADATA · NOT ONLINE VERIFIED · {FormatEnumLabel(inventory.UpdateEvidence.State)}\n" +
            $"{inventory.UpdateEvidence.Detail} · Provider time: {FormatTimestamp(inventory.UpdateEvidence.ProviderTimestampUtc)}";
        InspectorProvenanceText.Text =
            $"Source: {SanitizeSourceName(inventory.SourceName)} · Observed UTC: {inventory.ObservedAtUtc:u}\nFingerprint: {inventory.Fingerprint}";
        if (inventory.WarningCount > 0)
        {
            InspectorWarningStatus.IsOpen = true;
            InspectorWarningStatus.Title = $"{inventory.WarningCount} inventory warning{(inventory.WarningCount == 1 ? string.Empty : "s")}";
            InspectorWarningStatus.Message = string.Join(" ", inventory.Warnings.Take(8)) +
                (inventory.Warnings.Length > 8 ? $" +{inventory.Warnings.Length - 8} more." : string.Empty);
        }
    }

    private static string FormatOptionalNumber(int? value) => value?.ToString() ?? NullCellValue;

    private static TextAlignment ResolveCellAlignment(string value)
    {
        var normalized = value.Trim();
        if (string.Equals(normalized, NullCellValue, StringComparison.Ordinal))
        {
            return TextAlignment.Center;
        }
        if (decimal.TryParse(normalized.Replace(",", string.Empty), out _) ||
            DateTimeOffset.TryParse(normalized, out _) ||
            normalized.StartsWith("FE:", StringComparison.OrdinalIgnoreCase))
        {
            return TextAlignment.Right;
        }
        return TextAlignment.Left;
    }

    private static string FormatTimestamp(DateTimeOffset? value) => value?.ToString("u") ?? "unavailable";

    private static string SanitizeSourceName(string value) =>
        value.Contains(':') || value.Contains('\\') || value.Contains('/')
            ? "local manager metadata source"
            : value;

    private void OnModSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            SavePresentationState();
            _ = RefreshProjectionAsync(debounce: true);
        }
    }

    private void OnQueryControlChanged(object sender, RoutedEventArgs e)
    {
        if (!_suppressUiEvents)
        {
            _ = RefreshProjectionAsync(debounce: false);
        }
    }

    private void OnMoreFiltersClicked(object sender, RoutedEventArgs e)
    {
        var visible = MoreFiltersButton.IsChecked == true;
        AdvancedFilterRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SortFilterRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MoreFiltersButton.Content = visible ? "Less" : "More";
    }

    private void OnSortDirectionClicked(object sender, RoutedEventArgs e)
    {
        if (_workspaceState is null)
        {
            return;
        }

        var next = _workspaceState.Query.SortDirection == WorkspaceSortDirection.Ascending
            ? WorkspaceSortDirection.Descending
            : WorkspaceSortDirection.Ascending;
        _workspaceState.SetQuery(BuildQuery() with { SortDirection = next });
        SortDirectionButton.Content = next == WorkspaceSortDirection.Ascending ? "Ascending" : "Descending";
        _ = RefreshProjectionAsync(debounce: false);
    }

    private async void OnSeparatorSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _workspaceState is null || _profile is null ||
            SeparatorSelector.SelectedItem is not SeparatorChoice choice)
        {
            return;
        }

        if (!_workspaceState.SelectSeparator(choice.Id))
        {
            return;
        }

        ConfigureQueryControls(_profile);
        await RefreshProjectionAsync(debounce: false);
        var target = (ModsList.ItemsSource as IEnumerable<ModRowViewModel>)?
            .FirstOrDefault(row => row.Entry?.Id == choice.Id);
        if (target is not null)
        {
            ModsList.ScrollIntoView(target, ScrollIntoViewAlignment.Leading);
            ModsList.Focus(FocusState.Programmatic);
            InventoryViewStatus.IsOpen = true;
            InventoryViewStatus.Title = $"Section: {choice.Label}";
            InventoryViewStatus.Message = "Authoritative MO2 order restored. Grid scrolled to the separator and changed no external state.";
        }
    }

    private async void OnSeparatorRowClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModRowViewModel { Entry: ModEntry entry } } ||
            entry.Kind != ModEntryKind.Separator)
        {
            return;
        }

        if (!_collapsedSeparators.Add(entry.Id))
        {
            _collapsedSeparators.Remove(entry.Id);
        }

        SavePresentationState();
        await RefreshProjectionAsync(debounce: false, preserveWorkstationPosition: true);
    }

    private void OnModSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _workspaceState is null)
        {
            return;
        }

        var separators = ModsList.SelectedItems
            .OfType<ModRowViewModel>()
            .Where(row => row.Entry is null || row.Entry.Kind == ModEntryKind.Separator)
            .ToArray();
        foreach (var separator in separators)
        {
            ModsList.SelectedItems.Remove(separator);
        }

        _workspaceState.SelectMods(ModsList.SelectedItems
            .OfType<ModRowViewModel>()
            .Where(row => row.Entry is not null)
            .Select(row => row.Entry!.Id));
        UpdateSelectionPresentation();
        _contextChanged?.Invoke();
    }

    private void OnEnvironmentTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEnvironmentSearchPlaceholder();
        if (!_suppressUiEvents &&
            _workspaceState is not null &&
            EnvironmentTabs.SelectedItem is EnvironmentTabViewModel tab)
        {
            _workspaceState.SelectEnvironmentTab(tab.Capability);
            SavePresentationState();
            UpdateEnvironmentPaging();
            _contextChanged?.Invoke();
        }
    }

    private void UpdateEnvironmentSearchPlaceholder()
    {
        EnvironmentSearchBox.PlaceholderText = (EnvironmentTabs.SelectedItem as EnvironmentTabViewModel)?.Capability switch
        {
            EnvironmentTabCapability.Plugins => "Search plugins",
            EnvironmentTabCapability.Archives => "Search archives",
            EnvironmentTabCapability.Data => "Search data",
            EnvironmentTabCapability.Saves => "Search saves",
            EnvironmentTabCapability.Downloads => "Search downloads",
            _ => "Search current environment tab",
        };
    }

    private void OnEnvironmentRowSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents || _workspaceState is null || sender is not ListView list)
        {
            return;
        }

        var row = list.SelectedItem as EnvironmentRowViewModel;
        var tab = list.DataContext as EnvironmentTabViewModel;
        if (_isConnectedObservation && tab?.Capability == EnvironmentTabCapability.Plugins)
        {
            var plugin = _resolvedPlugins.FirstOrDefault(value => value.Id == row?.PluginId);
            _environmentState?.SelectPlugin(plugin?.Id);
            _workspaceState.SelectResolvedPlugin(plugin);
        }
        else if (_isConnectedObservation && tab?.Capability == EnvironmentTabCapability.Archives)
        {
            var archive = _resolvedArchives.FirstOrDefault(value => value.Id == row?.ArchiveId);
            _environmentState?.SelectArchive(archive?.Id);
            _workspaceState.SelectResolvedArchive(archive);
        }
        else if (_isConnectedObservation && tab?.Capability == EnvironmentTabCapability.Data)
        {
            var entry = _resolvedData.FirstOrDefault(value => value.Id == row?.VirtualPathId);
            _environmentState?.SelectVirtualPath(entry?.Id);
            _workspaceState.SelectResolvedData(entry);
        }
        else if (tab?.Capability == EnvironmentTabCapability.Plugins)
        {
            _workspaceState.SelectPlugin(row?.PluginId);
        }
        else if (_isConnectedObservation && tab?.Capability == EnvironmentTabCapability.Outputs)
        {
            _workspaceState.SelectEnvironmentEntry(row?.EnvironmentEntryId);
            _toolOutputState?.SelectOutput(row?.GeneratedOutputId);
        }
        else
        {
            _workspaceState.SelectEnvironmentEntry(row?.EnvironmentEntryId);
        }

        _contextChanged?.Invoke();
    }

    private async void OnEnvironmentRowInvoked(object sender, ItemClickEventArgs e)
    {
        if (_isConnectedObservation && e.ClickedItem is EnvironmentRowViewModel { GeneratedOutputId: GeneratedOutputId outputId })
        {
            var output = _toolOutputState?.Snapshot?.Outputs.FirstOrDefault(candidate => candidate.Id == outputId);
            if (output is not null)
            {
                Mo2GeneratedOutputObservation? rawOutput = null;
                if (_toolOutputQueryService is not null &&
                    _toolOutputQueryService.TryGetOutput(outputId, out var observedOutput))
                {
                    rawOutput = observedOutput;
                }

                var outputDialog = new GeneratedOutputInspectorDialog(output, rawOutput) { XamlRoot = XamlRoot };
                await outputDialog.ShowAsync();
            }

            return;
        }

        if (!_isConnectedObservation || e.ClickedItem is not EnvironmentRowViewModel row ||
            row.VirtualPathId is not VirtualPathId virtualPathId || _environmentState is null)
        {
            return;
        }

        var chain = await _environmentState.GetProviderChainAsync(virtualPathId);
        if (chain is null)
        {
            EnvironmentObservationStatus.Severity = InfoBarSeverity.Warning;
            EnvironmentObservationStatus.Title = "Provider chain unavailable";
            EnvironmentObservationStatus.Message = "This path is no longer present in the current immutable snapshot. Refresh to reconstruct evidence.";
            _notificationRaised?.Invoke(
                EnvironmentObservationStatus.Title,
                EnvironmentObservationStatus.Message,
                EnvironmentObservationStatus.Severity);
            return;
        }

        var dialog = new ProviderChainDialog(chain) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

    private void OnUnexpectedTabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        // TabItemsSource is immutable and this event deliberately performs no removal.
    }

    private void OnEnableSelectedClicked(object sender, RoutedEventArgs e) =>
        ExecuteCommand(() => _workspaceState?.SetSelectedEnabled(true), "Selected mods enabled in this mock session.");

    private void OnDisableSelectedClicked(object sender, RoutedEventArgs e) =>
        ExecuteCommand(() => _workspaceState?.SetSelectedEnabled(false), "Selected mods disabled in this mock session.");

    private async void OnModEnabledCheckBoxClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox ||
            checkBox.DataContext is not ModRowViewModel { Entry: ModEntry entry } row ||
            entry.Kind != ModEntryKind.Mod)
        {
            return;
        }

        var requestedState = checkBox.IsChecked == true;
        if (_isConnectedObservation)
        {
            checkBox.IsChecked = row.IsEnabled;
            if (_modStateMutationRunning) return;
            if (_installation is null || _profile is null || _modStateMutationService?.IsAvailable != true || _catalogRefreshRequested is null)
            {
                ShowCommandStatus("The authorized MO2 mod-state executor is unavailable. No external file was changed.", InfoBarSeverity.Error);
                return;
            }
            if (entry.Inventory?.SourceName != "modlist.txt" || entry.Inventory.SourceOrder is null || entry.Inventory.SourceMarker == "*")
            {
                ShowCommandStatus("Only one authoritative + or - entry from this profile's modlist.txt can be toggled. Grid-derived, missing, separator, and foreign/core rows remain protected.", InfoBarSeverity.Warning);
                return;
            }

            var verb = requestedState ? "Enable" : "Disable";
            var confirmation = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"{verb} {entry.Name}?",
                Content = $"Profile: {_profile.Name}\nMod: {entry.Name}\nChange: {(row.IsEnabled ? "Enabled" : "Disabled")} → {(requestedState ? "Enabled" : "Disabled")}\n\nGrid will change only this modlist.txt marker. It creates a timestamped backup, rereads the file to verify the result, and restores the backup if verification fails. MO2, Skyrim, SKSE, and xEdit must be closed.",
                PrimaryButtonText = verb,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            _modStateMutationRunning = true;
            checkBox.IsEnabled = false;
            try
            {
                var result = await _modStateMutationService.SetEnabledAsync(
                    _installation.Id, _profile.Name, entry.Name, row.IsEnabled, requestedState);
                ShowCommandStatus(
                    result.Changed
                        ? $"{entry.Name} is now {(result.IsEnabled ? "enabled" : "disabled")} in MO2. Backup and verification completed."
                        : result.Detail,
                    InfoBarSeverity.Success);
                await _catalogRefreshRequested();
            }
            catch (Exception exception)
            {
                checkBox.IsChecked = row.IsEnabled;
                ShowCommandStatus(exception.Message, InfoBarSeverity.Error);
            }
            finally
            {
                _modStateMutationRunning = false;
                checkBox.IsEnabled = true;
            }
            return;
        }

        if (!_permitsDevelopmentCommands)
        {
            checkBox.IsChecked = row.IsEnabled;
            ShowCommandStatus("This workspace does not permit mod-state changes. No external file was changed.", InfoBarSeverity.Warning);
            return;
        }

        _workspaceState?.SelectMods([entry.Id]);
        ExecuteCommand(
            () => _workspaceState?.SetSelectedEnabled(requestedState),
            requestedState ? "Selected mod enabled in this mock session." : "Selected mod disabled in this mock session.");
        _ = RefreshProjectionAsync(debounce: false);
    }

    private void OnMoveUpClicked(object sender, RoutedEventArgs e) =>
        ExecuteCommand(() => _workspaceState?.MoveSelected(-1), "Selected mod moved up in represented mod priority.");

    private void OnMoveDownClicked(object sender, RoutedEventArgs e) =>
        ExecuteCommand(() => _workspaceState?.MoveSelected(1), "Selected mod moved down in represented mod priority.");

    private void ExecuteCommand(Func<WorkspaceCommandResult?> command, string successMessage)
    {
        if (_isConnectedObservation)
        {
            ShowCommandStatus("Connected MO2 observations are read only. Grid did not change enablement, priority, load order, or external files.", InfoBarSeverity.Warning);
            return;
        }

        var result = command();
        if (result is null)
        {
            ShowCommandStatus("Workspace context is unavailable.", InfoBarSeverity.Error);
            return;
        }

        if (!result.Value.Succeeded)
        {
            var message = result.Value.Failure switch
            {
                WorkspaceCommandFailure.ContextUnavailable => "The selected installation/profile context is unavailable.",
                WorkspaceCommandFailure.NoSelection => "Select at least one represented mod first.",
                WorkspaceCommandFailure.ReorderUnavailable => "Reordering requires exactly one mod and the unfiltered ascending Mod priority view.",
                WorkspaceCommandFailure.Boundary => "The selected mod is already at the represented priority boundary.",
                _ => "The mock command was rejected without changing state.",
            };
            ShowCommandStatus(message, InfoBarSeverity.Warning);
            return;
        }

        ShowCommandStatus($"{successMessage} External files remain unchanged.", InfoBarSeverity.Success);
        _catalogChanged?.Invoke();
    }

    private void ShowCommandStatus(string message, InfoBarSeverity severity)
    {
        WorkspaceCommandStatus.Title = severity == InfoBarSeverity.Success
            ? "Mock command applied"
            : "Mock command not applied";
        WorkspaceCommandStatus.Message = message;
        WorkspaceCommandStatus.Severity = severity;
        WorkspaceCommandStatus.IsOpen = true;
    }

    private void OnWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyResponsiveLayout(e.NewSize.Width);

    private void ApplyResponsiveLayout(double width)
    {
        if (width <= 0 || PaneLayout is null)
        {
            return;
        }

        var contentWidth = Math.Max(0, width - PageHorizontalPadding);
        var shouldStack = contentWidth < StackThreshold;
        if (shouldStack)
        {
            _isStacked = true;
            PaneSplitter.Visibility = Visibility.Collapsed;
            EnvironmentPane.Margin = new Thickness(0, SplitterWidth, 0, 0);
            LeftPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            RightPaneColumn.Width = new GridLength(0);
            FirstPaneRow.Height = new GridLength(1, GridUnitType.Star);
            SecondPaneRow.Height = new GridLength(1, GridUnitType.Star);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(ModPane, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(ModPane, 0);
            Microsoft.UI.Xaml.Controls.Grid.SetRow(EnvironmentPane, 1);
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(EnvironmentPane, 0);
            return;
        }

        _isStacked = false;
        PaneSplitter.Visibility = Visibility.Visible;
        EnvironmentPane.Margin = new Thickness(0);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(ModPane, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ModPane, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(PaneSplitter, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(PaneSplitter, 1);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(EnvironmentPane, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(EnvironmentPane, 2);
        FirstPaneRow.Height = new GridLength(1, GridUnitType.Star);
        SecondPaneRow.Height = new GridLength(0);
        SplitterColumn.Width = new GridLength(SplitterWidth);

        var available = Math.Max(0, contentWidth - SplitterWidth);
        var desiredLeft = _userLeftPaneWidth ?? available * 0.5;
        var left = Math.Clamp(desiredLeft, LeftPaneMinimum, available - RightPaneMinimum);
        LeftPaneColumn.Width = new GridLength(left);
        RightPaneColumn.Width = new GridLength(Math.Max(RightPaneMinimum, available - left));
    }

    private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_isStacked)
        {
            return;
        }

        _userLeftPaneWidth = LeftPaneColumn.ActualWidth + e.HorizontalChange;
        ApplyResponsiveLayout(ActualWidth);
        SavePresentationState();
    }

    private void OnSplitterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_isStacked)
        {
            return;
        }

        if (e.Key == VirtualKey.Home)
        {
            _userLeftPaneWidth = null;
            ApplyResponsiveLayout(ActualWidth);
            SavePresentationState();
            e.Handled = true;
            return;
        }

        if (e.Key is not (VirtualKey.Left or VirtualKey.Right))
        {
            return;
        }

        _userLeftPaneWidth = LeftPaneColumn.ActualWidth + (e.Key == VirtualKey.Left ? -24 : 24);
        ApplyResponsiveLayout(ActualWidth);
        SavePresentationState();
        e.Handled = true;
    }

    private void OnModColumnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string raw } || !int.TryParse(raw, out var column))
        {
            return;
        }

        var minimum = column switch
        {
            3 => 56,
            4 => 72,
            _ => 42,
        };
        SharedModColumns.Resize(column - 2, -e.HorizontalChange, minimum, 260);
        ModConflictHeaderColumn.Width = SharedModColumns.Second;
        ModFlagsHeaderColumn.Width = SharedModColumns.Third;
        ModPriorityHeaderColumn.Width = SharedModColumns.Fourth;
        ModVersionHeaderColumn.Width = SharedModColumns.Fifth;
        ModCategoryHeaderColumn.Width = SharedModColumns.Sixth;
        SavePresentationState();
    }

    private void OnEnvironmentColumnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string raw } || !int.TryParse(raw, out var column))
        {
            return;
        }

        SharedEnvironmentColumns.Resize(column - 1, -e.HorizontalChange, 52, 280);
        SavePresentationState();
    }

    private void OnWorkspaceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && !string.IsNullOrEmpty(ModSearchBox.Text))
        {
            ModSearchBox.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void OnWorkspaceUnloaded(object sender, RoutedEventArgs e)
    {
        SavePresentationState();
        _projectionCancellation?.Cancel();
        _environmentQueryCancellation?.Cancel();
        _environmentState?.CancelRefresh();
    }

    private sealed class ModRowViewModel : INotifyPropertyChanged
    {
        private ModRowViewModel()
        {
            Entry = null;
            Name = "GRID-DERIVED · NOT IN MODLIST · NO MO2 PRIORITY";
            ModVisibility = Visibility.Collapsed;
            SeparatorVisibility = Visibility.Visible;
            IsEnabled = false;
            ToggleAutomationName = "Unavailable mod state";
            ToggleHelpText = "This derived boundary is not an MO2 mod.";
            Detail = Name;
            FlagsLabel = string.Empty;
            FlagsDetail = string.Empty;
            _separatorGlyph = "\uE70D";
            ConflictLabel = string.Empty;
            Category = string.Empty;
            InstalledVersion = string.Empty;
            UpdateLabel = string.Empty;
            Priority = string.Empty;
        }

        public ModRowViewModel(ModEntry entry, bool isExpanded, string? offlineAlertDetail = null)
        {
            Entry = entry;
            const string separatorSuffix = "_separator";
            Name = entry.Kind == ModEntryKind.Separator &&
                   entry.Name.EndsWith(separatorSuffix, StringComparison.OrdinalIgnoreCase)
                ? entry.Name[..^separatorSuffix.Length]
                : entry.Name;
            ModVisibility = entry.Kind == ModEntryKind.Separator ? Visibility.Collapsed : Visibility.Visible;
            SeparatorVisibility = entry.Kind == ModEntryKind.Separator ? Visibility.Visible : Visibility.Collapsed;
            IsEnabled = entry.IsEnabled;
            ToggleAutomationName = $"{(entry.IsEnabled ? "Disable" : "Enable")} {Name}";
            ToggleHelpText = "Shows the observed MO2 mod state. Production changes require exact confirmation, backup, verification, and automatic rollback on failure.";
            var evidenceDetail = entry.Inventory is null
                ? $"{entry.Health} health · {entry.Source}"
                : $"{FormatEnumLabel(entry.Inventory.Authority)} · {FormatEnumLabel(entry.Inventory.Reconciliation)} · {entry.Inventory.WarningCount} warning{(entry.Inventory.WarningCount == 1 ? string.Empty : "s")}";
            Detail = string.IsNullOrWhiteSpace(offlineAlertDetail)
                ? evidenceDetail
                : $"{evidenceDetail}\n\nOffline alerts:\n{offlineAlertDetail}";
            var flagParts = new List<string>();
            if (entry.Inventory?.WarningCount > 0 || entry.Health == HealthLevel.Warning)
            {
                flagParts.Add("⚠");
            }
            if (entry.UpdateState == ModUpdateState.UpdateAvailable)
            {
                flagParts.Add("↑");
            }
            FlagsLabel = string.Join(" ", flagParts);
            FlagsDetail = flagParts.Count == 0 ? "No observed flags" : Detail;
            _separatorGlyph = isExpanded ? "\uE70D" : "\uE76C";
            ConflictLabel = entry.Inventory is not null ? "Unavailable" : entry.ConflictState switch
            {
                ModConflictState.None => "None",
                ModConflictState.Overwrites => "Overwrites",
                ModConflictState.Overwritten => "Overwritten",
                ModConflictState.Mixed => "Mixed",
                _ => "Unknown",
            };
            Category = entry.Kind == ModEntryKind.Foreign
                ? "Game"
                : string.IsNullOrWhiteSpace(entry.Category) ? NullCellValue : entry.Category;
            InstalledVersion = string.IsNullOrWhiteSpace(entry.Version) || entry.Version == "Unknown" ? NullCellValue : entry.Version;
            UpdateLabel = entry.UpdateState switch
            {
                ModUpdateState.UpdateAvailable => $"{entry.AvailableVersion} available",
                ModUpdateState.Current => "Current",
                ModUpdateState.Ignored => "Ignored",
                ModUpdateState.Pinned => "Pinned",
                ModUpdateState.Error => "Error",
                _ => "Unknown",
            };
            Priority = entry.Priority?.ToString() ?? NullCellValue;
        }

        public static ModRowViewModel CreateDerivedBoundary() => new();

        public ModEntry? Entry { get; }
        public string Name { get; }
        public Visibility ModVisibility { get; }
        public Visibility SeparatorVisibility { get; }
        public bool IsEnabled { get; }
        public string ToggleAutomationName { get; }
        public string ToggleHelpText { get; }
        public string Detail { get; }
        public string FlagsLabel { get; }
        public string FlagsDetail { get; }
        private string _separatorGlyph;
        public string SeparatorGlyph
        {
            get => _separatorGlyph;
            private set
            {
                if (string.Equals(_separatorGlyph, value, StringComparison.Ordinal)) return;
                _separatorGlyph = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorGlyph)));
            }
        }
        public WorkspaceColumnLayout Columns => SharedModColumns;
        public string ConflictLabel { get; }
        public string Category { get; }
        public string InstalledVersion { get; }
        public TextAlignment CategoryAlignment => ResolveCellAlignment(Category);
        public TextAlignment InstalledVersionAlignment => ResolveCellAlignment(InstalledVersion);
        public TextAlignment PriorityAlignment => ResolveCellAlignment(Priority);
        public string UpdateLabel { get; }
        public string Priority { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void SynchronizePresentation(ModRowViewModel source) =>
            SeparatorGlyph = source.SeparatorGlyph;
    }

    private sealed record EnvironmentRowViewModel(
        string Name,
        string Detail,
        string Status,
        string OrderHeader,
        string OrderValue,
        PluginId? PluginId,
        EnvironmentEntryId? EnvironmentEntryId,
        ArchiveId? ArchiveId = null,
        VirtualPathId? VirtualPathId = null,
        GeneratedOutputId? GeneratedOutputId = null,
        string Column4 = "",
        string Column5 = "",
        bool CenterColumn2 = false,
        bool CenterColumn4 = false)
    {
        public string Column1 => Name;
        public string Column2 => Status;
        public string Column3 => OrderValue;
        public TextAlignment Column1Alignment => ResolveCellAlignment(Column1);
        public TextAlignment Column2Alignment => CenterColumn2 ? TextAlignment.Center : ResolveCellAlignment(Column2);
        public TextAlignment Column3Alignment => ResolveCellAlignment(Column3);
        public TextAlignment Column4Alignment => CenterColumn4 ? TextAlignment.Center : ResolveCellAlignment(Column4);
        public TextAlignment Column5Alignment => ResolveCellAlignment(Column5);
        public WorkspaceColumnLayout Columns => SharedEnvironmentColumns;

    }

    private sealed class EnvironmentTabViewModel
    {
        public EnvironmentTabViewModel(
            EnvironmentTabDescriptor descriptor,
            string description,
            IReadOnlyList<EnvironmentRowViewModel> rows,
            PluginId? selectedPluginId = null,
            EnvironmentEntryId? selectedEnvironmentEntryId = null,
            string? emptyMessage = null)
        {
            Capability = descriptor.Capability;
            Label = descriptor.Label;
            Description = description;
            Rows = rows;
            SelectedRow = rows.FirstOrDefault(row =>
                selectedPluginId is not null && row.PluginId == selectedPluginId ||
                selectedEnvironmentEntryId is not null && row.EnvironmentEntryId == selectedEnvironmentEntryId);
            AutomationName = $"{descriptor.Label} represented environment entries";
            ListVisibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyVisibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            HeaderVisibility = Visibility.Visible;
            (Header1, Header2, Header3, Header4, Header5) = descriptor.Capability switch
            {
                EnvironmentTabCapability.Plugins => ("NAME", "FLAGS", "PRIORITY", "MOD INDEX", ""),
                EnvironmentTabCapability.Archives => ("NAME", "FLAGS", "PROVIDER", "MEMBERS", ""),
                EnvironmentTabCapability.Data => ("NAME", "MOD", "TYPE", "SIZE", "DATE MODIFIED"),
                EnvironmentTabCapability.Saves => ("NAME", "FILE", "DETAILS", "STATUS", ""),
                EnvironmentTabCapability.Downloads => ("NAME", "STATUS", "SIZE", "FILETIME", ""),
                _ => ("NAME", "STATUS", "DETAIL", "", ""),
            };
            Header2Alignment = string.Equals(Header2, "FLAGS", StringComparison.Ordinal)
                ? TextAlignment.Center
                : TextAlignment.Left;
            Header4Alignment = Capability == EnvironmentTabCapability.Plugins
                ? TextAlignment.Center
                : TextAlignment.Left;
            EmptyMessage = emptyMessage ?? $"No observed {descriptor.Label.ToLowerInvariant()} entries for this profile.";
        }

        public EnvironmentTabCapability Capability { get; }
        public string Label { get; }
        public string Description { get; }
        public IReadOnlyList<EnvironmentRowViewModel> Rows { get; }
        public EnvironmentRowViewModel? SelectedRow { get; }
        public string AutomationName { get; }
        public Visibility ListVisibility { get; }
        public Visibility EmptyVisibility { get; }
        public Visibility HeaderVisibility { get; }
        public string Header1 { get; }
        public string Header2 { get; }
        public TextAlignment Header2Alignment { get; }
        public string Header3 { get; }
        public string Header4 { get; }
        public TextAlignment Header4Alignment { get; }
        public string Header5 { get; }
        public string EmptyMessage { get; }
        public WorkspaceColumnLayout Columns => SharedEnvironmentColumns;
    }

    private sealed class WorkspaceColumnLayout : INotifyPropertyChanged
    {
        private readonly GridLength[] _widths;

        public WorkspaceColumnLayout(double second, double third, double fourth, double fifth, double sixth) =>
            _widths = [new(second), new(third), new(fourth), new(fifth), new(sixth)];

        public event PropertyChangedEventHandler? PropertyChanged;

        public GridLength Second => _widths[0];
        public GridLength Third => _widths[1];
        public GridLength Fourth => _widths[2];
        public GridLength Fifth => _widths[3];
        public GridLength Sixth => _widths[4];
        public double[] Values => _widths.Select(width => width.Value).ToArray();

        public void Restore(IReadOnlyList<double>? values)
        {
            if (values is null) return;
            for (var index = 0; index < Math.Min(values.Count, _widths.Length); index++)
            {
                if (double.IsFinite(values[index]) && values[index] >= 0)
                {
                    _widths[index] = new GridLength(values[index]);
                }
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public void Resize(int index, double delta, double minimum, double maximum)
        {
            if (index < 0 || index >= _widths.Length)
            {
                return;
            }

            _widths[index] = new GridLength(Math.Clamp(_widths[index].Value + delta, minimum, maximum));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(index switch
            {
                0 => nameof(Second),
                1 => nameof(Third),
                2 => nameof(Fourth),
                3 => nameof(Fifth),
                _ => nameof(Sixth),
            }));
        }
    }
}
