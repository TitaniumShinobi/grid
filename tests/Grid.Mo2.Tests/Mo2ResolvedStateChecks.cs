using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ResolvedStateCheckResult(string Name, Exception? Failure);

static class Mo2ResolvedStateChecks
{
    public static async Task<ImmutableArray<Mo2ResolvedStateCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ResolvedStateCheckResult>();
        await RunAsync(results, "content-root authorization is exact and reference scoped", ContentAuthorizationAsync);
        await RunAsync(results, "content-tree observation is recursive bounded and deterministic", ContentTreeAsync);
        await RunAsync(results, "content-tree observation honors cancellation", ContentTreeCancellationAsync);
        await RunAsync(results, "plugin headers preserve flags masters and malformed evidence", PluginHeaderAsync);
        await RunAsync(results, "BSA indexes preserve members and reject unsupported formats", BsaIndexAsync);
        await RunAsync(results, "binary parsers honor cancellation", BinaryCancellationAsync);
        await RunAsync(results, "loose providers preserve MO2 priority and overwrite winners", LooseProviderResolutionAsync);
        await RunAsync(results, "mixed loose and archive providers remain explicitly uncertain", MixedProviderResolutionAsync);
        await RunAsync(results, "resolved service requires exact roots and publishes queryable cached evidence", ResolvedServiceAsync);
        return results.ToImmutable();
    }

    private static Task ContentAuthorizationAsync()
    {
        using var fixture = new ResolvedStateDirectoryFixture();
        var paths = new WindowsPathCanonicalizer();
        var service = new Mo2SessionContentPathAuthorization(paths);
        var first = new InstallationReferenceId("reference.mo2.content-one");
        var second = new InstallationReferenceId("reference.mo2.content-two");

        var authorization = service.AuthorizeRoot(first, Mo2ContentRootKind.GameData, fixture.Root);
        True(paths.Equals(fixture.Root, authorization.CanonicalRoot));
        True(service.IsRootAuthorized(first, Mo2ContentRootKind.GameData, fixture.Root));
        False(service.IsRootAuthorized(first, Mo2ContentRootKind.GameDirectory, fixture.Root));
        False(service.IsRootAuthorized(first, Mo2ContentRootKind.Overwrite, fixture.Root));
        False(service.IsRootAuthorized(second, Mo2ContentRootKind.GameData, fixture.Root));

        var sibling = Path.Combine(Path.GetDirectoryName(fixture.Root)!, $"{Path.GetFileName(fixture.Root)}-sibling");
        Directory.CreateDirectory(sibling);
        try
        {
            False(service.IsRootAuthorized(first, Mo2ContentRootKind.GameData, sibling));
        }
        finally
        {
            Directory.Delete(sibling);
        }

        service.Revoke(first, Mo2ContentRootKind.GameData);
        False(service.IsRootAuthorized(first, Mo2ContentRootKind.GameData, fixture.Root));
        return Task.CompletedTask;
    }

    private static async Task ContentTreeAsync()
    {
        using var fixture = new ResolvedStateDirectoryFixture();
        fixture.WriteFile("meshes\\actors\\hero.nif", [1, 2, 3]);
        fixture.WriteFile("textures\\actors\\hero.dds", [4, 5]);
        fixture.WriteFile("Root.esp", Mo2ResolvedStateFixtureBytes.Plugin(0x200, "Skyrim.esm"));
        var observer = new Mo2ContentTreeObserver(new WindowsPathCanonicalizer());
        var request = new Mo2ContentTreeRequest(
            new("reference.mo2.tree"),
            Mo2ContentRootKind.GameData,
            fixture.Root,
            new(MaximumEntries: 32));

        var first = await observer.ObserveAsync(request);
        var second = await observer.ObserveAsync(request);
        Equal(Mo2PathState.Present, first.State);
        False(first.IsPartial);
        Equal(first.MembershipFingerprint, second.MembershipFingerprint);
        Contains(first.Entries, entry =>
            entry.Kind == Mo2ContentEntryKind.File &&
            entry.VirtualPath == "meshes\\actors\\hero.nif" &&
            entry.Length == 3);
        True(first.Entries.All(entry => !Path.IsPathFullyQualified(entry.VirtualPath)));

        fixture.WriteFile("meshes\\actors\\added.nif", [9]);
        var changed = await observer.ObserveAsync(request);
        False(changed.MembershipFingerprint == first.MembershipFingerprint);

        var bounded = await observer.ObserveAsync(request with
        {
            Limits = new Mo2ContentObservationLimits(MaximumEntries: 2),
        });
        True(bounded.IsPartial);
        Equal(2, bounded.Entries.Length);
        Contains(bounded.Issues, issue => issue.Code == "mo2.content.entry_limit");
    }

    private static async Task ContentTreeCancellationAsync()
    {
        using var fixture = new ResolvedStateDirectoryFixture();
        fixture.WriteFile("file.txt", [1]);
        using var source = new CancellationTokenSource();
        source.Cancel();
        await ThrowsAsync<OperationCanceledException>(() =>
            new Mo2ContentTreeObserver(new WindowsPathCanonicalizer()).ObserveAsync(
                new(
                    new("reference.mo2.tree-cancel"),
                    Mo2ContentRootKind.GameData,
                    fixture.Root,
                    new()),
                source.Token));
    }

    private static async Task PluginHeaderAsync()
    {
        var parser = new Mo2PluginHeaderParser();
        await using var plugin = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Plugin(0x201, "Skyrim.esm", "Update.esm"),
            "C:\\fixture\\LightMaster.esp");
        var result = await parser.ParseAsync(plugin, "LightMaster.esp", new());
        Equal(Mo2BinaryObservationStatus.Complete, result.Status);
        Equal(Mo2PluginExtensionKind.Esp, result.Extension);
        Equal(true, result.HasMasterFlag);
        Equal(true, result.HasLightFlag);
        True(result.Masters.Select(master => master.Name).SequenceEqual(["Skyrim.esm", "Update.esm"]));
        Equal(64, result.ContentFingerprint!.Length);

        await using var malformed = new MemoryRandomAccessFile("NOT"u8.ToArray(), "C:\\fixture\\Bad.esp");
        var malformedResult = await parser.ParseAsync(malformed, "Bad.esp", new());
        Equal(Mo2BinaryObservationStatus.Malformed, malformedResult.Status);
        Contains(malformedResult.Warnings, warning => warning.Code == "mo2.plugin.header_truncated");

        await using var unsupported = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Plugin(0, "Skyrim.esm"),
            "C:\\fixture\\Native.dll");
        var unsupportedResult = await parser.ParseAsync(unsupported, "Native.dll", new());
        Equal(Mo2BinaryObservationStatus.Unsupported, unsupportedResult.Status);
    }

    private static async Task BsaIndexAsync()
    {
        var parser = new Mo2BsaIndexParser();
        await using var archive = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Bsa(3),
            "C:\\fixture\\Assets.bsa");
        var result = await parser.ParseAsync(archive, new());
        Equal(Mo2BinaryObservationStatus.Complete, result.Status);
        Equal(Mo2ArchiveFormat.Bsa, result.Format);
        Equal(Mo2ArchiveSupport.SupportedIndex, result.Support);
        True(result.IndexBytesRead > 0);
        Equal(3, result.Members.Length);
        Equal("textures\\fixture-000000.dds", result.Members[0].VirtualPath);
        True(result.Members.Select(member => member.SourceOrder).SequenceEqual([0, 1, 2]));
        Equal(64, result.ContentFingerprint!.Length);

        await using var ba2 = new MemoryRandomAccessFile(
            Encoding.ASCII.GetBytes("BTDXfixture"),
            "C:\\fixture\\Unsupported.ba2");
        var unsupported = await parser.ParseAsync(ba2, new());
        Equal(Mo2ArchiveFormat.Ba2, unsupported.Format);
        Equal(Mo2ArchiveSupport.UnsupportedFormat, unsupported.Support);
        Equal(Mo2BinaryObservationStatus.Unsupported, unsupported.Status);

        await using var oversized = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Bsa(3),
            "C:\\fixture\\Bounded.bsa");
        var bounded = await parser.ParseAsync(oversized, new(MaximumMembers: 2));
        Equal(Mo2BinaryObservationStatus.Oversized, bounded.Status);
        Contains(bounded.Warnings, warning => warning.Code == "mo2.archive.member_limit");
    }

    private static async Task BinaryCancellationAsync()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await using var plugin = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Plugin(0, "Skyrim.esm"),
            "C:\\fixture\\Canceled.esp");
        await ThrowsAsync<OperationCanceledException>(() =>
            new Mo2PluginHeaderParser().ParseAsync(plugin, "Canceled.esp", new(), source.Token));

        await using var archive = new MemoryRandomAccessFile(
            Mo2ResolvedStateFixtureBytes.Bsa(2),
            "C:\\fixture\\Canceled.bsa");
        await ThrowsAsync<OperationCanceledException>(() =>
            new Mo2BsaIndexParser().ParseAsync(archive, new(), source.Token));
    }

    private static Task LooseProviderResolutionAsync()
    {
        var snapshot = new ResolvedSnapshotId("resolved.fixture-loose");
        var inputs = new[]
        {
            Loose(VirtualProviderKind.BaseGameLooseFile, "Base Data", 0, "textures\\shared.dds"),
            Loose(VirtualProviderKind.ModLooseFile, "Lower mod", 10, "textures\\shared.dds"),
            Loose(VirtualProviderKind.ModLooseFile, "Higher mod", 20, "textures\\shared.dds"),
            Loose(VirtualProviderKind.OverwriteLooseFile, "MO2 overwrite", int.MaxValue, "textures\\shared.dds"),
        };
        var result = new Mo2VirtualDataResolver().Resolve(snapshot, inputs, [], Mo2ResolvedStateLimits.Default);
        var entry = result.Entries.Single();
        Equal(ProviderWinnerConfidence.Established, entry.WinnerConfidence);
        Equal("MO2 overwrite", entry.WinningProviderName);
        var chain = result.Chains[entry.Id];
        True(chain.Providers.Select(value => value.SourceName).SequenceEqual(["Base Data", "Lower mod", "Higher mod", "MO2 overwrite"]));
        True(chain.Providers.Single(value => value.IsWinner).Kind == VirtualProviderKind.OverwriteLooseFile);
        return Task.CompletedTask;
    }

    private static Task MixedProviderResolutionAsync()
    {
        var snapshot = new ResolvedSnapshotId("resolved.fixture-mixed");
        var archive = new ResolvedArchiveEntry(
            new("archive.fixture"), "Fixture.bsa", ArchiveFormat.Bsa, 105,
            ArchiveSupportStatus.Supported, ArchiveActivationState.Active,
            ArchiveActivationProvenance.IniResourceList, null, "Fixture mod", 1,
            new string('a', 64), DateTimeOffset.UnixEpoch, []);
        var result = new Mo2VirtualDataResolver().Resolve(
            snapshot,
            [Loose(VirtualProviderKind.ModLooseFile, "Fixture mod", 1, "meshes\\mixed.nif")],
            [new(archive, [new("meshes\\mixed.nif", 0, 1, 1, 1)], 1)],
            Mo2ResolvedStateLimits.Default);
        var entry = result.Entries.Single();
        Equal(ProviderWinnerConfidence.Uncertain, entry.WinnerConfidence);
        Equal(null, entry.WinningProviderId);
        True(result.Chains[entry.Id].Providers.All(value => !value.IsWinner));
        Contains(result.Discrepancies, value => value.Kind == EnvironmentDiscrepancyKind.ResolutionUncertain);
        return Task.CompletedTask;
    }

    private static async Task ResolvedServiceAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profilePath = fixture.CreateProfile(
            "Resolved",
            modList: "+Fixture Mod\n",
            plugins: "*Skyrim.esm\n*Fixture.esp\n",
            loadOrder: "Skyrim.esm\nccFixture.esl\nFixture.esp\n",
            settings: "[General]\nLocalSaves=false\nLocalSettings=true\n");
        fixture.Authorize();
        var modRoot = Path.Combine(fixture.InstanceRoot, "mods", "Fixture Mod");
        Directory.CreateDirectory(Path.Combine(modRoot, "textures"));
        await File.WriteAllBytesAsync(Path.Combine(modRoot, "Fixture.esp"), Mo2ResolvedStateFixtureBytes.Plugin(0x200, "CCFIXTURE.ESL"));
        await File.WriteAllBytesAsync(Path.Combine(modRoot, "Fixture.bsa"), Mo2ResolvedStateFixtureBytes.Bsa(1));
        await File.WriteAllTextAsync(Path.Combine(modRoot, "textures", "fixture-000000.dds"), "loose");
        await File.WriteAllBytesAsync(Path.Combine(fixture.GameRoot, "Data", "Skyrim.esm"), Mo2ResolvedStateFixtureBytes.Plugin(1));
        await File.WriteAllBytesAsync(Path.Combine(fixture.GameRoot, "Data", "ccFixture.esl"), Mo2ResolvedStateFixtureBytes.Plugin(0x200, "sKyRiM.EsM"));
        await File.WriteAllTextAsync(Path.Combine(fixture.GameRoot, "Skyrim.ccc"), "ccFixture.esl\n");
        var modsAuthorization = new Mo2SessionModsPathAuthorization(fixture.Paths);
        modsAuthorization.AuthorizeModsRoot(fixture.Reference.Id, fixture.Validation.ModsDirectory!);
        var profileService = fixture.CreateSnapshotService();
        var inventoryService = new Mo2ModInventoryService(
            fixture.FileSystem,
            new Mo2TextDecoder(),
            fixture.Paths,
            modsAuthorization);
        var contentAuthorization = new Mo2SessionContentPathAuthorization(fixture.Paths);
        var cache = new Mo2ResolvedStateCache();
        var service = new Mo2ResolvedStateService(
            new ProfileReferenceStore(fixture.Reference),
            _ => new ProfileStaticValidator(fixture.Validation),
            profileService,
            inventoryService,
            new Mo2ContentTreeObserver(fixture.Paths),
            new WindowsRandomAccessFileFactory(),
            new Mo2PluginHeaderParser(),
            new Mo2BsaIndexParser(),
            contentAuthorization,
            fixture.Paths,
            fixture.FileSystem,
            new Mo2TextDecoder(),
            cache,
            new Mo2VirtualDataResolver(),
            Path.Combine(profilePath, "unused-global-settings"));
        var profile = (await profileService.ObserveAsync(fixture.Request())).Profiles.Single();
        var context = new WorkspaceEnvironmentContext(
            fixture.Reference.GameId,
            fixture.Reference.InstallationId,
            profile.Id,
            "catalog.fixture",
            null);

        var unauthorized = await service.RefreshAsync(context, forceRefresh: true);
        Equal(ResolvedEnvironmentRefreshStatus.AuthorizationRequired, unauthorized.Status);
        Contains(unauthorized.RequiredAuthorizations, value => value == "Game Data");
        Contains(unauthorized.RequiredAuthorizations, value => value == "Game directory");
        False(await service.AuthorizeExactRootAsync(context, Mo2ContentRootKind.GameData, fixture.GameRoot));
        False(contentAuthorization.IsRootAuthorized(fixture.Reference.Id, Mo2ContentRootKind.GameData, fixture.GameRoot));
        True(await service.AuthorizeExactRootAsync(
            context,
            Mo2ContentRootKind.GameDirectory,
            fixture.GameRoot));
        True(await service.AuthorizeExactRootAsync(
            context,
            Mo2ContentRootKind.GameData,
            Path.Combine(fixture.GameRoot, "Data")));

        var refreshed = await service.RefreshAsync(context, forceRefresh: true);
        Equal(ResolvedEnvironmentRefreshStatus.Completed, refreshed.Status);
        True(refreshed.Snapshot is not null);
        var plugins = await service.QueryPluginsAsync(refreshed.Snapshot!.Id, PluginQuery.Default);
        Contains(plugins.Items, value => value.Name == "Fixture.esp" && value.IsEnabled && value.Observation?.HasLightFlag == true);
        var creation = plugins.Items.Single(value => value.Name == "ccFixture.esl");
        True(creation.IsEnabled);
        Equal(PluginActivationProvenance.CreationManifestImplicit, creation.Observation!.ActivationProvenance);
        var fixturePlugin = plugins.Items.Single(value => value.Name == "Fixture.esp");
        var creationMaster = fixturePlugin.Observation!.Masters.Single();
        Equal(PluginMasterStatus.PresentEnabled, creationMaster.Status);
        Equal(creation.Id, creationMaster.ResolvedPluginId);
        var skyrim = plugins.Items.Single(value => value.Name == "Skyrim.esm");
        Equal(skyrim.Id, creation.Observation.Masters.Single().ResolvedPluginId);
        Equal(3, plugins.Items.Count(value => value.IsEnabled));
        var archives = await service.QueryArchivesAsync(refreshed.Snapshot.Id, ArchiveQuery.Default);
        Contains(archives.Items, value => value.Name == "Fixture.bsa" && value.Activation == ArchiveActivationState.Active);
        var data = await service.QueryDataAsync(refreshed.Snapshot.Id, VirtualDataQuery.Default);
        var mixed = data.Items.Single(value => value.VirtualPath == "textures\\fixture-000000.dds");
        Equal(ProviderWinnerConfidence.Uncertain, mixed.WinnerConfidence);
        True(await service.GetProviderChainAsync(refreshed.Snapshot.Id, mixed.Id) is { Providers.Length: 2 });
        var cached = await service.RefreshAsync(context, forceRefresh: false);
        True(cached.Snapshot?.IsFromSessionCache == true);

        await File.WriteAllTextAsync(Path.Combine(fixture.GameRoot, "Skyrim.ccc"), "..\\unsafe.esl\n");
        var malformed = await service.RefreshAsync(context, forceRefresh: true);
        Equal(ResolvedEnvironmentRefreshStatus.Partial, malformed.Status);
        True(malformed.Snapshot is not null);
        Contains(malformed.Snapshot!.Discrepancies, value => value.Kind == EnvironmentDiscrepancyKind.MalformedInput);
        var malformedPlugins = await service.QueryPluginsAsync(malformed.Snapshot.Id, PluginQuery.Default);
        Contains(malformedPlugins.Items, value => value.Name == "ccFixture.esl" && !value.IsEnabled);
        Contains(malformedPlugins.Items, value => value.Name == "Fixture.esp" &&
            value.Observation!.Masters.Single().Status == PluginMasterStatus.PresentDisabled);
    }

    private static Mo2LooseProviderInput Loose(
        VirtualProviderKind kind,
        string name,
        int precedence,
        params string[] paths)
    {
        var entries = paths.Select((path, index) => new Mo2ContentTreeEntry(
            Mo2ContentEntryKind.File, path, $"C:\\fixture\\{index}", $"identity-{name}-{index}",
            path.Count(character => character == '\\') + 1, 1, index, FileAttributes.Normal)).ToImmutableArray();
        return new(kind, name, kind == VirtualProviderKind.ModLooseFile ? new ModId($"mod.{name.Replace(' ', '-')}") : null,
            precedence, new(Mo2ContentRootKind.Mod, "C:\\fixture", Mo2PathState.Present, entries, $"tree-{name}", false, []));
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ResolvedStateCheckResult>.Builder results,
        string name,
        Func<Task> check)
    {
        try
        {
            await check();
            results.Add(new(name, null));
        }
        catch (Exception exception)
        {
            results.Add(new(name, exception));
        }
    }

    private static void True(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool condition) => True(!condition);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate)) throw new InvalidOperationException("Expected matching item.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}

sealed class ResolvedStateDirectoryFixture : IDisposable
{
    public ResolvedStateDirectoryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-resolved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void WriteFile(string relativePath, ReadOnlySpan<byte> contents)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, contents.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

sealed class MemoryRandomAccessFile : IMo2RandomAccessFile
{
    private readonly byte[] bytes;
    private bool disposed;

    public MemoryRandomAccessFile(byte[] bytes, string path)
    {
        this.bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        InitialStamp = new(
            new(0x47524944, 1, bytes.LongLength, 638900000000000000),
            path);
    }

    public long Length => bytes.LongLength;

    public Mo2RandomAccessStamp InitialStamp { get; }

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0 || offset > bytes.LongLength || buffer.Length > bytes.LongLength - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        var count = Math.Min(buffer.Length, checked(bytes.Length - (int)offset));
        bytes.AsMemory(checked((int)offset), count).CopyTo(buffer);
        return ValueTask.FromResult(count);
    }

    public ValueTask<Mo2RandomAccessStamp> GetCurrentStampAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(InitialStamp);
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }
}

static class Mo2ResolvedStateFixtureBytes
{
    public static byte[] Plugin(uint flags, params string[] masters)
    {
        using var data = new MemoryStream();
        Span<byte> size = stackalloc byte[2];
        foreach (var master in masters)
        {
            var name = Encoding.Latin1.GetBytes(master + "\0");
            data.Write("MAST"u8);
            BinaryPrimitives.WriteUInt16LittleEndian(size, checked((ushort)name.Length));
            data.Write(size);
            data.Write(name);
        }

        var payload = data.ToArray();
        var result = new byte[24 + payload.Length];
        "TES4"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), checked((uint)payload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), flags);
        payload.CopyTo(result, 24);
        return result;
    }

    public static byte[] Bsa(int memberCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberCount);
        const int headerSize = 36;
        const int folderRecordSize = 24;
        const int fileRecordSize = 16;
        var folder = Encoding.Latin1.GetBytes("textures\0");
        var names = Enumerable.Range(0, memberCount)
            .Select(index => Encoding.Latin1.GetBytes($"fixture-{index:D6}.dds\0"))
            .ToArray();
        var folderNameBytes = folder.Length;
        var fileNameBytes = names.Sum(name => name.Length);
        var total = checked(headerSize + folderRecordSize + 1 + folderNameBytes + memberCount * fileRecordSize + fileNameBytes);
        var result = new byte[total];
        "BSA\0"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 105);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), 0x3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), checked((uint)memberCount));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), checked((uint)folderNameBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), checked((uint)fileNameBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), 0x2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(headerSize + 8, 4), checked((uint)memberCount));

        var position = headerSize + folderRecordSize;
        result[position++] = checked((byte)folder.Length);
        folder.CopyTo(result, position);
        position += folder.Length;
        for (var index = 0; index < memberCount; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(position, 8), checked((ulong)index + 1));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position + 8, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(position + 12, 4), checked((uint)(total + index * 32L)));
            position += fileRecordSize;
        }

        foreach (var name in names)
        {
            name.CopyTo(result, position);
            position += name.Length;
        }

        return result;
    }
}
