using System.Collections.Immutable;
using Grid.App.Services;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class ToolConfigurationDialog : ContentDialog
{
    private const double StackThreshold = 720;
    private readonly UserToolManagerState state;
    private readonly UserToolScope currentContext;
    private readonly IExecutableFilePicker picker;
    private readonly IInstalledToolIdentityService identityService;
    private readonly Action configurationsChanged;
    private readonly List<ToolDraft> drafts = [];
    private readonly Dictionary<InstalledToolKnowledgeId, InstalledToolKnowledge> pendingKnowledge = [];
    private readonly HashSet<UserToolConfigurationId> managedConfigurationIds = [];
    private ToolDraft? selected;
    private bool binding;

    public ToolConfigurationDialog(UserToolManagerState state, UserToolScope currentContext, IExecutableFilePicker picker, IInstalledToolIdentityService identityService, Action configurationsChanged)
    {
        this.state = state ?? throw new ArgumentNullException(nameof(state));
        this.currentContext = currentContext ?? throw new ArgumentNullException(nameof(currentContext));
        this.picker = picker ?? throw new ArgumentNullException(nameof(picker));
        this.identityService = identityService ?? throw new ArgumentNullException(nameof(identityService));
        this.configurationsChanged = configurationsChanged ?? throw new ArgumentNullException(nameof(configurationsChanged));
        InitializeComponent();
        ScopeSelector.Items.Add("This game installation");
        if (currentContext.ProfileId is not null) ScopeSelector.Items.Add("Current profile");
        ScopeText.Text = $"Exact installation: {currentContext.GameId.Value} · {currentContext.InstallationId.Value}. New tools default to this installation and remain visible when its active profile changes.";
        ResetFromState();
    }

    private void ResetFromState(UserToolConfigurationId? preferred = null)
    {
        drafts.Clear();
        managedConfigurationIds.Clear();
        var visible = state.ForContext(currentContext, includeDisabled: true);
        drafts.AddRange(visible.Select(value => new ToolDraft(value, state.FindKnowledge(value.KnowledgeId))));
        foreach (var configuration in visible) managedConfigurationIds.Add(configuration.Id);
        ToolList.ItemsSource = null;
        ToolList.ItemsSource = drafts;
        ToolList.SelectedItem = preferred is UserToolConfigurationId id ? drafts.FirstOrDefault(value => value.Id == id) : drafts.FirstOrDefault();
        Bind(ToolList.SelectedItem as ToolDraft);
    }

    private void OnToolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (binding) return;
        CaptureEditor();
        Bind(ToolList.SelectedItem as ToolDraft);
    }

    private void OnDialogContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width > 0 && e.NewSize.Width < StackThreshold;
        EditorLayout.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 300, stacked ? GridUnitType.Star : GridUnitType.Pixel);
        EditorLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        EditorLayout.RowDefinitions[0].Height = new GridLength(stacked ? 210 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        EditorLayout.RowDefinitions[1].Height = new GridLength(stacked ? 1 : 0, stacked ? GridUnitType.Star : GridUnitType.Pixel);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(ToolEditorScroller, stacked ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(ToolEditorScroller, stacked ? 1 : 0);
    }

    private void Bind(ToolDraft? draft)
    {
        selected = draft;
        binding = true;
        try
        {
            var enabled = draft is not null;
            EditorHeading.Text = draft?.Title ?? "Select or add a tool";
            TitleBox.Text = draft?.Title ?? string.Empty;
            BinaryBox.Text = draft?.BinaryPath ?? string.Empty;
            StartInBox.Text = draft?.StartInPath ?? string.Empty;
            ArgumentsBox.Text = draft is null ? string.Empty : string.Join(Environment.NewLine, draft.Arguments);
            EnabledCheckBox.IsChecked = draft?.Enabled ?? false;
            ScopeSelector.SelectedIndex = draft is null ? -1 : draft.Scope.IsInstallationScoped ? 0 : 1;
            TitleBox.IsEnabled = BinaryBox.IsEnabled = StartInBox.IsEnabled = ArgumentsBox.IsEnabled = EnabledCheckBox.IsEnabled = RemoveButton.IsEnabled = ScopeSelector.IsEnabled = enabled;
            IdentityText.Text = FormatKnowledge(draft?.Knowledge);
        }
        finally { binding = false; }
    }

    private void CaptureEditor()
    {
        if (binding || selected is null) return;
        selected.Title = TitleBox.Text.Trim();
        selected.BinaryPath = NullIfWhiteSpace(BinaryBox.Text);
        selected.StartInPath = NullIfWhiteSpace(StartInBox.Text);
        selected.Arguments = ArgumentsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim()).Where(value => value.Length > 0).ToImmutableArray();
        selected.Enabled = EnabledCheckBox.IsChecked == true;
        selected.RefreshStatus();
        EditorHeading.Text = string.IsNullOrWhiteSpace(selected.Title) ? "Untitled tool" : selected.Title;
    }

    private async void OnAddFromFileClicked(object sender, RoutedEventArgs e) => await AddFromFileAsync();
    private async void OnBrowseBinaryClicked(object sender, RoutedEventArgs e) => await AddFromFileAsync(true);

    private async Task AddFromFileAsync(bool replaceSelected = false)
    {
        var path = await picker.PickAsync();
        if (path is null) return;
        InstalledToolKnowledge? knowledge = null;
        try
        {
            knowledge = await identityService.ResolveAsync(path);
            pendingKnowledge[knowledge.Id] = knowledge;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ShowStatus("Identity evidence unavailable", $"The launch configuration can still be saved. Exact-file observation failed ({exception.GetType().Name}).", InfoBarSeverity.Warning);
        }

        var title = knowledge?.ProductName;
        if (string.IsNullOrWhiteSpace(title)) title = Path.GetFileNameWithoutExtension(path);
        var draft = replaceSelected && selected is not null ? selected : ToolDraft.Create(currentContext);
        draft.Title = replaceSelected && !string.IsNullOrWhiteSpace(draft.Title) ? draft.Title : title!;
        draft.BinaryPath = Path.GetFullPath(path);
        draft.StartInPath = Path.GetDirectoryName(draft.BinaryPath);
        draft.Knowledge = knowledge;
        draft.KnowledgeId = knowledge?.Id;
        draft.RefreshStatus();
        if (!drafts.Contains(draft))
        {
            drafts.Add(draft);
            managedConfigurationIds.Add(draft.Id);
        }
        RefreshList(draft);
    }

    private void OnAddEmptyClicked(object sender, RoutedEventArgs e)
    {
        CaptureEditor();
        var draft = ToolDraft.Create(currentContext);
        drafts.Add(draft);
        managedConfigurationIds.Add(draft.Id);
        RefreshList(draft);
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        var index = drafts.IndexOf(selected);
        drafts.Remove(selected);
        RefreshList(drafts.Count == 0 ? null : drafts[Math.Clamp(index, 0, drafts.Count - 1)]);
    }

    private async void OnApplyClicked(object sender, RoutedEventArgs e) => await CommitAsync(false);
    private async void OnOkClicked(object sender, RoutedEventArgs e) => await CommitAsync(true);
    private void OnCancelClicked(object sender, RoutedEventArgs e) => Hide();

    private async Task CommitAsync(bool close)
    {
        CaptureEditor();
        var validation = ValidateDrafts();
        if (validation is not null) { ShowStatus("Tools not saved", validation, InfoBarSeverity.Warning); return; }

        var now = DateTimeOffset.UtcNow;
        var scoped = drafts.Select(value => value.ToConfiguration(now)).ToImmutableArray();
        var all = state.Configurations.Configurations.Where(value => !managedConfigurationIds.Contains(value.Id)).Concat(scoped).ToImmutableArray();
        var result = await state.ReplaceConfigurationsAsync(all);
        if (!result.Succeeded) { ShowStatus("Tools not saved", result.Detail, InfoBarSeverity.Error); return; }

        string? knowledgeWarning = null;
        if (pendingKnowledge.Count > 0)
        {
            var evidenceResult = await state.MergeKnowledgeAsync(pendingKnowledge.Values);
            if (!evidenceResult.Succeeded) knowledgeWarning = evidenceResult.Detail;
            else pendingKnowledge.Clear();
        }
        configurationsChanged();
        if (close) { Hide(); return; }
        ResetFromState(selected?.Id);
        ShowStatus("Tools saved", knowledgeWarning is null ? "GRID saved the launch configurations for this exact workspace." : $"Launch configurations were saved. Identity evidence was not persisted: {knowledgeWarning}", knowledgeWarning is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private string? ValidateDrafts()
    {
        if (drafts.Select(value => value.Id).Distinct().Count() != drafts.Count) return "Tool identities must be unique.";
        foreach (var draft in drafts)
        {
            if (string.IsNullOrWhiteSpace(draft.Title)) return "Every tool needs a title.";
            if (draft.Title.Length > 160) return "Tool titles cannot exceed 160 characters.";
            if (draft.BinaryPath is not null && (!Path.IsPathFullyQualified(draft.BinaryPath) || !string.Equals(Path.GetExtension(draft.BinaryPath), ".exe", StringComparison.OrdinalIgnoreCase))) return $"{draft.Title}: Binary must be an absolute .exe path.";
            if (draft.StartInPath is not null && !Path.IsPathFullyQualified(draft.StartInPath)) return $"{draft.Title}: Start in must be an absolute directory path.";
            if (draft.Arguments.Length > 128 || draft.Arguments.Any(value => value.Length > 4096 || value.Any(char.IsControl))) return $"{draft.Title}: Arguments exceed GRID's bounded literal-argument contract.";
        }
        return null;
    }

    private void RefreshList(ToolDraft? selection)
    {
        binding = true;
        ToolList.ItemsSource = null;
        ToolList.ItemsSource = drafts;
        ToolList.SelectedItem = selection;
        binding = false;
        Bind(selection);
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e) { if (!binding) CaptureEditor(); }
    private void OnEditorToggleChanged(object sender, RoutedEventArgs e) { if (!binding) CaptureEditor(); }
    private void OnScopeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (binding || selected is null || ScopeSelector.SelectedIndex < 0) return;
        selected.Scope = ScopeSelector.SelectedIndex == 0
            ? new(currentContext.GameId, currentContext.InstallationId, null)
            : new(currentContext.GameId, currentContext.InstallationId, currentContext.ProfileId);
        selected.RefreshStatus();
    }
    private void OnBinaryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (binding || selected is null) return;
        var newPath = NullIfWhiteSpace(BinaryBox.Text);
        if (!StringComparer.OrdinalIgnoreCase.Equals(newPath, selected.BinaryPath))
        {
            selected.Knowledge = null;
            selected.KnowledgeId = null;
            IdentityText.Text = FormatKnowledge(null);
        }
        CaptureEditor();
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        OperationStatus.Title = title;
        OperationStatus.Message = message;
        OperationStatus.Severity = severity;
        OperationStatus.IsOpen = true;
    }

    private static string FormatKnowledge(InstalledToolKnowledge? knowledge)
    {
        if (knowledge is null) return "Unresolved. This does not prevent manual launching. Compatibility and diagnostic integration remain unavailable until provenance is sufficient.";
        var identity = knowledge.CanonicalToolId is ToolId id ? id.Value : "Canonical ToolID unresolved";
        var version = knowledge.ProductVersion ?? knowledge.FileVersion ?? "version unresolved";
        return $"{knowledge.ProductName ?? Path.GetFileName(knowledge.BinaryPath)} · {version}\n{identity} · {knowledge.SignatureStatus}\nCompatibility: unresolved (no GameID claim inferred from installation or filename).";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public sealed class ToolDraft
    {
        public ToolDraft(UserToolLaunchConfiguration configuration, InstalledToolKnowledge? knowledge)
        {
            Id = configuration.Id; Title = configuration.Title; BinaryPath = configuration.BinaryPath; StartInPath = configuration.StartInPath;
            Arguments = configuration.Arguments; Enabled = configuration.Enabled; Scope = configuration.Scope; CreatedAtUtc = configuration.CreatedAtUtc;
            SourceRevision = configuration.Revision; KnowledgeId = configuration.KnowledgeId; Knowledge = knowledge; RefreshStatus();
        }

        public string Title { get; set; }
        public string Status { get; private set; } = string.Empty;
        public UserToolConfigurationId Id { get; }
        public string? BinaryPath { get; set; }
        public string? StartInPath { get; set; }
        public ImmutableArray<string> Arguments { get; set; }
        public bool Enabled { get; set; }
        public UserToolScope Scope { get; set; }
        public DateTimeOffset CreatedAtUtc { get; }
        public int SourceRevision { get; }
        public InstalledToolKnowledgeId? KnowledgeId { get; set; }
        public InstalledToolKnowledge? Knowledge { get; set; }

        public static ToolDraft Create(UserToolScope currentContext) => new(new(UserToolLaunchConfiguration.CurrentSchemaVersion, new($"user-tool.{Guid.NewGuid():N}"), "New Executable", null, null, [], true, new(currentContext.GameId, currentContext.InstallationId, null), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0), null);
        public void RefreshStatus()
        {
            var availability = !Enabled ? "Hidden" : string.IsNullOrWhiteSpace(BinaryPath) ? "Configured · binary required" : "Runnable";
            Status = $"{availability} · {(Scope.IsInstallationScoped ? "This installation" : "Current profile")}";
        }
        public UserToolLaunchConfiguration ToConfiguration(DateTimeOffset now) => new(UserToolLaunchConfiguration.CurrentSchemaVersion, Id, Title.Trim(), BinaryPath is null ? null : Path.GetFullPath(BinaryPath), StartInPath is null ? null : Path.GetFullPath(StartInPath), Arguments, Enabled, Scope, CreatedAtUtc, now, Math.Max(1, SourceRevision + 1), KnowledgeId);
    }
}
