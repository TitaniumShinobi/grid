using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

var checks = 0;
var failures = new List<string>();

await CheckAsync("portable validation does not require portable.txt", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable(includePortableLock: false);
    var result = await fixture.ValidateAuthorizedAsync(portable, null);
    Equal(Mo2ValidationStatus.Valid, result.Status);
    Equal(Mo2SelectionKind.PortableInstance, result.SelectionKind);
    Equal(Mo2InstanceKind.Portable, result.InstanceKind);
    True(result.CanConnect);
});

await CheckAsync("portable.txt does not change portable classification", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable(includePortableLock: true);
    var result = await fixture.ValidateAuthorizedAsync(portable, portable);
    Equal(Mo2InstanceKind.Portable, result.InstanceKind);
    True(result.Paths.All(path => path.State == Mo2PathState.Present));
});

await CheckAsync("global application and instance remain distinct", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("FixtureGlobalOne");
    var result = await fixture.ValidateAuthorizedAsync(app, instance);
    Equal(Mo2SelectionKind.GlobalInstance, result.SelectionKind);
    Equal(Mo2InstanceKind.Global, result.InstanceKind);
    False(fixture.Paths.Equals(result.ApplicationDirectory!, result.InstanceDirectory!));
    True(result.CanConnect);
});

await CheckAsync("MO2 executable selection normalizes to application directory", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("FixtureGlobalTwo");
    var result = await fixture.ValidateAuthorizedAsync(Path.Combine(app, "ModOrganizer.exe"), instance);
    True(fixture.Paths.Equals(app, result.ApplicationDirectory!));
    True(result.ExecutablePath!.EndsWith("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase));
});

await CheckAsync("application-only selection is incomplete", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var result = await fixture.Validator.ValidateAsync(new(app, null, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Incomplete, result.Status);
    Equal(Mo2SelectionKind.ApplicationDirectory, result.SelectionKind);
    False(result.CanConnect);
});

await CheckAsync("game directory is distinguished from MO2", async () =>
{
    using var fixture = new Fixture();
    var game = fixture.CreateGame();
    var result = await fixture.Validator.ValidateAsync(new(null, game, Fixture.SkyrimGame));
    Equal(Mo2SelectionKind.GameDirectory, result.SelectionKind);
    False(result.CanConnect);
});

await CheckAsync("base directory is distinguished from instance directory", async () =>
{
    using var fixture = new Fixture();
    var baseDirectory = fixture.CreateContentRoot("base-only");
    var result = await fixture.Validator.ValidateAsync(new(null, baseDirectory, Fixture.SkyrimGame));
    Equal(Mo2SelectionKind.BaseDirectory, result.SelectionKind);
    False(result.CanConnect);
});

await CheckAsync("custom MO2 directories resolve from BASE_DIR", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var content = fixture.CreateContentRoot("custom-content");
    var instance = fixture.CreateGlobal("Custom", content);
    var result = await fixture.ValidateAuthorizedAsync(app, instance);
    True(fixture.Paths.Equals(Path.Combine(content, "mods"), result.ModsDirectory!));
    True(fixture.Paths.Equals(Path.Combine(content, "profiles"), result.ProfilesDirectory!));
});

await CheckAsync("missing content is reported as incomplete", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("MissingDownloads");
    Directory.Delete(Path.Combine(instance, "downloads"));
    var result = await fixture.ValidateAuthorizedAsync(app, instance);
    Equal(Mo2ValidationStatus.Incomplete, result.Status);
    Contains(result.Issues, issue => issue.Code == "mo2.path.missing");
});

await CheckAsync("inaccessible path is a distinct validation state", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Offline");
    var denied = Path.Combine(instance, "mods");
    var fileSystem = new InaccessibleFileSystem(new Mo2FileSystem(), denied, fixture.Paths);
    var validator = new Mo2InstallationValidator(fileSystem, new Mo2IniReader(fileSystem), fixture.Paths, fixture.GlobalRoot);
    var result = await validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Inaccessible, result.Status);
    Contains(result.Paths, path => path.State == Mo2PathState.Inaccessible);
});

await CheckAsync("malformed configuration fails safely", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    await File.WriteAllTextAsync(Path.Combine(portable, "ModOrganizer.ini"), "[General\ngamePath=x");
    var result = await fixture.Validator.ValidateAsync(new(portable, portable, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Invalid, result.Status);
    Contains(result.Issues, issue => issue.Code == "mo2.ini.unreadable");
});

await CheckAsync("oversized INI line fails safely", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    await File.AppendAllTextAsync(Path.Combine(portable, "ModOrganizer.ini"), $"oversized={new string('x', 17 * 1024)}");
    var result = await fixture.Validator.ValidateAsync(new(portable, portable, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Invalid, result.Status);
    Contains(result.Issues, issue => issue.Code == "mo2.ini.unreadable");
});

await CheckAsync("Qt ByteArray paths are decoded", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("ByteArray");
    var game = fixture.GameRoot.Replace("\\", "\\\\", StringComparison.Ordinal);
    var ini = await File.ReadAllTextAsync(Path.Combine(instance, "ModOrganizer.ini"));
    ini = ini.Replace($"gamePath={fixture.GameRoot.Replace('\\', '/')}", $"gamePath=@ByteArray({game})", StringComparison.Ordinal);
    await File.WriteAllTextAsync(Path.Combine(instance, "ModOrganizer.ini"), ini);
    var result = await fixture.ValidateAuthorizedAsync(app, instance);
    True(result.CanConnect);
});

await CheckAsync("relative traversal and unresolved tokens are rejected", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Traversal");
    var iniPath = Path.Combine(instance, "ModOrganizer.ini");
    var ini = await File.ReadAllTextAsync(iniPath);
    ini = ini.Replace("mod_directory=%BASE_DIR%/mods", "mod_directory=%BASE_DIR%/../mods", StringComparison.Ordinal);
    await File.WriteAllTextAsync(iniPath, ini);
    var result = await fixture.Validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Invalid, result.Status);
    False(result.CanConnect);
});

await CheckAsync("non-Skyrim and wrong adapter identities are rejected", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Identity");
    var connection = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    var wrongGame = await connection.ConnectAsync(new(new(app, instance, new("game.grand-theft-auto-v")), Fixture.Mo2Adapter, "Wrong"));
    False(wrongGame.Succeeded);
    var wrongAdapter = await connection.ConnectAsync(new(new(app, instance, Fixture.SkyrimGame), new("adapter.mock.mod-organizer-2"), "Wrong"));
    False(wrongAdapter.Succeeded);
});

await CheckAsync("reference store round-trips approved reconnect fields", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Stored");
    var reference = fixture.Reference(app, instance, "Stored");
    await fixture.ReferenceStore.SaveAsync([reference]);
    var loaded = await fixture.ReferenceStore.LoadAsync();
    Equal(1, loaded.References.Length);
    Equal(reference, loaded.References[0]);
    var json = await File.ReadAllTextAsync(fixture.ReferenceStorePath);
    False(json.Contains("modsDirectory", StringComparison.OrdinalIgnoreCase));
    False(json.Contains("connectionKey", StringComparison.OrdinalIgnoreCase));
    False(json.Contains("applicationDirectory", StringComparison.OrdinalIgnoreCase));
});

await CheckAsync("malformed reference store degrades safely", async () =>
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.ReferenceStorePath)!);
    await File.WriteAllTextAsync(fixture.ReferenceStorePath, "{not-json");
    var loaded = await fixture.ReferenceStore.LoadAsync();
    Equal(0, loaded.References.Length);
    Contains(loaded.Issues, issue => issue.Code == "mo2.references.unreadable");
});

await CheckAsync("malformed reference store is visible in the mixed catalog", async () =>
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.ReferenceStorePath)!);
    await File.WriteAllTextAsync(fixture.ReferenceStorePath, "{not-json");
    var service = new Mo2CatalogService(
        new MockGridCatalogService(),
        fixture.ReferenceStore,
        _ => fixture.Validator);

    var catalog = await service.GetCatalogAsync();

    Equal(CatalogSourceKind.Mixed, catalog.SourceKind);
    var skyrim = catalog.Games.Single(game => game.Id == Fixture.SkyrimGame);
    Contains(skyrim.Health.Advisories, advisory => advisory.Code == "mo2.references.unreadable");
});

await CheckAsync("catalog revision reflects sanitized connection-store issue details", async () =>
{
    var first = new Mo2CatalogService(
        new MockGridCatalogService(),
        new StaticReferenceStore(new(
            [],
            [new("mo2.references.unreadable", Mo2IssueSeverity.Error, "First safe recovery detail.")])),
        _ => throw new InvalidOperationException("No reference should be validated."));
    var second = new Mo2CatalogService(
        new MockGridCatalogService(),
        new StaticReferenceStore(new(
            [],
            [new("mo2.references.unreadable", Mo2IssueSeverity.Error, "Second safe recovery detail.")])),
        _ => throw new InvalidOperationException("No reference should be validated."));

    var firstCatalog = await first.GetCatalogAsync();
    var secondCatalog = await second.GetCatalogAsync();

    False(firstCatalog.Revision.Equals(secondCatalog.Revision, StringComparison.Ordinal));
});

await CheckAsync("unsupported reference schema degrades safely", async () =>
{
    using var fixture = new Fixture();
    Directory.CreateDirectory(Path.GetDirectoryName(fixture.ReferenceStorePath)!);
    await File.WriteAllTextAsync(fixture.ReferenceStorePath, "{\"schemaVersion\":99,\"references\":[]}");
    var loaded = await fixture.ReferenceStore.LoadAsync();
    Equal(0, loaded.References.Length);
    Contains(loaded.Issues, issue => issue.Code == "mo2.references.unreadable");
});

await CheckAsync("failed atomic save retains prior reference data", async () =>
{
    using var fixture = new Fixture();
    var atomic = new ToggleAtomicStore(new Mo2FileSystem());
    var store = new Mo2InstallationReferenceStore(atomic, fixture.Paths, fixture.ReferenceStorePath);
    var app = fixture.CreateApplication();
    var firstInstance = fixture.CreateGlobal("AtomicOne");
    var first = fixture.Reference(app, firstInstance, "One");
    await store.SaveAsync([first]);
    atomic.FailWrites = true;
    var second = fixture.Reference(app, fixture.CreateGlobal("AtomicTwo"), "Two");
    await ThrowsAsync<IOException>(() => store.SaveAsync([first, second]));
    atomic.FailWrites = false;
    var loaded = await store.LoadAsync();
    Equal(1, loaded.References.Length);
    Equal(first.Id, loaded.References[0].Id);
});

await CheckAsync("reference-store recovery restores exact bytes or removes a newly created store", async () =>
{
    using var fixture = new Fixture();
    var absent = await fixture.ReferenceStore.CaptureSnapshotAsync();
    False(absent.Existed);
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("RecoverySnapshot");
    await fixture.ReferenceStore.SaveAsync([fixture.Reference(app, instance, "Recovery")]);
    await fixture.ReferenceStore.RestoreSnapshotAsync(absent);
    False(File.Exists(fixture.ReferenceStorePath));

    Directory.CreateDirectory(Path.GetDirectoryName(fixture.ReferenceStorePath)!);
    const string exactEmptyStore = "{\"schemaVersion\":1,\"references\":[]}\r\n";
    await File.WriteAllTextAsync(fixture.ReferenceStorePath, exactEmptyStore);
    var existing = await fixture.ReferenceStore.CaptureSnapshotAsync();
    await fixture.ReferenceStore.SaveAsync([fixture.Reference(app, instance, "Recovery")]);
    await fixture.ReferenceStore.RestoreSnapshotAsync(existing);
    Equal(exactEmptyStore, await File.ReadAllTextAsync(fixture.ReferenceStorePath));
});

await CheckAsync("reference store rejects duplicate canonical paths", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("DuplicateStore");
    var first = fixture.Reference(app, instance, "One");
    var second = first with
    {
        Id = new("reference.mo2.second"),
        InstallationId = new("installation.mo2.second"),
        DisplayName = "Two",
    };
    await ThrowsAsync<InvalidDataException>(() => fixture.ReferenceStore.SaveAsync([first, second]));
});

await CheckAsync("reference store caps persisted references", async () =>
{
    using var fixture = new Fixture();
    var references = Enumerable.Range(0, 129)
        .Select(index => fixture.Reference(
            Path.Combine(fixture.Root, $"app-{index}"),
            Path.Combine(fixture.Root, $"instance-{index}"),
            $"Item {index}",
            index))
        .ToImmutableArray();
    await ThrowsAsync<ArgumentException>(() => fixture.ReferenceStore.SaveAsync(references));
});

await CheckAsync("connection creates deterministic name-independent IDs", async () =>
{
    using var firstFixture = new Fixture();
    var app = firstFixture.CreateApplication();
    var instance = firstFixture.CreateGlobal("Stable");
    var service = new Mo2ConnectionService(firstFixture.Validator, firstFixture.ReferenceStore, firstFixture.Paths);
    var firstRequest = await firstFixture.AuthorizedRequestAsync(app, instance);
    var first = await service.ConnectAsync(new(firstRequest, Fixture.Mo2Adapter, " First "));
    True(first.Succeeded);
    Equal("First", first.Reference!.DisplayName);

    using var secondFixture = new Fixture();
    var secondStore = new Mo2InstallationReferenceStore(new Mo2FileSystem(), secondFixture.Paths, secondFixture.ReferenceStorePath);
    var secondService = new Mo2ConnectionService(firstFixture.Validator, secondStore, firstFixture.Paths);
    var second = await secondService.ConnectAsync(new(firstRequest, Fixture.Mo2Adapter, "Renamed"));
    Equal(first.Reference.Id, second.Reference!.Id);
    Equal(first.Reference.InstallationId, second.Reference.InstallationId);
});

await CheckAsync("duplicate connection is rejected before save", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("DuplicateConnect");
    var service = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    var request = await fixture.AuthorizedRequestAsync(app, instance);
    True((await service.ConnectAsync(new(request, Fixture.Mo2Adapter, "One"))).Succeeded);
    var duplicate = await service.ConnectAsync(new(request, Fixture.Mo2Adapter, "Two"));
    False(duplicate.Succeeded);
    Contains(duplicate.Issues, issue => issue.Code == "mo2.connection.duplicate");
    Equal(1, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("registration validates and persists the exact selected profile", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    fixture.SelectProfile(portable, portable, "UNDEFEATED");
    var service = fixture.RegistrationService();

    var result = await service.RegisterAsync(new(
        portable,
        portable,
        "UNDEFEATED",
        "UNDEFEATED",
        Fixture.SkyrimGame,
        Fixture.Mo2Adapter));

    True(result.Succeeded);
    Equal(Mo2RegistrationStatus.Registered, result.Status);
    Equal(Mo2RegistrationRecoveryDisposition.None, result.RecoveryDisposition);
    Equal("UNDEFEATED", result.ProfileName);
    True(result.ProfileId is not null && result.ProfileId.Value.Value.StartsWith("profile.mo2.", StringComparison.Ordinal));
    Equal(1, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("registration idempotently reuses a matching reference", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    fixture.SelectProfile(portable, portable, "UNDEFEATED");
    var service = fixture.RegistrationService();
    var request = new Mo2RegistrationRequest(
        portable, portable, "UNDEFEATED", "UNDEFEATED", Fixture.SkyrimGame, Fixture.Mo2Adapter);

    var first = await service.RegisterAsync(request);
    var second = await service.RegisterAsync(request);

    Equal(Mo2RegistrationStatus.Registered, first.Status);
    Equal(Mo2RegistrationStatus.Reused, second.Status);
    Equal(first.Reference!.Id, second.Reference!.Id);
    Equal(first.ProfileId, second.ProfileId);
    Equal(1, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("registration reports unresolved internal configuration without persistence", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    fixture.SelectProfile(portable, portable, "Active Profile");

    var result = await fixture.RegistrationService().RegisterAsync(new(
        portable, portable, "Different Profile", "Fixture", Fixture.SkyrimGame, Fixture.Mo2Adapter));

    False(result.Succeeded);
    Equal(Mo2RegistrationStatus.InstallationContextUnresolved, result.Status);
    Equal(Mo2RegistrationRecoveryDisposition.ConfigurationRequired, result.RecoveryDisposition);
    Contains(result.Issues, issue => issue.Code == "mo2.profile.selection_mismatch");
    Equal(0, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("registration authorizes only exact derived roots on its second validation pass", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var content = fixture.CreateContentRoot("registration-content");
    var instance = fixture.CreateGlobal("RegistrationGlobal", content);
    fixture.SelectProfile(instance, content, "Selected");

    var result = await fixture.RegistrationService().RegisterAsync(new(
        app, instance, "Selected", "Selected", Fixture.SkyrimGame, Fixture.Mo2Adapter));

    True(result.Succeeded);
    True(result.AuthorizedDerivedPaths.Length >= 5);
    True(result.AuthorizedDerivedPaths.All(path =>
        fixture.Paths.IsWithinRoot(path, content) || fixture.Paths.IsWithinRoot(path, fixture.GameRoot)));
});

await CheckAsync("registration rejects removable installation volumes", async () =>
{
    using var fixture = new Fixture();
    var portable = fixture.CreatePortable();
    fixture.SelectProfile(portable, portable, "UNDEFEATED");

    var result = await fixture.RegistrationService(_ => DriveType.Removable).RegisterAsync(new(
        portable, portable, "UNDEFEATED", "UNDEFEATED", Fixture.SkyrimGame, Fixture.Mo2Adapter));

    False(result.Succeeded);
    Equal(Mo2RegistrationStatus.InstallationContextUnresolved, result.Status);
    Equal(Mo2RegistrationRecoveryDisposition.ConfigurationRequired, result.RecoveryDisposition);
    Contains(result.Issues, issue => issue.Code == "mo2.volume.unsupported");
    Equal(0, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("path removal immediately before Connect fails without persistence", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("RemovedBeforeConnect");
    var request = await fixture.AuthorizedRequestAsync(app, instance);
    Directory.Delete(Path.Combine(instance, "profiles"), recursive: true);
    var service = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    var result = await service.ConnectAsync(new(request, Fixture.Mo2Adapter, "Removed"));
    False(result.Succeeded);
    Equal(0, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

await CheckAsync("connection persistence failures return a typed result", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("PersistenceFailure");
    var atomic = new ToggleAtomicStore(new Mo2FileSystem()) { FailWrites = true };
    var store = new Mo2InstallationReferenceStore(atomic, fixture.Paths, fixture.ReferenceStorePath);
    var service = new Mo2ConnectionService(fixture.Validator, store, fixture.Paths);
    var result = await service.ConnectAsync(new(await fixture.AuthorizedRequestAsync(app, instance), Fixture.Mo2Adapter, "Failure"));
    False(result.Succeeded);
    Contains(result.Issues, issue => issue.Code == "mo2.connection.persistence_failed");
});

await CheckAsync("reparse aliases canonicalize to an existing connection", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("AliasTarget");
    var appAlias = Path.Combine(fixture.Root, "app-alias");
    var instanceAlias = Path.Combine(fixture.GlobalRoot, "instance-alias");
    try
    {
        Directory.CreateSymbolicLink(appAlias, app);
        Directory.CreateSymbolicLink(instanceAlias, instance);
    }
    catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    {
        Console.WriteLine("SKIP symbolic-link creation is unavailable");
        return;
    }

    var service = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    True((await service.ConnectAsync(new(await fixture.AuthorizedRequestAsync(app, instance), Fixture.Mo2Adapter, "Target"))).Succeeded);
    var duplicate = await service.ConnectAsync(new(await fixture.AuthorizedRequestAsync(appAlias, instanceAlias), Fixture.Mo2Adapter, "Alias"));
    False(duplicate.Succeeded);
    Contains(duplicate.Issues, issue => issue.Code == "mo2.connection.duplicate");
});

await CheckAsync("display names are validated before filesystem validation", async () =>
{
    using var fixture = new Fixture();
    var service = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    var result = await service.ConnectAsync(new(new("missing", "missing", Fixture.SkyrimGame), Fixture.Mo2Adapter, "\u0001"));
    False(result.Succeeded);
    Contains(result.Issues, issue => issue.Code == "mo2.name.invalid");
});

await CheckAsync("discovery stays bounded and does not pair unrelated evidence", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Detected");
    var source = new StaticEvidenceSource([
        new(Mo2EvidenceKind.InstallerDefault, app, null, "app", 60),
        new(Mo2EvidenceKind.GlobalInstanceRoot, null, instance, "instance", 80),
    ]);
    var service = new Mo2DiscoveryService(source, fixture.ReferenceStore, fixture.Paths);
    var result = await service.DiscoverAsync(new(fixture.LocalAppData, Path.GetPathRoot(fixture.Root)!, Fixture.SkyrimGame));
    Equal(2, result.Candidates.Length);
    Contains(result.Candidates, candidate => candidate.ApplicationDirectory is not null && candidate.InstancePath is null);
    Contains(result.Candidates, candidate => candidate.ApplicationDirectory is null && candidate.InstancePath is not null);
});

await CheckAsync("Windows evidence enumerates only immediate global children", async () =>
{
    using var fixture = new Fixture();
    var immediate = fixture.CreateGlobal("Immediate");
    var nested = Path.Combine(fixture.GlobalRoot, "container", "Nested");
    Directory.CreateDirectory(nested);
    await File.WriteAllTextAsync(Path.Combine(nested, "ModOrganizer.ini"), "[General]");
    var source = new WindowsMo2EvidenceSource(new Mo2FileSystem(), fixture.Paths, new EmptyRegistry(), new EmptyShortcutResolver());
    var evidence = await source.FindAsync(new(fixture.LocalAppData, Path.GetPathRoot(fixture.Root)!, Fixture.SkyrimGame));
    Contains(evidence, item => item.InstancePath is not null && fixture.Paths.Equals(item.InstancePath, immediate));
    False(evidence.Any(item => item.InstancePath is not null && fixture.Paths.Equals(item.InstancePath, nested)));
});

await CheckAsync("Windows evidence uses current-instance and protocol breadcrumbs", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    File.WriteAllText(Path.Combine(app, "nxmhandler.exe"), "fixture");
    var instance = fixture.CreateGlobal("Current");
    var registry = new RegistryMap(new Dictionary<(string, string), string>
    {
        [("Software\\Mod Organizer Team\\Mod Organizer", "CurrentInstance")] = "Current",
        [("Software\\Classes\\nxm\\shell\\open\\command", "")] = $"\"{Path.Combine(app, "nxmhandler.exe")}\" \"%1\"",
    });
    var source = new WindowsMo2EvidenceSource(new Mo2FileSystem(), fixture.Paths, registry, new EmptyShortcutResolver());
    var evidence = await source.FindAsync(new(fixture.LocalAppData, Path.GetPathRoot(fixture.Root)!, Fixture.SkyrimGame));
    Contains(evidence, item => item.Kind == Mo2EvidenceKind.CurrentInstance && item.InstancePath is not null && fixture.Paths.Equals(item.InstancePath, instance));
    Contains(evidence, item => item.Kind == Mo2EvidenceKind.ProtocolHandler && item.ApplicationDirectory is not null && fixture.Paths.Equals(item.ApplicationDirectory, app));
});

await CheckAsync("moved connected instance remains persisted but revalidates incomplete", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Moved");
    var reference = fixture.Reference(app, instance, "Moved");
    await fixture.ReferenceStore.SaveAsync([reference]);
    Directory.Delete(instance, recursive: true);
    Equal(1, (await fixture.ReferenceStore.LoadAsync()).References.Length);
    var validation = await fixture.Validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));
    False(validation.CanConnect);
});

await CheckAsync("catalog projection is mixed, connected, and health-only", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Catalog");
    await fixture.ReferenceStore.SaveAsync([fixture.Reference(app, instance, "Catalog")]);
    var service = new Mo2CatalogService(
        new MockGridCatalogService(),
        fixture.ReferenceStore,
        _ => fixture.Validator);
    var catalog = await service.GetCatalogAsync();
    Equal(CatalogSourceKind.Mixed, catalog.SourceKind);
    var connected = catalog.Games.Single(game => game.Id == Fixture.SkyrimGame)
        .Installations.Single(installation => installation.Metadata.Provenance == InstallationProvenanceKind.ConnectedReference);
    Equal(WorkspaceFeature.Health, connected.Capabilities.Features);
    Equal(EnvironmentTabCapability.None, connected.Capabilities.EnvironmentTabs);
    Equal(ProfileFeature.None, connected.SupportedProfileFeatures);
    Equal(InstallationAvailability.Available, connected.Metadata.Availability);
    Equal(HealthLevel.Advisory, connected.Health.Level);
    True(connected.Metadata.StatusDetail.Contains("reauthorization", StringComparison.OrdinalIgnoreCase));
    True(connected.Metadata.StatusDetail.Contains("were not checked", StringComparison.OrdinalIgnoreCase));
    True(connected.Metadata.StatusDetail.Contains("session reauthorization", StringComparison.OrdinalIgnoreCase));
});

await CheckAsync("cancellation is honored", async () =>
{
    using var fixture = new Fixture();
    using var source = new CancellationTokenSource();
    source.Cancel();
    await ThrowsAsync<OperationCanceledException>(() => fixture.Validator.ValidateAsync(new(null, null, Fixture.SkyrimGame), source.Token));
    await ThrowsAsync<OperationCanceledException>(() => fixture.ReferenceStore.LoadAsync(source.Token));
});

await CheckAsync("canonicalizer rejects traversal and control characters", () =>
{
    var paths = new WindowsPathCanonicalizer();
    False(paths.TryCanonicalize("C:\\safe\\..\\unsafe", out _, out _));
    False(paths.TryCanonicalize("C:\\safe\u0001", out _, out _));
    return Task.CompletedTask;
});

await CheckAsync("canonicalizer rejects relative UNC device and mapped-network paths", () =>
{
    var paths = new WindowsPathCanonicalizer();
    False(paths.TryCanonicalize("relative\\MO2", out _, out _));
    False(paths.TryCanonicalize("\\\\server\\share\\MO2", out _, out _));
    False(paths.TryCanonicalize("\\\\?\\C:\\MO2", out _, out _));
    False(paths.TryCanonicalize("\\\\.\\C:\\MO2", out _, out _));

    var mappedNetworkPaths = new WindowsPathCanonicalizer(_ => DriveType.Network);
    False(mappedNetworkPaths.TryCanonicalize(Path.GetTempPath(), out _, out _));
    return Task.CompletedTask;
});

await CheckAsync("current-instance registry evidence cannot escape its immediate root", async () =>
{
    using var fixture = new Fixture();
    var escaped = Path.Combine(fixture.LocalAppData, "Escaped");
    Directory.CreateDirectory(escaped);
    await File.WriteAllTextAsync(Path.Combine(escaped, "ModOrganizer.ini"), "[General]");
    var registry = new RegistryMap(new Dictionary<(string, string), string>
    {
        [("Software\\Mod Organizer Team\\Mod Organizer", "CurrentInstance")] = "..\\Escaped",
    });
    var source = new WindowsMo2EvidenceSource(
        new Mo2FileSystem(),
        fixture.Paths,
        registry,
        new EmptyShortcutResolver());
    var evidence = await source.FindAsync(new(
        fixture.LocalAppData,
        Path.GetPathRoot(fixture.Root)!,
        Fixture.SkyrimGame));
    False(evidence.Any(item => item.Kind == Mo2EvidenceKind.CurrentInstance));
});

await CheckAsync("remote protocol and handler evidence is rejected before probing", async () =>
{
    using var fixture = new Fixture();
    var handlerPath = Path.Combine(fixture.GlobalRoot, "downloadhandler.ini");
    await File.WriteAllTextAsync(handlerPath, "path=\\\\server\\share\\ModOrganizer.exe");
    var registry = new RegistryMap(new Dictionary<(string, string), string>
    {
        [("Software\\Classes\\nxm\\shell\\open\\command", "")] = "\"\\\\server\\share\\ModOrganizer.exe\" \"%1\"",
    });
    var tracking = new TrackingFileSystem(new Mo2FileSystem());
    var source = new WindowsMo2EvidenceSource(
        tracking,
        fixture.Paths,
        registry,
        new EmptyShortcutResolver());
    await source.FindAsync(new(
        fixture.LocalAppData,
        Path.GetPathRoot(fixture.Root)!,
        Fixture.SkyrimGame));
    False(tracking.ProbedPaths.Any(path => path.StartsWith("\\\\", StringComparison.Ordinal)));
});

await CheckAsync("read-only filesystem enforces the byte limit before decoding", async () =>
{
    using var fixture = new Fixture();
    var path = Path.Combine(fixture.Root, "bounded.txt");
    await File.WriteAllTextAsync(path, new string('x', 17));
    await ThrowsAsync<InvalidDataException>(() =>
        new Mo2FileSystem().ReadTextAsync(path, 16, CancellationToken.None));
});

await CheckAsync("reference store rejects unknown instance kinds", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("InvalidKind");
    var reference = fixture.Reference(app, instance, "Invalid kind") with
    {
        InstanceKind = (Mo2InstanceKind)99,
    };
    await ThrowsAsync<InvalidDataException>(() => fixture.ReferenceStore.SaveAsync([reference]));
});

await CheckAsync("catalog revision changes when connected validation state changes", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("Revision");
    await fixture.ReferenceStore.SaveAsync([fixture.Reference(app, instance, "Revision")]);
    var service = new Mo2CatalogService(
        new MockGridCatalogService(),
        fixture.ReferenceStore,
        _ => fixture.Validator);
    var available = await service.GetCatalogAsync();
    Directory.Delete(Path.Combine(instance, "profiles"), recursive: true);
    var incomplete = await service.GetCatalogAsync();
    False(string.Equals(available.Revision, incomplete.Revision, StringComparison.Ordinal));
    False(available.Revision.Contains(fixture.Root, StringComparison.OrdinalIgnoreCase));
});

await CheckAsync("configured paths outside selected roots require authorization without probing", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var content = fixture.CreateContentRoot("authorization-content");
    var instance = fixture.CreateGlobal("Authorization", content);
    var tracking = new TrackingFileSystem(new Mo2FileSystem());
    var validator = new Mo2InstallationValidator(
        tracking,
        new Mo2IniReader(tracking),
        fixture.Paths,
        fixture.GlobalRoot);

    var first = await validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));
    Equal(Mo2ValidationStatus.Incomplete, first.Status);
    False(first.CanConnect);
    var authorizationPaths = first.Paths
        .Where(path => path.State == Mo2PathState.AuthorizationRequired)
        .ToImmutableArray();
    Equal(8, authorizationPaths.Length);
    True(authorizationPaths.All(path => path.CanonicalPath is not null && Path.IsPathFullyQualified(path.CanonicalPath)));
    True(authorizationPaths.All(path => !tracking.ProbedPaths.Any(probed => fixture.Paths.Equals(probed, path.CanonicalPath!))));
    Contains(first.Issues, issue => issue.Code == "mo2.path.authorization_required");

    var authorized = new Mo2ValidationRequest(
        app,
        instance,
        Fixture.SkyrimGame,
        authorizationPaths.Select(path => path.CanonicalPath!).ToImmutableArray());
    var second = await validator.ValidateAsync(authorized);
    Equal(Mo2ValidationStatus.Valid, second.Status);
    True(second.CanConnect);
    False(second.Paths.Any(path => path.State == Mo2PathState.AuthorizationRequired));
});

await CheckAsync("configured paths are not physically canonicalized before authorization", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var content = fixture.CreateContentRoot("lexical-authorization-content");
    var instance = fixture.CreateGlobal("LexicalAuthorization", content);
    var guardedPaths = new PhysicalCanonicalizationGuard(
        fixture.Paths,
        [content, fixture.GameRoot]);
    var fileSystem = new Mo2FileSystem();
    var validator = new Mo2InstallationValidator(
        fileSystem,
        new Mo2IniReader(fileSystem),
        guardedPaths,
        fixture.GlobalRoot);

    var validation = await validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));

    Equal(Mo2ValidationStatus.Incomplete, validation.Status);
    Equal(8, validation.Paths.Count(path => path.State == Mo2PathState.AuthorizationRequired));
    Equal(0, guardedPaths.BlockedPhysicalCanonicalizationAttempts);
});

await CheckAsync("configured-path authorization is exact and not recursive", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("ExactAuthorization");
    var first = await fixture.Validator.ValidateAsync(new(app, instance, Fixture.SkyrimGame));
    var gameDirectory = first.Paths.Single(path => path.Label == "Game directory").CanonicalPath!;
    var result = await fixture.Validator.ValidateAsync(new(
        app,
        instance,
        Fixture.SkyrimGame,
        [gameDirectory]));

    Equal(Mo2PathState.Present, result.Paths.Single(path => path.Label == "Game directory").State);
    Equal(Mo2PathState.AuthorizationRequired, result.Paths.Single(path => path.Label == "Skyrim executable").State);
    Equal(Mo2PathState.AuthorizationRequired, result.Paths.Single(path => path.Label == "Skyrim Data directory").State);
    False(result.CanConnect);
});

await CheckAsync("changed configured paths require fresh authorization and are not probed", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("ChangedAuthorization");
    var authorized = await fixture.AuthorizedRequestAsync(app, instance);
    True((await fixture.Validator.ValidateAsync(authorized)).CanConnect);

    var changedGame = Path.Combine(fixture.Root, "ChangedGame");
    Directory.CreateDirectory(Path.Combine(changedGame, "Data"));
    await File.WriteAllTextAsync(Path.Combine(changedGame, "SkyrimSE.exe"), "fixture");
    var iniPath = Path.Combine(instance, "ModOrganizer.ini");
    var ini = await File.ReadAllTextAsync(iniPath);
    ini = ini.Replace(
        fixture.GameRoot.Replace('\\', '/'),
        changedGame.Replace('\\', '/'),
        StringComparison.Ordinal);
    await File.WriteAllTextAsync(iniPath, ini);

    var tracking = new TrackingFileSystem(new Mo2FileSystem());
    var validator = new Mo2InstallationValidator(
        tracking,
        new Mo2IniReader(tracking),
        fixture.Paths,
        fixture.GlobalRoot);
    var changed = await validator.ValidateAsync(authorized);
    var changedAuthorizations = changed.Paths
        .Where(path => path.State == Mo2PathState.AuthorizationRequired)
        .ToImmutableArray();
    Equal(3, changedAuthorizations.Length);
    True(changedAuthorizations.All(path => path.CanonicalPath!.StartsWith(changedGame, StringComparison.OrdinalIgnoreCase)));
    True(changedAuthorizations.All(path => !tracking.ProbedPaths.Any(probed => fixture.Paths.Equals(probed, path.CanonicalPath!))));
    False(changed.CanConnect);
});

await CheckAsync("connection revalidation uses the bound configured-path authorization", async () =>
{
    using var fixture = new Fixture();
    var app = fixture.CreateApplication();
    var instance = fixture.CreateGlobal("ConnectionAuthorization");
    var service = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
    var unauthorized = await service.ConnectAsync(new(
        new(app, instance, Fixture.SkyrimGame),
        Fixture.Mo2Adapter,
        "Unauthorized"));
    False(unauthorized.Succeeded);
    Contains(unauthorized.Validation.Paths, path => path.State == Mo2PathState.AuthorizationRequired);
    Equal(0, (await fixture.ReferenceStore.LoadAsync()).References.Length);

    var authorized = await service.ConnectAsync(new(
        await fixture.AuthorizedRequestAsync(app, instance),
        Fixture.Mo2Adapter,
        "Authorized"));
    True(authorized.Succeeded);
    Equal(1, (await fixture.ReferenceStore.LoadAsync()).References.Length);
});

foreach (var profileCheck in await Mo2ProfileChecks.RunAsync())
{
    checks++;
    if (profileCheck.Failure is null)
    {
        Console.WriteLine($"PASS {profileCheck.Name}");
    }
    else
    {
        failures.Add($"{profileCheck.Name}: {profileCheck.Failure.GetType().Name}: {profileCheck.Failure.Message}");
        Console.WriteLine($"FAIL {profileCheck.Name}");
    }
}

foreach (var inventoryCheck in await Mo2ModInventoryChecks.RunAsync())
{
    checks++;
    if (inventoryCheck.Failure is null)
    {
        Console.WriteLine($"PASS {inventoryCheck.Name}");
    }
    else
    {
        failures.Add($"{inventoryCheck.Name}: {inventoryCheck.Failure.GetType().Name}: {inventoryCheck.Failure.Message}");
        Console.WriteLine($"FAIL {inventoryCheck.Name}");
    }
}

foreach (var resolvedCheck in await Mo2ResolvedStateChecks.RunAsync())
{
    checks++;
    if (resolvedCheck.Failure is null)
    {
        Console.WriteLine($"PASS {resolvedCheck.Name}");
    }
    else
    {
        failures.Add($"{resolvedCheck.Name}: {resolvedCheck.Failure.GetType().Name}: {resolvedCheck.Failure.Message}");
        Console.WriteLine($"FAIL {resolvedCheck.Name}");
    }
}

foreach (var assetCheck in await Mo2AssetInspectionChecks.RunAsync())
{
    checks++;
    if (assetCheck.Failure is null)
    {
        Console.WriteLine($"PASS {assetCheck.Name}");
    }
    else
    {
        failures.Add($"{assetCheck.Name}: {assetCheck.Failure.GetType().Name}: {assetCheck.Failure.Message}");
        Console.WriteLine($"FAIL {assetCheck.Name}");
    }
}

foreach (var recordGraphCheck in await Mo2Tes4RecordGraphChecks.RunAsync())
{
    checks++;
    if (recordGraphCheck.Failure is null)
    {
        Console.WriteLine($"PASS {recordGraphCheck.Name}");
    }
    else
    {
        failures.Add($"{recordGraphCheck.Name}: {recordGraphCheck.Failure.GetType().Name}: {recordGraphCheck.Failure.Message}");
        Console.WriteLine($"FAIL {recordGraphCheck.Name}");
    }
}

foreach (var scriptDependencyCheck in await Mo2PluginScriptDependencyChecks.RunAsync())
{
    checks++;
    if (scriptDependencyCheck.Failure is null)
    {
        Console.WriteLine($"PASS {scriptDependencyCheck.Name}");
    }
    else
    {
        failures.Add($"{scriptDependencyCheck.Name}: {scriptDependencyCheck.Failure.GetType().Name}: {scriptDependencyCheck.Failure.Message}");
        Console.WriteLine($"FAIL {scriptDependencyCheck.Name}");
    }
}

foreach (var baselineCheck in await Mo2BaselineChecks.RunAsync())
{
    checks++;
    if (baselineCheck.Failure is null)
    {
        Console.WriteLine($"PASS {baselineCheck.Name}");
    }
    else
    {
        failures.Add($"{baselineCheck.Name}: {baselineCheck.Failure.GetType().Name}: {baselineCheck.Failure.Message}");
        Console.WriteLine($"FAIL {baselineCheck.Name}");
    }
}

foreach (var archiveCheck in await Mo2RepairArchiveChecks.RunAsync())
{
    checks++;
    if (archiveCheck.Failure is null)
    {
        Console.WriteLine($"PASS {archiveCheck.Name}");
    }
    else
    {
        failures.Add($"{archiveCheck.Name}: {archiveCheck.Failure.GetType().Name}: {archiveCheck.Failure.Message}");
        Console.WriteLine($"FAIL {archiveCheck.Name}");
    }
}

foreach (var executableCheck in await Mo2ExecutableChecks.RunAsync())
{
    checks++;
    if (executableCheck.Failure is null)
    {
        Console.WriteLine($"PASS {executableCheck.Name}");
    }
    else
    {
        failures.Add($"{executableCheck.Name}: {executableCheck.Failure.GetType().Name}: {executableCheck.Failure.Message}");
        Console.WriteLine($"FAIL {executableCheck.Name}");
    }
}

foreach (var outputCheck in await Mo2ToolOutputChecks.RunAsync())
{
    checks++;
    if (outputCheck.Failure is null)
    {
        Console.WriteLine($"PASS {outputCheck.Name}");
    }
    else
    {
        failures.Add($"{outputCheck.Name}: {outputCheck.Failure.GetType().Name}: {outputCheck.Failure.Message}");
        Console.WriteLine($"FAIL {outputCheck.Name}");
    }
}

foreach (var onboardingCheck in await Mo2OnboardingChecks.RunAsync())
{
    checks++;
    if (onboardingCheck.Failure is null) Console.WriteLine($"PASS {onboardingCheck.Name}");
    else { failures.Add($"{onboardingCheck.Name}: {onboardingCheck.Failure.GetType().Name}: {onboardingCheck.Failure.Message}"); Console.WriteLine($"FAIL {onboardingCheck.Name}"); }
}

foreach (var auditCheck in await Mo2FidelityAuditChecks.RunAsync())
{
    checks++;
    if (auditCheck.Failure is null) Console.WriteLine($"PASS {auditCheck.Name}");
    else { failures.Add($"{auditCheck.Name}: {auditCheck.Failure.GetType().Name}: {auditCheck.Failure.Message}"); Console.WriteLine($"FAIL {auditCheck.Name}"); }
}

foreach (var launchCheck in await Mo2LaunchChecks.RunAsync())
{
    checks++;
    if (launchCheck.Failure is null) Console.WriteLine($"PASS {launchCheck.Name}");
    else { failures.Add($"{launchCheck.Name}: {launchCheck.Failure.GetType().Name}: {launchCheck.Failure.Message}"); Console.WriteLine($"FAIL {launchCheck.Name}"); }
}

if (args.Contains("--benchmark", StringComparer.OrdinalIgnoreCase))
{
    await CheckAsync("resolved-state large synthetic benchmark completes within safety bounds", async () =>
    {
        var result = await Mo2ResolvedStateBenchmark.RunAsync();
        Console.WriteLine(
            $"BENCH resolved-state: {result.Mods:N0} mods, {result.LooseEntries:N0} loose entries, " +
            $"{result.ArchiveMembers:N0} archive members, {result.PluginHeaders:N0} plugin headers in {result.Elapsed.TotalSeconds:F3}s; " +
            $"{result.EntriesPerSecond:N0} entries/s; {result.AllocatedBytes / (1024d * 1024d):F1} MiB allocated.");
    });
}

if (failures.Count == 0)
{
    Console.WriteLine($"All {checks} Grid.Mo2 checks passed.");
    return 0;
}

Console.Error.WriteLine($"{failures.Count} of {checks} checks failed:");
foreach (var failure in failures)
{
    Console.Error.WriteLine($"- {failure}");
}

return 1;

async Task CheckAsync(string name, Func<Task> action)
{
    checks++;
    try
    {
        await action();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.GetType().Name}: {exception.Message}");
        Console.WriteLine($"FAIL {name}");
    }
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value) => True(!value);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
{
    if (!values.Any(predicate)) throw new InvalidOperationException("Expected matching item.");
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
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

sealed class Fixture : IDisposable
{
    public static readonly GameId SkyrimGame = new("game.skyrim-special-edition");
    public static readonly GameAdapterId Mo2Adapter = new("adapter.mod-organizer-2");
    private int sequence;

    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-tests-{Guid.NewGuid():N}");
        LocalAppData = Path.Combine(Root, "LocalAppData");
        GlobalRoot = Path.Combine(LocalAppData, "ModOrganizer");
        GameRoot = Path.Combine(Root, "Game");
        ReferenceStorePath = Path.Combine(Root, "Grid", "mo2-references.json");
        Directory.CreateDirectory(GlobalRoot);
        Paths = new WindowsPathCanonicalizer();
        var fileSystem = new Mo2FileSystem();
        Validator = new Mo2InstallationValidator(fileSystem, new Mo2IniReader(fileSystem), Paths, GlobalRoot);
        ReferenceStore = new Mo2InstallationReferenceStore(fileSystem, Paths, ReferenceStorePath);
    }

    public string Root { get; }
    public string LocalAppData { get; }
    public string GlobalRoot { get; }
    public string GameRoot { get; }
    public string ReferenceStorePath { get; }
    public WindowsPathCanonicalizer Paths { get; }
    public Mo2InstallationValidator Validator { get; }
    public Mo2InstallationReferenceStore ReferenceStore { get; }

    public string CreateApplication(string name = "MO2")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "ModOrganizer.exe"), "fixture");
        return path;
    }

    public string CreatePortable(bool includePortableLock = false)
    {
        var path = CreateApplication($"Portable-{++sequence}");
        CreateGame();
        CreateContentDirectories(path);
        WriteIni(path, path);
        if (includePortableLock) File.WriteAllText(Path.Combine(path, "portable.txt"), string.Empty);
        return path;
    }

    public string CreateGlobal(string name, string? contentRoot = null)
    {
        CreateGame();
        var instance = Path.Combine(GlobalRoot, name);
        Directory.CreateDirectory(instance);
        var content = contentRoot ?? instance;
        CreateContentDirectories(content);
        WriteIni(instance, content);
        return instance;
    }

    public string CreateContentRoot(string name)
    {
        var path = Path.Combine(Root, name);
        CreateContentDirectories(path);
        return path;
    }

    public string CreateGame()
    {
        Directory.CreateDirectory(Path.Combine(GameRoot, "Data"));
        File.WriteAllText(Path.Combine(GameRoot, "SkyrimSE.exe"), "fixture");
        return GameRoot;
    }

    public Mo2InstallationReference Reference(string app, string instance, string name, int? unique = null)
    {
        var key = Paths.GetIdentityKey(app, instance);
        var suffix = unique is null ? key[..24] : $"{unique.Value:D24}";
        return new(
            Mo2InstallationReference.CurrentSchemaVersion,
            new($"reference.mo2.{suffix}"),
            new($"installation.mo2.{suffix}"),
            SkyrimGame,
            Mo2Adapter,
            name,
            Paths.Equals(app, instance) ? Mo2InstanceKind.Portable : Mo2InstanceKind.Global,
            Path.Combine(app, "ModOrganizer.exe"),
            instance);
    }

    public Mo2RegistrationService RegistrationService(Func<string, DriveType>? driveType = null)
    {
        var fileSystem = new Mo2FileSystem();
        return new(
            Validator,
            new Mo2ConnectionService(Validator, ReferenceStore, Paths),
            ReferenceStore,
            fileSystem,
            new Mo2TextDecoder(),
            Paths,
            driveType);
    }

    public void SelectProfile(string instance, string contentRoot, string profileName)
    {
        Directory.CreateDirectory(Path.Combine(contentRoot, "profiles", profileName));
        var iniPath = Path.Combine(instance, "ModOrganizer.ini");
        var ini = File.ReadAllText(iniPath);
        ini = ini.Replace("[General]", $"[General]{Environment.NewLine}selected_profile={profileName}", StringComparison.Ordinal);
        File.WriteAllText(iniPath, ini);
    }

    public async Task<Mo2ValidationRequest> AuthorizedRequestAsync(
        string? applicationDirectory,
        string? instanceDirectory,
        GameId? expectedGameId = null)
    {
        var request = new Mo2ValidationRequest(
            applicationDirectory,
            instanceDirectory,
            expectedGameId ?? SkyrimGame);
        var first = await Validator.ValidateAsync(request);
        return request with
        {
            AuthorizedConfiguredPaths = first.Paths
                .Where(path => path.State == Mo2PathState.AuthorizationRequired && path.CanonicalPath is not null)
                .Select(path => path.CanonicalPath!)
                .ToImmutableArray(),
        };
    }

    public async Task<Mo2InstallationValidation> ValidateAuthorizedAsync(
        string? applicationDirectory,
        string? instanceDirectory,
        GameId? expectedGameId = null) =>
        await Validator.ValidateAsync(await AuthorizedRequestAsync(applicationDirectory, instanceDirectory, expectedGameId));

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }

    private void WriteIni(string instance, string content)
    {
        var normalizedContent = content.Replace('\\', '/');
        var normalizedGame = GameRoot.Replace('\\', '/');
        File.WriteAllText(
            Path.Combine(instance, "ModOrganizer.ini"),
            $"""
            [General]
            gameName=Skyrim Special Edition
            gamePath={normalizedGame}
            [Settings]
            base_directory={normalizedContent}
            mod_directory=%BASE_DIR%/mods
            profiles_directory=%BASE_DIR%/profiles
            download_directory=%BASE_DIR%/downloads
            overwrite_directory=%BASE_DIR%/overwrite
            """);
    }

    private static void CreateContentDirectories(string root)
    {
        foreach (var name in new[] { "mods", "profiles", "downloads", "overwrite" })
        {
            Directory.CreateDirectory(Path.Combine(root, name));
        }
    }
}

sealed class InaccessibleFileSystem(
    IMo2ReadOnlyFileSystem inner,
    string deniedPath,
    IMo2PathCanonicalizer paths) : IMo2ReadOnlyFileSystem
{
    public Mo2PathState ProbeFile(string path) => paths.Equals(path, deniedPath) ? Mo2PathState.Inaccessible : inner.ProbeFile(path);
    public Mo2PathState ProbeDirectory(string path) => paths.Equals(path, deniedPath) ? Mo2PathState.Inaccessible : inner.ProbeDirectory(path);
    public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);
    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) => inner.EnumerateDirectoriesWithState(path);
    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);
    public Mo2FileMetadata GetFileMetadata(string path) => paths.Equals(path, deniedPath) ? new(Mo2PathState.Inaccessible, null) : inner.GetFileMetadata(path);
    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) => inner.ReadBytesAsync(path, maximumBytes, cancellationToken);
    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) => inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) => inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}

sealed class StaticEvidenceSource(ImmutableArray<Mo2DiscoveryEvidence> evidence) : IWindowsMo2EvidenceSource
{
    public Task<ImmutableArray<Mo2DiscoveryEvidence>> FindAsync(Mo2DiscoveryOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(evidence);
    }
}

sealed class StaticReferenceStore(Mo2ReferenceLoadResult result) : IMo2InstallationReferenceStore
{
    public Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    public Task SaveAsync(
        ImmutableArray<Mo2InstallationReference> references,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

sealed class EmptyRegistry : IWindowsRegistryReader
{
    public string? ReadCurrentUserDefaultValue(string subKeyPath) => null;
    public string? ReadCurrentUserValue(string subKeyPath, string valueName) => null;
}

sealed class RegistryMap(IReadOnlyDictionary<(string Path, string Name), string> values) : IWindowsRegistryReader
{
    public string? ReadCurrentUserDefaultValue(string subKeyPath) => ReadCurrentUserValue(subKeyPath, string.Empty);
    public string? ReadCurrentUserValue(string subKeyPath, string valueName) =>
        values.TryGetValue((subKeyPath, valueName), out var value) ? value : null;
}

sealed class EmptyShortcutResolver : IWindowsShortcutResolver
{
    public string? TryResolveTarget(string shortcutPath) => null;
}

sealed class ToggleAtomicStore(IGridAtomicFileStore inner) : IGridAtomicFileStore
{
    public bool FailWrites { get; set; }
    public bool Exists(string path) => inner.Exists(path);
    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadTextAsync(path, maximumBytes, cancellationToken);
    public Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken) =>
        FailWrites
            ? Task.FromException(new IOException("Synthetic atomic-write failure."))
            : inner.WriteAtomicallyAsync(path, content, cancellationToken);
    public Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken) =>
        inner.DeleteIfExistsAsync(path, cancellationToken);
}

sealed class TrackingFileSystem(IMo2ReadOnlyFileSystem inner) : IMo2ReadOnlyFileSystem
{
    public List<string> ProbedPaths { get; } = [];

    public Mo2PathState ProbeFile(string path)
    {
        ProbedPaths.Add(path);
        return inner.ProbeFile(path);
    }

    public Mo2PathState ProbeDirectory(string path)
    {
        ProbedPaths.Add(path);
        return inner.ProbeDirectory(path);
    }

    public IReadOnlyList<string> EnumerateDirectories(string path) => inner.EnumerateDirectories(path);

    public Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path) => inner.EnumerateDirectoriesWithState(path);

    public IReadOnlyList<string> EnumerateFiles(string path) => inner.EnumerateFiles(path);

    public Mo2FileMetadata GetFileMetadata(string path) => inner.GetFileMetadata(path);

    public Task<ImmutableArray<byte>> ReadBytesAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesAsync(path, maximumBytes, cancellationToken);

    public Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken);

    public Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken) =>
        inner.ReadTextAsync(path, maximumBytes, cancellationToken);
}

sealed class PhysicalCanonicalizationGuard(
    IMo2PathCanonicalizer inner,
    ImmutableArray<string> protectedRoots) : IMo2PathCanonicalizer
{
    public int BlockedPhysicalCanonicalizationAttempts { get; private set; }

    public bool TryNormalizeLexically(string path, out string normalizedPath, out string? error) =>
        inner.TryNormalizeLexically(path, out normalizedPath, out error);

    public bool TryCanonicalize(string path, out string canonicalPath, out string? error)
    {
        if (inner.TryNormalizeLexically(path, out var normalized, out _) &&
            protectedRoots.Any(root => IsLexicallyWithinRoot(normalized, root)))
        {
            BlockedPhysicalCanonicalizationAttempts++;
            throw new InvalidOperationException("Configured external path was physically canonicalized before authorization.");
        }

        return inner.TryCanonicalize(path, out canonicalPath, out error);
    }

    public bool Equals(string left, string right) => inner.Equals(left, right);
    public bool IsImmediateChildOf(string child, string parent) => inner.IsImmediateChildOf(child, parent);
    public bool IsWithinRoot(string child, string parent) => inner.IsWithinRoot(child, parent);
    public string GetIdentityKey(params string[] paths) => inner.GetIdentityKey(paths);

    private static bool IsLexicallyWithinRoot(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
