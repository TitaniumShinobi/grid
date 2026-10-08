using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

/// <summary>
/// A read-only, pre-connection view of one existing MO2 profile.
/// </summary>
public sealed record Mo2ResolvedProfileChoice(
    Mo2ObservedProfile Profile,
    Mo2ProfileModInventory? Inventory)
{
    public ProfileId Id => Profile.Id;

    public string Name => Profile.Name;

    public int ManagedEnabledModCount => Inventory?.Entries.Count(mod =>
        mod.Marker == Mo2ModListMarker.Enabled &&
        mod.Reconciliation == Mo2ModReconciliationState.Matched) ?? 0;

    public int ManagedDisabledModCount => Inventory?.Entries.Count(mod =>
        mod.Marker == Mo2ModListMarker.Disabled &&
        mod.Reconciliation == Mo2ModReconciliationState.Matched) ?? 0;

    public int ForeignModCount => Inventory?.Entries.Count(mod =>
        mod.Marker == Mo2ModListMarker.Foreign ||
        mod.Reconciliation == Mo2ModReconciliationState.Foreign) ?? 0;

    public int SeparatorCount => Inventory?.Entries.Count(mod =>
        mod.Reconciliation == Mo2ModReconciliationState.Separator) ?? 0;

    public int ExplicitlyEnabledPluginCount =>
        Profile.PluginStates?.Entries.Count(plugin => plugin.IsEnabled) ?? 0;

    public int ExplicitlyDisabledPluginCount =>
        Profile.PluginStates?.Entries.Count(plugin => !plugin.IsEnabled) ?? 0;

    public int ActivePluginCount => Profile.Plugins.Count(plugin => plugin.IsEnabled);

    public int ImplicitlyActivePluginCount =>
        Math.Max(0, ActivePluginCount - ExplicitlyEnabledPluginCount);

    public int ProjectedPluginCount => Profile.Plugins.Length;

    public int WarningCount =>
        Profile.Observation.WarningCount + (Inventory?.Warnings.Length ?? 0);
}

/// <summary>
/// Validated MO2 environment evidence prepared for user review. Producing this
/// result does not create a connection or persist a manager reference.
/// </summary>
public sealed record Mo2ProfileEnvironmentResolution(
    string SelectedDirectory,
    Mo2DiscoveryCandidate Candidate,
    GameId GameId,
    string GameName,
    GameAdapterId AdapterId,
    Mo2ValidationRequest ValidationRequest,
    Mo2InstallationValidation Validation,
    Mo2ProfileSnapshot ProfileSnapshot,
    Mo2ModInventorySnapshot ModInventory,
    ImmutableArray<Mo2ResolvedProfileChoice> Profiles,
    bool ExactProfileSelected,
    ImmutableArray<Mo2ValidationIssue> Issues);
