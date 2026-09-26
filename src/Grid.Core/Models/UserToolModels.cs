using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct UserToolConfigurationId
{
    [JsonConstructor]
    public UserToolConfigurationId(string value) => Value = Require(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Tool configuration identity is required.", name) : value.Trim();
}

public readonly record struct InstalledToolKnowledgeId
{
    [JsonConstructor]
    public InstalledToolKnowledgeId(string value) => Value = Require(value, nameof(value));
    public string Value { get; }
    public override string ToString() => Value;

    private static string Require(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Tool knowledge identity is required.", name) : value.Trim();
}

public sealed record UserToolScope(GameId GameId, InstallationId InstallationId, ProfileId? ProfileId)
{
    public bool IsInstallationScoped => ProfileId is null;

    public bool IsVisibleIn(UserToolScope currentContext) =>
        currentContext.GameId == GameId &&
        currentContext.InstallationId == InstallationId &&
        (ProfileId is null || ProfileId == currentContext.ProfileId);

    public bool Matches(WorkspaceSelection selection) =>
        selection.GameId is GameId gameId &&
        selection.InstallationId is InstallationId installationId &&
        IsVisibleIn(new(gameId, installationId, selection.ProfileId));
}

public sealed record UserToolLaunchConfiguration(
    int SchemaVersion,
    UserToolConfigurationId Id,
    string Title,
    string? BinaryPath,
    string? StartInPath,
    ImmutableArray<string> Arguments,
    bool Enabled,
    UserToolScope Scope,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Revision,
    InstalledToolKnowledgeId? KnowledgeId = null)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsRunnable =>
        Enabled &&
        !string.IsNullOrWhiteSpace(BinaryPath) &&
        Path.IsPathFullyQualified(BinaryPath) &&
        string.Equals(Path.GetExtension(BinaryPath), ".exe", StringComparison.OrdinalIgnoreCase);

    public string Fingerprint
    {
        get
        {
            var normalized = string.Join('\n',
                SchemaVersion,
                Id.Value,
                Title.Trim(),
                BinaryPath is null ? string.Empty : Path.GetFullPath(BinaryPath),
                StartInPath is null ? string.Empty : Path.GetFullPath(StartInPath),
                string.Join('\u001f', Arguments),
                Enabled,
                Scope.GameId.Value,
                Scope.InstallationId.Value,
                Scope.ProfileId?.Value ?? string.Empty,
                Revision);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        }
    }
}

public enum InstalledToolIdentityState
{
    Installed,
    Identified,
    VersionResolved,
    CompatibilityResolved,
}

public enum ToolCompatibilityDisposition
{
    Compatible,
    Incompatible,
    Unresolved,
}

public sealed record ToolCompatibilityEvidence(
    GameId GameId,
    ToolCompatibilityDisposition Disposition,
    string SourceKind,
    string SourceIdentifier,
    string Claim,
    DateTimeOffset ObservedAtUtc);

public sealed record InstalledToolEvidenceReceipt(
    string Kind,
    string SourceIdentifier,
    string Claim,
    string Value,
    DateTimeOffset ObservedAtUtc);

public sealed record InstalledToolKnowledge(
    int SchemaVersion,
    InstalledToolKnowledgeId Id,
    string BinaryPath,
    string Sha256,
    string? ProductName,
    string? CompanyName,
    string? FileVersion,
    string? ProductVersion,
    string SignatureStatus,
    string? SignatureSubject,
    string? SignatureThumbprint,
    ToolId? CanonicalToolId,
    InstalledToolIdentityState State,
    ImmutableArray<ToolCompatibilityEvidence> Compatibility,
    ImmutableArray<InstalledToolEvidenceReceipt> Evidence,
    ImmutableArray<string> MissingEvidence,
    DateTimeOffset ResolvedAtUtc)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record UserToolConfigurationSnapshot(
    int Revision,
    ImmutableArray<UserToolLaunchConfiguration> Configurations,
    ImmutableArray<string> Issues)
{
    public static UserToolConfigurationSnapshot Empty { get; } = new(0, [], []);
}

public sealed record InstalledToolKnowledgeSnapshot(
    int Revision,
    ImmutableArray<InstalledToolKnowledge> Resolutions,
    ImmutableArray<string> Issues)
{
    public static InstalledToolKnowledgeSnapshot Empty { get; } = new(0, [], []);
}

public enum UserToolSaveStatus
{
    Saved,
    Conflict,
    Invalid,
    Failed,
}

public sealed record UserToolSaveResult(UserToolSaveStatus Status, int Revision, string Detail)
{
    public bool Succeeded => Status == UserToolSaveStatus.Saved;
}

public sealed record UserToolLaunchRequest(
    UserToolConfigurationId ConfigurationId,
    UserToolScope CurrentContext,
    string ConfigurationFingerprint);

public enum UserToolLaunchStatus
{
    Started,
    Unavailable,
    Stale,
    Failed,
}

public sealed record UserToolLaunchResult(UserToolLaunchStatus Status, int? ProcessId, string Detail)
{
    public bool Succeeded => Status == UserToolLaunchStatus.Started;
}
