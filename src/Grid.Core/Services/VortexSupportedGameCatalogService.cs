using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Adds frozen Vortex bundled game definitions to GRID's browse catalog without
/// changing GRID-native game identities or creating installation/connection state.
/// </summary>
public sealed class VortexSupportedGameCatalogService(IGridCatalogService inner) : IGridCatalogService
{
    public async Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await inner.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var games = catalog.Games.ToBuilder();

        var ids = games.Select(game => game.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = games.Select(game => game.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in VortexBundledCatalogGenerated.Entries
                     .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.GameId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ids.Add(entry.GameId) || !names.Add(entry.Name))
                continue;

            games.Add(new ManagedGame(
                new GameId(entry.GameId),
                entry.Name,
                new WorkspaceCapabilities(WorkspaceFeature.None, EnvironmentTabCapability.None),
                ImmutableArray.Create(
                    new GameAdapterIdentity(
                        new GameAdapterId("adapter.vortex-catalog-metadata"),
                        "Vortex catalog metadata")),
                ImmutableArray<ManagedInstallation>.Empty,
                new GameToolCatalog(
                    ImmutableArray<ToolDefinition>.Empty,
                    ImmutableArray<LaunchTargetDefinition>.Empty),
                new HealthSummary(
                    HealthLevel.Unknown,
                    "Vortex catalog definition",
                    ImmutableArray<Advisory>.Empty)));
        }

        return new GridCatalogSnapshot(
            $"{catalog.Revision}+vortex-bundled-2.6.3",
            CatalogSourceKind.Mixed,
            games);
    }
}
