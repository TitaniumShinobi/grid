using Grid.App.Services;
using Grid.App.Controls;
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
    private CanonicalCatalogRuntimeService? canonicalCatalogRuntimeService;
    private CanonicalRuntimeMatch? canonicalRuntimeMatch;
    private readonly HashSet<string> automaticAcquisitionAttempts = new(StringComparer.Ordinal);
    private string? sourceAcquisitionStatusOverride;
    private bool sourceAcquisitionRunning;
    private Action? layoutChanged;
    private CancellationToken lifetimeToken;
    private bool rendering;
    private ReferenceEditingContext? referenceEditingContext;
    private readonly Dictionary<TicketReferenceContextKind, ReferenceEditingContext> pendingOtherContexts = [];
    private sealed record ReferenceEditingContext(AssistantSessionState State, InvestigationId Investigation,
        GameId? Game, InstallationId? Installation, ProfileId? Profile, CancellationToken Lifetime);

    private ReferenceEditingContext? CaptureReferenceEditingContext()
    {
        if (state is null) return null;
        var draft = state.Snapshot().TicketDraft;
        return new(state, draft.InvestigationId, draft.GameId, draft.InstallationId, draft.ProfileId, lifetimeToken);
    }

    private bool IsCurrentReferenceEditingContext(ReferenceEditingContext? context) =>
        context is not null && !context.Lifetime.IsCancellationRequested &&
        context == CaptureReferenceEditingContext();

    public AssistantPanel()
    {
        InitializeComponent();
        var selectorStyle = (Style)Resources["TicketSelectorButtonStyle"];
        ToolsButton.Style = selectorStyle;
        ModsButton.Style = selectorStyle;
        foreach (var selector in new[] { LocationSelector, MissionSelector, ItemSelector, EntitySelector })
            selector.ApplyClosedSelectorStyle(selectorStyle);
        foreach (var selector in new[] { GameSelector, ProfileSelector, ClassSelector, ProblemSelector,
            TimingSelector, GoalSelector, ToolsButton, ModsButton })
            AttachContentSizedFlyout(selector);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ToolsButton, 0);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ModsButton, 1);
        LocationSelector.CanonicalSelectionCommitted += OnCanonicalSelectionCommitted;
        LocationSelector.ScaffoldSelectionCommitted += OnScaffoldSelectionCommitted;
        MissionSelector.ScaffoldSelectionCommitted += OnScaffoldSelectionCommitted;
        ItemSelector.ScaffoldSelectionCommitted += OnScaffoldSelectionCommitted;
        EntitySelector.ScaffoldSelectionCommitted += OnScaffoldSelectionCommitted;
        LocationSelector.OtherRequested += (_, _) => ShowOtherContext(TicketReferenceContextKind.Location);
        MissionSelector.OtherRequested += (_, _) => ShowOtherContext(TicketReferenceContextKind.MissionOrQuest);
        ItemSelector.OtherRequested += (_, _) => ShowOtherContext(TicketReferenceContextKind.Item);
        EntitySelector.OtherRequested += (_, _) => ShowOtherContext(TicketReferenceContextKind.Entity);
    }

    private static void AttachContentSizedFlyout(Button selector)
    {
        if (selector.Flyout is not Flyout { Content: ScrollViewer viewport } flyout) return;
        flyout.Opening += (_, _) => SelectorFlyoutSizing.Apply(selector, flyout, viewport, viewport);
    }

    public void BindState(
        AssistantSessionState assistantState,
        IEvidenceFilePicker filePicker,
        Action onLayoutChanged,
        LocalSourceAcquisitionPreferencesStore? acquisitionPreferencesStore = null,
        NexusRecoverySourceDownloader? recoverySourceDownloader = null,
        CanonicalCatalogRuntimeService? runtimeCatalogService = null,
        CancellationToken cancellationToken = default)
    {
        state = assistantState ?? throw new ArgumentNullException(nameof(assistantState));
        evidenceFilePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
        layoutChanged = onLayoutChanged ?? throw new ArgumentNullException(nameof(onLayoutChanged));
        sourceAcquisitionPreferencesStore = acquisitionPreferencesStore;
        nexusRecoverySourceDownloader = recoverySourceDownloader;
        canonicalCatalogRuntimeService = runtimeCatalogService;
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
        state.SetComposerText(text ?? string.Empty);
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
            canonicalRuntimeMatch = snapshot.TicketDraft.GameId is GameId refreshGameId
                ? canonicalCatalogRuntimeService?.Match(
                    refreshGameId, snapshot.TicketDraft.InstallationId, snapshot.TicketDraft.ProfileId)
                : null;
            if (PruneInvalidCanonicalSelections(snapshot))
                snapshot = state.Snapshot();
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
            DifContentInset.Padding = IntakeForm.Visibility == Visibility.Visible
                ? new Thickness(5) : new Thickness(16, 14, 16, 24);
            EmptyHome.Visibility = snapshot.IsFormVisible ? Visibility.Collapsed : Visibility.Visible;
            FormToggleButton.IsChecked = snapshot.IsFormVisible;
            FullScreenButton.IsChecked = snapshot.IsFullScreen;

            ComposerText.Text = snapshot.Surface == AssistantSurface.Task ? string.Empty : snapshot.ComposerText;
            ComposerText.PlaceholderText = snapshot.Surface == AssistantSurface.Task ? "Start a new request…" : "Message Grid";
            var preview = snapshot.TicketPreview ?? new AssistantTicketPreview(
                "New investigation", "grid.icon.unknown",
                AssistantTicketNameProvenance.DeterministicStructuredSelections, []);
            RequestTitleText.Text = preview.Name;
            RequestPill.Visibility = snapshot.Surface == AssistantSurface.Home && snapshot.TicketDraft.Class is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            ClassIcon.Glyph = ResolveClassGlyph(preview.IconId);
            var classBrush = ResolveClassBrush(preview.IconId);
            ClassIcon.Foreground = classBrush;
            RequestTitleText.Foreground = classBrush;
            ReadinessText.Text = snapshot.Surface == AssistantSurface.Task
                ? "Work locally"
                : FormatReadiness(snapshot.TicketReadiness);
            SendButton.IsEnabled = snapshot.Surface == AssistantSurface.Home && snapshot.TicketReadiness.CanSubmit && snapshot.Draft.CanSubmit;
            AutomationProperties.SetName(SendButton, SendButton.IsEnabled ? "Start Investigation" : "Start Investigation unavailable");
            LocationText.Text = FormatLocation(snapshot);

            PopulateSingleSelectors(snapshot);
            PopulateMods(snapshot);
            PopulateTools(snapshot);
            PopulateReferenceContexts(snapshot);
            if (TicketSelectorRows.ActualWidth > 0) ApplyTicketSelectorLayout(TicketSelectorRows.ActualWidth);
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
                var paths = scope.ExactReadPaths.Length == 0
                    ? "No filesystem paths requested"
                    : string.Join("\n", scope.ExactReadPaths);
                var resources = scope.ExactReadResources.IsDefaultOrEmpty
                    ? string.Empty
                    : "\n" + string.Join("\n", scope.ExactReadResources.Select(resource =>
                        $"{resource.ResourceType}: {resource.ResourceId}{(resource.Constraints.IsDefaultOrEmpty ? string.Empty : $" ({string.Join("; ", resource.Constraints)})")}"));
                AuthorizationScopes.Children.Add(new TextBlock
                {
                    Text = $"{scope.ProviderName} - {scope.Availability}\n{scope.ObservationMode ?? "No observation mode"}\n{paths}{resources}",
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
                    : $"\n\nAUTO routes\n{string.Join(Environment.NewLine, required.Resolutions.Select(route => $"• {route.RequiredCapabilityId}: {route.Status} — {route.NextAction}"))}");
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

    private void PopulateSingleSelectors(AssistantSessionSnapshot snapshot)
    {
        GameOptions.Children.Clear();
        var selectedGame = snapshot.Games.FirstOrDefault(option => option.Id == snapshot.TicketDraft.GameId);
        foreach (var game in snapshot.Games)
            GameOptions.Children.Add(CreateRadioOption(game.Name, game, game.Id == snapshot.TicketDraft.GameId, OnGameOptionChecked));
        GameSelector.Content = SelectorValue("Game", selectedGame?.Name);
        GameSelector.IsEnabled = snapshot.Games.Length > 0;

        ProfileOptions.Children.Clear();
        var selectedProfile = snapshot.Profiles.FirstOrDefault(option => option.Id == snapshot.TicketDraft.ProfileId);
        foreach (var profile in snapshot.Profiles)
            ProfileOptions.Children.Add(CreateRadioOption(profile.DisplayName, profile, profile.Id == snapshot.TicketDraft.ProfileId, OnProfileOptionChecked));
        ProfileSelector.Content = SelectorValue("Profile", selectedProfile?.DisplayName);
        ProfileSelector.IsEnabled = snapshot.TicketDraft.GameId is not null && snapshot.Profiles.Length > 0;

        ClassOptions.Children.Clear();
        var selectedClass = snapshot.Classes.FirstOrDefault(option => option.Id == snapshot.TicketDraft.Class?.Id.Value);
        foreach (var option in snapshot.Classes)
            ClassOptions.Children.Add(CreateRadioOption(option.DisplayName, option,
                option.Id == snapshot.TicketDraft.Class?.Id.Value, OnClassOptionChecked));
        ClassSelector.Content = SelectorValue("Class", selectedClass?.DisplayName);
        ClassSelector.IsEnabled = snapshot.Classes.Length > 0;

        ProblemOptions.Children.Clear();
        foreach (var option in snapshot.Problems)
            ProblemOptions.Children.Add(CreateRadioOption(option.DisplayName, option,
                option.Id == snapshot.TicketDraft.Problem?.Id, OnProblemOptionChecked));
        ProblemSelector.Content = SelectorValue("Problem", snapshot.TicketDraft.Problem?.DisplayName);
        ProblemSelector.IsEnabled = snapshot.TicketDraft.Class is not null && snapshot.Problems.Length > 0;

        TimingOptions.Children.Clear();
        foreach (var option in snapshot.Timings)
            TimingOptions.Children.Add(CreateRadioOption(option.DisplayName, option,
                option.Id == snapshot.TicketDraft.Timing?.Id, OnTimingOptionChecked));
        TimingSelector.Content = SelectorValue("Timing", snapshot.TicketDraft.Timing?.DisplayName);
        TimingSelector.IsEnabled = snapshot.TicketDraft.Class is not null && snapshot.Timings.Length > 0;

        GoalOptions.Children.Clear();
        foreach (var option in snapshot.Goals)
            GoalOptions.Children.Add(CreateRadioOption(option.DisplayName, option,
                option.Id == snapshot.TicketDraft.Goal?.Id, OnGoalOptionChecked));
        GoalSelector.Content = SelectorValue("Goal", snapshot.TicketDraft.Goal?.DisplayName);
        GoalSelector.IsEnabled = snapshot.Goals.Length > 0;
    }

    private static string SelectorValue(string field, string? value) =>
        string.IsNullOrWhiteSpace(value) ? field : value;

    private RadioButton CreateRadioOption(
        string displayName,
        object tag,
        bool selected,
        RoutedEventHandler checkedHandler)
    {
        var option = new RadioButton
        {
            Content = displayName,
            Tag = tag,
            IsChecked = selected,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Resources["TicketSelectorOptionStyle"],
        };
        option.Checked += checkedHandler;
        return option;
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
        ModsButton.IsEnabled = snapshot.Mods.Length > 0;
        ModsButton.Content = SelectorValue("Mod", snapshot.TicketDraft.Mods.LastOrDefault()?.DisplayName);
    }

    private void PopulateTools(AssistantSessionSnapshot snapshot)
    {
        ToolsChecklist.Children.Clear();
        if (snapshot.Tools.Length > 0)
            ToolsChecklist.Children.Add(CreateFlyoutGroupHeading("GRID-integrated tools"));
        foreach (var tool in snapshot.Tools)
        {
            var checkbox = new CheckBox
            {
                Content = $"{tool.Name} · {tool.Availability}",
                Tag = tool.Id,
                IsChecked = snapshot.Draft.ToolIds.Contains(tool.Id),
                IsEnabled = tool.Availability == AvailabilityState.Available,
            };
            checkbox.Checked += OnToolChecked;
            checkbox.Unchecked += OnToolChecked;
            ToolsChecklist.Children.Add(checkbox);
        }
        if (snapshot.ConfiguredTools.Length > 0)
        {
            ToolsChecklist.Children.Add(CreateFlyoutGroupHeading("Configured context"));
            foreach (var tool in snapshot.ConfiguredTools)
            {
                var checkbox = new CheckBox
                {
                    Content = $"{tool.DisplayName}\nConfigured · {(tool.IsRunnable ? "Runnable" : "Not runnable")} · Identity {tool.IdentityStatus} · GRID integration {tool.IntegrationStatus}",
                    Tag = tool.Id,
                    IsChecked = snapshot.TicketDraft.ConfiguredToolContext.Any(value => value.ConfigurationId == tool.Id),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                checkbox.Checked += OnConfiguredToolChecked;
                checkbox.Unchecked += OnConfiguredToolChecked;
                ToolsChecklist.Children.Add(checkbox);
            }
        }
        var availableCount = snapshot.Tools.Length + snapshot.ConfiguredTools.Length;
        ToolsButton.Content = SelectorValue("Tool", snapshot.TicketDraft.ConfiguredToolContext.LastOrDefault()?.DisplayName
            ?? snapshot.TicketDraft.IntegratedTools.LastOrDefault()?.DisplayName);
        ToolsButton.IsEnabled = availableCount > 0;
    }

    private static TextBlock CreateFlyoutGroupHeading(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 4, 0, 2),
        FontSize = 10,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };

    private void PopulateReferenceContexts(AssistantSessionSnapshot snapshot)
    {
        var currentContext = CaptureReferenceEditingContext();
        if (referenceEditingContext != currentContext)
        {
            foreach (var selector in new[] { LocationSelector, MissionSelector, ItemSelector, EntitySelector })
                selector.ResetScaffoldPresentationContext();
            foreach (var field in new[] { LocationOtherText, MissionOtherText, ItemOtherText, EntityOtherText })
            {
                field.Text = string.Empty;
                field.Visibility = Visibility.Collapsed;
            }
            pendingOtherContexts.Clear();
            referenceEditingContext = currentContext;
        }
        PopulateReferenceContext(snapshot, TicketReferenceContextKind.Location, KnowledgeKind.Location,
            "Location", LocationSelector, LocationOtherText, LocationSelectionPreview);
        PopulateReferenceContext(snapshot, TicketReferenceContextKind.MissionOrQuest, KnowledgeKind.MissionQuest,
            "Mission", MissionSelector, MissionOtherText, MissionSelectionPreview);
        PopulateReferenceContext(snapshot, TicketReferenceContextKind.Item, KnowledgeKind.Item,
            "Item", ItemSelector, ItemOtherText, ItemSelectionPreview);
        PopulateReferenceContext(snapshot, TicketReferenceContextKind.Entity, KnowledgeKind.Actor,
            "Actor", EntitySelector, EntityOtherText, ActorSelectionPreview);
    }

    private void PopulateReferenceContext(AssistantSessionSnapshot snapshot,
        TicketReferenceContextKind kind, KnowledgeKind knowledgeKind, string label,
        CanonicalSelectorNavigationControl selector, TextBox otherText, StackPanel preview)
    {
        var profileReady = snapshot.TicketDraft.ProfileId is not null;
        if (knowledgeKind == KnowledgeKind.Location && canonicalRuntimeMatch?.IsExact == true)
        {
            PopulateLocationSelectionPreview(snapshot);
            var locationCount = snapshot.TicketDraft.CanonicalSelections.Count(value => value.KnowledgeKind == KnowledgeKind.Location) +
                                snapshot.TicketDraft.UserContext.Count(value =>
                                    value.Kind == TicketReferenceContextKind.Location &&
                                    value.Resolution == TicketUserContextResolution.Unresolved);
            var selected = snapshot.TicketDraft.CanonicalSelections.LastOrDefault(value => value.KnowledgeKind == KnowledgeKind.Location);
            string? selectedDisplay = null;
            if (selected?.SelectedPathId is CanonicalNavigationPathId selectedPath)
                selectedDisplay = CanonicalSelectionLabel(knowledgeKind, selectedPath);
            ConfigureCanonicalSelector(selector, label, knowledgeKind, selected, selectedDisplay);
            selector.SetRuntimeStatus(FormatCanonicalRuntimeStatus(canonicalRuntimeMatch));
            selector.SetInteractionEnabled(profileReady);
            if (!profileReady)
                selector.SetClosedCaption("Location · Select Profile");
            else if (locationCount == 0)
                selector.SetClosedCaption("Location · Optional");
            else
                selector.SetClosedCaption($"Location · {locationCount} selected");
            otherText.Visibility = Visibility.Collapsed;
            return;
        }

        var entries = snapshot.TicketDraft.UserContext.Where(value => value.Kind == kind &&
            value.Resolution == TicketUserContextResolution.Unresolved).ToArray();
        selector.ConfigureScaffold(knowledgeKind, entries.Where(value => value.ScaffoldPathId is not null)
            .Select(value => value.ScaffoldPathId!).ToArray());
        selector.SetInteractionEnabled(profileReady);
        selector.SetScaffoldCaptionFallback(profileReady && entries.Length > 0 ? ReferenceContextLabel(entries[^1]) : null,
            profileReady && entries.Length > 0 ? entries[^1].ScaffoldPathId ?? "other:" + entries[^1].Value : null);
        PopulateReferenceSelectionPreview(preview, entries, kind, label, profileReady);
        if (!profileReady) otherText.Visibility = Visibility.Collapsed;
    }

    private void PopulateLocationSelectionPreview(AssistantSessionSnapshot snapshot)
    {
        LocationSelectionPreview.Children.Clear();
        if (snapshot.TicketDraft.ProfileId is null) return;
        var entries = new List<(string Key, string Label, bool IsCanonical)>();
        foreach (var selection in snapshot.TicketDraft.CanonicalSelections.Where(value => value.KnowledgeKind == KnowledgeKind.Location))
        {
            if (selection.KnowledgeRecordId is not { } recordId || selection.SelectedPathId is not { } pathId) continue;
            var displayLabel = CanonicalSelectionLabel(KnowledgeKind.Location, pathId) ?? "Location pending validation";
            entries.Add(($"{recordId.Value}:{pathId.Value}", displayLabel, true));
        }
        foreach (var context in snapshot.TicketDraft.UserContext.Where(value =>
                     value.Kind == TicketReferenceContextKind.Location &&
                     value.Resolution == TicketUserContextResolution.Unresolved))
            entries.Add((context.Value, context.Value, false));

        if (entries.Count == 0) return;
        const int inlineLimit = 3;
        var inline = entries.Take(inlineLimit).ToArray();
        foreach (var entry in inline)
            LocationSelectionPreview.Children.Add(CreateLocationPreviewRow(entry.Key, entry.Label, entry.IsCanonical));
        if (entries.Count > inlineLimit)
        {
            var overflow = new TextBlock
            {
                Text = "...",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["MutedTextBrush"],
            };
            var fullList = string.Join('\n', entries.Select(value => value.Label));
            ToolTipService.SetToolTip(overflow, new ToolTip { Content = fullList });
            AutomationProperties.SetName(overflow, $"Location selection overflow ({entries.Count} total)");
            AutomationProperties.SetHelpText(overflow, fullList);
            LocationSelectionPreview.Children.Add(overflow);
        }
    }

    private FrameworkElement CreateLocationPreviewRow(string key, string label, bool isCanonical)
    {
        var originatingContext = CaptureReferenceEditingContext();
        var row = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var remove = new Button
        {
            Content = "x",
            Tag = (key, isCanonical),
            Padding = new Thickness(4, 0, 4, 0),
            Height = 20,
            FontSize = 10,
            CornerRadius = new CornerRadius(0),
        };
        AutomationProperties.SetName(remove, $"Remove location {label}");
        remove.Click += (_, _) =>
        {
            if (rendering || state is null || !IsCurrentReferenceEditingContext(originatingContext)) return;
            if (remove.Tag is not (string removeKey, bool canonical)) return;
            if (canonical)
            {
                var separator = removeKey.IndexOf(':');
                if (separator <= 0) return;
                state.RemoveCanonicalLocationSelection(new KnowledgeRecordId(removeKey[..separator]),
                    new CanonicalNavigationPathId(removeKey[(separator + 1)..]));
            }
            else
                state.RemoveLocationOtherContext(removeKey);
            NotifyChanged();
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(text, label);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(text, 1);
        row.Children.Add(remove);
        row.Children.Add(text);
        return row;
    }

    private static string ReferenceContextLabel(TicketUserContext context)
    {
        if (context.ScaffoldPathId is null) return context.Value;
        var kind = context.Kind switch
        {
            TicketReferenceContextKind.Location => KnowledgeKind.Location,
            TicketReferenceContextKind.MissionOrQuest => KnowledgeKind.MissionQuest,
            TicketReferenceContextKind.Item => KnowledgeKind.Item,
            TicketReferenceContextKind.Entity => KnowledgeKind.Actor,
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };
        return SelectorScaffoldContract.Resolve(kind, context.ScaffoldPathId)?.Label
            ?? context.Value.Split(" → ", StringSplitOptions.None)[^1];
    }

    private void PopulateReferenceSelectionPreview(StackPanel preview, TicketUserContext[] entries,
        TicketReferenceContextKind kind, string label, bool profileReady)
    {
        preview.Children.Clear();
        AutomationProperties.SetAutomationId(preview, $"scaffold-preview:{kind}");
        AutomationProperties.SetHelpText(preview, string.Join('\n', entries.Select(ReferenceContextLabel)));
        if (!profileReady) return;
        foreach (var entry in entries.Take(3))
            preview.Children.Add(CreateReferencePreviewRow(entry, label));
        if (entries.Length <= 3) return;
        var overflow = new Button
        {
            Content = "...", FontSize = 11, Height = 20,
            Padding = new Thickness(4, 0, 4, 0), CornerRadius = new CornerRadius(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var fullList = string.Join('\n', entries.Select(ReferenceContextLabel));
        ToolTipService.SetToolTip(overflow, fullList);
        AutomationProperties.SetName(overflow, $"{label} selection overflow ({entries.Length} total)");
        AutomationProperties.SetAutomationId(overflow, $"scaffold-overflow:{kind}");
        AutomationProperties.SetHelpText(overflow, fullList);
        var allRows = new StackPanel { Spacing = 2 };
        var popup = new Flyout
        {
            Content = new ScrollViewer { Content = allRows,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters = { new Setter(Control.CornerRadiusProperty, new CornerRadius(0)) },
            },
        };
        foreach (var entry in entries) allRows.Children.Add(CreateReferencePreviewRow(entry, label, popup.Hide));
        overflow.Flyout = popup;
        AttachContentSizedFlyout(overflow);
        preview.Children.Add(overflow);
    }

    private FrameworkElement CreateReferencePreviewRow(TicketUserContext entry, string label, Action? afterRemove = null)
    {
        var originatingContext = CaptureReferenceEditingContext();
        var row = new Microsoft.UI.Xaml.Controls.Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var remove = new Button
        {
            Content = "x", Padding = new Thickness(4, 0, 4, 0), Height = 20,
            FontSize = 10, CornerRadius = new CornerRadius(0),
        };
        AutomationProperties.SetName(remove, $"Remove {label} {ReferenceContextLabel(entry)}");
        AutomationProperties.SetAutomationId(remove, $"scaffold-remove:{entry.Kind}:{entry.ScaffoldPathId ?? "other:" + entry.Value}");
        remove.Click += (_, _) =>
        {
            if (rendering || state is null || !IsCurrentReferenceEditingContext(originatingContext)) return;
            state.RemoveReferenceContext(entry.Kind, entry.Value, entry.ScaffoldPathId);
            afterRemove?.Invoke();
            NotifyChanged();
        };
        var text = new TextBlock
        {
            Text = ReferenceContextLabel(entry), FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(text, ReferenceContextLabel(entry));
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(text, 1);
        row.Children.Add(remove);
        row.Children.Add(text);
        return row;
    }

    private void OnScaffoldSelectionCommitted(object? sender, SelectorScaffoldSelection selection)
    {
        if (rendering || state is null || state.Snapshot().TicketDraft.ProfileId is null) return;
        if (selection.Kind == KnowledgeKind.Location && canonicalRuntimeMatch?.IsExact == true) return;
        var node = SelectorScaffoldContract.Resolve(selection.Kind, selection.PathId);
        if (node is null) return;
        var kind = selection.Kind switch
        {
            KnowledgeKind.Location => TicketReferenceContextKind.Location,
            KnowledgeKind.MissionQuest => TicketReferenceContextKind.MissionOrQuest,
            KnowledgeKind.Item => TicketReferenceContextKind.Item,
            KnowledgeKind.Actor => TicketReferenceContextKind.Entity,
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        state.ToggleScaffoldContext(kind, selection.PathId,
            SelectorScaffoldContract.DisplayPath(selection.Kind, selection.PathId));
        NotifyChanged();
    }

    private static string FormatCanonicalRuntimeStatus(CanonicalRuntimeMatch? match) => match switch
    {
        null => "Canonical catalog not evaluated.",
        { ValidationStatus: CatalogValidationStatus status } =>
            $"{match.State} ({status}): {match.Detail}",
        _ => $"{match.State}: {match.Detail}",
    };

    private bool PruneInvalidCanonicalSelections(AssistantSessionSnapshot snapshot)
    {
        if (state is null || snapshot.TicketDraft.CanonicalSelections.IsEmpty) return false;
        if (canonicalCatalogRuntimeService is null || canonicalCatalogRuntimeService.IsReadinessUnresolved) return false;
        var changed = false;
        foreach (var selection in snapshot.TicketDraft.CanonicalSelections)
        {
            if (selection.KnowledgeKind == KnowledgeKind.Location) continue;
            if (canonicalCatalogRuntimeService is not null && canonicalRuntimeMatch?.IsExact == true &&
                canonicalCatalogRuntimeService.ValidateSelection(canonicalRuntimeMatch, selection))
                continue;
            state.ClearCanonicalSelectorSelection(selection.KnowledgeKind);
            changed = true;
        }
        return changed;
    }

    private CanonicalSelectorResult? QueryCanonicalSelector(KnowledgeKind kind, CanonicalNavigationPathId? path) =>
        canonicalCatalogRuntimeService is not null && canonicalRuntimeMatch?.IsExact == true
            ? canonicalCatalogRuntimeService.Query(canonicalRuntimeMatch, kind, path)
            : null;

    private string? CanonicalSelectionLabel(KnowledgeKind kind, CanonicalNavigationPathId path) =>
        canonicalCatalogRuntimeService is not null && canonicalRuntimeMatch?.IsExact == true
            ? canonicalCatalogRuntimeService.GetSelectionLabel(canonicalRuntimeMatch, kind, path, state?.Snapshot().TicketDraft.ProfileId) : null;

    private void ConfigureCanonicalSelector(CanonicalSelectorNavigationControl selector, string label,
        KnowledgeKind kind, CanonicalSelectorSelection? selected, string? selectedDisplay)
    {
        if (canonicalCatalogRuntimeService?.UsesPreparedNavigation != true)
        {
            selector.Configure(label, kind, QueryCanonicalSelector, selected, selectedDisplay);
            return;
        }
        var expectedRuntime = canonicalCatalogRuntimeService;
        var expectedMatch = canonicalRuntimeMatch;
        var expectedState = state;
        selector.ConfigureAsync(label, kind, async (requestedKind, path, cursor, cancellationToken) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
            if (expectedMatch?.IsExact != true || !ReferenceEquals(expectedMatch, canonicalRuntimeMatch) ||
                !ReferenceEquals(expectedState, state)) return null;
            var result = await expectedRuntime.QueryPageAsync(
                expectedMatch, requestedKind, path, cursor, expectedState?.Snapshot().TicketDraft.ProfileId, linked.Token);
            if (!ReferenceEquals(expectedMatch, canonicalRuntimeMatch) || !ReferenceEquals(expectedState, state))
                throw new OperationCanceledException("Canonical context changed during navigation.");
            var statistics = expectedRuntime.PreparedStatistics;
            selector.SetRuntimeStatus($"{FormatCanonicalRuntimeStatus(expectedMatch)} Prepared local; " +
                $"sourceCatalogLoads={expectedRuntime.RuntimeSourceCatalogLoads}; pageReads={statistics?.PageReads}; " +
                $"cacheHits={statistics?.PageCacheHits}; rowsMaterialized={statistics?.RowsMaterialized}; " +
                $"cacheBytes={statistics?.CachedBytes}.");
            return result;
        }, selected, selectedDisplay);
    }

    private void OnCanonicalSelectionCommitted(object? sender, CanonicalSelectorSelection selection)
    {
        if (rendering || state is null) return;
        if (canonicalCatalogRuntimeService is null || canonicalRuntimeMatch?.IsExact != true ||
            !canonicalCatalogRuntimeService.ValidateSelection(canonicalRuntimeMatch, selection, state.Snapshot().TicketDraft.ProfileId))
            return; // A stale or invalid immutable generation cannot publish a selection.
        if (selection.KnowledgeKind == KnowledgeKind.Location)
            state.ToggleCanonicalLocationSelection(selection);
        else
            state.SetCanonicalSelectorSelection(selection);
        NotifyChanged();
    }

    private void ShowOtherContext(TicketReferenceContextKind kind)
    {
        var editingContext = CaptureReferenceEditingContext();
        if (rendering || state is null || editingContext?.Profile is null ||
            !IsCurrentReferenceEditingContext(editingContext)) return;
        pendingOtherContexts[kind] = editingContext;
        state.ClearCanonicalSelectorSelection(kind switch
        {
            TicketReferenceContextKind.Location => KnowledgeKind.Location,
            TicketReferenceContextKind.MissionOrQuest => KnowledgeKind.MissionQuest,
            TicketReferenceContextKind.Item => KnowledgeKind.Item,
            TicketReferenceContextKind.Entity => KnowledgeKind.Actor,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        var textBox = GetOtherContextTextBox(kind);
        textBox.Visibility = Visibility.Visible;
        textBox.Focus(FocusState.Programmatic);
    }

    private void OnTicketSelectorRowsSizeChanged(object sender, SizeChangedEventArgs e) =>
        ApplyTicketSelectorLayout(e.NewSize.Width);

    private void ApplyTicketSelectorLayout(double width)
    {
        ConfigureGrid(ClassificationRow, 3, 1);
        Place(ClassSelector, 0, 0);
        Place(ProblemSelector, 0, 1);
        Place(TimingSelector, 0, 2);

        ConfigureGrid(ReferenceContextRow, 4, 1);
        Place(LocationSelectorContainer, 0, 0);
        Place(MissionSelectorContainer, 0, 1);
        Place(ItemSelectorContainer, 0, 2);
        Place(EntitySelectorContainer, 0, 3);
    }

    private static void ConfigureGrid(Microsoft.UI.Xaml.Controls.Grid grid, int columns, int rows)
    {
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < rows; index++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
    }

    private static void Place(FrameworkElement element, int row, int column, int columnSpan = 1)
    {
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        Microsoft.UI.Xaml.Controls.Grid.SetColumnSpan(element, columnSpan);
    }

    private void PopulateDraftAttachments(AssistantSessionSnapshot snapshot)
    {
        DraftAttachmentList.Items.Clear();
        foreach (var attachment in snapshot.TicketDraft.Attachments)
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
                Tag = attachment.ResourceReference,
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

    private void OnHistoryTaskClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string taskId }) state?.OpenTask(taskId);
        NotifyChanged();
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        SendButton.IsEnabled = false;
        try
        {
            var snapshot = state.Snapshot();
            canonicalRuntimeMatch = snapshot.TicketDraft.GameId is GameId gameId
                ? canonicalCatalogRuntimeService?.Match(
                    gameId, snapshot.TicketDraft.InstallationId, snapshot.TicketDraft.ProfileId)
                : null;
            if (PruneInvalidCanonicalSelections(snapshot)) return;
            await state.PrepareSubmissionAsync(lifetimeToken);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OperationCanceledException) { }
        NotifyChanged();
    }

    private async void OnApproveAuthorizationClicked(object sender, RoutedEventArgs e)
    {
        if (state is null) return;
        try
        {
            var snapshot = state.Snapshot();
            canonicalRuntimeMatch = snapshot.TicketDraft.GameId is GameId gameId
                ? canonicalCatalogRuntimeService?.Match(
                    gameId, snapshot.TicketDraft.InstallationId, snapshot.TicketDraft.ProfileId)
                : null;
            if (PruneInvalidCanonicalSelections(snapshot)) return;
            await state.ApproveSubmissionAsync(NotifyChanged, lifetimeToken);
        }
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

    private void OnGameOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantGameOption option, IsChecked: true }) return;
        GameSelector.Flyout?.Hide();
        state.SelectGame(option.Id);
        NotifyChanged();
    }

    private void OnProfileOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantProfileOption option, IsChecked: true }) return;
        ProfileSelector.Flyout?.Hide();
        state.SelectProfile(option.Id);
        NotifyChanged();
    }

    private void OnClassOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantClassOption option, IsChecked: true }) return;
        ClassSelector.Flyout?.Hide();
        state.SelectClass(option.Id);
        NotifyChanged();
    }

    private void OnProblemOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantProblemOption option, IsChecked: true }) return;
        ProblemSelector.Flyout?.Hide();
        state.SelectProblem(new(option.Id, option.ClassId, option.DisplayName, TicketSelectionProvenance.ExplicitUserSelection));
        NotifyChanged();
    }

    private void OnTimingOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantTimingOption option, IsChecked: true }) return;
        TimingSelector.Flyout?.Hide();
        state.SelectTiming(new(option.Id, option.ClassId, option.DisplayName, TicketSelectionProvenance.ExplicitUserSelection));
        NotifyChanged();
    }

    private void OnGoalOptionChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not RadioButton { Tag: AssistantGoalOption option, IsChecked: true }) return;
        GoalSelector.Flyout?.Hide();
        state.SelectGoal(new(option.Id, option.DisplayName, TicketSelectionProvenance.ExplicitUserSelection));
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

    private void OnConfiguredToolChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not CheckBox { Tag: UserToolConfigurationId id } checkbox) return;
        state.SetConfiguredToolSelected(id, checkbox.IsChecked == true);
        NotifyChanged();
    }

    private void OnReferenceContextChecked(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not CheckBox { Tag: TicketReferenceContext context } checkbox) return;
        state.SetReferenceContextSelected(context, checkbox.IsChecked == true);
        NotifyChanged();
    }

    private void OnOtherContextToggleChanged(object sender, RoutedEventArgs e)
    {
        if (rendering || state is null || sender is not CheckBox { Tag: TicketReferenceContextKind kind } checkbox) return;
        var textBox = GetOtherContextTextBox(kind);
        if (checkbox.IsChecked == true)
        {
            textBox.Visibility = Visibility.Visible;
            textBox.Focus(FocusState.Programmatic);
            return;
        }
        state.ReplaceOtherContext(kind, null);
        NotifyChanged();
    }

    private void OnOtherContextLostFocus(object sender, RoutedEventArgs e) => CommitOtherContext(sender as TextBox);

    private void OnOtherContextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        CommitOtherContext(sender as TextBox);
        e.Handled = true;
    }

    private void CommitOtherContext(TextBox? textBox)
    {
        if (rendering || state is null || textBox?.Tag is not string kindText ||
            !Enum.TryParse<TicketReferenceContextKind>(kindText, out var kind)) return;
        if (!pendingOtherContexts.TryGetValue(kind, out var editingContext) ||
            !IsCurrentReferenceEditingContext(editingContext)) return;
        if (string.IsNullOrWhiteSpace(textBox.Text)) return;
        state.AppendOtherContext(kind, textBox.Text);
        textBox.Text = string.Empty;
        textBox.Visibility = Visibility.Collapsed;
        pendingOtherContexts.Remove(kind);
        NotifyChanged();
    }

    private TextBox GetOtherContextTextBox(TicketReferenceContextKind kind) => kind switch
    {
        TicketReferenceContextKind.Location => LocationOtherText,
        TicketReferenceContextKind.MissionOrQuest => MissionOtherText,
        TicketReferenceContextKind.Item => ItemOtherText,
        TicketReferenceContextKind.Entity => EntityOtherText,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
    {
        if (rendering || state is null) return;
        if (state.Snapshot().Surface == AssistantSurface.Task)
        {
            var text = ComposerText.Text;
            if (string.IsNullOrWhiteSpace(text)) return;
            state.StartNewInvestigation();
            state.SetComposerText(text);
            Refresh();
            ComposerText.Focus(FocusState.Programmatic);
            ComposerText.Select(ComposerText.Text.Length, 0);
            return;
        }
        state.SetComposerText(ComposerText.Text);
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
        sourceAcquisitionStatusOverride = "Preparing exact Nexus source acquisition…";
        NotifyChanged();
        try
        {
            var progress = new Progress<NexusSourceAcquisitionProgress>(item =>
            {
                sourceAcquisitionStatusOverride = $"Downloading source {Math.Min(item.Completed + 1, item.Total)} of {item.Total}: {item.CurrentArchiveLeaf}";
                if (item.Completed >= item.Total) sourceAcquisitionStatusOverride = "Verifying completed source downloads…";
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

    private static string FormatReadiness(InvestigationTicketReadiness readiness) => readiness.Status switch
    {
        InvestigationTicketReadinessStatus.Ready => "Ticket ready",
        InvestigationTicketReadinessStatus.Invalid => "Ticket invalid",
        _ when !readiness.MissingRequiredFields.IsDefaultOrEmpty =>
            $"Required: {string.Join(", ", readiness.MissingRequiredFields)}",
        _ => "Ticket incomplete",
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
