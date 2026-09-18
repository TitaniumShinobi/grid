using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.UiTests;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var configuration = args.Contains("--debug", StringComparer.Ordinal) ? "Debug" : "Release";
        try
        {
            VerifyPurePolicies();
            VerifyFirstRunStore();
            VerifyOfflineAlertIndexStore();
            VerifyWorkspacePresentationStore();
            VerifySourceAcquisitionPreferencesStore();
            VerifyNexusSourceRoutingPolicy();
            VerifyWorkspaceColumnContract();
            VerifyShellPanelContract();
            VerifyLegacyTaskHistoryCompatibility();
            VerifyRequestExecutionResponseContract();
            VerifyRequestExecutionFailureContract();
            VerifyTaskboardProjection();
            VerifyInvestigationSnapshotContract();
            VerifyInvestigationActionMarkup();
            await VerifyHistoryStoreAsync();
            if (args.Contains("--contract-only", StringComparer.Ordinal))
            {
                Console.WriteLine("Grid desktop contract checks passed without launching the application.");
                return 0;
            }
            var executable = UiAutomationDriver.ResolveExecutable(configuration);
            Console.WriteLine($"Grid UI executable: {executable}");
            using (var driver = await UiAutomationDriver.StartAsync(executable, demo: false))
            {
                driver.VerifyProductionIsEmpty();
                driver.VerifyEditorShell();
                driver.VerifyFirstRunWelcomeAndTabs();
                driver.VerifyActivityTaskboard();
                driver.VerifyPanelToggles();
                driver.VerifyShellCommandSeparation();
                driver.VerifyHomeAssistant();
                driver.VerifyGlobalNavigation();
                driver.VerifyResponsiveMatrix();
                driver.VerifyWindowStateTransitions();
            }

            using (var demo = await UiAutomationDriver.StartAsync(executable, demo: true))
            {
                demo.VerifyDemoIsExplicit();
                demo.VerifyEditorShell();
                demo.VerifyPanelToggles();
                demo.VerifyDemoWorkspaceAndAssistant();
                demo.VerifyResponsiveMatrix();
                demo.VerifyResponsiveMatrixWithAssistant();
            }
            Console.WriteLine("Grid UI checks passed: production empty, New Investigation intake, Demo isolation, and responsive bounds verified.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyLegacyTaskHistoryCompatibility()
    {
        const string json = """
            {
              "taskId": "legacy-empty-repair",
              "createdAt": "2026-09-01T00:00:00Z",
              "classId": "grid.class.installation-integrity",
              "terminalState": "RepairPlanned",
              "rawPrompt": "Apply a sealed repair.",
              "result": {
                "affectedMods": {},
                "modRoles": [],
                "finding": "A sealed repair is available.",
                "solution": "Review the repair.",
                "evidenceToolIds": []
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var document = JsonDocument.Parse(json);
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var parseTask = serviceType.GetMethod("ParseTask", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Task-history parser was not found.");
        try
        {
            _ = parseTask.Invoke(null, [document.RootElement]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException("Legacy empty repair history must remain loadable.", exception.InnerException);
        }

        const string capabilityJson = """
            {
              "taskId": "capability-assessment",
              "createdAt": "2026-09-11T16:00:00Z",
              "classId": "grid.class.outfits-bodies-physics",
              "terminalState": "EvidenceComplete",
              "result": {
                "affectedMods": [],
                "modRoles": [],
                "finding": "No installed mod in the captured profile satisfies this capability.",
                "solution": "Review the compatible provider candidate.",
                "evidenceToolIds": [],
                "capabilityAssessment": {
                  "schemaVersion": 1,
                  "capabilityId": "grid.capability.equipment.multiple-rings",
                  "displayName": "Wear rings on multiple fingers",
                  "installedStatus": "Absent",
                  "installedProviders": [],
                  "discoveryStatus": "Current",
                  "observedAtUtc": "2026-09-11T16:00:00Z",
                  "communityCandidates": [{
                    "name": "Fixture Multi-Ring Provider",
                    "provider": "Nexus",
                    "uri": "https://www.nexusmods.com/skyrimspecialedition/mods/12345",
                    "version": "2.0.0",
                    "compatibilityStatus": "Compatible",
                    "compatibilityDetail": "The fixture profile satisfies its declared requirements.",
                    "requiredPatches": [],
                    "evidenceIds": ["provider-evidence.fixture"]
                  }],
                  "evidenceIds": ["profile-evidence.fixture", "provider-evidence.fixture"]
                }
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var capabilityDocument = JsonDocument.Parse(capabilityJson);
        var parsed = (AssistantTaskRecord?)parseTask.Invoke(null, [capabilityDocument.RootElement]);
        if (parsed?.Finding?.CapabilityAssessment is not
            {
                InstalledStatus: AssistantInstalledCapabilityStatus.Absent,
                DiscoveryStatus: AssistantProviderDiscoveryStatus.Current,
                CommunityCandidates.Length: 1,
            } assessment ||
            assessment.CommunityCandidates[0].CompatibilityStatus != AssistantCandidateCompatibilityStatus.Compatible)
        {
            throw new InvalidOperationException("Capability assessment history did not preserve installed coverage, discovery freshness, and compatibility.");
        }

        const string detailedTaskJson = """
            {
              "taskId": "detailed-intake",
              "createdAt": "2026-09-11T16:00:00Z",
              "classId": "grid.class.installation-integrity",
              "terminalState": "EvidenceComplete",
              "caseId": "case-detailed-intake",
              "rawPrompt": "Audit the complete profile.",
              "intake": {
                "expectedBehavior": "All enabled components are complete and compatible.",
                "reproductionLocation": "The selected installation and profile.",
                "desiredOutcome": "Produce an evidence-backed repair plan."
              },
              "result": {
                "affectedMods": [],
                "modRoles": [],
                "finding": "Fixture finding.",
                "solution": "Fixture solution.",
                "evidenceToolIds": []
              },
              "toolReceipts": [],
              "resumable": false
            }
            """;
        using var detailedTaskDocument = JsonDocument.Parse(detailedTaskJson);
        var detailedTask = (AssistantTaskRecord?)parseTask.Invoke(null, [detailedTaskDocument.RootElement]);
        var detailedClaim = detailedTask?.Transcript.Single(entry => entry.Kind == AssistantTranscriptKind.UserClaim).Text;
        if (detailedClaim is null ||
            !detailedClaim.Contains("Expected behavior: All enabled components are complete and compatible.", StringComparison.Ordinal) ||
            !detailedClaim.Contains("Reproduction or location: The selected installation and profile.", StringComparison.Ordinal) ||
            !detailedClaim.Contains("Desired outcome: Produce an evidence-backed repair plan.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Persisted task history did not render every structured investigation instruction.");
        }
    }

    private static void VerifyRequestExecutionResponseContract()
    {
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var parseHistory = serviceType.GetMethod("ParseTaskHistoryResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Task-history response parser was not found.");
        using var emptyHistoryDocument = JsonDocument.Parse("""{ "tasks": [] }""");
        var emptyHistory = (System.Collections.IEnumerable?)parseHistory.Invoke(null, [emptyHistoryDocument.RootElement])
            ?? throw new InvalidOperationException("The task-history parser returned no collection.");
        if (emptyHistory.Cast<object>().Any())
            throw new InvalidOperationException("An explicit empty task-history array did not remain empty.");
        foreach (var invalidHistory in new[] { "{}", "{ \"tasks\": {} }" })
        {
            using var invalidHistoryDocument = JsonDocument.Parse(invalidHistory);
            try
            {
                _ = parseHistory.Invoke(null, [invalidHistoryDocument.RootElement]);
                throw new InvalidOperationException("An invalid history response was silently treated as a normal empty task list.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
            }
        }
        var parseResponse = serviceType.GetMethod("ParseExecuteResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Authorized-execution response parser was not found.");

        const string result = """
            {
              "affectedMods": [],
              "modRoles": [],
              "finding": "Fixture evidence was collected.",
              "solution": "Review the sealed fixture evidence.",
              "evidenceToolIds": [],
              "evidenceIds": [],
              "repairState": {
                "specificationAvailable": false,
                "applyEnabled": false,
                "rollbackAvailable": false
              },
              "mutationAuthorized": false,
              "terminalState": "EvidenceComplete"
            }
            """;
        using var normalDocument = JsonDocument.Parse($$"""
            {
              "tasks": [],
              "execution": {
                "CaseId": "case-normal",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        var normal = Invoke(normalDocument.RootElement, "submission-normal");
        if (normal.TaskId != "submission-normal" || normal.CaseId != "case-normal" || normal.TerminalState != "EvidenceComplete")
            throw new InvalidOperationException("A normal authorized execution with tasks: [] was not parsed as its execution result.");

        using var omittedTasksDocument = JsonDocument.Parse($$"""
            {
              "execution": {
                "CaseId": "case-no-tasks-property",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        if (Invoke(omittedTasksDocument.RootElement, "submission-no-tasks").CaseId != "case-no-tasks-property")
            throw new InvalidOperationException("A normal authorized execution without a tasks property was not parsed.");

        using var recoveredDocument = JsonDocument.Parse($$"""
            {
              "tasks": [{
                "taskId": "case-recovered",
                "createdAt": "2026-09-11T16:00:00Z",
                "classId": "grid.class.installation-integrity",
                "terminalState": "EvidenceComplete",
                "caseId": "case-recovered",
                "rawPrompt": "Recover this sealed task.",
                "result": {{result}},
                "toolReceipts": [],
                "repairState": {
                  "specificationAvailable": false,
                  "applyEnabled": false,
                  "rollbackAvailable": false
                },
                "resumable": false
              }],
              "execution": null
            }
            """);
        var recovered = Invoke(recoveredDocument.RootElement, "ignored-for-recovered-task");
        if (recovered.TaskId != "case-recovered" || recovered.CaseId != "case-recovered")
            throw new InvalidOperationException("A one-task recovery response did not preserve its sealed task identity.");

        using var multipleTasksDocument = JsonDocument.Parse($$"""
            {
              "tasks": [
                { "taskId": "one" },
                { "taskId": "two" }
              ],
              "execution": null
            }
            """);
        AssertInvalid(multipleTasksDocument.RootElement, "Multiple returned tasks must fail closed.");

        using var invalidTasksDocument = JsonDocument.Parse($$"""
            {
              "tasks": {},
              "execution": {
                "CaseId": "must-not-fall-through",
                "Result": {{result}},
                "ToolRun": { "toolReceipts": [] }
              }
            }
            """);
        AssertInvalid(invalidTasksDocument.RootElement, "A non-array tasks property must fail closed.");

        var resolveDirectAction = serviceType.GetMethod("ResolveDirectActionOperation", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Direct-action routing policy was not found.");
        if (!string.Equals((string?)resolveDirectAction.Invoke(null, [AssistantCaseAction.Diagnose]), "Diagnose", StringComparison.Ordinal))
            throw new InvalidOperationException("Diagnose must retain its deterministic direct-action route.");
        foreach (var authorizedAction in new[]
                 {
                     AssistantCaseAction.AttachEvidence,
                     AssistantCaseAction.CaptureCurrentState,
                     AssistantCaseAction.RefreshRecoverySources,
                     AssistantCaseAction.ApplyRepair,
                     AssistantCaseAction.RollBack,
                 })
        {
            try
            {
                _ = resolveDirectAction.Invoke(null, [authorizedAction]);
                throw new InvalidOperationException($"{authorizedAction} bypassed preparation and explicit authorization.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
            {
            }
        }

        var parsePreparedDraft = serviceType.GetMethod("ParsePreparedDraft", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Prepared successor parser was not found.");
        using var envelopeDocument = JsonDocument.Parse("""
            {
              "context": {
                "gameId": "skyrimspecialedition",
                "installationId": "installation.fixture",
                "profileId": "profile.fixture"
              },
              "class": {
                "classId": "grid.class.asset-mismatch",
                "recipeVersion": "1.0.0"
              },
              "selections": {
                "mods": [],
                "tools": [],
                "capabilities": []
              },
              "claims": { "text": "Inspect attached evidence." }
            }
            """);
        using var intakeDocument = JsonDocument.Parse("""
            {
              "expectedBehavior": "Evidence remains bound to the successor.",
              "reproductionLocation": "Fixture profile.",
              "desiredOutcome": "Preserve the selected attachment.",
              "attachments": [{
                "path": "C:\\fixture\\evidence.png",
                "mediaType": "image/png"
              }]
            }
            """);
        AssistantRequestDraft successorDraft;
        try
        {
            successorDraft = (AssistantRequestDraft?)parsePreparedDraft.Invoke(
                null,
                [envelopeDocument.RootElement, intakeDocument.RootElement, AssistantCaseAction.CaptureCurrentState])
                ?? throw new InvalidOperationException("The prepared successor parser returned no draft.");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
        if (successorDraft.Attachments is not [{ OriginalName: "evidence.png", MediaType: "image/png" }])
            throw new InvalidOperationException("A reopened Attach Evidence successor did not preserve its authorized attachment in session state.");
        if (!string.Equals(successorDraft.DisplayTitle, "Capture current state", StringComparison.Ordinal))
            throw new InvalidOperationException("A reopened Capture Current State successor did not preserve its action-specific title.");

        AssistantExecutionResult Invoke(JsonElement response, string taskId)
        {
            try
            {
                return (AssistantExecutionResult?)parseResponse.Invoke(null, [taskId, response])
                    ?? throw new InvalidOperationException("The execution response parser returned no result.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }
        }

        void AssertInvalid(JsonElement response, string message)
        {
            try
            {
                _ = Invoke(response, "invalid-response");
            }
            catch (InvalidDataException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }
    }

    private static void VerifyRequestExecutionFailureContract()
    {
        var serviceType = typeof(LocalUserHistoryStore).Assembly.GetType(
            "Grid.App.Services.PowerShellAssistantRequestExecutionService",
            throwOnError: true)!;
        var sanitizeError = serviceType.GetMethod("SanitizeError", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("The request-bridge failure sanitizer was not found.");

        const string bridgeOutput = """
            {
              "errorCode": "RequestSubmissionFailed",
              "message": "The property 'proposalId' cannot be found on this object."
            }
            """;
        var visible = (string?)sanitizeError.Invoke(null, [bridgeOutput, string.Empty]);
        if (visible is null ||
            !visible.Contains("RequestSubmissionFailed", StringComparison.Ordinal) ||
            !visible.Contains("proposalId", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A structured bridge failure discarded its bounded diagnostic message.");
        }

        const string secretOutput = """
            {
              "errorCode": "RequestSubmissionFailed",
              "message": "authorizationSecret=never-show-this"
            }
            """;
        var redacted = (string?)sanitizeError.Invoke(null, [secretOutput, string.Empty]);
        if (redacted is null || redacted.Contains("never-show-this", StringComparison.Ordinal) ||
            !redacted.Contains("[REDACTED]", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The bridge failure sanitizer did not redact a credential-like value.");
        }
    }

    private static void VerifyWorkspacePresentationStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"grid-presentation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "presentation.v1.json");
            var store = new LocalWorkspacePresentationStore(path);
            var expected = new WorkspacePresentationState(
                "above",
                "skyrim",
                EnvironmentTabCapability.Plugins.ToString(),
                ["separator.weather", "separator.interface"],
                612,
                [76, 56, 72, 72, 86],
                [76, 62, 76, 0, 0],
                3);
            if (!store.Save("profile.one", expected) || store.Load("profile.one") is not { } observed ||
                observed.ModSearch != expected.ModSearch ||
                observed.EnvironmentSearch != expected.EnvironmentSearch ||
                observed.LeftPaneWidth != expected.LeftPaneWidth ||
                observed.ModColumnLayoutVersion != expected.ModColumnLayoutVersion ||
                !observed.CollapsedSeparatorIds.SequenceEqual(expected.CollapsedSeparatorIds) ||
                !observed.ModColumnWidths.SequenceEqual(expected.ModColumnWidths) ||
                !observed.EnvironmentColumnWidths.SequenceEqual(expected.EnvironmentColumnWidths))
            {
                throw new InvalidOperationException("Workspace presentation state did not round-trip exactly.");
            }

            if (store.Load("profile.two") is not null)
            {
                throw new InvalidOperationException("Workspace presentation state leaked between profiles.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifySourceAcquisitionPreferencesStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "grid-source-acquisition-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "source-acquisition.v1.json");
            var store = new LocalSourceAcquisitionPreferencesStore(path);
            if (store.Load().AutomaticallyDownloadVerifiedSources)
                throw new InvalidOperationException("Automatic source downloads must default off.");
            if (!store.Save(new SourceAcquisitionPreferences(true)) || !store.Load().AutomaticallyDownloadVerifiedSources)
                throw new InvalidOperationException("Automatic source download preference did not round-trip.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyNexusSourceRoutingPolicy()
    {
        if (!NexusRecoverySourceDownloader.TryParseOfficialFilesUri(
                "https://www.nexusmods.com/skyrimspecialedition/mods/29194?tab=files", out var game, out var modId) ||
            game != "skyrimspecialedition" || modId != 29194)
            throw new InvalidOperationException("An exact official Nexus Files route was not accepted.");
        foreach (var refused in new[]
        {
            "http://www.nexusmods.com/skyrimspecialedition/mods/29194?tab=files",
            "https://evil.example/skyrimspecialedition/mods/29194?tab=files",
            "https://www.nexusmods.com/skyrimspecialedition/mods/not-a-number?tab=files",
            "https://www.nexusmods.com/skyrimspecialedition/mods/29194/files/12",
        })
        {
            if (NexusRecoverySourceDownloader.TryParseOfficialFilesUri(refused, out _, out _))
                throw new InvalidOperationException($"An unsafe or inexact Nexus route was accepted: {refused}");
        }
    }

    private static void VerifyWorkspaceColumnContract()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml.cs"));
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml"));
        const string pluginHeaders =
            "EnvironmentTabCapability.Plugins => (\"NAME\", \"FLAGS\", \"PRIORITY\", \"MOD INDEX\", \"\")";
        if (!source.Contains(pluginHeaders, StringComparison.Ordinal) ||
            !source.Contains("DefaultModColumnWidths = [76, 56, 72, 72, 86]", StringComparison.Ordinal) ||
            !source.Contains("CenterColumn4: true", StringComparison.Ordinal) ||
            !source.Contains("Header4Alignment = Capability == EnvironmentTabCapability.Plugins", StringComparison.Ordinal) ||
            !xaml.Contains("<ColumnDefinition Width=\"30\" />", StringComparison.Ordinal) ||
            !xaml.Contains("<ScaleTransform ScaleX=\"0.75\" ScaleY=\"0.75\" />", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ModFlagsHeaderColumn\" Width=\"56\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ModPriorityHeaderColumn\" Width=\"72\"", StringComparison.Ordinal) ||
            xaml.Contains("Margin=\"{Binding NameMargin}\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"PRIORITY\" TextAlignment=\"Center\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"CATEGORY\"", StringComparison.Ordinal) ||
            !xaml.Contains("Text=\"{Binding Header4}\" TextAlignment=\"{Binding Header4Alignment}\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The workstation tables must preserve their checkbox inset, mod-column order, readable Flags width, and centered plugin Mod Index contract.");
        }
    }

    private static void VerifyShellPanelContract()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "MainWindow.xaml.cs"));
        var resources = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "App.xaml"));
        var workspace = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml"));
        var workspaceSource = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "GameWorkspacePage.xaml.cs"));
        var selectionPresentationStart = workspaceSource.IndexOf("private void UpdateSelectionPresentation()", StringComparison.Ordinal);
        var resolvedEnvironmentRefreshStart = workspaceSource.IndexOf("private async Task RefreshResolvedEnvironmentTabsAsync()", StringComparison.Ordinal);
        foreach (var required in new[]
        {
            "x:Name=\"MainMenuBar\"",
            "x:Name=\"CompactMenuButton\"",
              "x:Name=\"GlobalSearchPanel\" Width=\"300\" Height=\"24\"",
              "x:Name=\"GlobalSearchBox\" MinHeight=\"0\" Padding=\"6,0\"",
            "x:Name=\"OpenInButton\"",
            "x:Name=\"OpenInLabel\"",
              "x:Name=\"LeftSidebar\" Grid.Column=\"2\" Margin=\"0,3.25\"",
              "x:Name=\"MainWorkspacePanel\" Grid.Column=\"4\" Margin=\"0,3.25\"",
              "x:Name=\"MainContentPanelOutline\" Grid.Row=\"0\" Grid.RowSpan=\"3\"",
              "x:Name=\"AssistantPanelShell\" Grid.Column=\"6\" AutomationProperties.Name=\"Grid Assistant panel\" Margin=\"0,3.25\"",
              "x:Name=\"BottomPanel\" Grid.Row=\"4\" AutomationProperties.Name=\"Bottom tool panel\"",
              "x:Name=\"EditorMoreActionsButton\"",
              "x:Name=\"ContextBar\" Grid.Row=\"1\" Padding=\"{StaticResource ShellPanelContentInset}\" HorizontalAlignment=\"Stretch\"",
              "x:Name=\"LeftSplitter\" Grid.Column=\"3\" Style=\"{StaticResource VerticalPanelSplitterStyle}\"",
              "x:Name=\"BottomSplitter\" Grid.Row=\"3\" Style=\"{StaticResource HorizontalPanelSplitterStyle}\"",
              "x:Name=\"AssistantSplitter\" Grid.Column=\"5\" Style=\"{StaticResource VerticalPanelSplitterStyle}\"",
              "AutomationProperties.Name=\"Application menu\"",
            "AutomationProperties.Name=\"Toggle left sidebar\"",
            "AutomationProperties.Name=\"Toggle bottom panel\"",
            "ToolTipService.ToolTip=\"Toggle right panel\"",
            "x:Name=\"LeftPanelToggleFill\"",
            "x:Name=\"BottomPanelToggleFill\"",
            "x:Name=\"AssistantToggleFill\"",
            "Grid.Column=\"3\" Orientation=\"Horizontal\" Margin=\"0,0,4,0\"",
            "Grid.Column=\"1\" Grid.ColumnSpan=\"4\" Orientation=\"Horizontal\" HorizontalAlignment=\"Left\"",
        })
        {
            if (!xaml.Contains(required, StringComparison.Ordinal))
                throw new InvalidOperationException($"The canonical shell panel contract is missing '{required}'.");
        }
        if (!source.Contains("e.NewSize.Width <= e.NewSize.Height", StringComparison.Ordinal) ||
            !source.Contains("OpenInLabel.Visibility = compactMenu ? Visibility.Collapsed : Visibility.Visible", StringComparison.Ordinal) ||
              !source.Contains("GlobalSearchPanel.Width = e.NewSize.Width < 820 ? 220 : compactMenu ? 280 : 300", StringComparison.Ordinal) ||
            !source.Contains("AssistantPresentationMode.Solo", StringComparison.Ordinal) ||
            source.Contains("band != ResponsiveLayoutBand.Narrow", StringComparison.Ordinal) ||
            source.Contains("AssistantDrawerLayer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The shell must use a square-width application menu and complete docked-or-solo workbench panels without a drawer.");
        }
        if (!resources.Contains("x:Key=\"ShellTabViewItemStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Margin\" Value=\"0,3,3,3\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"TabViewHeaderPadding\">0</Thickness>", StringComparison.Ordinal) ||
            !resources.Contains("x:Name=\"TabCard\"", StringComparison.Ordinal) ||
            !resources.Contains("Content=\"{TemplateBinding Header}\"", StringComparison.Ordinal) ||
            !resources.Contains("VerticalAlignment=\"Stretch\"", StringComparison.Ordinal) ||
            !resources.Contains("VerticalContentAlignment=\"Center\"", StringComparison.Ordinal) ||
            !resources.Contains("Margin=\"0,-2,0,2\"", StringComparison.Ordinal) ||
            !resources.Contains("Target=\"TabCard.Background\" Value=\"#2D2D30\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Height\" Value=\"24\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Padding\" Value=\"6,0\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellWorkbenchBrush\" Color=\"#111111\"", StringComparison.Ordinal) ||
            !resources.Contains("FontSize=\"13\"", StringComparison.Ordinal) ||
            !resources.Contains("BorderThickness=\"0\"", StringComparison.Ordinal) ||
            !xaml.Contains("Margin=\"-4,0,33,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("Style=\"{StaticResource ShellTabViewItemStyle}\"", StringComparison.Ordinal) ||
            !workspace.Contains("TabWidthMode=\"SizeToContent\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"ModWindow\"", StringComparison.Ordinal) ||
            !workspace.Contains("Click=\"OnModEnabledCheckBoxClicked\"", StringComparison.Ordinal) ||
            !workspace.Contains("<ListView.ItemContainerTransitions>", StringComparison.Ordinal) ||
            !workspace.Contains("<TransitionCollection />", StringComparison.Ordinal) ||
            workspace.Contains("IsChecked=\"{Binding IsEnabled}\" IsHitTestVisible=\"False\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentWindow\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentWindowOutline\"", StringComparison.Ordinal) ||
            !workspace.Contains("Margin=\"0,30,0,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("x:Name=\"EnvironmentTabHost\" Background=\"Transparent\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellPanelContentInset\">5</Thickness>", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableHeaderTextStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableSectionButtonStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableHeaderGridLineBrush\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellTableBodyGridLineBrush\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"TextAlignment\" Value=\"Left\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"VerticalAlignment\" Value=\"Center\"", StringComparison.Ordinal) ||
            !workspace.Contains("<Grid Padding=\"5,0,5,5\">", StringComparison.Ordinal) ||
            !workspace.Contains("Margin=\"-9,0,0,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("Margin=\"9,0,0,0\"", StringComparison.Ordinal) ||
            !workspace.Contains("Style=\"{StaticResource ShellTableSectionButtonStyle}\"", StringComparison.Ordinal) ||
            !workspace.Contains("Background=\"{ThemeResource ShellWorkbenchBrush}\"", StringComparison.Ordinal) ||
            !workspace.Contains("<Grid MinHeight=\"28\">", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding Priority}\" TextAlignment=\"{Binding PriorityAlignment}\"", StringComparison.Ordinal) ||
            !workspace.Contains("TextAlignment=\"{Binding Column3Alignment}\"", StringComparison.Ordinal) ||
            !workspaceSource.Contains("private const string NullCellValue = \"—\";", StringComparison.Ordinal) ||
            !workspaceSource.Contains("return TextAlignment.Center;", StringComparison.Ordinal) ||
            !workspaceSource.Contains("CenterColumn2 ? TextAlignment.Center", StringComparison.Ordinal) ||
            !workspaceSource.Contains("preserveWorkstationPosition: true", StringComparison.Ordinal) ||
            !workspaceSource.Contains("CaptureScrollPosition(ModsList)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("RestoreScrollPosition(ModsList, modScrollPosition)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ObservableCollection<ModRowViewModel> _modRows", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ReconcileModRows(rows)", StringComparison.Ordinal) ||
            !workspaceSource.Contains("SynchronizePresentation(ModRowViewModel source)", StringComparison.Ordinal) ||
            selectionPresentationStart < 0 ||
            resolvedEnvironmentRefreshStart <= selectionPresentationStart ||
            workspaceSource[selectionPresentationStart..resolvedEnvironmentRefreshStart].Contains("RefreshEnvironmentTabs();", StringComparison.Ordinal) ||
            !source.Contains("UpdateLayoutToggleGlyphs()", StringComparison.Ordinal) ||
            !source.Contains("Application.Current.Resources[\"ShellSuccessBrush\"]", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding FlagsLabel}\" TextAlignment=\"Center\"", StringComparison.Ordinal) ||
            !workspace.Contains("Text=\"{Binding Header2}\" TextAlignment=\"{Binding Header2Alignment}\"", StringComparison.Ordinal) ||
            !workspaceSource.Contains("ResolveCellAlignment", StringComparison.Ordinal) ||
            !source.Contains("MainWorkspacePanel.SizeChanged += OnMainWorkspacePanelSizeChanged", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"ContextBar\"", StringComparison.Ordinal) ||
            !xaml.Contains("BorderThickness=\"0\" Visibility=\"Collapsed\"", StringComparison.Ordinal) ||
            !resources.Contains("x:Key=\"ShellSearchTextBoxStyle\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"MinHeight\" Value=\"0\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"Padding\" Value=\"6,0\"", StringComparison.Ordinal) ||
            !resources.Contains("Property=\"VerticalContentAlignment\" Value=\"Center\"", StringComparison.Ordinal) ||
            !xaml.Contains("Background=\"{StaticResource ShellSurfaceBrush}\"", StringComparison.Ordinal) ||
            !xaml.Contains("TextBoxStyle=\"{StaticResource ShellSearchTextBoxStyle}\"", StringComparison.Ordinal) ||
            xaml.Contains("QueryIcon=\"Find\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"LaunchTargetFlyout\" Placement=\"BottomEdgeAlignedLeft\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"RefreshEnvironmentButton\"", StringComparison.Ordinal) ||
            !xaml.Contains("x:Name=\"LaunchTargetSelector\" Grid.Column=\"1\"", StringComparison.Ordinal) ||
            !source.Contains("await workspace.RefreshEnvironmentAsync();", StringComparison.Ordinal) ||
            !workspaceSource.Contains("public async Task RefreshEnvironmentAsync()", StringComparison.Ordinal) ||
            !workspace.Contains("Height=\"24\" HorizontalAlignment=\"Right\"", StringComparison.Ordinal) ||
            !workspace.Contains("PlaceholderText=\"Search mods\"", StringComparison.Ordinal) ||
            !workspace.Contains("PlaceholderText=\"Search plugins\"", StringComparison.Ordinal) ||
            !workspace.Contains("<AutoSuggestBox x:Name=\"ModSearchBox\"", StringComparison.Ordinal) ||
            !workspace.Contains("<AutoSuggestBox x:Name=\"EnvironmentSearchBox\"", StringComparison.Ordinal) ||
            workspace.Split("TextBoxStyle=\"{StaticResource ShellSearchTextBoxStyle}\"", StringSplitOptions.None).Length < 3 ||
            !workspaceSource.Contains("EnvironmentPane.Margin = new Thickness(0);", StringComparison.Ordinal) ||
            !source.Contains("ToolTargetPresentation.ForManagement()", StringComparison.Ordinal) ||
            !source.Contains("DispatcherQueue.TryEnqueue(() =>", StringComparison.Ordinal) ||
            source.Contains("ManageGameButton", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shell tabs must float on the canonical 3 DIP inset, and Manage must be the first launch-selector action.");
        }
    }

    private static void VerifyInvestigationActionMarkup()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml"));
        var source = File.ReadAllText(Path.Combine(root, "src", "Grid.App", "Views", "AssistantPanel.xaml.cs"));
        foreach (var chatContract in new[]
        {
            "AutomationProperties.Name=\"Grid message composer\"",
            "CornerRadius=\"20\"",
            "PlaceholderText=\"Message Grid\"",
            "AutomationProperties.Name=\"Conversation messages\"",
            "Header=\"Details and evidence\"",
        })
        {
            if (!xaml.Contains(chatContract, StringComparison.Ordinal))
                throw new InvalidOperationException($"Grid chat markup is missing its Codex-style contract: {chatContract}.");
        }
        if (!source.Contains("CreateTranscriptMessage", StringComparison.Ordinal) ||
            !source.Contains("HorizontalAlignment = HorizontalAlignment.Right", StringComparison.Ordinal) ||
            !source.Contains("entry.Kind == AssistantTranscriptKind.Progress", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Grid chat must render right-aligned user bubbles and reduce repeated progress receipts to one live message.");
        }
        if (!xaml.Contains("x:Name=\"CapabilitySelector\"", StringComparison.Ordinal) ||
            !xaml.Contains("SelectionChanged=\"OnCapabilitySelectionChanged\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "New Investigation must expose an explicitly wired gameplay-capability selector.");
        }
        var actions = new (string Name, string Handler)[]
        {
            ("Capture Current State", "OnCaptureCurrentStateClicked"),
            ("Continue live profile repair", "OnRefreshRecoverySourcesClicked"),
            ("Diagnose", "OnDiagnoseClicked"),
            ("Attach Evidence", "OnAttachEvidenceClicked"),
            ("Review Evidence", "OnReviewEvidenceClicked"),
            ("Review Repair", "OnReviewRepairClicked"),
            ("Copy complete recovery action list", "OnCopyRecoveryActionManifestClicked"),
            ("Download available exact recovery sources", "OnDownloadAvailableSourcesClicked"),
            ("Apply Repair", "OnApplyRepairClicked"),
            ("Roll Back", "OnRollBackClicked"),
        };
        foreach (var action in actions)
        {
            if (!xaml.Contains($"AutomationProperties.Name=\"{action.Name}\"", StringComparison.Ordinal) ||
                !xaml.Contains($"Click=\"{action.Handler}\"", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Investigation task markup does not expose a wired '{action.Name}' action.");
            }
        }

        foreach (var resultSurface in new[] { "Complete recovery human action manifest", "Archive candidate evidence", "Diagnostic result", "Capability Required result" })
        {
            if (!xaml.Contains($"AutomationProperties.Name=\"{resultSurface}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Investigation task markup does not expose the '{resultSurface}' surface.");
        }

        foreach (var label in new[]
        {
            "Affected mod(s):",
            "Mod role(s):",
            "Finding:",
            "Solution:",
            "Confidence:",
            "Evidence:",
        })
        {
            if (!xaml.Contains($"Text=\"{label}\"", StringComparison.Ordinal))
                throw new InvalidOperationException($"Diagnostic result markup does not expose the '{label}' field.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Grid.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the Grid repository root.");
    }

    private static void VerifyPurePolicies()
    {
        if (Grid.App.Composition.GridLaunchOptions.Parse(null) != Grid.App.Composition.GridApplicationMode.Production ||
            Grid.App.Composition.GridLaunchOptions.Parse("--demo", [], "demo") != Grid.App.Composition.GridApplicationMode.Demo ||
            Grid.App.Composition.GridLaunchOptions.Parse(string.Empty, ["--demo"], "demo") != Grid.App.Composition.GridApplicationMode.Demo ||
            Grid.App.Composition.GridLaunchOptions.Parse("--DEMO") != Grid.App.Composition.GridApplicationMode.Production)
        {
            throw new InvalidOperationException("Application mode parsing is not explicit and deterministic.");
        }

        double[] widths = [1920, 1536, 1280, 960, 1600, 1366];
        foreach (var width in widths)
        {
            foreach (var requested in new[] { 320d, 400d, 560d })
            {
                var decision = Grid.App.Services.ResponsiveLayoutPolicy.Evaluate(width - 64, true, requested);
                var reserved = decision.AssistantPresentation == Grid.App.Services.AssistantPresentationMode.Docked
                    ? decision.AssistantWidth + Grid.App.Services.ResponsiveLayoutPolicy.PaneDividerThickness : 0;
                if (decision.AssistantWidth < 0 || reserved > decision.AvailableWidth || decision.WorkspaceWidth < 0)
                {
                    throw new InvalidOperationException($"Responsive allocation exceeded the {width} DIP budget.");
                }
                var shouldDock = width - 64 >= Grid.App.Services.ResponsiveLayoutPolicy.AssistantDockThreshold;
                if (shouldDock != (decision.AssistantPresentation == Grid.App.Services.AssistantPresentationMode.Docked))
                {
                    throw new InvalidOperationException($"Assistant presentation did not match the {width} DIP budget.");
                }
            }
        }
    }



    private static void VerifyTaskboardProjection()
    {
        var context = ApplicationContextSnapshot.Home;
        var assistant = new AssistantSessionSnapshot(
            true, 360, AssistantOperatingMode.Ask, AssistantProviderAvailability.NotConfigured,
            AssistantLifecycleStage.Idle, context, "fixture", AssistantSurface.Home, false, false,
            new AssistantDraftSnapshot(AssistantIntakeScope.Grid, null, [], [], null, string.Empty, "New request", "grid.icon.unknown",
                AssistantDraftReadiness.Incomplete, "fixture", false),
            [], [], [], [], [
                new AssistantTaskSummary("queue", "Queued task", DateTimeOffset.UtcNow, "AwaitingApproval"),
                new AssistantTaskSummary("progress", "Running task", DateTimeOffset.UtcNow, "InProgress"),
                new AssistantTaskSummary("ready", "Ready task", DateTimeOffset.UtcNow, "EvidenceReady"),
                new AssistantTaskSummary("done", "Done task", DateTimeOffset.UtcNow, "Completed"),
                new AssistantTaskSummary("diagnosed", "Diagnosed task", DateTimeOffset.UtcNow, "Diagnosed")
            ], null, null, null);
        var board = Grid.Core.Application.TaskboardProjection.Create(assistant);
        if (board.QueueCount != 1 || board.InProgressCount != 1 || board.ReadyCount != 1 || board.LedgerCount != 2)
            throw new InvalidOperationException("Taskboard phase projection did not preserve the four-panel contract.");

        var historyTime = DateTimeOffset.UtcNow.AddMinutes(1);
        var activity = Grid.Core.Application.ActivityFeedProjection.CreateRecent(board,
        [
            new HistoryEntry(
                new HistoryEntryId("history.fixture"), historyTime, HistoryActor.User,
                HistoryEventKind.InstallationConnected, HistoryEventStatus.Succeeded,
                null, null, null, "Installation connected", "Fixture history detail.")
        ]);
        if (activity.Length != 6 || activity[0].Title != "Installation connected" ||
            activity.Count(item => item.TaskId is not null) != 5 ||
            activity.Single(item => item.TaskId == "done").Status != "Completed" ||
            activity.Single(item => item.TaskId == "diagnosed").Status != "Completed")
        {
            throw new InvalidOperationException("Recent Activity did not merge resumable investigations with ordinary history.");
        }
    }

    private static void VerifyInvestigationSnapshotContract()
    {
        var attachment = new AssistantAttachmentDraft(
            @"C:\fixture\evidence.png", "evidence.png", "image/png", 2048);
        var draft = new AssistantDraftSnapshot(
            AssistantIntakeScope.Game,
            new GameId("game.fixture"),
            [],
            [],
            "grid.class.world-objects",
            "The entrance is missing.",
            "Missing entrance",
            "grid.icon.world-object",
            AssistantDraftReadiness.ReadyForDeterministicCollection,
            "Ready to preserve and collect evidence.",
            true,
            InstallationId: new InstallationId("installation.fixture"),
            ProfileId: new ProfileId("profile.fixture"),
            Problem: "The entrance is missing.",
            ExpectedBehavior: "The entrance should be usable.",
            ReproductionOrLocation: "At the exterior hatch.",
            DesiredOutcome: "Identify the evidence-backed cause.",
            Attachments: [attachment]);

        var capabilityRequired = new AssistantCapabilityRequired(
            ["Runtime reference evidence was not collected."],
            ["Exact entrance reference identity"],
            ["grid.game.fixture.runtime-reference.collect"],
            [new AssistantCapabilityGapResolution(
                "gap.request.fixture", "grid.game.fixture.runtime-reference.collect",
                "CreationProposalRequired", "CreateCapabilityProposal", null, true,
                "Prepare a reusable CODE capability proposal.", new string('A', 64))]);
        var finding = new AssistantDeterministicFinding(
            [], [], "UNRESOLVED", "Collect the declared missing evidence.", [],
            CapabilityRequired: capabilityRequired);
        var archiveCandidate = new AssistantArchiveCandidate(
            "MysticismMagic.esp", "Mysticism", "Mysticism-2.4.2.zip", "ExactRestoration", "Complete",
            "NotRequiredForExactRestoration", [], "archive-candidate.1234567890abcdef12345678");
        var repair = new AssistantRepairAvailability(null, null, false, false, "No exact repair specification exists.",
            ArchiveCandidates: [archiveCandidate]);
        AssistantCaseActionState[] actions =
        [
            new(AssistantCaseAction.CaptureCurrentState, true, true, false, "Capture selected-profile context."),
            new(AssistantCaseAction.Diagnose, true, true, false, "Invoke registered deterministic coverage."),
            new(AssistantCaseAction.ApplyRepair, false, false, true, "No exact repair specification exists."),
            new(AssistantCaseAction.RollBack, false, false, true, "No verified rollback receipt exists."),
        ];

        if (draft.InstallationId?.Value != "installation.fixture" ||
            draft.ProfileId?.Value != "profile.fixture" ||
            draft.Attachments.Length != 1 ||
            draft.AuthorizationScope != AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly ||
            finding.CapabilityRequired?.MissingCapabilityIds.Length != 1 ||
            finding.CapabilityRequired?.Resolutions.Single().Route != "CreateCapabilityProposal" ||
            repair.HasExactSpecification || repair.HasVerifiedRollbackReceipt || repair.ArchiveCandidates.Length != 1 ||
            actions.Single(state => state.Action == AssistantCaseAction.ApplyRepair).IsEnabled ||
            actions.Single(state => state.Action == AssistantCaseAction.RollBack).IsVisible)
        {
            throw new InvalidOperationException("New Investigation snapshots did not preserve context, evidence, CapabilityRequired, or repair gating.");
        }
    }

    private static void VerifyFirstRunStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-first-run-check-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "first-run.v1.json");
        try
        {
            var store = new LocalFirstRunStateStore(path);
            if (store.IsComplete()) throw new InvalidOperationException("A missing first-run document was treated as complete.");
            if (!store.MarkComplete() || !store.IsComplete()) throw new InvalidOperationException("First-run completion did not persist atomically.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void VerifyOfflineAlertIndexStore()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-offline-alert-check-{Guid.NewGuid():N}");
        try
        {
            var store = new LocalOfflineAlertIndexStore(root);
            var context = new WorkspaceToolOutputContext(
                new GameId("game.fixture"),
                new InstallationId("installation.fixture"),
                new ProfileId("profile.fixture"),
                new InstallationReferenceId("reference.fixture"),
                "catalog.fixture",
                new string('A', 64),
                new string('B', 64));
            var expected = new OfflineAlertIndexBuilder().Build(context, null, null);
            store.Save(expected);
            var actual = store.Load(context.GameId, context.InstallationId, context.ProfileId);
            if (actual is null || actual.Fingerprint != expected.Fingerprint || actual.WorkspaceFingerprint != expected.WorkspaceFingerprint)
                throw new InvalidOperationException("The local offline alert index did not round-trip exact profile evidence.");

            store.Save(expected with { UpdatedAtUtc = expected.UpdatedAtUtc.AddMinutes(1) });
            if (Directory.GetFiles(root, "*.v1.json").Length != 1)
                throw new InvalidOperationException("The local offline alert index did not atomically replace its exact profile snapshot.");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyHistoryStoreAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"grid-history-check-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "history.v1.json");
        try
        {
            var store = new LocalUserHistoryStore(path);
            var entry = new HistoryEntry(
                new HistoryEntryId("history.ui.fixture"),
                DateTimeOffset.UtcNow,
                HistoryActor.User,
                HistoryEventKind.InstallationConnected,
                HistoryEventStatus.Succeeded,
                new GameId("game.fixture"),
                new InstallationId("installation.fixture"),
                null,
                "Installation connected",
                "Grid stored a reconnectable reference.");
            await store.AppendAsync(entry);
            var loaded = await store.LoadAsync();
            if (loaded.Issue is not null || loaded.Entries.Length != 1 || loaded.Entries[0].Id != entry.Id)
            {
                throw new InvalidOperationException("Versioned user History did not round-trip atomically.");
            }
            try
            {
                await store.AppendAsync(entry with { Id = new HistoryEntryId("history.path"), Detail = @"Private C:\fixture path" });
                throw new InvalidOperationException("History accepted a private absolute-path shape.");
            }
            catch (ArgumentException)
            {
            }
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
