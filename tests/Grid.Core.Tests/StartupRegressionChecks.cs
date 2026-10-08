using System.Collections.Immutable;
using Grid.Core.Startup;

internal static class StartupRegressionChecks
{
    public static int Run()
    {
        var count = 0;
        BaselineMatchesItselfPasses();
        count++;
        PreOptimizationReceiptRegresses();
        count++;
        SlowReadyDurationRegresses();
        count++;
        MissingRequiredStageRegresses();
        count++;
        ForbiddenRegistrationBeforeReadyRegresses();
        count++;
        NoBaselineReturnsNoBaseline();
        count++;
        Artifact1cSamplesPassAgainstBaseline();
        count++;
        return count;
    }

    private static void BaselineMatchesItselfPasses()
    {
        var baseline = RequireBaseline();
        var result = StartupReceiptComparison.CompareAgainstBaseline(baseline, baseline);
        Assert(result.Status == StartupRegressionComparisonStatus.Pass, result.StatusToken);
    }

    private static void PreOptimizationReceiptRegresses()
    {
        var baseline = RequireBaseline();
        var regressed = LoadArtifactReceipt("20261002T142243Z_2d5f67ec-23aa-471f-ade8-97fef5256b1c.json");
        var result = StartupReceiptComparison.CompareAgainstBaseline(regressed, baseline);
        Assert(result.Status == StartupRegressionComparisonStatus.Regression, result.StatusToken);
        Assert(
            result.Findings.Any(finding => finding.Code == "READY_DURATION_REGRESSION"),
            "expected ready duration regression for 1B receipt");
    }

    private static void SlowReadyDurationRegresses()
    {
        var baseline = RequireBaseline();
        var slowReadyAt = baseline.StartedAtUtc.AddMilliseconds(4000);
        var slow = baseline with
        {
            StartupId = Guid.NewGuid().ToString("D"),
            DurationMs = 4010,
            Stages = baseline.Stages.Select(stage => stage.StageId == StartupStageId.Ready
                ? stage with
                {
                    FinishedAtUtc = slowReadyAt,
                    DurationMs = 0,
                }
                : stage).ToImmutableArray(),
        };
        slow = StartupInstrumentation.WithComputedDigest(slow);
        var result = StartupReceiptComparison.CompareAgainstBaseline(slow, baseline);
        Assert(result.Status == StartupRegressionComparisonStatus.Regression, result.StatusToken);
    }

    private static void MissingRequiredStageRegresses()
    {
        var baseline = RequireBaseline();
        var trimmed = baseline with
        {
            StartupId = Guid.NewGuid().ToString("D"),
            Stages = baseline.Stages
                .Where(stage => stage.StageId != StartupStageId.ShellUiActivation)
                .ToImmutableArray(),
        };
        trimmed = StartupInstrumentation.WithComputedDigest(trimmed);
        var result = StartupReceiptComparison.CompareAgainstBaseline(trimmed, baseline);
        Assert(result.Status == StartupRegressionComparisonStatus.Regression, result.StatusToken);
        Assert(
            result.Findings.Any(finding =>
                finding.Code == "REQUIRED_STAGE_MISSING" &&
                finding.StageId == StartupStageId.ShellUiActivation),
            "missing UI activation stage");
    }

    private static void ForbiddenRegistrationBeforeReadyRegresses()
    {
        var baseline = RequireBaseline();
        var ready = baseline.Stages.First(stage => stage.StageId == StartupStageId.Ready);
        var poisoned = baseline with
        {
            StartupId = Guid.NewGuid().ToString("D"),
            Stages = baseline.Stages.Add(new StartupStageReceipt(
                "GAME_REGISTRATION",
                StartupStageOutcome.Completed,
                baseline.StartedAtUtc.AddMilliseconds(100),
                ready.StartedAtUtc.AddMilliseconds(-1),
                200,
                StartupWorkClassification.Registration,
                "Simulated registration during startup.")),
        };
        poisoned = StartupInstrumentation.WithComputedDigest(poisoned);
        var result = StartupReceiptComparison.CompareAgainstBaseline(poisoned, baseline);
        Assert(result.Status == StartupRegressionComparisonStatus.Regression, result.StatusToken);
        Assert(
            result.Findings.Any(finding => finding.Code == "FORBIDDEN_WORK_BEFORE_READY"),
            "registration before READY");
    }

    private static void NoBaselineReturnsNoBaseline()
    {
        var baseline = RequireBaseline();
        var result = StartupReceiptComparison.CompareAgainstBaseline(baseline, null);
        Assert(result.Status == StartupRegressionComparisonStatus.NoBaseline, result.StatusToken);
    }

    private static void Artifact1cSamplesPassAgainstBaseline()
    {
        var baseline = RequireBaseline();
        foreach (var file in new[]
                 {
                     "20261002T160031Z_2690681c-6872-4a2f-aae4-b4fa3e7a0bb1.json",
                     "20261002T160033Z_b871dad7-c842-445a-9c87-f4db93d0fa8f.json",
                     "20261002T160028Z_ec058bd1-17ed-4876-b054-627b6cb4175a.json",
                 })
        {
            var receipt = LoadArtifactReceipt(file);
            var result = StartupReceiptComparison.CompareAgainstBaseline(receipt, baseline);
            Assert(result.Status == StartupRegressionComparisonStatus.Pass, $"{file} => {result.StatusToken}");
        }
    }

    private static StartupReceiptDocument RequireBaseline()
    {
        var repositoryRoot = StartupRegressionPolicy.TryResolveRepositoryRoot()
            ?? throw new InvalidOperationException("Repository root not found for startup baseline tests.");
        Environment.SetEnvironmentVariable("GRID_REPOSITORY_ROOT", repositoryRoot);
        var baseline = StartupRegressionPolicy.TryLoadBaselineReceipt()
            ?? throw new InvalidOperationException("Baseline receipt missing from start/baselines.");
        return baseline;
    }

    private static StartupReceiptDocument LoadArtifactReceipt(string fileName)
    {
        var repositoryRoot = StartupRegressionPolicy.TryResolveRepositoryRoot()
            ?? throw new InvalidOperationException("Repository root not found.");
        var path = Path.Combine(repositoryRoot, "artifacts", "startup-receipts", fileName);
        var json = File.ReadAllText(path);
        var document = StartupReceiptJson.Deserialize(json)
            ?? throw new InvalidOperationException($"Could not deserialize {fileName}.");
        if (!StartupInstrumentation.ValidateReceipt(document, out var issue))
            throw new InvalidOperationException($"{fileName} invalid: {issue}");
        return document;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
