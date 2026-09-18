using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class Mo2LaunchConfirmationDialog : ContentDialog
{
    private readonly Mo2LaunchConfirmationModel _model;

    public Mo2LaunchConfirmationDialog(Mo2LaunchConfirmationModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        BindModel();
    }

    private void BindModel()
    {
        GameText.Text = _model.Game;
        InstallationText.Text = _model.Installation;
        ProfileText.Text = _model.Profile;
        ExecutableText.Text = _model.Executable;
        AuditText.Text = _model.AuditIdentity;
        InvocationText.Text = _model.InvocationDisclosure;
        ReadinessStatus.Title = _model.ReadinessStatus;
        ReadinessStatus.Message = _model.ReadinessDetail;
        ReadinessStatus.Severity = _model.CanLaunch ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        ExplicitLaunchCheckBox.IsEnabled = _model.CanLaunch;
        AuditAcknowledgementCheckBox.Visibility = _model.RequiresAcknowledgement
            ? Visibility.Visible
            : Visibility.Collapsed;
        AuditAcknowledgementCheckBox.IsEnabled = _model.CanLaunch;
        IsPrimaryButtonEnabled = false;
    }

    private void OnConfirmationChanged(object sender, RoutedEventArgs e) =>
        IsPrimaryButtonEnabled = _model.CanLaunch &&
            ExplicitLaunchCheckBox.IsChecked == true &&
            (!_model.RequiresAcknowledgement || AuditAcknowledgementCheckBox.IsChecked == true);

    private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!_model.CanLaunch ||
            ExplicitLaunchCheckBox.IsChecked != true ||
            (_model.RequiresAcknowledgement && AuditAcknowledgementCheckBox.IsChecked != true))
        {
            args.Cancel = true;
        }
    }

    public sealed record Mo2LaunchConfirmationModel(
        string Game,
        string Installation,
        string Profile,
        string Executable,
        string AuditIdentity,
        string InvocationDisclosure,
        string ReadinessStatus,
        string ReadinessDetail,
        bool CanLaunch,
        bool RequiresAcknowledgement);
}
