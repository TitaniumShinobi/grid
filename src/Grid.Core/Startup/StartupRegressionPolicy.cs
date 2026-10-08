using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grid.Core.Startup;

public sealed record StartupRegressionPolicyDocument(
    int SchemaVersion,
    string ManifestId,
    string Contract,
    string BaselineReceiptFile,
    StartupRegressionReadyDurationPolicy ReadyDuration,
    StartupRegressionStageDurationPolicy StageDuration,
    ImmutableArray<string> RequiredBeforeReadyStageIds,
    ImmutableArray<string> ForbiddenBeforeReadyWorkClassifications,
    StartupRegressionBackgroundWorkPolicy? BackgroundWork,
    StartupRegressionShellUsabilityPolicy? ShellUsability);

public sealed record StartupRegressionReadyDurationPolicy(
    double RelativeRatio,
    long AbsoluteMs,
    long? ReadyDurationMaxMs);

public sealed record StartupRegressionStageDurationPolicy(
    double RelativeRatio,
    long AbsoluteMs,
    ImmutableArray<string> MonitoredStageIds,
    IReadOnlyDictionary<string, long> StageDurationCeilingMs);

public sealed record StartupRegressionBackgroundWorkPolicy(
    string BackgroundStageId,
    bool MustNotCompleteBeforeReady,
    bool MustBeBackgroundOptionalClassification);

public sealed record StartupRegressionShellUsabilityPolicy(
    string ReadyStageId,
    string RequiredReadiness,
    string? RequiredReadyDetailContains);

public static class StartupRegressionPolicy
{
    public const string ManifestId = "grid.startup-regression-policy.v1";
    public const string DefaultPolicyFileName = "startup-regression-policy.v1.json";
    public const string DefaultBaselineReceiptFileName = "grid-startup-receipt-1c-baseline.json";

    private const string BaselinePathEnvironmentVariable = "GRID_STARTUP_BASELINE_PATH";
    private const string PolicyPathEnvironmentVariable = "GRID_STARTUP_REGRESSION_POLICY_PATH";
    private const string RepositoryRootEnvironmentVariable = "GRID_REPOSITORY_ROOT";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string ResolveBaselinesDirectory()
    {
        var baselinePath = Environment.GetEnvironmentVariable(BaselinePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(baselinePath))
        {
            if (File.Exists(baselinePath))
                return Path.GetDirectoryName(Path.GetFullPath(baselinePath))!;
            return Path.GetFullPath(baselinePath);
        }

        var repositoryRoot = TryResolveRepositoryRoot();
        return repositoryRoot is null
            ? string.Empty
            : Path.Combine(repositoryRoot, "start", "baselines");
    }

    public static string? TryResolveBaselineReceiptPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(BaselinePathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var directory = ResolveBaselinesDirectory();
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var path = Path.Combine(directory, DefaultBaselineReceiptFileName);
        return File.Exists(path) ? path : null;
    }

    public static string? TryResolvePolicyPath()
    {
        var overridePath = Environment.GetEnvironmentVariable(PolicyPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var directory = ResolveBaselinesDirectory();
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        var path = Path.Combine(directory, DefaultPolicyFileName);
        return File.Exists(path) ? path : null;
    }

    public static StartupRegressionPolicyDocument LoadDefault()
    {
        var path = TryResolvePolicyPath()
            ?? throw new FileNotFoundException("Startup regression policy was not found.", DefaultPolicyFileName);
        return Load(path);
    }

    public static StartupRegressionPolicyDocument Load(string path)
    {
        var payload = JsonSerializer.Deserialize<PolicyPayload>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Startup regression policy deserialized to null.");
        if (payload.SchemaVersion != 1)
            throw new InvalidDataException("Startup regression policy schemaVersion must be 1.");
        if (!string.Equals(payload.ManifestId, ManifestId, StringComparison.Ordinal))
            throw new InvalidDataException("Startup regression policy manifestId mismatch.");

        return new StartupRegressionPolicyDocument(
            payload.SchemaVersion,
            payload.ManifestId ?? ManifestId,
            payload.Contract ?? string.Empty,
            payload.BaselineReceiptFile ?? DefaultBaselineReceiptFileName,
            new StartupRegressionReadyDurationPolicy(
                payload.ReadyDuration?.RelativeRatio ?? 0.15,
                payload.ReadyDuration?.AbsoluteMs ?? 50,
                payload.ReadyDuration?.ReadyDurationMaxMs),
            new StartupRegressionStageDurationPolicy(
                payload.StageDuration?.RelativeRatio ?? 0.15,
                payload.StageDuration?.AbsoluteMs ?? 25,
                payload.StageDuration?.MonitoredStageIds?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
                payload.StageDuration?.StageDurationCeilingMs ?? new Dictionary<string, long>()),
            payload.RequiredBeforeReadyStageIds?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
            payload.ForbiddenBeforeReadyWorkClassifications?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
            payload.BackgroundWork is null
                ? null
                : new StartupRegressionBackgroundWorkPolicy(
                    payload.BackgroundWork.BackgroundStageId ?? StartupStageId.ShellBackgroundHydrate,
                    payload.BackgroundWork.MustNotCompleteBeforeReady,
                    payload.BackgroundWork.MustBeBackgroundOptionalClassification),
            payload.ShellUsability is null
                ? null
                : new StartupRegressionShellUsabilityPolicy(
                    payload.ShellUsability.ReadyStageId ?? StartupStageId.Ready,
                    payload.ShellUsability.RequiredReadiness ?? "READY",
                    payload.ShellUsability.RequiredReadyDetailContains));
    }

    public static StartupReceiptDocument? TryLoadBaselineReceipt()
    {
        var path = TryResolveBaselineReceiptPath();
        if (path is null)
            return null;

        var document = StartupReceiptJson.Deserialize(File.ReadAllText(path));
        if (document is null || !StartupInstrumentation.ValidateReceipt(document, out _))
            return null;

        return document;
    }

    public static string? TryResolveRepositoryRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(RepositoryRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
            return Path.GetFullPath(overrideRoot);

        var probe = AppContext.BaseDirectory;
        for (var depth = 0; depth < 12 && !string.IsNullOrWhiteSpace(probe); depth++)
        {
            var candidate = Path.Combine(probe, "start", "baselines", DefaultBaselineReceiptFileName);
            if (File.Exists(candidate))
                return probe;

            var parent = Directory.GetParent(probe)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, probe, StringComparison.OrdinalIgnoreCase))
                break;
            probe = parent;
        }

        return null;
    }

    private sealed class PolicyPayload
    {
        public int SchemaVersion { get; set; }
        public string? ManifestId { get; set; }
        public string? Contract { get; set; }
        public string? BaselineReceiptFile { get; set; }
        public ReadyDurationPayload? ReadyDuration { get; set; }
        public StageDurationPayload? StageDuration { get; set; }
        public string[]? RequiredBeforeReadyStageIds { get; set; }
        public string[]? ForbiddenBeforeReadyWorkClassifications { get; set; }
        public BackgroundWorkPayload? BackgroundWork { get; set; }
        public ShellUsabilityPayload? ShellUsability { get; set; }
    }

    private sealed class ReadyDurationPayload
    {
        public double RelativeRatio { get; set; }
        public long AbsoluteMs { get; set; }
        public long? ReadyDurationMaxMs { get; set; }
    }

    private sealed class StageDurationPayload
    {
        public double RelativeRatio { get; set; }
        public long AbsoluteMs { get; set; }
        public string[]? MonitoredStageIds { get; set; }
        public Dictionary<string, long>? StageDurationCeilingMs { get; set; }
    }

    private sealed class BackgroundWorkPayload
    {
        public string? BackgroundStageId { get; set; }
        public bool MustNotCompleteBeforeReady { get; set; }
        public bool MustBeBackgroundOptionalClassification { get; set; }
    }

    private sealed class ShellUsabilityPayload
    {
        public string? ReadyStageId { get; set; }
        public string? RequiredReadiness { get; set; }
        public string? RequiredReadyDetailContains { get; set; }
    }
}
