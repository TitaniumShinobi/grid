using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ModInventoryCheckResult(string Name, Exception? Failure);

static class Mo2ModInventoryChecks
{
    public static async Task<ImmutableArray<Mo2ModInventoryCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ModInventoryCheckResult>();
        await RunAsync(results, "inventory reconciles order and non-authoritative rows", ReconciliationAsync);
        await RunAsync(results, "metadata and categories preserve local evidence", MetadataAsync);
        await RunAsync(results, "mods-root authorization is exact and session only", AuthorizationAsync);
        await RunAsync(results, "catalog projects authoritative inventory without mock fallback", CatalogProjectionAsync);
        await RunAsync(results, "inventory reports ambiguous duplicate enumeration", AmbiguousEnumerationAsync);
        await RunAsync(results, "metadata bounds and concurrent changes are explicit", MetadataBoundsAndChangesAsync);
        await RunAsync(results, "large inventories have no adapter count limit", LargeInventoryAsync);
        await RunAsync(results, "unavailable inventory never exposes modlist placeholders", UnavailableCatalogAsync);
        await RunAsync(results, "inventory cancellation is honored", CancellationAsync);
        return results.ToImmutable();
    }

    private static async Task ReconciliationAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", string.Join('\n',
            "+Matched",
            "-Missing",
            "*Skyrim.esm",
            "+Section_separator",
            "+Upper_SEPARATOR",
            "+Generatedbackup12",
            "+Duplicate",
            "+Duplicate"));
        fixture.CreateMod("Matched");
        fixture.CreateMod("Section_separator");
        fixture.CreateMod("Upper_SEPARATOR");
        fixture.CreateMod("Generatedbackup12");
        fixture.CreateMod("CaseBackup12");
        fixture.CreateMod("Loosebackup2");
        fixture.CreateMod("Duplicate");
        fixture.CreateMod("Orphan");

        var snapshot = await fixture.ObserveAsync();
        var rows = snapshot.Profiles.Single().Entries;
        Equal(11, rows.Length);
        Equal("Duplicate", rows[0].Name);
        Equal(Mo2ModReconciliationState.Duplicate, rows[0].Reconciliation);
        Equal(null, rows[0].Mo2Priority);
        var firstDuplicate = rows.Single(row => row.Name == "Duplicate" && row.Reconciliation == Mo2ModReconciliationState.Matched);
        True(firstDuplicate.Mo2Priority is not null);
        Equal(Mo2ModReconciliationState.Foreign, rows.Single(row => row.Name == "Skyrim.esm").Reconciliation);
        True(rows.Single(row => row.Name == "Skyrim.esm").Mo2Priority is not null);
        Equal(Mo2ModReconciliationState.Missing, rows.Single(row => row.Name == "Missing").Reconciliation);
        Equal(null, rows.Single(row => row.Name == "Missing").Mo2Priority);
        Equal(Mo2ModReconciliationState.Separator, rows.Single(row => row.Name == "Section_separator").Reconciliation);
        Equal(Mo2ModReconciliationState.Matched, rows.Single(row => row.Name == "Upper_SEPARATOR").Reconciliation);
        Equal(Mo2ModReconciliationState.Backup, rows.Single(row => row.Name == "Generatedbackup12").Reconciliation);
        Equal(Mo2ModReconciliationState.Unlisted, rows.Single(row => row.Name == "CaseBackup12").Reconciliation);
        Equal(null, rows.Single(row => row.Name == "CaseBackup12").SourceOrder);
        Equal(Mo2ModReconciliationState.Backup, rows.Single(row => row.Name == "Loosebackup2").Reconciliation);
        Equal(null, rows.Single(row => row.Name == "Loosebackup2").SourceOrder);
        var unlisted = rows.Single(row => row.Name == "Orphan");
        Equal(Mo2ModReconciliationState.Unlisted, unlisted.Reconciliation);
        Equal(null, unlisted.Mo2Priority);
        True(rows.Where(row => row.Mo2Priority is not null)
            .Select(row => row.Mo2Priority!.Value)
            .SequenceEqual(Enumerable.Range(0, rows.Count(row => row.Mo2Priority is not null))));
    }

    private static async Task MetadataAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Unicode Æther");
        fixture.WriteCategories("1|Gameplay|100|0\n2|Visuals|200|0\n");
        fixture.CreateMod("Unicode Æther", """
            [General]
            version=1.2.0
            newestVersion=1.3.0
            ignoredVersion=
            category="1,2,"
            gameName=skyrimspecialedition
            modid=12345
            installationFile=archive.7z
            notes=Local evidence only
            mystery=value
            [installedFiles]
            modid=99999
            """);

        var snapshot = await fixture.ObserveAsync();
        var directory = snapshot.Directories.Single();
        var metadata = directory.Metadata.Metadata!;
        Equal("1.2.0", metadata.Version);
        Equal(12345L, metadata.NexusModId);
        True(metadata.CategoryIds.SequenceEqual([1, 2]));
        True(metadata.CategoryNames.SequenceEqual(["Gameplay", "Visuals"]));
        Equal(0, snapshot.Categories!.Categories.Single(category => category.Id == 1).ParentId);
        Contains(metadata.RawValues, value => value.Key == "mystery" && !value.IsSupported);
        Contains(metadata.RawValues, value => value.Section == "installedFiles" && value.Key == "modid" && !value.IsSupported);
        Equal(64, directory.Metadata.Source.RawFingerprint!.Length);

        var profileSnapshot = await fixture.ObserveProfilesAsync();
        var catalog = new Mo2CatalogService(
            new MockGridCatalogService(),
            new ProfileReferenceStore(fixture.Reference),
            _ => new ProfileStaticValidator(fixture.Validation),
            new Mo2ProfileSnapshotService(
                fixture.FileSystem,
                new Grid.Mo2.Infrastructure.Mo2TextDecoder(),
                fixture.Paths,
                new Mo2SessionPathAuthorization(fixture.Paths)),
            fixture.CreateService());
        _ = profileSnapshot;
        var result = await catalog.GetCatalogAsync();
        var mod = result.Games.SelectMany(game => game.Installations)
            .Single(installation => installation.Id == fixture.Reference.InstallationId)
            .Profiles.Single().Mods.Single();
        Equal(ModUpdateState.UpdateAvailable, mod.UpdateState);
        False(mod.Inventory!.UpdateEvidence.NetworkChecked);
    }

    private static async Task AuthorizationAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+External");
        var external = Path.Combine(fixture.Root, "external-mods");
        Directory.CreateDirectory(Path.Combine(external, "External"));
        var validation = fixture.Validation with { ModsDirectory = external };
        var unauthorized = await fixture.ObserveAsync(validation);
        Equal(Mo2InventoryObservationStatus.Unavailable, unauthorized.Status);
        Contains(unauthorized.Issues, issue => issue.Code == "mo2.mods.authorization_required");

        fixture.Authorization.AuthorizeModsRoot(fixture.Reference.Id, external);
        var authorized = await fixture.ObserveAsync(validation);
        Equal(1, authorized.Directories.Length);

        var other = Path.Combine(fixture.Root, "other-mods");
        Directory.CreateDirectory(other);
        var changed = await fixture.ObserveAsync(validation with { ModsDirectory = other });
        Equal(Mo2InventoryObservationStatus.Unavailable, changed.Status);
    }

    private static async Task CatalogProjectionAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Real\n-Missing");
        fixture.CreateMod("Real", "version=2.0\nnewestVersion=1.0\n");
        var catalog = new Mo2CatalogService(
            new MockGridCatalogService(),
            new ProfileReferenceStore(fixture.Reference),
            _ => new ProfileStaticValidator(fixture.Validation),
            new Mo2ProfileSnapshotService(
                fixture.FileSystem,
                new Grid.Mo2.Infrastructure.Mo2TextDecoder(),
                fixture.Paths,
                new Mo2SessionPathAuthorization(fixture.Paths)),
            fixture.CreateService());
        var result = await catalog.GetCatalogAsync();
        var profile = result.Games.SelectMany(game => game.Installations)
            .Single(installation => installation.Id == fixture.Reference.InstallationId)
            .Profiles.Single();
        Equal(2, profile.Mods.Length);
        True(profile.Mods.All(mod => mod.Inventory is not null));
        Equal(ModUpdateState.Unknown, profile.Mods.Single(mod => mod.Name == "Real").UpdateState);
        Equal(null, profile.Mods.Single(mod => mod.Name == "Missing").Priority);
        True(profile.Observation!.Inventory is not null);
    }

    private static async Task AmbiguousEnumerationAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Ambiguous");
        fixture.CreateMod("Ambiguous");
        var duplicate = new DuplicateEnumerationInventoryFileSystem(
            fixture.FileSystem,
            fixture.ModsRoot,
            fixture.Paths);
        var snapshot = await fixture.ObserveAsync(fileSystem: duplicate);
        var row = snapshot.Profiles.Single().Entries.Single(entry => entry.Name == "Ambiguous");
        Equal(Mo2ModReconciliationState.Ambiguous, row.Reconciliation);
        Equal(null, row.Mo2Priority);
        False(snapshot.Profiles.Single().Entries.Any(entry => entry.Reconciliation == Mo2ModReconciliationState.Unlisted));
    }

    private static async Task MetadataBoundsAndChangesAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Large\n+Changing");
        var large = fixture.CreateMod("Large", "version=1");
        await using (var stream = new FileStream(Path.Combine(large, "meta.ini"), FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(Mo2ModInventoryService.MaximumMetadataBytes + 1L);
        }

        var changing = fixture.CreateMod("Changing", "version=1");
        var mutating = new MutatingInventoryFileSystem(
            fixture.FileSystem,
            Path.Combine(changing, "meta.ini"),
            fixture.Paths);
        var snapshot = await fixture.ObserveAsync(fileSystem: mutating);
        Equal(Mo2MetadataAvailability.Oversized, snapshot.Directories.Single(directory => directory.Name == "Large").Metadata.Availability);
        Equal(Mo2MetadataAvailability.ChangedDuringRead, snapshot.Directories.Single(directory => directory.Name == "Changing").Metadata.Availability);
        Equal(Mo2InventoryObservationStatus.Inconsistent, snapshot.Status);
    }

    private static async Task LargeInventoryAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        var names = Enumerable.Range(0, 300).Select(index => $"Mod-{index:D4}").ToArray();
        fixture.CreateProfile("Default", string.Join('\n', names.Select(name => $"+{name}")));
        foreach (var name in names)
        {
            fixture.CreateMod(name);
        }

        var snapshot = await fixture.ObserveAsync();
        Equal(300, snapshot.Directories.Length);
        Equal(300, snapshot.Profiles.Single().Entries.Length);
        Equal(300, snapshot.Profiles.Single().Entries.Select(entry => entry.Id).Distinct().Count());
    }

    private static async Task UnavailableCatalogAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Must not project");
        var external = Path.Combine(fixture.Root, "unauthorized-mods");
        Directory.CreateDirectory(Path.Combine(external, "Must not project"));
        var validation = fixture.Validation with { ModsDirectory = external };
        var inventoryService = new Mo2ModInventoryService(
            fixture.FileSystem,
            new Grid.Mo2.Infrastructure.Mo2TextDecoder(),
            fixture.Paths,
            new Mo2SessionModsPathAuthorization(fixture.Paths));
        var catalog = new Mo2CatalogService(
            new MockGridCatalogService(),
            new ProfileReferenceStore(fixture.Reference),
            _ => new ProfileStaticValidator(validation),
            new Mo2ProfileSnapshotService(
                fixture.FileSystem,
                new Grid.Mo2.Infrastructure.Mo2TextDecoder(),
                fixture.Paths,
                new Mo2SessionPathAuthorization(fixture.Paths)),
            inventoryService);
        var result = await catalog.GetCatalogAsync();
        var profile = result.Games.SelectMany(game => game.Installations)
            .Single(installation => installation.Id == fixture.Reference.InstallationId)
            .Profiles.Single();
        Equal(0, profile.Mods.Length);
        Equal(ModInventoryObservationStatus.AuthorizationRequired, profile.Observation!.Inventory!.Status);
    }

    private static async Task CancellationAsync()
    {
        using var fixture = await Mo2ModInventoryFixtureBuilder.CreateAsync();
        fixture.CreateProfile("Default", "+Mod");
        fixture.CreateMod("Mod");
        using var source = new CancellationTokenSource();
        source.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
        {
            var profiles = await fixture.ObserveProfilesAsync();
            await fixture.CreateService().ObserveAsync(new(fixture.Reference, fixture.Validation, profiles), source.Token);
        });
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ModInventoryCheckResult>.Builder results,
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

sealed class DuplicateEnumerationInventoryFileSystem(
    IMo2InventoryFileSystem inner,
    string targetRoot,
    IMo2PathCanonicalizer paths) : IMo2InventoryFileSystem
{
    public Mo2PathState ProbeFile(string path) => inner.ProbeFile(path);
    public Mo2PathState ProbeDirectory(string path) => inner.ProbeDirectory(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => EnumerateDirectoriesWithState(path).Directories;
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path)
    {
        var result = inner.EnumerateDirectoriesWithState(path);
        return paths.Equals(path, targetRoot) && result.Directories.Length == 1
            ? result with { Directories = [result.Directories[0], result.Directories[0]] }
            : result;
    }
    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
    public Mo2FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);
    public Mo2DirectoryMetadata GetDirectoryMetadata(string path) => inner.GetDirectoryMetadata(path);
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesAsync(path, maximumBytes, cancellationToken);
    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}

sealed class MutatingInventoryFileSystem(
    IMo2InventoryFileSystem inner,
    string target,
    IMo2PathCanonicalizer paths) : IMo2InventoryFileSystem
{
    private int mutated;
    public Mo2PathState ProbeFile(string path) => inner.ProbeFile(path);
    public Mo2PathState ProbeDirectory(string path) => inner.ProbeDirectory(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) => inner.EnumerateDirectoriesWithState(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
    public Mo2FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);
    public Mo2DirectoryMetadata GetDirectoryMetadata(string path) => inner.GetDirectoryMetadata(path);
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesAsync(path, maximumBytes, cancellationToken);
    public async Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        var result = await inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);
        if (paths.Equals(path, target) && Interlocked.Exchange(ref mutated, 1) == 0)
        {
            await File.AppendAllTextAsync(target, "\nnotes=changed", cancellationToken);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(2));
        }

        return result;
    }
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}
