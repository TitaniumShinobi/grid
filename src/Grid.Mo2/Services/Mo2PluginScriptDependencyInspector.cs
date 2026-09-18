using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2PluginScriptDependencyInspector(IMo2RandomAccessFileFactory files)
{
    private const int RecordHeaderSize = 24;
    private const uint CompressedRecordFlag = 0x0004_0000;
    private const uint DeletedRecordFlag = 0x0000_0020;
    private const uint InitiallyDisabledRecordFlag = 0x0000_0800;
    private static readonly HashSet<string> DiagnosticRecordSignatures = new(StringComparer.Ordinal)
    {
        "REFR", "ACHR", "NPC_", "QUST", "PACK", "SCEN", "DIAL", "INFO",
        "DOOR", "FURN", "SPEL", "MGEF", "ARMO", "ALCH", "INGR", "COBJ",
        "KYWD", "OTFT", "LVLI", "FLST", "FACT", "RACE", "CELL", "WRLD",
        "LIGH", "STAT", "MSTT", "TXST", "LTEX",
    };
    private readonly IMo2RandomAccessFileFactory files = files ?? throw new ArgumentNullException(nameof(files));

    public async Task<Mo2PluginScriptDependencyResult> InspectAsync(
        Mo2PluginScriptDependencyRequest request,
        CancellationToken cancellationToken = default,
        Func<Mo2PluginScriptDependencyProgress, CancellationToken, ValueTask>? reportProgress = null,
        Func<Mo2PluginRecordCatalogEntry, CancellationToken, ValueTask>? emitRecordCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Limits);
        request.Limits.Validate();
        if (request.Plugins.IsDefault || request.AvailableVirtualPaths.IsDefault)
            throw new ArgumentException("Plugin and virtual-path collections must be initialized.", nameof(request));

        var enabled = request.Plugins.Where(value => value.IsEnabled)
            .OrderBy(value => value.LoadOrder ?? int.MaxValue)
            .ThenBy(value => value.SourceOrder)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var available = request.AvailableVirtualPaths
            .Select(NormalizeVirtualPath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var effectiveRecords = new Dictionary<string, RecordDependencies>(StringComparer.OrdinalIgnoreCase);
        var conflictGroups = new Dictionary<string, ConflictAggregate>(StringComparer.OrdinalIgnoreCase);
        var provenanceGroups = new Dictionary<string, ProvenanceAggregate>(StringComparer.OrdinalIgnoreCase);
        var recordCatalog = ImmutableArray.CreateBuilder<Mo2PluginRecordCatalogEntry>();
        using var recordCatalogHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long recordCatalogEntryCount = 0;
        var issues = ImmutableArray.CreateBuilder<Mo2PluginScriptDependencyIssue>();
        var budget = new Budget(request.Limits);
        var partial = false;
        long pluginsScanned = 0;
        if (enabled.Length > request.Limits.MaximumPlugins)
            return Refused("mo2.script_dependency.plugin_limit", "Enabled plugin count exceeds the configured inspection limit.");
        if (enabled.Any(value => value.LoadOrder is null))
        {
            AddIssue("mo2.script_dependency.load_order_incomplete",
                "One or more enabled plugins have no established right-panel load order; effective record winners cannot be claimed.");
            partial = true;
        }

        for (var pluginIndex = 0; pluginIndex < enabled.Length; pluginIndex++)
        {
            var plugin = enabled[pluginIndex];
            cancellationToken.ThrowIfCancellationRequested();
            await ReportProgressAsync("Started").ConfigureAwait(false);
            if (Path.GetFileName(plugin.Name) != plugin.Name || !Path.IsPathFullyQualified(plugin.CanonicalPath))
            {
                AddIssue("mo2.script_dependency.plugin_path_invalid", "The plugin identity or path is not a safe exact file.", plugin.Name);
                partial = true;
                await ReportProgressAsync("Skipped").ConfigureAwait(false);
                continue;
            }

            try
            {
                partial |= await InspectPluginAsync(plugin, effectiveRecords, conflictGroups, provenanceGroups, CaptureRecordCatalogAsync, issues, budget, cancellationToken).ConfigureAwait(false);
                pluginsScanned++;
                await ReportProgressAsync("Completed").ConfigureAwait(false);
            }
            catch (InspectionLimitException exception)
            {
                await ReportProgressAsync("Refused").ConfigureAwait(false);
                return Result(Mo2PluginScriptDependencyStatus.Refused,
                    issues.AddAndReturn(new(exception.Code, exception.Message, plugin.Name)),
                    pluginsScanned);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                AddIssue("mo2.script_dependency.plugin_unreadable",
                    $"The plugin could not be inspected completely ({exception.GetType().Name}: {exception.Message}).", plugin.Name);
                partial = true;
                await ReportProgressAsync("Unreadable").ConfigureAwait(false);
            }

            async ValueTask ReportProgressAsync(string stage)
            {
                if (reportProgress is null) return;
                await reportProgress(new(
                    pluginIndex + 1,
                    enabled.Length,
                    plugin.Name,
                    stage,
                    pluginsScanned,
                    budget.RecordHeaders,
                    budget.BytesScanned), cancellationToken).ConfigureAwait(false);
            }
        }

        return Result(partial ? Mo2PluginScriptDependencyStatus.Partial : Mo2PluginScriptDependencyStatus.Complete,
            issues, pluginsScanned);

        async ValueTask<bool> CaptureRecordCatalogAsync(Mo2PluginRecordCatalogEntry entry, CancellationToken token)
        {
            if (recordCatalogEntryCount >= request.Limits.MaximumRecordCatalogEntries) return false;
            AppendRecordCatalogHash(recordCatalogHash, entry);
            recordCatalogEntryCount++;
            if (emitRecordCatalog is null)
                recordCatalog.Add(entry);
            else
                await emitRecordCatalog(entry, token).ConfigureAwait(false);
            return true;
        }

        void AddIssue(string code, string detail, string? pluginName = null, long? recordOffset = null)
        {
            if (issues.Count >= request.Limits.MaximumIssues)
                throw new InspectionLimitException("mo2.script_dependency.issue_limit", "Script-dependency issue output exceeded its configured limit.");
            issues.Add(new(code, detail, pluginName, recordOffset));
        }

        Mo2PluginScriptDependencyResult Result(
            Mo2PluginScriptDependencyStatus status,
            ImmutableArray<Mo2PluginScriptDependencyIssue>.Builder resultIssues,
            long scanned)
        {
            var references = effectiveRecords.Values
                .SelectMany(value => value.ScriptReferences)
                .ToImmutableArray();
            var assetReferences = effectiveRecords.Values
                .SelectMany(value => value.AssetReferences)
                .ToImmutableArray();
            var orderedReferences = references
                .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ScriptName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.RecordOffset)
                .ToImmutableArray();
            var missing = orderedReferences
                .Where(value => !available.Contains(value.RequiredVirtualPath))
                .GroupBy(value => (value.PluginName, value.ScriptName, value.RequiredVirtualPath), ScriptKeyComparer.Instance)
                .Select(group => new Mo2MissingPluginScriptDependency(
                    group.Key.PluginName,
                    group.Key.ScriptName,
                    group.Key.RequiredVirtualPath,
                    group.Count(),
                    group.Take(8).ToImmutableArray()))
                .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.ScriptName, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var orderedAssets = assetReferences
                .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.RecordOffset)
                .ToImmutableArray();
            var missingAssets = orderedAssets
                .Where(value => !available.Contains(value.RequiredVirtualPath))
                .GroupBy(value => (value.PluginName, value.RequiredVirtualPath, value.Kind), AssetKeyComparer.Instance)
                .Select(group => new Mo2MissingPluginAssetDependency(
                    group.Key.PluginName,
                    group.Key.RequiredVirtualPath,
                    group.Key.Kind,
                    group.Count(),
                    group.Take(8).ToImmutableArray()))
                .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var recordConflicts = conflictGroups.Values
                .Select(value => value.ToSummary())
                .OrderBy(value => value.RecordSignature, StringComparer.Ordinal)
                .ThenBy(value => value.WinningPlugin, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.PreviousPlugin, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var recordProvenance = provenanceGroups.Values
                .Select(value => value.ToSummary())
                .OrderBy(value => value.RecordSignature, StringComparer.Ordinal)
                .ThenBy(value => value.LoadOrder ?? int.MaxValue)
                .ThenBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var orderedCatalog = recordCatalog
                .OrderBy(value => value.RecordSignature, StringComparer.Ordinal)
                .ThenBy(value => value.OriginPlugin, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.LocalFormId)
                .ThenBy(value => value.LoadOrder ?? int.MaxValue)
                .ThenBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.RecordOffset)
                .ToImmutableArray();
            return new(status, orderedReferences, missing, resultIssues.ToImmutable(), scanned,
                budget.RecordHeaders, budget.BytesScanned,
                HashInspection(status, scanned, budget, orderedReferences, orderedAssets, recordConflicts, recordProvenance,
                    recordCatalogEntryCount, GetRecordCatalogHash(recordCatalogHash), resultIssues),
                orderedAssets, missingAssets, recordConflicts, recordProvenance, orderedCatalog, recordCatalogEntryCount);
        }

        Mo2PluginScriptDependencyResult Refused(string code, string detail)
        {
            issues.Add(new(code, detail));
            return Result(Mo2PluginScriptDependencyStatus.Refused, issues, 0);
        }
    }

    private async Task<bool> InspectPluginAsync(
        Mo2Tes4PluginInput plugin,
        Dictionary<string, RecordDependencies> effectiveRecords,
        Dictionary<string, ConflictAggregate> conflictGroups,
        Dictionary<string, ProvenanceAggregate> provenanceGroups,
        Func<Mo2PluginRecordCatalogEntry, CancellationToken, ValueTask<bool>> captureRecordCatalog,
        ImmutableArray<Mo2PluginScriptDependencyIssue>.Builder issues,
        Budget budget,
        CancellationToken cancellationToken)
    {
        await using var file = await files.OpenReadAsync(plugin.CanonicalPath, cancellationToken).ConfigureAwait(false);
        if (file.Length < RecordHeaderSize) throw new InvalidDataException("TES4 header is truncated.");
        var position = 0L;
        var ends = new Stack<long>();
        ends.Push(file.Length);
        var header = new byte[RecordHeaderSize];
        var masters = ImmutableArray<string>.Empty;
        var sawTes4 = false;
        var partial = false;
        while (position < file.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (ends.Count > 1 && position == ends.Peek()) ends.Pop();
            if (position > ends.Peek() || ends.Peek() - position < RecordHeaderSize)
                throw new InvalidDataException($"Record hierarchy is truncated at offset {position}.");

            await ReadExactlyAsync(file, position, header, budget, cancellationToken).ConfigureAwait(false);
            budget.CountRecordHeader();
            var signature = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (signature == "GRUP")
            {
                if (size < RecordHeaderSize || position + size > ends.Peek())
                    throw new InvalidDataException($"Invalid GRUP size at offset {position}.");
                ends.Push(position + size);
                position += RecordHeaderSize;
                continue;
            }

            if (!IsSignature(signature) || position + RecordHeaderSize + size > ends.Peek())
                throw new InvalidDataException($"Invalid record header at offset {position}.");
            if (size > budget.Limits.MaximumRecordDataBytes)
                throw new InspectionLimitException("mo2.script_dependency.record_data_limit", $"Record at offset {position} exceeds the configured data limit.");
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var formId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            var packed = new byte[checked((int)size)];
            await ReadExactlyAsync(file, position + RecordHeaderSize, packed, budget, cancellationToken).ConfigureAwait(false);
            var data = Decompress(packed, flags, budget.Limits.MaximumRecordDataBytes);
            var subrecords = EnumerateSubrecords(data).ToArray();
            if (signature == "TES4")
            {
                if (sawTes4 || position != 0)
                    throw new InvalidDataException("The TES4 header is duplicated or is not the first record.");
                masters = ParseMasters(subrecords);
                sawTes4 = true;
                position += RecordHeaderSize + size;
                continue;
            }
            if (!sawTes4) throw new InvalidDataException("The plugin has no leading TES4 header.");

            var recordReferences = ImmutableArray.CreateBuilder<Mo2PluginScriptReference>();
            var recordAssetReferences = ImmutableArray.CreateBuilder<Mo2PluginAssetReference>();
            foreach (var subrecord in subrecords)
            {
                if (subrecord.Signature == "VMAD")
                {
                    var parsed = ParseVmad(subrecord.Data);
                    foreach (var scriptName in parsed.ScriptNames)
                    {
                        budget.CountScriptReference();
                        if (TryGetRequiredVirtualPath(scriptName, out var required))
                            recordReferences.Add(new(plugin.Name, signature, formId, position, scriptName, required));
                        else if (issues.Count < budget.Limits.MaximumIssues)
                            issues.Add(new("mo2.script_dependency.script_name_invalid", "A VMAD script name could not be converted to a safe virtual path.", plugin.Name, position));
                    }
                    if (!parsed.Complete && issues.Count < budget.Limits.MaximumIssues)
                        issues.Add(new("mo2.script_dependency.vmad_partial", "A VMAD property payload was unsupported or malformed; later scripts in this record were not inferred.", plugin.Name, position));
                }
                else if (TryGetRequiredAssetVirtualPath(subrecord.Signature, subrecord.Data.Span, out var declaredPath, out var requiredAsset, out var kind, out var candidate))
                {
                    budget.CountAssetReference();
                    recordAssetReferences.Add(new(plugin.Name, signature, formId, position, subrecord.Signature, declaredPath, requiredAsset, kind));
                }
                else if (candidate && issues.Count < budget.Limits.MaximumIssues)
                    issues.Add(new("mo2.asset_dependency.path_invalid", $"A {subrecord.Signature} asset path could not be normalized safely.", plugin.Name, position));
            }
            if (TryGetCanonicalRecordKey(plugin.Name, masters, formId, out var recordKey))
            {
                var isOverride = effectiveRecords.TryGetValue(recordKey, out var prior);
                RecordObservation? observation = null;
                if (DiagnosticRecordSignatures.Contains(signature))
                {
                    var separator = recordKey.IndexOf('|');
                    observation = new(
                        recordKey[..separator],
                        Convert.ToUInt32(recordKey[(separator + 1)..], 16),
                        signature,
                        plugin.Name,
                        plugin.LoadOrder,
                        formId,
                        position,
                        flags,
                        TryGetEditorId(subrecords),
                        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
                    var provenanceKey = $"{signature}\u001f{plugin.Name}";
                    if (!provenanceGroups.TryGetValue(provenanceKey, out var provenance))
                    {
                        if (provenanceGroups.Count >= budget.Limits.MaximumRecordProvenanceGroups)
                        {
                            if (!issues.Any(value => value.Code == "mo2.record_provenance.group_limit") && issues.Count < budget.Limits.MaximumIssues)
                                issues.Add(new("mo2.record_provenance.group_limit", "Whole-profile record-provenance grouping reached its configured limit; remaining provenance groups were not retained."));
                            partial = true;
                        }
                        else
                        {
                            provenance = new(signature, plugin.Name, plugin.LoadOrder);
                            provenanceGroups.Add(provenanceKey, provenance);
                        }
                    }
                    provenance?.Add(observation, isOverride);
                    if (!await captureRecordCatalog(new(
                            observation.Signature,
                            observation.OriginPlugin,
                            observation.LocalFormId,
                            observation.PluginName,
                            observation.LoadOrder,
                            observation.RawFormId,
                            observation.RecordOffset,
                            observation.RecordFlags,
                            observation.EditorId,
                            observation.DataSha256,
                            isOverride), cancellationToken).ConfigureAwait(false))
                    {
                        if (!issues.Any(value => value.Code == "mo2.record_catalog.entry_limit") && issues.Count < budget.Limits.MaximumIssues)
                            issues.Add(new("mo2.record_catalog.entry_limit", "Whole-profile record catalog reached its configured entry limit; remaining records were not retained."));
                        partial = true;
                    }
                }
                if (observation is not null && isOverride && prior!.Observation is not null)
                {
                    var transitionKey = $"{signature}\u001f{prior.Observation.PluginName}\u001f{plugin.Name}";
                    if (!conflictGroups.TryGetValue(transitionKey, out var aggregate))
                    {
                        if (conflictGroups.Count >= budget.Limits.MaximumRecordConflictGroups)
                        {
                            if (!issues.Any(value => value.Code == "mo2.record_conflict.group_limit") && issues.Count < budget.Limits.MaximumIssues)
                                issues.Add(new("mo2.record_conflict.group_limit", "Whole-profile record-conflict grouping reached its configured limit; remaining conflict groups were not retained."));
                            partial = true;
                        }
                        else
                        {
                            aggregate = new(signature, prior.Observation.PluginName, plugin.Name, prior.Observation.LoadOrder, plugin.LoadOrder);
                            conflictGroups.Add(transitionKey, aggregate);
                        }
                    }
                    aggregate?.Add(prior.Observation, observation);
                }
                // Plugins are inspected in the established right-panel order. Replacing
                // this value models TES4 override semantics, including a winning record
                // that deliberately removes an earlier script or asset reference.
                effectiveRecords[recordKey] = new(recordReferences.ToImmutable(), recordAssetReferences.ToImmutable(), observation);
            }
            else
            {
                if (issues.Count < budget.Limits.MaximumIssues)
                    issues.Add(new("mo2.script_dependency.record_owner_invalid",
                        "The record FormID does not map to the plugin's TES4 master table; its dependencies were not attributed.", plugin.Name, position));
                partial = true;
            }
            position += RecordHeaderSize + size;
        }

        while (ends.Count > 1 && position == ends.Peek()) ends.Pop();
        if (ends.Count != 1 || position != file.Length)
            throw new InvalidDataException("Record hierarchy did not terminate at the plugin boundary.");
        var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
        if (after != file.InitialStamp) throw new InvalidDataException("Plugin changed during inspection.");
        return partial;
    }

    private static ImmutableArray<string> ParseMasters(IEnumerable<Subrecord> subrecords)
    {
        var masters = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subrecord in subrecords.Where(value => value.Signature == "MAST"))
        {
            var name = Encoding.Latin1.GetString(subrecord.Data.Span).TrimEnd('\0').Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || Path.GetFileName(name) != name ||
                Path.GetExtension(name).ToLowerInvariant() is not (".esm" or ".esp" or ".esl"))
                throw new InvalidDataException("A TES4 MAST entry is not one safe plugin leaf name.");
            if (!seen.Add(name)) throw new InvalidDataException("The TES4 master table contains a duplicate plugin name.");
            masters.Add(name);
        }
        return masters.ToImmutable();
    }

    private static string? TryGetEditorId(IEnumerable<Subrecord> subrecords)
    {
        var field = subrecords.FirstOrDefault(value => value.Signature == "EDID");
        if (field.Data.IsEmpty) return null;
        var value = Encoding.Latin1.GetString(field.Data.Span).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl)
            ? null
            : value;
    }

    private static bool TryGetCanonicalRecordKey(
        string pluginName,
        ImmutableArray<string> masters,
        uint rawFormId,
        out string key)
    {
        key = string.Empty;
        var sourceIndex = checked((int)(rawFormId >> 24));
        if (sourceIndex > masters.Length) return false;
        var owner = sourceIndex == masters.Length ? pluginName : masters[sourceIndex];
        key = $"{owner}|{rawFormId & 0x00ff_ffff:X6}";
        return true;
    }

    private static IEnumerable<Subrecord> EnumerateSubrecords(byte[] data)
    {
        var offset = 0;
        uint? extendedSize = null;
        while (offset < data.Length)
        {
            if (data.Length - offset < 6) yield break;
            var signature = Encoding.ASCII.GetString(data, offset, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 4, 2));
            offset += 6;
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (size > int.MaxValue || offset > data.Length - (int)size) yield break;
            if (signature == "XXXX")
            {
                if (size != 4) yield break;
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            }
            else yield return new(signature, data.AsMemory(offset, checked((int)size)));
            offset += checked((int)size);
        }
    }

    private static VmadParseResult ParseVmad(ReadOnlyMemory<byte> memory)
    {
        var data = memory.Span;
        var offset = 0;
        if (!ReadUInt16(data, ref offset, out _) || !ReadUInt16(data, ref offset, out _) ||
            !ReadUInt16(data, ref offset, out var scriptCount)) return new([], false);
        var names = ImmutableArray.CreateBuilder<string>();
        for (var scriptIndex = 0; scriptIndex < scriptCount; scriptIndex++)
        {
            if (!ReadString(data, ref offset, out var name) || offset >= data.Length) return new(names.ToImmutable(), false);
            names.Add(name);
            offset++; // script status
            if (!ReadUInt16(data, ref offset, out var propertyCount)) return new(names.ToImmutable(), false);
            for (var propertyIndex = 0; propertyIndex < propertyCount; propertyIndex++)
            {
                if (!SkipProperty(data, ref offset)) return new(names.ToImmutable(), false);
            }
        }
        return new(names.ToImmutable(), true);
    }

    private static bool SkipProperty(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!ReadString(data, ref offset, out _) || offset > data.Length - 2) return false;
        var type = data[offset++];
        offset++; // property status
        return type switch
        {
            1 => Skip(data, ref offset, 8),
            2 => ReadString(data, ref offset, out _),
            3 or 4 => Skip(data, ref offset, 4),
            5 => Skip(data, ref offset, 1),
            11 => SkipArray(data, ref offset, 8, strings: false),
            12 => SkipArray(data, ref offset, 0, strings: true),
            13 or 14 => SkipArray(data, ref offset, 4, strings: false),
            15 => SkipArray(data, ref offset, 1, strings: false),
            _ => false,
        };
    }

    private static bool SkipArray(ReadOnlySpan<byte> data, ref int offset, int elementSize, bool strings)
    {
        if (!ReadUInt32(data, ref offset, out var count) || count > 10_000_000) return false;
        if (strings)
        {
            for (uint index = 0; index < count; index++) if (!ReadString(data, ref offset, out _)) return false;
            return true;
        }
        var bytes = (long)count * elementSize;
        return bytes <= int.MaxValue && Skip(data, ref offset, (int)bytes);
    }

    private static bool ReadString(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = string.Empty;
        if (!ReadUInt16(data, ref offset, out var length) || offset > data.Length - length) return false;
        value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return true;
    }

    private static bool ReadUInt16(ReadOnlySpan<byte> data, ref int offset, out ushort value)
    {
        value = 0;
        if (offset > data.Length - 2) return false;
        value = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
        offset += 2;
        return true;
    }

    private static bool ReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset > data.Length - 4) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
        offset += 4;
        return true;
    }

    private static bool Skip(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        if (count < 0 || offset > data.Length - count) return false;
        offset += count;
        return true;
    }

    private static byte[] Decompress(byte[] packed, uint flags, int maximumBytes)
    {
        if ((flags & CompressedRecordFlag) == 0) return packed;
        if (packed.Length < 4) throw new InvalidDataException("Compressed record header is truncated.");
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(packed);
        if (expected > maximumBytes) throw new InspectionLimitException("mo2.script_dependency.record_data_limit", "Decompressed record exceeds the configured data limit.");
        using var source = new MemoryStream(packed, 4, packed.Length - 4, writable: false);
        using var zlib = new ZLibStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream(checked((int)expected));
        zlib.CopyTo(output);
        if (output.Length != expected) throw new InvalidDataException("Compressed record length does not match its declaration.");
        return output.ToArray();
    }

    private static async Task ReadExactlyAsync(
        IMo2RandomAccessFile file,
        long offset,
        Memory<byte> destination,
        Budget budget,
        CancellationToken cancellationToken)
    {
        budget.ReserveBytes(destination.Length);
        var written = 0;
        while (written < destination.Length)
        {
            var read = await file.ReadAsync(offset + written, destination[written..], cancellationToken).ConfigureAwait(false);
            if (read <= 0) throw new EndOfStreamException();
            written += read;
        }
    }

    private static bool TryGetRequiredVirtualPath(string scriptName, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(scriptName) || scriptName.Any(char.IsControl)) return false;
        var normalized = scriptName.Replace('/', '\\').Trim('\\');
        if (normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains(':') || Path.IsPathRooted(normalized)) return false;
        path = $"scripts\\{normalized}.pex";
        return path.Length <= 1_024;
    }

    private static bool TryGetRequiredAssetVirtualPath(
        string subrecordSignature,
        ReadOnlySpan<byte> data,
        out string declaredPath,
        out string requiredPath,
        out Mo2PluginAssetReferenceKind kind,
        out bool candidate)
    {
        declaredPath = string.Empty;
        requiredPath = string.Empty;
        kind = Mo2PluginAssetReferenceKind.Mesh;
        candidate = false;
        var isModelField = subrecordSignature is "MODL" or "MOD2" or "MOD3" or "MOD4" or "MOD5";
        var isTextureField = subrecordSignature is "ICON" or "MICO" or "TX00" or "TX01" or "TX02" or "TX03" or "TX04" or "TX05" or "TX06" or "TX07" or "TX08" or "TX09";
        if (!isModelField && !isTextureField) return false;
        var raw = Encoding.Latin1.GetString(data).TrimEnd('\0').Trim();
        var extension = Path.GetExtension(raw);
        if (isModelField && !extension.Equals(".nif", StringComparison.OrdinalIgnoreCase)) return false;
        if (isTextureField && !extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) return false;
        candidate = true;
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl) || raw.Contains(':') || Path.IsPathRooted(raw)) return false;
        var normalized = raw.Replace('/', '\\').TrimStart('\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(value => value is "." or "..")) return false;
        declaredPath = normalized;
        kind = isModelField ? Mo2PluginAssetReferenceKind.Mesh : Mo2PluginAssetReferenceKind.Texture;
        var root = isModelField ? "meshes\\" : "textures\\";
        requiredPath = normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? normalized : root + normalized;
        return requiredPath.Length <= 1_024;
    }

    private static string? NormalizeVirtualPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return null;
        var normalized = path.Replace('/', '\\').TrimStart('\\');
        return normalized.Length <= 1_024 ? normalized : null;
    }

    private static bool IsSignature(string value) => value.Length == 4 && value.All(character =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static string HashInspection(
        Mo2PluginScriptDependencyStatus status,
        long pluginsScanned,
        Budget budget,
        IEnumerable<Mo2PluginScriptReference> scriptReferences,
        IEnumerable<Mo2PluginAssetReference> assetReferences,
        IEnumerable<Mo2PluginRecordConflictSummary> recordConflicts,
        IEnumerable<Mo2PluginRecordProvenanceSummary> recordProvenance,
        long recordCatalogEntryCount,
        string recordCatalogSha256,
        IEnumerable<Mo2PluginScriptDependencyIssue> issues)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append($"status|{status}");
        Append($"plugins|{pluginsScanned}");
        Append($"headers|{budget.RecordHeaders}");
        Append($"bytes|{budget.BytesScanned}");
        foreach (var value in scriptReferences)
            Append($"script|{value.PluginName}|{value.RecordSignature}|{value.RawFormId:X8}|{value.RecordOffset}|{value.ScriptName}|{value.RequiredVirtualPath}");
        foreach (var value in assetReferences)
            Append($"asset|{value.PluginName}|{value.RecordSignature}|{value.RawFormId:X8}|{value.RecordOffset}|{value.SubrecordSignature}|{value.DeclaredPath}|{value.RequiredVirtualPath}|{value.Kind}");
        foreach (var value in recordConflicts)
        {
            Append($"conflict|{value.RecordSignature}|{value.PreviousPlugin}|{value.WinningPlugin}|{value.PreviousLoadOrder}|{value.WinningLoadOrder}|{value.RecordCount}|{value.ContentChangedCount}|{value.FlagsChangedCount}|{value.DeletedWinnerCount}|{value.InitiallyDisabledWinnerCount}");
            foreach (var sample in value.Samples)
                Append($"conflict-sample|{sample.OriginPlugin}|{sample.LocalFormId:X6}|{sample.PreviousRawFormId:X8}|{sample.WinnerRawFormId:X8}|{sample.PreviousRecordOffset}|{sample.WinnerRecordOffset}|{sample.PreviousRecordFlags:X8}|{sample.WinnerRecordFlags:X8}|{sample.PreviousEditorId}|{sample.WinnerEditorId}|{sample.PreviousDataSha256}|{sample.WinnerDataSha256}");
        }
        foreach (var value in recordProvenance)
        {
            Append($"provenance|{value.RecordSignature}|{value.PluginName}|{value.LoadOrder}|{value.NewRecordCount}|{value.OverrideRecordCount}|{value.DeletedRecordCount}|{value.InitiallyDisabledRecordCount}");
            foreach (var sample in value.Samples)
                Append($"provenance-sample|{sample.OriginPlugin}|{sample.LocalFormId:X6}|{sample.RawFormId:X8}|{sample.RecordOffset}|{sample.RecordFlags:X8}|{sample.EditorId}|{sample.DataSha256}|{sample.IsOverride}");
        }
        Append($"catalog|{recordCatalogEntryCount}|{recordCatalogSha256}");
        foreach (var value in issues)
            Append($"issue|{value.Code}|{value.PluginName}|{value.RecordOffset}|{value.Detail}");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hash.AppendData("\n"u8);
        }
    }

    private static void AppendRecordCatalogHash(IncrementalHash hash, Mo2PluginRecordCatalogEntry value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(
            $"catalog|{value.RecordSignature}|{value.OriginPlugin}|{value.LocalFormId:X6}|{value.PluginName}|{value.LoadOrder}|{value.RawFormId:X8}|{value.RecordOffset}|{value.RecordFlags:X8}|{value.EditorId}|{value.DataSha256}|{value.IsOverride}"));
        hash.AppendData("\n"u8);
    }

    private static string GetRecordCatalogHash(IncrementalHash hash) =>
        Convert.ToHexString(hash.GetCurrentHash()).ToLowerInvariant();

    private readonly record struct Subrecord(string Signature, ReadOnlyMemory<byte> Data);
    private sealed record VmadParseResult(ImmutableArray<string> ScriptNames, bool Complete);
    private sealed record RecordDependencies(
        ImmutableArray<Mo2PluginScriptReference> ScriptReferences,
        ImmutableArray<Mo2PluginAssetReference> AssetReferences,
        RecordObservation? Observation);

    private sealed record RecordObservation(
        string OriginPlugin,
        uint LocalFormId,
        string Signature,
        string PluginName,
        int? LoadOrder,
        uint RawFormId,
        long RecordOffset,
        uint RecordFlags,
        string? EditorId,
        string DataSha256);

    private sealed class ConflictAggregate(string signature, string previousPlugin, string winningPlugin, int? previousLoadOrder, int? winningLoadOrder)
    {
        private const int MaximumSamples = 12;
        private readonly ImmutableArray<Mo2PluginRecordConflictSample>.Builder samples = ImmutableArray.CreateBuilder<Mo2PluginRecordConflictSample>();
        private int recordCount;
        private int contentChangedCount;
        private int flagsChangedCount;
        private int deletedWinnerCount;
        private int initiallyDisabledWinnerCount;

        public void Add(RecordObservation previous, RecordObservation winner)
        {
            recordCount++;
            if (!previous.DataSha256.Equals(winner.DataSha256, StringComparison.OrdinalIgnoreCase)) contentChangedCount++;
            if (previous.RecordFlags != winner.RecordFlags) flagsChangedCount++;
            if ((winner.RecordFlags & DeletedRecordFlag) != 0) deletedWinnerCount++;
            if ((winner.RecordFlags & InitiallyDisabledRecordFlag) != 0) initiallyDisabledWinnerCount++;
            if (samples.Count < MaximumSamples)
            {
                samples.Add(new(
                    winner.OriginPlugin,
                    winner.LocalFormId,
                    previous.RawFormId,
                    winner.RawFormId,
                    previous.RecordOffset,
                    winner.RecordOffset,
                    previous.RecordFlags,
                    winner.RecordFlags,
                    previous.EditorId,
                    winner.EditorId,
                    previous.DataSha256,
                    winner.DataSha256));
            }
        }

        public Mo2PluginRecordConflictSummary ToSummary() => new(
            signature,
            previousPlugin,
            winningPlugin,
            previousLoadOrder,
            winningLoadOrder,
            recordCount,
            contentChangedCount,
            flagsChangedCount,
            deletedWinnerCount,
            initiallyDisabledWinnerCount,
            samples.ToImmutable());
    }

    private sealed class ProvenanceAggregate(string signature, string pluginName, int? loadOrder)
    {
        private const int MaximumSamples = 12;
        private readonly ImmutableArray<Mo2PluginRecordProvenanceSample>.Builder samples = ImmutableArray.CreateBuilder<Mo2PluginRecordProvenanceSample>();
        private int newRecordCount;
        private int overrideRecordCount;
        private int deletedRecordCount;
        private int initiallyDisabledRecordCount;

        public void Add(RecordObservation observation, bool isOverride)
        {
            if (isOverride) overrideRecordCount++;
            else newRecordCount++;
            if ((observation.RecordFlags & DeletedRecordFlag) != 0) deletedRecordCount++;
            if ((observation.RecordFlags & InitiallyDisabledRecordFlag) != 0) initiallyDisabledRecordCount++;
            if (samples.Count < MaximumSamples)
            {
                samples.Add(new(
                    observation.OriginPlugin,
                    observation.LocalFormId,
                    observation.RawFormId,
                    observation.RecordOffset,
                    observation.RecordFlags,
                    observation.EditorId,
                    observation.DataSha256,
                    isOverride));
            }
        }

        public Mo2PluginRecordProvenanceSummary ToSummary() => new(
            signature,
            pluginName,
            loadOrder,
            newRecordCount,
            overrideRecordCount,
            deletedRecordCount,
            initiallyDisabledRecordCount,
            samples.ToImmutable());
    }

    private sealed class Budget(Mo2PluginScriptDependencyLimits limits)
    {
        public Mo2PluginScriptDependencyLimits Limits { get; } = limits;
        public long BytesScanned { get; private set; }
        public long RecordHeaders { get; private set; }
        public long ScriptReferences { get; private set; }
        public long AssetReferences { get; private set; }

        public void ReserveBytes(long count)
        {
            if (count < 0 || BytesScanned > Limits.MaximumAggregateBytesScanned - count)
                throw new InspectionLimitException("mo2.script_dependency.aggregate_byte_limit", "Script-dependency inspection reached its aggregate byte limit.");
            BytesScanned += count;
        }

        public void CountRecordHeader()
        {
            if (RecordHeaders >= Limits.MaximumRecordHeaders)
                throw new InspectionLimitException("mo2.script_dependency.record_header_limit", "Script-dependency inspection reached its record-header limit.");
            RecordHeaders++;
        }

        public void CountScriptReference()
        {
            if (ScriptReferences >= Limits.MaximumScriptReferences)
                throw new InspectionLimitException("mo2.script_dependency.reference_limit", "Script-reference inspection exceeded its configured limit.");
            ScriptReferences++;
        }

        public void CountAssetReference()
        {
            if (AssetReferences >= Limits.MaximumAssetReferences)
                throw new InspectionLimitException("mo2.asset_dependency.reference_limit", "Plugin asset-reference inspection exceeded its configured limit.");
            AssetReferences++;
        }
    }

    private sealed class InspectionLimitException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class ScriptKeyComparer : IEqualityComparer<(string PluginName, string ScriptName, string RequiredVirtualPath)>
    {
        public static ScriptKeyComparer Instance { get; } = new();
        public bool Equals((string PluginName, string ScriptName, string RequiredVirtualPath) left,
            (string PluginName, string ScriptName, string RequiredVirtualPath) right) =>
            left.PluginName.Equals(right.PluginName, StringComparison.OrdinalIgnoreCase) &&
            left.ScriptName.Equals(right.ScriptName, StringComparison.OrdinalIgnoreCase) &&
            left.RequiredVirtualPath.Equals(right.RequiredVirtualPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string PluginName, string ScriptName, string RequiredVirtualPath) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.PluginName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ScriptName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.RequiredVirtualPath));
    }

    private sealed class AssetKeyComparer : IEqualityComparer<(string PluginName, string RequiredVirtualPath, Mo2PluginAssetReferenceKind Kind)>
    {
        public static AssetKeyComparer Instance { get; } = new();
        public bool Equals((string PluginName, string RequiredVirtualPath, Mo2PluginAssetReferenceKind Kind) left,
            (string PluginName, string RequiredVirtualPath, Mo2PluginAssetReferenceKind Kind) right) =>
            left.PluginName.Equals(right.PluginName, StringComparison.OrdinalIgnoreCase) &&
            left.RequiredVirtualPath.Equals(right.RequiredVirtualPath, StringComparison.OrdinalIgnoreCase) &&
            left.Kind == right.Kind;
        public int GetHashCode((string PluginName, string RequiredVirtualPath, Mo2PluginAssetReferenceKind Kind) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.PluginName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.RequiredVirtualPath), value.Kind);
    }
}

file static class ImmutableBuilderExtensions
{
    public static ImmutableArray<T>.Builder AddAndReturn<T>(this ImmutableArray<T>.Builder builder, T value)
    {
        builder.Add(value);
        return builder;
    }
}
