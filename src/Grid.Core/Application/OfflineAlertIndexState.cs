using Grid.Core.Models;
using Grid.Core.Services;
using System.Text.Json;

namespace Grid.Core.Application;

/// <summary>
/// Owns the selected profile's normalized offline alert index. It only projects and
/// persists already-collected evidence; it cannot collect evidence or mutate game state.
/// </summary>
public sealed class OfflineAlertIndexState(
    IOfflineAlertIndexStore store,
    TimeProvider? timeProvider = null)
{
    private readonly IOfflineAlertIndexStore store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly OfflineAlertIndexBuilder builder = new();

    public WorkspaceToolOutputContext? Context { get; private set; }

    public OfflineAlertIndex? Snapshot { get; private set; }

    public bool IsFromPersistentCache { get; private set; }

    public bool IsCurrentSnapshotStale { get; private set; }

    public string? PersistenceIssue { get; private set; }

    public void SetContext(WorkspaceToolOutputContext? context)
    {
        if (Context == context)
            return;

        Context = context;
        Snapshot = null;
        IsFromPersistentCache = false;
        IsCurrentSnapshotStale = false;
        PersistenceIssue = null;
        if (context is not WorkspaceToolOutputContext selected)
            return;

        try
        {
            var loaded = store.Load(selected.GameId, selected.InstallationId, selected.ProfileId);
            if (loaded is null)
                return;
            if (!builder.IsValid(loaded))
            {
                PersistenceIssue = "The cached offline alert index failed integrity validation and was ignored.";
                return;
            }
            if (loaded.GameId != selected.GameId || loaded.InstallationId != selected.InstallationId || loaded.ProfileId != selected.ProfileId)
            {
                PersistenceIssue = "The cached offline alert index belongs to a different workspace and was ignored.";
                return;
            }

            IsFromPersistentCache = true;
            if (string.Equals(loaded.WorkspaceFingerprint, builder.GetWorkspaceFingerprint(selected), StringComparison.Ordinal))
            {
                Snapshot = loaded;
                return;
            }

            Snapshot = builder.Build(selected, null, null, loaded, timeProvider.GetUtcNow());
            IsCurrentSnapshotStale = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            PersistenceIssue = $"The cached offline alert index could not be loaded: {exception.GetType().Name}.";
        }
    }

    public OfflineAlertIndex? Refresh(
        ToolOutputObservationRefreshResult? toolOutputs,
        FidelityAuditResult? fidelityAudit,
        Profile? profile = null,
        ResolvedEnvironmentRefreshResult? resolvedEnvironment = null)
    {
        if (Context is not WorkspaceToolOutputContext context)
            return null;

        var updated = builder.Build(context, toolOutputs, fidelityAudit, Snapshot, timeProvider.GetUtcNow(), profile, resolvedEnvironment);
        Snapshot = updated;
        IsFromPersistentCache = false;
        IsCurrentSnapshotStale = updated.Alerts.Any(alert => alert.State == OfflineAlertState.Stale);
        PersistenceIssue = null;
        try
        {
            store.Save(updated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            PersistenceIssue = $"The offline alert index could not be persisted: {exception.GetType().Name}.";
        }
        return updated;
    }

    public bool SetDisposition(string alertId, OfflineAlertState state, string? reason = null)
    {
        if (Snapshot is null || state is not (OfflineAlertState.Active or OfflineAlertState.Acknowledged or OfflineAlertState.AccountedFor))
            return false;
        if (state == OfflineAlertState.AccountedFor && string.IsNullOrWhiteSpace(reason))
            return false;

        var index = -1;
        for (var candidateIndex = 0; candidateIndex < Snapshot.Alerts.Length; candidateIndex++)
        {
            if (!string.Equals(Snapshot.Alerts[candidateIndex].Id, alertId, StringComparison.Ordinal))
                continue;
            index = candidateIndex;
            break;
        }
        if (index < 0)
            return false;
        var alert = Snapshot.Alerts[index];
        if (alert.State is OfflineAlertState.Cleared or OfflineAlertState.Stale)
            return false;

        var alerts = Snapshot.Alerts.SetItem(index, alert with
        {
            State = state,
            DispositionReason = state == OfflineAlertState.Active ? null : reason?.Trim(),
        });
        Snapshot = Snapshot with
        {
            UpdatedAtUtc = timeProvider.GetUtcNow(),
            Alerts = alerts,
            Fingerprint = string.Empty,
        };
        Snapshot = builder.RecalculateFingerprint(Snapshot);
        try
        {
            store.Save(Snapshot);
            PersistenceIssue = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or JsonException)
        {
            PersistenceIssue = $"The offline alert disposition could not be persisted: {exception.GetType().Name}.";
        }
        return true;
    }
}
