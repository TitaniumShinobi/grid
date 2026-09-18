using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2InstallationReferenceStore(
    IGridAtomicFileStore fileStore,
    IMo2PathCanonicalizer paths,
    string storePath) : IMo2InstallationReferenceRecoveryStore
{
    private const int MaximumStoreBytes = 1024 * 1024;
    private const int MaximumReferences = 128;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default)
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

    private async Task<Mo2ReferenceLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!fileStore.Exists(storePath))
        {
            return new([], []);
        }

        try
        {
            var json = await fileStore.ReadTextAsync(storePath, MaximumStoreBytes, cancellationToken).ConfigureAwait(false);
            var document = JsonSerializer.Deserialize<StoreDocument>(json, JsonOptions)
                ?? throw new InvalidDataException("Reference store is empty.");
            if (document.SchemaVersion != Mo2InstallationReference.CurrentSchemaVersion || document.References is null)
            {
                throw new InvalidDataException("Reference store uses an unsupported schema version.");
            }

            var references = document.References.Select(FromDto).ToImmutableArray();
            ValidateReferences(references);
            return new(references, []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(
                [],
                [new("mo2.references.unreadable", Mo2IssueSeverity.Error, $"Connected MO2 references could not be loaded: {SafeError(exception)}")]);
        }
    }

    public async Task SaveAsync(
        ImmutableArray<Mo2InstallationReference> references,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(references, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<Mo2ReferenceStoreSnapshot> CaptureSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return !fileStore.Exists(storePath)
                ? new(false, null)
                : new(true, await fileStore.ReadTextAsync(storePath, MaximumStoreBytes, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task RestoreSnapshotAsync(
        Mo2ReferenceStoreSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (snapshot.Existed)
            {
                if (snapshot.ExactContent is null)
                {
                    throw new InvalidDataException("An existing reference-store snapshot must retain exact content.");
                }
                await fileStore.WriteAtomicallyAsync(storePath, snapshot.ExactContent, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await fileStore.DeleteIfExistsAsync(storePath, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SaveCoreAsync(
        ImmutableArray<Mo2InstallationReference> references,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (references.IsDefault)
        {
            throw new ArgumentException("Reference collection must be initialized.", nameof(references));
        }

        if (references.Length > MaximumReferences)
        {
            throw new ArgumentException($"At most {MaximumReferences} connected MO2 references may be stored.", nameof(references));
        }

        ValidateReferences(references);
        var document = new StoreDocument(
            Mo2InstallationReference.CurrentSchemaVersion,
            references.Select(ToDto).ToArray());
        var json = JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
        await fileStore.WriteAtomicallyAsync(storePath, json, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateReferences(ImmutableArray<Mo2InstallationReference> references)
    {
        var ids = new HashSet<InstallationReferenceId>();
        var installationIds = new HashSet<InstallationId>();
        var connectionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (reference.SchemaVersion != Mo2InstallationReference.CurrentSchemaVersion)
            {
                throw new InvalidDataException("Connected MO2 reference has an unsupported schema version.");
            }

            if (!Enum.IsDefined(reference.InstanceKind))
            {
                throw new InvalidDataException("Connected MO2 reference has an invalid instance kind.");
            }

            ValidateDisplayName(reference.DisplayName);
            EnforceSupportedIdentity(reference.GameId, reference.AdapterId);
            if (!paths.TryCanonicalize(reference.ExecutablePath, out var executable, out _) ||
                !paths.TryCanonicalize(reference.InstanceDirectory, out var instance, out _) ||
                !Path.GetFileName(executable).Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase) ||
                !IsCanonicalStoredPath(reference.ExecutablePath, executable) ||
                !IsCanonicalStoredPath(reference.InstanceDirectory, instance))
            {
                throw new InvalidDataException("Connected MO2 reference contains invalid canonical paths.");
            }

            var applicationDirectory = Path.GetDirectoryName(executable)
                ?? throw new InvalidDataException("Connected MO2 executable has no parent directory.");
            var connectionKey = paths.GetIdentityKey(applicationDirectory, instance);
            var suffix = connectionKey[..24];
            if (!reference.Id.Value.Equals($"reference.mo2.{suffix}", StringComparison.Ordinal) ||
                !reference.InstallationId.Value.Equals($"installation.mo2.{suffix}", StringComparison.Ordinal))
            {
                throw new InvalidDataException("Connected MO2 reference contains an identity that does not match its canonical paths.");
            }

            if (!ids.Add(reference.Id) || !installationIds.Add(reference.InstallationId) || !connectionKeys.Add(connectionKey))
            {
                throw new InvalidDataException("Connected MO2 references contain a duplicate identity.");
            }
        }
    }

    internal static void EnforceSupportedIdentity(GameId gameId, GameAdapterId adapterId)
    {
        if (!gameId.Value.Equals("game.skyrim-special-edition", StringComparison.Ordinal) ||
            !adapterId.Value.Equals("adapter.mod-organizer-2", StringComparison.Ordinal))
        {
            throw new ArgumentException("MO2 references currently support only Skyrim Special Edition through the read-only MO2 adapter.");
        }
    }

    private static bool IsCanonicalStoredPath(string stored, string canonical) =>
        Path.IsPathFullyQualified(stored) &&
        string.Equals(
            Path.TrimEndingDirectorySeparator(stored).Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar),
            canonical,
            StringComparison.OrdinalIgnoreCase);

    internal static string ValidateDisplayName(string value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 80 || trimmed.Any(char.IsControl))
        {
            throw new ArgumentException("Display name must contain 1–80 non-control characters.", nameof(value));
        }

        return trimmed;
    }

    private static Mo2InstallationReference FromDto(ReferenceDto value) => new(
        value.SchemaVersion,
        new(value.Id),
        new(value.InstallationId),
        new(value.GameId),
        new(value.AdapterId),
        value.DisplayName,
        value.InstanceKind,
        value.ExecutablePath,
        value.InstanceDirectory);

    private static ReferenceDto ToDto(Mo2InstallationReference value) => new(
        value.SchemaVersion,
        value.Id.Value,
        value.InstallationId.Value,
        value.GameId.Value,
        value.AdapterId.Value,
        value.DisplayName,
        value.InstanceKind,
        value.ExecutablePath,
        value.InstanceDirectory);

    private static string SafeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access denied",
        IOException => "I/O error",
        JsonException => "malformed JSON",
        _ => "invalid data",
    };

    private sealed record StoreDocument(int SchemaVersion, ReferenceDto[]? References);

    private sealed record ReferenceDto(
        int SchemaVersion,
        string Id,
        string InstallationId,
        string GameId,
        string AdapterId,
        string DisplayName,
        Mo2InstanceKind InstanceKind,
        string ExecutablePath,
        string InstanceDirectory);
}
