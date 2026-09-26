using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class UserToolManagerState(
    IUserToolConfigurationStore configurationStore,
    IInstalledToolKnowledgeStore knowledgeStore)
{
    public UserToolConfigurationSnapshot Configurations { get; private set; } = UserToolConfigurationSnapshot.Empty;
    public InstalledToolKnowledgeSnapshot Knowledge { get; private set; } = InstalledToolKnowledgeSnapshot.Empty;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Configurations = await configurationStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        Knowledge = await knowledgeStore.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public ImmutableArray<UserToolLaunchConfiguration> ForContext(UserToolScope currentContext, bool includeDisabled = false) =>
        Configurations.Configurations
            .Where(value => (includeDisabled || value.Enabled) && value.Scope.IsVisibleIn(currentContext))
            .OrderBy(value => value.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();

    public InstalledToolKnowledge? FindKnowledge(InstalledToolKnowledgeId? id) =>
        id is InstalledToolKnowledgeId value
            ? Knowledge.Resolutions.FirstOrDefault(candidate => candidate.Id == value)
            : null;

    public async Task<UserToolSaveResult> ReplaceConfigurationsAsync(
        ImmutableArray<UserToolLaunchConfiguration> values,
        CancellationToken cancellationToken = default)
    {
        var result = await configurationStore.ReplaceAsync(Configurations.Revision, values, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) Configurations = new(result.Revision, values, []);
        return result;
    }

    public async Task<UserToolSaveResult> MergeKnowledgeAsync(
        IEnumerable<InstalledToolKnowledge> values,
        CancellationToken cancellationToken = default)
    {
        var merged = Knowledge.Resolutions.ToDictionary(value => value.Id);
        foreach (var value in values) merged[value.Id] = value;
        var snapshot = merged.Values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        var result = await knowledgeStore.ReplaceAsync(Knowledge.Revision, snapshot, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) Knowledge = new(result.Revision, snapshot, []);
        return result;
    }
}

public sealed class UserToolLaunchState(IUserToolLaunchService service, UserToolManagerState tools)
{
    public Task<UserToolLaunchResult> LaunchAsync(
        UserToolLaunchConfiguration configuration,
        UserToolScope currentContext,
        CancellationToken cancellationToken = default)
    {
        var current = tools.Configurations.Configurations.FirstOrDefault(value => value.Id == configuration.Id);
        if (current is null || !current.Scope.IsVisibleIn(currentContext) || current.Fingerprint != configuration.Fingerprint)
            return Task.FromResult(new UserToolLaunchResult(UserToolLaunchStatus.Stale, null, "The selected launch configuration is stale or belongs to another workspace."));
        return service.LaunchAsync(current, new(current.Id, currentContext, current.Fingerprint), cancellationToken);
    }
}
