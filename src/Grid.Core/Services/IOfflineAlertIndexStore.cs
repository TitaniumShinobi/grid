using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Persists normalized alert snapshots for instant, offline presentation. Implementations
/// have no authority to collect evidence, launch tools, or mutate a managed game.
/// </summary>
public interface IOfflineAlertIndexStore
{
    OfflineAlertIndex? Load(GameId gameId, InstallationId installationId, ProfileId profileId);

    void Save(OfflineAlertIndex index);
}
