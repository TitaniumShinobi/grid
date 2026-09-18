using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum GridTaskboardPhase
{
    Queue,
    InProgress,
    Ready,
    Ledger,
}

public enum GridTaskTerminalStatus
{
    None,
    Completed,
    Cancelled,
    Failed,
}

public sealed record GridTaskboardCard(
    string TaskId,
    string Title,
    string Subtitle,
    GridTaskboardPhase Phase,
    GridTaskTerminalStatus TerminalStatus,
    DateTimeOffset UpdatedAtUtc,
    ImmutableArray<string> ReceiptIds,
    string? CaseId = null);

public sealed record GridTaskboardSuggestion(
    string SuggestionId,
    string Title,
    string Description,
    DateTimeOffset CreatedAtUtc);

public sealed record GridTaskboardSnapshot(
    ImmutableArray<GridTaskboardCard> Cards,
    ImmutableArray<GridTaskboardSuggestion> Suggestions)
{
    public int QueueCount => Cards.Count(card => card.Phase == GridTaskboardPhase.Queue) + Suggestions.Length;
    public int InProgressCount => Cards.Count(card => card.Phase == GridTaskboardPhase.InProgress);
    public int ReadyCount => Cards.Count(card => card.Phase == GridTaskboardPhase.Ready);
    public int LedgerCount => Cards.Count(card => card.Phase == GridTaskboardPhase.Ledger);
}

public sealed record GridActivityFeedItem(
    string Id,
    string Title,
    string Status,
    string Detail,
    DateTimeOffset OccurredAtUtc,
    string? TaskId = null);
