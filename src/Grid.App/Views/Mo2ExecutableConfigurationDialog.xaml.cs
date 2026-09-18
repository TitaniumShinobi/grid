using Grid.Core.Models;
using Grid.Mo2.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class Mo2ExecutableConfigurationDialog : ContentDialog
{
    private readonly Mo2ObservedExecutable _executable;
    private bool _revealed;

    public Mo2ExecutableConfigurationDialog(
        Mo2ObservedExecutable executable,
        ObservedExecutableSummary summary,
        Mo2ExecutableSourceProvenance? provenance,
        Mo2IconObservation? icon)
    {
        _executable = executable ?? throw new ArgumentNullException(nameof(executable));
        ArgumentNullException.ThrowIfNull(summary);
        InitializeComponent();

        TitleText.Text = executable.Title;
        ClassificationText.Text = $"{summary.RecognizedFamily} · {summary.RecognitionConfidence} · " +
            $"{summary.Availability} · {summary.LocationTrust} · {summary.Change}";
        BinaryText.Text = executable.Binary.CanonicalPath ?? executable.Binary.ConfiguredValue ?? "Not configured";
        WorkingDirectoryText.Text = executable.WorkingDirectory.CanonicalPath ??
            executable.WorkingDirectory.ConfiguredValue ?? "Not configured";
        ArgumentsText.Text = executable.Arguments.MaskedDisplay;
        RevealArgumentsButton.IsEnabled = !executable.Arguments.IsEmpty;
        FlagsText.Text = $"Steam app ID: {executable.SteamAppId ?? "not configured"} · toolbar: {Value(executable.Toolbar)} · " +
            $"own icon: {Value(executable.OwnIcon)} · hidden: {Value(executable.Hide)} · " +
            $"minimize to tray: {Value(executable.MinimizeToSystemTray)}";
        IconStatusText.Text = executable.OwnIcon != true
            ? "MO2 does not request an owned icon for this row."
            : icon is null
                ? "Icon evidence was not read because the executable path was unavailable or outside the authorized scope."
                : $"{icon.Status} · {(icon.IcoBytes.IsEmpty ? "no icon bytes retained" : $"{icon.IcoBytes.Length:N0} bounded ICO bytes observed")}" +
                    (icon.Fingerprint is null ? string.Empty : $" · fingerprint {Short(icon.Fingerprint)}");
        ProvenanceText.Text = provenance is null
            ? $"Source row {executable.SourceIndex} · fingerprint {Short(executable.Fingerprint)}"
            : $"Source row {executable.SourceIndex} · parser {provenance.ParserVersion} · " +
                $"observed {provenance.ObservedAtUtc:u} · fingerprint {Short(provenance.ContentFingerprint)}";
        var unsupported = executable.RawFields
            .Where(field => field.Support != Mo2QtValueSupport.Supported)
            .Select(field => $"Unsupported field: {field.Key}");
        var warnings = executable.Warnings.Select(warning => warning.Message);
        WarningsText.Text = string.Join(Environment.NewLine, unsupported.Concat(warnings).Distinct(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(WarningsText.Text)) WarningsText.Text = "No unsupported fields or parser warnings were observed.";
    }

    private void OnRevealArgumentsClicked(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        ArgumentsText.Text = _revealed ? _executable.Arguments.ExactValue : _executable.Arguments.MaskedDisplay;
        RevealArgumentsButton.Content = _revealed ? "Mask inert text" : "Reveal inert text · Local";
    }

    private static string Value(bool? value) => value switch { true => "yes", false => "no", _ => "not configured" };

    private static string Short(string value) => value.Length <= 20 ? value : value[..20] + "…";
}
