using Grid.App.Services;
using Grid.Core.Application;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Grid.App.Views;

public sealed partial class AssistantPanel : UserControl
{
    private const int MaximumInlineDiagnosticItems = 12;
    private AssistantSessionState? state;
    private IEvidenceFilePicker? evidenceFilePicker;
    private LocalSourceAcquisitionPreferencesStore? sourceAcquisitionPreferencesStore;
    private NexusRecoverySourceDownloader? nexusRecoverySourceDownloader;
    private readonly HashSet<string> automaticAcquisitionAttempts = new(StringComparer.Ordinal);
    private string? sourceAcquisitionStatusOverride;
    private bool sourceAcquisitionRunning;
    private Action? layoutChanged;
    private CancellationToken lifetimeToken;
    private bool rendering;

    public AssistantPanel() => InitializeComponent();

    public void BindState(
        AssistantSessionState assistantState,
        IEvidenceFilePicker filePicker,
        Action onLayoutChanged,
        LocalSourceAcquisitionPreferencesStore? acquisitionPreferencesStore = null,
        NexusRecoverySourceDownloader? recoverySourceDownloader = null,
        CancellationToken cancellationToken = default)
    {
        state = assistantState ?? throw new ArgumentNullException(nameof(assistantState));
        evidenceFilePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        layoutChanged = onLayoutChanged ?? throw new ArgumentNullException(nameof(onLayoutChanged));
        sourceAcquisitionPreferencesStore = acquisitionPreferencesStore;
        nexusRecoverySourceDownloader = recoverySourceDownloader;
        lifetimeToken = cancellationToken;
        Refresh();
    }

    public void RefreshContext() => Refresh();
    public void FocusPrimaryAction() => NewChatButton.Focus(FocusState.Programmatic);

    public void BeginTaskDraft(string text)
    {
        if (state is null) return;
        state.StartNewInvestigation();
        state.ToggleForm();
        state.SetPlainText(text ?? string.Empty);
        Refresh();
        ComposerText.Focus(FocusState.Programmatic);
        ComposerText.Select(ComposerText.Text.Length, 0);
    }

    private void Refresh()
    {
        if (state is null || rendering) return;
        rendering = true;
        try
        {
            var snapshot = state.Snapshot();
            TaskTitleText.Text = snapshot.Surface switch
            {
                AssistantSurface.Home => "New Investigation",
                AssistantSurface.History => "History",
                _ => snapshot.ActiveTask?.Summary.Title ?? "Investigation",
            };

            HomeSurface.Visibility = snapshot.Surface == AssistantSurface.Home ? Visibility.Visible : Visibility.Collapsed;
            HistorySurface.Visibility = snapshot.Surface == AssistantSurface.History ? Visibility.Visible : Visibility.Collapsed;
            TaskSurface.Visibility = snapshot.Surface == AssistantSurface.Task ? Visibility.Visible : Visibility.Collapsed;
            SuggestionsSurface.Visibility = snapshot.Surface == AssistantSurface.Home ? Visibility.Visible : Visibility.Collapsed;
            ComposerSurface.Visibility = snapshot.Surface == AssistantSurface.History ? Visibility.Collapsed : Visibility.Visible;
            IntakeForm.Visibility = snapshot.IsFormVisible && snapshot.Surface == AssistantSurface.Home ? Visibility.Visible : Visibility.Collapsed;
            EmptyHome.Visibility = snapshot.IsFormVisible ? Visibility.Collapsed : Visibility.Visible;
            FormToggleButton.IsChecked = snapshot.IsFormVisible;
            FullScreenButton.IsChecked = snapshot.IsFullScreen;

            GameScopeButton.IsChecked = snapshot.Draft.Scope == AssistantIntakeScope.Game;
            GridScopeButton.IsChecked = snapshot.Draft.Scope == AssistantIntakeScope.Grid;
            GameFields.Visibility = snapshot.Draft.Scope == AssistantIntakeScope.Game ? Visibility.Visible : Visibility.Collapsed;
            GridFields.Visibility = snapshot.Draft.Scope == AssistantIntakeScope.Grid ? Visibility.Visible : Visibility.Collapsed;

            GameSelector.ItemsSource = snapshot.Games;
            GameSelector.SelectedItem = snapshot.Games.FirstOrDefault(game => game.Id == snapshot.Draft.GameId);
            InstallationSelector.ItemsSource = snapshot.Installations;
            InstallationSelector.SelectedItem = snapshot.Installations.FirstOrDefault(item => item.Id == snapshot.Draft.InstallationId);
            ProfileSelector.ItemsSource = snapshot.Profiles;
            ProfileSelector.SelectedItem = snapshot.Profiles.FirstOrDefault(item => item.Id == snapshot.Draft.ProfileId);
            ClassSelector.ItemsSource = snapshot.Classes;
            ClassSelector.SelectedItem = snapshot.Classes.FirstOrDefault(option => option.Id == snapshot.Draft.ClassId);
            var selectedClass = snapshot.Classes.FirstOrDefault(option => option.Id == snapshot.Draft.ClassId);
            var gameplayCapabilities = selectedClass?.GameplayCapabilities.IsDefault == false
                ? selectedClass.GameplayCapabilities
                : [];
            CapabilitySelector.ItemsSource = gameplayCapabilities;
            CapabilitySelector.SelectedItem = gameplayCapabilities.FirstOrDefault(option => option.Id == snapshot.Draft.CapabilityId);
            CapabilitySelector.Visibility = gameplayCapabilities.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
            ComposerText.Text = snapshot.Surface == AssistantSurface.Task ? string.Empty : snapshot.Draft.PlainText;
            ComposerText.PlaceholderText = snapshot.Surface == AssistantSurface.Task ? "Start a new request…" : "Message Grid";
            ProblemText.Text = snapshot.Draft.Problem;
            ExpectedBehaviorText.Text = snapshot.Draft.ExpectedBehavior;
            ReproductionLocationText.Text = snapshot.Draft.ReproductionOrLocation;
            DesiredOutcomeText.Text = snapshot.Draft.DesiredOutcome;
            AuthorizationScopeSelector.SelectedIndex = snapshot.Draft.AuthorizationScope == AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly ? 0 : -1;
            DraftAuthorizationScopeText.Text = snapshot.Draft.AuthorizationScope switch
            {
                AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly =>
                    "Read-only access to the selected installation and profile context, plus the files explicitly attached below. Exact paths are displayed before authorization.",
                _ => "Unsupported authorization scope.",
            };
            RequestTitleText.Text = snapshot.Draft.DisplayTitle;
            RequestPill.Visibility = snapshot.Surface == AssistantSurface.Home && !string.IsNullOrWhiteSpace(snapshot.Draft.ClassId)
                ? Visibility.Visible
                : Visibility.Collapsed;
            ClassIcon.Glyph = ResolveClassGlyph(snapshot.Draft.ClassIconId);
            var classBrush = ResolveClassBrush(snapshot.Draft.ClassIconId);
            ClassIcon.Foreground = classBrush;
            RequestTitleText.Foreground = classBrush;
            ReadinessText.Text = snapshot.Surface == AssistantSurface.Task
                ? "Work locally"
                : FormatReadiness(snapshot.Draft.Readiness);
            SendButton.IsEnabled = snapshot.Surface == AssistantSurface.Home && snapshot.Draft.CanSubmit;
            AutomationProperties.SetName(SendButton, SendButton.IsEnabled ? "Start Investigation" : "Start Investigation unavailable");
            LocationText.Text = FormatLocation(snapshot);

            PopulateMods(snapshot);
            PopulateTools(snapshot);
            PopulateDraftAttachments(snapshot);
            PopulateHistory(snapshot);
            PopulateTask(snapshot);
        }
        finally
        {
            rendering = false;
        }
    }

    private void PopulateHistory(AssistantSessionSnapshot snapshot)
    {
        HistoryTaskList.Children.Clear();
        HistoryEmptyText.Visibility = snapshot.Tasks.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var task in snapshot.Tasks.OrderByDescending(task => task.CreatedAtUtc))
        {
            var button = new Button
            {
                Tag = task.Id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = $"{task.Title}\n{task.Status} · {task.CreatedAtUtc.LocalDateTime:g}",
            };
            AutomationProperties.SetName(button, $"Open task {task.Title}");
            button.Click += OnHistoryTaskClicked;
            HistoryTaskList.Children.Add(button);
        }
    }

    private void PopulateTask(AssistantSessionSnapshot snapshot)
    {
        var authorization = snapshot.PendingAuthorization;
        AuthorizationReview.Visibility = authorization is null ? Visibility.Collapsed : Visibility.Visible;
        AuthorizationScopes.Children.Clear();
        if (authorization is not null)
        {
            AuthorizationTitle.Text = authorization.MutationAuthorized ? "Authorize exact reversible change" : "Authorize exact read scope";
            ApproveAuthorizationButton.Content = authorization.MutationAuthorized ? "Authorize change" : "Authorize reads";
            AuthorizationStatement.Text = authorization.Statement;
            foreach (var scope in authorization.ReadScopes)
            {
                AuthorizationScopes.Children.Add(new TextBlock
                {
                    Text = $"{scope.ProviderName} · {scope.Availability}\n{scope.ObservationMode ?? "No observation mode"}\n{(scope.ExactReadPaths.Length == 0 ? "No filesystem paths requested" : string.Join("\n", scope.ExactReadPaths))}",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        TranscriptEntries.Children.Clear();
        ReceiptEntries.Children.Clear();
        var task = snapshot.ActiveTask;
        TaskStatusText.Text = task is null ? string.Empty : $"{task.TerminalState}{(task.CaseId is null ? string.Empty : $" · Case {task.CaseId}")}";
        ResumeTaskButton.Visibility = task?.IsResumable == true ? Visibility.Visible : Visibility.Collapsed;
        ApplyActionStates(task);
        PopulateRecoveryActionManifest(task?.RepairAvailability?.HumanActionManifest);
        PopulateSourceAcquisition(task?.RepairAvailability);
        PopulateArchiveCandidates(task?.RepairAvailability);
        PopulateFinding(task?.Finding);
        if (task is not null)
        {
            var userClaims = task.Transcript.Where(entry => entry.Kind == AssistantTranscriptKind.UserClaim);
            foreach (var entry in userClaims) TranscriptEntries.Children.Add(CreateTranscriptMessage(entry));

            if (task.Finding is null)
            {
                var latest = task.Transcript.LastOrDefault(entry => entry.Kind is AssistantTranscriptKind.Failure or AssistantTranscriptKind.Result or AssistantTranscriptKind.Evidence)
                    ?? task.Transcript.LastOrDefault(entry => entry.Kind == AssistantTranscriptKind.Progress);
                if (latest is not null) TranscriptEntries.Children.Add(CreateTranscriptMessage(latest));
            }
            else
            {
                foreach (var failure in task.Transcript.Where(entry => entry.Kind == AssistantTranscriptKind.Failure).TakeLast(1))
                    TranscriptEntries.Children.Add(CreateTranscriptMessage(failure));
            }

            foreach (var receipt in task.Receipts)
            {
                ReceiptEntries.Children.Add(new TextBlock
                {
                    Text = $"{receipt.ProviderName}\n{receipt.Availability} · {receipt.Status} · {receipt.ReceiptSha256}\n{receipt.Reason}",
                    FontSize = 11,
                    Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                });
            }
        }
        ExecutionErrorText.Text = snapshot.ExecutionError ?? string.Empty;
        ExecutionErrorText.Visibility = string.IsNullOrWhiteSpace(snapshot.ExecutionError) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static FrameworkElement CreateTranscriptMessage(AssistantTranscriptEntry entry)
    {
        var isUser = entry.Kind == AssistantTranscriptKind.UserClaim;
        var text = isUser ? PresentUserClaim(entry.Text) : PresentAssistantMessage(entry.Text);
        var body = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var meta = new TextBlock
        {
            Text = isUser
                ? entry.TimestampUtc.LocalDateTime.ToString("t")
                : $"Grid · {entry.TimestampUtc.LocalDateTime:t}",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
        };
        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(body);
        stack.Children.Add(meta);

        if (!isUser) return stack;
        return new Border
        {
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 10, 14, 9),
            CornerRadius = new CornerRadius(18),
            Background = (Brush)Application.Current.Resources["ShellInputBrush"],
            Child = stack,
        };
    }

    private static string PresentUserClaim(string text)
    {
        var problem = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(line => line.StartsWith("Problem:", StringComparison.OrdinalIgnoreCase));
        return problem is null ? PresentAssistantMessage(text) : problem["Problem:".Length..].Trim();
    }

    private static string PresentAssistantMessage(string text)
    {
        const int maximumCharacters = 1800;
        var normalized = (text ?? string.Empty).Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : $"{normalized[..maximumCharacters].TrimEnd()}…\n\nOpen Details and evidence for the complete deterministic record.";
    }

    private void PopulateRecoveryActionManifest(AssistantRecoveryActionManifest? manifest)
    {
        RecoveryActionManifestPanel.Visibility = manifest is null ? Visibility.Collapsed : Visibility.Visible;
        if (manifest is null)
        {
            RecoveryActionManifestSummaryText.Text = string.Empty;
            RecoveryActionManifestTextBox.Text = string.Empty;
            return;
        }

        RecoveryActionManifestSummaryText.Text =
            "The selected live MO2 profile is the source of truth; no original modlist is required. " +
            $"{manifest.ExactArchiveAcquisitionCount:N0} source archives may supply missing bytes; " +
            $"{manifest.LineageEvidenceRequiredCount:N0} unresolved source identities; " +
            $"{manifest.AffectedModCount:N0} affected mods; {manifest.AffectedPluginCount:N0} affected plugin consumers. " +
            $"Proven updates: {manifest.ProvenUpdateRequiredCount:N0}; proven reinstalls: {manifest.ProvenReinstallationRequiredCount:N0}; " +
            $"planned patch changes: {manifest.PlannedPatchChangeCount:N0}; repair-ready components: {manifest.RepairReadyCount:N0}.";
        RecoveryActionManifestTextBox.Text = RenderRecoveryActionManifest(manifest);
        CopyRecoveryActionManifestButton.Content = "Copy complete action list";
    }

    private static string RenderRecoveryActionManifest(AssistantRecoveryActionManifest manifest)
    {
        static string JoinOrNone(IEnumerable<string> values) =>
            string.Join(", ", values.Where(value => !string.IsNullOrWhiteSpace(value))) is { Length: > 0 } text ? text : "None";
        var text = new StringBuilder();
        text.AppendLine("LIVE-PROFILE REPAIR MANIFEST");
        text.AppendLine("Source of truth: selected live MO2 profile (no original modlist required)");
        text.AppendLine($"Manifest: {manifest.ManifestId}");
        text.AppendLine($"Plan: {manifest.PlanId}");
        text.AppendLine($"Status: {manifest.Status}");
        text.AppendLine();
        text.AppendLine("CLASSIFICATION");
        text.AppendLine($"Exact source archives to acquire: {manifest.ExactArchiveAcquisitionCount:N0}");
        text.AppendLine($"Source identities Grid must still resolve: {manifest.LineageEvidenceRequiredCount:N0}");
        text.AppendLine($"Affected mods: {manifest.AffectedModCount:N0}");
        text.AppendLine($"Affected plugin consumers: {manifest.AffectedPluginCount:N0}");
        text.AppendLine($"Missing component files: {manifest.MissingDependencyCount:N0}");
        text.AppendLine($"Updates proven required: {manifest.ProvenUpdateRequiredCount:N0}");
        text.AppendLine($"Reinstalls proven required: {manifest.ProvenReinstallationRequiredCount:N0}");
        text.AppendLine($"Version-changing candidates requiring further proof: {manifest.VersionChangingCandidateCount:N0}");
        text.AppendLine($"Exact-restoration candidates: {manifest.ExactRestorationCandidateCount:N0}");
        text.AppendLine($"Patches Grid will currently create or change: {manifest.PlannedPatchChangeCount:N0}");
        text.AppendLine($"Repair-ready components: {manifest.RepairReadyCount:N0}");
        text.AppendLine($"Mutation authorized: {(manifest.MutationAuthorized ? "Yes" : "No")}");
        foreach (var classification in manifest.Classifications) text.AppendLine($"- {classification}");

        text.AppendLine();
        text.AppendLine($"HUMAN ACTIONS — EXACT ARCHIVE DOWNLOADS ({manifest.ManualAcquisitions.Length:N0})");
        if (manifest.ManualAcquisitions.IsDefaultOrEmpty) text.AppendLine("None.");
        for (var index = 0; index < manifest.ManualAcquisitions.Length; index++)
        {
            var action = manifest.ManualAcquisitions[index];
            text.AppendLine();
            text.AppendLine($"{index + 1}. {JoinOrNone(action.ModNames)}");
            text.AppendLine($"   Installed version claim(s): {JoinOrNone(action.InstalledVersionClaims)}");
            text.AppendLine($"   Expected archive: {action.ExpectedArchiveLeaf}");
            text.AppendLine($"   Official Files page: {action.OfficialFilesUri}");
            text.AppendLine($"   Affected plugin(s): {JoinOrNone(action.AffectedPlugins)}");
            text.AppendLine($"   Missing components represented: {action.MissingDependencyCount:N0}");
            text.AppendLine($"   Action: {action.Instruction}");
        }

        text.AppendLine();
        text.AppendLine($"BLOCKED SOURCE IDENTITIES — NOT DOWNLOAD REQUESTS ({manifest.LineageRequirements.Length:N0})");
        if (manifest.LineageRequirements.IsDefaultOrEmpty) text.AppendLine("None.");
        foreach (var requirement in manifest.LineageRequirements)
        {
            text.AppendLine();
            text.AppendLine($"- {requirement.PluginName}");
            text.AppendLine($"  Current override provider: {requirement.CurrentOverrideProvider}");
            text.AppendLine($"  Missing components: {requirement.MissingDependencyCount:N0}");
            text.AppendLine($"  Examples: {JoinOrNone(requirement.RequiredFileExamples)}" +
                (requirement.OmittedRequiredFileCount > 0 ? $" (+{requirement.OmittedRequiredFileCount:N0} more)" : string.Empty));
            text.AppendLine($"  Next evidence step: {requirement.NextAction}");
        }

        text.AppendLine();
        text.AppendLine($"VERSION-CHANGING CANDIDATES — NOT APPROVED UPDATES ({manifest.VersionChangingCandidates.Length:N0})");
        if (manifest.VersionChangingCandidates.IsDefaultOrEmpty) text.AppendLine("None.");
        foreach (var candidate in manifest.VersionChangingCandidates)
            text.AppendLine($"- {candidate.ProviderName} / {candidate.ArchiveLeaf} -> {candidate.PluginName} [{candidate.CompatibilityStatus}]");

        text.AppendLine();
        text.AppendLine($"EXACT-RESTORATION CANDIDATES ({manifest.ExactRestorationCandidates.Length:N0})");
        if (manifest.ExactRestorationCandidates.IsDefaultOrEmpty) text.AppendLine("None.");
        foreach (var candidate in manifest.ExactRestorationCandidates)
            text.AppendLine($"- {candidate.ProviderName} / {candidate.ArchiveLeaf} -> {candidate.PluginName} [{candidate.CompatibilityStatus}]");

        text.AppendLine();
        text.AppendLine($"PATCH OR PROFILE CHANGES PLANNED ({manifest.PlannedPatchChanges.Length:N0})");
        if (manifest.PlannedPatchChanges.IsDefaultOrEmpty) text.AppendLine("None. A separate exact repair specification is required before Grid may propose a mutation.");
        foreach (var change in manifest.PlannedPatchChanges) text.AppendLine($"- {change}");

        text.AppendLine();
        text.AppendLine("GRID FOLLOW-UP ACTIONS");
        foreach (var action in manifest.GridFollowUpActions) text.AppendLine($"- {action}");

        text.AppendLine();
        text.AppendLine($"ALL AFFECTED MODS ({manifest.AffectedMods.Length:N0})");
        if (manifest.AffectedMods.IsDefaultOrEmpty) text.AppendLine("None recorded.");
        foreach (var mod in manifest.AffectedMods) text.AppendLine($"- {mod}");

        text.AppendLine();
        text.AppendLine($"ALL AFFECTED PLUGIN CONSUMERS ({manifest.AffectedPlugins.Length:N0})");
        if (manifest.AffectedPlugins.IsDefaultOrEmpty) text.AppendLine("None recorded.");
        foreach (var plugin in manifest.AffectedPlugins) text.AppendLine($"- {plugin}");
        return text.ToString().TrimEnd();
    }

    private void OnCopyRecoveryActionManifestClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RecoveryActionManifestTextBox.Text)) return;
        var package = new DataPackage();
        package.SetText(RecoveryActionManifestTextBox.Text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
        CopyRecoveryActionManifestButton.Content = "Copied";
    }

    private void PopulateSourceAcquisition(AssistantRepairAvailability? repair)
    {
        SourceAcquisitionLinks.Children.Clear();
        var validUri = Uri.TryCreate(repair?.NextManualAcquisitionUri, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase);
        var validLeaf = !string.IsNullOrWhiteSpace(repair?.NextExpectedArchiveLeaf) &&
            string.Equals(Path.GetFileName(repair.NextExpectedArchiveLeaf), repair.NextExpectedArchiveLeaf, StringComparison.Ordinal);
        var visible = repair is { ManualAcquisitionReadyCount: > 0 } && validUri && validLeaf;
        SourceAcquisitionPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var automatic = sourceAcquisitionPreferencesStore?.Load().AutomaticallyDownloadVerifiedSources == true;
        SourceAcquisitionStatusText.Text = visible
            ? $"{repair!.ManualAcquisitionReadyCount} verified official Files route(s) remain. " +
              (automatic
                  ? "Automatic acquisition is on; sources unsupported by the connected account remain available below."
                  : "Automatic acquisition is off; use the direct links below.")
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(sourceAcquisitionStatusOverride))
            SourceAcquisitionStatusText.Text = sourceAcquisitionStatusOverride;
        ExpectedArchiveLeafText.Text = visible ? $"Expected archive: {repair!.NextExpectedArchiveLeaf}" : string.Empty;
        OpenNexusFilesButton.Tag = visible ? uri!.AbsoluteUri : null;
        OpenNexusFilesButton.IsEnabled = visible;
        DownloadAvailableSourcesButton.IsEnabled = visible && !sourceAcquisitionRunning && nexusRecoverySourceDownloader?.HasCredential == true;
        var actions = repair?.HumanActionManifest?.ManualAcquisitions ?? [];
        foreach (var action in actions)
        {
            if (!IsAllowedNexusFilesUri(action.OfficialFilesUri, out var sourceUri) ||
                string.IsNullOrWhiteSpace(action.ExpectedArchiveLeaf) ||
                !string.Equals(Path.GetFileName(action.ExpectedArchiveLeaf), action.ExpectedArchiveLeaf, StringComparison.Ordinal)) continue;
            var modNames = action.ModNames.IsDefaultOrEmpty ? "Nexus source" : string.Join(", ", action.ModNames);
            var button = new Button
            {
                Tag = sourceUri.AbsoluteUri,
                Content = $"{modNames}\n{action.ExpectedArchiveLeaf}",
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
            };
            AutomationProperties.SetName(button, $"Open source for {action.ExpectedArchiveLeaf}");
            button.Click += OnOpenNexusFilesClicked;
            SourceAcquisitionLinks.Children.Add(button);
        }
        AllSourceLinksHeading.Visibility = SourceAcquisitionLinks.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var manifestId = repair?.HumanActionManifest?.ManifestId;
        if (visible && automatic && nexusRecoverySourceDownloader?.HasCredential == true &&
            !string.IsNullOrWhiteSpace(manifestId) && automaticAcquisitionAttempts.Add(manifestId))
            _ = AcquireAvailableSourcesAsync(repair!);
    }

    private static bool IsAllowedNexusFilesUri(string? target, out Uri uri)
    {
        var valid = Uri.TryCreate(target, UriKind.Absolute, out var candidate) &&
            candidate.Scheme == Uri.UriSchemeHttps &&
            string.Equals(candidate.Host, "www.nexusmods.com", StringComparison.OrdinalIgnoreCase) &&
            candidate.AbsolutePath.StartsWith("/skyrimspecialedition/mods/", StringComparison.OrdinalIgnoreCase);
        uri = candidate ?? new Uri("https://www.nexusmods.com/");
        return valid;
    }

    private void PopulateArchiveCandidates(AssistantRepairAvailability? repair)
    {
        ArchiveCandidateList.Children.Clear();
        var candidates = repair is null || repair.ArchiveCandidates.IsDefault
            ? []
            : repair.ArchiveCandidates;
        ArchiveCandidatePanel.Visibility = candidates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var candidate in candidates)
        {
            var required = candidate.RequiredEvidence.IsDefaultOrEmpty
                ? "No version-compatibility evidence required"
                : "Required: " + string.Join(", ", candidate.RequiredEvidence);
            ArchiveCandidateList.Children.Add(new TextBlock
            {
                Text = $"{candidate.PluginName} · {candidate.CandidateRole}\n{candidate.ProviderName} · {candidate.ArchiveLeaf}\n{candidate.Status} · {candidate.CompatibilityStatus}\n{required}\nEvidence: {candidate.EvidenceId}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void ApplyActionStates(AssistantTaskRecord? task)
    {
        var states = task is null || task.ActionStates.IsDefault ? [] : task.ActionStates;
        ApplyActionState(CaptureCurrentStateButton, FindState(states, AssistantCaseAction.CaptureCurrentState));
        ApplyActionState(RefreshRecoverySourcesButton, FindState(states, AssistantCaseAction.RefreshRecoverySources));
        ApplyActionState(DiagnoseButton, FindState(states, AssistantCaseAction.Diagnose));
        ApplyActionState(AttachEvidenceTaskButton, FindState(states, AssistantCaseAction.AttachEvidence));
        ApplyActionState(ReviewEvidenceButton, FindState(states, AssistantCaseAction.ReviewEvidence));
        ApplyActionState(ReviewRepairButton, FindState(states, AssistantCaseAction.ReviewRepair));
        ApplyActionState(ApplyRepairButton, FindState(states, AssistantCaseAction.ApplyRepair));
        ApplyActionState(RollBackButton, FindState(states, AssistantCaseAction.RollBack));
    }

    private static AssistantCaseActionState? FindState(
        IReadOnlyList<AssistantCaseActionState> states,
        AssistantCaseAction action) => states.FirstOrDefault(item => item.Action == action);

    private static void ApplyActionState(Button button, AssistantCaseActionState? actionState)
    {
        button.Visibility = actionState?.IsVisible == true ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = actionState?.IsEnabled == true;
        ToolTipService.SetToolTip(button, actionState?.Detail ?? "This action is unavailable for the current case state.");
    }

    private void PopulateFinding(AssistantDeterministicFinding? finding)
    {
        DiagnosticResultPanel.Visibility = finding is null ? Visibility.Collapsed : Visibility.Visible;
        CapabilityRequiredPanel.Visibility = finding?.CapabilityRequired is null ? Visibility.Collapsed : Visibility.Visible;
        PopulateCapabilityAssessment(finding?.CapabilityAssessment);
        FindingEvidenceList.Items.Clear();
        if (finding is null)
        {
            CapabilityRequiredText.Text = string.Empty;
            return;
        }

        AffectedModsText.Text = RenderBoundedDiagnosticValues(finding.AffectedMods, value => value, ", ");
        ModRolesText.Text = finding.ModRoles.IsDefaultOrEmpty
            ? "UNRESOLVED"
            : RenderBoundedDiagnosticValues(finding.ModRoles, role => $"{role.Mod}: {role.Role}", Environment.NewLine);
        FindingText.Text = string.IsNullOrWhiteSpace(finding.Finding) ? "UNRESOLVED" : finding.Finding;
        SolutionText.Text = string.IsNullOrWhiteSpace(finding.Solution) ? "UNRESOLVED" : finding.Solution;
        ConfidenceText.Text = string.IsNullOrWhiteSpace(finding.Confidence) ? "Not evaluated" : finding.Confidence;

        var evidence = finding.Evidence.IsDefault ? [] : finding.Evidence;
        foreach (var item in evidence.Take(MaximumInlineDiagnosticItems))
            FindingEvidenceList.Items.Add(new TextBlock { Text = $"• {item}", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (evidence.Length > MaximumInlineDiagnosticItems)
            FindingEvidenceList.Items.Add(new TextBlock
            {
                Text = $"• … {evidence.Length - MaximumInlineDiagnosticItems:N0} more sealed evidence IDs; use Review Evidence for the complete set.",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        if (evidence.Length == 0 && !finding.EvidenceToolIds.IsDefaultOrEmpty)
            foreach (var toolId in finding.EvidenceToolIds)
                FindingEvidenceList.Items.Add(new TextBlock { Text = $"• {toolId.Value}", FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (FindingEvidenceList.Items.Count == 0)
            FindingEvidenceList.Items.Add(new TextBlock { Text = "No diagnostic evidence receipt is available.", FontSize = 11, TextWrapping = TextWrapping.Wrap });

        if (finding.CapabilityRequired is { } required)
        {
            static string JoinOrNone(IEnumerable<string> values) =>
                string.Join(Environment.NewLine, values.Select(value => $"• {value}")) is { Length: > 0 } joined ? joined : "• None recorded";
            CapabilityRequiredText.Text =
                $"Coverage gaps\n{JoinOrNone(required.CoverageGaps)}\n\nMissing inputs\n{JoinOrNone(required.MissingInputs)}\n\nMissing capabilities\n{JoinOrNone(required.MissingCapabilityIds)}" +
                (required.Resolutions.IsDefaultOrEmpty
                    ? string.Empty
                    : $"\n\nAUTO routes\n{string.Join(Environment.NewLine, required.Resolutions.Select(route => $"â€¢ {route.RequiredCapabilityId}: {route.Status} — {route.NextAction}"))}");
        }
    }

    private static string RenderBoundedDiagnosticValues<T>(
        IReadOnlyCollection<T> values,
        Func<T, string> selector,
        string separator)
    {
        if (values.Count == 0) return "UNRESOLVED";
        var visible = string.Join(separator, values.Take(MaximumInlineDiagnosticItems).Select(selector));
        return values.Count <= MaximumInlineDiagnosticItems
            ? visible
            : $"{visible}{separator}… {values.Count - MaximumInlineDiagnosticItems:N0} more; use Review Evidence for the complete set.";
    }

    private void PopulateCapabilityAssessment(AssistantCapabilityAssessment? assessment)
    {
        InstalledCapabilityProviderList.Children.Clear();
        CommunityCapabilityCandidateList.Children.Clear();
        CapabilityAssessmentPanel.Visibility = assessment is null ? Visibility.Collapsed : Visibility.Visible;
        if (assessment is null)
        {
            CapabilityAssessmentTitleText.Text = string.Empty;
            InstalledCapabilityStatusText.Text = string.Empty;
            CapabilityDiscoveryStatusText.Text = string.Empty;
            return;
        }

        CapabilityAssessmentTitleText.Text = assessment.DisplayName;
        InstalledCapabilityStatusText.Text = assessment.InstalledStatus switch
        {
            AssistantInstalledCapabilityStatus.Satisfied => "Satisfied by the captured profile.",
            AssistantInstalledCapabilityStatus.Partial => "An installed provider was found, but its required coverage is incomplete.",
            AssistantInstalledCapabilityStatus.Absent => "No installed mod in the captured profile satisfies this capability.",
            _ => "Installed coverage is unresolved.",
        };
        foreach (var provider in assessment.InstalledProviders)
        {
            InstalledCapabilityProviderList.Children.Add(new TextBlock
            {
                Text = $"{provider.Name} - {provider.Role}\n{provider.Status}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        CapabilityDiscoveryStatusText.Text = assessment.DiscoveryStatus switch
        {
            AssistantProviderDiscoveryStatus.Current => assessment.ObservedAtUtc is { } observed
                ? $"Current provider evidence observed {observed.LocalDateTime:g}."
                : "Current provider evidence is available.",
            AssistantProviderDiscoveryStatus.Stale => "Provider evidence is stale and cannot support a current recommendation.",
            AssistantProviderDiscoveryStatus.AuthenticationRequired => "Connect the provider to refresh current candidates.",
            AssistantProviderDiscoveryStatus.Unavailable => "The provider could not be reached; no current recommendation is asserted.",
            _ => "Community discovery was not requested.",
        };
        foreach (var candidate in assessment.CommunityCandidates)
        {
            var version = string.IsNullOrWhiteSpace(candidate.Version) ? string.Empty : $" - {candidate.Version}";
            var patches = candidate.RequiredPatches.IsDefaultOrEmpty
                ? "No required patch is asserted."
                : "Required patches: " + string.Join(", ", candidate.RequiredPatches);
            CommunityCapabilityCandidateList.Children.Add(new TextBlock
            {
                Text = $"{candidate.Name}{version} - {candidate.Provider}\n{candidate.CompatibilityStatus}: {candidate.CompatibilityDetail}\n{patches}",
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private void PopulateMods(AssistantSessionSnapshot snapshot)
    {
        ModsChecklist.Children.Clear();
        foreach (var mod in snapshot.Mods)
        {
            var checkbox = new CheckBox
            {
                Content = $"{mod.Name} · {(mod.IsEnabled ? "Enabled" : "Disabled")}",
                Tag = mod.Id,
                IsChecked = snapshot.Draft.ModIds.Contains(mod.Id),
            };
            checkbox.Checked += OnModChecked;
            checkbox.Unchecked += OnModChecked;
            ModsChecklist.Children.Add(checkbox);
        }
        ModsButton.Content = snapshot.Draft.ModIds.Length == 0 ? "Mods · None selected" : $"Mods · {snapshot.Draft.ModIds.Length} selected";
        ModsButton.IsEnabled = snapshot.Mods.Length > 0;
    }

    private void PopulateTools(AssistantSessionSnapshot snapshot)
    {
        ToolsChecklist.Children.Clear();
        foreach (var tool in snapshot.Tools)
        {
            var checkbox = new CheckBox
            {
                Content = $"{tool.Name} · {tool.Availability}",
                Tag = tool.Id,
                IsChecked = snapshot.Draft.ToolIds.Contains(tool.Id),
            };
            checkbox.Checked += OnToolChecked;
            checkbox.Unchecked += OnToolChecked;
            ToolsChecklist.Children.Add(checkbox);
        }
        ToolsButton.Content = snapshot.Draft.ToolIds.Length == 0 ? "Tools · None selected" : $"Tools · {snapshot.Draft.ToolIds.Length} selected";
        ToolsButton.IsEnabled = snapshot.Tools.Length > 0;
    }

    private void PopulateDraftAttachments(AssistantSessionSnapshot snapshot)
    {
        DraftAttachmentList.Items.Clear();
        foreach (var attachment in snapshot.Draft.Attachments)
        {
            var row = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 6 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = attachment.OriginalName,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var remove = new Button
            {
                Tag = attachment.Path,
                Content = "Remove",
                Padding = new Thickness(6, 2, 6, 2),
            };
            Microsoft.UI.Xaml.Controls.Grid.SetColumn(remove, 1);
            AutomationProperties.SetName(remove, $"Remove attachment {attachment.OriginalName}");
            remove.Click += OnRemoveAttachmentClicked;
            row.Children.Add(remove);
            DraftAttachmentList.Items.Add(row);
        }
    }

    private void NotifyChanged()
    {
        Refresh();
        layoutChanged?.Invoke();
    }

    private void OnHistoryClicked(object sender, RoutedEventArgs e) { state?.ShowHistory(); NotifyChanged(); }
    private void OnNewChatClicked(object sender, RoutedEventArgs e) { state?.StartNewInvestigation(); NotifyChanged(); }
    private void OnFormClicked(object sender, RoutedEventArgs e) { state?.ToggleForm(); NotifyChanged(); }
    private void OnFullScreenClicked(object sender, RoutedEventArgs e) { state?.ToggleFullScreen(); NotifyChanged(); }
    private void OnGameScopeClicked(object sender, RoutedEventArgs e) { state?.SelectIntakeScope(AssistantIntakeScope.Game); NotifyChanged(); }
    private void OnGridScopeClicked(object sender, RoutedEventArgs e) { state?.SelectIntakeScope(AssistantIntakeScope.Grid); NotifyChanged(); }

    private void OnHistoryTaskClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string taskId }) state?.OpenTask(taskId);
        NotifyChanged();
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        SendButton.IsEnabled = false;
        try { await state.PrepareSubmissionAsync(lifetimeToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private async void OnApproveAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        try { await state.ApproveSubmissionAsync(NotifyChanged, lifetimeToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private void OnRejectAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        state?.RejectAuthorization();
        NotifyChanged();
    }

    private async void OnResumeTaskClicked(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        try { await state.ResumeActiveTaskAsync(NotifyChanged, lifetimeToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private void OnSuggestionClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string suggestion }) state?.ApplySuggestion(suggestion);
        NotifyChanged();
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SelectGame(GameSelector.SelectedItem is AssistantGameOption game ? game.Id : null);
        NotifyChanged();
    }

    private void OnInstallationSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SelectInstallation((InstallationSelector.SelectedItem as AssistantInstallationOption)?.Id);
        NotifyChanged();
    }

    private void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SelectProfile((ProfileSelector.SelectedItem as AssistantProfileOption)?.Id);
        NotifyChanged();
    }

    private void OnClassSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SelectClass((ClassSelector.SelectedItem as AssistantClassOption)?.Id);
        NotifyChanged();
    }

    private void OnCapabilitySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SelectCapability((CapabilitySelector.SelectedItem as AssistantGameplayCapabilityOption)?.Id);
        NotifyChanged();
    }

    private void OnModChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not CheckBox { Tag: ModId id } checkbox) return;
        state.SetModSelected(id, checkbox.IsChecked == true);
        NotifyChanged();
    }

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not CheckBox { Tag: ToolId id } checkbox) return;
        state.SetToolSelected(id, checkbox.IsChecked == true);
        NotifyChanged();
    }

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        if (state.Snapshot().Surface == AssistantSurface.Task)
        {
            var text = ComposerText.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            state.StartNewInvestigation();
            state.SetProblem(text);
            Refresh();
            ComposerText.Focus(FocusState.Programmatic);
            ComposerText.Select(ComposerText.Text.Length, 0);
            return;
        }
        state.SetProblem(ComposerText.Text);
        Refresh();
    }

    private void OnProblemTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SetProblem(ProblemText.Text);
        Refresh();
    }

    private void OnExpectedBehaviorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SetExpectedBehavior(ExpectedBehaviorText.Text);
        Refresh();
    }

    private void OnReproductionLocationTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SetReproductionOrLocation(ReproductionLocationText.Text);
        Refresh();
    }

    private void OnDesiredOutcomeTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        state.SetDesiredOutcome(DesiredOutcomeText.Text);
        Refresh();
    }

    private void OnAuthorizationScopeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || state is null || AuthorizationScopeSelector.SelectedIndex != 0) return;
        state.SetAuthorizationScope(AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly);
        Refresh();
    }

    private async void OnAttachEvidenceClicked(object sender, RoutedEventArgs e)
    {
        if (state is null || evidenceFilePicker is null) return;
        try
        {
            var selected = await evidenceFilePicker.PickFilesAsync(lifetimeToken);
            if (selected.IsEmpty) return;
            var attachments = selected.Select(path => new AssistantAttachmentDraft(
                path,
                Path.GetFileName(path),
                ResolveMediaType(path))).ToArray();
            if (state.Snapshot().Surface == AssistantSurface.Task)
                await state.ExecuteActiveActionAsync(AssistantCaseAction.AttachEvidence, attachments, NotifyChanged, lifetimeToken);
            else
                foreach (var attachment in attachments) state.AddAttachment(attachment);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private void OnRemoveAttachmentClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) state?.RemoveAttachment(path);
        NotifyChanged();
    }

    private async Task ExecuteActionAsync(AssistantCaseAction action)
    {
        if (state is null) return;
        try { await state.ExecuteActiveActionAsync(action, null, NotifyChanged, lifetimeToken); }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private async void OnCaptureCurrentStateClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.CaptureCurrentState);

    private async void OnRefreshRecoverySourcesClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.RefreshRecoverySources);

    private async void OnDiagnoseClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.Diagnose);

    private async void OnReviewEvidenceClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.ReviewEvidence);

    private async void OnReviewRepairClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.ReviewRepair);

    private async void OnApplyRepairClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.ApplyRepair);

    private async void OnRollBackClicked(object sender, RoutedEventArgs e) =>
        await ExecuteActionAsync(AssistantCaseAction.RollBack);

    private async void OnOpenNexusFilesClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } || !IsAllowedNexusFilesUri(target, out var uri)) return;
        await Launcher.LaunchUriAsync(uri);
    }

    private async void OnDownloadAvailableSourcesClicked(object sender, RoutedEventArgs e)
    {
        var repair = state?.Snapshot().ActiveTask?.RepairAvailability;
        if (repair is not null) await AcquireAvailableSourcesAsync(repair);
    }

    private async Task AcquireAvailableSourcesAsync(AssistantRepairAvailability repair)
    {
        if (sourceAcquisitionRunning || nexusRecoverySourceDownloader is null) return;
        sourceAcquisitionRunning = true;
        sourceAcquisitionStatusOverride = "Preparing exact Nexus source acquisitionâ€¦";
        NotifyChanged();
        try
        {
            var progress = new Progress<NexusSourceAcquisitionProgress>(item =>
            {
                sourceAcquisitionStatusOverride = $"Downloading source {Math.Min(item.Completed + 1, item.Total)} of {item.Total}: {item.CurrentArchiveLeaf}";
                if (item.Completed >= item.Total) sourceAcquisitionStatusOverride = "Verifying completed source downloadsâ€¦";
                NotifyChanged();
            });
            var result = await nexusRecoverySourceDownloader.AcquireAsync(repair, progress, lifetimeToken);
            sourceAcquisitionStatusOverride = $"Source acquisition finished: {result.Downloaded} downloaded, {result.AlreadyPresent} already present, " +
                $"{result.BrowserRequired + result.SourceUnresolved} need a link or source decision, {result.Failed} failed. " +
                "Exact links remain below. Downloading did not install or enable anything.";
        }
        catch (OperationCanceledException)
        {
            sourceAcquisitionStatusOverride = "Source acquisition stopped. Completed downloads remain in MO2 downloads.";
        }
        finally
        {
            sourceAcquisitionRunning = false;
            NotifyChanged();
        }
    }

    private static string? ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".txt" or ".log" => "text/plain",
        ".json" => "application/json",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        _ => "application/octet-stream",
    };

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || state?.IsFullScreen != true) return;
        state.ToggleFullScreen();
        NotifyChanged();
        e.Handled = true;
    }

    private static string FormatReadiness(AssistantDraftReadiness readiness) => readiness switch
    {
        AssistantDraftReadiness.Incomplete => "Draft incomplete",
        AssistantDraftReadiness.ReadyForDeterministicCollection => "Ready for deterministic collection",
        AssistantDraftReadiness.UnsupportedCoverage => "Unsupported coverage",
        AssistantDraftReadiness.RuntimeUnavailable => "Runtime unavailable",
        _ => readiness.ToString(),
    };

    private static string FormatTranscriptKind(AssistantTranscriptKind kind) => kind switch
    {
        AssistantTranscriptKind.UserClaim => "USER CLAIM",
        AssistantTranscriptKind.Progress => "PROGRESS",
        AssistantTranscriptKind.Evidence => "EVIDENCE",
        AssistantTranscriptKind.Result => "RESULT",
        AssistantTranscriptKind.Failure => "FAILURE",
        _ => kind.ToString().ToUpperInvariant(),
    };

    private static string FormatLocation(AssistantSessionSnapshot snapshot)
    {
        var game = snapshot.Games.FirstOrDefault(item => item.Id == snapshot.Draft.GameId)?.Name ?? "No game selected";
        var installation = snapshot.Installations.FirstOrDefault(item => item.Id == snapshot.Draft.InstallationId)?.Name ?? "installation not selected";
        var profile = snapshot.Profiles.FirstOrDefault(item => item.Id == snapshot.Draft.ProfileId)?.Name ?? "profile not selected";
        return $"{game} · {installation} · {profile}";
    }

    private static string ResolveClassGlyph(string iconId) => iconId switch
    {
        "grid.icon.installation" => "\uE73E",
        "grid.icon.crash" => "\uE7BA",
        "grid.icon.assets" => "\uE8B7",
        "grid.icon.mesh" => "\uE809",
        "grid.icon.texture" => "\uE790",
        "grid.icon.shader" => "\uE793",
        "grid.icon.weather" => "\uE706",
        "grid.icon.lod" => "\uE81E",
        "grid.icon.collision" => "\uE7C9",
        "grid.icon.world-object" => "\uE7F8",
        "grid.icon.animation" => "\uE768",
        "grid.icon.creature" => "\uE7EE",
        "grid.icon.audio" => "\uE767",
        "grid.icon.npc" => "\uE77B",
        "grid.icon.dialogue" => "\uE8BD",
        "grid.icon.quest" => "\uE81C",
        "grid.icon.combat" => "\uE7FC",
        "grid.icon.magic" => "\uE945",
        "grid.icon.appearance" => "\uE77B",
        "grid.icon.physics" => "\uE9CA",
        "grid.icon.ui" => "\uE7C3",
        "grid.icon.performance" => "\uE9D9",
        "grid.icon.plugin" => "\uE8F1",
        "grid.icon.skse" => "\uE943",
        "grid.icon.script" => "\uE756",
        "grid.icon.records" => "\uE8A5",
        "grid.icon.distribution" => "\uE8F9",
        "grid.icon.generated" => "\uE950",
        "grid.icon.update" => "\uE895",
        "grid.icon.archive" => "\uE7B8",
        "grid.icon.metapatch" => "\uE8B0",
        "grid.icon.nsfw" => "\uE72E",
        _ => "\uE946",
    };

    private static Brush ResolveClassBrush(string iconId)
    {
        var palette = new[]
        {
            Windows.UI.Color.FromArgb(255, 86, 156, 214),
            Windows.UI.Color.FromArgb(255, 78, 201, 176),
            Windows.UI.Color.FromArgb(255, 220, 170, 74),
            Windows.UI.Color.FromArgb(255, 194, 126, 220),
            Windows.UI.Color.FromArgb(255, 230, 111, 139),
            Windows.UI.Color.FromArgb(255, 126, 190, 98),
        };
        var stable = iconId.Aggregate(17, (value, character) => unchecked((value * 31) + character));
        return new SolidColorBrush(palette[(stable & int.MaxValue) % palette.Length]);
    }
}
