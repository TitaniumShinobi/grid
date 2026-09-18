using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2PeIconReader : IMo2PeIconReader
{
    private const ushort ResourceTypeIcon = 3;
    private const ushort ResourceTypeGroupIcon = 14;
    private readonly IMo2ReadOnlyFileSystem fileSystem;
    private readonly Mo2PeIconLimits limits;

    public Mo2PeIconReader(IMo2ReadOnlyFileSystem fileSystem, Mo2PeIconLimits? limits = null)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.limits = limits ?? new();
        this.limits.Validate();
    }

    public async Task<Mo2IconObservation> ReadFirstIconAsync(
        string authorizedExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedExecutablePath);
        cancellationToken.ThrowIfCancellationRequested();

        Mo2FileReadResult read;
        try
        {
            read = await fileSystem.ReadBytesWithMetadataAsync(
                authorizedExecutablePath,
                limits.MaximumExecutableBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return Result(Mo2IconObservationStatus.Missing, "mo2.icon.binary_missing", "The executable is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return Result(Mo2IconObservationStatus.Missing, "mo2.icon.binary_missing", "The executable is missing.");
        }
        catch (InvalidDataException)
        {
            return Result(Mo2IconObservationStatus.Oversized, "mo2.icon.binary_oversized", "The executable exceeds the bounded icon-read limit.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(Mo2IconObservationStatus.Inaccessible, "mo2.icon.binary_inaccessible", "The executable could not be read.");
        }

        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var bytes = read.Bytes.ToArray();
        if (!TryReadIcon(bytes, warnings, out var ico))
        {
            return new(
                warnings.Any(warning => warning.Code == "mo2.icon.not_present")
                    ? Mo2IconObservationStatus.Unsupported
                    : Mo2IconObservationStatus.Malformed,
                [],
                null,
                warnings.ToImmutable(),
                read.Before,
                read.After);
        }

        var changed = read.Before != read.After;
        if (changed)
        {
            warnings.Add(new("mo2.icon.changed_during_read", "The executable changed while its icon resource was observed."));
        }

        return new(
            changed ? Mo2IconObservationStatus.ChangedDuringRead : Mo2IconObservationStatus.Available,
            ImmutableArray.Create(ico),
            Convert.ToHexString(SHA256.HashData(ico)).ToLowerInvariant(),
            warnings.ToImmutable(),
            read.Before,
            read.After);
    }

    private bool TryReadIcon(
        byte[] image,
        ImmutableArray<Mo2ParseWarning>.Builder warnings,
        out byte[] ico)
    {
        ico = [];
        var data = image.AsSpan();
        if (data.Length < 0x40 || ReadUInt16(data, 0) != 0x5A4D)
        {
            warnings.Add(new("mo2.icon.pe_invalid", "The file is not a supported PE image."));
            return false;
        }

        var peOffset = ReadInt32(data, 0x3C);
        if (!Contains(data, peOffset, 24) || ReadUInt32(data, peOffset) != 0x00004550)
        {
            warnings.Add(new("mo2.icon.pe_invalid", "The PE header is malformed."));
            return false;
        }

        var sectionCount = ReadUInt16(data, peOffset + 6);
        var optionalSize = ReadUInt16(data, peOffset + 20);
        if (sectionCount == 0 || sectionCount > 96)
        {
            warnings.Add(new("mo2.icon.pe_sections_invalid", "The PE section count is unsupported."));
            return false;
        }

        var optionalOffset = peOffset + 24;
        if (!Contains(data, optionalOffset, optionalSize) || optionalSize < 112)
        {
            warnings.Add(new("mo2.icon.pe_optional_invalid", "The PE optional header is malformed."));
            return false;
        }

        var magic = ReadUInt16(data, optionalOffset);
        var directoryOffset = magic switch
        {
            0x10B => optionalOffset + 96,
            0x20B => optionalOffset + 112,
            _ => -1,
        };
        if (directoryOffset < 0 || !Contains(data, directoryOffset, 24))
        {
            warnings.Add(new("mo2.icon.pe_optional_unsupported", "The PE optional-header format is unsupported."));
            return false;
        }

        var resourceRva = ReadUInt32(data, directoryOffset + 16);
        var resourceSize = ReadUInt32(data, directoryOffset + 20);
        if (resourceRva == 0 || resourceSize == 0)
        {
            warnings.Add(new("mo2.icon.not_present", "The executable has no icon resource."));
            return false;
        }

        var sectionOffset = optionalOffset + optionalSize;
        if (!Contains(data, sectionOffset, checked(sectionCount * 40)))
        {
            warnings.Add(new("mo2.icon.pe_sections_invalid", "The PE section table is malformed."));
            return false;
        }

        var sections = new List<PeSection>(sectionCount);
        for (var index = 0; index < sectionCount; index++)
        {
            var offset = sectionOffset + (index * 40);
            sections.Add(new(
                ReadUInt32(data, offset + 12),
                ReadUInt32(data, offset + 8),
                ReadUInt32(data, offset + 20),
                ReadUInt32(data, offset + 16)));
        }

        if (!TryMapRva(resourceRva, sections, data.Length, out var resourceRoot))
        {
            warnings.Add(new("mo2.icon.resource_invalid", "The PE resource directory is outside the image."));
            return false;
        }

        var resourceEnd = (long)resourceRoot + resourceSize;
        if (resourceEnd > data.Length || resourceSize > limits.MaximumExecutableBytes)
        {
            warnings.Add(new("mo2.icon.resource_invalid", "The PE resource directory exceeds its declared bounds."));
            return false;
        }

        if (!TryFindResourceData(data, resourceRoot, ResourceTypeGroupIcon, null, sections, out var groupOffset, out var groupSize) ||
            !Contains(data, groupOffset, groupSize) || groupSize < 6)
        {
            warnings.Add(new("mo2.icon.not_present", "The executable has no supported group-icon resource."));
            return false;
        }

        var count = ReadUInt16(data, groupOffset + 4);
        if (count == 0 || count > limits.MaximumIconsPerGroup || !Contains(data, groupOffset + 6, checked(count * 14)))
        {
            warnings.Add(new("mo2.icon.group_invalid", "The PE group-icon resource is malformed or exceeds its limit."));
            return false;
        }

        var selected = groupOffset + 6;
        var iconBytes = ReadUInt32(data, selected + 8);
        var iconId = ReadUInt16(data, selected + 12);
        if (iconBytes == 0 || iconBytes > limits.MaximumIconBytes ||
            !TryFindResourceData(data, resourceRoot, ResourceTypeIcon, iconId, sections, out var iconOffset, out var actualSize) ||
            actualSize < iconBytes || !Contains(data, iconOffset, checked((int)iconBytes)))
        {
            warnings.Add(new("mo2.icon.image_invalid", "The selected icon image is missing, malformed, or exceeds its limit."));
            return false;
        }

        ico = new byte[checked(6 + 16 + (int)iconBytes)];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        data.Slice(selected, 8).CopyTo(ico.AsSpan(6, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), iconBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        data.Slice(iconOffset, checked((int)iconBytes)).CopyTo(ico.AsSpan(22));
        return true;
    }

    private bool TryFindResourceData(
        ReadOnlySpan<byte> data,
        int rootOffset,
        ushort typeId,
        ushort? itemId,
        IReadOnlyList<PeSection> sections,
        out int dataOffset,
        out int dataSize)
    {
        dataOffset = 0;
        dataSize = 0;
        if (!TryFindDirectoryEntry(data, rootOffset, 0, typeId, out var typeTarget, requireDirectory: true) ||
            !TryChooseDirectoryEntry(data, rootOffset, typeTarget, itemId, out var itemTarget, requireDirectory: true) ||
            !TryChooseDirectoryEntry(data, rootOffset, itemTarget, null, out var languageTarget, requireDirectory: false))
        {
            return false;
        }

        var dataEntry = checked(rootOffset + languageTarget);
        if (!Contains(data, dataEntry, 16))
        {
            return false;
        }

        var rva = ReadUInt32(data, dataEntry);
        var size = ReadUInt32(data, dataEntry + 4);
        if (size > int.MaxValue || !TryMapRva(rva, sections, data.Length, out dataOffset))
        {
            return false;
        }

        dataSize = (int)size;
        return Contains(data, dataOffset, dataSize);
    }

    private bool TryChooseDirectoryEntry(
        ReadOnlySpan<byte> data,
        int rootOffset,
        int relativeDirectory,
        ushort? id,
        out int target,
        bool requireDirectory)
    {
        target = 0;
        var directory = checked(rootOffset + relativeDirectory);
        if (!Contains(data, directory, 16))
        {
            return false;
        }

        var named = ReadUInt16(data, directory + 12);
        var identified = ReadUInt16(data, directory + 14);
        var count = checked(named + identified);
        if (count == 0 || count > limits.MaximumResourceEntries || !Contains(data, directory + 16, checked(count * 8)))
        {
            return false;
        }

        for (var index = 0; index < count; index++)
        {
            var entry = directory + 16 + (index * 8);
            var name = ReadUInt32(data, entry);
            if (id is { } exact && ((name & 0x80000000) != 0 || (name & 0xFFFF) != exact))
            {
                continue;
            }

            var rawTarget = ReadUInt32(data, entry + 4);
            var isDirectory = (rawTarget & 0x80000000) != 0;
            if (isDirectory != requireDirectory)
            {
                continue;
            }

            target = checked((int)(rawTarget & 0x7FFFFFFF));
            return true;
        }

        return false;
    }

    private bool TryFindDirectoryEntry(
        ReadOnlySpan<byte> data,
        int rootOffset,
        int relativeDirectory,
        ushort id,
        out int target,
        bool requireDirectory) =>
        TryChooseDirectoryEntry(data, rootOffset, relativeDirectory, id, out target, requireDirectory);

    private static bool TryMapRva(uint rva, IReadOnlyList<PeSection> sections, int imageLength, out int offset)
    {
        foreach (var section in sections)
        {
            var extent = Math.Max(section.VirtualSize, section.RawSize);
            if (rva < section.VirtualAddress || (ulong)rva >= (ulong)section.VirtualAddress + extent)
            {
                continue;
            }

            var candidate = (ulong)section.RawOffset + (rva - section.VirtualAddress);
            if (candidate <= int.MaxValue && candidate < (ulong)imageLength)
            {
                offset = (int)candidate;
                return true;
            }
        }

        offset = 0;
        return false;
    }

    private static Mo2IconObservation Result(Mo2IconObservationStatus status, string code, string message) =>
        new(status, [], null, [new(code, message)]);

    private static bool Contains(ReadOnlySpan<byte> data, int offset, int length) =>
        offset >= 0 && length >= 0 && (long)offset + length <= data.Length;

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        Contains(data, offset, 2) ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]) : (ushort)0;

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        Contains(data, offset, 4) ? BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) : 0;

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        Contains(data, offset, 4) ? BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) : -1;

    private readonly record struct PeSection(uint VirtualAddress, uint VirtualSize, uint RawOffset, uint RawSize);
}
