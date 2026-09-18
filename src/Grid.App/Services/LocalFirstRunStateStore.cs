using System.Text.Json;

namespace Grid.App.Services;

public sealed class LocalFirstRunStateStore(string path)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("First-run state path is required.", nameof(path))
        : Path.GetFullPath(path);

    public bool IsComplete()
    {
        try
        {
            if (!File.Exists(path)) return false;
            var document = JsonSerializer.Deserialize<FirstRunDocument>(File.ReadAllText(path), JsonOptions);
            return document is { SchemaVersion: SchemaVersion, Completed: true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    public bool MarkComplete()
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);
            var document = new FirstRunDocument(SchemaVersion, true);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record FirstRunDocument(int SchemaVersion, bool Completed);
}
