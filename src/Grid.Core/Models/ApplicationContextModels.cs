using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum ApplicationSurface
{
    Home,
    GameWorkspace,
    History,
    Settings,
}

public sealed record ApplicationContextSnapshot(
    ApplicationSurface Surface,
    GameId? GameId,
    InstallationId? InstallationId,
    ProfileId? ProfileId,
    ImmutableArray<ModId> ModIds,
    PluginId? PluginId,
    EnvironmentTabCapability? EnvironmentTab,
    EnvironmentEntryId? EnvironmentEntryId,
    ArchiveId? ArchiveId,
    VirtualPathId? VirtualPathId,
    LaunchTargetId? LaunchTargetId,
    ObservedExecutableId? ObservedExecutableId,
    DiagnosticsSelectionId? DiagnosticsSelectionId)
{
    public static ApplicationContextSnapshot Home { get; } = new(
        ApplicationSurface.Home,
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
        null);
}
