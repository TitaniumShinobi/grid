using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public interface IMo2DiscoveryService
{
    Task<Mo2DiscoveryResult> DiscoverAsync(
        Mo2DiscoveryOptions options,
        CancellationToken cancellationToken = default);
}

public interface IMo2InstallationValidator
{
    Task<Mo2InstallationValidation> ValidateAsync(
        Mo2ValidationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2InstallationReferenceStore
{
    Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        ImmutableArray<Mo2InstallationReference> references,
        CancellationToken cancellationToken = default);
}

public interface IMo2InstallationReferenceRecoveryStore : IMo2InstallationReferenceStore
{
    Task<Mo2ReferenceStoreSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

    Task RestoreSnapshotAsync(
        Mo2ReferenceStoreSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

public interface IMo2ConnectionService
{
    Task<Mo2ConnectionResult> ConnectAsync(
        Mo2ConnectionRequest request,
        CancellationToken cancellationToken = default);

    Task<Mo2DisconnectResult> DisconnectAsync(
        Mo2DisconnectRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2ProfileSnapshotService
{
    Task<Mo2ProfileSnapshot> ObserveAsync(
        Mo2ProfileSnapshotRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2ModInventoryService
{
    Task<Mo2ModInventorySnapshot> ObserveAsync(
        Mo2ModInventoryRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2ExecutableConfigurationService
{
    Task<Mo2ExecutableConfigurationSnapshot> ObserveAsync(
        Mo2ExecutableObservationRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2ExecutablePathAuthorization
{
    Mo2ExecutablePathAuthorization AuthorizePath(
        InstallationReferenceId referenceId,
        string exactPath);

    bool IsPathAuthorized(InstallationReferenceId referenceId, string exactPath);

    void Revoke(InstallationReferenceId referenceId, string? exactPath = null);
}

public interface IMo2PeIconReader
{
    Task<Mo2IconObservation> ReadFirstIconAsync(
        string authorizedExecutablePath,
        CancellationToken cancellationToken = default);
}

public interface IMo2ContentTreeObserver
{
    Task<Mo2ContentTreeObservation> ObserveAsync(
        Mo2ContentTreeRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2PluginHeaderParser
{
    Task<Mo2PluginHeaderSnapshot> ParseAsync(
        IMo2RandomAccessFile file,
        string fileName,
        Mo2PluginHeaderLimits limits,
        CancellationToken cancellationToken = default);
}

public interface IMo2Tes4RecordGraphCollector
{
    Task<Mo2Tes4RecordGraphResult> CollectAsync(
        Mo2Tes4RecordGraphRequest request,
        CancellationToken cancellationToken = default);
}

public interface IMo2BsaIndexParser
{
    Task<Mo2ArchiveIndexSnapshot> ParseAsync(
        IMo2RandomAccessFile file,
        Mo2BsaIndexLimits limits,
        CancellationToken cancellationToken = default);
}

public interface IMo2RandomAccessFileFactory
{
    Task<IMo2RandomAccessFile> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public interface IMo2RandomAccessFile : IAsyncDisposable
{
    long Length { get; }

    Mo2RandomAccessStamp InitialStamp { get; }

    ValueTask<int> ReadAsync(
        long offset,
        Memory<byte> buffer,
        CancellationToken cancellationToken = default);

    ValueTask<Mo2RandomAccessStamp> GetCurrentStampAsync(
        CancellationToken cancellationToken = default);
}

public interface IMo2ModsPathAuthorization
{
    Mo2ModsRootAuthorization AuthorizeModsRoot(
        InstallationReferenceId referenceId,
        string modsRoot);

    bool IsModsRootAuthorized(InstallationReferenceId referenceId, string modsRoot);

    void Revoke(InstallationReferenceId referenceId);
}

public interface IMo2SessionContentPathAuthorization
{
    Mo2ContentRootAuthorization AuthorizeRoot(
        InstallationReferenceId referenceId,
        Mo2ContentRootKind kind,
        string root);

    bool IsRootAuthorized(
        InstallationReferenceId referenceId,
        Mo2ContentRootKind kind,
        string root);

    void Revoke(InstallationReferenceId referenceId, Mo2ContentRootKind? kind = null);
}

public interface IMo2SessionPathAuthorization
{
    Mo2ProfilesRootAuthorization AuthorizeProfilesRoot(
        InstallationReferenceId referenceId,
        string profilesRoot);

    bool IsProfilesRootAuthorized(InstallationReferenceId referenceId, string profilesRoot);

    void Revoke(InstallationReferenceId referenceId);
}

public interface IMo2TextDecoder
{
    Mo2RawTextDocument Decode(
        ImmutableArray<byte> bytes,
        Mo2TextDecodingPolicy policy = Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
}

public interface IWindowsMo2EvidenceSource
{
    Task<ImmutableArray<Mo2DiscoveryEvidence>> FindAsync(
        Mo2DiscoveryOptions options,
        CancellationToken cancellationToken = default);
}

public interface IMo2IniReader
{
    Task<Mo2IniDocument> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public sealed class Mo2IniDocument
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections;

    public Mo2IniDocument(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections) =>
        this.sections = sections ?? throw new ArgumentNullException(nameof(sections));

    public string? Get(string section, string key) =>
        sections.TryGetValue(section, out var values) && values.TryGetValue(key, out var value)
            ? value
            : null;

    public IEnumerable<string> Values => sections.Values.SelectMany(section => section.Values);
}

public interface IMo2ReadOnlyFileSystem
{
    Mo2PathState ProbeFile(string path);

    Mo2PathState ProbeDirectory(string path);

    IReadOnlyList<string> EnumerateDirectories(string path);

    Mo2DirectoryEnumeration EnumerateDirectoriesWithState(string path);

    IReadOnlyList<string> EnumerateFiles(string path);

    Mo2FileMetadata GetFileMetadata(string path);

    Task<ImmutableArray<byte>> ReadBytesAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken);

    Task<Mo2FileReadResult> ReadBytesWithMetadataAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken);

    Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken);
}

public interface IMo2InventoryFileSystem : IMo2ReadOnlyFileSystem
{
    Mo2DirectoryMetadata GetDirectoryMetadata(string path);
}

public interface IGridAtomicFileStore
{
    bool Exists(string path);

    Task<string> ReadTextAsync(string path, int maximumBytes, CancellationToken cancellationToken);

    Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(string path, CancellationToken cancellationToken);
}

public interface IMo2PathCanonicalizer
{
    bool TryNormalizeLexically(string path, out string normalizedPath, out string? error);

    bool TryCanonicalize(string path, out string canonicalPath, out string? error);

    bool Equals(string left, string right);

    bool IsImmediateChildOf(string child, string parent);

    bool IsWithinRoot(string child, string parent);

    string GetIdentityKey(params string[] paths);
}

public interface IWindowsRegistryReader
{
    string? ReadCurrentUserDefaultValue(string subKeyPath);

    string? ReadCurrentUserValue(string subKeyPath, string valueName);
}

public interface IWindowsShortcutResolver
{
    string? TryResolveTarget(string shortcutPath);
}

public interface IMo2PeVersionReader
{
    Mo2VersionEvidence Read(string exactExecutablePath);
}

public interface IMo2ProcessProbe
{
    Task<Mo2ProcessProbeResult> ProbeAsync(string exactMo2ExecutablePath, CancellationToken cancellationToken = default);
}

public interface IMo2ProcessRunner
{
    Task<IMo2ProcessHandle> StartAsync(Mo2LaunchInvocation invocation, CancellationToken cancellationToken = default);
}

public interface IMo2FidelityEvidenceSink
{
    void Publish(FidelityAuditContext context, ImmutableArray<FidelityAuditItem> items);
}
