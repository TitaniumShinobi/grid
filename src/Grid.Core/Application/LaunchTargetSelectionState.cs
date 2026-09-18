using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Application;

public enum LaunchConfigurationFailure
{
    None,
    ContextUnavailable,
    TargetNotFound,
    TargetUnavailable,
    NotConfigurable,
    InvalidExecutablePath,
    InvalidWorkingDirectory,
    InvalidEnvironmentPolicy,
    InvalidArgument,
    TooManyArguments,
}

public readonly record struct LaunchConfigurationResult(
    bool Succeeded,
    LaunchConfigurationFailure Failure,
    LaunchTargetId? AffectedTargetId)
{
    public static LaunchConfigurationResult Applied(LaunchTargetId targetId) =>
        new(true, LaunchConfigurationFailure.None, targetId);

    public static LaunchConfigurationResult Rejected(LaunchConfigurationFailure failure) =>
        new(false, failure, null);
}

public sealed record LaunchConfigurationDraft(
    ConfiguredPathAnchor ExecutableAnchor,
    string ExecutablePath,
    ConfiguredPathAnchor WorkingDirectoryAnchor,
    string WorkingDirectory,
    EnvironmentPolicy EnvironmentPolicy,
    ImmutableArray<CommandArgument> Arguments);

public sealed record LaunchSafetyGateResult(
    LaunchSafetyGateKind Kind,
    LaunchGateDisposition Disposition,
    string Label,
    string Detail);

public sealed record LaunchCommandPreview(
    ConfiguredPath? Executable,
    ConfiguredPath? WorkingDirectory,
    ImmutableArray<CommandArgument> Arguments,
    EnvironmentPolicy? EnvironmentPolicy,
    GridInternalRoute? InternalRoute);

public sealed record ResolvedLaunchTarget(
    LaunchTargetDefinition Definition,
    ToolDefinition? Tool,
    AvailabilityState Availability,
    string? UnavailableReason,
    LaunchCommandPreview? Command,
    ImmutableArray<LaunchSafetyGateResult> SafetyGates)
{
    public bool CanPreview => Availability != AvailabilityState.Unavailable && Command is not null;

    public bool IsBlockedForFutureExecution =>
        SafetyGates.Any(gate => gate.Disposition == LaunchGateDisposition.Blocking);

    public string CategoryLabel => Definition.Kind switch
    {
        LaunchTargetKind.Game => "Game launch target",
        LaunchTargetKind.GridInternal => "Grid internal route",
        _ when Tool?.Kind == ToolKind.BuildTool => "Build tool",
        _ => "External utility",
    };

    public string PresentationStatus => $"{CategoryLabel} · {Availability}";
}

public sealed record LaunchPreview(
    string GameName,
    string? InstallationName,
    string? ProfileName,
    string AdapterName,
    ResolvedLaunchTarget Target,
    string NoExecutionDisclosure);

public sealed class LaunchTargetSelectionState
{
    public const int MaximumPathLength = 1024;
    public const int MaximumArgumentCount = 64;
    public const int MaximumArgumentLength = 512;

    private readonly string _baseRevision;
    private WorkspaceSelection _context;
    private int _revisionNumber;

    public LaunchTargetSelectionState(ShellNavigationState shell)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _baseRevision = shell.Workspace.Catalog.Revision;
        SynchronizeContext();
    }

    public ShellNavigationState Shell { get; }

    public GridCatalogSnapshot Catalog => Shell.Workspace.Catalog;

    public LaunchTargetId? SelectedTargetId { get; private set; }

    public void SynchronizeContext()
    {
        var selection = Shell.CurrentSelection;
        var targets = ResolveTargetsWithoutSynchronization();
        if (_context == selection &&
            SelectedTargetId is LaunchTargetId selectedTargetId &&
            targets.Any(target => target.Definition.Id == selectedTargetId))
        {
            return;
        }

        _context = selection;
        var profile = FindCurrentProfile();
        SelectedTargetId = profile?.DefaultLaunchTargetId is LaunchTargetId defaultTargetId &&
            targets.Any(target => target.Definition.Id == defaultTargetId)
                ? defaultTargetId
                : targets.FirstOrDefault(target => target.CanPreview)?.Definition.Id ??
                  targets.FirstOrDefault()?.Definition.Id;
    }

    public ImmutableArray<ResolvedLaunchTarget> GetTargets()
    {
        SynchronizeContext();
        return ResolveTargetsWithoutSynchronization();
    }

    public ResolvedLaunchTarget? GetSelectedTarget()
    {
        SynchronizeContext();
        return SelectedTargetId is LaunchTargetId targetId
            ? ResolveTargetsWithoutSynchronization().FirstOrDefault(target => target.Definition.Id == targetId)
            : null;
    }

    public void SelectTarget(LaunchTargetId? targetId)
    {
        SynchronizeContext();
        SelectedTargetId = targetId is LaunchTargetId requested &&
            ResolveTargetsWithoutSynchronization().Any(target => target.Definition.Id == requested)
                ? requested
                : null;
    }

    public LaunchPreview? CreatePreview()
    {
        var target = GetSelectedTarget();
        var game = FindCurrentGame();
        if (target is null || !target.CanPreview || game is null)
        {
            return null;
        }

        var installation = FindCurrentInstallation();
        var profile = FindCurrentProfile();
        var adapterName = installation is null
            ? game.Adapters.FirstOrDefault()?.Name ?? "Unknown adapter"
            : game.Adapters.First(adapter => adapter.Id == installation.AdapterId).Name;
        return new LaunchPreview(
            game.Name,
            installation?.Name,
            profile?.Name,
            adapterName,
            target,
            "PREVIEW ONLY · NO PROCESS STARTED · NO FILES OR EXTERNAL STATE CHANGED");
    }

    public LaunchConfigurationResult SaveConfiguration(
        LaunchTargetId targetId,
        LaunchConfigurationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var contextFailure = ResolveWritableContext(out var game, out var installation);
        if (contextFailure != LaunchConfigurationFailure.None)
        {
            return LaunchConfigurationResult.Rejected(contextFailure);
        }

        var resolved = GetTargets().FirstOrDefault(target => target.Definition.Id == targetId);
        if (resolved is null)
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.TargetNotFound);
        }

        if (!resolved.CanPreview)
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.TargetUnavailable);
        }

        if (resolved.Definition.Kind == LaunchTargetKind.GridInternal)
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.NotConfigurable);
        }

        var executableFailure = NormalizePath(
            draft.ExecutableAnchor,
            draft.ExecutablePath,
            LaunchConfigurationFailure.InvalidExecutablePath,
            out var executable);
        if (executableFailure != LaunchConfigurationFailure.None)
        {
            return LaunchConfigurationResult.Rejected(executableFailure);
        }

        var workingDirectoryFailure = NormalizePath(
            draft.WorkingDirectoryAnchor,
            draft.WorkingDirectory,
            LaunchConfigurationFailure.InvalidWorkingDirectory,
            out var workingDirectory);
        if (workingDirectoryFailure != LaunchConfigurationFailure.None)
        {
            return LaunchConfigurationResult.Rejected(workingDirectoryFailure);
        }

        if (!Enum.IsDefined(draft.EnvironmentPolicy))
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.InvalidEnvironmentPolicy);
        }

        var argumentFailure = NormalizeArguments(draft.Arguments, out var arguments);
        if (argumentFailure != LaunchConfigurationFailure.None)
        {
            return LaunchConfigurationResult.Rejected(argumentFailure);
        }

        var command = new ExecutableConfiguration(executable, workingDirectory, draft.EnvironmentPolicy);
        var currentInstallation = installation!;
        var targetConfiguration = currentInstallation.LaunchTargetConfigurations
            .First(configuration => configuration.LaunchTargetId == targetId);
        var updatedTargetConfiguration = targetConfiguration with { Arguments = arguments };
        var updatedToolConfigurations = currentInstallation.ToolConfigurations;
        if (resolved.Definition.Kind == LaunchTargetKind.Tool && resolved.Definition.ToolId is ToolId toolId)
        {
            var toolConfiguration = currentInstallation.ToolConfigurations
                .First(configuration => configuration.ToolId == toolId);
            updatedToolConfigurations = updatedToolConfigurations.SetItem(
                updatedToolConfigurations.IndexOf(toolConfiguration),
                toolConfiguration with { Command = command });
        }
        else
        {
            updatedTargetConfiguration = updatedTargetConfiguration with { GameCommand = command };
        }

        var updatedInstallation = currentInstallation with
        {
            ToolConfigurations = updatedToolConfigurations,
            LaunchTargetConfigurations = currentInstallation.LaunchTargetConfigurations.SetItem(
                currentInstallation.LaunchTargetConfigurations.IndexOf(targetConfiguration),
                updatedTargetConfiguration),
        };
        Apply(game!, updatedInstallation);
        SelectedTargetId = targetId;
        return LaunchConfigurationResult.Applied(targetId);
    }

    public LaunchConfigurationResult SetProfileDefault(LaunchTargetId targetId)
    {
        var contextFailure = ResolveWritableContext(out var game, out var installation);
        if (contextFailure != LaunchConfigurationFailure.None || FindCurrentProfile() is not Profile profile)
        {
            return LaunchConfigurationResult.Rejected(contextFailure == LaunchConfigurationFailure.None
                ? LaunchConfigurationFailure.ContextUnavailable
                : contextFailure);
        }

        var target = GetTargets().FirstOrDefault(candidate => candidate.Definition.Id == targetId);
        if (target is null)
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.TargetNotFound);
        }

        if (!target.CanPreview)
        {
            return LaunchConfigurationResult.Rejected(LaunchConfigurationFailure.TargetUnavailable);
        }

        var currentInstallation = installation!;
        var updatedProfile = profile with { DefaultLaunchTargetId = targetId };
        var updatedInstallation = currentInstallation with
        {
            Profiles = currentInstallation.Profiles.SetItem(
                currentInstallation.Profiles.IndexOf(profile),
                updatedProfile),
        };
        Apply(game!, updatedInstallation);
        SelectedTargetId = targetId;
        return LaunchConfigurationResult.Applied(targetId);
    }

    private ImmutableArray<ResolvedLaunchTarget> ResolveTargetsWithoutSynchronization()
    {
        var game = FindCurrentGame();
        if (game is null)
        {
            return [];
        }

        var installation = FindCurrentInstallation();
        var definitions = installation is null
            ? game.ToolCatalog.LaunchTargets
            : game.ToolCatalog.LaunchTargets
                .Where(definition => definition.AdapterIds.Contains(installation.AdapterId))
                .ToImmutableArray();
        return definitions.Select(definition => Resolve(game, installation, FindCurrentProfile(), definition)).ToImmutableArray();
    }

    private static ResolvedLaunchTarget Resolve(
        ManagedGame game,
        ManagedInstallation? installation,
        Profile? profile,
        LaunchTargetDefinition definition)
    {
        var tool = definition.ToolId is ToolId toolId
            ? game.ToolCatalog.Tools.FirstOrDefault(candidate => candidate.Id == toolId)
            : null;
        if (installation is null)
        {
            return Unavailable(definition, tool, "No managed installation is represented for this game.", profile);
        }

        if (installation.Metadata.Availability != InstallationAvailability.Available)
        {
            return Unavailable(definition, tool, $"Installation is {installation.Metadata.Availability.ToString().ToLowerInvariant()}.", profile);
        }

        var targetConfiguration = installation.LaunchTargetConfigurations
            .FirstOrDefault(configuration => configuration.LaunchTargetId == definition.Id);
        if (targetConfiguration is null)
        {
            return Unavailable(definition, tool, "The selected adapter has no installation binding for this target.", profile);
        }

        if (targetConfiguration.Availability == AvailabilityState.Unavailable)
        {
            return Unavailable(
                definition,
                tool,
                targetConfiguration.UnavailableReason ?? "Target configuration is unavailable.",
                profile);
        }

        LaunchCommandPreview? command;
        var effectiveAvailability = targetConfiguration.Availability;
        if (definition.Kind == LaunchTargetKind.GridInternal)
        {
            command = new LaunchCommandPreview(
                null,
                null,
                ImmutableArray<CommandArgument>.Empty,
                EnvironmentPolicy.GridInternal,
                definition.InternalRoute);
        }
        else if (definition.Kind == LaunchTargetKind.Game)
        {
            command = targetConfiguration.GameCommand is ExecutableConfiguration gameCommand
                ? new LaunchCommandPreview(
                    gameCommand.Executable,
                    gameCommand.WorkingDirectory,
                    targetConfiguration.Arguments,
                    gameCommand.EnvironmentPolicy,
                    null)
                : null;
        }
        else
        {
            var toolConfiguration = definition.ToolId is ToolId configuredToolId
                ? installation.ToolConfigurations.FirstOrDefault(configuration => configuration.ToolId == configuredToolId)
                : null;
            if (toolConfiguration is null ||
                toolConfiguration.Availability == AvailabilityState.Unavailable ||
                toolConfiguration.Command is null)
            {
                return Unavailable(
                    definition,
                    tool,
                    toolConfiguration?.UnavailableReason ?? "The referenced tool is not configured for this installation.",
                    profile);
            }

            if (toolConfiguration.Availability == AvailabilityState.PreviewOnly)
            {
                effectiveAvailability = AvailabilityState.PreviewOnly;
            }

            command = new LaunchCommandPreview(
                toolConfiguration.Command.Executable,
                toolConfiguration.Command.WorkingDirectory,
                targetConfiguration.Arguments,
                toolConfiguration.Command.EnvironmentPolicy,
                null);
        }

        return command is null
            ? Unavailable(definition, tool, "No exact structured command can be resolved.", profile)
            : new ResolvedLaunchTarget(
                definition,
                tool,
                effectiveAvailability,
                null,
                command,
                EvaluateSafety(definition.SafetyPolicy, profile));
    }

    private static ResolvedLaunchTarget Unavailable(
        LaunchTargetDefinition definition,
        ToolDefinition? tool,
        string reason,
        Profile? profile) =>
        new(
            definition,
            tool,
            AvailabilityState.Unavailable,
            reason,
            null,
            EvaluateSafety(definition.SafetyPolicy, profile));

    private static ImmutableArray<LaunchSafetyGateResult> EvaluateSafety(
        LaunchSafetyPolicy policy,
        Profile? profile)
    {
        var gates = new List<LaunchSafetyGateResult>();
        if (policy.RequiresProfileStateValidation)
        {
            gates.Add(profile?.LaunchReadiness.ProfileState switch
            {
                ProfileValidationState.Valid => Gate(LaunchSafetyGateKind.ProfileState, LaunchGateDisposition.Satisfied, "Profile state validated", "Represented profile validation passed."),
                ProfileValidationState.Advisory => Gate(LaunchSafetyGateKind.ProfileState, LaunchGateDisposition.Warning, "Profile validation advisory", "Represented profile validation requires review."),
                ProfileValidationState.Invalid => Gate(LaunchSafetyGateKind.ProfileState, LaunchGateDisposition.Blocking, "Profile state invalid", "Future execution would remain blocked."),
                _ => Gate(LaunchSafetyGateKind.ProfileState, LaunchGateDisposition.Blocking, "Profile state unverified", "No valid profile-state evidence is available."),
            });
        }

        if (policy.ObservesPendingDeployment)
        {
            gates.Add(profile?.LaunchReadiness.PendingDeployment switch
            {
                PendingChangeState.None => Gate(LaunchSafetyGateKind.PendingDeployment, LaunchGateDisposition.Satisfied, "No pending deployment", "Represented deployment state is clear."),
                PendingChangeState.Pending => Gate(LaunchSafetyGateKind.PendingDeployment, LaunchGateDisposition.Warning, "Pending deployment warning", "Represented deployment changes should be reviewed."),
                _ => Gate(LaunchSafetyGateKind.PendingDeployment, LaunchGateDisposition.Warning, "Deployment state unknown", "Future adapters must refresh deployment evidence."),
            });
        }

        if (policy.ObservesPendingOutputs)
        {
            gates.Add(profile?.LaunchReadiness.PendingOutputs switch
            {
                PendingChangeState.None => Gate(LaunchSafetyGateKind.PendingOutputs, LaunchGateDisposition.Satisfied, "Outputs represented current", "No pending generated outputs are represented."),
                PendingChangeState.Pending => Gate(LaunchSafetyGateKind.PendingOutputs, LaunchGateDisposition.Warning, "Pending output warning", "Represented generated outputs should be reviewed."),
                _ => Gate(LaunchSafetyGateKind.PendingOutputs, LaunchGateDisposition.Warning, "Output state unknown", "Future adapters must refresh output evidence."),
            });
        }

        if (policy.RequiresOnlineCleanVerification)
        {
            gates.Add(profile?.LaunchReadiness.OnlineCleanVerification == OnlineCleanVerificationState.Verified
                ? Gate(LaunchSafetyGateKind.OnlineCleanVerification, LaunchGateDisposition.Satisfied, "Online-clean state verified", "Adapter-provided verification is represented as current.")
                : Gate(LaunchSafetyGateKind.OnlineCleanVerification, LaunchGateDisposition.Blocking, "Online-clean verification required", "Purging a Story profile is never sufficient; fresh adapter verification is mandatory."));
        }

        if (policy.RecommendsSnapshot)
        {
            gates.Add(Gate(LaunchSafetyGateKind.Snapshot, LaunchGateDisposition.Recommendation, "Snapshot recommended", "A verified scoped snapshot is recommended before future execution."));
        }

        if (policy.RequiresApproval)
        {
            gates.Add(Gate(LaunchSafetyGateKind.Approval, LaunchGateDisposition.RequiredAtExecution, "Approval required at execution", "Preview does not request or grant approval."));
        }

        return gates.ToImmutableArray();
    }

    private static LaunchSafetyGateResult Gate(
        LaunchSafetyGateKind kind,
        LaunchGateDisposition disposition,
        string label,
        string detail) =>
        new(kind, disposition, label, detail);

    private LaunchConfigurationFailure ResolveWritableContext(
        out ManagedGame? game,
        out ManagedInstallation? installation)
    {
        game = FindCurrentGame();
        installation = FindCurrentInstallation();
        return game is null ||
            installation is null ||
            installation.Metadata.Provenance != InstallationProvenanceKind.Mock ||
            installation.Metadata.Availability != InstallationAvailability.Available
                ? LaunchConfigurationFailure.ContextUnavailable
                : LaunchConfigurationFailure.None;
    }

    private static LaunchConfigurationFailure NormalizePath(
        ConfiguredPathAnchor anchor,
        string? requestedPath,
        LaunchConfigurationFailure failure,
        out ConfiguredPath path)
    {
        path = default;
        if (!Enum.IsDefined(anchor))
        {
            return failure;
        }

        var value = requestedPath?.Trim();
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaximumPathLength ||
            value.Any(char.IsControl) ||
            value.StartsWith('/') ||
            value.StartsWith('\\') ||
            value.Contains(':'))
        {
            return failure;
        }

        var segments = value.Split(['/', '\\'], StringSplitOptions.None);
        if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            return failure;
        }

        path = new ConfiguredPath(anchor, value);
        return LaunchConfigurationFailure.None;
    }

    private static LaunchConfigurationFailure NormalizeArguments(
        ImmutableArray<CommandArgument> requestedArguments,
        out ImmutableArray<CommandArgument> arguments)
    {
        arguments = [];
        if (requestedArguments.IsDefault)
        {
            return LaunchConfigurationFailure.InvalidArgument;
        }

        if (requestedArguments.Length > MaximumArgumentCount)
        {
            return LaunchConfigurationFailure.TooManyArguments;
        }

        var normalized = new List<CommandArgument>();
        foreach (var requestedArgument in requestedArguments)
        {
            var value = requestedArgument.Value?.Trim();
            if (string.IsNullOrEmpty(value) ||
                value.Length > MaximumArgumentLength ||
                value.Any(char.IsControl))
            {
                return LaunchConfigurationFailure.InvalidArgument;
            }

            normalized.Add(new CommandArgument(value));
        }

        arguments = normalized.ToImmutableArray();
        return LaunchConfigurationFailure.None;
    }

    private void Apply(ManagedGame game, ManagedInstallation updatedInstallation)
    {
        var gameIndex = Catalog.Games.IndexOf(game);
        var installation = game.Installations.First(candidate => candidate.Id == updatedInstallation.Id);
        var updatedGame = game with
        {
            Installations = game.Installations.SetItem(game.Installations.IndexOf(installation), updatedInstallation),
        };
        var updatedCatalog = new GridCatalogSnapshot(
            $"{_baseRevision}.launch-{++_revisionNumber:D3}",
            Catalog.SourceKind,
            Catalog.Games.SetItem(gameIndex, updatedGame));
        Shell.ReplaceCatalog(updatedCatalog);
    }

    private ManagedGame? FindCurrentGame() => Shell.CurrentSelection.GameId is GameId gameId
        ? Catalog.Games.FirstOrDefault(game => game.Id == gameId)
        : null;

    private ManagedInstallation? FindCurrentInstallation()
    {
        var game = FindCurrentGame();
        return game is not null && Shell.CurrentSelection.InstallationId is InstallationId installationId
            ? game.Installations.FirstOrDefault(installation => installation.Id == installationId)
            : null;
    }

    private Profile? FindCurrentProfile()
    {
        var installation = FindCurrentInstallation();
        return installation is not null && Shell.CurrentSelection.ProfileId is ProfileId profileId
            ? installation.Profiles.FirstOrDefault(profile => profile.Id == profileId)
            : null;
    }
}
