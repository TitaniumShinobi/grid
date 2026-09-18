using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Grid.DevLauncher;

internal static class Program
{
    private const uint ErrorIcon = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var launcherPath = Environment.ProcessPath ??
                throw new LauncherConfigurationException("The root launcher executable path is unavailable.");
            var launcherDirectory = AppContext.BaseDirectory;
            var startInfo = Launcher.CreateStartInfo(launcherDirectory, launcherPath, args);
            if (Process.Start(startInfo) is null)
            {
                throw new InvalidOperationException("Windows did not create the Grid Debug process.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            NativeMethods.MessageBox(
                IntPtr.Zero,
                exception.Message,
                "Grid could not start",
                ErrorIcon);
            return 1;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        internal static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    }
}
