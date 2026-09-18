using Grid.Core.Models;

namespace Grid.Core.Application;

public sealed class WorkspaceSelectionState
{
    public WorkspaceSelectionState(GridCatalogSnapshot catalog, WorkspaceSelection? preferredSelection = null)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        Current = preferredSelection is WorkspaceSelection preferred
            ? RestorePreferred(catalog, preferred)
            : SelectFirst(catalog);
    }

    public GridCatalogSnapshot Catalog { get; private set; }

    public WorkspaceSelection Current { get; private set; }

    public WorkspaceSelection SelectGame(GameId? gameId)
    {
        if (gameId is null)
        {
            return Set(WorkspaceSelection.Empty);
        }

        var game = Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId.Value);
        return Set(game is null ? WorkspaceSelection.Empty : SelectFirst(game));
    }

    public WorkspaceSelection SelectInstallation(InstallationId? installationId)
    {
        if (Current.GameId is null)
        {
            return Set(WorkspaceSelection.Empty);
        }

        var game = FindGame(Current.GameId.Value);
        if (game is null || installationId is null)
        {
            return Set(new WorkspaceSelection(Current.GameId, null, null));
        }

        var installation = game.Installations.FirstOrDefault(candidate => candidate.Id == installationId.Value);
        return Set(installation is null
            ? new WorkspaceSelection(game.Id, null, null)
            : SelectFirst(game.Id, installation));
    }

    public WorkspaceSelection SelectProfile(ProfileId? profileId)
    {
        if (Current.GameId is null || Current.InstallationId is null)
        {
            return Set(new WorkspaceSelection(Current.GameId, Current.InstallationId, null));
        }

        var game = FindGame(Current.GameId.Value);
        var installation = game?.Installations.FirstOrDefault(candidate => candidate.Id == Current.InstallationId.Value);
        if (installation is null ||
            installation.Metadata.Availability != InstallationAvailability.Available ||
            profileId is null)
        {
            return Set(new WorkspaceSelection(Current.GameId, Current.InstallationId, null));
        }

        var profile = installation.Profiles.FirstOrDefault(candidate =>
            candidate.Id == profileId.Value &&
            candidate.Lifecycle == ProfileLifecycleState.Available);
        return Set(new WorkspaceSelection(game!.Id, installation.Id, profile?.Id));
    }

    public WorkspaceSelection ReplaceCatalog(GridCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;

        if (Current.GameId is null)
        {
            return Set(SelectFirst(catalog));
        }

        var game = FindGame(Current.GameId.Value);
        if (game is null)
        {
            return Set(SelectFirst(catalog));
        }

        if (Current.InstallationId is null)
        {
            return Set(SelectFirst(game));
        }

        var installation = game.Installations.FirstOrDefault(candidate => candidate.Id == Current.InstallationId.Value);
        if (installation is null)
        {
            return Set(SelectFirst(game));
        }

        if (Current.ProfileId is not null &&
            installation.Metadata.Availability == InstallationAvailability.Available &&
            installation.Profiles.Any(candidate =>
                candidate.Id == Current.ProfileId.Value &&
                candidate.Lifecycle == ProfileLifecycleState.Available))
        {
            return Set(Current);
        }

        return Set(SelectFirst(game.Id, installation));
    }

    private static WorkspaceSelection RestorePreferred(GridCatalogSnapshot catalog, WorkspaceSelection preferred)
    {
        if (preferred.GameId is not GameId gameId)
        {
            return SelectFirst(catalog);
        }

        var game = catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId);
        if (game is null)
        {
            return SelectFirst(catalog);
        }

        if (preferred.InstallationId is not InstallationId installationId)
        {
            return new WorkspaceSelection(game.Id, null, null);
        }

        var installation = game.Installations.FirstOrDefault(candidate => candidate.Id == installationId);
        if (installation is null)
        {
            return SelectFirst(game);
        }

        if (installation.Metadata.Availability != InstallationAvailability.Available)
        {
            return new WorkspaceSelection(game.Id, installation.Id, null);
        }

        if (preferred.ProfileId is not ProfileId profileId)
        {
            return new WorkspaceSelection(game.Id, installation.Id, null);
        }

        var profile = installation.Profiles.FirstOrDefault(candidate =>
            candidate.Id == profileId &&
            candidate.Lifecycle == ProfileLifecycleState.Available);
        return profile is null
            ? SelectFirst(game.Id, installation)
            : new WorkspaceSelection(game.Id, installation.Id, profile.Id);
    }

    private ManagedGame? FindGame(GameId gameId) =>
        Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId);

    private WorkspaceSelection Set(WorkspaceSelection selection)
    {
        Current = selection;
        return selection;
    }

    private static WorkspaceSelection SelectFirst(GridCatalogSnapshot catalog) =>
        catalog.Games.IsEmpty ? WorkspaceSelection.Empty : SelectFirst(catalog.Games[0]);

    private static WorkspaceSelection SelectFirst(ManagedGame game) =>
        game.Installations.IsEmpty
            ? new WorkspaceSelection(game.Id, null, null)
            : SelectFirst(
                game.Id,
                game.Installations.FirstOrDefault(installation =>
                    installation.Metadata.Availability == InstallationAvailability.Available) ?? game.Installations[0]);

    private static WorkspaceSelection SelectFirst(GameId gameId, ManagedInstallation installation)
    {
        if (installation.Metadata.Availability != InstallationAvailability.Available)
        {
            return new WorkspaceSelection(gameId, installation.Id, null);
        }

        var profile = installation.Profiles.FirstOrDefault(candidate =>
            candidate.Lifecycle == ProfileLifecycleState.Available);
        return new WorkspaceSelection(gameId, installation.Id, profile?.Id);
    }
}
