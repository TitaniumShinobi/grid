using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2ContentRootKind
{
    GameDirectory,
    GameData,
    Mod,
    Overwrite,
    GlobalGameSettings,
}

public enum Mo2ContentEntryKind
{
    Directory,
    File,
}

public enum Mo2BinaryObservationStatus
{
    Complete,
    Unsupported,
    Malformed,
    Inaccessible,
    Oversized,
    ChangedDuringRead,
    Cancelled,
}

public enum Mo2PluginExtensionKind
{
    Esm,
    Esp,
    Esl,
    Unsupported,
}

public enum Mo2ArchiveFormat
{
    Bsa,
    Ba2,
    Unknown,
}

public enum Mo2ArchiveSupport
{
    SupportedIndex,
    UnsupportedVersion,
    UnsupportedFormat,
    Malformed,
}

public sealed record Mo2ContentObservationLimits(
    int MaximumDepth = 64,
    int MaximumSegmentLength = 255,
    int MaximumVirtualPathLength = 1024,
    long MaximumEntries = 2_000_000)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSegmentLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumVirtualPathLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumEntries);
    }
}

public sealed record Mo2PluginHeaderLimits(long MaximumHeaderBytes = 16L * 1024 * 1024)
{
    public void Validate() => ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHeaderBytes);
}

public sealed record Mo2BsaIndexLimits(
    long MaximumIndexBytes = 64L * 1024 * 1024,
    int MaximumMembers = 1_000_000,
    int MaximumVirtualPathLength = 1024,
    int MaximumSegmentLength = 255)
{
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIndexBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumMembers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumVirtualPathLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSegmentLength);
    }
}

public readonly record struct Mo2FileIdentity(
    uint VolumeSerialNumber,
    ulong FileIndex,
    long Length,
    long LastWriteTimeUtcTicks)
{
    public string StableKey => $"{VolumeSerialNumber:x8}:{FileIndex:x16}";
}

public sealed record Mo2RandomAccessStamp(
    Mo2FileIdentity Identity,
    string CanonicalPath);

public sealed record Mo2ContentTreeEntry(
    Mo2ContentEntryKind Kind,
    string VirtualPath,
    string CanonicalPath,
    string IdentityKey,
    int Depth,
    long? Length,
    long LastWriteTimeUtcTicks,
    FileAttributes Attributes);

public sealed record Mo2ContentTreeObservation(
    Mo2ContentRootKind RootKind,
    string CanonicalRoot,
    Mo2PathState State,
    ImmutableArray<Mo2ContentTreeEntry> Entries,
    string MembershipFingerprint,
    bool IsPartial,
    ImmutableArray<Mo2ValidationIssue> Issues);

public sealed record Mo2ContentTreeRequest(
    InstallationReferenceId ReferenceId,
    Mo2ContentRootKind RootKind,
    string RootPath,
    Mo2ContentObservationLimits Limits);

public sealed record Mo2ContentRootAuthorization(
    InstallationReferenceId ReferenceId,
    Mo2ContentRootKind Kind,
    string CanonicalRoot);

public sealed record Mo2PluginMasterReference(
    string Name,
    int SourceOrder);

public sealed record Mo2PluginHeaderSnapshot(
    Mo2BinaryObservationStatus Status,
    Mo2PluginExtensionKind Extension,
    bool? HasMasterFlag,
    bool? HasLightFlag,
    ImmutableArray<Mo2PluginMasterReference> Masters,
    uint? RecordFlags,
    string? ContentFingerprint,
    Mo2RandomAccessStamp? Before,
    Mo2RandomAccessStamp? After,
    ImmutableArray<Mo2ParseWarning> Warnings);

public sealed record Mo2ArchiveMember(
    string VirtualPath,
    int SourceOrder,
    ulong NameHash,
    uint PackedSize,
    uint DataOffset);

public sealed record Mo2ArchiveIndexSnapshot(
    Mo2BinaryObservationStatus Status,
    Mo2ArchiveFormat Format,
    Mo2ArchiveSupport Support,
    uint? Version,
    uint? ArchiveFlags,
    uint? FileFlags,
    ImmutableArray<Mo2ArchiveMember> Members,
    string? ContentFingerprint,
    Mo2RandomAccessStamp? Before,
    Mo2RandomAccessStamp? After,
    ImmutableArray<Mo2ParseWarning> Warnings,
    long IndexBytesRead = 0);
