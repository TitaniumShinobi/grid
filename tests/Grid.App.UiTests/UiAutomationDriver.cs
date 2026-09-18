using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Grid.App.UiTests;

internal sealed class UiAutomationDriver : IDisposable
{
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
    private AutomationElement root;

    private UiAutomationDriver(Process process, AutomationElement root, string captureRoot, string dataRoot)
    {
        this.process = process;
        this.root = root;
        this.captureRoot = captureRoot;
        this.dataRoot = dataRoot;
    }

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
            await Task.Delay(100);
            process.Refresh();
        }
        if (process.MainWindowHandle == IntPtr.Zero) throw new TimeoutException("Grid did not expose a main window.");
        var root = AutomationElement.FromHandle(process.MainWindowHandle);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var loading = root.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Any(element => element.Current.Name.Equals("Loading connected games", StringComparison.OrdinalIgnoreCase));
            if (!loading) break;
            await Task.Delay(100);
        }
        return new(process, root, captures, dataRoot);
    }

    public void VerifyProductionIsEmpty()
    {
        var names = DescendantNames();
        foreach (var fixture in FixtureNames)
        {
            var matches = names.Where(name => name.Equals(fixture, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert(matches.Length == 0,
                $"Production exposed fixture name '{fixture}' in: {string.Join(" | ", matches)}.");
        }
        Assert(names.Any(name => name.Equals("GRID Welcome", StringComparison.OrdinalIgnoreCase)), "First production launch did not expose Welcome.");
        Assert(names.Any(name => name.Equals("Find setups", StringComparison.OrdinalIgnoreCase)), "First production launch did not expose setup discovery.");
        Assert(names.Any(name => name.Equals("Activities", StringComparison.OrdinalIgnoreCase)), "Production shell did not expose Activities.");
        Assert(names.Any(name => name.Contains("Grid Assistant", StringComparison.OrdinalIgnoreCase)), "Production shell did not expose the AUTO sidebar toggle.");
        Assert(!names.Any(name => name.Contains("PRODUCTION", StringComparison.OrdinalIgnoreCase)),
            "Production exposed internal mode terminology.");
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
        Activate("Skip setup for now");
        Thread.Sleep(250);
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Welcome tab", StringComparison.OrdinalIgnoreCase)),
            "Leaving Welcome closed the Welcome tab implicitly.");
        Assert(names.Any(name => name.Equals("Home tab", StringComparison.OrdinalIgnoreCase)),
            "Opening Home did not create the singleton Home tab.");
        Assert(names.Any(name => name.Equals("Add game", StringComparison.OrdinalIgnoreCase)),
            "Skipping incomplete setup did not open normal Home.");
        Activate("Home");
        names = DescendantNames();
        Assert(names.Any(name => name.Equals("Home tab", StringComparison.OrdinalIgnoreCase)),
            "The Grid brand did not route to the singleton Home tab.");
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
        var names = DescendantNames();
        Assert(names.Any(name => name.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase)),
            "The isolated development harness did not expose a connected game card.");
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

    public void VerifyHomeAssistant()
    {
        NormalizeShell();
        EnsureAssistantOpen();
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
                names.Any(name => name.Equals("Game intake", StringComparison.Ordinal)) &&
                names.Any(name => name.Equals("Grid intake", StringComparison.Ordinal))) break;
            Thread.Sleep(100);
        } while (DateTime.UtcNow < intakeDeadline);
        Assert(names.Any(name => name.Equals("Grid data intake form", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Game intake", StringComparison.Ordinal)) &&
            names.Any(name => name.Equals("Grid intake", StringComparison.Ordinal)),
            "The visible intake form did not expose its form-local GAME and GRID controls.");
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
        EnsureAssistantOpen();
        VerifyNoPageLevelHorizontalScroll();
        Activate("Hide Grid Assistant");
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
        AutomationElement? hide = null;
        AutomationElement? show = null;
        var controlDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < controlDeadline)
        {
            root = AutomationElement.FromHandle(process.MainWindowHandle);
            hide = FindByName("Hide Grid Assistant");
            try
            {
                if (hide is not null && hide.Current.IsEnabled) return;
            }
            catch (ElementNotAvailableException) { }
            show = FindByName("Show Grid Assistant");
            try
            {
                if (show is not null && show.Current.IsEnabled) break;
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        Assert(show is not null,
            "The closed assistant shell did not expose an enabled Show Grid Assistant control.");

        Activate("Show Grid Assistant");

        // This helper owns only shell state. Content readiness is verified by the
        // specific chat test that needs it. That distinction matters while other
        // specialized center-workspace surfaces (for example full Console) are active.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            Thread.Sleep(100);
            hide = FindByName("Hide Grid Assistant");
            if (hide is not null && hide.Current.IsEnabled)
            {
                return;
            }
        }

        throw new InvalidOperationException(
            "Opening the assistant shell did not transition the shell to its expanded state.");
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

    private static bool Covers(System.Windows.Rect cover, System.Windows.Rect target)
    {
        if (cover.IsEmpty || target.IsEmpty) return false;
        const double tolerance = 1.0;
        return cover.Left <= target.Left + tolerance
            && cover.Top <= target.Top + tolerance
            && cover.Right >= target.Right - tolerance
            && cover.Bottom >= target.Bottom - tolerance;
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
        if (!process.HasExited)
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(3000)) process.Kill(entireProcessTree: true);
        }
        process.Dispose();
        try { Directory.Delete(dataRoot, recursive: true); } catch { }
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
    private static extern bool SetForegroundWindow(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    private const int ShowMaximized = 3;
    private const int ShowRestored = 9;
}
