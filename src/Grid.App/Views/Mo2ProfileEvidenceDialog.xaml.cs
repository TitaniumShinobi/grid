using Grid.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class Mo2ProfileEvidenceDialog : ContentDialog
{
    public Mo2ProfileEvidenceDialog(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        InitializeComponent();
        BindProfile(profile);
    }

    private void BindProfile(Profile profile)
    {
        ProfileNameText.Text = profile.Name;
        var observation = profile.Observation;
        ManagerStateText.Text = observation?.ManagerState switch
        {
            ManagerProfileState.Active => "ACTIVE IN MO2 · selected independently in Grid",
            ManagerProfileState.Inactive => "Inactive in MO2 · selected independently in Grid",
            ManagerProfileState.Unknown => "MO2 active-profile state is uncertain",
            _ => "No MO2 manager-state evidence",
        };
        LocalSettingsText.Text = observation?.LocalSettingsEnabled switch
        {
            true => "Enabled · profile-local Skyrim INIs are authoritative for this observation",
            false => "Disabled · shared Skyrim settings require separate exact session authorization",
            null => "Unknown · Grid does not infer this setting from filenames",
        };
        LocalSavesText.Text = observation?.LocalSavesEnabled switch
        {
            true => "Enabled · profile save-directory presence only; save contents are not inspected",
            false => "Disabled · no profile-local save content is inferred",
            null => "Unknown · Grid does not infer this setting from directory names",
        };
        SnapshotText.Text = observation is null
            ? "No connected profile snapshot"
            : $"{observation.Status} · {observation.SnapshotFingerprint} · {observation.ObservedAtUtc:u}";

        var rows = observation?.Sources.Select(source => new SourceRow(
            source.Name,
            $"{source.Availability} · {source.ParseStatus}",
            source.WarningCount == 0
                ? "No source warning recorded"
                : $"{source.WarningCount} warning{(source.WarningCount == 1 ? string.Empty : "s")} recorded",
            "Bound to the profile snapshot fingerprint shown above")).ToArray() ?? [];
        SourcesList.ItemsSource = rows;
        NoSourcesState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record SourceRow(string Name, string Status, string Detail, string Fingerprint);
}
