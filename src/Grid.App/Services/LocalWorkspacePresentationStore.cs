using System.Text.Json;

namespace Grid.App.Services;

public sealed record WorkspacePresentationState(
    string ModSearch,
    string EnvironmentSearch,
    string SelectedEnvironmentTab,
    IReadOnlyList<string> CollapsedSeparatorIds,
    double? LeftPaneWidth,
    IReadOnlyList<double> ModColumnWidths,
    IReadOnlyList<double> EnvironmentColumnWidths,
    int ModColumnLayoutVersion = 1);

public sealed class LocalWorkspacePresentationStore(string path)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string path = Path.GetFullPath(path);

    public WorkspacePresentationState? Load(string profileId)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            return document is { SchemaVersion: SchemaVersion } &&
                   document.Profiles.TryGetValue(profileId, out var state)
                ? state
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public bool Save(string profileId, WorkspacePresentationState state)
    {
        try
        {
            var profiles = new Dictionary<string, WorkspacePresentationState>(StringComparer.Ordinal);
            if (File.Exists(path))
            {
                var existing = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
                if (existing is { SchemaVersion: SchemaVersion })
                {
                    foreach (var pair in existing.Profiles) profiles[pair.Key] = pair.Value;
                }
            }

            profiles[profileId] = state;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory)) return false;
            Directory.CreateDirectory(directory);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new Document(SchemaVersion, profiles), JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed record Document(int SchemaVersion, Dictionary<string, WorkspacePresentationState> Profiles);
}
