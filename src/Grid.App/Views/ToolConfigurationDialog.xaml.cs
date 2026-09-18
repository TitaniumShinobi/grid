using System.Collections.Immutable;
using Grid.Core.Application;
using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class ToolConfigurationDialog : ContentDialog
{
    private const double StackThreshold = 720;
    private readonly Action _catalogChanged;
    private readonly LaunchTargetSelectionState _state;
    private ResolvedLaunchTarget? _selectedTarget;
    private bool _suppressSelection;

    public ToolConfigurationDialog(LaunchTargetSelectionState state, Action catalogChanged)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _catalogChanged = catalogChanged ?? throw new ArgumentNullException(nameof(catalogChanged));
        InitializeComponent();

        ExecutableAnchorSelector.ItemsSource = Enum.GetValues<ConfiguredPathAnchor>();
        WorkingAnchorSelector.ItemsSource = Enum.GetValues<ConfiguredPathAnchor>();
        EnvironmentPolicySelector.ItemsSource = Enum.GetValues<EnvironmentPolicy>();
        RefreshTargets(_state.SelectedTargetId);
    }

    private void RefreshTargets(LaunchTargetId? selectedTargetId)
    {
        var targets = _state.GetTargets();
        _suppressSelection = true;
        try
        {
            TargetList.ItemsSource = targets;
            TargetList.SelectedItem = selectedTargetId is LaunchTargetId targetId
                ? targets.FirstOrDefault(target => target.Definition.Id == targetId)
                : targets.FirstOrDefault();
        }
        finally
        {
            _suppressSelection = false;
        }

        BindTarget(TargetList.SelectedItem as ResolvedLaunchTarget);
    }

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection)
        {
            return;
        }

        BindTarget(TargetList.SelectedItem as ResolvedLaunchTarget);
    }

    private void BindTarget(ResolvedLaunchTarget? target)
    {
        _selectedTarget = target;
        OperationStatus.IsOpen = false;
        if (target is null)
        {
            TargetNameText.Text = "No represented targets";
            TargetMetadataText.Text = "The selected game exposes no adapter-declared tool catalog.";
            SetEditorEnabled(false);
            ApplyButton.IsEnabled = false;
            return;
        }

        TargetNameText.Text = target.Definition.Name;
        TargetMetadataText.Text = target.Tool is null
            ? $"{target.CategoryLabel} · adapters: {FormatAdapters(target.Definition.AdapterIds)}"
            : $"{target.CategoryLabel} · {target.Tool.Description} · adapters: {FormatAdapters(target.Definition.AdapterIds)}";
        AvailabilityInfo.IsOpen = target.Availability == AvailabilityState.Unavailable;
        AvailabilityInfo.Title = target.Availability == AvailabilityState.Unavailable
            ? "Target unavailable"
            : "Target available for preview only";
        AvailabilityInfo.Message = target.UnavailableReason ??
            "Configuration is represented only; no executable or directory has been probed.";

        var command = target.Command;
        var externalCommand = target.Definition.Kind != LaunchTargetKind.GridInternal && command is not null;
        SetEditorEnabled(externalCommand);
        if (externalCommand)
        {
            ExecutableAnchorSelector.SelectedItem = command!.Executable?.Anchor;
            ExecutablePathBox.Text = command.Executable?.RelativePath ?? string.Empty;
            WorkingAnchorSelector.SelectedItem = command.WorkingDirectory?.Anchor;
            WorkingDirectoryBox.Text = command.WorkingDirectory?.RelativePath ?? string.Empty;
            EnvironmentPolicySelector.SelectedItem = command.EnvironmentPolicy;
            ArgumentsBox.Text = string.Join(Environment.NewLine, command.Arguments.Select(argument => argument.Value));
        }
        else
        {
            ExecutableAnchorSelector.SelectedIndex = -1;
            ExecutablePathBox.Text = target.Definition.Kind == LaunchTargetKind.GridInternal
                ? "Not applicable · Grid internal route"
                : string.Empty;
            WorkingAnchorSelector.SelectedIndex = -1;
            WorkingDirectoryBox.Text = string.Empty;
            EnvironmentPolicySelector.SelectedItem = target.Definition.Kind == LaunchTargetKind.GridInternal
                ? EnvironmentPolicy.GridInternal
                : null;
            ArgumentsBox.Text = string.Empty;
        }

        var currentDefault = FindCurrentProfile()?.DefaultLaunchTargetId;
        DefaultTargetCheckBox.IsChecked = currentDefault == target.Definition.Id;
        DefaultTargetCheckBox.IsEnabled = FindCurrentProfile() is not null && target.CanPreview;
        ApplyButton.IsEnabled = externalCommand || DefaultTargetCheckBox.IsEnabled;
    }

    private void SetEditorEnabled(bool isEnabled)
    {
        ExecutableAnchorSelector.IsEnabled = isEnabled;
        ExecutablePathBox.IsEnabled = isEnabled;
        WorkingAnchorSelector.IsEnabled = isEnabled;
        WorkingDirectoryBox.IsEnabled = isEnabled;
        EnvironmentPolicySelector.IsEnabled = isEnabled;
        ArgumentsBox.IsEnabled = isEnabled;
    }

    private void OnDialogContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width > 0 && e.NewSize.Width < StackThreshold;
        EditorLayout.ColumnDefinitions[0].Width = new GridLength(stacked ? 1 : 310, stacked ? GridUnitType.Star : GridUnitType.Pixel);
        EditorLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        EditorLayout.RowDefinitions[0].Height = new GridLength(stacked ? 220 : 1, stacked ? GridUnitType.Pixel : GridUnitType.Star);
        EditorLayout.RowDefinitions[1].Height = new GridLength(stacked ? 1 : 0, stacked ? GridUnitType.Star : GridUnitType.Pixel);
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(TargetEditorScroller, stacked ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(TargetEditorScroller, stacked ? 1 : 0);
    }

    private void OnApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedTarget is null)
        {
            return;
        }

        var targetId = _selectedTarget.Definition.Id;
        if (_selectedTarget.Definition.Kind != LaunchTargetKind.GridInternal)
        {
            var arguments = ArgumentsBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Select(value => new CommandArgument(value))
                .ToImmutableArray();
            if (ExecutableAnchorSelector.SelectedItem is not ConfiguredPathAnchor executableAnchor ||
                WorkingAnchorSelector.SelectedItem is not ConfiguredPathAnchor workingAnchor ||
                EnvironmentPolicySelector.SelectedItem is not EnvironmentPolicy environmentPolicy)
            {
                ShowResult(LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.InvalidExecutablePath));
                return;
            }

            var result = _state.SaveConfiguration(
                targetId,
                new LaunchConfigurationDraft(
                    executableAnchor,
                    ExecutablePathBox.Text,
                    workingAnchor,
                    WorkingDirectoryBox.Text,
                    environmentPolicy,
                    arguments));
            if (!result.Succeeded)
            {
                ShowResult(result);
                return;
            }
        }

        if (DefaultTargetCheckBox.IsChecked == true)
        {
            var defaultResult = _state.SetProfileDefault(targetId);
            if (!defaultResult.Succeeded)
            {
                ShowResult(defaultResult);
                return;
            }
        }

        OperationStatus.Title = "Mock configuration applied";
        OperationStatus.Message = "Only the in-memory catalog changed. No path was probed and no external state changed.";
        OperationStatus.Severity = InfoBarSeverity.Success;
        OperationStatus.IsOpen = true;
        _catalogChanged();
        RefreshTargets(targetId);
    }

    private void ShowResult(LaunchConfigurationResult result)
    {
        OperationStatus.Title = "Mock configuration not applied";
        OperationStatus.Message = result.Failure switch
        {
            LaunchConfigurationFailure.ContextUnavailable => "An available mock installation/profile context is required.",
            LaunchConfigurationFailure.TargetNotFound => "The target no longer exists in the current adapter catalog.",
            LaunchConfigurationFailure.TargetUnavailable => "Unavailable targets cannot be configured or made default by a mock path edit.",
            LaunchConfigurationFailure.NotConfigurable => "Grid-internal routes have no external executable configuration.",
            LaunchConfigurationFailure.InvalidExecutablePath => "Enter a safe relative executable path without a root, control character, or traversal segment.",
            LaunchConfigurationFailure.InvalidWorkingDirectory => "Enter a safe relative working directory without a root, control character, or traversal segment.",
            LaunchConfigurationFailure.InvalidEnvironmentPolicy => "Select a recognized typed environment policy.",
            LaunchConfigurationFailure.TooManyArguments => $"Targets support at most {LaunchTargetSelectionState.MaximumArgumentCount} literal arguments.",
            LaunchConfigurationFailure.InvalidArgument => $"Each argument must be non-empty, contain no control characters, and be at most {LaunchTargetSelectionState.MaximumArgumentLength} characters.",
            _ => "The mock update was rejected without changing state.",
        };
        OperationStatus.Severity = InfoBarSeverity.Warning;
        OperationStatus.IsOpen = true;
    }

    private Profile? FindCurrentProfile()
    {
        var selection = _state.Shell.CurrentSelection;
        var installation = selection.GameId is GameId gameId &&
            selection.InstallationId is InstallationId installationId
                ? _state.Catalog.Games
                    .FirstOrDefault(game => game.Id == gameId)?
                    .Installations.FirstOrDefault(candidate => candidate.Id == installationId)
                : null;
        return installation is not null && selection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            : null;
    }

    private string FormatAdapters(ImmutableArray<GameAdapterId> adapterIds)
    {
        var game = _state.Shell.CurrentSelection.GameId is GameId gameId
            ? _state.Catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId)
            : null;
        return string.Join(", ", adapterIds.Select(adapterId =>
            game?.Adapters.FirstOrDefault(adapter => adapter.Id == adapterId)?.Name ?? adapterId.Value));
    }
}
