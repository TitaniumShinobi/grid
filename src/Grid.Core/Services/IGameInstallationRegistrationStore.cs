using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IGameInstallationRegistrationStore
{
    Task<GameRegistrationLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ImmutableArray<GameInstallationRegistration> registrations, CancellationToken cancellationToken = default);

    Task<GameInstallationRegistration> RegisterAsync(
        GameId gameId,
        GameAdapterId adapterId,
        string displayName,
        string edition,
        string providerId,
        string installRoot,
        string executablePath,
        IEnumerable<string> managerProviderIds,
        CancellationToken cancellationToken = default);

    Task<bool> RemoveAsync(InstallationId installationId, CancellationToken cancellationToken = default);
}
