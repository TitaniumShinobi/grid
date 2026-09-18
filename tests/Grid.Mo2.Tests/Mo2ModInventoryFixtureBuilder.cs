using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed class Mo2ModInventoryFixtureBuilder : IDisposable
{
    private readonly Mo2ProfileFixtureBuilder profiles;

    private Mo2ModInventoryFixtureBuilder(Mo2ProfileFixtureBuilder profiles)
    {
        this.profiles = profiles;
        ModsRoot = Path.Combine(profiles.InstanceRoot, "mods");
        Authorization = new Mo2SessionModsPathAuthorization(profiles.Paths);
    }

    public string Root => profiles.Root;
    public string InstanceRoot => profiles.InstanceRoot;
    public string ModsRoot { get; }
    public Mo2InstallationReference Reference => profiles.Reference;
    public Mo2InstallationValidation Validation => profiles.Validation;
    public Mo2FileSystem FileSystem => profiles.FileSystem;
    public WindowsPathCanonicalizer Paths => profiles.Paths;
    public Mo2SessionModsPathAuthorization Authorization { get; }

    public static async Task<Mo2ModInventoryFixtureBuilder> CreateAsync() =>
        new(await Mo2ProfileFixtureBuilder.CreateAsync());

    public string CreateProfile(string name, string modList) =>
        profiles.CreateProfile(name, modList: modList);

    public string CreateMod(string name, string? metadata = null)
    {
        var directory = Path.Combine(ModsRoot, name);
        Directory.CreateDirectory(directory);
        if (metadata is not null)
        {
            File.WriteAllText(Path.Combine(directory, "meta.ini"), metadata);
        }

        return directory;
    }

    public void WriteCategories(string content) =>
        File.WriteAllText(Path.Combine(InstanceRoot, "categories.dat"), content);

    public Mo2ModInventoryService CreateService(
        IMo2InventoryFileSystem? fileSystem = null,
        Func<DateTimeOffset>? utcNow = null) =>
        new(
            fileSystem ?? FileSystem,
            new Mo2TextDecoder(),
            Paths,
            Authorization,
            utcNow);

    public async Task<Mo2ProfileSnapshot> ObserveProfilesAsync() =>
        await profiles.CreateSnapshotService().ObserveAsync(profiles.Request());

    public async Task<Mo2ModInventorySnapshot> ObserveAsync(
        Mo2InstallationValidation? validation = null,
        IMo2InventoryFileSystem? fileSystem = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var snapshot = await ObserveProfilesAsync();
        return await CreateService(fileSystem, utcNow).ObserveAsync(new(
            Reference,
            validation ?? Validation,
            snapshot));
    }

    public void Dispose() => profiles.Dispose();
}
