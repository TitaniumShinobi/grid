using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum CatalogSourceKind
{
    Mock,
    Adapter,
    Mixed,
}

public enum InstallationProvenanceKind
{
    Mock,
    ConnectedReference,
}

public enum WorkspaceAccessMode
{
    ReadOnly,
    Operational,
}

public enum InstallationKind
{
    External,
    Managed,
}

public enum InstallationAvailability
{
    Available,
    Missing,
    Unavailable,
}

public enum ProfileLifecycleState
{
    Available,
    Archived,
}

public enum ProfileObservationStatus
{
    Complete,
    Partial,
    Inconsistent,
    Unavailable,
}

public enum ManagerProfileState
{
    Active,
    Inactive,
    Unknown,
}

public enum ProfileSourceAvailability
{
    Read,
    RequiredMissing,
    OptionalAbsent,
    Inaccessible,
    Oversized,
    ChangedDuringRead,
    AuthorizationRequired,
}

public enum ProfileSourceParseStatus
{
    Parsed,
    UnsupportedSyntax,
    Malformed,
    NotParsed,
}

[Flags]
public enum ProfileFeature
{
    None = 0,
    Saves = 1 << 0,
    ConfigurationFiles = 1 << 1,
}

public enum ModEntryKind
{
    Mod,
    Separator,
    Foreign,
    Backup,
    UnlistedDirectory,
}

public enum ModUpdateState
{
    Current,
    UpdateAvailable,
    Ignored,
    Pinned,
    Error,
    Unknown,
}

public enum ModInventoryAuthority
{
    Mock,
    ManagerAuthoritative,
    GridDerived,
}

public enum ModReconciliationState
{
    Matched,
    Separator,
    Foreign,
    Backup,
    Missing,
    Duplicate,
    Ambiguous,
    Unlisted,
    Inaccessible,
    AuthorizationRequired,
    Inconsistent,
}

public enum ModMetadataAvailability
{
    Available,
    Missing,
    NotApplicable,
    Inaccessible,
    Malformed,
    Oversized,
    Inconsistent,
}

public enum ModInventoryObservationStatus
{
    Complete,
    Partial,
    Inconsistent,
    AuthorizationRequired,
    Unavailable,
}

public enum ModConflictState
{
    None,
    Overwrites,
    Overwritten,
    Mixed,
    Unknown,
}

[Flags]
public enum WorkspaceFeature
{
    None = 0,
    Profiles = 1 << 0,
    ModList = 1 << 1,
    Health = 1 << 2,
    Tools = 1 << 3,
    LaunchTargets = 1 << 4,
}

[Flags]
public enum EnvironmentTabCapability
{
    None = 0,
    Plugins = 1 << 0,
    Archives = 1 << 1,
    Data = 1 << 2,
    Saves = 1 << 3,
    Downloads = 1 << 4,
    Conflicts = 1 << 5,
    Outputs = 1 << 6,
    Activity = 1 << 7,
}

public enum AvailabilityState
{
    Available,
    PreviewOnly,
    Unavailable,
}

public enum LaunchTargetKind
{
    Game,
    Tool,
    GridInternal,
}

public enum ToolKind
{
    ExternalUtility,
    BuildTool,
}

public enum ConfiguredPathAnchor
{
    GameRoot,
    InstallationRoot,
    ManagerRoot,
    GridRoot,
}

public enum EnvironmentPolicy
{
    RestrictedInherited,
    CleanAllowlist,
    AdapterManaged,
    GridInternal,
}

public enum GridInternalRoute
{
    Diagnostics,
}

public enum ProfileValidationState
{
    Unknown,
    Valid,
    Advisory,
    Invalid,
}

public enum PendingChangeState
{
    Unknown,
    None,
    Pending,
}

public enum OnlineCleanVerificationState
{
    NotApplicable,
    Unverified,
    Verified,
}

public enum LaunchSafetyGateKind
{
    ProfileState,
    PendingDeployment,
    PendingOutputs,
    OnlineCleanVerification,
    Snapshot,
    Approval,
}

public enum LaunchGateDisposition
{
    Satisfied,
    Warning,
    Blocking,
    Recommendation,
    RequiredAtExecution,
}

public readonly record struct WorkspaceCapabilities(
    WorkspaceFeature Features,
    EnvironmentTabCapability EnvironmentTabs)
{
    public bool Supports(WorkspaceFeature feature) => (Features & feature) == feature;

    public bool Supports(EnvironmentTabCapability tab) => (EnvironmentTabs & tab) == tab;

    public bool IsSubsetOf(WorkspaceCapabilities other) =>
        (Features & ~other.Features) == WorkspaceFeature.None &&
        (EnvironmentTabs & ~other.EnvironmentTabs) == EnvironmentTabCapability.None;
}

public sealed record GameAdapterIdentity(GameAdapterId Id, string Name);

public sealed record Advisory(string Code, string Title, string Detail, HealthLevel Level);

public sealed record HealthSummary(
    HealthLevel Level,
    string Label,
    ImmutableArray<Advisory> Advisories);

public sealed record InstallationMetadata(
    string? LocationDisplay,
    InstallationAvailability Availability,
    string StatusDetail,
    InstallationProvenanceKind Provenance = InstallationProvenanceKind.Mock,
    InstallationReferenceId? ReferenceId = null,
    ToolOutputObservationSummary? ToolOutputObservation = null,
    string? ConnectionFingerprint = null);

public sealed record ModMetadataRawValue(
    string Key,
    string RawValue,
    string? NormalizedValue);

public sealed record ModMetadataSummary(
    string? InstalledVersion,
    string? NewestVersion,
    string? IgnoredVersion,
    ImmutableArray<int> CategoryIds,
    ImmutableArray<string> CategoryNames,
    string? NexusGameName,
    long? NexusModId,
    string? InstallationFile,
    DateTimeOffset? InstallationTimeUtc,
    string? Notes,
    string? Comments,
    string? Repository,
    DateTimeOffset? ProviderTimestampUtc,
    string? ProviderStatus,
    ImmutableArray<ModMetadataRawValue> RawValues);

public sealed record ModUpdateEvidence(
    ModUpdateState State,
    string Detail,
    DateTimeOffset? ProviderTimestampUtc,
    bool NetworkChecked);

public sealed record ModInventoryObservation(
    ModInventoryAuthority Authority,
    ModReconciliationState Reconciliation,
    int DisplayOrder,
    int? SourceOrder,
    string? SourceMarker,
    ModMetadataAvailability MetadataAvailability,
    DateTimeOffset ObservedAtUtc,
    string SourceName,
    string Fingerprint,
    int WarningCount,
    ImmutableArray<string> Warnings,
    ModMetadataSummary? Metadata,
    ModUpdateEvidence UpdateEvidence);

public sealed record ModInventorySummary(
    ModInventoryObservationStatus Status,
    DateTimeOffset ObservedAtUtc,
    string Fingerprint,
    int WarningCount,
    int AuthoritativeEntryCount,
    int DerivedEntryCount);

public sealed record ModEntry(
    ModId Id,
    string Name,
    string Version,
    string Source,
    bool IsEnabled,
    int? Priority,
    HealthLevel Health,
    ModEntryKind Kind = ModEntryKind.Mod,
    string Category = "Uncategorized",
    string? AvailableVersion = null,
    ModUpdateState UpdateState = ModUpdateState.Unknown,
    ModConflictState ConflictState = ModConflictState.Unknown,
    ModInventoryObservation? Inventory = null);

public sealed record PluginEntry(
    PluginId Id,
    string Name,
    bool IsEnabled,
    int? LoadOrder,
    HealthLevel Health,
    int? SourcePriority = null,
    PluginObservation? Observation = null);

public sealed record EnvironmentEntry(
    EnvironmentEntryId Id,
    EnvironmentTabCapability Tab,
    string Name,
    string Detail,
    string Status,
    HealthLevel Health,
    ImmutableArray<ModId> RelatedModIds,
    GeneratedOutputId? ObservedOutputId = null);

public sealed record ProfileSourceObservation(
    string Name,
    ProfileSourceAvailability Availability,
    ProfileSourceParseStatus ParseStatus,
    int WarningCount);

public sealed record ProfileObservationSummary(
    ProfileObservationStatus Status,
    ManagerProfileState ManagerState,
    DateTimeOffset ObservedAtUtc,
    string SnapshotFingerprint,
    int WarningCount,
    bool? LocalSavesEnabled,
    bool? LocalSettingsEnabled,
    ImmutableArray<ProfileSourceObservation> Sources,
    ModInventorySummary? Inventory = null,
    ResolvedEnvironmentSummary? Environment = null,
    ToolOutputObservationSummary? ToolOutputs = null);

public sealed record Profile(
    ProfileId Id,
    InstallationId InstallationId,
    string Name,
    ImmutableArray<ModEntry> Mods,
    ImmutableArray<PluginEntry> Plugins,
    HealthSummary Health,
    ProfileFeature Features = ProfileFeature.None,
    ProfileLifecycleState Lifecycle = ProfileLifecycleState.Available,
    ImmutableArray<EnvironmentEntry> EnvironmentEntries = default,
    LaunchTargetId? DefaultLaunchTargetId = null,
    ProfileLaunchReadiness LaunchReadiness = default,
    ProfileObservationSummary? Observation = null,
    ImmutableArray<GeneratedOutputSummary> ObservedOutputs = default);

public readonly record struct ConfiguredPath(
    ConfiguredPathAnchor Anchor,
    string RelativePath);

public readonly record struct CommandArgument(string Value);

public sealed record ExecutableConfiguration(
    ConfiguredPath Executable,
    ConfiguredPath WorkingDirectory,
    EnvironmentPolicy EnvironmentPolicy);

public readonly record struct LaunchSafetyPolicy(
    bool RequiresProfileStateValidation,
    bool ObservesPendingDeployment,
    bool ObservesPendingOutputs,
    bool RequiresOnlineCleanVerification,
    bool RecommendsSnapshot,
    bool RequiresApproval);

public readonly record struct ProfileLaunchReadiness(
    ProfileValidationState ProfileState,
    PendingChangeState PendingDeployment,
    PendingChangeState PendingOutputs,
    OnlineCleanVerificationState OnlineCleanVerification);

public sealed record ToolDefinition(
    ToolId Id,
    string Name,
    string Description,
    ToolKind Kind,
    ImmutableArray<GameAdapterId> AdapterIds);

public sealed record LaunchTargetDefinition(
    LaunchTargetId Id,
    string Name,
    LaunchTargetKind Kind,
    ToolId? ToolId,
    GridInternalRoute? InternalRoute,
    ImmutableArray<GameAdapterId> AdapterIds,
    LaunchSafetyPolicy SafetyPolicy);

public sealed record GameToolCatalog(
    ImmutableArray<ToolDefinition> Tools,
    ImmutableArray<LaunchTargetDefinition> LaunchTargets);

public sealed record ToolConfiguration(
    ToolId ToolId,
    AvailabilityState Availability,
    string? UnavailableReason,
    ExecutableConfiguration? Command);

public sealed record LaunchTargetConfiguration(
    LaunchTargetId LaunchTargetId,
    AvailabilityState Availability,
    string? UnavailableReason,
    ExecutableConfiguration? GameCommand,
    ImmutableArray<CommandArgument> Arguments);

public sealed record ManagedInstallation(
    InstallationId Id,
    GameId GameId,
    GameAdapterId AdapterId,
    string Name,
    InstallationKind Kind,
    WorkspaceAccessMode AccessMode,
    WorkspaceCapabilities Capabilities,
    ImmutableArray<Profile> Profiles,
    ImmutableArray<ToolConfiguration> ToolConfigurations,
    ImmutableArray<LaunchTargetConfiguration> LaunchTargetConfigurations,
    HealthSummary Health,
    ProfileFeature SupportedProfileFeatures,
    InstallationMetadata Metadata,
    ImmutableArray<ObservedExecutableSummary> ObservedExecutables = default);

public sealed record ManagedGame(
    GameId Id,
    string Name,
    WorkspaceCapabilities Capabilities,
    ImmutableArray<GameAdapterIdentity> Adapters,
    ImmutableArray<ManagedInstallation> Installations,
    GameToolCatalog ToolCatalog,
    HealthSummary Health);

public readonly record struct WorkspaceSelection(
    GameId? GameId,
    InstallationId? InstallationId,
    ProfileId? ProfileId)
{
    public static WorkspaceSelection Empty { get; } = new(null, null, null);
}

public sealed class GridCatalogSnapshot
{
    private const WorkspaceFeature KnownWorkspaceFeatures =
        WorkspaceFeature.Profiles |
        WorkspaceFeature.ModList |
        WorkspaceFeature.Health |
        WorkspaceFeature.Tools |
        WorkspaceFeature.LaunchTargets;

    private const EnvironmentTabCapability KnownEnvironmentTabs =
        EnvironmentTabCapability.Plugins |
        EnvironmentTabCapability.Archives |
        EnvironmentTabCapability.Data |
        EnvironmentTabCapability.Saves |
        EnvironmentTabCapability.Downloads |
        EnvironmentTabCapability.Conflicts |
        EnvironmentTabCapability.Outputs |
        EnvironmentTabCapability.Activity;

    private const ProfileFeature KnownProfileFeatures =
        ProfileFeature.Saves |
        ProfileFeature.ConfigurationFiles;

    public GridCatalogSnapshot(
        string revision,
        CatalogSourceKind sourceKind,
        IEnumerable<ManagedGame> games)
    {
        Revision = RequireText(revision, nameof(revision));
        if (!Enum.IsDefined(sourceKind))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        SourceKind = sourceKind;
        Games = games?.ToImmutableArray() ?? throw new ArgumentNullException(nameof(games));
        Validate();
    }

    public string Revision { get; }

    public CatalogSourceKind SourceKind { get; }

    public ImmutableArray<ManagedGame> Games { get; }

    private void Validate()
    {
        var gameIds = new HashSet<GameId>();
        var installationIds = new HashSet<InstallationId>();
        var installationReferenceIds = new HashSet<InstallationReferenceId>();
        var profileIds = new HashSet<ProfileId>();

        foreach (var game in Games)
        {
            EnsureId(game.Id.Value, "Game ID");
            RequireText(game.Name, "Game name");
            EnsureUnique(gameIds, game.Id, "Game ID");
            ValidateCapabilities(game.Capabilities, $"Game '{game.Id}' capabilities");
            ValidateHealth(game.Health, $"Game '{game.Id}' health");

            if (game.Adapters.IsDefault)
            {
                throw new ArgumentException($"Game '{game.Id}' adapters must be initialized.");
            }

            if (game.Installations.IsDefault)
            {
                throw new ArgumentException($"Game '{game.Id}' installations must be initialized.");
            }

            if (game.Adapters.IsEmpty)
            {
                throw new ArgumentException($"Game '{game.Id}' must register at least one adapter identity.");
            }

            var adapterIds = new HashSet<GameAdapterId>();
            foreach (var adapter in game.Adapters)
            {
                EnsureId(adapter.Id.Value, "Adapter ID");
                RequireText(adapter.Name, "Adapter name");
                EnsureUnique(adapterIds, adapter.Id, $"Adapter ID for game '{game.Id}'");
            }

            var (toolDefinitions, targetDefinitions) = ValidateToolCatalog(game, adapterIds);

            foreach (var installation in game.Installations)
            {
                ValidateInstallation(
                    game,
                    adapterIds,
                    toolDefinitions,
                    targetDefinitions,
                    installation,
                    installationIds,
                    installationReferenceIds,
                    profileIds);
            }
        }
    }

    private static void ValidateInstallation(
        ManagedGame game,
        HashSet<GameAdapterId> adapterIds,
        IReadOnlyDictionary<ToolId, ToolDefinition> toolDefinitions,
        IReadOnlyDictionary<LaunchTargetId, LaunchTargetDefinition> targetDefinitions,
        ManagedInstallation installation,
        HashSet<InstallationId> installationIds,
        HashSet<InstallationReferenceId> installationReferenceIds,
        HashSet<ProfileId> profileIds)
    {
        EnsureId(installation.Id.Value, "Installation ID");
        RequireText(installation.Name, "Installation name");
        EnsureUnique(installationIds, installation.Id, "Installation ID");

        if (installation.GameId != game.Id)
        {
            throw new ArgumentException($"Installation '{installation.Id}' does not belong to game '{game.Id}'.");
        }

        if (!adapterIds.Contains(installation.AdapterId))
        {
            throw new ArgumentException($"Installation '{installation.Id}' references an adapter not registered by game '{game.Id}'.");
        }

        if (!installation.Capabilities.IsSubsetOf(game.Capabilities))
        {
            throw new ArgumentException($"Installation '{installation.Id}' capabilities exceed game '{game.Id}' capabilities.");
        }

        ValidateCapabilities(installation.Capabilities, $"Installation '{installation.Id}' capabilities");

        if (!Enum.IsDefined(installation.Kind) || !Enum.IsDefined(installation.AccessMode))
        {
            throw new ArgumentException($"Installation '{installation.Id}' has an invalid state.");
        }

        ValidateHealth(installation.Health, $"Installation '{installation.Id}' health");
        ValidateProfileFeatures(installation.SupportedProfileFeatures, $"Installation '{installation.Id}' profile features");
        ValidateInstallationMetadata(
            installation.Metadata,
            installation.Id,
            installation.Kind,
            installation.AccessMode,
            installationReferenceIds);
        EnsureInitialized(installation.Profiles, $"Installation '{installation.Id}' profiles");
        EnsureInitialized(installation.ToolConfigurations, $"Installation '{installation.Id}' tool configurations");
        EnsureInitialized(installation.LaunchTargetConfigurations, $"Installation '{installation.Id}' launch-target configurations");

        if (installation.Profiles.Length > 0 && !installation.Capabilities.Supports(WorkspaceFeature.Profiles))
        {
            throw new ArgumentException($"Installation '{installation.Id}' has profiles without the Profiles capability.");
        }

        var configuredToolIds = new HashSet<ToolId>();
        foreach (var configuration in installation.ToolConfigurations)
        {
            EnsureUnique(configuredToolIds, configuration.ToolId, $"Tool configuration for installation '{installation.Id}'");
            if (!toolDefinitions.TryGetValue(configuration.ToolId, out var definition) ||
                !definition.AdapterIds.Contains(installation.AdapterId))
            {
                throw new ArgumentException($"Tool configuration '{configuration.ToolId}' is not declared for installation '{installation.Id}' adapter.");
            }

            ValidateAvailability(configuration.Availability, configuration.UnavailableReason, $"Tool configuration '{configuration.ToolId}'");
            if (configuration.Command is not null)
            {
                ValidateExecutableConfiguration(configuration.Command, $"Tool configuration '{configuration.ToolId}'");
            }

            if (configuration.Availability != AvailabilityState.Unavailable && configuration.Command is null)
            {
                throw new ArgumentException($"Tool configuration '{configuration.ToolId}' requires a command when available for preview.");
            }
        }

        if (installation.ToolConfigurations.Length > 0 && !installation.Capabilities.Supports(WorkspaceFeature.Tools))
        {
            throw new ArgumentException($"Installation '{installation.Id}' has tools without the Tools capability.");
        }

        var configuredTargetIds = new HashSet<LaunchTargetId>();
        foreach (var configuration in installation.LaunchTargetConfigurations)
        {
            EnsureUnique(configuredTargetIds, configuration.LaunchTargetId, $"Launch-target configuration for installation '{installation.Id}'");
            if (!targetDefinitions.TryGetValue(configuration.LaunchTargetId, out var definition) ||
                !definition.AdapterIds.Contains(installation.AdapterId))
            {
                throw new ArgumentException($"Launch-target configuration '{configuration.LaunchTargetId}' is not declared for installation '{installation.Id}' adapter.");
            }

            ValidateAvailability(configuration.Availability, configuration.UnavailableReason, $"Launch-target configuration '{configuration.LaunchTargetId}'");
            ValidateArguments(configuration.Arguments, $"Launch-target configuration '{configuration.LaunchTargetId}' arguments");

            if (definition.Kind == LaunchTargetKind.Tool &&
                (definition.ToolId is null || !configuredToolIds.Contains(definition.ToolId.Value)))
            {
                throw new ArgumentException($"Launch target '{definition.Id}' does not reference a configured tool in installation '{installation.Id}'.");
            }

            if (definition.Kind == LaunchTargetKind.Game)
            {
                if (configuration.GameCommand is not null)
                {
                    ValidateExecutableConfiguration(configuration.GameCommand, $"Game launch target '{definition.Id}'");
                }

                if (configuration.Availability != AvailabilityState.Unavailable && configuration.GameCommand is null)
                {
                    throw new ArgumentException($"Game launch target '{definition.Id}' requires a command when available for preview.");
                }
            }
            else if (configuration.GameCommand is not null)
            {
                throw new ArgumentException($"Non-game launch target '{definition.Id}' cannot contain a game command.");
            }

            if (definition.Kind == LaunchTargetKind.GridInternal && !configuration.Arguments.IsEmpty)
            {
                throw new ArgumentException($"Grid-internal launch target '{definition.Id}' cannot contain external command arguments.");
            }
        }

        if (installation.LaunchTargetConfigurations.Length > 0 && !installation.Capabilities.Supports(WorkspaceFeature.LaunchTargets))
        {
            throw new ArgumentException($"Installation '{installation.Id}' has launch targets without the LaunchTargets capability.");
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in installation.Profiles)
        {
            if (!profileNames.Add(profile.Name))
            {
                throw new ArgumentException($"Profile name '{profile.Name}' is duplicated in installation '{installation.Id}'.");
            }

            ValidateProfile(installation, targetDefinitions, profile, profileIds);
        }

        if (installation.Profiles.Count(profile =>
            profile.Observation?.ManagerState == ManagerProfileState.Active) > 1)
        {
            throw new ArgumentException($"Installation '{installation.Id}' has more than one manager-active profile observation.");
        }
    }

    private static void ValidateProfile(
        ManagedInstallation installation,
        IReadOnlyDictionary<LaunchTargetId, LaunchTargetDefinition> targetDefinitions,
        Profile profile,
        HashSet<ProfileId> profileIds)
    {
        EnsureId(profile.Id.Value, "Profile ID");
        RequireText(profile.Name, "Profile name");
        EnsureUnique(profileIds, profile.Id, "Profile ID");

        if (!string.Equals(profile.Name, profile.Name.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Profile '{profile.Id}' name must not contain leading or trailing whitespace.");
        }

        if (profile.InstallationId != installation.Id)
        {
            throw new ArgumentException($"Profile '{profile.Id}' does not belong to installation '{installation.Id}'.");
        }

        EnsureInitialized(profile.Mods, $"Profile '{profile.Id}' mods");
        EnsureInitialized(profile.Plugins, $"Profile '{profile.Id}' plugins");
        EnsureInitialized(profile.EnvironmentEntries, $"Profile '{profile.Id}' environment entries");
        ValidateHealth(profile.Health, $"Profile '{profile.Id}' health");
        ValidateProfileFeatures(profile.Features, $"Profile '{profile.Id}' features");

        if (installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference)
        {
            if (profile.Observation is null)
            {
                throw new ArgumentException($"Connected profile '{profile.Id}' requires an observation summary.");
            }

            ValidateProfileObservation(profile.Observation, profile.Id);
        }
        else if (profile.Observation is not null)
        {
            throw new ArgumentException($"Mock profile '{profile.Id}' cannot contain a connected observation summary.");
        }

        if (!Enum.IsDefined(profile.Lifecycle))
        {
            throw new ArgumentException($"Profile '{profile.Id}' lifecycle is invalid.");
        }

        if (!Enum.IsDefined(profile.LaunchReadiness.ProfileState) ||
            !Enum.IsDefined(profile.LaunchReadiness.PendingDeployment) ||
            !Enum.IsDefined(profile.LaunchReadiness.PendingOutputs) ||
            !Enum.IsDefined(profile.LaunchReadiness.OnlineCleanVerification))
        {
            throw new ArgumentException($"Profile '{profile.Id}' launch readiness is invalid.");
        }

        if (profile.DefaultLaunchTargetId is LaunchTargetId defaultTargetId &&
            (!targetDefinitions.TryGetValue(defaultTargetId, out var defaultTarget) ||
             !defaultTarget.AdapterIds.Contains(installation.AdapterId)))
        {
            throw new ArgumentException($"Profile '{profile.Id}' default launch target is not compatible with installation '{installation.Id}'.");
        }

        if ((profile.Features & ~installation.SupportedProfileFeatures) != ProfileFeature.None)
        {
            throw new ArgumentException($"Profile '{profile.Id}' features exceed installation '{installation.Id}' support.");
        }

        if (profile.Features.HasFlag(ProfileFeature.Saves) &&
            !installation.Capabilities.Supports(EnvironmentTabCapability.Saves))
        {
            throw new ArgumentException($"Profile '{profile.Id}' cannot expose saves without the installation Saves capability.");
        }

        if (profile.Mods.Length > 0 && !installation.Capabilities.Supports(WorkspaceFeature.ModList))
        {
            throw new ArgumentException($"Profile '{profile.Id}' has mods without the ModList capability.");
        }

        if (profile.Plugins.Length > 0 && !installation.Capabilities.Supports(EnvironmentTabCapability.Plugins))
        {
            throw new ArgumentException($"Profile '{profile.Id}' has plugins without the Plugins tab capability.");
        }

        var modIds = new HashSet<ModId>();
        var modPriorities = new HashSet<int>();
        var modDisplayOrders = new HashSet<int>();
        foreach (var mod in profile.Mods)
        {
            EnsureId(mod.Id.Value, "Mod ID");
            RequireText(mod.Name, "Mod name");
            EnsureUnique(modIds, mod.Id, $"Mod ID for profile '{profile.Id}'");
            ValidateHealthLevel(mod.Health, $"Mod '{mod.Id}' health");
            if (mod.Priority is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), $"Mod '{mod.Id}' priority cannot be negative.");
            }

            if (mod.Priority is int priority && !modPriorities.Add(priority))
            {
                throw new ArgumentException($"Mod priority '{priority}' is duplicated in profile '{profile.Id}'.");
            }

            if (!Enum.IsDefined(mod.Kind) || !Enum.IsDefined(mod.UpdateState) || !Enum.IsDefined(mod.ConflictState))
            {
                throw new ArgumentException($"Mod '{mod.Id}' contains an invalid represented state.");
            }

            if (mod.Kind == ModEntryKind.Separator)
            {
                if (mod.IsEnabled ||
                    !string.IsNullOrEmpty(mod.Version) ||
                    !string.IsNullOrEmpty(mod.Source) ||
                    !string.IsNullOrEmpty(mod.Category) ||
                    mod.AvailableVersion is not null)
                {
                    throw new ArgumentException($"Separator '{mod.Id}' cannot contain mod package state.");
                }
            }
            else
            {
                if (mod.Inventory is null)
                {
                    RequireText(mod.Version, "Mod version");
                    RequireText(mod.Source, "Mod source");
                    RequireText(mod.Category, "Mod category");
                }

                if (mod.UpdateState == ModUpdateState.UpdateAvailable &&
                    string.IsNullOrWhiteSpace(mod.AvailableVersion))
                {
                    throw new ArgumentException($"Mod '{mod.Id}' requires an available version when an update is represented.");
                }
            }

            if (mod.Inventory is ModInventoryObservation inventory)
            {
                ValidateModInventory(mod, inventory, profile.Id, modDisplayOrders);
            }

            if (mod.Kind == ModEntryKind.UnlistedDirectory && mod.Priority is not null)
            {
                throw new ArgumentException($"Grid-derived unlisted directory '{mod.Id}' cannot claim a manager priority.");
            }
        }

        var pluginIds = new HashSet<PluginId>();
        foreach (var plugin in profile.Plugins)
        {
            EnsureId(plugin.Id.Value, "Plugin ID");
            RequireText(plugin.Name, "Plugin name");
            EnsureUnique(pluginIds, plugin.Id, $"Plugin ID for profile '{profile.Id}'");
            ValidateHealthLevel(plugin.Health, $"Plugin '{plugin.Id}' health");
            if (plugin.LoadOrder is < 0 || plugin.SourcePriority is < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), $"Plugin '{plugin.Id}' represented order cannot be negative.");
            }

            if (plugin.Observation is PluginObservation pluginObservation)
            {
                ValidatePluginObservation(plugin, pluginObservation);
            }
        }

        var environmentEntryIds = new HashSet<EnvironmentEntryId>();
        foreach (var entry in profile.EnvironmentEntries)
        {
            EnsureId(entry.Id.Value, "Environment entry ID");
            EnsureUnique(environmentEntryIds, entry.Id, $"Environment entry ID for profile '{profile.Id}'");
            RequireText(entry.Name, "Environment entry name");
            RequireText(entry.Detail, "Environment entry detail");
            RequireText(entry.Status, "Environment entry status");
            ValidateHealthLevel(entry.Health, $"Environment entry '{entry.Id}' health");
            EnsureInitialized(entry.RelatedModIds, $"Environment entry '{entry.Id}' related mod IDs");

            var rawTab = (int)entry.Tab;
            if (rawTab == 0 || (rawTab & (rawTab - 1)) != 0 || entry.Tab == EnvironmentTabCapability.Plugins)
            {
                throw new ArgumentException($"Environment entry '{entry.Id}' must reference one non-Plugins tab.");
            }

            if (!installation.Capabilities.Supports(entry.Tab))
            {
                throw new ArgumentException($"Environment entry '{entry.Id}' uses an unsupported installation tab.");
            }

            if (entry.Tab == EnvironmentTabCapability.Saves &&
                !profile.Features.HasFlag(ProfileFeature.Saves))
            {
                throw new ArgumentException($"Environment entry '{entry.Id}' exposes saves for a profile without Saves support.");
            }

            if (entry.RelatedModIds.Any(relatedModId => !modIds.Contains(relatedModId)))
            {
                throw new ArgumentException($"Environment entry '{entry.Id}' references a mod outside profile '{profile.Id}'.");
            }
        }
    }

    private static (
        IReadOnlyDictionary<ToolId, ToolDefinition> Tools,
        IReadOnlyDictionary<LaunchTargetId, LaunchTargetDefinition> Targets) ValidateToolCatalog(
            ManagedGame game,
            HashSet<GameAdapterId> registeredAdapterIds)
    {
        ArgumentNullException.ThrowIfNull(game.ToolCatalog);
        EnsureInitialized(game.ToolCatalog.Tools, $"Game '{game.Id}' tool definitions");
        EnsureInitialized(game.ToolCatalog.LaunchTargets, $"Game '{game.Id}' launch-target definitions");

        if (!game.ToolCatalog.Tools.IsEmpty && !game.Capabilities.Supports(WorkspaceFeature.Tools))
        {
            throw new ArgumentException($"Game '{game.Id}' declares tools without the Tools capability.");
        }

        if (!game.ToolCatalog.LaunchTargets.IsEmpty && !game.Capabilities.Supports(WorkspaceFeature.LaunchTargets))
        {
            throw new ArgumentException($"Game '{game.Id}' declares launch targets without the LaunchTargets capability.");
        }

        var tools = new Dictionary<ToolId, ToolDefinition>();
        foreach (var tool in game.ToolCatalog.Tools)
        {
            EnsureId(tool.Id.Value, "Tool definition ID");
            RequireText(tool.Name, "Tool definition name");
            RequireText(tool.Description, "Tool definition description");
            if (!Enum.IsDefined(tool.Kind))
            {
                throw new ArgumentException($"Tool definition '{tool.Id}' has an invalid kind.");
            }

            EnsureInitialized(tool.AdapterIds, $"Tool definition '{tool.Id}' adapters");
            if (tool.AdapterIds.IsEmpty ||
                tool.AdapterIds.Distinct().Count() != tool.AdapterIds.Length ||
                tool.AdapterIds.Any(adapterId => !registeredAdapterIds.Contains(adapterId)))
            {
                throw new ArgumentException($"Tool definition '{tool.Id}' must reference unique adapters registered by game '{game.Id}'.");
            }

            if (!tools.TryAdd(tool.Id, tool))
            {
                throw new ArgumentException($"Tool definition ID '{tool.Id}' is duplicated for game '{game.Id}'.");
            }
        }

        var targets = new Dictionary<LaunchTargetId, LaunchTargetDefinition>();
        foreach (var target in game.ToolCatalog.LaunchTargets)
        {
            EnsureId(target.Id.Value, "Launch target definition ID");
            RequireText(target.Name, "Launch target definition name");
            if (!Enum.IsDefined(target.Kind))
            {
                throw new ArgumentException($"Launch target definition '{target.Id}' has an invalid kind.");
            }

            EnsureInitialized(target.AdapterIds, $"Launch target definition '{target.Id}' adapters");
            if (target.AdapterIds.IsEmpty ||
                target.AdapterIds.Distinct().Count() != target.AdapterIds.Length ||
                target.AdapterIds.Any(adapterId => !registeredAdapterIds.Contains(adapterId)))
            {
                throw new ArgumentException($"Launch target definition '{target.Id}' must reference unique adapters registered by game '{game.Id}'.");
            }

            if (target.Kind == LaunchTargetKind.Tool)
            {
                if (target.ToolId is not ToolId toolId ||
                    !tools.TryGetValue(toolId, out var tool) ||
                    target.InternalRoute is not null ||
                    target.AdapterIds.Any(adapterId => !tool.AdapterIds.Contains(adapterId)))
                {
                    throw new ArgumentException($"Tool launch target '{target.Id}' must reference a compatible tool definition.");
                }
            }
            else if (target.Kind == LaunchTargetKind.Game &&
                (target.ToolId is not null || target.InternalRoute is not null))
            {
                throw new ArgumentException($"Game launch target '{target.Id}' cannot reference a tool or Grid route.");
            }
            else if (target.Kind == LaunchTargetKind.GridInternal &&
                (target.ToolId is not null ||
                 target.InternalRoute is not GridInternalRoute internalRoute ||
                 !Enum.IsDefined(internalRoute)))
            {
                throw new ArgumentException($"Grid-internal launch target '{target.Id}' requires one valid internal route and no tool.");
            }

            if (!targets.TryAdd(target.Id, target))
            {
                throw new ArgumentException($"Launch target definition ID '{target.Id}' is duplicated for game '{game.Id}'.");
            }
        }

        return (tools, targets);
    }

    private static void ValidateExecutableConfiguration(
        ExecutableConfiguration configuration,
        string description)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguredPath(configuration.Executable, $"{description} executable");
        ValidateConfiguredPath(configuration.WorkingDirectory, $"{description} working directory");
        if (!Enum.IsDefined(configuration.EnvironmentPolicy))
        {
            throw new ArgumentException($"{description} environment policy is invalid.");
        }
    }

    private static void ValidateConfiguredPath(ConfiguredPath path, string description)
    {
        if (!Enum.IsDefined(path.Anchor))
        {
            throw new ArgumentException($"{description} anchor is invalid.");
        }

        var value = RequireText(path.RelativePath, description);
        if (value.Length > 1024 ||
            value.Any(char.IsControl) ||
            value.StartsWith('/') ||
            value.StartsWith('\\') ||
            value.Contains(':'))
        {
            throw new ArgumentException($"{description} must be a safe relative represented path of at most 1,024 characters.");
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new ArgumentException($"{description} contains an empty or traversal segment.");
        }
    }

    private static void ValidateArguments(ImmutableArray<CommandArgument> arguments, string description)
    {
        EnsureInitialized(arguments, description);
        if (arguments.Length > 64)
        {
            throw new ArgumentException($"{description} cannot contain more than 64 literal arguments.");
        }

        foreach (var argument in arguments)
        {
            var value = RequireText(argument.Value, description);
            if (value.Length > 512 || value.Any(char.IsControl))
            {
                throw new ArgumentException($"{description} values must be at most 512 characters and contain no control characters.");
            }
        }
    }

    private static void ValidateHealth(HealthSummary health, string description)
    {
        ArgumentNullException.ThrowIfNull(health);
        RequireText(health.Label, $"{description} label");
        EnsureInitialized(health.Advisories, $"{description} advisories");
        ValidateHealthLevel(health.Level, $"{description} level");

        foreach (var advisory in health.Advisories)
        {
            RequireText(advisory.Code, "Advisory code");
            RequireText(advisory.Title, "Advisory title");
            RequireText(advisory.Detail, "Advisory detail");
            ValidateHealthLevel(advisory.Level, $"Advisory '{advisory.Code}' level");
        }
    }

    private static void ValidateCapabilities(WorkspaceCapabilities capabilities, string description)
    {
        if ((capabilities.Features & ~KnownWorkspaceFeatures) != WorkspaceFeature.None ||
            (capabilities.EnvironmentTabs & ~KnownEnvironmentTabs) != EnvironmentTabCapability.None)
        {
            throw new ArgumentException($"{description} contain unknown flags.");
        }
    }

    private static void ValidateProfileFeatures(ProfileFeature features, string description)
    {
        if ((features & ~KnownProfileFeatures) != ProfileFeature.None)
        {
            throw new ArgumentException($"{description} contain unknown flags.");
        }
    }

    private static void ValidateProfileObservation(
        ProfileObservationSummary observation,
        ProfileId profileId)
    {
        if (!Enum.IsDefined(observation.Status) || !Enum.IsDefined(observation.ManagerState))
        {
            throw new ArgumentException($"Profile '{profileId}' observation state is invalid.");
        }

        if (observation.ObservedAtUtc == default || observation.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Profile '{profileId}' observation time must be a non-default UTC value.");
        }

        RequireText(observation.SnapshotFingerprint, $"Profile '{profileId}' observation fingerprint");
        if (observation.WarningCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), $"Profile '{profileId}' observation warning count cannot be negative.");
        }

        EnsureInitialized(observation.Sources, $"Profile '{profileId}' observation sources");
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in observation.Sources)
        {
            RequireText(source.Name, $"Profile '{profileId}' observation source name");
            if (!sourceNames.Add(source.Name))
            {
                throw new ArgumentException($"Profile '{profileId}' observation source name '{source.Name}' is duplicated.");
            }

            if (!Enum.IsDefined(source.Availability) || !Enum.IsDefined(source.ParseStatus))
            {
                throw new ArgumentException($"Profile '{profileId}' observation source '{source.Name}' state is invalid.");
            }

            if (source.WarningCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(observation), $"Profile '{profileId}' observation source '{source.Name}' warning count cannot be negative.");
            }
        }

        if (observation.Inventory is ModInventorySummary inventory)
        {
            if (!Enum.IsDefined(inventory.Status))
            {
                throw new ArgumentException($"Profile '{profileId}' inventory observation state is invalid.");
            }

            ValidateUtc(inventory.ObservedAtUtc, $"Profile '{profileId}' inventory observation time");
            RequireText(inventory.Fingerprint, $"Profile '{profileId}' inventory fingerprint");
            if (inventory.WarningCount < 0 ||
                inventory.AuthoritativeEntryCount < 0 ||
                inventory.DerivedEntryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(observation), $"Profile '{profileId}' inventory counts cannot be negative.");
            }
        }

        if (observation.Environment is ResolvedEnvironmentSummary environment)
        {
            ValidateResolvedEnvironmentSummary(environment, profileId);
        }

        if (observation.ToolOutputs is ToolOutputObservationSummary toolOutputs)
        {
            ValidateToolOutputObservationSummary(toolOutputs, $"Profile '{profileId}' tool/output");
        }
    }

    private static void ValidatePluginObservation(PluginEntry plugin, PluginObservation observation)
    {
        if (!Enum.IsDefined(observation.Extension) ||
            !Enum.IsDefined(observation.ActivationProvenance) ||
            !Enum.IsDefined(observation.OrderProvenance) ||
            !Enum.IsDefined(observation.FileAvailability))
        {
            throw new ArgumentException($"Plugin '{plugin.Id}' observation contains an invalid state.");
        }

        if (observation.SourcePriority is < 0 || observation.SourcePriority != plugin.SourcePriority)
        {
            throw new ArgumentException($"Plugin '{plugin.Id}' observation source priority must match its represented source priority.");
        }

        if (observation.ActivationProvenance == PluginActivationProvenance.PluginsFileMarker &&
            observation.FileAvailability == PluginFileAvailability.Unknown)
        {
            throw new ArgumentException($"Plugin '{plugin.Id}' cannot claim profile activation evidence with unknown file availability.");
        }

        RequireText(observation.Fingerprint, $"Plugin '{plugin.Id}' observation fingerprint");
        ValidateUtc(observation.ObservedAtUtc, $"Plugin '{plugin.Id}' observation time");
        EnsureInitialized(observation.Masters, $"Plugin '{plugin.Id}' master references");
        EnsureInitialized(observation.Warnings, $"Plugin '{plugin.Id}' warnings");
        var masterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceOrders = new HashSet<int>();
        foreach (var master in observation.Masters)
        {
            RequireText(master.Name, $"Plugin '{plugin.Id}' master name");
            if (master.SourceOrder < 0 || !sourceOrders.Add(master.SourceOrder))
            {
                throw new ArgumentException($"Plugin '{plugin.Id}' master source order must be unique and non-negative.");
            }

            if (!masterNames.Add(master.Name) || !Enum.IsDefined(master.Status))
            {
                throw new ArgumentException($"Plugin '{plugin.Id}' contains duplicate or invalid master evidence.");
            }
        }

        foreach (var warning in observation.Warnings)
        {
            RequireText(warning, $"Plugin '{plugin.Id}' warning");
        }
    }

    private static void ValidateResolvedEnvironmentSummary(
        ResolvedEnvironmentSummary summary,
        ProfileId profileId)
    {
        if (!Enum.IsDefined(summary.Status))
        {
            throw new ArgumentException($"Profile '{profileId}' resolved-environment status is invalid.");
        }

        ValidateUtc(summary.ObservedAtUtc, $"Profile '{profileId}' resolved-environment observation time");
        RequireText(summary.Fingerprint, $"Profile '{profileId}' resolved-environment fingerprint");
        if (summary.PluginCount < 0 ||
            summary.ArchiveCount < 0 ||
            summary.VirtualPathCount < 0 ||
            summary.ProviderCount < 0 ||
            summary.DiscrepancyCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(summary), $"Profile '{profileId}' resolved-environment counts cannot be negative.");
        }
    }

    private static void ValidateModInventory(
        ModEntry mod,
        ModInventoryObservation inventory,
        ProfileId profileId,
        HashSet<int> displayOrders)
    {
        if (!Enum.IsDefined(inventory.Authority) ||
            !Enum.IsDefined(inventory.Reconciliation) ||
            !Enum.IsDefined(inventory.MetadataAvailability))
        {
            throw new ArgumentException($"Mod '{mod.Id}' inventory state is invalid.");
        }

        if (inventory.DisplayOrder < 0 || !displayOrders.Add(inventory.DisplayOrder))
        {
            throw new ArgumentException($"Mod '{mod.Id}' inventory display order must be unique and non-negative in profile '{profileId}'.");
        }

        if (inventory.SourceOrder is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inventory), $"Mod '{mod.Id}' source order cannot be negative.");
        }

        ValidateUtc(inventory.ObservedAtUtc, $"Mod '{mod.Id}' inventory observation time");
        RequireText(inventory.SourceName, $"Mod '{mod.Id}' inventory source name");
        if (inventory.SourceName.StartsWith('/') ||
            inventory.SourceName.StartsWith('\\') ||
            inventory.SourceName.Contains(':'))
        {
            throw new ArgumentException($"Mod '{mod.Id}' inventory source name must not expose an absolute path.");
        }

        RequireText(inventory.Fingerprint, $"Mod '{mod.Id}' inventory fingerprint");
        EnsureInitialized(inventory.Warnings, $"Mod '{mod.Id}' inventory warnings");
        if (inventory.WarningCount < 0 || inventory.WarningCount != inventory.Warnings.Length)
        {
            throw new ArgumentException($"Mod '{mod.Id}' inventory warning count must match its warnings.");
        }

        foreach (var warning in inventory.Warnings)
        {
            RequireText(warning, $"Mod '{mod.Id}' inventory warning");
        }

        if (inventory.UpdateEvidence.State != mod.UpdateState || inventory.UpdateEvidence.NetworkChecked)
        {
            throw new ArgumentException($"Mod '{mod.Id}' local update evidence must match its represented state and cannot claim a network check.");
        }

        RequireText(inventory.UpdateEvidence.Detail, $"Mod '{mod.Id}' update-evidence detail");
        if (inventory.UpdateEvidence.ProviderTimestampUtc is DateTimeOffset providerTime)
        {
            ValidateUtc(providerTime, $"Mod '{mod.Id}' update-evidence provider time");
        }

        if (inventory.Authority == ModInventoryAuthority.GridDerived &&
            inventory.Reconciliation is not (ModReconciliationState.Unlisted or ModReconciliationState.Backup))
        {
            throw new ArgumentException($"Grid-derived mod '{mod.Id}' must be represented as unlisted or backup evidence.");
        }

        if (mod.Kind == ModEntryKind.UnlistedDirectory &&
            (inventory.Authority != ModInventoryAuthority.GridDerived ||
             inventory.Reconciliation != ModReconciliationState.Unlisted))
        {
            throw new ArgumentException($"Unlisted directory '{mod.Id}' requires Grid-derived unlisted inventory evidence.");
        }

        if (inventory.MetadataAvailability == ModMetadataAvailability.Available && inventory.Metadata is null)
        {
            throw new ArgumentException($"Mod '{mod.Id}' reports available metadata without a metadata summary.");
        }

        if (inventory.Reconciliation is ModReconciliationState.Matched or
            ModReconciliationState.Foreign && mod.Priority is null)
        {
            throw new ArgumentException($"Authoritative reconciled mod '{mod.Id}' requires an effective manager priority.");
        }

        if (inventory.Reconciliation is not (ModReconciliationState.Unlisted or ModReconciliationState.Backup) &&
            inventory.SourceOrder is null)
        {
            throw new ArgumentException($"Authoritative inventory row '{mod.Id}' requires its original source order.");
        }

        if (mod.Kind == ModEntryKind.Foreign && inventory.Reconciliation != ModReconciliationState.Foreign ||
            mod.Kind == ModEntryKind.Backup && inventory.Reconciliation != ModReconciliationState.Backup)
        {
            throw new ArgumentException($"Mod '{mod.Id}' kind does not match its reconciliation evidence.");
        }

        if (inventory.Metadata is ModMetadataSummary metadata)
        {
            EnsureInitialized(metadata.CategoryIds, $"Mod '{mod.Id}' metadata category IDs");
            EnsureInitialized(metadata.CategoryNames, $"Mod '{mod.Id}' metadata category names");
            EnsureInitialized(metadata.RawValues, $"Mod '{mod.Id}' raw metadata values");
            foreach (var raw in metadata.RawValues)
            {
                RequireText(raw.Key, $"Mod '{mod.Id}' raw metadata key");
                ArgumentNullException.ThrowIfNull(raw.RawValue);
            }

            if (metadata.InstallationTimeUtc is DateTimeOffset installationTime)
            {
                ValidateUtc(installationTime, $"Mod '{mod.Id}' derived installation time");
            }

            if (metadata.ProviderTimestampUtc is DateTimeOffset metadataProviderTime)
            {
                ValidateUtc(metadataProviderTime, $"Mod '{mod.Id}' metadata provider time");
            }
        }
    }

    private static void ValidateUtc(DateTimeOffset value, string description)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{description} must be a non-default UTC value.");
        }
    }

    private static void ValidateInstallationMetadata(
        InstallationMetadata metadata,
        InstallationId installationId,
        InstallationKind installationKind,
        WorkspaceAccessMode accessMode,
        HashSet<InstallationReferenceId> installationReferenceIds)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (!Enum.IsDefined(metadata.Availability) || !Enum.IsDefined(metadata.Provenance))
        {
            throw new ArgumentException($"Installation '{installationId}' metadata state is invalid.");
        }

        if (metadata.LocationDisplay is not null && string.IsNullOrWhiteSpace(metadata.LocationDisplay))
        {
            throw new ArgumentException($"Installation '{installationId}' display location must be null or non-empty.");
        }

        RequireText(metadata.StatusDetail, $"Installation '{installationId}' status detail");

        if (metadata.Provenance == InstallationProvenanceKind.Mock && metadata.ReferenceId is not null)
        {
            throw new ArgumentException($"Mock installation '{installationId}' cannot reference a persisted external installation.");
        }

        if (metadata.Provenance == InstallationProvenanceKind.Mock && metadata.ToolOutputObservation is not null)
        {
            throw new ArgumentException($"Mock installation '{installationId}' cannot contain an external tool/output observation.");
        }

        if (metadata.Provenance == InstallationProvenanceKind.ConnectedReference)
        {
            if (metadata.ReferenceId is not InstallationReferenceId referenceId)
            {
                throw new ArgumentException($"Connected installation '{installationId}' requires a persisted reference identity.");
            }

            EnsureId(referenceId.Value, "Installation reference ID");
            EnsureUnique(installationReferenceIds, referenceId, "Installation reference ID");
            if (installationKind != InstallationKind.External || accessMode != WorkspaceAccessMode.ReadOnly)
            {
                throw new ArgumentException($"Connected installation '{installationId}' must remain external and read-only.");
            }


            if (metadata.ToolOutputObservation is ToolOutputObservationSummary toolOutputs)
            {
                ValidateToolOutputObservationSummary(toolOutputs, $"Installation '{installationId}' tool/output");
            }
        }
    }

    private static void ValidateToolOutputObservationSummary(
        ToolOutputObservationSummary summary,
        string description)
    {
        if (!Enum.IsDefined(summary.Status))
        {
            throw new ArgumentException($"{description} observation state is invalid.");
        }

        ValidateUtc(summary.ObservedAtUtc, $"{description} observation time");
        RequireText(summary.Fingerprint, $"{description} observation fingerprint");
        if (summary.ExecutableCount < 0 || summary.OutputCount < 0 || summary.WarningCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(summary), $"{description} counts cannot be negative.");
        }
    }

    private static void ValidateHealthLevel(HealthLevel level, string description)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentException($"{description} is invalid.");
        }
    }

    private static void ValidateAvailability(AvailabilityState state, string? reason, string description)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (state == AvailabilityState.Unavailable && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException($"{description} requires a reason when unavailable.");
        }
    }

    private static void EnsureId(string? value, string description) => DomainIdentifier.Ensure(value, description);

    private static string RequireText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} must be non-empty.");
        }

        return value;
    }

    private static void EnsureInitialized<T>(ImmutableArray<T> items, string description)
    {
        if (items.IsDefault)
        {
            throw new ArgumentException($"{description} must be initialized.");
        }
    }

    private static void EnsureUnique<T>(HashSet<T> values, T value, string description)
        where T : notnull
    {
        if (!values.Add(value))
        {
            throw new ArgumentException($"{description} '{value}' is duplicated.");
        }
    }
}
