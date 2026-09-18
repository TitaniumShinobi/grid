using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed class Mo2ProfileFixtureBuilder : IDisposable
{
    private static readonly GameId SkyrimGame = new("game.skyrim-special-edition");
    private static readonly GameAdapterId Mo2Adapter = new("adapter.mod-organizer-2");

    private Mo2ProfileFixtureBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-profile-tests-{Guid.NewGuid():N}");
        ApplicationRoot = Path.Combine(Root, "MO2");
        InstanceRoot = Path.Combine(Root, "LocalAppData", "ModOrganizer", "Fixture");
        ProfilesRoot = Path.Combine(InstanceRoot, "profiles");
        GameRoot = Path.Combine(Root, "Skyrim");
        Directory.CreateDirectory(ApplicationRoot);
        Directory.CreateDirectory(ProfilesRoot);
        Directory.CreateDirectory(Path.Combine(InstanceRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(InstanceRoot, "downloads"));
        Directory.CreateDirectory(Path.Combine(InstanceRoot, "overwrite"));
        Directory.CreateDirectory(Path.Combine(GameRoot, "Data"));
        File.WriteAllText(Path.Combine(ApplicationRoot, "ModOrganizer.exe"), "fixture");
        File.WriteAllText(Path.Combine(GameRoot, "SkyrimSE.exe"), "fixture");
        FileSystem = new Mo2FileSystem();
        Paths = new WindowsPathCanonicalizer();
        Authorization = new Mo2SessionPathAuthorization(Paths);
        WriteInstanceIni(null);
    }

    public string Root { get; }
    public string ApplicationRoot { get; }
    public string InstanceRoot { get; }
    public string ProfilesRoot { get; }
    public string GameRoot { get; }
    public Mo2FileSystem FileSystem { get; }
    public WindowsPathCanonicalizer Paths { get; }
    public Mo2SessionPathAuthorization Authorization { get; }
    public Mo2InstallationReference Reference { get; private set; } = null!;
    public Mo2InstallationValidation Validation { get; private set; } = null!;

    public static async Task<Mo2ProfileFixtureBuilder> CreateAsync()
    {
        var fixture = new Mo2ProfileFixtureBuilder();
        var validator = new Mo2InstallationValidator(
            fixture.FileSystem,
            new Mo2IniReader(fixture.FileSystem),
            fixture.Paths,
            Path.GetDirectoryName(fixture.InstanceRoot)!);
        var firstRequest = new Mo2ValidationRequest(fixture.ApplicationRoot, fixture.InstanceRoot, SkyrimGame);
        var first = await validator.ValidateAsync(firstRequest);
        var authorized = firstRequest with
        {
            AuthorizedConfiguredPaths = first.Paths
                .Where(path => path.State == Mo2PathState.AuthorizationRequired && path.CanonicalPath is not null)
                .Select(path => path.CanonicalPath!)
                .ToImmutableArray(),
        };
        fixture.Validation = await validator.ValidateAsync(authorized);
        if (!fixture.Validation.CanConnect)
        {
            fixture.Dispose();
            throw new InvalidOperationException("Synthetic MO2 profile fixture did not validate.");
        }

        var key = fixture.Validation.ConnectionKey!;
        fixture.Reference = new(
            Mo2InstallationReference.CurrentSchemaVersion,
            new($"reference.mo2.{key[..24]}"),
            new($"installation.mo2.{key[..24]}"),
            SkyrimGame,
            Mo2Adapter,
            "Profile fixture",
            Mo2InstanceKind.Global,
            fixture.Validation.ExecutablePath!,
            fixture.Validation.InstanceDirectory!);
        return fixture;
    }

    public string CreateProfile(
        string name,
        string modList = "+High Priority\r\n-Low Priority\n*Foreign Entry\rUnmarked Entry\n+Section_separator\n+section_SEPARATOR",
        string plugins = "Skyrim.esm\nUpdate.esm\n*LiteralName.esp\n",
        string loadOrder = "update.ESM\nSkyrim.esm\nMissing.esp\nLiteralName.esp\n",
        string? settings = "[General]\nLocalSaves=true\nLocalSettings=false\n",
        bool includeSkyrimInis = true)
    {
        var profile = Path.Combine(ProfilesRoot, name);
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "modlist.txt"), modList);
        File.WriteAllText(Path.Combine(profile, "plugins.txt"), plugins);
        File.WriteAllText(Path.Combine(profile, "loadorder.txt"), loadOrder);
        if (settings is not null)
        {
            File.WriteAllText(Path.Combine(profile, "settings.ini"), settings);
        }

        if (includeSkyrimInis)
        {
            File.WriteAllBytes(Path.Combine(profile, "skyrim.ini"), [0xFF, 0xFE, 0x5B, 0x00, 0x47, 0x00, 0x5D, 0x00]);
            File.WriteAllText(Path.Combine(profile, "skyrimprefs.ini"), "[Display]\r\n");
        }

        return profile;
    }

    public void SelectProfile(string? name) => WriteInstanceIni(name);

    public Mo2ProfileSnapshotService CreateSnapshotService(
        IMo2ReadOnlyFileSystem? fileSystem = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var source = fileSystem ?? FileSystem;
        return new(
            source,
            new Mo2TextDecoder(),
            Paths,
            Authorization,
            utcNow);
    }

    public Mo2ProfileSnapshotRequest Request() => new(Reference, Validation);

    public void Authorize() => Authorization.AuthorizeProfilesRoot(Reference.Id, ProfilesRoot);

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private void WriteInstanceIni(string? selectedProfile)
    {
        var selected = selectedProfile is null ? string.Empty : $"selected_profile={selectedProfile}\n";
        File.WriteAllText(
            Path.Combine(InstanceRoot, "ModOrganizer.ini"),
            $"""
            [General]
            gameName=Skyrim Special Edition
            gamePath={GameRoot.Replace('\\', '/')}
            {selected}[Settings]
            base_directory={InstanceRoot.Replace('\\', '/')}
            mod_directory=%BASE_DIR%/mods
            profiles_directory=%BASE_DIR%/profiles
            download_directory=%BASE_DIR%/downloads
            overwrite_directory=%BASE_DIR%/overwrite
            """);
    }
}
