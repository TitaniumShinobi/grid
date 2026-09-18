using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ExecutableConfigurationService : IMo2ExecutableConfigurationService
{
    private readonly IMo2ReadOnlyFileSystem fileSystem;
    private readonly IMo2PathCanonicalizer paths;
    private readonly IMo2TextDecoder textDecoder;
    private readonly Mo2QSettingsArrayParser parser;
    private readonly IMo2ExecutablePathAuthorization authorizations;
    private readonly TimeProvider timeProvider;
    private readonly Mo2ExecutableObservationLimits limits;

    public Mo2ExecutableConfigurationService(
        IMo2ReadOnlyFileSystem fileSystem,
        IMo2PathCanonicalizer paths,
        IMo2TextDecoder textDecoder,
        IMo2ExecutablePathAuthorization authorizations,
        Mo2QSettingsArrayParser? parser = null,
        TimeProvider? timeProvider = null,
        Mo2ExecutableObservationLimits? limits = null)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.textDecoder = textDecoder ?? throw new ArgumentNullException(nameof(textDecoder));
        this.authorizations = authorizations ?? throw new ArgumentNullException(nameof(authorizations));
        this.parser = parser ?? new();
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.limits = limits ?? new();
        this.limits.Validate();
    }

    public async Task<Mo2ExecutableConfigurationSnapshot> ObserveAsync(
        Mo2ExecutableObservationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sourcePath = request.Validation.IniPath;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Missing,
                "mo2.executables.source_missing",
                "The validated MO2 instance has no readable configuration source.");
        }

        if (!paths.TryCanonicalize(sourcePath, out var canonicalSource, out _))
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Inaccessible,
                "mo2.executables.source_inaccessible",
                "The MO2 executable configuration source could not be canonicalized.");
        }

        Mo2FileReadResult read;
        try
        {
            read = await fileSystem.ReadBytesWithMetadataAsync(
                canonicalSource,
                limits.MaximumConfigurationBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Missing,
                "mo2.executables.source_missing",
                "The MO2 executable configuration source is missing.");
        }
        catch (DirectoryNotFoundException)
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Missing,
                "mo2.executables.source_missing",
                "The MO2 executable configuration source is missing.");
        }
        catch (InvalidDataException)
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Malformed,
                "mo2.executables.source_oversized",
                "The MO2 executable configuration exceeds the bounded read limit.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Inaccessible,
                "mo2.executables.source_inaccessible",
                "The MO2 executable configuration could not be read.");
        }

        Mo2RawTextDocument document;
        try
        {
            document = textDecoder.Decode(read.Bytes, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
        }
        catch (InvalidDataException)
        {
            return EmptySnapshot(
                request.Reference.Id,
                Mo2ExecutableConfigurationStatus.Malformed,
                "mo2.executables.encoding_invalid",
                "The MO2 executable configuration encoding is not supported.");
        }

        var parsed = parser.Parse(document, "customExecutables", limits);
        var expectedRoots = BuildExpectedRoots(request);
        var projected = parsed.Entries
            .OrderBy(entry => entry.SourceOrder)
            .Select((entry, index) => ProjectEntry(request.Reference.Id, entry, index, expectedRoots))
            .ToArray();
        MarkDuplicates(projected);

        var changed = read.Before != read.After;
        var contentFingerprint = Sha256(read.Bytes.AsSpan());
        var provenance = new Mo2ExecutableSourceProvenance(
            canonicalSource,
            timeProvider.GetUtcNow(),
            Mo2QSettingsArrayParser.ParserVersion,
            contentFingerprint,
            read.Before,
            read.After,
            document.Encoding,
            document.Lines.Select(line => line.Terminator).ToImmutableArray());
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (changed)
        {
            issues.Add(new(
                "mo2.executables.changed_during_read",
                Mo2IssueSeverity.Warning,
                "The MO2 executable configuration changed while it was being observed."));
        }

        if (parsed.Status != Grid.Core.Models.ProfileSourceParseStatus.Parsed)
        {
            issues.Add(new(
                "mo2.executables.parse_partial",
                Mo2IssueSeverity.Warning,
                "Some executable configuration syntax was preserved but could not be interpreted."));
        }

        var status = changed
            ? Mo2ExecutableConfigurationStatus.ChangedDuringRead
            : parsed.Status == Grid.Core.Models.ProfileSourceParseStatus.Malformed
                ? Mo2ExecutableConfigurationStatus.Malformed
                : parsed.Warnings.Length > 0 || projected.Any(entry => entry.Warnings.Length > 0)
                    ? Mo2ExecutableConfigurationStatus.Partial
                    : Mo2ExecutableConfigurationStatus.Complete;
        var revision = Sha256(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            contentFingerprint,
            changed,
            string.Join("\n", projected.Select(entry => entry.Fingerprint)),
            string.Join("\n", projected.Select(entry => $"{entry.Binary.Availability}:{entry.Binary.LocationTrust}:{entry.IsDuplicate}")))));
        return new(
            request.Reference.Id,
            status,
            projected.ToImmutableArray(),
            parsed,
            document,
            provenance,
            issues.ToImmutable(),
            revision);
    }

    private Mo2ObservedExecutable ProjectEntry(
        Grid.Core.Models.InstallationReferenceId referenceId,
        Mo2QSettingsArrayEntry entry,
        int sourceOrder,
        ImmutableArray<string> expectedRoots)
    {
        var warnings = entry.Warnings.ToBuilder();
        var title = Get(entry, "title") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            warnings.Add(new("mo2.executables.title_missing", "The executable entry has no usable display title."));
        }

        var binary = ObservePath(
            referenceId,
            "Binary",
            Get(entry, "binary"),
            expectDirectory: false,
            expectedRoots);
        var workingDirectory = ObservePath(
            referenceId,
            "Working directory",
            Get(entry, "workingDirectory"),
            expectDirectory: true,
            expectedRoots);
        var arguments = new Mo2OpaqueArguments(Get(entry, "arguments") ?? string.Empty);
        var fingerprint = Sha256(Encoding.UTF8.GetBytes(string.Join(
            "\n",
            entry.ArrayIndex.ToString(CultureInfo.InvariantCulture),
            string.Join("\n", entry.Fields.Select(field => $"{field.SourceLineIndex}:{field.RawKey}={field.RawValue}")))));

        return new(
            entry.ArrayIndex,
            sourceOrder,
            title,
            binary,
            arguments,
            workingDirectory,
            Get(entry, "steamAppID"),
            ParseBoolean(entry, "toolbar", warnings),
            ParseBoolean(entry, "ownicon", warnings),
            ParseBoolean(entry, "hide", warnings),
            ParseBoolean(entry, "minimizeToSystemTray", warnings),
            entry.Fields,
            false,
            warnings.ToImmutable(),
            fingerprint);
    }

    private Mo2ExecutablePathObservation ObservePath(
        Grid.Core.Models.InstallationReferenceId referenceId,
        string label,
        string? configuredValue,
        bool expectDirectory,
        ImmutableArray<string> expectedRoots)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return new(
                label,
                configuredValue,
                null,
                Mo2ExecutablePathAvailability.NotConfigured,
                Mo2ExecutableLocationTrust.Unknown,
                false,
                "mo2.executables.path_not_configured");
        }

        if (!paths.TryNormalizeLexically(configuredValue, out var lexical, out _))
        {
            return new(
                label,
                configuredValue,
                null,
                Mo2ExecutablePathAvailability.Invalid,
                Mo2ExecutableLocationTrust.Unknown,
                false,
                "mo2.executables.path_invalid");
        }

        var isExpected = expectedRoots.Any(root => IsWithinLexicalRoot(lexical, root));
        var isAuthorized = !isExpected && authorizations.IsPathAuthorized(referenceId, lexical);
        if (!isExpected && !isAuthorized)
        {
            return new(
                label,
                configuredValue,
                lexical,
                Mo2ExecutablePathAvailability.OutsideExpectedRoots,
                Mo2ExecutableLocationTrust.OutsideExpectedRoots,
                false,
                "mo2.executables.path_authorization_required");
        }

        if (!paths.TryCanonicalize(lexical, out var canonical, out _))
        {
            return new(
                label,
                configuredValue,
                null,
                Mo2ExecutablePathAvailability.Inaccessible,
                isExpected ? Mo2ExecutableLocationTrust.ExpectedRoot : Mo2ExecutableLocationTrust.ExplicitlyAuthorized,
                true,
                "mo2.executables.path_inaccessible");
        }

        var state = expectDirectory ? fileSystem.ProbeDirectory(canonical) : fileSystem.ProbeFile(canonical);
        var availability = state switch
        {
            Mo2PathState.Present => Mo2ExecutablePathAvailability.Available,
            Mo2PathState.Missing => Mo2ExecutablePathAvailability.Missing,
            Mo2PathState.Inaccessible or Mo2PathState.Unavailable => Mo2ExecutablePathAvailability.Inaccessible,
            _ => Mo2ExecutablePathAvailability.Invalid,
        };
        return new(
            label,
            configuredValue,
            canonical,
            availability,
            isExpected ? Mo2ExecutableLocationTrust.ExpectedRoot : Mo2ExecutableLocationTrust.ExplicitlyAuthorized,
            true,
            availability == Mo2ExecutablePathAvailability.Available ? null : $"mo2.executables.path_{availability.ToString().ToLowerInvariant()}");
    }

    private ImmutableArray<string> BuildExpectedRoots(Mo2ExecutableObservationRequest request)
    {
        var candidates = new[]
        {
            request.Validation.ApplicationDirectory,
            request.Validation.InstanceDirectory,
            request.Validation.BaseDirectory,
            request.Validation.ModsDirectory,
            request.Validation.ProfilesDirectory,
            request.Validation.DownloadsDirectory,
            request.Validation.OverwriteDirectory,
            request.Validation.GameDirectory,
        }.Concat(request.EffectiveAdditionalExpectedRoots);
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => paths.TryCanonicalize(candidate!, out var canonical, out _) ? canonical : null)
            .Where(canonical => canonical is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static void MarkDuplicates(Mo2ObservedExecutable[] entries)
    {
        var duplicateTitles = entries
            .Select((entry, index) => (entry, index))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.entry.Title))
            .GroupBy(pair => pair.entry.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(pair => pair.index))
            .ToHashSet();
        var duplicateBinaries = entries
            .Select((entry, index) => (entry, index))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.entry.Binary.CanonicalPath))
            .GroupBy(pair => pair.entry.Binary.CanonicalPath!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(pair => pair.index))
            .ToHashSet();
        for (var index = 0; index < entries.Length; index++)
        {
            if (duplicateTitles.Contains(index) || duplicateBinaries.Contains(index))
            {
                entries[index] = entries[index] with
                {
                    IsDuplicate = true,
                    Warnings = entries[index].Warnings.Add(new(
                        "mo2.executables.duplicate",
                        "Another executable entry has the same title or canonical binary path.")),
                };
            }
        }
    }

    private static string? Get(Mo2QSettingsArrayEntry entry, string key) => entry.LastSupported(key)?.LogicalValue;

    private static bool? ParseBoolean(
        Mo2QSettingsArrayEntry entry,
        string key,
        ImmutableArray<Mo2ParseWarning>.Builder warnings)
    {
        var field = entry.LastSupported(key);
        if (field is null)
        {
            return null;
        }

        if (bool.TryParse(field.LogicalValue, out var boolean))
        {
            return boolean;
        }

        if (field.LogicalValue is "1")
        {
            return true;
        }

        if (field.LogicalValue is "0")
        {
            return false;
        }

        warnings.Add(new("mo2.executables.boolean_invalid", $"The '{key}' field is not a supported Boolean value.", field.SourceLineIndex));
        return null;
    }

    private static bool IsWithinLexicalRoot(string child, string root)
    {
        if (string.Equals(child, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return child.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static Mo2ExecutableConfigurationSnapshot EmptySnapshot(
        Grid.Core.Models.InstallationReferenceId referenceId,
        Mo2ExecutableConfigurationStatus status,
        string code,
        string message)
    {
        var parsed = new Mo2QSettingsArrayParseResult(
            "customExecutables",
            null,
            [],
            [],
            [],
            Grid.Core.Models.ProfileSourceParseStatus.NotParsed);
        return new(
            referenceId,
            status,
            [],
            parsed,
            null,
            null,
            [new(code, Mo2IssueSeverity.Warning, message)],
            Sha256(Encoding.UTF8.GetBytes($"{status}:{code}")));
    }
}
