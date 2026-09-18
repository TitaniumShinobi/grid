using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public enum ModEnabledFilter
{
    All,
    Enabled,
    Disabled,
}

public enum ModSortColumn
{
    Priority,
    Name,
    Category,
    UpdateState,
    Reconciliation,
    InstallationTime,
}

public enum WorkspaceSortDirection
{
    Ascending,
    Descending,
}

public enum WorkspaceCommandFailure
{
    None,
    ContextUnavailable,
    NoSelection,
    ReorderUnavailable,
    Boundary,
}

public readonly record struct ModListQuery(
    string SearchText,
    ModEnabledFilter EnabledFilter,
    string? Category,
    bool ConflictsOnly,
    bool UpdatesOnly,
    ModSortColumn SortColumn,
    WorkspaceSortDirection SortDirection,
    ModReconciliationState? Reconciliation = null,
    bool WarningsOnly = false,
    ModUpdateState? UpdateState = null)
{
    public static ModListQuery Default { get; } = new(
        string.Empty,
        ModEnabledFilter.All,
        null,
        false,
        false,
        ModSortColumn.Priority,
        WorkspaceSortDirection.Ascending,
        null,
        false,
        null);

    public bool IsUnfilteredPriorityView =>
        string.IsNullOrWhiteSpace(SearchText) &&
        EnabledFilter == ModEnabledFilter.All &&
        Category is null &&
        !ConflictsOnly &&
        !UpdatesOnly &&
        Reconciliation is null &&
        !WarningsOnly &&
        UpdateState is null &&
        SortColumn == ModSortColumn.Priority &&
        SortDirection == WorkspaceSortDirection.Ascending;
}

public sealed record ModSeparatorDescriptor(
    ModId Id,
    string Name,
    int DisplayOrder,
    int? ManagerPriority,
    ModInventoryAuthority Authority);

public sealed record EnvironmentTabDescriptor(
    EnvironmentTabCapability Capability,
    string Label);

public readonly record struct WorkspaceCommandResult(
    bool Succeeded,
    WorkspaceCommandFailure Failure)
{
    public static WorkspaceCommandResult Applied { get; } = new(true, WorkspaceCommandFailure.None);

    public static WorkspaceCommandResult Rejected(WorkspaceCommandFailure failure) => new(false, failure);
}

public sealed class WorkspaceSessionState
{
    private static readonly ImmutableArray<EnvironmentTabDescriptor> CanonicalTabs =
    [
        new(EnvironmentTabCapability.Plugins, "Plugins"),
        new(EnvironmentTabCapability.Archives, "Archives"),
        new(EnvironmentTabCapability.Data, "Data"),
        new(EnvironmentTabCapability.Saves, "Saves"),
        new(EnvironmentTabCapability.Downloads, "Downloads"),
        new(EnvironmentTabCapability.Conflicts, "Conflicts"),
        new(EnvironmentTabCapability.Outputs, "Outputs"),
        new(EnvironmentTabCapability.Activity, "Activity"),
    ];

    private readonly string _baseRevision;
    private WorkspaceSelection _contextSelection;
    private int _revisionNumber;

    public WorkspaceSessionState(ShellNavigationState shell)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _baseRevision = shell.Workspace.Catalog.Revision;
        SynchronizeContext();
    }

    public ShellNavigationState Shell { get; }

    public ModListQuery Query { get; private set; } = ModListQuery.Default;

    public ImmutableHashSet<ModId> SelectedModIds { get; private set; } = [];

    public PluginId? SelectedPluginId { get; private set; }

    public EnvironmentEntryId? SelectedEnvironmentEntryId { get; private set; }

    public EnvironmentTabCapability? SelectedEnvironmentTab { get; private set; }

    public ResolvedEnvironmentSelection? SelectedResolvedEnvironment { get; private set; }

    public GridCatalogSnapshot Catalog => Shell.Workspace.Catalog;

    public void SynchronizeContext()
    {
        var selection = Shell.CurrentSelection;
        if (_contextSelection == selection)
        {
            ReconcileSelections();
            ReconcileEnvironmentTab();
            return;
        }

        _contextSelection = selection;
        Query = ModListQuery.Default;
        SelectedModIds = [];
        SelectedPluginId = null;
        SelectedEnvironmentEntryId = null;
        SelectedResolvedEnvironment = null;
        SelectedEnvironmentTab = null;
        ReconcileEnvironmentTab();
    }

    public void SetQuery(ModListQuery query)
    {
        SynchronizeContext();
        Query = query with { SearchText = query.SearchText?.Trim() ?? string.Empty };
        var visibleIds = GetVisibleMods()
            .Where(IsSelectableInventoryRow)
            .Select(mod => mod.Id)
            .ToImmutableHashSet();
        SelectedModIds = SelectedModIds.Intersect(visibleIds);
    }

    public void SelectMods(IEnumerable<ModId> modIds)
    {
        ArgumentNullException.ThrowIfNull(modIds);
        SynchronizeContext();
        var profile = FindCurrentProfile();
        if (profile is null)
        {
            SelectedModIds = [];
            return;
        }

        var validIds = profile.Mods
            .Where(IsSelectableInventoryRow)
            .Select(mod => mod.Id)
            .ToImmutableHashSet();
        SelectedModIds = modIds.ToImmutableHashSet().Intersect(validIds);
    }

    public void SelectPlugin(PluginId? pluginId)
    {
        SynchronizeContext();
        var profile = FindCurrentProfile();
        SelectedPluginId = pluginId is not null &&
            profile is not null &&
            profile.Plugins.Any(plugin => plugin.Id == pluginId.Value)
                ? pluginId
                : null;
        SelectedEnvironmentEntryId = null;
        SelectedResolvedEnvironment = null;
    }

    public void SelectResolvedPlugin(PluginEntry? plugin)
    {
        SynchronizeContext();
        SelectedPluginId = plugin?.Id;
        SelectedEnvironmentEntryId = null;
        SelectedResolvedEnvironment = plugin is null
            ? null
            : new ResolvedEnvironmentSelection(
                ResolvedSelectionKind.Plugin,
                plugin.Id.Value,
                plugin.Name,
                $"{(plugin.IsEnabled ? "Enabled" : "Disabled")} - load order {plugin.LoadOrder?.ToString() ?? "unavailable"}");
    }

    public void SelectResolvedArchive(ResolvedArchiveEntry? archive)
    {
        SynchronizeContext();
        SelectedPluginId = null;
        SelectedEnvironmentEntryId = null;
        SelectedResolvedEnvironment = archive is null
            ? null
            : new ResolvedEnvironmentSelection(
                ResolvedSelectionKind.Archive,
                archive.Id.Value,
                archive.Name,
                $"{archive.Activation} - {archive.SupportStatus}");
    }

    public void SelectResolvedData(VirtualDataEntry? entry)
    {
        SynchronizeContext();
        SelectedPluginId = null;
        SelectedEnvironmentEntryId = null;
        SelectedResolvedEnvironment = entry is null
            ? null
            : new ResolvedEnvironmentSelection(
                ResolvedSelectionKind.VirtualPath,
                entry.Id.Value,
                entry.VirtualPath,
                $"{entry.ProviderCount} provider(s) - {entry.WinnerConfidence}");
    }

    public void SelectEnvironmentEntry(EnvironmentEntryId? entryId)
    {
        SynchronizeContext();
        var profile = FindCurrentProfile();
        SelectedEnvironmentEntryId = entryId is not null &&
            profile is not null &&
            profile.EnvironmentEntries.Any(entry =>
                entry.Id == entryId.Value && entry.Tab == SelectedEnvironmentTab)
                ? entryId
                : null;
        SelectedPluginId = null;
        SelectedResolvedEnvironment = null;
    }

    public ImmutableArray<ModEntry> GetVisibleMods()
    {
        SynchronizeContext();
        var profile = FindCurrentProfile();
        return profile is null ? [] : ProjectMods(profile.Mods, Query);
    }

    public ImmutableArray<ModSeparatorDescriptor> GetSeparatorDescriptors()
    {
        SynchronizeContext();
        var profile = FindCurrentProfile();
        return profile is null
            ? []
            : OrderForAuthoritativeView(profile.Mods)
                .Where(mod => mod.Kind == ModEntryKind.Separator)
                .Select(mod => new ModSeparatorDescriptor(
                    mod.Id,
                    mod.Name,
                    GetDisplayOrder(mod),
                    mod.Priority,
                    mod.Inventory?.Authority ?? ModInventoryAuthority.Mock))
                .ToImmutableArray();
    }

    public bool SelectSeparator(ModId separatorId)
    {
        SynchronizeContext();
        var profile = FindCurrentProfile();
        if (profile is null || !profile.Mods.Any(mod => mod.Id == separatorId && mod.Kind == ModEntryKind.Separator))
        {
            return false;
        }

        Query = ModListQuery.Default;
        SelectedModIds = [];
        return true;
    }

    public ImmutableArray<EnvironmentTabDescriptor> GetEnvironmentTabs()
    {
        SynchronizeContext();
        var installation = FindCurrentInstallation();
        var profile = FindCurrentProfile();
        return installation is null || profile is null
            ? []
            : CreateEnvironmentTabs(installation.Capabilities, profile.Features);
    }

    public void SelectEnvironmentTab(EnvironmentTabCapability capability)
    {
        SynchronizeContext();
        var next = GetEnvironmentTabs().Any(tab => tab.Capability == capability)
            ? capability
            : GetEnvironmentTabs().FirstOrDefault()?.Capability;
        if (SelectedEnvironmentTab != next)
        {
            SelectedPluginId = null;
            SelectedEnvironmentEntryId = null;
            SelectedResolvedEnvironment = null;
        }

        SelectedEnvironmentTab = next;
    }

    public WorkspaceCommandResult SetSelectedEnabled(bool isEnabled)
    {
        SynchronizeContext();
        var context = ResolveAvailableContext();
        if (context is null)
        {
            return WorkspaceCommandResult.Rejected(WorkspaceCommandFailure.ContextUnavailable);
        }

        if (SelectedModIds.IsEmpty)
        {
            return WorkspaceCommandResult.Rejected(WorkspaceCommandFailure.NoSelection);
        }

        var updatedMods = context.Value.Profile.Mods
            .Select(mod => SelectedModIds.Contains(mod.Id) && mod.Kind == ModEntryKind.Mod
                ? mod with { IsEnabled = isEnabled }
                : mod)
            .ToImmutableArray();
        Apply(context.Value.Game, context.Value.Installation, context.Value.Profile with { Mods = updatedMods });
        return WorkspaceCommandResult.Applied;
    }

    public WorkspaceCommandResult MoveSelected(int offset)
    {
        SynchronizeContext();
        var context = ResolveAvailableContext();
        if (context is null)
        {
            return WorkspaceCommandResult.Rejected(WorkspaceCommandFailure.ContextUnavailable);
        }

        if (SelectedModIds.Count != 1)
        {
            return WorkspaceCommandResult.Rejected(SelectedModIds.IsEmpty
                ? WorkspaceCommandFailure.NoSelection
                : WorkspaceCommandFailure.ReorderUnavailable);
        }

        if (!Query.IsUnfilteredPriorityView || offset is not (-1 or 1))
        {
            return WorkspaceCommandResult.Rejected(WorkspaceCommandFailure.ReorderUnavailable);
        }

        var ordered = context.Value.Profile.Mods.OrderBy(mod => mod.Priority).ToList();
        var selectedId = SelectedModIds.Single();
        var currentIndex = ordered.FindIndex(mod => mod.Id == selectedId && mod.Kind == ModEntryKind.Mod);
        var targetIndex = currentIndex + offset;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
        {
            return WorkspaceCommandResult.Rejected(WorkspaceCommandFailure.Boundary);
        }

        (ordered[currentIndex], ordered[targetIndex]) = (ordered[targetIndex], ordered[currentIndex]);
        var reordered = ordered
            .Select((mod, priority) => mod with { Priority = priority })
            .ToImmutableArray();
        Apply(context.Value.Game, context.Value.Installation, context.Value.Profile with { Mods = reordered });
        return WorkspaceCommandResult.Applied;
    }

    public static ImmutableArray<EnvironmentTabDescriptor> CreateEnvironmentTabs(
        WorkspaceCapabilities capabilities,
        ProfileFeature profileFeatures) =>
        CanonicalTabs
            .Where(tab => capabilities.Supports(tab.Capability))
            .Where(tab => tab.Capability != EnvironmentTabCapability.Saves ||
                profileFeatures.HasFlag(ProfileFeature.Saves))
            .ToImmutableArray();

    public static ImmutableArray<ModEntry> ProjectMods(
        ImmutableArray<ModEntry> mods,
        ModListQuery query)
    {
        if (mods.IsDefault)
        {
            throw new ArgumentException("Mod collection must be initialized.", nameof(mods));
        }

        var ordered = OrderForAuthoritativeView(mods).ToArray();
        var matchingIds = ordered
            .Where(mod => IsSelectableInventoryRow(mod) && Matches(mod, query))
            .Select(mod => mod.Id)
            .ToHashSet();

        if (query.SortColumn == ModSortColumn.Priority &&
            query.SortDirection == WorkspaceSortDirection.Ascending)
        {
            var projected = new List<ModEntry>();
            for (var index = 0; index < ordered.Length; index++)
            {
                var mod = ordered[index];
                if (IsSelectableInventoryRow(mod))
                {
                    if (matchingIds.Contains(mod.Id))
                    {
                        projected.Add(mod);
                    }

                    continue;
                }

                var groupHasMatch = ordered
                    .Skip(index + 1)
                    .TakeWhile(candidate => candidate.Kind != ModEntryKind.Separator)
                    .Any(candidate => matchingIds.Contains(candidate.Id));
                if (groupHasMatch)
                {
                    projected.Add(mod);
                }
            }

            return projected.ToImmutableArray();
        }

        var matchingMods = ordered.Where(mod => matchingIds.Contains(mod.Id));
        var sorted = query.SortColumn switch
        {
            ModSortColumn.Name => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Priority)
                : matchingMods.OrderByDescending(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Priority),
            ModSortColumn.Category => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(mod => mod.Category, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Priority)
                : matchingMods.OrderByDescending(mod => mod.Category, StringComparer.OrdinalIgnoreCase).ThenBy(mod => mod.Priority),
            ModSortColumn.UpdateState => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(mod => mod.UpdateState).ThenBy(GetDisplayOrder)
                : matchingMods.OrderByDescending(mod => mod.UpdateState).ThenBy(GetDisplayOrder),
            ModSortColumn.Reconciliation => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(mod => mod.Inventory?.Reconciliation).ThenBy(GetDisplayOrder)
                : matchingMods.OrderByDescending(mod => mod.Inventory?.Reconciliation).ThenBy(GetDisplayOrder),
            ModSortColumn.InstallationTime => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(mod => mod.Inventory?.Metadata?.InstallationTimeUtc).ThenBy(GetDisplayOrder)
                : matchingMods.OrderByDescending(mod => mod.Inventory?.Metadata?.InstallationTimeUtc).ThenBy(GetDisplayOrder),
            _ => query.SortDirection == WorkspaceSortDirection.Ascending
                ? matchingMods.OrderBy(GetDisplayOrder)
                : matchingMods.OrderByDescending(GetDisplayOrder),
        };
        return sorted.ToImmutableArray();
    }

    private static bool Matches(ModEntry mod, ModListQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.SearchText) &&
            !Contains(mod.Name, query.SearchText))
        {
            return false;
        }

        if (mod.Inventory?.Authority == ModInventoryAuthority.GridDerived &&
            query.EnabledFilter != ModEnabledFilter.All)
        {
            return false;
        }

        if (query.EnabledFilter == ModEnabledFilter.Enabled && !mod.IsEnabled ||
            query.EnabledFilter == ModEnabledFilter.Disabled && mod.IsEnabled)
        {
            return false;
        }

        if (query.Category is not null &&
            !string.Equals(mod.Category, query.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.ConflictsOnly && mod.ConflictState is ModConflictState.None or ModConflictState.Unknown)
        {
            return false;
        }

        if (query.Reconciliation is ModReconciliationState reconciliation &&
            mod.Inventory?.Reconciliation != reconciliation)
        {
            return false;
        }

        if (query.WarningsOnly && (mod.Inventory?.WarningCount ?? 0) == 0)
        {
            return false;
        }

        if (query.UpdateState is ModUpdateState updateState && mod.UpdateState != updateState)
        {
            return false;
        }

        return !query.UpdatesOnly || mod.UpdateState == ModUpdateState.UpdateAvailable;
    }

    private static bool Contains(string value, string search) =>
        value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesMetadata(ModMetadataSummary? metadata, string search) =>
        metadata is not null &&
        (ContainsNullable(metadata.InstallationFile, search) ||
         ContainsNullable(metadata.NexusGameName, search) ||
         ContainsNullable(metadata.Notes, search) ||
         ContainsNullable(metadata.Comments, search) ||
         ContainsNullable(metadata.Repository, search) ||
         metadata.NexusModId?.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) == true ||
         metadata.CategoryNames.Any(value => Contains(value, search)) ||
         metadata.CategoryIds.Any(value => value.ToString().Contains(search, StringComparison.OrdinalIgnoreCase)) ||
         metadata.RawValues.Any(value =>
             Contains(value.Key, search) ||
             Contains(value.RawValue, search) ||
             ContainsNullable(value.NormalizedValue, search)));

    private static bool ContainsNullable(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private void ReconcileEnvironmentTab()
    {
        var tabs = GetEnvironmentTabsWithoutSynchronization();
        if (SelectedEnvironmentTab is null || !tabs.Any(tab => tab.Capability == SelectedEnvironmentTab))
        {
            SelectedEnvironmentTab = tabs.FirstOrDefault()?.Capability;
        }
    }

    private void ReconcileSelections()
    {
        var profile = FindCurrentProfile();
        if (profile is null)
        {
            SelectedModIds = [];
            SelectedPluginId = null;
            SelectedEnvironmentEntryId = null;
            SelectedResolvedEnvironment = null;
            return;
        }

        var modIds = profile.Mods
            .Where(IsSelectableInventoryRow)
            .Select(mod => mod.Id)
            .ToImmutableHashSet();
        SelectedModIds = SelectedModIds.Intersect(modIds);

        if (SelectedPluginId is PluginId pluginId &&
            !profile.Plugins.Any(plugin => plugin.Id == pluginId) &&
            !(SelectedResolvedEnvironment is
            {
                Kind: ResolvedSelectionKind.Plugin,
                StableId: var stableId,
            } && string.Equals(stableId, pluginId.Value, StringComparison.Ordinal)))
        {
            SelectedPluginId = null;
            if (SelectedResolvedEnvironment?.Kind == ResolvedSelectionKind.Plugin)
            {
                SelectedResolvedEnvironment = null;
            }
        }

        if (SelectedEnvironmentEntryId is EnvironmentEntryId entryId &&
            !profile.EnvironmentEntries.Any(entry =>
                entry.Id == entryId && entry.Tab == SelectedEnvironmentTab))
        {
            SelectedEnvironmentEntryId = null;
        }
    }

    private ImmutableArray<EnvironmentTabDescriptor> GetEnvironmentTabsWithoutSynchronization()
    {
        var installation = FindCurrentInstallation();
        var profile = FindCurrentProfile();
        return installation is null || profile is null
            ? []
            : CreateEnvironmentTabs(installation.Capabilities, profile.Features);
    }

    private (ManagedGame Game, ManagedInstallation Installation, Profile Profile)? ResolveAvailableContext()
    {
        var game = FindCurrentGame();
        var installation = FindCurrentInstallation();
        var profile = FindCurrentProfile();
        return game is null ||
            installation is null ||
            profile is null ||
            installation.Metadata.Provenance != InstallationProvenanceKind.Mock ||
            installation.Metadata.Availability != InstallationAvailability.Available
            ? null
            : (game, installation, profile);
    }

    private ManagedGame? FindCurrentGame() => Shell.CurrentSelection.GameId is GameId gameId
        ? Catalog.Games.FirstOrDefault(game => game.Id == gameId)
        : null;

    private ManagedInstallation? FindCurrentInstallation()
    {
        var game = FindCurrentGame();
        return game is not null && Shell.CurrentSelection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(installation => installation.Id == installationId)
            : null;
    }

    private Profile? FindCurrentProfile()
    {
        var installation = FindCurrentInstallation();
        return installation is not null && Shell.CurrentSelection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            : null;
    }

    private void Apply(ManagedGame game, ManagedInstallation installation, Profile updatedProfile)
    {
        var profile = installation.Profiles.First(candidate => candidate.Id == updatedProfile.Id);
        var updatedInstallation = installation with
        {
            Profiles = installation.Profiles.SetItem(installation.Profiles.IndexOf(profile), updatedProfile),
        };
        var updatedGame = game with
        {
            Installations = game.Installations.SetItem(game.Installations.IndexOf(installation), updatedInstallation),
        };
        var updatedCatalog = new GridCatalogSnapshot(
            $"{_baseRevision}.workspace-{++_revisionNumber:D3}",
            Catalog.SourceKind,
            Catalog.Games.SetItem(Catalog.Games.IndexOf(game), updatedGame));
        Shell.ReplaceCatalog(updatedCatalog);
    }

    private static bool IsSelectableInventoryRow(ModEntry mod) => mod.Kind != ModEntryKind.Separator;

    private static int GetDisplayOrder(ModEntry mod) =>
        mod.Inventory?.DisplayOrder ?? mod.Priority ?? int.MaxValue;

    private static IOrderedEnumerable<ModEntry> OrderForAuthoritativeView(IEnumerable<ModEntry> mods) =>
        mods.OrderBy(GetDisplayOrder).ThenBy(mod => mod.Id.Value, StringComparer.Ordinal);
}
