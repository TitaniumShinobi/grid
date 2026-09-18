using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2ProfileSnapshotService : IMo2ProfileSnapshotService
{
    public const int MaximumFileBytes = 32 * 1024 * 1024;
    public const int MaximumRefreshBytes = 256 * 1024 * 1024;
    public const int MaximumConcurrentReads = 4;
    private static readonly SourceSpec[] Sources =
    [
        new("modlist.txt", true, Mo2TextDecodingPolicy.StrictUtf8),
        new("plugins.txt", false, Mo2TextDecodingPolicy.WindowsSystemCodePage),
        new("loadorder.txt", false, Mo2TextDecodingPolicy.StrictUtf8),
        new("settings.ini", false, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback),
        new("skyrim.ini", false, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback),
        new("skyrimprefs.ini", false, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback),
        new("skyrimcustom.ini", false, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback),
    ];
    private static readonly SourceSpec InstanceConfiguration =
        new("ModOrganizer.ini", true, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);

    private readonly IMo2ReadOnlyFileSystem fileSystem;
    private readonly IMo2TextDecoder decoder;
    private readonly IMo2PathCanonicalizer paths;
    private readonly IMo2SessionPathAuthorization authorization;
    private readonly Func<DateTimeOffset> utcNow;

    public Mo2ProfileSnapshotService(
        IMo2ReadOnlyFileSystem fileSystem,
        IMo2TextDecoder decoder,
        IMo2PathCanonicalizer paths,
        IMo2SessionPathAuthorization authorization,
        Func<DateTimeOffset>? utcNow = null)
    {
        this.fileSystem = fileSystem;
        this.decoder = decoder;
        this.paths = paths;
        this.authorization = authorization;
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<Mo2ProfileSnapshot> ObserveAsync(
        Mo2ProfileSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var reference = request.Reference;
        var validation = request.Validation;
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        if (validation.InstanceDirectory is null ||
            validation.IniPath is null ||
            validation.ProfilesDirectory is null ||
            !paths.Equals(reference.InstanceDirectory, validation.InstanceDirectory) ||
            !paths.Equals(Path.Combine(reference.InstanceDirectory, "ModOrganizer.ini"), validation.IniPath))
        {
            issues.Add(new(
                "mo2.profiles.validation_required",
                Mo2IssueSeverity.Error,
                "A current validation bound to this connected MO2 reference is required."));
            return Empty(reference.Id, ProfileObservationStatus.Unavailable, issues);
        }

        var profilesRoot = validation.ProfilesDirectory;
        var executableRoot = Path.GetDirectoryName(reference.ExecutablePath);
        var automaticallyAuthorized = paths.IsWithinRoot(profilesRoot, reference.InstanceDirectory) ||
            executableRoot is not null && paths.IsWithinRoot(profilesRoot, executableRoot);
        if (!automaticallyAuthorized && !authorization.IsProfilesRootAuthorized(reference.Id, profilesRoot))
        {
            issues.Add(new(
                "mo2.profiles.authorization_required",
                Mo2IssueSeverity.Error,
                "Explicit session authorization is required before Grid reads the exact configured profiles root.",
                "Profiles directory"));
            return Empty(
                reference.Id,
                ProfileObservationStatus.Unavailable,
                issues,
                profilesRootSource: new(
                    "profiles-root",
                    ProfileSourceAvailability.AuthorizationRequired,
                    ProfileSourceParseStatus.NotParsed,
                    null,
                    [],
                    null,
                    null,
                    profilesRoot,
                    utcNow().ToUniversalTime()));
        }

        var initialEnumeration = fileSystem.EnumerateDirectoriesWithState(profilesRoot);
        if (initialEnumeration.State != Mo2PathState.Present)
        {
            issues.Add(new(
                "mo2.profiles.root_unavailable",
                Mo2IssueSeverity.Error,
                "The authorized profiles root is missing or inaccessible.",
                "Profiles directory"));
            return Empty(
                reference.Id,
                ProfileObservationStatus.Unavailable,
                issues,
                profilesRootSource: new(
                    "profiles-root",
                    ProfileSourceAvailability.Inaccessible,
                    ProfileSourceParseStatus.NotParsed,
                    null,
                    [],
                    null,
                    null,
                    profilesRoot,
                    utcNow().ToUniversalTime()));
        }

        var observedAt = utcNow().ToUniversalTime();
        var profilesRootSource = new Mo2ProfileSourceSnapshot(
            "profiles-root",
            ProfileSourceAvailability.Read,
            ProfileSourceParseStatus.NotParsed,
            null,
            [],
            null,
            null,
            profilesRoot,
            observedAt);
        var budget = new RefreshBudget(MaximumRefreshBytes);
        var instanceSource = await ReadSourceAsync(
            reference.InstanceDirectory,
            InstanceConfiguration,
            observedAt,
            budget,
            cancellationToken).ConfigureAwait(false);
        if (instanceSource.RawDocument is not null &&
            instanceSource.ParseStatus == ProfileSourceParseStatus.NotParsed)
        {
            instanceSource = instanceSource with { ParseStatus = ProfileSourceParseStatus.Parsed };
        }
        var configBefore = instanceSource.Before;
        var initialDirectories = EnumerateProfiles(profilesRoot, initialEnumeration, issues);
        var selectedProfile = instanceSource.RawDocument is null
            ? null
            : Mo2ProfileParsers.ParseSelectedProfile(instanceSource.RawDocument);
        if (!string.IsNullOrWhiteSpace(request.SelectedProfileName))
        {
            if (!string.Equals(selectedProfile, request.SelectedProfileName, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new(
                    "mo2.profiles.selected_profile_mismatch",
                    Mo2IssueSeverity.Error,
                    "The requested selected profile does not match General/selected_profile.",
                    "ModOrganizer.ini"));
                return Empty(reference.Id, ProfileObservationStatus.Unavailable, issues,
                    instanceConfigurationSource: instanceSource, profilesRootSource: profilesRootSource);
            }

            initialDirectories = initialDirectories
                .Where(directory => string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),
                    request.SelectedProfileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
            if (initialDirectories.Length != 1)
            {
                issues.Add(new(
                    "mo2.profiles.selected_profile_unavailable",
                    Mo2IssueSeverity.Error,
                    "The selected profile was not found as one unique immediate child of the profiles root.",
                    "Profiles directory"));
                return Empty(reference.Id, ProfileObservationStatus.Unavailable, issues,
                    instanceConfigurationSource: instanceSource, profilesRootSource: profilesRootSource);
            }
        }
        using var readGate = new SemaphoreSlim(MaximumConcurrentReads, MaximumConcurrentReads);
        var tasks = initialDirectories.Select(async directory =>
        {
            await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ObserveProfileAsync(reference, directory, observedAt, budget, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                readGate.Release();
            }
        }).ToArray();
        var profiles = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToImmutableArray();

        var finalEnumeration = fileSystem.EnumerateDirectoriesWithState(profilesRoot);
        var finalDirectories = EnumerateProfiles(profilesRoot, finalEnumeration, issues);
        if (!string.IsNullOrWhiteSpace(request.SelectedProfileName))
        {
            finalDirectories = finalDirectories
                .Where(directory => string.Equals(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)),
                    request.SelectedProfileName,
                    StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
        }
        var configAfter = fileSystem.GetFileMetadata(validation.IniPath);
        var containerChanged = finalEnumeration.State != Mo2PathState.Present ||
            !Equals(configBefore, configAfter.Stamp) ||
            !initialDirectories.SequenceEqual(finalDirectories, StringComparer.OrdinalIgnoreCase);
        if (containerChanged)
        {
            profilesRootSource = profilesRootSource with
            {
                Availability = ProfileSourceAvailability.ChangedDuringRead,
                Warnings = [new(
                    "mo2.profiles.changed_during_read",
                    "The profiles directory or instance configuration changed during the refresh.")],
            };
        }
        var managerStates = ResolveManagerStates(profiles, selectedProfile);
        profiles = profiles.Select((profile, index) =>
        {
            var sources = profile.Sources;
            var status = profile.Observation.Status;
            if (containerChanged)
            {
                status = ProfileObservationStatus.Inconsistent;
                sources = sources.Add(new(
                    "profile-container",
                    ProfileSourceAvailability.ChangedDuringRead,
                    ProfileSourceParseStatus.NotParsed,
                    null,
                    [new("mo2.profiles.changed_during_read", "The instance configuration or profiles directory changed during the refresh.")],
                    configBefore,
                    configAfter.Stamp));
            }

            var sourceObservations = sources.Select(ToCoreSource).ToImmutableArray();
            var warningCount = sources.Sum(source => source.Warnings.Length);
            var fingerprint = Fingerprint(profile.Name, sources, profile.Mods, profile.Plugins, status);
            var observation = profile.Observation with
            {
                Status = status,
                ManagerState = managerStates[index],
                SnapshotFingerprint = fingerprint,
                WarningCount = warningCount,
                Sources = sourceObservations,
            };
            return profile with
            {
                ManagerState = managerStates[index],
                Observation = observation,
                Sources = sources,
            };
        }).ToImmutableArray();

        var overall = AggregateStatus(profiles.Select(profile => profile.Observation.Status));
        var revision = Fingerprint(
            reference.Id.Value,
            profiles.Select(profile => profile.Observation.SnapshotFingerprint));
        return new(reference.Id, revision, overall, profiles, issues.ToImmutable(), instanceSource, profilesRootSource);
    }

    private async Task<Mo2ObservedProfile> ObserveProfileAsync(
        Mo2InstallationReference reference,
        string profileDirectory,
        DateTimeOffset observedAt,
        RefreshBudget budget,
        CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(profileDirectory));
        var profileId = Mo2ProfileParsers.StableProfileId(reference.Id, profileDirectory);
        var sources = ImmutableArray.CreateBuilder<Mo2ProfileSourceSnapshot>(Sources.Length);
        foreach (var source in Sources)
        {
            sources.Add(await ReadSourceAsync(profileDirectory, source, observedAt, budget, cancellationToken).ConfigureAwait(false));
        }

        var sourceArray = sources.ToImmutable();
        var modSource = sourceArray.Single(source => source.Name == "modlist.txt");
        var pluginSource = sourceArray.Single(source => source.Name == "plugins.txt");
        var loadOrderSource = sourceArray.Single(source => source.Name == "loadorder.txt");
        var settingsSource = sourceArray.Single(source => source.Name == "settings.ini");

        var modParse = modSource.RawDocument is null
            ? new([], [], ProfileSourceParseStatus.NotParsed)
            : Mo2ProfileParsers.ParseModList(modSource.RawDocument);
        var pluginParse = pluginSource.RawDocument is null
            ? new([], [], ProfileSourceParseStatus.NotParsed)
            : Mo2ProfileParsers.ParsePluginStates(pluginSource.RawDocument);
        var loadOrderParse = loadOrderSource.RawDocument is null
            ? new([], [], ProfileSourceParseStatus.NotParsed)
            : Mo2ProfileParsers.ParseLoadOrder(loadOrderSource.RawDocument);
        var settingsParse = settingsSource.RawDocument is null
            ? new(null, null, [], ProfileSourceParseStatus.NotParsed)
            : Mo2ProfileParsers.ParseProfileSettings(settingsSource.RawDocument);

        sourceArray = sourceArray.Select(source => source.Name switch
        {
            "modlist.txt" => source with { ParseStatus = PreserveReadFailure(source, modParse.Status), Warnings = source.Warnings.AddRange(modParse.Warnings) },
            "plugins.txt" => source with { ParseStatus = PreserveReadFailure(source, pluginParse.Status), Warnings = source.Warnings.AddRange(pluginParse.Warnings) },
            "loadorder.txt" => source with { ParseStatus = PreserveReadFailure(source, loadOrderParse.Status), Warnings = source.Warnings.AddRange(loadOrderParse.Warnings) },
            "settings.ini" when source.RawDocument is not null => source with { ParseStatus = PreserveReadFailure(source, settingsParse.Status), Warnings = source.Warnings.AddRange(settingsParse.Warnings) },
            _ when source.RawDocument is not null => source with { ParseStatus = ProfileSourceParseStatus.Parsed },
            _ => source,
        }).ToImmutableArray();

        var mods = Mo2ProfileParsers.ProjectMods(profileId, modParse);
        var (plugins, joinWarnings) = Mo2ProfileParsers.ProjectPlugins(profileId, pluginParse, loadOrderParse);
        if (!joinWarnings.IsEmpty)
        {
            var index = sourceArray
                .Select((source, sourceIndex) => (source, sourceIndex))
                .Single(item => item.source.Name == "loadorder.txt")
                .sourceIndex;
            sourceArray = sourceArray.SetItem(index, sourceArray[index] with
            {
                Warnings = sourceArray[index].Warnings.AddRange(joinWarnings),
            });
        }

        if (settingsParse.LocalSettingsEnabled == true)
        {
            sourceArray = sourceArray.Select(source =>
                source.Name is "skyrim.ini" or "skyrimprefs.ini" &&
                source.Availability == ProfileSourceAvailability.OptionalAbsent
                    ? source with
                    {
                        Warnings = source.Warnings.Add(new(
                            "mo2.profile.local_settings_source_missing",
                            "Local settings are enabled but this expected Skyrim INI is absent.")),
                    }
                    : source).ToImmutableArray();
        }

        if (settingsParse.LocalSavesEnabled == true)
        {
            sourceArray = sourceArray.Add(ObserveSavesDirectory(profileDirectory, observedAt));
        }

        var status = DetermineProfileStatus(sourceArray);
        var warningCount = sourceArray.Sum(source => source.Warnings.Length);
        var observation = new ProfileObservationSummary(
            status,
            ManagerProfileState.Unknown,
            observedAt,
            Fingerprint(name, sourceArray, mods, plugins, status),
            warningCount,
            settingsParse.LocalSavesEnabled,
            settingsParse.LocalSettingsEnabled,
            sourceArray.Select(ToCoreSource).ToImmutableArray());
        return new(
            profileId,
            name,
            ManagerProfileState.Unknown,
            mods,
            plugins,
            observation,
            sourceArray,
            PluginStates: pluginParse,
            LoadOrder: loadOrderParse,
            Settings: settingsParse);
    }

    private async Task<Mo2ProfileSourceSnapshot> ReadSourceAsync(
        string profileDirectory,
        SourceSpec source,
        DateTimeOffset observedAt,
        RefreshBudget budget,
        CancellationToken cancellationToken)
    {
        var candidatePath = Path.Combine(profileDirectory, source.Name);
        if (!paths.TryCanonicalize(candidatePath, out var path, out _) ||
            !paths.IsWithinRoot(path, profileDirectory))
        {
            return new(
                source.Name,
                ProfileSourceAvailability.Inaccessible,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.profile.source_path_escape", "The source resolved outside its authorized profile directory and was not read.")],
                null,
                null,
                path,
                observedAt);
        }

        var before = fileSystem.GetFileMetadata(path);
        if (before.State != Mo2PathState.Present || before.Stamp is not Mo2FileStamp stamp)
        {
            var availability = before.State switch
            {
                Mo2PathState.Missing when source.Required => ProfileSourceAvailability.RequiredMissing,
                Mo2PathState.Missing => ProfileSourceAvailability.OptionalAbsent,
                Mo2PathState.Inaccessible or Mo2PathState.Unavailable => ProfileSourceAvailability.Inaccessible,
                _ => source.Required ? ProfileSourceAvailability.RequiredMissing : ProfileSourceAvailability.OptionalAbsent,
            };
            return new(source.Name, availability, ProfileSourceParseStatus.NotParsed, null, [], before.Stamp, null, path, observedAt);
        }

        if (stamp.Length > MaximumFileBytes || !budget.TryReserve(stamp.Length))
        {
            return new(
                source.Name,
                ProfileSourceAvailability.Oversized,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.profile.source_oversized", "The source exceeds the bounded read budget.")],
                stamp,
                null,
                path,
                observedAt);
        }

        try
        {
            var read = await fileSystem.ReadBytesWithMetadataAsync(path, MaximumFileBytes, cancellationToken).ConfigureAwait(false);
            var postRead = fileSystem.GetFileMetadata(path);
            var changed = !Equals(stamp, read.Before) ||
                !Equals(read.Before, read.After) ||
                postRead.State != Mo2PathState.Present ||
                !Equals(read.After, postRead.Stamp);
            Mo2RawTextDocument? document = null;
            var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
            var parseStatus = ProfileSourceParseStatus.NotParsed;
            try
            {
                document = decoder.Decode(read.Bytes, source.Policy);
                if (document.UsedSystemEncodingFallback)
                {
                    warnings.Add(new(
                        "mo2.profile.system_encoding_fallback",
                        "The INI was not valid UTF-8 and was decoded with the Windows system code page."));
                }
            }
            catch (InvalidDataException)
            {
                parseStatus = ProfileSourceParseStatus.Malformed;
                warnings.Add(new("mo2.profile.encoding_invalid", "The source does not satisfy its required text encoding."));
            }

            if (changed)
            {
                warnings.Add(new("mo2.profile.source_changed", "The source changed during the read; the observation is inconsistent."));
            }

            var rawFingerprint = Convert.ToHexString(SHA256.HashData(read.Bytes.AsSpan())).ToLowerInvariant();
            return new(
                source.Name,
                changed ? ProfileSourceAvailability.ChangedDuringRead : ProfileSourceAvailability.Read,
                parseStatus,
                document,
                warnings.ToImmutable(),
                read.Before,
                postRead.Stamp ?? read.After,
                path,
                observedAt,
                RawFingerprint: rawFingerprint,
                RawBytes: read.Bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return new(
                source.Name,
                ProfileSourceAvailability.Oversized,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.profile.source_oversized", "The source exceeded the bounded read limit.")],
                stamp,
                null,
                path,
                observedAt);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(
                source.Name,
                ProfileSourceAvailability.Inaccessible,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.profile.source_inaccessible", "The source could not be read.")],
                stamp,
                null,
                path,
                observedAt);
        }
    }

    private ImmutableArray<string> EnumerateProfiles(
        string profilesRoot,
        Mo2DirectoryEnumeration enumeration,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        if (enumeration.State != Mo2PathState.Present)
        {
            issues.Add(new(
                "mo2.profiles.enumeration_unavailable",
                Mo2IssueSeverity.Error,
                "The profiles root could not be enumerated."));
            return [];
        }

        foreach (var candidate in enumeration.Directories)
        {
            if (!paths.TryCanonicalize(candidate, out var canonical, out _) ||
                !paths.IsImmediateChildOf(canonical, profilesRoot))
            {
                issues.Add(new(
                    "mo2.profile.path_escape",
                    Mo2IssueSeverity.Warning,
                    "A profile directory resolved outside the authorized profiles root and was ignored."));
                continue;
            }

            result.Add(canonical);
        }

        return result.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }

    private static ImmutableArray<ManagerProfileState> ResolveManagerStates(
        ImmutableArray<Mo2ObservedProfile> profiles,
        string? selectedProfile)
    {
        if (string.IsNullOrWhiteSpace(selectedProfile))
        {
            return Enumerable.Repeat(ManagerProfileState.Unknown, profiles.Length).ToImmutableArray();
        }

        var matching = profiles
            .Select((profile, index) => (profile, index))
            .Where(item => item.profile.Name.Equals(selectedProfile, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matching.Length != 1)
        {
            return Enumerable.Repeat(ManagerProfileState.Unknown, profiles.Length).ToImmutableArray();
        }

        return Enumerable.Range(0, profiles.Length)
            .Select(index => index == matching[0].index ? ManagerProfileState.Active : ManagerProfileState.Inactive)
            .ToImmutableArray();
    }

    private static ProfileObservationStatus DetermineProfileStatus(ImmutableArray<Mo2ProfileSourceSnapshot> sources)
    {
        if (sources.Any(source => source.Availability == ProfileSourceAvailability.ChangedDuringRead))
        {
            return ProfileObservationStatus.Inconsistent;
        }

        var required = sources.Where(source => Sources.Any(spec =>
            spec.Required && spec.Name == source.Name)).ToArray();
        if (!sources.Any(source => source.Availability == ProfileSourceAvailability.Read))
        {
            return ProfileObservationStatus.Unavailable;
        }

        if (required.Any(source => source.Availability != ProfileSourceAvailability.Read) ||
            sources.Any(source => source.ParseStatus is ProfileSourceParseStatus.Malformed or ProfileSourceParseStatus.UnsupportedSyntax) ||
            sources.Any(source => source.Warnings.Any(warning => warning.Code == "mo2.profile.local_settings_source_missing")))
        {
            return ProfileObservationStatus.Partial;
        }

        return ProfileObservationStatus.Complete;
    }

    private static ProfileObservationStatus AggregateStatus(IEnumerable<ProfileObservationStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Length == 0 || values.All(status => status == ProfileObservationStatus.Complete))
        {
            return ProfileObservationStatus.Complete;
        }

        if (values.Any(status => status == ProfileObservationStatus.Inconsistent))
        {
            return ProfileObservationStatus.Inconsistent;
        }

        if (values.All(status => status == ProfileObservationStatus.Unavailable))
        {
            return ProfileObservationStatus.Unavailable;
        }

        return ProfileObservationStatus.Partial;
    }

    private static ProfileSourceObservation ToCoreSource(Mo2ProfileSourceSnapshot source) =>
        new(source.Name, source.Availability, source.ParseStatus, source.Warnings.Length);

    private static ProfileSourceParseStatus PreserveReadFailure(
        Mo2ProfileSourceSnapshot source,
        ProfileSourceParseStatus parsedStatus) =>
        source.ParseStatus == ProfileSourceParseStatus.Malformed
            ? ProfileSourceParseStatus.Malformed
            : parsedStatus;

    private static string Fingerprint(
        string profileName,
        ImmutableArray<Mo2ProfileSourceSnapshot> sources,
        ImmutableArray<ModEntry> mods,
        ImmutableArray<PluginEntry> plugins,
        ProfileObservationStatus status)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, profileName);
        Append(hash, status.ToString());
        foreach (var source in sources)
        {
            Append(hash, source.Name);
            Append(hash, source.Availability.ToString());
            Append(hash, source.ParseStatus.ToString());
            if (!source.RawBytes.IsDefault)
            {
                hash.AppendData(source.RawBytes.AsSpan());
            }
        }

        foreach (var mod in mods)
        {
            Append(hash, $"{mod.Id.Value}:{mod.Priority}:{mod.IsEnabled}");
        }

        foreach (var plugin in plugins)
        {
            Append(hash, $"{plugin.Id.Value}:{plugin.LoadOrder}:{plugin.IsEnabled}");
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Fingerprint(string referenceId, IEnumerable<string> profileFingerprints)
    {
        var value = string.Join('\n', profileFingerprints.Prepend(referenceId));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value) =>
        hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));

    private static Mo2ProfileSnapshot Empty(
        InstallationReferenceId referenceId,
        ProfileObservationStatus status,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        Mo2ProfileSourceSnapshot? instanceConfigurationSource = null,
        Mo2ProfileSourceSnapshot? profilesRootSource = null)
    {
        var issueEvidence = string.Join('\n', issues.Select(issue => $"{issue.Code}:{issue.Severity}"));
        var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issueEvidence))).ToLowerInvariant();
        return new(referenceId, revision, status, [], issues.ToImmutable(), instanceConfigurationSource, profilesRootSource);
    }

    private Mo2ProfileSourceSnapshot ObserveSavesDirectory(string profileDirectory, DateTimeOffset observedAt)
    {
        var candidate = Path.Combine(profileDirectory, "saves");
        if (!paths.TryCanonicalize(candidate, out var canonical, out _) ||
            !paths.IsWithinRoot(canonical, profileDirectory))
        {
            return new(
                "saves-directory",
                ProfileSourceAvailability.Inaccessible,
                ProfileSourceParseStatus.NotParsed,
                null,
                [new("mo2.profile.saves_path_escape", "The saves directory resolved outside the profile and was not inspected.")],
                null,
                null,
                canonical,
                observedAt);
        }

        var state = fileSystem.ProbeDirectory(canonical);
        return new(
            "saves-directory",
            state switch
            {
                Mo2PathState.Present => ProfileSourceAvailability.Read,
                Mo2PathState.Missing => ProfileSourceAvailability.OptionalAbsent,
                _ => ProfileSourceAvailability.Inaccessible,
            },
            ProfileSourceParseStatus.NotParsed,
            null,
            [],
            null,
            null,
            canonical,
            observedAt);
    }

    private sealed record SourceSpec(string Name, bool Required, Mo2TextDecodingPolicy Policy);

    private sealed class RefreshBudget(long maximumBytes)
    {
        private long reservedBytes;

        public bool TryReserve(long bytes) => Interlocked.Add(ref reservedBytes, bytes) <= maximumBytes;
    }
}
