using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2ProfileEnvironmentResolutionCheckResult(string Name, Exception? Failure);

static class Mo2ProfileEnvironmentResolutionChecks
{
    public static async Task<string> InspectReadOnlyAsync(string selectedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        var paths = new WindowsPathCanonicalizer();
        var fileSystem = new Mo2FileSystem();
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var validator = new Mo2InstallationValidator(
            fileSystem,
            new Mo2IniReader(fileSystem),
            paths,
            Path.Combine(localApplicationData, "ModOrganizer"));
        var references = new ReadOnlyEmptyReferenceStore();
        var discovery = new Mo2DiscoveryService(
            new WindowsMo2EvidenceSource(fileSystem, paths),
            references,
            paths);
        var profileAuthorization = new Mo2SessionPathAuthorization(paths);
        var modAuthorization = new Mo2SessionModsPathAuthorization(paths);
        var decoder = new Mo2TextDecoder();
        var resolver = new Mo2ProfileEnvironmentResolutionService(
            discovery,
            validator,
            new Mo2ProfileSnapshotService(fileSystem, decoder, paths, profileAuthorization),
            new Mo2ModInventoryService(fileSystem, decoder, paths, modAuthorization),
            profileAuthorization,
            modAuthorization,
            paths,
            fileSystem);
        var catalog = await new ProductionGridCatalogService().GetCatalogAsync().ConfigureAwait(false);
        var options = new Mo2DiscoveryOptions(
            localApplicationData,
            Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            ProductionGridCatalogService.SkyrimSpecialEditionId);
        var result = await resolver.ResolveAsync(
            selectedDirectory,
            catalog.Games,
            options).ConfigureAwait(false);

        var evidence = new
        {
            result.Validation.SelectionKind,
            result.Validation.InstanceKind,
            result.Validation.ApplicationDirectory,
            result.Validation.InstanceDirectory,
            result.GameName,
            ConfiguredGameName = result.Validation.GameName,
            result.Validation.GameDirectory,
            result.Validation.CanConnect,
            result.ExactProfileSelected,
            Profiles = result.Profiles.Select(choice => new
            {
                ProfileId = choice.Id.Value,
                choice.Name,
                choice.Profile.Observation.Status,
                choice.Profile.ManagerState,
                InventoryStatus = choice.Inventory?.Status,
                choice.ManagedEnabledModCount,
                choice.ManagedDisabledModCount,
                choice.ForeignModCount,
                choice.SeparatorCount,
                choice.ExplicitlyEnabledPluginCount,
                choice.ExplicitlyDisabledPluginCount,
                choice.ActivePluginCount,
                choice.ImplicitlyActivePluginCount,
                choice.ProjectedPluginCount,
                choice.WarningCount,
                choice.Profile.Settings?.LocalSavesEnabled,
                choice.Profile.Settings?.LocalSettingsEnabled,
                CustomOutputCount = choice.Profile.Settings?.CustomOverwrites.Length ?? 0,
                Warnings = choice.Profile.Sources
                    .SelectMany(source => source.Warnings.Select(warning => warning.Message))
                    .Concat(choice.Inventory?.Warnings.Select(warning => warning.Message) ?? [])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            }),
            IssueCodes = result.Issues.Select(issue => issue.Code),
            PersistenceCalls = references.SaveCount,
        };
        return JsonSerializer.Serialize(evidence, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        });
    }

    public static async Task<ImmutableArray<Mo2ProfileEnvironmentResolutionCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2ProfileEnvironmentResolutionCheckResult>();
        await RunAsync(results, "portable markers resolve colocated application and instance", PortableAsync);
        await RunAsync(results, "an exact profile path resolves only that profile", ExactProfileAsync);
        await RunAsync(results, "an environment with multiple valid profiles preserves every choice", MultipleProfilesAsync);
        await RunAsync(results, "global and configured profile roots resolve through bounded discovery", GlobalConfiguredRootAsync);
        await RunAsync(results, "application-only selection resolves only through matching discovery evidence", ApplicationOnlyAsync);
        await RunAsync(results, "unrelated application and instance markers are rejected", UnrelatedMarkersAsync);
        await RunAsync(results, "adapter-accepted game aliases remain accepted", AcceptedAliasAsync);
        await RunAsync(results, "rejected candidate issues do not contaminate successful evidence", RejectedCandidateIsolationAsync);
        await RunAsync(results, "missing and malformed required profile sources are rejected", RequiredSourceFailuresAsync);
        await RunAsync(results, "inaccessible and changed required profile sources are rejected", UnstableSourceFailuresAsync);
        await RunAsync(results, "ambiguous validated environments fail closed", AmbiguousAsync);
        await RunAsync(results, "transient authorizations are revoked on observation failure", RevocationOnFailureAsync);
        await RunAsync(results, "transient authorizations are revoked on cancellation", RevocationOnCancellationAsync);
        await RunAsync(results, "resolver construction has no persistence dependency", NoWriterDependency);
        return results.ToImmutable();
    }

    private static async Task PortableAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Portable", "Active");
        fixture.CreateProfile(portable, "Active");
        var before = fixture.SnapshotFiles(portable);

        var result = await fixture.CreateService([]).ResolveAsync(
            portable,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        Equal(Mo2InstanceKind.Portable, result.Validation.InstanceKind);
        True(fixture.Paths.Equals(portable, result.Validation.ApplicationDirectory!));
        True(fixture.Paths.Equals(portable, result.Validation.InstanceDirectory!));
        Equal(1, result.Profiles.Length);
        Equal("Active", result.Profiles[0].Name);
        Equal(1, result.Profiles[0].ManagedEnabledModCount);
        Equal(1, result.Profiles[0].ManagedDisabledModCount);
        Equal(1, result.Profiles[0].ForeignModCount);
        Equal(1, result.Profiles[0].SeparatorCount);
        Equal(1, result.Profiles[0].ExplicitlyEnabledPluginCount);
        Equal(1, result.Profiles[0].ExplicitlyDisabledPluginCount);
        Equal(1, result.Profiles[0].ActivePluginCount);
        Equal(0, result.Profiles[0].ImplicitlyActivePluginCount);
        False(fixture.ProfileAuthorization.IsProfilesRootAuthorized(
            fixture.LastReferenceId(result), result.Validation.ProfilesDirectory!));
        False(fixture.ModAuthorization.IsModsRootAuthorized(
            fixture.LastReferenceId(result), result.Validation.ModsDirectory!));
        True(before.SequenceEqual(fixture.SnapshotFiles(portable)));
    }

    private static async Task ExactProfileAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Profiles", "First");
        fixture.CreateProfile(portable, "First");
        var selected = fixture.CreateProfile(portable, "Second");

        var result = await fixture.CreateService([]).ResolveAsync(
            selected,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        True(result.ExactProfileSelected);
        Equal(1, result.Profiles.Length);
        Equal("Second", result.Profiles[0].Name);
    }

    private static async Task MultipleProfilesAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Multiple", "Zulu");
        fixture.CreateProfile(portable, "Zulu");
        fixture.CreateProfile(portable, "Alpha");

        var result = await fixture.CreateService([]).ResolveAsync(
            portable,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        False(result.ExactProfileSelected);
        Equal(2, result.Profiles.Length);
        Equal("Alpha", result.Profiles[0].Name);
        Equal("Zulu", result.Profiles[1].Name);
    }

    private static async Task GlobalConfiguredRootAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var application = fixture.CreateApplication("Application");
        var content = fixture.CreateContentRoot("External content");
        var instance = fixture.CreateGlobal("Global", content, "External");
        var selected = fixture.CreateProfile(content, "External");
        var evidence = new Mo2DiscoveryCandidate(
            "global",
            "Global",
            application,
            instance,
            Mo2EvidenceKind.GlobalInstanceRoot,
            "Synthetic exact global-instance evidence");

        var result = await fixture.CreateService([evidence]).ResolveAsync(
            selected,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        Equal(Mo2InstanceKind.Global, result.Validation.InstanceKind);
        True(fixture.Paths.Equals(application, result.Validation.ApplicationDirectory!));
        True(fixture.Paths.Equals(instance, result.Validation.InstanceDirectory!));
        True(fixture.Paths.Equals(content, result.Validation.BaseDirectory!));
        Equal("External", result.Profiles.Single().Name);
        True(result.ValidationRequest.EffectiveAuthorizedConfiguredPaths.Length > 0);
    }

    private static async Task AmbiguousAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var application = fixture.CreateApplication("Application");
        var content = fixture.CreateContentRoot("Shared content");
        var first = fixture.CreateGlobal("First", content, "Shared");
        var second = fixture.CreateGlobal("Second", content, "Shared");
        var selected = fixture.CreateProfile(content, "Shared");
        var evidence = ImmutableArray.Create(
            new Mo2DiscoveryCandidate("first", "First", application, first,
                Mo2EvidenceKind.GlobalInstanceRoot, "First exact instance"),
            new Mo2DiscoveryCandidate("second", "Second", application, second,
                Mo2EvidenceKind.GlobalInstanceRoot, "Second exact instance"));

        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await fixture.CreateService(evidence).ResolveAsync(
                selected,
                fixture.Catalog.Games,
                fixture.DiscoveryOptions));
    }

    private static async Task ApplicationOnlyAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var application = fixture.CreateApplication("Application only");
        var content = fixture.CreateContentRoot("Application content");
        var instance = fixture.CreateGlobal("Application instance", content, "Profile");
        fixture.CreateProfile(content, "Profile");
        var evidence = ImmutableArray.Create(
            new Mo2DiscoveryCandidate(
                "application-only",
                "Application",
                application,
                null,
                Mo2EvidenceKind.InstallerDefault,
                "Bounded application evidence"),
            new Mo2DiscoveryCandidate(
                "instance-only",
                "Application instance",
                null,
                instance,
                Mo2EvidenceKind.GlobalInstanceRoot,
                "Bounded global-instance evidence"));

        var result = await fixture.CreateService(evidence).ResolveAsync(
            application,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        Equal(Mo2InstanceKind.Global, result.Validation.InstanceKind);
        True(fixture.Paths.Equals(application, result.Validation.ApplicationDirectory!));
        True(fixture.Paths.Equals(instance, result.Validation.InstanceDirectory!));
    }

    private static async Task UnrelatedMarkersAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var selectedApplication = fixture.CreateApplication("Selected application");
        var differentApplication = fixture.CreateApplication("Different application");
        var content = fixture.CreateContentRoot("Unrelated content");
        var instance = fixture.CreateGlobal("Unrelated instance", content, "Profile");
        fixture.CreateProfile(content, "Profile");
        var unrelated = new Mo2DiscoveryCandidate(
            "unrelated",
            "Unrelated",
            differentApplication,
            instance,
            Mo2EvidenceKind.GlobalInstanceRoot,
            "Unrelated discovered topology");

        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await fixture.CreateService([unrelated]).ResolveAsync(
                selectedApplication,
                fixture.Catalog.Games,
                fixture.DiscoveryOptions));
    }

    private static async Task AcceptedAliasAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Alias", "Alias profile");
        fixture.CreateProfile(portable, "Alias profile");
        fixture.ReplaceGameName(portable, "SkyrimSE");

        var result = await fixture.CreateService([]).ResolveAsync(
            portable,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        Equal("SkyrimSE", result.Validation.GameName);
        Equal(1, result.Profiles.Length);
    }

    private static async Task RejectedCandidateIsolationAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Valid portable", "Profile");
        fixture.CreateProfile(portable, "Profile");
        var staleContent = fixture.CreateContentRoot("Stale content");
        var staleInstance = fixture.CreateGlobal("Stale instance", staleContent, "Stale");
        fixture.CreateProfile(staleContent, "Stale");
        var stale = new Mo2DiscoveryCandidate(
            "stale",
            "Stale",
            null,
            staleInstance,
            Mo2EvidenceKind.CurrentInstance,
            "Stale instance without application evidence");

        var result = await fixture.CreateService([stale]).ResolveAsync(
            portable,
            fixture.Catalog.Games,
            fixture.DiscoveryOptions);

        False(result.Issues.Any(issue => issue.Code == "mo2.application.required"));
        True(fixture.Paths.Equals(portable, result.Validation.InstanceDirectory!));
    }

    private static async Task RevocationOnFailureAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Failure", "Active");
        fixture.CreateProfile(portable, "Active");
        var profileAuthorization = new TrackingProfileAuthorization(fixture.Paths);
        var modAuthorization = new TrackingModAuthorization(fixture.Paths);
        var decoder = new Mo2TextDecoder();
        var service = new Mo2ProfileEnvironmentResolutionService(
            new EnvironmentResolutionDiscoveryService([]),
            fixture.Validator,
            new Mo2ProfileSnapshotService(fixture.FileSystem, decoder, fixture.Paths, profileAuthorization),
            new ThrowingInventoryService(),
            profileAuthorization,
            modAuthorization,
            fixture.Paths,
            fixture.FileSystem);

        await ThrowsAsync<IOException>(async () =>
            _ = await service.ResolveAsync(portable, fixture.Catalog.Games, fixture.DiscoveryOptions));
        Equal(1, profileAuthorization.AuthorizationCount);
        Equal(1, profileAuthorization.RevocationCount);
        Equal(1, modAuthorization.AuthorizationCount);
        Equal(1, modAuthorization.RevocationCount);
    }

    private static async Task RevocationOnCancellationAsync()
    {
        using var fixture = await EnvironmentResolutionFixture.CreateAsync();
        var portable = fixture.CreatePortable("Cancellation", "Active");
        fixture.CreateProfile(portable, "Active");
        var profileAuthorization = new TrackingProfileAuthorization(fixture.Paths);
        var modAuthorization = new TrackingModAuthorization(fixture.Paths);
        var decoder = new Mo2TextDecoder();
        using var cancellation = new CancellationTokenSource();
        var service = new Mo2ProfileEnvironmentResolutionService(
            new EnvironmentResolutionDiscoveryService([]),
            fixture.Validator,
            new Mo2ProfileSnapshotService(fixture.FileSystem, decoder, fixture.Paths, profileAuthorization),
            new CancelingInventoryService(cancellation),
            profileAuthorization,
            modAuthorization,
            fixture.Paths,
            fixture.FileSystem);

        await ThrowsAsync<OperationCanceledException>(async () =>
            _ = await service.ResolveAsync(
                portable,
                fixture.Catalog.Games,
                fixture.DiscoveryOptions,
                cancellation.Token));
        Equal(1, profileAuthorization.RevocationCount);
        Equal(1, modAuthorization.RevocationCount);
    }

    private static Task NoWriterDependency()
    {
        var parameterTypes = typeof(Mo2ProfileEnvironmentResolutionService)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        False(parameterTypes.Any(type => typeof(IMo2InstallationReferenceStore).IsAssignableFrom(type)));
        False(parameterTypes.Any(type => typeof(IMo2ConnectionService).IsAssignableFrom(type)));
        False(parameterTypes.Any(type => typeof(IGridAtomicFileStore).IsAssignableFrom(type)));
        return Task.CompletedTask;
    }

    private static async Task RequiredSourceFailuresAsync()
    {
        using var missingFixture = await EnvironmentResolutionFixture.CreateAsync();
        var missing = missingFixture.CreatePortable("Missing", "Profile");
        var missingProfile = missingFixture.CreateProfile(missing, "Profile");
        File.Delete(Path.Combine(missingProfile, "modlist.txt"));
        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await missingFixture.CreateService([]).ResolveAsync(
                missing,
                missingFixture.Catalog.Games,
                missingFixture.DiscoveryOptions));

        using var malformedFixture = await EnvironmentResolutionFixture.CreateAsync();
        var malformed = malformedFixture.CreatePortable("Malformed", "Profile");
        var malformedProfile = malformedFixture.CreateProfile(malformed, "Profile");
        File.WriteAllBytes(Path.Combine(malformedProfile, "modlist.txt"), [0xff, 0xff, 0xff]);
        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await malformedFixture.CreateService([]).ResolveAsync(
                malformed,
                malformedFixture.Catalog.Games,
                malformedFixture.DiscoveryOptions));
    }

    private static async Task UnstableSourceFailuresAsync()
    {
        using var inaccessibleFixture = await EnvironmentResolutionFixture.CreateAsync();
        var inaccessible = inaccessibleFixture.CreatePortable("Inaccessible", "Profile");
        var inaccessibleProfile = inaccessibleFixture.CreateProfile(inaccessible, "Profile");
        var inaccessibleSource = Path.Combine(inaccessibleProfile, "modlist.txt");
        var inaccessibleFileSystem = new InaccessibleFileSystem(
            inaccessibleFixture.FileSystem,
            inaccessibleSource,
            inaccessibleFixture.Paths);
        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await inaccessibleFixture.CreateService([], inaccessibleFileSystem).ResolveAsync(
                inaccessible,
                inaccessibleFixture.Catalog.Games,
                inaccessibleFixture.DiscoveryOptions));

        using var changedFixture = await EnvironmentResolutionFixture.CreateAsync();
        var changed = changedFixture.CreatePortable("Changed", "Profile");
        var changedProfile = changedFixture.CreateProfile(changed, "Profile");
        var changingFileSystem = new MutatingReadFileSystem(
            changedFixture.FileSystem,
            Path.Combine(changedProfile, "modlist.txt"),
            changedFixture.Paths);
        await ThrowsAsync<InvalidDataException>(async () =>
            _ = await changedFixture.CreateService([], changingFileSystem).ResolveAsync(
                changed,
                changedFixture.Catalog.Games,
                changedFixture.DiscoveryOptions));
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2ProfileEnvironmentResolutionCheckResult>.Builder results,
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

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
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

sealed class ReadOnlyEmptyReferenceStore : IMo2InstallationReferenceStore
{
    public int SaveCount { get; private set; }

    public Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Mo2ReferenceLoadResult([], []));
    }

    public Task SaveAsync(
        ImmutableArray<Mo2InstallationReference> references,
        CancellationToken cancellationToken = default)
    {
        SaveCount++;
        throw new InvalidOperationException("The read-only inspection path attempted to persist a reference.");
    }
}

sealed class EnvironmentResolutionFixture : IDisposable
{
    private EnvironmentResolutionFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-environment-resolution-{Guid.NewGuid():N}");
        LocalApplicationData = Path.Combine(Root, "LocalAppData");
        GlobalRoot = Path.Combine(LocalApplicationData, "ModOrganizer");
        GameRoot = Path.Combine(Root, "Skyrim Special Edition");
        Directory.CreateDirectory(GlobalRoot);
        Directory.CreateDirectory(Path.Combine(GameRoot, "Data"));
        File.WriteAllText(Path.Combine(GameRoot, "SkyrimSE.exe"), "fixture");
        Paths = new WindowsPathCanonicalizer();
        FileSystem = new Mo2FileSystem();
        Validator = new Mo2InstallationValidator(FileSystem, new Mo2IniReader(FileSystem), Paths, GlobalRoot);
        ProfileAuthorization = new Mo2SessionPathAuthorization(Paths);
        ModAuthorization = new Mo2SessionModsPathAuthorization(Paths);
        DiscoveryOptions = new(LocalApplicationData, Path.GetPathRoot(Root)!, new("game.skyrim-special-edition"));
    }

    public string Root { get; }
    public string LocalApplicationData { get; }
    public string GlobalRoot { get; }
    public string GameRoot { get; }
    public WindowsPathCanonicalizer Paths { get; }
    public Mo2FileSystem FileSystem { get; }
    public Mo2InstallationValidator Validator { get; }
    public Mo2SessionPathAuthorization ProfileAuthorization { get; }
    public Mo2SessionModsPathAuthorization ModAuthorization { get; }
    public GridCatalogSnapshot Catalog { get; private set; } = null!;
    public Mo2DiscoveryOptions DiscoveryOptions { get; }

    public static async Task<EnvironmentResolutionFixture> CreateAsync()
    {
        var fixture = new EnvironmentResolutionFixture();
        fixture.Catalog = await new ProductionGridCatalogService().GetCatalogAsync();
        return fixture;
    }

    public string CreateApplication(string name)
    {
        var root = Path.Combine(Root, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "ModOrganizer.exe"), "fixture");
        return root;
    }

    public string CreatePortable(string name, string selectedProfile)
    {
        var root = CreateApplication(name);
        CreateContentDirectories(root);
        WriteIni(root, root, selectedProfile);
        return root;
    }

    public string CreateGlobal(string name, string contentRoot, string selectedProfile)
    {
        var root = Path.Combine(GlobalRoot, name);
        Directory.CreateDirectory(root);
        WriteIni(root, contentRoot, selectedProfile);
        return root;
    }

    public string CreateContentRoot(string name)
    {
        var root = Path.Combine(Root, name);
        CreateContentDirectories(root);
        return root;
    }

    public string CreateProfile(string contentRoot, string name)
    {
        var profile = Path.Combine(contentRoot, "profiles", name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"),
            "+Enabled mod\n-Disabled mod\n*Foreign plugin\n+Section_separator\n");
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), "*Enabled.esp\nDisabled.esp\n");
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), "Enabled.esp\nDisabled.esp\n");
        File.WriteAllText(Path.Combine(profile, "settings.ini"), "[General]\nLocalSaves=true\nLocalSettings=true\n");
        foreach (var directory in new[] { "Enabled mod", "Disabled mod", "Section_separator" })
            Directory.CreateDirectory(Path.Combine(contentRoot, "mods", directory));
        return profile;
    }

    public void ReplaceGameName(string instanceRoot, string gameName)
    {
        var iniPath = Path.Combine(instanceRoot, "ModOrganizer.ini");
        var content = File.ReadAllText(iniPath);
        File.WriteAllText(iniPath,
            content.Replace("gameName=Skyrim Special Edition", $"gameName={gameName}", StringComparison.Ordinal));
    }

    public ImmutableArray<string> SnapshotFiles(string root) => Directory
        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => $"{Path.GetRelativePath(root, path)}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
        .ToImmutableArray();

    public Mo2ProfileEnvironmentResolutionService CreateService(
        ImmutableArray<Mo2DiscoveryCandidate> evidence,
        IMo2ReadOnlyFileSystem? profileFileSystem = null)
    {
        var discovery = new EnvironmentResolutionDiscoveryService(evidence);
        var decoder = new Mo2TextDecoder();
        var source = profileFileSystem ?? FileSystem;
        return new(
            discovery,
            Validator,
            new Mo2ProfileSnapshotService(source, decoder, Paths, ProfileAuthorization),
            new Mo2ModInventoryService(FileSystem, decoder, Paths, ModAuthorization),
            ProfileAuthorization,
            ModAuthorization,
            Paths,
            FileSystem);
    }

    public InstallationReferenceId LastReferenceId(Mo2ProfileEnvironmentResolution result)
    {
        var key = result.Validation.ConnectionKey ??
            Paths.GetIdentityKey(result.Validation.ExecutablePath!, result.Validation.InstanceDirectory!);
        var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..24];
        return new($"reference.mo2.preview.{suffix}");
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private void WriteIni(string instanceRoot, string contentRoot, string selectedProfile)
    {
        var content = contentRoot.Replace('\\', '/');
        var game = GameRoot.Replace('\\', '/');
        File.WriteAllText(Path.Combine(instanceRoot, "ModOrganizer.ini"),
            $"[General]\nselected_profile={selectedProfile}\ngameName=Skyrim Special Edition\ngamePath={game}\n" +
            $"[Settings]\nbase_directory={content}\nmod_directory=%BASE_DIR%/mods\n" +
            "profiles_directory=%BASE_DIR%/profiles\ndownload_directory=%BASE_DIR%/downloads\n" +
            "overwrite_directory=%BASE_DIR%/overwrite\n");
    }

    private static void CreateContentDirectories(string root)
    {
        foreach (var directory in new[] { "mods", "profiles", "downloads", "overwrite" })
            Directory.CreateDirectory(Path.Combine(root, directory));
    }
}

sealed class EnvironmentResolutionDiscoveryService(ImmutableArray<Mo2DiscoveryCandidate> candidates)
    : IMo2DiscoveryService
{
    public Task<Mo2DiscoveryResult> DiscoverAsync(
        Mo2DiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new Mo2DiscoveryResult(candidates, []));
    }
}

sealed class ThrowingInventoryService : IMo2ModInventoryService
{
    public Task<Mo2ModInventorySnapshot> ObserveAsync(
        Mo2ModInventoryRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromException<Mo2ModInventorySnapshot>(new IOException("Synthetic observation failure."));
}

sealed class CancelingInventoryService(CancellationTokenSource cancellation) : IMo2ModInventoryService
{
    public Task<Mo2ModInventorySnapshot> ObserveAsync(
        Mo2ModInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellation.Cancel();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The cancellation token was not observed.");
    }
}

sealed class TrackingProfileAuthorization(IMo2PathCanonicalizer paths) : IMo2SessionPathAuthorization
{
    private readonly Mo2SessionPathAuthorization inner = new(paths);
    public int AuthorizationCount { get; private set; }
    public int RevocationCount { get; private set; }

    public Mo2ProfilesRootAuthorization AuthorizeProfilesRoot(InstallationReferenceId referenceId, string profilesRoot)
    {
        AuthorizationCount++;
        return inner.AuthorizeProfilesRoot(referenceId, profilesRoot);
    }

    public bool IsProfilesRootAuthorized(InstallationReferenceId referenceId, string profilesRoot) =>
        inner.IsProfilesRootAuthorized(referenceId, profilesRoot);

    public void Revoke(InstallationReferenceId referenceId)
    {
        RevocationCount++;
        inner.Revoke(referenceId);
    }
}

sealed class TrackingModAuthorization(IMo2PathCanonicalizer paths) : IMo2ModsPathAuthorization
{
    private readonly Mo2SessionModsPathAuthorization inner = new(paths);
    public int AuthorizationCount { get; private set; }
    public int RevocationCount { get; private set; }

    public Mo2ModsRootAuthorization AuthorizeModsRoot(InstallationReferenceId referenceId, string modsRoot)
    {
        AuthorizationCount++;
        return inner.AuthorizeModsRoot(referenceId, modsRoot);
    }

    public bool IsModsRootAuthorized(InstallationReferenceId referenceId, string modsRoot) =>
        inner.IsModsRootAuthorized(referenceId, modsRoot);

    public void Revoke(InstallationReferenceId referenceId)
    {
        RevocationCount++;
        inner.Revoke(referenceId);
    }
}
