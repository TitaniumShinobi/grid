using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ModInventoryService : IMo2ModInventoryService
{
    public const int MaximumMetadataBytes = 2 * 1024 * 1024;
    public const int MaximumCategoriesBytes = 8 * 1024 * 1024;
    public const int MaximumRefreshBytes = 256 * 1024 * 1024;
    public const int MaximumConcurrentReads = 4;
    public const string ParserVersion = "grid.mo2.inventory.v1";

    private readonly IMo2InventoryFileSystem fileSystem;
    private readonly IMo2TextDecoder decoder;
    private readonly IMo2PathCanonicalizer paths;
    private readonly IMo2ModsPathAuthorization authorization;
    private readonly Func<DateTimeOffset> utcNow;

    public Mo2ModInventoryService(
        IMo2InventoryFileSystem fileSystem,
        IMo2TextDecoder decoder,
        IMo2PathCanonicalizer paths,
        IMo2ModsPathAuthorization authorization,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.fileSystem = fileSystem;
        this.decoder = decoder;
        this.paths = paths;
        this.authorization = authorization;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<Mo2ModInventorySnapshot> ObserveAsync(
        Mo2ModInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = utcNow().ToUniversalTime();
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        var validation = request.Validation;
        if (validation.ModsDirectory is null || validation.InstanceDirectory is null ||
            !paths.Equals(request.Reference.InstanceDirectory, validation.InstanceDirectory))
        {
            issues.Add(new(
                "mo2.mods.validation_required",
                Mo2IssueSeverity.Error,
                "A current validation bound to this connected MO2 reference is required."));
            return Empty(request.Reference.Id, observedAt, issues);
        }

        var modsRoot = validation.ModsDirectory;
        var executableRoot = Path.GetDirectoryName(request.Reference.ExecutablePath);
        var automaticallyAuthorized = paths.IsWithinRoot(modsRoot, request.Reference.InstanceDirectory) ||
            executableRoot is not null && paths.IsWithinRoot(modsRoot, executableRoot);
        if (!automaticallyAuthorized && !authorization.IsModsRootAuthorized(request.Reference.Id, modsRoot))
        {
            issues.Add(new(
                "mo2.mods.authorization_required",
                Mo2IssueSeverity.Error,
                "Explicit session authorization is required before Grid reads the exact configured mods root.",
                "Mods directory"));
            return Empty(request.Reference.Id, observedAt, issues, modsRoot);
        }

        if (!paths.TryCanonicalize(modsRoot, out var canonicalRoot, out _))
        {
            issues.Add(new(
                "mo2.mods.root_unavailable",
                Mo2IssueSeverity.Error,
                "The authorized mods root could not be canonicalized.",
                "Mods directory"));
            return Empty(request.Reference.Id, observedAt, issues, modsRoot);
        }

        var initial = fileSystem.EnumerateDirectoriesWithState(canonicalRoot);
        if (initial.State != Mo2PathState.Present)
        {
            issues.Add(new(
                "mo2.mods.root_unavailable",
                Mo2IssueSeverity.Error,
                "The authorized mods root is missing or inaccessible.",
                "Mods directory"));
            return Empty(request.Reference.Id, observedAt, issues, canonicalRoot);
        }

        var safeDirectories = CanonicalizeChildren(canonicalRoot, initial, issues);
        var budget = new ReadBudget(MaximumRefreshBytes);
        var categories = await ReadCategoriesAsync(
            request.Reference.InstanceDirectory,
            observedAt,
            budget,
            cancellationToken).ConfigureAwait(false);
        using var gate = new SemaphoreSlim(MaximumConcurrentReads, MaximumConcurrentReads);
        var directoryTasks = safeDirectories.Select(async directory =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ObserveDirectoryAsync(directory, categories, observedAt, budget, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var directories = (await Task.WhenAll(directoryTasks).ConfigureAwait(false))
            .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(directory => directory.IdentityKey, StringComparer.Ordinal)
            .ToImmutableArray();

        var final = fileSystem.EnumerateDirectoriesWithState(canonicalRoot);
        var finalDirectories = final.State == Mo2PathState.Present
            ? CanonicalizeChildren(canonicalRoot, final, issues)
            : [];
        var containerChanged = final.State != Mo2PathState.Present ||
            !safeDirectories.SequenceEqual(finalDirectories, StringComparer.OrdinalIgnoreCase);
        if (containerChanged)
        {
            issues.Add(new(
                "mo2.mods.changed_during_read",
                Mo2IssueSeverity.Warning,
                "The mods-directory membership changed during observation; refresh before relying on this inventory.",
                "Mods directory"));
        }

        var profiles = request.Profiles.Profiles
            .Select(profile => ReconcileProfile(
                profile,
                directories,
                containerChanged,
                categories?.Source.RawFingerprint,
                categories?.Warnings ?? []))
            .ToImmutableArray();
        var sourceChanged = directories.Any(directory =>
                directory.Metadata.Availability == Mo2MetadataAvailability.ChangedDuringRead) ||
            categories?.Source.Availability == ProfileSourceAvailability.ChangedDuringRead;
        var status = containerChanged || sourceChanged
            ? Mo2InventoryObservationStatus.Inconsistent
            : directories.Any(directory => directory.Metadata.Availability is
                Mo2MetadataAvailability.Inaccessible or Mo2MetadataAvailability.Oversized or
                Mo2MetadataAvailability.Malformed or Mo2MetadataAvailability.ChangedDuringRead) ||
              profiles.Any(profile => profile.Status == Mo2InventoryObservationStatus.Partial) ||
              categories?.Warnings.Length > 0 ||
              issues.Any(issue => issue.Severity == Mo2IssueSeverity.Warning)
                ? Mo2InventoryObservationStatus.Partial
                : Mo2InventoryObservationStatus.Complete;
        var revision = Fingerprint(
            request.Reference.Id.Value,
            canonicalRoot,
            status.ToString(),
            categories?.Source.RawFingerprint ?? string.Empty,
            string.Join('\n', directories.Select(DirectoryEvidence)),
            string.Join('\n', profiles.Select(profile => profile.Fingerprint)));
        return new(
            request.Reference.Id,
            revision,
            status,
            canonicalRoot,
            observedAt,
            directories,
            profiles,
            categories,
            issues.ToImmutable());
    }

    private ImmutableArray<string> CanonicalizeChildren(
        string root,
        Mo2DirectoryEnumeration enumeration,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var candidate in enumeration.Directories)
        {
            if (!paths.TryCanonicalize(candidate, out var canonical, out _) ||
                !paths.IsImmediateChildOf(canonical, root))
            {
                issues.Add(new(
                    "mo2.mods.path_escape",
                    Mo2IssueSeverity.Warning,
                    "A directory resolved outside the authorized mods root and was ignored.",
                    "Mods directory"));
                continue;
            }

            result.Add(canonical);
        }

        return result
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private async Task<Mo2CategoryCatalog?> ReadCategoriesAsync(
        string instanceRoot,
        DateTimeOffset observedAt,
        ReadBudget budget,
        CancellationToken cancellationToken)
    {
        var candidate = Path.Combine(instanceRoot, "categories.dat");
        if (!paths.TryCanonicalize(candidate, out var path, out _) || !paths.IsWithinRoot(path, instanceRoot))
        {
            return null;
        }

        var source = await ReadTextSourceAsync(
            "categories.dat",
            path,
            MaximumCategoriesBytes,
            observedAt,
            budget,
            cancellationToken).ConfigureAwait(false);
        return source.Availability == ProfileSourceAvailability.OptionalAbsent
            ? null
            : Mo2MetaIniParser.ParseCategories(source);
    }

    private async Task<Mo2ModDirectoryObservation> ObserveDirectoryAsync(
        string directory,
        Mo2CategoryCatalog? categories,
        DateTimeOffset observedAt,
        ReadBudget budget,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var metadata = fileSystem.GetDirectoryMetadata(directory);
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        if (metadata.State != Mo2PathState.Present)
        {
            warnings.Add(new("mo2.mod.directory_inaccessible", "The mod directory became unavailable during observation."));
        }

        var metaCandidate = Path.Combine(directory, "meta.ini");
        Mo2MetaIniSnapshot meta;
        if (!paths.TryCanonicalize(metaCandidate, out var metaPath, out _) || !paths.IsWithinRoot(metaPath, directory))
        {
            var source = EmptySource("meta.ini", ProfileSourceAvailability.Inaccessible, observedAt, metaCandidate);
            meta = new(
                Mo2MetadataAvailability.Inaccessible,
                null,
                source,
                [new("mo2.mod.meta_path_escape", "The metadata path resolved outside its mod directory and was not read.")]);
        }
        else
        {
            var source = await ReadTextSourceAsync(
                "meta.ini",
                metaPath,
                MaximumMetadataBytes,
                observedAt,
                budget,
                cancellationToken).ConfigureAwait(false);
            var availability = source.Availability switch
            {
                ProfileSourceAvailability.Read => Mo2MetadataAvailability.Available,
                ProfileSourceAvailability.OptionalAbsent => Mo2MetadataAvailability.Missing,
                ProfileSourceAvailability.Oversized => Mo2MetadataAvailability.Oversized,
                ProfileSourceAvailability.ChangedDuringRead => Mo2MetadataAvailability.ChangedDuringRead,
                _ => Mo2MetadataAvailability.Inaccessible,
            };
            Mo2NormalizedModMetadata? parsed = null;
            var parseWarnings = ImmutableArray<Mo2ParseWarning>.Empty;
            if (source.RawDocument is not null)
            {
                (parsed, parseWarnings) = Mo2MetaIniParser.ParseMetadata(source.RawDocument, categories);
            }

            if (source.ParseStatus == ProfileSourceParseStatus.Malformed)
            {
                availability = Mo2MetadataAvailability.Malformed;
            }

            meta = new(availability, parsed, source, source.Warnings.AddRange(parseWarnings));
        }

        warnings.AddRange(meta.Warnings);
        return new(
            name,
            directory,
            paths.GetIdentityKey(directory),
            metadata.State,
            metadata.CreatedAtUtc,
            meta,
            warnings.ToImmutable());
    }

    private Mo2ProfileModInventory ReconcileProfile(
        Mo2ObservedProfile profile,
        ImmutableArray<Mo2ModDirectoryObservation> directories,
        bool containerChanged,
        string? categoriesFingerprint,
        ImmutableArray<Mo2ParseWarning> categoryWarnings)
    {
        var source = profile.Sources.FirstOrDefault(item => item.Name == "modlist.txt");
        if (source?.RawDocument is null)
        {
            return new(
                profile.Id,
                [],
                Mo2InventoryObservationStatus.Unavailable,
                Fingerprint(profile.Id.Value, "modlist-unavailable"),
                [new("mo2.modlist.unavailable", "The profile mod list is unavailable, so no inventory was reconciled.")]);
        }

        var parsed = Mo2ProfileParsers.ParseModList(source.RawDocument);
        var directoryGroups = directories
            .GroupBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var usedIdentities = new HashSet<string>(StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<Mo2ReconciledMod>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        warnings.AddRange(parsed.Warnings);
        warnings.AddRange(categoryWarnings);
        var priority = 0;
        foreach (var item in parsed.Entries.Select((entry, sourceOrder) => (entry, sourceOrder)).Reverse())
        {
            var parsedEntry = item.entry;
            var rowWarnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
            var separator = IsSeparator(parsedEntry.Name);
            var backup = IsBackup(parsedEntry.Name);
            var duplicate = parsedEntry.Occurrence > 0;
            Mo2ModDirectoryObservation? directory = null;
            Mo2ModReconciliationState reconciliation;
            int? effectivePriority = null;
            if (duplicate)
            {
                reconciliation = Mo2ModReconciliationState.Duplicate;
                rowWarnings.Add(new("mo2.mod.duplicate_entry", "This mod-list name is duplicated; no effective MO2 priority was assigned to the later occurrence.", parsedEntry.SourceLineIndex));
            }
            else if (parsedEntry.Marker == Mo2ModListMarker.Foreign)
            {
                reconciliation = Mo2ModReconciliationState.Foreign;
                effectivePriority = priority++;
            }
            else if (directoryGroups.TryGetValue(parsedEntry.Name, out var matches) && matches.Length > 1)
            {
                reconciliation = Mo2ModReconciliationState.Ambiguous;
                foreach (var match in matches)
                {
                    usedIdentities.Add(match.IdentityKey);
                }
                rowWarnings.Add(new("mo2.mod.ambiguous_directory", "Multiple directory identities match this mod-list name; Grid did not choose one.", parsedEntry.SourceLineIndex));
            }
            else if (directoryGroups.TryGetValue(parsedEntry.Name, out matches) && matches.Length == 1)
            {
                directory = matches[0];
                usedIdentities.Add(directory.IdentityKey);
                reconciliation = directory.State != Mo2PathState.Present
                    ? Mo2ModReconciliationState.Inaccessible
                    : containerChanged || directory.Metadata.Availability == Mo2MetadataAvailability.ChangedDuringRead
                        ? Mo2ModReconciliationState.Inconsistent
                        : separator
                        ? Mo2ModReconciliationState.Separator
                        : backup
                            ? Mo2ModReconciliationState.Backup
                            : Mo2ModReconciliationState.Matched;
                effectivePriority = priority++;
                rowWarnings.AddRange(directory.Warnings);
            }
            else
            {
                reconciliation = Mo2ModReconciliationState.Missing;
                rowWarnings.Add(new(
                    "mo2.mod.missing_directory",
                    separator
                        ? "The separator row has no matching directory under the configured mods root."
                        : backup
                            ? "The backup row has no matching directory under the configured mods root."
                            : "The authoritative mod-list row has no matching directory under the configured mods root.",
                    parsedEntry.SourceLineIndex));
            }

            var id = StableModId(profile.Id, parsedEntry.Name, parsedEntry.Occurrence, "profile");
            entries.Add(new(
                profile.Id,
                id,
                parsedEntry.Name,
                parsedEntry.Marker,
                parsedEntry.IsEnabled && !separator,
                parsedEntry.SourceLineIndex,
                item.sourceOrder,
                effectivePriority,
                reconciliation,
                directory,
                rowWarnings.ToImmutable()));
            warnings.AddRange(rowWarnings);
        }

        var unlistedDirectories = directories
            .Where(directory => !usedIdentities.Contains(directory.IdentityKey))
            .ToArray();
        foreach (var directory in unlistedDirectories)
        {
            var backup = IsBackup(directory.Name);
            var rowWarning = new Mo2ParseWarning(
                backup ? "mo2.mod.backup_directory" : "mo2.mod.unlisted_directory",
                backup
                    ? "MO2 exposes this backup directory outside the active profile order; it has no effective priority."
                    : "This immediate child is not represented by the profile mod list and has no authoritative MO2 priority.");
            var directoryWarnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
            directoryWarnings.Add(rowWarning);
            directoryWarnings.AddRange(directory.Warnings);
            entries.Add(new(
                profile.Id,
                StableModId(profile.Id, directory.IdentityKey, 0, "unlisted"),
                directory.Name,
                Mo2ModListMarker.Unmarked,
                false,
                null,
                null,
                null,
                backup ? Mo2ModReconciliationState.Backup : Mo2ModReconciliationState.Unlisted,
                directory,
                directoryWarnings.ToImmutable()));
            warnings.AddRange(directoryWarnings);
        }

        var result = entries.ToImmutable();
        var status = containerChanged
            ? Mo2InventoryObservationStatus.Inconsistent
            : warnings.Count > 0 || result.Any(entry => entry.Reconciliation is
                Mo2ModReconciliationState.Missing or Mo2ModReconciliationState.Ambiguous or
                Mo2ModReconciliationState.Duplicate or Mo2ModReconciliationState.Inaccessible)
                ? Mo2InventoryObservationStatus.Partial
                : Mo2InventoryObservationStatus.Complete;
        var fingerprint = Fingerprint(
            profile.Id.Value,
            source.RawFingerprint ?? string.Empty,
            categoriesFingerprint ?? string.Empty,
            status.ToString(),
            string.Join('\n', result.Select(EntryEvidence)));
        return new(profile.Id, result, status, fingerprint, warnings.ToImmutable());
    }

    private async Task<Mo2ProfileSourceSnapshot> ReadTextSourceAsync(
        string name,
        string path,
        int maximumBytes,
        DateTimeOffset observedAt,
        ReadBudget budget,
        CancellationToken cancellationToken)
    {
        var metadata = fileSystem.GetFileMetadata(path);
        if (metadata.State != Mo2PathState.Present || metadata.Stamp is not Mo2FileStamp stamp)
        {
            return EmptySource(
                name,
                metadata.State == Mo2PathState.Missing
                    ? ProfileSourceAvailability.OptionalAbsent
                    : ProfileSourceAvailability.Inaccessible,
                observedAt,
                path,
                metadata.Stamp);
        }

        if (stamp.Length > maximumBytes || !budget.TryReserve(stamp.Length))
        {
            return new(
                name,
                ProfileSourceAvailability.Oversized,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.mod.meta_oversized", "The source exceeds the bounded inventory read budget.")],
                stamp,
                null,
                path,
                observedAt,
                ParserVersion);
        }

        try
        {
            var read = await fileSystem.ReadBytesWithMetadataAsync(path, maximumBytes, cancellationToken).ConfigureAwait(false);
            var after = fileSystem.GetFileMetadata(path);
            var changed = !Equals(stamp, read.Before) || !Equals(read.Before, read.After) ||
                after.State != Mo2PathState.Present || !Equals(read.After, after.Stamp);
            Mo2RawTextDocument? document = null;
            var parseStatus = ProfileSourceParseStatus.Parsed;
            var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
            try
            {
                document = decoder.Decode(read.Bytes, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
                if (document.UsedSystemEncodingFallback)
                {
                    warnings.Add(new("mo2.mod.meta_encoding_fallback", "The source used the Windows system code-page fallback."));
                }
            }
            catch (InvalidDataException)
            {
                parseStatus = ProfileSourceParseStatus.Malformed;
                warnings.Add(new("mo2.mod.meta_malformed", "The source text encoding is malformed."));
            }

            if (changed)
            {
                warnings.Add(new("mo2.mod.meta_changed", "The source changed during observation."));
            }

            return new(
                name,
                changed ? ProfileSourceAvailability.ChangedDuringRead : ProfileSourceAvailability.Read,
                parseStatus,
                document,
                warnings.ToImmutable(),
                read.Before,
                after.Stamp ?? read.After,
                path,
                observedAt,
                ParserVersion,
                Convert.ToHexString(SHA256.HashData(read.Bytes.AsSpan())).ToLowerInvariant(),
                read.Bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return new(
                name,
                ProfileSourceAvailability.Oversized,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.mod.meta_oversized", "The source exceeded the bounded inventory read limit.")],
                stamp,
                null,
                path,
                observedAt,
                ParserVersion);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                name,
                ProfileSourceAvailability.Inaccessible,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.mod.meta_inaccessible", "The source could not be read.")],
                stamp,
                null,
                path,
                observedAt,
                ParserVersion);
        }
    }

    private static Mo2ProfileSourceSnapshot EmptySource(
        string name,
        ProfileSourceAvailability availability,
        DateTimeOffset observedAt,
        string path,
        Mo2FileStamp? stamp = null) =>
        new(name, availability, ProfileSourceParseStatus.NotParsed, null, [], stamp, null, path, observedAt, ParserVersion);

    private static bool IsSeparator(string name) => name.EndsWith("_separator", StringComparison.Ordinal);

    private static bool IsBackup(string name) =>
        Regex.IsMatch(name, "^.*backup[0-9]*$", RegexOptions.CultureInvariant);

    private static ModId StableModId(ProfileId profileId, string value, int occurrence, string kind) =>
        new($"mod.mo2.{Fingerprint(profileId.Value, value.ToUpperInvariant(), occurrence.ToString(), kind)[..24]}");

    private static string DirectoryEvidence(Mo2ModDirectoryObservation directory) =>
        $"{directory.IdentityKey}:{directory.Metadata.Availability}:{directory.Metadata.Source.RawFingerprint}";

    private static string EntryEvidence(Mo2ReconciledMod entry) =>
        $"{entry.Id.Value}:{entry.Marker}:{entry.SourceOrder}:{entry.Mo2Priority}:{entry.Reconciliation}:" +
        $"{entry.Directory?.IdentityKey}:{entry.Directory?.Metadata.Source.RawFingerprint}";

    private static string Fingerprint(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', values))))
            .ToLowerInvariant();

    private static Mo2ModInventorySnapshot Empty(
        InstallationReferenceId referenceId,
        DateTimeOffset observedAt,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        string? root = null)
    {
        var revision = Fingerprint(referenceId.Value, string.Join('\n', issues.Select(issue => issue.Code)));
        return new(
            referenceId,
            revision,
            Mo2InventoryObservationStatus.Unavailable,
            root,
            observedAt,
            [],
            [],
            null,
            issues.ToImmutable());
    }

    private sealed class ReadBudget(long maximum)
    {
        private long used;

        public bool TryReserve(long bytes)
        {
            while (true)
            {
                var current = Volatile.Read(ref used);
                if (bytes < 0 || current > maximum - bytes)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref used, current + bytes, current) == current)
                {
                    return true;
                }
            }
        }
    }
}
