using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class JsonGameInstallationRegistrationStore(string storePath) : IGameInstallationRegistrationStore
{
    private const int MaximumRegistrations = 512;
    private const int MaximumStoreBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath = Path.GetFullPath(string.IsNullOrWhiteSpace(storePath)
        ? throw new ArgumentException("Registration-store path is required.", nameof(storePath))
        : storePath);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<GameRegistrationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ImmutableArray<GameInstallationRegistration> registrations, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(registrations, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<GameInstallationRegistration> RegisterAsync(
        GameId gameId,
        GameAdapterId adapterId,
        string displayName,
        string edition,
        string providerId,
        string installRoot,
        string executablePath,
        IEnumerable<string> managerProviderIds,
        CancellationToken cancellationToken = default)
    {
        var canonicalRoot=CanonicalDirectory(installRoot);
        var canonicalExecutable=CanonicalFile(executablePath);
        if(!File.Exists(canonicalExecutable) || !Directory.Exists(canonicalRoot) ||
           !Path.GetDirectoryName(canonicalExecutable)!.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The selected executable must exist beneath the selected game directory.");
        var identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{gameId.Value}\n{edition.Trim()}\n{canonicalRoot}"))).ToLowerInvariant()[..24];
        var registration=new GameInstallationRegistration(
            GameInstallationRegistration.CurrentSchemaVersion,
            new($"reference.game.{identity}"),new($"installation.game.{identity}"),gameId,adapterId,
            Require(displayName,nameof(displayName)),Require(edition,nameof(edition)),Require(providerId,nameof(providerId)),
            canonicalRoot,canonicalExecutable,
            managerProviderIds.Where(value=>!string.IsNullOrWhiteSpace(value)).Select(value=>value.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order().ToImmutableArray(),
            DateTimeOffset.UtcNow);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded=await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if(!loaded.Issues.IsEmpty) throw new InvalidDataException("Existing game registrations must be repaired before adding another installation.");
            var updated=loaded.Registrations
                .Where(value=>!HasCanonicalIdentity(value,gameId,edition,canonicalRoot))
                .Append(registration)
                .ToImmutableArray();
            await SaveCoreAsync(updated,cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        return registration;
    }

    public async Task<bool> RemoveAsync(InstallationId installationId,CancellationToken cancellationToken=default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded=await LoadCoreAsync(cancellationToken).ConfigureAwait(false);if(!loaded.Issues.IsEmpty)return false;
            var updated=loaded.Registrations.Where(value=>value.InstallationId!=installationId).ToImmutableArray();
            if(updated.Length==loaded.Registrations.Length)return false;
            await SaveCoreAsync(updated,cancellationToken).ConfigureAwait(false);return true;
        }
        finally{gate.Release();}
    }

    private async Task<GameRegistrationLoadResult> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if(!File.Exists(storePath)) return new([],[]);
        try
        {
            var info=new FileInfo(storePath);if(info.Length>MaximumStoreBytes) throw new InvalidDataException("Registration store exceeds its size limit.");
            var json=await File.ReadAllTextAsync(storePath,cancellationToken).ConfigureAwait(false);
            var document=JsonSerializer.Deserialize<StoreDocument>(json,JsonOptions) ?? throw new InvalidDataException("Registration store is empty.");
            if(document.SchemaVersion!=1 || document.Registrations is null) throw new InvalidDataException("Registration store schema is unsupported.");
            var registrations=document.Registrations.ToImmutableArray();Validate(registrations);return new(registrations,[]);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return new([],[$"Game registrations could not be loaded ({exception.GetType().Name})."]); }
    }

    private async Task SaveCoreAsync(ImmutableArray<GameInstallationRegistration> registrations,CancellationToken cancellationToken)
    {
        Validate(registrations);
        var directory=Path.GetDirectoryName(storePath)!;Directory.CreateDirectory(directory);
        var json=JsonSerializer.Serialize(new StoreDocument(1,registrations.ToArray()),JsonOptions)+Environment.NewLine;
        var temporary=storePath+".tmp";await File.WriteAllTextAsync(temporary,json,cancellationToken).ConfigureAwait(false);File.Move(temporary,storePath,true);
    }

    private static void Validate(ImmutableArray<GameInstallationRegistration> registrations)
    {
        if(registrations.IsDefault || registrations.Length>MaximumRegistrations) throw new InvalidDataException("Game registration count is invalid.");
        if(registrations.Any(value=>value.SchemaVersion!=1 || string.IsNullOrWhiteSpace(value.ReferenceId.Value) || string.IsNullOrWhiteSpace(value.InstallationId.Value) ||
            string.IsNullOrWhiteSpace(value.GameId.Value) || string.IsNullOrWhiteSpace(value.AdapterId.Value) || string.IsNullOrWhiteSpace(value.DisplayName) || string.IsNullOrWhiteSpace(value.Edition) ||
            string.IsNullOrWhiteSpace(value.ProviderId) || !Path.IsPathFullyQualified(value.InstallRoot) || !Path.IsPathFullyQualified(value.ExecutablePath) ||
            value.ManagerProviderIds.IsDefault || value.RegisteredAtUtc==default)) throw new InvalidDataException("A game registration is invalid.");
        if(registrations.Select(value=>value.InstallationId).Distinct().Count()!=registrations.Length || registrations.Select(value=>value.ReferenceId).Distinct().Count()!=registrations.Length)
            throw new InvalidDataException("Game registrations contain duplicate identities.");
    }
    private static string CanonicalDirectory(string value)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(Require(value,nameof(value))));
    private static string CanonicalFile(string value)=>Path.GetFullPath(Require(value,nameof(value)));
    private static bool HasCanonicalIdentity(GameInstallationRegistration value,GameId gameId,string edition,string canonicalRoot)=>
        StringComparer.Ordinal.Equals(value.GameId.Value,gameId.Value) &&
        StringComparer.OrdinalIgnoreCase.Equals(value.Edition,edition.Trim()) &&
        StringComparer.OrdinalIgnoreCase.Equals(CanonicalDirectory(value.InstallRoot),canonicalRoot);
    private static string Require(string? value,string name)=>string.IsNullOrWhiteSpace(value)?throw new ArgumentException($"{name} is required.",name):value.Trim();
    private sealed record StoreDocument(int SchemaVersion,GameInstallationRegistration[]? Registrations);
}
