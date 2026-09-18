using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class JsonVortexInstallationConnectionStore(string storePath) : IVortexInstallationConnectionStore
{
    private const int MaximumConnections = 512;
    private const int MaximumStoreBytes = 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath = Path.GetFullPath(string.IsNullOrWhiteSpace(storePath)
        ? throw new ArgumentException("Connection-store path is required.", nameof(storePath))
        : storePath);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<VortexConnectionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<VortexInstallationConnection> ConnectAsync(InstallationId installationId, string stagingRoot, CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Require(stagingRoot, nameof(stagingRoot))));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The selected Vortex staging folder does not exist.");
        var connection = new VortexInstallationConnection(VortexInstallationConnection.CurrentSchemaVersion, installationId, root, DateTimeOffset.UtcNow);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.Issues.IsEmpty) throw new InvalidDataException("Existing Vortex connections must be repaired before adding another connection.");
            await SaveCoreAsync(loaded.Connections.Where(value => value.InstallationId != installationId).Append(connection).ToImmutableArray(), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        return connection;
    }

    public async Task<bool> RemoveAsync(InstallationId installationId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!loaded.Issues.IsEmpty) return false;
            var updated = loaded.Connections.Where(value => value.InstallationId != installationId).ToImmutableArray();
            if (updated.Length == loaded.Connections.Length) return false;
            await SaveCoreAsync(updated, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    private async Task<VortexConnectionLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath)) return new([], []);
        try
        {
            if (new FileInfo(storePath).Length > MaximumStoreBytes) throw new InvalidDataException("Vortex connection store exceeds its size limit.");
            var document = JsonSerializer.Deserialize<StoreDocument>(await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Vortex connection store is empty.");
            if (document.SchemaVersion != 1 || document.Connections is null) throw new InvalidDataException("Vortex connection store schema is unsupported.");
            var values = document.Connections.ToImmutableArray();
            Validate(values);
            return new(values, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return new([], [$"Vortex connections could not be loaded ({exception.GetType().Name})."]); }
    }

    private async Task SaveCoreAsync(ImmutableArray<VortexInstallationConnection> connections, CancellationToken cancellationToken)
    {
        Validate(connections);
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var temporary = storePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new StoreDocument(1, connections.ToArray()), JsonOptions) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, storePath, true);
    }

    private static void Validate(ImmutableArray<VortexInstallationConnection> connections)
    {
        if (connections.IsDefault || connections.Length > MaximumConnections) throw new InvalidDataException("Vortex connection count is invalid.");
        if (connections.Any(value => value.SchemaVersion != 1 || string.IsNullOrWhiteSpace(value.InstallationId.Value) || !Path.IsPathFullyQualified(value.StagingRoot) || value.ConnectedAtUtc == default))
            throw new InvalidDataException("A Vortex connection is invalid.");
        if (connections.Select(value => value.InstallationId).Distinct().Count() != connections.Length)
            throw new InvalidDataException("Vortex connections contain duplicate installation identities.");
    }

    private static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.", name) : value.Trim();
    private sealed record StoreDocument(int SchemaVersion, VortexInstallationConnection[]? Connections);
}
