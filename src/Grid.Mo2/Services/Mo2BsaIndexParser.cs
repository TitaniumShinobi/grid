using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2BsaIndexParser : IMo2BsaIndexParser
{
    private const int HeaderSize = 36;
    private const int FolderRecordSizeVersion105 = 24;
    private const int FileRecordSize = 16;

    public async Task<Mo2ArchiveIndexSnapshot> ParseAsync(
        IMo2RandomAccessFile file,
        Mo2BsaIndexLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length < 4)
            {
                return Malformed(file, Mo2ArchiveFormat.Unknown, "mo2.archive.signature_truncated", "The archive signature is truncated.");
            }

            var prefix = new byte[checked((int)Math.Min(HeaderSize, file.Length))];
            await ReadExactlyAsync(file, 0, prefix, cancellationToken).ConfigureAwait(false);
            if (prefix.AsSpan(0, 4).SequenceEqual("BTDX"u8))
            {
                return Result(
                    Mo2BinaryObservationStatus.Unsupported,
                    Mo2ArchiveFormat.Ba2,
                    Mo2ArchiveSupport.UnsupportedFormat,
                    null,
                    null,
                    null,
                    [],
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.archive.ba2_unsupported", "BA2 archives are represented but are not indexed for Skyrim SE.")]);
            }

            if (!prefix.AsSpan(0, 4).SequenceEqual("BSA\0"u8))
            {
                return Result(
                    Mo2BinaryObservationStatus.Unsupported,
                    Mo2ArchiveFormat.Unknown,
                    Mo2ArchiveSupport.UnsupportedFormat,
                    null,
                    null,
                    null,
                    [],
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.archive.format_unsupported", "The archive format is unsupported.")]);
            }

            if (prefix.Length < HeaderSize)
            {
                return Malformed(file, Mo2ArchiveFormat.Bsa, "mo2.archive.header_truncated", "The BSA header is truncated.");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4, 4));
            var archiveFlags = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12, 4));
            var fileFlags = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(32, 4));
            if (version != 105)
            {
                return Result(
                    Mo2BinaryObservationStatus.Unsupported,
                    Mo2ArchiveFormat.Bsa,
                    Mo2ArchiveSupport.UnsupportedVersion,
                    version,
                    archiveFlags,
                    fileFlags,
                    [],
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.archive.bsa_version_unsupported", "Only the Skyrim SE BSA version 105 index is supported.")]);
            }

            var folderRecordOffset = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8, 4));
            var folderCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(16, 4));
            var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(20, 4));
            var folderNameBytes = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(24, 4));
            var fileNameBytes = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(28, 4));
            if (fileCount > limits.MaximumMembers || folderCount > fileCount + 1)
            {
                return Result(
                    Mo2BinaryObservationStatus.Oversized,
                    Mo2ArchiveFormat.Bsa,
                    Mo2ArchiveSupport.Malformed,
                    version,
                    archiveFlags,
                    fileFlags,
                    [],
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.archive.member_limit", "The BSA member count exceeds the configured safety limit.")]);
            }

            long indexLength;
            try
            {
                var recordBytes = checked((long)folderCount * FolderRecordSizeVersion105);
                var memberRecordBytes = checked((long)fileCount * FileRecordSize);
                // The header's total folder-name length excludes each folder block's
                // one-byte length prefix. Account for those prefixes explicitly.
                indexLength = checked((long)folderRecordOffset + recordBytes + folderCount + folderNameBytes + memberRecordBytes + fileNameBytes);
            }
            catch (OverflowException)
            {
                return Malformed(file, Mo2ArchiveFormat.Bsa, "mo2.archive.index_overflow", "The BSA index sizes overflow their valid range.");
            }

            if (folderRecordOffset < HeaderSize || indexLength > limits.MaximumIndexBytes)
            {
                return Result(
                    Mo2BinaryObservationStatus.Oversized,
                    Mo2ArchiveFormat.Bsa,
                    Mo2ArchiveSupport.Malformed,
                    version,
                    archiveFlags,
                    fileFlags,
                    [],
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.archive.index_limit", "The BSA index exceeds the bounded read limit or has an invalid offset.")]);
            }

            if (indexLength > file.Length || indexLength > int.MaxValue)
            {
                return Malformed(file, Mo2ArchiveFormat.Bsa, "mo2.archive.index_truncated", "The BSA index extends beyond the archive.");
            }

            var bytes = new byte[checked((int)indexLength)];
            await ReadExactlyAsync(file, 0, bytes, cancellationToken).ConfigureAwait(false);
            var members = ParseIndex(
                bytes,
                checked((int)folderRecordOffset),
                checked((int)folderCount),
                checked((int)fileCount),
                limits,
                warnings,
                out var malformed);
            var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            var changed = after != file.InitialStamp;
            if (changed)
            {
                warnings.Add(new("mo2.archive.changed_during_read", "The archive was replaced or changed during index observation."));
            }

            return Result(
                changed
                    ? Mo2BinaryObservationStatus.ChangedDuringRead
                    : malformed ? Mo2BinaryObservationStatus.Malformed : Mo2BinaryObservationStatus.Complete,
                Mo2ArchiveFormat.Bsa,
                malformed ? Mo2ArchiveSupport.Malformed : Mo2ArchiveSupport.SupportedIndex,
                version,
                archiveFlags,
                fileFlags,
                members,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                file.InitialStamp,
                after,
                warnings.ToImmutable(),
                indexLength);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or OverflowException)
        {
            warnings.Add(new("mo2.archive.index_inaccessible", "The archive index could not be read safely."));
            return Result(
                Mo2BinaryObservationStatus.Inaccessible,
                Mo2ArchiveFormat.Unknown,
                Mo2ArchiveSupport.Malformed,
                null,
                null,
                null,
                [],
                null,
                file.InitialStamp,
                null,
                warnings.ToImmutable());
        }
    }

    private static ImmutableArray<Mo2ArchiveMember> ParseIndex(
        ReadOnlySpan<byte> bytes,
        int folderRecordOffset,
        int folderCount,
        int expectedFileCount,
        Mo2BsaIndexLimits limits,
        ImmutableArray<Mo2ParseWarning>.Builder warnings,
        out bool malformed)
    {
        malformed = false;
        var folderCounts = new int[folderCount];
        var recordsEnd = checked(folderRecordOffset + folderCount * FolderRecordSizeVersion105);
        if (recordsEnd > bytes.Length)
        {
            warnings.Add(new("mo2.archive.folder_records_truncated", "The BSA folder record table is truncated."));
            malformed = true;
            return [];
        }

        long countedFiles = 0;
        for (var index = 0; index < folderCount; index++)
        {
            var offset = folderRecordOffset + index * FolderRecordSizeVersion105;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4));
            if (count > int.MaxValue)
            {
                warnings.Add(new("mo2.archive.folder_count_invalid", "A BSA folder contains an invalid member count."));
                malformed = true;
                return [];
            }

            folderCounts[index] = (int)count;
            countedFiles = checked(countedFiles + count);
        }

        if (countedFiles != expectedFileCount)
        {
            warnings.Add(new("mo2.archive.file_count_mismatch", "The BSA folder and header file counts disagree."));
            malformed = true;
            return [];
        }

        var pending = new List<PendingMember>(expectedFileCount);
        var position = recordsEnd;
        for (var folderIndex = 0; folderIndex < folderCount; folderIndex++)
        {
            if (position >= bytes.Length)
            {
                warnings.Add(new("mo2.archive.folder_name_truncated", "A BSA folder name is truncated."));
                malformed = true;
                return [];
            }

            var folderLength = bytes[position++];
            if (folderLength == 0 || folderLength > bytes.Length - position)
            {
                warnings.Add(new("mo2.archive.folder_name_invalid", "A BSA folder name has an invalid length."));
                malformed = true;
                return [];
            }

            var folderBytes = bytes.Slice(position, folderLength);
            position += folderLength;
            var folder = DecodeNullTerminated(folderBytes, out var folderTerminated);
            if (!folderTerminated)
            {
                warnings.Add(new("mo2.archive.folder_name_not_terminated", "A BSA folder name is not null terminated."));
                malformed = true;
            }

            for (var fileIndex = 0; fileIndex < folderCounts[folderIndex]; fileIndex++)
            {
                if (bytes.Length - position < FileRecordSize)
                {
                    warnings.Add(new("mo2.archive.file_records_truncated", "The BSA file record table is truncated."));
                    malformed = true;
                    return [];
                }

                pending.Add(new(
                    folder,
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(position, 8)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 8, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(position + 12, 4))));
                position += FileRecordSize;
            }
        }

        var declaredFileNamesOffset = checked(
            recordsEnd +
            folderCount +
            BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(24, 4)) +
            expectedFileCount * FileRecordSize);
        if (position != declaredFileNamesOffset || declaredFileNamesOffset < 0 || declaredFileNamesOffset > bytes.Length)
        {
            warnings.Add(new(
                "mo2.archive.folder_block_size_mismatch",
                "The BSA folder blocks do not match the declared folder-name size."));
            malformed = true;
            if (declaredFileNamesOffset < 0 || declaredFileNamesOffset > bytes.Length)
            {
                return [];
            }

            position = declaredFileNamesOffset;
        }

        var members = ImmutableArray.CreateBuilder<Mo2ArchiveMember>();
        for (var index = 0; index < pending.Count; index++)
        {
            var remaining = bytes[position..];
            var terminator = remaining.IndexOf((byte)0);
            if (terminator < 0)
            {
                warnings.Add(new("mo2.archive.file_name_truncated", "The BSA file-name table is truncated."));
                malformed = true;
                break;
            }

            var file = Encoding.Latin1.GetString(remaining[..terminator]);
            position += terminator + 1;
            var candidate = string.IsNullOrEmpty(pending[index].Folder)
                ? file
                : $"{pending[index].Folder}\\{file}";
            if (!TryNormalizeVirtualPath(candidate, limits, out var virtualPath))
            {
                warnings.Add(new("mo2.archive.member_path_invalid", "A BSA member path is unsafe and was rejected."));
                malformed = true;
                continue;
            }

            members.Add(new(
                virtualPath,
                index,
                pending[index].Hash,
                pending[index].PackedSize,
                pending[index].DataOffset));
        }

        return members.ToImmutable();
    }

    private static string DecodeNullTerminated(ReadOnlySpan<byte> bytes, out bool terminated)
    {
        var terminator = bytes.IndexOf((byte)0);
        terminated = terminator >= 0;
        return Encoding.Latin1.GetString(terminated ? bytes[..terminator] : bytes);
    }

    private static bool TryNormalizeVirtualPath(
        string value,
        Mo2BsaIndexLimits limits,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > limits.MaximumVirtualPathLength ||
            value.Any(char.IsControl) ||
            value.StartsWith('\\') ||
            value.StartsWith('/') ||
            value.Contains(':'))
        {
            return false;
        }

        var segments = value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment =>
            segment is "." or ".." || segment.Length > limits.MaximumSegmentLength))
        {
            return false;
        }

        normalized = string.Join('\\', segments);
        return normalized.Length <= limits.MaximumVirtualPathLength;
    }

    private static async Task ReadExactlyAsync(
        IMo2RandomAccessFile file,
        long offset,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await file.ReadAsync(offset + total, destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            total += read;
        }
    }

    private static Mo2ArchiveIndexSnapshot Malformed(
        IMo2RandomAccessFile file,
        Mo2ArchiveFormat format,
        string code,
        string message) =>
        Result(
            Mo2BinaryObservationStatus.Malformed,
            format,
            Mo2ArchiveSupport.Malformed,
            null,
            null,
            null,
            [],
            null,
            file.InitialStamp,
            file.InitialStamp,
            [new(code, message)]);

    private static Mo2ArchiveIndexSnapshot Result(
        Mo2BinaryObservationStatus status,
        Mo2ArchiveFormat format,
        Mo2ArchiveSupport support,
        uint? version,
        uint? archiveFlags,
        uint? fileFlags,
        ImmutableArray<Mo2ArchiveMember> members,
        string? fingerprint,
        Mo2RandomAccessStamp? before,
        Mo2RandomAccessStamp? after,
        ImmutableArray<Mo2ParseWarning> warnings,
        long indexBytesRead = 0) =>
        new(status, format, support, version, archiveFlags, fileFlags, members, fingerprint, before, after, warnings, indexBytesRead);

    private sealed record PendingMember(string Folder, ulong Hash, uint PackedSize, uint DataOffset);
}
