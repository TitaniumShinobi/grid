using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2LaunchService : IWorkspaceLaunchService
{
    private static readonly SemaphoreSlim GlobalLaunchGate = new(1, 1);
    private readonly Func<ExternalLaunchIntent, CancellationToken, Task<Mo2LaunchEvidence?>> evidenceResolver;
    private readonly Func<CancellationToken, Task<string>> postRunRefresh;
    private readonly Mo2LaunchInvocationBuilder builder;
    private readonly IMo2PeVersionReader versionReader;
    private readonly IMo2ProcessProbe processProbe;
    private readonly IMo2ProcessRunner runner;
    private readonly TimeProvider timeProvider;
    private readonly ConcurrentDictionary<ExternalLaunchSessionId, Prepared> prepared = new();
    private readonly ConcurrentDictionary<LaunchApprovalId, byte> consumedApprovals = new();

    public Mo2LaunchService(
        Func<ExternalLaunchIntent, CancellationToken, Task<Mo2LaunchEvidence?>> evidenceResolver,
        Mo2LaunchInvocationBuilder builder,
        IMo2PeVersionReader versionReader,
        IMo2ProcessProbe processProbe,
        IMo2ProcessRunner runner,
        Func<CancellationToken, Task<string>>? postRunRefresh = null,
        TimeProvider? timeProvider = null)
    {
        this.evidenceResolver = evidenceResolver ?? throw new ArgumentNullException(nameof(evidenceResolver));
        this.builder = builder ?? throw new ArgumentNullException(nameof(builder));
        this.versionReader = versionReader ?? throw new ArgumentNullException(nameof(versionReader));
        this.processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.postRunRefresh = postRunRefresh ?? (_ => Task.FromResult("Post-run refresh is not configured."));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ExternalLaunchPreparationResult> PrepareAsync(
        ExternalLaunchIntent intent,
        FidelityAuditSnapshot audit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audit);
        cancellationToken.ThrowIfCancellationRequested();
        var mismatch = AuditMismatch(intent, audit);
        if (mismatch is not null) return new(ExternalLaunchPreparationStatus.Stale, null, mismatch);

        var evidence = await evidenceResolver(intent, cancellationToken).ConfigureAwait(false);
        if (evidence is null) return new(ExternalLaunchPreparationStatus.Unavailable, null, "Selected MO2 launch evidence is unavailable.");
        var preflight = await PreflightAsync(intent, evidence, audit.Context.ProfileSnapshotFingerprint, cancellationToken).ConfigureAwait(false);
        if (preflight.Error is not null) return new(ExternalLaunchPreparationStatus.Blocked, null, preflight.Error);

        var blocking = audit.Items.Where(item => item.IsBlocking || item.Status == FidelityAuditItemStatus.Stale).ToImmutableArray();
        var acknowledgement = audit.Items.Where(item => item.RequiresAcknowledgement).ToImmutableArray();
        var status = !blocking.IsEmpty ? ExternalLaunchPreparationStatus.Blocked
            : !acknowledgement.IsEmpty ? ExternalLaunchPreparationStatus.RequiresAcknowledgement
            : ExternalLaunchPreparationStatus.Ready;
        var sessionId = new ExternalLaunchSessionId($"launch.mo2.{Guid.NewGuid():N}");
        var fingerprint = Hash(intent.AuditFingerprint, intent.ExecutableFingerprint, preflight.Invocation!.RouteFingerprint, sessionId.Value);
        var preparation = new ExternalLaunchPreparation(
            sessionId, intent, fingerprint, timeProvider.GetUtcNow(), status, blocking, acknowledgement,
            status == ExternalLaunchPreparationStatus.Blocked ? "The fidelity audit blocks launch." : "Ready for one-use explicit approval. Launching authorizes MO2 and the selected runtime to perform their normal external writes; Grid offers no rollback.");
        if (preparation.CanApprove) prepared[sessionId] = new(preparation, audit, evidence, preflight.Invocation);
        return new(status, preparation, preparation.Detail);
    }

    public async Task<ExternalLaunchResult> LaunchAsync(
        ExternalLaunchPreparation preparation,
        ExternalLaunchApproval approval,
        IProgress<ExternalLaunchEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(approval);
        if (!prepared.TryRemove(preparation.SessionId, out var captured) || captured.Preparation != preparation)
            return new(ExternalLaunchSessionStatus.Rejected, null, "The preparation is missing, stale, or already used.");
        if (!ApprovalMatches(preparation, approval) || !consumedApprovals.TryAdd(approval.Id, 0))
            return new(ExternalLaunchSessionStatus.Rejected, null, "The one-use approval does not match this preparation.");
        if (!preparation.AcknowledgementItems.IsEmpty && !approval.AcknowledgedWarningsAndUnsupported)
            return new(ExternalLaunchSessionStatus.Rejected, null, "Warnings and unsupported evidence were not acknowledged.");

        var events = ImmutableArray.CreateBuilder<ExternalLaunchEvent>();
        void Add(ExternalLaunchEventKind kind, string detail, int? exitCode = null)
        {
            var item = new ExternalLaunchEvent(
                new($"launch-event.mo2.{preparation.SessionId.Value}.{events.Count + 1}"), preparation.SessionId,
                events.Count + 1, timeProvider.GetUtcNow(), kind, detail, exitCode, ConfiguredProcessLifecycleObserved: false);
            events.Add(item); progress?.Report(item);
        }

        Add(ExternalLaunchEventKind.LaunchRequested, "Launch requested with fingerprint-bound approval.");
        if (!await GlobalLaunchGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result(ExternalLaunchSessionStatus.Rejected, "Another Grid MO2 launch is active.", events, preparation, AddError: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = await evidenceResolver(preparation.Intent, cancellationToken).ConfigureAwait(false);
            if (evidence is null) return Result(ExternalLaunchSessionStatus.Rejected, "Launch evidence became unavailable.", events, preparation, AddError: true);
            var checkedAgain = await PreflightAsync(preparation.Intent, evidence, captured.Audit.Context.ProfileSnapshotFingerprint, cancellationToken).ConfigureAwait(false);
            if (checkedAgain.Error is not null || checkedAgain.Invocation is null ||
                !string.Equals(checkedAgain.Invocation.RouteFingerprint, captured.Invocation.RouteFingerprint, StringComparison.Ordinal) ||
                !string.Equals(checkedAgain.Invocation.ExecutablePath, captured.Invocation.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !checkedAgain.Invocation.Arguments.SequenceEqual(captured.Invocation.Arguments, StringComparer.Ordinal))
                return Result(ExternalLaunchSessionStatus.Rejected, checkedAgain.Error ?? "The exact MO2 route changed after confirmation.", events, preparation, AddError: true);

            Add(ExternalLaunchEventKind.ManagerStartRequested, "Starting the validated MO2 run -e route.");
            IMo2ProcessHandle handle;
            try { handle = await runner.StartAsync(checkedAgain.Invocation!, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { return Result(ExternalLaunchSessionStatus.Canceled, "Launch was canceled before MO2 started.", events, preparation); }
            catch (Exception exception)
            { return Result(ExternalLaunchSessionStatus.Failed, $"MO2 could not be started: {exception.GetType().Name}.", events, preparation, AddError: true); }

            await using (handle.ConfigureAwait(false))
            {
                Add(ExternalLaunchEventKind.ManagerProcessStarted, "The MO2 command process started; configured child process identity is not observable.");
                int exitCode;
                try { exitCode = await handle.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Add(ExternalLaunchEventKind.MonitoringDetached, "Monitoring detached; Grid did not terminate MO2 or its configured runtime.");
                    return Result(ExternalLaunchSessionStatus.MonitoringDetached, "Monitoring detached after MO2 start.", events, preparation);
                }
                Add(ExternalLaunchEventKind.ManagerInvocationExited,
                    exitCode == 0 ? "MO2's waiting command completed without reporting a ProcessRunner error." : "MO2 rejected the request or reported a ProcessRunner failure.", exitCode);
                if (exitCode != 0) return Result(ExternalLaunchSessionStatus.Rejected, "MO2 returned a nonzero command result.", events, preparation);
            }

            Add(ExternalLaunchEventKind.PostRunRefreshRequested, "Refreshing the selected connected context once.");
            try
            {
                var detail = await postRunRefresh(CancellationToken.None).ConfigureAwait(false);
                Add(ExternalLaunchEventKind.PostRunRefreshCompleted, detail);
            }
            catch (Exception exception)
            {
                Add(ExternalLaunchEventKind.Error, $"Post-run refresh failed safely: {exception.GetType().Name}.");
            }
            return Result(ExternalLaunchSessionStatus.Completed, "MO2 reported command completion; no child PID or child exit code is claimed.", events, preparation);
        }
        finally { GlobalLaunchGate.Release(); }
    }

    private async Task<(Mo2LaunchInvocation? Invocation, string? Error)> PreflightAsync(
        ExternalLaunchIntent intent, Mo2LaunchEvidence evidence, string expectedProfileFingerprint, CancellationToken cancellationToken)
    {
        if (evidence.Reference.Id != intent.ReferenceId || evidence.Reference.InstallationId != intent.InstallationId ||
            evidence.Reference.GameId != intent.GameId || evidence.SelectedProfileId != intent.ProfileId ||
            evidence.SelectedExecutableId != intent.ExecutableId || evidence.Executables.ReferenceId != intent.ReferenceId ||
            evidence.SelectedExecutable.Fingerprint != intent.ExecutableFingerprint ||
            !string.Equals(evidence.ActiveProfileFingerprint, expectedProfileFingerprint, StringComparison.Ordinal) ||
            evidence.ActiveProfileState != ManagerProfileState.Active || evidence.SelectedExecutable.IsDuplicate)
            return (null, "Selected executable or ACTIVE IN MO2 evidence is stale or ambiguous.");
        if (evidence.Executables.Entries.Count(entry => entry.Fingerprint == intent.ExecutableFingerprint && !entry.IsDuplicate) != 1)
            return (null, "The selected executable is no longer unique in MO2 configuration.");
        var version = versionReader.Read(evidence.Reference.ExecutablePath);
        if (version.Status != Mo2VersionEvidenceStatus.Supported) return (null, version.Detail);
        var process = await processProbe.ProbeAsync(evidence.Reference.ExecutablePath, cancellationToken).ConfigureAwait(false);
        if (process.Status != Mo2ProcessProbeStatus.Clear) return (null, process.Detail);
        try { return (builder.Build(evidence.Reference, evidence.Validation, evidence.SelectedExecutable, version), null); }
        catch (InvalidOperationException exception) { return (null, exception.Message); }
    }

    private static string? AuditMismatch(ExternalLaunchIntent intent, FidelityAuditSnapshot audit)
    {
        if (audit.Id != intent.AuditId || audit.Fingerprint != intent.AuditFingerprint || audit.Readiness is FidelityAuditReadiness.Blocked or FidelityAuditReadiness.Stale)
            return "The fidelity audit is stale or blocking.";
        var context = audit.Context;
        return context.GameId != intent.GameId || context.InstallationId != intent.InstallationId ||
            context.ProfileId != intent.ProfileId || context.ReferenceId != intent.ReferenceId ||
            context.SelectedExecutableId != intent.ExecutableId || context.SelectedExecutableFingerprint != intent.ExecutableFingerprint
                ? "The launch selection does not match the audited selection." : null;
    }

    private static bool ApprovalMatches(ExternalLaunchPreparation preparation, ExternalLaunchApproval approval) =>
        approval.SessionId == preparation.SessionId && approval.PreparationFingerprint == preparation.PreparationFingerprint &&
        approval.AuditFingerprint == preparation.Intent.AuditFingerprint && approval.ExplicitUserAction;

    private ExternalLaunchResult Result(ExternalLaunchSessionStatus status, string detail,
        ImmutableArray<ExternalLaunchEvent>.Builder events, ExternalLaunchPreparation preparation, bool AddError = false)
    {
        if (AddError)
        {
            var error = new ExternalLaunchEvent(new($"launch-event.mo2.{preparation.SessionId.Value}.{events.Count + 1}"), preparation.SessionId,
                events.Count + 1, timeProvider.GetUtcNow(), ExternalLaunchEventKind.Error, detail);
            events.Add(error);
        }
        var session = new ExternalLaunchSession(preparation.SessionId, preparation.Intent, status, preparation.PreparedAtUtc,
            status is ExternalLaunchSessionStatus.Running or ExternalLaunchSessionStatus.StartingManager ? null : timeProvider.GetUtcNow(), events.ToImmutable(), detail);
        return new(status, session, detail);
    }

    private static string Hash(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values)))).ToLowerInvariant();

    private sealed record Prepared(ExternalLaunchPreparation Preparation, FidelityAuditSnapshot Audit,
        Mo2LaunchEvidence Evidence, Mo2LaunchInvocation Invocation);
}
