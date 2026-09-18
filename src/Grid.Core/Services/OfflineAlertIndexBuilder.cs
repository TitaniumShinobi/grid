using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class OfflineAlertIndexBuilder
{
    public const int SchemaVersion = 1;

    public OfflineAlertIndex Build(
        WorkspaceToolOutputContext context,
        ToolOutputObservationRefreshResult? toolOutputs,
        FidelityAuditResult? fidelityAudit,
        OfflineAlertIndex? previous = null,
        DateTimeOffset? observedAtUtc = null,
        Profile? profile = null,
        ResolvedEnvironmentRefreshResult? resolvedEnvironment = null)
    {
        if (previous is not null &&
            (previous.GameId != context.GameId || previous.InstallationId != context.InstallationId || previous.ProfileId != context.ProfileId))
            throw new ArgumentException("A previous offline alert index must belong to the exact selected game, installation, and profile.", nameof(previous));

        var now = (observedAtUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var workspaceFingerprint = GetWorkspaceFingerprint(context);
        var contextFingerprint = Hash(string.Join('\n',
            workspaceFingerprint,
            toolOutputs?.Snapshot?.Summary.Fingerprint ?? string.Empty,
            fidelityAudit?.Snapshot?.Fingerprint ?? string.Empty,
            profile?.Observation?.SnapshotFingerprint ?? string.Empty,
            resolvedEnvironment?.Snapshot?.Summary.Fingerprint ?? string.Empty));
        var current = new Dictionary<string, OfflineAlert>(StringComparer.Ordinal);

        if (fidelityAudit?.Snapshot is { } audit)
        {
            foreach (var item in audit.Items.Where(item => item.Status != FidelityAuditItemStatus.Pass))
            {
                var state = item.Status == FidelityAuditItemStatus.Stale ? OfflineAlertState.Stale : OfflineAlertState.Active;
                Add(new(
                    Id(context, OfflineAlertSourceKind.FidelityAudit, item.Code, item.Code),
                    item.Code,
                    context.GameId,
                    context.InstallationId,
                    context.ProfileId,
                    OfflineAlertSourceKind.FidelityAudit,
                    audit.Id.Value,
                    item.Title,
                    item.Detail,
                    Severity(item.Status),
                    state,
                    item.IsBlocking,
                    FirstObserved(Id(context, OfflineAlertSourceKind.FidelityAudit, item.Code, item.Code), now),
                    now,
                    contextFingerprint,
                    item.EvidenceFingerprint,
                    ["GRID fidelity audit", audit.Id.Value, item.Area.ToString()]));
            }
        }

        if (toolOutputs?.Snapshot is { } tools)
        {
            foreach (var warning in tools.Warnings.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                var warningKey = Hash(warning);
                var id = Id(context, OfflineAlertSourceKind.ToolOutput, "grid.tool-output.warning", warningKey);
                Add(new(
                    id,
                    "grid.tool-output.warning",
                    context.GameId,
                    context.InstallationId,
                    context.ProfileId,
                    OfflineAlertSourceKind.ToolOutput,
                    tools.Id.Value,
                    "Tool output warning",
                    warning,
                    OfflineAlertSeverity.Warning,
                    OfflineAlertState.Active,
                    false,
                    FirstObserved(id, tools.Summary.ObservedAtUtc),
                    tools.Summary.ObservedAtUtc,
                    contextFingerprint,
                    tools.Summary.Fingerprint,
                    ["GRID tool-output observation", tools.Id.Value]));
            }

            foreach (var executable in tools.Executables.Where(value => value.WarningCount > 0))
            {
                var id = Id(context, OfflineAlertSourceKind.Executable, "grid.executable.warnings", executable.Id.Value);
                Add(new(
                    id,
                    "grid.executable.warnings",
                    context.GameId,
                    context.InstallationId,
                    context.ProfileId,
                    OfflineAlertSourceKind.Executable,
                    executable.Id.Value,
                    executable.Title,
                    $"{executable.WarningCount} warning(s) were retained for this configured executable.",
                    OfflineAlertSeverity.Warning,
                    OfflineAlertState.Active,
                    false,
                    FirstObserved(id, tools.Summary.ObservedAtUtc),
                    tools.Summary.ObservedAtUtc,
                    contextFingerprint,
                    executable.Fingerprint,
                    ["MO2 executable configuration", tools.Id.Value]));
            }

            foreach (var output in tools.Outputs.Where(value => value.WarningCount > 0))
            {
                var id = Id(context, OfflineAlertSourceKind.GeneratedOutput, "grid.generated-output.warnings", output.Id.Value);
                Add(new(
                    id,
                    "grid.generated-output.warnings",
                    context.GameId,
                    context.InstallationId,
                    context.ProfileId,
                    OfflineAlertSourceKind.GeneratedOutput,
                    output.Id.Value,
                    output.Name,
                    $"{output.WarningCount} warning(s) were retained for this generated output.",
                    OfflineAlertSeverity.Warning,
                    OfflineAlertState.Active,
                    false,
                    FirstObserved(id, tools.Summary.ObservedAtUtc),
                    tools.Summary.ObservedAtUtc,
                    contextFingerprint,
                    output.Fingerprint,
                    ["MO2 generated-output observation", tools.Id.Value]));
            }
        }

        if (profile is not null)
        {
            if (profile.Id != context.ProfileId || profile.InstallationId != context.InstallationId)
                throw new ArgumentException("Profile alert evidence must belong to the exact selected installation and profile.", nameof(profile));

            if (profile.Observation is { } observation)
            {
                if (observation.Status != ProfileObservationStatus.Complete)
                {
                    var id = Id(context, OfflineAlertSourceKind.Profile, "grid.profile.observation", profile.Id.Value);
                    Add(new(
                        id,
                        "grid.profile.observation",
                        context.GameId,
                        context.InstallationId,
                        context.ProfileId,
                        OfflineAlertSourceKind.Profile,
                        profile.Id.Value,
                        "Profile observation is incomplete",
                        $"The selected profile observation is {observation.Status}; absent evidence is not treated as clean.",
                        observation.Status == ProfileObservationStatus.Inconsistent ? OfflineAlertSeverity.Error : OfflineAlertSeverity.Warning,
                        OfflineAlertState.Active,
                        observation.Status == ProfileObservationStatus.Inconsistent,
                        FirstObserved(id, observation.ObservedAtUtc),
                        observation.ObservedAtUtc,
                        contextFingerprint,
                        observation.SnapshotFingerprint,
                        ["MO2 profile observation", profile.Id.Value]));
                }

                foreach (var source in observation.Sources.Where(source =>
                             source.Availability is not (ProfileSourceAvailability.Read or ProfileSourceAvailability.OptionalAbsent) ||
                             source.ParseStatus is not (ProfileSourceParseStatus.Parsed or ProfileSourceParseStatus.NotParsed)))
                {
                    var id = Id(context, OfflineAlertSourceKind.Profile, "grid.profile.source", source.Name);
                    var blocking = source.Availability is ProfileSourceAvailability.RequiredMissing or ProfileSourceAvailability.Inaccessible;
                    Add(new(
                        id,
                        "grid.profile.source",
                        context.GameId,
                        context.InstallationId,
                        context.ProfileId,
                        OfflineAlertSourceKind.Profile,
                        source.Name,
                        $"{source.Name} profile source",
                        $"Availability: {source.Availability}. Parse status: {source.ParseStatus}. Retained warnings: {source.WarningCount}.",
                        blocking ? OfflineAlertSeverity.Error : OfflineAlertSeverity.Warning,
                        OfflineAlertState.Active,
                        blocking,
                        FirstObserved(id, observation.ObservedAtUtc),
                        observation.ObservedAtUtc,
                        contextFingerprint,
                        observation.SnapshotFingerprint,
                        ["MO2 profile source", profile.Id.Value, source.Name]));
                }
            }

            foreach (var mod in profile.Mods.Where(mod => mod.Kind == ModEntryKind.Mod))
            {
                if (mod.Inventory is { } inventory)
                {
                    foreach (var warning in inventory.Warnings.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
                    {
                        var id = Id(context, OfflineAlertSourceKind.Mod, "grid.mod.inventory-warning", mod.Id.Value + "\n" + Hash(warning));
                        Add(new(
                            id,
                            "grid.mod.inventory-warning",
                            context.GameId,
                            context.InstallationId,
                            context.ProfileId,
                            OfflineAlertSourceKind.Mod,
                            mod.Id.Value,
                            mod.Name,
                            warning,
                            OfflineAlertSeverity.Warning,
                            OfflineAlertState.Active,
                            false,
                            FirstObserved(id, inventory.ObservedAtUtc),
                            inventory.ObservedAtUtc,
                            contextFingerprint,
                            inventory.Fingerprint,
                            ["MO2 mod inventory", mod.Id.Value, inventory.SourceName]));
                    }

                    if (inventory.UpdateEvidence.State == ModUpdateState.UpdateAvailable)
                    {
                        var id = Id(context, OfflineAlertSourceKind.Mod, "grid.mod.update-available", mod.Id.Value);
                        Add(new(
                            id,
                            "grid.mod.update-available",
                            context.GameId,
                            context.InstallationId,
                            context.ProfileId,
                            OfflineAlertSourceKind.Mod,
                            mod.Id.Value,
                            $"{mod.Name} update available",
                            inventory.UpdateEvidence.Detail,
                            OfflineAlertSeverity.Advisory,
                            OfflineAlertState.Active,
                            false,
                            FirstObserved(id, inventory.ObservedAtUtc),
                            inventory.ObservedAtUtc,
                            contextFingerprint,
                            inventory.Fingerprint,
                            ["MO2 mod metadata", mod.Id.Value, inventory.UpdateEvidence.NetworkChecked ? "network checked" : "cached provider metadata"]));
                    }
                }

                if (mod.ConflictState is ModConflictState.Overwrites or ModConflictState.Overwritten or ModConflictState.Mixed)
                {
                    var id = Id(context, OfflineAlertSourceKind.Mod, "grid.mod.conflict", mod.Id.Value);
                    Add(new(
                        id,
                        "grid.mod.conflict",
                        context.GameId,
                        context.InstallationId,
                        context.ProfileId,
                        OfflineAlertSourceKind.Mod,
                        mod.Id.Value,
                        $"{mod.Name} file conflict",
                        $"The deterministic inventory records this mod as {mod.ConflictState}.",
                        OfflineAlertSeverity.Information,
                        OfflineAlertState.Active,
                        false,
                        FirstObserved(id, mod.Inventory?.ObservedAtUtc ?? profile.Observation?.ObservedAtUtc ?? now),
                        mod.Inventory?.ObservedAtUtc ?? profile.Observation?.ObservedAtUtc ?? now,
                        contextFingerprint,
                        mod.Inventory?.Fingerprint ?? profile.Observation?.SnapshotFingerprint,
                        ["MO2 mod conflict state", mod.Id.Value]));
                }
            }

            foreach (var plugin in profile.Plugins.Where(plugin => plugin.Observation is not null))
            {
                var pluginObservation = plugin.Observation!;
                foreach (var warning in pluginObservation.Warnings.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
                {
                    var id = Id(context, OfflineAlertSourceKind.Plugin, "grid.plugin.warning", plugin.Id.Value + "\n" + Hash(warning));
                    Add(new(
                        id,
                        "grid.plugin.warning",
                        context.GameId,
                        context.InstallationId,
                        context.ProfileId,
                        OfflineAlertSourceKind.Plugin,
                        plugin.Id.Value,
                        plugin.Name,
                        warning,
                        pluginObservation.FileAvailability is PluginFileAvailability.Missing or PluginFileAvailability.Malformed
                            ? OfflineAlertSeverity.Error : OfflineAlertSeverity.Warning,
                        OfflineAlertState.Active,
                        pluginObservation.FileAvailability is PluginFileAvailability.Missing or PluginFileAvailability.Malformed,
                        FirstObserved(id, pluginObservation.ObservedAtUtc),
                        pluginObservation.ObservedAtUtc,
                        contextFingerprint,
                        pluginObservation.Fingerprint,
                        ["MO2 plugin observation", plugin.Id.Value]));
                }
            }
        }

        if (resolvedEnvironment?.Snapshot is { } environment)
        {
            if (environment.Context.GameId != context.GameId || environment.Context.InstallationId != context.InstallationId || environment.Context.ProfileId != context.ProfileId)
                throw new ArgumentException("Resolved-environment alerts must belong to the exact selected game, installation, and profile.", nameof(resolvedEnvironment));
            foreach (var discrepancy in environment.Discrepancies)
            {
                var id = Id(context, OfflineAlertSourceKind.ResolvedEnvironment, discrepancy.Kind.ToString(), discrepancy.Id.Value);
                Add(new(
                    id,
                    $"grid.environment.{discrepancy.Kind.ToString().ToLowerInvariant()}",
                    context.GameId,
                    context.InstallationId,
                    context.ProfileId,
                    OfflineAlertSourceKind.ResolvedEnvironment,
                    discrepancy.Id.Value,
                    discrepancy.Title,
                    discrepancy.Detail,
                    discrepancy.Severity switch
                    {
                        EnvironmentDiscrepancySeverity.Error => OfflineAlertSeverity.Error,
                        EnvironmentDiscrepancySeverity.Warning => OfflineAlertSeverity.Warning,
                        _ => OfflineAlertSeverity.Information,
                    },
                    OfflineAlertState.Active,
                    discrepancy.Severity == EnvironmentDiscrepancySeverity.Error,
                    FirstObserved(id, environment.Summary.ObservedAtUtc),
                    environment.Summary.ObservedAtUtc,
                    contextFingerprint,
                    environment.Summary.Fingerprint,
                    ["GRID resolved environment", environment.Id.Value, discrepancy.Id.Value]));
            }
        }

        if (previous is not null)
        {
            foreach (var old in previous.Alerts)
            {
                if (current.ContainsKey(old.Id))
                    continue;

                var sourceObservation = GetSourceObservation(old.SourceKind);
                var workspaceChanged = !string.Equals(previous.WorkspaceFingerprint, workspaceFingerprint, StringComparison.Ordinal);
                var nextState = sourceObservation switch
                {
                    SourceObservation.Complete => OfflineAlertState.Cleared,
                    SourceObservation.Incomplete => OfflineAlertState.Stale,
                    _ when workspaceChanged => OfflineAlertState.Stale,
                    _ => old.State,
                };
                current[old.Id] = old with
                {
                    State = nextState,
                    Detail = nextState == OfflineAlertState.Cleared
                        ? "This alert was not present in the latest complete observation."
                        : nextState == OfflineAlertState.Stale
                            ? "The latest source observation was incomplete, unavailable, or context-shifted; the prior alert is stale, not cleared."
                            : old.Detail,
                    ContextFingerprint = contextFingerprint,
                    LastObservedAtUtc = old.LastObservedAtUtc,
                };
            }
        }

        var alerts = current.Values
            .OrderByDescending(alert => alert.State == OfflineAlertState.Active && alert.IsBlocking)
            .ThenByDescending(alert => alert.Severity)
            .ThenBy(alert => alert.SourceKind)
            .ThenBy(alert => alert.Code, StringComparer.Ordinal)
            .ThenBy(alert => alert.Id, StringComparer.Ordinal)
            .ToImmutableArray();
        var fingerprint = Fingerprint(contextFingerprint, alerts);
        return new(SchemaVersion, context.GameId, context.InstallationId, context.ProfileId, now, workspaceFingerprint, contextFingerprint, alerts, fingerprint);

        void Add(OfflineAlert alert)
        {
            var retained = previous?.Alerts.FirstOrDefault(candidate => string.Equals(candidate.Id, alert.Id, StringComparison.Ordinal));
            if (retained is { State: OfflineAlertState.Acknowledged or OfflineAlertState.AccountedFor } &&
                string.Equals(retained.EvidenceFingerprint, alert.EvidenceFingerprint, StringComparison.Ordinal))
            {
                alert = alert with
                {
                    State = retained.State,
                    DispositionReason = retained.DispositionReason,
                };
            }
            if (!current.TryAdd(alert.Id, alert))
                throw new InvalidOperationException($"Duplicate offline alert identity '{alert.Id}'.");
        }

        DateTimeOffset FirstObserved(string id, DateTimeOffset fallback) =>
            previous?.Alerts.FirstOrDefault(alert => string.Equals(alert.Id, id, StringComparison.Ordinal))?.FirstObservedAtUtc
            ?? fallback.ToUniversalTime();

        SourceObservation GetSourceObservation(OfflineAlertSourceKind kind) => kind switch
        {
            OfflineAlertSourceKind.FidelityAudit => fidelityAudit is null
                ? SourceObservation.Absent
                : fidelityAudit.Status == FidelityAuditRunStatus.Completed ? SourceObservation.Complete : SourceObservation.Incomplete,
            OfflineAlertSourceKind.ToolOutput or OfflineAlertSourceKind.Executable or OfflineAlertSourceKind.GeneratedOutput => toolOutputs is null
                ? SourceObservation.Absent
                : toolOutputs.Status == ExternalObservationRefreshStatus.Completed ? SourceObservation.Complete : SourceObservation.Incomplete,
            OfflineAlertSourceKind.Profile or OfflineAlertSourceKind.Mod or OfflineAlertSourceKind.Plugin => profile is null
                ? SourceObservation.Absent
                : profile.Observation?.Status == ProfileObservationStatus.Complete ? SourceObservation.Complete : SourceObservation.Incomplete,
            OfflineAlertSourceKind.ResolvedEnvironment => resolvedEnvironment is null
                ? SourceObservation.Absent
                : resolvedEnvironment.Status == ResolvedEnvironmentRefreshStatus.Completed ? SourceObservation.Complete : SourceObservation.Incomplete,
            _ => SourceObservation.Incomplete,
        };
    }

    private enum SourceObservation
    {
        Absent,
        Complete,
        Incomplete,
    }

    private static OfflineAlertSeverity Severity(FidelityAuditItemStatus status) => status switch
    {
        FidelityAuditItemStatus.Failure => OfflineAlertSeverity.Error,
        FidelityAuditItemStatus.Warning => OfflineAlertSeverity.Warning,
        FidelityAuditItemStatus.Unsupported => OfflineAlertSeverity.Advisory,
        FidelityAuditItemStatus.Stale => OfflineAlertSeverity.Warning,
        _ => OfflineAlertSeverity.Information,
    };

    private static string Id(WorkspaceToolOutputContext context, OfflineAlertSourceKind source, string code, string target) =>
        $"alert.{Hash(string.Join('\n', context.GameId.Value, context.InstallationId.Value, context.ProfileId.Value, source, code, target))[..24].ToLowerInvariant()}";

    public string GetWorkspaceFingerprint(WorkspaceToolOutputContext context) => Hash(string.Join('\n',
        context.GameId.Value,
        context.InstallationId.Value,
        context.ProfileId.Value,
        context.CatalogRevision,
        context.ProfileSnapshotFingerprint ?? string.Empty,
        context.InventoryFingerprint ?? string.Empty));

    public bool IsValid(OfflineAlertIndex? index)
    {
        if (index is null || index.SchemaVersion != SchemaVersion ||
            string.IsNullOrWhiteSpace(index.WorkspaceFingerprint) ||
            string.IsNullOrWhiteSpace(index.ContextFingerprint) ||
            string.IsNullOrWhiteSpace(index.Fingerprint))
            return false;

        if (index.Alerts.Any(alert =>
                string.IsNullOrWhiteSpace(alert.Id) ||
                alert.GameId != index.GameId ||
                alert.InstallationId != index.InstallationId ||
                alert.ProfileId != index.ProfileId ||
                !string.Equals(alert.ContextFingerprint, index.ContextFingerprint, StringComparison.Ordinal) ||
                alert.Provenance.IsDefaultOrEmpty))
            return false;

        return string.Equals(index.Fingerprint, Fingerprint(index.ContextFingerprint, index.Alerts), StringComparison.Ordinal);
    }

    public OfflineAlertIndex RecalculateFingerprint(OfflineAlertIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index with { Fingerprint = Fingerprint(index.ContextFingerprint, index.Alerts) };
    }

    private static string Fingerprint(string contextFingerprint, ImmutableArray<OfflineAlert> alerts)
    {
        var semantic = alerts.Select(alert => new
        {
            alert.Id,
            alert.Code,
            SourceKind = alert.SourceKind.ToString(),
            alert.SourceIdentity,
            alert.Title,
            alert.Detail,
            Severity = alert.Severity.ToString(),
            State = alert.State.ToString(),
            alert.IsBlocking,
            alert.EvidenceFingerprint,
            alert.DispositionReason,
            Provenance = alert.Provenance.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
        });
        return Hash(contextFingerprint + "\n" + JsonSerializer.Serialize(semantic));
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
