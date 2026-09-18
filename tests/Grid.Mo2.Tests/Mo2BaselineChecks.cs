using System.Collections.Immutable;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2BaselineCheckResult(string Name, Exception? Failure);

static class Mo2BaselineChecks
{
    public static async Task<ImmutableArray<Mo2BaselineCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2BaselineCheckResult>();
        await RunAsync(results, "baseline emits selected context, graph, providers, and streaming hashes", CompleteAsync);
        await RunAsync(results, "baseline reports exact authorization gates without content traversal", AuthorizationAsync);
        await RunAsync(results, "baseline refuses a profile that is not selected_profile", ActiveProfileAsync);
        await RunAsync(results, "baseline reads only selected profile contents", SelectedProfileOnlyAsync);
        await RunAsync(results, "plugin seeds resolve provider and master closure", PluginProviderClosureAsync);
        await RunAsync(results, "baseline resumes verified predecessor hash partitions", ResumePartitionAsync);
        await RunAsync(results, "baseline bounds exact SKSE diagnostic outputs without saves or recursion", SkseDiagnosticLogsAsync);
        await RunAsync(results, "baseline stops hashing at the aggregate byte budget", HashBudgetAsync);
        await RunAsync(results, "baseline refuses non-leaf installationFile source claims", SourceArchiveLeafBoundaryAsync);
        await RunAsync(results, "baseline resolves an absolute archive claim only through the authorized downloads leaf", SourceArchiveAbsoluteClaimDownloadsFallbackAsync);
        await RunAsync(results, "baseline discovers matching downloaded versions by exact Nexus sidecar identity", MatchingDownloadedVersionsAsync);
        await RunAsync(results, "baseline follows winning NIFs to missing embedded textures", EmbeddedNifTextureDependencyAsync);
        await RunAsync(results, "partial NIF coverage retains independently proven missing textures", PartialNifCoverageRetainsEvidenceAsync);
        return results.ToImmutable();
    }

    private static async Task CompleteAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var events = new List<Mo2BaselineEvent>();
        var summary = await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        True(summary.Status is Mo2BaselineStatus.Completed or Mo2BaselineStatus.Partial);
        Contains(events, item => item.RecordType == "resolvedRoot");
        Contains(events, item => item.RecordType == "protectedSnapshot");
        Contains(events, item => item.RecordType == "mod");
        Contains(events, item => item.RecordType == "plugin");
        Contains(events, item => item.RecordType == "pluginMaster");
        Contains(events, item => item.RecordType == "pluginScriptInspection" &&
            item.Payload is Mo2BaselinePluginScriptInspectionRecord
            {
                Status: Mo2PluginScriptDependencyStatus.Complete,
                MissingDependencies: 0,
                IssueCount: 0,
            });
        Contains(events, item => item.RecordType == "pluginAssetInspection" &&
            item.Payload is Mo2BaselinePluginAssetInspectionRecord
            {
                Status: Mo2PluginScriptDependencyStatus.Complete,
                AssetReferences: 0,
                MissingDependencies: 0,
                IssueCount: 0,
            });
        False(events.Any(item => item.RecordType == "pluginScriptDependency"));
        False(events.Any(item => item.RecordType == "pluginAssetDependency"));
        Contains(events, item => item.RecordType == "spidInspection" &&
            item.Payload is Mo2BaselineSpidInspectionRecord
            {
                Status: Mo2SpidDistributionStatus.Complete,
                DocumentsScanned: 1,
                DisabledSourceReferences: 1,
                MissingSourceReferences: 0,
            });
        Contains(events, item => item.RecordType == "spidSourceIssue" &&
            item.Payload is Mo2BaselineSpidSourceIssueRecord
            {
                PluginName: "Panties.esp",
                Status: Mo2SpidPluginReferenceStatus.Disabled,
                RuleCount: 1,
            });
        Contains(events, item => item.RecordType == "archive");
        Contains(events, item => item.RecordType == "virtualProvider");
        Contains(events, item => item.RecordType == "mod" &&
            item.Payload is Mo2BaselineModRecord
            {
                Version: "1.2.3",
                NewestVersion: "1.2.4",
                IgnoredVersion: "1.2.2",
                NexusGameName: "SkyrimSE",
                NexusModId: 1234,
                InstallationFile: "Fixture-123.7z",
                Repository: "Nexus",
                ProviderStatus: "1",
                Notes: "fixture notes",
                Comments: "fixture comments",
            } mod && mod.CategoryIds.SequenceEqual([7]) && !mod.RawValues.IsEmpty);
        Contains(events, item => item.RecordType == "sourceArchive" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                Kind: Mo2BaselineSourceArtifactKind.InstallationArchive,
                ExactLeafName: "Fixture-123.7z",
                State: Mo2PathState.Present,
                HashStatus: Mo2BaselineHashStatus.Complete,
                Sha256.Length: 64,
            });
        Contains(events, item => item.RecordType == "sourceArchiveSidecar" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                Kind: Mo2BaselineSourceArtifactKind.MetadataSidecar,
                ExactLeafName: "Fixture-123.7z.meta",
                State: Mo2PathState.Present,
                HashStatus: Mo2BaselineHashStatus.Complete,
                Sha256.Length: 64,
                ProviderIdentity:
                {
                    Repository: "Nexus",
                    GameName: "SkyrimSE",
                    ModId: 1234,
                    FileId: 5678,
                    Version: "1.2.3.0",
                    ArchiveLeaf: "Fixture-123.7z",
                    Status: "ObservedComplete",
                },
            });
        Contains(events, item => item.RecordType == "checkpoint");
        Contains(events, item => item.RecordType == "page");
        Contains(events, item => item.RecordType == "fileHash" &&
            item.Payload is Mo2BaselineFileHashRecord { Status: Mo2BaselineHashStatus.Complete, Sha256.Length: 64 });
        Equal(summary.PhysicalFiles, summary.CompleteHashes);
        True(summary.HashedBytes > 0);
        Equal(2L, summary.SourceArtifacts);
        Equal(2L, summary.CompleteSourceArtifactHashes);
        Equal("summary", events[^1].RecordType);
    }

    private static async Task MatchingDownloadedVersionsAsync()
    {
        using var fixture = await CreateFixtureAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Fixture-125.7z"), "alternate fixture archive");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Fixture-125.7z.meta"),
            "[General]\nrepository=Nexus\ngameName=SkyrimSE\nmodID=1234\nfileID=7777\nversion=1.2.5\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Foreign.7z"), "foreign archive");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Foreign.7z.meta"),
            "[General]\nrepository=Nexus\ngameName=SkyrimSE\nmodID=9999\nfileID=8888\nversion=9.9.9\n");
        var events = new List<Mo2BaselineEvent>();
        var summary = await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "sourceArchive" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                ExactLeafName: "Fixture-125.7z",
                Relationship: "DiscoveredByMatchingMo2DownloadSidecar; repository, game, mod ID, file ID, and archive leaf were observed before classification.",
                HashStatus: Mo2BaselineHashStatus.Complete,
            });
        Contains(events, item => item.RecordType == "sourceArchiveSidecar" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                ExactLeafName: "Fixture-125.7z.meta",
                ProviderIdentity: { ModId: 1234, FileId: 7777, Version: "1.2.5" },
            });
        False(events.Any(item => item.Payload is Mo2BaselineSourceArtifactRecord { ExactLeafName: "Foreign.7z" or "Foreign.7z.meta" }));
        Equal(4L, summary.SourceArtifacts);
    }

    private static async Task AuthorizationAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var events = new List<Mo2BaselineEvent>();
        var request = Request(fixture) with { AuthorizedPaths = [] };
        var summary = await CreateService(fixture).CaptureAsync(
            request,
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Equal(Mo2BaselineStatus.AuthorizationRequired, summary.Status);
        True(!summary.RequiredAuthorizations.IsEmpty);
        Contains(events, item => item.RecordType == "gateIssue");
        False(events.Any(item => item.RecordType is "mod" or "fileHash" or "virtualProvider"));
    }

    private static async Task ActiveProfileAsync()
    {
        using var fixture = await CreateFixtureAsync();
        fixture.CreateProfile("Other", settings: "[General]\nLocalSaves=false\nLocalSettings=true\n");
        fixture.SelectProfile("Other");
        var events = new List<Mo2BaselineEvent>();
        var summary = await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Equal(Mo2BaselineStatus.ContextUnavailable, summary.Status);
        Contains(events, item => item.RecordType == "gateIssue" &&
            item.Payload is Mo2BaselineGateRecord { Code: "mo2.baseline.active_profile_mismatch" });
        False(events.Any(item => item.RecordType == "fileHash"));
    }

    private static async Task HashBudgetAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var events = new List<Mo2BaselineEvent>();
        var request = Request(fixture) with
        {
            Limits = new(MaximumEntries: 10_000, MaximumTotalHashBytes: 1, MaximumFileBytes: 0, CheckpointInterval: 1),
        };
        var summary = await CreateService(fixture).CaptureAsync(
            request,
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Equal(Mo2BaselineStatus.Partial, summary.Status);
        Contains(events, item => item.RecordType == "fileHash" &&
            item.Payload is Mo2BaselineFileHashRecord { Status: Mo2BaselineHashStatus.AggregateLimitExceeded });
        Equal(0L, summary.HashedBytes);
    }

    private static async Task SelectedProfileOnlyAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var other = fixture.CreateProfile("Other", modList: "+Must Not Read\n");
        var guarded = new ForeignProfileGuardFileSystem(fixture.FileSystem, other);
        var summary = await CreateService(fixture, guarded).CaptureAsync(
            Request(fixture), (_, _) => ValueTask.CompletedTask);

        True(summary.Status is Mo2BaselineStatus.Completed or Mo2BaselineStatus.Partial);
        Equal(0, guarded.ForbiddenReads);
    }

    private static async Task PluginProviderClosureAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var events = new List<Mo2BaselineEvent>();
        var request = Request(fixture) with { ExplicitProviderSeeds = [] };
        await CreateService(fixture).CaptureAsync(
            request, (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "sourceArchive" &&
            item.Payload is Mo2BaselineSourceArtifactRecord { ModName: "Fixture Mod" });
        Contains(events, item => item.RecordType == "fileHash" &&
            item.Payload is Mo2BaselineFileHashRecord { ProviderName: "Fixture Mod" });
    }

    private static async Task ResumePartitionAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var firstEvents = new List<Mo2BaselineEvent>();
        await CreateService(fixture).CaptureAsync(
            Request(fixture), (item, _) => { firstEvents.Add(item); return ValueTask.CompletedTask; });
        var completed = firstEvents
            .Where(item => item.RecordType == "fileHash")
            .Select(item => (Mo2BaselineFileHashRecord)item.Payload)
            .Where(item => item.Status == Mo2BaselineHashStatus.Complete)
            .Take(1)
            .ToImmutableDictionary(item => item.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        True(completed.Count == 1);

        var resumedEvents = new List<Mo2BaselineEvent>();
        await CreateService(fixture).CaptureAsync(
            Request(fixture) with { ResumeHashes = completed },
            (item, _) => { resumedEvents.Add(item); return ValueTask.CompletedTask; });
        Contains(resumedEvents, item => item.RecordType == "fileHash" &&
            item.Payload is Mo2BaselineFileHashRecord { Detail: "ReusedFromVerifiedPredecessorPartition" });
    }

    private static async Task SkseDiagnosticLogsAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var skse = Path.Combine(fixture.Root, "global-settings", "SKSE");
        Directory.CreateDirectory(Path.Combine(skse, "nested"));
        await File.WriteAllTextAsync(Path.Combine(skse, "crash-2026-01-01.log"), "crash evidence");
        await File.WriteAllTextAsync(Path.Combine(skse, "skse64.log"), "native plugin load evidence");
        await File.WriteAllTextAsync(Path.Combine(skse, "unrelated.log"), "must not be collected");
        await File.WriteAllTextAsync(Path.Combine(skse, "nested", "crash-nested.log"), "must not recurse");
        var events = new List<Mo2BaselineEvent>();
        var request = Request(fixture) with { AuthorizedPaths = Request(fixture).AuthorizedPaths.Add(skse) };
        await CreateService(fixture).CaptureAsync(
            request, (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "fileHash" && item.Payload is Mo2BaselineFileHashRecord
            { Kind: Mo2BaselineFileKind.DiagnosticOutput, VirtualPath: "diagnostics/skse/crash-2026-01-01.log" });
        Contains(events, item => item.RecordType == "fileHash" && item.Payload is Mo2BaselineFileHashRecord
            { Kind: Mo2BaselineFileKind.DiagnosticOutput, VirtualPath: "diagnostics/skse/skse64.log" });
        False(events.Any(item => item.RecordType == "fileHash" && item.Payload is Mo2BaselineFileHashRecord hash &&
            (hash.VirtualPath.Contains("nested", StringComparison.OrdinalIgnoreCase) || hash.VirtualPath.Contains("unrelated.log", StringComparison.OrdinalIgnoreCase))));
    }

    private static async Task SourceArchiveLeafBoundaryAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        await File.WriteAllTextAsync(Path.Combine(modRoot, "meta.ini"), "[General]\ninstallationFile=../outside.7z\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "outside.7z"), "must not be inspected");
        var events = new List<Mo2BaselineEvent>();

        await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "sourceArchive" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                State: Mo2PathState.Invalid,
                CanonicalPath: null,
                HashStatus: null,
                Sha256: null,
            });
        False(events.Any(item => item.RecordType == "sourceArchiveSidecar"));
    }

    private static async Task SourceArchiveAbsoluteClaimDownloadsFallbackAsync()
    {
        using var fixture = await CreateFixtureAsync();
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        await File.WriteAllTextAsync(Path.Combine(modRoot, "meta.ini"),
            "[General]\nversion=1.2.3\ngameName=SkyrimSE\nmodid=1234\nrepository=Nexus\n" +
            "installationFile=C:/Retired Archive Library/Fixture-123.7z\n");
        var events = new List<Mo2BaselineEvent>();

        await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "sourceArchive" &&
            item.Payload is Mo2BaselineSourceArtifactRecord
            {
                ExactLeafName: "Fixture-123.7z",
                State: Mo2PathState.Present,
                HashStatus: Mo2BaselineHashStatus.Complete,
            } source && source.Relationship.StartsWith("LeafDerivedFromMo2ExternalPathClaim", StringComparison.Ordinal));
        False(events.Any(item => item.Payload is Mo2BaselineSourceArtifactRecord source &&
            source.CanonicalPath?.Contains("Retired Archive Library", StringComparison.OrdinalIgnoreCase) == true));
    }

    private static async Task EmbeddedNifTextureDependencyAsync()
    {
        using var fixture = await CreateFixtureAsync();
        using var pluginFixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await pluginFixture.WritePluginAsync(
            "Fixture.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f4d5241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMO", 0x01000110, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"armor\wearable.nif"))));
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        File.Copy(plugin, Path.Combine(modRoot, "Fixture.esp"), overwrite: true);
        var meshRoot = Path.Combine(modRoot, "meshes", "armor");
        Directory.CreateDirectory(meshRoot);
        await File.WriteAllBytesAsync(Path.Combine(meshRoot, "wearable.nif"),
            AssetFixture.Nif(@"textures\armor\missing_embedded_d.dds"));
        var events = new List<Mo2BaselineEvent>();

        var summary = await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        True(summary.Status is Mo2BaselineStatus.Completed or Mo2BaselineStatus.Partial);
        Contains(events, item => item.RecordType == "pluginAssetInspection" &&
            item.Payload is Mo2BaselinePluginAssetInspectionRecord
            {
                NifMeshesInspected: 1,
                EmbeddedTextureReferences: 1,
                MissingDependencies: 1,
            });
        Contains(events, item => item.RecordType == "pluginAssetDependency" &&
            item.Payload is Mo2BaselinePluginAssetDependencyRecord dependency &&
            dependency.RequiredVirtualPath == @"textures\armor\missing_embedded_d.dds" &&
            dependency.Samples.Any(sample => sample.DiscoveredThroughVirtualPath == @"meshes\armor\wearable.nif"));
        Contains(events, item => item.RecordType == "pluginRecordProvenance" &&
            item.Payload is Mo2PluginRecordProvenanceSummary
            {
                RecordSignature: "ARMO",
                PluginName: "Fixture.esp",
                NewRecordCount: 1,
                OverrideRecordCount: 0,
            });
        Contains(events, item => item.RecordType == "pluginRecordCatalog" &&
            item.Payload is Mo2PluginRecordCatalogEntry
            {
                RecordSignature: "ARMO",
                PluginName: "Fixture.esp",
                EditorId: null,
                IsOverride: false,
            });
        Contains(events, item => item.RecordType == "gateIssue" &&
            item.Payload is Mo2BaselineGateRecord { Code: "mo2.baseline.plugin_texture_missing" } issue &&
            issue.Detail.Contains(@"meshes\armor\wearable.nif", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task PartialNifCoverageRetainsEvidenceAsync()
    {
        using var fixture = await CreateFixtureAsync();
        using var pluginFixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await pluginFixture.WritePluginAsync(
            "Fixture.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f4d5241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMO", 0x01000110, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"armor\wearable.nif")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMO", 0x01000111, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"armor\malformed.nif"))));
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        File.Copy(plugin, Path.Combine(modRoot, "Fixture.esp"), overwrite: true);
        var meshRoot = Path.Combine(modRoot, "meshes", "armor");
        Directory.CreateDirectory(meshRoot);
        await File.WriteAllBytesAsync(Path.Combine(meshRoot, "wearable.nif"),
            AssetFixture.Nif(@"textures\armor\missing_embedded_d.dds"));
        await File.WriteAllBytesAsync(Path.Combine(meshRoot, "malformed.nif"), "not-a-nif"u8.ToArray());
        var events = new List<Mo2BaselineEvent>();

        await CreateService(fixture).CaptureAsync(
            Request(fixture),
            (item, _) => { events.Add(item); return ValueTask.CompletedTask; });

        Contains(events, item => item.RecordType == "pluginAssetInspection" &&
            item.Payload is Mo2BaselinePluginAssetInspectionRecord
            {
                Status: Mo2PluginScriptDependencyStatus.Partial,
                DirectRecordStatus: Mo2PluginScriptDependencyStatus.Complete,
                NifTextureStatus: Mo2PluginScriptDependencyStatus.Partial,
                MissingDependencies: 1,
            });
        Contains(events, item => item.RecordType == "pluginAssetDependency" &&
            item.Payload is Mo2BaselinePluginAssetDependencyRecord dependency &&
            dependency.RequiredVirtualPath == @"textures\armor\missing_embedded_d.dds");
        Contains(events, item => item.RecordType == "gateIssue" &&
            item.Payload is Mo2BaselineGateRecord { Code: "mo2.nif_texture.mesh_inspection_incomplete" });
    }

    private static async Task<Mo2ProfileFixtureBuilder> CreateFixtureAsync()
    {
        var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        fixture.CreateProfile(
            "Baseline",
            modList: "+Fixture Mod\n",
            plugins: "Skyrim.esm\n*Fixture.esp\nPanties.esp\n",
            loadOrder: "Skyrim.esm\nFixture.esp\nPanties.esp\n",
            settings: "[General]\nLocalSaves=false\nLocalSettings=true\n");
        fixture.SelectProfile("Baseline");
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "textures"));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "meta.ini"),
            "[General]\nversion=1.2.3\nnewestVersion=1.2.4\nignoredVersion=1.2.2\n" +
            "category=7\ngameName=SkyrimSE\nmodid=1234\ninstallationFile=Fixture-123.7z\n" +
            "notes=fixture notes\ncomments=fixture comments\nrepository=Nexus\n" +
            "lastNexusUpdate=1700000000\nnexusFileStatus=1\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Fixture-123.7z"), "fixture archive");
        await File.WriteAllTextAsync(Path.Combine(fixture.InstanceRoot, "downloads", "Fixture-123.7z.meta"),
            "[General]\nrepository=Nexus\ngameName=SkyrimSE\nmodID=1234\nfileID=5678\nversion=1.2.3.0\n" +
            "url=https://cdn.example.invalid/archive?token=must-not-be-retained\n");
        await File.WriteAllBytesAsync(Path.Combine(modRoot, "Fixture.esp"),
            Mo2ResolvedStateFixtureBytes.Plugin(0x200, "Skyrim.esm"));
        await File.WriteAllBytesAsync(Path.Combine(modRoot, "Panties.esp"),
            Mo2ResolvedStateFixtureBytes.Plugin(0x201, "Skyrim.esm"));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "Fixture_DISTR.ini"),
            "Outfit = 0x800~Panties.esp|NONE|NONE|NONE|F|NONE|100\n");
        await File.WriteAllBytesAsync(Path.Combine(modRoot, "Fixture.bsa"), Mo2ResolvedStateFixtureBytes.Bsa(1));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "textures", "fixture.dds"), "fixture texture");
        await File.WriteAllBytesAsync(Path.Combine(fixture.GameRoot, "Data", "Skyrim.esm"),
            Mo2ResolvedStateFixtureBytes.Plugin(1));
        return fixture;
    }

    private static Mo2BaselineService CreateService(
        Mo2ProfileFixtureBuilder fixture,
        IMo2InventoryFileSystem? fileSystem = null) => new(
        fileSystem ?? fixture.FileSystem,
        fixture.Paths,
        Path.GetDirectoryName(fixture.InstanceRoot)!,
        Path.Combine(fixture.Root, "global-settings"));

    private static Mo2BaselineRequest Request(Mo2ProfileFixtureBuilder fixture) => new(
        fixture.ApplicationRoot,
        fixture.InstanceRoot,
        "Baseline",
        fixture.Validation.Paths
            .Where(path => path.CanonicalPath is not null)
            .Select(path => path.CanonicalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray(),
        ["Fixture Mod"],
        ["Fixture.esp"],
        new(MaximumEntries: 10_000, MaximumTotalHashBytes: 128 * 1024 * 1024,
            MaximumFileBytes: 0, CheckpointInterval: 2));

    private static async Task RunAsync(
        ImmutableArray<Mo2BaselineCheckResult>.Builder results,
        string name,
        Func<Task> action)
    {
        try
        {
            await action();
            results.Add(new(name, null));
        }
        catch (Exception exception)
        {
            results.Add(new(name, exception));
        }
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }

    private static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate))
        {
            var observed = string.Join(",", values.OfType<Mo2BaselineEvent>().Select(item => item.RecordType));
            throw new InvalidOperationException($"Expected a matching item. Observed baseline records: {observed}");
        }
    }
}

sealed class ForeignProfileGuardFileSystem(
    IMo2InventoryFileSystem inner,
    string forbiddenRoot) : IMo2InventoryFileSystem
{
    public int ForbiddenReads { get; private set; }
    private void Guard(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(forbiddenRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            ForbiddenReads++;
            throw new InvalidOperationException("A non-selected profile content path was read.");
        }
    }
    public Mo2PathState ProbeFile(string path) { Guard(path); return inner.ProbeFile(path); }
    public Mo2PathState ProbeDirectory(string path) { Guard(path); return inner.ProbeDirectory(path); }
    public IReadOnlyList<string> EnumerateDirectories(string path) { Guard(path); return inner.EnumerateDirectories(path); }
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) { Guard(path); return inner.EnumerateDirectoriesWithState(path); }
    public IReadOnlyList<string> EnumerateFiles(string path) { Guard(path); return inner.EnumerateFiles(path); }
    public Mo2FileMetadata GetFileMetadata(string path) { Guard(path); return inner.GetFileMetadata(path); }
    public Mo2DirectoryMetadata GetDirectoryMetadata(string path) { Guard(path); return inner.GetDirectoryMetadata(path); }
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) { Guard(path); return inner.ReadBytesAsync(path, maximumBytes, cancellationToken); }
    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) { Guard(path); return inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken); }
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) { Guard(path); return inner.ReadTextAsync(path, maximumBytes, cancellationToken); }
}
