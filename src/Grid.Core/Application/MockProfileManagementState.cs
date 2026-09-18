using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public enum ProfileManagementFailure
{
    None,
    InvalidName,
    DuplicateName,
    InstallationNotFound,
    ProfileNotFound,
    ProfilesUnsupported,
    InstallationUnavailable,
    AlreadyArchived,
    ActiveProfile,
    FinalProfile,
    FinalAvailableProfile,
}

public readonly record struct ProfileManagementResult(
    bool Succeeded,
    ProfileManagementFailure Failure,
    ProfileId? AffectedProfileId)
{
    public static ProfileManagementResult Applied(ProfileId profileId) =>
        new(true, ProfileManagementFailure.None, profileId);

    public static ProfileManagementResult Rejected(ProfileManagementFailure failure) =>
        new(false, failure, null);
}

public sealed class MockProfileManagementState
{
    public const int MaximumProfileNameLength = 80;

    private readonly string _baseRevision;
    private int _nextProfileNumber = 1;
    private int _revisionNumber;

    public MockProfileManagementState(ShellNavigationState shell)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _baseRevision = shell.Workspace.Catalog.Revision;
    }

    public ShellNavigationState Shell { get; }

    public GridCatalogSnapshot Catalog => Shell.Workspace.Catalog;

    public ProfileManagementResult CreateProfile(InstallationId installationId, string? requestedName)
    {
        var contextFailure = ResolveInstallation(installationId, out var game, out var installation);
        if (contextFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(contextFailure);
        }

        var nameFailure = NormalizeName(installation!, requestedName, null, out var name);
        if (nameFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(nameFailure);
        }

        var profileId = CreateProfileId();
        LaunchTargetId? defaultTargetId = installation!.LaunchTargetConfigurations
            .Where(configuration => configuration.Availability != AvailabilityState.Unavailable)
            .Where(configuration => game!.ToolCatalog.LaunchTargets.Any(definition =>
                definition.Id == configuration.LaunchTargetId &&
                definition.AdapterIds.Contains(installation.AdapterId)))
            .Select(configuration => (LaunchTargetId?)configuration.LaunchTargetId)
            .FirstOrDefault();
        var profile = new Profile(
            profileId,
            installation.Id,
            name!,
            ImmutableArray<ModEntry>.Empty,
            ImmutableArray<PluginEntry>.Empty,
            new HealthSummary(HealthLevel.Unknown, "New mock profile", ImmutableArray<Advisory>.Empty),
            installation.SupportedProfileFeatures,
            ProfileLifecycleState.Available,
            ImmutableArray<EnvironmentEntry>.Empty,
            defaultTargetId,
            new ProfileLaunchReadiness(
                ProfileValidationState.Unknown,
                PendingChangeState.Unknown,
                PendingChangeState.Unknown,
                OnlineCleanVerificationState.NotApplicable));

        Apply(game!, installation with { Profiles = installation.Profiles.Add(profile) });
        Shell.SelectProfile(profileId);
        return ProfileManagementResult.Applied(profileId);
    }

    public ProfileManagementResult DuplicateProfile(ProfileId sourceProfileId, string? requestedName)
    {
        var profileFailure = ResolveProfile(sourceProfileId, out var game, out var installation, out var source);
        if (profileFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(profileFailure);
        }

        var nameFailure = NormalizeName(installation!, requestedName, null, out var name);
        if (nameFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(nameFailure);
        }

        var profileId = CreateProfileId();
        var duplicate = source! with
        {
            Id = profileId,
            Name = name!,
            Lifecycle = ProfileLifecycleState.Available,
        };

        Apply(game!, installation! with { Profiles = installation.Profiles.Add(duplicate) });
        Shell.SelectProfile(profileId);
        return ProfileManagementResult.Applied(profileId);
    }

    public ProfileManagementResult RenameProfile(ProfileId profileId, string? requestedName)
    {
        var profileFailure = ResolveProfile(profileId, out var game, out var installation, out var profile);
        if (profileFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(profileFailure);
        }

        var nameFailure = NormalizeName(installation!, requestedName, profileId, out var name);
        if (nameFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(nameFailure);
        }

        var currentInstallation = installation!;
        var currentProfile = profile!;
        var index = currentInstallation.Profiles.IndexOf(currentProfile);
        var updated = currentInstallation with
        {
            Profiles = currentInstallation.Profiles.SetItem(index, currentProfile with { Name = name! }),
        };
        Apply(game!, updated);
        return ProfileManagementResult.Applied(profileId);
    }

    public ProfileManagementResult ArchiveProfile(ProfileId profileId)
    {
        var profileFailure = ResolveProfile(profileId, out var game, out var installation, out var profile);
        if (profileFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(profileFailure);
        }

        if (profile!.Lifecycle == ProfileLifecycleState.Archived)
        {
            return ProfileManagementResult.Rejected(ProfileManagementFailure.AlreadyArchived);
        }

        if (installation!.Profiles.Count(candidate =>
                candidate.Lifecycle == ProfileLifecycleState.Available) <= 1)
        {
            return ProfileManagementResult.Rejected(ProfileManagementFailure.FinalAvailableProfile);
        }

        var currentProfile = profile;
        var index = installation.Profiles.IndexOf(currentProfile);
        var updated = installation with
        {
            Profiles = installation.Profiles.SetItem(
                index,
                currentProfile with { Lifecycle = ProfileLifecycleState.Archived }),
        };
        Apply(game!, updated);
        return ProfileManagementResult.Applied(profileId);
    }

    public ProfileManagementResult DeleteProfile(ProfileId profileId)
    {
        var profileFailure = ResolveProfile(profileId, out var game, out var installation, out var profile);
        if (profileFailure != ProfileManagementFailure.None)
        {
            return ProfileManagementResult.Rejected(profileFailure);
        }

        if (installation!.Profiles.Length == 1)
        {
            return ProfileManagementResult.Rejected(ProfileManagementFailure.FinalProfile);
        }

        if (Shell.CurrentSelection.ProfileId == profileId)
        {
            return ProfileManagementResult.Rejected(ProfileManagementFailure.ActiveProfile);
        }

        Apply(game!, installation with { Profiles = installation.Profiles.Remove(profile!) });
        return ProfileManagementResult.Applied(profileId);
    }

    public string SuggestDuplicateName(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var installation = FindCurrentInstallation();
        if (installation is null || !installation.Profiles.Contains(profile))
        {
            return $"{profile.Name} Copy";
        }

        const string copySuffix = " Copy";
        var stemLength = Math.Min(profile.Name.Length, MaximumProfileNameLength - copySuffix.Length);
        var baseName = $"{profile.Name[..stemLength]}{copySuffix}";
        var candidate = baseName;
        var suffix = 2;
        while (HasName(installation, candidate, null))
        {
            candidate = $"{baseName} {suffix++}";
        }

        return candidate;
    }

    private ProfileManagementFailure ResolveInstallation(
        InstallationId installationId,
        out ManagedGame? game,
        out ManagedInstallation? installation)
    {
        game = null;
        installation = null;

        if (Shell.CurrentSelection.GameId is not GameId gameId ||
            Shell.CurrentSelection.InstallationId != installationId)
        {
            return ProfileManagementFailure.InstallationNotFound;
        }

        game = Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId);
        installation = game?.Installations.FirstOrDefault(candidate => candidate.Id == installationId);
        if (game is null || installation is null)
        {
            return ProfileManagementFailure.InstallationNotFound;
        }

        if (installation.Metadata.Provenance != InstallationProvenanceKind.Mock)
        {
            return ProfileManagementFailure.ProfilesUnsupported;
        }

        if (!installation.Capabilities.Supports(WorkspaceFeature.Profiles))
        {
            return ProfileManagementFailure.ProfilesUnsupported;
        }

        return installation.Metadata.Availability == InstallationAvailability.Available
            ? ProfileManagementFailure.None
            : ProfileManagementFailure.InstallationUnavailable;
    }

    private ProfileManagementFailure ResolveProfile(
        ProfileId profileId,
        out ManagedGame? game,
        out ManagedInstallation? installation,
        out Profile? profile)
    {
        profile = null;
        if (Shell.CurrentSelection.InstallationId is not InstallationId installationId)
        {
            game = null;
            installation = null;
            return ProfileManagementFailure.InstallationNotFound;
        }

        var failure = ResolveInstallation(installationId, out game, out installation);
        if (failure != ProfileManagementFailure.None)
        {
            return failure;
        }

        profile = installation!.Profiles.FirstOrDefault(candidate => candidate.Id == profileId);
        return profile is null ? ProfileManagementFailure.ProfileNotFound : ProfileManagementFailure.None;
    }

    private ProfileManagementFailure NormalizeName(
        ManagedInstallation installation,
        string? requestedName,
        ProfileId? excludedProfileId,
        out string? normalizedName)
    {
        normalizedName = requestedName?.Trim();
        if (string.IsNullOrEmpty(normalizedName) ||
            normalizedName.Length > MaximumProfileNameLength ||
            normalizedName.Any(char.IsControl))
        {
            return ProfileManagementFailure.InvalidName;
        }

        return HasName(installation, normalizedName, excludedProfileId)
            ? ProfileManagementFailure.DuplicateName
            : ProfileManagementFailure.None;
    }

    private static bool HasName(
        ManagedInstallation installation,
        string name,
        ProfileId? excludedProfileId) =>
        installation.Profiles.Any(profile =>
            (excludedProfileId is null || profile.Id != excludedProfileId.Value) &&
            string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));

    private ProfileId CreateProfileId()
    {
        while (true)
        {
            var id = new ProfileId($"profile.mock.session.{_nextProfileNumber++:D4}");
            if (!Catalog.Games
                .SelectMany(game => game.Installations)
                .SelectMany(installation => installation.Profiles)
                .Any(profile => profile.Id == id))
            {
                return id;
            }
        }
    }

    private void Apply(ManagedGame game, ManagedInstallation updatedInstallation)
    {
        var gameIndex = Catalog.Games.IndexOf(game);
        var currentInstallation = game.Installations.First(candidate => candidate.Id == updatedInstallation.Id);
        var installationIndex = game.Installations.IndexOf(currentInstallation);
        var updatedGame = game with
        {
            Installations = game.Installations.SetItem(installationIndex, updatedInstallation),
        };
        var updatedCatalog = new GridCatalogSnapshot(
            $"{_baseRevision}.session-{++_revisionNumber:D3}",
            Catalog.SourceKind,
            Catalog.Games.SetItem(gameIndex, updatedGame));
        Shell.ReplaceCatalog(updatedCatalog);
    }

    private ManagedInstallation? FindCurrentInstallation()
    {
        if (Shell.CurrentSelection.GameId is not GameId gameId ||
            Shell.CurrentSelection.InstallationId is not InstallationId installationId)
        {
            return null;
        }

        return Catalog.Games
            .FirstOrDefault(game => game.Id == gameId)?
            .Installations.FirstOrDefault(installation => installation.Id == installationId);
    }
}
