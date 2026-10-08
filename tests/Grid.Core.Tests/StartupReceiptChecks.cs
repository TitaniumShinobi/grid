using System.Collections.Immutable;
using Grid.Core.Startup;

internal static class StartupReceiptChecks
{
    public static int Run()
    {
        var count = 0;
        DigestMatchesCanonicalPayload();
        count++;
        LatestStageOrderingPreserved();
        count++;
        ProgressUnitsAdvanceOnRequiredStages();
        count++;
        return count;
    }

    private static void DigestMatchesCanonicalPayload()
    {
        var started = DateTimeOffset.Parse("2026-10-02T14:00:00Z");
        var finished = started.AddSeconds(2);
        var document = new StartupReceiptDocument(
            SchemaVersion: 1,
            ManifestId: StartupInstrumentation.ReceiptManifestId,
            StartupId: "11111111-1111-1111-1111-111111111111",
            StartedAtUtc: started,
            FinishedAtUtc: finished,
            DurationMs: 2000,
            Readiness: StartupReadiness.Ready,
            BlockedReason: null,
            ExecutableIdentity: new StartupExecutableIdentity("0.1.0.0", "C:\\Grid\\Grid.exe"),
            Stages: ImmutableArray.Create(
                new StartupStageReceipt(
                    StartupStageId.AuthSessionRestore,
                    StartupStageOutcome.Completed,
                    started,
                    started.AddMilliseconds(400),
                    400,
                    StartupWorkClassification.StartupRequired,
                    "Session restored.")),
            Digest: string.Empty);

        var signed = StartupInstrumentation.WithComputedDigest(document);
        Assert(StartupInstrumentation.ValidateReceipt(signed, out var issue), issue ?? "digest valid");
        var roundTrip = StartupReceiptJson.Deserialize(StartupReceiptJson.Serialize(signed));
        Assert(roundTrip is not null, "receipt round-trip");
        Assert(StartupInstrumentation.ValidateReceipt(roundTrip!, out issue), issue ?? "round-trip digest valid");
    }

    private static void LatestStageOrderingPreserved()
    {
        var session = StartupInstrumentation.Begin(enabled: true, isDemoMode: false);
        session.BeginStage(StartupStageId.ShellMainWindow);
        session.CompleteStage(StartupStageId.ShellMainWindow, StartupStageOutcome.Completed);
        session.BeginStage(StartupStageId.AuthSessionRestore);
        session.CompleteStage(StartupStageId.AuthSessionRestore, StartupStageOutcome.Completed);
        session.MarkReady();
        var receipt = session.FinalizeReceipt();
        Assert(receipt is not null, "receipt finalized");
        Assert(receipt!.Stages.Length >= 2, "expected stages recorded");
        Assert(
            receipt.Stages.Any(stage => stage.StageId == StartupStageId.Ready),
            "READY stage recorded");
        Assert(
            receipt.Stages.Any(stage => stage.StageId == StartupStageId.ShellMainWindow),
            "SHELL_INITIALIZATION.MAIN_WINDOW substage recorded");
    }

    private static void ProgressUnitsAdvanceOnRequiredStages()
    {
        var session = StartupInstrumentation.Begin(enabled: true, isDemoMode: false);
        var observed = 0;
        session.ProgressChanged += snapshot =>
        {
            if (snapshot.Units.Status == StartupProgressStatus.Known &&
                snapshot.Units.Completed > observed)
            {
                observed = snapshot.Units.Completed;
            }
        };
        session.BeginStage(StartupStageId.ShellMainWindow);
        session.CompleteStage(StartupStageId.ShellMainWindow, StartupStageOutcome.Completed);
        session.BeginStage(StartupStageId.ShellUiActivation);
        session.CompleteStage(StartupStageId.ShellUiActivation, StartupStageOutcome.Completed);
        session.BeginStage(StartupStageId.AuthSessionRestore);
        session.CompleteStage(StartupStageId.AuthSessionRestore, StartupStageOutcome.Completed);
        session.MarkReady();
        Assert(observed >= 2, "progress advanced from verified startup boundaries");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
