using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Reads only caller-supplied Skyrim asset paths. It never enumerates directories or
/// extracts an archive tree, and every materialized byte is covered by explicit limits.
/// </summary>
public sealed class Mo2SkyrimAssetInspectionService(IMo2RandomAccessFileFactory files)
{
    private const uint BsaCompressedByDefault = 0x0004;
    private const uint BsaEmbedFileNames = 0x0100;
    private const uint BsaCompressionToggle = 0x40000000;
    private readonly IMo2RandomAccessFileFactory _files = files ?? throw new ArgumentNullException(nameof(files));

    public async Task<Mo2AssetInspectionResult> InspectAsync(
        Mo2AssetInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Limits.Validate();
        if (request.Targets.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one exact asset target is required.", nameof(request));
        }

        if (request.Targets.Length > request.Limits.MaximumTargets)
        {
            return new(Mo2AssetInspectionStatus.Oversized, 0, [],
                [new("mo2.asset.target_limit", "The exact target count exceeds the configured limit.")]);
        }

        var ordered = request.Targets
            .OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.VirtualPath, StringComparer.Ordinal)
            .ThenBy(value => value.CanonicalSourcePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicate = ordered.GroupBy(
                value => $"{NormalizeVirtualPath(value.VirtualPath)}\n{value.CanonicalSourcePath}\n{value.ArchiveMemberPath}",
                StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new ArgumentException("Duplicate exact asset targets are not accepted.", nameof(request));
        }

        var processingOrder = ordered
            .OrderBy(value => value.CanonicalSourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.CanonicalSourcePath, StringComparer.Ordinal)
            .ThenBy(value => value.ArchiveMemberPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var results = ImmutableArray.CreateBuilder<Mo2AssetTargetInspection>(ordered.Length);
        var issues = ImmutableArray.CreateBuilder<Mo2AssetIssue>();
        var archiveCache = new Dictionary<string, CachedBsaIndex>(StringComparer.OrdinalIgnoreCase);
        string? cachedArchivePath = null;
        long aggregateBytes = 0;
        foreach (var target in processingOrder)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                issues.Add(new("mo2.asset.cancelled", "Asset inspection was cancelled before the next target."));
                return new(Mo2AssetInspectionStatus.Cancelled, aggregateBytes, results.ToImmutable(), issues.ToImmutable());
            }

            Mo2AssetTargetInspection result;
            if (string.IsNullOrWhiteSpace(target.ArchiveMemberPath))
            {
                result = await InspectLooseAsync(target, request.Limits, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!target.CanonicalSourcePath.Equals(cachedArchivePath, StringComparison.OrdinalIgnoreCase))
                {
                    archiveCache.Clear();
                    cachedArchivePath = target.CanonicalSourcePath;
                }
                var member = await ReadBsaMemberCoreAsync(
                    target.CanonicalSourcePath,
                    target.ArchiveMemberPath,
                    request.Limits,
                    archiveCache,
                    cancellationToken).ConfigureAwait(false);
                result = InspectMaterialized(
                    target,
                    Mo2AssetByteOrigin.BsaMember,
                    member.Status,
                    member.Content,
                    member.Before,
                    member.After,
                    member.Issues,
                    member.MemberSha256,
                    request.Limits);
            }

            if (result.Length is long length)
            {
                if (length > request.Limits.MaximumAggregateBytes - aggregateBytes)
                {
                    issues.Add(new("mo2.asset.aggregate_byte_limit", "The aggregate materialized-byte limit was reached."));
                    return new(Mo2AssetInspectionStatus.Oversized, aggregateBytes, OrderResults(results), issues.ToImmutable());
                }

                aggregateBytes += length;
            }

            results.Add(result);
        }

        var status = AggregateStatus(results);
        return new(status, aggregateBytes, OrderResults(results), issues.ToImmutable());
    }

    public Task<Mo2BsaMemberReadResult> ReadBsaMemberAsync(
        string archivePath,
        string exactMemberPath,
        Mo2AssetInspectionLimits limits,
        CancellationToken cancellationToken = default)
        => ReadBsaMemberCoreAsync(archivePath, exactMemberPath, limits, null, cancellationToken);

    private async Task<Mo2BsaMemberReadResult> ReadBsaMemberCoreAsync(
        string archivePath,
        string exactMemberPath,
        Mo2AssetInspectionLimits limits,
        IDictionary<string, CachedBsaIndex>? archiveCache,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactMemberPath);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        string normalizedMember;
        try
        {
            normalizedMember = NormalizeVirtualPath(exactMemberPath);
        }
        catch (ArgumentException exception)
        {
            return EmptyBsa(Mo2AssetInspectionStatus.Malformed, archivePath, exactMemberPath, 0, null, null,
                new("mo2.asset.member_path_invalid", exception.Message));
        }

        try
        {
            await using var archive = await _files.OpenReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var before = archive.InitialStamp;
            var archiveLength = archive.Length;
            Mo2ArchiveIndexSnapshot index;
            IReadOnlyDictionary<string, ImmutableArray<Mo2ArchiveMember>>? membersByPath = null;
            string? archiveSha256;
            if (archiveCache is not null && archiveCache.TryGetValue(before.CanonicalPath, out var cached))
            {
                if (cached.Before != before || cached.ArchiveLength != archiveLength)
                {
                    return EmptyBsa(Mo2AssetInspectionStatus.ChangedDuringRead, before.CanonicalPath, normalizedMember,
                        archiveLength, cached.Before, before,
                        new("mo2.asset.archive_changed_between_members", "The archive identity changed between exact member reads."));
                }
                index = cached.Index;
                membersByPath = cached.MembersByPath;
                archiveSha256 = cached.ArchiveSha256;
            }
            else
            {
                var parser = new Mo2BsaIndexParser();
                index = await parser.ParseAsync(
                    archive,
                    new(
                        limits.MaximumArchiveIndexBytes,
                        limits.MaximumArchiveMembers,
                        MaximumVirtualPathLength: 1_024,
                        MaximumSegmentLength: 255),
                    cancellationToken).ConfigureAwait(false);
                archiveSha256 = null;
                if (archiveCache is not null)
                {
                    membersByPath = BuildMemberLookup(index.Members);
                }
                var afterIndex = await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
                if (afterIndex == before && archiveCache is not null)
                {
                    archiveCache[before.CanonicalPath] = new(before, archiveLength, index, membersByPath!, null);
                }
            }
            if (index.Status != Mo2BinaryObservationStatus.Complete || index.Version != 105)
            {
                return EmptyBsa(
                    index.Status == Mo2BinaryObservationStatus.Oversized ? Mo2AssetInspectionStatus.Oversized :
                    index.Status == Mo2BinaryObservationStatus.Unsupported ? Mo2AssetInspectionStatus.Unsupported :
                    Mo2AssetInspectionStatus.Malformed,
                    before.CanonicalPath,
                    normalizedMember,
                    archiveLength,
                    before,
                    await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false),
                    new("mo2.asset.archive_index_unavailable", string.Join("; ", index.Warnings.Select(value => value.Message))));
            }

            var matches = membersByPath is not null && membersByPath.TryGetValue(normalizedMember, out var indexedMatches)
                ? indexedMatches
                : index.Members
                    .Where(value => value.VirtualPath.Equals(normalizedMember, StringComparison.OrdinalIgnoreCase))
                    .ToImmutableArray();
            if (matches.Length != 1)
            {
                return EmptyBsa(
                    matches.Length == 0 ? Mo2AssetInspectionStatus.Missing : Mo2AssetInspectionStatus.Malformed,
                    before.CanonicalPath,
                    normalizedMember,
                    archiveLength,
                    before,
                    await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false),
                    new(
                        matches.Length == 0 ? "mo2.asset.archive_member_missing" : "mo2.asset.archive_member_ambiguous",
                        matches.Length == 0
                            ? "The exact member is absent from this archive."
                            : "The archive contains case-colliding copies of the exact member."));
            }

            var member = matches[0];
            var storedBytes = (long)(member.PackedSize & ~BsaCompressionToggle);
            if (storedBytes <= 0 || storedBytes > limits.MaximumSourceBytes ||
                member.DataOffset > archiveLength || storedBytes > archiveLength - member.DataOffset)
            {
                return EmptyBsa(Mo2AssetInspectionStatus.Oversized, before.CanonicalPath, normalizedMember,
                    archiveLength, before, await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false),
                    new("mo2.asset.archive_member_bounds", "The exact member exceeds its declared or configured bounds."));
            }

            var stored = new byte[checked((int)storedBytes)];
            await ReadExactlyAsync(archive, member.DataOffset, stored, cancellationToken).ConfigureAwait(false);
            var cursor = 0;
            if ((index.ArchiveFlags.GetValueOrDefault() & BsaEmbedFileNames) != 0)
            {
                if (stored.Length < 1) throw new InvalidDataException("The embedded member name is truncated.");
                var nameBytes = stored[cursor++];
                if (nameBytes > stored.Length - cursor) throw new InvalidDataException("The embedded member name is truncated.");
                cursor += nameBytes;
            }

            var compressed = (index.ArchiveFlags.GetValueOrDefault() & BsaCompressedByDefault) != 0;
            if ((member.PackedSize & BsaCompressionToggle) != 0) compressed = !compressed;
            byte[] content;
            long unpackedBytes;
            if (compressed)
            {
                if (stored.Length - cursor < 4) throw new InvalidDataException("The compressed member size is truncated.");
                unpackedBytes = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(cursor, 4));
                cursor += 4;
                if (unpackedBytes <= 0 || unpackedBytes > limits.MaximumSourceBytes)
                {
                    return EmptyBsa(Mo2AssetInspectionStatus.Oversized, before.CanonicalPath, normalizedMember,
                        archiveLength, before, await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false),
                        new("mo2.asset.archive_member_expanded_limit", "The expanded member exceeds the configured limit."));
                }

                content = DecodeLz4Block(stored.AsSpan(cursor), checked((int)unpackedBytes));
            }
            else
            {
                unpackedBytes = stored.Length - cursor;
                content = stored.AsSpan(cursor).ToArray();
            }

            var after = await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            if (before.Identity != after.Identity || !before.CanonicalPath.Equals(after.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return EmptyBsa(Mo2AssetInspectionStatus.ChangedDuringRead, before.CanonicalPath, normalizedMember,
                    archiveLength, before, after,
                    new("mo2.asset.archive_changed_during_read", "The archive identity changed during the exact member read."));
            }

            if (archiveSha256 is null && limits.MaximumArchiveBytesHashed > 0 && archiveLength <= limits.MaximumArchiveBytesHashed)
            {
                archiveSha256 = await HashAsync(archive, archiveLength, limits.BufferBytes, cancellationToken).ConfigureAwait(false);
                var hashAfter = await archive.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
                if (before.Identity != hashAfter.Identity)
                {
                    return EmptyBsa(Mo2AssetInspectionStatus.ChangedDuringRead, before.CanonicalPath, normalizedMember,
                        archiveLength, before, hashAfter,
                        new("mo2.asset.archive_changed_during_hash", "The archive identity changed during hashing."));
                }

                after = hashAfter;
                if (archiveCache is not null)
                {
                    archiveCache[before.CanonicalPath] = new(
                        before,
                        archiveLength,
                        index,
                        membersByPath ?? BuildMemberLookup(index.Members),
                        archiveSha256);
                }
            }

            return new(
                Mo2AssetInspectionStatus.Complete,
                before.CanonicalPath,
                normalizedMember,
                archiveLength,
                archiveSha256,
                Sha256(content),
                member.SourceOrder,
                storedBytes,
                unpackedBytes,
                compressed,
                content.ToImmutableArray(),
                before,
                after,
                []);
        }
        catch (OperationCanceledException)
        {
            return EmptyBsa(Mo2AssetInspectionStatus.Cancelled, archivePath, normalizedMember, 0, null, null,
                new("mo2.asset.cancelled", "The exact archive-member read was cancelled."));
        }
        catch (FileNotFoundException exception)
        {
            return EmptyBsa(Mo2AssetInspectionStatus.Missing, archivePath, normalizedMember, 0, null, null,
                new("mo2.asset.archive_missing", exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyBsa(Mo2AssetInspectionStatus.Inaccessible, archivePath, normalizedMember, 0, null, null,
                new("mo2.asset.archive_inaccessible", exception.GetType().Name));
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            return EmptyBsa(Mo2AssetInspectionStatus.Malformed, archivePath, normalizedMember, 0, null, null,
                new("mo2.asset.archive_member_malformed", exception.Message));
        }
    }

    private static ImmutableArray<Mo2AssetTargetInspection> OrderResults(
        ImmutableArray<Mo2AssetTargetInspection>.Builder results) => results
        .OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(value => value.VirtualPath, StringComparer.Ordinal)
        .ThenBy(value => value.SourcePath, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();

    private static IReadOnlyDictionary<string, ImmutableArray<Mo2ArchiveMember>> BuildMemberLookup(
        ImmutableArray<Mo2ArchiveMember> members) => members
        .GroupBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.ToImmutableArray(),
            StringComparer.OrdinalIgnoreCase);

    private sealed record CachedBsaIndex(
        Mo2RandomAccessStamp Before,
        long ArchiveLength,
        Mo2ArchiveIndexSnapshot Index,
        IReadOnlyDictionary<string, ImmutableArray<Mo2ArchiveMember>> MembersByPath,
        string? ArchiveSha256);

    public Mo2NifInspection InspectNif(
        ReadOnlyMemory<byte> content,
        Mo2AssetInspectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (content.Length > limits.MaximumSourceBytes)
        {
            return NifFailure(Mo2AssetInspectionStatus.Oversized, "mo2.asset.nif_size_limit", "The NIF exceeds the configured byte limit.");
        }

        try
        {
            var bytes = content.Span;
            var newline = bytes[..Math.Min(bytes.Length, 256)].IndexOf((byte)'\n');
            if (newline < 0) throw new InvalidDataException("The NIF header line is missing or oversized.");
            var header = Encoding.ASCII.GetString(bytes[..newline]);
            if (!header.StartsWith("Gamebryo File Format, Version ", StringComparison.Ordinal))
            {
                return NifFailure(Mo2AssetInspectionStatus.Unsupported, "mo2.asset.nif_header_unsupported", "The NIF header family is unsupported.");
            }

            var reader = new SpanReader(bytes[(newline + 1)..]);
            var version = reader.UInt32();
            var endian = reader.Byte();
            if (version != 0x14020007 || endian != 1)
            {
                return new(Mo2AssetInspectionStatus.Unsupported, header, version, endian, null, null, null, null, [],
                    [new("mo2.asset.nif_version_unsupported", "Only little-endian Skyrim NIF 20.2.0.7 is supported.")]);
            }

            var userVersion = reader.UInt32();
            var blockCount = reader.UInt32();
            if (blockCount > limits.MaximumNifBlocks) throw new AssetLimitException("The NIF block count exceeds the configured limit.");
            uint? bethesdaVersion = null;
            if (userVersion == 12)
            {
                bethesdaVersion = reader.UInt32();
                for (var index = 0; index < 3; index++)
                {
                    var length = reader.Byte();
                    if (length > limits.MaximumNifStringBytes) throw new AssetLimitException("A NIF export string exceeds the configured limit.");
                    reader.Skip(length);
                }
            }

            var blockTypeCount = reader.UInt16();
            if (blockTypeCount > limits.MaximumNifBlocks) throw new AssetLimitException("The NIF block-type count exceeds the configured limit.");
            var blockTypes = new string[blockTypeCount];
            for (var index = 0; index < blockTypes.Length; index++)
            {
                blockTypes[index] = reader.SizedString(limits.MaximumNifStringBytes);
            }

            var blockTypeIndexes = new ushort[checked((int)blockCount)];
            for (var index = 0; index < blockTypeIndexes.Length; index++)
            {
                blockTypeIndexes[index] = reader.UInt16();
                if (blockTypeIndexes[index] >= blockTypes.Length) throw new InvalidDataException("A NIF block type index is invalid.");
            }

            long blockBytes = 0;
            var blockSizes = new int[checked((int)blockCount)];
            for (var index = 0; index < blockCount; index++)
            {
                var blockSize = reader.UInt32();
                if (blockSize > int.MaxValue) throw new AssetLimitException("A NIF block exceeds the configured byte limit.");
                blockSizes[index] = checked((int)blockSize);
                blockBytes = checked(blockBytes + blockSize);
                if (blockBytes > limits.MaximumSourceBytes) throw new AssetLimitException("The NIF block table exceeds the configured byte limit.");
            }

            var stringCount = reader.UInt32();
            if (stringCount > limits.MaximumNifStrings) throw new AssetLimitException("The NIF string count exceeds the configured limit.");
            var maximumStringLength = reader.UInt32();
            if (maximumStringLength > limits.MaximumNifStringBytes) throw new AssetLimitException("The NIF declares oversized strings.");
            var textures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < stringCount; index++)
            {
                var value = reader.SizedString(limits.MaximumNifStringBytes);
                if (value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    var normalized = NormalizeVirtualPath(value);
                    if (!normalized.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = $"textures\\{normalized}";
                    }

                    textures.Add(normalized);
                    if (textures.Count > limits.MaximumNifTexturePaths) throw new AssetLimitException("The NIF texture-path count exceeds the configured limit.");
                }
            }

            var groupCount = reader.UInt32();
            if (groupCount > limits.MaximumNifBlocks) throw new AssetLimitException("The NIF group count exceeds the configured limit.");
            for (var index = 0; index < groupCount; index++) _ = reader.UInt32();
            for (var index = 0; index < blockSizes.Length; index++)
            {
                var block = reader.Slice(blockSizes[index]);
                if (!blockTypes[blockTypeIndexes[index]].Equals("BSShaderTextureSet", StringComparison.Ordinal)) continue;
                var textureSet = new SpanReader(block);
                var textureCount = textureSet.UInt32();
                if (textureCount > limits.MaximumNifTexturePaths) throw new AssetLimitException("A NIF texture-set count exceeds the configured limit.");
                for (var textureIndex = 0; textureIndex < textureCount; textureIndex++)
                {
                    AddTexture(textureSet.SizedString(limits.MaximumNifStringBytes));
                }

                if (!textureSet.AtEnd) throw new InvalidDataException("A BSShaderTextureSet block has unaccounted trailing bytes.");
            }

            var rootCount = reader.UInt32();
            if (rootCount > blockCount) throw new InvalidDataException("The NIF root count exceeds its block count.");
            for (var index = 0; index < rootCount; index++)
            {
                var root = reader.Int32();
                if (root < 0 || root >= blockCount) throw new InvalidDataException("A NIF root index is invalid.");
            }

            if (!reader.AtEnd) throw new InvalidDataException("The NIF has unaccounted trailing bytes.");
            return new(
                Mo2AssetInspectionStatus.Complete,
                header,
                version,
                endian,
                userVersion,
                bethesdaVersion,
                checked((int)blockCount),
                checked((int)stringCount),
                textures.Order(StringComparer.OrdinalIgnoreCase).ThenBy(value => value, StringComparer.Ordinal).ToImmutableArray(),
                []);

            void AddTexture(string value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (!value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("A BSShaderTextureSet entry is not an exact DDS virtual path.");
                }

                var normalized = NormalizeVirtualPath(value);
                if (!normalized.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = $"textures\\{normalized}";
                }

                textures.Add(normalized);
                if (textures.Count > limits.MaximumNifTexturePaths) throw new AssetLimitException("The NIF texture-path count exceeds the configured limit.");
            }
        }
        catch (AssetLimitException exception)
        {
            return NifFailure(Mo2AssetInspectionStatus.Oversized, "mo2.asset.nif_limit", exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException)
        {
            return NifFailure(Mo2AssetInspectionStatus.Malformed, "mo2.asset.nif_malformed", exception.Message);
        }
    }

    public Mo2DdsInspection InspectDds(
        ReadOnlyMemory<byte> content,
        Mo2AssetInspectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (content.Length > limits.MaximumSourceBytes)
        {
            return DdsFailure(Mo2AssetInspectionStatus.Oversized, "mo2.asset.dds_size_limit", "The DDS exceeds the configured byte limit.");
        }

        try
        {
            var bytes = content.Span;
            if (bytes.Length < 128 || !bytes[..4].SequenceEqual("DDS "u8)) throw new InvalidDataException("The DDS signature is absent or truncated.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..8]) != 124 ||
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[76..80]) != 32)
            {
                throw new InvalidDataException("The DDS header sizes are invalid.");
            }

            var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..16]);
            var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..20]);
            var depth = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..28]);
            var mipCount = Math.Max(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..32]));
            if (width == 0 || height == 0 || depth > 1) throw new InvalidDataException("Only nonempty two-dimensional DDS assets are supported.");
            var fourCc = Encoding.ASCII.GetString(bytes[84..88]);
            var payloadOffset = 128;
            var (format, blockBytes, bitsPerPixel) = LegacyDdsFormat(fourCc, BinaryPrimitives.ReadUInt32LittleEndian(bytes[88..92]));
            if (fourCc == "DX10")
            {
                if (bytes.Length < 148) throw new InvalidDataException("The DDS DX10 header is truncated.");
                payloadOffset = 148;
                var dxgi = BinaryPrimitives.ReadUInt32LittleEndian(bytes[128..132]);
                (format, blockBytes, bitsPerPixel) = DxgiFormat(dxgi);
            }

            if (blockBytes == 0 && bitsPerPixel == 0)
            {
                return new(Mo2AssetInspectionStatus.Unsupported, width, height, mipCount, format, null,
                    bytes.Length - payloadOffset,
                    [new("mo2.asset.dds_format_unsupported", "The DDS pixel format is not in the bounded validator vocabulary.")]);
            }

            long expected = 0;
            var mipWidth = width;
            var mipHeight = height;
            for (var index = 0; index < mipCount; index++)
            {
                expected = checked(expected + (blockBytes > 0
                    ? checked((long)Math.Max(1u, (mipWidth + 3) / 4) * Math.Max(1u, (mipHeight + 3) / 4) * blockBytes)
                    : checked((long)mipWidth * mipHeight * bitsPerPixel / 8)));
                mipWidth = Math.Max(1u, mipWidth / 2);
                mipHeight = Math.Max(1u, mipHeight / 2);
            }

            var observed = bytes.Length - payloadOffset;
            if (observed < expected) throw new InvalidDataException("The DDS mip payload is truncated.");
            return new(Mo2AssetInspectionStatus.Complete, width, height, mipCount, format, expected, observed, []);
        }
        catch (AssetLimitException exception)
        {
            return DdsFailure(Mo2AssetInspectionStatus.Oversized, "mo2.asset.dds_limit", exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or ArgumentException)
        {
            return DdsFailure(Mo2AssetInspectionStatus.Malformed, "mo2.asset.dds_malformed", exception.Message);
        }
    }

    private async Task<Mo2AssetTargetInspection> InspectLooseAsync(
        Mo2AssetTarget target,
        Mo2AssetInspectionLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var file = await _files.OpenReadAsync(target.CanonicalSourcePath, cancellationToken).ConfigureAwait(false);
            var before = file.InitialStamp;
            if (file.Length <= 0 || file.Length > limits.MaximumSourceBytes || file.Length > int.MaxValue)
            {
                return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, Mo2AssetInspectionStatus.Oversized,
                    [], before, before, [new("mo2.asset.loose_size_limit", "The loose asset exceeds the configured limit.")], null, limits);
            }

            var content = new byte[checked((int)file.Length)];
            await ReadExactlyAsync(file, 0, content, cancellationToken).ConfigureAwait(false);
            var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            if (before.Identity != after.Identity || !before.CanonicalPath.Equals(after.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, Mo2AssetInspectionStatus.ChangedDuringRead,
                    [], before, after, [new("mo2.asset.loose_changed_during_read", "The loose asset identity changed during its read.")], null, limits);
            }

            var hash = Sha256(content);
            var status = !string.IsNullOrWhiteSpace(target.ExpectedSha256) &&
                !hash.Equals(target.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
                    ? Mo2AssetInspectionStatus.DigestMismatch
                    : Mo2AssetInspectionStatus.Complete;
            var issues = status == Mo2AssetInspectionStatus.DigestMismatch
                ? ImmutableArray.Create(new Mo2AssetIssue("mo2.asset.digest_mismatch", "The asset SHA-256 differs from the evidence-bound digest."))
                : ImmutableArray<Mo2AssetIssue>.Empty;
            return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, status, content.ToImmutableArray(), before, after, issues, hash, limits);
        }
        catch (OperationCanceledException)
        {
            return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, Mo2AssetInspectionStatus.Cancelled,
                [], null, null, [new("mo2.asset.cancelled", "The loose asset read was cancelled.")], null, limits);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, Mo2AssetInspectionStatus.Missing,
                [], null, null, [new("mo2.asset.loose_missing", exception.Message)], null, limits);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return InspectMaterialized(target, Mo2AssetByteOrigin.LooseFile, Mo2AssetInspectionStatus.Inaccessible,
                [], null, null, [new("mo2.asset.loose_inaccessible", exception.GetType().Name)], null, limits);
        }
    }

    private Mo2AssetTargetInspection InspectMaterialized(
        Mo2AssetTarget target,
        Mo2AssetByteOrigin origin,
        Mo2AssetInspectionStatus sourceStatus,
        ImmutableArray<byte> content,
        Mo2RandomAccessStamp? before,
        Mo2RandomAccessStamp? after,
        ImmutableArray<Mo2AssetIssue> sourceIssues,
        string? sha256,
        Mo2AssetInspectionLimits limits)
    {
        Mo2NifInspection? nif = null;
        Mo2DdsInspection? dds = null;
        Mo2PapyrusInspection? papyrus = null;
        var status = sourceStatus;
        var issues = sourceIssues.ToBuilder();
        if (sourceStatus == Mo2AssetInspectionStatus.Complete &&
            !string.IsNullOrWhiteSpace(target.ExpectedSha256) &&
            !string.Equals(sha256, target.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            status = Mo2AssetInspectionStatus.DigestMismatch;
            issues.Add(new("mo2.asset.digest_mismatch", "The asset SHA-256 differs from the evidence-bound digest."));
        }

        if (sourceStatus == Mo2AssetInspectionStatus.Complete || sourceStatus == Mo2AssetInspectionStatus.DigestMismatch)
        {
            if (target.Kind == Mo2AssetKind.Nif)
            {
                nif = InspectNif(content.AsMemory(), limits);
                if (status == Mo2AssetInspectionStatus.Complete) status = nif.Status;
                issues.AddRange(nif.Issues);
            }
            else if (target.Kind == Mo2AssetKind.Dds)
            {
                dds = InspectDds(content.AsMemory(), limits);
                if (status == Mo2AssetInspectionStatus.Complete) status = dds.Status;
                issues.AddRange(dds.Issues);
            }
            else
            {
                var parser = new Mo2PapyrusInspectionService();
                papyrus = target.Kind == Mo2AssetKind.Psc
                    ? parser.InspectPsc(content.AsMemory())
                    : parser.InspectPex(content.AsMemory());
                var papyrusStatus = papyrus.Status switch
                {
                    Mo2PapyrusInspectionStatus.Complete or Mo2PapyrusInspectionStatus.MetadataOnly => Mo2AssetInspectionStatus.Complete,
                    Mo2PapyrusInspectionStatus.Oversized => Mo2AssetInspectionStatus.Oversized,
                    Mo2PapyrusInspectionStatus.Unsupported => Mo2AssetInspectionStatus.Unsupported,
                    _ => Mo2AssetInspectionStatus.Malformed,
                };
                if (status == Mo2AssetInspectionStatus.Complete) status = papyrusStatus;
                issues.AddRange(papyrus.Issues);
            }
        }

        return new(
            NormalizeVirtualPath(target.VirtualPath),
            target.Kind,
            origin,
            status,
            before?.CanonicalPath ?? target.CanonicalSourcePath,
            target.ArchiveMemberPath is null ? null : NormalizeVirtualPath(target.ArchiveMemberPath),
            content.IsDefaultOrEmpty ? null : content.Length,
            sha256,
            before,
            after,
            target.Provider,
            nif,
            dds,
            papyrus,
            issues.ToImmutable());
    }

    private static (string Format, int BlockBytes, int BitsPerPixel) LegacyDdsFormat(string fourCc, uint bitsPerPixel) =>
        fourCc switch
        {
            "DXT1" => ("BC1", 8, 0),
            "DXT3" => ("BC2", 16, 0),
            "DXT5" => ("BC3", 16, 0),
            "ATI1" or "BC4U" => ("BC4", 8, 0),
            "ATI2" or "BC5U" => ("BC5", 16, 0),
            "\0\0\0\0" when bitsPerPixel is 8 or 16 or 24 or 32 => ($"RGB{bitsPerPixel}", 0, checked((int)bitsPerPixel)),
            _ => (fourCc.TrimEnd('\0'), 0, 0),
        };

    private static (string Format, int BlockBytes, int BitsPerPixel) DxgiFormat(uint format) => format switch
    {
        71 or 72 => ($"DXGI_{format}_BC1", 8, 0),
        74 or 75 => ($"DXGI_{format}_BC2", 16, 0),
        77 or 78 => ($"DXGI_{format}_BC3", 16, 0),
        80 or 81 => ($"DXGI_{format}_BC4", 8, 0),
        83 or 84 => ($"DXGI_{format}_BC5", 16, 0),
        95 or 96 => ($"DXGI_{format}_BC6", 16, 0),
        98 or 99 => ($"DXGI_{format}_BC7", 16, 0),
        28 or 29 => ($"DXGI_{format}_RGBA8", 0, 32),
        _ => ($"DXGI_{format}", 0, 0),
    };

    private static byte[] DecodeLz4Block(ReadOnlySpan<byte> source, int expectedLength)
    {
        if (source.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(source) == 0x184D2204)
        {
            return DecodeLz4Frame(source, expectedLength);
        }
        return DecodeLz4RawBlock(source, expectedLength, requireExactLength: true);
    }

    private static byte[] DecodeLz4Frame(ReadOnlySpan<byte> source, int expectedLength)
    {
        var input = 4;
        if (source.Length - input < 3) throw new InvalidDataException("The LZ4 frame header is truncated.");
        var flags = source[input++];
        var descriptor = source[input++];
        if ((flags & 0xc0) != 0x40 || (flags & 0x20) == 0 || (flags & 0x02) != 0 || (descriptor & 0x8f) != 0)
        {
            throw new InvalidDataException("The LZ4 frame descriptor is unsupported or reserved.");
        }

        var blockMaximum = ((descriptor >> 4) & 0x07) switch
        {
            4 => 64 * 1024,
            5 => 256 * 1024,
            6 => 1024 * 1024,
            7 => 4 * 1024 * 1024,
            _ => throw new InvalidDataException("The LZ4 frame block maximum is invalid."),
        };
        ulong? declaredContentSize = null;
        if ((flags & 0x08) != 0)
        {
            if (source.Length - input < 8) throw new InvalidDataException("The LZ4 content size is truncated.");
            declaredContentSize = BinaryPrimitives.ReadUInt64LittleEndian(source[input..]);
            input += 8;
            if (declaredContentSize != checked((ulong)expectedLength)) throw new InvalidDataException("The LZ4 content size is inconsistent.");
        }

        if ((flags & 0x01) != 0) throw new InvalidDataException("Dictionary-bound LZ4 frames are unsupported.");
        input++; // Header checksum; decompression and exact expanded length remain independently verified.
        if (input > source.Length) throw new InvalidDataException("The LZ4 frame header checksum is truncated.");
        using var output = new MemoryStream(expectedLength);
        while (true)
        {
            if (source.Length - input < 4) throw new InvalidDataException("The LZ4 block header is truncated.");
            var blockHeader = BinaryPrimitives.ReadUInt32LittleEndian(source[input..]);
            input += 4;
            if (blockHeader == 0) break;
            var uncompressed = (blockHeader & 0x80000000) != 0;
            var blockBytes = checked((int)(blockHeader & 0x7fffffff));
            if (blockBytes <= 0 || blockBytes > source.Length - input) throw new InvalidDataException("The LZ4 block is truncated or invalid.");
            var remaining = checked(expectedLength - (int)output.Length);
            if (remaining <= 0) throw new InvalidDataException("The LZ4 frame expands beyond its declared size.");
            if (uncompressed)
            {
                if (blockBytes > blockMaximum || blockBytes > remaining) throw new InvalidDataException("An uncompressed LZ4 block exceeds its bounds.");
                output.Write(source.Slice(input, blockBytes));
            }
            else
            {
                var decoded = DecodeLz4RawBlock(source.Slice(input, blockBytes), Math.Min(blockMaximum, remaining), requireExactLength: false);
                output.Write(decoded);
            }

            input += blockBytes;
            if ((flags & 0x10) != 0)
            {
                if (source.Length - input < 4) throw new InvalidDataException("The LZ4 block checksum is truncated.");
                input += 4;
            }
        }

        if ((flags & 0x04) != 0)
        {
            if (source.Length - input < 4) throw new InvalidDataException("The LZ4 content checksum is truncated.");
            input += 4;
        }

        if (input != source.Length || output.Length != expectedLength) throw new InvalidDataException("The LZ4 frame length is inconsistent.");
        return output.ToArray();
    }

    private static byte[] DecodeLz4RawBlock(ReadOnlySpan<byte> source, int outputCapacity, bool requireExactLength)
    {
        var output = new byte[outputCapacity];
        var input = 0;
        var written = 0;
        while (input < source.Length)
        {
            var token = source[input++];
            var literalLength = token >> 4;
            if (literalLength == 15) literalLength = checked(literalLength + ReadLz4Length(source, ref input));
            if (literalLength > source.Length - input || literalLength > output.Length - written)
            {
                throw new InvalidDataException("The LZ4 literal run exceeds its bounds.");
            }

            source.Slice(input, literalLength).CopyTo(output.AsSpan(written));
            input += literalLength;
            written += literalLength;
            if (input == source.Length) break;
            if (source.Length - input < 2) throw new InvalidDataException("The LZ4 match offset is truncated.");
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(source[input..]);
            input += 2;
            if (offset == 0 || offset > written) throw new InvalidDataException("The LZ4 match offset is invalid.");
            var matchLength = token & 0x0f;
            if (matchLength == 15) matchLength = checked(matchLength + ReadLz4Length(source, ref input));
            matchLength = checked(matchLength + 4);
            if (matchLength > output.Length - written) throw new InvalidDataException("The LZ4 match exceeds the expanded size.");
            for (var index = 0; index < matchLength; index++)
            {
                output[written + index] = output[written - offset + index];
            }

            written += matchLength;
        }

        if ((requireExactLength && written != output.Length) || input != source.Length) throw new InvalidDataException("The LZ4 expanded length is inconsistent.");
        return written == output.Length ? output : output.AsSpan(0, written).ToArray();
    }

    private static int ReadLz4Length(ReadOnlySpan<byte> source, ref int input)
    {
        var total = 0;
        byte next;
        do
        {
            if (input >= source.Length) throw new InvalidDataException("The LZ4 extended length is truncated.");
            next = source[input++];
            total = checked(total + next);
        } while (next == byte.MaxValue);
        return total;
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
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static async Task<string> HashAsync(
        IMo2RandomAccessFile file,
        long length,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[bufferBytes];
        long offset = 0;
        while (offset < length)
        {
            var requested = checked((int)Math.Min(buffer.Length, length - offset));
            var read = await file.ReadAsync(offset, buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static Mo2AssetInspectionStatus AggregateStatus(IEnumerable<Mo2AssetTargetInspection> targets)
    {
        var statuses = targets.Select(value => value.Status).ToArray();
        if (statuses.All(value => value == Mo2AssetInspectionStatus.Complete)) return Mo2AssetInspectionStatus.Complete;
        foreach (var status in new[]
        {
            Mo2AssetInspectionStatus.Cancelled,
            Mo2AssetInspectionStatus.ChangedDuringRead,
            Mo2AssetInspectionStatus.Oversized,
            Mo2AssetInspectionStatus.DigestMismatch,
            Mo2AssetInspectionStatus.Missing,
            Mo2AssetInspectionStatus.Inaccessible,
            Mo2AssetInspectionStatus.Unsupported,
            Mo2AssetInspectionStatus.Malformed,
        })
        {
            if (statuses.Contains(status)) return status;
        }

        return Mo2AssetInspectionStatus.Malformed;
    }

    private static string NormalizeVirtualPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 1_024 || value.Any(char.IsControl) || value.StartsWith('\\') || value.StartsWith('/') || value.Contains(':'))
        {
            throw new ArgumentException("The virtual path is absolute, contains control characters, or exceeds its limit.", nameof(value));
        }

        var segments = value.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Length > 255))
        {
            throw new ArgumentException("The virtual path contains an unsafe segment.", nameof(value));
        }

        return string.Join('\\', segments);
    }

    private static Mo2BsaMemberReadResult EmptyBsa(
        Mo2AssetInspectionStatus status,
        string archivePath,
        string memberPath,
        long archiveLength,
        Mo2RandomAccessStamp? before,
        Mo2RandomAccessStamp? after,
        Mo2AssetIssue issue) =>
        new(status, archivePath, memberPath, archiveLength, null, null, null, null, null, null, [], before, after, [issue]);

    private static Mo2NifInspection NifFailure(Mo2AssetInspectionStatus status, string code, string detail) =>
        new(status, null, null, null, null, null, null, null, [], [new(code, detail)]);

    private static Mo2DdsInspection DdsFailure(Mo2AssetInspectionStatus status, string code, string detail) =>
        new(status, null, null, null, null, null, null, [new(code, detail)]);

    private sealed class AssetLimitException(string message) : Exception(message);

    private ref struct SpanReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public bool AtEnd => _position == _bytes.Length;

        public byte Byte()
        {
            Require(1);
            return _bytes[_position++];
        }

        public ushort UInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes[_position..]);
            _position += 2;
            return value;
        }

        public uint UInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }

        public int Int32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes[_position..]);
            _position += 4;
            return value;
        }

        public string SizedString(int maximumBytes)
        {
            var length = UInt32();
            if (length > maximumBytes) throw new AssetLimitException("A NIF string exceeds the configured limit.");
            Require(checked((int)length));
            var value = Encoding.UTF8.GetString(_bytes.Slice(_position, checked((int)length)));
            _position += checked((int)length);
            return value.TrimEnd('\0');
        }

        public void Skip(long count)
        {
            if (count < 0 || count > int.MaxValue) throw new InvalidDataException("A NIF length is invalid.");
            Require(checked((int)count));
            _position += checked((int)count);
        }

        public ReadOnlySpan<byte> Slice(int count)
        {
            Require(count);
            var value = _bytes.Slice(_position, count);
            _position += count;
            return value;
        }

        private void Require(int count)
        {
            if (count < 0 || count > _bytes.Length - _position) throw new InvalidDataException("The NIF is truncated.");
        }
    }
}
