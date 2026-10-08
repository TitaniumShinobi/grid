using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grid.Core.Startup;

public enum StartupRegressionComparisonStatus
{
    Pass,
    Regression,
    NoBaseline,
    Indeterminate,
}

public sealed record StartupRegressionFinding(
    string Code,
    string? StageId,
    string Detail);

public sealed record StartupRegressionComparisonResult(
    StartupRegressionComparisonStatus Status,
    ImmutableArray<StartupRegressionFinding> Findings)
{
    public string StatusToken => Status switch
    {
        StartupRegressionComparisonStatus.Pass => "PASS",
        StartupRegressionComparisonStatus.Regression => "REGRESSION",
        StartupRegressionComparisonStatus.NoBaseline => "NO_BASELINE",
        _ => "INDETERMINATE",
    };
}

public static class StartupReceiptComparison
{
    public static StartupRegressionComparisonResult CompareAgainstBaseline(
        StartupReceiptDocument receipt,
        StartupReceiptDocument? baseline,
        StartupRegressionPolicyDocument? policy = null)
    {
        if (baseline is null)
            return new(StartupRegressionComparisonStatus.NoBaseline, []);

        policy ??= StartupRegressionPolicy.LoadDefault();

        if (!StartupInstrumentation.ValidateReceipt(receipt, out var receiptIssue))
        {
            return Indeterminate($"Receipt validation failed: {receiptIssue}");
        }

        if (!StartupInstrumentation.ValidateReceipt(baseline, out var baselineIssue))
        {
            return Indeterminate($"Baseline receipt validation failed: {baselineIssue}");
        }

        var findings = new List<StartupRegressionFinding>();
        ValidateShellUsability(receipt, policy.ShellUsability, findings);
        ValidateRequiredStages(receipt, policy, findings);
        ValidateForbiddenWork(receipt, policy, findings);
        ValidateBackgroundWork(receipt, policy.BackgroundWork, findings);
        CompareReadyDuration(receipt, baseline, policy.ReadyDuration, findings);
        CompareStageDurations(receipt, baseline, policy.StageDuration, findings);

        if (findings.Any(finding => finding.Code is "RECEIPT_INDETERMINATE"))
            return new(StartupRegressionComparisonStatus.Indeterminate, findings.ToImmutableArray());

        return findings.Count == 0
            ? new(StartupRegressionComparisonStatus.Pass, [])
            : new(StartupRegressionComparisonStatus.Regression, findings.ToImmutableArray());
    }

    public static StartupRegressionComparisonResult CompareLatestAgainstRepositoryBaseline()
    {
        var baseline = StartupRegressionPolicy.TryLoadBaselineReceipt();
        var latest = StartupInstrumentation.TryReadLatestReceipt();
        if (latest is null && baseline is not null)
            return Indeterminate("No startup receipt found to compare.");

        return CompareAgainstBaseline(latest!, baseline);
    }

    public static string FormatResult(StartupRegressionComparisonResult result)
    {
        var lines = new List<string> { $"STARTUP_REGRESSION_STATUS={result.StatusToken}" };
        foreach (var finding in result.Findings)
        {
            var stage = finding.StageId is null ? string.Empty : $" stage={finding.StageId}";
            lines.Add($"FINDING code={finding.Code}{stage} detail={finding.Detail}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string SerializeResult(StartupRegressionComparisonResult result)
    {
        var payload = new ComparisonPayload(
            result.StatusToken,
            result.Findings.Select(finding => new FindingPayload(finding.Code, finding.StageId, finding.Detail)).ToArray());
        return JsonSerializer.Serialize(payload, ComparisonJsonOptions);
    }

    internal static long ComputeReadyDurationMs(StartupReceiptDocument document)
    {
        var ready = document.Stages.FirstOrDefault(stage => stage.StageId == StartupStageId.Ready);
        if (ready?.FinishedAtUtc is not null)
            return Math.Max(0, (long)(ready.FinishedAtUtc.Value - document.StartedAtUtc).TotalMilliseconds);

        if (ready is not null && ready.DurationMs >= 0)
            return ready.DurationMs;

        return document.DurationMs;
    }

    internal static DateTimeOffset? TryGetReadyInstant(StartupReceiptDocument document)
    {
        var ready = document.Stages.FirstOrDefault(stage => stage.StageId == StartupStageId.Ready);
        return ready?.FinishedAtUtc ?? ready?.StartedAtUtc;
    }

    private static void CompareReadyDuration(
        StartupReceiptDocument receipt,
        StartupReceiptDocument baseline,
        StartupRegressionReadyDurationPolicy policy,
        List<StartupRegressionFinding> findings)
    {
        var observed = ComputeReadyDurationMs(receipt);
        var previous = ComputeReadyDurationMs(baseline);
        if (policy.ReadyDurationMaxMs is long ceiling)
        {
            if (observed > ceiling)
            {
                findings.Add(new(
                    "READY_DURATION_REGRESSION",
                    StartupStageId.Ready,
                    $"observed={observed}ms ceiling={ceiling}ms baseline={previous}ms"));
            }

            return;
        }

        var allowedDelta = Math.Max(policy.AbsoluteMs, (long)Math.Ceiling(previous * policy.RelativeRatio));
        if (observed - previous > allowedDelta)
        {
            findings.Add(new(
                "READY_DURATION_REGRESSION",
                StartupStageId.Ready,
                $"observed={observed}ms baseline={previous}ms allowedDelta={allowedDelta}ms"));
        }
    }

    private static void CompareStageDurations(
        StartupReceiptDocument receipt,
        StartupReceiptDocument baseline,
        StartupRegressionStageDurationPolicy policy,
        List<StartupRegressionFinding> findings)
    {
        var baselineStages = baseline.Stages
            .Where(stage => policy.MonitoredStageIds.Contains(stage.StageId, StringComparer.Ordinal))
            .ToDictionary(stage => stage.StageId, StringComparer.Ordinal);

        foreach (var stageId in policy.MonitoredStageIds)
        {
            if (!baselineStages.TryGetValue(stageId, out var prior))
            {
                findings.Add(new(
                    "BASELINE_STAGE_MISSING",
                    stageId,
                    "Monitored stage absent from baseline receipt."));
                continue;
            }

            var observedStage = receipt.Stages.FirstOrDefault(stage => stage.StageId == stageId);
            if (observedStage is null)
            {
                findings.Add(new(
                    "REQUIRED_STAGE_MISSING",
                    stageId,
                    "Monitored stage absent from observed receipt."));
                continue;
            }

            if (observedStage.Outcome is StartupStageOutcome.Failed or StartupStageOutcome.Indeterminate)
            {
                findings.Add(new(
                    "REQUIRED_STAGE_UNMEASURABLE",
                    stageId,
                    $"Stage outcome={observedStage.Outcome}."));
                continue;
            }

            if (policy.StageDurationCeilingMs.TryGetValue(stageId, out var ceiling))
            {
                if (observedStage.DurationMs > ceiling)
                {
                    findings.Add(new(
                        "STAGE_DURATION_REGRESSION",
                        stageId,
                        $"observed={observedStage.DurationMs}ms ceiling={ceiling}ms baseline={prior.DurationMs}ms"));
                }

                continue;
            }

            var allowedDelta = Math.Max(policy.AbsoluteMs, (long)Math.Ceiling(prior.DurationMs * policy.RelativeRatio));
            if (observedStage.DurationMs - prior.DurationMs > allowedDelta)
            {
                findings.Add(new(
                    "STAGE_DURATION_REGRESSION",
                    stageId,
                    $"observed={observedStage.DurationMs}ms baseline={prior.DurationMs}ms allowedDelta={allowedDelta}ms"));
            }
        }
    }

    private static void ValidateRequiredStages(
        StartupReceiptDocument receipt,
        StartupRegressionPolicyDocument policy,
        List<StartupRegressionFinding> findings)
    {
        foreach (var stageId in policy.RequiredBeforeReadyStageIds)
        {
            var stage = receipt.Stages.FirstOrDefault(entry => entry.StageId == stageId);
            if (stage is null)
            {
                findings.Add(new("REQUIRED_STAGE_MISSING", stageId, "Required before READY."));
                continue;
            }

            if (stage.Outcome is StartupStageOutcome.Failed)
                findings.Add(new("REQUIRED_STAGE_FAILED", stageId, stage.Detail ?? "Stage failed."));
        }
    }

    private static void ValidateForbiddenWork(
        StartupReceiptDocument receipt,
        StartupRegressionPolicyDocument policy,
        List<StartupRegressionFinding> findings)
    {
        var readyInstant = TryGetReadyInstant(receipt);
        foreach (var stage in receipt.Stages)
        {
            if (stage.WorkClassification is null)
                continue;

            var token = StartupReceiptJson.ToClassificationTokenForComparison(stage.WorkClassification.Value);
            if (!policy.ForbiddenBeforeReadyWorkClassifications.Contains(token, StringComparer.Ordinal))
                continue;

            if (readyInstant is null || StageEndedBefore(stage, readyInstant.Value))
                findings.Add(new("FORBIDDEN_WORK_BEFORE_READY", stage.StageId, token));
        }

        foreach (var stage in receipt.Stages.Where(entry =>
                     entry.WorkClassification is StartupWorkClassification.Registration or StartupWorkClassification.Rebuild))
        {
            if (readyInstant is null || StageEndedBefore(stage, readyInstant.Value))
            {
                findings.Add(new(
                    "FORBIDDEN_WORK_BEFORE_READY",
                    stage.StageId,
                    StartupReceiptJson.ToClassificationTokenForComparison(stage.WorkClassification!.Value)));
            }
        }

        foreach (var stage in receipt.Stages.Where(entry =>
                     entry.WorkClassification == StartupWorkClassification.StaleRefresh &&
                     entry.Outcome == StartupStageOutcome.Completed))
        {
            if (readyInstant is null || StageEndedBefore(stage, readyInstant.Value))
                findings.Add(new("STALE_REFRESH_BEFORE_READY", stage.StageId, stage.Detail ?? "Stale refresh completed before READY."));
        }
    }

    private static void ValidateBackgroundWork(
        StartupReceiptDocument receipt,
        StartupRegressionBackgroundWorkPolicy? policy,
        List<StartupRegressionFinding> findings)
    {
        if (policy is null)
            return;

        var readyInstant = TryGetReadyInstant(receipt);
        var background = receipt.Stages.FirstOrDefault(stage => stage.StageId == policy.BackgroundStageId);
        if (background is null)
            return;

        if (policy.MustBeBackgroundOptionalClassification &&
            background.WorkClassification != StartupWorkClassification.BackgroundOptional)
        {
            findings.Add(new(
                "BACKGROUND_WORK_MISCLASSIFIED",
                background.StageId,
                $"Expected BACKGROUND_OPTIONAL but observed {background.WorkClassification}."));
        }

        if (!policy.MustNotCompleteBeforeReady || readyInstant is null)
            return;

        if (background.Outcome == StartupStageOutcome.Completed &&
            background.FinishedAtUtc is not null &&
            background.FinishedAtUtc.Value <= readyInstant.Value)
        {
            findings.Add(new(
                "BACKGROUND_WORK_COMPLETED_BEFORE_READY",
                background.StageId,
                "Optional hydrate completed before READY boundary."));
        }

        if (background.Outcome == StartupStageOutcome.Completed &&
            background.WorkClassification == StartupWorkClassification.StartupRequired)
        {
            findings.Add(new(
                "OPTIONAL_WORK_BLOCKING_READY",
                background.StageId,
                "Background hydrate classified as startup-required."));
        }
    }

    private static void ValidateShellUsability(
        StartupReceiptDocument receipt,
        StartupRegressionShellUsabilityPolicy? policy,
        List<StartupRegressionFinding> findings)
    {
        if (policy is null)
            return;

        if (!string.Equals(
                StartupReceiptJson.ToReadinessToken(receipt.Readiness),
                policy.RequiredReadiness,
                StringComparison.Ordinal))
        {
            findings.Add(new(
                "READY_NOT_REACHED",
                policy.ReadyStageId,
                $"Readiness={StartupReceiptJson.ToReadinessToken(receipt.Readiness)}."));
        }

        var ready = receipt.Stages.FirstOrDefault(stage => stage.StageId == policy.ReadyStageId);
        if (ready is null || ready.Outcome is not StartupStageOutcome.Completed)
        {
            findings.Add(new("READY_NOT_REACHED", policy.ReadyStageId, "READY stage missing or incomplete."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(policy.RequiredReadyDetailContains) &&
            (ready.Detail is null ||
             !ready.Detail.Contains(policy.RequiredReadyDetailContains, StringComparison.Ordinal)))
        {
            findings.Add(new(
                "READY_BEFORE_USABLE_SHELL",
                policy.ReadyStageId,
                "READY detail did not attest shell usability."));
        }
    }

    private static bool StageEndedBefore(StartupStageReceipt stage, DateTimeOffset readyInstant)
    {
        var end = stage.FinishedAtUtc ?? stage.StartedAtUtc;
        return end <= readyInstant;
    }

    private static StartupRegressionComparisonResult Indeterminate(string detail)
        => new(
            StartupRegressionComparisonStatus.Indeterminate,
            [new StartupRegressionFinding("RECEIPT_INDETERMINATE", null, detail)]);

    private static readonly JsonSerializerOptions ComparisonJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private sealed record ComparisonPayload(string Status, FindingPayload[] Findings);

    private sealed record FindingPayload(string Code, string? StageId, string Detail);
}
