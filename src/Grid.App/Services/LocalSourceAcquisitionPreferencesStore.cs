using System.Text.Json;

namespace Grid.App.Services;

public sealed record SourceAcquisitionPreferences(bool AutomaticallyDownloadVerifiedSources)
{
    public static SourceAcquisitionPreferences Default { get; } = new(false);
}

public sealed class LocalSourceAcquisitionPreferencesStore(string path)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string path = Path.GetFullPath(path);

    public SourceAcquisitionPreferences Load()
    {
        try
        {
            if (!File.Exists(path)) return SourceAcquisitionPreferences.Default;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            return document is { SchemaVersion: SchemaVersion }
                ? document.Preferences
                : SourceAcquisitionPreferences.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return SourceAcquisitionPreferences.Default;
        }
    }

    public bool Save(SourceAcquisitionPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Document(SchemaVersion, preferences), JsonOptions));
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record Document(int SchemaVersion, SourceAcquisitionPreferences Preferences);
}
