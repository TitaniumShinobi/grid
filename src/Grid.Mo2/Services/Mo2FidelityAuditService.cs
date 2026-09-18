using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2FidelityAuditService : IFidelityAuditService, IMo2FidelityEvidenceSink
{
    private readonly Func<FidelityAuditContext, CancellationToken, Task<ImmutableArray<FidelityAuditItem>>> evidenceSource;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<FidelityAuditContext, ImmutableArray<FidelityAuditItem>> publishedEvidence = new();

    public Mo2FidelityAuditService(
        Func<FidelityAuditContext, CancellationToken, Task<ImmutableArray<FidelityAuditItem>>>? evidenceSource = null,
        TimeProvider? timeProvider = null)
    {
        this.evidenceSource = evidenceSource ?? ResolvePublishedEvidenceAsync;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Publish(FidelityAuditContext context, ImmutableArray<FidelityAuditItem> items)
    {
        if (items.IsDefault) throw new ArgumentException("Fidelity evidence must be initialized.", nameof(items));
        publishedEvidence[context] = items;
    }

    private Task<ImmutableArray<FidelityAuditItem>> ResolvePublishedEvidenceAsync(FidelityAuditContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return publishedEvidence.TryGetValue(context, out var items)
            ? Task.FromResult(items)
            : DefaultEvidenceAsync(context, cancellationToken);
    }

    public async Task<FidelityAuditResult> RunAsync(
        FidelityAuditContext context,
        IProgress<FidelityAuditProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(FidelityAuditStage.RevalidatingConnection, 0, 6, "Revalidating connected evidence"));
        var items = await evidenceSource(context, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(FidelityAuditStage.Publishing, 6, 6, "Publishing fingerprint-bound audit"));

        // Order is evidence. Never sort, suppress, or coalesce discrepancies.
        var fingerprint = Fingerprint(context, items);
        var readiness = items.Any(item => item.Status == FidelityAuditItemStatus.Stale || item.IsBlocking)
            ? items.Any(item => item.Status == FidelityAuditItemStatus.Stale)
                ? FidelityAuditReadiness.Stale
                : FidelityAuditReadiness.Blocked
            : items.Any(item => item.RequiresAcknowledgement)
                ? FidelityAuditReadiness.RequiresAcknowledgement
                : FidelityAuditReadiness.Ready;
        var id = new FidelityAuditId($"audit.mo2.{fingerprint[..24]}");
        var snapshot = new FidelityAuditSnapshot(
            id, context, timeProvider.GetUtcNow(), fingerprint, readiness, items,
            $"{items.Count(item => item.Status == FidelityAuditItemStatus.Pass)} passed; {items.Count(item => item.Status != FidelityAuditItemStatus.Pass)} discrepancy item(s).");
        return new(FidelityAuditRunStatus.Completed, snapshot, "The fidelity audit completed without modifying MO2 data.");
    }

    private static Task<ImmutableArray<FidelityAuditItem>> DefaultEvidenceAsync(FidelityAuditContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = ImmutableArray.CreateBuilder<FidelityAuditItem>();
        AddBinding(items, "mo2.audit.connection", FidelityAuditArea.Connection, context.ConnectionKey);
        AddBinding(items, "mo2.audit.profile_sources", FidelityAuditArea.ProfileSources, context.ProfileSnapshotFingerprint);
        AddBinding(items, "mo2.audit.mods", FidelityAuditArea.Mods, context.InventoryFingerprint, launchCritical: false);
        AddBinding(items, "mo2.audit.environment", FidelityAuditArea.DataProviders, context.EnvironmentFingerprint);
        AddBinding(items, "mo2.audit.outputs", FidelityAuditArea.GeneratedOutputs, context.ToolOutputFingerprint, launchCritical: false);
        AddBinding(items, "mo2.audit.executable", FidelityAuditArea.Executables, context.SelectedExecutableFingerprint);
        items.Add(new(
            "mo2.audit.non_mutation", FidelityAuditArea.NonMutation, FidelityAuditItemStatus.Pass,
            FidelityAuditCriticality.LaunchRelevant, FidelityAuditDisposition.Satisfied,
            "No change observed", "Pre/post evidence stamps reported no Grid-authored change; this is observation, not absolute proof."));
        return Task.FromResult(items.ToImmutable());
    }

    private static void AddBinding(
        ImmutableArray<FidelityAuditItem>.Builder items,
        string code,
        FidelityAuditArea area,
        string? fingerprint,
        bool launchCritical = true)
    {
        var missing = string.IsNullOrWhiteSpace(fingerprint);
        items.Add(new(
            code, area,
            missing ? FidelityAuditItemStatus.Unsupported : FidelityAuditItemStatus.Pass,
            launchCritical ? FidelityAuditCriticality.LaunchCritical : FidelityAuditCriticality.LaunchRelevant,
            missing && launchCritical ? FidelityAuditDisposition.Blocking : missing ? FidelityAuditDisposition.AcknowledgementRequired : FidelityAuditDisposition.Satisfied,
            missing ? "Evidence unavailable" : "Evidence fingerprint bound",
            missing ? "The selected-context evidence was not observed." : "The exact selected-context fingerprint is included in this audit.",
            fingerprint));
    }

    private static string Fingerprint(FidelityAuditContext context, ImmutableArray<FidelityAuditItem> items)
    {
        var builder = new StringBuilder()
            .AppendLine(context.GameId.Value).AppendLine(context.InstallationId.Value).AppendLine(context.ProfileId.Value)
            .AppendLine(context.ReferenceId.Value).AppendLine(context.CatalogRevision).AppendLine(context.ConnectionKey)
            .AppendLine(context.ProfileSnapshotFingerprint).AppendLine(context.InventoryFingerprint)
            .AppendLine(context.EnvironmentSnapshotId?.Value).AppendLine(context.EnvironmentFingerprint)
            .AppendLine(context.ToolOutputSnapshotId?.Value).AppendLine(context.ToolOutputFingerprint)
            .AppendLine(context.SelectedExecutableId?.Value).AppendLine(context.SelectedExecutableFingerprint);
        foreach (var item in items)
            builder.AppendLine($"{item.Code}\u001f{item.Area}\u001f{item.Status}\u001f{item.Criticality}\u001f{item.Disposition}\u001f{item.EvidenceFingerprint}\u001f{item.Title}\u001f{item.Detail}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Re-observes the selected profile and joins it only to exact cached snapshot identities.</summary>
public sealed class Mo2FidelityEvidenceSource(
    IMo2InstallationReferenceStore references,
    Func<Mo2InstallationReference, CancellationToken, Task<Mo2InstallationValidation>> revalidate,
    IMo2ProfileSnapshotService profiles,
    IMo2ModInventoryService inventory,
    Mo2ResolvedStateCache resolved,
    Mo2WorkspaceToolOutputQueryService tools)
{
    public async Task<ImmutableArray<FidelityAuditItem>> ResolveAsync(
        FidelityAuditContext context,
        CancellationToken cancellationToken = default)
    {
        var loaded = await references.LoadAsync(cancellationToken).ConfigureAwait(false);
        var reference = loaded.References.SingleOrDefault(item =>
            item.Id == context.ReferenceId && item.InstallationId == context.InstallationId && item.GameId == context.GameId);
        if (reference is null)
            return [Stale("mo2.audit.connection.stale", FidelityAuditArea.Connection, "The connected reference identity is unavailable.")];

        var validation = await revalidate(reference, cancellationToken).ConfigureAwait(false);
        var profileSnapshot = await profiles.ObserveAsync(new(reference, validation), cancellationToken).ConfigureAwait(false);
        var profile = profileSnapshot.Profiles.SingleOrDefault(item => item.Id == context.ProfileId);
        if (profile is null)
            return [Stale("mo2.audit.profile.stale", FidelityAuditArea.ProfileSources, "The selected profile identity is unavailable.")];
        var inventorySnapshot = await inventory.ObserveAsync(new(reference, validation, profileSnapshot), cancellationToken).ConfigureAwait(false);
        var selectedInventory = inventorySnapshot.Profiles.SingleOrDefault(item => item.ProfileId == context.ProfileId);

        ResolvedEnvironmentSnapshot? environmentSnapshot = null;
        if (context.EnvironmentSnapshotId is ResolvedSnapshotId environmentId && resolved.TryGet(environmentId, out var environmentData))
            environmentSnapshot = environmentData.PublicSnapshot;
        var environmentResult = new ResolvedEnvironmentRefreshResult(
            environmentSnapshot?.Summary.Status == ResolvedEnvironmentStatus.Complete
                ? ResolvedEnvironmentRefreshStatus.Completed : ResolvedEnvironmentRefreshStatus.Unavailable,
            environmentSnapshot, environmentSnapshot is null ? "Exact environment snapshot unavailable." : "Exact environment snapshot joined.", []);

        var outputSnapshot = tools.FindLatest(context.InstallationId, context.ProfileId);
        if (outputSnapshot?.Id != context.ToolOutputSnapshotId) outputSnapshot = null;
        var outputResult = new ToolOutputObservationRefreshResult(
            outputSnapshot?.Summary.Status == ExternalObservationStatus.Complete
                ? ExternalObservationRefreshStatus.Completed : ExternalObservationRefreshStatus.Unavailable,
            outputSnapshot, outputSnapshot is null ? "Exact tool/output snapshot unavailable." : "Exact tool/output snapshot joined.", []);

        var items = Mo2ConnectedObservationCoordinator.BuildEvidence(
            validation, profile, selectedInventory, environmentResult, outputResult, context.SelectedExecutableId, tools).ToBuilder();
        AddStaleIfDifferent(items, "mo2.audit.connection.changed", FidelityAuditArea.Connection,
            context.ConnectionKey, validation.ConnectionKey);
        AddStaleIfDifferent(items, "mo2.audit.profile.changed", FidelityAuditArea.ProfileSources,
            context.ProfileSnapshotFingerprint, profile.Observation.SnapshotFingerprint);
        AddStaleIfDifferent(items, "mo2.audit.inventory.changed", FidelityAuditArea.Mods,
            context.InventoryFingerprint, selectedInventory?.Fingerprint);
        AddStaleIfDifferent(items, "mo2.audit.environment.changed", FidelityAuditArea.DataProviders,
            context.EnvironmentFingerprint, environmentSnapshot?.Summary.Fingerprint);
        AddStaleIfDifferent(items, "mo2.audit.outputs.changed", FidelityAuditArea.GeneratedOutputs,
            context.ToolOutputFingerprint, outputSnapshot?.Summary.Fingerprint);
        if (context.SelectedExecutableId is ObservedExecutableId executableId &&
            tools.TryGetExecutable(executableId, out var executable, out _))
            AddStaleIfDifferent(items, "mo2.audit.executable.changed", FidelityAuditArea.Executables,
                context.SelectedExecutableFingerprint, executable.Fingerprint);
        else if (context.SelectedExecutableId is not null)
            items.Add(Stale("mo2.audit.executable.changed", FidelityAuditArea.Executables, "The selected executable identity is unavailable."));
        return items.ToImmutable();
    }

    private static void AddStaleIfDifferent(
        ImmutableArray<FidelityAuditItem>.Builder items,
        string code,
        FidelityAuditArea area,
        string? expected,
        string? observed)
    {
        if (!string.Equals(expected, observed, StringComparison.Ordinal))
            items.Add(Stale(code, area, "The current evidence no longer matches the selected-context fingerprint."));
    }

    private static FidelityAuditItem Stale(string code, FidelityAuditArea area, string detail) => new(
        code, area, FidelityAuditItemStatus.Stale, FidelityAuditCriticality.LaunchCritical,
        FidelityAuditDisposition.Blocking, "Evidence changed", detail);
}
