using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Grid.App.Services;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.UiTests;

/// <summary>Explicit, headless, read-only canonical runtime benchmark. Writes measurements only.</summary>
internal static class CanonicalRuntimePerformanceChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly KnowledgeKind[] Kinds =
        [KnowledgeKind.Location, KnowledgeKind.MissionQuest, KnowledgeKind.Item, KnowledgeKind.Actor];

    public static async Task RunAsync(string storePath, string registrationPath, string gameIdValue,
        bool allowCandidate, CatalogPackageId packageId, string outputDirectory)
    {
        storePath = Path.GetFullPath(storePath);
        registrationPath = Path.GetFullPath(registrationPath);
        outputDirectory = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(outputDirectory))
            throw new InvalidOperationException("Benchmark output must be a new isolated directory.");
        Directory.CreateDirectory(outputDirectory);
        var storeInfo = new FileInfo(storePath);
        string storeDigest;
        await using (var stream = File.OpenRead(storePath))
            storeDigest = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        var observedCatalog = await new RegisteredGameCatalogService(new ProductionGridCatalogService(),
            new JsonGameInstallationRegistrationStore(registrationPath)).GetCatalogAsync();
        var gameId = new GameId(gameIdValue);
        var game = observedCatalog.Games.Single(value => value.Id == gameId);
        var installation = game.Installations.Single(value => value.Metadata.Availability == InstallationAvailability.Available);
        var profile = installation.Profiles.Single();
        var locale = new CanonicalTerminologyLocalePreference("en-US", ["en"]);
        var runtime = new CanonicalCatalogRuntimeService(storePath, locale, allowCandidate, packageId, usePreparedNavigation: false);
        var measurements = new List<Measurement>();
        var semantics = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        using var sampler = new MemorySampler();
        var stages = new List<CanonicalRuntimeStageMeasurement>();
        var pendingGraphs = new List<CanonicalSelectorGraphSnapshot>();
        var graphDigests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using var diagnostics = CanonicalRuntimeDiagnostics.Observe(stages.Add, pendingGraphs.Add);
        var initialMemory = ReadMemory();
        Console.WriteLine($"BENCHMARK input {storeInfo.Length} bytes; output {outputDirectory}");

        async Task<T> Measure<T>(string phase, string operation, int cycle, Func<Task<T>> action)
        {
            var before = ReadCounters();
            var stageStart = stages.Count;
            var timestamp = Stopwatch.GetTimestamp();
            var result = await action();
            var elapsed = Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
            var after = ReadCounters();
            var measurement = new Measurement(phase, operation, cycle, elapsed,
                after.CpuMilliseconds - before.CpuMilliseconds,
                after.AllocatedBytes - before.AllocatedBytes,
                after.Gen0 - before.Gen0, after.Gen1 - before.Gen1, after.Gen2 - before.Gen2,
                ReadMemory(), stages.Skip(stageStart).ToArray());
            measurements.Add(measurement);
            await File.AppendAllTextAsync(Path.Combine(outputDirectory, "progress.jsonl"),
                JsonSerializer.Serialize(measurement) + Environment.NewLine);
            Console.WriteLine($"{phase} {operation} cycle={cycle} {elapsed:F3} ms");
            return result;
        }

        await Measure("cold", "load-validation", 0, async () => { await runtime.LoadAsync(observedCatalog); return true; });
        var match = await Measure("cold", "applicability-match", 0,
            () => Task.FromResult(runtime.Match(gameId, installation.Id, profile.Id)));
        if (!match.IsExact || match.ProjectionInput is null)
            throw new InvalidDataException($"Benchmark requires exact runtime match: {match.State}: {match.Detail}");
        await Measure("warm", "applicability-match", 0, () => Task.FromResult(runtime.Match(gameId, installation.Id, profile.Id)));
        var roots = new Dictionary<KnowledgeKind, CanonicalSelectorResult>();
        var descents = new Dictionary<KnowledgeKind, CanonicalNavigationPathId>();
        var unvisited = new Dictionary<KnowledgeKind, CanonicalNavigationPathId>();

        async Task<CanonicalSelectorResult> Query(string phase, string operation, int cycle,
            KnowledgeKind kind, CanonicalNavigationPathId? path = null)
        {
            var result = await Measure(phase, $"{kind}/{operation}", cycle,
                () => Task.FromResult(runtime.Query(match, kind, path)));
            // Serialization is deliberately outside the operation timer.
            foreach (var graph in pendingGraphs)
            {
                var graphKey = $"{graph.KnowledgeKind}-{graph.IncludeIdentifierOnly}-{graph.InspectionMode}-{graph.TerminologyLocale.RequestedLanguageTag}";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(graph.Materialize(), Json);
                var digest = Convert.ToHexString(SHA256.HashData(bytes));
                if (graphDigests.TryGetValue(graphKey, out var priorDigest))
                {
                    if (priorDigest != digest) throw new InvalidDataException("Repeated graph projection changed semantics.");
                }
                else
                {
                    graphDigests[graphKey] = digest;
                    await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"graph-{graphKey}.json"), bytes);
                }
            }
            pendingGraphs.Clear();
            var key = $"{kind}/{result.CurrentPathId.Value}";
            var snapshot = JsonSerializer.SerializeToElement(result, Json);
            if (semantics.TryGetValue(key, out var previous) && previous.GetRawText() != snapshot.GetRawText())
                throw new InvalidDataException("Repeated navigation changed selector semantics.");
            semantics[key] = snapshot;
            return result;
        }

        foreach (var kind in Kinds)
            roots[kind] = await Query("first-kind", "root", 0, kind);
        foreach (var kind in Kinds)
        {
            var candidates = roots[kind].ImmediateChildren.Where(value => value.CanDescend).ToArray();
            if (candidates.Length == 0)
            {
                Console.WriteLine($"{kind}: no nested path exists; descent and Back measurements are not applicable.");
                await Query("warm", "reopen", 0, kind);
                continue;
            }
            descents[kind] = candidates[0].PathId;
            if (candidates.Length > 1) unvisited[kind] = candidates[1].PathId;
            var nested = await Query("first-path", "descent", 0, kind, descents[kind]);
            if (!unvisited.ContainsKey(kind))
            {
                var next = nested.ImmediateChildren.FirstOrDefault(value => value.CanDescend);
                if (next is not null) unvisited[kind] = next.PathId;
            }
            await Query("warm", "back", 0, kind, CanonicalSelectorProjectionEngine.Back(nested));
            await Query("warm", "reopen", 0, kind);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retainedBeforeCycles = ReadMemory();
        var cycleMemory = new List<MemoryReading>();
        for (var cycle = 1; cycle <= 20; cycle++)
        {
            foreach (var kind in Kinds)
            {
                await Query("warm", "root", cycle, kind);
                if (descents.TryGetValue(kind, out var descent))
                {
                    var nested = await Query("warm", "descent", cycle, kind, descent);
                    await Query("warm", "back", cycle, kind, CanonicalSelectorProjectionEngine.Back(nested));
                }
                await Query("warm", "reopen", cycle, kind);
            }
            cycleMemory.Add(ReadMemory());
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retainedAfterCycles = ReadMemory();
        foreach (var (kind, path) in unvisited)
            await Query("new-path-after-warm", "unvisited-descent", 0, kind, path);
        sampler.Stop();
        var summaries = measurements.Where(value => value.Phase == "warm" && value.Cycle > 0)
            .GroupBy(value => value.Operation).Select(group =>
            {
                var times = group.Select(value => value.ElapsedMilliseconds).Order().ToArray();
                return new { operation = group.Key, count = times.Length, medianMilliseconds = times[times.Length / 2],
                    p95Milliseconds = times[(int)Math.Ceiling(times.Length * .95) - 1], maxMilliseconds = times[^1],
                    allocatedBytes = group.Sum(value => value.AllocatedBytes) };
            }).ToArray();
        var semanticsBytes = JsonSerializer.SerializeToUtf8Bytes(semantics, Json);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "navigation-semantics.json"), semanticsBytes);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "benchmark.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, processId = Environment.ProcessId, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            storePath, storeLength = storeInfo.Length, storeSha256 = storeDigest, registrationPath,
            packageId = packageId.Value, gameId = gameId.Value, installationId = installation.Id.Value, profileId = profile.Id.Value,
            projectionPolicy = match.ProjectionInput.Policy.ExactVersion, locale, allowCandidate,
            initialMemory, retainedBeforeCycles, retainedAfterCycles, cycleMemory,
            peakSampledPrivateBytes = sampler.PeakPrivateBytes, peakSampledWorkingSetBytes = sampler.PeakWorkingSetBytes,
            measurements, warmSummaries = summaries, diagnostics = stages, graphSemanticSha256 = graphDigests,
            navigationSemanticSha256 = Convert.ToHexString(SHA256.HashData(semanticsBytes)),
            limitations = new[] { "Process-cold initialization; operating-system file cache is not flushed.",
                "Headless GRID runtime process; unrelated desktop/UI memory is excluded.",
                "Private-memory peak is sampled every 50 ms; forced collections occur only outside operation timers.",
                "Historical store; no Plan 2 registration or store generation performed." }
        }, Json));
        Console.WriteLine($"BENCHMARK COMPLETE {outputDirectory}");
    }

    private static Counters ReadCounters()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
    }
    private static MemoryReading ReadMemory()
    {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        return new(process.PrivateMemorySize64, process.WorkingSet64, process.PeakWorkingSet64,
            GC.GetTotalMemory(false), gc.HeapSizeBytes, gc.FragmentedBytes);
    }
    private sealed record Counters(double CpuMilliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
    private sealed record MemoryReading(long PrivateBytes, long WorkingSetBytes, long PeakWorkingSetBytes,
        long ManagedBytes, long HeapBytes, long FragmentedBytes);
    private sealed record Measurement(string Phase, string Operation, int Cycle, double ElapsedMilliseconds,
        double CpuMilliseconds, long AllocatedBytes, int Gen0, int Gen1, int Gen2, MemoryReading Memory,
        CanonicalRuntimeStageMeasurement[] Stages);

    private sealed class MemorySampler : IDisposable
    {
        private readonly System.Threading.Timer timer;
        private readonly object gate = new();
        public long PeakPrivateBytes { get; private set; }
        public long PeakWorkingSetBytes { get; private set; }
        public MemorySampler() => timer = new System.Threading.Timer(_ =>
        {
            lock (gate)
            {
                using var process = Process.GetCurrentProcess();
                PeakPrivateBytes = Math.Max(PeakPrivateBytes, process.PrivateMemorySize64);
                PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.WorkingSet64);
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        public void Stop() { timer.Change(Timeout.Infinite, Timeout.Infinite); lock (gate) { } }
        public void Dispose() => timer.Dispose();
    }

}
