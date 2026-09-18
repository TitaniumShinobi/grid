using System.Collections.Immutable;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Grid.App.Views;

public sealed partial class ExistingMo2ConnectionDialog : ContentDialog
{
    private const int MaximumDisplayNameLength = 80;

    private readonly IMo2InstallationValidator _validator;
    private readonly Mo2OnboardingCoordinator _onboardingCoordinator;
    private readonly IInstallationPathPicker _pathPicker;
    private readonly Mo2DiscoveryOptions _discoveryOptions;
    private readonly GameAdapterId _adapterId;
    private CancellationTokenSource? _operationCancellation;
    private string? _manualApplicationPath;
    private string? _manualInstancePath;
    private Mo2DiscoveryCandidate? _selectedCandidate;
    private Mo2ValidationRequest? _validatedRequest;
    private Mo2InstallationValidation? _validation;
    private bool _isBusy;
    private int _wizardStep = 1;
    private int _candidateCount;

    public ExistingMo2ConnectionDialog(
        IMo2InstallationValidator validator,
        Mo2OnboardingCoordinator onboardingCoordinator,
        IInstallationPathPicker pathPicker,
        Mo2DiscoveryOptions discoveryOptions,
        GameAdapterId adapterId)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _onboardingCoordinator = onboardingCoordinator ?? throw new ArgumentNullException(nameof(onboardingCoordinator));
        _pathPicker = pathPicker ?? throw new ArgumentNullException(nameof(pathPicker));
        _discoveryOptions = discoveryOptions ?? throw new ArgumentNullException(nameof(discoveryOptions));
        _adapterId = adapterId;

        InitializeComponent();
        Closed += OnDialogClosed;
        RenderWizardStep();
    }

    public Mo2InstallationReference? ConnectedReference { get; private set; }

    private async void OnDetectClicked(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("Detecting bounded MO2 locations", async cancellationToken =>
        {
            var result = await _onboardingCoordinator.DetectAsync(_discoveryOptions, cancellationToken);
            _manualApplicationPath = null;
            _manualInstancePath = null;
            BindCandidates(result.Candidates);
            if (result.Candidates.IsEmpty)
            {
                StepDetectResult.Visibility = Visibility.Collapsed;
                ShowStatus(
                    "No installations detected",
                    FormatIssues(result.Issues, "Grid found no likely MO2 installations in its bounded discovery locations. Browse to an installation you trust."),
                    InfoBarSeverity.Warning);
            }
            else
            {
                OperationStatus.IsOpen = false;
                StepDetectResultText.Text = $"{result.Candidates.Length} installation{(result.Candidates.Length == 1 ? string.Empty : "s")} found";
                StepDetectResult.Visibility = Visibility.Visible;

                if (!result.Issues.IsEmpty)
                {
                    ToolTipService.SetToolTip(
                        StepDetectResult,
                        FormatIssues(result.Issues, "Detection completed with advisory evidence."));
                }
            }

            NextButton.Focus(FocusState.Programmatic);
        });
    }

    private async void OnBrowseExecutableClicked(object sender, RoutedEventArgs e)
    {
        await BrowseAsync(
            "Choosing ModOrganizer executable",
            _pathPicker.PickExecutableAsync,
            isExecutable: true);
    }

    private async void OnBrowseDirectoryClicked(object sender, RoutedEventArgs e)
    {
        await BrowseAsync(
            "Choosing MO2 directory",
            _pathPicker.PickDirectoryAsync,
            isExecutable: false);
    }

    private async Task BrowseAsync(
        string status,
        Func<CancellationToken, Task<string?>> browse,
        bool isExecutable)
    {
        await RunOperationAsync(status, async cancellationToken =>
        {
            var path = await browse(cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
            {
                BusyText.Text = "Selection canceled";
                return;
            }

            var displayName = isExecutable
                ? Path.GetFileName(path)
                : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            _manualApplicationPath = isExecutable
                ? path
                : _selectedCandidate?.ApplicationDirectory ?? _manualApplicationPath;
            _manualInstancePath = isExecutable
                ? _selectedCandidate?.InstancePath ?? _manualInstancePath
                : path;
            var candidate = new Mo2DiscoveryCandidate(
                "manual:selected-location",
                string.IsNullOrWhiteSpace(displayName) ? "Manually selected MO2 location" : displayName,
                _manualApplicationPath,
                _manualInstancePath,
                Mo2EvidenceKind.ManualSelection,
                isExecutable ? "Executable selected by the user" : "Directory selected by the user");

            BindCandidates([candidate]);
            CandidateList.SelectedIndex = 0;
            OperationStatus.IsOpen = false;
            StepDetectResultText.Text = "1 installation selected";
            StepDetectResult.Visibility = Visibility.Visible;
            NextButton.Focus(FocusState.Programmatic);
        });
    }

    private void BindCandidates(IEnumerable<Mo2DiscoveryCandidate> candidates)
    {
        var rows = candidates.Select(candidate => new CandidateRow(candidate)).ToArray();
        CandidateList.ItemsSource = rows;
        CandidateList.SelectedItem = null;
        _candidateCount = rows.Length;
        _selectedCandidate = null;
        if (rows.Length == 0)
        {
            StepDetectResult.Visibility = Visibility.Collapsed;
        }
        InvalidateValidation();
        NoCandidatesState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigationState();
    }

    private void OnCandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCandidate = (CandidateList.SelectedItem as CandidateRow)?.Candidate;
        if (_selectedCandidate is not null)
        {
            _manualApplicationPath = _selectedCandidate.ApplicationDirectory;
            _manualInstancePath = _selectedCandidate.InstancePath;
        }
        InvalidateValidation();
        UpdateNavigationState();
        if (_selectedCandidate is not null && string.IsNullOrWhiteSpace(DisplayNameBox.Text))
        {
            DisplayNameBox.Text = _selectedCandidate.DisplayName;
        }
        UpdateNavigationState();
    }

    private async void OnValidateClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null)
        {
            return;
        }

        await ValidateRequestAsync(
            CreateValidationRequest(_selectedCandidate),
            "Validating selected MO2 location");
    }

    private async void OnAuthorizePathsClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCandidate is null || _validation is null)
        {
            return;
        }

        var exactPaths = _validation.Paths
            .Where(path => path.State == Mo2PathState.AuthorizationRequired && path.CanonicalPath is not null)
            .Select(path => path.CanonicalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (exactPaths.IsEmpty)
        {
            return;
        }

        await ValidateRequestAsync(
            CreateValidationRequest(_selectedCandidate) with { AuthorizedConfiguredPaths = exactPaths },
            "Checking the explicitly authorized local paths");
    }

    private async Task ValidateRequestAsync(Mo2ValidationRequest request, string busyStatus)
    {
        await RunOperationAsync(busyStatus, async cancellationToken =>
        {
            var validation = await _validator.ValidateAsync(request, cancellationToken);
            _validation = validation;
            _validatedRequest = request;
            BindValidation(validation);
            UpdateNavigationState();

            var severity = validation.Status == Mo2ValidationStatus.Valid
                ? InfoBarSeverity.Success
                : validation.Status is Mo2ValidationStatus.Inaccessible
                    ? InfoBarSeverity.Error
                    : InfoBarSeverity.Warning;
            if (validation.CanConnect)
            {
                OperationStatus.IsOpen = false;
            }
            else
            {
                ShowStatus(
                    "Validation did not pass",
                    FormatIssues(validation.Issues, "Resolve the reported evidence before connecting."),
                    severity);
            }

            var authorizationOnly = HasAuthorizationOnlyBlocker(validation);
            (authorizationOnly ? AuthorizePathsButton : NextButton).Focus(FocusState.Programmatic);
        });
    }

    private void BindValidation(Mo2InstallationValidation validation)
    {
        ValidationReview.Visibility = Visibility.Visible;
        ClassificationText.Text = validation.InstanceKind is null
            ? validation.SelectionKind.ToString()
            : $"{validation.SelectionKind} · {validation.InstanceKind} instance";
        ApplicationPathText.Text = FormatPath(validation.ExecutablePath ?? validation.ApplicationDirectory);
        InstancePathText.Text = FormatPath(validation.InstanceDirectory ?? validation.BaseDirectory);
        GamePathText.Text = FormatPath(validation.GameDirectory);
        ToolTipService.SetToolTip(ApplicationPathText, validation.ExecutablePath ?? validation.ApplicationDirectory);
        ToolTipService.SetToolTip(InstancePathText, validation.InstanceDirectory ?? validation.BaseDirectory);
        ToolTipService.SetToolTip(GamePathText, validation.GameDirectory);
        DirectoryEvidenceText.Text = validation.Paths.IsEmpty
            ? "No directory observations returned"
            : string.Join(
                Environment.NewLine,
                validation.Paths.Select(path =>
                    $"{path.Label}: {path.State} · {FormatPath(path.CanonicalPath)}"));
        ValidationResultText.Text = validation.CanConnect
            ? "VALID · READY TO CONNECT READ ONLY"
            : $"{validation.Status.ToString().ToUpperInvariant()} · CONNECTION BLOCKED";
        FinalInstallationText.Text = $"{FormatPath(validation.InstanceDirectory ?? validation.BaseDirectory)} · {FormatPath(validation.GameDirectory)}";
        AuthorizePathsButton.Visibility = HasAuthorizationOnlyBlocker(validation)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task ConnectAsync()
    {
        if (_selectedCandidate is null || _validation?.CanConnect != true)
        {
            return;
        }

        var displayName = DisplayNameBox.Text.Trim();
        if (!IsDisplayNameValid(displayName))
        {
            ShowStatus(
                "Display name required",
                $"Enter a name from 1 to {MaximumDisplayNameLength} characters without control characters.",
                InfoBarSeverity.Warning);
            DisplayNameBox.Focus(FocusState.Programmatic);
            return;
        }

        if (_validatedRequest is null)
        {
            return;
        }

        var request = new Mo2ConnectionRequest(
            _validatedRequest,
            _adapterId,
            displayName);
        await RunOperationAsync("Persisting read-only connection reference", async cancellationToken =>
        {
            var onboarding = await _onboardingCoordinator.ConnectAsync(request, cancellationToken);
            if (onboarding.Reference is null || !onboarding.ConnectionPersisted)
            {
                _validation = onboarding.Validation ?? _validation;
                if (_validation is not null)
                {
                    BindValidation(_validation);
                }
                ShowStatus(
                    "Connection not saved",
                    onboarding.Detail,
                    InfoBarSeverity.Error);
                return;
            }

            ConnectedReference = onboarding.Reference;
            var authorizationDetail = onboarding.RequiredAuthorizations.IsEmpty
                ? "Continue in the game workspace and select an observed profile."
                : $"Use Manage game to authorize {onboarding.RequiredAuthorizations.Length} exact external root{(onboarding.RequiredAuthorizations.Length == 1 ? string.Empty : "s")}, then select a profile in the game workspace.";
            ConnectionNextStepText.Text = authorizationDetail + " Grid selection never changes MO2's active profile; fidelity review is required before launch.";
            ShowStatus(
                "Connected read only",
                $"Grid saved a minimal reconnect reference. {authorizationDetail} The external MO2 installation remains in place and unchanged.",
                InfoBarSeverity.Success);
            Hide();
        });
    }

    private void OnDisplayNameChanged(object sender, TextChangedEventArgs e)
    {
        UpdateNavigationState();
    }

    private async Task RunOperationAsync(
        string busyStatus,
        Func<CancellationToken, Task> operation)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        SetBusy(true, busyStatus);
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
            BusyText.Text = "Canceled";
        }
        catch (Exception)
        {
            ShowStatus(
                "Read-only operation could not complete",
                "Grid could not complete the request. The selected path was not logged, no reference was saved, and no external files were changed.",
                InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false, BusyText.Text == busyStatus ? "Ready" : BusyText.Text);
        }
    }

    private void SetBusy(bool isBusy, string status)
    {
        _isBusy = isBusy;
        BusyIndicator.IsActive = isBusy;
        BusyText.Text = status;
        OperationProgress.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        DetectButton.IsEnabled = !isBusy;
        BrowseExecutableButton.IsEnabled = !isBusy;
        BrowseDirectoryButton.IsEnabled = !isBusy;
        CandidateList.IsEnabled = !isBusy;
        AuthorizePathsButton.IsEnabled = !isBusy && _validation is not null;
        CancelWizardButton.IsEnabled = !isBusy;
        BackButton.IsEnabled = !isBusy;
        UpdateNavigationState();
    }

    private void OnCancelOperationClicked(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is { IsCancellationRequested: false })
        {
            BusyText.Text = "Canceling safely";
            _operationCancellation.Cancel();
        }
    }

    private void InvalidateValidation()
    {
        _validation = null;
        _validatedRequest = null;
        ValidationReview.Visibility = Visibility.Collapsed;
        AuthorizePathsButton.Visibility = Visibility.Collapsed;
        UpdateNavigationState();
    }

    private async void OnNextClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        switch (_wizardStep)
        {
            case 1:
                if (_candidateCount == 0)
                {
                    ShowStatus(
                        "Find an installation first",
                        "Detect a likely MO2 installation or browse to one before continuing.",
                        InfoBarSeverity.Warning);
                    return;
                }

                SetWizardStep(2);
                CandidateList.Focus(FocusState.Programmatic);
                break;

            case 2:
                if (_selectedCandidate is null)
                {
                    ShowStatus(
                        "Select an installation",
                        "Choose one candidate before continuing.",
                        InfoBarSeverity.Warning);
                    return;
                }

                SetWizardStep(3);
                await ValidateRequestAsync(
                    CreateValidationRequest(_selectedCandidate),
                    "Validating selected MO2 location");
                break;

            case 3:
                if (_validation?.CanConnect != true)
                {
                    ShowStatus(
                        "Validation required",
                        HasAuthorizationOnlyBlocker(_validation!)
                            ? "Authorize the listed read-only paths and revalidate before continuing."
                            : "This installation must pass validation before continuing.",
                        InfoBarSeverity.Warning);
                    return;
                }

                SetWizardStep(4);
                DisplayNameBox.Focus(FocusState.Programmatic);
                break;

            case 4:
                await ConnectAsync();
                break;
        }
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _wizardStep <= 1)
        {
            return;
        }

        SetWizardStep(_wizardStep - 1);
    }

    private void OnCancelWizardClicked(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        Hide();
    }

    private void SetWizardStep(int step)
    {
        _wizardStep = Math.Clamp(step, 1, 4);
        OperationStatus.IsOpen = false;
        RenderWizardStep();
    }

    private void RenderWizardStep()
    {
        StepDetect.Visibility = _wizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepSelect.Visibility = _wizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepValidate.Visibility = _wizardStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepConnect.Visibility = _wizardStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _wizardStep > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _wizardStep == 4 ? "Connect" : "Next";
        AutomationProperties.SetName(
            NextButton,
            _wizardStep == 4
                ? "Connect validated MO2 installation read only"
                : "Next in MO2 onboarding");

        UpdateNavigationState();
    }

    private void UpdateNavigationState()
    {
        if (NextButton is null)
        {
            return;
        }

        NextButton.IsEnabled = !_isBusy && _wizardStep switch
        {
            1 => _candidateCount > 0,
            2 => _selectedCandidate is not null,
            3 => _validation?.CanConnect == true,
            4 => _validation?.CanConnect == true && IsDisplayNameValid(DisplayNameBox.Text),
            _ => false,
        };
    }

    private Mo2ValidationRequest CreateValidationRequest(Mo2DiscoveryCandidate candidate) =>
        new(candidate.ApplicationDirectory, candidate.InstancePath, _discoveryOptions.ExpectedGameId);

    private static bool IsDisplayNameValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length <= MaximumDisplayNameLength &&
        !value.Any(char.IsControl);

    private static string FormatPath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "Not resolved" : value;

    private static bool HasAuthorizationOnlyBlocker(Mo2InstallationValidation validation)
    {
        var hasAuthorization = validation.Paths.Any(path => path.State == Mo2PathState.AuthorizationRequired);
        return hasAuthorization && validation.Issues
            .Where(issue => issue.Severity == Mo2IssueSeverity.Error)
            .All(issue => issue.Code == "mo2.path.authorization_required");
    }

    private static string FormatIssues(IEnumerable<Mo2ValidationIssue> issues, string fallback)
    {
        var messages = issues.Select(issue => issue.Message).Distinct(StringComparer.Ordinal).ToArray();
        return messages.Length == 0 ? fallback : string.Join(" ", messages);
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        OperationStatus.Title = title;
        OperationStatus.Message = message;
        OperationStatus.Severity = severity;
        OperationStatus.IsOpen = true;
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
    }

    private sealed class CandidateRow
    {
        public CandidateRow(Mo2DiscoveryCandidate candidate)
        {
            Candidate = candidate;
            Name = candidate.DisplayName;
            Location = candidate.ApplicationDirectory ?? candidate.InstancePath ?? "No location returned";
            Source = candidate.EvidenceDescription;
        }

        public Mo2DiscoveryCandidate Candidate { get; }

        public string Name { get; }

        public string Location { get; }

        public string Source { get; }
    }
}
