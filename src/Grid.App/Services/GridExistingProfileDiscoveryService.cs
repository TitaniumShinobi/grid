using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.App.Services;

public sealed record ExistingProfileDiscoveryTarget(
    GameId GameId,
    string GameName,
    string InstallationRoot,
    IReadOnlyList<ProviderManagerCandidate> Managers);

public sealed record ExistingProfileDiscoveryIssue(string ProviderId, string Message);

public sealed record ExistingProfileDiscoveryResult(
    ImmutableArray<ResolvedProfileEnvironment> Profiles,
    ImmutableArray<ExistingProfileDiscoveryIssue> Issues);

public interface IExistingProfileDiscoveryAdapter
{
    string ProviderId { get; }

    bool Supports(ManagedGame game);

    Task<ExistingProfileDiscoveryResult> DiscoverAsync(
        ExistingProfileDiscoveryTarget target,
        GridCatalogSnapshot catalog,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Routes provider evidence to manager-owned, read-only profile resolvers. It
/// neither creates a profile nor owns a connection persistence dependency.
/// </summary>
public sealed class GridExistingProfileDiscoveryService(IEnumerable<IExistingProfileDiscoveryAdapter> adapters)
{
    private readonly ImmutableDictionary<string, IExistingProfileDiscoveryAdapter> adapters = adapters
        .ToImmutableDictionary(adapter => adapter.ProviderId, StringComparer.OrdinalIgnoreCase);

    public async Task<ExistingProfileDiscoveryResult> DiscoverAsync(
        ExistingProfileDiscoveryTarget target,
        GridCatalogSnapshot catalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(catalog);

        var profiles = ImmutableArray.CreateBuilder<ResolvedProfileEnvironment>();
        var issues = ImmutableArray.CreateBuilder<ExistingProfileDiscoveryIssue>();

        var game = catalog.Games.Single(candidate => candidate.Id == target.GameId);
        foreach (var adapter in adapters.Values
                     .Where(candidate => candidate.Supports(game))
                     .OrderBy(candidate => candidate.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await adapter.DiscoverAsync(
                target with
                {
                    Managers = target.Managers
                        .Where(candidate => candidate.ProviderId.Equals(adapter.ProviderId, StringComparison.OrdinalIgnoreCase))
                        .ToArray(),
                },
                catalog,
                cancellationToken).ConfigureAwait(false);
            profiles.AddRange(result.Profiles);
            issues.AddRange(result.Issues);
        }

        return new(
            profiles
                .DistinctBy(ProfileIdentity, StringComparer.OrdinalIgnoreCase)
                .OrderBy(profile => profile.ManagerName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.ProfileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.ProfileId.Value, StringComparer.Ordinal)
                .ToImmutableArray(),
            issues
                .Distinct()
                .OrderBy(issue => issue.ProviderId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(issue => issue.Message, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static string ProfileIdentity(ResolvedProfileEnvironment profile) =>
        $"{profile.AdapterId.Value}|{profile.InstanceDirectory}|{profile.ProfileId.Value}";
}
