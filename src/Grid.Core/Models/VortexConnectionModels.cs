using System.Collections.Immutable;

namespace Grid.Core.Models;

public sealed record VortexInstallationConnection(
    int SchemaVersion,
    InstallationId InstallationId,
    string StagingRoot,
    DateTimeOffset ConnectedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record VortexConnectionLoadResult(
    ImmutableArray<VortexInstallationConnection> Connections,
    ImmutableArray<string> Issues);
