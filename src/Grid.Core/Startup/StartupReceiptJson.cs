using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grid.Core.Startup;

public static class StartupReceiptJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Serialize(StartupReceiptDocument document)
    {
        var payload = new ReceiptPayload(
            document.SchemaVersion,
            document.ManifestId,
            document.StartupId,
            document.StartedAtUtc,
            document.FinishedAtUtc,
            document.DurationMs,
            ToReadinessToken(document.Readiness),
            document.BlockedReason,
            document.ExecutableIdentity is null
                ? null
                : new ExecutablePayload(
                    document.ExecutableIdentity.AssemblyVersion,
                    document.ExecutableIdentity.ProcessPath),
            document.Stages.Select(stage => new StagePayload(
                stage.StageId,
                ToOutcomeToken(stage.Outcome),
                stage.StartedAtUtc,
                stage.FinishedAtUtc,
                stage.DurationMs,
                stage.WorkClassification is null ? null : ToClassificationTokenForComparison(stage.WorkClassification.Value),
                stage.Detail)).ToArray(),
            document.Digest);
        return JsonSerializer.Serialize(payload, Options);
    }

    public static StartupReceiptDocument? Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<ReceiptPayload>(json, Options);
        if (payload is null)
            return null;

        return new StartupReceiptDocument(
            payload.SchemaVersion,
            payload.ManifestId,
            payload.StartupId,
            payload.StartedAtUtc,
            payload.FinishedAtUtc,
            payload.DurationMs,
            ParseReadiness(payload.Readiness),
            payload.BlockedReason,
            payload.ExecutableIdentity is null
                ? null
                : new StartupExecutableIdentity(
                    payload.ExecutableIdentity.AssemblyVersion,
                    payload.ExecutableIdentity.ProcessPath),
            payload.Stages.Select(stage => new StartupStageReceipt(
                stage.StageId,
                ParseOutcome(stage.Outcome),
                stage.StartedAtUtc,
                stage.FinishedAtUtc,
                stage.DurationMs,
                stage.WorkClassification is null ? null : ParseClassification(stage.WorkClassification),
                stage.Detail)).ToImmutableArray(),
            payload.Digest);
    }

    internal static string ToReadinessToken(StartupReadiness readiness) => readiness switch
    {
        StartupReadiness.Ready => "READY",
        StartupReadiness.AuthRequired => "AUTH_REQUIRED",
        _ => "BLOCKED",
    };

    private static StartupReadiness ParseReadiness(string token) => token switch
    {
        "READY" => StartupReadiness.Ready,
        "AUTH_REQUIRED" => StartupReadiness.AuthRequired,
        _ => StartupReadiness.Blocked,
    };

    private static string ToOutcomeToken(StartupStageOutcome outcome) => outcome switch
    {
        StartupStageOutcome.Skipped => "skipped",
        StartupStageOutcome.Failed => "failed",
        StartupStageOutcome.Indeterminate => "indeterminate",
        _ => "completed",
    };

    private static StartupStageOutcome ParseOutcome(string token) => token switch
    {
        "skipped" => StartupStageOutcome.Skipped,
        "failed" => StartupStageOutcome.Failed,
        "indeterminate" => StartupStageOutcome.Indeterminate,
        _ => StartupStageOutcome.Completed,
    };

    internal static string ToClassificationTokenForComparison(StartupWorkClassification classification) => classification switch
    {
        StartupWorkClassification.Registration => "REGISTRATION",
        StartupWorkClassification.BackgroundOptional => "BACKGROUND_OPTIONAL",
        StartupWorkClassification.StaleRefresh => "STALE_REFRESH",
        StartupWorkClassification.Rebuild => "REBUILD",
        _ => "STARTUP_REQUIRED",
    };

    private static StartupWorkClassification ParseClassification(string token) => token switch
    {
        "REGISTRATION" => StartupWorkClassification.Registration,
        "BACKGROUND_OPTIONAL" => StartupWorkClassification.BackgroundOptional,
        "STALE_REFRESH" => StartupWorkClassification.StaleRefresh,
        "REBUILD" => StartupWorkClassification.Rebuild,
        _ => StartupWorkClassification.StartupRequired,
    };

    private sealed record ReceiptPayload(
        int SchemaVersion,
        string ManifestId,
        string StartupId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        long DurationMs,
        string Readiness,
        string? BlockedReason,
        ExecutablePayload? ExecutableIdentity,
        StagePayload[] Stages,
        string Digest);

    private sealed record ExecutablePayload(string AssemblyVersion, string ProcessPath);

    private sealed record StagePayload(
        string StageId,
        string Outcome,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? FinishedAtUtc,
        long DurationMs,
        string? WorkClassification,
        string? Detail);
}
