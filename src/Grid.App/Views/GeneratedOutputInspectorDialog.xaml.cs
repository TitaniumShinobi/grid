using Grid.Core.Models;
using Grid.Mo2.Models;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class GeneratedOutputInspectorDialog : ContentDialog
{
    public GeneratedOutputInspectorDialog(
        GeneratedOutputSummary summary,
        Mo2GeneratedOutputObservation? observation = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        InitializeComponent();
        NameText.Text = summary.Name;
        StatusText.Text = $"{summary.Kind} · {summary.Availability} · {summary.EnabledState} · " +
            $"association {summary.AssociationConfidence}";
        EvidenceText.Text = observation is null || observation.Evidence.IsEmpty
            ? "No additional association evidence is available in this view."
            : string.Join(Environment.NewLine, observation.Evidence.Select(value => "• " + value));
        FingerprintText.Text = $"{summary.FingerprintStrength} · {summary.Change} · " +
            $"{summary.FileCount:N0} files · {summary.DirectoryCount:N0} directories · {summary.TotalBytes:N0} bytes · " +
            $"fingerprint {Short(summary.Fingerprint)}";
        WarningsText.Text = observation is null || observation.Issues.IsEmpty
            ? summary.WarningCount == 0 ? "No observation warnings." : $"{summary.WarningCount} warning(s); detailed evidence is unavailable."
            : string.Join(Environment.NewLine, observation.Issues.Select(issue => $"• {issue.Message}"));
    }

    private static string Short(string value) => value.Length <= 24 ? value : value[..24] + "…";
}
