using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed class Mo2ToolOutputFixtureBuilder : IDisposable
{
    private readonly Mo2ModInventoryFixtureBuilder inventory;
    private readonly Mo2SessionContentPathAuthorization contentAuthorization;

    private Mo2ToolOutputFixtureBuilder(Mo2ModInventoryFixtureBuilder inventory)
    {
        this.inventory = inventory;
        contentAuthorization = new(inventory.Paths);
    }

    public string Root => inventory.Root;

    public string OverwriteRoot => inventory.Validation.OverwriteDirectory!;

    public Mo2InstallationReference Reference => inventory.Reference;

    public Mo2InstallationValidation Validation => inventory.Validation;

    public static async Task<Mo2ToolOutputFixtureBuilder> CreateAsync() =>
        new(await Mo2ModInventoryFixtureBuilder.CreateAsync());

    public void CreateProfile(string modList) => inventory.CreateProfile("Default", modList);

    public string CreateOutputMod(string name) => inventory.CreateMod(name);

    public string WriteFile(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public async Task<Mo2ToolOutputObservationRequest> RequestAsync(
        ImmutableArray<Mo2ToolRecognitionResult> tools,
        ImmutableArray<Mo2CustomOverwriteMapping> mappings)
    {
        var profiles = await inventory.ObserveProfilesAsync();
        var observedInventory = await inventory.ObserveAsync();
        var profile = profiles.Profiles.Single() with
        {
            Inventory = observedInventory.Profiles.Single(),
        };
        return new(Reference, Validation, profile, tools, mappings);
    }

    public Mo2ToolOutputService CreateService(
        Mo2ToolOutputObservationLimits? limits = null,
        Func<DateTimeOffset>? utcNow = null,
        IMo2RandomAccessFileFactory? files = null) =>
        new(
            new Mo2ContentTreeObserver(inventory.Paths),
            files ?? new WindowsRandomAccessFileFactory(),
            inventory.Paths,
            contentAuthorization,
            limits,
            utcNow);

    public Mo2ToolOutputSnapshotCache CreateCache(Mo2ToolOutputObservationLimits? limits = null) =>
        new(CreateService(limits));

    public void AuthorizeOverwrite(string root) =>
        contentAuthorization.AuthorizeRoot(Reference.Id, Mo2ContentRootKind.Overwrite, root);

    public static Mo2ToolRecognitionResult Tool(
        string id,
        string title,
        Mo2RecognizedToolFamily family,
        Mo2RecognitionConfidence confidence = Mo2RecognitionConfidence.Corroborated) =>
        new(new(id), title, family, confidence, ["configured title", "canonical binary leaf"]);

    public static string HashTree(string root)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(root, path);
            hash.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            if (File.Exists(path))
            {
                hash.AppendData(File.ReadAllBytes(path));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose() => inventory.Dispose();
}
