using Microsoft.UI.Windowing;

namespace Grid.App.Services;

/// <summary>
/// Applies the replaceable Stage 1 application identity without coupling it to layout or product behavior.
/// </summary>
internal static class TemporaryApplicationIdentity
{
    private static readonly string IconPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "TemporaryIdentity",
        "GridTemporary.ico");

    public static void Apply(AppWindow appWindow)
    {
        ArgumentNullException.ThrowIfNull(appWindow);
        if (File.Exists(IconPath))
        {
            appWindow.SetIcon(IconPath);
        }
    }
}
