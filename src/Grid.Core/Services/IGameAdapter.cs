using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IGameAdapter
{
    GameAdapterIdentity Identity { get; }

    GameId GameId { get; }

    WorkspaceCapabilities Capabilities { get; }

    GameToolCatalog ToolCatalog { get; }

    Task<ImmutableArray<ManagedInstallation>> GetInstallationsAsync(
        CancellationToken cancellationToken = default);
}
