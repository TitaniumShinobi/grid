using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class JsonRegistrationRefreshReceiptStore(string directoryPath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string DirectoryPath { get; } = Path.GetFullPath(directoryPath);

    public async Task<RegistrationRefreshReceipt?> LoadLatestAsync(
        GameId gameId,
        ProfileId profileId,
        CancellationToken cancellationToken = default)
    {
        var path = ReceiptPath(gameId, profileId);
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RegistrationRefreshReceipt>(stream, Json, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteAsync(RegistrationRefreshReceipt receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        Directory.CreateDirectory(DirectoryPath);
        var path = ReceiptPath(receipt.GameId, receipt.ProfileId);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, receipt, Json, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private string ReceiptPath(GameId gameId, ProfileId profileId) =>
        Path.Combine(DirectoryPath, Sanitize(gameId.Value) + "." + Sanitize(profileId.Value) + ".registration-refresh.v1.json");

    private static string Sanitize(string value) =>
        string.Concat(value.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
