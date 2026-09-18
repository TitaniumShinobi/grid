using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IGridCatalogService
{
    Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default);
}
