using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IUserToolConfigurationStore
{
    Task<UserToolConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task<UserToolSaveResult> ReplaceAsync(
        int expectedRevision,
        ImmutableArray<UserToolLaunchConfiguration> configurations,
        CancellationToken cancellationToken = default);
}

public interface IInstalledToolKnowledgeStore
{
    Task<InstalledToolKnowledgeSnapshot> LoadAsync(CancellationToken cancellationToken = default);

    Task<UserToolSaveResult> ReplaceAsync(
        int expectedRevision,
        ImmutableArray<InstalledToolKnowledge> resolutions,
        CancellationToken cancellationToken = default);
}

public interface IInstalledToolIdentityService
{
    Task<InstalledToolKnowledge> ResolveAsync(string exactBinaryPath, CancellationToken cancellationToken = default);
}

public interface IUserToolLaunchService
{
    Task<UserToolLaunchResult> LaunchAsync(
        UserToolLaunchConfiguration configuration,
        UserToolLaunchRequest request,
        CancellationToken cancellationToken = default);
}
