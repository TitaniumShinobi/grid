using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum HistoryActor
{
    User,
    Assistant,
    Automation,
    System,
}

public enum HistoryEventKind
{
    InstallationConnected,
    InstallationDisconnected,
    ExternalObservationRefreshed,
    SessionAuthorizationGranted,
    ActionProposed,
    ActionApproved,
    ActionStarted,
    ActionVerified,
    ActionFailed,
    RecoveryRequired,
}

public enum HistoryEventStatus
{
    Pending,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record HistoryEntry(
    HistoryEntryId Id,
    DateTimeOffset OccurredAtUtc,
    HistoryActor Actor,
    HistoryEventKind Kind,
    HistoryEventStatus Status,
    GameId? GameId,
    InstallationId? InstallationId,
    ProfileId? ProfileId,
    string Title,
    string Detail);

public sealed record HistoryLoadResult(
    ImmutableArray<HistoryEntry> Entries,
    string? Issue);
