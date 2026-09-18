using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public sealed class AssistantSessionState
{
    public const double DefaultPanelWidth = 400;
    public const double MinimumPanelWidth = 320;
    public const double MaximumPanelWidth = 560;

    private readonly GridCatalogSnapshot catalog;
    private readonly ImmutableArray<AssistantClassOption> classes;
    private readonly ImmutableArray<AssistantToolOption> registeredTools;
    private readonly IAssistantRequestExecutionService? executionService;
    private readonly TimeProvider timeProvider;
    private readonly HashSet<ModId> selectedMods = [];
    private readonly HashSet<ToolId> selectedTools = [];
    private string? selectedClassId;
    private string? selectedCapabilityId;
    private bool inferredClassSelection;
    private bool inferredCapabilitySelection;
    private string plainText = string.Empty;
    private string expectedBehavior = string.Empty;
    private string reproductionOrLocation = string.Empty;
    private string desiredOutcome = string.Empty;
    private ImmutableArray<AssistantAttachmentDraft> attachments = [];
    private AssistantAuthorizationScope authorizationScope = AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly;
    private GameId? selectedGameId;
    private InstallationId? selectedInstallationId;
    private ProfileId? selectedProfileId;
    private ImmutableArray<AssistantTaskRecord> tasks = [];
    private AssistantAuthorizationReview? pendingAuthorization;
    private AssistantCaseAction? pendingAction;
    private AssistantTaskRecord? pendingActionPredecessor;
    private AssistantTaskRecord? activeTask;
    private string? executionError;

    public AssistantSessionState(
        ApplicationContextSnapshot context,
        GridCatalogSnapshot catalog,
        IEnumerable<AssistantClassOption> classes,
        IEnumerable<AssistantToolOption>? registeredTools = null,
        IAssistantRequestExecutionService? executionService = null,
        TimeProvider? timeProvider = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.executionService = executionService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        ArgumentNullException.ThrowIfNull(classes);
        this.classes = classes.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        this.registeredTools = (registeredTools ?? []).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (this.classes.Any(option => string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.DisplayName) || string.IsNullOrWhiteSpace(option.IconId)) ||
            this.classes.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != this.classes.Length)
            throw new ArgumentException("Assistant Class options must be complete and have unique stable identities.", nameof(classes));
        PrefillFromContext(context);
    }

    public bool IsExpanded { get; private set; }
    public bool IsFullScreen { get; private set; }
    public bool IsFormVisible { get; private set; }
    public AssistantSurface Surface { get; private set; } = AssistantSurface.Home;
    public AssistantIntakeScope IntakeScope { get; private set; } = AssistantIntakeScope.Game;
    public double RequestedPanelWidth { get; private set; } = DefaultPanelWidth;
    public AssistantOperatingMode Mode { get; private set; } = AssistantOperatingMode.Ask;
    public AssistantProviderAvailability ProviderAvailability { get; } = AssistantProviderAvailability.NotConfigured;
    public AssistantLifecycleStage LifecycleStage { get; private set; } = AssistantLifecycleStage.Idle;
    public ApplicationContextSnapshot Context { get; private set; }
    public bool HasExecutionService => executionService?.IsAvailable == true;

    public void Toggle() => IsExpanded = !IsExpanded;
    public void Expand() => IsExpanded = true;
    public void Collapse()
    {
        IsExpanded = false;
        IsFullScreen = false;
    }
    public void ToggleFullScreen() { IsFullScreen = !IsFullScreen; if (IsFullScreen) IsExpanded = true; }
    public void ShowHome() => Surface = AssistantSurface.Home;
    public void ShowHistory() { Surface = AssistantSurface.History; IsFormVisible = false; }

    public void OpenTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        var selected = tasks.FirstOrDefault(task => task.Summary.Id == taskId)
            ?? throw new ArgumentException("The requested task is not available in local history.", nameof(taskId));
        activeTask = selected with { ActionStates = GetActionStates(selected) };
        pendingAuthorization = null;
        executionError = null;
        Surface = AssistantSurface.Task;
        IsFormVisible = false;
    }

    public AssistantTaskRecord? GetActionableRepairTask(InstallationId? installationId, ProfileId? profileId)
    {
        if (installationId is null || profileId is null) return null;

        return tasks
            .Where(task =>
                task.RepairAvailability is
                {
                    HasExactSpecification: true,
                    InstallationId: InstallationId taskInstallationId,
                    ProfileId: ProfileId taskProfileId,
                } &&
                taskInstallationId == installationId.Value &&
                taskProfileId == profileId.Value &&
                !string.Equals(task.TerminalState, "InProgress", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(task.TerminalState, "AwaitingApproval", StringComparison.OrdinalIgnoreCase) &&
                GetActionStates(task).Any(state => state.Action == AssistantCaseAction.ReviewRepair && state.IsEnabled))
            .OrderByDescending(task => task.Summary.CreatedAtUtc)
            .ThenByDescending(task => task.Summary.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public void StartNewChat()
    {
        Surface = AssistantSurface.Home;
        IsFormVisible = false;
        IntakeScope = AssistantIntakeScope.Game;
        selectedMods.Clear();
        selectedTools.Clear();
        selectedClassId = null;
        selectedCapabilityId = null;
        inferredClassSelection = false;
        inferredCapabilitySelection = false;
        plainText = string.Empty;
        expectedBehavior = string.Empty;
        reproductionOrLocation = string.Empty;
        desiredOutcome = string.Empty;
        attachments = [];
        authorizationScope = AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly;
        selectedGameId = null;
        selectedInstallationId = null;
        selectedProfileId = null;
        PrefillFromContext(Context);
        pendingAuthorization = null;
        activeTask = null;
        executionError = null;
        LifecycleStage = AssistantLifecycleStage.Idle;
    }

    public void StartNewInvestigation() => StartNewChat();

    public void ToggleForm()
    {
        if (Surface != AssistantSurface.Home) { Surface = AssistantSurface.Home; IsFormVisible = true; return; }
        IsFormVisible = !IsFormVisible;
    }

    public void SelectIntakeScope(AssistantIntakeScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        IntakeScope = scope;
    }

    public void SelectGame(GameId? gameId)
    {
        if (gameId is not null && !catalog.Games.Any(game => game.Id == gameId.Value))
            throw new ArgumentException("The selected game is not present in the catalog.", nameof(gameId));
        if (selectedGameId == gameId) return;
        selectedGameId = gameId;
        selectedInstallationId = null;
        selectedProfileId = null;
        selectedMods.Clear();
        selectedTools.Clear();
        PrefillInstallationAndProfile(Context);
    }

    public void SelectInstallation(InstallationId? installationId)
    {
        var installations = GetInstallationOptions();
        if (installationId is not null && !installations.Any(item => item.Id == installationId.Value))
            throw new ArgumentException("The selected installation is not present for the selected game.", nameof(installationId));
        if (selectedInstallationId == installationId) return;
        selectedInstallationId = installationId;
        selectedProfileId = null;
        selectedMods.Clear();
        selectedTools.Clear();
        if (installationId == Context.InstallationId && Context.ProfileId is not null)
            selectedProfileId = Context.ProfileId;
    }

    public void SelectProfile(ProfileId? profileId)
    {
        if (profileId is not null && !GetProfileOptions().Any(item => item.Id == profileId.Value))
            throw new ArgumentException("The selected profile is not present for the selected installation.", nameof(profileId));
        if (selectedProfileId == profileId) return;
        selectedProfileId = profileId;
        selectedMods.Clear();
        if (profileId == Context.ProfileId)
            foreach (var modId in Context.ModIds) selectedMods.Add(modId);
    }

    public void SelectClass(string? classId)
    {
        if (classId is not null && !classes.Any(option => option.Id == classId))
            throw new ArgumentException("The selected Class is not registered in the Grid Class catalog.", nameof(classId));
        if (string.Equals(selectedClassId, classId, StringComparison.Ordinal)) return;
        selectedCapabilityId = null;
        selectedClassId = classId;
        inferredClassSelection = false;
        inferredCapabilitySelection = false;
    }

    public void SelectCapability(string? capabilityId)
    {
        var selectedClass = classes.FirstOrDefault(option => option.Id == selectedClassId);
        var options = selectedClass?.GameplayCapabilities.IsDefault == false
            ? selectedClass.GameplayCapabilities
            : [];
        if (capabilityId is not null && !options.Any(option => option.Id == capabilityId))
            throw new ArgumentException("The selected gameplay capability is not registered for this Class.", nameof(capabilityId));
        if (string.Equals(selectedCapabilityId, capabilityId, StringComparison.Ordinal)) return;
        selectedCapabilityId = capabilityId;
        inferredCapabilitySelection = false;
    }

    public void SetPlainText(string value) => SetProblem(value);

    public void SetProblem(string value)
    {
        plainText = value ?? string.Empty;
        InferGameplayCapabilityFromProblem();
    }

    private void InferGameplayCapabilityFromProblem()
    {
        if (inferredCapabilitySelection) selectedCapabilityId = null;
        if (inferredClassSelection) selectedClassId = null;
        inferredCapabilitySelection = false;
        inferredClassSelection = false;

        var normalizedProblem = NormalizeIntentText(plainText);
        if (normalizedProblem.Length == 0) return;

        var eligibleClasses = selectedClassId is null
            ? classes
            : classes.Where(option => option.Id == selectedClassId).ToImmutableArray();
        var matches = eligibleClasses.SelectMany(classOption =>
        {
            var capabilities = classOption.GameplayCapabilities.IsDefault ? [] : classOption.GameplayCapabilities;
            return capabilities
                .Where(capability => !(capability.IntentPhrases.IsDefaultOrEmpty) && capability.IntentPhrases.Any(phrase =>
                    ContainsIntentPhrase(normalizedProblem, NormalizeIntentText(phrase))))
                .Select(capability => (Class: classOption, Capability: capability));
        }).ToArray();

        if (matches.Length != 1) return;
        if (selectedClassId is null)
        {
            selectedClassId = matches[0].Class.Id;
            inferredClassSelection = true;
        }
        selectedCapabilityId = matches[0].Capability.Id;
        inferredCapabilitySelection = true;
    }

    private static bool ContainsIntentPhrase(string normalizedText, string normalizedPhrase) =>
        normalizedPhrase.Length > 0 && $" {normalizedText} ".Contains($" {normalizedPhrase} ", StringComparison.Ordinal);

    private static string NormalizeIntentText(string value) => string.Join(' ',
        new string((value ?? string.Empty).ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public void SetExpectedBehavior(string value) => expectedBehavior = value ?? string.Empty;

    public void SetReproductionOrLocation(string value) => reproductionOrLocation = value ?? string.Empty;

    public void SetDesiredOutcome(string value) => desiredOutcome = value ?? string.Empty;

    public void SetAuthorizationScope(AssistantAuthorizationScope scope)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        authorizationScope = scope;
    }

    public void SetAttachments(IEnumerable<AssistantAttachmentDraft> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Select(ValidateAttachment)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (normalized.Length > 32) throw new ArgumentOutOfRangeException(nameof(values), "At most 32 evidence attachments may be selected.");
        attachments = normalized;
    }

    public void AddAttachment(AssistantAttachmentDraft attachment) => SetAttachments(attachments.Add(attachment));

    public void RemoveAttachment(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        attachments = attachments.Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
    }

    public void SetModSelected(ModId modId, bool selected)
    {
        if (!GetModOptions().Any(mod => mod.Id == modId)) throw new ArgumentException("Mod is unavailable for this profile.", nameof(modId));
        if (selected) selectedMods.Add(modId); else selectedMods.Remove(modId);
    }

    public void SetToolSelected(ToolId toolId, bool selected)
    {
        if (!GetToolOptions().Any(tool => tool.Id == toolId)) throw new ArgumentException("Tool is unavailable for this game.", nameof(toolId));
        if (selected) selectedTools.Add(toolId); else selectedTools.Remove(toolId);
    }

    public void ApplySuggestion(string suggestionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        Surface = AssistantSurface.Home;
        IsFormVisible = true;
        IntakeScope = AssistantIntakeScope.Game;
        var preset = suggestionId switch
        {
            "installation" => new AssistantSuggestionPreset(
                "grid.class.installation-integrity",
                "Audit the complete selected profile for unresolved plugin, asset, archive, load-order, record-conflict, and installation-integrity problems.",
                "All enabled mods and plugins have complete compatible assets and dependencies, with no unresolved conflicts, missing files, or corrupt installation state.",
                "The currently selected Skyrim Special Edition MO2 profile, including its mods, plugins, downloads, overwrite, and game Data.",
                "Produce an evidence-backed diagnosis and a reversible repair plan. Do not change the profile until a separate repair is reviewed and approved."),
            "crash" => new AssistantSuggestionPreset(
                "grid.class.crash-freeze",
                "Skyrim crashes, freezes, or becomes unresponsive in the selected profile.",
                "The game remains stable and responsive during the same gameplay sequence.",
                "Capture the location, action, and approximate time of the most recent crash or freeze.",
                "Identify an evidence-backed root cause and prepare a reversible repair plan without changing the profile."),
            "assets" => new AssistantSuggestionPreset(
                "grid.class.asset-mismatch",
                "An in-game object has a missing, purple, invisible, or incorrect visual asset.",
                "The affected object renders with the intended mesh, texture, material, and appearance.",
                "Record the affected object and the in-game location where the mismatch is visible.",
                "Trace the winning asset provider and conflict chain, then prepare a reversible repair plan without changing the profile."),
            _ => throw new ArgumentOutOfRangeException(nameof(suggestionId)),
        };
        selectedClassId = preset.ClassId;
        selectedCapabilityId = null;
        if (string.IsNullOrWhiteSpace(plainText)) plainText = preset.Problem;
        if (string.IsNullOrWhiteSpace(expectedBehavior)) expectedBehavior = preset.ExpectedBehavior;
        if (string.IsNullOrWhiteSpace(reproductionOrLocation)) reproductionOrLocation = preset.ReproductionOrLocation;
        if (string.IsNullOrWhiteSpace(desiredOutcome)) desiredOutcome = preset.DesiredOutcome;
    }

    public double Resize(double requestedWidth)
    {
        RequestedPanelWidth = Math.Clamp(requestedWidth, MinimumPanelWidth, MaximumPanelWidth);
        return RequestedPanelWidth;
    }

    public void SelectMode(AssistantOperatingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        Mode = mode;
    }

    public void SetContext(ApplicationContextSnapshot context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        PrefillFromContext(context);
        selectedMods.IntersectWith(GetModOptions().Select(mod => mod.Id));
        selectedTools.IntersectWith(GetToolOptions().Select(tool => tool.Id));
    }

    public async Task LoadPersistedTasksAsync(CancellationToken cancellationToken = default)
    {
        if (executionService?.IsAvailable != true) return;
        var loaded = await executionService.LoadTasksAsync(cancellationToken);
        tasks = loaded.Select(task => task with { ActionStates = GetActionStates(task) })
            .OrderBy(task => task.Summary.CreatedAtUtc).ToImmutableArray();
        if (activeTask is not null)
            activeTask = tasks.FirstOrDefault(task => task.Summary.Id == activeTask.Summary.Id) ?? activeTask;
    }

    public async Task PrepareSubmissionAsync(CancellationToken cancellationToken = default)
    {
        if (executionService?.IsAvailable != true)
            throw new InvalidOperationException("No deterministic request execution service is available.");
        var snapshot = Snapshot();
        if (!snapshot.Draft.CanSubmit || snapshot.Draft.GameId is not GameId gameId ||
            snapshot.Draft.InstallationId is not InstallationId installationId || snapshot.Draft.ProfileId is not ProfileId profileId ||
            string.IsNullOrWhiteSpace(snapshot.Draft.ClassId))
            throw new InvalidOperationException("The structured request is not ready for deterministic submission.");

        executionError = null;
        var selectedClass = classes.Single(option => option.Id == snapshot.Draft.ClassId);
        var modOptions = GetModOptions().ToDictionary(option => option.Id);
        var toolOptions = GetToolOptions().ToDictionary(option => option.Id);
        var request = new AssistantRequestDraft(
            gameId, installationId, profileId,
            snapshot.Draft.ModIds.Select(id => new AssistantRequestModSelection(id, modOptions[id].Name, "Subject")).ToImmutableArray(),
            snapshot.Draft.ToolIds.Select(id => new AssistantRequestToolSelection(id, toolOptions[id].Name, "UserSelected")).ToImmutableArray(),
            snapshot.Draft.ClassId, selectedClass.RecipeVersion, snapshot.Draft.Problem, snapshot.Draft.DisplayTitle,
            snapshot.Draft.ExpectedBehavior, snapshot.Draft.ReproductionOrLocation, snapshot.Draft.DesiredOutcome,
            snapshot.Draft.Attachments, snapshot.Draft.AuthorizationScope, snapshot.Draft.CapabilityId);
        try
        {
            pendingAction = null;
            pendingActionPredecessor = null;
            pendingAuthorization = await executionService.PrepareAsync(request, cancellationToken);
            if (!DraftsMatch(pendingAuthorization.Request.Draft, request))
                throw new InvalidOperationException("The execution service changed the structured request while preparing authorization.");
            activeTask = CreatePendingTask(pendingAuthorization);
            LifecycleStage = AssistantLifecycleStage.AwaitingApproval;
            Surface = AssistantSurface.Task;
            IsFormVisible = false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            executionError = exception.Message;
            LifecycleStage = AssistantLifecycleStage.Failed;
            throw;
        }
    }

    public async Task ApproveSubmissionAsync(Action? progressChanged = null, CancellationToken cancellationToken = default)
    {
        if (executionService?.IsAvailable != true || pendingAuthorization is null || activeTask is null)
            throw new InvalidOperationException("There is no prepared authorization to approve.");
        if (pendingAction is null)
            EnsureContextHasNotDrifted(pendingAuthorization.Request);
        var authorization = pendingAuthorization;
        pendingAuthorization = null;
        LifecycleStage = AssistantLifecycleStage.InProgress;
        activeTask = activeTask with
        {
            Summary = activeTask.Summary with { Status = "InProgress" },
            TerminalState = "InProgress",
        };
        progressChanged?.Invoke();

        var progress = new Progress<AssistantExecutionProgress>(item =>
        {
            if (activeTask is null) return;
            activeTask = activeTask with
            {
                Transcript = activeTask.Transcript.Add(new(item.TimestampUtc, AssistantTranscriptKind.Progress, $"{item.Stage}: {item.Message}")),
            };
            progressChanged?.Invoke();
        });

        try
        {
            var grant = await executionService.GrantAsync(authorization, cancellationToken);
            var mutationExpected = pendingAction is AssistantCaseAction.ApplyRepair or AssistantCaseAction.RollBack;
            if (grant.ReviewId != authorization.ReviewId || grant.RequestId != authorization.Request.RequestId ||
                grant.SubmissionId != authorization.SubmissionId || string.IsNullOrWhiteSpace(grant.GrantId) ||
                string.IsNullOrWhiteSpace(grant.AuthorizationSecret) || grant.MutationAuthorized != mutationExpected || authorization.MutationAuthorized != mutationExpected)
                throw new InvalidOperationException("The authorization grant is not bound to the exact reviewed authority class and scope.");
            var result = await executionService.ExecuteAsync(grant, progress, cancellationToken);
            if (!string.Equals(result.TaskId, result.CaseId, StringComparison.Ordinal) && pendingAction is not null)
                throw new InvalidOperationException("Successor execution returned inconsistent task and sealed case identities.");
            if (pendingAction is null && result.TaskId != activeTask.Summary.Id)
                throw new InvalidOperationException("Execution returned a result for a different task.");
            activeTask = CompleteTask(activeTask, result);
            ReplaceTask(activeTask);
            pendingAction = null;
            pendingActionPredecessor = null;
            LifecycleStage = result.TerminalState == "EvidenceFailed" ? AssistantLifecycleStage.Failed : AssistantLifecycleStage.Completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            executionError = exception.Message;
            if (pendingAction is not null && pendingActionPredecessor is not null)
            {
                activeTask = pendingActionPredecessor with
                {
                    Transcript = pendingActionPredecessor.Transcript.Add(new(
                        timeProvider.GetUtcNow(),
                        AssistantTranscriptKind.Failure,
                        exception.Message)),
                };
                ReplaceTask(activeTask);
                pendingAction = null;
                pendingActionPredecessor = null;
            }
            else
            {
                activeTask = activeTask with
                {
                    Summary = activeTask.Summary with { Status = "EvidenceFailed" },
                    TerminalState = "EvidenceFailed",
                    Transcript = activeTask.Transcript.Add(new(timeProvider.GetUtcNow(), AssistantTranscriptKind.Failure, exception.Message)),
                };
                ReplaceTask(activeTask);
            }
            LifecycleStage = AssistantLifecycleStage.Failed;
            throw;
        }
        finally
        {
            progressChanged?.Invoke();
        }
    }

    public void RejectAuthorization()
    {
        if (pendingAuthorization is null) return;
        pendingAuthorization = null;
        activeTask = pendingActionPredecessor;
        pendingAction = null;
        pendingActionPredecessor = null;
        executionError = null;
        LifecycleStage = activeTask is null ? AssistantLifecycleStage.Idle : AssistantLifecycleStage.Completed;
        Surface = activeTask is null ? AssistantSurface.Home : AssistantSurface.Task;
    }

    public async Task ResumeActiveTaskAsync(Action? progressChanged = null, CancellationToken cancellationToken = default)
    {
        if (executionService?.IsAvailable != true || activeTask?.IsResumable != true)
            throw new InvalidOperationException("The active task has no verified resumable workspace.");
        var taskId = activeTask.Summary.Id;
        LifecycleStage = AssistantLifecycleStage.InProgress;
        var progress = new Progress<AssistantExecutionProgress>(item =>
        {
            if (activeTask is null) return;
            activeTask = activeTask with { Transcript = activeTask.Transcript.Add(new(item.TimestampUtc, AssistantTranscriptKind.Progress, $"{item.Stage}: {item.Message}")) };
            progressChanged?.Invoke();
        });
        try
        {
            var result = await executionService.ResumeAsync(taskId, progress, cancellationToken);
            activeTask = CompleteTask(activeTask, result);
            ReplaceTask(activeTask);
            LifecycleStage = result.TerminalState == "EvidenceFailed" ? AssistantLifecycleStage.Failed : AssistantLifecycleStage.Completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            executionError = exception.Message;
            LifecycleStage = AssistantLifecycleStage.Failed;
            throw;
        }
        finally { progressChanged?.Invoke(); }
    }

    public async Task ExecuteActiveActionAsync(
        AssistantCaseAction action,
        IEnumerable<AssistantAttachmentDraft>? selectedAttachments = null,
        Action? progressChanged = null,
        CancellationToken cancellationToken = default)
    {
        if (executionService?.IsAvailable != true || activeTask is null)
            throw new InvalidOperationException("There is no active persisted investigation.");
        var actionState = GetActionStates(activeTask).FirstOrDefault(item => item.Action == action);
        if (actionState is null || !actionState.IsVisible || !actionState.IsEnabled)
            throw new InvalidOperationException(actionState?.Detail ?? "The requested investigation action is unavailable.");

        var actionAttachments = selectedAttachments?.Select(ValidateAttachment).ToImmutableArray() ?? [];
        if (action != AssistantCaseAction.AttachEvidence && !actionAttachments.IsEmpty)
            throw new ArgumentException("Attachments may be supplied only to Attach Evidence.", nameof(selectedAttachments));
        if (action == AssistantCaseAction.AttachEvidence && actionAttachments.IsEmpty)
            throw new InvalidOperationException("Attach Evidence requires at least one selected file.");

        executionError = null;
        if (action is AssistantCaseAction.AttachEvidence or AssistantCaseAction.CaptureCurrentState or AssistantCaseAction.RefreshRecoverySources or AssistantCaseAction.ApplyRepair or AssistantCaseAction.RollBack)
        {
            try
            {
                pendingAction = action;
                pendingActionPredecessor = activeTask;
                pendingAuthorization = await executionService.PrepareActionAsync(
                    activeTask.Request, activeTask.Summary.Id, action, actionAttachments, cancellationToken);
                activeTask = CreatePendingTask(pendingAuthorization);
                LifecycleStage = AssistantLifecycleStage.AwaitingApproval;
                Surface = AssistantSurface.Task;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                executionError = exception.Message;
                pendingAction = null;
                pendingActionPredecessor = null;
                pendingAuthorization = null;
                LifecycleStage = AssistantLifecycleStage.Failed;
                throw;
            }
            finally { progressChanged?.Invoke(); }
            return;
        }
        LifecycleStage = actionState.RequiresApproval ? AssistantLifecycleStage.AwaitingApproval : AssistantLifecycleStage.InProgress;
        var progress = new Progress<AssistantExecutionProgress>(item =>
        {
            if (activeTask is null) return;
            activeTask = activeTask with
            {
                Transcript = activeTask.Transcript.Add(new(item.TimestampUtc, AssistantTranscriptKind.Progress, $"{item.Stage}: {item.Message}")),
            };
            progressChanged?.Invoke();
        });

        try
        {
            var predecessor = activeTask;
            var refreshed = await executionService.ExecuteActionAsync(
                predecessor.Summary.Id, action, actionAttachments, progress, cancellationToken);
            if (string.IsNullOrWhiteSpace(refreshed.Summary.Id))
                throw new InvalidOperationException("The investigation action returned an invalid persisted task identity.");
            activeTask = refreshed with { ActionStates = GetActionStates(refreshed) };
            ReplaceTask(activeTask);
            LifecycleStage = string.Equals(activeTask.TerminalState, "Failed", StringComparison.OrdinalIgnoreCase) ||
                activeTask.TerminalState.EndsWith("Failed", StringComparison.OrdinalIgnoreCase)
                    ? AssistantLifecycleStage.Failed
                    : AssistantLifecycleStage.Completed;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            executionError = exception.Message;
            LifecycleStage = AssistantLifecycleStage.Failed;
            if (activeTask is not null)
                activeTask = activeTask with
                {
                    Transcript = activeTask.Transcript.Add(new(timeProvider.GetUtcNow(), AssistantTranscriptKind.Failure, exception.Message)),
                };
            throw;
        }
        finally { progressChanged?.Invoke(); }
    }

    public AssistantSessionSnapshot Snapshot()
    {
        var selectedClass = classes.FirstOrDefault(option => option.Id == selectedClassId);
        var readiness = ResolveReadiness(selectedClass);
        var hasRequiredIntake = IntakeScope == AssistantIntakeScope.Game && selectedGameId is not null &&
            selectedInstallationId is not null && selectedProfileId is not null && selectedClass is not null &&
            !string.IsNullOrWhiteSpace(plainText);
        var draft = new AssistantDraftSnapshot(
            IntakeScope, selectedGameId,
            selectedMods.OrderBy(id => id.Value, StringComparer.Ordinal).ToImmutableArray(),
            selectedTools.OrderBy(id => id.Value, StringComparer.Ordinal).ToImmutableArray(),
            selectedClassId, plainText, CreateDisplayTitle(selectedClass), selectedClass?.IconId ?? "grid.icon.unknown",
            readiness.Status, readiness.Detail,
            CanSubmit: hasRequiredIntake &&
                readiness.Status is AssistantDraftReadiness.ReadyForDeterministicCollection or AssistantDraftReadiness.UnsupportedCoverage &&
                executionService?.IsAvailable == true && LifecycleStage == AssistantLifecycleStage.Idle,
            InstallationId: selectedInstallationId, ProfileId: selectedProfileId, Problem: plainText,
            ExpectedBehavior: expectedBehavior, ReproductionOrLocation: reproductionOrLocation, DesiredOutcome: desiredOutcome,
            Attachments: attachments, AuthorizationScope: authorizationScope, CapabilityId: selectedCapabilityId);

        return new(IsExpanded, RequestedPanelWidth, Mode, ProviderAvailability, LifecycleStage, Context,
            executionService?.IsAvailable == true
                ? "Deterministic request execution is available. Chat and AI remain unconfigured."
                : "Deterministic request intake is available. No production execution service is connected.",
            Surface, IsFormVisible, IsFullScreen, draft,
            catalog.Games.Select(game => new AssistantGameOption(game.Id, game.Name)).ToImmutableArray(),
            GetModOptions(), GetToolOptions(), classes, tasks.Select(task => task.Summary).ToImmutableArray(),
            pendingAuthorization, activeTask, executionError, GetInstallationOptions(), GetProfileOptions());
    }

    private void PrefillFromContext(ApplicationContextSnapshot context)
    {
        if (IntakeScope != AssistantIntakeScope.Game) return;
        if (selectedGameId is null && context.GameId is not null) selectedGameId = context.GameId;
        PrefillInstallationAndProfile(context);
        if (selectedGameId == context.GameId)
            foreach (var modId in context.ModIds) selectedMods.Add(modId);
    }

    private void PrefillInstallationAndProfile(ApplicationContextSnapshot context)
    {
        if (selectedGameId != context.GameId) return;
        if (selectedInstallationId is null && context.InstallationId is not null &&
            GetInstallationOptions().Any(item => item.Id == context.InstallationId.Value))
            selectedInstallationId = context.InstallationId;
        if (selectedProfileId is null && selectedInstallationId == context.InstallationId && context.ProfileId is not null &&
            GetProfileOptions().Any(item => item.Id == context.ProfileId.Value))
            selectedProfileId = context.ProfileId;
    }

    private ImmutableArray<AssistantInstallationOption> GetInstallationOptions()
    {
        var game = selectedGameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == selectedGameId.Value);
        return game?.Installations.Select(item => new AssistantInstallationOption(item.Id, game.Id, item.Name)).ToImmutableArray() ?? [];
    }

    private ImmutableArray<AssistantProfileOption> GetProfileOptions()
    {
        if (selectedGameId is null || selectedInstallationId is null) return [];
        var installation = catalog.Games.FirstOrDefault(game => game.Id == selectedGameId.Value)?.Installations
            .FirstOrDefault(item => item.Id == selectedInstallationId.Value);
        return installation?.Profiles.Select(item => new AssistantProfileOption(item.Id, installation.Id, item.Name)).ToImmutableArray() ?? [];
    }

    private ImmutableArray<AssistantModOption> GetModOptions()
    {
        var game = selectedGameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == selectedGameId.Value);
        if (game is null) return [];
        var installation = selectedInstallationId is null ? null : game.Installations.FirstOrDefault(candidate => candidate.Id == selectedInstallationId.Value);
        var profile = installation is null || selectedProfileId is null ? null : installation.Profiles.FirstOrDefault(candidate => candidate.Id == selectedProfileId.Value);
        return profile?.Mods.Where(mod => mod.Kind != ModEntryKind.Separator)
            .Select(mod => new AssistantModOption(mod.Id, mod.Name, mod.IsEnabled, mod.Kind)).ToImmutableArray() ?? [];
    }

    private ImmutableArray<AssistantToolOption> GetToolOptions()
    {
        if (!registeredTools.IsEmpty) return selectedGameId is null ? [] : registeredTools;
        var game = selectedGameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == selectedGameId.Value);
        if (game is null) return [];
        var installation = selectedInstallationId is null ? null : game.Installations.FirstOrDefault(candidate => candidate.Id == selectedInstallationId.Value);
        return game.ToolCatalog.Tools.Select(tool =>
        {
            var configuration = installation?.ToolConfigurations.FirstOrDefault(candidate => candidate.ToolId == tool.Id);
            return new AssistantToolOption(tool.Id, tool.Name, configuration?.Availability ?? AvailabilityState.Unavailable,
                configuration?.UnavailableReason ?? "Not configured for the selected installation.");
        }).ToImmutableArray();
    }

    private (AssistantDraftReadiness Status, string Detail) ResolveReadiness(AssistantClassOption? selectedClass)
    {
        if (IntakeScope == AssistantIntakeScope.Grid)
            return (AssistantDraftReadiness.RuntimeUnavailable, "GRID development intake has no repository-owned registry or execution route yet.");
        if (selectedGameId is null || selectedClass is null)
            return (AssistantDraftReadiness.Incomplete, "Choose a game and Class to complete the structured request.");
        if (selectedInstallationId is null || selectedProfileId is null)
            return (AssistantDraftReadiness.Incomplete, "Choose a connected installation and profile to establish exact collection context.");
        if (string.IsNullOrWhiteSpace(plainText))
            return (AssistantDraftReadiness.Incomplete, "Describe the problem to preserve the investigation claim.");
        var gameplayCapabilities = selectedClass.GameplayCapabilities.IsDefault ? [] : selectedClass.GameplayCapabilities;
        if (!gameplayCapabilities.IsEmpty && string.IsNullOrWhiteSpace(selectedCapabilityId))
            return (AssistantDraftReadiness.Incomplete, "Choose the exact gameplay capability to assess.");
        if (!selectedClass.IsRegistered)
            return (AssistantDraftReadiness.UnsupportedCoverage, $"{selectedClass.DisplayName} has no registered deterministic coverage; the request can be preserved with CapabilityRequired.");
        if (selectedMods.Count < selectedClass.MinimumMods)
            return (AssistantDraftReadiness.Incomplete, $"This Class recipe is missing {selectedClass.MinimumMods - selectedMods.Count} required mod selection(s); submission will preserve the gap.");
        if (selectedTools.Count < selectedClass.MinimumTools)
            return (AssistantDraftReadiness.Incomplete, $"This Class recipe is missing {selectedClass.MinimumTools - selectedTools.Count} required tool selection(s); submission will preserve the gap.");
        if (!selectedClass.AllowedToolIds.IsDefault && selectedTools.Any(id => !selectedClass.AllowedToolIds.Contains(id)))
            return (AssistantDraftReadiness.UnsupportedCoverage, "One or more selected tools are outside this Class recipe's registered coverage.");
        if (selectedTools.Any(id => !GetToolOptions().Any(tool => tool.Id == id && tool.Availability == AvailabilityState.Available)))
            return (AssistantDraftReadiness.RuntimeUnavailable, "One or more selected tools are unavailable; submission will preserve the missing runtime capability.");
        return executionService?.IsAvailable == true
            ? (AssistantDraftReadiness.ReadyForDeterministicCollection, "Structured request complete and ready for exact read-scope review.")
            : (AssistantDraftReadiness.RuntimeUnavailable, "Structured request complete; no production execution service is connected.");
    }

    private string CreateDisplayTitle(AssistantClassOption? selectedClass)
    {
        if (selectedClass is null) return "New request";
        var selectedCapability = selectedClass.GameplayCapabilities.IsDefault
            ? null
            : selectedClass.GameplayCapabilities.FirstOrDefault(option => option.Id == selectedCapabilityId);
        if (selectedCapability is not null) return string.Join(' ', TitleWords(selectedCapability.DisplayName).Take(5));
        var classWords = TitleWords(selectedClass.DisplayName).Take(4).ToArray();
        var selectedMod = GetModOptions().FirstOrDefault(option => selectedMods.Contains(option.Id));
        var selectedGame = selectedGameId is null
            ? null
            : catalog.Games.FirstOrDefault(game => game.Id == selectedGameId.Value);
        var subject = selectedMod?.Name ?? selectedGame?.Name;
        if (string.IsNullOrWhiteSpace(subject)) return string.Join(' ', classWords.Take(5));

        var subjectWords = TitleWords(subject)
            .Where(word => !classWords.Contains(word, StringComparer.OrdinalIgnoreCase))
            .Take(Math.Max(1, 5 - classWords.Length));
        return string.Join(' ', subjectWords.Concat(classWords).Take(5));
    }

    private static IEnumerable<string> TitleWords(string value) => value
        .Split([' ', '-', '_', '/', '\\'], StringSplitOptions.RemoveEmptyEntries)
        .Select(word => word.Trim(',', '.', ':', ';', '(', ')', '[', ']', '&'))
        .Where(word => word.Length > 0 && !word.Equals("The", StringComparison.OrdinalIgnoreCase));

    private AssistantTaskRecord CreatePendingTask(AssistantAuthorizationReview authorization)
    {
        var request = authorization.Request;
        var transcript = ImmutableArray.CreateBuilder<AssistantTranscriptEntry>();
        if (!string.IsNullOrWhiteSpace(request.Draft.VerbatimUserText))
            transcript.Add(new(request.SubmittedAtUtc, AssistantTranscriptKind.UserClaim, RenderRequestClaim(request.Draft)));
        transcript.Add(new(timeProvider.GetUtcNow(), AssistantTranscriptKind.Progress,
            authorization.MutationAuthorized ? "Exact reversible change prepared; awaiting one-use authorization." : "Exact read scope prepared; awaiting authorization."));
        return new(
            new(authorization.SubmissionId, request.Draft.DisplayTitle, request.SubmittedAtUtc, "AwaitingApproval"),
            request, null, "AwaitingApproval", transcript.ToImmutable(), [], null, false, [], null);
    }

    private void EnsureContextHasNotDrifted(AssistantCanonicalRequest request)
    {
        var game = catalog.Games.FirstOrDefault(item => item.Id == request.Draft.GameId);
        var installation = game?.Installations.FirstOrDefault(item => item.Id == request.Draft.InstallationId);
        var profileExists = installation?.Profiles.Any(item => item.Id == request.Draft.ProfileId) == true;
        var authorizedMods = request.Draft.Mods.Select(item => item.ModId).ToHashSet();
        var authorizedTools = request.Draft.Tools.Select(item => item.ToolId).ToHashSet();
        if (game is null || installation is null || !profileExists ||
            selectedGameId != request.Draft.GameId || selectedInstallationId != request.Draft.InstallationId || selectedProfileId != request.Draft.ProfileId ||
            selectedClassId != request.Draft.ClassId || plainText != request.Draft.VerbatimUserText ||
            expectedBehavior != request.Draft.ExpectedBehavior || reproductionOrLocation != request.Draft.ReproductionOrLocation ||
            desiredOutcome != request.Draft.DesiredOutcome || authorizationScope != request.Draft.AuthorizationScope ||
            !selectedMods.SetEquals(authorizedMods) || !selectedTools.SetEquals(authorizedTools) ||
            !attachments.SequenceEqual(Normalize(request.Draft.Attachments)))
            throw new InvalidOperationException("The selected context or investigation intake changed after authorization review. Prepare a new request.");
    }

    private AssistantTaskRecord CompleteTask(AssistantTaskRecord task, AssistantExecutionResult result)
    {
        var finding = result.Finding;
        var affected = RenderBounded(finding.AffectedMods, value => value);
        var roles = RenderBounded(finding.ModRoles, role => $"{role.Mod}: {role.Role}");
        var text = $"Affected mod(s): {affected}\nMod role(s): {roles}\nFinding: {finding.Finding}\nSolution: {finding.Solution}" +
            $"\nConfidence: {finding.Confidence}";
        if (!finding.Evidence.IsDefaultOrEmpty)
            text += $"\nEvidence: {finding.Evidence.Length:N0} sealed evidence IDs (open Review Evidence for the complete set).";
        if (finding.CapabilityRequired is { } required)
            text += $"\nCapabilityRequired: {RenderCapabilityRequired(required)}";
        var updated = task with
        {
            Summary = task.Summary with { Id = result.TaskId, Status = result.TerminalState },
            CaseId = result.CaseId,
            TerminalState = result.TerminalState,
            Finding = finding,
            Receipts = result.Receipts,
            Transcript = task.Transcript.Add(new(result.CompletedAtUtc, AssistantTranscriptKind.Result, text)),
            IsResumable = false,
            RepairAvailability = result.RepairAvailability,
        };
        return updated with
        {
            ActionStates = result.ActionStates.IsDefault ? GetActionStates(updated) : result.ActionStates,
        };
    }

    private static string RenderBounded<T>(ImmutableArray<T> values, Func<T, string> selector, int maximum = 12)
    {
        if (values.IsDefaultOrEmpty) return "UNRESOLVED";
        var visible = string.Join(", ", values.Take(maximum).Select(selector));
        return values.Length <= maximum ? visible : $"{visible}, … {values.Length - maximum:N0} more (open Review Evidence)";
    }

    private void ReplaceTask(AssistantTaskRecord task)
    {
        var index = -1;
        for (var candidateIndex = 0; candidateIndex < tasks.Length; candidateIndex++)
            if (tasks[candidateIndex].Summary.Id == task.Summary.Id) { index = candidateIndex; break; }
        tasks = index < 0 ? tasks.Add(task) : tasks.SetItem(index, task);
        tasks = tasks.OrderBy(candidate => candidate.Summary.CreatedAtUtc).ToImmutableArray();
    }

    private static bool DraftsMatch(AssistantRequestDraft left, AssistantRequestDraft right) =>
        left.GameId == right.GameId && left.InstallationId == right.InstallationId && left.ProfileId == right.ProfileId &&
        left.Mods.SequenceEqual(right.Mods) && left.Tools.SequenceEqual(right.Tools) &&
        left.ClassId == right.ClassId && left.RecipeVersion == right.RecipeVersion &&
        left.VerbatimUserText == right.VerbatimUserText && left.DisplayTitle == right.DisplayTitle &&
        left.ExpectedBehavior == right.ExpectedBehavior && left.ReproductionOrLocation == right.ReproductionOrLocation &&
        left.DesiredOutcome == right.DesiredOutcome && Normalize(left.Attachments).SequenceEqual(Normalize(right.Attachments)) &&
        left.AuthorizationScope == right.AuthorizationScope && left.CapabilityId == right.CapabilityId;

    private static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values) => values.IsDefault ? [] : values;

    private static AssistantAttachmentDraft ValidateAttachment(AssistantAttachmentDraft attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (string.IsNullOrWhiteSpace(attachment.Path) || !Path.IsPathFullyQualified(attachment.Path))
            throw new ArgumentException("Evidence attachment paths must be absolute.", nameof(attachment));
        if (string.IsNullOrWhiteSpace(attachment.OriginalName) ||
            !string.Equals(Path.GetFileName(attachment.OriginalName), attachment.OriginalName, StringComparison.Ordinal))
            throw new ArgumentException("Evidence attachments require a leaf original name.", nameof(attachment));
        if (attachment.SizeBytes is < 0)
            throw new ArgumentOutOfRangeException(nameof(attachment), "Evidence attachment size cannot be negative.");
        return attachment with { Path = Path.GetFullPath(attachment.Path) };
    }

    private static ImmutableArray<AssistantCaseActionState> GetActionStates(AssistantTaskRecord task)
    {
        if (!task.ActionStates.IsDefaultOrEmpty) return task.ActionStates;
        var persisted = !string.IsNullOrWhiteSpace(task.CaseId);
        var sealedCase = persisted && task.Finding is not null;
        var stable = sealedCase && !string.Equals(task.TerminalState, "InProgress", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(task.TerminalState, "AwaitingApproval", StringComparison.OrdinalIgnoreCase);
        var evidenceAvailable = persisted && !string.Equals(task.TerminalState, "AwaitingApproval", StringComparison.OrdinalIgnoreCase) &&
            (!task.Receipts.IsDefaultOrEmpty || task.Finding is not null);
        var exactRepair = task.RepairAvailability?.HasExactSpecification == true;
        var recoveryPlan = task.RepairAvailability?.HasRecoveryPlan == true;
        var rollback = task.RepairAvailability?.HasVerifiedRollbackReceipt == true;
        return
        [
            new(AssistantCaseAction.AttachEvidence, sealedCase, stable, true, stable ? "Review exact attachment paths before importing evidence into a sealed successor." : "A stable sealed investigation is required."),
            new(AssistantCaseAction.CaptureCurrentState, sealedCase, stable && !recoveryPlan, true,
                recoveryPlan ? "This task already has a sealed live-profile repair baseline. Continue Repair verifies only newly arrived source files; another whole-profile capture is unnecessary." : stable ? "Review exact selected-profile paths before capturing a sealed successor." : "A stable sealed investigation is required."),
            new(AssistantCaseAction.RefreshRecoverySources, recoveryPlan, stable && recoveryPlan, true,
                stable && recoveryPlan ? "Continue the selected live-profile repair by verifying only newly arrived MO2 source archives; the whole profile is not recaptured." : "A sealed component recovery plan is required."),
            new(AssistantCaseAction.Diagnose, sealedCase, stable, false, stable ? "Invoke the registered Class and capability orchestration path." : "A stable sealed investigation is required."),
            new(AssistantCaseAction.ReviewEvidence, evidenceAvailable, evidenceAvailable, false, evidenceAvailable ? "Review validated evidence from the case store." : "No validated evidence is available."),
            new(AssistantCaseAction.ReviewRepair, exactRepair || recoveryPlan, stable && (exactRepair || recoveryPlan), false,
                exactRepair ? "Review the exact evidence-bound repair specification." : recoveryPlan ? "Review the deterministic recovery requirements that must be satisfied before repair." : "No repair or recovery plan exists."),
            new(AssistantCaseAction.ApplyRepair, exactRepair, stable && exactRepair && task.RepairAvailability?.HistoryState is "NotApplied" or "Undone", true,
                task.RepairAvailability?.HistoryState == "Undone" ? "Redo the exact preserved repair with a fresh one-use mutation authorization." : exactRepair ? "Apply the exact sealed repair with a fresh one-use mutation authorization." : "No exact repair specification exists."),
            new(AssistantCaseAction.RollBack, rollback, stable && rollback && task.RepairAvailability?.HistoryState == "Applied", true,
                rollback ? "Undo the exact applied repair with a fresh one-use mutation authorization; the repaired tree is preserved for Redo." : "No verified applied repair exists."),
        ];
    }

    private static string RenderCapabilityRequired(AssistantCapabilityRequired required)
    {
        var values = Normalize(required.MissingCapabilityIds)
            .Concat(Normalize(required.MissingInputs))
            .Concat(Normalize(required.CoverageGaps))
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var rendered = string.Join(", ", values);
        var routes = Normalize(required.Resolutions)
            .Select(route => $"{route.RequiredCapabilityId}: {route.Status} ({route.NextAction})");
        var routeText = string.Join("; ", routes);
        if (string.IsNullOrWhiteSpace(rendered) && string.IsNullOrWhiteSpace(routeText))
            return "Additional deterministic coverage is required.";
        if (string.IsNullOrWhiteSpace(rendered)) return routeText;
        return string.IsNullOrWhiteSpace(routeText) ? rendered : $"{rendered}. AUTO route: {routeText}";
    }

    private static string RenderRequestClaim(AssistantRequestDraft draft)
    {
        var lines = new List<string> { $"Problem: {draft.VerbatimUserText}" };
        if (!string.IsNullOrWhiteSpace(draft.ExpectedBehavior)) lines.Add($"Expected behavior: {draft.ExpectedBehavior}");
        if (!string.IsNullOrWhiteSpace(draft.ReproductionOrLocation)) lines.Add($"Reproduction or location: {draft.ReproductionOrLocation}");
        if (!string.IsNullOrWhiteSpace(draft.DesiredOutcome)) lines.Add($"Desired outcome: {draft.DesiredOutcome}");
        return string.Join("\n", lines);
    }

    private sealed record AssistantSuggestionPreset(
        string ClassId,
        string Problem,
        string ExpectedBehavior,
        string ReproductionOrLocation,
        string DesiredOutcome);

}
