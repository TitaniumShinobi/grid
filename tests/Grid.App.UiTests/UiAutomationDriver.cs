using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Text.Json;
using Grid.Core.Models;
using Grid.App.Controls;

namespace Grid.App.UiTests;

internal sealed class UiAutomationDriver : IDisposable
{
    private const string ExpectedGtaCatalogRevision =
        "grid.catalog-revision.v7.sha256.a2ba34243f23eee66f350a06304321ab376d27f4a63468d1b44ab7aa91346a41";
    private const string CanonicalLiveClaim =
        "Verify the selected GTA V Enhanced canonical contexts with read-only evidence.";
    private const string CasinoHeistClaim =
        "GTA Online Casino Heist crashes after leaving an activity.";
    private const string CasinoHeistOtherContext = "GTA Online Casino Heist";

    private static readonly (int Width, int Height)[] Viewports =
    [
        (1920, 1080), (1536, 864), (1280, 720), (960, 540), (960, 960), (720, 960), (1600, 900), (1366, 768),
    ];
    private static readonly string[] FixtureNames =
    [
        "UNDEFEATED", "NEFARAM", "Custom", "Default", "Performance", "Legacy Benchmark",
        "Grand Theft Auto V",
    ];

    private readonly Process process;
    private readonly string captureRoot;
    private readonly string dataRoot;
    private readonly bool ownsDataRoot;
    private readonly bool ownsProcess;
    private AutomationElement root;

    private UiAutomationDriver(
        Process process,
        AutomationElement root,
        string captureRoot,
        string dataRoot,
        bool ownsDataRoot,
        bool ownsProcess)
    {
        this.process = process;
        this.root = root;
        this.captureRoot = captureRoot;
        this.dataRoot = dataRoot;
        this.ownsDataRoot = ownsDataRoot;
        this.ownsProcess = ownsProcess;
    }

    public string DataRoot => dataRoot;

    public static string ResolveExecutable(string configuration)
    {
        var configuredExecutable = Environment.GetEnvironmentVariable("GRID_UI_EXECUTABLE");
        string executable;
        if (string.IsNullOrWhiteSpace(configuredExecutable))
        {
            var repository = FindRepositoryRoot();
            executable = Path.Combine(repository, "src", "Grid.App", "bin", "x64", configuration,
                "net9.0-windows10.0.19041.0", "win-x64", "Grid.exe");
        }
        else
        {
            configuredExecutable = configuredExecutable.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(configuredExecutable))
            {
                throw new InvalidOperationException(
                    "GRID_UI_EXECUTABLE must be an absolute path to a published or installed Grid.exe.");
            }

            executable = Path.GetFullPath(configuredExecutable);
        }

        if (!Path.GetFileName(executable).Equals("Grid.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The UI verification target must be named Grid.exe, but '{Path.GetFileName(executable)}' was provided.");
        }

        if (!File.Exists(executable))
        {
            var source = string.IsNullOrWhiteSpace(configuredExecutable)
                ? "Build Grid.App before UI verification"
                : "GRID_UI_EXECUTABLE does not identify an existing executable";
            throw new FileNotFoundException($"{source}.", executable);
        }

        return executable;
    }

    public static async Task<UiAutomationDriver> StartAsync(string executable, bool demo)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"grid-ui-data-{Guid.NewGuid():N}");
        return await StartCoreAsync(executable, demo, dataRoot, ownsDataRoot: true);
    }

    /// <summary>
    /// Starts GRID against a caller-owned data root. The root is retained when
    /// the driver is disposed so a later launch can exercise the same device
    /// state without sharing the user's real GRID data.
    /// </summary>
    public static Task<UiAutomationDriver> StartAsync(string executable, bool demo, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        return StartCoreAsync(executable, demo, Path.GetFullPath(dataRoot), ownsDataRoot: false);
    }

    public static UiAutomationDriver Attach(int processId)
    {
        var process = Process.GetProcessById(processId);
        for (var attempt = 0; attempt < 100 && process.MainWindowHandle == IntPtr.Zero; attempt++)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"GRID process {processId} exited before exposing its main window.");
            Thread.Sleep(100);
            process.Refresh();
        }
        if (process.MainWindowHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"GRID process {processId} did not expose a main window within 10 seconds.");
        var captures = Environment.GetEnvironmentVariable("GRID_UI_CAPTURE_ROOT");
        if (string.IsNullOrWhiteSpace(captures))
            captures = Path.Combine(FindRepositoryRoot(), "artifacts", "ui-tests", "screenshots");
        Directory.CreateDirectory(captures);
        return new UiAutomationDriver(
            process,
            AutomationElement.FromHandle(process.MainWindowHandle),
            captures,
            string.Empty,
            ownsDataRoot: false,
            ownsProcess: false);
    }

    private static async Task<UiAutomationDriver> StartCoreAsync(
        string executable,
        bool demo,
        string dataRoot,
        bool ownsDataRoot)
    {
        var captures = Environment.GetEnvironmentVariable("GRID_UI_CAPTURE_ROOT");
        if (string.IsNullOrWhiteSpace(captures))
        {
            captures = Path.Combine(FindRepositoryRoot(), "artifacts", "ui-tests", "screenshots");
        }
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(captures);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Arguments = demo ? "--demo" : string.Empty,
        };
        start.Environment["GRID_DATA_ROOT"] = dataRoot;
        if (demo) start.Environment["GRID_TEST_APP_MODE"] = "demo";
        var process = Process.Start(start) ?? throw new InvalidOperationException("Grid did not start.");
        for (var attempt = 0; attempt < 100 && process.MainWindowHandle == IntPtr.Zero; attempt++)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Grid exited before exposing a main window (exit code {process.ExitCode}).");
            await Task.Delay(100);
            process.Refresh();
        }
        if (process.MainWindowHandle == IntPtr.Zero)
        {
            var diagnostics = WriteStartupDiagnostics(process, null, captures, "main-window-timeout");
            throw new TimeoutException(
                $"Grid process {process.Id} did not expose a main window within 10 seconds. Diagnostics: {diagnostics}");
        }
        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var loading = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => element.Current.Name.Equals("Loading connected games", StringComparison.OrdinalIgnoreCase));
            if (!loading) break;
            await Task.Delay(100);
        }
        var shellReady = false;
        for (var attempt = 0; attempt < 600; attempt++)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Grid exited before exposing its stable application shell (exit code {process.ExitCode}).");
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                root = AutomationElement.FromHandle(process.MainWindowHandle);
            shellReady = IsStableShellBoundary(root, process.Id);
            if (shellReady) break;
            await Task.Delay(100);
        }
        if (!shellReady)
        {
            var diagnostics = WriteStartupDiagnostics(process, root, captures, "shell-readiness-timeout");
            throw new TimeoutException(
                $"Grid process {process.Id} did not expose its visible PID-owned main workbench shell within 60 seconds. " +
                $"Diagnostics: {diagnostics}");
        }
        return new(process, root, captures, dataRoot, ownsDataRoot, ownsProcess: true);
    }

    private static bool IsStableShellBoundary(AutomationElement root, int processId)
    {
        try
        {
            if (root.Current.ProcessId != processId || root.Current.IsOffscreen ||
                root.Current.BoundingRectangle.IsEmpty)
            {
                return false;
            }

            var workbench = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Main workbench panel"));
            return workbench is not null && workbench.Current.ProcessId == processId &&
                   !workbench.Current.IsOffscreen && !workbench.Current.BoundingRectangle.IsEmpty;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static string WriteStartupDiagnostics(
        Process process,
        AutomationElement? root,
        string captureRoot,
        string label)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
        var prefix = $"{label}-pid-{process.Id}-{stamp}";
        var treePath = Path.Combine(captureRoot, $"{prefix}.txt");
        var screenshotPath = Path.Combine(captureRoot, $"{prefix}.png");
        var lines = new List<string>
        {
            $"CapturedAtUtc={DateTime.UtcNow:O}",
            $"ProcessId={process.Id}",
        };

        try
        {
            process.Refresh();
            lines.Add($"HasExited={process.HasExited}");
            if (!process.HasExited)
            {
                lines.Add($"Responding={process.Responding}");
                lines.Add($"MainWindowHandle=0x{process.MainWindowHandle.ToInt64():X}");
                lines.Add($"MainWindowTitle={process.MainWindowTitle}");
                if (process.MainWindowHandle != IntPtr.Zero &&
                    GetWindowRect(process.MainWindowHandle, out var rect))
                {
                    lines.Add($"NativeBounds={rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}");
                    try
                    {
                        ShowWindow(process.MainWindowHandle, ShowRestored);
                        SetForegroundWindow(process.MainWindowHandle);
                        Thread.Sleep(180);
                        using var bitmap = new Bitmap(
                            Math.Max(1, rect.Right - rect.Left),
                            Math.Max(1, rect.Bottom - rect.Top));
                        using var graphics = Graphics.FromImage(bitmap);
                        graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
                        bitmap.Save(screenshotPath);
                    }
                    catch (Exception exception)
                    {
                        lines.Add($"ScreenshotError={exception}");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lines.Add($"ProcessDiagnosticError={exception}");
        }

        if (root is not null)
        {
            try
            {
                var elements = root.FindAll(TreeScope.Subtree, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Take(1000)
                    .ToArray();
                lines.Add($"AutomationElementCount={elements.Length}");
                foreach (var element in elements)
                {
                    try
                    {
                        var current = element.Current;
                        lines.Add(
                            $"pid={current.ProcessId}|type={current.ControlType.ProgrammaticName}|" +
                            $"name={current.Name}|automationId={current.AutomationId}|enabled={current.IsEnabled}|" +
                            $"offscreen={current.IsOffscreen}|bounds={current.BoundingRectangle}");
                    }
                    catch (ElementNotAvailableException)
                    {
                        lines.Add("<automation-element-unavailable>");
                    }
                }
            }
            catch (Exception exception)
            {
                lines.Add($"AutomationTreeError={exception}");
            }
        }

        try
        {
            File.WriteAllLines(treePath, lines);
        }
        catch
        {
            return captureRoot;
        }

        return File.Exists(screenshotPath) ? $"{treePath}; {screenshotPath}" : treePath;
    }

    public void VerifyProductionIsEmpty()
    {
        WaitForElementName("GRID Welcome", TimeSpan.FromSeconds(20));
        var names = DescendantNames();
        foreach (var fixture in FixtureNames)
        {
            var matches = names.Where(name => name.Equals(fixture, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert(matches.Length == 0,
                $"Production exposed fixture name '{fixture}' in: {string.Join(" | ", matches)}.");
        }
        Assert(names.Any(name => name.Equals("GRID Welcome", StringComparison.OrdinalIgnoreCase)),
            $"First production launch did not expose Welcome. Visible names: {string.Join(" | ", names)}");
        Assert(names.Any(name => name.Equals("Browse Game Catalog", StringComparison.OrdinalIgnoreCase)), "First production launch did not route into the Game Catalog.");
        Assert(names.Any(name => name.Equals("Find my games", StringComparison.OrdinalIgnoreCase)), "First production launch did not expose optional game discovery.");
        Assert(!names.Any(name => name.Equals("Find setups", StringComparison.OrdinalIgnoreCase)), "First production launch still exposed mandatory setup discovery.");
        Assert(names.Any(name => name.Equals("Activities", StringComparison.OrdinalIgnoreCase)), "Production shell did not expose Activities.");
        Assert(names.Any(name => name.Contains("Grid Assistant", StringComparison.OrdinalIgnoreCase)), "Production shell did not expose the AUTO sidebar toggle.");
        Assert(!names.Any(name => name.Contains("PRODUCTION", StringComparison.OrdinalIgnoreCase)),
            "Production exposed internal mode terminology.");
    }

    public void VerifyReturningAccountHome()
    {
        WaitForElementName("No games connected", TimeSpan.FromSeconds(20));
        var names = DescendantNames();
        Assert(names.Any(name => name.Equals("Home tab", StringComparison.OrdinalIgnoreCase)),
            "A completed returning account did not open Home.");
        Assert(names.Any(name => name.Equals("Add game", StringComparison.OrdinalIgnoreCase)),
            "A completed returning account did not expose the normal Home actions.");
        Assert(names.Any(name => name.Equals("No games connected", StringComparison.OrdinalIgnoreCase)),
            "A completed returning account inherited a connected installation.");
        Assert(!names.Any(name => name.Equals("GRID Welcome", StringComparison.OrdinalIgnoreCase)),
            "A completed returning account reopened Welcome.");
        Assert(!names.Any(name => name.EndsWith(", connected game", StringComparison.OrdinalIgnoreCase)),
            "A completed returning account projected an unowned installation as connected.");
    }

    public void VerifyAuthenticatedAccountAvatar()
    {
        var account = FindByName("Authenticated account")
            ?? throw new InvalidOperationException("The authenticated lower-left account area was not exposed.");
        Assert(!account.Current.IsOffscreen && !account.Current.BoundingRectangle.IsEmpty,
            "The authenticated lower-left account area was present but not visible.");
        Assert(account.Current.HelpText is
                "Profile image from the authenticated identity" or
                "Neutral authenticated account avatar",
            "The account area did not report an authenticated image or neutral fallback source.");
    }

    public void VerifyEditorShell()
    {
        var names = DescendantNames();
        foreach (var required in new[]
        {
            "Home", "Back", "Forward", "Global Grid search", "Open in manager instance", "Customize layout", "Open tabs",
            "Toggle left sidebar", "Toggle bottom panel", "Explorer", "Search", "Activities",
            "Discrepancies", "Concerns", "Notifications",
        })
        {
            Assert(names.Any(name => name.Equals(required, StringComparison.OrdinalIgnoreCase)),
                $"The editor shell did not expose '{required}'.");
        }
        foreach (var duplicate in new[] { "Home page tab", "Games page tab", "Workstation page tab" })
            Assert(!names.Any(name => name.Equals(duplicate, StringComparison.OrdinalIgnoreCase)),
                $"The main panel exposed obsolete duplicate navigation '{duplicate}'.");

        VerifyActivityRailLayout();
        VerifyFooterLayout();
    }

    private void VerifyActivityRailLayout()
    {
        foreach (var name in new[] { "Search", "Explorer", "Activities", "Settings" })
        {
            var controls = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            var visible = false;
            foreach (AutomationElement control in controls)
            {
                if (!control.Current.IsOffscreen && !control.Current.BoundingRectangle.IsEmpty)
                {
                    visible = true;
                    break;
                }
            }
            Assert(visible, $"The activity-rail button '{name}' was present but collapsed or offscreen.");
        }
    }

    private void VerifyFooterLayout()
    {
        var availability = FindByName("Manager availability")
            ?? throw new InvalidOperationException("The footer did not expose its manager availability indicator.");
        var discrepancies = FindByName("Discrepancies")
            ?? throw new InvalidOperationException("The footer did not expose the discrepancy counter.");
        var concerns = FindByName("Concerns")
            ?? throw new InvalidOperationException("The footer did not expose the concern counter.");
        var correctiveAction = FindByName("Review corrective actions")
            ?? throw new InvalidOperationException("The footer did not expose the corrective-action control.");

        var availabilityBounds = availability.Current.BoundingRectangle;
        var discrepancyBounds = discrepancies.Current.BoundingRectangle;
        var concernBounds = concerns.Current.BoundingRectangle;
        var correctiveBounds = correctiveAction.Current.BoundingRectangle;
        Assert(availabilityBounds.Width <= 50,
            "The footer availability indicator expanded beyond the compact left-rail dot surface.");
        Assert(!string.IsNullOrWhiteSpace(availability.Current.HelpText),
            "The footer availability dot did not expose its status through the hover/accessibility popup text.");
        Assert(discrepancyBounds.Left < concernBounds.Left && concernBounds.Left < correctiveBounds.Left,
            "The footer controls are not ordered as discrepancies, concerns, then corrective action.");
        Assert(Math.Abs(discrepancyBounds.Right - concernBounds.Left) <= 2 &&
               Math.Abs(concernBounds.Right - correctiveBounds.Left) <= 2,
            "The footer counters and corrective action are not a contiguous aligned group.");
    }


    public void VerifyFirstRunWelcomeAndTabs()
    {
        var names = DescendantNames();
        Assert(names.Any(name => name.Equals("Welcome tab", StringComparison.OrdinalIgnoreCase)),
            "First-run Welcome was not represented as an editor tab.");
        Activate("Browse Game Catalog");
        Thread.Sleep(250);
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Welcome tab", StringComparison.OrdinalIgnoreCase)),
            "Leaving Welcome closed the Welcome tab implicitly.");
        Assert(names.Any(name => name.Equals("Game Catalog tab", StringComparison.OrdinalIgnoreCase)),
            "Welcome did not route Browse Game Catalog to the canonical main-panel tab.");
        Assert(names.Any(name => name.Equals("Game Catalog", StringComparison.OrdinalIgnoreCase)),
            "Welcome did not render Game Catalog as a main-panel page.");
        Activate("Home");
        Thread.Sleep(250);
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Home tab", StringComparison.OrdinalIgnoreCase)),
            "Leaving Game Catalog for Home did not create the singleton Home tab.");
        Assert(names.Any(name => name.Equals("Add game", StringComparison.OrdinalIgnoreCase)),
            "Continuing from Welcome did not open normal Home.");
        Assert(names.Any(name => name.Equals("No games connected", StringComparison.OrdinalIgnoreCase)),
            "A new account with no explicit connection did not report zero connected games on Home.");
        Assert(!names.Any(name => name.EndsWith(", connected game", StringComparison.OrdinalIgnoreCase)),
            "Home projected a catalog or discovery candidate as a connected installation.");
        Activate("Home");
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Home tab", StringComparison.OrdinalIgnoreCase)),
            "The Grid brand did not route to the singleton Home tab.");
    }

    public void VerifyGameCatalogContract()
    {
        Activate("Add game");
        WaitForElementName("Game Catalog", TimeSpan.FromSeconds(10));
        var names = DescendantNames();
        foreach (var required in new[]
        {
            "Game Catalog", "Add profile", "Find my games", "Search Game Catalog", "Filter Game Catalog",
        })
        {
            Assert(names.Any(name => name.Equals(required, StringComparison.OrdinalIgnoreCase)),
                $"The Game Catalog did not expose '{required}'.");
        }
        foreach (var gameName in new[] { "Skyrim Special Edition", "GTA V Legacy", "GTA V Enhanced" })
        {
            SetValue("Search Game Catalog", gameName);
            WaitForElementName(gameName, TimeSpan.FromSeconds(10));
            names = DescendantNames();
            Assert(names.Any(name => name.Equals(gameName, StringComparison.OrdinalIgnoreCase)),
                $"The searchable Game Catalog did not expose '{gameName}'.");
        }
        SetValue("Search Game Catalog", "Skyrim Special Edition");
        Assert(names.Any(name => name.Equals("Game Catalog tab", StringComparison.OrdinalIgnoreCase)),
            "Home Add Game did not reuse the canonical Game Catalog tab.");

        Activate("Add profile");
        WaitForElementName("Add Profile", TimeSpan.FromSeconds(5));
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Browse for existing profile", StringComparison.OrdinalIgnoreCase)),
            "Add Profile did not begin with the minimal Browse task surface.");
        Assert(!names.Any(name => name.Equals("MO2", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Vortex", StringComparison.OrdinalIgnoreCase)),
            "Add Profile exposed manager interpretation in the frontend before Browse.");
        Assert(!names.Any(name => name.Equals("Connect resolved profile", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Resolved profile selection", StringComparison.OrdinalIgnoreCase)),
            "Add Profile exposed review or persistence actions before Browse resolved evidence.");
        Activate("Back to Game Catalog");
        WaitForElementName("Game Catalog", TimeSpan.FromSeconds(5));

        SetValue("Search Game Catalog", "Skyrim Special Edition");
        WaitForElementName("Skyrim Special Edition", TimeSpan.FromSeconds(10));
        Activate("Skyrim Special Edition");
        WaitForElementName("Browse for game directory", TimeSpan.FromSeconds(10));
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Browse for game directory", StringComparison.OrdinalIgnoreCase)),
            "Selecting a catalog game did not preserve the manual Browse fallback.");
        var browse = FindByName("Browse for game directory")
            ?? throw new InvalidOperationException("The Add Game dialog did not expose its Browse control.");
        var browseBounds = browse.Current.BoundingRectangle;
        Assert(browse.Current.IsEnabled,
            "The Add Game manual Browse fallback was disabled after automatic discovery.");
        Assert(!browse.Current.IsOffscreen && !browseBounds.IsEmpty,
            $"The Add Game manual Browse fallback existed only off-screen after automatic discovery: '{browseBounds}'.");
        var dialogBounds = FindByName("Add game")?.Current.BoundingRectangle ?? root.Current.BoundingRectangle;
        Assert(browseBounds.Left >= dialogBounds.Left && browseBounds.Right <= dialogBounds.Right,
            $"The Add Game manual Browse fallback bounds '{browseBounds}' exceeded the dialog bounds '{dialogBounds}'.");
        Activate("Cancel");
    }


    public void VerifyActivityTaskboard()
    {
        NormalizeShell();
        Activate("Activities");
        var names = DescendantNames();
        foreach (var required in new[] { "Queue task count", "In Progress task count", "Ready task count", "Ledger task count", "Activity composer", "See more activity" })
            Assert(names.Any(name => name.Equals(required, StringComparison.OrdinalIgnoreCase)),
                $"Collapsed Activity did not expose '{required}'.");

        Activate("See more activity");
        Thread.Sleep(200);
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("GRID Taskboard", StringComparison.OrdinalIgnoreCase)),
            "Expanding Activity did not expose the four-panel Taskboard.");
        Assert(names.Any(name => name.Equals("Close Activity Taskboard", StringComparison.OrdinalIgnoreCase)),
            "Expanded Taskboard did not expose a deterministic close control.");
        var board = FindByName("GRID Taskboard")
            ?? throw new InvalidOperationException("Expanded Taskboard surface was unavailable for geometry verification.");
        var boardBounds = board.Current.BoundingRectangle;
        var assistantPanel = FindByName("Grid Assistant panel");
        Assert(assistantPanel is null || assistantPanel.Current.IsOffscreen,
            "Expanded Taskboard exposed the ordinary assistant shell.");

        var bottomToolPanel = FindByName("Bottom tool panel");
        Assert(bottomToolPanel is null || bottomToolPanel.Current.IsOffscreen,
            "Expanded Taskboard exposed the ordinary bottom tool panel.");

        var windowBounds = root.Current.BoundingRectangle;
        Assert(boardBounds.Left <= windowBounds.Left + 80,
            "Expanded Taskboard did not begin at the shell workspace boundary.");
        Assert(boardBounds.Right >= windowBounds.Right - 40,
            "Expanded Taskboard did not extend across the shell workspace.");
        Assert(boardBounds.Top <= windowBounds.Top + 80,
            "Expanded Taskboard did not begin beneath the top bar.");
        Assert(boardBounds.Bottom >= windowBounds.Bottom - 80,
            "Expanded Taskboard did not extend to the footer boundary.");
        Activate("Close Activity Taskboard");
    }

    public void VerifyPanelToggles()
    {
        NormalizeShell();
        VerifyUniversalHeaderGeometry(
            "Main",
            FindVisibleByName("Main workbench panel"),
            FindVisibleByName("Home tab"),
            FindVisibleByAutomationId("EditorMoreActionsButton"),
            headerRow: null);
        Activate("Explorer");
        var explorerTree = FindByName("Instance explorer");
        Assert(explorerTree is not null && !explorerTree.Current.IsOffscreen,
            "Opening Explorer did not expose the read-only instance explorer.");
        Assert(FindByName("Collapse Explorer folders") is not null,
            "GRID Explorer did not expose Collapse folders.");
        Assert(FindByName("Refresh Explorer") is not null,
            "GRID Explorer did not expose Refresh.");
        Assert(FindByName("Explorer read only status") is not null,
            "GRID Explorer did not expose its read-only boundary.");

        Activate("Explorer");
        explorerTree = FindByName("Instance explorer");
        Assert(explorerTree is null || explorerTree.Current.IsOffscreen,
            "Clicking the active Explorer icon again did not close the sidebar.");

        Activate("Search");
        var searchPanel = FindByName("Sidebar search panel");
        Assert(searchPanel is not null && !searchPanel.Current.IsOffscreen,
            "Clicking Search while the sidebar was closed did not open the Search page.");

        Activate("Explorer");
        explorerTree = FindByName("Instance explorer");
        Assert(explorerTree is not null && !explorerTree.Current.IsOffscreen,
            "Selecting Explorer from another open sidebar page did not switch pages and remain open.");

        Activate("Toggle left sidebar");
        var explorer = FindByName("Instance explorer");
        Assert(explorer is null || explorer.Current.IsOffscreen, "The left-sidebar toggle did not hide the Explorer panel.");
        Activate("Toggle left sidebar");

        var terminal = FindByName("Grid terminal command");
        Assert(terminal is null || terminal.Current.IsOffscreen,
            "The default shell unexpectedly started with the bottom panel open.");

        Activate("Toggle bottom panel");
        terminal = FindByName("Grid terminal command");
        Assert(terminal is not null && !terminal.Current.IsOffscreen,
            "The bottom-panel toggle did not open the terminal from the closed default.");
        var bottomPanel = FindVisibleByName("Bottom tool panel")
            ?? throw new InvalidOperationException("The visible Console did not expose its shell boundary.");
        VerifyTerminalRightClearance(bottomPanel, "Console");
        VerifyUniversalHeaderGeometry(
            "Console",
            bottomPanel,
            FindVisibleByAutomationId("TerminalTabButton"),
            FindVisibleByAutomationId("ClearTerminalButton"),
            FindVisibleByName("Console panel header row"));

        Activate("Toggle bottom panel");
        terminal = FindByName("Grid terminal command");
        Assert(terminal is null || terminal.Current.IsOffscreen,
            "The bottom-panel toggle did not close the terminal.");

        Activate("Toggle bottom panel");
        terminal = FindByName("Grid terminal command");
        Assert(terminal is not null && !terminal.Current.IsOffscreen,
            "The bottom panel did not reopen before maximize verification.");

        Activate("Maximize bottom panel");
        var maximizedTerminal = FindByName("Maximized Grid terminal command");
        Assert(maximizedTerminal is not null && !maximizedTerminal.Current.IsOffscreen,
            "Maximizing the bottom panel did not expose the full-view terminal surface.");

        var maximizedBottomPanel = FindByName("Maximized bottom tool panel")
            ?? throw new InvalidOperationException("The maximized bottom tool panel did not expose its stable automation surface.");
        var windowBounds = root.Current.BoundingRectangle;
        var bottomBounds = maximizedBottomPanel.Current.BoundingRectangle;
        Assert(bottomBounds.Top <= windowBounds.Top + 80,
            "Maximized Console did not begin directly beneath the top bar.");
        Assert(bottomBounds.Bottom >= windowBounds.Bottom - 80,
            "Maximized Console did not extend to the footer boundary.");
        VerifyTerminalRightClearance(maximizedBottomPanel, "Maximized Console");
        VerifyUniversalHeaderGeometry(
            "Maximized Console",
            maximizedBottomPanel,
            FindVisibleByAutomationId("FullTerminalTabButton"),
            FindVisibleByAutomationId("FullClearTerminalButton"),
            FindVisibleByName("Maximized Console panel header row"));

        Activate("Toggle bottom panel");
        maximizedBottomPanel = FindByName("Maximized bottom tool panel");
        Assert(maximizedBottomPanel is null || maximizedBottomPanel.Current.IsOffscreen,
            "The top-bar bottom-panel toggle did not close a maximized Console.");
        Assert(FindByName("Maximized Grid terminal command") is null ||
               FindByName("Maximized Grid terminal command")!.Current.IsOffscreen,
            "Closing a maximized Console left its full-view projection visible.");
        Activate("Toggle bottom panel");
        Activate("Maximize bottom panel");

        // Full Console owns the center workspace only. The left and right panels
        // must retain their ordinary independent toggle behavior.
        var leftSidebar = FindByName("Side panel options");
        Assert(leftSidebar is not null && !leftSidebar.Current.IsOffscreen,
            "Maximized Console unexpectedly hid the left content sidebar.");
        Activate("Toggle left sidebar");
        leftSidebar = FindByName("Side panel options");
        Assert(leftSidebar is null || leftSidebar.Current.IsOffscreen,
            "The left-panel toggle stopped working while Console was maximized.");
        Activate("Toggle left sidebar");
        leftSidebar = FindByName("Side panel options");
        Assert(leftSidebar is not null && !leftSidebar.Current.IsOffscreen,
            "The left content sidebar did not restore while Console was maximized.");

        EnsureAssistantOpen();
        var assistantPanel = FindByName("Grid Assistant panel");
        Assert(assistantPanel is not null && !assistantPanel.Current.IsOffscreen,
            "Maximized Console unexpectedly hid the assistant panel.");
        VerifyTerminalRightClearance(assistantPanel!, "Chat");
        VerifyUniversalHeaderGeometry(
            "Chat",
            assistantPanel,
            FindVisibleByName("Chat"),
            FindVisibleByAutomationId("NewChatButton"),
            FindVisibleByName("Chat panel header row"));
        Activate("Hide Grid Assistant");
        assistantPanel = FindByName("Grid Assistant panel");
        Assert(assistantPanel is null || assistantPanel.Current.IsOffscreen,
            "The assistant toggle stopped working while Console was maximized.");
        Activate("Show Grid Assistant");
        assistantPanel = FindByName("Grid Assistant panel");
        Assert(assistantPanel is not null && !assistantPanel.Current.IsOffscreen,
            "The assistant panel did not restore while Console was maximized.");

        Activate("Restore bottom panel");

        EnsureAssistantOpen();
        Activate("Hide Grid Assistant");
        Assert(FindByName("Show Grid Assistant") is not null, "The AUTO-sidebar toggle did not expose its collapsed state.");
        Activate("Show Grid Assistant");
    }

    public void VerifyShellCommandSeparation()
    {
        var assistant = FindByName("Hide Grid Assistant") ?? FindByName("Show Grid Assistant")
            ?? throw new InvalidOperationException("The shell assistant toggle was unavailable.");
        var assistantBounds = assistant.Current.BoundingRectangle;
        var addGameControls = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Add game"));
        foreach (AutomationElement addGame in addGameControls)
        {
            var bounds = addGame.Current.BoundingRectangle;
            if (bounds.IsEmpty || addGame.Current.IsOffscreen) continue;
            Assert(!assistantBounds.IntersectsWith(bounds),
                $"The shell assistant toggle '{assistantBounds}' overlapped Add game '{bounds}'.");
        }
    }

    public void VerifyDemoIsExplicit()
    {
        WaitForElementName("Skyrim Special Edition, connected game", TimeSpan.FromSeconds(20));
        var names = DescendantNames();
        Assert(names.Any(name => name.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase)),
            $"The isolated development harness did not expose a connected game card. Visible names: {string.Join(" | ", names)}");
    }

    public void VerifyModManagerWorkspaceContract()
    {
        var run = FindByName("Run selected tool");
        Assert(run is not null, "GRID Mod Manager did not expose the tool Run surface.");
        var tool = FindByName("Tool or launch target");
        Assert(tool is not null, "GRID Mod Manager did not expose the deterministic tool selector.");
    }

    public void VerifyGlobalNavigation()
    {
        var add = FindByName("Add game");
        Assert(add is not null && add.Current.IsEnabled, "Production Home did not expose enabled onboarding.");
        foreach (var destination in new[] { "Activities", "Settings" })
        {
            Activate(destination);
            VerifyNoPageLevelHorizontalScroll();
        }
    }

    public void VerifyCanonicalCatalogInspection()
    {
        Activate("View");
        Thread.Sleep(150);
        Activate("Catalog Review");
        WaitForElementName("Canonical catalog inspection panels", TimeSpan.FromSeconds(10));
        var names = DescendantNames();
        foreach (var required in new[]
        {
            "Catalog Review", "Select preproduction catalog package", "Canonical catalog inspection panels",
            "Summary", "Records", "Relationships", "Evidence", "Unresolved", "Conflicts",
            "Sources / Revisions", "Package / Validation", "Structurally valid",
        })
        {
            Assert(names.Any(name => name.Equals(required, StringComparison.OrdinalIgnoreCase)),
                $"Catalog Review did not expose '{required}'. Visible names: {string.Join(" | ", names)}");
        }
        foreach (var forbidden in new[]
        {
            "Edit terminology", "Rename record", "Create alias", "Choose winner", "Delete record",
            "Approve package", "Publish package", "Resolve Other",
        })
        {
            Assert(!names.Any(name => name.Equals(forbidden, StringComparison.OrdinalIgnoreCase)),
                $"Catalog Review exposed forbidden mutation command '{forbidden}'.");
        }
        Capture("canonical-catalog-review-summary");
    }

    public void VerifyCanonicalDifOnly()
    {
        AutomationElement? panel = null;
        var shellDeadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < shellDeadline)
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            panel = FindByName("Grid Assistant panel");
            if (panel is not null && !panel.Current.IsOffscreen && !panel.Current.BoundingRectangle.IsEmpty) break;
            var show = FindByName("Show Grid Assistant");
            if (show is not null && show.Current.IsEnabled)
            {
                ActivateElement(show, "Show Grid Assistant");
                Thread.Sleep(250);
            }
            else Thread.Sleep(150);
        }
        Assert(panel is not null && !panel.Current.IsOffscreen && !panel.Current.BoundingRectangle.IsEmpty,
            "The bounded DIF verification could not expose the assistant shell after startup settled.");
        WaitForAssistantIntake();
        Activate("Toggle data intake form");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        string[] names;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            names = DescendantNames();
            if (names.Any(name => name.Equals("Grid data intake form", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Select game", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Select Goal", StringComparison.Ordinal))) break;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < deadline);

        foreach (var required in new[]
        {
            "Grid data intake form", "Select game", "Select profile", "Select Class", "Select Problem", "Select Timing",
            "Select multiple tools", "Select multiple mods", "Select Location", "Select Mission/Quest", "Select Item",
            "Select Actor", "Select Goal", "Authorization information", "Grid message composer", "Investigation suggestions",
        })
        {
            Assert(names.Any(name => name.Equals(required, StringComparison.Ordinal)),
                $"Canonical Ticket DIF did not expose '{required}'.");
        }
        Assert(!names.Any(name => name.Equals("Expected behavior", StringComparison.Ordinal) ||
                                  name.Equals("Reproduction or location", StringComparison.Ordinal) ||
                                  name.Equals("Desired outcome", StringComparison.Ordinal) ||
                                  name.Equals("Attach screenshots or files", StringComparison.Ordinal)),
            "Canonical Ticket DIF retained legacy prose or duplicate attachment controls.");
        var composer = FindByName("Grid message composer");
        var suggestions = FindByName("Investigation suggestions");
        Assert(composer is not null && !composer.Current.IsOffscreen &&
               suggestions is not null && !suggestions.Current.IsOffscreen,
            "Opening the compact DIF hid the fixed Composer or investigation suggestions.");
        Capture("canonical-ticket-dif");
    }

    /// <summary>Walks the shared local DIF scaffolds and verifies reversible context selection on an attached product.</summary>
    public bool VerifySelectorPresentationAttached(string reportPath) =>
        VerifyDifScaffoldsAttached(reportPath, presentationOnly: true);

    public bool VerifyDifLayoutCleanupAttached(string reportPath) =>
        VerifyDifScaffoldsAttached(reportPath, presentationOnly: true, layoutCleanup: true);

    public bool VerifyDifScaffoldsAttached(string reportPath, bool remainingItemActorOnly = false, bool presentationOnly = false, bool layoutCleanup = false)
    {
        if (ownsProcess) throw new InvalidOperationException("DIF scaffold acceptance requires an attached process.");
        KnowledgeKind[] kinds = remainingItemActorOnly ? [KnowledgeKind.Item, KnowledgeKind.Actor] :
            [KnowledgeKind.Location, KnowledgeKind.MissionQuest, KnowledgeKind.Item, KnowledgeKind.Actor];
        var steps = new List<Location2AStep>();
        var navigation = new List<object>();
        var levels = new List<object>();
        var views = new List<object>();
        var diagnostics = new List<string>();
        var initial = new Dictionary<KnowledgeKind, string[]>();
        var final = new Dictionary<KnowledgeKind, string[]>();
        var owned = new List<(KnowledgeKind Kind, string Id, string Label)>();
        var cleanupErrors = new List<string>();
        var selectionEvidence = new List<object>();
        var screenshots = new List<string>();
        var presentationEvidence = new List<object>();
        var flyoutBounds = new Dictionary<string, System.Windows.Rect>();
        var pixelScale = GetDpiForWindow(process.MainWindowHandle) / 96d;
        var marker = "GRID scaffold check " + Guid.NewGuid().ToString("N");
        var passed = false;
        string? error = null;
        string? originalComposer = null;
        string? finalComposer = null;
        string Context(KnowledgeKind kind) => kind switch
        {
            KnowledgeKind.MissionQuest => "MissionOrQuest", KnowledgeKind.Actor => "Entity", _ => kind.ToString(),
        };
        string EditorName(KnowledgeKind kind) => kind switch
        {
            KnowledgeKind.Location => "Other location", KnowledgeKind.MissionQuest => "Other mission or quest",
            KnowledgeKind.Item => "Other item", _ => "Other actor",
        };
        void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
        void Refresh() => root = AutomationElement.FromHandle(process.MainWindowHandle);
        void Await(Func<bool> predicate, string detail)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !process.HasExited)
            {
                Refresh();
                try { if (predicate()) return; }
                catch (ElementNotAvailableException) { }
                Thread.Sleep(10);
            }
            throw new InvalidOperationException(detail);
        }
        void Invoke(AutomationElement element)
        {
            var name = element.Current.Name;
            try { ActivateElement(element, name); }
            catch (COMException failure) { diagnostics.Add(name + ": " + failure.Message); }
            // Never retry a disposed peer blindly. Every caller verifies its exact resulting state.
        }
        void Step(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); steps.Add(new(name, "PASS", watch.Elapsed.TotalMilliseconds, null)); Console.WriteLine("PASS " + name); }
            catch (Exception failure) { steps.Add(new(name, "FAIL", watch.Elapsed.TotalMilliseconds, failure.Message)); throw; }
        }
        AutomationElement Selector(KnowledgeKind kind) => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Select " + SelectorScaffoldContract.Title(kind)))
            .Cast<AutomationElement>().First(element => !element.Current.IsOffscreen &&
                element.Current.AutomationId == $"canonical-selector:{kind}" &&
                element.Current.ItemStatus.StartsWith(SelectorScaffoldContract.Title(kind) + " scaffold", StringComparison.Ordinal));
        string[] Preview(KnowledgeKind kind)
        {
            var element = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty,
                "scaffold-preview:" + Context(kind))).Cast<AutomationElement>().FirstOrDefault();
            Require(element is not null, "Missing ordered preview for " + kind);
            return element!.Current.HelpText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        string? Composer() => (FindVisibleByAutomationId("ComposerText") ?? FindVisibleByName("Grid message composer")) is { } element &&
            element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? ((ValuePattern)pattern).Current.Value : null;
        string VisibleCaption(AutomationElement element) => string.Join(" ", element.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
            .Where(value => !value.Current.IsOffscreen).Select(value => value.Current.Name));
        void EqualRow(string row, AutomationElement[] elements)
        {
            var bounds = elements.Select(element => element.Current.BoundingRectangle).ToArray();
            Require(bounds.All(value => !value.IsEmpty && value.Width > 0), row + " contains an unrendered cell.");
            Require(bounds.Max(value => value.Width) - bounds.Min(value => value.Width) <= 1.5 &&
                bounds.Max(value => value.Top) - bounds.Min(value => value.Top) <= 1.5,
                row + " must remain one equal-width horizontal row.");
            presentationEvidence.Add(new { row, bounds = bounds.Select(value => new { value.X, value.Y, value.Width, value.Height }).ToArray() });
        }
        void VerifyCleanupGeometry()
        {
            Require(pixelScale > 0, "Could not establish GRID's window DPI scale.");
            var viewport = FindVisibleByAutomationId("ChatContentScrollViewer") ?? FindVisibleByName("Grid chat content")
                ?? throw new InvalidOperationException("Chat content viewport is not exposed.");
            var form = FindVisibleByAutomationId("IntakeForm") ?? throw new InvalidOperationException("DIF is absent.");
            var outer = viewport.Current.BoundingRectangle;
            var inner = form.Current.BoundingRectangle;
            var left = (inner.Left - outer.Left) / pixelScale;
            var right = (outer.Right - inner.Right) / pixelScale;
            var top = (inner.Top - outer.Top) / pixelScale;
            Require(new[] { left, right, top }.All(value => Math.Abs(value - 5) <= 1),
                $"DIF outer insets must be 5 DIPs: actual left={left:F2},right={right:F2},top={top:F2}.");
            var fields = new[] { "GameSelector", "ProfileSelector", "ClassSelector", "ProblemSelector", "TimingSelector", "GoalSelector" }
                .Select(id => FindVisibleByAutomationId(id) ?? throw new InvalidOperationException("Missing field " + id))
                .Concat(kinds.Select(Selector)).ToArray();
            Require(fields.All(element => Math.Abs(element.Current.BoundingRectangle.Height / pixelScale - 32) <= 1),
                "Every closed single and scaffold selector must share the 32-DIP height.");
            var goal = FindVisibleByAutomationId("GoalSelector")!.Current.BoundingRectangle;
            var middle = FindVisibleByAutomationId("ProblemSelector")!.Current.BoundingRectangle;
            Require(Math.Abs((goal.Left + goal.Width / 2) - (middle.Left + middle.Width / 2)) <= 1.5 * pixelScale &&
                Math.Abs(goal.Width - middle.Width) <= 1.5 * pixelScale,
                "Goal must occupy the centered one-third column using the shared field geometry.");
            presentationEvidence.Add(new { check = "cleanup-geometry", pixelScale,
                insetDips = new { left, right, top },
                fieldHeightsDips = fields.Select(element => new { id = element.Current.AutomationId, height = element.Current.BoundingRectangle.Height / pixelScale }).ToArray(),
                goal = new { goal.X, goal.Y, goal.Width, goal.Height } });
        }
        void MeasureFlyout(KnowledgeKind kind, SelectorScaffoldNode? parent)
        {
            var surface = FindVisibleByAutomationId($"selector-flyout-surface:{kind}");
            if (surface is null)
            {
                // WinUI may hoist an unnamed Grid even with AccessibilityView.Control.
                // Measure the actual rendered Flyout peer instead of requiring a test-only container.
                AutomationElement? candidate = FindVisibleByAutomationIdPrefix($"scaffold-row:{kind}:").FirstOrDefault();
                for (var depth = 0; depth < 16 && candidate is not null; depth++)
                {
                    if (candidate.Current.ClassName == "Flyout") { surface = candidate; break; }
                    candidate = TreeWalker.RawViewWalker.GetParent(candidate);
                }
            }
            Require(surface is not null, "Rendered flyout ancestor is absent: " + kind);
            var bounds = surface!.Current.BoundingRectangle;
            Require(bounds.Width > 0 && bounds.Height > 0, "Measured flyout surface has empty bounds.");
            var key = kind + ":" + (parent?.Key ?? "root");
            flyoutBounds[key] = bounds;
            presentationEvidence.Add(new { check = "content-sized-flyout", key,
                measuredPeer = surface.Current.ClassName,
                rowCount = (parent?.Children ?? SelectorScaffoldContract.Roots(kind)).Length,
                widthDips = bounds.Width / pixelScale, heightDips = bounds.Height / pixelScale,
                screenBounds = new { bounds.X, bounds.Y, bounds.Width, bounds.Height } });
        }
        void VerifyClassFlyout()
        {
            AutomationElement[] VisibleRadios() => root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton))
                .Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen).ToArray();
            var selector = FindVisibleByAutomationId("ClassSelector") ?? throw new InvalidOperationException("Class selector is absent.");
            var originalCaption = VisibleCaption(selector);
            Invoke(selector);
            try
            {
                // The noninteractive ClassOptions StackPanel is hoisted out of the UIA tree.
                // Its actual radio peers and scroll viewport remain directly observable.
                Await(() => VisibleRadios().Length > 0, "Class options did not open.");
                AutomationElement? viewport = null;
                var ancestors = new List<string>();
                AutomationElement? candidate = VisibleRadios()[0];
                for (var depth = 0; depth < 12 && candidate is not null; depth++)
                {
                    ancestors.Add(candidate.Current.ClassName + ":" + candidate.Current.AutomationId);
                    if (candidate.Current.ClassName.Contains("ScrollViewer", StringComparison.Ordinal)) { viewport = candidate; break; }
                    candidate = TreeWalker.RawViewWalker.GetParent(candidate);
                }
                Require(viewport is not null, "Could not identify the actual Class scroll viewport: " + string.Join(" / ", ancestors));
                var bounds = viewport!.Current.BoundingRectangle;
                var radioRows = viewport.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)).Cast<AutomationElement>().ToArray();
                var rowCount = radioRows.Length;
                var rowBounds = radioRows.Select(element => element.Current.BoundingRectangle).Where(value => !value.IsEmpty).ToArray();
                var contentHeight = rowBounds.Length == 0 ? 0 : rowBounds.Max(value => value.Bottom) - rowBounds.Min(value => value.Top);
                var window = root.Current.BoundingRectangle;
                if (rowCount > 10 && window.Height / pixelScale > 500)
                    Require(bounds.Height / pixelScale > 260, "Long Class flyout retained its old 260-DIP cap despite available window space.");
                Require(bounds.Height <= window.Height && bounds.Width <= window.Width, "Class flyout exceeds its available window.");
                var canScroll = viewport.TryGetCurrentPattern(ScrollPattern.Pattern, out var scroll) && ((ScrollPattern)scroll).Current.VerticallyScrollable;
                if (contentHeight > bounds.Height + pixelScale) Require(canScroll, "Clamped Class content cannot scroll.");
                presentationEvidence.Add(new { check = "single-class-content-sized-flyout", rowCount,
                    widthDips = bounds.Width / pixelScale, heightDips = bounds.Height / pixelScale,
                    contentHeightDips = contentHeight / pixelScale, canScroll, ancestors });
                Snapshot("dif-layout-class-flyout");
            }
            finally
            {
                if (VisibleRadios().Length > 0)
                {
                    Require(GetForegroundWindow() == process.MainWindowHandle, "GRID lost foreground; Class dismissal was not sent.");
                    PressVirtualKey(VirtualKeyEscape);
                    Await(() => VisibleRadios().Length == 0, "Class flyout did not close.");
                    SettledClosed();
                }
            }
            Require(VisibleCaption(FindVisibleByAutomationId("ClassSelector")!) == originalCaption,
                "Opening/dismissing the Class flyout changed its selection.");
        }
        void Snapshot(string label)
        {
            Require(GetForegroundWindow() == process.MainWindowHandle, "GRID lost foreground before its window capture.");
            if (!GetWindowRect(process.MainWindowHandle, out var rect)) return;
            using var bitmap = new Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, label + ".png");
            bitmap.Save(path);
            screenshots.Add(path);
        }
        void SettledClosed()
        {
            Stopwatch? settled = null;
            Await(() =>
            {
                if (FindVisibleByAutomationIdPrefix("scaffold-row:").Length != 0)
                { settled = null; return false; }
                settled ??= Stopwatch.StartNew();
                return settled.ElapsedMilliseconds >= 250;
            }, "Scaffold flyout did not finish closing.");
        }
        void Close()
        {
            if (FindVisibleByAutomationIdPrefix("scaffold-row:").Length == 0) return;
            Require(GetForegroundWindow() == process.MainWindowHandle, "GRID lost foreground; global dismissal was not sent.");
            PressVirtualKey(VirtualKeyEscape);
            SettledClosed();
        }
        void Transition(KnowledgeKind kind, AutomationElement target, string path, string operation)
        {
            // Discovery and the complete schema audit are outside this invocation-to-visible-row interval.
            var watch = Stopwatch.StartNew();
            Invoke(target);
            Await(() => FindVisibleByAutomationId($"scaffold-row:{kind}:{path}") is not null,
                $"{operation} did not expose {kind}/{path}.");
            navigation.Add(new { kind = kind.ToString(), operation, path, invokeToVisibleRowMilliseconds = watch.Elapsed.TotalMilliseconds });
        }
        void Open(KnowledgeKind kind)
        {
            SettledClosed();
            Transition(kind, Selector(kind), SelectorScaffoldContract.Roots(kind)[0].Key, "root/reopen");
        }
        void VerifyLevel(KnowledgeKind kind, SelectorScaffoldNode? parent)
        {
            var expected = parent?.Children ?? SelectorScaffoldContract.Roots(kind);
            var actual = FindVisibleByAutomationIdPrefix($"scaffold-row:{kind}:");
            Require(actual.Select(element => element.Current.AutomationId).SequenceEqual(
                expected.Select(node => $"scaffold-row:{kind}:{node.Key}")), "Immediate row order/identity differs at " + kind + "/" + parent?.Key);
            for (var index = 0; index < expected.Length; index++)
            {
                var node = expected[index];
                Require(actual[index].Current.Name == "Select " + node.Label &&
                    actual[index].Current.HelpText == SelectorScaffoldContract.DisplayPath(kind, node.Key), "Label or full path differs for " + node.Key);
                Require(actual[index].TryGetCurrentPattern(InvokePattern.Pattern, out _), "Scaffold label is not selectable: " + node.Key);
                Require((FindVisibleByAutomationId($"scaffold-descend:{kind}:{node.Key}") is not null) == !node.Children.IsEmpty,
                    "Chevron differs from descendant availability for " + node.Key);
            }
            Require((FindVisibleByAutomationId($"scaffold-back:{kind}") is not null) == (parent is not null), "Back scope differs.");
            Require((FindVisibleByAutomationId($"canonical-other:{kind}") is not null) == (parent is null), "Other must appear at root only.");
            Require(FindVisibleByAutomationId($"scaffold-helper:{kind}") is null, "Presentation retained the redundant visible scaffold helper.");
            foreach (var prefix in new[] { "canonical-record:", "canonical-path:", "canonical-descend:" })
                Require(FindVisibleByAutomationIdPrefix(prefix).Length == 0, "Canonical content leaked into scaffold: " + prefix);
            var status = Selector(kind).Current.ItemStatus;
            Require(status.Contains("canonical queries: 0; no catalog access.", StringComparison.Ordinal) &&
                status.Contains("Canonical records: 0.", StringComparison.Ordinal), "Scaffold reports catalog access: " + status);
            levels.Add(new { kind = kind.ToString(), parent = parent?.Key, labels = expected.Select(node => node.Label).ToArray(),
                paths = expected.Select(node => node.Key).ToArray(), status });
            if (presentationOnly)
            {
                var context = FindVisibleByAutomationId($"scaffold-context:{kind}");
                var separator = FindVisibleByAutomationId($"scaffold-context-separator:{kind}");
                Require((context is not null) == (parent is not null), "Root must omit redundant L1 context; nested level must show its context.");
                if (parent is not null)
                {
                    Require(context!.Current.Name == parent.Label && !context.TryGetCurrentPattern(InvokePattern.Pattern, out _),
                        "Nested context must be its plain label, not a selectable option.");
                    Require(separator is not null, "Nested context separator is absent.");
                    Require(VisibleCaption(Selector(kind)) == parent.Label, "Closed field caption does not follow the traversed context.");
                }
                presentationEvidence.Add(new { kind = kind.ToString(), parent = parent?.Key,
                    caption = VisibleCaption(Selector(kind)), context = context?.Current.Name, separator = separator is not null });
            }
            if (layoutCleanup) MeasureFlyout(kind, parent);
        }
        void Walk(KnowledgeKind kind, SelectorScaffoldNode? parent)
        {
            VerifyLevel(kind, parent);
            if (parent is null && kind is KnowledgeKind.Item or KnowledgeKind.Actor) Snapshot("dif-scaffold-" + kind + "-root");
            if (presentationOnly && kind == KnowledgeKind.Location && parent?.Key == "world") Snapshot("selector-presentation-nested-context");
            var children = parent?.Children ?? SelectorScaffoldContract.Roots(kind);
            if (parent is not null && !parent.SortViews.IsEmpty)
            {
                foreach (var view in parent.SortViews.Prepend("Default").Append("Default"))
                {
                    Invoke(FindVisibleByAutomationId($"scaffold-sort:{kind}:{view}") ?? throw new InvalidOperationException("Missing sort view " + view));
                    Await(() => FindVisibleByAutomationId($"scaffold-sort:{kind}:{view}")?.Current.ItemStatus == "Selected sort view", "Sort view did not change UI state.");
                    VerifyLevel(kind, parent);
                    views.Add(new { kind = kind.ToString(), parent = parent.Key, view, unchangedChildPaths = children.Select(node => node.Key).ToArray() });
                    if (kind == KnowledgeKind.Item && parent.Key == "vehicles" && view == "Manufacturer") Snapshot("dif-scaffold-vehicle-sort");
                }
            }
            if (presentationOnly && parent is not null) return;
            foreach (var node in children.Where(node => !node.Children.IsEmpty).Take(presentationOnly ? 1 : int.MaxValue))
            {
                Transition(kind, FindVisibleByAutomationId($"scaffold-descend:{kind}:{node.Key}")!, node.Children[0].Key, "descent");
                Walk(kind, node);
                Transition(kind, FindVisibleByAutomationId($"scaffold-back:{kind}")!, children[0].Key, "back");
                VerifyLevel(kind, parent);
            }
        }
        void SelectPath(KnowledgeKind kind, string path, string[] expected)
        {
            Open(kind);
            var segments = path.Split('/');
            var prefix = segments[0];
            for (var index = 1; index < segments.Length; index++)
            {
                var next = prefix + "/" + segments[index];
                Transition(kind, FindVisibleByAutomationId($"scaffold-descend:{kind}:{prefix}")!, next, "selection-descent");
                prefix = next;
            }
            Invoke(FindVisibleByAutomationId($"scaffold-row:{kind}:{path}")!);
            Await(() => Preview(kind).SequenceEqual(expected), "Label click did not produce the exact ordered preview: " + path);
            if (presentationOnly && expected.Contains(SelectorScaffoldContract.Resolve(kind, path)!.Label))
                Require(VisibleCaption(Selector(kind)) == SelectorScaffoldContract.Resolve(kind, path)!.Label,
                "Selected field caption is not the selected value alone.");
            SettledClosed();
        }
        void Remove(KnowledgeKind kind, string id, string[] expected)
        {
            Close();
            var target = FindVisibleByAutomationId($"scaffold-remove:{Context(kind)}:{id}");
            if (target is null)
            {
                Invoke(FindVisibleByAutomationId("scaffold-overflow:" + Context(kind)) ?? throw new InvalidOperationException("Hidden selection has no overflow."));
                Await(() => FindVisibleByAutomationId($"scaffold-remove:{Context(kind)}:{id}") is not null, "Overflow did not expose owned selection.");
                target = FindVisibleByAutomationId($"scaffold-remove:{Context(kind)}:{id}");
            }
            Invoke(target!);
            Await(() => Preview(kind).SequenceEqual(expected), "Removal changed order or another selection.");
        }
        try
        {
            Step("preserve-existing-draft", () =>
            {
                Refresh();
                Require(FindVisibleByName("Grid data intake form") is not null, "Open the existing DIF before attached acceptance.");
                Require(GetForegroundWindow() == process.MainWindowHandle, "GRID must be foreground before bounded attached acceptance.");
                originalComposer = Composer();
                Require(originalComposer is not null, "The existing composer could not be read for preservation.");
                foreach (var kind in kinds)
                {
                    Require(Selector(kind).Current.IsEnabled, "A connected profile is required: " + kind);
                    initial[kind] = Preview(kind);
                    var editor = FindVisibleByName(EditorName(kind));
                    Require(editor is null || !editor.TryGetCurrentPattern(ValuePattern.Pattern, out var value) ||
                        string.IsNullOrEmpty(((ValuePattern)value).Current.Value), "Uncommitted Other text is preserved; cannot overwrite " + kind);
                }
                if (presentationOnly)
                {
                    foreach (var id in new[] { "GameSelector", "ProfileSelector", "ClassSelector" })
                    {
                        var element = FindVisibleByAutomationId(id) ?? throw new InvalidOperationException("Missing single selector " + id);
                        var caption = VisibleCaption(element);
                        Require(caption.Length > 0 && !caption.Contains('·') && !caption.Contains("selected", StringComparison.OrdinalIgnoreCase),
                            "Single selector retains a field prefix/status/count: " + caption);
                        presentationEvidence.Add(new { selector = id, caption });
                    }
                    EqualRow("row2", new[] { "ClassSelector", "ProblemSelector", "TimingSelector" }
                        .Select(id => FindVisibleByAutomationId(id) ?? throw new InvalidOperationException("Missing row2 cell " + id)).ToArray());
                    EqualRow("row4", kinds.Select(Selector).ToArray());
                    if (layoutCleanup)
                    {
                        VerifyCleanupGeometry();
                        Snapshot("dif-layout-closed-fields");
                    }
                }
                Close();
            });
            if (layoutCleanup) Step("single-class-content-sized-flyout", VerifyClassFlyout);
            foreach (var kind in kinds)
            {
                Step(kind + (presentationOnly ? "-bounded-presentation-levels-and-sort-views" : "-complete-tree-and-sort-views"), () =>
                {
                    Open(kind); Walk(kind, null);
                    if (layoutCleanup && kind == KnowledgeKind.Item)
                    {
                        var consumables = SelectorScaffoldContract.Resolve(kind, "consumables")!;
                        var ingredients = SelectorScaffoldContract.Resolve(kind, "consumables/ingredients")!;
                        Transition(kind, FindVisibleByAutomationId($"scaffold-descend:{kind}:{consumables.Key}")!, consumables.Children[0].Key, "content-width-descent");
                        Transition(kind, FindVisibleByAutomationId($"scaffold-descend:{kind}:{ingredients.Key}")!, ingredients.Children[0].Key, "long-label-descent");
                        VerifyLevel(kind, ingredients);
                        Snapshot("dif-layout-long-label-flyout");
                        Transition(kind, FindVisibleByAutomationId($"scaffold-back:{kind}")!, consumables.Children[0].Key, "long-label-back");
                        Transition(kind, FindVisibleByAutomationId($"scaffold-back:{kind}")!, SelectorScaffoldContract.Roots(kind)[0].Key, "content-width-back");
                        VerifyLevel(kind, null);
                    }
                    Close();
                });
                Step(kind + "-ordered-context-other-overflow-toggle-removal", () =>
                {
                    var all = SelectorScaffoldContract.Enumerate(kind).ToArray();
                    var leaf = all.FirstOrDefault(node => node.Children.IsEmpty &&
                        !initial[kind].Contains(node.Label));
                    Require(leaf is not null, "No unselected leaf remains for reversible test: " + kind);
                    var chain = leaf!.Key.Split('/').Select((_, index) => string.Join('/', leaf.Key.Split('/').Take(index + 1)))
                        .Where(path => !initial[kind].Contains(SelectorScaffoldContract.Resolve(kind, path)!.Label)).ToArray();
                    var paths = new List<string> { chain[0] };
                    if (chain.Length > 2) paths.Add(chain[1]);
                    if (!paths.Contains(chain[^1])) paths.Add(chain[^1]);
                    foreach (var node in all.Where(node => !paths.Contains(node.Key) && !initial[kind].Contains(node.Label)))
                    {
                        if (paths.Count == 3) break;
                        paths.Add(node.Key);
                    }
                    Require(paths.Count == 3, "Three unselected contexts are required without touching the existing draft: " + kind);
                    var expected = initial[kind].ToList();
                    void AddPath(string path)
                    {
                        var label = SelectorScaffoldContract.Resolve(kind, path)!.Label;
                        owned.Add((kind, path, label)); expected.Add(label);
                        SelectPath(kind, path, expected.ToArray());
                    }
                    AddPath(paths[0]);
                    Open(kind);
                    Invoke(FindVisibleByAutomationId($"canonical-other:{kind}")!);
                    Await(() => FindVisibleByName(EditorName(kind)) is not null, "Other editor did not open.");
                    var editor = FindVisibleByName(EditorName(kind))!;
                    var text = marker + " " + kind;
                    var otherLabel = text;
                    owned.Add((kind, "other:" + text, otherLabel)); expected.Add(otherLabel);
                    ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(text);
                    editor.SetFocus();
                    Require(GetForegroundWindow() == process.MainWindowHandle, "GRID lost foreground; Enter was not sent.");
                    PressVirtualKey(VirtualKeyEnter);
                    Await(() => Preview(kind).SequenceEqual(expected), "Enter did not append Other in selection order.");
                    if (presentationOnly) Require(VisibleCaption(Selector(kind)) == text,
                        "Other submission must show its value alone in the field caption.");
                    var remainingEditor = FindVisibleByName(EditorName(kind));
                    Require(remainingEditor is null || ((ValuePattern)remainingEditor.GetCurrentPattern(ValuePattern.Pattern)).Current.Value.Length == 0,
                        "Other did not clear after Enter.");
                    SettledClosed();
                    AddPath(paths[1]); AddPath(paths[2]);
                    var overflow = FindVisibleByAutomationId("scaffold-overflow:" + Context(kind));
                    Require(overflow is not null && overflow.Current.HelpText.Split('\n', StringSplitOptions.RemoveEmptyEntries).SequenceEqual(expected),
                        "Overflow does not retain the complete ordered preview.");
                    if (presentationOnly)
                    {
                        var preview = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty,
                            "scaffold-preview:" + Context(kind))).Cast<AutomationElement>().First();
                        var visibleLabels = preview.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)).Cast<AutomationElement>()
                            .Where(element => !element.Current.IsOffscreen).Select(element => element.Current.Name)
                            .Where(value => value is not "x" and not "...").ToArray();
                        Require(visibleLabels.SequenceEqual(expected.Take(3)), "Inline preview must show only ordered value labels, not full paths or prefixes.");
                        presentationEvidence.Add(new { kind = kind.ToString(), visiblePreview = visibleLabels, orderedOverflow = expected.ToArray() });
                        if (kind == KnowledgeKind.Item) Snapshot("selector-presentation-simple-preview");
                    }
                    selectionEvidence.Add(new { kind = kind.ToString(), orderedPreview = expected.ToArray(), scaffoldPaths = paths.ToArray(), otherInterleaved = true });
                    var last = owned.Last(entry => entry.Kind == kind);
                    expected.Remove(last.Label); Remove(kind, last.Id, expected.ToArray()); owned.Remove(last);
                    var first = owned.First(entry => entry.Kind == kind);
                    expected.Remove(first.Label); SelectPath(kind, first.Id, expected.ToArray()); owned.Remove(first);
                    foreach (var entry in owned.Where(entry => entry.Kind == kind).ToArray())
                    {
                        expected.Remove(entry.Label); Remove(kind, entry.Id, expected.ToArray()); owned.Remove(entry);
                    }
                    Require(Preview(kind).SequenceEqual(initial[kind]), "Existing selections changed after cleanup.");
                });
            }
            if (layoutCleanup)
            {
                var itemRoot = flyoutBounds["Item:root"];
                var actorRoot = flyoutBounds["Actor:root"];
                var longLabel = flyoutBounds["Item:consumables/ingredients"];
                var shortLabel = flyoutBounds["MissionQuest:dlc"];
                Require(itemRoot.Height > actorRoot.Height + 80 * pixelScale,
                    "Seven-row Item and two-row Actor flyouts retained the same oversized height.");
                Require(longLabel.Width > shortLabel.Width + 20 * pixelScale && longLabel.Width > actorRoot.Width + 20 * pixelScale,
                    "Flyout width did not grow for the widest label and shrink for shorter content.");
                presentationEvidence.Add(new { check = "dynamic-size-comparison", itemRootHeightDips = itemRoot.Height / pixelScale,
                    actorRootHeightDips = actorRoot.Height / pixelScale, longLabelWidthDips = longLabel.Width / pixelScale,
                    shortLabelWidthDips = shortLabel.Width / pixelScale, actorRootWidthDips = actorRoot.Width / pixelScale });
            }
            finalComposer = Composer();
            Require(finalComposer == originalComposer, "Acceptance changed the message composer.");
            passed = true;
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            foreach (var entry in owned.ToArray())
            {
                try
                {
                    Refresh();
                    var current = Preview(entry.Kind);
                    if (current.Contains(entry.Label)) Remove(entry.Kind, entry.Id, current.Where(value => value != entry.Label).ToArray());
                    var editor = FindVisibleByName(EditorName(entry.Kind));
                    if (editor is not null && editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                        ((ValuePattern)pattern).Current.Value.StartsWith(marker, StringComparison.Ordinal))
                        ((ValuePattern)pattern).SetValue(string.Empty);
                }
                catch (Exception failure) { cleanupErrors.Add(entry.Kind + ": " + failure.Message); passed = false; }
            }
            try
            {
                Close();
                foreach (var kind in initial.Keys)
                {
                    final[kind] = Preview(kind);
                    if (!final[kind].SequenceEqual(initial[kind])) { cleanupErrors.Add("Original context differs: " + kind); passed = false; }
                }
                finalComposer = Composer();
            }
            catch (Exception failure) { cleanupErrors.Add(failure.Message); passed = false; }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenario = layoutCleanup ? "grid.dif-layout-cleanup.attach-only" : presentationOnly ? "grid.selector-presentation.attach-only" : "grid.dif-scaffolds.attach-only", processId = process.Id,
            remainingItemActorOnly,
            status = passed ? "PASS" : "FAIL", measuredUtc = DateTimeOffset.UtcNow, error, cleanupErrors,
            steps, navigation, levels, views, selectionEvidence, presentationEvidence, screenshots, diagnostics, initial, final, originalComposer, finalComposer,
            timingNote = "Target discovery and full UIA schema enumeration are outside navigation intervals; flyout close settles separately for 250 ms.",
            registrationInvoked = false, investigationSubmitted = false, processLeftRunning = !process.HasExited,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    /// <summary>Exercises only the empty Location scaffold on an existing product; no catalog or registration entry point is invoked.</summary>
    public bool VerifyLocationScaffoldAttached(string reportPath)
    {
        if (ownsProcess) throw new InvalidOperationException("Scaffold acceptance requires an attached process.");
        string[] labels = ["World", "Continent", "Country", "State", "County/Region", "City", "Town/Neighborhood", "Street", "Structure", "Room"];
        var steps = new List<Location2AStep>();
        var statuses = new List<string>();
        var interactionMeasurements = new List<object>();
        var marker = "GRID scaffold acceptance " + Guid.NewGuid().ToString("N");
        string[] originalOther = [];
        var markerOwned = false;
        var passed = false;
        string? error = null;
        string? cleanupError = null;
        void Refresh() => root = AutomationElement.FromHandle(process.MainWindowHandle);
        AutomationElement Selector() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Select Location")).Cast<AutomationElement>()
            .First(value => !value.Current.IsOffscreen && value.Current.ItemStatus.StartsWith("Location scaffold", StringComparison.Ordinal));
        string[] OtherLabels() => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Where(value => !value.Current.IsOffscreen && value.Current.Name.StartsWith("Remove location ", StringComparison.Ordinal))
            .Select(value => value.Current.Name["Remove location ".Length..]).ToArray();
        void Await(Func<bool> condition, string detail)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(5) && !process.HasExited)
            {
                Refresh();
                if (condition()) return;
                Thread.Sleep(15);
            }
            throw new InvalidOperationException(detail);
        }
        void Invoke(AutomationElement element)
        {
            try { ActivateElement(element, element.Current.Name); }
            catch (COMException) { /* A disposed peer may follow a successful click; exact postconditions decide. */ }
        }
        void InvokeToLevel(AutomationElement target, int expectedLevel, string operation)
        {
            // Target discovery is complete before the timer. One exact-ID lookup observes readiness;
            // the full label/canonical-leak/preservation audit follows outside this interval.
            var watch = Stopwatch.StartNew();
            Invoke(target);
            AutomationElement? slot = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                slot = FindVisibleByAutomationId($"location-scaffold:slot:{expectedLevel}");
                if (slot is not null) break;
                Thread.Sleep(2);
            }
            var elapsed = watch.Elapsed.TotalMilliseconds;
            interactionMeasurements.Add(new { operation, level = expectedLevel, label = labels[expectedLevel],
                invokeToVisibleSlotMilliseconds = elapsed, success = slot is not null });
            Assert(slot is not null, "Scaffold interaction did not expose its expected slot within 3 seconds.");
        }
        void Step(string name, Action action)
        {
            var watch = Stopwatch.StartNew();
            try { action(); steps.Add(new(name, "PASS", watch.Elapsed.TotalMilliseconds, null)); }
            catch (Exception failure) { steps.Add(new(name, "FAIL", watch.Elapsed.TotalMilliseconds, failure.Message)); throw; }
        }
        void VerifyLevel(int index)
        {
            Await(() => FindVisibleByAutomationId($"location-scaffold:slot:{index}") is not null, $"Scaffold level {index} is absent.");
            var slot = FindVisibleByAutomationId($"location-scaffold:slot:{index}")!;
            Assert(slot.Current.Name == labels[index], "The exact structural label changed.");
            Assert(!slot.TryGetCurrentPattern(InvokePattern.Pattern, out _) &&
                   !slot.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _), "A scaffold label is selectable.");
            Assert(FindVisibleByAutomationIdPrefix("location-scaffold:slot:").Length == 1, "Scaffold must replace one level, not accumulate descendants.");
            Assert(FindVisibleByAutomationId("location-scaffold:helper")?.Current.Name == "Location scaffold — not populated", "Scaffold helper must state that no locations are populated.");
            Assert((FindVisibleByAutomationId("location-scaffold:back") is not null) == (index > 0), "Back visibility does not match the current level.");
            Assert((FindVisibleByAutomationId("canonical-other:Location") is not null) == (index == 0), "Other must be available only at the root.");
            Assert((FindVisibleByAutomationId($"location-scaffold:descend:{index}") is not null) == (index < 9), "Terminal and nonterminal descent behavior differs from the contract.");
            foreach (var prefix in new[] { "canonical-record:", "canonical-path:", "canonical-descend:", "canonical-selection:Location:" })
                Assert(FindVisibleByAutomationIdPrefix(prefix).Length == 0, "Canonical/GTA content leaked into the empty Location scaffold: " + prefix);
            Assert(!Selector().Current.HelpText.StartsWith("canonical-selection:", StringComparison.Ordinal), "Scaffold created a canonical selection coordinate.");
            Assert(OtherLabels().SequenceEqual(originalOther), "Structural navigation changed existing Location Other context.");
            var status = Selector().Current.ItemStatus;
            Assert(status.Contains("Location queries: 0; no catalog access.", StringComparison.Ordinal) &&
                   status.Contains("Selectable records: 0.", StringComparison.Ordinal), "Scaffold reports catalog queries or selectable records.");
            statuses.Add(status);
        }
        void Close()
        {
            Refresh();
            if (FindVisibleByAutomationIdPrefix("location-scaffold:slot:").Length == 0) return;
            SetForegroundWindow(process.MainWindowHandle);
            if (GetForegroundWindow() != process.MainWindowHandle)
                throw new InvalidOperationException("GRID is not foreground; no global dismissal input was sent.");
            PressVirtualKey(VirtualKeyEscape);
            Stopwatch? settled = null;
            Await(() =>
            {
                if (FindVisibleByAutomationIdPrefix("location-scaffold:slot:").Length != 0) { settled = null; return false; }
                settled ??= Stopwatch.StartNew();
                return settled.ElapsedMilliseconds >= 250;
            }, "Scaffold did not settle closed.");
        }
        void Open()
        {
            Refresh();
            InvokeToLevel(Selector(), 0, "root-open-or-reopen");
            VerifyLevel(0);
        }
        try
        {
            Step("preserve-existing-draft", () =>
            {
                Refresh();
                if (FindVisibleByName("Grid data intake form") is null)
                {
                    EnsureAssistantOpen();
                    if (FindVisibleByName("Grid data intake form") is null) Activate("Toggle data intake form");
                }
                Await(() => FindVisibleByAutomationId("canonical-selector:Location") is not null, "Location selector is absent.");
                Assert(Selector().Current.IsEnabled, "Location scaffold requires a selected connected profile.");
                originalOther = OtherLabels();
                Assert(originalOther.Length < 3 && !root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                    .Any(value => !value.Current.IsOffscreen && value.Current.Name.StartsWith("Location selection overflow", StringComparison.Ordinal)),
                    "Existing Other context occupies the removable inline slots; preserved without adding a test marker.");
                var editor = FindVisibleByName("Other location");
                Assert(editor is null || !editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                    string.IsNullOrEmpty(((ValuePattern)pattern).Current.Value), "Existing uncommitted Other text is preserved; attach test cannot overwrite it.");
                Close();
            });
            Step("root-empty-world", Open);
            for (var index = 1; index < labels.Length; index++)
            {
                var level = index;
                Step("descend-" + labels[level], () =>
                {
                    InvokeToLevel(FindVisibleByAutomationId($"location-scaffold:descend:{level - 1}")!, level, "descent");
                    VerifyLevel(level);
                });
            }
            for (var index = labels.Length - 2; index >= 0; index--)
            {
                var level = index;
                Step("back-" + labels[level], () => { InvokeToLevel(FindVisibleByAutomationId("location-scaffold:back")!, level, "back"); VerifyLevel(level); });
            }
            Step("nested-close-reopens-root", () =>
            {
                InvokeToLevel(FindVisibleByAutomationId("location-scaffold:descend:0")!, 1, "descent-before-reopen");
                VerifyLevel(1);
                Close();
                Open();
            });
            Step("other-unresolved-add", () =>
            {
                Invoke(FindVisibleByAutomationId("canonical-other:Location")!);
                Await(() => FindVisibleByName("Other location") is not null, "Other editor is absent.");
                var editor = FindVisibleByName("Other location")!;
                editor.SetFocus();
                markerOwned = true;
                ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(marker);
                Selector().SetFocus();
                Await(() => OtherLabels().SequenceEqual(originalOther.Append(marker)), "Other did not append exact unresolved context while preserving existing order.");
                Assert(!Selector().Current.HelpText.StartsWith("canonical-selection:", StringComparison.Ordinal), "Other became a canonical selection.");
            });
            Step("remove-only-owned-other", () =>
            {
                Invoke(FindVisibleByName("Remove location " + marker)!);
                Await(() => OtherLabels().SequenceEqual(originalOther), "Removing the test marker changed existing Other context.");
                markerOwned = false;
            });
            Step("final-root-stays-empty", () => { Open(); Close(); });
            passed = true;
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            if (markerOwned && !process.HasExited)
            {
                try
                {
                    Refresh();
                    var remove = FindVisibleByName("Remove location " + marker);
                    if (remove is not null) Invoke(remove);
                    var editor = FindVisibleByName("Other location");
                    if (editor is not null && editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                        ((ValuePattern)pattern).Current.Value == marker)
                        ((ValuePattern)pattern).SetValue(string.Empty);
                    Await(() => OtherLabels().SequenceEqual(originalOther), "Owned marker cleanup did not preserve original context.");
                }
                catch (Exception failure) { cleanupError = failure.Message; passed = false; }
            }
        }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, scenario = "grid.location-scaffold.attach-only", processId = process.Id,
            status = passed ? "PASS" : "FAIL", measuredUtc = DateTimeOffset.UtcNow, error, cleanupError,
            labels, steps, interactionMeasurements, statuses, originalOther, finalOther = process.HasExited ? [] : OtherLabels(),
            timingDefinition = "Interaction measurements run from invocation through an exact visible-slot UIA lookup; target discovery and full functional enumeration are outside that timer. Step durations include the full functional audit. UIA visibility is not a CompositionTarget frame timestamp.",
            catalogAccessProof = "Every observed Location status reports zero Location queries. Focused source guards additionally verify detached providers and no canonical query/selection in scaffold rendering.",
            registrationInvoked = false, investigationSubmitted = false, processLeftRunning = !process.HasExited,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    /// <summary>Measures only root opening on the attached product; UIA enumeration is timed separately.</summary>
    public bool MeasureLocation2AAttached(string reportPath)
    {
        if (ownsProcess) throw new InvalidOperationException("Location measurement requires an attached product.");
        var measurements = new List<object>();
        var closeSettleMilliseconds = new List<double>();
        var passed = true;
        long warmPrivateBytes = 0;
        string? error = null;
        AutomationElement Selector() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Select Location")).Cast<AutomationElement>()
            .First(value => !value.Current.IsOffscreen && !string.IsNullOrEmpty(value.Current.ItemStatus));
        try
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            if (FindByAutomationIdPrefix("canonical-selection:Location:").Length > 0)
                throw new InvalidOperationException("Measurement requires the preserved empty Location draft.");
            CloseCanonicalFlyoutIfOpen(TimeSpan.FromSeconds(1));
            for (var cycle = 0; cycle < 21; cycle++)
            {
                root = AutomationElement.FromHandle(process.MainWindowHandle);
                var discover = Stopwatch.StartNew();
                var selector = Selector();
                var discoveryMilliseconds = discover.Elapsed.TotalMilliseconds;
                var timer = Stopwatch.StartNew();
                ActivateElement(selector, "Select Location");
                AutomationElement? other = null;
                while (timer.Elapsed < TimeSpan.FromSeconds(3))
                {
                    other = FindVisibleByAutomationId("canonical-other:Location");
                    if (other is not null && !other.Current.IsOffscreen) break;
                    Thread.Sleep(5);
                }
                var readyMilliseconds = timer.Elapsed.TotalMilliseconds;
                if (other is null || other.Current.IsOffscreen) throw new InvalidOperationException("Location failed to populate within 3 seconds.");
                var enumeration = Stopwatch.StartNew();
                var options = FindByAutomationIdPrefix("canonical-record:");
                var enumerationMilliseconds = enumeration.Elapsed.TotalMilliseconds;
                var itemStatus = selector.Current.ItemStatus;
                process.Refresh();
                if (cycle == 0) warmPrivateBytes = process.PrivateMemorySize64;
                measurements.Add(new { cycle, discoveryMilliseconds, readyMilliseconds, enumerationMilliseconds,
                    realizedRecordControls = options.Length, itemStatus, privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64 });
                // Never click an assumed title-bar coordinate: verify the exact foreground window before dismissal.
                SetForegroundWindow(process.MainWindowHandle);
                if (GetForegroundWindow() != process.MainWindowHandle)
                    throw new InvalidOperationException("GRID is not foreground; no dismissal input was sent.");
                PressVirtualKey(VirtualKeyEscape);
                var closed = Stopwatch.StartNew();
                Stopwatch? stableClosure = null;
                while (closed.Elapsed < TimeSpan.FromSeconds(2))
                {
                    root = AutomationElement.FromHandle(process.MainWindowHandle);
                    if (FindVisibleByAutomationId("canonical-other:Location") is null)
                    {
                        stableClosure ??= Stopwatch.StartNew();
                        if (stableClosure.ElapsedMilliseconds >= 250) break;
                    }
                    else stableClosure = null;
                    Thread.Sleep(5);
                }
                closeSettleMilliseconds.Add(closed.Elapsed.TotalMilliseconds);
                if (stableClosure is null || stableClosure.ElapsedMilliseconds < 250 ||
                    GetForegroundWindow() != process.MainWindowHandle)
                    throw new InvalidOperationException("Location closure did not settle in foreground; no subsequent measurement was attempted.");
            }
        }
        catch (Exception exception) { passed = false; error = exception.Message; }
        process.Refresh();
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            scenario = "grid.location-2a.attached-root-measurement", processId = process.Id, status = passed ? "PASS" : "FAIL", error,
            measuredUtc = DateTimeOffset.UtcNow, measurements, closeSettleMilliseconds, warmPrivateBytes, finalPrivateBytes = process.PrivateMemorySize64,
            privateGrowthBytes = warmPrivateBytes == 0 ? (long?)null : process.PrivateMemorySize64 - warmPrivateBytes,
            timingDefinition = "Invocation from a fully settled closed flyout through visible root Other. A 250 ms continuously hidden close-transition settlement is recorded separately outside opening timers. UIA discovery/enumeration is separate; ItemStatus records product page-query and populated-composition-frame timings.",
            memoryDefinition = "Observed process private bytes, not a forced-GC managed retained-heap measurement.",
            investigationPrepared = false, authorizationInvoked = false, processStartedOrStopped = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed;
    }

    /// <summary>
    /// Exercises only Location on an already attached, visible intake with an empty Location draft.
    /// It neither starts an investigation nor approves a request, and removes only its own test entries.
    /// </summary>
    public bool VerifyLocation2AAttached(string reportPath)
    {
        if (ownsProcess) throw new InvalidOperationException("Location 2A acceptance requires an attached process.");
        var started = DateTimeOffset.UtcNow;
        var steps = new List<Location2AStep>();
        var viewMetrics = new List<string>();
        var rootOptions = Array.Empty<Location2AOption>();
        var nestedOptions = Array.Empty<Location2AOption>();
        var ownedLabels = new HashSet<string>(StringComparer.Ordinal);
        var selectedCoordinates = new List<string>();
        var runtimeStatus = string.Empty;
        var baselineWasEmpty = false;
        var runId = Guid.NewGuid().ToString("N");

        void RefreshRoot() => root = AutomationElement.FromHandle(process.MainWindowHandle);
        AutomationElement[] RemoveButtons() => root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen &&
                element.Current.Name.StartsWith("Remove location ", StringComparison.Ordinal)).ToArray();
        AutomationElement Selector() => root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, "Select Location"))
            .Cast<AutomationElement>().FirstOrDefault(element => !element.Current.IsOffscreen &&
                !string.IsNullOrEmpty(element.Current.ItemStatus))
            ?? throw new InvalidOperationException("LOCATION_SELECTOR_NOT_VISIBLE: Select Location has no visible runtime status.");
        void Await(Func<bool> condition, string detail)
        {
            var timer = Stopwatch.StartNew();
            do
            {
                RefreshRoot();
                if (condition()) return;
                Thread.Sleep(40);
            } while (timer.Elapsed < TimeSpan.FromSeconds(8) && !process.HasExited);
            throw new InvalidOperationException(detail);
        }
        void Step(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                action();
                steps.Add(new(name, "PASS", timer.Elapsed.TotalMilliseconds, null));
                viewMetrics.Add($"{name}: {Selector().Current.ItemStatus}");
            }
            catch (Exception exception)
            {
                steps.Add(new(name, "FAIL", timer.Elapsed.TotalMilliseconds, exception.ToString()));
                throw;
            }
        }
        Location2AOption[] Options() => FindByAutomationIdPrefix("canonical-")
            .Where(element => element.Current.AutomationId.StartsWith("canonical-record:", StringComparison.Ordinal) ||
                              element.Current.AutomationId.StartsWith("canonical-path:", StringComparison.Ordinal) ||
                              element.Current.AutomationId.StartsWith("canonical-descend:", StringComparison.Ordinal))
            .Select(element => new Location2AOption(element.Current.AutomationId, element.Current.Name,
                element.Current.IsOffscreen)).ToArray();
        ScrollPattern? LevelScroll()
        {
            var host = FindVisibleByAutomationId("canonical-level:Location");
            if (host is not null && host.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)) return (ScrollPattern)pattern;
            return null;
        }
        Location2AOption[] ScanLevel()
        {
            var seen = new Dictionary<string, Location2AOption>(StringComparer.Ordinal);
            if (LevelScroll() is { } initialScroll && initialScroll.Current.VerticallyScrollable) initialScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            for (var page = 0; page < 80; page++)
            {
                RefreshRoot();
                foreach (var option in Options()) seen.TryAdd(option.AutomationId, option);
                var scroll = LevelScroll();
                if (scroll is null || !scroll.Current.VerticallyScrollable) break;
                if (scroll.Current.VerticalScrollPercent >= 99.9)
                {
                    if (!Selector().Current.ItemStatus.Contains("More: True", StringComparison.Ordinal)) break;
                    Thread.Sleep(30);
                }
                else scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                Thread.Sleep(15);
            }
            return seen.Values.ToArray();
        }
        void InvokeId(string id)
        {
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1 && LevelScroll() is { } initialScroll && initialScroll.Current.VerticallyScrollable) initialScroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
                for (var page = 0; page < 80; page++)
                {
                    RefreshRoot();
                    var element = FindByAutomationIdPrefix(id).FirstOrDefault(value => value.Current.AutomationId == id && !value.Current.IsOffscreen);
                    if (element is not null) { ActivateElement(element, element.Current.Name); return; }
                    var scroll = LevelScroll();
                    if (scroll is null || !scroll.Current.VerticallyScrollable) break;
                    if (scroll.Current.VerticalScrollPercent >= 99.9 && !Selector().Current.ItemStatus.Contains("More: True", StringComparison.Ordinal)) break;
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    Thread.Sleep(15);
                }
            }
            throw new InvalidOperationException($"LOCATION_OPTION_UNAVAILABLE: {id}");
        }
        void OpenRoot()
        {
            RefreshRoot();
            if (FindVisibleByAutomationId("canonical-other:Location") is not null) return;
            ActivateElement(Selector(), "Select Location");
            Await(() => FindVisibleByAutomationId("canonical-other:Location") is not null,
                "LOCATION_ROOT_UNAVAILABLE: the Location flyout did not expose root-only Other.");
        }
        void AwaitSelectionFlyoutClosed()
        {
            var deadline = Stopwatch.StartNew();
            Stopwatch? stable = null;
            while (deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                RefreshRoot();
                if (FindVisibleByAutomationId("canonical-level:Location") is null)
                {
                    stable ??= Stopwatch.StartNew();
                    if (stable.ElapsedMilliseconds >= 250) return;
                }
                else stable = null;
                Thread.Sleep(10);
            }
            throw new InvalidOperationException("LOCATION_SELECTION_FLYOUT_DID_NOT_SETTLE_CLOSED");
        }
        void SelectOption(Location2AOption option, int expectedCount)
        {
            var label = CanonicalRecordAnchor(option.Name);
            ownedLabels.Add(label);
            OpenRoot();
            InvokeId(option.AutomationId);
            Await(() => RemoveButtons().Length == Math.Min(expectedCount, 3) &&
                        FindVisibleByAutomationId("canonical-other:Location") is null,
                $"LOCATION_MULTISELECT_MISMATCH: expected {expectedCount} preview entries after selecting '{label}'.");
            var coordinate = Selector().Current.HelpText;
            if (expectedCount > 0 && !coordinate.StartsWith("canonical-selection:Location:", StringComparison.Ordinal))
                throw new InvalidOperationException("LOCATION_SELECTION_COORDINATE_MISSING: committed draft has no exact record/path coordinate.");
            selectedCoordinates.Add(coordinate);
            AwaitSelectionFlyoutClosed();
        }
        void Remove(string label, int expectedCount)
        {
            RefreshRoot();
            var button = FindVisibleByName($"Remove location {label}") ??
                throw new InvalidOperationException($"LOCATION_REMOVE_UNAVAILABLE: '{label}' has no preview removal control.");
            ActivateElement(button, button.Current.Name);
            Await(() => RemoveButtons().Length == Math.Min(expectedCount, 3),
                $"LOCATION_REMOVE_MISMATCH: expected {expectedCount} preview entries after removing '{label}'.");
        }
        void AddOther(string value, int expectedCount)
        {
            OpenRoot();
            InvokeId("canonical-other:Location");
            Await(() => FindVisibleByName("Other location") is not null, "LOCATION_OTHER_EDITOR_UNAVAILABLE");
            var editor = FindVisibleByName("Other location")!;
            ownedLabels.Add(value);
            editor.SetFocus();
            if (!editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                throw new InvalidOperationException("LOCATION_OTHER_VALUE_PATTERN_UNAVAILABLE");
            ((ValuePattern)pattern).SetValue(value);
            Selector().SetFocus(); // Commit through the product's LostFocus handler, without global keyboard input.
            Await(() => RemoveButtons().Length == expectedCount &&
                        FindVisibleByName($"Remove location {value}") is not null,
                "LOCATION_OTHER_PREVIEW_MISMATCH");
        }

        try
        {
            Step("preflight-existing-empty-location-draft", () =>
            {
                RefreshRoot();
                if (FindVisibleByName("Grid data intake form") is null)
                {
                    EnsureAssistantOpen();
                    if (FindVisibleByName("Grid data intake form") is null)
                        Activate("Toggle data intake form");
                }
                if (FindVisibleByName("Grid data intake form") is null)
                    throw new InvalidOperationException("INTAKE_NOT_VISIBLE: open the existing intake before attach-only acceptance.");
                Await(() =>
                {
                    RefreshRoot();
                    var selector = Selector();
                    runtimeStatus = selector.Current.ItemStatus;
                    return selector.Current.IsEnabled &&
                           (runtimeStatus.StartsWith("Exact:", StringComparison.Ordinal) ||
                            runtimeStatus.StartsWith("Exact (", StringComparison.Ordinal));
                }, "LOCATION_RUNTIME_NOT_EXACT");
                runtimeStatus = Selector().Current.ItemStatus;
                if (RemoveButtons().Length > 0 || FindByAutomationIdPrefix("canonical-selection:Location:").Length > 0)
                    throw new InvalidOperationException("EXISTING_LOCATION_DRAFT_PRESERVED: the test requires an empty Location draft.");
                var editor = FindVisibleByName("Other location");
                if (editor is not null && editor.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) &&
                    !string.IsNullOrEmpty(((ValuePattern)valuePattern).Current.Value))
                    throw new InvalidOperationException("EXISTING_LOCATION_TEXT_PRESERVED: uncommitted Other text is present.");
                CloseCanonicalFlyoutIfOpen(TimeSpan.FromSeconds(1));
                baselineWasEmpty = true;
            });
            Step("location-root-open", () =>
            {
                OpenRoot();
                rootOptions = ScanLevel();
                if (rootOptions.Length == 0)
                    throw new InvalidOperationException("LOCATION_ROOT_HAS_NO_CANONICAL_OPTIONS");
                if (rootOptions.Any(value => value.Name.Contains("Source identifier", StringComparison.Ordinal)))
                    throw new InvalidOperationException("LOCATION_IDENTIFIER_ONLY_ENTRY_VISIBLE");
            });
            var parent = rootOptions.Where(value => value.AutomationId.StartsWith("canonical-descend:", StringComparison.Ordinal) && value.Name == "Open Vinewood")
                .Select(descend => (Descend: descend, Labels: rootOptions.Where(label =>
                    label.AutomationId.StartsWith("canonical-record:", StringComparison.Ordinal) &&
                    label.Name == "Select " + CanonicalBranchAnchor(descend.Name)).ToArray()))
                .FirstOrDefault(pair => pair.Labels.Length == 1);
            if (parent.Descend is null)
            {
                steps.Add(new("nested-label-versus-chevron", "FAIL", null,
                    "NO_SELECTABLE_LOCATION_PARENT: the actual root exposes no unambiguous selectable parent with a separate descent control. No hierarchy was invented."));
            }
            else
            {
                var childOptions = Array.Empty<Location2AOption>();
                Step("location-chevron-descent", () =>
                {
                    InvokeId(parent.Descend.AutomationId);
                    Await(() => FindVisibleByName("Back one Location level") is not null &&
                                FindVisibleByAutomationId("canonical-other:Location") is null, "LOCATION_DESCENT_DID_NOT_REPLACE_LEVEL");
                    if (RemoveButtons().Length != 0)
                        throw new InvalidOperationException("LOCATION_CHEVRON_SELECTED_A_RECORD");
                    childOptions = ScanLevel().Where(value => value.AutomationId.StartsWith("canonical-record:", StringComparison.Ordinal)).ToArray();
                    nestedOptions = childOptions;
                    if (childOptions.Length != 4 || childOptions.Any(value => value.Name.Contains("Source identifier", StringComparison.Ordinal)))
                        throw new InvalidOperationException("LOCATION_PARENT_HAS_NO_PROJECTED_CHILDREN");
                });
                Step("location-back", () =>
                {
                    ActivateElement(FindVisibleByName("Back one Location level")!, "Back one Location level");
                    Await(() => FindVisibleByAutomationId("canonical-other:Location") is not null, "LOCATION_BACK_DID_NOT_RETURN_ROOT");
                    if (!ScanLevel().Select(value => (value.AutomationId, value.Name)).OrderBy(value => value.AutomationId, StringComparer.Ordinal)
                        .SequenceEqual(rootOptions.Select(value => (value.AutomationId, value.Name)).OrderBy(value => value.AutomationId, StringComparer.Ordinal)))
                        throw new InvalidOperationException("LOCATION_ROOT_ORDER_CHANGED_AFTER_BACK");
                });
                Step("location-parent-label-selects", () => SelectOption(parent.Labels[0], 1));
                Step("location-parent-preview-remove", () => Remove(CanonicalRecordAnchor(parent.Labels[0].Name), 0));
                var orderedLabels = new[] { CanonicalRecordAnchor(parent.Labels[0].Name) }
                    .Concat(childOptions.Select(value => CanonicalRecordAnchor(value.Name))).ToArray();
                Step("location-five-ordered-selections-overflow", () =>
                {
                    SelectOption(parent.Labels[0], 1);
                    for (var index = 0; index < childOptions.Length; index++)
                    {
                        OpenRoot();
                        InvokeId(parent.Descend.AutomationId);
                        Await(() => FindVisibleByName("Back one Location level") is not null, "LOCATION_CHILD_LEVEL_UNAVAILABLE");
                        var child = childOptions[index];
                        ownedLabels.Add(CanonicalRecordAnchor(child.Name));
                        InvokeId(child.AutomationId);
                        var count = index + 2;
                        Await(() => RemoveButtons().Length == Math.Min(count, 3) &&
                            (count <= 3 || FindVisibleByName($"Location selection overflow ({count} total)") is not null), "LOCATION_OVERFLOW_COUNT_MISMATCH");
                        selectedCoordinates.Add(Selector().Current.HelpText);
                        AwaitSelectionFlyoutClosed();
                    }
                    if (!RemoveButtons().Select(value => value.Current.Name["Remove location ".Length..]).SequenceEqual(orderedLabels.Take(3)))
                        throw new InvalidOperationException("LOCATION_PREVIEW_ORDER_CHANGED");
                    var overflow = FindVisibleByName("Location selection overflow (5 total)")!;
                    if (overflow.Current.HelpText != string.Join('\n', orderedLabels))
                        throw new InvalidOperationException("LOCATION_OVERFLOW_ORDER_OR_LABELS_MISMATCH");
                });
                Step("location-five-preview-removals", () =>
                {
                    for (var index = 0; index < orderedLabels.Length; index++) Remove(orderedLabels[index], orderedLabels.Length - index - 1);
                });
            }

            var leaves = rootOptions.Where(value => value.AutomationId.StartsWith("canonical-record:", StringComparison.Ordinal) &&
                    value.Name.StartsWith("Select ", StringComparison.Ordinal) &&
                    !value.Name.Contains("Source identifier", StringComparison.Ordinal) &&
                    !rootOptions.Any(other => other.AutomationId.StartsWith("canonical-descend:", StringComparison.Ordinal) &&
                        other.Name == "Open " + CanonicalRecordAnchor(value.Name)))
                .GroupBy(value => value.Name, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .Select(group => group.Single()).Take(2).ToArray();
            if (leaves.Length != 2)
                steps.Add(new("two-location-leaf-selection", "FAIL", null,
                    "INSUFFICIENT_UNAMBIGUOUS_NAMED_ROOT_LEAVES: two actual named leaf options were not available for this bounded test."));
            else
            {
                Step("location-first-leaf-select", () => SelectOption(leaves[0], 1));
                Step("location-reopen-second-leaf-select", () =>
                {
                    SelectOption(leaves[1], 2);
                    if (leaves.Any(value => FindVisibleByName($"Remove location {CanonicalRecordAnchor(value.Name)}") is null))
                        throw new InvalidOperationException("LOCATION_MULTISELECT_PREVIEW_LOST_A_SELECTION");
                });
                Step("location-reopen-toggle-first-off", () =>
                {
                    SelectOption(leaves[0], 1);
                    if (FindVisibleByName($"Remove location {CanonicalRecordAnchor(leaves[0].Name)}") is not null ||
                        FindVisibleByName($"Remove location {CanonicalRecordAnchor(leaves[1].Name)}") is null)
                        throw new InvalidOperationException("LOCATION_TOGGLE_REMOVED_THE_WRONG_SELECTION");
                });
                Step("location-remaining-preview-remove", () => Remove(CanonicalRecordAnchor(leaves[1].Name), 0));
            }
            var firstOther = $"GRID 2A local acceptance {runId} A";
            var secondOther = $"GRID 2A local acceptance {runId} B";
            Step("location-other-first-lostfocus-commit", () => AddOther(firstOther, 1));
            Step("location-other-second-lostfocus-commit", () => AddOther(secondOther, 2));
            Step("location-other-first-remove", () => Remove(firstOther, 1));
            Step("location-other-second-remove", () => Remove(secondOther, 0));
        }
        catch (Exception exception)
        {
            if (steps.Count == 0 || steps[^1].Status != "FAIL")
                steps.Add(new("location-acceptance", "FAIL", null, exception.Message));
        }
        finally
        {
            if (baselineWasEmpty)
            {
                try
                {
                    Step("restore-empty-location-draft", () =>
                    {
                        RefreshRoot();
                        var editor = FindVisibleByName("Other location");
                        if (editor is not null && editor.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                            ownedLabels.Contains(((ValuePattern)pattern).Current.Value))
                        {
                            ((ValuePattern)pattern).SetValue(string.Empty);
                            Selector().SetFocus();
                        }
                        foreach (var label in ownedLabels)
                        {
                            RefreshRoot();
                            if (FindVisibleByName($"Remove location {label}") is { } button)
                            {
                                ActivateElement(button, button.Current.Name);
                                Await(() => FindVisibleByName($"Remove location {label}") is null,
                                    "LOCATION_TEST_ENTRY_CLEANUP_FAILED");
                            }
                        }
                        if (RemoveButtons().Length != 0 || FindByAutomationIdPrefix("canonical-selection:Location:").Length != 0)
                            throw new InvalidOperationException("LOCATION_BASELINE_NOT_RESTORED: inspect the remaining draft; no unrelated entries were removed.");
                    });
                }
                catch (Exception) { /* The failed cleanup step is retained in the report. */ }
            }
        }

        var passed = steps.Count > 0 && steps.All(value => value.Status == "PASS");
        var report = new
        {
            schemaVersion = 1, scenario = "grid.location-2a.attach-only", startedUtc = started,
            completedUtc = DateTimeOffset.UtcNow, processId = process.Id, status = passed ? "PASS" : "FAIL",
            runtimeStatus, baselineWasEmpty, rootOptions, nestedOptions, selectedCoordinates, steps, viewMetrics,
            timingDefinition = "Observed UI wall time including UIA invocation, rendering and bounded postcondition polling; not engine-only query latency.",
            investigationPrepared = false, authorizationInvoked = false, processStartedOrStopped = false,
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Location 2A attached acceptance {(passed ? "passed" : "failed")}: {reportPath}");
        return passed;
    }

    public bool ExerciseWorkstationRegistrationRefreshAttached(string reportPath)
    {
        if (ownsProcess)
            throw new InvalidOperationException("Workstation registration refresh requires an attached product.");

        var evidenceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Grid",
            "evidence",
            "registration-refresh");
        var receiptsBefore = Directory.Exists(evidenceRoot)
            ? Directory.GetFiles(evidenceRoot, "*.registration-refresh.v1.json").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

        NormalizeShell();
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var connectedGame = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
                !element.Current.IsOffscreen &&
                element.Current.Name.Contains("GTA V Enhanced", StringComparison.OrdinalIgnoreCase) &&
                element.Current.Name.EndsWith(", connected game", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(element => element.Current.BoundingRectangle.Width)
            .FirstOrDefault()
            ?? root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(element =>
                    !element.Current.IsOffscreen &&
                    element.Current.Name.EndsWith(", connected game", StringComparison.OrdinalIgnoreCase));
        if (connectedGame is null)
            throw new InvalidOperationException("No connected game route was available for workstation refresh.");

        ActivateElement(connectedGame, connectedGame.Current.Name);
        WaitForElementName("Refresh read-only environment index", TimeSpan.FromSeconds(30));
        var refresh = FindVisibleByName("Refresh read-only environment index")
            ?? throw new InvalidOperationException("Workstation refresh control was not visible.");
        ActivateElement(refresh, refresh.Current.Name);

        string? receiptPath = null;
        var deadline = DateTime.UtcNow.AddMinutes(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(evidenceRoot))
            {
                receiptPath = Directory.GetFiles(evidenceRoot, "*.registration-refresh.v1.json")
                    .FirstOrDefault(path => !receiptsBefore.Contains(path));
                if (receiptPath is not null) break;
            }
            Thread.Sleep(500);
        }

        if (receiptPath is null)
            throw new TimeoutException("Registration refresh did not write a receipt within 15 minutes.");

        var receiptJson = File.ReadAllText(receiptPath);
        var receipt = JsonSerializer.Deserialize<RegistrationRefreshReceipt>(receiptJson)
            ?? throw new InvalidDataException("Registration refresh receipt could not be deserialized.");
        var report = new
        {
            schemaVersion = 1,
            scenario = "grid.workstation-registration-refresh.attach-only",
            processId = process.Id,
            receiptPath,
            receipt,
            status = receipt.Status.ToString(),
            mode = receipt.Mode.ToString(),
            detail = receipt.Detail,
            completedUtc = DateTimeOffset.UtcNow,
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Workstation registration refresh exercised: {receiptPath}");
        return receipt.Status == RegistrationRefreshStatus.Completed;
    }

    /// <summary>Completes only multi-select/overflow/Other after separately recorded hierarchy acceptance.</summary>
    public bool CompleteLocation2ARemainingAttached(string reportPath)
    {
        if (ownsProcess) throw new InvalidOperationException("Attach-only acceptance required.");
        var steps = new List<Location2AStep>();
        var selected = new List<Location2AOption>();
        var coordinates = new List<string>();
        var owned = new List<string>();
        var viewMetrics = new List<string>();
        var invocationDiagnostics = new List<string>();
        var safeToClean = false;
        void InvokeObserved(AutomationElement element, string name)
        {
            try { ActivateElement(element, name); }
            catch (COMException error)
            {
                // A successful click can dispose its own WinUI peer before Invoke returns.
                // Never repeat the click; each caller must prove the product's resulting state.
                invocationDiagnostics.Add($"{name}: 0x{error.HResult:X8}; invocation was not retried; following exact postcondition decides outcome.");
            }
        }
        void Refresh() => root = AutomationElement.FromHandle(process.MainWindowHandle);
        AutomationElement Selector() => root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, "Select Location")).Cast<AutomationElement>()
            .First(value => !value.Current.IsOffscreen && !string.IsNullOrEmpty(value.Current.ItemStatus));
        AutomationElement[] Removers() => root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Where(value => !value.Current.IsOffscreen && value.Current.Name.StartsWith("Remove location ", StringComparison.Ordinal)).ToArray();
        void Await(Func<bool> condition, string failure)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5)) { Refresh(); if (condition()) return; Thread.Sleep(15); }
            throw new InvalidOperationException(failure);
        }
        void Closed()
        {
            Stopwatch? settled = null;
            Await(() =>
            {
                if (FindVisibleByAutomationId("canonical-level:Location") is not null) { settled = null; return false; }
                settled ??= Stopwatch.StartNew(); return settled.ElapsedMilliseconds >= 250;
            }, "Location close transition did not settle.");
        }
        void Open()
        {
            Closed();
            InvokeObserved(Selector(), "Select Location");
            Await(() => FindVisibleByAutomationId("canonical-other:Location") is not null &&
                FindVisibleByAutomationIdPrefix("canonical-record:").Length >= 5, "Location root did not populate.");
            viewMetrics.Add(Selector().Current.ItemStatus);
            if (selected.Count > 0)
            {
                var visibleNames = FindVisibleByAutomationIdPrefix("canonical-record:")
                    .Where(value => value.Current.Name.StartsWith("Select ", StringComparison.Ordinal))
                    .Select(value => value.Current.Name).Take(selected.Count).ToArray();
                if (!visibleNames.SequenceEqual(selected.Select(value => value.Name)))
                    throw new InvalidOperationException("ROOT_VIEWPORT_CHANGED_AFTER_REOPEN: " + string.Join(", ", visibleNames));
            }
        }
        void Step(string name, Action action)
        {
            var timer = Stopwatch.StartNew();
            try { action(); steps.Add(new(name, "PASS", timer.Elapsed.TotalMilliseconds, null)); }
            catch (Exception error) { steps.Add(new(name, "FAIL", timer.Elapsed.TotalMilliseconds, error.ToString())); throw; }
        }
        void ExpectCount(int count)
        {
            Await(() => Removers().Length == Math.Min(count, 3) &&
                (count <= 3 || FindVisibleByName($"Location selection overflow ({count} total)") is not null), "Location preview count mismatch.");
        }
        void Select(Location2AOption option, int count)
        {
            Open();
            var target = FindVisibleByAutomationId(option.AutomationId) ?? throw new InvalidOperationException("Observed first-page option no longer visible.");
            InvokeObserved(target, option.Name);
            ExpectCount(count);
            var coordinate = Selector().Current.HelpText;
            if (count > coordinates.Count && !coordinate.Contains(option.AutomationId["canonical-record:".Length..], StringComparison.Ordinal))
                throw new InvalidOperationException("Selected canonical coordinate does not match the invoked record.");
            coordinates.Add(coordinate);
            Closed();
        }
        void Remove(string label, int count)
        {
            var button = FindVisibleByName("Remove location " + label) ?? throw new InvalidOperationException("Expected owned preview row absent.");
            InvokeObserved(button, button.Current.Name); ExpectCount(count);
        }
        void Other(string value, int count)
        {
            Open();
            InvokeObserved(FindVisibleByAutomationId("canonical-other:Location")!, "Other Location");
            Await(() => FindVisibleByName("Other location") is not null, "Other editor unavailable.");
            var editor = FindVisibleByName("Other location")!;
            ((ValuePattern)editor.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
            Selector().SetFocus();
            ExpectCount(count); Closed();
        }
        try
        {
            Step("remaining-preflight-empty-exact-location", () =>
            {
                Refresh();
                if (GetForegroundWindow() != process.MainWindowHandle) throw new InvalidOperationException("GRID must remain foreground.");
                if (!Selector().Current.ItemStatus.StartsWith("Exact", StringComparison.Ordinal) || Removers().Length != 0 ||
                    FindByAutomationIdPrefix("canonical-selection:Location:").Length != 0)
                    throw new InvalidOperationException("The exact empty Location draft is required; existing work remains untouched.");
                safeToClean = true;
            });
            Step("five-observed-named-root-selections", () =>
            {
                Open();
                selected.AddRange(FindVisibleByAutomationIdPrefix("canonical-record:")
                    .Where(value => value.Current.Name.StartsWith("Select ", StringComparison.Ordinal) &&
                        !value.Current.Name.Contains("Source identifier", StringComparison.Ordinal))
                    .Select(value => new Location2AOption(value.Current.AutomationId, value.Current.Name, false))
                    .GroupBy(value => value.AutomationId, StringComparer.Ordinal).Select(group => group.First()).Take(5));
                if (selected.Count != 5) throw new InvalidOperationException("Five observed first-page records required.");
                owned.AddRange(selected.Select(value => CanonicalRecordAnchor(value.Name)));
                InvokeObserved(FindVisibleByAutomationId(selected[0].AutomationId)!, selected[0].Name);
                ExpectCount(1); coordinates.Add(Selector().Current.HelpText); Closed();
                for (var index = 1; index < selected.Count; index++) Select(selected[index], index + 1);
                if (!Removers().Select(value => value.Current.Name["Remove location ".Length..]).SequenceEqual(owned.Take(3)))
                    throw new InvalidOperationException("Inline selection order changed.");
                var overflow = FindVisibleByName("Location selection overflow (5 total)")!;
                if (overflow.Current.HelpText != string.Join('\n', owned)) throw new InvalidOperationException("Overflow order or labels changed.");
            });
            Step("toggle-first-selection-preserves-order", () =>
            {
                Select(selected[0], 4);
                if (!Removers().Select(value => value.Current.Name["Remove location ".Length..]).SequenceEqual(owned.Skip(1).Take(3)))
                    throw new InvalidOperationException("Toggling first selection altered remaining order.");
            });
            Step("preview-removal-including-overflowed-records", () =>
            {
                for (var index = 1; index < owned.Count; index++) Remove(owned[index], owned.Count - index - 1);
            });
            var first = "GRID 2A local unresolved context " + Guid.NewGuid().ToString("N") + " A";
            var second = first[..^1] + "B";
            owned.Add(first); owned.Add(second);
            Step("other-unresolved-context-two-values", () => { Other(first, 1); Other(second, 2); });
            Step("other-preview-removal", () => { Remove(first, 1); Remove(second, 0); });
        }
        catch (Exception error)
        {
            if (steps.Count == 0 || steps[^1].Status != "FAIL") steps.Add(new("remaining-acceptance", "FAIL", null, error.ToString()));
        }
        finally
        {
            if (safeToClean)
            {
                try
                {
                    Step("restore-empty-location-draft", () =>
                    {
                        Refresh();
                        var editor = FindVisibleByName("Other location");
                        if (editor is not null && editor.TryGetCurrentPattern(ValuePattern.Pattern, out var value) &&
                            owned.Contains(((ValuePattern)value).Current.Value))
                        { ((ValuePattern)value).SetValue(string.Empty); Selector().SetFocus(); }
                        for (var pass = 0; pass < owned.Count + 1; pass++)
                        {
                            Refresh();
                            var button = Removers().FirstOrDefault(value => owned.Contains(value.Current.Name["Remove location ".Length..]));
                            if (button is null) break;
                            var name = button.Current.Name;
                            InvokeObserved(button, name); Await(() => FindVisibleByName(name) is null, "Owned test entry cleanup failed.");
                        }
                        if (Removers().Length != 0 || FindByAutomationIdPrefix("canonical-selection:Location:").Length != 0)
                            throw new InvalidOperationException("Original empty draft was not restored.");
                    });
                }
                catch (Exception) { }
            }
        }
        var passed = steps.Count > 0 && steps.All(value => value.Status == "PASS");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            scenario = "grid.location-2a.remaining-attached", processId = process.Id, status = passed ? "PASS" : "FAIL",
            selected, coordinates, viewMetrics, steps, invocationDiagnostics, foregroundRequired = true,
            priorHierarchyEvidence = "location-live-acceptance-transient-uia.json",
            timingDefinition = "Functional UIA wall time; includes enumeration and close settlement. View metrics record product rendering independently.",
            investigationPrepared = false, authorizationInvoked = false, processStartedOrStopped = false,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Location 2A remaining acceptance {(passed ? "passed" : "failed")}: {reportPath}");
        return passed;
    }

    private sealed record Location2AStep(string Name, string Status, double? ElapsedMilliseconds, string? Detail);
    private sealed record Location2AOption(string AutomationId, string Name, bool WasOffscreen);

    public void VerifyCanonicalGtaLiveDif(bool completeLifecycle = false, bool selectProfile = true)
    {
        WaitForElementName("GTA V Enhanced", TimeSpan.FromSeconds(20));
        EnsureAssistantOpen();
        WaitForAssistantIntake();
        if (FindVisibleByName("Grid data intake form") is null)
            Activate("Toggle data intake form");

        if (FindVisibleByName("Game · GTA V Enhanced") is null)
        {
            Activate("Select game");
            ActivateAllowingAutomationTimeout("GTA V Enhanced");
        }
        if (selectProfile)
        {
            if (FindVisibleByName("Profile · GTA V Enhanced") is null)
            {
                Activate("Select profile");
                Activate("GTA V Enhanced");
            }
        }
        else
        {
            WaitForElementName("Profile · GTA V Enhanced", TimeSpan.FromMinutes(4));
        }

        if (FindVisibleByName("Class · Crash & Freeze") is null)
        {
            Activate("Select Class");
            Activate("Crash & Freeze");
        }

        var location = SelectFirstCanonicalRecord(
            KnowledgeKind.Location, requireTerminology: true, requireIdentifierOnly: false);
        var mission = SelectFirstCanonicalRecord(
            KnowledgeKind.MissionQuest, requireTerminology: false, requireIdentifierOnly: true);
        var item = SelectFirstCanonicalRecord(
            KnowledgeKind.Item, requireTerminology: true, requireIdentifierOnly: false, "Weapons");
        var actor = SelectFirstCanonicalRecord(
            KnowledgeKind.Actor, requireTerminology: false, requireIdentifierOnly: true, "NPC", "DLC");

        var selections = new[] { location, mission, item, actor };
        Assert(!string.IsNullOrWhiteSpace(location.DisplayAnchor) &&
               !location.DisplayAnchor.Contains("Source identifier", StringComparison.Ordinal),
            "Location did not present its exact registered player-facing name.");
        Assert(selections.Select(value => value.RecordId).Distinct(StringComparer.Ordinal).Count() == 4,
            "The four live canonical selectors did not commit four distinct canonical KnowledgeRecordIds.");
        Assert(selections.All(value => value.CatalogRevisionId.Equals(ExpectedGtaCatalogRevision, StringComparison.Ordinal)),
            "A committed selector did not expose the pinned GTA V Enhanced catalog revision.");
        Assert(selections.Select(value => value.CompositionId).Distinct(StringComparer.Ordinal).Count() == 1,
            "The four committed selectors did not expose one exact runtime catalog composition.");
        Capture("canonical-gta-committed-selections");
        if (!completeLifecycle) return;

        var existingIntakes = EnumerateAccountScopedInvestigationIntakes()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetFocusedValue("Message Grid", CanonicalLiveClaim);
        WaitForVisibleEnabledElement("Start Investigation", TimeSpan.FromSeconds(10));
        Activate("Start Investigation");
        WaitForVisibleEnabledElement("Authorize exact scope", TimeSpan.FromMinutes(2));
        Capture("canonical-gta-authorization");
        Activate("Authorize exact scope");

        var intakePath = WaitForSingleNewInvestigationIntake(existingIntakes, TimeSpan.FromMinutes(3));
        VerifyPersistedCanonicalIntake(intakePath, selections);
        Capture("canonical-gta-persisted-ticket");
    }

    public void VerifyCanonicalGtaCasinoCanary()
    {
        // First prove that all four selectors are backed by the pinned canonical
        // catalog. These selections are intentionally discarded before the real
        // Casino Heist intake so unrelated records cannot contaminate the ticket.
        VerifyCanonicalGtaLiveDif();
        Activate("New Investigation");
        WaitForAssistantIntake();
        if (FindVisibleByName("Grid data intake form") is null)
            Activate("Toggle data intake form");

        Activate("Select game");
        Activate("GTA V Enhanced");
        Activate("Select profile");
        Activate("GTA V Enhanced");
        Activate("Select Class");
        Activate("Crash & Freeze");
        Activate("Select Problem");
        Activate("Crash");
        Activate("Select Timing");
        Activate("After leaving an activity");
        Activate("Select Goal");
        Activate("Identify evidence-backed cause");

        var missionSelector = FindVisibleByAutomationId("canonical-selector:MissionQuest") ??
                              FindVisibleByAutomationId("MissionSelector") ??
                              throw new InvalidOperationException("The Mission/Quest selector was unavailable for Other context.");
        ActivateElement(missionSelector, "Select Mission/Quest");
        WaitForCanonicalSelectorLevel(KnowledgeKind.MissionQuest, TimeSpan.FromMinutes(4));
        WaitForCanonicalRootOther(KnowledgeKind.MissionQuest, TimeSpan.FromSeconds(30));
        Activate("Other Mission/Quest");
        SetAndCommitValue("Other mission or quest", CasinoHeistOtherContext);
        SetFocusedValue("Message Grid", CasinoHeistClaim);
        Capture("gta-casino-heist-unresolved-intake");

        var existingIntakes = EnumerateAccountScopedInvestigationIntakes()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        WaitForVisibleEnabledElement("Start Investigation", TimeSpan.FromSeconds(10));
        Activate("Start Investigation");
        WaitForVisibleEnabledElement("Authorize exact scope", TimeSpan.FromMinutes(2));
        Capture("gta-casino-heist-authorization");
        Activate("Authorize exact scope");

        var intakePath = WaitForSingleNewInvestigationIntake(existingIntakes, TimeSpan.FromMinutes(3));
        VerifyPersistedCasinoHeistIntake(intakePath);
        VerifyPersistedEvidenceBoundResult(intakePath);
        WaitForVisibleTextContaining("UNRESOLVED", TimeSpan.FromMinutes(2));
        Capture("gta-casino-heist-result");
    }

    private void WaitForVisibleTextContaining(string expectedText, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            if (EnumerateProcessElements().Any(element =>
                {
                    try
                    {
                        return !element.Current.IsOffscreen &&
                               element.Current.Name.Contains(expectedText, StringComparison.Ordinal);
                    }
                    catch (ElementNotAvailableException)
                    {
                        return false;
                    }
                }))
                return;
            Thread.Sleep(200);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"GRID persisted the evidence result but did not visibly render '{expectedText}' within {timeout}.");
    }

    private CanonicalSelectionCoordinates SelectFirstCanonicalRecord(
        KnowledgeKind kind,
        bool requireTerminology,
        bool requireIdentifierOnly,
        params string[] requiredLeadingBranches)
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var selectorAutomationId = kind switch
        {
            KnowledgeKind.Location => "LocationSelector",
            KnowledgeKind.MissionQuest => "MissionSelector",
            KnowledgeKind.Item => "ItemSelector",
            KnowledgeKind.Actor => "EntitySelector",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var selector = FindVisibleByAutomationId($"canonical-selector:{kind}") ??
                       FindVisibleByAutomationIdPrefix($"canonical-selection:{kind}:").FirstOrDefault() ??
                       FindVisibleByAutomationId(selectorAutomationId)
            ?? throw new InvalidOperationException(
                $"The live {kind} canonical selector was not available. Visible controls: " +
                string.Join(" | ", root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>()
                    .Where(element => !element.Current.IsOffscreen &&
                                      (element.Current.Name.Contains(kind.ToString(), StringComparison.OrdinalIgnoreCase) ||
                                       element.Current.Name.Contains("Location", StringComparison.OrdinalIgnoreCase) ||
                                       element.Current.AutomationId.Contains("canonical", StringComparison.OrdinalIgnoreCase)))
                    .Select(element => $"{element.Current.ControlType.ProgrammaticName}:{element.Current.Name} [{element.Current.AutomationId}]")));
        AssertCanonicalSelectorClosedHeight(selector, kind);
        CloseCanonicalFlyoutIfOpen(TimeSpan.FromSeconds(10));
        ActivateElement(selector, $"Select {kind}");
        WaitForCanonicalSelectorLevel(kind, TimeSpan.FromMinutes(4));
        WaitForCanonicalRootOther(kind, TimeSpan.FromSeconds(30));
        AssertCanonicalFlyoutAttachedBelow(selector, kind);
        var rootOther = FindVisibleByAutomationId($"canonical-other:{kind}");
        Assert(rootOther is not null, $"The {kind} selector did not expose Other at its root.");
        var rootOrder = VisibleCanonicalOptionIds();
        Assert(rootOrder.Length > 0, $"The {kind} selector root exposed no immediate canonical children.");
        Capture($"canonical-{kind.ToString().ToLowerInvariant()}-other");
        if (kind == KnowledgeKind.MissionQuest)
        {
            AssertCanonicalVerticalOverflow(kind);
            AssertVisibleCanonicalRecordsIdentifierOnly(kind);
        }

        if (requiredLeadingBranches.Length > 0)
        {
            var branch = FindVisibleByName($"Open {requiredLeadingBranches[0]}")
                ?? throw new InvalidOperationException(
                    $"The {kind} selector did not expose required immediate child '{requiredLeadingBranches[0]}'.");
            var branchId = branch.Current.AutomationId;
            ActivateElement(branch, branch.Current.Name);
            WaitForCanonicalSelectorLevel(kind, TimeSpan.FromMinutes(4));
            Assert(FindVisibleByName($"Back one {CanonicalSelectorTitle(kind)} level") is not null,
                $"The nested {kind} selector did not expose one-level Back navigation.");
            Assert(FindVisibleByAutomationId($"canonical-other:{kind}") is null,
                $"The nested {kind} selector exposed root-only Other.");
            Assert(FindVisibleByAutomationId(branchId) is null,
                $"The {kind} selector appended nested children instead of replacing its immediate-child level.");
            Capture($"canonical-{kind.ToString().ToLowerInvariant()}-nested");
            Capture($"canonical-{kind.ToString().ToLowerInvariant()}-back");
            Activate($"Back one {CanonicalSelectorTitle(kind)} level");
            WaitForCanonicalSelectorLevel(kind, TimeSpan.FromMinutes(4));
            Assert(FindVisibleByAutomationId($"canonical-other:{kind}") is not null,
                $"Returning to the {kind} root did not restore root-only Other.");
            Assert(VisibleCanonicalOptionIds().SequenceEqual(rootOrder, StringComparer.Ordinal),
                $"The {kind} immediate-child ordering changed after one-level Back navigation.");
        }
        Capture($"canonical-{kind.ToString().ToLowerInvariant()}-root");
        var traversedBranches = 0;
        for (var depth = 0; depth < 12; depth++)
        {
            var focused = AutomationElement.FocusedElement;
            var focusedId = focused?.Current.AutomationId ?? string.Empty;
            if (focusedId.StartsWith("canonical-record:", StringComparison.Ordinal))
            {
                var identifierOnly = IsIdentifierOnlyCanonicalRow(focused!);
                if (requireTerminology && identifierOnly)
                    throw new InvalidOperationException($"The {kind} canonical path did not expose player-facing terminology.");
                if (requireIdentifierOnly && !identifierOnly)
                    throw new InvalidOperationException($"The {kind} canonical path unexpectedly exposed unproven terminology.");
                if (requireIdentifierOnly) AssertVisibleCanonicalRecordsIdentifierOnly(kind);
                Assert(traversedBranches >= requiredLeadingBranches.Length,
                    $"The {kind} path did not traverse its required evidence-backed organizational branches.");
                var recordId = focusedId["canonical-record:".Length..];
                var displayAnchor = CanonicalRecordAnchor(focused!.Current.Name);
                ActivateElement(focused!, focused.Current.Name);
                Thread.Sleep(320);
                root = AutomationElement.FromHandle(process.MainWindowHandle);
                var committedSelector = FindVisibleByAutomationIdPrefix(
                        $"canonical-selection:{kind}:{recordId}:").FirstOrDefault()
                    ?? throw new InvalidOperationException($"The {kind} selector disappeared after selection.");
                Assert(committedSelector.Current.HelpText.StartsWith(
                           $"canonical-selection:{kind}:{recordId}:", StringComparison.Ordinal),
                    $"The {kind} selector did not expose its TicketDraft record/path/composition coordinates.");
                return ParseCanonicalSelectionCoordinate(kind, committedSelector.Current.HelpText, displayAnchor);
            }
            if (focusedId.StartsWith("canonical-path:", StringComparison.Ordinal))
            {
                var branchName = CanonicalBranchAnchor(focused!.Current.Name);
                if (traversedBranches < requiredLeadingBranches.Length)
                    Assert(branchName.Equals(requiredLeadingBranches[traversedBranches], StringComparison.Ordinal),
                        $"The {kind} path expected '{requiredLeadingBranches[traversedBranches]}' but found '{branchName}'.");
                traversedBranches++;
                ActivateElement(focused!, focused.Current.Name);
                WaitForCanonicalSelectorLevel(kind, TimeSpan.FromMinutes(4));
                Capture($"canonical-{kind.ToString().ToLowerInvariant()}-level-{traversedBranches}");
                continue;
            }

            root = AutomationElement.FromHandle(process.MainWindowHandle);
            var records = FindVisibleByAutomationIdPrefix("canonical-record:")
                .Where(element => !requireTerminology ||
                    !element.Current.Name.Contains("Source identifier", StringComparison.Ordinal))
                .OrderBy(element => element.Current.Name, StringComparer.Ordinal)
                .ThenBy(element => element.Current.AutomationId, StringComparer.Ordinal)
                .ToArray();
            if (records.Length > 0)
            {
                var selected = records[0];
                var identifierOnly = IsIdentifierOnlyCanonicalRow(selected);
                Assert(!requireTerminology || !identifierOnly,
                    $"The {kind} canonical path did not expose player-facing terminology.");
                Assert(!requireIdentifierOnly || identifierOnly,
                    $"The {kind} canonical path unexpectedly exposed unproven terminology.");
                if (requireIdentifierOnly) AssertVisibleCanonicalRecordsIdentifierOnly(kind);
                Assert(traversedBranches >= requiredLeadingBranches.Length,
                    $"The {kind} path did not traverse its required evidence-backed organizational branches.");
                var recordId = selected.Current.AutomationId["canonical-record:".Length..];
                var displayAnchor = CanonicalRecordAnchor(selected.Current.Name);
                if (selected.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var scrollItem))
                    ((ScrollItemPattern)scrollItem).ScrollIntoView();
                ActivateElement(selected, selected.Current.Name);
                Thread.Sleep(300);
                root = AutomationElement.FromHandle(process.MainWindowHandle);
                var committed = FindVisibleByAutomationIdPrefix(
                    $"canonical-selection:{kind}:{recordId}:").FirstOrDefault();
                Assert(committed is not null &&
                       committed.Current.HelpText.StartsWith(
                           $"canonical-selection:{kind}:{recordId}:", StringComparison.Ordinal),
                    $"The {kind} selector did not re-render from TicketDraft with its selected canonical record/path/composition coordinates.");
                return ParseCanonicalSelectionCoordinate(kind, committed!.Current.HelpText, displayAnchor);
            }

            var branches = FindVisibleByAutomationIdPrefix("canonical-path:")
                .OrderBy(element => element.Current.Name, StringComparer.Ordinal)
                .ThenBy(element => element.Current.AutomationId, StringComparer.Ordinal)
                .ToArray();
            Assert(branches.Length > 0,
                $"The {kind} selector exposed neither a selectable canonical record nor another immediate-child level. " +
                $"Runtime status: {selector.Current.ItemStatus}. " +
                $"Selector diagnostics: {string.Join(" | ", DescendantNames().Where(name => name.Contains("options", StringComparison.OrdinalIgnoreCase) || name.Contains("Unavailable", StringComparison.OrdinalIgnoreCase)))}. " +
                $"Visible process buttons: {DescribeVisibleProcessButtons()}. " +
                $"Top-level windows: {DescribeTopLevelWindows()}");
            var branch = branches[0];
            var fallbackBranchName = CanonicalBranchAnchor(branch.Current.Name);
            if (traversedBranches < requiredLeadingBranches.Length)
                Assert(fallbackBranchName.Equals(requiredLeadingBranches[traversedBranches], StringComparison.Ordinal),
                    $"The {kind} path expected '{requiredLeadingBranches[traversedBranches]}' but found '{branch.Current.Name}'.");
            traversedBranches++;
            ActivateElement(branch, branch.Current.Name);
            WaitForCanonicalSelectorLevel(kind, TimeSpan.FromMinutes(4));
            Capture($"canonical-{kind.ToString().ToLowerInvariant()}-level-{traversedBranches}");
        }
        throw new InvalidOperationException($"The {kind} selector exceeded the bounded immediate-child navigation depth.");
    }

    private static bool IsIdentifierOnlyCanonicalRow(AutomationElement row)
    {
        if (row.Current.Name.Contains("Source identifier", StringComparison.Ordinal)) return true;
        return row.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Any(element => element.Current.Name.Contains("Source identifier", StringComparison.Ordinal));
    }

    private static string CanonicalBranchAnchor(string automationName) =>
        automationName.StartsWith("Open ", StringComparison.Ordinal)
            ? automationName["Open ".Length..]
            : automationName;

    private static string CanonicalRecordAnchor(string automationName) =>
        automationName.StartsWith("Select ", StringComparison.Ordinal)
            ? automationName["Select ".Length..]
            : automationName;

    private static string CanonicalSelectorTitle(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Location => "Location",
        KnowledgeKind.MissionQuest => "Mission/Quest",
        KnowledgeKind.Item => "Item",
        KnowledgeKind.Actor => "Actor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void AssertCanonicalSelectorClosedHeight(AutomationElement selector, KnowledgeKind kind)
    {
        var scale = Math.Max(96u, GetDpiForWindow(process.MainWindowHandle)) / 96d;
        var expectedHeight = 28d * scale;
        var observedHeight = selector.Current.BoundingRectangle.Height;
        Assert(Math.Abs(observedHeight - expectedHeight) <= Math.Max(1.5d, scale * 1.5d),
            $"The closed {kind} selector measured {observedHeight:0.##} px; expected 28 DIP " +
            $"({expectedHeight:0.##} px at {scale:0.###} scale).");
    }

    private void AssertCanonicalFlyoutAttachedBelow(AutomationElement selector, KnowledgeKind kind)
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var liveSelector = FindVisibleByAutomationId($"canonical-selector:{kind}") ??
                           FindVisibleByAutomationIdPrefix($"canonical-selection:{kind}:").FirstOrDefault() ??
                           selector;
        var option = FindVisibleByAutomationIdPrefix("canonical-record:")
            .Concat(FindVisibleByAutomationIdPrefix("canonical-path:"))
            .Append(FindVisibleByAutomationId($"canonical-other:{kind}"))
            .Where(candidate => candidate is not null)
            .OrderBy(candidate => candidate!.Current.BoundingRectangle.Top)
            .ThenBy(candidate => candidate!.Current.BoundingRectangle.Left)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"The {kind} flyout exposed no attached option geometry.");
        var selectorBounds = liveSelector.Current.BoundingRectangle;
        var optionBounds = option.Current.BoundingRectangle;
        var scale = Math.Max(96u, GetDpiForWindow(process.MainWindowHandle)) / 96d;
        Assert(optionBounds.Top >= selectorBounds.Bottom - (2d * scale) &&
               optionBounds.Top - selectorBounds.Bottom <= 48d * scale,
            $"The {kind} flyout was not bottom-attached to its closed selector. " +
            $"Selector={selectorBounds}; first option={optionBounds}.");
        Assert(optionBounds.Right >= selectorBounds.Left && optionBounds.Left <= selectorBounds.Right,
            $"The {kind} flyout lost its horizontal attachment. Selector={selectorBounds}; first option={optionBounds}.");
    }

    private string[] VisibleCanonicalOptionIds()
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
            {
                try
                {
                    var id = element.Current.AutomationId;
                    return (id.StartsWith("canonical-record:", StringComparison.Ordinal) ||
                            id.StartsWith("canonical-path:", StringComparison.Ordinal)) &&
                           !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .OrderBy(element => element.Current.BoundingRectangle.Top)
            .ThenBy(element => element.Current.BoundingRectangle.Left)
            .Select(element => element.Current.AutomationId)
            .ToArray();
    }

    private void AssertCanonicalVerticalOverflow(KnowledgeKind kind)
    {
        var overflow = EnumerateProcessElements().Any(element =>
        {
            try
            {
                if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern)) return false;
                var bounds = element.Current.BoundingRectangle;
                return !element.Current.IsOffscreen && bounds.Width is >= 280 and <= 360 &&
                       ((ScrollPattern)pattern).Current.VerticallyScrollable;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
        });
        Assert(overflow, $"The {kind} selector did not expose vertical overflow for its bounded attached flyout.");
    }

    private void AssertVisibleCanonicalRecordsIdentifierOnly(KnowledgeKind kind)
    {
        var records = FindVisibleByAutomationIdPrefix("canonical-record:");
        Assert(records.Length > 0 && records.All(IsIdentifierOnlyCanonicalRow),
            $"The visible {kind} record level exposed invented player-facing terminology instead of source identifiers. " +
            $"Rows={string.Join(" | ", records.Select(record => $"{record.Current.AutomationId}={record.Current.Name}"))}");
    }

    private CanonicalSelectionCoordinates ParseCanonicalSelectionCoordinate(
        KnowledgeKind expectedKind,
        string coordinate,
        string displayAnchor)
    {
        var parts = coordinate.Split(':', StringSplitOptions.None);
        Assert(parts.Length == 6 && parts[0].Equals("canonical-selection", StringComparison.Ordinal) &&
               parts[1].Equals(expectedKind.ToString(), StringComparison.Ordinal),
            $"The committed {expectedKind} selector exposed malformed coordinates '{coordinate}'.");
        Assert(parts[2].StartsWith("grid.knowledge-record.v1.sha256.", StringComparison.Ordinal) &&
               parts[3].StartsWith("grid.canonical-navigation-path.v1.sha256.", StringComparison.Ordinal) &&
               parts[4].StartsWith("grid.catalog-revision.v", StringComparison.Ordinal) &&
               parts[5].StartsWith("grid.runtime-catalog-composition.v1.sha256.", StringComparison.Ordinal),
            $"The committed {expectedKind} selector did not expose exact record/path/revision/composition identities.");
        return new(expectedKind, parts[2], parts[3], parts[4], parts[5], displayAnchor);
    }

    private void WaitForVisibleEnabledElement(string automationName, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            var element = FindVisibleByName(automationName);
            if (element is not null && element.Current.IsEnabled) return;
            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"GRID did not expose enabled '{automationName}' within {timeout}. " +
            $"Visible process buttons: {DescribeVisibleProcessButtons()}.");
    }

    private string[] EnumerateAccountScopedInvestigationIntakes()
    {
        var baseDataRoot = string.IsNullOrWhiteSpace(dataRoot)
            ? Environment.GetEnvironmentVariable("GRID_DATA_ROOT")
            : dataRoot;
        if (string.IsNullOrWhiteSpace(baseDataRoot))
            baseDataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grid");
        var accountsRoot = Path.Combine(Path.GetFullPath(baseDataRoot), "accounts", "v1");
        return Directory.Exists(accountsRoot)
            ? Directory.EnumerateFiles(accountsRoot, "investigation-intake.v1.json", SearchOption.AllDirectories)
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
    }

    private string WaitForSingleNewInvestigationIntake(
        HashSet<string> existingIntakes,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string[] added = [];
        do
        {
            added = EnumerateAccountScopedInvestigationIntakes()
                .Where(path => !existingIntakes.Contains(path))
                .ToArray();
            if (added.Length > 1)
                throw new InvalidOperationException(
                    $"Authorization sealed {added.Length} new investigation intakes; exactly one was expected: " +
                    string.Join(" | ", added));
            if (added.Length == 1)
            {
                try
                {
                    using var ignored = JsonDocument.Parse(File.ReadAllText(added[0]));
                    return added[0];
                }
                catch (IOException)
                {
                }
                catch (JsonException)
                {
                }
            }
            Thread.Sleep(200);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            "Authorization did not persist exactly one new account-scoped request/investigation-intake.v1.json. " +
            $"Observed new paths: {string.Join(" | ", added)}. Visible process buttons: {DescribeVisibleProcessButtons()}.");
    }

    private void VerifyPersistedCanonicalIntake(
        string intakePath,
        IReadOnlyCollection<CanonicalSelectionCoordinates> expectedSelections)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(intakePath));
        var intake = document.RootElement;
        Assert(intake.GetProperty("problem").GetString()?.Equals(CanonicalLiveClaim, StringComparison.Ordinal) == true,
            "The sealed intake did not preserve the visible composer's exact claim.");
        Assert(intake.GetProperty("unresolvedUserContext").GetArrayLength() == 0,
            "The all-canonical live journey unexpectedly persisted unresolved Other context.");
        var persisted = intake.GetProperty("canonicalSelections").EnumerateArray().ToArray();
        Assert(persisted.Length == 4,
            $"The sealed intake persisted {persisted.Length} canonical selections instead of four.");

        foreach (var expected in expectedSelections)
        {
            var actual = persisted.SingleOrDefault(value =>
                value.GetProperty("knowledgeKind").GetString()?.Equals(expected.Kind.ToString(), StringComparison.Ordinal) == true);
            Assert(actual.ValueKind == JsonValueKind.Object,
                $"The sealed intake dropped the {expected.Kind} canonical selection.");
            Assert(actual.GetProperty("selectionKind").GetString()?.Equals("CanonicalRecord", StringComparison.Ordinal) == true &&
                   actual.GetProperty("projectionPolicyId").GetString()?.Equals("grid.canonical-selector-projection", StringComparison.Ordinal) == true &&
                   actual.GetProperty("projectionPolicyVersion").GetString()?.Equals("1", StringComparison.Ordinal) == true &&
                   actual.GetProperty("knowledgeRecordId").GetString()?.Equals(expected.RecordId, StringComparison.Ordinal) == true &&
                   actual.GetProperty("selectedPathId").GetString()?.Equals(expected.PathId, StringComparison.Ordinal) == true &&
                   actual.GetProperty("catalogRevisionId").GetString()?.Equals(expected.CatalogRevisionId, StringComparison.Ordinal) == true &&
                   actual.GetProperty("catalogCompositionId").GetString()?.Equals(expected.CompositionId, StringComparison.Ordinal) == true,
                $"The sealed intake changed the {expected.Kind} selection kind, policy, record, path, catalog revision, or composition coordinate.");
        }
    }

    private void VerifyPersistedCasinoHeistIntake(string intakePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(intakePath));
        var intake = document.RootElement;
        Assert(intake.GetProperty("schemaVersion").GetInt32() == 3,
            "The Casino Heist request did not persist the structured intake-v3 contract.");
        Assert(intake.GetProperty("problem").GetString()?.Equals(CasinoHeistClaim, StringComparison.Ordinal) == true,
            "The Casino Heist intake changed the visible composer's exact claim.");
        Assert(intake.GetProperty("canonicalSelections").GetArrayLength() == 0,
            "The Casino Heist intake guessed a canonical record instead of retaining unresolved Other context.");
        var unresolved = intake.GetProperty("unresolvedUserContext").EnumerateArray().ToArray();
        Assert(unresolved.Length == 1 &&
               unresolved[0].GetProperty("kind").GetString()?.Equals("MissionOrQuest", StringComparison.Ordinal) == true &&
               unresolved[0].GetProperty("value").GetString()?.Equals(CasinoHeistOtherContext, StringComparison.Ordinal) == true &&
               unresolved[0].GetProperty("resolution").GetString()?.Equals("Unresolved", StringComparison.Ordinal) == true &&
               unresolved[0].GetProperty("matchedReferenceId").ValueKind == JsonValueKind.Null,
            "The Casino Heist wording was not preserved as exact unresolved Mission/Quest ticket context.");

        AssertTaxonomySelection(intake, "problemSelection", "grid.problem.crash", "Crash", "grid.class.crash-freeze");
        AssertTaxonomySelection(intake, "timingSelection", "grid.timing.after-leaving-activity",
            "After leaving an activity", "grid.class.crash-freeze");
        AssertTaxonomySelection(intake, "goalSelection", "grid.goal.identify-evidence-backed-cause",
            "Identify evidence-backed cause", null);
    }

    private void AssertTaxonomySelection(
        JsonElement intake,
        string propertyName,
        string expectedId,
        string expectedDisplayName,
        string? expectedClassId)
    {
        var selection = intake.GetProperty(propertyName);
        Assert(selection.GetProperty("id").GetString()?.Equals(expectedId, StringComparison.Ordinal) == true &&
               selection.GetProperty("displayName").GetString()?.Equals(expectedDisplayName, StringComparison.Ordinal) == true &&
               selection.GetProperty("provenance").GetString()?.Equals("ExplicitUserSelection", StringComparison.Ordinal) == true,
            $"The persisted {propertyName} changed its exact GRID taxonomy identity, text, or provenance.");
        if (expectedClassId is not null)
            Assert(selection.GetProperty("classId").GetString()?.Equals(expectedClassId, StringComparison.Ordinal) == true,
                $"The persisted {propertyName} changed its exact Class binding.");
    }

    private void VerifyPersistedEvidenceBoundResult(string intakePath)
    {
        var caseDirectory = Directory.GetParent(Directory.GetParent(intakePath)!.FullName)!.FullName;
        var resultPath = Path.Combine(caseDirectory, "result", "request-evidence-result.v1.json");
        Assert(File.Exists(resultPath), "The Casino Heist investigation did not seal an evidence result.");
        using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
        var result = document.RootElement;
        var terminalState = result.GetProperty("terminalState").GetString();
        Assert(terminalState is "EvidencePartial" or "EvidenceComplete" or "Diagnosed",
            $"The Casino Heist result ended in unsupported state '{terminalState}'.");
        Assert(result.TryGetProperty("finding", out var finding) &&
               !string.IsNullOrWhiteSpace(finding.GetString()) &&
               result.TryGetProperty("solution", out var solution) &&
               !string.IsNullOrWhiteSpace(solution.GetString()),
            "The Casino Heist result did not preserve the evidence-backed four-field finding/solution contract.");
        Assert(result.GetProperty("mutationAuthorized").ValueKind == JsonValueKind.False,
            "The read-only Casino Heist investigation unexpectedly authorized mutation.");

        var toolRunPath = Path.Combine(caseDirectory, "evidence", "tool-evidence-run.v2.json");
        Assert(File.Exists(toolRunPath), "The Casino Heist investigation did not seal its authorized evidence run.");
        using var toolRunDocument = JsonDocument.Parse(File.ReadAllText(toolRunPath));
        var receipts = toolRunDocument.RootElement.GetProperty("toolReceipts");
        Assert(receipts.ValueKind == JsonValueKind.Array && receipts.GetArrayLength() > 0,
            "The Casino Heist answer was returned without a sealed deterministic evidence receipt.");
    }

    private void SetAndCommitValue(string automationName, string value)
    {
        var element = FindByName(automationName)
            ?? throw new InvalidOperationException($"UI element '{automationName}' was not found.");
        element.SetFocus();
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"UI element '{automationName}' has no value pattern.");
        ((ValuePattern)pattern).SetValue(value);
        PressVirtualKey(VirtualKeyEnter);
        Thread.Sleep(320);
        root = AutomationElement.FromHandle(process.MainWindowHandle);
    }

    private void SetFocusedValue(string automationName, string value)
    {
        var element = FindByName(automationName)
            ?? throw new InvalidOperationException($"UI element '{automationName}' was not found.");
        element.SetFocus();
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"UI element '{automationName}' has no value pattern.");
        ((ValuePattern)pattern).SetValue(value);
        PressVirtualKey(VirtualKeyTab);
        Thread.Sleep(320);
        root = AutomationElement.FromHandle(process.MainWindowHandle);
    }

    private sealed record CanonicalSelectionCoordinates(
        KnowledgeKind Kind,
        string RecordId,
        string PathId,
        string CatalogRevisionId,
        string CompositionId,
        string DisplayAnchor);

    private void WaitForCanonicalSelectorLevel(KnowledgeKind kind, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            if (FindVisibleByAutomationIdPrefix("canonical-record:").Length > 0 ||
                FindVisibleByAutomationIdPrefix("canonical-path:").Length > 0)
                return;
            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"The {kind} selector did not expose an immediate canonical level within {timeout}. " +
            $"Visible process buttons: {DescribeVisibleProcessButtons()}. " +
            $"Top-level windows: {DescribeTopLevelWindows()}");
    }

    private void WaitForCanonicalRootOther(KnowledgeKind kind, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            if (FindVisibleByAutomationId($"canonical-other:{kind}") is not null) return;
            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"The {kind} selector did not expose root-only Other within {timeout} after its canonical level became visible.");
    }

    private void WaitForCanonicalFlyoutClosed(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            if (FindVisibleByAutomationIdPrefix("canonical-record:").Length == 0 &&
                FindVisibleByAutomationIdPrefix("canonical-path:").Length == 0 &&
                FindVisibleByAutomationIdPrefix("canonical-other:").Length == 0)
                return;
            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"An existing canonical selector flyout did not close within {timeout} before the next selector opened.");
    }

    private void CloseCanonicalFlyoutIfOpen(TimeSpan timeout)
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var isOpen = FindVisibleByAutomationIdPrefix("canonical-record:").Length != 0 ||
                     FindVisibleByAutomationIdPrefix("canonical-path:").Length != 0 ||
                     FindVisibleByAutomationIdPrefix("canonical-other:").Length != 0;
        if (!isOpen) return;

        throw new InvalidOperationException(
            "A canonical selector flyout was already open before the next selector journey. " +
            "The attach-mode acceptance does not send global Escape input into another foreground application.");
    }

    private static void PressVirtualKey(byte virtualKey)
    {
        keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
        keybd_event(virtualKey, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    private void ScrollOpenCanonicalFlyoutToTop()
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var value)) continue;
                var scroll = (ScrollPattern)value;
                var bounds = element.Current.BoundingRectangle;
                if (!scroll.Current.VerticallyScrollable || bounds.Width < 280 || bounds.Width > 360) continue;
                scroll.SetScrollPercent(ScrollPattern.NoScroll, 0);
            }
            catch (InvalidOperationException)
            {
                // A transient WinUI popup peer may disappear while another flyout closes.
            }
            catch (ElementNotAvailableException)
            {
            }
        }
        Thread.Sleep(120);
    }

    public void VerifyHomeAssistant()
    {
        NormalizeShell();
        EnsureAssistantOpen();
        var assistantShell = FindVisibleByName("Grid Assistant panel")
            ?? throw new InvalidOperationException("Chat did not expose its shell boundary.");
        VerifyTerminalRightClearance(assistantShell, "Chat");
        WaitForAssistantIntake();
        var names = DescendantNames();
        foreach (var required in new[] { "Grid chat content", "Toggle data intake form", "Investigation history", "New Investigation", "Start Investigation unavailable", "Toggle chat full screen" })
        {
            Assert(names.Any(name => name.Equals(required, StringComparison.Ordinal)), $"Chat intake surface did not expose '{required}'.");
        }
        Assert(!names.Any(name => name.Equals("Close Grid assistant", StringComparison.Ordinal)),
            "The chat panel duplicated the shell-owned close control.");
        Activate("Toggle data intake form");
        var intakeDeadline = DateTime.UtcNow.AddSeconds(5);
        do
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            names = DescendantNames();
            if (names.Any(name => name.Equals("Grid data intake form", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Select game", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Select Class", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Select Goal", StringComparison.Ordinal))) break;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < intakeDeadline);
        Assert(names.Any(name => name.Equals("Grid data intake form", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select game", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select profile", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Class", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Problem", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Timing", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select multiple tools", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select multiple mods", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Location", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Mission/Quest", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Item", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Actor", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Select Goal", StringComparison.Ordinal)),
            "The visible intake form did not expose the canonical five semantic selector groups.");
        Assert(!names.Any(name => name.Equals("Expected behavior", StringComparison.Ordinal) ||
                                  name.Equals("Reproduction or location", StringComparison.Ordinal) ||
                                  name.Equals("Desired outcome", StringComparison.Ordinal) ||
                                  name.Equals("Attach screenshots or files", StringComparison.Ordinal)),
            "The canonical DIF retained legacy prose or duplicate attachment controls.");
        var composer = FindByName("Grid message composer");
        var suggestions = FindByName("Investigation suggestions");
        Assert(composer is not null && !composer.Current.IsOffscreen &&
               suggestions is not null && !suggestions.Current.IsOffscreen,
            "Opening the compact DIF hid the fixed Composer or investigation suggestions.");
        Capture("canonical-ticket-dif");
        Activate("Toggle data intake form");
        Activate("Toggle left sidebar");
        Activate("Toggle chat full screen");
        var fullScreenChat = FindByName("Grid chat content");
        Assert(fullScreenChat is not null && !fullScreenChat.Current.IsOffscreen,
            "The authoritative full-workbench chat surface was not visible.");
        var sidePanel = FindByName("Side panel options");
        Assert(sidePanel is null || sidePanel.Current.IsOffscreen,
            "A sidebar remained behind the one-panel full-workbench chat presentation.");
        Activate("Toggle chat full screen");
        sidePanel = FindByName("Side panel options");
        Assert(sidePanel is not null && !sidePanel.Current.IsOffscreen,
            "Leaving full-workbench chat did not restore the requested sidebar panel.");
        Activate("Hide Grid Assistant");
    }

    public void VerifyDemoWorkspaceAndAssistant()
    {
        NormalizeShell();
        Activate("Skyrim Special Edition, connected game");
        var snapshot = FindByName("Game snapshot");
        Assert(snapshot is not null && !snapshot.Current.IsOffscreen,
            "Selecting a circular game route did not expose its side-panel snapshot.");
        Assert(FindVisibleByName("CORE & FRAMEWORKS") is null,
            "Opening a game snapshot also changed the main workstation.");
        Activate("Open Skyrim Special Edition workstation");
        snapshot = FindByName("Game snapshot");
        Assert(snapshot is not null && !snapshot.Current.IsOffscreen,
            "Opening the workstation from its snapshot unexpectedly closed the snapshot.");
        VerifyWorkstationGeometry(expectStacked: false);
        Activate("CORE & FRAMEWORKS");
        var enabledMod = FindByName("Disable Address Library");
        Assert(enabledMod is not null && !enabledMod.Current.IsOffscreen,
            "Expanded demo mod rows did not expose their per-row enablement checkbox.");
        ActivateElement(enabledMod!, "Disable Address Library");
        Thread.Sleep(250);
        Activate("CORE & FRAMEWORKS");
        var disabledMod = FindByName("Enable Address Library");
        Assert(disabledMod is not null && !disabledMod.Current.IsOffscreen,
            "The per-row checkbox did not apply the demo mod-state command.");
        ActivateElement(disabledMod!, "Enable Address Library");
        Thread.Sleep(250);
        Activate("CORE & FRAMEWORKS");
        VerifyWorkstationPortraitGeometry();
        EnsureAssistantOpen();
        VerifyNoPageLevelHorizontalScroll();
        Activate("Hide Grid Assistant");
    }

    public void VerifyConnectedWorkspaceGeometry()
    {
        NormalizeShell();
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var connectedGame = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
                !element.Current.IsOffscreen &&
                element.Current.Name.EndsWith(", connected game", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(element => element.Current.BoundingRectangle.Width)
            .FirstOrDefault();
        if (connectedGame is null)
            throw new InvalidOperationException("The installed authenticated product exposed no connected game route for workstation measurement.");

        ActivateElement(connectedGame, connectedGame.Current.Name);
        WaitForElementName("Tool or launch target", TimeSpan.FromSeconds(20));
        VerifyWorkstationGeometry(expectStacked: false);
        VerifyWorkstationPortraitGeometry();
        Thread.Sleep(600);
        Capture("installed-workstation-geometry");
    }

    private void VerifyWorkstationPortraitGeometry()
    {
        if (!GetWindowRect(process.MainWindowHandle, out var original))
            throw new InvalidOperationException("The workstation window bounds were unavailable before portrait verification.");

        ShowWindow(process.MainWindowHandle, ShowRestored);
        MoveWindow(process.MainWindowHandle, original.Left, original.Top, 720, 960, true);
        Thread.Sleep(600);
        VerifyWorkstationGeometry(expectStacked: true);
        VerifyPortraitAuxiliaryHeaderGeometry();
        MoveWindow(
            process.MainWindowHandle,
            original.Left,
            original.Top,
            Math.Max(1200, original.Right - original.Left),
            Math.Max(720, original.Bottom - original.Top),
            true);
        Thread.Sleep(600);
    }

    private void VerifyPortraitAuxiliaryHeaderGeometry()
    {
        Activate("Toggle bottom panel");
        var bottomPanel = FindVisibleByName("Bottom tool panel")
            ?? throw new InvalidOperationException("Portrait Console did not expose its shell boundary.");
        VerifyUniversalHeaderGeometry(
            "Console portrait",
            bottomPanel,
            FindVisibleByAutomationId("TerminalTabButton"),
            FindVisibleByAutomationId("ClearTerminalButton"),
            FindVisibleByName("Console panel header row"));
        Activate("Toggle bottom panel");

        EnsureAssistantOpen();
        var assistantPanel = FindVisibleByName("Grid Assistant panel")
            ?? throw new InvalidOperationException("Portrait Chat did not expose its shell boundary.");
        VerifyUniversalHeaderGeometry(
            "Chat portrait",
            assistantPanel,
            FindVisibleByName("Chat"),
            FindVisibleByAutomationId("NewChatButton"),
            FindVisibleByName("Chat panel header row"));
        Activate("Hide Grid Assistant");
    }

    private void VerifyWorkstationGeometry(bool expectStacked)
    {
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        var workbench = FindVisibleByName("Main workbench panel")
            ?? throw new InvalidOperationException("The game workstation did not expose its main workbench boundary.");
        var layoutRoot = FindVisibleByName("Workstation layout root")
            ?? throw new InvalidOperationException("The workstation did not expose its authoritative layout root.");
        var content = FindVisibleByName("Workstation content")
            ?? throw new InvalidOperationException("The workstation did not expose its authoritative content rectangle.");
        var modHeader = FindVisibleByName("Mod pane header")
            ?? throw new InvalidOperationException("The workstation did not expose its mod-pane header.");
        var modBody = FindVisibleByName("Mod pane body")
            ?? throw new InvalidOperationException("The workstation did not expose its mod-pane body.");
        var pluginHeader = FindVisibleByName("Plugin pane header")
            ?? throw new InvalidOperationException("The workstation did not expose its plugin-pane header.");
        var pluginBody = FindVisibleByName("Plugin pane body")
            ?? throw new InvalidOperationException("The workstation did not expose its plugin-pane body.");
        var modSearch = FindVisibleByName("Search mods")
            ?? throw new InvalidOperationException("The workstation did not expose its mod filter.");
        var pluginSearch = FindVisibleByName("Search resolved environment")
            ?? throw new InvalidOperationException("The workstation did not expose its plugin filter.");
        var modLcd = FindVisibleByName("Active mods")
            ?? throw new InvalidOperationException("The workstation did not expose the accessible Active mods LCD.");
        var pluginLcd = FindVisibleByName("Active plugins")
            ?? throw new InvalidOperationException("The workstation did not expose the accessible Active plugins LCD.");

        VerifyUniversalHeaderGeometry(
            "Main",
            workbench,
            FindVisibleByName("Home tab"),
            FindVisibleByAutomationId("EditorMoreActionsButton"),
            headerRow: null);
        VerifyUniversalHeaderGeometry(
            "Plugin",
            FindVisibleByName("Plugin pane outline"),
            FindVisibleByName("Plugins"),
            rightControl: null,
            headerRow: FindVisibleByName("Plugin panel tab strip"));

        const double tolerance = 1.0;
        var workbenchBounds = workbench.Current.BoundingRectangle;
        var rootBounds = layoutRoot.Current.BoundingRectangle;
        var contentBounds = content.Current.BoundingRectangle;
        // WinUI's named transparent root reports the union of its visible
        // content, so its automation bounds intentionally stop at the real
        // empty perimeter columns rather than including them.
        Assert(Math.Abs(rootBounds.Right - (workbenchBounds.Right - 5)) <= tolerance,
            $"Workstation root right '{rootBounds.Right}' was not exactly 5 px inside shell/client right '{workbenchBounds.Right}'.");
        Assert(Math.Abs(contentBounds.Right - (workbenchBounds.Right - 5)) <= tolerance,
            $"Workstation content right '{contentBounds.Right}' was not exactly 5 px inside shell/client right '{workbenchBounds.Right}'.");

        var modHeaderBounds = modHeader.Current.BoundingRectangle;
        var modBodyBounds = modBody.Current.BoundingRectangle;
        var pluginHeaderBounds = pluginHeader.Current.BoundingRectangle;
        var pluginBodyBounds = pluginBody.Current.BoundingRectangle;
        var modSearchBounds = modSearch.Current.BoundingRectangle;
        var pluginSearchBounds = pluginSearch.Current.BoundingRectangle;
        var modLcdBounds = modLcd.Current.BoundingRectangle;
        var pluginLcdBounds = pluginLcd.Current.BoundingRectangle;

        foreach (var (name, bounds) in new[]
        {
            ("mod header", modHeaderBounds), ("mod body", modBodyBounds),
            ("plugin header", pluginHeaderBounds), ("plugin body", pluginBodyBounds),
            ("mod search", modSearchBounds), ("plugin search", pluginSearchBounds),
            ("mod LCD", modLcdBounds), ("plugin LCD", pluginLcdBounds),
        })
        {
            Assert(bounds.Left >= contentBounds.Left - tolerance && bounds.Right <= contentBounds.Right + tolerance,
                $"The {name} bounds '{bounds}' escaped the authoritative content rectangle '{contentBounds}'.");
        }

        Assert(Math.Abs(modLcdBounds.Width - 59) <= tolerance && Math.Abs(pluginLcdBounds.Width - 49) <= tolerance,
            $"LCD capacities changed their fixed rendered widths: mods '{modLcdBounds.Width}', plugins '{pluginLcdBounds.Width}'.");
        Assert(Math.Abs(modSearchBounds.Right - modBodyBounds.Right) <= tolerance &&
               Math.Abs(pluginSearchBounds.Right - pluginBodyBounds.Right) <= tolerance,
            "A workstation search field was not right-aligned with its pane.");
        Assert(modSearchBounds.Width <= 281 && pluginSearchBounds.Width <= 281,
            $"A workstation search field exceeded its compact 280 px cap: mods '{modSearchBounds.Width}', plugins '{pluginSearchBounds.Width}'.");
        Assert(modSearchBounds.Left >= modLcdBounds.Right + 4 && pluginSearchBounds.Left >= pluginLcdBounds.Right + 4,
            "A workstation search field did not follow its leftmost LCD with the required spacing.");

        var visibleActiveLabels = root.FindAll(
                TreeScope.Descendants,
                new OrCondition(
                    new PropertyCondition(AutomationElement.NameProperty, "Active mods"),
                    new PropertyCondition(AutomationElement.NameProperty, "Active plugins")))
            .Cast<AutomationElement>()
            .Where(element => !element.Current.IsOffscreen &&
                              element.Current.ControlType == ControlType.Text)
            .ToArray();
        Assert(visibleActiveLabels.Length == 0,
            "Visible Active mods/plugins text remained before the accessible LCDs.");

        var divider = FindVisibleByName("Resize mod and environment panes");
        if (expectStacked)
        {
            Assert(divider is null, "The center divider remained visible in portrait/stacked layout.");
            Assert(Math.Abs(modBodyBounds.Left - pluginBodyBounds.Left) <= tolerance &&
                   Math.Abs(modBodyBounds.Right - pluginBodyBounds.Right) <= tolerance,
                $"Portrait panes did not retain the same full-width allocation: mod '{modBodyBounds}', plugin '{pluginBodyBounds}'.");
        }
        else
        {
            VerifyTerminalRightClearance(workbench, "Main window");
            Assert(divider is not null, "The center divider was not visible in landscape layout.");
            var dividerBounds = divider!.Current.BoundingRectangle;
            Assert(dividerBounds.Left >= contentBounds.Left - tolerance && dividerBounds.Right <= contentBounds.Right + tolerance,
                "The center divider escaped the authoritative content rectangle.");
            Assert(modHeaderBounds.Right <= dividerBounds.Left + tolerance && modBodyBounds.Right <= dividerBounds.Left + tolerance,
                "The mod-pane header or body crossed its allocated landscape column.");
            Assert(pluginHeaderBounds.Left >= dividerBounds.Right - tolerance && pluginBodyBounds.Left >= dividerBounds.Right - tolerance,
                "The plugin-pane header or body crossed the center divider.");

            var toolSelector = FindVisibleByName("Tool or launch target")
                ?? throw new InvalidOperationException("The workstation tool-row baseline was unavailable.");
            var editorTabs = FindVisibleByName("Open tabs")
                ?? throw new InvalidOperationException("The main editor-tab baseline was unavailable.");
            var pluginOutline = FindVisibleByName("Plugin pane outline")
                ?? throw new InvalidOperationException("The workstation did not expose its authoritative plugin-pane outline.");
            var pluginTab = FindVisibleByName("Plugins")
                ?? throw new InvalidOperationException("The workstation did not expose its first plugin tab.");
            var toolBounds = toolSelector.Current.BoundingRectangle;
            var editorTabBounds = editorTabs.Current.BoundingRectangle;
            var pluginOutlineBounds = pluginOutline.Current.BoundingRectangle;
            var pluginTabBounds = pluginTab.Current.BoundingRectangle;
            Assert(Math.Abs(modBodyBounds.Top - toolBounds.Top) <= tolerance,
                $"The mod pane top '{modBodyBounds.Top}' did not align with the tool-row baseline '{toolBounds.Top}'.");
            Assert(pluginOutlineBounds.Top >= toolBounds.Bottom + 4,
                $"The plugin pane top '{pluginOutlineBounds.Top}' did not remain below the tool row ending at '{toolBounds.Bottom}'.");
            Assert(modBodyBounds.Top < pluginOutlineBounds.Top && modBodyBounds.Height > pluginOutlineBounds.Height,
                "The landscape mod pane was not visibly taller than the plugin pane.");
            Assert(Math.Abs(pluginTabBounds.Left - (pluginOutlineBounds.Left + 4)) <= tolerance,
                $"The first plugin tab left '{pluginTabBounds.Left}' was not 3 px inside the pane interior beginning at '{pluginOutlineBounds.Left + 1}'.");

            Console.WriteLine("INSTALLED_WORKSTATION_GEOMETRY_BEGIN");
            Console.WriteLine("Element|Left|Right|Width|Required max-right|PASS/FAIL");
            WriteGeometryRow("Workstation root", rootBounds, contentBounds.Right, tolerance);
            WriteGeometryRow("Mod pane header", modHeaderBounds, dividerBounds.Left, tolerance);
            WriteGeometryRow("Mod pane body", modBodyBounds, dividerBounds.Left, tolerance);
            WriteGeometryRow("Mod search", modSearchBounds, dividerBounds.Left, tolerance);
            WriteGeometryRow("Mod LCD", modLcdBounds, dividerBounds.Left, tolerance);
            WriteGeometryRow("Plugin pane header", pluginHeaderBounds, contentBounds.Right, tolerance);
            WriteGeometryRow("Plugin pane body", pluginBodyBounds, contentBounds.Right, tolerance);
            WriteGeometryRow("Plugin search", pluginSearchBounds, contentBounds.Right, tolerance);
            WriteGeometryRow("Plugin LCD", pluginLcdBounds, contentBounds.Right, tolerance);
            Console.WriteLine("INSTALLED_WORKSTATION_GEOMETRY_END");
            Console.WriteLine(
                $"INSTALLED_WORKSTATION_VERTICAL|MainTabsTop={editorTabBounds.Top:0.##}|MainTabsBottom={editorTabBounds.Bottom:0.##}|Tool={toolBounds.Top:0.##}|Mod={modBodyBounds.Top:0.##}|Plugin={pluginOutlineBounds.Top:0.##}|PluginTabOffset={pluginTabBounds.Left - pluginOutlineBounds.Left:0.##}");
        }
    }

    private void VerifyUniversalHeaderGeometry(
        string surfaceName,
        AutomationElement? surface,
        AutomationElement? firstTab,
        AutomationElement? rightControl,
        AutomationElement? headerRow)
    {
        Assert(surface is not null, $"{surfaceName} did not expose its tab-owning surface.");
        Assert(firstTab is not null, $"{surfaceName} did not expose its first tab.");
        if (surface is null || firstTab is null) return;

        const double tolerance = 1.0;
        const double borderThickness = 1.0;
        const double requiredInset = 3.0;
        var surfaceBounds = surface.Current.BoundingRectangle;
        var tabBounds = firstTab.Current.BoundingRectangle;
        var rowBounds = headerRow?.Current.BoundingRectangle ?? tabBounds;
        var interiorLeft = surfaceBounds.Left + borderThickness;
        var interiorTop = surfaceBounds.Top + borderThickness;
        var interiorRight = surfaceBounds.Right - borderThickness;
        var leftInset = tabBounds.Left - interiorLeft;
        var topInset = rowBounds.Top - interiorTop;

        Assert(Math.Abs(leftInset - requiredInset) <= tolerance,
            $"{surfaceName} first-tab inset was '{leftInset}', not 3 DIP.");
        Assert(Math.Abs(topInset - requiredInset) <= tolerance,
            $"{surfaceName} header-row top inset was '{topInset}', not 3 DIP.");

        var rightInset = double.NaN;
        var centerDelta = double.NaN;
        if (rightControl is not null)
        {
            var controlBounds = rightControl.Current.BoundingRectangle;
            rightInset = interiorRight - controlBounds.Right;
            centerDelta = Math.Abs(
                (tabBounds.Top + (tabBounds.Height / 2)) -
                (controlBounds.Top + (controlBounds.Height / 2)));
            Assert(Math.Abs(rightInset - requiredInset) <= tolerance,
                $"{surfaceName} right-control inset was '{rightInset}', not 3 DIP.");
            Assert(centerDelta <= tolerance,
                $"{surfaceName} tab/control center delta was '{centerDelta}', greater than 1 DIP.");
        }

        var separatorY = surfaceName.Contains("Chat", StringComparison.Ordinal) ||
                         surfaceName.Contains("Console", StringComparison.Ordinal)
            ? rowBounds.Bottom.ToString("0.##")
            : "N/A";

        Console.WriteLine(
            $"INSTALLED_TAB_GEOMETRY|{surfaceName}|SurfaceLeft={surfaceBounds.Left:0.##}|SurfaceTop={surfaceBounds.Top:0.##}|" +
            $"SurfaceRight={surfaceBounds.Right:0.##}|SurfaceBottom={surfaceBounds.Bottom:0.##}|Border=1|" +
            $"InteriorLeft={interiorLeft:0.##}|InteriorTop={interiorTop:0.##}|InteriorRight={interiorRight:0.##}|" +
            $"HeaderTop={rowBounds.Top:0.##}|HeaderBottom={rowBounds.Bottom:0.##}|" +
            $"FirstTabLeft={tabBounds.Left:0.##}|FirstTabTop={tabBounds.Top:0.##}|TabCenter={tabBounds.Top + (tabBounds.Height / 2):0.##}|" +
            $"RightControlRight={(rightControl is null ? "N/A" : rightControl.Current.BoundingRectangle.Right.ToString("0.##"))}|" +
            $"ControlCenter={(rightControl is null ? "N/A" : (rightControl.Current.BoundingRectangle.Top + (rightControl.Current.BoundingRectangle.Height / 2)).ToString("0.##"))}|" +
            $"LeftInset={leftInset:0.##}|TopInset={topInset:0.##}|RightInset={(double.IsNaN(rightInset) ? "N/A" : rightInset.ToString("0.##"))}|" +
            $"CenterDelta={(double.IsNaN(centerDelta) ? "N/A" : centerDelta.ToString("0.##"))}|SeparatorY={separatorY}");
    }

    private void VerifyTerminalRightClearance(AutomationElement surface, string surfaceName)
    {
        const double tolerance = 1.0;
        var clientBounds = GetClientScreenBounds();
        var surfaceBounds = surface.Current.BoundingRectangle;
        Assert(Math.Abs(surfaceBounds.Right - (clientBounds.Right - 8)) <= tolerance,
            $"{surfaceName} right '{surfaceBounds.Right}' did not preserve the shared 8 px shell terminal column ending at client right '{clientBounds.Right}'.");

        var stroke = SampleScreenPixel((int)Math.Round(surfaceBounds.Right - 1), (int)Math.Round(surfaceBounds.Top + (surfaceBounds.Height / 2)));
        var clearance = SampleScreenPixel((int)Math.Round(surfaceBounds.Right + 2), (int)Math.Round(surfaceBounds.Top + (surfaceBounds.Height / 2)));
        Assert(IsNear(stroke, Color.FromArgb(43, 45, 48), 3),
            $"{surfaceName} right stroke was not visibly rendered: observed {stroke}.");
        Assert(IsNear(clearance, Color.FromArgb(24, 24, 24), 3),
            $"{surfaceName} had no genuine shell-background clearance outside its right stroke: observed {clearance}.");
        Console.WriteLine(
            $"INSTALLED_SHELL_RIGHT_EDGE|{surfaceName}|SurfaceRight={surfaceBounds.Right:0.##}|ClientRight={clientBounds.Right:0.##}|Clearance={clientBounds.Right - surfaceBounds.Right:0.##}|Stroke={stroke.R},{stroke.G},{stroke.B}|Outside={clearance.R},{clearance.G},{clearance.B}");
    }

    private System.Windows.Rect GetClientScreenBounds()
    {
        if (!GetClientRect(process.MainWindowHandle, out var client))
            throw new InvalidOperationException("The GRID Win32 client rectangle was unavailable.");
        var origin = new NativePoint { X = client.Left, Y = client.Top };
        if (!ClientToScreen(process.MainWindowHandle, ref origin))
            throw new InvalidOperationException("The GRID Win32 client origin could not be mapped to screen coordinates.");
        return new System.Windows.Rect(origin.X, origin.Y, client.Right - client.Left, client.Bottom - client.Top);
    }

    private static Color SampleScreenPixel(int x, int y)
    {
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
        return bitmap.GetPixel(0, 0);
    }

    private static bool IsNear(Color actual, Color expected, int tolerance) =>
        Math.Abs(actual.R - expected.R) <= tolerance &&
        Math.Abs(actual.G - expected.G) <= tolerance &&
        Math.Abs(actual.B - expected.B) <= tolerance;

    private static void WriteGeometryRow(string name, System.Windows.Rect bounds, double maximumRight, double tolerance)
    {
        var status = bounds.Right <= maximumRight + tolerance ? "PASS" : "FAIL";
        Console.WriteLine(
            $"{name}|{bounds.Left:0.##}|{bounds.Right:0.##}|{bounds.Width:0.##}|{maximumRight:0.##}|{status}");
    }

    public void VerifyResponsiveMatrixWithAssistant()
    {
        EnsureAssistantOpen();
        VerifyResponsiveMatrix();
        Activate("Hide Grid Assistant");
    }

    private void NormalizeShell()
    {
        // Every stateful UI acceptance case starts from the same product baseline.
        // This prevents Explorer/Console/Assistant state from leaking between tests.

        var closeTaskboard = FindVisibleByName("Close Activity Taskboard");
        if (closeTaskboard is not null)
        {
            ActivateElement(closeTaskboard, "Close Activity Taskboard");
            Thread.Sleep(100);
        }

        var restoreBottom = FindVisibleByName("Restore bottom panel");
        if (restoreBottom is not null)
        {
            ActivateElement(restoreBottom, "Restore bottom panel");
            Thread.Sleep(100);
        }

        // Home is the canonical route. The G intentionally opens the Home side panel,
        // so the left panel is closed again below to establish the clean shell baseline.
        var home = FindVisibleByName("Home");
        if (home is not null)
        {
            ActivateElement(home, "Home");
            Thread.Sleep(100);
        }

        var hideAssistant = FindVisibleByName("Hide Grid Assistant");
        if (hideAssistant is not null)
        {
            ActivateElement(hideAssistant, "Hide Grid Assistant");
            Thread.Sleep(100);
        }

        var terminal = FindVisibleByName("Grid terminal command");
        if (terminal is not null)
        {
            var bottomToggle = FindVisibleByName("Toggle bottom panel")
                ?? throw new InvalidOperationException(
                    "A visible terminal did not expose the shell-owned bottom-panel toggle.");
            ActivateElement(bottomToggle, "Toggle bottom panel");
            Thread.Sleep(100);
        }

        var sidePanel = FindVisibleByName("Side panel options");
        if (sidePanel is not null)
        {
            // Route changes can replace WinUI automation peers one dispatcher pass
            // after the panel itself becomes visible. Use the bounded activating
            // lookup so the test observes the settled shell rather than a stale peer.
            Activate("Toggle left sidebar");
        }

        Assert(FindVisibleByName("Grid Assistant panel") is null,
            "Shell normalization left the assistant visible.");
        Assert(FindVisibleByName("Grid terminal command") is null,
            "Shell normalization left the terminal visible.");
        Assert(FindVisibleByName("Side panel options") is null,
            "Shell normalization left the left panel visible.");
        Assert(FindVisibleByName("GRID Taskboard") is null,
            "Shell normalization left the Taskboard visible.");
    }

    private AutomationElement? FindVisibleByName(string automationName)
    {
        var matches = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, automationName));

        foreach (AutomationElement element in matches)
        {
            var bounds = element.Current.BoundingRectangle;
            if (!element.Current.IsOffscreen && !bounds.IsEmpty)
            {
                return element;
            }
        }

        return null;
    }

    private AutomationElement? FindVisibleByAutomationId(string automationId)
    {
        var matches = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

        foreach (AutomationElement element in matches)
        {
            var bounds = element.Current.BoundingRectangle;
            if (!element.Current.IsOffscreen && !bounds.IsEmpty)
            {
                return element;
            }
        }

        return null;
    }

    private AutomationElement[] FindVisibleByAutomationIdPrefix(string automationIdPrefix)
    {
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
            {
                try
                {
                    var bounds = element.Current.BoundingRectangle;
                    return element.Current.AutomationId.StartsWith(automationIdPrefix, StringComparison.Ordinal) &&
                           !element.Current.IsOffscreen &&
                           !bounds.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .ToArray();
    }

    private AutomationElement[] FindByAutomationIdPrefix(string automationIdPrefix)
    {
        return root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
            {
                try
                {
                    return element.Current.AutomationId.StartsWith(automationIdPrefix, StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .ToArray();
    }

    private AutomationElement[] FindVisibleProcessElementsByAutomationIdPrefix(string automationIdPrefix)
    {
        return EnumerateProcessElements()
            .Where(element =>
            {
                try
                {
                    return element.Current.AutomationId.StartsWith(automationIdPrefix, StringComparison.Ordinal) &&
                           !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .ToArray();
    }

    private string DescribeVisibleProcessButtons()
    {
        return string.Join(" | ", EnumerateProcessElements()
            .Where(element =>
            {
                try
                {
                    return element.Current.ControlType == ControlType.Button &&
                           !element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .Select(element => $"{element.Current.Name} [{element.Current.AutomationId}]"));
    }

    private IEnumerable<AutomationElement> EnumerateProcessElements()
    {
        var processCondition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, processCondition))
        {
            yield return window;
            foreach (AutomationElement descendant in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                yield return descendant;
        }
    }

    private static string DescribeTopLevelWindows() => string.Join(" | ",
        AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Select(element =>
            {
                try
                {
                    return $"{element.Current.ControlType.ProgrammaticName}:{element.Current.Name} " +
                           $"[{element.Current.AutomationId}] pid={element.Current.ProcessId}";
                }
                catch (ElementNotAvailableException)
                {
                    return "<unavailable>";
                }
            }));

    private static void ActivateElement(AutomationElement element, string automationName)
    {
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
            return;
        }

        throw new InvalidOperationException(
            $"UI element '{automationName}' did not expose an invokable automation pattern.");
    }

    private void EnsureAssistantOpen()
    {
        AutomationElement? toggle = null;
        var showInvoked = false;
        var controlDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < controlDeadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Grid exited before the assistant could open (exit code {process.ExitCode}).");
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                root = AutomationElement.FromHandle(process.MainWindowHandle);
            toggle = FindReadyAssistantToggle();
            if (toggle is not null && IsAssistantExpanded(toggle)) return;
            if (toggle is not null && IsAssistantCollapsed(toggle))
            {
                ActivateElement(toggle, "Show Grid Assistant");
                showInvoked = true;
                break;
            }
            Thread.Sleep(100);
        }
        if (!showInvoked)
        {
            var diagnostics = WriteStartupDiagnostics(
                process,
                root,
                captureRoot,
                "assistant-pre-invoke-timeout");
            throw new InvalidOperationException(
                "The closed assistant shell did not expose an enabled visible Show Grid Assistant control. " +
                $"Diagnostics: {diagnostics}");
        }

        // This helper owns only shell state. Content readiness is verified by the
        // specific chat test that needs it. That distinction matters while other
        // specialized center-workspace surfaces (for example full Console) are active.
        var transitionDeadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < transitionDeadline)
        {
            if (process.HasExited)
                throw new InvalidOperationException(
                    $"Grid exited while opening the assistant (exit code {process.ExitCode}).");
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                root = AutomationElement.FromHandle(process.MainWindowHandle);
            toggle = FindReadyAssistantToggle();
            if (toggle is not null && IsAssistantExpanded(toggle)) return;
            Thread.Sleep(100);
        }

        var transitionDiagnostics = WriteStartupDiagnostics(
            process,
            root,
            captureRoot,
            "assistant-post-invoke-timeout");
        throw new InvalidOperationException(
            "Opening the assistant shell did not transition the shell to its expanded state. " +
            $"Diagnostics: {transitionDiagnostics}");
    }

    private AutomationElement? FindReadyAssistantToggle()
    {
        var matches = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element =>
            {
                try
                {
                    var current = element.Current;
                    var isAssistantToggle = current.AutomationId.Equals(
                                                "AssistantToggleButton",
                                                StringComparison.Ordinal) ||
                                            current.Name is "Show Grid Assistant" or "Hide Grid Assistant";
                    return isAssistantToggle && current.ProcessId == process.Id && current.IsEnabled &&
                           !current.IsOffscreen && !current.BoundingRectangle.IsEmpty;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .OrderByDescending(element =>
            {
                try
                {
                    return element.Current.AutomationId.Equals(
                        "AssistantToggleButton",
                        StringComparison.Ordinal);
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            })
            .ToArray();

        return matches.FirstOrDefault();
    }

    private bool IsAssistantExpanded(AutomationElement toggle)
    {
        try
        {
            var toggleOn = toggle.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                ? ((TogglePattern)pattern).Current.ToggleState == ToggleState.On
                : toggle.Current.Name.Equals("Hide Grid Assistant", StringComparison.Ordinal);
            if (!toggleOn) return false;

            var panel = FindVisibleByName("Grid Assistant panel");
            return panel is not null && panel.Current.ProcessId == process.Id;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool IsAssistantCollapsed(AutomationElement toggle)
    {
        try
        {
            return toggle.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern)
                ? ((TogglePattern)pattern).Current.ToggleState == ToggleState.Off
                : toggle.Current.Name.Equals("Show Grid Assistant", StringComparison.Ordinal);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private void WaitForAssistantIntake()
    {
        // The shell is the stable WinUI automation boundary. Nested ScrollViewer
        // materialization is not a reliable visibility sentinel across WinUI UIA passes.
        // Functional chat controls are verified separately by VerifyHomeAssistant.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var assistantPanel = FindByName("Grid Assistant panel");
            if (assistantPanel is not null &&
                !assistantPanel.Current.IsOffscreen &&
                !assistantPanel.Current.BoundingRectangle.IsEmpty)
            {
                return;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException(
            "The expanded assistant did not expose a visible stable assistant shell.");
    }

    private AutomationElement? FindByName(string automationName) => root.FindFirst(
        TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, automationName));

    private AutomationElement? FindActivatableByName(string automationName)
    {
        var matches = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, automationName));
        AutomationElement? fallback = null;
        foreach (AutomationElement element in matches)
        {
            var activatable = element.TryGetCurrentPattern(InvokePattern.Pattern, out _) ||
                              element.TryGetCurrentPattern(TogglePattern.Pattern, out _) ||
                              element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _);
            if (!activatable) continue;
            fallback ??= element;
            if (!element.Current.IsOffscreen && !element.Current.BoundingRectangle.IsEmpty) return element;
        }
        return fallback;
    }

    public void VerifyResponsiveMatrix()
    {
        foreach (var (width, height) in Viewports)
        {
            MoveWindow(process.MainWindowHandle, 0, 0, width, height, true);
            // WinUI completes NavigationView, frame, and operator reflow on separate
            // dispatcher passes. Read UIA only after those bounds have settled.
            Thread.Sleep(500);
            var elements = FindControlElementsAfterResize();
            var client = root.Current.BoundingRectangle;
            foreach (var element in elements)
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || element.Current.IsOffscreen ||
                    element.Current.Name.Equals("PopupHost", StringComparison.Ordinal)) continue;
                Assert(bounds.Left >= client.Left - 2 && bounds.Right <= client.Right + 2,
                    $"'{element.Current.Name}' bounds '{bounds}' exceeded the {width}x{height} client bounds '{client}'.");
            }
            var applicationMenu = elements.FirstOrDefault(element =>
                element.Current.Name.Equals("Application menu", StringComparison.Ordinal));
            var applicationMenuVisible = applicationMenu is not null &&
                                         !applicationMenu.Current.IsOffscreen &&
                                         !applicationMenu.Current.BoundingRectangle.IsEmpty;
            Assert(applicationMenuVisible == (width <= height),
                $"The File/Edit/View/Tools/Help hamburger did not match the {width}x{height} square-or-narrow rule.");
            var assistant = elements.FirstOrDefault(element =>
                element.Current.Name.Equals("Grid Assistant panel", StringComparison.Ordinal));
            var assistantVisible = assistant is not null && !assistant.Current.IsOffscreen &&
                                   !assistant.Current.BoundingRectangle.IsEmpty;
            if (assistantVisible && width >= 960)
            {
                var visibleAssistant = assistant!;
                var workbench = elements.FirstOrDefault(element =>
                    element.Current.Name.Equals("Main workbench panel", StringComparison.Ordinal))
                    ?? throw new InvalidOperationException(
                        $"Chat replaced the workbench instead of docking beside it at {width}x{height}.");
                Assert(!workbench.Current.IsOffscreen && !workbench.Current.BoundingRectangle.IsEmpty,
                    $"Chat replaced the workbench instead of docking beside it at {width}x{height}.");
                Assert(workbench.Current.BoundingRectangle.Right <= visibleAssistant.Current.BoundingRectangle.Left,
                    $"Docked Chat overlapped the workbench at {width}x{height}.");
            }
            VerifyNoPageLevelHorizontalScroll();
            if (width is 1920 or 960 or 1366)
            {
                Capture($"success-{width}x{height}");
            }
        }
    }

    public void VerifyWindowStateTransitions()
    {
        ShowWindow(process.MainWindowHandle, ShowMaximized);
        Thread.Sleep(600);
        Assert(IsZoomed(process.MainWindowHandle), "Grid did not enter the maximized window state.");
        VerifyCurrentWindowBounds("maximized");

        ShowWindow(process.MainWindowHandle, ShowRestored);
        Thread.Sleep(600);
        Assert(!IsZoomed(process.MainWindowHandle), "Grid did not return to the restored window state.");
        // WinUI's child screen coordinates retain the pre-restore origin in UIA,
        // so restored layout is verified through scroll patterns; the preceding
        // explicit resize matrix supplies authoritative bounding-box coverage.
        root = AutomationElement.FromHandle(process.MainWindowHandle);
        VerifyNoPageLevelHorizontalScroll();
    }

    private void VerifyCurrentWindowBounds(string state)
    {
        string? lastViolation = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            lastViolation = null;
            var elements = FindControlElementsAfterResize();
            if (!GetWindowRect(process.MainWindowHandle, out var client))
            {
                throw new InvalidOperationException($"The {state} native window bounds were unavailable.");
            }
            foreach (var element in elements)
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty || element.Current.IsOffscreen ||
                    element.Current.Name.Equals("PopupHost", StringComparison.Ordinal)) continue;
                if (bounds.Left < client.Left - 2 || bounds.Right > client.Right + 2)
                {
                    lastViolation = $"'{element.Current.Name}' bounds '{bounds}' exceeded the {state} native window " +
                        $"bounds '{client.Left},{client.Top},{client.Right - client.Left},{client.Bottom - client.Top}'.";
                    break;
                }
            }

            if (lastViolation is null)
            {
                VerifyNoPageLevelHorizontalScroll();
                return;
            }

            Thread.Sleep(250);
        }

        Assert(false, lastViolation ?? $"The {state} automation bounds did not stabilize.");
    }

    private AutomationElement[] FindControlElementsAfterResize()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                root = AutomationElement.FromHandle(process.MainWindowHandle);
                return root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.IsControlElementProperty, true))
                    .Cast<AutomationElement>()
                    .ToArray();
            }
            catch (ElementNotAvailableException) when (attempt < 4)
            {
                Thread.Sleep(150);
            }
        }

        throw new ElementNotAvailableException("The Grid automation tree did not stabilize after resize.");
    }

    private void VerifyNoPageLevelHorizontalScroll()
    {
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants,
                     new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true)))
        {
            if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) &&
                pattern is ScrollPattern scroll &&
                scroll.Current.HorizontallyScrollable)
            {
                var name = element.Current.Name;
                // WinUI TabView exposes its bounded header-strip navigation as
                // an unnamed horizontal ScrollPattern. It scrolls tab headers
                // inside the control and never widens the page or workspace.
                var allowedDenseSurface = name.Contains("mod", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("plugin", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("data", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("provider", StringComparison.OrdinalIgnoreCase) ||
                    element.Current.AutomationId.Equals("TabListView", StringComparison.Ordinal);
                Assert(allowedDenseSurface,
                    $"Page-level horizontal scrolling was exposed by '{name}' " +
                    $"(automation id '{element.Current.AutomationId}', control type '{element.Current.ControlType.ProgrammaticName}', " +
                    $"bounds '{element.Current.BoundingRectangle}').");
            }
        }
    }

    private void Activate(string automationName)
    {
        AutomationElement? element = null;
        for (var attempt = 0; attempt < 12 && element is null; attempt++)
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            element = FindActivatableByName(automationName);
            if (element is null) Thread.Sleep(100);
        }
        Assert(element is not null, $"UI element '{automationName}' was not found.");
        if (element is null) throw new InvalidOperationException($"UI element '{automationName}' was not found.");
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
        {
            ((InvokePattern)invoke).Invoke();
        }
        else if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
        {
            ((TogglePattern)toggle).Toggle();
        }
        else if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
        {
            ((SelectionItemPattern)selection).Select();
        }
        else
        {
            throw new InvalidOperationException($"UI element '{automationName}' has no invokable selection pattern.");
        }
        Thread.Sleep(220);
        root = AutomationElement.FromHandle(process.MainWindowHandle);
    }

    private void ActivateAllowingAutomationTimeout(string automationName)
    {
        try
        {
            Activate(automationName);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80131505))
        {
            // WinUI UIA waits for the invoked handler. Selecting a game performs
            // exact package verification synchronously and may exceed UIA's RPC
            // timeout even though the action was delivered. The caller waits for
            // a concrete selected-profile postcondition before another action.
        }
    }

    private void SetValue(string automationName, string value)
    {
        var element = FindByName(automationName)
            ?? throw new InvalidOperationException($"UI element '{automationName}' was not found.");
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            throw new InvalidOperationException($"UI element '{automationName}' has no value pattern.");
        ((ValuePattern)pattern).SetValue(value);
        Thread.Sleep(220);
        root = AutomationElement.FromHandle(process.MainWindowHandle);
    }

    private static bool Covers(System.Windows.Rect cover, System.Windows.Rect target)
    {
        if (cover.IsEmpty || target.IsEmpty) return false;
        const double tolerance = 1.0;
        return cover.Left <= target.Left + tolerance
            && cover.Top <= target.Top + tolerance
            && cover.Right >= target.Right - tolerance
            && cover.Bottom >= target.Bottom - tolerance;
    }

    private void WaitForElementName(string expectedName, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            try
            {
                root = AutomationElement.FromHandle(process.MainWindowHandle);
                if (DescendantNames().Any(name => name.Equals(expectedName, StringComparison.OrdinalIgnoreCase)))
                    return;
            }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80131505))
            {
                // A long-running WinUI handler can temporarily exceed UIA's RPC timeout.
            }
            Thread.Sleep(100);
        }
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited);

        throw new InvalidOperationException(
            $"GRID did not expose '{expectedName}' within {timeout}.");
    }

    private string[] DescendantNames()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var names = new List<string>();
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try
                    {
                        var name = element.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
                    }
                    catch (ElementNotAvailableException)
                    {
                        // WinUI may replace a transient peer between FindAll and the
                        // property read. The current tree is what the assertion needs.
                    }
                }
                return names.ToArray();
            }
            catch (ElementNotAvailableException) when (attempt < 4)
            {
                Thread.Sleep(120);
                root = AutomationElement.FromHandle(process.MainWindowHandle);
            }
        }
        throw new ElementNotAvailableException("The Grid automation tree did not stabilize while enumerating descendants.");
    }

    private void Assert(bool condition, string message)
    {
        if (condition) return;
        Capture("failure");
        throw new InvalidOperationException($"{message} Diagnostic captures: {captureRoot}");
    }

    private void Capture(string label)
    {
        try
        {
            ShowWindow(process.MainWindowHandle, ShowRestored);
            SetForegroundWindow(process.MainWindowHandle);
            Thread.Sleep(180);
            if (!GetWindowRect(process.MainWindowHandle, out var rect)) return;
            using var bitmap = new Bitmap(Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top));
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(captureRoot, $"{label}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.png"));
        }
        catch (Exception exception)
        {
            File.WriteAllText(Path.Combine(captureRoot, "screenshot-unavailable.txt"), exception.ToString());
        }
    }

    public void Dispose()
    {
        if (ownsProcess && !process.HasExited)
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
        process.Dispose();
        if (ownsDataRoot)
        {
            try { Directory.Delete(dataRoot, recursive: true); } catch { }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Grid.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Grid repository root was not found.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr handle, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr handle, ref NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private const int ShowMaximized = 3;
    private const int ShowRestored = 9;
    private const byte VirtualKeyEnter = 0x0D;
    private const byte VirtualKeyEscape = 0x1B;
    private const byte VirtualKeyTab = 0x09;
    private const byte VirtualKeyUp = 0x26;
    private const uint KeyEventKeyUp = 0x0002;
}
