using Grid.Core.Models;

namespace Grid.Core.Application;

public enum ShellRoute
{
    Home,
    GameWorkspace,
    History,
    Settings,
}

public sealed class ShellNavigationState
{
    public ShellNavigationState(GridCatalogSnapshot catalog, WorkspaceSelection? preferredSelection = null)
    {
        Workspace = new WorkspaceSelectionState(catalog, preferredSelection);
    }

    public WorkspaceSelectionState Workspace { get; }

    public ShellRoute CurrentRoute { get; private set; } = ShellRoute.Home;

    public WorkspaceSelection CurrentSelection => Workspace.Current;

    public ShellRoute NavigateHome() => SetRoute(ShellRoute.Home);

    public ShellRoute NavigateHistory() => SetRoute(ShellRoute.History);

    public ShellRoute NavigateSettings() => SetRoute(ShellRoute.Settings);

    public ShellRoute NavigateGame(GameId? gameId)
    {
        var isCurrentGame = gameId is not null && CurrentSelection.GameId == gameId;
        var gameExists = gameId is not null &&
            Workspace.Catalog.Games.Any(game => game.Id == gameId.Value);

        if (!isCurrentGame || !gameExists)
        {
            Workspace.SelectGame(gameExists ? gameId : null);
        }

        return SetRoute(Workspace.Current.GameId is null ? ShellRoute.Home : ShellRoute.GameWorkspace);
    }

    public WorkspaceSelection SelectInstallation(InstallationId? installationId) =>
        Workspace.SelectInstallation(installationId);

    public WorkspaceSelection SelectProfile(ProfileId? profileId) =>
        Workspace.SelectProfile(profileId);

    public WorkspaceSelection ReplaceCatalog(GridCatalogSnapshot catalog)
    {
        var selection = Workspace.ReplaceCatalog(catalog);
        if (CurrentRoute == ShellRoute.GameWorkspace && selection.GameId is null)
        {
            CurrentRoute = ShellRoute.Home;
        }

        return selection;
    }

    private ShellRoute SetRoute(ShellRoute route)
    {
        CurrentRoute = route;
        return route;
    }
}
