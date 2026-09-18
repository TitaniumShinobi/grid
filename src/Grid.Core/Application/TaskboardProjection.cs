using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public static class TaskboardProjection
{
    public static GridTaskboardSnapshot Create(AssistantSessionSnapshot assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var active = assistant.ActiveTask;
        var cards = assistant.Tasks
            .Select(summary => Project(summary, active))
            .OrderByDescending(card => card.UpdatedAtUtc)
            .ToImmutableArray();

        // Suggestions are deliberately empty until GRID has an evidence-backed mediation record.
        // The Queue UI renders only real suggestions supplied by a deterministic source.
        return new GridTaskboardSnapshot(cards, []);
    }

    private static GridTaskboardCard Project(AssistantTaskSummary summary, AssistantTaskRecord? active)
    {
        var record = active?.Summary.Id == summary.Id ? active : null;
        var status = (record?.TerminalState ?? summary.Status ?? string.Empty).Trim();
        var phase = ResolvePhase(status);
        var terminal = phase == GridTaskboardPhase.Ledger ? ResolveTerminal(status) : GridTaskTerminalStatus.None;
        var subtitle = phase switch
        {
            GridTaskboardPhase.Queue => QueueSubtitle(record, summary),
            GridTaskboardPhase.InProgress => CurrentStep(record, summary),
            GridTaskboardPhase.Ready => ReadySummary(record, summary),
            GridTaskboardPhase.Ledger => terminal.ToString(),
            _ => summary.Status,
        };
        var receipts = record?.Receipts.Select(receipt => receipt.ReceiptSha256).ToImmutableArray() ?? [];
        return new(summary.Id, summary.Title, OneLine(subtitle), phase, terminal,
            record?.Transcript.LastOrDefault()?.TimestampUtc ?? summary.CreatedAtUtc, receipts, record?.CaseId);
    }

    private static GridTaskboardPhase ResolvePhase(string status)
    {
        var normalized = status.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        if (ContainsAny(normalized, "complete", "diagnos", "verified", "applied", "archive", "cancel", "fail", "rejected"))
            return GridTaskboardPhase.Ledger;
        if (ContainsAny(normalized, "ready", "review", "evidenceavailable", "proposalavailable"))
            return GridTaskboardPhase.Ready;
        if (ContainsAny(normalized, "progress", "running", "execut", "verifying", "collect"))
            return GridTaskboardPhase.InProgress;
        return GridTaskboardPhase.Queue;
    }

    private static GridTaskTerminalStatus ResolveTerminal(string status)
    {
        if (status.Contains("cancel", StringComparison.OrdinalIgnoreCase) || status.Contains("reject", StringComparison.OrdinalIgnoreCase))
            return GridTaskTerminalStatus.Cancelled;
        if (status.Contains("fail", StringComparison.OrdinalIgnoreCase) || status.Contains("recovery", StringComparison.OrdinalIgnoreCase))
            return GridTaskTerminalStatus.Failed;
        return GridTaskTerminalStatus.Completed;
    }

    private static string QueueSubtitle(AssistantTaskRecord? record, AssistantTaskSummary summary) =>
        record?.Request?.Draft.VerbatimUserText
        ?? record?.Transcript.FirstOrDefault(entry => entry.Kind == AssistantTranscriptKind.UserClaim)?.Text
        ?? summary.Status;

    private static string CurrentStep(AssistantTaskRecord? record, AssistantTaskSummary summary) =>
        record?.Transcript.LastOrDefault(entry => entry.Kind == AssistantTranscriptKind.Progress)?.Text
        ?? summary.Status;

    private static string ReadySummary(AssistantTaskRecord? record, AssistantTaskSummary summary) =>
        record?.Finding?.Finding
        ?? record?.Transcript.LastOrDefault(entry => entry.Kind is AssistantTranscriptKind.Result or AssistantTranscriptKind.Evidence)?.Text
        ?? summary.Status;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "No detail recorded";
        var line = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 92 ? line : $"{line[..89]}...";
    }
}
