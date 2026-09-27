using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
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
    private readonly UserToolManagerState? userTools;
    private readonly AssistantTicketTaxonomy taxonomy;
    private InvestigationTicketDraft ticketDraft;
    private string? selectedCapabilityId;
    private string composerText = string.Empty;
    private bool composerTextWasSet;
    // Milestone-2 compatibility inputs for the existing DIF/executor only. These
    // values are never promoted into the canonical Ticket Draft and are removed
    // when the structured Milestone-3 selectors replace the legacy controls.
    private string legacyProblem = string.Empty;
    private string legacyExpectedBehavior = string.Empty;
    private string legacyReproductionOrLocation = string.Empty;
    private string legacyDesiredOutcome = string.Empty;
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
        TimeProvider? timeProvider = null,
        UserToolManagerState? userTools = null,
        AssistantTicketTaxonomy? taxonomy = null)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.executionService = executionService;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.userTools = userTools;
        this.taxonomy = taxonomy ?? AssistantTicketTaxonomy.Empty;
        ticketDraft = CreateEmptyTicketDraft();
        ArgumentNullException.ThrowIfNull(classes);
        this.classes = classes.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        this.registeredTools = (registeredTools ?? []).OrderBy(option => option.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (this.classes.Any(option => string.IsNullOrWhiteSpace(option.Id) || string.IsNullOrWhiteSpace(option.DisplayName) || string.IsNullOrWhiteSpace(option.IconId) ||
                (!option.SupportedGameIds.IsDefault && (option.SupportedGameIds.Any(string.IsNullOrWhiteSpace) ||
                    option.SupportedGameIds.Distinct(StringComparer.Ordinal).Count() != option.SupportedGameIds.Length))) ||
            this.classes.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != this.classes.Length)
            throw new ArgumentException("Assistant Class options must be complete and have unique stable identities.", nameof(classes));
        if (this.registeredTools.Any(tool =>
                string.IsNullOrWhiteSpace(tool.Name) ||
                tool.GameCompatibility.IsDefaultOrEmpty ||
                tool.GameCompatibility.Any(compatibility =>
                    compatibility.EvidenceCapabilityIds.IsDefaultOrEmpty ||
                    compatibility.EvidenceCapabilityIds.Any(string.IsNullOrWhiteSpace) ||
                    string.IsNullOrWhiteSpace(compatibility.Provenance)) ||
                tool.GameCompatibility.Select(compatibility => compatibility.GameId).Distinct().Count() != tool.GameCompatibility.Length) ||
            this.registeredTools.Select(tool => tool.Id).Distinct().Count() != this.registeredTools.Length)
            throw new ArgumentException("Assistant tool options must have unique ToolIDs and explicit canonical game compatibility evidence.", nameof(registeredTools));
        ValidateTaxonomy(this.taxonomy);
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
        ticketDraft = CreateEmptyTicketDraft();
        selectedCapabilityId = null;
        composerText = string.Empty;
        composerTextWasSet = false;
        legacyProblem = string.Empty;
        legacyExpectedBehavior = string.Empty;
        legacyReproductionOrLocation = string.Empty;
        legacyDesiredOutcome = string.Empty;
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
        if (gameId is not null && !GetGameOptions().Any(game => game.Id == gameId.Value))
            throw new ArgumentException("The selected game is not connected to this account.", nameof(gameId));
        if (ticketDraft.GameId == gameId)
        {
            if (gameId is not null && ticketDraft.GameProvenance != TicketSelectionProvenance.ExplicitUserSelection)
                UpdateTicket(draft => draft with { GameProvenance = TicketSelectionProvenance.ExplicitUserSelection });
            return;
        }

        var compatibleIntegratedTools = gameId is null
            ? []
            : Normalize(ticketDraft.IntegratedTools)
                .Where(selection => IsIntegratedToolCompatible(selection.ToolId, gameId.Value))
                .ToImmutableArray();
        var compatibleReferences = Normalize(ticketDraft.ReferenceContext)
            .Where(reference => reference.GameId is null || reference.GameId == gameId)
            .Where(reference => reference.ModId is null)
            .ToImmutableArray();

        UpdateTicket(draft => draft with
        {
            GameId = gameId,
            GameProvenance = gameId is null ? null : TicketSelectionProvenance.ExplicitUserSelection,
            InstallationId = null,
            InstallationProvenance = null,
            ProfileId = null,
            ProfileProvenance = null,
            Mods = [],
            IntegratedTools = compatibleIntegratedTools,
            ConfiguredToolContext = [],
            ReferenceContext = compatibleReferences,
            CanonicalSelections = [],
        });
        PrefillInstallationAndProfile(Context);
        SelectOnlyRegisteredClassForSelectedGame();
    }

    public void SelectProfile(ProfileId? profileId)
    {
        var option = profileId is null ? null : GetProfileOptions().FirstOrDefault(item => item.Id == profileId.Value);
        if (profileId is not null && option is null)
            throw new ArgumentException("The selected profile is not connected to the selected game.", nameof(profileId));
        if (ticketDraft.ProfileId == profileId && ticketDraft.InstallationId == option?.InstallationId)
        {
            if (profileId is not null &&
                (ticketDraft.ProfileProvenance != TicketSelectionProvenance.ExplicitUserSelection ||
                 ticketDraft.InstallationProvenance != TicketSelectionProvenance.DeterministicallyResolved))
            {
                UpdateTicket(draft => draft with
                {
                    ProfileProvenance = TicketSelectionProvenance.ExplicitUserSelection,
                    InstallationProvenance = TicketSelectionProvenance.DeterministicallyResolved,
                });
            }
            return;
        }

        var installationId = option?.InstallationId;
        var allowedModOptions = GetModOptions(ticketDraft.GameId, installationId, profileId).ToDictionary(value => value.Id);
        var compatibleMods = Normalize(ticketDraft.Mods)
            .Where(selection => allowedModOptions.ContainsKey(selection.ModId))
            .Select(selection => selection with { DisplayName = allowedModOptions[selection.ModId].Name })
            .ToImmutableArray();
        if (profileId == Context.ProfileId && installationId == Context.InstallationId)
        {
            var selectedIds = compatibleMods.Select(value => value.ModId).ToHashSet();
            compatibleMods = compatibleMods.AddRange(Context.ModIds
                .Where(id => allowedModOptions.ContainsKey(id) && selectedIds.Add(id))
                .Select(id => new TicketModSelection(
                    id,
                    allowedModOptions[id].Name,
                    TicketSelectionProvenance.ContextInherited)));
        }

        var scope = ticketDraft.GameId is GameId selectedGame && installationId is InstallationId selectedInstallation
            ? new UserToolScope(selectedGame, selectedInstallation, profileId)
            : null;
        UpdateTicket(draft => draft with
        {
            InstallationId = installationId,
            InstallationProvenance = installationId is null ? null : TicketSelectionProvenance.DeterministicallyResolved,
            ProfileId = profileId,
            ProfileProvenance = profileId is null ? null : TicketSelectionProvenance.ExplicitUserSelection,
            Mods = compatibleMods,
            ConfiguredToolContext = FilterConfiguredToolContext(draft.ConfiguredToolContext, scope),
            ReferenceContext = FilterReferenceContextForMods(draft.ReferenceContext, compatibleMods),
            CanonicalSelections = [],
        });
    }

    public void SelectClass(string? classId)
    {
        var option = classId is null ? null : classes.FirstOrDefault(value => value.Id == classId);
        if (classId is not null && option is null)
            throw new ArgumentException("The selected Class is not registered in the Grid Class catalog.", nameof(classId));
        if (string.Equals(ticketDraft.Class?.Id.Value, classId, StringComparison.Ordinal))
        {
            if (ticketDraft.Class is not null && ticketDraft.Class.Provenance != TicketSelectionProvenance.ExplicitUserSelection)
                UpdateTicket(draft => draft with { Class = draft.Class! with { Provenance = TicketSelectionProvenance.ExplicitUserSelection } });
            return;
        }
        selectedCapabilityId = null;
        UpdateTicket(draft => draft with
        {
            Class = option is null ? null : ToTicketClassSelection(option, TicketSelectionProvenance.ExplicitUserSelection),
            Problem = null,
            Timing = null,
        });
    }

    public void SelectCapability(string? capabilityId)
    {
        var selectedClass = classes.FirstOrDefault(option => option.Id == ticketDraft.Class?.Id.Value);
        var options = selectedClass?.GameplayCapabilities.IsDefault == false
            ? selectedClass.GameplayCapabilities
            : [];
        if (capabilityId is not null && !options.Any(option => option.Id == capabilityId))
            throw new ArgumentException("The selected gameplay capability is not registered for this Class.", nameof(capabilityId));
        if (string.Equals(selectedCapabilityId, capabilityId, StringComparison.Ordinal)) return;
        selectedCapabilityId = capabilityId;
    }

    public void SetPlainText(string value) => SetProblem(value);

    public void SetComposerText(string value)
    {
        composerText = value ?? string.Empty;
        composerTextWasSet = true;
    }

    public void SetProblem(string value)
    {
        legacyProblem = value ?? string.Empty;
    }

    public void SetExpectedBehavior(string value) => legacyExpectedBehavior = value ?? string.Empty;

    public void SetReproductionOrLocation(string value) => legacyReproductionOrLocation = value ?? string.Empty;

    public void SetDesiredOutcome(string value) => legacyDesiredOutcome = value ?? string.Empty;

    public void SelectProblem(TicketProblemSelection? problem)
    {
        if (problem is not null && (ticketDraft.Class is null || problem.ClassId != ticketDraft.Class.Id))
            throw new ArgumentException("Problem must belong to the selected Class.", nameof(problem));
        if (ticketDraft.Problem == problem) return;
        UpdateTicket(draft => draft with { Problem = problem });
    }

    public void SelectTiming(TicketTimingSelection? timing)
    {
        if (timing is not null && (ticketDraft.Class is null || timing.ClassId != ticketDraft.Class.Id))
            throw new ArgumentException("Timing must belong to the selected Class.", nameof(timing));
        if (ticketDraft.Timing == timing) return;
        UpdateTicket(draft => draft with { Timing = timing });
    }

    public void SelectGoal(TicketGoalSelection? goal)
    {
        if (ticketDraft.Goal == goal) return;
        UpdateTicket(draft => draft with { Goal = goal });
    }

    public void SetAttachments(IEnumerable<AssistantAttachmentDraft> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values.Select(ValidateAttachment)
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        if (normalized.Length > 32) throw new ArgumentOutOfRangeException(nameof(values), "At most 32 evidence attachments may be selected.");
        var ticketAttachments = normalized.Select(ToTicketAttachment).ToImmutableArray();
        if (Normalize(ticketDraft.Attachments).SequenceEqual(ticketAttachments)) return;
        UpdateTicket(draft => draft with { Attachments = ticketAttachments });
    }

    public void AddAttachment(AssistantAttachmentDraft attachment) => SetAttachments(GetLegacyAttachments().Add(attachment));

    public void RemoveAttachment(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SetAttachments(GetLegacyAttachments().Where(item => !string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)));
    }

    public void SetModSelected(ModId modId, bool selected)
    {
        var option = GetModOptions().FirstOrDefault(mod => mod.Id == modId)
            ?? throw new ArgumentException("Mod is unavailable for this profile.", nameof(modId));
        var values = Normalize(ticketDraft.Mods).Where(value => value.ModId != modId).ToImmutableArray();
        if (selected)
            values = values.Add(new TicketModSelection(modId, option.Name, TicketSelectionProvenance.ExplicitUserSelection));
        if (Normalize(ticketDraft.Mods).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with
        {
            Mods = values,
            ReferenceContext = FilterReferenceContextForMods(draft.ReferenceContext, values),
            CanonicalSelections = [],
        });
    }

    public void SetToolSelected(ToolId toolId, bool selected)
    {
        var option = GetToolOptions().FirstOrDefault(tool => tool.Id == toolId)
            ?? throw new ArgumentException("Tool is unavailable for this game.", nameof(toolId));
        var values = Normalize(ticketDraft.IntegratedTools).Where(value => value.ToolId != toolId).ToImmutableArray();
        if (selected)
            values = values.Add(new TicketIntegratedToolSelection(toolId, option.Name, TicketSelectionProvenance.ExplicitUserSelection));
        if (Normalize(ticketDraft.IntegratedTools).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with { IntegratedTools = values });
    }

    public void SetConfiguredToolSelected(UserToolConfigurationId configurationId, bool selected)
    {
        var scope = GetCurrentUserToolScope()
            ?? throw new InvalidOperationException("Configured-tool context requires an exact Game and Installation.");
        var configuration = userTools?.ForContext(scope, includeDisabled: true)
            .FirstOrDefault(value => value.Id == configurationId)
            ?? throw new ArgumentException("Configured tool is unavailable for this exact workspace.", nameof(configurationId));
        var values = Normalize(ticketDraft.ConfiguredToolContext)
            .Where(value => value.ConfigurationId != configurationId)
            .ToImmutableArray();
        if (selected) values = values.Add(ToConfiguredToolContext(configuration));
        if (Normalize(ticketDraft.ConfiguredToolContext).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with { ConfiguredToolContext = values });
    }

    public void SetReferenceContextSelected(TicketReferenceContext context, bool selected)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureReferenceContextCompatible(context);
        var values = Normalize(ticketDraft.ReferenceContext).Where(value => value.Id != context.Id).ToImmutableArray();
        if (selected) values = values.Add(context);
        if (Normalize(ticketDraft.ReferenceContext).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with { ReferenceContext = values });
    }

    public void SetCanonicalSelectorSelection(CanonicalSelectorSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.SelectionKind != CanonicalSelectorSelectionKind.CanonicalRecord)
            throw new ArgumentException("Other remains unresolved ticket context outside canonical selections.", nameof(selection));
        var values = Normalize(ticketDraft.CanonicalSelections)
            .Where(value => value.KnowledgeKind != selection.KnowledgeKind)
            .Append(selection)
            .OrderBy(value => value.KnowledgeKind)
            .ToImmutableArray();
        var otherKind = ToTicketReferenceKind(selection.KnowledgeKind);
        var userContext = Normalize(ticketDraft.UserContext)
            .Where(value => value.Kind != otherKind || value.Resolution != TicketUserContextResolution.Unresolved)
            .ToImmutableArray();
        UpdateTicket(draft => draft with { CanonicalSelections = values, UserContext = userContext });
    }

    public void ClearCanonicalSelectorSelection(KnowledgeKind kind)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var values = Normalize(ticketDraft.CanonicalSelections)
            .Where(value => value.KnowledgeKind != kind)
            .ToImmutableArray();
        if (Normalize(ticketDraft.CanonicalSelections).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with { CanonicalSelections = values });
    }

    public void SetUserContextSelected(TicketUserContext context, bool selected)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Resolution == TicketUserContextResolution.Unresolved && context.MatchedReferenceId is not null)
            throw new ArgumentException("Unresolved user context cannot claim a matched reference identity.", nameof(context));
        if (context.Provenance is not TicketSelectionProvenance.ExplicitUserSelection and not TicketSelectionProvenance.LegacyImported)
            throw new ArgumentException("User context must retain explicit-user or legacy-import provenance.", nameof(context));
        if (selected && context.Resolution == TicketUserContextResolution.Unresolved &&
            Normalize(ticketDraft.CanonicalSelections).Any(selection => selection.KnowledgeKind == ToKnowledgeKind(context.Kind)))
            throw new ArgumentException("Canonical selection and unresolved Other context cannot coexist for the same semantic kind.", nameof(context));
        var values = Normalize(ticketDraft.UserContext)
            .Where(value => value.Kind != context.Kind || !string.Equals(value.Value, context.Value, StringComparison.Ordinal))
            .ToImmutableArray();
        if (selected) values = values.Add(context);
        if (Normalize(ticketDraft.UserContext).SequenceEqual(values)) return;
        UpdateTicket(draft => draft with { UserContext = values });
    }

    public void SetOtherContextSelected(TicketReferenceContextKind kind, string value, bool selected)
    {
        var exact = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Other context requires a non-empty value.", nameof(value))
            : value;
        if (selected)
        {
            ReplaceOtherContext(kind, exact);
            return;
        }
        SetUserContextSelected(new(
            kind,
            exact,
            TicketUserContextResolution.Unresolved,
            TicketSelectionProvenance.ExplicitUserSelection), selected);
    }

    public void ReplaceOtherContext(TicketReferenceContextKind kind, string? value)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var exact = string.IsNullOrWhiteSpace(value) ? null : value;
        var values = Normalize(ticketDraft.UserContext)
            .Where(candidate => candidate.Kind != kind || candidate.Resolution != TicketUserContextResolution.Unresolved)
            .ToImmutableArray();
        if (exact is not null)
        {
            values = values.Add(new(
                kind,
                exact,
                TicketUserContextResolution.Unresolved,
                TicketSelectionProvenance.ExplicitUserSelection));
        }
        var canonicalSelections = exact is null
            ? Normalize(ticketDraft.CanonicalSelections)
            : Normalize(ticketDraft.CanonicalSelections)
                .Where(selection => selection.KnowledgeKind != ToKnowledgeKind(kind))
                .ToImmutableArray();
        if (Normalize(ticketDraft.UserContext).SequenceEqual(values) &&
            Normalize(ticketDraft.CanonicalSelections).SequenceEqual(canonicalSelections)) return;
        UpdateTicket(draft => draft with { UserContext = values, CanonicalSelections = canonicalSelections });
    }

    private static TicketReferenceContextKind ToTicketReferenceKind(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Location => TicketReferenceContextKind.Location,
        KnowledgeKind.MissionQuest => TicketReferenceContextKind.MissionOrQuest,
        KnowledgeKind.Item => TicketReferenceContextKind.Item,
        KnowledgeKind.Actor => TicketReferenceContextKind.Entity,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static KnowledgeKind ToKnowledgeKind(TicketReferenceContextKind kind) => kind switch
    {
        TicketReferenceContextKind.Location => KnowledgeKind.Location,
        TicketReferenceContextKind.MissionOrQuest => KnowledgeKind.MissionQuest,
        TicketReferenceContextKind.Item => KnowledgeKind.Item,
        TicketReferenceContextKind.Entity => KnowledgeKind.Actor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public void ApplySuggestion(string suggestionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestionId);
        Surface = AssistantSurface.Home;
        IsFormVisible = true;
        IntakeScope = AssistantIntakeScope.Game;
        var classId = suggestionId switch
        {
            "installation" => "grid.class.installation-integrity",
            "crash" => "grid.class.crash-freeze",
            "assets" => "grid.class.asset-mismatch",
            _ => throw new ArgumentOutOfRangeException(nameof(suggestionId)),
        };
        var classOption = classes.Single(option => option.Id == classId);
        UpdateTicket(draft => draft with
        {
            Class = ToTicketClassSelection(classOption, TicketSelectionProvenance.ExplicitUserSelection),
            Problem = null,
            Timing = null,
        });
        selectedCapabilityId = null;
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
        ReconcileInheritedContext(context);
        PrefillFromContext(context);
        PruneIncompatibleTicketContext();
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
        if (!snapshot.TicketReadiness.CanSubmit || !snapshot.Draft.CanSubmit || snapshot.Draft.GameId is not GameId gameId ||
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
            snapshot.Draft.Attachments, snapshot.Draft.CapabilityId,
            snapshot.Draft.GameSelectionSource, snapshot.Draft.InstallationSelectionSource,
            snapshot.Draft.ProfileSelectionSource, snapshot.Draft.ClassSelectionSource,
            Normalize(ticketDraft.CanonicalSelections), Normalize(ticketDraft.UserContext)
                .Where(context => context.Resolution == TicketUserContextResolution.Unresolved)
                .ToImmutableArray(),
            ticketDraft.Problem, ticketDraft.Timing, ticketDraft.Goal);
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
        var selectedClass = classes.FirstOrDefault(option => option.Id == ticketDraft.Class?.Id.Value);
        var readiness = ResolveReadiness(selectedClass);
        var canonicalReadiness = InvestigationTicketDraftPolicy.Evaluate(ticketDraft);
        var preview = CreateTicketPreview();
        var modIds = Normalize(ticketDraft.Mods).Select(value => value.ModId).ToImmutableArray();
        var toolIds = Normalize(ticketDraft.IntegratedTools).Select(value => value.ToolId).ToImmutableArray();
        var legacyAttachments = GetLegacyAttachments();
        var requestClaim = ResolveRequestClaim();
        var hasRequiredIntake = IntakeScope == AssistantIntakeScope.Game && ticketDraft.GameId is not null &&
            ticketDraft.InstallationId is not null && ticketDraft.ProfileId is not null && selectedClass is not null &&
            !string.IsNullOrWhiteSpace(requestClaim);
        // Removable compatibility projection for the current Milestone-2 UI and
        // deterministic executor. Canonical readiness above comes only from the
        // InvestigationTicketDraft and its Class + Problem + Goal policy.
        var draft = new AssistantDraftSnapshot(
            IntakeScope, ticketDraft.GameId,
            modIds,
            toolIds,
            ticketDraft.Class?.Id.Value, requestClaim, CreateDisplayTitle(selectedClass), selectedClass?.IconId ?? "grid.icon.unknown",
            readiness.Status, readiness.Detail,
            CanSubmit: hasRequiredIntake &&
                readiness.Status is AssistantDraftReadiness.ReadyForDeterministicCollection or AssistantDraftReadiness.UnsupportedCoverage &&
                executionService?.IsAvailable == true && LifecycleStage == AssistantLifecycleStage.Idle,
            InstallationId: ticketDraft.InstallationId, ProfileId: ticketDraft.ProfileId, Problem: requestClaim,
            ExpectedBehavior: legacyExpectedBehavior, ReproductionOrLocation: legacyReproductionOrLocation, DesiredOutcome: legacyDesiredOutcome,
            Attachments: legacyAttachments, CapabilityId: selectedCapabilityId,
            GameSelectionSource: ToAssistantSelectionSource(ticketDraft.GameProvenance),
            InstallationSelectionSource: ToAssistantSelectionSource(ticketDraft.InstallationProvenance),
            ProfileSelectionSource: ToAssistantSelectionSource(ticketDraft.ProfileProvenance),
            ClassSelectionSource: ToAssistantSelectionSource(ticketDraft.Class?.Provenance));

        return new(IsExpanded, RequestedPanelWidth, Mode, ProviderAvailability, LifecycleStage, Context,
            executionService?.IsAvailable == true
                ? "Deterministic request execution is available. Chat and AI remain unconfigured."
                : "Deterministic request intake is available. No production execution service is connected.",
            Surface, IsFormVisible, IsFullScreen, ticketDraft, canonicalReadiness, composerText, draft,
            GetGameOptions(),
            GetModOptions(), GetToolOptions(), classes, tasks.Select(task => task.Summary).ToImmutableArray(),
            pendingAuthorization, activeTask, executionError, GetInstallationOptions(), GetProfileOptions(),
            GetProblemOptions(), GetTimingOptions(), GetGoalOptions(), GetConfiguredToolOptions(),
            GetReferenceContextOptions(), preview);
    }

    private void PrefillFromContext(ApplicationContextSnapshot context)
    {
        if (IntakeScope != AssistantIntakeScope.Game || context.Surface != ApplicationSurface.GameWorkspace) return;
        if (ticketDraft.GameId is null && context.GameId is not null &&
            GetGameOptions().Any(game => game.Id == context.GameId.Value))
        {
            UpdateTicket(draft => draft with
            {
                GameId = context.GameId,
                GameProvenance = TicketSelectionProvenance.ContextInherited,
            });
        }
        PrefillInstallationAndProfile(context);
        SelectOnlyRegisteredClassForSelectedGame();
        if (ticketDraft.GameId == context.GameId && ticketDraft.ProfileId == context.ProfileId)
        {
            var options = GetModOptions().ToDictionary(value => value.Id);
            var existing = Normalize(ticketDraft.Mods);
            var ids = existing.Select(value => value.ModId).ToHashSet();
            var inherited = context.ModIds
                .Where(id => options.ContainsKey(id) && ids.Add(id))
                .Select(id => new TicketModSelection(id, options[id].Name, TicketSelectionProvenance.ContextInherited));
            var combined = existing.AddRange(inherited);
            if (!existing.SequenceEqual(combined)) UpdateTicket(draft => draft with { Mods = combined });
        }
    }

    private void PrefillInstallationAndProfile(ApplicationContextSnapshot context)
    {
        if (ticketDraft.GameId is null || ticketDraft.InstallationId is not null || ticketDraft.ProfileId is not null) return;
        var profile = context.Surface == ApplicationSurface.GameWorkspace && ticketDraft.GameId == context.GameId &&
                      context.ProfileId is ProfileId contextProfileId && context.InstallationId is InstallationId contextInstallationId
            ? GetProfileOptions().FirstOrDefault(item => item.Id == contextProfileId && item.InstallationId == contextInstallationId)
            : null;
        if (profile is not null)
        {
            UpdateTicket(draft => draft with
            {
                InstallationId = profile.InstallationId,
                InstallationProvenance = TicketSelectionProvenance.ContextInherited,
                ProfileId = profile.Id,
                ProfileProvenance = TicketSelectionProvenance.ContextInherited,
            });
            return;
        }

        // A game selected outside an active workspace has no context profile to
        // inherit. Resolve only the single connected profile when there is no
        // choice to make and it is the empty GRID-owned base-game profile that
        // the canonical runtime matcher can independently validate. Ambiguous,
        // manager-owned, archived, unavailable, or modded profiles remain
        // explicitly unselected.
        var game = catalog.Games.FirstOrDefault(candidate => candidate.Id == ticketDraft.GameId.Value);
        if (game is null) return;
        var candidates = GetConnectedInstallations(game)
            .SelectMany(installation => installation.Profiles.Select(candidate => new
            {
                Installation = installation,
                Profile = candidate,
            }))
            .ToArray();
        if (candidates.Length != 1) return;
        var selected = candidates[0];
        if (selected.Installation.Metadata.Availability != InstallationAvailability.Available ||
            selected.Installation.Kind != InstallationKind.External ||
            selected.Installation.Metadata.Provenance != InstallationProvenanceKind.ConnectedReference ||
            string.IsNullOrWhiteSpace(selected.Installation.Metadata.LocationDisplay) ||
            selected.Profile.Origin != ProfileOrigin.Grid ||
            selected.Profile.Lifecycle != ProfileLifecycleState.Available ||
            !selected.Profile.Mods.IsEmpty ||
            !selected.Profile.Plugins.IsEmpty)
        {
            return;
        }

        UpdateTicket(draft => draft with
        {
            InstallationId = selected.Installation.Id,
            InstallationProvenance = TicketSelectionProvenance.DeterministicallyResolved,
            ProfileId = selected.Profile.Id,
            ProfileProvenance = TicketSelectionProvenance.DeterministicallyResolved,
        });
    }

    private ImmutableArray<AssistantGameOption> GetGameOptions() => catalog.Games
        .Where(game => GetConnectedInstallations(game).Length > 0)
        .Select(game => new AssistantGameOption(game.Id, game.Name))
        .OrderBy(game => game.Name, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();

    private ImmutableArray<ManagedInstallation> GetConnectedInstallations(ManagedGame game) =>
        catalog.SourceKind == CatalogSourceKind.Mock
            ? game.Installations
            : game.Installations
                .Where(installation => installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference &&
                    installation.Metadata.ReferenceId is not null)
                .ToImmutableArray();

    private ImmutableArray<AssistantInstallationOption> GetInstallationOptions()
    {
        var game = ticketDraft.GameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == ticketDraft.GameId.Value);
        return game is null ? [] : GetConnectedInstallations(game)
            .Select(item => new AssistantInstallationOption(item.Id, game.Id, item.Name))
            .ToImmutableArray();
    }

    private ImmutableArray<AssistantProfileOption> GetProfileOptions()
    {
        if (ticketDraft.GameId is null) return [];
        var game = catalog.Games.FirstOrDefault(candidate => candidate.Id == ticketDraft.GameId.Value);
        if (game is null) return [];
        var profiles = GetConnectedInstallations(game)
            .SelectMany(installation => installation.Profiles.Select(profile => new
            {
                Profile = profile,
                Installation = installation,
            }))
            .ToArray();
        var duplicateNames = profiles.GroupBy(item => item.Profile.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return profiles
            .Select(item => new AssistantProfileOption(
                item.Profile.Id,
                item.Installation.Id,
                item.Profile.Name,
                item.Installation.Name,
                duplicateNames.Contains(item.Profile.Name)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.InstallationName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private ImmutableArray<AssistantModOption> GetModOptions()
        => GetModOptions(ticketDraft.GameId, ticketDraft.InstallationId, ticketDraft.ProfileId);

    private ImmutableArray<AssistantModOption> GetModOptions(
        GameId? gameId,
        InstallationId? installationId,
        ProfileId? profileId)
    {
        var game = gameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId.Value);
        if (game is null) return [];
        var installation = installationId is null ? null : game.Installations.FirstOrDefault(candidate => candidate.Id == installationId.Value);
        var profile = installation is null || profileId is null ? null : installation.Profiles.FirstOrDefault(candidate => candidate.Id == profileId.Value);
        return profile?.Mods.Where(mod => mod.Kind != ModEntryKind.Separator)
            .Select(mod => new AssistantModOption(mod.Id, mod.Name, mod.IsEnabled, mod.Kind)).ToImmutableArray() ?? [];
    }

    private ImmutableArray<AssistantToolOption> GetToolOptions()
    {
        if (!registeredTools.IsEmpty)
            return ticketDraft.GameId is null ? [] : registeredTools
                .Where(tool => tool.GameCompatibility.Any(compatibility => compatibility.GameId == ticketDraft.GameId.Value))
                .ToImmutableArray();
        var game = ticketDraft.GameId is null ? null : catalog.Games.FirstOrDefault(candidate => candidate.Id == ticketDraft.GameId.Value);
        if (game is null) return [];
        var installation = ticketDraft.InstallationId is null ? null : game.Installations.FirstOrDefault(candidate => candidate.Id == ticketDraft.InstallationId.Value);
        return game.ToolCatalog.Tools.Select(tool =>
        {
            var configuration = installation?.ToolConfigurations.FirstOrDefault(candidate => candidate.ToolId == tool.Id);
            var evidence = tool.AdapterIds.Select(adapter => $"catalog:{game.Id.Value}:{adapter.Value}").ToImmutableArray();
            return new AssistantToolOption(tool.Id, tool.Name, configuration?.Availability ?? AvailabilityState.Unavailable,
                configuration?.UnavailableReason ?? "Not configured for the selected installation.",
                [new AssistantToolGameCompatibility(game.Id, evidence, "Canonical game-owned tool catalog")]);
        }).ToImmutableArray();
    }

    private ImmutableArray<AssistantProblemOption> GetProblemOptions() => ticketDraft.Class is null
        ? []
        : Normalize(taxonomy.Problems)
            .Where(option => option.ClassId == ticketDraft.Class.Id)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();

    private ImmutableArray<AssistantTimingOption> GetTimingOptions() => ticketDraft.Class is null
        ? []
        : Normalize(taxonomy.Timings)
            .Where(option => option.ClassId == ticketDraft.Class.Id)
            .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(option => option.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();

    private ImmutableArray<AssistantGoalOption> GetGoalOptions() => Normalize(taxonomy.Goals)
        .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(option => option.Id.Value, StringComparer.Ordinal)
        .ToImmutableArray();

    private ImmutableArray<AssistantConfiguredToolOption> GetConfiguredToolOptions()
    {
        var scope = GetCurrentUserToolScope();
        if (scope is null || userTools is null) return [];
        return userTools.ForContext(scope)
            .Select(configuration =>
            {
                var context = ToConfiguredToolContext(configuration);
                return new AssistantConfiguredToolOption(
                    configuration.Id,
                    configuration.Title,
                    configuration.Scope,
                    configuration.IsRunnable,
                    context.IdentityStatus,
                    context.CompatibilityStatus,
                    context.IntegrationStatus,
                    context.InvestigationCapabilityStatus);
            })
            .ToImmutableArray();
    }

    private ImmutableArray<TicketReferenceContext> GetReferenceContextOptions()
    {
        var modIds = Normalize(ticketDraft.Mods).Select(value => value.ModId).ToHashSet();
        return Normalize(taxonomy.ReferenceContexts)
            .Where(value => value.GameId is null || value.GameId == ticketDraft.GameId)
            .Where(value => value.ModId is null || modIds.Contains(value.ModId.Value))
            .OrderBy(value => ReferenceKindPriority(value.Kind))
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private AssistantTicketPreview CreateTicketPreview()
    {
        var contributions = ImmutableArray.CreateBuilder<AssistantTicketNameContribution>();
        var phrases = new List<string>();
        var references = Normalize(ticketDraft.ReferenceContext)
            .OrderBy(value => ReferenceKindPriority(value.Kind))
            .ThenBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal)
            .Take(2)
            .ToArray();

        foreach (var reference in references)
        {
            var phrase = CompactTitlePhrase(reference.DisplayName, 4);
            var kind = ReferenceKindLabel(reference.Kind);
            if (!phrase.Contains(kind, StringComparison.OrdinalIgnoreCase)) phrase = $"{phrase} {kind}";
            phrases.Add(phrase);
            contributions.Add(new(reference.Kind.ToString(), reference.Id.Value));
        }

        if (references.Length == 0 && ticketDraft.GameId is GameId gameId &&
            catalog.Games.FirstOrDefault(value => value.Id == gameId) is { } game)
        {
            phrases.Add(CompactTitlePhrase(game.Name, 4));
            contributions.Add(new("Game", gameId.Value));
        }

        if (ticketDraft.Problem is { } problem)
        {
            phrases.Add(CompactTitlePhrase(problem.DisplayName, 4));
            contributions.Add(new("Problem", problem.Id.Value));
        }
        else if (ticketDraft.Class is { } selectedClass)
        {
            phrases.Add(CompactTitlePhrase(selectedClass.DisplayName, 4));
            contributions.Add(new("Class", selectedClass.Id.Value));
        }

        var title = string.Join(' ', phrases.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(title)) title = "New investigation";
        return new(
            title,
            ticketDraft.Class?.IconId ?? "grid.icon.unknown",
            AssistantTicketNameProvenance.DeterministicStructuredSelections,
            contributions.ToImmutable());
    }

    private static int ReferenceKindPriority(TicketReferenceContextKind kind) => kind switch
    {
        TicketReferenceContextKind.MissionOrQuest => 0,
        TicketReferenceContextKind.Location => 1,
        TicketReferenceContextKind.Entity => 2,
        TicketReferenceContextKind.Item => 3,
        _ => int.MaxValue,
    };

    private static string ReferenceKindLabel(TicketReferenceContextKind kind) => kind switch
    {
        TicketReferenceContextKind.MissionOrQuest => "Mission",
        TicketReferenceContextKind.Location => "Location",
        TicketReferenceContextKind.Entity => "Entity",
        TicketReferenceContextKind.Item => "Item",
        _ => "Context",
    };

    private static string CompactTitlePhrase(string value, int maximumWords) => string.Join(' ', TitleWords(value).Take(maximumWords));

    private (AssistantDraftReadiness Status, string Detail) ResolveReadiness(AssistantClassOption? selectedClass)
    {
        if (IntakeScope == AssistantIntakeScope.Grid)
            return (AssistantDraftReadiness.RuntimeUnavailable, "GRID development intake has no repository-owned registry or execution route yet.");
        if (ticketDraft.GameId is null || selectedClass is null)
            return (AssistantDraftReadiness.Incomplete, "Choose a game and Class to complete the structured request.");
        if (ticketDraft.InstallationId is null || ticketDraft.ProfileId is null)
            return (AssistantDraftReadiness.Incomplete, "Choose a connected installation and profile to establish exact collection context.");
        if (!SupportsSelectedGame(selectedClass))
            return (AssistantDraftReadiness.UnsupportedCoverage, $"{selectedClass.DisplayName} does not support the selected game.");
        if (string.IsNullOrWhiteSpace(ResolveRequestClaim()))
            return (AssistantDraftReadiness.Incomplete, "Describe the problem to preserve the investigation claim.");
        var gameplayCapabilities = selectedClass.GameplayCapabilities.IsDefault ? [] : selectedClass.GameplayCapabilities;
        if (!gameplayCapabilities.IsEmpty && string.IsNullOrWhiteSpace(selectedCapabilityId))
            return (AssistantDraftReadiness.Incomplete, "Choose the exact gameplay capability to assess.");
        if (!selectedClass.IsRegistered)
            return (AssistantDraftReadiness.UnsupportedCoverage, $"{selectedClass.DisplayName} has no registered deterministic coverage; the request can be preserved with CapabilityRequired.");
        var selectedMods = Normalize(ticketDraft.Mods);
        var selectedTools = Normalize(ticketDraft.IntegratedTools);
        if (selectedMods.Length < selectedClass.MinimumMods)
            return (AssistantDraftReadiness.Incomplete, $"This Class recipe is missing {selectedClass.MinimumMods - selectedMods.Length} required mod selection(s); submission will preserve the gap.");
        if (selectedTools.Length < selectedClass.MinimumTools)
            return (AssistantDraftReadiness.Incomplete, $"This Class recipe is missing {selectedClass.MinimumTools - selectedTools.Length} required tool selection(s); submission will preserve the gap.");
        if (!selectedClass.AllowedToolIds.IsDefault && selectedTools.Any(selection => !selectedClass.AllowedToolIds.Contains(selection.ToolId)))
            return (AssistantDraftReadiness.UnsupportedCoverage, "One or more selected tools are outside this Class recipe's registered coverage.");
        if (selectedTools.Any(selection => !GetToolOptions().Any(tool => tool.Id == selection.ToolId && tool.Availability == AvailabilityState.Available)))
            return (AssistantDraftReadiness.RuntimeUnavailable, "One or more selected tools are unavailable; submission will preserve the missing runtime capability.");
        return executionService?.IsAvailable == true
            ? (AssistantDraftReadiness.ReadyForDeterministicCollection, "Structured request complete and ready for exact read-scope review.")
            : (AssistantDraftReadiness.RuntimeUnavailable, "Structured request complete; no production execution service is connected.");
    }

    private void SelectOnlyRegisteredClassForSelectedGame()
    {
        if (ticketDraft.GameId is null || ticketDraft.Class is not null) return;
        var engineGameId = ToEngineGameId(ticketDraft.GameId.Value);
        var supported = classes.Where(option => option.IsRegistered &&
            !option.SupportedGameIds.IsDefaultOrEmpty &&
            option.SupportedGameIds.Contains(engineGameId, StringComparer.Ordinal)).ToArray();
        if (supported.Length == 1)
        {
            UpdateTicket(draft => draft with
            {
                Class = ToTicketClassSelection(supported[0], TicketSelectionProvenance.ContextInherited),
            });
        }
    }

    private bool SupportsSelectedGame(AssistantClassOption selectedClass)
    {
        if (ticketDraft.GameId is null || selectedClass.SupportedGameIds.IsDefaultOrEmpty) return true;
        return selectedClass.SupportedGameIds.Contains(ToEngineGameId(ticketDraft.GameId.Value), StringComparer.Ordinal);
    }

    private static string ToEngineGameId(GameId gameId) => gameId.Value switch
    {
        "game.skyrim-special-edition" => "skyrimspecialedition",
        "game.grandtheftautov-enhanced" => "grandtheftautov-enhanced",
        "game.grandtheftautov-legacy" => "grandtheftautov-legacy",
        _ => gameId.Value,
    };

    private string CreateDisplayTitle(AssistantClassOption? selectedClass)
    {
        if (selectedClass is null) return "New request";
        var selectedCapability = selectedClass.GameplayCapabilities.IsDefault
            ? null
            : selectedClass.GameplayCapabilities.FirstOrDefault(option => option.Id == selectedCapabilityId);
        if (selectedCapability is not null) return string.Join(' ', TitleWords(selectedCapability.DisplayName).Take(5));
        var classWords = TitleWords(selectedClass.DisplayName).Take(4).ToArray();
        var selectedModIds = Normalize(ticketDraft.Mods).Select(value => value.ModId).ToHashSet();
        var selectedMod = GetModOptions().FirstOrDefault(option => selectedModIds.Contains(option.Id));
        var selectedGame = ticketDraft.GameId is null
            ? null
            : catalog.Games.FirstOrDefault(game => game.Id == ticketDraft.GameId.Value);
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
            ticketDraft.GameId != request.Draft.GameId || ticketDraft.InstallationId != request.Draft.InstallationId || ticketDraft.ProfileId != request.Draft.ProfileId ||
            ticketDraft.Class?.Id.Value != request.Draft.ClassId || ResolveRequestClaim() != request.Draft.VerbatimUserText ||
            legacyExpectedBehavior != request.Draft.ExpectedBehavior || legacyReproductionOrLocation != request.Draft.ReproductionOrLocation ||
            legacyDesiredOutcome != request.Draft.DesiredOutcome || ToAssistantSelectionSource(ticketDraft.GameProvenance) != request.Draft.GameSelectionSource ||
            ToAssistantSelectionSource(ticketDraft.InstallationProvenance) != request.Draft.InstallationSelectionSource ||
            ToAssistantSelectionSource(ticketDraft.ProfileProvenance) != request.Draft.ProfileSelectionSource ||
            ToAssistantSelectionSource(ticketDraft.Class?.Provenance) != request.Draft.ClassSelectionSource ||
            ticketDraft.Problem != request.Draft.ProblemSelection ||
            ticketDraft.Timing != request.Draft.TimingSelection ||
            ticketDraft.Goal != request.Draft.GoalSelection ||
            !Normalize(ticketDraft.Mods).Select(value => value.ModId).ToHashSet().SetEquals(authorizedMods) ||
            !Normalize(ticketDraft.IntegratedTools).Select(value => value.ToolId).ToHashSet().SetEquals(authorizedTools) ||
            !GetLegacyAttachments().SequenceEqual(Normalize(request.Draft.Attachments)) ||
            !Normalize(ticketDraft.CanonicalSelections).SequenceEqual(Normalize(request.Draft.CanonicalSelections)) ||
            !Normalize(ticketDraft.UserContext)
                .Where(context => context.Resolution == TicketUserContextResolution.Unresolved)
                .SequenceEqual(Normalize(request.Draft.UnresolvedUserContext)))
            throw new InvalidOperationException("The selected context or investigation intake changed after authorization review. Prepare a new request.");
    }

    private string ResolveRequestClaim() => composerTextWasSet ? composerText : legacyProblem;

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

    private static InvestigationTicketDraft CreateEmptyTicketDraft() => new(
        InvestigationTicketDraft.CurrentSchemaVersion,
        new InvestigationId($"investigation.{Guid.NewGuid():N}"),
        new TicketId($"ticket.{Guid.NewGuid():N}"),
        1,
        null,
        null,
        null,
        Mods: [],
        IntegratedTools: [],
        ConfiguredToolContext: [],
        ReferenceContext: [],
        UserContext: [],
        Attachments: [],
        CanonicalSelections: []);

    private void UpdateTicket(Func<InvestigationTicketDraft, InvestigationTicketDraft> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var candidate = update(ticketDraft);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate == ticketDraft) return;
        candidate = candidate with { Revision = checked(ticketDraft.Revision + 1) };
        InvestigationTicketDraftPolicy.Validate(candidate);
        ticketDraft = candidate;
    }

    private static TicketClassSelection ToTicketClassSelection(
        AssistantClassOption option,
        TicketSelectionProvenance provenance) => new(
            new TicketClassId(option.Id),
            option.DisplayName,
            option.IconId,
            provenance);

    private static AssistantSelectionSource ToAssistantSelectionSource(TicketSelectionProvenance? provenance) => provenance switch
    {
        TicketSelectionProvenance.ContextInherited => AssistantSelectionSource.ContextInherited,
        TicketSelectionProvenance.DeterministicallyResolved => AssistantSelectionSource.DerivedFromProfile,
        _ => AssistantSelectionSource.UserSelected,
    };

    private TicketAttachmentReference ToTicketAttachment(AssistantAttachmentDraft attachment)
    {
        var path = Path.GetFullPath(attachment.Path);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).ToLowerInvariant();
        return new(
            new TicketAttachmentId($"attachment.sha256.{digest}"),
            attachment.OriginalName,
            path,
            attachment.MediaType,
            attachment.SizeBytes,
            TicketSelectionProvenance.ExplicitUserSelection);
    }

    private ImmutableArray<AssistantAttachmentDraft> GetLegacyAttachments() => Normalize(ticketDraft.Attachments)
        .Select(value => new AssistantAttachmentDraft(
            value.ResourceReference,
            value.OriginalName,
            value.MediaType,
            value.SizeBytes))
        .ToImmutableArray();

    private bool IsIntegratedToolCompatible(ToolId toolId, GameId gameId) =>
        registeredTools.Any(tool => tool.Id == toolId &&
            tool.GameCompatibility.Any(compatibility => compatibility.GameId == gameId));

    private UserToolScope? GetCurrentUserToolScope() =>
        ticketDraft.GameId is GameId gameId && ticketDraft.InstallationId is InstallationId installationId
            ? new UserToolScope(gameId, installationId, ticketDraft.ProfileId)
            : null;

    private TicketConfiguredToolContext ToConfiguredToolContext(UserToolLaunchConfiguration configuration)
    {
        var knowledge = userTools?.FindKnowledge(configuration.KnowledgeId);
        var identityResolved = knowledge is
        {
            CanonicalToolId: not null,
            State: >= InstalledToolIdentityState.Identified,
        };
        var compatibilityResolved = ticketDraft.GameId is GameId gameId &&
            knowledge is { State: >= InstalledToolIdentityState.CompatibilityResolved } &&
            knowledge.Compatibility.Any(value => value.GameId == gameId && value.Disposition != ToolCompatibilityDisposition.Unresolved);
        var integrationAvailable = identityResolved && knowledge?.CanonicalToolId is ToolId toolId &&
            ticketDraft.GameId is GameId selectedGameId && IsIntegratedToolCompatible(toolId, selectedGameId);

        return new(
            configuration.Id,
            configuration.Title,
            configuration.Scope,
            configuration.BinaryPath,
            configuration.Fingerprint,
            configuration.IsRunnable,
            identityResolved ? ConfiguredToolIdentityStatus.Resolved : ConfiguredToolIdentityStatus.Unresolved,
            compatibilityResolved ? ConfiguredToolCompatibilityStatus.Resolved : ConfiguredToolCompatibilityStatus.Unresolved,
            integrationAvailable ? ConfiguredToolIntegrationStatus.Available : ConfiguredToolIntegrationStatus.Unavailable,
            integrationAvailable ? ConfiguredToolInvestigationCapabilityStatus.Available : ConfiguredToolInvestigationCapabilityStatus.None,
            TicketSelectionProvenance.ExplicitUserSelection);
    }

    private static ImmutableArray<TicketConfiguredToolContext> FilterConfiguredToolContext(
        ImmutableArray<TicketConfiguredToolContext> values,
        UserToolScope? scope) => scope is null
            ? []
            : Normalize(values).Where(value => value.Scope.IsVisibleIn(scope)).ToImmutableArray();

    private static ImmutableArray<TicketReferenceContext> FilterReferenceContextForMods(
        ImmutableArray<TicketReferenceContext> values,
        ImmutableArray<TicketModSelection> mods)
    {
        var ids = Normalize(mods).Select(value => value.ModId).ToHashSet();
        return Normalize(values).Where(value => value.ModId is null || ids.Contains(value.ModId.Value)).ToImmutableArray();
    }

    private void EnsureReferenceContextCompatible(TicketReferenceContext context)
    {
        if (context.GameId is GameId gameId && ticketDraft.GameId != gameId)
            throw new ArgumentException("Reference context belongs to a different canonical Game.", nameof(context));
        if (context.ModId is ModId modId && !Normalize(ticketDraft.Mods).Any(value => value.ModId == modId))
            throw new ArgumentException("Mod-scoped reference context requires that exact selected Mod.", nameof(context));
    }

    private void ReconcileInheritedContext(ApplicationContextSnapshot context)
    {
        var hasWorkspace = context.Surface == ApplicationSurface.GameWorkspace;
        var inheritedGameChanged = ticketDraft.GameProvenance == TicketSelectionProvenance.ContextInherited &&
            (!hasWorkspace || ticketDraft.GameId != context.GameId);
        if (inheritedGameChanged)
        {
            UpdateTicket(draft => draft with
            {
                GameId = null,
                GameProvenance = null,
                InstallationId = null,
                InstallationProvenance = null,
                ProfileId = null,
                ProfileProvenance = null,
                Mods = [],
                IntegratedTools = [],
                ConfiguredToolContext = [],
                ReferenceContext = Normalize(draft.ReferenceContext)
                    .Where(value => value.GameId is null && value.ModId is null)
                    .ToImmutableArray(),
                CanonicalSelections = [],
            });
            return;
        }

        var inheritedProfileChanged =
            (ticketDraft.InstallationProvenance == TicketSelectionProvenance.ContextInherited ||
             ticketDraft.ProfileProvenance == TicketSelectionProvenance.ContextInherited) &&
            (!hasWorkspace || ticketDraft.GameId != context.GameId ||
             ticketDraft.InstallationId != context.InstallationId || ticketDraft.ProfileId != context.ProfileId);
        if (!inheritedProfileChanged) return;
        UpdateTicket(draft => draft with
        {
            InstallationId = null,
            InstallationProvenance = null,
            ProfileId = null,
            ProfileProvenance = null,
            Mods = [],
            ConfiguredToolContext = [],
            ReferenceContext = Normalize(draft.ReferenceContext).Where(value => value.ModId is null).ToImmutableArray(),
            CanonicalSelections = [],
        });
    }

    private void PruneIncompatibleTicketContext()
    {
        var gameExists = ticketDraft.GameId is GameId gameId && GetGameOptions().Any(value => value.Id == gameId);
        if (ticketDraft.GameId is not null && !gameExists)
        {
            UpdateTicket(draft => draft with
            {
                GameId = null,
                GameProvenance = null,
                InstallationId = null,
                InstallationProvenance = null,
                ProfileId = null,
                ProfileProvenance = null,
                Mods = [],
                IntegratedTools = [],
                ConfiguredToolContext = [],
                ReferenceContext = Normalize(draft.ReferenceContext)
                    .Where(value => value.GameId is null && value.ModId is null)
                    .ToImmutableArray(),
                CanonicalSelections = [],
            });
            return;
        }

        InstallationId? installationId = ticketDraft.InstallationId is InstallationId currentInstallation &&
            GetInstallationOptions().Any(value => value.Id == currentInstallation)
                ? currentInstallation
                : null;
        var profile = ticketDraft.ProfileId is ProfileId profileId && installationId is not null
            ? GetProfileOptions().FirstOrDefault(value => value.Id == profileId && value.InstallationId == installationId)
            : null;
        var profileIdValue = profile?.Id;
        var modOptions = GetModOptions(ticketDraft.GameId, installationId, profileIdValue).ToDictionary(value => value.Id);
        var mods = Normalize(ticketDraft.Mods)
            .Where(value => modOptions.ContainsKey(value.ModId))
            .Select(value => value with { DisplayName = modOptions[value.ModId].Name })
            .ToImmutableArray();
        var integratedTools = ticketDraft.GameId is GameId currentGame
            ? Normalize(ticketDraft.IntegratedTools).Where(value => IsIntegratedToolCompatible(value.ToolId, currentGame)).ToImmutableArray()
            : [];
        var scope = ticketDraft.GameId is GameId scopedGame && installationId is InstallationId scopedInstallation
            ? new UserToolScope(scopedGame, scopedInstallation, profileIdValue)
            : null;
        var candidate = ticketDraft with
        {
            InstallationId = installationId,
            InstallationProvenance = installationId is null ? null : ticketDraft.InstallationProvenance,
            ProfileId = profileIdValue,
            ProfileProvenance = profileIdValue is null ? null : ticketDraft.ProfileProvenance,
            Mods = mods,
            IntegratedTools = integratedTools,
            ConfiguredToolContext = FilterConfiguredToolContext(ticketDraft.ConfiguredToolContext, scope),
            ReferenceContext = FilterReferenceContextForMods(ticketDraft.ReferenceContext, mods),
            CanonicalSelections = installationId is null || profileIdValue is null ? [] : ticketDraft.CanonicalSelections,
        };
        if (candidate != ticketDraft) UpdateTicket(_ => candidate);
    }

    private static bool DraftsMatch(AssistantRequestDraft left, AssistantRequestDraft right) =>
        left.GameId == right.GameId && left.InstallationId == right.InstallationId && left.ProfileId == right.ProfileId &&
        left.Mods.SequenceEqual(right.Mods) && left.Tools.SequenceEqual(right.Tools) &&
        left.ClassId == right.ClassId && left.RecipeVersion == right.RecipeVersion &&
        left.VerbatimUserText == right.VerbatimUserText && left.DisplayTitle == right.DisplayTitle &&
        left.ExpectedBehavior == right.ExpectedBehavior && left.ReproductionOrLocation == right.ReproductionOrLocation &&
        left.DesiredOutcome == right.DesiredOutcome && Normalize(left.Attachments).SequenceEqual(Normalize(right.Attachments)) &&
        left.CapabilityId == right.CapabilityId && left.GameSelectionSource == right.GameSelectionSource &&
        left.InstallationSelectionSource == right.InstallationSelectionSource &&
        left.ProfileSelectionSource == right.ProfileSelectionSource && left.ClassSelectionSource == right.ClassSelectionSource &&
        Normalize(left.CanonicalSelections).SequenceEqual(Normalize(right.CanonicalSelections)) &&
        Normalize(left.UnresolvedUserContext).SequenceEqual(Normalize(right.UnresolvedUserContext)) &&
        left.ProblemSelection == right.ProblemSelection && left.TimingSelection == right.TimingSelection &&
        left.GoalSelection == right.GoalSelection;

    private void ValidateTaxonomy(AssistantTicketTaxonomy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var classIds = classes.Select(option => new TicketClassId(option.Id)).ToHashSet();
        var problems = Normalize(value.Problems);
        var timings = Normalize(value.Timings);
        var goals = Normalize(value.Goals);
        var references = Normalize(value.ReferenceContexts);
        if (problems.Any(option => string.IsNullOrWhiteSpace(option.DisplayName) || !classIds.Contains(option.ClassId)) ||
            problems.Select(option => option.Id).Distinct().Count() != problems.Length)
            throw new ArgumentException("Problem options require unique IDs and a registered Class.", nameof(value));
        if (timings.Any(option => string.IsNullOrWhiteSpace(option.DisplayName) || !classIds.Contains(option.ClassId)) ||
            timings.Select(option => option.Id).Distinct().Count() != timings.Length)
            throw new ArgumentException("Timing options require unique IDs and a registered Class.", nameof(value));
        if (goals.Any(option => string.IsNullOrWhiteSpace(option.DisplayName)) ||
            goals.Select(option => option.Id).Distinct().Count() != goals.Length)
            throw new ArgumentException("Goal options require unique IDs and display names.", nameof(value));
        if (references.Any(option => string.IsNullOrWhiteSpace(option.DisplayName)) ||
            references.Select(option => option.Id).Distinct().Count() != references.Length)
            throw new ArgumentException("Reference-context options require unique IDs and display names.", nameof(value));
    }

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

}
