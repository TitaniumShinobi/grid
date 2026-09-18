using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grid.Diagnostics;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    try
    {
        if (arguments.Length == 0 || arguments[0] is "--help" or "-h")
        {
            WriteUsage();
            return arguments.Length == 0 ? 2 : 0;
        }
        if (arguments.Length == 2 && arguments[1] is "--help" or "-h")
        {
            WriteUsage();
            return 0;
        }

        return arguments[0].ToLowerInvariant() switch
        {
            "mo2-register" => await RunRegistrationAsync(arguments[1..]),
            "mo2-baseline" => await RunBaselineAsync(arguments[1..]),
            "bsa-index" => await RunBsaIndexAsync(arguments[1..]),
            "repair-archive-inspect" => await RunArchiveInspectionAsync(arguments[1..]),
            "repair-archive-match" => await RunArchiveMatchAsync(arguments[1..]),
            "repair-archive-classify" => await RunArchiveCandidateClassificationAsync(arguments[1..]),
            "repair-fomod-reconcile" => await RunFomodReconciliationAsync(arguments[1..]),
            "root-cause-collect" => await RunRootCauseCollectionAsync(arguments[1..]),
            "papyrus-inspect" => await RunPapyrusInspectionAsync(arguments[1..]),
            _ => throw new ArgumentException($"Unsupported command '{arguments[0]}'."),
        };
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("Grid.Diagnostics: operation canceled.");
        return 130;
    }
    catch (Exception exception) when (exception is ArgumentException or OverflowException)
    {
        Console.Error.WriteLine($"Grid.Diagnostics: {exception.Message}");
        WriteUsage();
        return 2;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Grid.Diagnostics: {exception.GetType().Name}: operation failed.");
        return 1;
    }
}

static async Task<int> RunBsaIndexAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.BsaIndexKnown,
        new HashSet<string>(["member"], StringComparer.OrdinalIgnoreCase));
    var archivePath = Path.GetFullPath(parsed.Required("archive"));
    var requestedMembers = parsed.All("member")
        .Select(value => value.Replace('/', '\\').TrimStart('\\'))
        .Where(value => value.Length != 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();
    if (requestedMembers.IsEmpty)
    {
        throw new ArgumentException("At least one exact --member path is required.");
    }

    var limits = new Mo2BsaIndexLimits(
        MaximumIndexBytes: parsed.PositiveInt64("max-index-bytes", 64L * 1024 * 1024),
        MaximumMembers: parsed.PositiveInt32("max-members", 1_000_000),
        MaximumVirtualPathLength: parsed.PositiveInt32("max-path-length", 1_024),
        MaximumSegmentLength: parsed.PositiveInt32("max-segment-length", 255));
    await using var file = await new WindowsRandomAccessFileFactory().OpenReadAsync(archivePath);
    var snapshot = await new Mo2BsaIndexParser().ParseAsync(file, limits);
    var indexed = snapshot.Members.ToDictionary(member => member.VirtualPath, StringComparer.OrdinalIgnoreCase);
    var matches = requestedMembers.Select(path =>
    {
        indexed.TryGetValue(path, out var member);
        return new
        {
            virtualPath = path,
            present = member is not null,
            sourceOrder = member?.SourceOrder,
            packedSize = member?.PackedSize,
            dataOffset = member?.DataOffset,
        };
    }).ToImmutableArray();
    var result = new
    {
        schemaVersion = 1,
        status = snapshot.Status,
        archivePath,
        format = snapshot.Format,
        support = snapshot.Support,
        version = snapshot.Version,
        memberCount = snapshot.Members.Length,
        requestedMemberCount = matches.Length,
        presentMemberCount = matches.Count(match => match.present),
        indexFingerprint = snapshot.ContentFingerprint,
        matches,
        warnings = snapshot.Warnings,
    };
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    if (snapshot.Status != Mo2BinaryObservationStatus.Complete)
    {
        return snapshot.Status == Mo2BinaryObservationStatus.Unsupported ? 5 : 4;
    }
    return matches.All(match => match.present) ? 0 : 3;
}

static async Task<int> RunPapyrusInspectionAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.PapyrusKnown, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var inputPath = Path.GetFullPath(parsed.Required("input"));
    var extension = Path.GetExtension(inputPath);
    var requestedFormat = parsed.Optional("format");
    if (requestedFormat is not null && !requestedFormat.Equals("pex", StringComparison.OrdinalIgnoreCase) && !requestedFormat.Equals("psc", StringComparison.OrdinalIgnoreCase))
    {
        throw new ArgumentException("--format must be pex or psc.");
    }
    var format = requestedFormat ?? (extension.Equals(".pex", StringComparison.OrdinalIgnoreCase) ? "pex" : extension.Equals(".psc", StringComparison.OrdinalIgnoreCase) ? "psc" : null);
    if (format is null)
    {
        throw new ArgumentException("--input must name one exact .pex or .psc file unless --format is supplied for a content-addressed blob.");
    }
    var before = new FileInfo(inputPath);
    if (!before.Exists || before.Length <= 0 || before.Length > 32L * 1024 * 1024)
    {
        throw new ArgumentException("--input must name a present non-empty file no larger than 32 MiB.");
    }
    if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
    {
        throw new ArgumentException("--input may not be a reparse point.");
    }
    var expectedSha256 = parsed.Optional("expected-sha256");
    if (expectedSha256 is not null && (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit)))
    {
        throw new ArgumentException("--expected-sha256 must be exactly 64 hexadecimal characters.");
    }

    var bytes = await File.ReadAllBytesAsync(inputPath);
    var after = new FileInfo(inputPath);
    var changed = before.Length != after.Length || before.LastWriteTimeUtc != after.LastWriteTimeUtc;
    var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    var inspection = format.Equals("pex", StringComparison.OrdinalIgnoreCase)
        ? new Mo2PapyrusInspectionService().InspectPex(bytes)
        : new Mo2PapyrusInspectionService().InspectPsc(bytes);
    var status = changed ? "ChangedDuringRead"
        : expectedSha256 is not null && !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase) ? "DigestMismatch"
        : inspection.Status.ToString();
    var result = new PapyrusFileInspectionResult(
        1,
        status,
        inputPath,
        before.Length,
        sha256,
        before.LastWriteTimeUtc.Ticks,
        after.LastWriteTimeUtc.Ticks,
        DateTimeOffset.UtcNow,
        inspection);
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return status == "Complete" ? 0 : status switch
    {
        "DigestMismatch" or "ChangedDuringRead" => 3,
        "Oversized" => 4,
        "Unsupported" => 5,
        _ => 1,
    };
}

static async Task<int> RunRootCauseCollectionAsync(string[] arguments)
{
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    var parsed = Parse(arguments, CliOptions.RootCauseKnown, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var requestPath = Path.GetFullPath(parsed.Required("request"));
    var outputPath = Path.GetFullPath(parsed.Required("output"));
    var requestInfo = new FileInfo(requestPath);
    if (!requestInfo.Exists || requestInfo.Length > 16L * 1024 * 1024)
    {
        throw new ArgumentException("--request must name a present JSON file no larger than 16 MiB.");
    }

    var options = CreateJsonOptions();
    await using var input = File.OpenRead(requestPath);
    var request = await JsonSerializer.DeserializeAsync<RootCauseCollectionRequest>(input, options)
        ?? throw new ArgumentException("--request did not contain a root-cause collection request.");
    if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.CaseId) ||
        request.BaselineManifestSha256 is null || request.BaselineManifestSha256.Length != 64)
    {
        throw new ArgumentException("--request has an unsupported schema or invalid case binding.");
    }
    foreach (var target in request.AssetTargets)
    {
        var extension = Path.GetExtension(target.VirtualPath);
        var expected = target.Kind switch
        {
            Mo2AssetKind.Nif => ".nif",
            Mo2AssetKind.Dds => ".dds",
            Mo2AssetKind.Psc => ".psc",
            Mo2AssetKind.Pex => ".pex",
            _ => throw new ArgumentException("--request contains an unsupported exact asset kind."),
        };
        if (!extension.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--request asset kind does not match its exact virtual-path extension.");
        }
    }

    var files = new WindowsRandomAccessFileFactory();
    var recordRequest = new Mo2Tes4RecordGraphRequest(
        request.Plugins,
        request.Targets,
        request.Limits.RecordLimits ?? new Mo2Tes4RecordGraphLimits(),
        request.CellScope);
    var assetRequest = new Mo2AssetInspectionRequest(
        request.AssetTargets,
        request.Limits.AssetLimits ?? new Mo2AssetInspectionLimits());
    var recordGraph = await new Mo2Tes4RecordGraphCollector(files).CollectAsync(recordRequest);
    var assetGraph = assetRequest.Targets.IsDefaultOrEmpty
        ? new Mo2AssetInspectionResult(Mo2AssetInspectionStatus.Complete, 0, [], [])
        : await new Mo2SkyrimAssetInspectionService(files).InspectAsync(assetRequest);
    var recordBlocking = recordGraph.Status is Mo2Tes4RecordGraphStatus.Refused;
    // Missing, malformed, and digest-mismatched assets are successful observations:
    // they are evidence inputs to diagnosis, not collector transport failures.
    var assetBlocking = assetGraph.Status is Mo2AssetInspectionStatus.Inaccessible or
        Mo2AssetInspectionStatus.Oversized or Mo2AssetInspectionStatus.Cancelled;
    var inputPaths = request.Plugins.ToDictionary(value => value.Name, value => value.CanonicalPath, StringComparer.OrdinalIgnoreCase);
    var rawSources = recordGraph.Plugins.Select(plugin => new RootCauseRawSource(
            $"plugin:{plugin.Name}", inputPaths[plugin.Name], plugin.Name, "Plugin", plugin.RawSha256, plugin.Length, "Readable",
            plugin.Before.Identity.LastWriteTimeUtcTicks, plugin.After.Identity.LastWriteTimeUtcTicks))
        .Concat(assetGraph.Targets.Where(asset => asset.Sha256 is not null).Select(asset => new RootCauseRawSource(
            $"asset:{asset.VirtualPath}", asset.SourcePath, asset.VirtualPath, asset.Origin.ToString(), asset.Sha256!, asset.Length ?? 0, "Readable",
            asset.Before?.Identity.LastWriteTimeUtcTicks, asset.After?.Identity.LastWriteTimeUtcTicks)))
        .OrderBy(source => source.Kind, StringComparer.Ordinal)
        .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(source => source.Name, StringComparer.Ordinal)
        .ToImmutableArray();
    var issues = recordGraph.Issues.Select(issue => new RootCauseIssue(issue.Code, issue.Detail, "Warning"))
        .Concat(assetGraph.Issues.Select(issue => new RootCauseIssue(issue.Code, issue.Detail, "Warning")))
        .ToImmutableArray();
    var result = new RootCauseCollectionResult(
        1,
        recordBlocking || assetBlocking ? "Failed" : "Completed",
        request.CaseId,
        request.BaselineManifestSha256.ToUpperInvariant(),
        request.InstallationId,
        request.ProfileId,
        request.ContextFingerprint,
        recordGraph,
        assetGraph,
        issues,
        new(recordGraph.RecordHeadersExamined, assetGraph.Targets.Length,
            checked(recordGraph.BytesScanned + assetGraph.InspectedBytes), recordGraph.BytesScanned, assetGraph.InspectedBytes,
            elapsed.ElapsedMilliseconds),
        rawSources);

    var outputDirectory = Path.GetDirectoryName(outputPath)
        ?? throw new ArgumentException("--output must include a parent directory.");
    Directory.CreateDirectory(outputDirectory);
    var temporary = Path.Combine(outputDirectory, $".grid-root-cause-{Guid.NewGuid():N}.tmp");
    try
    {
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, result, options);
            await stream.FlushAsync();
        }
        File.Move(temporary, outputPath, overwrite: false);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
    return result.Status == "Completed" ? 0 : 5;
}

static async Task<int> RunRegistrationAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.RegistrationKnown, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var application = parsed.Required("application", "application-path");
    var instance = parsed.Required("instance", "instance-path");
    var expectedProfile = parsed.Required("expected-profile", "profile", "profile-name");
    var displayName = parsed.Required("display-name");
    var dataRoot = Path.GetFullPath(parsed.Required("grid-data-root"));
    var globalInstancesRoot = parsed.Optional("global-instances-root") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
    var storePath = Path.Combine(dataRoot, "connections", "mo2-installations.v1.json");

    var fileSystem = new Mo2FileSystem();
    var paths = new WindowsPathCanonicalizer();
    var ini = new Mo2IniReader(fileSystem);
    var validator = new Mo2InstallationValidator(fileSystem, ini, paths, globalInstancesRoot);
    var store = new Mo2InstallationReferenceStore(fileSystem, paths, storePath);
    var connections = new Mo2ConnectionService(validator, store, paths);
    var service = new Mo2RegistrationService(validator, connections, store, fileSystem, new Mo2TextDecoder(), paths);
    var result = await service.RegisterAsync(new(
        application,
        instance,
        expectedProfile,
        displayName,
        new("game.skyrim-special-edition"),
        new("adapter.mod-organizer-2")));

    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return result.Status switch
    {
        Mo2RegistrationStatus.Registered or Mo2RegistrationStatus.Reused => 0,
        Mo2RegistrationStatus.InstallationContextUnresolved => 4,
        Mo2RegistrationStatus.PersistenceFailed => 5,
        _ => 1,
    };
}

static async Task<int> RunBaselineAsync(string[] arguments)
{
        var parsed = Parse(arguments, CliOptions.BaselineKnown,
            new HashSet<string>(["authorize", "provider", "plugin"], StringComparer.OrdinalIgnoreCase));
        var application = parsed.Required("application", "application-path");
        var instance = parsed.Required("instance", "instance-path");
        var profile = parsed.Required("profile", "profile-name");
        var format = (parsed.Optional("format") ?? "ndjson").ToLowerInvariant();
        if (format is not ("json" or "ndjson"))
        {
            throw new ArgumentException("--format must be json or ndjson.");
        }

        var limits = new Mo2BaselineLimits(
            MaximumEntries: parsed.PositiveInt64("max-entries", 2_000_000),
            MaximumTotalHashBytes: parsed.PositiveInt64("max-total-hash-bytes", 1_099_511_627_776L),
            MaximumFileBytes: parsed.NonNegativeInt64("max-file-bytes", 0),
            CheckpointInterval: checked((int)parsed.PositiveInt64("checkpoint-interval", 100_000)),
            MaximumRecordCatalogEntries: parsed.PositiveInt64("max-record-catalog-entries", 10_000_000));
        limits.Validate();
        var request = new Mo2BaselineRequest(
            application,
            instance,
            profile,
            parsed.All("authorize").ToImmutableArray(),
            parsed.All("provider").ToImmutableArray(),
            parsed.All("plugin").ToImmutableArray(),
            limits,
            LoadResumeHashes(parsed.Optional("resume"), limits.MaximumEntries));

        await using var writer = new Mo2BaselineEventWriter(Console.OpenStandardOutput(), format);
        var summary = await Mo2BaselineService.CreateDefault().CaptureAsync(request, writer.WriteAsync);
        return summary.Status switch
        {
            Mo2BaselineStatus.Completed or Mo2BaselineStatus.Partial => 0,
            Mo2BaselineStatus.AuthorizationRequired => 3,
            Mo2BaselineStatus.ContextUnavailable or Mo2BaselineStatus.Unavailable => 4,
            Mo2BaselineStatus.Canceled => 130,
            _ => 1,
        };
}

static async Task<int> RunArchiveInspectionAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.ArchiveKnown,
        new HashSet<string>(["select"], StringComparer.OrdinalIgnoreCase));
    var mode = (parsed.Optional("mode") ?? "inspect").ToLowerInvariant() switch
    {
        "inspect" => Mo2ArchiveExtractionMode.None,
        "full" => Mo2ArchiveExtractionMode.FullArchive,
        "fomod" => Mo2ArchiveExtractionMode.FomodSelection,
        _ => throw new ArgumentException("--mode must be inspect, full, or fomod."),
    };
    var staging = parsed.Optional("staging");
    if (mode != Mo2ArchiveExtractionMode.None && string.IsNullOrWhiteSpace(staging))
    {
        throw new ArgumentException("--staging is required when --mode is full or fomod.");
    }
    if (mode == Mo2ArchiveExtractionMode.None && staging is not null)
    {
        throw new ArgumentException("--staging is valid only when --mode is full or fomod.");
    }

    var selections = ParseSelections(parsed.All("select"));
    if (mode != Mo2ArchiveExtractionMode.FomodSelection && !selections.IsEmpty)
    {
        throw new ArgumentException("--select is valid only when --mode is fomod.");
    }

    var limits = new Mo2ArchiveInspectionLimits(
        MaximumArchiveBytes: parsed.PositiveInt64("max-archive-bytes", 1_099_511_627_776L),
        MaximumEntries: parsed.PositiveInt32("max-entries", 1_000_000),
        MaximumDepth: parsed.PositiveInt32("max-depth", 64),
        MaximumPathLength: parsed.PositiveInt32("max-path-length", 1_024),
        MaximumSegmentLength: parsed.PositiveInt32("max-segment-length", 255),
        MaximumExpandedBytes: parsed.PositiveInt64("max-expanded-bytes", 17_179_869_184L),
        MaximumCompressionRatio: parsed.DoubleAtLeast("max-compression-ratio", 1_000d, 1d),
        MaximumFomodDocumentBytes: parsed.PositiveInt32("max-fomod-document-bytes", 4 * 1024 * 1024),
        BufferBytes: parsed.Int32AtLeast("buffer-bytes", 64 * 1024, 4 * 1024));
    limits.Validate();
    var request = new Mo2ArchiveInspectionRequest(
        parsed.Required("archive"),
        parsed.Optional("expected-sha256"),
        mode,
        staging,
        selections,
        limits);
    var result = await new Mo2RepairArchiveService().InspectAsync(request);
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return result.Status switch
    {
        Mo2ArchiveInspectionStatus.Complete or Mo2ArchiveInspectionStatus.Extracted => 0,
        Mo2ArchiveInspectionStatus.Rejected or Mo2ArchiveInspectionStatus.ChangedDuringRead => 3,
        Mo2ArchiveInspectionStatus.Inaccessible => 4,
        Mo2ArchiveInspectionStatus.Unsupported => 5,
        _ => 1,
    };
}

static async Task<int> RunArchiveMatchAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.ArchiveMatchKnown,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    var requirementsPath = Path.GetFullPath(parsed.Required("requirements"));
    var requirementsInfo = new FileInfo(requirementsPath);
    if (!requirementsInfo.Exists || requirementsInfo.Length > 512L * 1024 * 1024 ||
        (requirementsInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("--requirements must name a present, non-reparse NDJSON file no larger than 512 MiB.");

    var maximumEntries = parsed.PositiveInt32("max-entries", 1_000_000);
    var requirements = ImmutableArray.CreateBuilder<Mo2ArchiveRequiredFile>();
    foreach (var line in File.ReadLines(requirementsPath))
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        if (requirements.Count >= maximumEntries)
            throw new ArgumentException("--requirements exceeds the configured record limit.");
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        requirements.Add(new(
            root.GetProperty("pluginName").GetString() ?? throw new ArgumentException("A requirement omits pluginName."),
            root.GetProperty("requiredVirtualPath").GetString() ?? throw new ArgumentException("A requirement omits requiredVirtualPath.")));
    }

    var limits = new Mo2ArchiveInspectionLimits(
        MaximumArchiveBytes: parsed.PositiveInt64("max-archive-bytes", 1_099_511_627_776L),
        MaximumEntries: maximumEntries,
        MaximumDepth: parsed.PositiveInt32("max-depth", 64),
        MaximumPathLength: parsed.PositiveInt32("max-path-length", 1_024),
        MaximumSegmentLength: parsed.PositiveInt32("max-segment-length", 255),
        MaximumExpandedBytes: parsed.PositiveInt64("max-expanded-bytes", 17_179_869_184L),
        MaximumCompressionRatio: parsed.DoubleAtLeast("max-compression-ratio", 1_000d, 1d),
        MaximumFomodDocumentBytes: parsed.PositiveInt32("max-fomod-document-bytes", 4 * 1024 * 1024),
        BufferBytes: parsed.Int32AtLeast("buffer-bytes", 64 * 1024, 4 * 1024));
    var inspection = await new Mo2RepairArchiveService().InspectAsync(new(
        parsed.Required("archive"), parsed.Optional("expected-sha256"),
        Mo2ArchiveExtractionMode.None, null, [], limits));
    var result = new Mo2ArchiveRequirementMatcher().Match(inspection, requirements);
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return inspection.Status switch
    {
        Mo2ArchiveInspectionStatus.Complete => 0,
        Mo2ArchiveInspectionStatus.Rejected or Mo2ArchiveInspectionStatus.ChangedDuringRead => 3,
        Mo2ArchiveInspectionStatus.Inaccessible => 4,
        Mo2ArchiveInspectionStatus.Unsupported => 5,
        _ => 1,
    };
}

static async Task<int> RunArchiveCandidateClassificationAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.ArchiveCandidateKnown,
        new HashSet<string>(["dependent", "bundled", "select"], StringComparer.OrdinalIgnoreCase));
    var limits = new Mo2ArchiveInspectionLimits(
        MaximumArchiveBytes: parsed.PositiveInt64("max-archive-bytes", 1_099_511_627_776L),
        MaximumEntries: parsed.PositiveInt32("max-entries", 1_000_000),
        MaximumDepth: parsed.PositiveInt32("max-depth", 64),
        MaximumPathLength: parsed.PositiveInt32("max-path-length", 1_024),
        MaximumSegmentLength: parsed.PositiveInt32("max-segment-length", 255),
        MaximumExpandedBytes: parsed.PositiveInt64("max-expanded-bytes", 17_179_869_184L),
        MaximumCompressionRatio: parsed.DoubleAtLeast("max-compression-ratio", 1_000d, 1d),
        MaximumFomodDocumentBytes: parsed.PositiveInt32("max-fomod-document-bytes", 4 * 1024 * 1024),
        BufferBytes: parsed.Int32AtLeast("buffer-bytes", 64 * 1024, 4 * 1024));
    limits.Validate();
    var inspection = await new Mo2RepairArchiveService().InspectAsync(new(
        parsed.Required("archive"),
        parsed.Optional("expected-sha256"),
        Mo2ArchiveExtractionMode.None,
        null,
        ParseSelections(parsed.All("select")),
        limits));

    var dependents = ParseNamedValues(parsed.All("dependent"), "--dependent", requireSha256: true);
    var bundled = ParseNamedValues(parsed.All("bundled"), "--bundled", requireSha256: false);
    var unknownBundled = bundled.Keys.Where(name => !dependents.ContainsKey(name)).ToArray();
    if (unknownBundled.Length != 0)
        throw new ArgumentException("Every --bundled plugin must also have one --dependent installed hash.");
    var dependentEvidence = dependents
        .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .Select(item => new Mo2InstalledDependentPluginEvidence(
            item.Key,
            item.Value,
            bundled.TryGetValue(item.Key, out var entryPath) ? entryPath : null))
        .ToImmutableArray();
    var result = new Mo2ArchiveCandidateAssessmentService().Assess(new(
        inspection,
        parsed.Required("primary-plugin-name"),
        parsed.Optional("primary-plugin-entry"),
        parsed.Optional("primary-archive-entry"),
        parsed.Required("installed-plugin-sha256"),
        parsed.Optional("installed-version"),
        parsed.Optional("candidate-version"),
        dependentEvidence,
        parsed.Optional("installed-archive-sha256")));
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return inspection.Status switch
    {
        Mo2ArchiveInspectionStatus.Complete => 0,
        Mo2ArchiveInspectionStatus.Rejected or Mo2ArchiveInspectionStatus.ChangedDuringRead => 3,
        Mo2ArchiveInspectionStatus.Inaccessible => 4,
        Mo2ArchiveInspectionStatus.Unsupported => 5,
        _ => 1,
    };
}

static async Task<int> RunFomodReconciliationAsync(string[] arguments)
{
    var parsed = Parse(arguments, CliOptions.FomodReconciliationKnown,
        new HashSet<string>(["provider"], StringComparer.OrdinalIgnoreCase));
    var providers = parsed.All("provider")
        .Select(value => value.Trim())
        .Where(value => value.Length != 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToImmutableArray();
    if (providers.IsEmpty)
        throw new ArgumentException("At least one exact --provider name is required.");

    var maximumEntries = parsed.PositiveInt32("max-entries", 1_000_000);
    var installedFiles = LoadInstalledFileEvidence(
        parsed.Required("installed-files"), providers, maximumEntries);
    var limits = new Mo2ArchiveInspectionLimits(
        MaximumArchiveBytes: parsed.PositiveInt64("max-archive-bytes", 1_099_511_627_776L),
        MaximumEntries: maximumEntries,
        MaximumDepth: parsed.PositiveInt32("max-depth", 64),
        MaximumPathLength: parsed.PositiveInt32("max-path-length", 1_024),
        MaximumSegmentLength: parsed.PositiveInt32("max-segment-length", 255),
        MaximumExpandedBytes: parsed.PositiveInt64("max-expanded-bytes", 17_179_869_184L),
        MaximumCompressionRatio: parsed.DoubleAtLeast("max-compression-ratio", 1_000d, 1d),
        MaximumFomodDocumentBytes: parsed.PositiveInt32("max-fomod-document-bytes", 4 * 1024 * 1024),
        BufferBytes: parsed.Int32AtLeast("buffer-bytes", 64 * 1024, 4 * 1024));
    limits.Validate();
    var inspection = await new Mo2RepairArchiveService().InspectAsync(new(
        parsed.Required("archive"), parsed.Optional("expected-sha256"),
        Mo2ArchiveExtractionMode.None, null, [], limits));
    var result = new Mo2FomodSelectionReconciliationService().Reconcile(
        inspection, installedFiles, parsed.Optional("installed-archive-sha256"));
    await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), result, CreateJsonOptions());
    Console.Out.WriteLine();
    return result.Status == "Complete" ? 0 : 3;
}

static ImmutableArray<Mo2InstalledFileEvidence> LoadInstalledFileEvidence(
    string path,
    ImmutableArray<string> providers,
    int maximumEntries)
{
    var full = Path.GetFullPath(path);
    var info = new FileInfo(full);
    if (!info.Exists || info.Length > 16L * 1024 * 1024 * 1024 ||
        (info.Attributes & FileAttributes.ReparsePoint) != 0)
        throw new ArgumentException("--installed-files must name a present, non-reparse bounded NDJSON artifact.");
    var result = ImmutableArray.CreateBuilder<Mo2InstalledFileEvidence>();
    long lines = 0;
    foreach (var line in File.ReadLines(full))
    {
        if (++lines > maximumEntries * 8L)
            throw new ArgumentException("--installed-files exceeds the configured record limit.");
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.TryGetProperty("recordType", out var recordType))
        {
            if (recordType.GetString() != "fileHash" || !root.TryGetProperty("payload", out root)) continue;
        }
        if (!root.TryGetProperty("status", out var status) ||
            !string.Equals(status.GetString(), "complete", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("providerName", out var providerProperty) ||
            providerProperty.GetString() is not { } provider ||
            !providers.Contains(provider, StringComparer.OrdinalIgnoreCase)) continue;
        if (!root.TryGetProperty("virtualPath", out var pathProperty) ||
            !root.TryGetProperty("sha256", out var hashProperty) ||
            pathProperty.GetString() is not { } virtualPath ||
            hashProperty.GetString() is not { } sha256) continue;
        result.Add(new(virtualPath, provider, sha256));
        if (result.Count > maximumEntries)
            throw new ArgumentException("--installed-files contains too many matching records.");
    }
    return result.ToImmutable();
}

static ImmutableDictionary<string, Mo2BaselineFileHashRecord> LoadResumeHashes(string? path, long maximumEntries)
{
    var empty = ImmutableDictionary<string, Mo2BaselineFileHashRecord>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(path)) return empty;
    var full = Path.GetFullPath(path);
    var info = new FileInfo(full);
    if (!info.Exists || info.Length > 16L * 1024 * 1024 * 1024)
        throw new ArgumentException("--resume must name a present bounded predecessor NDJSON artifact.");
    var builder = empty.ToBuilder();
    long lines = 0;
    foreach (var line in File.ReadLines(full))
    {
        if (++lines > maximumEntries * 8) throw new ArgumentException("--resume exceeds the bounded predecessor record count.");
        if (string.IsNullOrWhiteSpace(line)) continue;
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("recordType", out var recordType) || recordType.GetString() != "fileHash") continue;
        var payload = root.GetProperty("payload");
        if (!string.Equals(payload.GetProperty("status").GetString(), "complete", StringComparison.OrdinalIgnoreCase)) continue;
        var canonicalPath = Path.GetFullPath(payload.GetProperty("canonicalPath").GetString()!);
        var sha256 = payload.GetProperty("sha256").GetString();
        if (sha256 is null || sha256.Length != 64) throw new ArgumentException("--resume contains an invalid completed hash.");
        builder[canonicalPath] = new(
            payload.GetProperty("virtualPath").GetString()!, canonicalPath,
            Enum.Parse<Mo2BaselineFileKind>(payload.GetProperty("kind").GetString()!, true),
            payload.GetProperty("providerName").GetString()!, payload.GetProperty("length").GetInt64(),
            payload.GetProperty("lastWriteTimeUtcTicks").GetInt64(), Mo2BaselineHashStatus.Complete,
            sha256, "Verified predecessor partition");
    }
    return builder.ToImmutable();
}

static ImmutableArray<Mo2FomodGroupSelection> ParseSelections(IEnumerable<string> values)
{
    var selections = new Dictionary<string, List<string>>(StringComparer.Ordinal);
    foreach (var value in values)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || value.IndexOf('=', separator + 1) >= 0)
        {
            throw new ArgumentException("--select must use the exact form group=plugin.");
        }
        var group = value[..separator].Trim();
        var plugin = value[(separator + 1)..].Trim();
        if (group.Length == 0)
        {
            throw new ArgumentException("--select group names must not be empty.");
        }
        if (!selections.TryGetValue(group, out var plugins))
        {
            plugins = [];
            selections.Add(group, plugins);
        }
        if (plugin.Length != 0)
        {
            plugins.Add(plugin);
        }
    }
    return selections.OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => new Mo2FomodGroupSelection(
            item.Key,
            item.Value.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray()))
        .ToImmutableArray();
}

static Dictionary<string, string> ParseNamedValues(
    IEnumerable<string> values,
    string optionName,
    bool requireSha256)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in values)
    {
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
            throw new ArgumentException($"{optionName} must use the exact form plugin=value.");
        var name = value[..separator].Trim();
        var mappedValue = value[(separator + 1)..].Trim();
        if (name.Length == 0 || name.Any(char.IsControl) || mappedValue.Length == 0 ||
            (requireSha256 && (mappedValue.Length != 64 || mappedValue.Any(character => !Uri.IsHexDigit(character)))))
            throw new ArgumentException($"{optionName} contains an invalid plugin name or value.");
        if (!result.TryAdd(name, mappedValue))
            throw new ArgumentException($"{optionName} names plugin '{name}' more than once.");
    }
    return result;
}

static ParsedArguments Parse(
    string[] arguments,
    IReadOnlySet<string> knownOptions,
    IReadOnlySet<string> repeatableOptions)
{
    var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < arguments.Length; index++)
    {
        var token = arguments[index];
        if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
        {
            throw new ArgumentException($"Unexpected argument '{token}'.");
        }
        if (index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Option '{token}' requires a value.");
        }

        var name = token[2..];
        if (!knownOptions.Contains(name))
        {
            throw new ArgumentException($"Unknown option '{token}'.");
        }
        if (!values.TryGetValue(name, out var list))
        {
            list = [];
            values.Add(name, list);
        }
        if (!repeatableOptions.Contains(name) && list.Count > 0)
        {
            throw new ArgumentException($"Option '{token}' may be supplied only once.");
        }
        list.Add(arguments[++index]);
    }
    return new(values);
}

static void WriteUsage() => Console.Error.WriteLine("""
    Usage:
      Grid.Diagnostics mo2-register --application <path> --instance <path> --expected-profile <name> --display-name <name> --grid-data-root <path> [--global-instances-root <path>]
      Grid.Diagnostics mo2-baseline --application <path> --instance <path> --profile <name> [--authorize <exact-path>]... [--provider <exact-provider-name>]... [--plugin <exact-plugin-name>]... [--resume <sealed-predecessor.ndjson>] [--format ndjson|json] [--max-entries N] [--max-record-catalog-entries N] [--max-total-hash-bytes N] [--max-file-bytes N] [--checkpoint-interval N]
      Grid.Diagnostics bsa-index --archive <path> --member <exact-virtual-path>... [--max-index-bytes N] [--max-members N] [--max-path-length N] [--max-segment-length N]
      Grid.Diagnostics repair-archive-inspect --archive <path> [--expected-sha256 <hex>] [--mode inspect|full|fomod] [--staging <empty-path>] [--select <group=plugin>]... [--max-archive-bytes N] [--max-entries N] [--max-depth N] [--max-path-length N] [--max-segment-length N] [--max-expanded-bytes N] [--max-compression-ratio N] [--max-fomod-document-bytes N] [--buffer-bytes N]
      Grid.Diagnostics repair-archive-match --archive <path> --expected-sha256 <hex> --requirements <case-local.ndjson> [--max-archive-bytes N] [--max-entries N] [--max-depth N] [--max-path-length N] [--max-segment-length N] [--max-expanded-bytes N] [--max-compression-ratio N] [--max-fomod-document-bytes N] [--buffer-bytes N]
      Grid.Diagnostics repair-archive-classify --archive <path> --expected-sha256 <hex> --primary-plugin-name <name> --installed-plugin-sha256 <hex> [--installed-archive-sha256 <hex>] [--select <group=plugin>]... [--primary-plugin-entry <archive-path>] [--primary-archive-entry <archive-path>] [--installed-version <version>] [--candidate-version <version>] [--dependent <plugin=installed-sha256>]... [--bundled <plugin=archive-entry>]... [archive resource limits]
      Grid.Diagnostics repair-fomod-reconcile --archive <path> --expected-sha256 <hex> --installed-files <case-local.ndjson> --provider <exact-name>... [--installed-archive-sha256 <hex>] [archive resource limits]
      Grid.Diagnostics root-cause-collect --request <case-local.json> --output <case-local.json>
      Grid.Diagnostics papyrus-inspect --input <exact.pex|exact.psc|content-addressed-blob> [--format pex|psc] [--expected-sha256 <hex>]
    """);

static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
{
    Converters = { new JsonStringEnumConverter() },
};

static class CliOptions
{
    public static readonly HashSet<string> RegistrationKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "application-path", "instance", "instance-path", "expected-profile", "profile", "profile-name",
        "display-name", "grid-data-root", "global-instances-root",
    };

    public static readonly HashSet<string> BaselineKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "application-path", "instance", "instance-path", "profile", "profile-name", "authorize", "provider", "plugin", "resume",
        "format", "max-entries", "max-record-catalog-entries", "max-total-hash-bytes", "max-file-bytes", "checkpoint-interval",
    };

    public static readonly HashSet<string> ArchiveKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "expected-sha256", "mode", "staging", "select", "max-archive-bytes", "max-entries",
        "max-depth", "max-path-length", "max-segment-length", "max-expanded-bytes", "max-compression-ratio",
        "max-fomod-document-bytes", "buffer-bytes",
    };

    public static readonly HashSet<string> BsaIndexKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "member", "max-index-bytes", "max-members", "max-path-length", "max-segment-length",
    };

    public static readonly HashSet<string> ArchiveMatchKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "expected-sha256", "requirements", "max-archive-bytes", "max-entries",
        "max-depth", "max-path-length", "max-segment-length", "max-expanded-bytes", "max-compression-ratio",
        "max-fomod-document-bytes", "buffer-bytes",
    };

    public static readonly HashSet<string> ArchiveCandidateKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "expected-sha256", "primary-plugin-name", "primary-plugin-entry", "primary-archive-entry",
        "installed-plugin-sha256", "installed-archive-sha256", "installed-version", "candidate-version", "dependent", "bundled", "select",
        "max-archive-bytes", "max-entries", "max-depth", "max-path-length", "max-segment-length",
        "max-expanded-bytes", "max-compression-ratio", "max-fomod-document-bytes", "buffer-bytes",
    };

    public static readonly HashSet<string> FomodReconciliationKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "archive", "expected-sha256", "installed-files", "provider", "installed-archive-sha256",
        "max-archive-bytes", "max-entries", "max-depth", "max-path-length", "max-segment-length",
        "max-expanded-bytes", "max-compression-ratio", "max-fomod-document-bytes", "buffer-bytes",
    };

    public static readonly HashSet<string> RootCauseKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "request", "output",
    };

    public static readonly HashSet<string> PapyrusKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "format", "expected-sha256",
    };
}

sealed record PapyrusFileInspectionResult(
    int SchemaVersion,
    string Status,
    string SourcePath,
    long SizeBytes,
    string Sha256,
    long BeforeLastWriteTimeUtcTicks,
    long AfterLastWriteTimeUtcTicks,
    DateTimeOffset CollectedAtUtc,
    Mo2PapyrusInspection Inspection);

sealed record RootCauseCollectionRequest(
    int SchemaVersion,
    string CaseId,
    string BaselineManifestSha256,
    string InstallationId,
    string ProfileId,
    string ContextFingerprint,
    ImmutableArray<Mo2Tes4PluginInput> Plugins,
    ImmutableArray<Mo2Tes4CanonicalRecordKey> Targets,
    Mo2Tes4CellScopeRequest? CellScope,
    ImmutableArray<Mo2AssetTarget> AssetTargets,
    ImmutableArray<RootCauseAllowedSource> AllowedSources,
    RootCauseCollectionLimits Limits);

sealed record RootCauseAllowedSource(string Path, string Sha256);

sealed record RootCauseCollectionLimits(
    int MaximumWallClockSeconds,
    long? MaximumRecords,
    long? MaximumAssets,
    long? MaximumBytes,
    Mo2Tes4RecordGraphLimits? RecordLimits,
    Mo2AssetInspectionLimits? AssetLimits);

sealed record RootCauseRawSource(
    string SourceId,
    string Path,
    string Name,
    string Kind,
    string Sha256,
    long SizeBytes,
    string Readability,
    long? BeforeLastWriteTimeUtcTicks,
    long? AfterLastWriteTimeUtcTicks);

sealed record RootCauseIssue(string Code, string Detail, string Severity);

sealed record RootCauseUsage(
    long RecordsExamined,
    long AssetsExamined,
    long BytesRead,
    long PluginBytesScanned,
    long AssetBytesInspected,
    long WallClockMilliseconds);

sealed record RootCauseCollectionResult(
    int SchemaVersion,
    string Status,
    string CaseId,
    string BaselineManifestSha256,
    string InstallationId,
    string ProfileId,
    string ContextFingerprint,
    Mo2Tes4RecordGraphResult RecordGraph,
    Mo2AssetInspectionResult AssetGraph,
    ImmutableArray<RootCauseIssue> Issues,
    RootCauseUsage Usage,
    ImmutableArray<RootCauseRawSource> RawSources);

sealed class ParsedArguments(Dictionary<string, List<string>> values)
{
    public string Required(params string[] names) =>
        names.Select(Optional).FirstOrDefault(value => value is not null)
        ?? throw new ArgumentException($"Missing required option --{names[0]}.");

    public string? Optional(string name) =>
        values.TryGetValue(name, out var entries) ? entries.Single() : null;

    public IEnumerable<string> All(string name) =>
        values.TryGetValue(name, out var entries) ? entries : [];

    public long PositiveInt64(string name, long defaultValue)
    {
        var value = ParseInt64(name, defaultValue);
        return value > 0 ? value : throw new ArgumentException($"--{name} must be greater than zero.");
    }

    public long NonNegativeInt64(string name, long defaultValue)
    {
        var value = ParseInt64(name, defaultValue);
        return value >= 0 ? value : throw new ArgumentException($"--{name} must be zero or greater.");
    }

    public int PositiveInt32(string name, int defaultValue) => checked((int)PositiveInt64(name, defaultValue));

    public int Int32AtLeast(string name, int defaultValue, int minimum)
    {
        var value = checked((int)ParseInt64(name, defaultValue));
        return value >= minimum ? value : throw new ArgumentException($"--{name} must be at least {minimum}.");
    }

    public double DoubleAtLeast(string name, double defaultValue, double minimum)
    {
        var raw = Optional(name);
        var value = raw is null
            ? defaultValue
            : double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"--{name} must be a decimal number.");
        return double.IsFinite(value) && value >= minimum
            ? value
            : throw new ArgumentException($"--{name} must be a finite value of at least {minimum}.");
    }

    private long ParseInt64(string name, long defaultValue)
    {
        var raw = Optional(name);
        if (raw is null)
        {
            return defaultValue;
        }
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"--{name} must be an integer.");
    }
}
