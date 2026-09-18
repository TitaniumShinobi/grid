using System.Diagnostics;
using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ResolvedStateBenchmarkResult(
    int Mods,
    int LooseEntries,
    int ArchiveMembers,
    int PluginHeaders,
    TimeSpan Elapsed,
    long AllocatedBytes)
{
    public double EntriesPerSecond =>
        (ArchiveMembers + PluginHeaders) / Math.Max(Elapsed.TotalSeconds, 0.000001);
}

static class Mo2ResolvedStateBenchmark
{
    public const int ArchiveMemberCount = 250_000;
    public const int PluginHeaderCount = 500;
    public const int ModCount = 2_000;
    public const int LooseEntryCount = 250_000;

    public static async Task<Mo2ResolvedStateBenchmarkResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var archiveBytes = Mo2ResolvedStateFixtureBytes.Bsa(ArchiveMemberCount);
        var pluginBytes = Mo2ResolvedStateFixtureBytes.Plugin(0x200, "Skyrim.esm", "Update.esm");
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();

        await using var archive = new MemoryRandomAccessFile(archiveBytes, "C:\\benchmark\\Synthetic.bsa");
        var archiveResult = await new Mo2BsaIndexParser().ParseAsync(
            archive,
            new Mo2BsaIndexLimits(MaximumMembers: ArchiveMemberCount),
            cancellationToken);
        if (archiveResult.Status != Mo2BinaryObservationStatus.Complete ||
            archiveResult.Members.Length != ArchiveMemberCount)
        {
            throw new InvalidOperationException(
                $"Synthetic archive benchmark resolved {archiveResult.Members.Length} of {ArchiveMemberCount} members with status {archiveResult.Status}.");
        }

        var loose = ImmutableArray.CreateBuilder<Mo2LooseProviderInput>(ModCount);
        var sequence = 0;
        for (var mod = 0; mod < ModCount; mod++)
        {
            var entries = ImmutableArray.CreateBuilder<Mo2ContentTreeEntry>(LooseEntryCount / ModCount);
            for (var local = 0; local < LooseEntryCount / ModCount; local++, sequence++)
            {
                entries.Add(new(
                    Mo2ContentEntryKind.File,
                    $"textures\\fixture-{sequence:D6}.dds",
                    $"C:\\synthetic\\mod-{mod:D4}\\fixture-{sequence:D6}.dds",
                    $"identity-{sequence}",
                    1,
                    1,
                    sequence,
                    FileAttributes.Normal));
            }

            loose.Add(new(
                VirtualProviderKind.ModLooseFile,
                $"Synthetic mod {mod:D4}",
                new($"mod.synthetic-{mod:D4}"),
                mod,
                new(
                    Mo2ContentRootKind.Mod,
                    $"C:\\synthetic\\mod-{mod:D4}",
                    Mo2PathState.Present,
                    entries.ToImmutable(),
                    $"tree-{mod:D4}",
                    false,
                    [])));
        }

        var archiveEntry = new ResolvedArchiveEntry(
            new("archive.synthetic"), "Synthetic.bsa", ArchiveFormat.Bsa, 105,
            ArchiveSupportStatus.Supported, ArchiveActivationState.Active,
            ArchiveActivationProvenance.IniResourceList, null, "Synthetic archive", ArchiveMemberCount,
            archiveResult.ContentFingerprint!, DateTimeOffset.UnixEpoch, []);
        var resolution = new Mo2VirtualDataResolver().Resolve(
            new("resolved.synthetic-benchmark"),
            loose,
            [new(archiveEntry, archiveResult.Members, ModCount + 1)],
            Mo2ResolvedStateLimits.Default,
            cancellationToken);
        if (resolution.Entries.Length != LooseEntryCount ||
            resolution.ProviderCount != LooseEntryCount + ArchiveMemberCount ||
            resolution.IsPartial)
        {
            throw new InvalidOperationException("Synthetic provider resolution produced incorrect or partial counts.");
        }

        var pluginParser = new Mo2PluginHeaderParser();
        await using var plugin = new MemoryRandomAccessFile(pluginBytes, "C:\\benchmark\\Synthetic.esp");
        for (var index = 0; index < PluginHeaderCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pluginResult = await pluginParser.ParseAsync(plugin, "Synthetic.esp", new(), cancellationToken);
            if (pluginResult.Status != Mo2BinaryObservationStatus.Complete ||
                pluginResult.HasLightFlag != true ||
                pluginResult.Masters.Length != 2)
            {
                throw new InvalidOperationException($"Synthetic plugin benchmark failed at header {index}.");
            }
        }

        stopwatch.Stop();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        return new(ModCount, LooseEntryCount, ArchiveMemberCount, PluginHeaderCount, stopwatch.Elapsed, allocated);
    }
}
