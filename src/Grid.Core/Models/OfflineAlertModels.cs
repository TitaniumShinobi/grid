using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum OfflineAlertSeverity
{
    Information,
    Advisory,
    Warning,
    Error,
}

public enum OfflineAlertState
{
    Active,
    Acknowledged,
    AccountedFor,
    Cleared,
    Stale,
}

public enum OfflineAlertSourceKind
{
    FidelityAudit,
    ToolOutput,
    Executable,
    GeneratedOutput,
    Profile,
    Mod,
    Plugin,
    ResolvedEnvironment,
}

public sealed record OfflineAlert(
    string Id,
    string Code,
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    OfflineAlertSourceKind SourceKind,
    string SourceIdentity,
    string Title,
    string Detail,
    OfflineAlertSeverity Severity,
    OfflineAlertState State,
    bool IsBlocking,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    string ContextFingerprint,
    string? EvidenceFingerprint,
    ImmutableArray<string> Provenance,
    string? DispositionReason = null);

public sealed record OfflineAlertIndex(
    int SchemaVersion,
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    DateTimeOffset UpdatedAtUtc,
    string WorkspaceFingerprint,
    string ContextFingerprint,
    ImmutableArray<OfflineAlert> Alerts,
    string Fingerprint)
{
    public int ActiveCount => Alerts.Count(alert => alert.State == OfflineAlertState.Active);
    public int BlockingCount => Alerts.Count(alert => alert.State == OfflineAlertState.Active && alert.IsBlocking);
}
