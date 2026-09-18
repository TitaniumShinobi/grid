using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public sealed class ApplicationContextState
{
    private readonly ShellNavigationState shell;
    private readonly WorkspaceSessionState workspace;
    private readonly LaunchTargetSelectionState launchTargets;
    private readonly WorkspaceToolOutputState? toolOutputs;

    public ApplicationContextState(
        ShellNavigationState shell,
        WorkspaceSessionState workspace,
        LaunchTargetSelectionState launchTargets,
        WorkspaceToolOutputState? toolOutputs = null)
    {
        this.shell = shell ?? throw new ArgumentNullException(nameof(shell));
        this.workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        this.launchTargets = launchTargets ?? throw new ArgumentNullException(nameof(launchTargets));
        this.toolOutputs = toolOutputs;
        Current = Build();
    }

    public ApplicationContextSnapshot Current { get; private set; }

    public DiagnosticsSelectionId? DiagnosticsSelection { get; private set; }

    public ApplicationContextSnapshot Synchronize()
    {
        workspace.SynchronizeContext();
        launchTargets.SynchronizeContext();
        Current = Build();
        return Current;
    }

    public void SelectDiagnostics(DiagnosticsSelectionId? selection)
    {
        DiagnosticsSelection = selection;
        Current = Build();
    }

    private ApplicationContextSnapshot Build()
    {
        var surface = shell.CurrentRoute switch
        {
            ShellRoute.GameWorkspace => ApplicationSurface.GameWorkspace,
            ShellRoute.History => ApplicationSurface.History,
            ShellRoute.Settings => ApplicationSurface.Settings,
            _ => ApplicationSurface.Home,
        };
        if (surface != ApplicationSurface.GameWorkspace)
        {
            return new ApplicationContextSnapshot(
                surface,
                null,
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                surface == ApplicationSurface.Settings ? DiagnosticsSelection : null);
        }

        var selection = shell.CurrentSelection;
        ArchiveId? archiveId = null;
        VirtualPathId? virtualPathId = null;
        if (workspace.SelectedResolvedEnvironment is { } resolved)
        {
            if (resolved.Kind == ResolvedSelectionKind.Archive)
            {
                archiveId = new ArchiveId(resolved.StableId);
            }
            else if (resolved.Kind == ResolvedSelectionKind.VirtualPath)
            {
                virtualPathId = new VirtualPathId(resolved.StableId);
            }
        }

        return new ApplicationContextSnapshot(
            surface,
            selection.GameId,
            selection.InstallationId,
            selection.ProfileId,
            workspace.SelectedModIds.OrderBy(id => id.Value, StringComparer.Ordinal).ToImmutableArray(),
            workspace.SelectedPluginId,
            workspace.SelectedEnvironmentTab,
            workspace.SelectedEnvironmentEntryId,
            archiveId,
            virtualPathId,
            launchTargets.SelectedTargetId,
            toolOutputs?.SelectedExecutableId,
            null);
    }
}
