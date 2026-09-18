using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

public sealed class LocalUserHistoryStore(string storePath) : IUserHistoryStore
{
    public const int CurrentSchemaVersion = 1;
    public const int MaximumEntries = 5000;
    public const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath = Path.GetFullPath(storePath ?? throw new ArgumentNullException(nameof(storePath)));

    public async Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Validate(entry);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.Issue is not null)
            {
                throw new InvalidDataException("Existing history is unreadable.");
            }

            var entries = loaded.Entries
                .Where(candidate => candidate.Id != entry.Id)
                .Append(entry)
                .OrderBy(candidate => candidate.OccurredAtUtc)
                .TakeLast(MaximumEntries)
                .ToArray();
            var document = new StoreDocument(CurrentSchemaVersion, entries.Select(StoreEntry.FromHistory).ToArray());
            var json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            {
                throw new InvalidDataException("History exceeds its bounded document size.");
            }

            var directory = Path.GetDirectoryName(storePath) ?? throw new InvalidOperationException("History has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(storePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                {
                    await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporary, storePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HistoryLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath))
        {
            return new([], null);
        }

        try
        {
            var info = new FileInfo(storePath);
            if (info.Length > MaximumDocumentBytes)
            {
                throw new InvalidDataException("History document is oversized.");
            }

            var json = await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("History document is empty.");
            if (document.SchemaVersion != CurrentSchemaVersion || document.Entries is null || document.Entries.Length > MaximumEntries)
            {
                throw new InvalidDataException("History schema or entry count is invalid.");
            }

            var entries = document.Entries.Select(entry => entry.ToHistory()).ToArray();
            foreach (var entry in entries)
            {
                Validate(entry);
            }

            return new(entries.ToImmutableArray(), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        {
            return new([], "History could not be loaded. Diagnostics contains no private path details.");
        }
    }

    private static void Validate(HistoryEntry entry)
    {
        if (!Enum.IsDefined(entry.Actor) || !Enum.IsDefined(entry.Kind) || !Enum.IsDefined(entry.Status) ||
            entry.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("History entry state is invalid.", nameof(entry));
        }

        ValidateText(entry.Title, 120, nameof(entry.Title));
        ValidateText(entry.Detail, 1000, nameof(entry.Detail));
    }

    private static void ValidateText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) ||
            value.Contains(@":\", StringComparison.Ordinal) ||
            value.Contains(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException("History text is empty, oversized, contains controls, or contains a private path shape.", name);
        }
    }

    private sealed record StoreDocument(int SchemaVersion, StoreEntry[]? Entries);

    private sealed record StoreEntry(
        string Id,
        DateTimeOffset OccurredAtUtc,
        HistoryActor Actor,
        HistoryEventKind Kind,
        HistoryEventStatus Status,
        string? GameId,
        string? InstallationId,
        string? ProfileId,
        string Title,
        string Detail)
    {
        public static StoreEntry FromHistory(HistoryEntry entry) => new(
            entry.Id.Value,
            entry.OccurredAtUtc,
            entry.Actor,
            entry.Kind,
            entry.Status,
            entry.GameId?.Value,
            entry.InstallationId?.Value,
            entry.ProfileId?.Value,
            entry.Title,
            entry.Detail);

        public HistoryEntry ToHistory() => new(
            new HistoryEntryId(Id),
            OccurredAtUtc,
            Actor,
            Kind,
            Status,
            GameId is null ? null : new GameId(GameId),
            InstallationId is null ? null : new InstallationId(InstallationId),
            ProfileId is null ? null : new ProfileId(ProfileId),
            Title,
            Detail);
    }
}

public sealed class InMemoryUserHistoryStore : IUserHistoryStore
{
    private ImmutableArray<HistoryEntry> entries = [];

    public Task<HistoryLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HistoryLoadResult(entries, null));
    }

    public Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries = entries.Where(candidate => candidate.Id != entry.Id).Append(entry).TakeLast(LocalUserHistoryStore.MaximumEntries).ToImmutableArray();
        return Task.CompletedTask;
    }
}
