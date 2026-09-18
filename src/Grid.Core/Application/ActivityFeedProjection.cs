using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public static class ActivityFeedProjection
{
    public static ImmutableArray<GridActivityFeedItem> CreateRecent(
        GridTaskboardSnapshot taskboard,
        IEnumerable<HistoryEntry> historyEntries,
        int maximum = 12)
    {
        ArgumentNullException.ThrowIfNull(taskboard);
        ArgumentNullException.ThrowIfNull(historyEntries);
        if (maximum <= 0) throw new ArgumentOutOfRangeException(nameof(maximum));

        var tasks = taskboard.Cards.Select(card => new GridActivityFeedItem(
            $"task:{card.TaskId}",
            card.Title,
            TaskStatus(card),
            card.Subtitle,
            card.UpdatedAtUtc,
            card.TaskId));
        var history = historyEntries.Select(entry => new GridActivityFeedItem(
            $"history:{entry.Id.Value}",
            entry.Title,
            entry.Status.ToString(),
            entry.Detail,
            entry.OccurredAtUtc));

        return tasks.Concat(history)
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .Take(maximum)
            .ToImmutableArray();
    }

    private static string TaskStatus(GridTaskboardCard card) => card.Phase switch
    {
        GridTaskboardPhase.Queue => "Queued",
        GridTaskboardPhase.InProgress => "In progress",
        GridTaskboardPhase.Ready => "Ready",
        GridTaskboardPhase.Ledger when card.TerminalStatus == GridTaskTerminalStatus.Cancelled => "Cancelled",
        GridTaskboardPhase.Ledger when card.TerminalStatus == GridTaskTerminalStatus.Failed => "Failed",
        GridTaskboardPhase.Ledger => "Completed",
        _ => "Recorded",
    };
}
