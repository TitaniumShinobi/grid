using Grid.Core.Models;

namespace Grid.Core.Services;

internal static class GtaSelectorDisplayRules
{
    public static bool IsGtaGame(GameId gameId) =>
        gameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
        gameId == ProductionGridCatalogService.GrandTheftAutoVLegacyId;
}
