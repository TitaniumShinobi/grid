using Grid.Core.Models;

namespace Grid.Core.Services;

public interface IUserHistoryStore
{
    Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
}
