using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ProfileCheckResult(string Name, Exception? Failure);

static class Mo2ProfileChecks
{
    public static async Task<ImmutableArray<Mo2ProfileCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ProfileCheckResult>();
        await RunAsync(results, "text decoding preserves bytes and byte line offsets", TextDecodingAsync);
        await RunAsync(results, "mod-list semantics preserve duplicates and reverse Core priority", ModListAsync);
        await RunAsync(results, "plugin active rows join load order safely", PluginsAsync);
        await RunAsync(results, "profile settings accept only explicit MO2 boolean keys", SettingsAsync);
        await RunAsync(results, "session authorization prevents profile-root reads until exact grant", AuthorizationAsync);
        await RunAsync(results, "complete snapshot preserves raw sources and active manager state", CompleteSnapshotAsync);
        await RunAsync(results, "required missing and optional absent remain distinct", MissingSourcesAsync);
        await RunAsync(results, "strict UTF-8 failure preserves raw source evidence", StrictEncodingAsync);
        await RunAsync(results, "profile collections have no adapter count cap", LargeProfileCollectionAsync);
        await RunAsync(results, "inaccessible profile enumeration is unavailable rather than empty", InaccessibleEnumerationAsync);
        await RunAsync(results, "local settings and saves observations stay bounded", LocalFeatureSourcesAsync);
        await RunAsync(results, "oversized source is bounded and partial", OversizedAsync);
        await RunAsync(results, "source changes during read produce inconsistent snapshot without retry", ChangedDuringReadAsync);
        await RunAsync(results, "connected catalog projects observed profiles and capability boundary", CatalogProjectionAsync);
        await RunAsync(results, "profile observation honors cancellation", CancellationAsync);
        return results.ToImmutable();
    }

    private static Task TextDecodingAsync()
    {
        var raw = Encoding.UTF8.GetBytes("one\r\ntwo\nthree\rfour").ToImmutableArray();
        var document = new Mo2TextDecoder().Decode(raw);
        Equal(raw, document.Bytes);
        Equal(4, document.Lines.Length);
        Equal((0, 3, 2), (document.Lines[0].ByteOffset, document.Lines[0].ByteLength, document.Lines[0].TerminatorByteLength));
        Equal(Mo2LineTerminator.CarriageReturnLineFeed, document.Lines[0].Terminator);
        Equal((5, 3, 1), (document.Lines[1].ByteOffset, document.Lines[1].ByteLength, document.Lines[1].TerminatorByteLength));
        Equal(Mo2LineTerminator.LineFeed, document.Lines[1].Terminator);
        Equal(Mo2LineTerminator.CarriageReturn, document.Lines[2].Terminator);
        Equal("four", document.Lines[3].Text);

        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("A\r\nB")).ToImmutableArray();
        var utf16Document = new Mo2TextDecoder().Decode(utf16);
        Equal(Mo2TextEncodingKind.Utf16LittleEndian, utf16Document.Encoding);
        Equal(2, utf16Document.ByteOrderMarkLength);
        Equal((2, 2, 4), (utf16Document.Lines[0].ByteOffset, utf16Document.Lines[0].ByteLength, utf16Document.Lines[0].TerminatorByteLength));
        return Task.CompletedTask;
    }

    private static Task ModListAsync()
    {
        var document = new Mo2TextDecoder().Decode(Encoding.UTF8.GetBytes(
            "+Same\n-Same\n*Foreign\nUnmarked\n+Section_separator\n+section_SEPARATOR").ToImmutableArray());
        var parsed = Mo2ProfileParsers.ParseModList(document);
        Equal(6, parsed.Entries.Length);
        True(parsed.Entries[0].IsEnabled);
        False(parsed.Entries[1].IsEnabled);
        Equal(Mo2ModListMarker.Foreign, parsed.Entries[2].Marker);
        Contains(parsed.Warnings, warning => warning.Code == "mo2.modlist.unmarked_enabled");
        var mods = Mo2ProfileParsers.ProjectMods(new("profile.test"), parsed);
        Equal("section_SEPARATOR", mods[0].Name);
        Equal(ModEntryKind.Mod, mods[0].Kind);
        Equal(ModEntryKind.Separator, mods[1].Kind);
        Equal(0, mods[0].Priority);
        Equal(5, mods[^1].Priority);
        False(mods[^1].Id == mods[^2].Id);
        return Task.CompletedTask;
    }

    private static Task PluginsAsync()
    {
        var decoder = new Mo2TextDecoder();
        var states = Mo2ProfileParsers.ParsePluginStates(decoder.Decode(
            Encoding.UTF8.GetBytes("Skyrim.esm\nUpdate.esm\n*Literal.esp\nDuplicate.esp\nDuplicate.esp").ToImmutableArray()));
        var order = Mo2ProfileParsers.ParseLoadOrder(decoder.Decode(
            Encoding.UTF8.GetBytes("update.ESM\nSkyrim.esm\nccBGSSSE001-Fish.esm\nMissing.esp\nLiteral.esp\nDuplicate.esp").ToImmutableArray()));
        var (plugins, warnings) = Mo2ProfileParsers.ProjectPlugins(new("profile.plugins"), states, order);
        Equal(4, plugins.Length);
        False(plugins[0].IsEnabled);
        False(plugins[1].IsEnabled);
        True(plugins[2].IsEnabled);
        Equal("ccBGSSSE001-Fish.esm", plugins[2].Name);
        True(plugins[3].IsEnabled);
        Equal("Literal.esp", plugins[3].Name);
        Equal(0, plugins[0].LoadOrder);
        Equal(0, plugins[0].SourcePriority);
        Equal(2, plugins[2].SourcePriority);
        Equal(4, plugins[3].SourcePriority);
        Contains(warnings, warning => warning.Code == "mo2.loadorder.plugin_unmatched");
        Contains(warnings, warning => warning.Code == "mo2.loadorder.plugin_ambiguous");
        return Task.CompletedTask;
    }

    private static Task SettingsAsync()
    {
        var document = new Mo2TextDecoder().Decode(Encoding.UTF8.GetBytes(
            "LocalSaves=true\nLocalSettings=FALSE\nlocal_saves=true\nLocalSettings=1").ToImmutableArray());
        var settings = Mo2ProfileParsers.ParseProfileSettings(document);
        Equal(true, settings.LocalSavesEnabled);
        Equal(false, settings.LocalSettingsEnabled);
        Equal(ProfileSourceParseStatus.UnsupportedSyntax, settings.Status);
        return Task.CompletedTask;
    }

    private static async Task AuthorizationAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var internalProfile = fixture.CreateProfile("Default");
        var externalRoot = Path.Combine(fixture.Root, "external-profiles");
        Directory.CreateDirectory(externalRoot);
        Directory.Move(internalProfile, Path.Combine(externalRoot, "Default"));
        var tracking = new ProfileTrackingFileSystem(fixture.FileSystem);
        var service = fixture.CreateSnapshotService(tracking);
        var externalRequest = new Mo2ProfileSnapshotRequest(
            fixture.Reference,
            fixture.Validation with { ProfilesDirectory = externalRoot });
        var unauthorized = await service.ObserveAsync(externalRequest);
        Equal(ProfileObservationStatus.Unavailable, unauthorized.Status);
        Equal(0, tracking.ReadBoundaryCalls);
        Equal(ProfileSourceAvailability.AuthorizationRequired, unauthorized.ProfilesRootSource!.Availability);
        Contains(unauthorized.Issues, issue => issue.Code == "mo2.profiles.authorization_required");

        fixture.Authorization.AuthorizeProfilesRoot(fixture.Reference.Id, externalRoot);
        var authorized = await service.ObserveAsync(externalRequest);
        Equal(ProfileObservationStatus.Complete, authorized.Status);
        True(tracking.ReadBoundaryCalls > 0);

        var changedRoot = Path.Combine(fixture.Root, "other-profiles");
        Directory.CreateDirectory(changedRoot);
        tracking.Reset();
        var changed = await service.ObserveAsync(new(
            fixture.Reference,
            fixture.Validation with { ProfilesDirectory = changedRoot }));
        Equal(ProfileObservationStatus.Unavailable, changed.Status);
        Equal(0, tracking.ReadBoundaryCalls);
    }

    private static async Task CompleteSnapshotAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Zulu");
        fixture.CreateProfile("alpha");
        fixture.SelectProfile("ALPHA");
        var observedAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        var snapshot = await fixture.CreateSnapshotService(utcNow: () => observedAt).ObserveAsync(fixture.Request());
        Equal(ProfileObservationStatus.Complete, snapshot.Status);
        Equal(2, snapshot.Profiles.Length);
        var active = snapshot.Profiles.Single(profile => profile.ManagerState == ManagerProfileState.Active);
        Equal("alpha", active.Name);
        Equal(observedAt, active.Observation.ObservedAtUtc);
        True(active.Observation.LocalSavesEnabled == true);
        True(active.Observation.LocalSettingsEnabled == false);
        Equal(8, active.Sources.Length);
        True(active.Sources.Where(source => source.Availability == ProfileSourceAvailability.Read).All(source =>
            source.Name == "saves-directory" || source.RawDocument is not null));
        True(active.Sources.Where(source => source.RawDocument is not null).All(source =>
            source.CanonicalSourcePath is not null &&
            source.ObservedAtUtc == observedAt &&
            source.ParserVersion == "grid.mo2.profile.v1" &&
            source.RawFingerprint?.Length == 64));
        True(snapshot.InstanceConfigurationSource is
        {
            ParseStatus: ProfileSourceParseStatus.Parsed,
            RawDocument: not null,
            RawFingerprint.Length: 64,
        });
        True(active.Mods.Select(mod => mod.Id).Distinct().Count() == active.Mods.Length);
        Equal(3, active.Plugins.Length);
    }

    private static async Task StrictEncodingAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profile = fixture.CreateProfile("Encoding");
        await File.WriteAllBytesAsync(Path.Combine(profile, "modlist.txt"), [0xFF, 0xFE, 0xFD]);
        var snapshot = await fixture.CreateSnapshotService().ObserveAsync(fixture.Request());
        var source = snapshot.Profiles.Single().Sources.Single(item => item.Name == "modlist.txt");
        Equal(ProfileSourceParseStatus.Malformed, source.ParseStatus);
        Equal(3, source.RawBytes.Length);
        Equal(null, source.RawDocument);
        Equal(ProfileObservationStatus.Partial, snapshot.Status);
    }

    private static async Task LargeProfileCollectionAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        for (var index = 0; index < 129; index++)
        {
            var profile = Path.Combine(fixture.ProfilesRoot, $"Profile-{index:D3}");
            Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "modlist.txt"), $"+Mod {index}");
        }

        var snapshot = await fixture.CreateSnapshotService().ObserveAsync(fixture.Request());
        Equal(129, snapshot.Profiles.Length);
        Equal(ProfileObservationStatus.Complete, snapshot.Status);
    }

    private static async Task InaccessibleEnumerationAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Hidden");
        var inaccessible = new InaccessibleEnumerationFileSystem(fixture.FileSystem, fixture.ProfilesRoot, fixture.Paths);
        var snapshot = await fixture.CreateSnapshotService(inaccessible).ObserveAsync(fixture.Request());
        Equal(ProfileObservationStatus.Unavailable, snapshot.Status);
        Equal(0, snapshot.Profiles.Length);
        Contains(snapshot.Issues, issue => issue.Code == "mo2.profiles.root_unavailable");
    }

    private static async Task LocalFeatureSourcesAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profile = fixture.CreateProfile(
            "LocalFeatures",
            settings: "LocalSaves=true\nLocalSettings=true\n",
            includeSkyrimInis: false);
        Directory.CreateDirectory(Path.Combine(profile, "saves"));
        File.WriteAllText(Path.Combine(profile, "saves", "save.ess"), "must not be enumerated");
        var tracking = new ProfileTrackingFileSystem(fixture.FileSystem);
        var snapshot = await fixture.CreateSnapshotService(tracking).ObserveAsync(fixture.Request());
        var observed = snapshot.Profiles.Single();
        Equal(ProfileObservationStatus.Partial, observed.Observation.Status);
        Equal(ProfileSourceAvailability.Read, observed.Observation.Sources.Single(source => source.Name == "saves-directory").Availability);
        False(tracking.EnumeratedDirectoryPaths.Any(path => fixture.Paths.Equals(path, Path.Combine(profile, "saves"))));
        Contains(observed.Sources.SelectMany(source => source.Warnings), warning =>
            warning.Code == "mo2.profile.local_settings_source_missing");
    }

    private static async Task MissingSourcesAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profile = fixture.CreateProfile("Missing", settings: null, includeSkyrimInis: false);
        File.Delete(Path.Combine(profile, "modlist.txt"));
        fixture.Authorize();
        var snapshot = await fixture.CreateSnapshotService().ObserveAsync(fixture.Request());
        var observed = snapshot.Profiles.Single();
        Equal(ProfileObservationStatus.Partial, observed.Observation.Status);
        Equal(ProfileSourceAvailability.RequiredMissing, observed.Observation.Sources.Single(source => source.Name == "modlist.txt").Availability);
        Equal(ProfileSourceAvailability.OptionalAbsent, observed.Observation.Sources.Single(source => source.Name == "settings.ini").Availability);
        Equal(null, observed.Observation.LocalSavesEnabled);
    }

    private static async Task OversizedAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profile = fixture.CreateProfile("Oversized");
        await using (var stream = new FileStream(Path.Combine(profile, "modlist.txt"), FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(Mo2ProfileSnapshotService.MaximumFileBytes + 1L);
        }

        fixture.Authorize();
        var snapshot = await fixture.CreateSnapshotService().ObserveAsync(fixture.Request());
        Equal(ProfileSourceAvailability.Oversized, snapshot.Profiles.Single().Observation.Sources.Single(source => source.Name == "modlist.txt").Availability);
        Equal(ProfileObservationStatus.Partial, snapshot.Status);
    }

    private static async Task ChangedDuringReadAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        var profile = fixture.CreateProfile("Changing");
        var target = Path.Combine(profile, "modlist.txt");
        var changing = new MutatingReadFileSystem(fixture.FileSystem, target, fixture.Paths);
        fixture.Authorize();
        var snapshot = await fixture.CreateSnapshotService(changing).ObserveAsync(fixture.Request());
        Equal(1, changing.TargetReadCount);
        Equal(ProfileObservationStatus.Inconsistent, snapshot.Status);
        Equal(ProfileSourceAvailability.ChangedDuringRead, snapshot.Profiles.Single().Observation.Sources.Single(source => source.Name == "modlist.txt").Availability);
    }

    private static async Task CatalogProjectionAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Zulu");
        fixture.CreateProfile("Alpha");
        fixture.SelectProfile("Zulu");
        fixture.Authorize();
        var snapshotService = fixture.CreateSnapshotService();
        var catalog = new Mo2CatalogService(
            new MockGridCatalogService(),
            new ProfileReferenceStore(fixture.Reference),
            _ => new ProfileStaticValidator(fixture.Validation),
            snapshotService);
        var result = await catalog.GetCatalogAsync();
        var installation = result.Games.Single(game => game.Id == fixture.Reference.GameId)
            .Installations.Single(item => item.Id == fixture.Reference.InstallationId);
        Equal(WorkspaceFeature.Profiles | WorkspaceFeature.ModList | WorkspaceFeature.Health, installation.Capabilities.Features);
        Equal(
            EnvironmentTabCapability.Plugins |
            EnvironmentTabCapability.Archives |
            EnvironmentTabCapability.Data |
            EnvironmentTabCapability.Saves |
            EnvironmentTabCapability.Downloads,
            installation.Capabilities.EnvironmentTabs);
        False(installation.Capabilities.Supports(WorkspaceFeature.Tools));
        Equal("Zulu", installation.Profiles[0].Name);
        True(installation.Profiles.All(profile => profile.Observation is not null));
        True(installation.Profiles.All(profile => profile.Mods.IsEmpty));
        True(installation.Profiles.All(profile => profile.Plugins.Length == 3));
        True(installation.Profiles.All(profile => profile.Observation!.Inventory is null));
        True(installation.Metadata.StatusDetail.Contains("loaded", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CancellationAsync()
    {
        using var fixture = await Mo2ProfileFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Cancelled");
        fixture.Authorize();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await ThrowsAsync<OperationCanceledException>(() =>
            fixture.CreateSnapshotService().ObserveAsync(fixture.Request(), source.Token));
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ProfileCheckResult>.Builder results,
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

sealed class ProfileTrackingFileSystem(IMo2ReadOnlyFileSystem inner) : IMo2ReadOnlyFileSystem
{
    public int ReadBoundaryCalls { get; private set; }
    public List<string> EnumeratedDirectoryPaths { get; } = [];
    public void Reset() { ReadBoundaryCalls = 0; EnumeratedDirectoryPaths.Clear(); }
    public Mo2PathState ProbeFile(string path) { ReadBoundaryCalls++; return inner.ProbeFile(path); }
    public Mo2PathState ProbeDirectory(string path) { ReadBoundaryCalls++; return inner.ProbeDirectory(path); }
    public IReadOnlyList<string> EnumerateDirectories(string path) { ReadBoundaryCalls++; return inner.EnumerateDirectories(path); }
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) { ReadBoundaryCalls++; EnumeratedDirectoryPaths.Add(path); return inner.EnumerateDirectoriesWithState(path); }
    public IReadOnlyList<string> EnumerateFiles(string path) { ReadBoundaryCalls++; return inner.EnumerateFiles(path); }
    public Mo2FileMetadata GetFileMetadata(string path) { ReadBoundaryCalls++; return inner.GetFileMetadata(path); }
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) { ReadBoundaryCalls++; return inner.ReadBytesAsync(path, maximumBytes, cancellationToken); }
    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) { ReadBoundaryCalls++; return inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken); }
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) { ReadBoundaryCalls++; return inner.ReadTextAsync(path, maximumBytes, cancellationToken); }
}

sealed class InaccessibleEnumerationFileSystem(
    IMo2ReadOnlyFileSystem inner,
    string inaccessibleRoot,
    IMo2PathCanonicalizer paths) : IMo2ReadOnlyFileSystem
{
    public Mo2PathState ProbeFile(string path) => inner.ProbeFile(path);
    public Mo2PathState ProbeDirectory(string path) => inner.ProbeDirectory(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) =>
        paths.Equals(path, inaccessibleRoot) ? [] : inner.EnumerateDirectories(path);
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) =>
        paths.Equals(path, inaccessibleRoot)
            ? new(Mo2PathState.Inaccessible, [])
            : inner.EnumerateDirectoriesWithState(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
    public Mo2FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesAsync(path, maximumBytes, cancellationToken);
    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}

sealed class MutatingReadFileSystem(
    IMo2ReadOnlyFileSystem inner,
    string target,
    IMo2PathCanonicalizer paths) : IMo2ReadOnlyFileSystem
{
    public int TargetReadCount { get; private set; }
    public Mo2PathState ProbeFile(string path) => inner.ProbeFile(path);
    public Mo2PathState ProbeDirectory(string path) => inner.ProbeDirectory(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) => inner.EnumerateDirectoriesWithState(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
    public Mo2FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);
    public async Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var bytes = await inner.ReadBytesAsync(path, maximumBytes, cancellationToken);
        if (paths.Equals(path, target))
        {
            TargetReadCount++;
            await File.AppendAllTextAsync(target, "\n+Changed", cancellationToken);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(2));
        }

        return bytes;
    }
    public async Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var read = await inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);
        if (paths.Equals(path, target))
        {
            TargetReadCount++;
            await File.AppendAllTextAsync(target, "\n+Changed", cancellationToken);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(2));
        }

        return read;
    }
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) => inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}

sealed class ProfileStaticValidator(Mo2InstallationValidation validation) : IMo2InstallationValidator
{
    public Task<Mo2InstallationValidation> ValidateAsync(Mo2ValidationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(validation);
    }
}

sealed class ProfileReferenceStore(Mo2InstallationReference reference) : IMo2InstallationReferenceStore
{
    public Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Mo2ReferenceLoadResult([reference], []));
    }

    public Task SaveAsync(ImmutableArray<Mo2InstallationReference> references, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
