using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2Tes4RecordGraphCollector(IMo2RandomAccessFileFactory files) : IMo2Tes4RecordGraphCollector
{
    private const int RecordHeaderSize = 24;
    private const uint CompressedRecordFlag = 0x0004_0000;
    private const uint DeletedRecordFlag = 0x0000_0020;
    private const uint InitiallyDisabledRecordFlag = 0x0000_0800;
    private readonly IMo2RandomAccessFileFactory files = files ?? throw new ArgumentNullException(nameof(files));

    public async Task<Mo2Tes4RecordGraphResult> CollectAsync(
        Mo2Tes4RecordGraphRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Limits);
        request.Limits.Validate();
        ValidateRequest(request);

        var budget = new ObservationBudget(request.Limits);
        var issues = ImmutableArray.CreateBuilder<Mo2Tes4RecordGraphIssue>();
        var states = ImmutableArray.CreateBuilder<PluginState>();
        var partial = false;
        try
        {
            foreach (var input in request.Plugins.OrderBy(value => value.SourceOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PluginState? state;
                try
                {
                    state = await ObservePluginAsync(input, budget, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    state = null;
                }
                if (state is null)
                {
                    partial = true;
                    issues.Add(new("mo2.tes4.plugin_malformed", $"Plugin '{SafeName(input.Name)}' is not a readable, stable TES4 file.", input.Name, input.CanonicalPath));
                    continue;
                }

                states.Add(state);
            }

            var keyComparer = CanonicalKeyComparer.Instance;
            var roots = new HashSet<Mo2Tes4CanonicalRecordKey>(request.Targets, keyComparer);
            if (request.CellScope is { } scope)
            {
                roots.Add(scope.Worldspace);
                foreach (var cell in scope.Cells) roots.Add(cell);
            }

            var traversed = new HashSet<Mo2Tes4CanonicalRecordKey>(keyComparer);
            var frontier = new HashSet<Mo2Tes4CanonicalRecordKey>(roots, keyComparer);
            var records = new Dictionary<Mo2Tes4CanonicalRecordKey, List<Mo2Tes4RecordSnapshot>>(keyComparer);
            var discoveries = new Dictionary<Mo2Tes4CanonicalRecordKey, Mo2Tes4CellScopeDiscovery>(keyComparer);
            var outputRecordCount = 0;
            var depth = 0;
            while (frontier.Count > 0)
            {
                if (depth > request.Limits.MaximumGraphDepth)
                {
                    partial = true;
                    issues.Add(new("mo2.tes4.graph_depth_limit", "Enable-parent or linked-reference traversal reached its configured depth limit."));
                    break;
                }

                foreach (var state in states)
                {
                    var scan = await ScanPluginAsync(
                        state,
                        frontier,
                        depth == 0 ? request.CellScope : null,
                        budget,
                        cancellationToken).ConfigureAwait(false);
                    partial |= scan.Partial;
                    issues.AddRange(scan.Issues);
                    foreach (var record in scan.Records)
                    {
                        if (!records.TryGetValue(record.Key, out var chain))
                        {
                            chain = [];
                            records.Add(record.Key, chain);
                        }

                        if (outputRecordCount >= request.Limits.MaximumOutputRecords)
                        {
                            throw new ObservationLimitException("mo2.tes4.output_record_limit", "TES4 record output exceeded its configured limit.");
                        }

                        chain.Add(record);
                        outputRecordCount++;
                    }

                    foreach (var discovery in scan.Discoveries)
                    {
                        if (discoveries.Count >= (request.CellScope?.MaximumReferences ?? 0) && !discoveries.ContainsKey(discovery.Key))
                        {
                            throw new ObservationLimitException("mo2.tes4.cell_reference_limit", "Cell-scope discovery exceeded its configured reference limit.");
                        }

                        if (!discoveries.TryGetValue(discovery.Key, out var prior) ||
                            Nullable.Compare(discovery.DistanceFromNearestSeed, prior.DistanceFromNearestSeed) < 0)
                        {
                            discoveries[discovery.Key] = discovery;
                        }
                    }
                }

                foreach (var key in frontier) traversed.Add(key);
                var next = new HashSet<Mo2Tes4CanonicalRecordKey>(keyComparer);
                foreach (var key in frontier)
                {
                    if (!records.TryGetValue(key, out var chain)) continue;
                    foreach (var record in chain)
                    {
                        if (record.BaseObject is { } baseObject && !traversed.Contains(baseObject)) next.Add(baseObject);
                        if (record.EnableParent is { } parent && !traversed.Contains(parent.Parent)) next.Add(parent.Parent);
                        foreach (var link in record.LinkedReferences)
                        {
                            if (!traversed.Contains(link.Reference)) next.Add(link.Reference);
                        }
                        foreach (var reference in record.RecordReferences)
                        {
                            if (!traversed.Contains(reference.Target)) next.Add(reference.Target);
                        }
                    }
                }

                if (traversed.Count + next.Count > request.Limits.MaximumGraphNodes)
                {
                    partial = true;
                    issues.Add(new("mo2.tes4.graph_node_limit", "Enable-parent or linked-reference traversal reached its configured node limit."));
                    break;
                }

                frontier = next;
                depth++;
            }

            var orderedKeys = traversed.OrderBy(KeyText, StringComparer.Ordinal).ToImmutableArray();
            var chains = orderedKeys.Select(key =>
            {
                var ordered = records.GetValueOrDefault(key, [])
                    .OrderBy(value => value.LoadOrder ?? int.MaxValue)
                    .ThenBy(value => value.SourceOrder)
                    .ThenBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.RecordOffset)
                    .ToImmutableArray();
                var winner = ordered
                    .Where(value => value.IsEnabled && value.LoadOrder is not null)
                    .OrderBy(value => value.LoadOrder)
                    .ThenBy(value => value.SourceOrder)
                    .LastOrDefault();
                return new Mo2Tes4OverrideChain(key, ordered, winner);
            }).ToImmutableArray();
            var orderedDiscoveries = discoveries.Values
                .OrderBy(value => KeyText(value.Key), StringComparer.Ordinal)
                .ToImmutableArray();
            var pluginObservations = states.Select(value => value.Observation).ToImmutableArray();
            var status = partial ? Mo2Tes4RecordGraphStatus.Partial : Mo2Tes4RecordGraphStatus.Complete;
            var fingerprint = Fingerprint(pluginObservations, chains, orderedDiscoveries);
            return new(
                status,
                pluginObservations,
                chains,
                orderedKeys,
                orderedDiscoveries,
                orderedDiscoveries.Select(value => value.Key).Distinct(keyComparer).ToImmutableArray(),
                budget.RecordHeaders,
                budget.BytesScanned,
                issues.OrderBy(value => value.Code, StringComparer.Ordinal).ThenBy(value => value.Detail, StringComparer.Ordinal).ToImmutableArray(),
                fingerprint);
        }
        catch (ObservationLimitException exception)
        {
            issues.Add(new(exception.Code, exception.Message));
            return new(
                Mo2Tes4RecordGraphStatus.Refused,
                states.Select(value => value.Observation).ToImmutableArray(),
                [], [], [], [],
                budget.RecordHeaders,
                budget.BytesScanned,
                issues.ToImmutable(),
                EmptyFingerprint());
        }
    }

    private async Task<PluginState?> ObservePluginAsync(
        Mo2Tes4PluginInput input,
        ObservationBudget budget,
        CancellationToken cancellationToken)
    {
        await using var file = await files.OpenReadAsync(input.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (file.Length < RecordHeaderSize) return null;
        var header = new byte[RecordHeaderSize];
        await ReadExactlyAsync(file, 0, header, budget, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8)) return null;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (headerSize > 16 * 1024 * 1024 || RecordHeaderSize + (long)headerSize > file.Length) return null;
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var data = new byte[checked((int)headerSize)];
        await ReadExactlyAsync(file, RecordHeaderSize, data, budget, cancellationToken).ConfigureAwait(false);
        if (!TryReadMasters(data, out var masters)) return null;
        var hash = await HashAsync(file, budget, cancellationToken).ConfigureAwait(false);
        var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
        if (after != file.InitialStamp) return null;
        var observation = new Mo2Tes4PluginObservation(
            input.Name,
            input.CanonicalPath,
            input.SourceOrder,
            input.LoadOrder,
            input.IsEnabled,
            (flags & 0x1) != 0,
            (flags & 0x200) != 0,
            masters,
            hash,
            file.Length,
            file.InitialStamp,
            after);
        return new(input, observation);
    }

    private async Task<PluginScan> ScanPluginAsync(
        PluginState state,
        HashSet<Mo2Tes4CanonicalRecordKey> targets,
        Mo2Tes4CellScopeRequest? cellScope,
        ObservationBudget budget,
        CancellationToken cancellationToken)
    {
        await using var file = await files.OpenReadAsync(state.Input.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (file.InitialStamp != state.Observation.Before)
        {
            return new([], [], [new("mo2.tes4.plugin_changed", $"Plugin '{SafeName(state.Input.Name)}' changed after its raw hash was recorded.", state.Input.Name, state.Input.CanonicalPath)], true);
        }

        var reader = new BufferedReader(file, budget, state.Input.Name, state.Observation.Masters, state.Observation.HasLightFlag, state.Input, state.Observation.RawSha256);
        try
        {
            await reader.ScanRangeAsync(0, file.Length, [], targets, cellScope, cancellationToken).ConfigureAwait(false);
            var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            if (after != file.InitialStamp)
            {
                reader.Issues.Add(new("mo2.tes4.plugin_changed", $"Plugin '{SafeName(state.Input.Name)}' changed during record collection.", state.Input.Name, state.Input.CanonicalPath));
                reader.Partial = true;
            }

            return new(reader.Records.ToImmutableArray(), reader.Discoveries.ToImmutableArray(), reader.Issues.ToImmutableArray(), reader.Partial);
        }
        catch (InvalidDataException exception)
        {
            return new([], [], [new("mo2.tes4.plugin_malformed", $"Plugin '{SafeName(state.Input.Name)}' is malformed: {exception.Message}", state.Input.Name, state.Input.CanonicalPath)], true);
        }
    }

    private sealed class BufferedReader(
        IMo2RandomAccessFile file,
        ObservationBudget budget,
        string pluginName,
        ImmutableArray<string> masters,
        bool isLight,
        Mo2Tes4PluginInput input,
        string rawSha256)
    {
        private readonly byte[] buffer = new byte[budget.Limits.BufferBytes];
        private long bufferStart = -1;
        private int bufferLength;

        public List<Mo2Tes4RecordSnapshot> Records { get; } = [];
        public List<Mo2Tes4CellScopeDiscovery> Discoveries { get; } = [];
        public ImmutableArray<Mo2Tes4RecordGraphIssue>.Builder Issues { get; } = ImmutableArray.CreateBuilder<Mo2Tes4RecordGraphIssue>();
        public bool Partial { get; set; }

        public async Task ScanRangeAsync(
            long start,
            long end,
            ImmutableArray<Mo2Tes4GroupAncestry> ancestry,
            HashSet<Mo2Tes4CanonicalRecordKey> targets,
            Mo2Tes4CellScopeRequest? cellScope,
            CancellationToken cancellationToken)
        {
            if (ancestry.Length > budget.Limits.MaximumGroupDepth)
                throw new ObservationLimitException("mo2.tes4.group_depth_limit", "TES4 group nesting exceeded its configured limit.");
            var position = start;
            var header = new byte[RecordHeaderSize];
            while (position < end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (end - position < RecordHeaderSize) throw new InvalidDataException($"truncated header at offset {position}");
                await ReadIntoAsync(position, header, cancellationToken).ConfigureAwait(false);
                budget.CountRecordHeader();
                var signature = Encoding.ASCII.GetString(header, 0, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                if (signature == "GRUP")
                {
                    if (size < RecordHeaderSize || size > end - position) throw new InvalidDataException($"invalid GRUP at offset {position}");
                    var rawLabel = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                    var groupType = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12, 4));
                    var canonicalLabel = GroupHasFormIdLabel(groupType) ? Resolve(rawLabel, reportIssue: false) : null;
                    var childAncestry = ancestry.Add(new(groupType, rawLabel, canonicalLabel, position, position + size));
                    await ScanRangeAsync(position + RecordHeaderSize, position + size, childAncestry, targets, cellScope, cancellationToken).ConfigureAwait(false);
                    position += size;
                    continue;
                }

                if (size > end - position - RecordHeaderSize) throw new InvalidDataException($"record extends beyond its group at offset {position}");
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
                var key = Resolve(rawFormId, reportIssue: false);
                var worldspace = ancestry.LastOrDefault(value => value.GroupType == 1)?.CanonicalLabel;
                var cell = ancestry.LastOrDefault(value => value.GroupType is 6 or 8 or 9 or 10)?.CanonicalLabel;
                var placement = Placement(ancestry);
                var exactTarget = key is not null && targets.Contains(key);
                var cellCandidate = signature == "REFR" && key is not null && cellScope is not null &&
                    worldspace is not null && CanonicalKeyComparer.Instance.Equals(worldspace, cellScope.Worldspace) &&
                    cell is not null && cellScope.Cells.Contains(cell, CanonicalKeyComparer.Instance);
                if (exactTarget || cellCandidate)
                {
                    if (size > budget.Limits.MaximumRecordDataBytes)
                        throw new ObservationLimitException("mo2.tes4.record_data_limit", "A matched TES4 record exceeds its configured data limit.");
                    var packed = new byte[checked((int)size)];
                    await ReadIntoAsync(position + RecordHeaderSize, packed, cancellationToken).ConfigureAwait(false);
                    var data = DecompressIfRequired(packed, flags, budget.Limits.MaximumRecordDataBytes);
                    var parsed = ParseData(data, signature);
                    if (!parsed.Issues.IsDefaultOrEmpty)
                    {
                        Partial = true;
                        foreach (var issue in parsed.Issues)
                        {
                            Issues.Add(new(issue.Code, $"Plugin '{SafeName(pluginName)}' record {signature} {rawFormId:X8}: {issue.Detail}", pluginName, input.CanonicalPath));
                        }
                    }
                    if (exactTarget && key is not null)
                    {
                        key = Resolve(rawFormId, reportIssue: true) ?? key;
                        var snapshot = new Mo2Tes4RecordSnapshot(
                            key,
                            signature,
                            pluginName,
                            input.SourceOrder,
                            input.LoadOrder,
                            input.IsEnabled,
                            rawSha256,
                            rawFormId,
                            flags,
                            (flags & DeletedRecordFlag) != 0,
                            (flags & InitiallyDisabledRecordFlag) != 0,
                            position,
                            size,
                            ancestry,
                            worldspace,
                            cell,
                            placement,
                            parsed.EditorId,
                            parsed.ModelPath,
                            Resolve(parsed.BaseObject),
                            Resolve(parsed.EnableParent) is { } parent ? new(parent, parsed.EnableParentFlags) : null,
                            parsed.Links.Select(value => new Mo2Tes4LinkedReference(Resolve(value.Reference)!, Resolve(value.Keyword))).Where(value => value.Reference is not null).ToImmutableArray(),
                            parsed.References.Select(value => Resolve(value.RawFormId) is { } target
                                ? new Mo2Tes4RecordReference(value.Kind, value.SubrecordSignature, value.RecordDataOffset, target)
                                : null).Where(value => value is not null).Select(value => value!).ToImmutableArray(),
                            parsed.ActorTemplateFlags,
                            parsed.Transform,
                            parsed.Scale);
                        Records.Add(snapshot);
                    }

                    if (cellCandidate && key is not null && worldspace is not null && cell is not null &&
                        TryGetDistance(parsed.Transform, cellScope!, out var distance))
                    {
                        Discoveries.Add(new(key, worldspace, cell, placement, parsed.Transform, distance));
                    }
                }

                position += RecordHeaderSize + size;
            }

            if (position != end) throw new InvalidDataException($"range ended at {position}, expected {end}");
        }

        private async Task ReadIntoAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            var written = 0;
            while (written < destination.Length)
            {
                if (offset + written >= bufferStart && offset + written < bufferStart + bufferLength)
                {
                    var bufferOffset = checked((int)(offset + written - bufferStart));
                    var available = Math.Min(bufferLength - bufferOffset, destination.Length - written);
                    buffer.AsMemory(bufferOffset, available).CopyTo(destination[written..]);
                    written += available;
                    continue;
                }

                bufferStart = offset + written;
                bufferLength = checked((int)Math.Min(buffer.Length, file.Length - bufferStart));
                if (bufferLength <= 0) throw new EndOfStreamException();
                budget.ReserveBytes(bufferLength);
                var read = 0;
                while (read < bufferLength)
                {
                    var count = await file.ReadAsync(bufferStart + read, buffer.AsMemory(read, bufferLength - read), cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw new EndOfStreamException();
                    read += count;
                }
            }
        }

        private Mo2Tes4CanonicalRecordKey? Resolve(uint? rawFormId, bool reportIssue = true)
        {
            if (rawFormId is null || rawFormId == 0) return null;
            var value = rawFormId.Value;
            var index = checked((int)(value >> 24));
            var origin = index < masters.Length ? masters[index] : index == masters.Length ? pluginName : null;
            if (origin is null)
            {
                if (reportIssue)
                {
                    Partial = true;
                    Issues.Add(new("mo2.tes4.formid_master_index", $"A requested record in plugin '{SafeName(pluginName)}' contains a FormID with an invalid file-local master index.", pluginName, input.CanonicalPath));
                }
                return null;
            }

            var local = value & 0x00ff_ffff;
            if (reportIssue && index == masters.Length && isLight && local > 0x0fff)
            {
                Partial = true;
                Issues.Add(new("mo2.tes4.light_formid_range", $"Light plugin '{SafeName(pluginName)}' contains a self record outside the compact FormID range.", pluginName, input.CanonicalPath));
            }

            return new(origin, local);
        }

        private static bool TryGetDistance(Mo2Tes4Transform? transform, Mo2Tes4CellScopeRequest scope, out float? distance)
        {
            distance = null;
            if (scope.SpatialSeeds.IsDefaultOrEmpty) return true;
            if (transform is null) return false;
            var minimumSquared = double.MaxValue;
            foreach (var seed in scope.SpatialSeeds)
            {
                var x = (double)transform.X - seed.X;
                var y = (double)transform.Y - seed.Y;
                var z = (double)transform.Z - seed.Z;
                minimumSquared = Math.Min(minimumSquared, x * x + y * y + z * z);
            }

            if (minimumSquared > (double)scope.Radius * scope.Radius) return false;
            distance = checked((float)Math.Sqrt(minimumSquared));
            return true;
        }
    }

    private static ParsedData ParseData(byte[] data, string signature)
    {
        string? editorId = null;
        string? modelPath = null;
        uint? baseObject = null;
        uint? enableParent = null;
        uint enableParentFlags = 0;
        var links = ImmutableArray.CreateBuilder<RawLink>();
        var references = ImmutableArray.CreateBuilder<RawReference>();
        var issues = ImmutableArray.CreateBuilder<RawParseIssue>();
        Mo2Tes4ActorTemplateFlags? actorTemplateFlags = null;
        Mo2Tes4Transform? transform = null;
        float? scale = null;
        var position = 0;
        uint? extendedSize = null;
        while (position < data.Length)
        {
            if (data.Length - position < 6) throw new InvalidDataException("truncated subrecord header");
            var subrecord = data.AsSpan(position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4, 2));
            position += 6;
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size > int.MaxValue || size > data.Length - position) throw new InvalidDataException("subrecord exceeds record data");
            var contentOffset = position;
            var content = data.AsSpan(position, checked((int)size));
            position += checked((int)size);
            if (subrecord.SequenceEqual("XXXX"u8))
            {
                if (size != 4) throw new InvalidDataException("invalid XXXX subrecord");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(content);
                continue;
            }

            var subrecordSignature = Encoding.ASCII.GetString(subrecord);
            if (subrecord.SequenceEqual("EDID"u8)) editorId = Encoding.Latin1.GetString(content).TrimEnd('\0');
            else if (subrecord.SequenceEqual("MODL"u8) && signature is not ("ARMO" or "ARMA")) modelPath = Encoding.Latin1.GetString(content).TrimEnd('\0');
            else if (IsPlacedReference(signature) && subrecord.SequenceEqual("NAME"u8) && size == 4) baseObject = BinaryPrimitives.ReadUInt32LittleEndian(content);
            else if (IsPlacedReference(signature) && subrecord.SequenceEqual("XESP"u8) && size >= 4)
            {
                enableParent = BinaryPrimitives.ReadUInt32LittleEndian(content);
                if (size >= 8) enableParentFlags = BinaryPrimitives.ReadUInt32LittleEndian(content[4..]);
            }
            else if (IsPlacedReference(signature) && subrecord.SequenceEqual("XLKR"u8)) ParseLinks(content, links);
            else if (IsPlacedReference(signature) && subrecord.SequenceEqual("DATA"u8) && size == 24)
            {
                transform = new(
                    ReadSingle(content, 0), ReadSingle(content, 4), ReadSingle(content, 8),
                    ReadSingle(content, 12), ReadSingle(content, 16), ReadSingle(content, 20));
            }
            else if (IsPlacedReference(signature) && subrecord.SequenceEqual("XSCL"u8) && size == 4) scale = ReadSingle(content, 0);
            else if (signature == "SPEL" && subrecord.SequenceEqual("EFID"u8))
                ParseSingleReference(content, Mo2Tes4RecordReferenceKind.SpellEffect, subrecordSignature, contentOffset, references, issues);
            else if (signature == "NPC_" && subrecord.SequenceEqual("ACBS"u8))
                actorTemplateFlags = ParseActorConfiguration(content, issues);
            else if (signature == "NPC_")
                ParseNpcReference(subrecordSignature, content, contentOffset, references, issues);
            else if (signature == "OTFT" && subrecord.SequenceEqual("INAM"u8))
                ParseReferenceArray(content, Mo2Tes4RecordReferenceKind.OutfitItem, subrecordSignature, contentOffset, references, issues);
            else if (signature == "ARMO" && subrecord.SequenceEqual("MODL"u8))
                ParseSingleReference(content, Mo2Tes4RecordReferenceKind.ArmorArmature, subrecordSignature, contentOffset, references, issues);
            else if (signature == "ARMO" && subrecord.SequenceEqual("TNAM"u8))
                ParseSingleReference(content, Mo2Tes4RecordReferenceKind.ArmorTemplate, subrecordSignature, contentOffset, references, issues);
            else if (signature == "MGEF" && subrecord.SequenceEqual("DATA"u8))
                ParseMagicEffectData(content, subrecordSignature, contentOffset, references, issues);
            else if (signature == "MGEF" && subrecord.SequenceEqual("ESCE"u8))
                ParseSingleReference(content, Mo2Tes4RecordReferenceKind.MagicEffectCounterEffect, subrecordSignature, contentOffset, references, issues);
        }

        if (extendedSize is not null) throw new InvalidDataException("orphaned XXXX subrecord");
        return new(editorId, modelPath, baseObject, enableParent, enableParentFlags, links.ToImmutable(), references.ToImmutable(), issues.ToImmutable(), actorTemplateFlags, transform, scale);
    }

    private static bool IsPlacedReference(string signature) => signature is "REFR" or "ACHR";

    private static Mo2Tes4ActorTemplateFlags? ParseActorConfiguration(
        ReadOnlySpan<byte> content,
        ImmutableArray<RawParseIssue>.Builder issues)
    {
        const int supportedSize = 24;
        const int templateFlagsOffset = 18;
        if (content.Length < supportedSize)
        {
            issues.Add(new("mo2.tes4.npc_acbs_size", $"NPC_ ACBS is {content.Length} bytes; at least {supportedSize} bytes are required for the supported Skyrim Special Edition layout."));
            return null;
        }

        return (Mo2Tes4ActorTemplateFlags)BinaryPrimitives.ReadUInt16LittleEndian(content[templateFlagsOffset..]);
    }

    private static void ParseNpcReference(
        string subrecordSignature,
        ReadOnlySpan<byte> content,
        int contentOffset,
        ImmutableArray<RawReference>.Builder references,
        ImmutableArray<RawParseIssue>.Builder issues)
    {
        var kind = subrecordSignature switch
        {
            "TPLT" => Mo2Tes4RecordReferenceKind.ActorTemplate,
            "PKID" => Mo2Tes4RecordReferenceKind.ActorPackage,
            "DOFT" => Mo2Tes4RecordReferenceKind.ActorDefaultOutfit,
            "SOFT" => Mo2Tes4RecordReferenceKind.ActorSleepingOutfit,
            "DPLT" => Mo2Tes4RecordReferenceKind.ActorDefaultPackageList,
            "SPOR" => Mo2Tes4RecordReferenceKind.ActorSpectatorPackageList,
            "OCOR" => Mo2Tes4RecordReferenceKind.ActorObserveDeadBodyPackageList,
            "GWOR" => Mo2Tes4RecordReferenceKind.ActorGuardWarnPackageList,
            "ECOR" => Mo2Tes4RecordReferenceKind.ActorCombatPackageList,
            _ => (Mo2Tes4RecordReferenceKind?)null,
        };
        if (kind is { } value)
            ParseSingleReference(content, value, subrecordSignature, contentOffset, references, issues);
    }

    private static void ParseMagicEffectData(
        ReadOnlySpan<byte> content,
        string subrecordSignature,
        int contentOffset,
        ImmutableArray<RawReference>.Builder references,
        ImmutableArray<RawParseIssue>.Builder issues)
    {
        const int supportedSize = 152;
        if (content.Length < supportedSize)
        {
            issues.Add(new("mo2.tes4.mgef_data_size", $"MGEF DATA is {content.Length} bytes; at least {supportedSize} bytes are required for the supported Skyrim Special Edition layout."));
            return;
        }

        AddReferenceAt(content, 8, Mo2Tes4RecordReferenceKind.MagicEffectAssociatedItem, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 24, Mo2Tes4RecordReferenceKind.MagicEffectCastingLight, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 32, Mo2Tes4RecordReferenceKind.MagicEffectHitShader, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 36, Mo2Tes4RecordReferenceKind.MagicEffectEnchantShader, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 72, Mo2Tes4RecordReferenceKind.MagicEffectProjectile, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 76, Mo2Tes4RecordReferenceKind.MagicEffectExplosion, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 92, Mo2Tes4RecordReferenceKind.MagicEffectCastingArt, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 96, Mo2Tes4RecordReferenceKind.MagicEffectHitEffectArt, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 100, Mo2Tes4RecordReferenceKind.MagicEffectImpactData, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 108, Mo2Tes4RecordReferenceKind.MagicEffectDualCastingArt, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 116, Mo2Tes4RecordReferenceKind.MagicEffectEnchantArt, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 120, Mo2Tes4RecordReferenceKind.MagicEffectHitVisuals, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 124, Mo2Tes4RecordReferenceKind.MagicEffectEnchantVisuals, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 128, Mo2Tes4RecordReferenceKind.MagicEffectEquipAbility, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 132, Mo2Tes4RecordReferenceKind.MagicEffectImageSpaceModifier, subrecordSignature, contentOffset, references);
        AddReferenceAt(content, 136, Mo2Tes4RecordReferenceKind.MagicEffectPerk, subrecordSignature, contentOffset, references);
    }

    private static void AddReferenceAt(
        ReadOnlySpan<byte> content,
        int offset,
        Mo2Tes4RecordReferenceKind kind,
        string subrecordSignature,
        int contentOffset,
        ImmutableArray<RawReference>.Builder references)
    {
        var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
        if (rawFormId != 0) references.Add(new(kind, subrecordSignature, contentOffset + offset, rawFormId));
    }

    private static void ParseSingleReference(
        ReadOnlySpan<byte> content,
        Mo2Tes4RecordReferenceKind kind,
        string subrecordSignature,
        int contentOffset,
        ImmutableArray<RawReference>.Builder references,
        ImmutableArray<RawParseIssue>.Builder issues)
    {
        if (content.Length != 4)
        {
            issues.Add(new("mo2.tes4.reference_subrecord_size", $"{subrecordSignature} for {kind} is {content.Length} bytes; expected 4."));
            return;
        }

        var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(content);
        if (rawFormId != 0) references.Add(new(kind, subrecordSignature, contentOffset, rawFormId));
    }

    private static void ParseReferenceArray(
        ReadOnlySpan<byte> content,
        Mo2Tes4RecordReferenceKind kind,
        string subrecordSignature,
        int contentOffset,
        ImmutableArray<RawReference>.Builder references,
        ImmutableArray<RawParseIssue>.Builder issues)
    {
        if (content.Length == 0 || content.Length % 4 != 0)
        {
            issues.Add(new("mo2.tes4.reference_subrecord_size", $"{subrecordSignature} for {kind} is {content.Length} bytes; expected one or more 4-byte FormIDs."));
            return;
        }

        for (var offset = 0; offset < content.Length; offset += 4)
        {
            var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
            if (rawFormId != 0) references.Add(new(kind, subrecordSignature, contentOffset + offset, rawFormId));
        }
    }

    private static void ParseLinks(ReadOnlySpan<byte> content, ImmutableArray<RawLink>.Builder links)
    {
        if (content.Length == 4)
        {
            links.Add(new(BinaryPrimitives.ReadUInt32LittleEndian(content), null));
            return;
        }

        if (content.Length % 8 != 0) throw new InvalidDataException("XLKR subrecord has an unsupported size");
        for (var offset = 0; offset < content.Length; offset += 8)
        {
            var reference = BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
            var keyword = BinaryPrimitives.ReadUInt32LittleEndian(content[(offset + 4)..]);
            links.Add(new(reference, keyword == 0 ? null : keyword));
        }
    }

    private static byte[] DecompressIfRequired(byte[] packed, uint flags, int maximumBytes)
    {
        if ((flags & CompressedRecordFlag) == 0) return packed;
        if (packed.Length < 6) throw new InvalidDataException("compressed record is truncated");
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(packed.AsSpan(0, 4));
        if (expected > maximumBytes) throw new ObservationLimitException("mo2.tes4.decompressed_record_limit", "A compressed TES4 record exceeds its configured expanded-size limit.");
        using var input = new MemoryStream(packed, 4, packed.Length - 4, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(checked((int)expected));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length > expected - read)
                throw new InvalidDataException("compressed record expanded beyond its declared size");
            output.Write(buffer, 0, read);
        }
        var data = output.ToArray();
        if (data.Length != expected) throw new InvalidDataException("compressed record expanded to an unexpected size");
        return data;
    }

    private static bool TryReadMasters(byte[] data, out ImmutableArray<string> masters)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        var position = 0;
        uint? extendedSize = null;
        while (position < data.Length)
        {
            if (data.Length - position < 6) { masters = []; return false; }
            var signature = data.AsSpan(position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4, 2));
            position += 6;
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size > int.MaxValue || size > data.Length - position) { masters = []; return false; }
            var content = data.AsSpan(position, checked((int)size));
            position += checked((int)size);
            if (signature.SequenceEqual("XXXX"u8))
            {
                if (size != 4) { masters = []; return false; }
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(content);
            }
            else if (signature.SequenceEqual("MAST"u8))
            {
                var nullIndex = content.IndexOf((byte)0);
                if (nullIndex <= 0) { masters = []; return false; }
                var name = Encoding.Latin1.GetString(content[..nullIndex]);
                if (name.Any(char.IsControl) || Path.GetFileName(name) != name) { masters = []; return false; }
                result.Add(name);
            }
        }

        masters = result.ToImmutable();
        return extendedSize is null;
    }

    private static async Task<string> HashAsync(IMo2RandomAccessFile file, ObservationBudget budget, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Math.Min(64 * 1024, checked((int)Math.Max(1, Math.Min(file.Length, 64 * 1024))))];
        long offset = 0;
        while (offset < file.Length)
        {
            var length = checked((int)Math.Min(buffer.Length, file.Length - offset));
            budget.ReserveBytes(length);
            var read = await file.ReadAsync(offset, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            offset += read;
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task ReadExactlyAsync(IMo2RandomAccessFile file, long offset, Memory<byte> destination, ObservationBudget budget, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            budget.ReserveBytes(destination.Length - total);
            var read = await file.ReadAsync(offset + total, destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static void ValidateRequest(Mo2Tes4RecordGraphRequest request)
    {
        if (request.Plugins.IsDefaultOrEmpty) throw new ArgumentException("At least one exact plugin input is required.", nameof(request));
        if (request.Plugins.Length > request.Limits.MaximumPlugins) throw new ArgumentException("Plugin input exceeds the configured limit.", nameof(request));
        if (request.Targets.IsDefaultOrEmpty && request.CellScope is null) throw new ArgumentException("At least one target or cell scope is required.", nameof(request));
        if (request.Targets.Length > request.Limits.MaximumTargetKeys) throw new ArgumentException("Target input exceeds the configured limit.", nameof(request));
        if (request.Plugins.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Plugins.Length)
            throw new ArgumentException("Plugin names must be unique case-insensitively.", nameof(request));
        if (request.Plugins.Select(value => value.SourceOrder).Distinct().Count() != request.Plugins.Length)
            throw new ArgumentException("Plugin source order values must be unique.", nameof(request));
        var enabledOrders = request.Plugins.Where(value => value.IsEnabled && value.LoadOrder is not null).Select(value => value.LoadOrder!.Value).ToArray();
        if (enabledOrders.Distinct().Count() != enabledOrders.Length) throw new ArgumentException("Enabled plugin load order values must be unique.", nameof(request));
        foreach (var plugin in request.Plugins)
        {
            if (string.IsNullOrWhiteSpace(plugin.Name) || Path.GetFileName(plugin.Name) != plugin.Name || Path.GetExtension(plugin.Name).ToLowerInvariant() is not (".esm" or ".esp" or ".esl"))
                throw new ArgumentException("Plugin inputs require safe plugin filenames.", nameof(request));
            if (!Path.IsPathFullyQualified(plugin.CanonicalPath)) throw new ArgumentException("Plugin paths must be absolute.", nameof(request));
            if (plugin.IsEnabled && plugin.LoadOrder is null) throw new ArgumentException("Enabled plugins require a load-order value.", nameof(request));
        }

        foreach (var key in request.Targets) ValidateKey(key, nameof(request));
        if (request.CellScope is { } scope)
        {
            ValidateKey(scope.Worldspace, nameof(request));
            if (scope.Cells.IsDefaultOrEmpty || scope.Cells.Length > scope.MaximumCells || scope.MaximumCells > 9)
                throw new ArgumentException("Cell scope requires one to nine bounded cells.", nameof(request));
            if (scope.MaximumReferences <= 0 || scope.MaximumReferences > 25_000)
                throw new ArgumentException("Cell scope reference limit must be between one and 25,000.", nameof(request));
            if (!float.IsFinite(scope.Radius) || scope.Radius <= 0 || scope.Radius > 4096)
                throw new ArgumentException("Cell scope radius must be finite and no greater than 4096 units.", nameof(request));
            foreach (var cell in scope.Cells) ValidateKey(cell, nameof(request));
            foreach (var seed in scope.SpatialSeeds)
            {
                if (!float.IsFinite(seed.X) || !float.IsFinite(seed.Y) || !float.IsFinite(seed.Z))
                    throw new ArgumentException("Spatial seeds must contain finite coordinates.", nameof(request));
            }
        }
        var rootCount = request.Targets.Length + (request.CellScope is null ? 0 : request.CellScope.Cells.Length + 1);
        if (rootCount > request.Limits.MaximumTargetKeys || rootCount > request.Limits.MaximumGraphNodes)
            throw new ArgumentException("Combined record and cell-scope roots exceed the configured graph limits.", nameof(request));
    }

    private static void ValidateKey(Mo2Tes4CanonicalRecordKey key, string parameter)
    {
        if (string.IsNullOrWhiteSpace(key.OriginPlugin) || Path.GetFileName(key.OriginPlugin) != key.OriginPlugin || key.LocalFormId > 0x00ff_ffff)
            throw new ArgumentException("Canonical record keys require a safe origin plugin and a 24-bit local FormID.", parameter);
    }

    private static string Fingerprint(
        ImmutableArray<Mo2Tes4PluginObservation> plugins,
        ImmutableArray<Mo2Tes4OverrideChain> chains,
        ImmutableArray<Mo2Tes4CellScopeDiscovery> discoveries)
    {
        var text = new StringBuilder("grid.mo2.tes4-record-graph.v1\n");
        foreach (var plugin in plugins.OrderBy(value => value.SourceOrder))
            text.Append(plugin.Name.ToUpperInvariant()).Append('|').Append(plugin.SourceOrder).Append('|').Append(plugin.LoadOrder).Append('|')
                .Append(plugin.IsEnabled).Append('|').Append(plugin.HasMasterFlag).Append('|').Append(plugin.HasLightFlag).Append('|')
                .Append(string.Join(',', plugin.Masters.Select(value => value.ToUpperInvariant()))).Append('|').Append(plugin.RawSha256).Append('\n');
        foreach (var chain in chains)
        {
            text.Append(KeyText(chain.Target)).Append('\n');
            foreach (var record in chain.Records)
            {
                text.Append(record.PluginName.ToUpperInvariant()).Append('|').Append(record.Signature).Append('|')
                    .Append(record.SourceOrder).Append('|').Append(record.LoadOrder).Append('|').Append(record.IsEnabled).Append('|')
                    .Append(record.RecordFlags.ToString("X8", CultureInfo.InvariantCulture)).Append('|').Append(record.Placement).Append('|')
                    .Append(KeyText(record.Worldspace)).Append('|').Append(KeyText(record.Cell)).Append('|').Append(record.EditorId).Append('|').Append(record.ModelPath).Append('|')
                    .Append(KeyText(record.BaseObject)).Append('|').Append(KeyText(record.EnableParent?.Parent)).Append('|')
                    .Append(record.EnableParent?.Flags.ToString("X8", CultureInfo.InvariantCulture)).Append('|')
                    .Append(string.Join(',', record.LinkedReferences.Select(value => $"{KeyText(value.Reference)}>{KeyText(value.Keyword)}"))).Append('|')
                    .Append(string.Join(',', record.RecordReferences.Select(value => $"{value.Kind}:{value.SubrecordSignature}:{value.RecordDataOffset}:{KeyText(value.Target)}"))).Append('|')
                    .Append(((ushort?)record.ActorTemplateFlags)?.ToString("X4", CultureInfo.InvariantCulture)).Append('|')
                    .Append(Float(record.Transform?.X)).Append('|').Append(Float(record.Transform?.Y)).Append('|').Append(Float(record.Transform?.Z)).Append('|')
                    .Append(Float(record.Transform?.RotationX)).Append('|').Append(Float(record.Transform?.RotationY)).Append('|').Append(Float(record.Transform?.RotationZ)).Append('|')
                    .Append(Float(record.Scale)).Append('\n');
            }
        }
        foreach (var discovery in discoveries)
            text.Append("D|").Append(KeyText(discovery.Key)).Append('|').Append(KeyText(discovery.Worldspace)).Append('|')
                .Append(KeyText(discovery.Cell)).Append('|').Append(discovery.Placement).Append('|')
                .Append(Float(discovery.Transform?.X)).Append('|').Append(Float(discovery.Transform?.Y)).Append('|').Append(Float(discovery.Transform?.Z)).Append('|')
                .Append(Float(discovery.DistanceFromNearestSeed)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static string EmptyFingerprint() => Convert.ToHexString(SHA256.HashData("grid.mo2.tes4-record-graph.refused.v1"u8)).ToLowerInvariant();
    private static string KeyText(Mo2Tes4CanonicalRecordKey? key) => key is null ? string.Empty : $"{key.OriginPlugin.ToUpperInvariant()}:{key.LocalFormId:X6}";
    private static string Float(float? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string SafeName(string value) => value.Length <= 260 ? value : value[..260];
    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes[offset..]));
    private static bool GroupHasFormIdLabel(int groupType) => groupType is 1 or 6 or 7 or 8 or 9 or 10;
    private static Mo2Tes4CellPlacement Placement(ImmutableArray<Mo2Tes4GroupAncestry> ancestry) =>
        ancestry.LastOrDefault(value => value.GroupType is 6 or 8 or 9 or 10)?.GroupType switch
        {
            6 => Mo2Tes4CellPlacement.CellChildren,
            8 => Mo2Tes4CellPlacement.Persistent,
            9 => Mo2Tes4CellPlacement.Temporary,
            10 => Mo2Tes4CellPlacement.VisibleDistant,
            _ => Mo2Tes4CellPlacement.None,
        };

    private sealed record PluginState(Mo2Tes4PluginInput Input, Mo2Tes4PluginObservation Observation);
    private sealed record ParsedData(string? EditorId, string? ModelPath, uint? BaseObject, uint? EnableParent, uint EnableParentFlags, ImmutableArray<RawLink> Links, ImmutableArray<RawReference> References, ImmutableArray<RawParseIssue> Issues, Mo2Tes4ActorTemplateFlags? ActorTemplateFlags, Mo2Tes4Transform? Transform, float? Scale);
    private sealed record RawLink(uint Reference, uint? Keyword);
    private sealed record RawReference(Mo2Tes4RecordReferenceKind Kind, string SubrecordSignature, int RecordDataOffset, uint RawFormId);
    private sealed record RawParseIssue(string Code, string Detail);
    private sealed record PluginScan(ImmutableArray<Mo2Tes4RecordSnapshot> Records, ImmutableArray<Mo2Tes4CellScopeDiscovery> Discoveries, ImmutableArray<Mo2Tes4RecordGraphIssue> Issues, bool Partial);

    private sealed class ObservationBudget(Mo2Tes4RecordGraphLimits limits)
    {
        public Mo2Tes4RecordGraphLimits Limits { get; } = limits;
        public long BytesScanned { get; private set; }
        public long RecordHeaders { get; private set; }
        public void ReserveBytes(long count)
        {
            if (count < 0 || BytesScanned > Limits.MaximumAggregateBytesScanned - count)
                throw new ObservationLimitException("mo2.tes4.aggregate_byte_limit", "TES4 collection reached its aggregate byte limit.");
            BytesScanned += count;
        }
        public void CountRecordHeader()
        {
            if (RecordHeaders >= Limits.MaximumRecordHeaders)
                throw new ObservationLimitException("mo2.tes4.record_header_limit", "TES4 collection reached its record-header limit.");
            RecordHeaders++;
        }
    }

    private sealed class ObservationLimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class CanonicalKeyComparer : IEqualityComparer<Mo2Tes4CanonicalRecordKey>
    {
        public static CanonicalKeyComparer Instance { get; } = new();
        public bool Equals(Mo2Tes4CanonicalRecordKey? left, Mo2Tes4CanonicalRecordKey? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.LocalFormId == right.LocalFormId &&
            left.OriginPlugin.Equals(right.OriginPlugin, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(Mo2Tes4CanonicalRecordKey value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.OriginPlugin), value.LocalFormId);
    }
}
