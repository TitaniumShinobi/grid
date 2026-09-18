using System.Collections.Immutable;

namespace Grid.Core.Models;

public sealed record GameInstallationRegistration(
    int SchemaVersion,
    InstallationReferenceId ReferenceId,
    InstallationId InstallationId,
    GameId GameId,
    GameAdapterId AdapterId,
    string DisplayName,
    string Edition,
    string ProviderId,
    string InstallRoot,
    string ExecutablePath,
    ImmutableArray<string> ManagerProviderIds,
    DateTimeOffset RegisteredAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record GameRegistrationLoadResult(
    ImmutableArray<GameInstallationRegistration> Registrations,
    ImmutableArray<string> Issues);
