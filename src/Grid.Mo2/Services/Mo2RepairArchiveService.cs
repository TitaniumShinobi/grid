using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Grid.Mo2.Models;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Grid.Mo2.Services;

/// <summary>
/// Performs bounded, read-only archive validation and optionally materializes a verified
/// archive or explicit FOMOD selection into a caller-owned, empty staging directory.
/// It never writes to an MO2 installation and never overwrites an existing staging entry.
/// </summary>
public sealed class Mo2RepairArchiveService
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public async Task<Mo2ArchiveInspectionResult> InspectAsync(
        Mo2ArchiveInspectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArchivePath);
        ArgumentNullException.ThrowIfNull(request.Limits);
        request.Limits.Validate();

        var archivePath = Path.GetFullPath(request.ArchivePath);
        var issues = ImmutableArray.CreateBuilder<Mo2ArchiveInspectionIssue>();
        if (!File.Exists(archivePath))
        {
            return EmptyResult(Mo2ArchiveInspectionStatus.Inaccessible, archivePath,
                [new("mo2.repair.archive.missing", "The archive is not an existing file.")]);
        }

        var before = new FileInfo(archivePath);
        if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return EmptyResult(Mo2ArchiveInspectionStatus.Rejected, archivePath,
                [new("mo2.repair.archive.reparse_refused", "Archive reparse points are not accepted.")]);
        }

        if (before.Length > request.Limits.MaximumArchiveBytes)
        {
            return EmptyResult(Mo2ArchiveInspectionStatus.Rejected, archivePath,
                [new("mo2.repair.archive.size_limit", "The archive exceeds the configured byte limit.")], before);
        }

        string archiveHash;
        try
        {
            archiveHash = await HashFileAsync(archivePath, request.Limits.BufferBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptyResult(Mo2ArchiveInspectionStatus.Inaccessible, archivePath,
                [new("mo2.repair.archive.unreadable", "The archive could not be read.")], before);
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256))
        {
            if (!IsSha256(request.ExpectedSha256) ||
                !archiveHash.Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("mo2.repair.archive.digest_mismatch",
                    "The archive SHA-256 does not match the evidence-bound expected digest."));
            }
        }

        var entries = ImmutableArray.CreateBuilder<Mo2ArchiveEntryInspection>();
        var seenPaths = new Dictionary<string, string>(PathComparer);
        byte[]? infoXml = null;
        byte[]? moduleConfigXml = null;
        string? format = null;
        long expandedTotal = 0;
        long actualExpandedTotal = 0;
        var aggregateRatioArchive = false;

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            format = archive.Type.ToString();
            // SharpCompress does not provide an independent compressed size for every
            // SevenZip entry, even when IsSolid is false. Treat all 7z archives as an
            // aggregate-ratio format and retain per-entry ratio checks for ZIP-like formats.
            aggregateRatioArchive = archive.IsSolid || archive.Type == ArchiveType.SevenZip;
            var fomodWrapperPrefix = FindSingleFomodWrapperPrefix(archive, request.Limits);
            var ordinal = 0;
            foreach (var cursor in EnumerateArchiveEntries(archive))
            {
                var entry = cursor.Entry;
                cancellationToken.ThrowIfCancellationRequested();
                ordinal++;
                if (ordinal > request.Limits.MaximumEntries)
                {
                    issues.Add(new("mo2.repair.archive.entry_limit",
                        "The archive contains more entries than the configured limit."));
                    break;
                }

                var originalPath = entry.Key ?? string.Empty;
                if (!TryNormalizeRelativePath(originalPath, request.Limits, allowEmpty: false,
                        out var archivePathName, out var pathFailure))
                {
                    issues.Add(new("mo2.repair.archive.unsafe_path", pathFailure, originalPath));
                    continue;
                }

                if (fomodWrapperPrefix is not null &&
                    archivePathName.Equals(fomodWrapperPrefix.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
                    entry.IsDirectory)
                {
                    continue;
                }
                var normalizedPath = fomodWrapperPrefix is not null &&
                    archivePathName.StartsWith(fomodWrapperPrefix, StringComparison.OrdinalIgnoreCase)
                        ? archivePathName[fomodWrapperPrefix.Length..]
                        : archivePathName;

                if (!seenPaths.TryAdd(normalizedPath, originalPath))
                {
                    issues.Add(new("mo2.repair.archive.path_collision",
                        "Two entries collide after Windows case and Unicode normalization.", originalPath));
                    continue;
                }

                var isLink = !string.IsNullOrEmpty(entry.LinkTarget);
                if (isLink)
                {
                    issues.Add(new("mo2.repair.archive.link_refused",
                        "Archive links and device-like entries are not materialized.", originalPath));
                }
                if (entry.IsEncrypted)
                {
                    issues.Add(new("mo2.repair.archive.encrypted_refused",
                        "Encrypted archive entries require an unsupported secret-bearing operation.", originalPath));
                }

                if (entry.Size < 0 || entry.CompressedSize < 0)
                {
                    issues.Add(new("mo2.repair.archive.invalid_size",
                        "An archive entry reports an invalid size.", originalPath));
                    continue;
                }

                if (!entry.IsDirectory)
                {
                    expandedTotal = checked(expandedTotal + entry.Size);
                    if (expandedTotal > request.Limits.MaximumExpandedBytes)
                    {
                        issues.Add(new("mo2.repair.archive.expanded_size_limit",
                            "The declared expanded bytes exceed the configured limit.", originalPath));
                    }

                    // Solid and SevenZip archives can share compressed blocks or omit a useful
                    // per-entry denominator, so only the aggregate archive ratio is meaningful.
                    var ratio = entry.CompressedSize == 0
                        ? entry.Size == 0 ? 1d : double.PositiveInfinity
                        : entry.Size / (double)entry.CompressedSize;
                    if (!aggregateRatioArchive && ratio > request.Limits.MaximumCompressionRatio)
                    {
                        issues.Add(new("mo2.repair.archive.compression_ratio_limit",
                            "An entry exceeds the configured compression-ratio limit.", originalPath));
                    }
                }

                string? entryHash = null;
                if (!entry.IsDirectory && !isLink && !entry.IsEncrypted &&
                    expandedTotal <= request.Limits.MaximumExpandedBytes)
                {
                    var capture = IsFomodDocument(normalizedPath);
                    if (capture && entry.Size > request.Limits.MaximumFomodDocumentBytes)
                    {
                        issues.Add(new("mo2.repair.fomod.document_limit",
                            "A FOMOD document exceeds the configured byte limit.", originalPath));
                    }
                    else
                    {
                        await using var entryStream = cursor.OpenStream();
                        var content = await ReadAndHashEntryAsync(
                            entryStream,
                            capture ? request.Limits.MaximumFomodDocumentBytes : 0,
                            request.Limits.MaximumExpandedBytes - actualExpandedTotal,
                            request.Limits.BufferBytes,
                            cancellationToken).ConfigureAwait(false);
                        actualExpandedTotal = checked(actualExpandedTotal + content.BytesRead);
                        entryHash = content.Sha256;
                        if (content.BytesRead != entry.Size)
                        {
                            issues.Add(new("mo2.repair.archive.expanded_size_mismatch",
                                "The entry stream length differs from the archive header.", originalPath));
                        }
                        if (capture)
                        {
                            if (normalizedPath.Equals("fomod\\info.xml", StringComparison.OrdinalIgnoreCase))
                            {
                                infoXml = content.Captured;
                            }
                            else
                            {
                                moduleConfigXml = content.Captured;
                            }
                        }
                    }
                }

                entries.Add(new(
                    ordinal,
                    originalPath,
                    normalizedPath,
                    entry.IsDirectory,
                    entry.IsEncrypted,
                    isLink,
                    entry.CompressedSize,
                    entry.Size,
                    entryHash));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidDataException or IOException or
                UnauthorizedAccessException or NotSupportedException or OverflowException or ArchiveException)
        {
            issues.Add(new("mo2.repair.archive.integrity_failure",
                "The archive could not be completely decoded and integrity-checked."));
        }

        if (aggregateRatioArchive && before.Length > 0 && expandedTotal / (double)before.Length > request.Limits.MaximumCompressionRatio)
        {
            issues.Add(new("mo2.repair.archive.compression_ratio_limit",
                "The solid archive exceeds the configured aggregate compression-ratio limit."));
        }

        AddEntryPrefixCollisionIssues(entries, issues);
        var fomod = ParseFomod(infoXml, moduleConfigXml, request.EffectiveFomodSelections,
            entries.ToImmutable(), request.Limits);
        issues.AddRange(fomod.Issues);

        var mappings = request.ExtractionMode switch
        {
            Mo2ArchiveExtractionMode.None => ImmutableArray<Mo2ArchiveInstallMapping>.Empty,
            Mo2ArchiveExtractionMode.FullArchive => CreateFullArchiveMappings(entries, issues),
            Mo2ArchiveExtractionMode.FomodSelection => CreateFomodMappings(
                moduleConfigXml, fomod, entries.ToImmutable(), request.Limits, issues),
            _ => throw new ArgumentOutOfRangeException(nameof(request.ExtractionMode)),
        };

        var after = new FileInfo(archivePath);
        var changed = before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc;
        if (!changed)
        {
            try
            {
                changed = !archiveHash.Equals(
                    await HashFileAsync(archivePath, request.Limits.BufferBytes, cancellationToken).ConfigureAwait(false),
                    StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                changed = true;
            }
        }
        if (changed)
        {
            issues.Add(new("mo2.repair.archive.changed_during_read",
                "The archive identity changed during inspection."));
        }

        var rejectingIssues = issues.Any(IsRejectingIssue);
        var status = changed
            ? Mo2ArchiveInspectionStatus.ChangedDuringRead
            : rejectingIssues ? Mo2ArchiveInspectionStatus.Rejected : Mo2ArchiveInspectionStatus.Complete;
        string? stagingDirectory = null;

        if (request.ExtractionMode != Mo2ArchiveExtractionMode.None && status == Mo2ArchiveInspectionStatus.Complete)
        {
            if (string.IsNullOrWhiteSpace(request.StagingDirectory))
            {
                issues.Add(new("mo2.repair.staging.path_required",
                    "Extraction requires an explicit caller-supplied staging directory."));
                status = Mo2ArchiveInspectionStatus.Rejected;
            }
            else
            {
                stagingDirectory = Path.GetFullPath(request.StagingDirectory);
                var stagingIssue = ValidateEmptyStagingRoot(stagingDirectory);
                if (stagingIssue is not null)
                {
                    issues.Add(stagingIssue);
                    status = Mo2ArchiveInspectionStatus.Rejected;
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(stagingDirectory);
                        await ExtractMappingsAsync(
                            archivePath,
                            stagingDirectory,
                            mappings,
                            entries.ToImmutable(),
                            request.Limits.BufferBytes,
                            cancellationToken).ConfigureAwait(false);
                        status = Mo2ArchiveInspectionStatus.Extracted;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or InvalidDataException or
                            InvalidOperationException or NotSupportedException or ArchiveException)
                    {
                        issues.Add(new("mo2.repair.staging.extraction_failed",
                            "A staged entry could not be materialized and verified."));
                        status = Mo2ArchiveInspectionStatus.Rejected;
                    }
                }
            }
        }

        return new(
            status,
            archivePath,
            before.Length,
            archiveHash,
            format,
            before.LastWriteTimeUtc,
            after.LastWriteTimeUtc,
            entries.OrderBy(item => item.NormalizedPath, PathComparer).ThenBy(item => item.Ordinal).ToImmutableArray(),
            fomod,
            mappings,
            stagingDirectory,
            issues.ToImmutable());
    }

    private static async Task ExtractMappingsAsync(
        string archivePath,
        string stagingRoot,
        ImmutableArray<Mo2ArchiveInstallMapping> mappings,
        ImmutableArray<Mo2ArchiveEntryInspection> inspectedEntries,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        var expected = inspectedEntries
            .Where(entry => !entry.IsDirectory)
            .ToDictionary(entry => entry.NormalizedPath, PathComparer);
        var logicalPathByArchivePath = inspectedEntries
            .ToDictionary(entry => entry.ArchivePath, entry => entry.NormalizedPath, PathComparer);
        var pending = mappings
            .GroupBy(mapping => mapping.SourcePath, PathComparer)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), PathComparer);

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var cursor in EnumerateArchiveEntries(archive))
        {
            var entry = cursor.Entry;
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.Key is null) continue;
            if (!logicalPathByArchivePath.TryGetValue(entry.Key, out var source)) continue;
            if (!pending.TryGetValue(source, out var destinations)) continue;

            if (!expected.TryGetValue(source, out var expectedEntry) || expectedEntry.Sha256 is null)
            {
                throw new InvalidDataException("A mapped entry lacks a verified inspection digest.");
            }

            var outputs = new List<StagingOutput>();
            try
            {
                foreach (var mapping in destinations)
                {
                    var destination = ContainedPath(stagingRoot, mapping.DestinationPath);
                    EnsureSafeDirectory(stagingRoot, Path.GetDirectoryName(destination)!);
                    if (File.Exists(destination) || Directory.Exists(destination))
                    {
                        throw new IOException("A staging destination already exists.");
                    }

                    var temporary = destination + $".grid-{Guid.NewGuid():N}.tmp";
                    outputs.Add(new(destination, temporary,
                        new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                            bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan)));
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var sourceStream = cursor.OpenStream();
                var buffer = new byte[bufferBytes];
                int read;
                while ((read = await sourceStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    foreach (var output in outputs)
                    {
                        await output.Stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!actualHash.Equals(expectedEntry.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A staged entry differs from its inspected digest.");
                }

                foreach (var output in outputs)
                {
                    await output.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Stream.Flush(flushToDisk: true);
                    output.Stream.Dispose();
                    File.Move(output.TemporaryPath, output.DestinationPath);
                }
                pending.Remove(source);
            }
            finally
            {
                foreach (var output in outputs)
                {
                    output.Stream.Dispose();
                    if (File.Exists(output.TemporaryPath))
                    {
                        File.Delete(output.TemporaryPath);
                    }
                }
            }
        }

        if (pending.Count != 0)
        {
            throw new InvalidDataException("One or more mapped archive entries were not extracted.");
        }
    }

    private static ImmutableArray<Mo2ArchiveInstallMapping> CreateFullArchiveMappings(
        IEnumerable<Mo2ArchiveEntryInspection> entries,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues) =>
        ResolveMappingCollisions(
            entries.Where(entry => !entry.IsDirectory && !entry.IsEncrypted && !entry.IsLink)
                .Select(entry => new Mo2ArchiveInstallMapping(
                    entry.NormalizedPath, entry.NormalizedPath, 0, "FullArchive")), issues);

    private static ImmutableArray<Mo2ArchiveInstallMapping> CreateFomodMappings(
        byte[]? moduleConfig,
        Mo2FomodInstallerInfo fomod,
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        Mo2ArchiveInspectionLimits limits,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        if (moduleConfig is null || fomod.Status != Mo2FomodStatus.Valid)
        {
            issues.Add(new("mo2.repair.fomod.selection_unavailable",
                "FOMOD extraction requires a supported configuration and a complete valid selection vector."));
            return [];
        }

        var document = LoadXml(moduleConfig, limits.MaximumFomodDocumentBytes);
        var selected = fomod.SelectionVector.ToDictionary(
            item => item.GroupName,
            item => item.EffectivePluginNames.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var specs = new List<MappingSpec>();
        foreach (var required in Descendants(document, "requiredInstallFiles").SelectMany(element => element.Elements()))
        {
            AddMappingSpec(required, "Required", specs, issues);
        }
        var mappings = ExpandMappingSpecs(specs, entries, limits, issues).ToList();

        foreach (var group in fomod.Groups)
        {
            if (!selected.TryGetValue(group.Name, out var pluginNames))
            {
                continue;
            }
            foreach (var option in group.EffectivePluginOptions)
            {
                if (!pluginNames.Contains(option.Name))
                {
                    continue;
                }
                mappings.AddRange(option.InstallMappings);
            }
        }

        return ResolveMappingCollisions(mappings, issues);
    }

    private static IEnumerable<Mo2ArchiveInstallMapping> ExpandMappingSpecs(
        IEnumerable<MappingSpec> specs,
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        Mo2ArchiveInspectionLimits limits,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        var files = entries.Where(entry => !entry.IsDirectory).ToImmutableArray();
        var mappings = new List<Mo2ArchiveInstallMapping>();
        foreach (var spec in specs)
        {
            if (!TryNormalizeRelativePath(spec.Source, limits, false, out var source, out var sourceFailure))
            {
                issues.Add(new("mo2.repair.fomod.source_unsafe", sourceFailure, spec.Source));
                continue;
            }

            if (spec.IsFolder)
            {
                var prefix = source + "\\";
                var matches = files.Where(entry => entry.NormalizedPath.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)).ToImmutableArray();
                if (matches.IsEmpty)
                {
                    issues.Add(new("mo2.repair.fomod.source_missing",
                        "A selected FOMOD folder has no archive members.", spec.Source));
                    continue;
                }
                foreach (var match in matches)
                {
                    var relative = match.NormalizedPath[prefix.Length..];
                    var combined = string.IsNullOrWhiteSpace(spec.Destination)
                        ? relative
                        : $"{spec.Destination.TrimEnd('\\', '/')}\\{relative}";
                    AddValidatedMapping(match.NormalizedPath, combined, spec, limits, mappings, issues);
                }
            }
            else
            {
                var match = files.SingleOrDefault(entry =>
                    entry.NormalizedPath.Equals(source, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    issues.Add(new("mo2.repair.fomod.source_missing",
                        "A selected FOMOD file is absent from the archive.", spec.Source));
                    continue;
                }
                AddValidatedMapping(match.NormalizedPath,
                    string.IsNullOrWhiteSpace(spec.Destination) ? source : spec.Destination,
                    spec, limits, mappings, issues);
            }
        }
        return mappings;
    }

    private static void AddValidatedMapping(
        string source,
        string destination,
        MappingSpec spec,
        Mo2ArchiveInspectionLimits limits,
        List<Mo2ArchiveInstallMapping> mappings,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        if (!TryNormalizeRelativePath(destination, limits, false, out var normalized, out var failure))
        {
            issues.Add(new("mo2.repair.fomod.destination_unsafe", failure, destination));
            return;
        }
        mappings.Add(new(source, normalized, spec.Priority, spec.Origin));
    }

    private static ImmutableArray<Mo2ArchiveInstallMapping> ResolveMappingCollisions(
        IEnumerable<Mo2ArchiveInstallMapping> source,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        var result = ImmutableArray.CreateBuilder<Mo2ArchiveInstallMapping>();
        foreach (var group in source.GroupBy(item => item.DestinationPath, PathComparer)
                     .OrderBy(group => group.Key, PathComparer))
        {
            var ordered = group.OrderByDescending(item => item.Priority)
                .ThenBy(item => item.SourcePath, PathComparer).ToImmutableArray();
            var highest = ordered[0].Priority;
            var winners = ordered.Where(item => item.Priority == highest)
                .DistinctBy(item => item.SourcePath, PathComparer).ToImmutableArray();
            if (winners.Length != 1)
            {
                issues.Add(new("mo2.repair.fomod.destination_collision",
                    "Multiple equally prioritized sources map to the same destination.", group.Key));
                continue;
            }
            result.Add(winners[0]);
        }
        var resolved = result.ToImmutable();
        AddPathPrefixCollisionIssues(
            resolved.Select(item => item.DestinationPath), issues);
        return resolved;
    }

    private static Mo2FomodInstallerInfo ParseFomod(
        byte[]? infoBytes,
        byte[]? moduleBytes,
        ImmutableArray<Mo2FomodGroupSelection> requestedSelections,
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        Mo2ArchiveInspectionLimits limits)
    {
        var issues = ImmutableArray.CreateBuilder<Mo2ArchiveInspectionIssue>();
        string? name = null;
        string? author = null;
        string? version = null;
        string? website = null;
        try
        {
            if (infoBytes is not null)
            {
                var info = LoadXml(infoBytes, limits.MaximumFomodDocumentBytes);
                name = FirstValue(info, "Name");
                author = FirstValue(info, "Author");
                version = FirstValue(info, "Version");
                website = FirstValue(info, "Website");
            }

            if (moduleBytes is null)
            {
                return new(Mo2FomodStatus.Absent, name, author, version, website, [], [], issues.ToImmutable());
            }

            var module = LoadXml(moduleBytes, limits.MaximumFomodDocumentBytes);
            var unsupported = module.Descendants().FirstOrDefault(element => element.Name.LocalName is
                "conditionalFileInstalls" or "conditionFlags" or "dependencies" or "visible");
            if (unsupported is not null)
            {
                issues.Add(new("mo2.repair.fomod.logic_unsupported",
                    $"Conditional installer element '{unsupported.Name.LocalName}' is unsupported."));
                return new(Mo2FomodStatus.Unsupported, name, author, version, website, [], [], issues.ToImmutable());
            }

            var parsedGroups = ReadFomodGroups(module);
            var groups = parsedGroups
                .Select(group => new Mo2FomodGroup(
                    group.SelectionKey,
                    group.Type,
                    group.PluginNames,
                    CreateFomodPluginOptions(group, entries, limits, issues)))
                .OrderBy(group => group.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            if (groups.Any(group => group.Name.Length == 0 || group.PluginNames.IsEmpty) ||
                groups.Select(group => group.Name).Distinct(StringComparer.Ordinal).Count() != groups.Length)
            {
                issues.Add(new("mo2.repair.fomod.group_invalid",
                    "FOMOD group selection identities and plugin names must be non-empty and unique."));
                return new(Mo2FomodStatus.Malformed, name, author, version, website, groups, [], issues.ToImmutable());
            }

            var requestGroups = requestedSelections.ToLookup(item => item.GroupName, StringComparer.Ordinal);
            var vector = ImmutableArray.CreateBuilder<Mo2FomodGroupSelection>();
            foreach (var group in groups)
            {
                var requestedGroup = requestGroups[group.Name].ToImmutableArray();
                if (requestedGroup.Length != 1)
                {
                    issues.Add(new("mo2.repair.fomod.selection_required",
                        $"Group '{group.Name}' requires one explicit selection-vector entry."));
                    continue;
                }
                var selected = requestedGroup[0].EffectivePluginNames
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
                if (selected.Any(item => !group.PluginNames.Contains(item, StringComparer.Ordinal)) ||
                    !ValidGroupSelection(group.Type, selected.Length, group.PluginNames.Length))
                {
                    issues.Add(new("mo2.repair.fomod.selection_invalid",
                        $"Group '{group.Name}' has an invalid selection for type '{group.Type}'."));
                    continue;
                }
                vector.Add(new(group.Name, selected));
            }
            if (requestedSelections.Any(selection => !groups.Any(group => group.Name == selection.GroupName)))
            {
                issues.Add(new("mo2.repair.fomod.selection_unknown_group",
                    "The selection vector names a group not declared by the FOMOD."));
            }

            var status = issues.Any(issue => issue.Code.Contains("selection", StringComparison.Ordinal))
                ? Mo2FomodStatus.SelectionRequired
                : Mo2FomodStatus.Valid;
            return new(status, name, author, version, website, groups, vector.ToImmutable(), issues.ToImmutable());
        }
        catch (Exception exception) when (exception is XmlException or InvalidDataException)
        {
            issues.Add(new("mo2.repair.fomod.malformed", "A FOMOD XML document is malformed or exceeds its bounds."));
            return new(Mo2FomodStatus.Malformed, name, author, version, website, [], [], issues.ToImmutable());
        }
    }

    private static bool ValidGroupSelection(string type, int selected, int available) => type switch
    {
        "SelectExactlyOne" => selected == 1,
        "SelectAtMostOne" => selected <= 1,
        "SelectAtLeastOne" => selected >= 1,
        "SelectAny" => true,
        "SelectAll" => selected == available,
        _ => false,
    };

    private static ImmutableArray<Mo2FomodPluginOption> CreateFomodPluginOptions(
        FomodGroupDescriptor group,
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        Mo2ArchiveInspectionLimits limits,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        var options = ImmutableArray.CreateBuilder<Mo2FomodPluginOption>();
        foreach (var plugin in group.Element.Descendants().Where(element => element.Name.LocalName == "plugin"))
        {
            var pluginName = (Attribute(plugin, "name") ?? string.Empty).Trim();
            if (pluginName.Length == 0) continue;
            var specs = new List<MappingSpec>();
            foreach (var file in plugin.Descendants()
                         .Where(element => element.Parent?.Name.LocalName == "files" &&
                             element.Name.LocalName is "file" or "folder"))
            {
                AddMappingSpec(file, $"Fomod:{group.SelectionKey}/{pluginName}", specs, issues);
            }
            var mappings = ResolveMappingCollisions(
                ExpandMappingSpecs(specs, entries, limits, issues), issues);
            options.Add(new(pluginName, mappings));
        }
        return options.OrderBy(option => option.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static void AddMappingSpec(
        XElement element,
        string origin,
        List<MappingSpec> specs,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        if (element.Name.LocalName is not ("file" or "folder"))
        {
            issues.Add(new("mo2.repair.fomod.mapping_unsupported",
                "A FOMOD mapping element is unsupported."));
            return;
        }
        var source = Attribute(element, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            issues.Add(new("mo2.repair.fomod.source_missing", "A FOMOD mapping has no source."));
            return;
        }
        var priority = int.TryParse(Attribute(element, "priority"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var parsedPriority) ? parsedPriority : 0;
        specs.Add(new(source, Attribute(element, "destination") ?? string.Empty,
            element.Name.LocalName == "folder", priority, origin));
    }

    private static XDocument LoadXml(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length > maximumBytes)
        {
            throw new InvalidDataException("XML exceeds its limit.");
        }
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = XmlReader.Create(stream, new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumBytes,
            MaxCharactersFromEntities = 0,
            IgnoreComments = true,
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static ImmutableArray<FomodGroupDescriptor> ReadFomodGroups(XDocument document)
    {
        var raw = document.Descendants()
            .Where(element => element.Name.LocalName == "group")
            .Select(element => new
            {
                Element = element,
                Name = (Attribute(element, "name") ?? string.Empty).Trim(),
                StepName = (element.Ancestors().FirstOrDefault(ancestor => ancestor.Name.LocalName == "installStep") is { } step
                    ? Attribute(step, "name")
                    : null)?.Trim() ?? string.Empty,
                Type = Attribute(element, "type") ?? "SelectAny",
                PluginNames = element.Descendants().Where(candidate => candidate.Name.LocalName == "plugin")
                    .Select(plugin => (Attribute(plugin, "name") ?? string.Empty).Trim())
                    .Where(pluginName => pluginName.Length != 0)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToImmutableArray(),
            })
            .ToImmutableArray();
        var duplicateNames = raw.GroupBy(group => group.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return raw.Select(group => new FomodGroupDescriptor(
                group.Element,
                duplicateNames.Contains(group.Name) && group.StepName.Length != 0
                    ? $"{group.StepName} / {group.Name}"
                    : group.Name,
                group.Type,
                group.PluginNames))
            .ToImmutableArray();
    }

    private static IEnumerable<XElement> Descendants(XDocument document, string name) =>
        document.Descendants().Where(element => element.Name.LocalName == name);

    private static string? FirstValue(XDocument document, string name) =>
        document.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == name)?.Value;

    private static bool IsFomodDocument(string normalizedPath) =>
        normalizedPath.Equals("fomod\\info.xml", StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.Equals("fomod\\ModuleConfig.xml", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<ArchiveEntryCursor> EnumerateArchiveEntries(IArchive archive)
    {
        if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
        {
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                yield return new(reader.Entry, () => reader.OpenEntryStream());
            }
            yield break;
        }

        foreach (var entry in archive.Entries)
        {
            yield return new(entry, () => entry.OpenEntryStream());
        }
    }

    private static string? FindSingleFomodWrapperPrefix(
        IArchive archive,
        Mo2ArchiveInspectionLimits limits)
    {
        var prefixes = new HashSet<string>(PathComparer);
        foreach (var entry in archive.Entries)
        {
            if (entry.Key is null ||
                !TryNormalizeRelativePath(entry.Key, limits, allowEmpty: false, out var normalized, out _))
            {
                continue;
            }

            foreach (var suffix in new[] { "fomod\\info.xml", "fomod\\ModuleConfig.xml" })
            {
                if (!normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var prefix = normalized[..^suffix.Length];
                if (prefix.Length == 0) return null;
                var root = prefix.TrimEnd('\\');
                if (root.Length == 0 || root.Contains('\\')) return null;
                prefixes.Add(prefix);
            }
        }
        return prefixes.Count == 1 ? prefixes.Single() : null;
    }

    private static async Task<EntryContent> ReadAndHashEntryAsync(
        Stream stream,
        int captureLimit,
        long maximumBytes,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var capture = captureLimit > 0 ? new MemoryStream(Math.Min(captureLimit, 64 * 1024)) : null;
        var buffer = new byte[bufferBytes];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes)
            {
                throw new InvalidDataException("Expanded archive data exceeds the aggregate byte limit.");
            }
            hash.AppendData(buffer, 0, read);
            if (capture is not null)
            {
                if (total > captureLimit)
                {
                    throw new InvalidDataException("FOMOD document exceeds its read limit.");
                }
                await capture.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }
        return new(total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), capture?.ToArray());
    }

    private static async Task<string> HashFileAsync(
        string path,
        int bufferBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[bufferBytes];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryNormalizeRelativePath(
        string value,
        Mo2ArchiveInspectionLimits limits,
        bool allowEmpty,
        out string normalized,
        out string failure)
    {
        normalized = string.Empty;
        failure = string.Empty;
        var candidate = value.Replace('/', '\\').Normalize(NormalizationForm.FormC);
        if (candidate.Length == 0)
        {
            failure = "The path is empty.";
            return allowEmpty;
        }
        if (Path.IsPathRooted(value) || value.StartsWith('/') || value.StartsWith('\\') ||
            value.Contains(':') || value.Any(char.IsControl))
        {
            failure = "The path is rooted, contains an alternate data stream, or contains control characters.";
            return false;
        }
        while (candidate.StartsWith(".\\", StringComparison.Ordinal))
        {
            candidate = candidate[2..];
        }
        candidate = candidate.TrimEnd('\\');
        if (candidate.Contains("\\\\", StringComparison.Ordinal))
        {
            failure = "The path contains an empty segment.";
            return false;
        }
        var segments = candidate.Split('\\', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Length > limits.MaximumDepth)
        {
            failure = "The path exceeds the configured depth.";
            return false;
        }
        foreach (var segment in segments)
        {
            var baseName = segment.Split('.')[0];
            if (segment is "." or ".." || segment.Length > limits.MaximumSegmentLength ||
                segment.EndsWith(' ') || segment.EndsWith('.') || ReservedNames.Contains(baseName) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                failure = "The path has an unsafe or unsupported segment.";
                return false;
            }
        }
        normalized = string.Join('\\', segments);
        if (normalized.Length > limits.MaximumPathLength)
        {
            failure = "The path exceeds the configured character limit.";
            return false;
        }
        return true;
    }

    private static Mo2ArchiveInspectionIssue? ValidateEmptyStagingRoot(string stagingRoot)
    {
        if (File.Exists(stagingRoot))
        {
            return new("mo2.repair.staging.not_directory", "The staging path is an existing file.");
        }
        var cursor = new DirectoryInfo(stagingRoot);
        while (cursor is not null)
        {
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new("mo2.repair.staging.reparse_refused",
                    "The staging path crosses an existing reparse point.");
            }
            cursor = cursor.Parent;
        }
        if (Directory.Exists(stagingRoot) && Directory.EnumerateFileSystemEntries(stagingRoot).Any())
        {
            return new("mo2.repair.staging.not_empty", "The staging directory must be empty.");
        }
        return null;
    }

    private static void EnsureSafeDirectory(string root, string directory)
    {
        var relative = Path.GetRelativePath(root, directory);
        var cursor = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cursor = Path.Combine(cursor, segment);
            if (File.Exists(cursor))
            {
                throw new IOException("A staging directory component is an existing file.");
            }
            if (!Directory.Exists(cursor))
            {
                Directory.CreateDirectory(cursor);
            }
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("A staging directory component is a reparse point.");
            }
        }
    }

    private static string ContainedPath(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A staging destination escapes its root.");
        }
        return candidate;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(character => Uri.IsHexDigit(character));

    private static bool IsRejectingIssue(Mo2ArchiveInspectionIssue issue) =>
        !issue.Code.EndsWith(".optional", StringComparison.Ordinal);

    private sealed record FomodGroupDescriptor(
        XElement Element,
        string SelectionKey,
        string Type,
        ImmutableArray<string> PluginNames);

    private static void AddEntryPrefixCollisionIssues(
        IEnumerable<Mo2ArchiveEntryInspection> entries,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues) =>
        AddPathPrefixCollisionIssues(
            entries.Where(entry => !entry.IsDirectory).Select(entry => entry.NormalizedPath), issues);

    private static void AddPathPrefixCollisionIssues(
        IEnumerable<string> paths,
        ImmutableArray<Mo2ArchiveInspectionIssue>.Builder issues)
    {
        var ordered = paths.Distinct(PathComparer).Order(PathComparer).ToImmutableArray();
        for (var index = 0; index + 1 < ordered.Length; index++)
        {
            if (ordered[index + 1].StartsWith(ordered[index] + "\\", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new("mo2.repair.archive.path_prefix_collision",
                    "A file path is also the parent of another file path.", ordered[index]));
            }
        }
    }

    private static Mo2ArchiveInspectionResult EmptyResult(
        Mo2ArchiveInspectionStatus status,
        string archivePath,
        ImmutableArray<Mo2ArchiveInspectionIssue> issues,
        FileInfo? file = null) =>
        new(status, archivePath, file?.Length ?? 0, null, null,
            file?.LastWriteTimeUtc, file?.LastWriteTimeUtc, [],
            new(Mo2FomodStatus.Absent, null, null, null, null, [], [], []),
            [], null, issues);

    private sealed record EntryContent(long BytesRead, string Sha256, byte[]? Captured);
    private sealed record ArchiveEntryCursor(IEntry Entry, Func<Stream> OpenStream);
    private sealed record MappingSpec(string Source, string Destination, bool IsFolder, int Priority, string Origin);
    private sealed record StagingOutput(string DestinationPath, string TemporaryPath, FileStream Stream);
}
