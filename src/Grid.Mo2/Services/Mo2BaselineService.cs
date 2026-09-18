using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2BaselineService
{
    private const int MaximumSkseDiagnosticLogs = 256;
    private static readonly GameId SkyrimGame = new("game.skyrim-special-edition");
    private static readonly GameAdapterId Mo2Adapter = new("adapter.mod-organizer-2");
    private readonly IMo2InventoryFileSystem fileSystem;
    private readonly IMo2PathCanonicalizer paths;
    private readonly string globalInstancesRoot;
    private readonly string globalSettingsDirectory;

    public Mo2BaselineService(
        IMo2InventoryFileSystem fileSystem,
        IMo2PathCanonicalizer paths,
        string globalInstancesRoot,
        string globalSettingsDirectory)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        this.globalInstancesRoot = globalInstancesRoot ?? throw new ArgumentNullException(nameof(globalInstancesRoot));
        this.globalSettingsDirectory = globalSettingsDirectory ?? throw new ArgumentNullException(nameof(globalSettingsDirectory));
    }

    public static Mo2BaselineService CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new(
            new Mo2FileSystem(),
            new WindowsPathCanonicalizer(),
            Path.Combine(localApplicationData, "ModOrganizer"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games",
                "Skyrim Special Edition"));
    }

    public async Task<Mo2BaselineSummaryRecord> CaptureAsync(
        Mo2BaselineRequest request,
        Func<Mo2BaselineEvent, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emit);
        request.Limits.Validate();
        if (string.IsNullOrWhiteSpace(request.ProfileName) || request.ProfileName.Any(char.IsControl))
        {
            throw new ArgumentException("ProfileName must contain visible text without control characters.", nameof(request));
        }

        var requiredAuthorizations = ImmutableArray.CreateBuilder<string>();
        var issueCount = 0;
        var mods = 0L;
        var plugins = 0L;
        var archives = 0L;
        var virtualPaths = 0L;
        var physicalFiles = 0L;
        var hashCandidatesProcessed = 0L;
        var completeHashes = 0L;
        var sourceArtifacts = 0L;
        var completeSourceArtifactHashes = 0L;
        var hashedBytes = 0L;
        var pluginScriptInspectionPartial = false;
        var spidInspectionPartial = false;
        var sourceArtifactHashCache = new Dictionary<string, Mo2BaselineFileHashRecord>(StringComparer.OrdinalIgnoreCase);
        var accountedSourceArtifactHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasher = new WindowsRandomAccessFileFactory();
        string? installationId = null;
        string? profileId = null;
        string? connectionFingerprint = null;
        string? profileFingerprint = null;
        string? inventoryFingerprint = null;
        string? environmentFingerprint = null;

        try
        {
            var validator = new Mo2InstallationValidator(
                fileSystem,
                new Mo2IniReader(fileSystem),
                paths,
                globalInstancesRoot);
            var validation = await validator.ValidateAsync(
                new(
                    request.ApplicationDirectory,
                    request.InstanceDirectory,
                    SkyrimGame,
                    request.EffectiveAuthorizedPaths),
                cancellationToken).ConfigureAwait(false);

            var authorized = CanonicalAuthorizations(request.EffectiveAuthorizedPaths);
            foreach (var root in validation.Paths)
            {
                if (root.CanonicalPath is null)
                {
                    continue;
                }

                await emit(new(
                    "resolvedRoot",
                    new Mo2BaselineRootRecord(
                        root.Label,
                        root.CanonicalPath,
                        root.State,
                        root.State != Mo2PathState.AuthorizationRequired)), cancellationToken).ConfigureAwait(false);
            }

            foreach (var issue in validation.Issues)
            {
                issueCount++;
                var exactPath = validation.Paths.FirstOrDefault(path => path.Label == issue.PathLabel)?.CanonicalPath;
                await emit(new(
                    "gateIssue",
                    new Mo2BaselineGateRecord(issue.PathLabel ?? "Validation", exactPath, issue.Code, issue.Message)),
                    cancellationToken).ConfigureAwait(false);
            }

            var validationRequirements = validation.Paths
                .Where(root => root.State == Mo2PathState.AuthorizationRequired && root.CanonicalPath is not null)
                .Select(root => root.CanonicalPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            if (!validationRequirements.IsEmpty)
            {
                requiredAuthorizations.AddRange(validationRequirements);
                return await FinishAsync(Mo2BaselineStatus.AuthorizationRequired).ConfigureAwait(false);
            }

            if (!validation.CanConnect || validation.ConnectionKey is null ||
                validation.ExecutablePath is null || validation.InstanceDirectory is null ||
                validation.InstanceKind is not Mo2InstanceKind instanceKind)
            {
                return await FinishAsync(Mo2BaselineStatus.Unavailable).ConfigureAwait(false);
            }

            connectionFingerprint = validation.ConnectionKey;
            var suffix = validation.ConnectionKey[..24];
            var reference = new Mo2InstallationReference(
                Mo2InstallationReference.CurrentSchemaVersion,
                new($"reference.mo2.{suffix}"),
                new($"installation.mo2.{suffix}"),
                SkyrimGame,
                Mo2Adapter,
                Path.GetFileName(Path.TrimEndingDirectorySeparator(validation.InstanceDirectory)),
                instanceKind,
                validation.ExecutablePath,
                validation.InstanceDirectory);
            installationId = reference.InstallationId.Value;

            var profileAuthorization = new Mo2SessionPathAuthorization(paths);
            if (validation.ProfilesDirectory is not null && IsAuthorizedOrWithinConnection(validation.ProfilesDirectory, reference, authorized))
            {
                profileAuthorization.AuthorizeProfilesRoot(reference.Id, validation.ProfilesDirectory);
            }

            var profileService = new Mo2ProfileSnapshotService(
                fileSystem,
                new Mo2TextDecoder(),
                paths,
                profileAuthorization);
            // Baseline capture is selected-profile-only. The profile service may enumerate
            // immediate directory names to establish uniqueness, but it reads content only
            // from this exact selected profile.
            var profileSnapshot = await profileService.ObserveAsync(
                new(reference, validation, request.ProfileName), cancellationToken).ConfigureAwait(false);
            foreach (var issue in profileSnapshot.Issues)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    issue.PathLabel ?? "Profiles", validation.ProfilesDirectory, issue.Code, issue.Message)), cancellationToken).ConfigureAwait(false);
            }

            var matches = profileSnapshot.Profiles
                .Where(profile => profile.Name.Equals(request.ProfileName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                var selectedProfileMismatch = profileSnapshot.Issues.Any(issue =>
                    issue.Code == "mo2.profiles.selected_profile_mismatch");
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Profile", validation.ProfilesDirectory,
                    selectedProfileMismatch ? "mo2.baseline.active_profile_mismatch" : "mo2.baseline.profile_unavailable",
                    selectedProfileMismatch
                        ? "The requested profile is not the uniquely active MO2 selected_profile; Grid will not change it."
                        : matches.Length == 0
                        ? "The requested profile was not found in the observed profiles root."
                        : "The requested profile name is ambiguous.")), cancellationToken).ConfigureAwait(false);
                return await FinishAsync(selectedProfileMismatch
                    ? Mo2BaselineStatus.ContextUnavailable
                    : Mo2BaselineStatus.Unavailable).ConfigureAwait(false);
            }

            var profile = matches[0];
            profileId = profile.Id.Value;
            profileFingerprint = profile.Observation.SnapshotFingerprint;
            await emit(new("profile", new Mo2BaselineProfileRecord(
                profile.Id.Value,
                profile.Name,
                profile.ManagerState.ToString(),
                profile.Observation.Status.ToString(),
                profile.Observation.SnapshotFingerprint)), cancellationToken).ConfigureAwait(false);

            if (profile.ManagerState != ManagerProfileState.Active)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Active profile",
                    validation.IniPath,
                    "mo2.baseline.active_profile_mismatch",
                    "The requested profile is not the uniquely active MO2 selected_profile; Grid will not change it.")), cancellationToken).ConfigureAwait(false);
                return await FinishAsync(Mo2BaselineStatus.ContextUnavailable).ConfigureAwait(false);
            }

            var diagnosticOutputRoot = Path.Combine(globalSettingsDirectory, "SKSE");
            if (Directory.Exists(diagnosticOutputRoot))
            {
                if (!paths.TryCanonicalize(diagnosticOutputRoot, out diagnosticOutputRoot, out _) ||
                    !paths.IsWithinRoot(diagnosticOutputRoot, globalSettingsDirectory))
                {
                    issueCount++;
                    await emit(new("gateIssue", new Mo2BaselineGateRecord(
                        "SKSE diagnostic outputs", null, "mo2.baseline.diagnostic_output_root_invalid",
                        "The exact game-owned SKSE diagnostic-output root could not be safely canonicalized.")), cancellationToken).ConfigureAwait(false);
                    diagnosticOutputRoot = string.Empty;
                }
                else if (!authorized.Any(path => paths.Equals(path, diagnosticOutputRoot)))
                {
                    requiredAuthorizations.Add(diagnosticOutputRoot);
                    await emit(new("gateIssue", new Mo2BaselineGateRecord(
                        "SKSE diagnostic outputs", diagnosticOutputRoot, "mo2.baseline.diagnostic_output_authorization_required",
                        "Exact session read authorization is required for bounded immediate crash-*.log evidence.")), cancellationToken).ConfigureAwait(false);
                    return await FinishAsync(Mo2BaselineStatus.AuthorizationRequired).ConfigureAwait(false);
                }
            }

            foreach (var source in profile.Sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase))
            {
                await emit(new("profileSource", new Mo2BaselineProfileSourceRecord(
                    source.Name,
                    source.Availability.ToString(),
                    source.ParseStatus.ToString(),
                    source.CanonicalSourcePath,
                    source.RawFingerprint,
                    source.Warnings.Length)), cancellationToken).ConfigureAwait(false);
                if (source.Name.Equals("saves-directory", StringComparison.OrdinalIgnoreCase) && source.CanonicalSourcePath is not null)
                {
                    await emit(new("protectedSnapshot", new Mo2BaselineProtectedSnapshotRecord(
                        "Saves",
                        source.CanonicalSourcePath,
                        SourceState(source.Availability),
                        "Directory presence only; save contents are not enumerated or hashed.")), cancellationToken).ConfigureAwait(false);
                }
            }

            if (validation.DownloadsDirectory is not null)
            {
                var downloadState = validation.Paths.FirstOrDefault(path => path.Label == "Downloads directory")?.State
                    ?? Mo2PathState.Unavailable;
                await emit(new("protectedSnapshot", new Mo2BaselineProtectedSnapshotRecord(
                    "Downloads",
                    validation.DownloadsDirectory,
                    downloadState,
                    "Directory validation only; download contents are not enumerated or hashed.")), cancellationToken).ConfigureAwait(false);
            }

            var modsAuthorization = new Mo2SessionModsPathAuthorization(paths);
            if (validation.ModsDirectory is not null && IsAuthorizedOrWithinConnection(validation.ModsDirectory, reference, authorized))
            {
                modsAuthorization.AuthorizeModsRoot(reference.Id, validation.ModsDirectory);
            }

            var inventoryService = new Mo2ModInventoryService(
                fileSystem,
                new Mo2TextDecoder(),
                paths,
                modsAuthorization);
            var inventory = await inventoryService.ObserveAsync(
                new(reference, validation, profileSnapshot),
                cancellationToken).ConfigureAwait(false);
            foreach (var issue in inventory.Issues)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    issue.PathLabel ?? "Mod inventory",
                    validation.ModsDirectory,
                    issue.Code,
                    issue.Message)), cancellationToken).ConfigureAwait(false);
            }
            var selectedInventory = inventory.Profiles.SingleOrDefault(value => value.ProfileId == profile.Id);
            inventoryFingerprint = selectedInventory?.Fingerprint ?? inventory.Revision;
            var relevantProviders = request.EffectiveProviderSeeds
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedInventory is not null)
            {
                foreach (var mod in selectedInventory.Entries.OrderBy(value => value.Mo2Priority ?? int.MaxValue)
                    .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
                {
                    mods++;
                    await emit(new("mod", new Mo2BaselineModRecord(
                        mod.Id.Value,
                        mod.Name,
                        mod.IsEnabled,
                        mod.Mo2Priority,
                        mod.Reconciliation.ToString(),
                        mod.Directory?.CanonicalPath,
                        mod.Directory?.Metadata.Source.RawFingerprint,
                        mod.Directory?.Metadata.Availability.ToString() ?? Mo2MetadataAvailability.NotApplicable.ToString(),
                        mod.Directory?.Metadata.Metadata?.Version,
                        mod.Directory?.Metadata.Metadata?.NewestVersion,
                        mod.Directory?.Metadata.Metadata?.IgnoredVersion,
                        mod.Directory?.Metadata.Metadata?.CategoryIds ?? [],
                        mod.Directory?.Metadata.Metadata?.CategoryNames ?? [],
                        mod.Directory?.Metadata.Metadata?.NexusGameName,
                        mod.Directory?.Metadata.Metadata?.NexusModId,
                        mod.Directory?.Metadata.Metadata?.InstallationFile,
                        mod.Directory?.Metadata.Metadata?.Notes,
                        mod.Directory?.Metadata.Metadata?.Comments,
                        mod.Directory?.Metadata.Metadata?.Repository,
                        mod.Directory?.Metadata.Metadata?.ProviderUpdatedAtUtc,
                        mod.Directory?.Metadata.Metadata?.ProviderStatus,
                        mod.Directory?.Metadata.Metadata?.RawValues ?? [],
                        mod.Warnings.Length)), cancellationToken).ConfigureAwait(false);

                    await CheckpointAsync("mods", mods, selectedInventory.Entries.Length).ConfigureAwait(false);
                }
            }

            var contentAuthorization = new Mo2SessionContentPathAuthorization(paths);
            var requiredContentRoots = RequiredContentRoots(reference, validation, profile);
            foreach (var root in requiredContentRoots)
            {
                if (!authorized.Any(path => paths.Equals(path, root.Path)))
                {
                    requiredAuthorizations.Add(root.Path);
                    await emit(new("gateIssue", new Mo2BaselineGateRecord(
                        root.Label,
                        root.Path,
                        "mo2.baseline.content_authorization_required",
                        "Exact session read authorization is required before this content root is observed.")), cancellationToken).ConfigureAwait(false);
                    issueCount++;
                    continue;
                }

                contentAuthorization.AuthorizeRoot(reference.Id, root.Kind, root.Path);
            }

            if (requiredAuthorizations.Count > 0)
            {
                return await FinishAsync(Mo2BaselineStatus.AuthorizationRequired).ConfigureAwait(false);
            }

            var resolvedLimits = Mo2ResolvedStateLimits.Default with
            {
                Content = Mo2ResolvedStateLimits.Default.Content with { MaximumEntries = request.Limits.MaximumEntries },
                MaximumVirtualPaths = request.Limits.MaximumEntries,
            };
            var resolvedService = new Mo2ResolvedStateService(
                new SingleReferenceStore(reference),
                _ => new StaticValidator(validation),
                new SelectedProfileSnapshotService(profileSnapshot),
                inventoryService,
                new Mo2ContentTreeObserver(paths),
                new WindowsRandomAccessFileFactory(),
                new Mo2PluginHeaderParser(),
                new Mo2BsaIndexParser(),
                contentAuthorization,
                paths,
                fileSystem,
                new Mo2TextDecoder(),
                new Mo2ResolvedStateCache(),
                new Mo2VirtualDataResolver(),
                globalSettingsDirectory,
                resolvedLimits);
            var context = new WorkspaceEnvironmentContext(
                reference.GameId,
                reference.InstallationId,
                profile.Id,
                $"diagnostics.baseline.{suffix}",
                inventoryFingerprint);
            var refresh = await resolvedService.RefreshAsync(context, forceRefresh: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (refresh.Status == ResolvedEnvironmentRefreshStatus.AuthorizationRequired)
            {
                requiredAuthorizations.AddRange(refresh.RequiredAuthorizations);
                return await FinishAsync(Mo2BaselineStatus.AuthorizationRequired).ConfigureAwait(false);
            }

            if (refresh.Snapshot is null)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Resolved environment", null, "mo2.baseline.environment_unavailable", refresh.Detail)), cancellationToken).ConfigureAwait(false);
                return await FinishAsync(Mo2BaselineStatus.Unavailable).ConfigureAwait(false);
            }

            environmentFingerprint = refresh.Snapshot.Summary.Fingerprint;
            var baseline = resolvedService.GetBaselineSnapshot(refresh.Snapshot.Id);

            var spidDocuments = ImmutableArray.CreateBuilder<Mo2SpidDistributionDocument>();
            var spidChains = baseline.ProviderChains
                .Where(value => value.VirtualPath.EndsWith("_DISTR.ini", StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (spidChains.Length > 10_000)
            {
                spidInspectionPartial = true;
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "SPID distributions", null, "mo2.baseline.spid_document_limit",
                    "The resolved profile exceeds the 10,000-document SPID inspection limit.")), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var decoder = new Mo2TextDecoder();
                foreach (var chain in spidChains)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var winner = chain.Providers.SingleOrDefault(value => value.IsWinner);
                    if (chain.WinnerConfidence != ProviderWinnerConfidence.Established || winner is null)
                    {
                        spidInspectionPartial = true;
                        issueCount++;
                        await emit(new("gateIssue", new Mo2BaselineGateRecord(
                            "SPID distributions", null, "mo2.baseline.spid_winner_unresolved",
                            $"The winning provider for '{chain.VirtualPath}' is not established.")), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (winner.Kind == VirtualProviderKind.ArchiveMember)
                    {
                        spidInspectionPartial = true;
                        issueCount++;
                        await emit(new("gateIssue", new Mo2BaselineGateRecord(
                            "SPID distributions", null, "mo2.baseline.spid_archive_visibility_unresolved",
                            $"Winning SPID configuration '{chain.VirtualPath}' is archive-backed; this baseline does not claim the SKSE plugin can load it.")), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var physical = baseline.PhysicalFiles
                        .Where(value => value.VirtualPath.Equals(chain.VirtualPath, StringComparison.OrdinalIgnoreCase) &&
                            value.ProviderName.Equals(winner.SourceName, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(value => value.Precedence)
                        .ThenBy(value => value.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (physical is null)
                    {
                        spidInspectionPartial = true;
                        issueCount++;
                        await emit(new("gateIssue", new Mo2BaselineGateRecord(
                            "SPID distributions", null, "mo2.baseline.spid_physical_path_unavailable",
                            $"The winning loose SPID configuration '{chain.VirtualPath}' has no exact physical-file evidence.")), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var read = await fileSystem.ReadBytesWithMetadataAsync(physical.CanonicalPath, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                        if (read.Before != read.After)
                        {
                            spidInspectionPartial = true;
                            issueCount++;
                            await emit(new("gateIssue", new Mo2BaselineGateRecord(
                                "SPID distributions", physical.CanonicalPath, "mo2.baseline.spid_changed_during_read",
                                $"SPID configuration '{chain.VirtualPath}' changed during its bounded read.")), cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        var text = decoder.Decode(read.Bytes, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
                        var sha = Convert.ToHexString(SHA256.HashData(read.Bytes.AsSpan()));
                        spidDocuments.Add(new(chain.VirtualPath, winner.SourceName, sha, text));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        spidInspectionPartial = true;
                        issueCount++;
                        await emit(new("gateIssue", new Mo2BaselineGateRecord(
                            "SPID distributions", physical.CanonicalPath, "mo2.baseline.spid_read_failed",
                            $"SPID configuration '{chain.VirtualPath}' could not be inspected: {exception.GetType().Name}.")), cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            var spidInspection = new Mo2SpidDistributionInspector().Inspect(
                spidDocuments.ToImmutable(), baseline.Plugins, new());
            spidInspectionPartial |= spidInspection.Status != Mo2SpidDistributionStatus.Complete ||
                spidDocuments.Count != spidChains.Length;
            await emit(new("spidInspection", new Mo2BaselineSpidInspectionRecord(
                spidInspection.Status,
                spidInspection.DocumentsScanned,
                spidInspection.LinesScanned,
                spidInspection.RulesScanned,
                spidInspection.SourcePluginReferences.Length,
                spidInspection.SourcePluginReferences.Count(value => value.Status == Mo2SpidPluginReferenceStatus.Disabled),
                spidInspection.SourcePluginReferences.Count(value => value.Status == Mo2SpidPluginReferenceStatus.Missing),
                spidInspection.Issues.Length,
                spidInspection.SemanticFingerprint)), cancellationToken).ConfigureAwait(false);

            foreach (var group in spidInspection.SourcePluginReferences
                         .Where(value => value.Status != Mo2SpidPluginReferenceStatus.Enabled)
                         .GroupBy(value => new { value.VirtualPath, value.ProviderName, value.PluginName, value.Status })
                         .OrderBy(value => value.Key.VirtualPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(value => value.Key.PluginName, StringComparer.OrdinalIgnoreCase))
            {
                var lines = group.Select(value => value.Line).Distinct().Order().Take(12).ToImmutableArray();
                await emit(new("spidSourceIssue", new Mo2BaselineSpidSourceIssueRecord(
                    group.Key.VirtualPath, group.Key.ProviderName, group.Key.PluginName, group.Key.Status,
                    group.Count(), lines)), cancellationToken).ConfigureAwait(false);
                issueCount++;
                var state = group.Key.Status == Mo2SpidPluginReferenceStatus.Disabled ? "disabled" : "missing";
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "SPID distributions", null, $"mo2.baseline.spid_source_plugin_{state}",
                    $"'{group.Key.VirtualPath}' has {group.Count()} distribution rule(s) whose source form belongs to {state} plugin '{group.Key.PluginName}' (provider '{group.Key.ProviderName}').")), cancellationToken).ConfigureAwait(false);
            }
            foreach (var issue in spidInspection.Issues
                         .Where(value => value.Code is not "mo2.spid.source_plugin_disabled" and not "mo2.spid.source_plugin_missing")
                         .Take(1_024))
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "SPID distributions", null, issue.Code,
                    $"{issue.VirtualPath} line {issue.Line}: {issue.Detail}")), cancellationToken).ConfigureAwait(false);
            }

            var scriptInputs = ImmutableArray.CreateBuilder<Mo2Tes4PluginInput>();
            foreach (var plugin in baseline.Plugins
                .Where(value => value.IsEnabled && value.Observation?.FileAvailability == PluginFileAvailability.Present)
                .OrderBy(value => value.LoadOrder ?? int.MaxValue)
                .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var sourceProvider = plugin.Observation?.SourceProvider;
                var physical = baseline.PhysicalFiles
                    .Where(file => file.VirtualPath.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase) &&
                        sourceProvider is not null && file.ProviderName.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => file.Precedence)
                    .ThenBy(file => file.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (physical is null)
                {
                    issueCount++;
                    pluginScriptInspectionPartial = true;
                    await emit(new("gateIssue", new Mo2BaselineGateRecord(
                        "Plugin script dependencies",
                        null,
                        "mo2.baseline.plugin_physical_path_unavailable",
                        $"The resolved physical file for enabled plugin '{plugin.Name}' was unavailable; attached-script completeness was not claimed for it.")), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                scriptInputs.Add(new(
                    plugin.Name,
                    physical.CanonicalPath,
                    plugin.SourcePriority ?? int.MaxValue,
                    plugin.LoadOrder,
                    true));
            }

            var scriptInspection = await new Mo2PluginScriptDependencyInspector(new WindowsRandomAccessFileFactory())
                .InspectAsync(new(
                    scriptInputs.ToImmutable(),
                    baseline.ProviderChains.Select(value => value.VirtualPath).ToImmutableArray(),
                    new(MaximumRecordCatalogEntries: request.Limits.MaximumRecordCatalogEntries)), cancellationToken, async (progress, token) =>
                    {
                        await emit(new("pluginScriptProgress", new Mo2BaselinePluginScriptProgressRecord(
                            progress.PluginIndex,
                            progress.TotalPlugins,
                            progress.PluginName,
                            progress.Stage,
                            progress.PluginsScanned,
                            progress.RecordHeadersExamined,
                            progress.BytesScanned)), token).ConfigureAwait(false);
                    }, async (entry, token) =>
                    {
                        await emit(new("pluginRecordCatalog", entry), token).ConfigureAwait(false);
                    }).ConfigureAwait(false);
            var publishedScriptInspectionStatus = refresh.Status == ResolvedEnvironmentRefreshStatus.Partial
                ? Mo2PluginScriptDependencyStatus.Partial
                : scriptInspection.Status;
            var nifTextureInspection = await new Mo2NifTextureDependencyInspector(new WindowsRandomAccessFileFactory())
                .InspectAsync(new(baseline, scriptInspection.AssetReferences, new()), cancellationToken).ConfigureAwait(false);
            var publishedAssetInspectionStatus = refresh.Status == ResolvedEnvironmentRefreshStatus.Partial ||
                publishedScriptInspectionStatus != Mo2PluginScriptDependencyStatus.Complete ||
                nifTextureInspection.Status != Mo2PluginScriptDependencyStatus.Complete
                    ? Mo2PluginScriptDependencyStatus.Partial
                    : Mo2PluginScriptDependencyStatus.Complete;
            var publishedDirectAssetStatus = refresh.Status == ResolvedEnvironmentRefreshStatus.Partial ||
                scriptInspection.Status != Mo2PluginScriptDependencyStatus.Complete
                    ? Mo2PluginScriptDependencyStatus.Partial
                    : Mo2PluginScriptDependencyStatus.Complete;
            var publishedNifTextureStatus = refresh.Status == ResolvedEnvironmentRefreshStatus.Partial ||
                scriptInspection.Status != Mo2PluginScriptDependencyStatus.Complete ||
                nifTextureInspection.Status != Mo2PluginScriptDependencyStatus.Complete
                    ? Mo2PluginScriptDependencyStatus.Partial
                    : Mo2PluginScriptDependencyStatus.Complete;
            var publishableMissingAssets = MergeMissingAssetDependencies(
                refresh.Status == ResolvedEnvironmentRefreshStatus.Partial
                    ? []
                    : scriptInspection.MissingAssets,
                refresh.Status == ResolvedEnvironmentRefreshStatus.Partial
                    ? []
                    : nifTextureInspection.Missing);
            var assetSemanticFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{scriptInspection.SemanticFingerprint}\u001f{nifTextureInspection.SemanticFingerprint}"))).ToLowerInvariant();
            pluginScriptInspectionPartial |= publishedScriptInspectionStatus != Mo2PluginScriptDependencyStatus.Complete;
            pluginScriptInspectionPartial |= publishedAssetInspectionStatus != Mo2PluginScriptDependencyStatus.Complete;
            if (refresh.Status == ResolvedEnvironmentRefreshStatus.Partial)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Plugin script dependencies",
                    null,
                    "mo2.baseline.plugin_script_virtual_data_partial",
                    "The resolved virtual Data tree is partial; apparent missing PEX paths are retained only as candidates and are not asserted as absent.")), cancellationToken).ConfigureAwait(false);
            }
            await emit(new("pluginScriptInspection", new Mo2BaselinePluginScriptInspectionRecord(
                publishedScriptInspectionStatus,
                scriptInspection.PluginsScanned,
                scriptInspection.RecordHeadersExamined,
                scriptInspection.BytesScanned,
                scriptInspection.References.Length,
                scriptInspection.Missing.Length,
                scriptInspection.Issues.Length,
                scriptInspection.SemanticFingerprint,
                scriptInspection.RecordConflicts.Length,
                scriptInspection.RecordConflicts.Sum(value => (long)value.RecordCount),
                scriptInspection.RecordProvenance.Length,
                scriptInspection.RecordProvenance.Sum(value => (long)value.NewRecordCount + value.OverrideRecordCount),
                scriptInspection.RecordCatalogEntryCount)), cancellationToken).ConfigureAwait(false);
            await emit(new("pluginAssetInspection", new Mo2BaselinePluginAssetInspectionRecord(
                publishedAssetInspectionStatus,
                scriptInspection.PluginsScanned,
                scriptInspection.RecordHeadersExamined,
                scriptInspection.BytesScanned,
                scriptInspection.AssetReferences.Length + nifTextureInspection.References.Length,
                publishableMissingAssets.Length,
                scriptInspection.Issues.Count(value => value.Code.StartsWith("mo2.asset_dependency.", StringComparison.Ordinal)) + nifTextureInspection.Issues.Length,
                assetSemanticFingerprint,
                nifTextureInspection.MeshesInspected,
                nifTextureInspection.TextureReferences,
                publishedDirectAssetStatus,
                publishedNifTextureStatus)), cancellationToken).ConfigureAwait(false);
            foreach (var inspectionIssue in scriptInspection.Issues)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Plugin script dependencies",
                    null,
                    inspectionIssue.Code,
                    inspectionIssue.PluginName is null
                        ? inspectionIssue.Detail
                        : $"{inspectionIssue.PluginName}: {inspectionIssue.Detail}")), cancellationToken).ConfigureAwait(false);
            }
            foreach (var inspectionIssue in nifTextureInspection.Issues)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "NIF texture dependencies",
                    null,
                    inspectionIssue.Code,
                    inspectionIssue.VirtualPath is null
                        ? inspectionIssue.Detail
                        : $"{inspectionIssue.VirtualPath}: {inspectionIssue.Detail}")), cancellationToken).ConfigureAwait(false);
            }
            foreach (var conflict in scriptInspection.RecordConflicts)
            {
                await emit(new("pluginRecordConflict", conflict), cancellationToken).ConfigureAwait(false);
            }
            foreach (var provenance in scriptInspection.RecordProvenance)
            {
                await emit(new("pluginRecordProvenance", provenance), cancellationToken).ConfigureAwait(false);
            }
            foreach (var missing in refresh.Status == ResolvedEnvironmentRefreshStatus.Partial
                ? []
                : scriptInspection.Missing)
            {
                var sourceProvider = baseline.Plugins.FirstOrDefault(value =>
                    value.Name.Equals(missing.PluginName, StringComparison.OrdinalIgnoreCase))?.Observation?.SourceProvider;
                await emit(new("pluginScriptDependency", new Mo2BaselinePluginScriptDependencyRecord(
                    missing.PluginName,
                    sourceProvider,
                    missing.ScriptName,
                    missing.RequiredVirtualPath,
                    missing.ReferenceCount,
                    missing.Samples)), cancellationToken).ConfigureAwait(false);
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Plugin script dependencies",
                    null,
                    "mo2.baseline.plugin_script_missing",
                    $"Enabled plugin '{missing.PluginName}' references '{missing.RequiredVirtualPath}', but the resolved virtual Data tree has no provider for that file.")), cancellationToken).ConfigureAwait(false);
            }
            foreach (var missing in publishableMissingAssets)
            {
                var sourceProvider = baseline.Plugins.FirstOrDefault(value =>
                    value.Name.Equals(missing.PluginName, StringComparison.OrdinalIgnoreCase))?.Observation?.SourceProvider;
                await emit(new("pluginAssetDependency", new Mo2BaselinePluginAssetDependencyRecord(
                    missing.PluginName,
                    sourceProvider,
                    missing.RequiredVirtualPath,
                    missing.Kind,
                    missing.ReferenceCount,
                    missing.Samples)), cancellationToken).ConfigureAwait(false);
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Plugin asset dependencies",
                    null,
                    missing.Kind == Mo2PluginAssetReferenceKind.Mesh
                        ? "mo2.baseline.plugin_mesh_missing"
                        : "mo2.baseline.plugin_texture_missing",
                    BuildMissingAssetDetail(missing))), cancellationToken).ConfigureAwait(false);
            }
            // Resolve evidence scope deterministically. Provider seeds are exact MO2
            // identities. Plugin seeds are followed through their recursive TES4 master
            // closure, and only then translated to observed source providers.
            AddPluginProviderClosure(relevantProviders, request.EffectivePluginSeeds, baseline.Plugins);

            if (selectedInventory is not null && validation.DownloadsDirectory is not null)
            {
                foreach (var mod in selectedInventory.Entries
                    .Where(mod => relevantProviders.Contains(mod.Name))
                    .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var installationFile = mod.Directory?.Metadata.Metadata?.InstallationFile;
                    if (string.IsNullOrWhiteSpace(installationFile)) continue;
                    var declaredSourceArtifacts = await ObserveDeclaredSourceArtifactsAsync(
                        mod.Id.Value, mod.Name, installationFile, validation.DownloadsDirectory,
                        hasher, request.Limits, hashedBytes, sourceArtifactHashCache,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var sourceArtifact in declaredSourceArtifacts)
                    {
                        sourceArtifacts++;
                        if (sourceArtifact.HashStatus == Mo2BaselineHashStatus.Complete)
                        {
                            completeSourceArtifactHashes++;
                            if (sourceArtifact.CanonicalPath is not null &&
                                sourceArtifactHashCache.TryGetValue(sourceArtifact.CanonicalPath, out var cached) &&
                                cached.Status == Mo2BaselineHashStatus.Complete &&
                                accountedSourceArtifactHashes.Add(sourceArtifact.CanonicalPath))
                            {
                                hashedBytes += cached.Length;
                            }
                        }
                        await emit(new(
                            sourceArtifact.Kind == Mo2BaselineSourceArtifactKind.InstallationArchive
                                ? "sourceArchive" : "sourceArchiveSidecar",
                            sourceArtifact), cancellationToken).ConfigureAwait(false);
                    }
                    var matchingSourceArtifacts = await ObserveMatchingDownloadedSourceArtifactsAsync(
                        mod.Id.Value, mod.Name, installationFile, mod.Directory?.Metadata.Metadata,
                        validation.DownloadsDirectory, hasher, request.Limits, hashedBytes,
                        sourceArtifactHashCache, cancellationToken).ConfigureAwait(false);
                    foreach (var sourceArtifact in matchingSourceArtifacts)
                    {
                        sourceArtifacts++;
                        if (sourceArtifact.HashStatus == Mo2BaselineHashStatus.Complete)
                        {
                            completeSourceArtifactHashes++;
                            if (sourceArtifact.CanonicalPath is not null &&
                                sourceArtifactHashCache.TryGetValue(sourceArtifact.CanonicalPath, out var cached) &&
                                cached.Status == Mo2BaselineHashStatus.Complete &&
                                accountedSourceArtifactHashes.Add(sourceArtifact.CanonicalPath))
                            {
                                hashedBytes += cached.Length;
                            }
                        }
                        await emit(new(
                            sourceArtifact.Kind == Mo2BaselineSourceArtifactKind.InstallationArchive
                                ? "sourceArchive" : "sourceArchiveSidecar",
                            sourceArtifact), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            foreach (var discrepancy in baseline.Snapshot.Discrepancies)
            {
                issueCount++;
                await emit(new("gateIssue", new Mo2BaselineGateRecord(
                    "Resolved environment",
                    null,
                    discrepancy.Id.Value,
                    $"{discrepancy.Title}: {discrepancy.Detail}")), cancellationToken).ConfigureAwait(false);
            }
            foreach (var plugin in baseline.Plugins.OrderBy(value => value.LoadOrder ?? int.MaxValue)
                .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                plugins++;
                await emit(new("plugin", new Mo2BaselinePluginRecord(plugin)), cancellationToken).ConfigureAwait(false);
                foreach (var master in plugin.Observation?.Masters ?? [])
                {
                    await emit(new("pluginMaster", new Mo2BaselinePluginMasterRecord(
                        plugin.Id.Value,
                        plugin.Name,
                        master)), cancellationToken).ConfigureAwait(false);
                }
                await CheckpointAsync("plugins", plugins, baseline.Plugins.Length).ConfigureAwait(false);
            }

            foreach (var archive in baseline.Archives.OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase))
            {
                archives++;
                await emit(new("archive", new Mo2BaselineArchiveRecord(archive)), cancellationToken).ConfigureAwait(false);
                await CheckpointAsync("archives", archives, baseline.Archives.Length).ConfigureAwait(false);
            }

            foreach (var chain in baseline.ProviderChains)
            {
                virtualPaths++;
                foreach (var provider in chain.Providers)
                {
                    await emit(new("virtualProvider", new Mo2BaselineVirtualProviderRecord(
                        chain.VirtualPath,
                        chain.WinnerConfidence,
                        chain.WinningProviderId,
                        provider)), cancellationToken).ConfigureAwait(false);
                }
                await CheckpointAsync("virtualProviders", virtualPaths, baseline.ProviderChains.Length).ConfigureAwait(false);
            }

            var hashCandidates = baseline.PhysicalFiles
                .Where(file => IsPluginOrArchive(file.VirtualPath) || relevantProviders.Contains(file.ProviderName))
                .Concat(ObserveSkseDiagnosticLogs(diagnosticOutputRoot, emit, cancellationToken))
                .ToImmutableArray();
            physicalFiles = hashCandidates.Length;
            foreach (var file in hashCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hashCandidatesProcessed++;
                var hash = await HashFileAsync(file, hasher, request.Limits, hashedBytes, request.EffectiveResumeHashes, cancellationToken)
                    .ConfigureAwait(false);
                if (hash.Status == Mo2BaselineHashStatus.Complete)
                {
                    completeHashes++;
                    if (hash.Detail != "ReusedFromVerifiedPredecessorPartition") hashedBytes += hash.Length;
                }

                await emit(new("fileHash", hash), cancellationToken).ConfigureAwait(false);
                await CheckpointAsync("fileHashes", hashCandidatesProcessed, hashCandidates.Length).ConfigureAwait(false);
                if (hash.Status == Mo2BaselineHashStatus.AggregateLimitExceeded)
                {
                    break;
                }
            }

            var partial = refresh.Status == ResolvedEnvironmentRefreshStatus.Partial ||
                inventory.Status != Mo2InventoryObservationStatus.Complete ||
                completeHashes != physicalFiles ||
                pluginScriptInspectionPartial ||
                spidInspectionPartial;
            return await FinishAsync(partial ? Mo2BaselineStatus.Partial : Mo2BaselineStatus.Completed).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await FinishAsync(Mo2BaselineStatus.Canceled, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            issueCount++;
            await emit(new("gateIssue", new Mo2BaselineGateRecord(
                "Baseline", null, "mo2.baseline.failed", exception.Message)), CancellationToken.None).ConfigureAwait(false);
            return await FinishAsync(Mo2BaselineStatus.Failed, CancellationToken.None).ConfigureAwait(false);
        }

        async Task CheckpointAsync(string stage, long completed, long? total)
        {
            if (completed % request.Limits.CheckpointInterval == 0 || completed == total)
            {
                await emit(new("checkpoint", new Mo2BaselineCheckpointRecord(
                    stage, completed, total, hashedBytes)), cancellationToken).ConfigureAwait(false);
                var pageNumber = checked((completed + request.Limits.CheckpointInterval - 1) /
                    request.Limits.CheckpointInterval);
                var first = checked((pageNumber - 1) * request.Limits.CheckpointInterval + 1);
                await emit(new("page", new Mo2BaselinePageRecord(
                    stage, pageNumber, first, completed, total)), cancellationToken).ConfigureAwait(false);
            }
        }

        async Task<Mo2BaselineSummaryRecord> FinishAsync(
            Mo2BaselineStatus status,
            CancellationToken token = default)
        {
            var summary = new Mo2BaselineSummaryRecord(
                status,
                installationId,
                profileId,
                connectionFingerprint,
                profileFingerprint,
                inventoryFingerprint,
                environmentFingerprint,
                mods,
                plugins,
                archives,
                virtualPaths,
                physicalFiles,
                completeHashes,
                sourceArtifacts,
                completeSourceArtifactHashes,
                hashedBytes,
                issueCount,
                requiredAuthorizations.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
            await emit(new("summary", summary), token).ConfigureAwait(false);
            return summary;
        }
    }

    private async Task<ImmutableArray<Mo2BaselineSourceArtifactRecord>> ObserveDeclaredSourceArtifactsAsync(
        string modId,
        string modName,
        string declaredInstallationFile,
        string downloadsDirectory,
        IMo2RandomAccessFileFactory files,
        Mo2BaselineLimits limits,
        long alreadyHashedBytes,
        Dictionary<string, Mo2BaselineFileHashRecord> hashCache,
        CancellationToken cancellationToken,
        string? relationshipOverride = null)
    {
        var claim = declaredInstallationFile.Trim();
        var isRootedClaim = false;
        string leaf;
        try
        {
            isRootedClaim = Path.IsPathRooted(claim);
            leaf = Path.GetFileName(claim);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            leaf = string.Empty;
        }
        var relationship = relationshipOverride ?? (isRootedClaim
            ? "LeafDerivedFromMo2ExternalPathClaim; only the same-named immediate child of the authorized MO2 downloads directory is inspected."
            : "DeclaredByMo2Metadata; archive contents are not inferred from the filename.");
        var relativePathClaim = !isRootedClaim && claim.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
        if (claim.Length == 0 || leaf.Length == 0 || relativePathClaim || leaf is "." or ".." ||
            leaf.Any(char.IsControl) || !string.Equals(Path.GetFileName(leaf), leaf, StringComparison.Ordinal))
        {
            return [new(
                modId,
                modName,
                declaredInstallationFile,
                relationship,
                Mo2BaselineSourceArtifactKind.InstallationArchive,
                leaf,
                null,
                Mo2PathState.Invalid,
                null,
                null,
                null,
                null,
                "installationFile does not yield one safe archive leaf; no downloads path was inspected.")];
        }

        var observations = ImmutableArray.CreateBuilder<Mo2BaselineSourceArtifactRecord>(2);
        var hashBudget = alreadyHashedBytes;
        foreach (var candidate in new[]
        {
            (Kind: Mo2BaselineSourceArtifactKind.InstallationArchive, Leaf: leaf),
            (Kind: Mo2BaselineSourceArtifactKind.MetadataSidecar, Leaf: leaf + ".meta"),
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string lexicalPath;
            try
            {
                lexicalPath = Path.Combine(downloadsDirectory, candidate.Leaf);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                observations.Add(new(
                    modId, modName, declaredInstallationFile, relationship, candidate.Kind, candidate.Leaf,
                    null, Mo2PathState.Invalid, null, null, null, null, exception.GetType().Name));
                continue;
            }

            if (!paths.TryCanonicalize(lexicalPath, out var canonicalPath, out var error) ||
                !paths.IsImmediateChildOf(canonicalPath, downloadsDirectory))
            {
                observations.Add(new(
                    modId, modName, declaredInstallationFile, relationship, candidate.Kind, candidate.Leaf,
                    null, Mo2PathState.Invalid, null, null, null, null,
                    error ?? "The exact downloads child resolves outside the validated downloads root."));
                continue;
            }

            var metadata = fileSystem.GetFileMetadata(canonicalPath);
            if (metadata.State != Mo2PathState.Present || metadata.Stamp is not Mo2FileStamp stamp)
            {
                observations.Add(new(
                    modId, modName, declaredInstallationFile, relationship, candidate.Kind, candidate.Leaf,
                    canonicalPath, metadata.State, metadata.Stamp?.Length, metadata.Stamp?.LastWriteTimeUtcTicks,
                    null, null, metadata.State == Mo2PathState.Missing ? null : "The exact source artifact is unreadable."));
                continue;
            }

            if (!hashCache.TryGetValue(canonicalPath, out var hash))
            {
                hash = await HashFileAsync(
                    new(
                        candidate.Leaf,
                        canonicalPath,
                        VirtualProviderKind.ModLooseFile,
                        modName,
                        0,
                        stamp.Length,
                        stamp.LastWriteTimeUtcTicks),
                    files,
                    limits,
                    hashBudget,
                    null,
                    cancellationToken).ConfigureAwait(false);
                hashCache.Add(canonicalPath, hash);
                if (hash.Status == Mo2BaselineHashStatus.Complete)
                {
                    hashBudget += hash.Length;
                }
            }

            var providerIdentity = candidate.Kind == Mo2BaselineSourceArtifactKind.MetadataSidecar
                ? await ReadSourceProviderIdentityAsync(canonicalPath, leaf, cancellationToken).ConfigureAwait(false)
                : null;
            observations.Add(new(
                modId,
                modName,
                declaredInstallationFile,
                relationship,
                candidate.Kind,
                candidate.Leaf,
                canonicalPath,
                metadata.State,
                stamp.Length,
                stamp.LastWriteTimeUtcTicks,
                hash.Status,
                hash.Sha256,
                hash.Detail,
                providerIdentity));
        }

        return observations.ToImmutable();
    }

    private async Task<ImmutableArray<Mo2BaselineSourceArtifactRecord>> ObserveMatchingDownloadedSourceArtifactsAsync(
        string modId,
        string modName,
        string declaredInstallationFile,
        Mo2NormalizedModMetadata? metadata,
        string downloadsDirectory,
        IMo2RandomAccessFileFactory files,
        Mo2BaselineLimits limits,
        long alreadyHashedBytes,
        Dictionary<string, Mo2BaselineFileHashRecord> hashCache,
        CancellationToken cancellationToken)
    {
        if (metadata?.Repository?.Equals("Nexus", StringComparison.OrdinalIgnoreCase) != true ||
            string.IsNullOrWhiteSpace(metadata.NexusGameName) || metadata.NexusModId is null or <= 0)
            return [];

        IReadOnlyList<string> downloadFiles;
        try { downloadFiles = fileSystem.EnumerateFiles(downloadsDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
        var maximumCandidates = (int)Math.Min(limits.MaximumEntries, 100_000L);
        if (downloadFiles.Count > maximumCandidates) return [];

        var declaredLeaf = Path.GetFileName(declaredInstallationFile.Trim());
        var observations = ImmutableArray.CreateBuilder<Mo2BaselineSourceArtifactRecord>();
        var localBudget = alreadyHashedBytes;
        foreach (var sidecarPath in downloadFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.TryCanonicalize(sidecarPath, out var canonicalSidecar, out _) ||
                !paths.IsImmediateChildOf(canonicalSidecar, downloadsDirectory)) continue;
            var sidecarLeaf = Path.GetFileName(canonicalSidecar);
            if (!sidecarLeaf.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            var archiveLeaf = sidecarLeaf[..^5];
            if (archiveLeaf.Length == 0 || archiveLeaf.Equals(declaredLeaf, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(archiveLeaf), archiveLeaf, StringComparison.Ordinal)) continue;

            var identity = await ReadSourceProviderIdentityAsync(canonicalSidecar, archiveLeaf, cancellationToken).ConfigureAwait(false);
            if (identity is null || identity.Status != "ObservedComplete" ||
                !identity.Repository!.Equals(metadata.Repository, StringComparison.OrdinalIgnoreCase) ||
                !identity.GameName!.Equals(metadata.NexusGameName, StringComparison.OrdinalIgnoreCase) ||
                identity.ModId != metadata.NexusModId) continue;

            var discovered = await ObserveDeclaredSourceArtifactsAsync(
                modId, modName, archiveLeaf, downloadsDirectory, files, limits, localBudget,
                hashCache, cancellationToken,
                "DiscoveredByMatchingMo2DownloadSidecar; repository, game, mod ID, file ID, and archive leaf were observed before classification.").ConfigureAwait(false);
            observations.AddRange(discovered);
            foreach (var record in discovered.Where(record => record.HashStatus == Mo2BaselineHashStatus.Complete && record.Length is > 0))
                localBudget += record.Length!.Value;
        }
        return observations.ToImmutable();
    }

    private async Task<Mo2BaselineSourceProviderIdentity?> ReadSourceProviderIdentityAsync(
        string sidecarPath,
        string archiveLeaf,
        CancellationToken cancellationToken)
    {
        const int maximumSidecarBytes = 256 * 1024;
        string text;
        try
        {
            text = await fileSystem.ReadTextAsync(sidecarPath, maximumSidecarBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(null, null, null, null, null, archiveLeaf, $"Unreadable:{exception.GetType().Name}");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "General";
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }
            if (!section.Equals("General", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim().ToLowerInvariant();
            if (key is not ("repository" or "gamename" or "modid" or "fileid" or "version")) continue;
            values.TryAdd(key, line[(separator + 1)..].Trim().Trim('"'));
        }

        static string? Value(Dictionary<string, string> source, params string[] keys)
        {
            foreach (var key in keys)
                if (source.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
            return null;
        }
        static long? PositiveLong(string? value) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                ? parsed : null;

        var repository = Value(values, "repository");
        var gameName = Value(values, "gamename");
        var providerModId = PositiveLong(Value(values, "modid"));
        var providerFileId = PositiveLong(Value(values, "fileid"));
        var version = Value(values, "version");
        var status = repository?.Equals("Nexus", StringComparison.OrdinalIgnoreCase) == true &&
            !string.IsNullOrWhiteSpace(gameName) && providerModId.HasValue && providerFileId.HasValue
                ? "ObservedComplete"
                : "ObservedIncomplete";
        return new(repository, gameName, providerModId, providerFileId, version, archiveLeaf, status);
    }

    private ImmutableArray<string> CanonicalAuthorizations(ImmutableArray<string> authorizations) =>
        authorizations
            .Select(path => paths.TryCanonicalize(path, out var canonical, out _) ? canonical : null)
            .Where(path => path is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private bool IsAuthorizedOrWithinConnection(
        string path,
        Mo2InstallationReference reference,
        ImmutableArray<string> authorized)
    {
        var application = Path.GetDirectoryName(reference.ExecutablePath);
        return paths.IsWithinRoot(path, reference.InstanceDirectory) ||
            application is not null && paths.IsWithinRoot(path, application) ||
            authorized.Any(value => paths.Equals(value, path));
    }

    private ImmutableArray<(Mo2ContentRootKind Kind, string Label, string Path)> RequiredContentRoots(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedProfile profile)
    {
        var roots = ImmutableArray.CreateBuilder<(Mo2ContentRootKind, string, string)>();
        if (validation.GameDirectory is not null)
        {
            roots.Add((Mo2ContentRootKind.GameDirectory, "Game directory", validation.GameDirectory));
            roots.Add((Mo2ContentRootKind.GameData, "Game Data", Path.Combine(validation.GameDirectory, "Data")));
        }
        if (validation.OverwriteDirectory is not null && !paths.IsWithinRoot(validation.OverwriteDirectory, reference.InstanceDirectory))
        {
            roots.Add((Mo2ContentRootKind.Overwrite, "External overwrite", validation.OverwriteDirectory));
        }
        if (profile.Observation.LocalSettingsEnabled == false)
        {
            roots.Add((Mo2ContentRootKind.GlobalGameSettings, "Global Skyrim settings", globalSettingsDirectory));
        }
        return roots.ToImmutable();
    }

    private static Mo2PathState SourceState(ProfileSourceAvailability availability) => availability switch
    {
        ProfileSourceAvailability.Read => Mo2PathState.Present,
        ProfileSourceAvailability.OptionalAbsent or ProfileSourceAvailability.RequiredMissing => Mo2PathState.Missing,
        ProfileSourceAvailability.Inaccessible => Mo2PathState.Inaccessible,
        _ => Mo2PathState.Unavailable,
    };

    private static bool IsPluginOrArchive(string virtualPath) => Path.GetExtension(virtualPath).ToLowerInvariant() switch
    {
        ".esm" or ".esp" or ".esl" or ".bsa" or ".ba2" => true,
        _ => false,
    };

    private static IEnumerable<Mo2BaselinePhysicalFile> ObserveSkseDiagnosticLogs(
        string diagnosticOutputRoot,
        Func<Mo2BaselineEvent, CancellationToken, ValueTask> emit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(diagnosticOutputRoot) || !Directory.Exists(diagnosticOutputRoot)) yield break;
        FileInfo[] observed;
        try
        {
            var directory = new DirectoryInfo(diagnosticOutputRoot);
            observed = new[] { "crash-*.log", "skse*.log" }
                .SelectMany(pattern => directory.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
                .Where(file => !file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .GroupBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumSkseDiagnosticLogs)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            emit(new("gateIssue", new Mo2BaselineGateRecord(
                "SKSE diagnostic outputs", diagnosticOutputRoot, "mo2.baseline.diagnostic_output_unreadable",
                exception.GetType().Name)), cancellationToken).AsTask().GetAwaiter().GetResult();
            yield break;
        }
        foreach (var file in observed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(
                $"diagnostics/skse/{file.Name}", file.FullName, VirtualProviderKind.ModLooseFile,
                "SKSE diagnostic outputs", int.MaxValue, file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }

    private static ImmutableArray<Mo2MissingPluginAssetDependency> MergeMissingAssetDependencies(
        IEnumerable<Mo2MissingPluginAssetDependency> direct,
        IEnumerable<Mo2MissingPluginAssetDependency> transitive)
    {
        var groups = new Dictionary<string, List<Mo2MissingPluginAssetDependency>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in direct.Concat(transitive))
        {
            var key = $"{dependency.PluginName}\u001f{dependency.Kind}\u001f{dependency.RequiredVirtualPath}";
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups.Add(key, values);
            }
            values.Add(dependency);
        }

        return groups.Values.Select(values => new Mo2MissingPluginAssetDependency(
                values[0].PluginName,
                values[0].RequiredVirtualPath,
                values[0].Kind,
                values.Sum(value => value.ReferenceCount),
                values.SelectMany(value => value.Samples)
                    .OrderBy(value => value.DiscoveredThroughVirtualPath is null ? 0 : 1)
                    .ThenBy(value => value.RecordOffset)
                    .Take(8)
                    .ToImmutableArray()))
            .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static string BuildMissingAssetDetail(Mo2MissingPluginAssetDependency missing)
    {
        var through = missing.Samples.Select(value => value.DiscoveredThroughVirtualPath)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var provenance = through.Length == 0
            ? string.Empty
            : $" through resolved NIF '{through[0]}'";
        return $"Enabled plugin '{missing.PluginName}' references {missing.Kind.ToString().ToLowerInvariant()} '{missing.RequiredVirtualPath}'{provenance}, but the resolved virtual Data tree has no provider for that file.";
    }

    private static void AddPluginProviderClosure(
        HashSet<string> providers,
        ImmutableArray<string> pluginSeeds,
        ImmutableArray<PluginEntry> plugins)
    {
        var byName = plugins.ToDictionary(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(pluginSeeds
            .Where(seed => !string.IsNullOrWhiteSpace(seed))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var name = pending.Dequeue();
            if (!visited.Add(name) || !byName.TryGetValue(name, out var plugin)) continue;
            if (!string.IsNullOrWhiteSpace(plugin.Observation?.SourceProvider))
            {
                providers.Add(plugin.Observation.SourceProvider);
            }
            foreach (var master in (plugin.Observation?.Masters ?? [])
                .OrderBy(master => master.SourceOrder))
            {
                pending.Enqueue(master.Name);
            }
        }
    }

    private static async Task<Mo2BaselineFileHashRecord> HashFileAsync(
        Mo2BaselinePhysicalFile candidate,
        IMo2RandomAccessFileFactory files,
        Mo2BaselineLimits limits,
        long hashedBytes,
        ImmutableDictionary<string, Mo2BaselineFileHashRecord>? resumeHashes,
        CancellationToken cancellationToken)
    {
        var kind = Path.GetExtension(candidate.VirtualPath).ToLowerInvariant() switch
        {
            ".log" when candidate.ProviderName == "SKSE diagnostic outputs" => Mo2BaselineFileKind.DiagnosticOutput,
            ".esm" or ".esp" or ".esl" => Mo2BaselineFileKind.Plugin,
            ".bsa" or ".ba2" => Mo2BaselineFileKind.Archive,
            _ => Mo2BaselineFileKind.LooseFile,
        };
        if (limits.MaximumFileBytes > 0 && candidate.Length > limits.MaximumFileBytes)
        {
            return Result(Mo2BaselineHashStatus.PerFileLimitExceeded, null, "The file exceeds the configured per-file hashing limit.");
        }
        if (resumeHashes is not null && resumeHashes.TryGetValue(candidate.CanonicalPath, out var prior) &&
            prior.Status == Mo2BaselineHashStatus.Complete && prior.Length == candidate.Length &&
            prior.LastWriteTimeUtcTicks == candidate.LastWriteTimeUtcTicks &&
            !string.IsNullOrWhiteSpace(prior.Sha256))
        {
            return Result(Mo2BaselineHashStatus.Complete, prior.Sha256, "ReusedFromVerifiedPredecessorPartition");
        }
        if (candidate.Length < 0 || hashedBytes > limits.MaximumTotalHashBytes - candidate.Length)
        {
            return Result(Mo2BaselineHashStatus.AggregateLimitExceeded, null, "The aggregate hashing byte budget was reached.");
        }

        try
        {
            var pathWriteBefore = File.GetLastWriteTimeUtc(candidate.CanonicalPath).Ticks;
            if (pathWriteBefore != candidate.LastWriteTimeUtcTicks)
            {
                return Result(Mo2BaselineHashStatus.ChangedDuringRead, null, "The file timestamp changed after provider observation.");
            }
            await using var file = await files.OpenReadAsync(candidate.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (file.Length != candidate.Length)
            {
                return Result(Mo2BaselineHashStatus.ChangedDuringRead, null, "The file length changed after provider observation.");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (offset < file.Length)
            {
                var count = checked((int)Math.Min(buffer.Length, file.Length - offset));
                var read = await file.ReadAsync(offset, buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    return Result(Mo2BaselineHashStatus.Inaccessible, null, "The file ended before the observed length was read.");
                }
                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            var after = await file.GetCurrentStampAsync(cancellationToken).ConfigureAwait(false);
            if (after != file.InitialStamp ||
                file.InitialStamp.Identity.Length != candidate.Length ||
                File.GetLastWriteTimeUtc(candidate.CanonicalPath).Ticks != pathWriteBefore)
            {
                return Result(Mo2BaselineHashStatus.ChangedDuringRead, null, "The file identity or timestamp changed during hashing.");
            }

            return Result(
                Mo2BaselineHashStatus.Complete,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result(Mo2BaselineHashStatus.Inaccessible, null, exception.GetType().Name);
        }

        Mo2BaselineFileHashRecord Result(Mo2BaselineHashStatus status, string? sha256, string? detail) => new(
            candidate.VirtualPath,
            candidate.CanonicalPath,
            kind,
            candidate.ProviderName,
            candidate.Length,
            candidate.LastWriteTimeUtcTicks,
            status,
            sha256,
            detail);
    }

    private sealed class SingleReferenceStore(Mo2InstallationReference reference) : IMo2InstallationReferenceStore
    {
        public Task<Mo2ReferenceLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Mo2ReferenceLoadResult([reference], []));

        public Task SaveAsync(ImmutableArray<Mo2InstallationReference> references, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The diagnostic baseline host never persists installation references.");
    }

    private sealed class StaticValidator(Mo2InstallationValidation validation) : IMo2InstallationValidator
    {
        public Task<Mo2InstallationValidation> ValidateAsync(
            Mo2ValidationRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(validation);
    }

    private sealed class SelectedProfileSnapshotService(Mo2ProfileSnapshot snapshot) : IMo2ProfileSnapshotService
    {
        public Task<Mo2ProfileSnapshot> ObserveAsync(
            Mo2ProfileSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Reference.Id != snapshot.ReferenceId)
            {
                throw new InvalidOperationException("The selected-profile snapshot is not bound to this installation reference.");
            }
            return Task.FromResult(snapshot);
        }
    }
}
