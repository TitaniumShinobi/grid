using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class HistoryState(IUserHistoryStore store)
{
    private readonly IUserHistoryStore store = store ?? throw new ArgumentNullException(nameof(store));

    public ImmutableArray<HistoryEntry> Entries { get; private set; } = [];

    public string? Issue { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        Entries = result.Entries.OrderByDescending(entry => entry.OccurredAtUtc).ToImmutableArray();
        Issue = result.Issue;
    }

    public async Task RecordAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            await store.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            Entries = Entries.Insert(0, entry);
            Issue = null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Issue = "History could not be updated. The underlying operation was not changed.";
        }
    }
}
