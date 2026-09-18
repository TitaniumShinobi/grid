using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2InstanceKind
{
    Portable,
    Global,
}

public enum Mo2SelectionKind
{
    Invalid,
    ApplicationDirectory,
    PortableInstance,
    GlobalInstance,
    InstanceDirectory,
    BaseDirectory,
    GameDirectory,
}

public enum Mo2EvidenceKind
{
    PersistedReference,
    GlobalInstanceRoot,
    InstallerDefault,
    Shortcut,
    ProtocolHandler,
    DownloadHandler,
    CurrentInstance,
    ManualSelection,
}

public enum Mo2PathState
{
    Present,
    Missing,
    Unavailable,
    Inaccessible,
    Invalid,
    AuthorizationRequired,
}

public enum Mo2ValidationStatus
{
    Valid,
    Incomplete,
    Invalid,
    Inaccessible,
}

public enum Mo2IssueSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record Mo2ValidationIssue(
    string Code,
    Mo2IssueSeverity Severity,
    string Message,
    string? PathLabel = null);

public sealed record Mo2PathObservation(
    string Label,
    string? CanonicalPath,
    Mo2PathState State);

public sealed record Mo2DiscoveryEvidence(
    Mo2EvidenceKind Kind,
    string? ApplicationDirectory,
    string? InstancePath,
    string Description,
    int Rank);

public sealed record Mo2DiscoveryCandidate(
    string Key,
    string DisplayName,
    string? ApplicationDirectory,
    string? InstancePath,
    Mo2EvidenceKind EvidenceKind,
    string EvidenceDescription);

public sealed record Mo2DiscoveryResult(
    ImmutableArray<Mo2DiscoveryCandidate> Candidates,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ValidationRequest(
    string? ApplicationDirectory,
    string? InstancePath,
    GameId ExpectedGameId,
    ImmutableArray<string> AuthorizedConfiguredPaths = default)
{
    public ImmutableArray<string> EffectiveAuthorizedConfiguredPaths =>
        AuthorizedConfiguredPaths.IsDefault ? [] : AuthorizedConfiguredPaths;
}

public sealed record Mo2InstallationValidation(
    Mo2ValidationStatus Status,
    Mo2SelectionKind SelectionKind,
    bool CanConnect,
    Mo2InstanceKind? InstanceKind,
    string? ApplicationDirectory,
    string? ExecutablePath,
    string? InstanceDirectory,
    string? IniPath,
    string? BaseDirectory,
    string? ModsDirectory,
    string? ProfilesDirectory,
    string? DownloadsDirectory,
    string? OverwriteDirectory,
    string? GameDirectory,
    string? GameName,
    string? ConnectionKey,
    ImmutableArray<Mo2PathObservation> Paths,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2InstallationReference(
    int SchemaVersion,
    InstallationReferenceId Id,
    InstallationId InstallationId,
    GameId GameId,
    GameAdapterId AdapterId,
    string DisplayName,
    Mo2InstanceKind InstanceKind,
    string ExecutablePath,
    string InstanceDirectory)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record Mo2ReferenceLoadResult(
    ImmutableArray<Mo2InstallationReference> References,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ReferenceStoreSnapshot(
    bool Existed,
    string? ExactContent);

public sealed record Mo2ConnectionResult(
    bool Succeeded,
    Mo2InstallationReference? Reference,
    Mo2InstallationValidation Validation,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ConnectionRequest(
    Mo2ValidationRequest Validation,
    GameAdapterId AdapterId,
    string DisplayName,
    bool ReuseExisting = false);

public enum Mo2RegistrationStatus
{
    Registered,
    Reused,
    InstallationContextUnresolved,
    PersistenceFailed,
}

public enum Mo2RegistrationRecoveryDisposition
{
    None,
    ConfigurationRequired,
}

public sealed record Mo2RegistrationRequest(
    string ApplicationDirectory,
    string InstanceDirectory,
    string ExpectedProfileName,
    string DisplayName,
    GameId ExpectedGameId,
    GameAdapterId AdapterId);

public sealed record Mo2RegistrationResult(
    Mo2RegistrationStatus Status,
    Mo2RegistrationRecoveryDisposition RecoveryDisposition,
    bool Succeeded,
    bool Persisted,
    Mo2InstallationReference? Reference,
    string? ProfileName,
    ProfileId? ProfileId,
    Mo2InstallationValidation Validation,
    ImmutableArray<string> AuthorizedDerivedPaths,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2DiscoveryOptions(
    string LocalApplicationDataPath,
    string SystemDriveRoot,
    GameId ExpectedGameId);
