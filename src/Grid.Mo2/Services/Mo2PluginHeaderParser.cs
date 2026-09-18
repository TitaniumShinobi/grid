using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2PluginHeaderParser : IMo2PluginHeaderParser
{
    private const int RecordHeaderSize = 24;

    public async Task<Mo2PluginHeaderSnapshot> ParseAsync(
        IMo2RandomAccessFile file,
        string fileName,
        Mo2PluginHeaderLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var extension = Extension(fileName);
        if (extension == Mo2PluginExtensionKind.Unsupported)
        {
            return Result(
                Mo2BinaryObservationStatus.Unsupported,
                extension,
                null,
                null,
                [],
                null,
                null,
                file.InitialStamp,
                file.InitialStamp,
                [new("mo2.plugin.extension_unsupported", "The file extension is not a supported Bethesda plugin type.")]);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Length < RecordHeaderSize)
            {
                return Malformed(file, extension, "mo2.plugin.header_truncated", "The TES4 record header is truncated.");
            }

            var header = new byte[RecordHeaderSize];
            await ReadExactlyAsync(file, 0, header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8))
            {
                return Malformed(file, extension, "mo2.plugin.signature_invalid", "The first record is not TES4.");
            }

            var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            long observedLength;
            try
            {
                observedLength = checked(RecordHeaderSize + dataSize);
            }
            catch (OverflowException)
            {
                return Malformed(file, extension, "mo2.plugin.header_overflow", "The TES4 record length is invalid.");
            }

            if (observedLength > limits.MaximumHeaderBytes)
            {
                return Result(
                    Mo2BinaryObservationStatus.Oversized,
                    extension,
                    null,
                    null,
                    [],
                    flags,
                    null,
                    file.InitialStamp,
                    file.InitialStamp,
                    [new("mo2.plugin.header_oversized", "The TES4 header exceeds the bounded examination limit.")]);
            }

            if (observedLength > file.Length)
            {
                return Malformed(file, extension, "mo2.plugin.record_truncated", "The TES4 record data extends beyond the file.");
            }

            var bytes = new byte[checked((int)observedLength)];
            header.CopyTo(bytes, 0);
            if (dataSize > 0)
            {
                await ReadExactlyAsync(
                    file,
                    RecordHeaderSize,
                    bytes.AsMemory(RecordHeaderSize, checked((int)dataSize)),
                    cancellationToken).ConfigureAwait(false);
            }

            var masters = ParseMasters(bytes.AsSpan(RecordHeaderSize), warnings, out var malformed);
            var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            var changed = after != file.InitialStamp;
            if (changed)
            {
                warnings.Add(new(
                    "mo2.plugin.changed_during_read",
                    "The plugin was replaced or changed during header observation."));
            }

            return Result(
                changed
                    ? Mo2BinaryObservationStatus.ChangedDuringRead
                    : malformed ? Mo2BinaryObservationStatus.Malformed : Mo2BinaryObservationStatus.Complete,
                extension,
                (flags & 0x1) != 0,
                (flags & 0x200) != 0,
                masters,
                flags,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                file.InitialStamp,
                after,
                warnings.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            warnings.Add(new("mo2.plugin.header_inaccessible", "The plugin header could not be read safely."));
            return Result(
                Mo2BinaryObservationStatus.Inaccessible,
                extension,
                null,
                null,
                [],
                null,
                null,
                file.InitialStamp,
                null,
                warnings.ToImmutable());
        }
    }

    private static ImmutableArray<Mo2PluginMasterReference> ParseMasters(
        ReadOnlySpan<byte> data,
        ImmutableArray<Mo2ParseWarning>.Builder warnings,
        out bool malformed)
    {
        var masters = ImmutableArray.CreateBuilder<Mo2PluginMasterReference>();
        malformed = false;
        var position = 0;
        uint? extendedSize = null;
        while (position < data.Length)
        {
            if (data.Length - position < 6)
            {
                warnings.Add(new("mo2.plugin.subrecord_truncated", "A TES4 subrecord header is truncated."));
                malformed = true;
                break;
            }

            var signature = data.Slice(position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(position + 4, 2));
            position += 6;
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size > int.MaxValue || size > data.Length - position)
            {
                warnings.Add(new("mo2.plugin.subrecord_bounds", "A TES4 subrecord extends beyond the header record."));
                malformed = true;
                break;
            }

            var content = data.Slice(position, checked((int)size));
            position += checked((int)size);
            if (signature.SequenceEqual("XXXX"u8))
            {
                if (size != 4)
                {
                    warnings.Add(new("mo2.plugin.xxxx_invalid", "An XXXX size-extension subrecord is malformed."));
                    malformed = true;
                    continue;
                }

                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(content);
                continue;
            }

            if (!signature.SequenceEqual("MAST"u8))
            {
                continue;
            }

            var nullIndex = content.IndexOf((byte)0);
            var nameBytes = nullIndex >= 0 ? content[..nullIndex] : content;
            var name = Encoding.Latin1.GetString(nameBytes).Trim();
            if (name.Length == 0 || name.Any(char.IsControl))
            {
                warnings.Add(new("mo2.plugin.master_name_invalid", "A MAST subrecord has an invalid plugin name."));
                malformed = true;
                continue;
            }

            if (nullIndex < 0)
            {
                warnings.Add(new("mo2.plugin.master_not_terminated", "A MAST name is not null terminated."));
                malformed = true;
            }

            masters.Add(new(name, masters.Count));
        }

        if (extendedSize is not null)
        {
            warnings.Add(new("mo2.plugin.xxxx_orphaned", "An XXXX size extension has no following subrecord."));
            malformed = true;
        }

        return masters.ToImmutable();
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

    private static Mo2PluginExtensionKind Extension(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".esm" => Mo2PluginExtensionKind.Esm,
            ".esp" => Mo2PluginExtensionKind.Esp,
            ".esl" => Mo2PluginExtensionKind.Esl,
            _ => Mo2PluginExtensionKind.Unsupported,
        };

    private static Mo2PluginHeaderSnapshot Malformed(
        IMo2RandomAccessFile file,
        Mo2PluginExtensionKind extension,
        string code,
        string message) =>
        Result(
            Mo2BinaryObservationStatus.Malformed,
            extension,
            null,
            null,
            [],
            null,
            null,
            file.InitialStamp,
            file.InitialStamp,
            [new(code, message)]);

    private static Mo2PluginHeaderSnapshot Result(
        Mo2BinaryObservationStatus status,
        Mo2PluginExtensionKind extension,
        bool? master,
        bool? light,
        ImmutableArray<Mo2PluginMasterReference> masters,
        uint? flags,
        string? fingerprint,
        Mo2RandomAccessStamp? before,
        Mo2RandomAccessStamp? after,
        ImmutableArray<Mo2ParseWarning> warnings) =>
        new(status, extension, master, light, masters, flags, fingerprint, before, after, warnings);
}
