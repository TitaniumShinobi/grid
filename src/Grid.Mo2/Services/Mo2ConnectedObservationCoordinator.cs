using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>Observes one selected profile coherently; it never fans tool/output work across the catalog.</summary>
public sealed class Mo2ConnectedObservationCoordinator(
    IMo2ProfileSnapshotService profiles,
    IMo2ModInventoryService inventory,
    IWorkspaceEnvironmentQueryService environment,
    Mo2WorkspaceToolOutputQueryService toolOutputs,
    IFidelityAuditService audits,
    IMo2FidelityEvidenceSink evidenceSink)
{
    private Mo2ConnectedObservation? last;

    public async Task<Mo2ConnectedObservation> RefreshAsync(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        ProfileId selectedProfileId,
        string catalogRevision,
        ObservedExecutableId? executableId,
        string? executableFingerprint,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var profileSnapshot = await profiles.ObserveAsync(new(reference, validation), cancellationToken).ConfigureAwait(false);
            var selected = profileSnapshot.Profiles.SingleOrDefault(profile => profile.Id == selectedProfileId)
                ?? throw new InvalidOperationException("The selected MO2 profile is no longer present.");
            var inventorySnapshot = await inventory.ObserveAsync(new(reference, validation, profileSnapshot), cancellationToken).ConfigureAwait(false);
            var selectedInventory = inventorySnapshot.Profiles.SingleOrDefault(value => value.ProfileId == selectedProfileId);
            var inventoryFingerprint = selectedInventory?.Fingerprint;
            var environmentResult = await environment.RefreshAsync(
                new(reference.GameId, reference.InstallationId, selectedProfileId, catalogRevision, inventoryFingerprint),
                forceRefresh: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var outputResult = await toolOutputs.RefreshAsync(
                new(reference.GameId, reference.InstallationId, selectedProfileId, reference.Id, catalogRevision,
                    selected.Observation.SnapshotFingerprint, inventoryFingerprint),
                forceRefresh: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            var auditContext = new FidelityAuditContext(
                reference.GameId, reference.InstallationId, selectedProfileId, reference.Id, catalogRevision,
                validation.ConnectionKey ?? string.Empty, selected.Observation.SnapshotFingerprint, inventoryFingerprint,
                environmentResult.Snapshot?.Id, environmentResult.Snapshot?.Summary.Fingerprint,
                outputResult.Snapshot?.Id, outputResult.Snapshot?.Summary.Fingerprint, executableId, executableFingerprint);
            evidenceSink.Publish(auditContext, BuildEvidence(validation, selected, selectedInventory,
                environmentResult, outputResult, executableId, toolOutputs));
            var audit = await audits.RunAsync(auditContext, cancellationToken: cancellationToken).ConfigureAwait(false);
            last = new(reference, validation, profileSnapshot, selected, inventorySnapshot, environmentResult, outputResult, audit,
                IsStale: false, "Selected MO2 context observed once and fidelity-audited.");
            return last;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (last is not null)
        {
            return last with { IsStale = true, Detail = $"Refresh failed safely; prior evidence is stale ({exception.GetType().Name})." };
        }
    }

    internal static System.Collections.Immutable.ImmutableArray<FidelityAuditItem> BuildEvidence(
        Mo2InstallationValidation validation,
        Mo2ObservedProfile profile,
        Mo2ProfileModInventory? inventory,
        ResolvedEnvironmentRefreshResult environment,
        ToolOutputObservationRefreshResult outputs,
        ObservedExecutableId? executableId,
        Mo2WorkspaceToolOutputQueryService rawTools)
    {
        var items = System.Collections.Immutable.ImmutableArray.CreateBuilder<FidelityAuditItem>();
        items.Add(Item("mo2.audit.connection", FidelityAuditArea.Connection,
            validation.CanConnect ? FidelityAuditItemStatus.Pass : FidelityAuditItemStatus.Failure,
            "Connected reference validation", validation.CanConnect ? "The connected reference revalidated." : "The connected reference did not revalidate.",
            critical: true));
        items.Add(Item("mo2.audit.active_profile", FidelityAuditArea.ActiveProfile,
            profile.ManagerState == ManagerProfileState.Active ? FidelityAuditItemStatus.Pass : FidelityAuditItemStatus.Failure,
            "ACTIVE IN MO2", profile.ManagerState == ManagerProfileState.Active ? "The selected profile is unambiguously active in MO2." : "The selected profile is not freshly active in MO2.",
            critical: true, fingerprint: profile.Observation.SnapshotFingerprint));

        foreach (var source in profile.Sources)
        {
            var area = SourceArea(source.Name);
            var status = source.Availability == ProfileSourceAvailability.ChangedDuringRead ? FidelityAuditItemStatus.Stale
                : source.Availability is ProfileSourceAvailability.Read or ProfileSourceAvailability.OptionalAbsent && source.ParseStatus == ProfileSourceParseStatus.Parsed
                    ? FidelityAuditItemStatus.Pass
                    : source.ParseStatus == ProfileSourceParseStatus.UnsupportedSyntax ? FidelityAuditItemStatus.Unsupported
                    : source.Availability == ProfileSourceAvailability.OptionalAbsent ? FidelityAuditItemStatus.Pass
                    : FidelityAuditItemStatus.Failure;
            items.Add(Item($"mo2.audit.source.{items.Count}", area, status, source.Name,
                $"{source.Availability}; {source.ParseStatus}; {source.Warnings.Length} warning(s).",
                critical: area is FidelityAuditArea.ProfileSources or FidelityAuditArea.Plugins,
                fingerprint: source.RawFingerprint));
        }

        items.Add(Item("mo2.audit.inventory", FidelityAuditArea.Mods,
            inventory?.Status == Mo2InventoryObservationStatus.Complete ? FidelityAuditItemStatus.Pass : FidelityAuditItemStatus.Failure,
            "Mod inventory", inventory is null ? "Selected-profile inventory was not observed." : $"{inventory.Entries.Length} row(s), {inventory.Warnings.Length} warning(s), exact order preserved.",
            critical: true, fingerprint: inventory?.Fingerprint));

        var environmentStatus = environment.Status == ResolvedEnvironmentRefreshStatus.Completed ? FidelityAuditItemStatus.Pass
            : environment.Status == ResolvedEnvironmentRefreshStatus.Partial ? FidelityAuditItemStatus.Warning
            : FidelityAuditItemStatus.Failure;
        var summary = environment.Snapshot?.Summary;
        items.Add(Item("mo2.audit.plugins", FidelityAuditArea.Plugins, environmentStatus, "Resolved plugins",
            summary is null ? "Not observed." : $"{summary.PluginCount} plugin(s).", true, summary?.Fingerprint));
        items.Add(Item("mo2.audit.archives", FidelityAuditArea.Archives, environmentStatus, "Resolved archives",
            summary is null ? "Not observed." : $"{summary.ArchiveCount} archive(s).", true, summary?.Fingerprint));
        items.Add(Item("mo2.audit.data", FidelityAuditArea.DataProviders, environmentStatus, "Resolved Data providers",
            summary is null ? "Not observed." : $"{summary.VirtualPathCount} path(s), {summary.ProviderCount} provider(s).", true, summary?.Fingerprint));
        foreach (var discrepancy in environment.Snapshot?.Discrepancies ?? [])
        {
            var status = discrepancy.Severity == EnvironmentDiscrepancySeverity.Error ? FidelityAuditItemStatus.Failure
                : discrepancy.Kind == EnvironmentDiscrepancyKind.UnsupportedFormat ? FidelityAuditItemStatus.Unsupported
                : FidelityAuditItemStatus.Warning;
            items.Add(Item($"mo2.audit.environment.{discrepancy.Id.Value}", FidelityAuditArea.DataProviders, status,
                discrepancy.Title, discrepancy.Detail, critical: discrepancy.Severity == EnvironmentDiscrepancySeverity.Error));
        }

        var outputStatus = outputs.Status == ExternalObservationRefreshStatus.Completed ? FidelityAuditItemStatus.Pass
            : outputs.Status == ExternalObservationRefreshStatus.Partial ? FidelityAuditItemStatus.Warning
            : FidelityAuditItemStatus.Failure;
        items.Add(Item("mo2.audit.outputs", FidelityAuditArea.GeneratedOutputs, outputStatus, "Generated outputs",
            outputs.Snapshot is null ? "Not observed." : $"{outputs.Snapshot.Outputs.Length} output(s), {outputs.Snapshot.Warnings.Length} warning(s).",
            critical: false, fingerprint: outputs.Snapshot?.Summary.Fingerprint));
        var overwriteCount = outputs.Snapshot?.Outputs.Count(output => output.LocationKind == GeneratedOutputLocationKind.Overwrite) ?? 0;
        items.Add(Item("mo2.audit.overwrite", FidelityAuditArea.Overwrite, outputStatus, "Overwrite",
            outputs.Snapshot is null ? "Not observed." : $"{overwriteCount} Overwrite observation(s).", critical: false,
            fingerprint: outputs.Snapshot?.Summary.Fingerprint));

        Mo2ExecutableSourceProvenance? executableProvenance = null;
        if (executableId is ObservedExecutableId id && rawTools.TryGetExecutable(id, out var executable, out executableProvenance))
            items.Add(Item("mo2.audit.executable", FidelityAuditArea.Executables,
                executable.IsDuplicate || executable.Availability != Mo2ExecutablePathAvailability.Available ? FidelityAuditItemStatus.Failure : FidelityAuditItemStatus.Pass,
                "Selected executable", $"Source index {executable.SourceIndex}; exact configured title and fingerprint retained.", true, executable.Fingerprint));
        else
            items.Add(Item("mo2.audit.executable", FidelityAuditArea.Executables, FidelityAuditItemStatus.Unsupported,
                "Selected executable", "The selected executable evidence was not available.", true));

        var unchangedSources = profile.Sources.All(source => source.Before is null || source.After is null || source.Before == source.After);
        var unchangedExecutable = executableProvenance is null || executableProvenance.Before == executableProvenance.After;
        items.Add(Item("mo2.audit.non_mutation", FidelityAuditArea.NonMutation,
            unchangedSources && unchangedExecutable ? FidelityAuditItemStatus.Pass : FidelityAuditItemStatus.Stale,
            "No change observed", unchangedSources && unchangedExecutable
                ? "Pre/post source stamps report no observed change; this is not absolute proof."
                : "At least one pre/post source stamp changed during observation.", true));
        return items.ToImmutable();
    }

    private static FidelityAuditArea SourceArea(string name) => name.ToLowerInvariant() switch
    {
        "plugins.txt" or "loadorder.txt" => FidelityAuditArea.Plugins,
        "skyrim.ini" or "skyrimprefs.ini" or "profile settings" => FidelityAuditArea.ProfileSettings,
        "saves" => FidelityAuditArea.Saves,
        _ => FidelityAuditArea.ProfileSources,
    };

    private static FidelityAuditItem Item(string code, FidelityAuditArea area, FidelityAuditItemStatus status,
        string title, string detail, bool critical, string? fingerprint = null)
    {
        var criticality = critical ? FidelityAuditCriticality.LaunchCritical : FidelityAuditCriticality.LaunchRelevant;
        var disposition = status is FidelityAuditItemStatus.Failure or FidelityAuditItemStatus.Stale ||
            status == FidelityAuditItemStatus.Unsupported && critical
                ? FidelityAuditDisposition.Blocking
                : status is FidelityAuditItemStatus.Warning or FidelityAuditItemStatus.Unsupported
                    ? FidelityAuditDisposition.AcknowledgementRequired : FidelityAuditDisposition.Satisfied;
        return new(code, area, status, criticality, disposition, title, detail, fingerprint);
    }
}
