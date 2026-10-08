using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Services;

namespace Grid.App.Services;

/// <summary>
/// Converts bounded provider-discovery roots into the canonical MO2 profile
/// resolver and accepts only profiles backed by the selected game installation.
/// </summary>
public sealed class Mo2ExistingProfileDiscoveryAdapter(
    GridProfileEnvironmentResolver resolver,
    IMo2PathCanonicalizer paths) : IExistingProfileDiscoveryAdapter
{
    private static readonly GameAdapterId Mo2AdapterId = new("adapter.mod-organizer-2");

    public string ProviderId => "mo2";

    public bool Supports(ManagedGame game) => game.Adapters.Any(adapter => adapter.Id == Mo2AdapterId);

    public async Task<ExistingProfileDiscoveryResult> DiscoverAsync(
        ExistingProfileDiscoveryTarget target,
        GridCatalogSnapshot catalog,
        CancellationToken cancellationToken = default)
    {
        var profiles = ImmutableArray.CreateBuilder<ResolvedProfileEnvironment>();
        var issues = ImmutableArray.CreateBuilder<ExistingProfileDiscoveryIssue>();
        var roots = new[] { new CandidateSeed(target.InstallationRoot, true) }
            .Concat(target.Managers.SelectMany(CandidateRoots).Select(root => new CandidateSeed(root, false)))
            .Where(seed => !string.IsNullOrWhiteSpace(seed.Root))
            .GroupBy(seed => seed.Root, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(seed => seed.IsInstallationSeed).First())
            .OrderBy(seed => seed.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var seed in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resolution = await resolver.ResolveAsync(seed.Root, catalog, cancellationToken).ConfigureAwait(false);
                profiles.AddRange(resolution.Profiles.Where(profile =>
                    profile.GameId == target.GameId &&
                    paths.Equals(profile.GameDirectory, target.InstallationRoot)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                if (!seed.IsInstallationSeed)
                    issues.Add(new(ProviderId, $"{seed.Root}: {exception.Message}"));
            }
        }

        return new(
            profiles
                .DistinctBy(profile => $"{profile.InstanceDirectory}|{profile.ProfileId.Value}", StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            issues.ToImmutable());
    }

    private static IEnumerable<string> CandidateRoots(ProviderManagerCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.InstanceRoot))
        {
            yield return candidate.InstanceRoot;
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(candidate.ApplicationRoot))
        {
            yield return candidate.ApplicationRoot;
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(candidate.ExecutablePath) && Path.GetDirectoryName(candidate.ExecutablePath) is { } executableRoot)
        {
            yield return executableRoot;
            yield break;
        }
        foreach (var profileRoot in candidate.ProfileRoots) yield return profileRoot;
    }

    private sealed record CandidateSeed(string Root, bool IsInstallationSeed);
}
