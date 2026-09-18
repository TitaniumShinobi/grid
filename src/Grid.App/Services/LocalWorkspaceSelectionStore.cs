using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

public sealed class LocalWorkspaceSelectionStore(string path)
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("Workspace-selection path is required.", nameof(path))
        : Path.GetFullPath(path);

    public WorkspaceSelection? Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<WorkspaceSelectionDocument>(File.ReadAllText(path), JsonOptions);
            if (document is null || document.SchemaVersion != SchemaVersion)
            {
                return null;
            }

            return new WorkspaceSelection(
                ParseGameId(document.GameId),
                ParseInstallationId(document.InstallationId),
                ParseProfileId(document.ProfileId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public bool Save(WorkspaceSelection selection)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            var document = new WorkspaceSelectionDocument(
                SchemaVersion,
                selection.GameId?.Value,
                selection.InstallationId?.Value,
                selection.ProfileId?.Value);
            var json = JsonSerializer.Serialize(document, JsonOptions);
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static GameId? ParseGameId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new GameId(value);

    private static InstallationId? ParseInstallationId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new InstallationId(value);

    private static ProfileId? ParseProfileId(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : new ProfileId(value);

    private sealed record WorkspaceSelectionDocument(
        int SchemaVersion,
        string? GameId,
        string? InstallationId,
        string? ProfileId);
}
