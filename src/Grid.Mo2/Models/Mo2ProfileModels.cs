using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public enum Mo2TextEncodingKind
{
    Utf8,
    Utf16LittleEndian,
    Utf16BigEndian,
    WindowsSystemCodePage,
}

public enum Mo2TextDecodingPolicy
{
    StrictUtf8,
    WindowsSystemCodePage,
    IniBomUtf8SystemFallback,
}

public enum Mo2LineTerminator
{
    None,
    CarriageReturn,
    LineFeed,
    CarriageReturnLineFeed,
}

public enum Mo2ModListMarker
{
    Enabled,
    Disabled,
    Foreign,
    Unmarked,
}

public enum Mo2PluginListMarker
{
    Disabled,
    Enabled,
}

public readonly record struct Mo2FileStamp(long Length, long LastWriteTimeUtcTicks);

public sealed record Mo2FileMetadata(Mo2PathState State, Mo2FileStamp? Stamp);

public sealed record Mo2DirectoryMetadata(
    Mo2PathState State,
    DateTimeOffset? CreatedAtUtc,
    DateTimeOffset? LastWriteTimeUtc);

public sealed record Mo2DirectoryEnumeration(
    Mo2PathState State,
    ImmutableArray<string> Directories);

public sealed record Mo2FileReadResult(
    ImmutableArray<byte> Bytes,
    Mo2FileStamp Before,
    Mo2FileStamp After);

public sealed record Mo2RawLine(
    int Index,
    int ByteOffset,
    int ByteLength,
    int TerminatorByteLength,
    Mo2LineTerminator Terminator,
    string Text);

public sealed record Mo2RawTextDocument(
    ImmutableArray<byte> Bytes,
    Mo2TextEncodingKind Encoding,
    int ByteOrderMarkLength,
    bool UsedSystemEncodingFallback,
    ImmutableArray<Mo2RawLine> Lines);

public sealed record Mo2ParseWarning(string Code, string Message, int? LineIndex = null);

public sealed record Mo2ParsedModListEntry(
    string Name,
    Mo2ModListMarker Marker,
    bool IsEnabled,
    int SourceLineIndex,
    int Occurrence);

public sealed record Mo2ParsedPluginState(
    string Name,
    bool IsEnabled,
    int SourceLineIndex,
    int Occurrence,
    Mo2PluginListMarker Marker = Mo2PluginListMarker.Disabled);

public sealed record Mo2ParsedLoadOrderEntry(
    string Name,
    int SourceLineIndex,
    int Occurrence);

public sealed record Mo2ModListParseResult(
    ImmutableArray<Mo2ParsedModListEntry> Entries,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status);

public sealed record Mo2PluginStateParseResult(
    ImmutableArray<Mo2ParsedPluginState> Entries,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status);

public sealed record Mo2LoadOrderParseResult(
    ImmutableArray<Mo2ParsedLoadOrderEntry> Entries,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status);

public sealed record Mo2ProfileSettingsParseResult(
    bool? LocalSavesEnabled,
    bool? LocalSettingsEnabled,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status,
    ImmutableArray<Mo2CustomOverwriteSetting> CustomOverwrites = default);

public sealed record Mo2CustomOverwriteSetting(
    string ExecutableTitle,
    string OutputModName,
    int SourceLineIndex,
    string RawKey,
    string RawValue);

public sealed record Mo2ArchiveListParseResult(
    ImmutableArray<string> ArchiveNames,
    ImmutableArray<Mo2ParseWarning> Warnings,
    ProfileSourceParseStatus Status);

public sealed record Mo2ProfileSourceSnapshot(
    string Name,
    ProfileSourceAvailability Availability,
    ProfileSourceParseStatus ParseStatus,
    Mo2RawTextDocument? RawDocument,
    ImmutableArray<Mo2ParseWarning> Warnings,
    Mo2FileStamp? Before,
    Mo2FileStamp? After,
    string? CanonicalSourcePath = null,
    DateTimeOffset ObservedAtUtc = default,
    string ParserVersion = "grid.mo2.profile.v1",
    string? RawFingerprint = null,
    ImmutableArray<byte> RawBytes = default);

public sealed record Mo2ObservedProfile(
    ProfileId Id,
    string Name,
    ManagerProfileState ManagerState,
    ImmutableArray<ModEntry> Mods,
    ImmutableArray<PluginEntry> Plugins,
    ProfileObservationSummary Observation,
    ImmutableArray<Mo2ProfileSourceSnapshot> Sources,
    Mo2ProfileModInventory? Inventory = null,
    Mo2PluginStateParseResult? PluginStates = null,
    Mo2LoadOrderParseResult? LoadOrder = null,
    Mo2ProfileSettingsParseResult? Settings = null);

public sealed record Mo2ProfileSnapshot(
    InstallationReferenceId ReferenceId,
    string Revision,
    ProfileObservationStatus Status,
    ImmutableArray<Mo2ObservedProfile> Profiles,
    ImmutableArray<Mo2ValidationIssue> Issues,
    Mo2ProfileSourceSnapshot? InstanceConfigurationSource = null,
    Mo2ProfileSourceSnapshot? ProfilesRootSource = null,
    Mo2ModInventorySnapshot? ModInventory = null);

public sealed record Mo2ProfileSnapshotRequest(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    string? SelectedProfileName = null);

public sealed record Mo2ProfilesRootAuthorization(
    InstallationReferenceId ReferenceId,
    string CanonicalProfilesRoot);
