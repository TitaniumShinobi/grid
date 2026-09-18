using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

internal sealed class PowerShellAssistantRequestExecutionService : IAssistantRequestExecutionService
{
    private const int MaximumOutputCharacters = 16 * 1024 * 1024;
    private readonly string entryPoint;
    private readonly string gridDataRoot;
    private readonly string caseStoreRoot;
    private readonly string transactionRoot;
    private readonly string actorId;
    private readonly string sessionId = $"session-{Guid.NewGuid():N}";
    private readonly Dictionary<string, PreparedRequest> prepared = new(StringComparer.Ordinal);

    public PowerShellAssistantRequestExecutionService(string engineRoot, string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        entryPoint = Path.GetFullPath(Path.Combine(engineRoot, "scripts", "health", "Invoke-GridRequestSubmission.ps1"));
        gridDataRoot = Path.GetFullPath(dataRoot);
        caseStoreRoot = Path.GetFullPath(dataRoot);
        transactionRoot = Path.GetFullPath(Path.Combine(dataRoot, "requests"));
        actorId = CreateLocalActorId();
    }

    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(entryPoint);
    public bool RequiresToolSelection => true;

    public async Task<ImmutableArray<AssistantTaskRecord>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        using var result = await InvokeAsync(new { schemaVersion = 2, operation = "History", caseStoreRoot }, TimeSpan.FromMinutes(2), cancellationToken);
        return ParseTaskHistoryResponse(result.RootElement);
    }

    public async Task<AssistantAuthorizationReview> PrepareAsync(AssistantRequestDraft request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var submissionId = $"submission-{Guid.NewGuid():N}";
        var input = CreateInput("Prepare", request, submissionId, null);
        using var result = await InvokeAsync(input, TimeSpan.FromMinutes(2), cancellationToken);
        var root = result.RootElement;
        var envelope = root.GetProperty("requestEnvelope");
        var review = root.GetProperty("authorizationReview");
        var canonical = new AssistantCanonicalRequest(
            RequiredString(envelope, "requestId"), RequiredString(envelope, "envelopeSha256"),
            DateTimeOffset.Parse(RequiredString(envelope, "submittedAt")), request);
        var names = request.Tools.ToDictionary(tool => tool.ToolId, tool => tool.ProviderName);
        var scopes = review.GetProperty("scopes").EnumerateArray().Select(scope =>
        {
            var toolId = new ToolId(RequiredString(scope, "toolId"));
            return new AssistantReadScope(toolId, names.GetValueOrDefault(toolId, toolId.Value),
                OptionalString(scope, "adapter"), OptionalString(scope, "observationMode"), RequiredString(scope, "availability"),
                scope.GetProperty("exactReadPaths").EnumerateArray().Select(value => value.GetString()!).ToImmutableArray());
        }).ToImmutableArray();
        var semanticBinding = review.GetProperty("semanticBinding");
        var authorityClass = RequiredString(review, "authorityClass");
        var authorization = new AssistantAuthorizationReview(
            RequiredString(review, "reviewId"), RequiredString(semanticBinding, "submissionId"), canonical,
            RequiredString(semanticBinding, "planSha256"), scopes, RequiredString(review, "semanticBindingSha256"),
            !string.Equals(authorityClass, "Read", StringComparison.Ordinal),
            "Authorize only the displayed exact read paths. Papyrus attachments may launch the bundled read-only Grid diagnostics collector; this grants no mutation authority.");
        if (authorization.MutationAuthorized) throw new InvalidDataException("The request bridge returned a mutating authorization review.");
        prepared[authorization.ReviewId] = new(request, submissionId, CloneObject(input));
        return authorization;
    }

    public async Task<AssistantAuthorizationGrant> GrantAsync(AssistantAuthorizationReview authorization, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (!prepared.TryGetValue(authorization.ReviewId, out var item)) throw new InvalidOperationException("The authorization review is not current in this application session.");
        using var result = await InvokeAsync(WithOperation(item.Input, "Authorize", null), TimeSpan.FromMinutes(2), cancellationToken);
        var root = result.RootElement;
        var returnedReview = root.GetProperty("authorizationReview");
        if (!string.Equals(RequiredString(returnedReview, "reviewId"), authorization.ReviewId, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(returnedReview, "semanticBindingSha256"), authorization.SemanticBindingSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The authorization issuer returned a grant for a different review or semantic binding.");
        return new AssistantAuthorizationGrant(
            authorization.ReviewId, authorization.Request.RequestId, authorization.SubmissionId,
            RequiredString(root, "grantId"), RequiredString(root, "authorizationSecret"),
            DateTimeOffset.Parse(RequiredString(root, "expiresAt")), authorization.MutationAuthorized);
    }

    public async Task<AssistantExecutionResult> ExecuteAsync(
        AssistantAuthorizationGrant grant,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(progress);
        if (!prepared.TryGetValue(grant.ReviewId, out var item)) throw new InvalidOperationException("The authorization grant has no current prepared request.");
        progress.Report(new(DateTimeOffset.UtcNow, grant.MutationAuthorized ? "Mutating" : "Collecting",
            grant.MutationAuthorized ? "Running the reviewed exact reversible repair transition." : "Running registered read-only evidence collectors."));
        var input = WithOperation(item.Input, "Execute", grant.GrantId);
        try
        {
            using var result = await InvokeAsync(input, TimeSpan.FromHours(2), cancellationToken, grant.AuthorizationSecret, progress);
            return ParseExecuteResponse(item.SubmissionId, result.RootElement);
        }
        finally
        {
            // An execution grant is one-use. Never retain its prepared input after
            // an attempt, including a bridge or response-contract failure.
            prepared.Remove(grant.ReviewId);
        }
    }

    public Task<AssistantExecutionResult> ResumeAsync(
        string taskId,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(progress);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<AssistantExecutionResult>(cancellationToken);
        }

        return Task.FromException<AssistantExecutionResult>(
            new InvalidOperationException(
                "Automatic resume requires a newly reviewed, context-bound authorization grant; interrupted v2 workspaces are intentionally not resumed with stale authority."));
    }

    public async Task<AssistantAuthorizationReview> PrepareActionAsync(
        AssistantCanonicalRequest? parentRequest,
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        cancellationToken.ThrowIfCancellationRequested();

        var operation = action switch
        {
            AssistantCaseAction.AttachEvidence => "AttachEvidence",
            AssistantCaseAction.CaptureCurrentState => "CaptureState",
            AssistantCaseAction.RefreshRecoverySources => "RefreshRecoverySources",
            AssistantCaseAction.ApplyRepair => "ApplyRepair",
            AssistantCaseAction.RollBack => "RollBack",
            _ => throw new InvalidOperationException("This action does not require a separate authorization review."),
        };
        if (action == AssistantCaseAction.AttachEvidence && attachments.IsEmpty)
            throw new InvalidOperationException("Attach Evidence requires at least one selected file.");
        if (action != AssistantCaseAction.AttachEvidence && !attachments.IsEmpty)
            throw new ArgumentException("Attachments may be supplied only to Attach Evidence.", nameof(attachments));

        var input = new
        {
            schemaVersion = 2,
            operation,
            taskId,
            submissionId = $"action-{Guid.NewGuid():N}",
            attachments = attachments.ToArray(),
            actorId,
            sessionId,
            gridDataRoot,
            caseStoreRoot,
        };
        using var result = await InvokeAsync(input, TimeSpan.FromMinutes(2), cancellationToken);
        var root = result.RootElement;
        var envelope = Property(root, "requestEnvelope");
        if (parentRequest is not null) VerifyActionEnvelope(envelope, parentRequest.Draft);
        var successorDraft = parentRequest is null
            ? ParsePreparedDraft(envelope, Property(root, "investigationIntake"), action)
            : parentRequest.Draft with { Attachments = attachments };
        var review = Property(root, "authorizationReview");
        var canonical = new AssistantCanonicalRequest(
            RequiredString(envelope, "requestId"), RequiredString(envelope, "envelopeSha256"),
            DateTimeOffset.Parse(RequiredString(envelope, "submittedAt")), successorDraft);
        var names = successorDraft.Tools.ToDictionary(tool => tool.ToolId, tool => tool.ProviderName);
        var scopes = Property(review, "scopes").EnumerateArray().Select(scope =>
        {
            var toolId = new ToolId(RequiredString(scope, "toolId"));
            return new AssistantReadScope(toolId, names.GetValueOrDefault(toolId, toolId.Value),
                OptionalString(scope, "adapter"), OptionalString(scope, "observationMode"), RequiredString(scope, "availability"),
                Property(scope, "exactReadPaths").EnumerateArray().Select(value => value.GetString()!).ToImmutableArray());
        }).ToImmutableArray();
        var semanticBinding = Property(review, "semanticBinding");
        var authorityClass = RequiredString(review, "authorityClass");
        var mutationAuthorized = !string.Equals(authorityClass, "Read", StringComparison.Ordinal);
        var authorization = new AssistantAuthorizationReview(
            RequiredString(review, "reviewId"), RequiredString(semanticBinding, "submissionId"), canonical,
            RequiredString(semanticBinding, "planSha256"), scopes, RequiredString(review, "semanticBindingSha256"),
            mutationAuthorized,
            mutationAuthorized
                ? "Authorize one exact reversible filesystem transition on only the displayed targets. The grant is single-use, context-bound, verified, and recorded for Undo/Redo."
                : "Authorize only the displayed exact read paths. Papyrus attachments may launch the bundled read-only Grid diagnostics collector; this grants no mutation authority.");
        if (authorization.MutationAuthorized != (action is AssistantCaseAction.ApplyRepair or AssistantCaseAction.RollBack))
            throw new InvalidDataException("The action authorization class does not match the requested operation.");

        var preparedInput = Property(root, "preparedInput").Clone();
        prepared[authorization.ReviewId] = new(successorDraft, authorization.SubmissionId, preparedInput);
        return authorization;
    }

    public async Task<AssistantTaskRecord> ExecuteActionAsync(
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        if (!attachments.IsEmpty)
            throw new ArgumentException("Attachments are accepted only through the prepared Attach Evidence successor flow.", nameof(attachments));

        var operation = ResolveDirectActionOperation(action);

        progress.Report(new(DateTimeOffset.UtcNow, operation, "Running deterministic case action."));
        using var result = await InvokeAsync(new
        {
            schemaVersion = 2,
            operation,
            taskId,
            actorId,
            sessionId,
            gridDataRoot,
            caseStoreRoot,
        }, TimeSpan.FromHours(2), cancellationToken);

        var tasks = Property(result.RootElement, "tasks");
        if (tasks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The deterministic case action did not return a task collection.");
        var returned = tasks.EnumerateArray().ToArray();
        if (returned.Length != 1)
            throw new InvalidDataException("The deterministic case action did not return exactly one persisted task.");
        return ParseTask(returned[0]);
    }

    private object CreateInput(string operation, AssistantRequestDraft request, string submissionId, string? authorizationGrantId) => new
    {
        schemaVersion = 2,
        operation,
        gameId = ToEngineGameId(request.GameId),
        installationId = request.InstallationId.Value,
        profileId = request.ProfileId.Value,
        classId = request.ClassId,
        capabilityIds = string.IsNullOrWhiteSpace(request.CapabilityId) ? [] : new[] { request.CapabilityId },
        mods = request.Mods.Select(mod => mod.ProviderName).ToArray(),
        tools = request.Tools.Select(tool => tool.ToolId.Value).ToArray(),
        request = request.VerbatimUserText,
        expectedBehavior = request.ExpectedBehavior,
        reproductionLocation = request.ReproductionOrLocation,
        desiredOutcome = request.DesiredOutcome,
        authorizationScope = request.Attachments.IsDefaultOrEmpty ? "SelectedContext" : "SelectedContextAndAttachments",
        captureCurrentState = true,
        attachments = request.Attachments.IsDefault ? [] : request.Attachments.ToArray(),
        submissionId,
        actorId,
        sessionId,
        toolInputs = new Dictionary<string, object>(),
        authorizationGrantId,
        gridDataRoot,
        caseStoreRoot,
    };

    private async Task<JsonDocument> InvokeAsync(
        object input,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? authorizationSecret = null,
        IProgress<AssistantExecutionProgress>? progress = null)
    {
        Directory.CreateDirectory(transactionRoot);
        var inputPath = Path.Combine(transactionRoot, $"request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input), cancellationToken);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(entryPoint);
            start.ArgumentList.Add("-InputJsonPath");
            start.ArgumentList.Add(inputPath);
            if (!string.IsNullOrWhiteSpace(authorizationSecret)) start.Environment["GRID_AUTHORIZATION_SECRET"] = authorizationSecret;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The deterministic request process could not be started.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumOutputCharacters, timeoutSource.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 64 * 1024, timeoutSource.Token);
            var processStartedAt = DateTimeOffset.UtcNow;
            try
            {
                var waitTask = process.WaitForExitAsync(timeoutSource.Token);
                while (!waitTask.IsCompleted && progress is not null)
                {
                    var interval = Task.Delay(TimeSpan.FromSeconds(15), timeoutSource.Token);
                    if (await Task.WhenAny(waitTask, interval) == waitTask) break;
                    var elapsed = DateTimeOffset.UtcNow - processStartedAt;
                    progress.Report(new(DateTimeOffset.UtcNow, "Collecting",
                        $"Read-only collection is still running (elapsed {elapsed:hh\\:mm\\:ss})."));
                }
                await waitTask;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("The deterministic request bridge exceeded its bounded execution time.");
            }
            var output = await outputTask;
            var error = await errorTask;
            if (output.Exceeded) throw new InvalidDataException("The request bridge exceeded its bounded output limit.");
            if (process.ExitCode != 0) throw new InvalidOperationException(SanitizeError(output.Text, error.Text));
            return JsonDocument.Parse(output.Text);
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
        }
    }

    private static AssistantExecutionResult ParseExecution(string taskId, JsonElement execution)
    {
        var result = Property(execution, "Result");
        var toolRun = TryProperty(execution, "ToolRun", out var run) ? run : default;
        var receipts = toolRun.ValueKind == JsonValueKind.Object && TryProperty(toolRun, "toolReceipts", out var values)
            ? values.EnumerateArray().Select(ParseReceipt).ToImmutableArray() : [];
        var finding = ParseFinding(result);
        return new(taskId, RequiredString(execution, "CaseId"), RequiredString(result, "terminalState"), finding, receipts, DateTimeOffset.UtcNow);
    }

    private static ImmutableArray<AssistantTaskRecord> ParseTaskHistoryResponse(JsonElement response)
    {
        var tasks = Property(response, "tasks");
        if (tasks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The request bridge returned an invalid task-history collection.");
        return tasks.EnumerateArray().Select(ParseTask).OrderBy(task => task.Summary.CreatedAtUtc).ToImmutableArray();
    }

    private static string ResolveDirectActionOperation(AssistantCaseAction action) => action switch
    {
        AssistantCaseAction.Diagnose => "Diagnose",
        AssistantCaseAction.ReviewEvidence => "ReviewEvidence",
        AssistantCaseAction.ReviewRepair => "ReviewRepair",
        AssistantCaseAction.AttachEvidence or AssistantCaseAction.CaptureCurrentState or AssistantCaseAction.RefreshRecoverySources or
            AssistantCaseAction.ApplyRepair or AssistantCaseAction.RollBack =>
            throw new InvalidOperationException("Authorized successor and mutation actions must pass through preparation and explicit authorization."),
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static AssistantExecutionResult ParseExecuteResponse(string taskId, JsonElement response)
    {
        if (TryProperty(response, "tasks", out var taskValues))
        {
            if (taskValues.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The request bridge returned an invalid task collection.");

            var returnedTasks = taskValues.EnumerateArray().ToArray();
            if (returnedTasks.Length > 1)
                throw new InvalidDataException("The authorized execution returned more than one sealed task.");
            if (returnedTasks.Length == 1)
            {
                var refreshed = ParseTask(returnedTasks[0]);
                return new AssistantExecutionResult(
                    refreshed.Summary.Id,
                    refreshed.CaseId ?? throw new InvalidDataException("The returned task has no sealed case identity."),
                    refreshed.TerminalState,
                    refreshed.Finding ?? throw new InvalidDataException("The returned task has no deterministic result."),
                    refreshed.Receipts,
                    DateTimeOffset.UtcNow,
                    RepairAvailability: refreshed.RepairAvailability);
            }
        }

        return ParseExecution(taskId, Property(response, "execution"));
    }

    private static AssistantToolReceipt ParseReceipt(JsonElement receipt) => new(
        new ToolId(RequiredString(receipt, "toolId")), RequiredString(receipt, "toolId"), RequiredString(receipt, "availability"), RequiredString(receipt, "status"),
        DateTimeOffset.Parse(RequiredString(receipt, "startedAt")), DateTimeOffset.Parse(RequiredString(receipt, "completedAt")),
        receipt.GetProperty("durationMilliseconds").GetInt64(), receipt.TryGetProperty("exitCode", out var exit) && exit.ValueKind == JsonValueKind.Number ? exit.GetInt32() : null,
        OptionalString(receipt, "reason"), RequiredString(receipt, "stdout"), RequiredString(receipt, "stderr"), RequiredString(receipt, "receiptSha256"),
        receipt.GetProperty("evidence").EnumerateArray().Select(value => value.GetRawText()).ToImmutableArray());

    private static AssistantTaskRecord ParseTask(JsonElement task)
    {
        var taskId = RequiredString(task, "taskId");
        var createdAt = DateTimeOffset.Parse(RequiredString(task, "createdAt"));
        var classId = RequiredString(task, "classId");
        var terminal = RequiredString(task, "terminalState");
        var prompt = OptionalString(task, "rawPrompt") ?? string.Empty;
        var transcript = ImmutableArray.CreateBuilder<AssistantTranscriptEntry>();
        if (!string.IsNullOrWhiteSpace(prompt)) transcript.Add(new(createdAt, AssistantTranscriptKind.UserClaim, RenderTaskClaim(task, prompt)));
        AssistantDeterministicFinding? finding = null;
        if (TryProperty(task, "result", out var result) && result.ValueKind == JsonValueKind.Object)
        {
            finding = ParseFinding(result);
            transcript.Add(new(createdAt, AssistantTranscriptKind.Result, RenderFinding(finding)));
        }
        var receipts = TryProperty(task, "toolReceipts", out var receiptValues) && receiptValues.ValueKind == JsonValueKind.Array
            ? receiptValues.EnumerateArray().Select(ParseReceipt).ToImmutableArray() : [];
        var resumable = TryProperty(task, "resumable", out var resumableValue) && resumableValue.ValueKind == JsonValueKind.True;
        var repair = ParseRepairAvailability(task);
        return new(new(taskId, classId, createdAt, terminal), null, OptionalString(task, "caseId"), terminal, transcript.ToImmutable(), receipts, finding, resumable, RepairAvailability: repair);
    }

    private static AssistantRepairAvailability? ParseRepairAvailability(JsonElement task)
    {
        if (!TryProperty(task, "repairState", out var state) || state.ValueKind != JsonValueKind.Object) return null;
        var exact = TryProperty(state, "specificationAvailable", out var exactValue) && exactValue.ValueKind == JsonValueKind.True;
        var rollback = TryProperty(state, "rollbackAvailable", out var rollbackValue) && rollbackValue.ValueKind == JsonValueKind.True;
        var recovery = TryProperty(state, "recoveryPlanAvailable", out var recoveryValue) && recoveryValue.ValueKind == JsonValueKind.True;
        var manualReady = TryProperty(state, "manualAcquisitionReadyCount", out var manualReadyValue) && manualReadyValue.ValueKind == JsonValueKind.Number
            ? manualReadyValue.GetInt32() : 0;
        var detail = OptionalString(state, "detail") ?? (exact
            ? "An exact evidence-bound repair specification is available."
            : recovery ? "A deterministic recovery plan is available, but its evidence gates are not yet complete."
            : "No exact repair specification exists.");
        var installationId = OptionalString(state, "installationId");
        var profileId = OptionalString(state, "profileId");
        var archiveCandidates = TryProperty(state, "archiveCandidates", out var archiveCandidateValues) && archiveCandidateValues.ValueKind == JsonValueKind.Array
            ? archiveCandidateValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object).Select(value =>
                new AssistantArchiveCandidate(
                    OptionalString(value, "pluginName") ?? "Unknown plugin",
                    OptionalString(value, "providerName") ?? "Unknown provider",
                    OptionalString(value, "archiveLeaf") ?? "Unknown archive",
                    OptionalString(value, "candidateRole") ?? "Unclassified",
                    OptionalString(value, "status") ?? "Unknown",
                    OptionalString(value, "compatibilityStatus") ?? "Unknown",
                    TryProperty(value, "requiredEvidence", out var requiredEvidence) && requiredEvidence.ValueKind == JsonValueKind.Array
                        ? requiredEvidence.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToImmutableArray()
                        : [],
                    OptionalString(value, "evidenceId") ?? string.Empty)).ToImmutableArray()
            : [];
        var humanActionManifest = ParseRecoveryActionManifest(state);
        return new(OptionalString(state, "specificationId"), OptionalString(state, "specificationSha256"), exact, rollback, detail, recovery,
            manualReady, OptionalString(state, "nextManualAcquisitionUri"), OptionalString(state, "nextExpectedArchiveLeaf"), OptionalString(state, "historyState"),
            OptionalString(state, "repairKind"),
            string.IsNullOrWhiteSpace(installationId) ? null : new InstallationId(installationId),
            string.IsNullOrWhiteSpace(profileId) ? null : new ProfileId(profileId), archiveCandidates, humanActionManifest);
    }

    private static AssistantRecoveryActionManifest? ParseRecoveryActionManifest(JsonElement repairState)
    {
        if (!TryProperty(repairState, "humanActionManifest", out var manifest) || manifest.ValueKind == JsonValueKind.Null)
            return null;
        if (manifest.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Optional 'humanActionManifest' must be an object or null.");
        var summary = Property(manifest, "summary");
        if (summary.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Recovery action manifest summary must be an object.");

        var acquisitions = OptionalArrayItems(manifest, "manualAcquisitions").Select(value =>
            new AssistantRecoveryAcquisitionAction(
                RequiredString(value, "actionId"),
                OptionalArrayItems(value, "modNames").Select(item => item.GetString()!).ToImmutableArray(),
                OptionalArrayItems(value, "installedVersionClaims").Select(item => item.GetString()!).ToImmutableArray(),
                RequiredString(value, "expectedArchiveLeaf"),
                RequiredString(value, "officialFilesUri"),
                OptionalArrayItems(value, "affectedPlugins").Select(item => item.GetString()!).ToImmutableArray(),
                Property(value, "missingDependencyCount").GetInt64(),
                RequiredString(value, "instruction"),
                RequiredString(value, "completionEvidence"))).ToImmutableArray();
        var lineage = OptionalArrayItems(manifest, "lineageRequirements").Select(value =>
            new AssistantRecoveryLineageRequirement(
                RequiredString(value, "pluginName"),
                OptionalString(value, "currentOverrideProvider") ?? "Unknown override provider",
                Property(value, "missingDependencyCount").GetInt32(),
                OptionalArrayItems(value, "requiredFileExamples").Select(item => item.GetString()!).ToImmutableArray(),
                Property(value, "omittedRequiredFileCount").GetInt32(),
                RequiredString(value, "status"),
                RequiredString(value, "nextAction"))).ToImmutableArray();
        static ImmutableArray<AssistantRecoveryCandidateAction> ParseCandidates(JsonElement value, string propertyName) =>
            OptionalArrayItems(value, propertyName).Select(item => new AssistantRecoveryCandidateAction(
                RequiredString(item, "pluginName"),
                RequiredString(item, "providerName"),
                RequiredString(item, "archiveLeaf"),
                RequiredString(item, "candidateRole"),
                RequiredString(item, "compatibilityStatus"),
                RequiredString(item, "evidenceId"))).ToImmutableArray();

        return new(
            RequiredString(manifest, "manifestId"),
            RequiredString(manifest, "manifestSha256"),
            RequiredString(manifest, "planId"),
            RequiredString(manifest, "planSha256"),
            RequiredString(manifest, "status"),
            Property(summary, "affectedModCount").GetInt32(),
            Property(summary, "affectedPluginCount").GetInt32(),
            Property(summary, "missingDependencyCount").GetInt64(),
            Property(summary, "exactArchiveAcquisitionCount").GetInt32(),
            Property(summary, "lineageEvidenceRequiredCount").GetInt32(),
            Property(summary, "versionChangingCandidateCount").GetInt32(),
            Property(summary, "exactRestorationCandidateCount").GetInt32(),
            Property(summary, "provenUpdateRequiredCount").GetInt32(),
            Property(summary, "provenReinstallationRequiredCount").GetInt32(),
            Property(summary, "plannedPatchChangeCount").GetInt32(),
            Property(summary, "repairReadyCount").GetInt32(),
            TryProperty(summary, "mutationAuthorized", out var authorized) && authorized.ValueKind == JsonValueKind.True,
            acquisitions,
            lineage,
            ParseCandidates(manifest, "versionChangingCandidates"),
            ParseCandidates(manifest, "exactRestorationCandidates"),
            OptionalArrayItems(manifest, "affectedMods").Select(item => item.GetString()!).ToImmutableArray(),
            OptionalArrayItems(manifest, "affectedPlugins").Select(item => item.GetString()!).ToImmutableArray(),
            OptionalArrayItems(manifest, "gridFollowUpActions").Select(item => item.GetString()!).ToImmutableArray(),
            OptionalArrayItems(manifest, "plannedPatchChanges").Select(item => item.GetString()!).ToImmutableArray(),
            OptionalArrayItems(manifest, "classifications").Select(item => item.GetString()!).ToImmutableArray());
    }

    private static AssistantDeterministicFinding ParseFinding(JsonElement result) => new(
        ArrayItemsOrEmpty(result, "affectedMods").Select(value => value.GetString()!).ToImmutableArray(),
        ArrayItemsOrEmpty(result, "modRoles").Select(role => new AssistantModRole(RequiredString(role, "mod"), RequiredString(role, "role"))).ToImmutableArray(),
        RequiredString(result, "finding"), RequiredString(result, "solution"),
        ArrayItemsOrEmpty(result, "evidenceToolIds").Select(value => new ToolId(value.GetString()!)).ToImmutableArray(),
        Confidence: ParseConfidence(result),
        Evidence: OptionalArrayItems(result, "evidenceIds").Select(value => value.GetString()!).ToImmutableArray(),
        CapabilityRequired: ParseCapabilityRequired(result),
        CapabilityAssessment: ParseCapabilityAssessment(result));

    private static string ParseConfidence(JsonElement result)
    {
        if (!TryProperty(result, "confidence", out var confidence) || confidence.ValueKind != JsonValueKind.Object)
            return "Not evaluated";
        var status = OptionalString(confidence, "status") ?? "NotEvaluated";
        var rating = OptionalString(confidence, "rating");
        return string.IsNullOrWhiteSpace(rating) ? status : $"{status} - {rating}";
    }

    private static AssistantCapabilityRequired? ParseCapabilityRequired(JsonElement result)
    {
        if (!TryProperty(result, "capabilityRequired", out var required) || required.ValueKind == JsonValueKind.Null)
            return null;
        if (required.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Optional 'capabilityRequired' must be an object or null.");
        var resolutions = OptionalArrayItems(required, "resolutions")
            .Select(value => new AssistantCapabilityGapResolution(
                RequiredString(value, "gapId"),
                RequiredString(value, "requiredCapabilityId"),
                RequiredString(value, "status"),
                RequiredString(value, "route"),
                OptionalString(value, "selectedCandidateId"),
                TryProperty(value, "autoMayContinue", out var autoMayContinue) && autoMayContinue.ValueKind == JsonValueKind.True,
                RequiredString(value, "nextAction"),
                RequiredString(value, "resolutionSha256")))
            .ToImmutableArray();
        return new(
            OptionalArrayItems(required, "coverageGaps").Select(value => value.GetString()!).ToImmutableArray(),
            OptionalArrayItems(required, "missingInputs").Select(value => value.GetString()!).ToImmutableArray(),
            OptionalArrayItems(required, "missingCapabilityIds").Select(value => value.GetString()!).ToImmutableArray(),
            resolutions);
    }

    private static AssistantCapabilityAssessment? ParseCapabilityAssessment(JsonElement result)
    {
        if (!TryProperty(result, "capabilityAssessment", out var assessment) || assessment.ValueKind == JsonValueKind.Null)
            return null;
        if (assessment.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Optional 'capabilityAssessment' must be an object or null.");

        var installedProviders = ArrayItemsOrEmpty(assessment, "installedProviders")
            .Select(value => new AssistantInstalledCapabilityProvider(
                RequiredString(value, "name"),
                RequiredString(value, "role"),
                RequiredString(value, "status"),
                ArrayItemsOrEmpty(value, "evidenceIds").Select(item => item.GetString()!).ToImmutableArray()))
            .ToImmutableArray();
        var candidates = ArrayItemsOrEmpty(assessment, "communityCandidates")
            .Select(value => new AssistantCommunityCapabilityCandidate(
                RequiredString(value, "name"),
                RequiredString(value, "provider"),
                OptionalString(value, "uri"),
                OptionalString(value, "version"),
                ParseEnum<AssistantCandidateCompatibilityStatus>(value, "compatibilityStatus"),
                RequiredString(value, "compatibilityDetail"),
                ArrayItemsOrEmpty(value, "requiredPatches").Select(item => item.GetString()!).ToImmutableArray(),
                ArrayItemsOrEmpty(value, "evidenceIds").Select(item => item.GetString()!).ToImmutableArray()))
            .ToImmutableArray();
        var observedAt = OptionalString(assessment, "observedAtUtc");
        return new(
            RequiredString(assessment, "capabilityId"),
            RequiredString(assessment, "displayName"),
            ParseEnum<AssistantInstalledCapabilityStatus>(assessment, "installedStatus"),
            installedProviders,
            ParseEnum<AssistantProviderDiscoveryStatus>(assessment, "discoveryStatus"),
            string.IsNullOrWhiteSpace(observedAt) ? null : DateTimeOffset.Parse(observedAt),
            candidates,
            ArrayItemsOrEmpty(assessment, "evidenceIds").Select(item => item.GetString()!).ToImmutableArray());
    }

    private static T ParseEnum<T>(JsonElement element, string property) where T : struct, Enum
    {
        var value = RequiredString(element, property);
        return Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Unsupported '{property}' value '{value}'.");
    }

    private static IReadOnlyList<JsonElement> ArrayItemsOrEmpty(JsonElement element, string property)
    {
        var value = Property(element, property);
        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray().Select(item => item.Clone()).ToArray();
        }

        // Older PowerShell projections could serialize an empty collection as
        // an empty object. It carries the same empty meaning and no authority.
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any())
        {
            return [];
        }

        throw new InvalidDataException($"Required '{property}' must be an array.");
    }

    private static IReadOnlyList<JsonElement> OptionalArrayItems(JsonElement element, string property) =>
        TryProperty(element, property, out _) ? ArrayItemsOrEmpty(element, property) : [];

    private static string RenderFinding(AssistantDeterministicFinding finding) =>
        $"Affected mods: {RenderBounded(finding.AffectedMods, value => value)}\n" +
        $"Mod roles: {RenderBounded(finding.ModRoles, role => $"{role.Mod}: {role.Role}")}\n" +
        $"Finding: {finding.Finding}\nSolution: {finding.Solution}";

    private static string RenderBounded<T>(ImmutableArray<T> values, Func<T, string> selector, int maximum = 12)
    {
        if (values.IsDefaultOrEmpty) return "Unresolved";
        var visible = string.Join(", ", values.Take(maximum).Select(selector));
        return values.Length <= maximum ? visible : $"{visible}, … {values.Length - maximum:N0} more (open Review Evidence)";
    }

    private static string RenderTaskClaim(JsonElement task, string problem)
    {
        if (!TryProperty(task, "intake", out var intake) || intake.ValueKind != JsonValueKind.Object)
            return problem;

        var lines = new List<string> { $"Problem: {problem}" };
        Add("Expected behavior", "expectedBehavior");
        Add("Reproduction or location", "reproductionLocation");
        Add("Desired outcome", "desiredOutcome");
        return string.Join("\n", lines);

        void Add(string label, string property)
        {
            var value = OptionalString(intake, property);
            if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value}");
        }
    }

    private static JsonElement Property(JsonElement element, string property) =>
        TryProperty(element, property, out var value) ? value : throw new InvalidDataException($"Missing required '{property}'.");

    private static bool TryProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.TryGetProperty(property, out value)) return true;
        foreach (var candidate in element.EnumerateObject())
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) { value = candidate.Value; return true; }
        value = default;
        return false;
    }

    private static string RequiredString(JsonElement element, string property) => Property(element, property).GetString() ?? throw new InvalidDataException($"Missing required '{property}'.");
    private static string? OptionalString(JsonElement element, string property) => !TryProperty(element, property, out var value) || value.ValueKind == JsonValueKind.Null ? null : value.GetString();

    private static string CreateLocalActorId()
    {
        var identity = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return $"local-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static JsonElement CloneObject(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement WithOperation(JsonElement preparedInput, string operation, string? authorizationGrantId)
    {
        if (preparedInput.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The prepared request input is not a JSON object.");
        var values = preparedInput.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.Ordinal);
        values["operation"] = JsonSerializer.SerializeToElement(operation);
        if (authorizationGrantId is null) values.Remove("authorizationGrantId");
        else values["authorizationGrantId"] = JsonSerializer.SerializeToElement(authorizationGrantId);
        return JsonSerializer.SerializeToElement(values);
    }

    private static void VerifyActionEnvelope(JsonElement envelope, AssistantRequestDraft parentDraft)
    {
        var context = Property(envelope, "context");
        var classValue = Property(envelope, "class");
        var selections = Property(envelope, "selections");
        var claims = Property(envelope, "claims");
        if (!string.Equals(RequiredString(context, "gameId"), ToEngineGameId(parentDraft.GameId), StringComparison.Ordinal) ||
            !string.Equals(RequiredString(context, "installationId"), parentDraft.InstallationId.Value, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(context, "profileId"), parentDraft.ProfileId.Value, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(classValue, "classId"), parentDraft.ClassId, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(classValue, "recipeVersion"), parentDraft.RecipeVersion, StringComparison.Ordinal) ||
            !string.Equals(RequiredString(claims, "text"), parentDraft.VerbatimUserText, StringComparison.Ordinal))
            throw new InvalidDataException("The successor action resolved to a different canonical request context.");

        var returnedMods = Property(selections, "mods").EnumerateArray()
            .Select(value => RequiredString(value, "providerName")).ToArray();
        var expectedMods = parentDraft.Mods.Select(value => value.ProviderName).ToArray();
        var returnedTools = Property(selections, "tools").EnumerateArray()
            .Select(value => RequiredString(value, "toolId")).ToArray();
        var expectedTools = parentDraft.Tools.Select(value => value.ToolId.Value).ToArray();
        var returnedCapabilities = TryProperty(selections, "capabilities", out var capabilityValues)
            ? capabilityValues.EnumerateArray().Select(value => RequiredString(value, "capabilityId")).ToArray()
            : [];
        var expectedCapabilities = string.IsNullOrWhiteSpace(parentDraft.CapabilityId)
            ? []
            : new[] { parentDraft.CapabilityId };
        if (!returnedMods.SequenceEqual(expectedMods, StringComparer.Ordinal) ||
            !returnedTools.SequenceEqual(expectedTools, StringComparer.Ordinal) ||
            !returnedCapabilities.SequenceEqual(expectedCapabilities, StringComparer.Ordinal))
            throw new InvalidDataException("The successor action changed the canonical mod, tool, or gameplay-capability selections.");
    }

    private static AssistantRequestDraft ParsePreparedDraft(JsonElement envelope, JsonElement intake, AssistantCaseAction action)
    {
        var context = Property(envelope, "context");
        var classValue = Property(envelope, "class");
        var selections = Property(envelope, "selections");
        var claims = Property(envelope, "claims");
        var mods = Property(selections, "mods").EnumerateArray().Select((value, index) =>
            new AssistantRequestModSelection(new ModId($"mod.recovery.{index:D4}"), RequiredString(value, "providerName"), "RecoveryProvider")).ToImmutableArray();
        var tools = Property(selections, "tools").EnumerateArray().Select(value =>
        {
            var id = new ToolId(RequiredString(value, "toolId"));
            return new AssistantRequestToolSelection(id, id.Value, "DeterministicRecoveryContext");
        }).ToImmutableArray();
        var attachments = Property(intake, "attachments").EnumerateArray().Select(value =>
        {
            var path = RequiredString(value, "path");
            return new AssistantAttachmentDraft(path, Path.GetFileName(path), OptionalString(value, "mediaType"));
        }).ToImmutableArray();
        return new AssistantRequestDraft(
            new GameId(ToApplicationGameId(RequiredString(context, "gameId"))),
            new InstallationId(RequiredString(context, "installationId")),
            new ProfileId(RequiredString(context, "profileId")),
            mods,
            tools,
            RequiredString(classValue, "classId"),
            RequiredString(classValue, "recipeVersion"),
            RequiredString(claims, "text"),
            action switch
            {
                AssistantCaseAction.CaptureCurrentState => "Capture current state",
                AssistantCaseAction.AttachEvidence => "Attach evidence",
                AssistantCaseAction.RefreshRecoverySources => "Verify recovery sources",
                AssistantCaseAction.ApplyRepair => "Apply repair",
                AssistantCaseAction.RollBack => "Roll back repair",
                _ => RequiredString(classValue, "classId"),
            },
            OptionalString(intake, "expectedBehavior") ?? string.Empty,
            OptionalString(intake, "reproductionLocation") ?? string.Empty,
            OptionalString(intake, "desiredOutcome") ?? string.Empty,
            attachments,
            AssistantAuthorizationScope.SelectedInstallationAndProfileReadOnly,
            TryProperty(selections, "capabilities", out var capabilityValues) && capabilityValues.ValueKind == JsonValueKind.Array
                ? capabilityValues.EnumerateArray().Select(value => RequiredString(value, "capabilityId")).SingleOrDefault()
                : null);
    }

    private static string ToApplicationGameId(string gameId) => gameId switch
    {
        "skyrimspecialedition" => "game.skyrim-special-edition",
        _ => gameId,
    };

    private static string ToEngineGameId(GameId gameId) => gameId.Value switch
    {
        "game.skyrim-special-edition" => "skyrimspecialedition",
        _ => gameId.Value,
    };
    private static string SanitizeError(string output, string error)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var code = OptionalString(document.RootElement, "errorCode");
            if (!string.IsNullOrWhiteSpace(code) && Regex.IsMatch(code, "^[A-Za-z][A-Za-z0-9._-]{0,63}$"))
            {
                var message = OptionalString(document.RootElement, "message");
                var safeMessage = SanitizeBridgeMessage(message);
                return string.IsNullOrWhiteSpace(safeMessage)
                    ? $"The deterministic request bridge refused the operation ({code})."
                    : $"The deterministic request bridge refused the operation ({code}): {safeMessage}";
            }
        }
        catch (JsonException) { }
        var match = Regex.Match(error ?? string.Empty, @"\b([A-Za-z][A-Za-z0-9]+):");
        return match.Success
            ? $"The deterministic request bridge refused the operation ({match.Groups[1].Value})."
            : "The deterministic request bridge failed without a safe public diagnostic.";
    }

    private static string? SanitizeBridgeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;

        var normalized = Regex.Replace(message, @"[\x00-\x1F\x7F]+", " ").Trim();
        normalized = Regex.Replace(
            normalized,
            @"(?i)\b(authorizationSecret|secret|token|password)\b\s*[:=]\s*[^\s,;]+",
            "$1=[REDACTED]");
        const int maximumLength = 512;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength] + "…";
    }

    private static async Task<BoundedText> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var text = new System.Text.StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var exceeded = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            var remaining = maximumCharacters - text.Length;
            if (remaining > 0) text.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) exceeded = true;
        }
        return new(text.ToString(), exceeded);
    }

    private sealed record PreparedRequest(AssistantRequestDraft Draft, string SubmissionId, JsonElement Input);
    private sealed record BoundedText(string Text, bool Exceeded);
}
