using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Describes production-supported games without fabricating managed installations.
/// Real installations are appended only by infrastructure-backed catalog decorators.
/// </summary>
public sealed class ProductionGridCatalogService : IGridCatalogService
{
    public static readonly GameId SkyrimSpecialEditionId = new("game.skyrim-special-edition");
    public static readonly GameId GrandTheftAutoVId = new("game.grand-theft-auto-v");
    public static readonly GameAdapterId ModOrganizer2AdapterId = new("adapter.mod-organizer-2");
    public static readonly GameAdapterId ProviderDiscoveryAdapterId = new("adapter.provider-discovery");

    private static readonly GridCatalogSnapshot Catalog = CreateCatalog();

    public Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Catalog);
    }

    private static GridCatalogSnapshot CreateCatalog()
    {
        var capabilities = new WorkspaceCapabilities(
            WorkspaceFeature.Profiles |
            WorkspaceFeature.ModList |
            WorkspaceFeature.Health |
            WorkspaceFeature.Tools |
            WorkspaceFeature.LaunchTargets,
            EnvironmentTabCapability.Plugins |
            EnvironmentTabCapability.Archives |
            EnvironmentTabCapability.Data |
            EnvironmentTabCapability.Saves |
            EnvironmentTabCapability.Downloads |
            EnvironmentTabCapability.Conflicts |
            EnvironmentTabCapability.Outputs |
            EnvironmentTabCapability.Activity);

        var skyrim = new ManagedGame(
            SkyrimSpecialEditionId,
            "Skyrim Special Edition",
            capabilities,
            ImmutableArray.Create(new GameAdapterIdentity(ModOrganizer2AdapterId, "Mod Organizer 2")),
            ImmutableArray<ManagedInstallation>.Empty,
            new GameToolCatalog(
                ImmutableArray<ToolDefinition>.Empty,
                ImmutableArray<LaunchTargetDefinition>.Empty),
            new HealthSummary(
                HealthLevel.Unknown,
                "No installation observed",
                ImmutableArray<Advisory>.Empty));

        var gta = new ManagedGame(
            GrandTheftAutoVId,
            "Grand Theft Auto V",
            capabilities,
            ImmutableArray.Create(new GameAdapterIdentity(ProviderDiscoveryAdapterId, "Provider discovery")),
            ImmutableArray<ManagedInstallation>.Empty,
            new GameToolCatalog(
                ImmutableArray<ToolDefinition>.Empty,
                ImmutableArray<LaunchTargetDefinition>.Empty),
            new HealthSummary(
                HealthLevel.Unknown,
                "No installation observed",
                ImmutableArray<Advisory>.Empty));

        return new GridCatalogSnapshot(
            "production.supported-games.v1",
            CatalogSourceKind.Adapter,
            [skyrim, gta]);
    }
}
