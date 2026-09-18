using Grid.Core.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class LaunchPreviewDialog : ContentDialog
{
    public LaunchPreviewDialog(LaunchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();

        PreviewDisclosure.Title = preview.Target.IsBlockedForFutureExecution
            ? "BLOCKED FOR FUTURE EXECUTION · PREVIEW ONLY"
            : "PREVIEW ONLY · FUTURE APPROVAL STILL REQUIRED";
        PreviewDisclosure.Message = preview.NoExecutionDisclosure;
        PreviewDisclosure.Severity = preview.Target.IsBlockedForFutureExecution
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Informational;
        ContextText.Text = $"Game: {preview.GameName} · Installation: {preview.InstallationName ?? "Unavailable"} · Profile: {preview.ProfileName ?? "Unavailable"}";
        TargetText.Text = preview.Target.Definition.Name;
        ProvenanceText.Text = $"{preview.Target.CategoryLabel} · {preview.AdapterName} · {preview.Target.Availability}";
        SafetyGatesList.ItemsSource = preview.Target.SafetyGates;

        var command = preview.Target.Command!;
        if (command.InternalRoute is not null)
        {
            ExternalCommandPanel.Visibility = Visibility.Collapsed;
            InternalRoutePanel.Visibility = Visibility.Visible;
            InternalRouteText.Text = command.InternalRoute.ToString();
            return;
        }

        ExternalCommandPanel.Visibility = Visibility.Visible;
        InternalRoutePanel.Visibility = Visibility.Collapsed;
        ExecutableText.Text = FormatPath(command.Executable);
        WorkingDirectoryText.Text = FormatPath(command.WorkingDirectory);
        EnvironmentPolicyText.Text = command.EnvironmentPolicy?.ToString() ?? "Unavailable";
        var arguments = command.Arguments
            .Select((argument, index) => new ArgumentRow(index + 1, argument.Value))
            .ToArray();
        ArgumentsList.ItemsSource = arguments;
        ArgumentsList.Visibility = arguments.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoArgumentsText.Visibility = arguments.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatPath(Grid.Core.Models.ConfiguredPath? path) => path is null
        ? "Unavailable"
        : $"${{{path.Value.Anchor}}}\\{path.Value.RelativePath}";

    private sealed record ArgumentRow(int Index, string Value);
}
