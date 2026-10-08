namespace Grid.Core.Startup;

public static class StartupStageId
{
    public const string AuthSessionRestore = "AUTH_SESSION_RESTORE";
    public const string AccountLookup = "ACCOUNT_LOOKUP";
    public const string GridAccountState = "GRID_ACCOUNT_STATE";
    public const string LocalDeviceState = "LOCAL_DEVICE_STATE";
    public const string RegisteredStateLoad = "REGISTERED_STATE_LOAD";
    public const string StaleStateAssessment = "STALE_STATE_ASSESSMENT";
    public const string PreparedCanonicalStateLoad = "PREPARED_CANONICAL_STATE_LOAD";
    public const string ShellInitialization = "SHELL_INITIALIZATION";
    public const string ShellMainWindow = "SHELL_INITIALIZATION.MAIN_WINDOW";
    public const string ShellProductState = "SHELL_INITIALIZATION.PRODUCT_STATE";
    public const string ShellUiActivation = "SHELL_INITIALIZATION.UI_ACTIVATION";
    public const string ShellBackgroundHydrate = "SHELL_INITIALIZATION.BACKGROUND_HYDRATE";
    public const string Ready = "READY";

    public static readonly string[] Ordered =
    [
        AuthSessionRestore,
        AccountLookup,
        GridAccountState,
        LocalDeviceState,
        RegisteredStateLoad,
        StaleStateAssessment,
        PreparedCanonicalStateLoad,
        ShellInitialization,
        Ready,
    ];
}
