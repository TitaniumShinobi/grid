using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.App.Services;

/// <summary>Manager-neutral evidence rendered by the Add Profile review surface.</summary>
public sealed class ResolvedProfileEnvironment
{
    internal ResolvedProfileEnvironment(
        GameId gameId,
        string gameName,
        GameAdapterId adapterId,
        string managerName,
        ProfileId profileId,
        string profileName,
        string topology,
        string applicationDirectory,
        string instanceDirectory,
        string gameDirectory,
        string modsSummary,
        string pluginsSummary,
        string configurationSummary,
        string observationSummary,
        int enabledModCount,
        int activePluginCount,
        ImmutableArray<string> warnings,
        string evidenceSummary,
        Mo2ValidationRequest validationRequest)
    {
        GameId = gameId;
        GameName = gameName;
        AdapterId = adapterId;
        ManagerName = managerName;
        ProfileId = profileId;
        ProfileName = profileName;
        Topology = topology;
        ApplicationDirectory = applicationDirectory;
        InstanceDirectory = instanceDirectory;
        GameDirectory = gameDirectory;
        ModsSummary = modsSummary;
        PluginsSummary = pluginsSummary;
        ConfigurationSummary = configurationSummary;
        ObservationSummary = observationSummary;
        EnabledModCount = enabledModCount;
        ActivePluginCount = activePluginCount;
        Warnings = warnings;
        EvidenceSummary = evidenceSummary;
        ValidationRequest = validationRequest;
    }

    public GameId GameId { get; }
    public string GameName { get; }
    public GameAdapterId AdapterId { get; }
    public string ManagerName { get; }
    public ProfileId ProfileId { get; }
    public string ProfileName { get; }
    public string Topology { get; }
    public string ApplicationDirectory { get; }
    public string InstanceDirectory { get; }
    public string GameDirectory { get; }
    public string ModsSummary { get; }
    public string PluginsSummary { get; }
    public string ConfigurationSummary { get; }
    public string ObservationSummary { get; }
    public int EnabledModCount { get; }
    public int ActivePluginCount { get; }
    public ImmutableArray<string> Warnings { get; }
    public string EvidenceSummary { get; }

    internal Mo2ValidationRequest ValidationRequest { get; }
}

public sealed record ResolvedProfileEnvironmentResolution(
    ImmutableArray<ResolvedProfileEnvironment> Profiles)
{
    public bool RequiresSelection => Profiles.Length > 1;
}

/// <summary>Pure selection state for the Add Profile review workflow.</summary>
public sealed record AddProfileReviewState<T>(ImmutableArray<T> Choices, T? Selected)
    where T : class
{
    public static AddProfileReviewState<T> Empty { get; } = new([], null);

    public bool IsVisible => !Choices.IsEmpty;
    public bool RequiresSelection => Choices.Length > 1;
    public bool CanConnect => Selected is not null;

    public static AddProfileReviewState<T> FromChoices(ImmutableArray<T> choices)
    {
        if (choices.IsDefaultOrEmpty)
            throw new ArgumentException("At least one resolved profile is required.", nameof(choices));
        return new(choices, choices.Length == 1 ? choices[0] : null);
    }

    public AddProfileReviewState<T> Select(T? choice)
    {
        if (choice is not null && !Choices.Contains(choice))
            throw new ArgumentException("The selected profile is not part of this resolution.", nameof(choice));
        return this with { Selected = choice };
    }

    public AddProfileReviewState<T> Clear() => Empty;
}

/// <summary>
/// Maps adapter-owned resolution evidence into the manager-neutral review
/// contract consumed by the frontend. This service has no persistence dependency.
/// </summary>
public sealed class GridProfileEnvironmentResolver(
    Mo2ProfileEnvironmentResolutionService mo2Resolver,
    Mo2DiscoveryOptions discoveryOptions)
{
    public async Task<ResolvedProfileEnvironmentResolution> ResolveAsync(
        string selectedDirectory,
        GridCatalogSnapshot catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var result = await mo2Resolver.ResolveAsync(
            selectedDirectory,
            catalog.Games,
            discoveryOptions,
            cancellationToken).ConfigureAwait(false);

        var game = catalog.Games.Single(candidate => candidate.Id == result.GameId);
        var managerName = game.Adapters.Single(adapter => adapter.Id == result.AdapterId).Name;
        var sharedWarnings = result.Issues
            .Where(issue => issue.Severity != Mo2IssueSeverity.Information)
            .Select(issue => issue.Message);
        var profiles = result.Profiles
            .Select(choice => Map(result, choice, managerName, sharedWarnings))
            .OrderBy(choice => choice.ProfileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(choice => choice.ProfileId.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        if (profiles.IsEmpty)
            throw new InvalidDataException("The selected environment has no reviewable existing profiles.");

        return new(profiles);
    }

    private static ResolvedProfileEnvironment Map(
        Mo2ProfileEnvironmentResolution result,
        Mo2ResolvedProfileChoice choice,
        string managerName,
        IEnumerable<string> sharedWarnings)
    {
        var profile = choice.Profile;
        var settings = profile.Settings;
        var customOutputs = settings?.CustomOverwrites.IsDefaultOrEmpty == false
            ? settings.CustomOverwrites.Length
            : 0;
        var warnings = sharedWarnings
            .Concat(profile.Sources.SelectMany(source => source.Warnings.Select(warning => warning.Message)))
            .Concat(choice.Inventory?.Warnings.Select(warning => warning.Message) ?? [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        var topology = result.Validation.InstanceKind switch
        {
            Mo2InstanceKind.Portable => "Portable instance",
            Mo2InstanceKind.Global => "Global instance",
            _ => "Validated instance",
        };
        var configuration = string.Join(" | ", new[]
        {
            $"Local saves: {FormatSetting(settings?.LocalSavesEnabled)}",
            $"Local settings: {FormatSetting(settings?.LocalSettingsEnabled)}",
            $"Custom outputs: {customOutputs:N0}",
        });
        var inventoryStatus = choice.Inventory?.Status.ToString() ?? "Unavailable";
        var evidence = $"{result.Candidate.EvidenceDescription}. GRID validated the {topology.ToLowerInvariant()}, observed the existing profile, and read its manager-owned configuration without persisting a connection.";

        return new(
            result.GameId,
            result.GameName,
            result.AdapterId,
            managerName,
            profile.Id,
            profile.Name,
            topology,
            result.Validation.ApplicationDirectory ?? "Not available",
            result.Validation.InstanceDirectory ?? result.Candidate.InstancePath ?? "Not available",
            result.Validation.GameDirectory ?? "Not available",
            $"{inventoryStatus}: {choice.ManagedEnabledModCount:N0} enabled | {choice.ManagedDisabledModCount:N0} disabled | {choice.ForeignModCount:N0} foreign | {choice.SeparatorCount:N0} separators",
            $"{choice.ActivePluginCount:N0} active | {choice.ExplicitlyEnabledPluginCount:N0} explicit | {choice.ImplicitlyActivePluginCount:N0} implicit | {choice.ExplicitlyDisabledPluginCount:N0} disabled | {choice.ProjectedPluginCount:N0} observed total",
            configuration,
            $"Profile: {profile.Observation.Status} | Manager: {profile.ManagerState} | Inventory: {inventoryStatus} | Warnings: {warnings.Length:N0}",
            choice.ManagedEnabledModCount,
            choice.ActivePluginCount,
            warnings,
            evidence,
            result.ValidationRequest);
    }

    private static string FormatSetting(bool? value) => value switch
    {
        true => "enabled",
        false => "disabled",
        null => "not established",
    };
}
