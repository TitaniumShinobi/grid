using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class Mo2FidelityAuditDialog : ContentDialog
{
    private readonly FidelityAuditDialogModel _model;

    public Mo2FidelityAuditDialog(FidelityAuditDialogModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        BindModel();
    }

    public bool WarningsAcknowledged =>
        !_model.RequiresAcknowledgement || AcknowledgeWarningsCheckBox.IsChecked == true;

    private void BindModel()
    {
        ContextText.Text = _model.Context;
        SnapshotText.Text = _model.SnapshotIdentity;
        ObservedText.Text = _model.ObservedAtUtc is DateTimeOffset observedAt
            ? observedAt.ToLocalTime().ToString("F")
            : "Observation time unavailable";
        NonMutationText.Text = _model.NonMutationEvidence;
        AuditChecksList.ItemsSource = _model.Checks;
        NoChecksState.Visibility = _model.Checks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AcknowledgeWarningsCheckBox.Visibility = _model.RequiresAcknowledgement
            ? Visibility.Visible
            : Visibility.Collapsed;

        OverallStatus.Title = _model.Status;
        OverallStatus.Message = _model.Summary;
        OverallStatus.Severity = _model.BlocksLaunch
            ? InfoBarSeverity.Error
            : _model.RequiresAcknowledgement
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Success;
    }

    private void OnAcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        OverallStatus.Message = AcknowledgeWarningsCheckBox.IsChecked == true
            ? $"{_model.Summary} Warnings are acknowledged for this exact snapshot only."
            : _model.Summary;
    }

    public sealed record FidelityAuditDialogModel(
        string Context,
        string SnapshotIdentity,
        DateTimeOffset? ObservedAtUtc,
        string Status,
        string Summary,
        string NonMutationEvidence,
        IReadOnlyList<FidelityAuditCheckRow> Checks,
        bool BlocksLaunch,
        bool RequiresAcknowledgement);

    public sealed record FidelityAuditCheckRow(
        string Status,
        string Title,
        string Detail,
        string Evidence);
}
