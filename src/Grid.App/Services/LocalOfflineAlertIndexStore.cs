using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

/// <summary>
/// Local, read/write cache for normalized alert presentation. Files are isolated by
/// exact game/installation/profile identity and written atomically.
/// </summary>
public sealed class LocalOfflineAlertIndexStore(string rootPath) : IOfflineAlertIndexStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly string rootPath = string.IsNullOrWhiteSpace(rootPath)
        ? throw new ArgumentException("Offline-alert root path is required.", nameof(rootPath))
        : Path.GetFullPath(rootPath);

    public OfflineAlertIndex? Load(GameId gameId, InstallationId installationId, ProfileId profileId)
    {
        var path = GetPath(gameId, installationId, profileId);
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<OfflineAlertIndex>(File.ReadAllText(path), JsonOptions);
    }

    public void Save(OfflineAlertIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Directory.CreateDirectory(rootPath);
        var path = GetPath(index.GameId, index.InstallationId, index.ProfileId);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(index, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private string GetPath(GameId gameId, InstallationId installationId, ProfileId profileId)
    {
        var identity = string.Join('\n', gameId.Value, installationId.Value, profileId.Value);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return Path.Combine(rootPath, $"{hash}.v1.json");
    }
}
