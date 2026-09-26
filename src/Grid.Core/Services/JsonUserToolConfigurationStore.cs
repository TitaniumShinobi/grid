using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class JsonUserToolConfigurationStore(string storePath) : IUserToolConfigurationStore
{
    private const int MaximumEntries = 512;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath = Path.GetFullPath(Require(storePath, nameof(storePath)));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<UserToolConfigurationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<UserToolSaveResult> ReplaceAsync(int expectedRevision, ImmutableArray<UserToolLaunchConfiguration> configurations, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Issues.IsEmpty)
                return new(UserToolSaveStatus.Failed, current.Revision, "The existing tool configuration store is invalid and was not overwritten.");
            if (current.Revision != expectedRevision)
                return new(UserToolSaveStatus.Conflict, current.Revision, "Tool configurations changed since this editor was opened.");
            try { Validate(configurations); }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
            { return new(UserToolSaveStatus.Invalid, current.Revision, exception.Message); }

            var nextRevision = checked(current.Revision + 1);
            await WriteAtomicAsync(new Document(1, nextRevision, configurations.ToArray()), cancellationToken).ConfigureAwait(false);
            return new(UserToolSaveStatus.Saved, nextRevision, "Tool configurations saved.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return new(UserToolSaveStatus.Failed, expectedRevision, $"Tool configurations were not saved ({exception.GetType().Name})."); }
        finally { gate.Release(); }
    }

    private async Task<UserToolConfigurationSnapshot> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath)) return UserToolConfigurationSnapshot.Empty;
        try
        {
            if (new FileInfo(storePath).Length > MaximumBytes) throw new InvalidDataException("Tool configuration store exceeds its size limit.");
            var document = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false), Options)
                ?? throw new InvalidDataException("Tool configuration store is empty.");
            if (document.SchemaVersion != 1 || document.Revision < 0 || document.Configurations is null)
                throw new InvalidDataException("Tool configuration store schema is unsupported.");
            var configurations = document.Configurations.ToImmutableArray();
            Validate(configurations);
            return new(document.Revision, configurations, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return new(0, [], [$"Tool configurations could not be loaded ({exception.GetType().Name})."]); }
    }

    private async Task WriteAtomicAsync(Document document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(storePath)!;
        Directory.CreateDirectory(directory);
        var temporary = storePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Options) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, storePath, true);
    }

    internal static void Validate(ImmutableArray<UserToolLaunchConfiguration> configurations)
    {
        if (configurations.IsDefault || configurations.Length > MaximumEntries) throw new InvalidDataException("Tool configuration count is invalid.");
        if (configurations.Select(value => value.Id).Distinct().Count() != configurations.Length) throw new InvalidDataException("Tool configurations contain duplicate identities.");
        foreach (var value in configurations)
        {
            if (value.SchemaVersion != UserToolLaunchConfiguration.CurrentSchemaVersion || string.IsNullOrWhiteSpace(value.Title) || value.Title.Length > 160 ||
                value.Arguments.IsDefault || value.Arguments.Length > 128 || value.Arguments.Any(argument => argument.Length > 4096 || argument.Any(char.IsControl)) ||
                value.CreatedAtUtc == default || value.UpdatedAtUtc == default || value.Revision < 1 ||
                string.IsNullOrWhiteSpace(value.Scope.GameId.Value) || string.IsNullOrWhiteSpace(value.Scope.InstallationId.Value) ||
                value.Scope.ProfileId is ProfileId profileId && string.IsNullOrWhiteSpace(profileId.Value))
                throw new InvalidDataException("A tool configuration is invalid.");
            if (value.BinaryPath is not null && (!Path.IsPathFullyQualified(value.BinaryPath) || !string.Equals(Path.GetExtension(value.BinaryPath), ".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A configured binary must be an absolute .exe path.");
            if (value.StartInPath is not null && !Path.IsPathFullyQualified(value.StartInPath))
                throw new InvalidDataException("A configured working directory must be an absolute path.");
        }
    }

    private static string Require(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Store path is required.", name) : value;
    private sealed record Document(int SchemaVersion, int Revision, UserToolLaunchConfiguration[]? Configurations);
}

public sealed class JsonInstalledToolKnowledgeStore(string storePath) : IInstalledToolKnowledgeStore
{
    private const int MaximumEntries = 1024;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string storePath = Path.GetFullPath(string.IsNullOrWhiteSpace(storePath) ? throw new ArgumentException("Store path is required.", nameof(storePath)) : storePath);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<InstalledToolKnowledgeSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<UserToolSaveResult> ReplaceAsync(int expectedRevision, ImmutableArray<InstalledToolKnowledge> resolutions, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!current.Issues.IsEmpty) return new(UserToolSaveStatus.Failed, current.Revision, "The existing tool evidence store is invalid and was not overwritten.");
            if (current.Revision != expectedRevision) return new(UserToolSaveStatus.Conflict, current.Revision, "Tool evidence changed since it was loaded.");
            Validate(resolutions);
            var nextRevision = checked(current.Revision + 1);
            var directory = Path.GetDirectoryName(storePath)!;
            Directory.CreateDirectory(directory);
            var temporary = storePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new Document(1, nextRevision, resolutions.ToArray()), Options) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, storePath, true);
            return new(UserToolSaveStatus.Saved, nextRevision, "Tool evidence saved.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return new(UserToolSaveStatus.Failed, expectedRevision, $"Tool evidence was not saved ({exception.GetType().Name})."); }
        finally { gate.Release(); }
    }

    private async Task<InstalledToolKnowledgeSnapshot> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(storePath)) return InstalledToolKnowledgeSnapshot.Empty;
        try
        {
            if (new FileInfo(storePath).Length > MaximumBytes) throw new InvalidDataException("Tool evidence store exceeds its size limit.");
            var document = JsonSerializer.Deserialize<Document>(await File.ReadAllTextAsync(storePath, cancellationToken).ConfigureAwait(false), Options)
                ?? throw new InvalidDataException("Tool evidence store is empty.");
            if (document.SchemaVersion != 1 || document.Revision < 0 || document.Resolutions is null) throw new InvalidDataException("Tool evidence store schema is unsupported.");
            var values = document.Resolutions.ToImmutableArray();
            Validate(values);
            return new(document.Revision, values, []);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
        { return new(0, [], [$"Tool evidence could not be loaded ({exception.GetType().Name})."]); }
    }

    private static void Validate(ImmutableArray<InstalledToolKnowledge> values)
    {
        if (values.IsDefault || values.Length > MaximumEntries || values.Select(value => value.Id).Distinct().Count() != values.Length) throw new InvalidDataException("Tool evidence collection is invalid.");
        foreach (var value in values)
            if (value.SchemaVersion != InstalledToolKnowledge.CurrentSchemaVersion || !Path.IsPathFullyQualified(value.BinaryPath) || value.Sha256.Length != 64 || value.Evidence.IsDefault || value.Compatibility.IsDefault || value.MissingEvidence.IsDefault || value.ResolvedAtUtc == default)
                throw new InvalidDataException("A tool evidence record is invalid.");
    }

    private sealed record Document(int SchemaVersion, int Revision, InstalledToolKnowledge[]? Resolutions);
}
