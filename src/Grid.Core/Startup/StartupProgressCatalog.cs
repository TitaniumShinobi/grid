namespace Grid.Core.Startup;

/// <summary>
/// Truthful startup progress weights used by the shell loading bar. Each unit
/// advances only when a required startup boundary completes or is skipped.
/// </summary>
public static class StartupProgressCatalog
{
    public static readonly string[] RequiredUnits =
    [
        StartupStageId.AuthSessionRestore,
        StartupStageId.AccountLookup,
        StartupStageId.GridAccountState,
        StartupStageId.LocalDeviceState,
        StartupStageId.RegisteredStateLoad,
        StartupStageId.PreparedCanonicalStateLoad,
        StartupStageId.ShellMainWindow,
        StartupStageId.ShellProductState,
        StartupStageId.ShellUiActivation,
        StartupStageId.Ready,
    ];

    public static int TotalUnits => RequiredUnits.Length;
}
