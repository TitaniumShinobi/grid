using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public enum OperatorSubmissionFailure
{
    None,
    EmptyMessage,
    MessageTooLong,
    InvalidCharacters,
}

public readonly record struct OperatorSubmissionResult(
    bool Succeeded,
    OperatorSubmissionFailure Failure)
{
    public static OperatorSubmissionResult Applied { get; } = new(true, OperatorSubmissionFailure.None);

    public static OperatorSubmissionResult Rejected(OperatorSubmissionFailure failure) => new(false, failure);
}

public sealed class MockOperatorSessionState
{
    public const double CollapsedPanelWidth = 48;
    public const double DefaultPanelWidth = 400;
    public const double MinimumPanelWidth = 320;
    public const double MaximumPanelWidth = 560;
    public const int MaximumUserTextLength = 2000;
    public const int MaximumContextSubjects = 10;
    public const int MaximumContextAdvisories = 5;
    public const int MaximumRecentActivity = 5;
    public const int MaximumServiceHistory = 12;

    private static readonly DateTimeOffset RepresentedTimeBase =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<OperatorContextKey, List<OperatorMessage>> _histories = [];
    private readonly Dictionary<OperatorContextKey, int> _transcriptRevisions = [];
    private readonly List<OperatorAuditRecord> _auditRecords = [];
    private readonly IAiOperatorService _service;
    private int _auditNumber;
    private int _messageNumber;
    private int _proposalNumber;
    private int _representedMinute;

    public MockOperatorSessionState(
        WorkspaceSessionState workspace,
        LaunchTargetSelectionState launchTargets,
        IAiOperatorService service,
        WorkspaceToolOutputState? toolOutputs = null)
    {
        Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        LaunchTargets = launchTargets ?? throw new ArgumentNullException(nameof(launchTargets));
        ToolOutputs = toolOutputs;
        _service = service ?? throw new ArgumentNullException(nameof(service));
        if (!ReferenceEquals(Workspace.Shell, LaunchTargets.Shell))
        {
            throw new ArgumentException("Workspace and launch-target state must share one shell context.");
        }

        CurrentContext = BuildContext();
        ActivateContext("Initial structured context activated.");
    }

    public WorkspaceSessionState Workspace { get; }

    public LaunchTargetSelectionState LaunchTargets { get; }

    public WorkspaceToolOutputState? ToolOutputs { get; }

    public bool IsExpanded { get; private set; }

    public double PanelWidth { get; private set; } = DefaultPanelWidth;

    public OperatorContextSnapshot CurrentContext { get; private set; }

    public void TogglePanel() => IsExpanded = !IsExpanded;

    public void ExpandPanel() => IsExpanded = true;

    public void CollapsePanel() => IsExpanded = false;

    public double ResizePanel(double requestedWidth)
    {
        PanelWidth = Math.Clamp(requestedWidth, MinimumPanelWidth, MaximumPanelWidth);
        return PanelWidth;
    }

    public OperatorContextSnapshot SynchronizeContext()
    {
        Workspace.SynchronizeContext();
        LaunchTargets.SynchronizeContext();
        var previous = CurrentContext;
        CurrentContext = BuildContext();
        if (previous.Key != CurrentContext.Key || previous.Fingerprint != CurrentContext.Fingerprint)
        {
            ActivateContext(previous.Key == CurrentContext.Key
                ? "Structured selection context changed."
                : "Game, installation, or profile context changed.");
        }
        else
        {
            EnsureHistory(CurrentContext);
        }

        return CurrentContext;
    }

    public OperatorTranscriptSnapshot GetCurrentTranscript()
    {
        EnsureHistory(CurrentContext);
        return new OperatorTranscriptSnapshot(
            CurrentContext.Key,
            _transcriptRevisions[CurrentContext.Key],
            _histories[CurrentContext.Key].ToImmutableArray());
    }

    public ImmutableArray<OperatorAuditRecord> GetAuditRecords() => _auditRecords.ToImmutableArray();

    public bool IsProposalStale(OperatorProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return proposal.ContextFingerprint != CurrentContext.Fingerprint;
    }

    public async Task<OperatorSubmissionResult> SubmitAsync(
        string? userText,
        CancellationToken cancellationToken = default)
    {
        var normalized = userText?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return OperatorSubmissionResult.Rejected(OperatorSubmissionFailure.EmptyMessage);
        }

        if (normalized.Length > MaximumUserTextLength)
        {
            return OperatorSubmissionResult.Rejected(OperatorSubmissionFailure.MessageTooLong);
        }

        if (normalized.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            return OperatorSubmissionResult.Rejected(OperatorSubmissionFailure.InvalidCharacters);
        }

        SynchronizeContext();
        var capturedContext = CurrentContext;
        EnsureHistory(capturedContext);
        var recentHistory = _histories[capturedContext.Key]
            .TakeLast(MaximumServiceHistory)
            .ToImmutableArray();
        var response = await _service.RespondAsync(
            new OperatorRequest(capturedContext, recentHistory, normalized),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var statements = response.Statements.IsDefault ? [] : response.Statements;
        OperatorProposal? proposal = null;
        if (response.ProposedActionId is DeterministicActionId proposedActionId)
        {
            var action = capturedContext.AvailableActions.FirstOrDefault(candidate =>
                candidate.Id == proposedActionId &&
                candidate.Availability != OperatorActionAvailability.Unavailable);
            if (action is null)
            {
                statements = statements.Add(new OperatorStatement(
                    OperatorContentKind.Warning,
                    "Rejected service output",
                    "The mock response referenced an action outside the supplied deterministic action set. No proposal was created."));
                AppendAudit(
                    OperatorAuditEventKind.ServiceResponseRejected,
                    capturedContext,
                    $"Rejected unknown or unavailable action ID {proposedActionId.Value}.");
            }
            else
            {
                proposal = new OperatorProposal(
                    new OperatorProposalId($"operator-proposal-{++_proposalNumber:D4}"),
                    action.Id,
                    capturedContext.Fingerprint,
                    action.Title,
                    action.Preview,
                    action.ExpectedEffects,
                    action.Exclusions,
                    action.Verification,
                    action.RollbackAvailability,
                    action.RollbackDetail,
                    "APPROVAL REQUIRED · UNAVAILABLE IN MOCK PHASE",
                    "EXECUTION UNAVAILABLE · PRESENTATION ONLY");
            }
        }

        var history = _histories[capturedContext.Key];
        history.Add(CreateMessage(
            OperatorMessageRole.User,
            normalized,
            [],
            null,
            capturedContext.Fingerprint));
        AppendAudit(OperatorAuditEventKind.UserMessageRecorded, capturedContext, "Inert user text recorded in the mock transcript.");

        history.Add(CreateMessage(
            OperatorMessageRole.Operator,
            response.Body,
            statements,
            proposal,
            capturedContext.Fingerprint));
        AppendAudit(OperatorAuditEventKind.MockResponseRecorded, capturedContext, "Deterministic mock response recorded.");
        if (proposal is not null)
        {
            AppendAudit(
                OperatorAuditEventKind.ProposalPresented,
                capturedContext,
                $"Proposal {proposal.Id.Value} presented without approval or execution.");
        }

        _transcriptRevisions[capturedContext.Key]++;
        return OperatorSubmissionResult.Applied;
    }

    public OperatorSessionSnapshot ExportSnapshot()
    {
        var transcripts = _histories
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => new OperatorTranscriptSnapshot(
                pair.Key,
                _transcriptRevisions[pair.Key],
                pair.Value.ToImmutableArray()))
            .ToImmutableArray();
        return new OperatorSessionSnapshot(
            IsExpanded,
            PanelWidth,
            CurrentContext,
            transcripts,
            _auditRecords.ToImmutableArray());
    }

    private OperatorContextSnapshot BuildContext()
    {
        var catalog = Workspace.Catalog;
        var selection = Workspace.Shell.CurrentSelection;
        var game = selection.GameId is GameId gameId
            ? catalog.Games.FirstOrDefault(candidate => candidate.Id == gameId)
            : null;
        var installation = game is not null && selection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(candidate => candidate.Id == installationId)
            : null;
        var profile = installation is not null && selection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(candidate => candidate.Id == profileId)
            : null;
        var selectedTarget = LaunchTargets.GetSelectedTarget();

        var allSubjects = CreateSubjects(profile);
        var subjects = allSubjects.Take(MaximumContextSubjects).ToImmutableArray();
        var omittedSubjectCount = Math.Max(0, allSubjects.Length - subjects.Length);
        var healthSource = profile?.Health ?? installation?.Health ?? game?.Health ??
            new HealthSummary(HealthLevel.Unknown, "No selected managed-game context", []);
        var health = healthSource with
        {
            Advisories = healthSource.Advisories.Take(MaximumContextAdvisories).ToImmutableArray(),
        };
        var activity = CreateRecentActivity(profile);
        var actions = CreateActions(game, installation, profile, subjects, selectedTarget);
        var key = new OperatorContextKey(selection.GameId, selection.InstallationId, selection.ProfileId);
        var fingerprint = CreateFingerprint(
            catalog.Revision,
            key,
            subjects,
            health,
            actions,
            selectedTarget?.Definition.Id);

        return new OperatorContextSnapshot(
            key,
            fingerprint,
            catalog.Revision,
            catalog.SourceKind,
            game?.Name ?? "No game selected",
            installation?.Name,
            profile?.Name,
            selectedTarget?.Definition.Name,
            subjects,
            omittedSubjectCount,
            health,
            activity,
            actions,
            new OperatorPermissionState(
                CanReadContext: true,
                CanDraftProposals: true,
                CanApprove: false,
                CanExecute: false,
                Summary: "READ ONLY · PROPOSALS ALLOWED · APPROVAL AND EXECUTION UNAVAILABLE"));
    }

    private ImmutableArray<OperatorSubject> CreateSubjects(Profile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var subjects = new List<OperatorSubject>();
        subjects.AddRange(profile.Mods
            .Where(mod => Workspace.SelectedModIds.Contains(mod.Id))
            .OrderBy(mod => mod.Inventory?.DisplayOrder ?? mod.Priority ?? int.MaxValue)
            .Select(mod => new OperatorSubject(
                OperatorSubjectKind.Mod,
                mod.Id.Value,
                mod.Name,
                CreateModSubjectDetail(mod))));

        if (Workspace.SelectedResolvedEnvironment is ResolvedEnvironmentSelection resolved)
        {
            subjects.Add(new OperatorSubject(
                resolved.Kind switch
                {
                    ResolvedSelectionKind.Plugin => OperatorSubjectKind.Plugin,
                    ResolvedSelectionKind.Archive => OperatorSubjectKind.Archive,
                    ResolvedSelectionKind.VirtualPath => OperatorSubjectKind.DataFile,
                    _ => throw new ArgumentOutOfRangeException(nameof(resolved)),
                },
                resolved.StableId,
                resolved.Name,
                resolved.Detail));
        }

        if (Workspace.SelectedResolvedEnvironment?.Kind != ResolvedSelectionKind.Plugin &&
            Workspace.SelectedPluginId is PluginId pluginId)
        {
            var plugin = profile.Plugins.FirstOrDefault(candidate => candidate.Id == pluginId);
            if (plugin is not null)
            {
                subjects.Add(new OperatorSubject(
                    OperatorSubjectKind.Plugin,
                    plugin.Id.Value,
                    plugin.Name,
                    $"Plugin load order {plugin.LoadOrder} · {plugin.Health} represented health"));
            }
        }

        if (Workspace.SelectedEnvironmentEntryId is EnvironmentEntryId entryId)
        {
            var entry = profile.EnvironmentEntries.FirstOrDefault(candidate => candidate.Id == entryId);
            if (entry is not null)
            {
                subjects.Add(new OperatorSubject(
                    ToSubjectKind(entry.Tab),
                    entry.Id.Value,
                    entry.Name,
                    $"{entry.Status} · {entry.Health} represented health"));
            }
        }

        if (ToolOutputs?.GetSelectedOutput() is GeneratedOutputSummary output)
        {
            subjects.Add(new OperatorSubject(
                OperatorSubjectKind.Output,
                output.Id.Value,
                output.Name,
                $"Read-only output observation · {output.Availability} · {output.FingerprintStrength} fingerprint evidence"));
        }

        return subjects.ToImmutableArray();
    }

    private ImmutableArray<OperatorActivityEvidence> CreateRecentActivity(Profile? profile)
    {
        if (profile is null)
        {
            return [];
        }

        var selectedModIds = Workspace.SelectedModIds;
        return profile.EnvironmentEntries
            .Where(entry => entry.Tab == EnvironmentTabCapability.Activity)
            .OrderByDescending(entry => entry.RelatedModIds.Any(selectedModIds.Contains))
            .ThenBy(entry => entry.Id.Value, StringComparer.Ordinal)
            .Take(MaximumRecentActivity)
            .Select(entry => new OperatorActivityEvidence(entry.Id, entry.Name, entry.Detail, entry.Status))
            .ToImmutableArray();
    }

    private ImmutableArray<DeterministicActionDescriptor> CreateActions(
        ManagedGame? game,
        ManagedInstallation? installation,
        Profile? profile,
        ImmutableArray<OperatorSubject> subjects,
        ResolvedLaunchTarget? selectedTarget)
    {
        if (game is null)
        {
            return [];
        }

        var actions = new List<DeterministicActionDescriptor>
        {
            ReadOnlyAction(
                "action.operator.review-health",
                "Review represented health",
                "Organize the supplied health and advisory records without inspecting external state."),
        };

        if (!subjects.IsEmpty)
        {
            actions.Add(ReadOnlyAction(
                "action.operator.review-selection",
                "Review selected evidence",
                "Explain the currently selected typed evidence without opening files or changing state."));
        }

        if (profile is not null &&
            installation?.Metadata.Provenance == InstallationProvenanceKind.Mock &&
            !Workspace.SelectedModIds.IsEmpty)
        {
            actions.Add(MockMutationAction(
                "action.workspace.enable-selected-mods",
                "Propose enabling selected mods",
                "Preview an in-memory enablement change for the selected mod IDs."));
            actions.Add(MockMutationAction(
                "action.workspace.disable-selected-mods",
                "Propose disabling selected mods",
                "Preview an in-memory disablement change for the selected mod IDs."));

            if (Workspace.SelectedModIds.Count == 1 && Workspace.Query.IsUnfilteredPriorityView)
            {
                actions.Add(MockMutationAction(
                    "action.workspace.move-selected-mod",
                    "Propose a mod-priority move",
                    "Preview a bounded priority change for the single selected mod."));
            }
        }

        if (installation?.Metadata.Availability == InstallationAvailability.Available &&
            selectedTarget?.CanPreview == true)
        {
            actions.Add(new DeterministicActionDescriptor(
                new DeterministicActionId("action.launch.preview-selected-target"),
                "Preview selected launch target",
                $"Show the existing structured preview for {selectedTarget.Definition.Name}; start no process.",
                OperatorActionAvailability.PreviewOnly,
                ImmutableArray.Create("Present anchored command metadata or an internal route."),
                ImmutableArray.Create("No process start.", "No shell command.", "No approval or execution."),
                "Verify that the displayed target remains bound to the same installation/profile context.",
                OperatorRollbackAvailability.NotApplicable,
                "A read-only preview changes no state."));
        }

        if (LaunchTargets.GetTargets().Any(target =>
            target.Definition.Kind == LaunchTargetKind.GridInternal && target.CanPreview))
        {
            actions.Add(ReadOnlyAction(
                "action.grid.open-diagnostics",
                "Review Grid diagnostics route",
                "Present the typed Grid-internal diagnostics route without external process execution."));
        }

        return actions.ToImmutableArray();
    }

    private static DeterministicActionDescriptor ReadOnlyAction(string id, string title, string preview) =>
        new(
            new DeterministicActionId(id),
            title,
            preview,
            OperatorActionAvailability.ReadOnly,
            ImmutableArray.Create("Present structured represented evidence."),
            ImmutableArray.Create("No external inspection.", "No mutation.", "No approval or execution."),
            "Verify that every displayed claim maps to the captured structured context.",
            OperatorRollbackAvailability.NotApplicable,
            "Read-only presentation requires no rollback.");

    private static DeterministicActionDescriptor MockMutationAction(string id, string title, string preview) =>
        new(
            new DeterministicActionId(id),
            title,
            preview,
            OperatorActionAvailability.PreviewOnly,
            ImmutableArray.Create("Describe a bounded change for the selected stable mod IDs."),
            ImmutableArray.Create("No manager files changed.", "No automatic approval.", "No execution from the operator."),
            "A future deterministic route must verify postconditions against a fresh adapter snapshot.",
            OperatorRollbackAvailability.RequiredForExecution,
            "Future operational execution requires a verified scoped snapshot and rollback route.");

    private static string CreateModSubjectDetail(ModEntry mod)
    {
        if (mod.Inventory is not ModInventoryObservation inventory)
        {
            return $"Mod priority {mod.Priority?.ToString() ?? "unavailable"} · {mod.Health} represented health";
        }

        var priority = mod.Priority?.ToString() ?? "unavailable";
        var authority = inventory.Authority switch
        {
            ModInventoryAuthority.ManagerAuthoritative => "authoritative read-only manager observation",
            ModInventoryAuthority.GridDerived => "Grid-derived unlisted-directory observation",
            _ => "represented mock evidence",
        };
        return $"Mod priority {priority} · {inventory.Reconciliation} · {authority}";
    }

    private static OperatorSubjectKind ToSubjectKind(EnvironmentTabCapability tab) => tab switch
    {
        EnvironmentTabCapability.Archives => OperatorSubjectKind.Archive,
        EnvironmentTabCapability.Data => OperatorSubjectKind.DataFile,
        EnvironmentTabCapability.Saves => OperatorSubjectKind.Save,
        EnvironmentTabCapability.Downloads => OperatorSubjectKind.Download,
        EnvironmentTabCapability.Conflicts => OperatorSubjectKind.Conflict,
        EnvironmentTabCapability.Outputs => OperatorSubjectKind.Output,
        EnvironmentTabCapability.Activity => OperatorSubjectKind.Activity,
        _ => OperatorSubjectKind.DataFile,
    };

    private static OperatorContextFingerprint CreateFingerprint(
        string revision,
        OperatorContextKey key,
        ImmutableArray<OperatorSubject> subjects,
        HealthSummary health,
        ImmutableArray<DeterministicActionDescriptor> actions,
        LaunchTargetId? targetId)
    {
        var material = string.Join(
            "|",
            revision,
            key,
            targetId?.Value ?? "none",
            health.Level,
            string.Join(",", health.Advisories.Select(advisory => advisory.Code)),
            string.Join(",", subjects.Select(subject => $"{subject.Kind}:{subject.StableId}")),
            string.Join(",", actions.Select(action => action.Id.Value)));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return new OperatorContextFingerprint(Convert.ToHexString(hash).ToLowerInvariant()[..16]);
    }

    private void ActivateContext(string detail)
    {
        EnsureHistory(CurrentContext);
        AppendAudit(OperatorAuditEventKind.ContextActivated, CurrentContext, detail);
    }

    private void EnsureHistory(OperatorContextSnapshot context)
    {
        if (_histories.ContainsKey(context.Key))
        {
            return;
        }

        _histories.Add(context.Key, []);
        _transcriptRevisions.Add(context.Key, 1);
        _histories[context.Key].Add(CreateMessage(
            OperatorMessageRole.System,
            "Structured mock context ready. No model, file inspection, approval, or execution is connected.",
            ImmutableArray.Create(
                new OperatorStatement(
                    OperatorContentKind.Evidence,
                    "Context source",
                    $"Catalog {context.CatalogRevision} supplied {context.GameName} as deterministic {context.SourceKind} data."),
                new OperatorStatement(
                    OperatorContentKind.Warning,
                    "Read-only boundary",
                    context.Permissions.Summary)),
            null,
            context.Fingerprint));
    }

    private OperatorMessage CreateMessage(
        OperatorMessageRole role,
        string body,
        ImmutableArray<OperatorStatement> statements,
        OperatorProposal? proposal,
        OperatorContextFingerprint fingerprint) =>
        new(
            new OperatorMessageId($"operator-message-{++_messageNumber:D4}"),
            role,
            body,
            statements,
            proposal,
            fingerprint,
            NextRepresentedTime());

    private void AppendAudit(
        OperatorAuditEventKind kind,
        OperatorContextSnapshot context,
        string detail) =>
        _auditRecords.Add(new OperatorAuditRecord(
            new OperatorAuditId($"operator-audit-{++_auditNumber:D4}"),
            kind,
            context.Key,
            context.Fingerprint,
            detail,
            NextRepresentedTime()));

    private DateTimeOffset NextRepresentedTime() =>
        RepresentedTimeBase.AddMinutes(_representedMinute++);
}
