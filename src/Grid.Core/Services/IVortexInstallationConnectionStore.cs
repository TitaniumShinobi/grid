using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IVortexInstallationConnectionStore
{
    Task<VortexConnectionLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task<VortexInstallationConnection> ConnectAsync(InstallationId installationId, string stagingRoot, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(InstallationId installationId, CancellationToken cancellationToken = default);
}
