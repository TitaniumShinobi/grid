using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grid.Core.Startup;

public enum StartupStageOutcome
{
    Completed,
    Skipped,
    Failed,
    Indeterminate,
}

public enum StartupReadiness
{
    Ready,
    Blocked,
    AuthRequired,
}

public sealed record StartupStageReceipt(
    string StageId,
    StartupStageOutcome Outcome,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long DurationMs,
    StartupWorkClassification? WorkClassification,
    string? Detail);

public sealed record StartupExecutableIdentity(string AssemblyVersion, string ProcessPath);

public sealed record StartupReceiptDocument(
    int SchemaVersion,
    string ManifestId,
    string StartupId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    long DurationMs,
    StartupReadiness Readiness,
    string? BlockedReason,
    StartupExecutableIdentity? ExecutableIdentity,
    ImmutableArray<StartupStageReceipt> Stages,
    string Digest);

public static class StartupInstrumentation
{
    public const string ReceiptManifestId = "grid.startup-receipt.v1";
    private const string ReceiptsDirectoryEnvironmentVariable = "GRID_STARTUP_RECEIPTS_DIR";
    private const string AutomationCloseEnvironmentVariable = "GRID_STARTUP_AUTOMATION_CLOSE";
    private const string CompareBaselineEnvironmentVariable = "GRID_STARTUP_COMPARE_BASELINE";

    internal static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static GridStartupSession? active;

    public static GridStartupSession Begin(bool enabled, bool isDemoMode)
    {
        if (!enabled || isDemoMode)
        {
            active = null;
            return Disabled;
        }

        active?.Dispose();
        active = new GridStartupSession();
        return active;
    }

    public static GridStartupSession? Current => active;

    public static bool AutomationCloseRequested =>
        string.Equals(
            Environment.GetEnvironmentVariable(AutomationCloseEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static bool CompareBaselineRequested =>
        string.Equals(
            Environment.GetEnvironmentVariable(CompareBaselineEnvironmentVariable),
            "1",
            StringComparison.Ordinal);

    public static GridStartupSession Disabled { get; } = new(enabled: false);

    public static StartupReceiptDocument WithComputedDigest(StartupReceiptDocument document)
    {
        var digest = ComputeDigest(document with { Digest = string.Empty });
        return document with { Digest = digest };
    }

    public static bool ValidateReceipt(StartupReceiptDocument document, out string? issue)
    {
        issue = null;
        if (document.SchemaVersion != 1)
        {
            issue = "schemaVersion must be 1.";
            return false;
        }

        if (!string.Equals(document.ManifestId, ReceiptManifestId, StringComparison.Ordinal))
        {
            issue = "manifestId mismatch.";
            return false;
        }

        if (document.DurationMs < 0)
        {
            issue = "durationMs invalid.";
            return false;
        }

        var expected = ComputeDigest(document with { Digest = string.Empty });
        if (!string.Equals(expected, document.Digest, StringComparison.Ordinal))
        {
            issue = "digest mismatch.";
            return false;
        }

        return true;
    }

    public static StartupReceiptDocument? TryReadLatestReceipt(string? directory = null)
    {
        directory ??= ResolveReceiptsDirectory();
        if (!Directory.Exists(directory))
            return null;

        var latest = Directory.EnumerateFiles(directory, "*.json")
            .Where(path => !path.EndsWith(".events.jsonl", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (latest is null)
            return null;

        return StartupReceiptJson.Deserialize(File.ReadAllText(latest));
    }

    public static string ResolveReceiptsDirectory()
    {
        var overridePath = Environment.GetEnvironmentVariable(ReceiptsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        var baseData = Environment.GetEnvironmentVariable("GRID_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(baseData))
            return Path.Combine(Path.GetFullPath(baseData), "startup-receipts");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Grid",
            "startup-receipts");
    }

    internal static string ComputeDigest(StartupReceiptDocument document)
    {
        var payload = StartupReceiptJson.Serialize(document with { Digest = string.Empty });
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static void ClearActive(GridStartupSession session)
    {
        if (ReferenceEquals(active, session))
            active = null;
    }

    internal static void WriteRegressionComparisonIfRequested(StartupReceiptDocument document)
    {
        if (!CompareBaselineRequested)
            return;

        StartupRegressionComparisonResult result;
        try
        {
            var baseline = StartupRegressionPolicy.TryLoadBaselineReceipt();
            result = StartupReceiptComparison.CompareAgainstBaseline(document, baseline);
        }
        catch (Exception exception)
        {
            result = new StartupRegressionComparisonResult(
                StartupRegressionComparisonStatus.Indeterminate,
                [new StartupRegressionFinding("RECEIPT_INDETERMINATE", null, exception.Message)]);
        }

        var directory = ResolveReceiptsDirectory();
        Directory.CreateDirectory(directory);
        var comparisonPath = Path.Combine(directory, $"{document.StartupId}.regression.json");
        File.WriteAllText(comparisonPath, StartupReceiptComparison.SerializeResult(result));
        Console.WriteLine(StartupReceiptComparison.FormatResult(result));
    }
}

public sealed class GridStartupSession : IDisposable
{
    private readonly bool enabled;
    private readonly Stopwatch total = Stopwatch.StartNew();
    private readonly string startupId = Guid.NewGuid().ToString("D");
    private readonly DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
    private readonly List<StartupStageReceipt> stages = [];
    private readonly Dictionary<string, (DateTimeOffset Started, Stopwatch Timer, StartupWorkClassification? Work)> openStages = new(StringComparer.Ordinal);
    private readonly HashSet<string> completedProgressUnits = new(StringComparer.Ordinal);

    private StartupReadiness readiness = StartupReadiness.Blocked;
    private string? blockedReason;
    private bool finalized;
    private StartupProgressSnapshot progressSnapshot = new(
        StartupProgressUnits.Known(0, StartupProgressCatalog.TotalUnits),
        null,
        null);

    internal GridStartupSession(bool enabled = true) => this.enabled = enabled;

    public string StartupId => startupId;

    public event Action<StartupProgressSnapshot>? ProgressChanged;

    public StartupProgressSnapshot ProgressSnapshot => progressSnapshot;

    public void BeginStage(string stageId, StartupWorkClassification? workClassification = null, string? detail = null)
    {
        if (!enabled)
            return;

        if (openStages.ContainsKey(stageId))
            CompleteStage(stageId, StartupStageOutcome.Completed);

        var started = DateTimeOffset.UtcNow;
        openStages[stageId] = (started, Stopwatch.StartNew(), workClassification);
        AppendEvent(stageId, "stage_started", null, workClassification, detail);
        ReportProgress(stageId, detail);
    }

    public void CompleteStage(
        string stageId,
        StartupStageOutcome outcome,
        StartupWorkClassification? workClassification = null,
        string? detail = null)
    {
        if (!enabled)
            return;

        if (!openStages.Remove(stageId, out var open))
        {
            stages.Add(new StartupStageReceipt(
                stageId,
                outcome,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                0,
                workClassification,
                detail ?? "stage closed without an explicit begin boundary"));
            return;
        }

        open.Timer.Stop();
        var finished = DateTimeOffset.UtcNow;
        var classification = workClassification ?? open.Work;
        stages.Add(new StartupStageReceipt(
            stageId,
            outcome,
            open.Started,
            finished,
            open.Timer.ElapsedMilliseconds,
            classification,
            detail));
        var eventKind = outcome switch
        {
            StartupStageOutcome.Skipped => "stage_skipped",
            StartupStageOutcome.Failed => "stage_failed",
            _ => "stage_completed",
        };
        AppendEvent(
            stageId,
            eventKind,
            open.Timer.ElapsedMilliseconds,
            classification,
            detail);
        if (outcome is StartupStageOutcome.Completed or StartupStageOutcome.Skipped)
            RegisterProgressUnit(stageId, detail);
    }

    public void ReportProgress(string stageId, string? detail = null)
    {
        if (!enabled)
            return;

        PublishProgress(stageId, detail, advanceUnit: false);
    }

    public void SkipStage(string stageId, string? detail = null)
        => CompleteStage(stageId, StartupStageOutcome.Skipped, detail: detail);

    public void MarkReady(string? detail = null)
    {
        readiness = StartupReadiness.Ready;
        blockedReason = null;
        BeginStage(StartupStageId.Ready, StartupWorkClassification.StartupRequired, detail);
        CompleteStage(StartupStageId.Ready, StartupStageOutcome.Completed, detail: detail);
    }

    public void MarkAuthRequired(string? detail = null)
    {
        readiness = StartupReadiness.AuthRequired;
        blockedReason = detail;
    }

    public void MarkBlocked(string? detail = null)
    {
        readiness = StartupReadiness.Blocked;
        blockedReason = detail;
    }

    public StartupReceiptDocument? FinalizeReceipt()
    {
        if (!enabled || finalized)
            return null;

        finalized = true;
        foreach (var stageId in openStages.Keys.ToArray())
            CompleteStage(stageId, StartupStageOutcome.Indeterminate, detail: "startup finalized while stage was still open");

        var finishedAtUtc = DateTimeOffset.UtcNow;
        var executable = new StartupExecutableIdentity(
            typeof(StartupInstrumentation).Assembly.GetName().Version?.ToString() ?? "unknown",
            Environment.ProcessPath ?? AppContext.BaseDirectory);

        var documentWithoutDigest = new StartupReceiptDocument(
            SchemaVersion: 1,
            ManifestId: StartupInstrumentation.ReceiptManifestId,
            StartupId: startupId,
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: finishedAtUtc,
            DurationMs: total.ElapsedMilliseconds,
            Readiness: readiness,
            BlockedReason: blockedReason,
            ExecutableIdentity: executable,
            Stages: [.. stages],
            Digest: string.Empty);

        var digest = StartupInstrumentation.ComputeDigest(documentWithoutDigest);
        var document = documentWithoutDigest with { Digest = digest };
        Persist(document);
        StartupInstrumentation.WriteRegressionComparisonIfRequested(document);
        StartupInstrumentation.ClearActive(this);
        return document;
    }

    public void Dispose() => FinalizeReceipt();

    private void Persist(StartupReceiptDocument document)
    {
        var directory = StartupInstrumentation.ResolveReceiptsDirectory();
        Directory.CreateDirectory(directory);
        var fileName = $"{document.StartedAtUtc:yyyyMMdd'T'HHmmss'Z'}_{document.StartupId}.json";
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, StartupReceiptJson.Serialize(document));
    }

    private void RegisterProgressUnit(string stageId, string? detail)
    {
        if (!StartupProgressCatalog.RequiredUnits.Contains(stageId, StringComparer.Ordinal))
            return;

        if (!completedProgressUnits.Add(stageId))
            return;

        PublishProgress(stageId, detail, advanceUnit: true);
    }

    private void PublishProgress(string stageId, string? detail, bool advanceUnit)
    {
        var completed = completedProgressUnits.Count;
        var units = StartupProgressUnits.Known(completed, StartupProgressCatalog.TotalUnits);
        progressSnapshot = new StartupProgressSnapshot(units, stageId, detail);
        ProgressChanged?.Invoke(progressSnapshot);
        if (!advanceUnit)
        {
            AppendEvent(
                stageId,
                "progress_observed",
                null,
                null,
                detail,
                units);
            return;
        }

        AppendEvent(
            stageId,
            "progress_advanced",
            null,
            null,
            detail,
            units);
    }

    private void AppendEvent(
        string stageId,
        string eventKind,
        long? durationMs,
        StartupWorkClassification? workClassification,
        string? detail,
        StartupProgressUnits? progressUnits = null)
    {
        var directory = StartupInstrumentation.ResolveReceiptsDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{startupId}.events.jsonl");
        var units = progressUnits ?? progressSnapshot.Units;
        var payload = new
        {
            schemaVersion = 1,
            startupId,
            stageId,
            eventKind,
            observedAtUtc = DateTimeOffset.UtcNow,
            durationMs,
            workClassification = workClassification?.ToString(),
            detail,
            progressUnits = new
            {
                status = units.Status == StartupProgressStatus.Known ? "KNOWN" : "INDETERMINATE",
                completed = units.Completed,
                total = units.Total,
            },
        };
        File.AppendAllText(
            path,
            JsonSerializer.Serialize(payload, StartupInstrumentation.EventJsonOptions) + Environment.NewLine);
    }
}
