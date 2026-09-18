using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

/// <summary>
/// Composes framework-neutral application state around one authoritative shell.
/// Infrastructure services retain all filesystem and process authority.
/// </summary>
public sealed class GridApplicationSession : IDisposable
{
    private bool disposed;

    public GridApplicationSession(
        GridCatalogSnapshot catalog,
        IUserHistoryStore historyStore,
        IWorkspaceToolOutputQueryService? toolOutputService = null,
        IWorkspaceEnvironmentQueryService? environmentService = null,
        IFidelityAuditService? fidelityAuditService = null,
        IWorkspaceLaunchService? launchService = null,
        TimeProvider? timeProvider = null,
        IEnumerable<AssistantClassOption>? assistantClasses = null,
        IEnumerable<AssistantToolOption>? assistantTools = null,
        IAssistantRequestExecutionService? assistantExecutionService = null,
        WorkspaceSelection? initialSelection = null,
        IOfflineAlertIndexStore? offlineAlertIndexStore = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(historyStore);
        if ((fidelityAuditService is null) != (launchService is null))
        {
            throw new ArgumentException("Fidelity-audit and external-launch services must be supplied together.");
        }

        Shell = new ShellNavigationState(catalog, initialSelection);
        Workspace = new WorkspaceSessionState(Shell);
        LaunchTargets = new LaunchTargetSelectionState(Shell);
        Environment = environmentService is null ? null : new WorkspaceEnvironmentState(environmentService);
        ToolOutputs = toolOutputService is null ? null : new WorkspaceToolOutputState(toolOutputService);
        OfflineAlerts = offlineAlertIndexStore is null ? null : new OfflineAlertIndexState(offlineAlertIndexStore, timeProvider);
        FidelityAudit = fidelityAuditService is null ? null : new FidelityAuditState(fidelityAuditService);
        ExternalLaunch = launchService is null ? null : new WorkspaceLaunchState(launchService, timeProvider);
        SynchronizeEvidenceContexts();
        Context = new ApplicationContextState(Shell, Workspace, LaunchTargets, ToolOutputs);
        Assistant = new AssistantSessionState(Context.Current, catalog, assistantClasses ?? [], assistantTools, assistantExecutionService, timeProvider);
        History = new HistoryState(historyStore);
    }

    public ShellNavigationState Shell { get; }

    public WorkspaceSessionState Workspace { get; }

    public LaunchTargetSelectionState LaunchTargets { get; }

    public WorkspaceEnvironmentState? Environment { get; }

    public WorkspaceToolOutputState? ToolOutputs { get; }

    public OfflineAlertIndexState? OfflineAlerts { get; }

    public FidelityAuditState? FidelityAudit { get; }

    public WorkspaceLaunchState? ExternalLaunch { get; }

    public ApplicationContextState Context { get; }

    public AssistantSessionState Assistant { get; }

    public HistoryState History { get; }

    public GridCatalogSnapshot Catalog => Shell.Workspace.Catalog;

    public ApplicationContextSnapshot SynchronizeContext()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Workspace.SynchronizeContext();
        LaunchTargets.SynchronizeContext();
        SynchronizeEvidenceContexts();
        SynchronizeAuditAndLaunchContexts();
        var context = Context.Synchronize();
        Assistant.SetContext(context);
        return context;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Environment?.Dispose();
        ToolOutputs?.Dispose();
        FidelityAudit?.Dispose();
        ExternalLaunch?.Dispose();
    }

    private void SynchronizeEvidenceContexts()
    {
        var resolved = ResolveCurrentContext();
        if (resolved is null)
        {
            Environment?.SetContext(null);
            ToolOutputs?.SetContext(null);
            OfflineAlerts?.SetContext(null);
            return;
        }

        var (game, installation, profile, referenceId, observation) = resolved.Value;
        Environment?.SetContext(new WorkspaceEnvironmentContext(
            game.Id,
            installation.Id,
            profile.Id,
            Catalog.Revision,
            observation.Inventory?.Fingerprint));
        var toolOutputContext = new WorkspaceToolOutputContext(
            game.Id,
            installation.Id,
            profile.Id,
            referenceId,
            Catalog.Revision,
            observation.SnapshotFingerprint,
            observation.Inventory?.Fingerprint);
        ToolOutputs?.SetContext(toolOutputContext);
        OfflineAlerts?.SetContext(toolOutputContext);
    }

    private void SynchronizeAuditAndLaunchContexts()
    {
        if (FidelityAudit is null || ExternalLaunch is null)
        {
            return;
        }

        var resolved = ResolveCurrentContext();
        var executable = ToolOutputs?.GetSelectedExecutable();
        if (resolved is null ||
            string.IsNullOrWhiteSpace(resolved.Value.Installation.Metadata.ConnectionFingerprint))
        {
            FidelityAudit.SetContext(null);
            ExternalLaunch.SetIntent(null);
            return;
        }

        var (game, installation, profile, referenceId, observation) = resolved.Value;
        var context = new FidelityAuditContext(
            game.Id,
            installation.Id,
            profile.Id,
            referenceId,
            Catalog.Revision,
            installation.Metadata.ConnectionFingerprint!,
            observation.SnapshotFingerprint,
            observation.Inventory?.Fingerprint,
            Environment?.Snapshot?.Id,
            Environment?.Snapshot?.Summary.Fingerprint,
            ToolOutputs?.Snapshot?.Id,
            ToolOutputs?.Snapshot?.Summary.Fingerprint,
            executable?.Id,
            executable?.Fingerprint);
        FidelityAudit.SetContext(context);

        var audit = FidelityAudit.Snapshot;
        ExternalLaunch.SetIntent(
            executable is null ||
            audit is null ||
            FidelityAudit.IsCurrentSnapshotStale ||
            audit.Context != context
                ? null
                : new ExternalLaunchIntent(
                    game.Id,
                    installation.Id,
                    profile.Id,
                    referenceId,
                    executable.Id,
                    executable.Fingerprint,
                    audit.Id,
                    audit.Fingerprint));
    }

    private (ManagedGame Game, ManagedInstallation Installation, Profile Profile, InstallationReferenceId ReferenceId, ProfileObservationSummary Observation)? ResolveCurrentContext()
    {
        var selection = Shell.CurrentSelection;
        var game = selection.GameId is GameId gameId
            ? Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId)
            : null;
        var installation = game is not null && selection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(candidate => candidate.Id == installationId)
            : null;
        var profile = installation is not null && selection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(candidate => candidate.Id == profileId)
            : null;

        return game is null ||
            installation?.Metadata.Provenance != InstallationProvenanceKind.ConnectedReference ||
            installation.Metadata.ReferenceId is not InstallationReferenceId referenceId ||
            profile?.Observation is not ProfileObservationSummary observation
                ? null
                : (game, installation, profile, referenceId, observation);
    }
}
