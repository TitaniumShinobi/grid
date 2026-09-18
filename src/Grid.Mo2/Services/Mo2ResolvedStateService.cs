using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed record Mo2ContentAuthorizationRequirement(
    Mo2ContentRootKind Kind,
    string Label,
    string ExpectedPath);

public sealed class Mo2ResolvedStateService : IWorkspaceEnvironmentQueryService
{
    private const int MaximumCreationManifestBytes = 64 * 1024;
    private const int MaximumCreationManifestEntries = 512;
    private static readonly HashSet<string> ImplicitCorePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    private readonly IMo2InstallationReferenceStore _references;
    private readonly Func<Mo2InstallationReference, IMo2InstallationValidator> _validatorFactory;
    private readonly IMo2ProfileSnapshotService _profiles;
    private readonly IMo2ModInventoryService _inventory;
    private readonly IMo2ContentTreeObserver _trees;
    private readonly IMo2RandomAccessFileFactory _files;
    private readonly IMo2PluginHeaderParser _pluginParser;
    private readonly IMo2BsaIndexParser _archiveParser;
    private readonly IMo2SessionContentPathAuthorization _authorization;
    private readonly IMo2PathCanonicalizer _paths;
    private readonly IMo2ReadOnlyFileSystem _readOnlyFileSystem;
    private readonly IMo2TextDecoder _textDecoder;
    private readonly Mo2ResolvedStateCache _cache;
    private readonly Mo2VirtualDataResolver _resolver;
    private readonly Mo2ResolvedStateLimits _limits;
    private readonly string _globalSettingsDirectory;

    public Mo2ResolvedStateService(
        IMo2InstallationReferenceStore references,
        Func<Mo2InstallationReference, IMo2InstallationValidator> validatorFactory,
        IMo2ProfileSnapshotService profiles,
        IMo2ModInventoryService inventory,
        IMo2ContentTreeObserver trees,
        IMo2RandomAccessFileFactory files,
        IMo2PluginHeaderParser pluginParser,
        IMo2BsaIndexParser archiveParser,
        IMo2SessionContentPathAuthorization authorization,
        IMo2PathCanonicalizer paths,
        IMo2ReadOnlyFileSystem readOnlyFileSystem,
        IMo2TextDecoder textDecoder,
        Mo2ResolvedStateCache cache,
        Mo2VirtualDataResolver resolver,
        string globalSettingsDirectory,
        Mo2ResolvedStateLimits? limits = null)
    {
        _references = references ?? throw new ArgumentNullException(nameof(references));
        _validatorFactory = validatorFactory ?? throw new ArgumentNullException(nameof(validatorFactory));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _trees = trees ?? throw new ArgumentNullException(nameof(trees));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _pluginParser = pluginParser ?? throw new ArgumentNullException(nameof(pluginParser));
        _archiveParser = archiveParser ?? throw new ArgumentNullException(nameof(archiveParser));
        _authorization = authorization ?? throw new ArgumentNullException(nameof(authorization));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _readOnlyFileSystem = readOnlyFileSystem ?? throw new ArgumentNullException(nameof(readOnlyFileSystem));
        _textDecoder = textDecoder ?? throw new ArgumentNullException(nameof(textDecoder));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _globalSettingsDirectory = globalSettingsDirectory ?? throw new ArgumentNullException(nameof(globalSettingsDirectory));
        _limits = limits ?? Mo2ResolvedStateLimits.Default;
        _limits.Validate();
    }

    public async Task<ImmutableArray<Mo2ContentAuthorizationRequirement>> GetAuthorizationRequirementsAsync(
        WorkspaceEnvironmentContext context,
        CancellationToken cancellationToken = default)
    {
        var observation = await ObserveContextAsync(context, includeInventory: false, cancellationToken).ConfigureAwait(false);
        if (observation is null)
        {
            return [];
        }

        return RequiredRoots(observation.Value.Reference, observation.Value.Validation, observation.Value.Profile)
            .Where(requirement => !_authorization.IsRootAuthorized(
                observation.Value.Reference.Id,
                requirement.Kind,
                requirement.ExpectedPath))
            .ToImmutableArray();
    }

    public async Task<bool> AuthorizeExactRootAsync(
        WorkspaceEnvironmentContext context,
        Mo2ContentRootKind kind,
        string selectedPath,
        CancellationToken cancellationToken = default)
    {
        var requirements = await GetAuthorizationRequirementsAsync(context, cancellationToken).ConfigureAwait(false);
        var expected = requirements.FirstOrDefault(requirement => requirement.Kind == kind);
        if (expected is null)
        {
            return false;
        }

        try
        {
            if (!_paths.TryCanonicalize(selectedPath, out var selectedCanonical, out _) ||
                !_paths.TryCanonicalize(expected.ExpectedPath, out var expectedCanonical, out _) ||
                !_paths.Equals(selectedCanonical, expectedCanonical))
            {
                return false;
            }

            _authorization.AuthorizeRoot(
                (await RequireReferenceAsync(context.InstallationId, cancellationToken).ConfigureAwait(false)).Id,
                kind,
                selectedCanonical);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async Task<ResolvedEnvironmentRefreshResult> RefreshAsync(
        WorkspaceEnvironmentContext context,
        bool forceRefresh,
        IProgress<ResolvedEnvironmentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cache.TryGetCurrent(context, out var cached))
        {
            return new(
                ResolvedEnvironmentRefreshStatus.Completed,
                cached.PublicSnapshot with { IsFromSessionCache = true },
                "Complete read-only evidence restored from the reconstructable session cache.",
                []);
        }

        try
        {
            progress?.Report(new(EnvironmentObservationStage.Validating, 0, null, "Revalidating the connected MO2 reference."));
            var observation = await ObserveContextAsync(context, includeInventory: true, cancellationToken).ConfigureAwait(false);
            if (observation is null)
            {
                return new(ResolvedEnvironmentRefreshStatus.Unavailable, null, "The selected connected profile is unavailable.", []);
            }

            var (reference, validation, profile, inventory) = observation.Value;
            var requirements = RequiredRoots(reference, validation, profile)
                .Where(requirement => !_authorization.IsRootAuthorized(reference.Id, requirement.Kind, requirement.ExpectedPath))
                .ToImmutableArray();
            if (!requirements.IsEmpty)
            {
                return new(
                    ResolvedEnvironmentRefreshStatus.AuthorizationRequired,
                    null,
                    "Exact session authorization is required before external content roots are observed.",
                    requirements.Select(requirement => requirement.Label).ToImmutableArray());
            }

            progress?.Report(new(EnvironmentObservationStage.ObservingLooseFiles, 0, null, "Observing bounded read-only provider trees."));
            var loose = await ObserveLooseProvidersAsync(reference, validation, profile, inventory, cancellationToken).ConfigureAwait(false);
            var physicalWinners = BuildLooseWinners(loose);
            var creationManifest = await ObserveCreationManifestAsync(reference, validation, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(EnvironmentObservationStage.ParsingPlugins, 0, physicalWinners.Count, "Reading bounded TES4 headers."));
            var plugins = await BuildPluginsAsync(profile, creationManifest, physicalWinners, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new(EnvironmentObservationStage.ParsingArchives, 0, null, "Reading supported archive indexes without payload extraction."));
            var archiveNames = await ObserveEffectiveArchiveListAsync(profile, cancellationToken).ConfigureAwait(false);
            var archives = await BuildArchivesAsync(profile, archiveNames, physicalWinners, plugins, progress, cancellationToken).ConfigureAwait(false);

            var seed = EvidenceFingerprint(context, validation, profile, inventory, creationManifest, loose, plugins, archives);
            var snapshotId = new ResolvedSnapshotId($"resolved.{seed[..24]}");
            progress?.Report(new(EnvironmentObservationStage.ResolvingProviders, 0, null, "Resolving explainable provider chains."));
            var data = _resolver.Resolve(snapshotId, loose, archives.Select(value => value.Provider), _limits, cancellationToken);
            var discrepancyBuilder = BuildDiscrepancies(creationManifest, loose, plugins, archives, data).ToImmutableArray().ToBuilder();
            var candidateLimitExceeded = physicalWinners.Keys.Count(IsPlugin) > _limits.MaximumPlugins ||
                physicalWinners.Keys.Count(IsArchive) > _limits.MaximumArchives;
            if (candidateLimitExceeded)
            {
                discrepancyBuilder.Add(Discrepancy(
                    "binary-candidate-limit",
                    EnvironmentDiscrepancyKind.SafetyLimitExceeded,
                    "Binary candidate safety limit reached",
                    "The snapshot is partial; omitted plugin or archive candidates are not presented as resolved."));
            }

            var discrepancies = discrepancyBuilder.ToImmutable();
            var partial = creationManifest.IsPartial || candidateLimitExceeded || loose.Any(input => input.Tree.IsPartial) ||
                archives.Any(value => value.Partial) || data.IsPartial;
            var observedAt = DateTimeOffset.UtcNow;
            var summary = new ResolvedEnvironmentSummary(
                partial ? ResolvedEnvironmentStatus.Partial : ResolvedEnvironmentStatus.Complete,
                observedAt,
                seed,
                plugins.Length,
                archives.Length,
                data.Entries.Length,
                data.ProviderCount,
                discrepancies.Length);
            var snapshot = new ResolvedEnvironmentSnapshot(snapshotId, context, summary, discrepancies, false);
            var physicalFiles = loose
                .SelectMany(provider => provider.Tree.Entries
                    .Where(entry => entry.Kind == Mo2ContentEntryKind.File)
                    .Select(entry => new Mo2BaselinePhysicalFile(
                        entry.VirtualPath,
                        entry.CanonicalPath,
                        provider.Kind,
                        provider.SourceName,
                        provider.Precedence,
                        entry.Length ?? 0,
                        entry.LastWriteTimeUtcTicks)))
                .OrderBy(file => file.VirtualPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(file => file.Precedence)
                .ThenBy(file => file.CanonicalPath, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var resolved = new Mo2ResolvedEnvironmentData(
                snapshot,
                plugins,
                archives.Select(value => value.Entry).ToImmutableArray(),
                data.Entries,
                data.Chains,
                physicalFiles,
                seed);
            if (partial)
            {
                _cache.PublishTransient(resolved);
            }
            else
            {
                _cache.Store(resolved);
            }

            progress?.Report(new(EnvironmentObservationStage.Publishing, 1, 1, "Published immutable read-only evidence."));
            return new(
                partial ? ResolvedEnvironmentRefreshStatus.Partial : ResolvedEnvironmentRefreshStatus.Completed,
                snapshot,
                partial
                    ? "The read-only snapshot is partial; every omitted or uncertain result is disclosed."
                    : "The read-only snapshot is complete for the supported evidence policy.",
                []);
        }
        catch (OperationCanceledException)
        {
            return new(ResolvedEnvironmentRefreshStatus.Canceled, null, "The read-only refresh was canceled.", []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(
                ResolvedEnvironmentRefreshStatus.Failed,
                null,
                $"The read-only refresh failed safely ({exception.GetType().Name}); no external state changed.",
                []);
        }
    }

    public Task<EnvironmentPage<PluginEntry>> QueryPluginsAsync(
        ResolvedSnapshotId snapshotId,
        PluginQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RequireSnapshot(snapshotId);
        IEnumerable<PluginEntry> result = snapshot.Plugins;
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            result = result.Where(plugin => plugin.Name.Contains(query.SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (query.Enabled is bool enabled) result = result.Where(plugin => plugin.IsEnabled == enabled);
        if (query.Extension is PluginFileExtension extension) result = result.Where(plugin => plugin.Observation?.Extension == extension);
        if (query.MasterStatus is PluginMasterStatus master) result = result.Where(plugin => plugin.Observation?.Masters.Any(value => value.Status == master) == true);
        return Task.FromResult(Page(result, query.Offset, query.PageSize, snapshotId));
    }

    public Task<EnvironmentPage<ResolvedArchiveEntry>> QueryArchivesAsync(
        ResolvedSnapshotId snapshotId,
        ArchiveQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RequireSnapshot(snapshotId);
        IEnumerable<ResolvedArchiveEntry> result = snapshot.Archives;
        if (!string.IsNullOrWhiteSpace(query.SearchText)) result = result.Where(value => value.Name.Contains(query.SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (query.Activation is ArchiveActivationState activation) result = result.Where(value => value.Activation == activation);
        if (query.SupportStatus is ArchiveSupportStatus support) result = result.Where(value => value.SupportStatus == support);
        return Task.FromResult(Page(result, query.Offset, query.PageSize, snapshotId));
    }

    public Task<EnvironmentPage<VirtualDataEntry>> QueryDataAsync(
        ResolvedSnapshotId snapshotId,
        VirtualDataQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RequireSnapshot(snapshotId);
        IEnumerable<VirtualDataEntry> result = snapshot.Data;
        if (!string.IsNullOrWhiteSpace(query.SearchText)) result = result.Where(value => value.VirtualPath.Contains(query.SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
        if (query.ConflictsOnly) result = result.Where(value => value.ProviderCount > 1);
        if (query.WinnerConfidence is ProviderWinnerConfidence confidence) result = result.Where(value => value.WinnerConfidence == confidence);
        return Task.FromResult(Page(result, query.Offset, query.PageSize, snapshotId));
    }

    public Task<ProviderChain?> GetProviderChainAsync(
        ResolvedSnapshotId snapshotId,
        VirtualPathId virtualPathId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RequireSnapshot(snapshotId);
        return Task.FromResult(snapshot.ProviderChains.GetValueOrDefault(virtualPathId));
    }

    public Mo2ResolvedBaselineSnapshot GetBaselineSnapshot(ResolvedSnapshotId snapshotId)
    {
        var snapshot = RequireSnapshot(snapshotId);
        return new(
            snapshot.PublicSnapshot,
            snapshot.Plugins,
            snapshot.Archives,
            snapshot.ProviderChains.Values
                .OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.VirtualPath, StringComparer.Ordinal)
                .ToImmutableArray(),
            snapshot.PhysicalFiles);
    }

    private async Task<(Mo2InstallationReference Reference, Mo2InstallationValidation Validation, Mo2ObservedProfile Profile, Mo2ModInventorySnapshot Inventory)?> ObserveContextAsync(
        WorkspaceEnvironmentContext context,
        bool includeInventory,
        CancellationToken cancellationToken)
    {
        var reference = await RequireReferenceAsync(context.InstallationId, cancellationToken).ConfigureAwait(false);
        var validation = await _validatorFactory(reference).ValidateAsync(
            new(Path.GetDirectoryName(reference.ExecutablePath), reference.InstanceDirectory, reference.GameId),
            cancellationToken).ConfigureAwait(false);
        if (validation.GameDirectory is null || validation.Status is Mo2ValidationStatus.Invalid or Mo2ValidationStatus.Inaccessible)
        {
            return null;
        }

        var snapshot = await _profiles.ObserveAsync(new(reference, validation), cancellationToken).ConfigureAwait(false);
        var profile = snapshot.Profiles.FirstOrDefault(value => value.Id == context.ProfileId);
        if (profile is null)
        {
            return null;
        }

        var inventory = includeInventory
            ? await _inventory.ObserveAsync(new(reference, validation, snapshot), cancellationToken).ConfigureAwait(false)
            : snapshot.ModInventory ?? new(
                reference.Id,
                string.Empty,
                Mo2InventoryObservationStatus.Unavailable,
                validation.ModsDirectory,
                default,
                [],
                [],
                null,
                []);
        var profileInventory = inventory.Profiles.FirstOrDefault(value => value.ProfileId == profile.Id);
        if (profileInventory is not null)
        {
            profile = profile with { Inventory = profileInventory };
        }

        return (reference, validation, profile, inventory);
    }

    private async Task<Mo2InstallationReference> RequireReferenceAsync(InstallationId installationId, CancellationToken cancellationToken)
    {
        var loaded = await _references.LoadAsync(cancellationToken).ConfigureAwait(false);
        return loaded.References.FirstOrDefault(reference => reference.InstallationId == installationId)
            ?? throw new InvalidDataException("The connected installation reference no longer exists.");
    }

    private ImmutableArray<Mo2ContentAuthorizationRequirement> RequiredRoots(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedProfile profile)
    {
        var builder = ImmutableArray.CreateBuilder<Mo2ContentAuthorizationRequirement>();
        if (validation.GameDirectory is not null)
        {
            builder.Add(new(Mo2ContentRootKind.GameDirectory, "Game directory", validation.GameDirectory));
            builder.Add(new(Mo2ContentRootKind.GameData, "Game Data", Path.Combine(validation.GameDirectory, "Data")));
        }

        if (validation.OverwriteDirectory is not null && !IsWithin(validation.OverwriteDirectory, reference.InstanceDirectory))
        {
            builder.Add(new(Mo2ContentRootKind.Overwrite, "External overwrite", validation.OverwriteDirectory));
        }

        if (profile.Observation.LocalSettingsEnabled == false)
        {
            builder.Add(new(Mo2ContentRootKind.GlobalGameSettings, "Global Skyrim settings", _globalSettingsDirectory));
        }

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<Mo2LooseProviderInput>> ObserveLooseProvidersAsync(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedProfile profile,
        Mo2ModInventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        var inputs = ImmutableArray.CreateBuilder<Mo2LooseProviderInput>();
        long remainingEntries = _limits.Content.MaximumEntries;
        if (validation.GameDirectory is not null)
        {
            var tree = await ObserveTree(
                reference.Id,
                Mo2ContentRootKind.GameData,
                Path.Combine(validation.GameDirectory, "Data"),
                remainingEntries,
                cancellationToken).ConfigureAwait(false);
            remainingEntries = Math.Max(0, remainingEntries - tree.Entries.Length);
            inputs.Add(new(
                VirtualProviderKind.BaseGameLooseFile,
                "Base game Data",
                null,
                0,
                tree));
        }

        var entries = profile.Inventory?.Entries ?? [];
        foreach (var mod in entries
            .Where(value => value.IsEnabled && value.Mo2Priority is not null && value.Directory is not null &&
                value.Reconciliation == Mo2ModReconciliationState.Matched)
            .OrderBy(value => value.Mo2Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tree = await ObserveTree(
                reference.Id,
                Mo2ContentRootKind.Mod,
                mod.Directory!.CanonicalPath,
                remainingEntries,
                cancellationToken).ConfigureAwait(false);
            remainingEntries = Math.Max(0, remainingEntries - tree.Entries.Length);
            inputs.Add(new(
                VirtualProviderKind.ModLooseFile,
                SafeDisplay(mod.Name),
                mod.Id,
                checked(1 + mod.Mo2Priority!.Value),
                tree));
        }

        if (validation.OverwriteDirectory is not null)
        {
            var tree = await ObserveTree(
                reference.Id,
                Mo2ContentRootKind.Overwrite,
                validation.OverwriteDirectory,
                remainingEntries,
                cancellationToken).ConfigureAwait(false);
            inputs.Add(new(
                VirtualProviderKind.OverwriteLooseFile,
                "MO2 overwrite",
                null,
                int.MaxValue,
                tree));
        }

        return inputs.ToImmutable();
    }

    private Task<Mo2ContentTreeObservation> ObserveTree(
        InstallationReferenceId referenceId,
        Mo2ContentRootKind kind,
        string path,
        long remainingEntries,
        CancellationToken cancellationToken) =>
        remainingEntries <= 0
            ? Task.FromResult(new Mo2ContentTreeObservation(
                kind,
                string.Empty,
                Mo2PathState.Unavailable,
                [],
                string.Empty,
                true,
                [new("mo2.content.aggregate_entry_limit", Mo2IssueSeverity.Warning, "The aggregate physical-entry safety limit was reached.")]))
            : _trees.ObserveAsync(
                new(referenceId, kind, path, _limits.Content with { MaximumEntries = remainingEntries }),
                cancellationToken);

    private static Dictionary<string, (Mo2LooseProviderInput Provider, Mo2ContentTreeEntry Entry)> BuildLooseWinners(
        ImmutableArray<Mo2LooseProviderInput> inputs)
    {
        var winners = new Dictionary<string, (Mo2LooseProviderInput, Mo2ContentTreeEntry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs.OrderBy(value => value.Precedence))
        {
            foreach (var file in input.Tree.Entries.Where(value => value.Kind == Mo2ContentEntryKind.File))
            {
                winners[file.VirtualPath] = (input, file);
            }
        }

        return winners;
    }

    private async Task<ImmutableArray<PluginEntry>> BuildPluginsAsync(
        Mo2ObservedProfile profile,
        CreationManifestObservation creationManifest,
        Dictionary<string, (Mo2LooseProviderInput Provider, Mo2ContentTreeEntry Entry)> winners,
        IProgress<ResolvedEnvironmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var pluginFiles = winners
            .Where(pair => IsPlugin(pair.Key) && !pair.Key.Contains('\\'))
            .ToDictionary(pair => Path.GetFileName(pair.Key), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var entry in profile.LoadOrder?.Entries ?? []) AddUnique(names, entry.Name);
        foreach (var entry in profile.PluginStates?.Entries ?? []) AddUnique(names, entry.Name);
        foreach (var name in creationManifest.Entries) AddUnique(names, name);
        foreach (var name in pluginFiles.Keys.Order(StringComparer.OrdinalIgnoreCase)) AddUnique(names, name);
        if (names.Count > _limits.MaximumPlugins)
        {
            names = names.Take(_limits.MaximumPlugins).ToList();
        }

        var headers = new ConcurrentDictionary<string, Mo2PluginHeaderSnapshot>(StringComparer.OrdinalIgnoreCase);
        await RunFixedWorkersAsync(
            pluginFiles.Where(pair => names.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToArray(),
            async pair =>
            {
                await using var file = await _files.OpenReadAsync(pair.Value.Entry.CanonicalPath, cancellationToken).ConfigureAwait(false);
                if (!_paths.IsWithinRoot(file.InitialStamp.CanonicalPath, pair.Value.Provider.Tree.CanonicalRoot))
                {
                    headers[pair.Key] = new(
                        Mo2BinaryObservationStatus.Inaccessible,
                        Mo2PluginExtensionKind.Unsupported,
                        null,
                        null,
                        [],
                        null,
                        null,
                        file.InitialStamp,
                        file.InitialStamp,
                        [new("mo2.plugin.handle_escape", "The opened plugin resolved outside its authorized provider root.")]);
                    return;
                }

                headers[pair.Key] = await _pluginParser.ParseAsync(file, pair.Key, _limits.PluginHeaders, cancellationToken).ConfigureAwait(false);
            },
            progress,
            EnvironmentObservationStage.ParsingPlugins,
            cancellationToken).ConfigureAwait(false);

        var states = (profile.PluginStates?.Entries ?? []).GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var order = (profile.LoadOrder?.Entries ?? []).Select((value, index) => (value.Name, Index: index))
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Index, StringComparer.OrdinalIgnoreCase);
        var preliminary = names.Select((name, index) =>
        {
            var hasFile = pluginFiles.TryGetValue(name, out var physical);
            headers.TryGetValue(name, out var header);
            var implicitCreation = creationManifest.Status == CreationManifestStatus.Complete &&
                creationManifest.Entries.Contains(name, StringComparer.OrdinalIgnoreCase);
            var implicitCore = ImplicitCorePlugins.Contains(name) && hasFile && !states.ContainsKey(name);
            var enabled = implicitCreation || (states.TryGetValue(name, out var state) ? state.IsEnabled : implicitCore);
            var activation = implicitCreation
                ? PluginActivationProvenance.CreationManifestImplicit
                : implicitCore
                    ? PluginActivationProvenance.CoreGameImplicit
                    : enabled || order.ContainsKey(name)
                        ? PluginActivationProvenance.PluginsFileMarker
                        : PluginActivationProvenance.NotListed;
            var sourcePriority = order.GetValueOrDefault(name, -1);
            return new PendingPlugin(name, index, enabled, activation, sourcePriority >= 0 ? sourcePriority : null, hasFile ? physical.Provider.SourceName : null, header);
        }).ToArray();
        var enabledOrder = preliminary.Where(value => value.Enabled && value.SourcePriority is not null)
            .OrderBy(value => value.SourcePriority)
            .Select((value, index) => (value.Name, Index: index))
            .ToDictionary(value => value.Name, value => value.Index, StringComparer.OrdinalIgnoreCase);
        var byName = preliminary.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        return preliminary.Select(value => ToPlugin(value, enabledOrder, byName)).ToImmutableArray();
    }

    private static PluginEntry ToPlugin(
        PendingPlugin value,
        Dictionary<string, int> enabledOrder,
        Dictionary<string, PendingPlugin> byName)
    {
        var header = value.Header;
        var id = StablePluginId(value.Name);
        var masters = header?.Masters.Select(master =>
        {
            var status = byName.TryGetValue(master.Name, out var target)
                ? target.Enabled ? PluginMasterStatus.PresentEnabled : PluginMasterStatus.PresentDisabled
                : PluginMasterStatus.Missing;
            return new PluginMasterReference(
                SafeDisplay(master.Name),
                master.SourceOrder,
                status,
                byName.ContainsKey(master.Name) ? StablePluginId(master.Name) : null);
        }).ToImmutableArray() ?? [];
        var warnings = header?.Warnings.Select(warning => warning.Message).ToImmutableArray() ?? [];
        var availability = header?.Status switch
        {
            Mo2BinaryObservationStatus.Complete => PluginFileAvailability.Present,
            Mo2BinaryObservationStatus.Inaccessible => PluginFileAvailability.Inaccessible,
            Mo2BinaryObservationStatus.Oversized => PluginFileAvailability.Oversized,
            Mo2BinaryObservationStatus.ChangedDuringRead => PluginFileAvailability.ChangedDuringRead,
            Mo2BinaryObservationStatus.Malformed => PluginFileAvailability.Malformed,
            _ when header is null => PluginFileAvailability.Missing,
            _ => PluginFileAvailability.Unknown,
        };
        var health = availability == PluginFileAvailability.Present && masters.All(master => master.Status != PluginMasterStatus.Missing)
            ? HealthLevel.Healthy
            : HealthLevel.Warning;
        return new(
            id,
            SafeDisplay(value.Name),
            value.Enabled,
            enabledOrder.GetValueOrDefault(value.Name, -1) is var load && load >= 0 ? load : null,
            health,
            value.SourcePriority,
            new(
                value.SourcePriority,
                header?.Extension switch
                {
                    Mo2PluginExtensionKind.Esm => PluginFileExtension.Esm,
                    Mo2PluginExtensionKind.Esp => PluginFileExtension.Esp,
                    Mo2PluginExtensionKind.Esl => PluginFileExtension.Esl,
                    _ => PluginFileExtension.Unknown,
                },
                header?.HasMasterFlag,
                header?.HasLightFlag,
                value.ActivationProvenance,
                value.SourcePriority is not null ? PluginOrderProvenance.LoadOrderFile : PluginOrderProvenance.DiscoveredUnordered,
                availability,
                value.Provider,
                header?.ContentFingerprint ?? string.Empty,
                DateTimeOffset.UtcNow,
                masters,
                warnings));
    }

    private async Task<ImmutableArray<PendingArchive>> BuildArchivesAsync(
        Mo2ObservedProfile profile,
        ImmutableArray<string> effectiveArchiveNames,
        Dictionary<string, (Mo2LooseProviderInput Provider, Mo2ContentTreeEntry Entry)> winners,
        ImmutableArray<PluginEntry> plugins,
        IProgress<ResolvedEnvironmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = winners.Where(pair => IsArchive(pair.Key) && !pair.Key.Contains('\\'))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(_limits.MaximumArchives)
            .ToArray();
        var iniNames = effectiveArchiveNames;
        var iniOrder = iniNames.Select((name, index) => (name, index))
            .GroupBy(value => value.name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.OrdinalIgnoreCase);
        var enabledPlugins = plugins.Where(plugin => plugin.IsEnabled).ToArray();
        var activeCandidates = candidates
            .Where(pair => iniOrder.ContainsKey(Path.GetFileName(pair.Key)) ||
                enabledPlugins.Any(plugin => IsAssociated(pair.Key, plugin.Name)))
            .OrderBy(pair => iniOrder.TryGetValue(Path.GetFileName(pair.Key), out var resourceOrder)
                ? resourceOrder
                : int.MaxValue)
            .ThenBy(pair => enabledPlugins.FirstOrDefault(plugin => IsAssociated(pair.Key, plugin.Name))?.LoadOrder ?? int.MaxValue)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var parsed = new ConcurrentDictionary<string, Mo2ArchiveIndexSnapshot>(StringComparer.OrdinalIgnoreCase);
        long reservedArchiveBytes = 0;
        await RunFixedWorkersAsync(
            activeCandidates,
            async pair =>
            {
                await using var file = await _files.OpenReadAsync(pair.Value.Entry.CanonicalPath, cancellationToken).ConfigureAwait(false);
                if (!_paths.IsWithinRoot(file.InitialStamp.CanonicalPath, pair.Value.Provider.Tree.CanonicalRoot))
                {
                    parsed[pair.Key] = new(
                        Mo2BinaryObservationStatus.Inaccessible,
                        Mo2ArchiveFormat.Unknown,
                        Mo2ArchiveSupport.Malformed,
                        null,
                        null,
                        null,
                        [],
                        null,
                        file.InitialStamp,
                        file.InitialStamp,
                        [new("mo2.archive.handle_escape", "The opened archive resolved outside its authorized provider root.")]);
                    return;
                }

                var observed = await _archiveParser.ParseAsync(file, _limits.Archives, cancellationToken).ConfigureAwait(false);
                if (observed.IndexBytesRead > 0 &&
                    !TryReserve(ref reservedArchiveBytes, observed.IndexBytesRead, _limits.MaximumArchiveIndexBytes))
                {
                    parsed[pair.Key] = observed with
                    {
                        Status = Mo2BinaryObservationStatus.Oversized,
                        Support = Mo2ArchiveSupport.Malformed,
                        Members = [],
                        ContentFingerprint = null,
                        Warnings = observed.Warnings.Add(new(
                            "mo2.archive.aggregate_byte_limit",
                            "The aggregate retained archive-index byte budget was reached.")),
                    };
                    return;
                }

                parsed[pair.Key] = observed;
            },
            progress,
            EnvironmentObservationStage.ParsingArchives,
            cancellationToken).ConfigureAwait(false);
        var builder = ImmutableArray.CreateBuilder<PendingArchive>();
        long totalMembers = 0;
        foreach (var pair in candidates)
        {
            parsed.TryGetValue(pair.Key, out var index);
            var associated = enabledPlugins.FirstOrDefault(plugin => IsAssociated(pair.Key, plugin.Name));
            var iniActive = iniOrder.TryGetValue(Path.GetFileName(pair.Key), out var resourceOrder);
            var active = iniActive || associated is not null;
            var status = ToArchiveSupport(index);
            var members = index?.Support == Mo2ArchiveSupport.SupportedIndex && active
                ? index.Members
                : [];
            if (totalMembers + members.Length > _limits.MaximumTotalArchiveMembers)
            {
                members = [];
                status = ArchiveSupportStatus.Malformed;
            }
            else
            {
                totalMembers += members.Length;
            }

            var id = new ArchiveId($"archive.{Mo2VirtualDataResolver.Hash(pair.Key)[..24]}");
            var entry = new ResolvedArchiveEntry(
                id,
                SafeDisplay(Path.GetFileName(pair.Key)),
                index?.Format switch { Mo2ArchiveFormat.Bsa => ArchiveFormat.Bsa, Mo2ArchiveFormat.Ba2 => ArchiveFormat.Ba2, _ => ArchiveFormat.Unknown },
                index?.Version is uint version ? checked((int)version) : null,
                status,
                active ? ArchiveActivationState.Active : ArchiveActivationState.Inactive,
                iniActive ? ArchiveActivationProvenance.IniResourceList :
                    associated is not null ? ArchiveActivationProvenance.EnabledPluginAssociation : ArchiveActivationProvenance.NoActivationEvidence,
                associated?.Id,
                SafeDisplay(pair.Value.Provider.SourceName),
                index?.Members.Length,
                index?.ContentFingerprint ?? string.Empty,
                DateTimeOffset.UtcNow,
                index?.Warnings.Select(warning => warning.Message).ToImmutableArray() ?? []);
            var orderEstablished = iniActive || associated?.LoadOrder is not null;
            var archiveOrder = iniActive
                ? resourceOrder
                : associated?.LoadOrder is int pluginOrder
                    ? checked(iniNames.Length + pluginOrder)
                    : pair.Value.Provider.Precedence;
            builder.Add(new(entry, members, archiveOrder,
                active && index?.Status != Mo2BinaryObservationStatus.Complete,
                orderEstablished));
        }

        return builder.ToImmutable();
    }

    private async Task<ImmutableArray<string>> ObserveEffectiveArchiveListAsync(
        Mo2ObservedProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.Observation.LocalSettingsEnabled != false)
        {
            return profile.Sources.FirstOrDefault(source =>
                    source.Name.Equals("Skyrim.ini", StringComparison.OrdinalIgnoreCase))?.RawDocument is { } localIni
                ? Mo2ProfileParsers.ParseArchiveLists(localIni).ArchiveNames
                : [];
        }

        var path = Path.Combine(_globalSettingsDirectory, "Skyrim.ini");
        if (_readOnlyFileSystem.ProbeFile(path) != Mo2PathState.Present)
        {
            return [];
        }

        var read = await _readOnlyFileSystem.ReadBytesWithMetadataAsync(
            path,
            4 * 1024 * 1024,
            cancellationToken).ConfigureAwait(false);
        if (read.Before != read.After)
        {
            return [];
        }

        var document = _textDecoder.Decode(read.Bytes, Mo2TextDecodingPolicy.IniBomUtf8SystemFallback);
        return Mo2ProfileParsers.ParseArchiveLists(document).ArchiveNames;
    }

    private static IEnumerable<EnvironmentDiscrepancy> BuildDiscrepancies(
        CreationManifestObservation creationManifest,
        ImmutableArray<Mo2LooseProviderInput> loose,
        ImmutableArray<PluginEntry> plugins,
        ImmutableArray<PendingArchive> archives,
        Mo2VirtualDataResolution data)
    {
        foreach (var issue in data.Discrepancies) yield return issue;
        if (creationManifest.Status is not CreationManifestStatus.Complete and not CreationManifestStatus.Missing)
        {
            var kind = creationManifest.Status switch
            {
                CreationManifestStatus.Malformed => EnvironmentDiscrepancyKind.MalformedInput,
                CreationManifestStatus.Oversized => EnvironmentDiscrepancyKind.SafetyLimitExceeded,
                CreationManifestStatus.ChangedDuringRead => EnvironmentDiscrepancyKind.ChangedDuringRead,
                _ => EnvironmentDiscrepancyKind.InaccessibleInput,
            };
            yield return Discrepancy(
                "creation-manifest",
                kind,
                "Creation plugin activation evidence is incomplete",
                creationManifest.Detail);
        }
        foreach (var tree in loose.Where(value => value.Tree.IsPartial))
        {
            yield return Discrepancy("tree:" + tree.SourceName, EnvironmentDiscrepancyKind.InaccessibleInput, "Provider tree is partial", $"{SafeDisplay(tree.SourceName)} could not be observed completely.");
        }

        foreach (var plugin in plugins.Where(value => value.Observation?.FileAvailability != PluginFileAvailability.Present))
        {
            yield return Discrepancy("plugin:" + plugin.Name, EnvironmentDiscrepancyKind.MissingEvidence, "Plugin file evidence is incomplete", $"{plugin.Name}: {plugin.Observation?.FileAvailability}.");
        }

        foreach (var plugin in plugins.Where(value => value.Observation?.Masters.Any(master => master.Status is PluginMasterStatus.Missing or PluginMasterStatus.PresentDisabled) == true))
        {
            yield return Discrepancy("masters:" + plugin.Name, EnvironmentDiscrepancyKind.ConflictingEvidence, "Plugin master state requires attention", $"{plugin.Name} has a missing or disabled master. Source order was not corrected.");
        }

        foreach (var archive in archives.Where(value => value.Entry.SupportStatus != ArchiveSupportStatus.Supported))
        {
            yield return Discrepancy("archive:" + archive.Entry.Name, EnvironmentDiscrepancyKind.UnsupportedFormat, "Archive cannot contribute a confident index", $"{archive.Entry.Name}: {archive.Entry.SupportStatus}.");
        }
    }

    private static EnvironmentDiscrepancy Discrepancy(string key, EnvironmentDiscrepancyKind kind, string title, string detail) =>
        new(new($"discrepancy.{Mo2VirtualDataResolver.Hash(key)[..24]}"), kind, EnvironmentDiscrepancySeverity.Warning, title, detail);

    private string EvidenceFingerprint(
        WorkspaceEnvironmentContext context,
        Mo2InstallationValidation validation,
        Mo2ObservedProfile profile,
        Mo2ModInventorySnapshot inventory,
        CreationManifestObservation creationManifest,
        ImmutableArray<Mo2LooseProviderInput> loose,
        ImmutableArray<PluginEntry> plugins,
        ImmutableArray<PendingArchive> archives) =>
        Mo2VirtualDataResolver.Fingerprint(
            context.GameId.Value,
            context.InstallationId.Value,
            context.ProfileId.Value,
            validation.ConnectionKey,
            profile.Observation.SnapshotFingerprint,
            inventory.Revision,
            creationManifest.Fingerprint,
            string.Join('|', loose.Select(value => value.Tree.MembershipFingerprint)),
            string.Join('|', plugins.Select(value => $"{value.Id.Value}:{value.IsEnabled}:{value.LoadOrder}:{value.Observation?.ActivationProvenance}:{value.Observation?.Fingerprint}")),
            string.Join('|', archives.Select(value => value.Entry.Fingerprint)),
            "grid.mo2.resolved.v2",
            _limits.ToString());

    private async Task<CreationManifestObservation> ObserveCreationManifestAsync(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        CancellationToken cancellationToken)
    {
        if (validation.GameDirectory is null ||
            !_authorization.IsRootAuthorized(reference.Id, Mo2ContentRootKind.GameDirectory, validation.GameDirectory))
        {
            return new(CreationManifestStatus.Inaccessible, [], string.Empty,
                "The exact game directory was not authorized for bounded Skyrim.ccc observation.");
        }

        var path = Path.Combine(validation.GameDirectory, "Skyrim.ccc");
        if (!_paths.TryCanonicalize(validation.GameDirectory, out var canonicalRoot, out _) ||
            !_paths.TryCanonicalize(path, out var canonicalPath, out _) ||
            !_paths.IsWithinRoot(canonicalPath, canonicalRoot))
        {
            return new(CreationManifestStatus.Inaccessible, [], string.Empty,
                "Skyrim.ccc could not be resolved within the authorized game directory.");
        }

        var metadata = _readOnlyFileSystem.GetFileMetadata(canonicalPath);
        if (metadata.State == Mo2PathState.Missing)
        {
            return new(CreationManifestStatus.Missing, [], "missing", "Skyrim.ccc is absent; no Creation manifest activation was inferred.");
        }
        if (metadata.State != Mo2PathState.Present || metadata.Stamp is null)
        {
            return new(CreationManifestStatus.Inaccessible, [], string.Empty,
                $"Skyrim.ccc could not be read ({metadata.State}).");
        }
        if (metadata.Stamp.Value.Length > MaximumCreationManifestBytes)
        {
            return new(CreationManifestStatus.Oversized, [], string.Empty,
                $"Skyrim.ccc exceeds the {MaximumCreationManifestBytes:N0}-byte safety limit.");
        }

        try
        {
            var read = await _readOnlyFileSystem.ReadBytesWithMetadataAsync(
                canonicalPath,
                MaximumCreationManifestBytes,
                cancellationToken).ConfigureAwait(false);
            var fingerprint = Convert.ToHexString(SHA256.HashData(read.Bytes.AsSpan())).ToLowerInvariant();
            if (read.Before != read.After)
            {
                return new(CreationManifestStatus.ChangedDuringRead, [], fingerprint,
                    "Skyrim.ccc changed during the bounded read; no Creation activation was inferred.");
            }

            Mo2RawTextDocument document;
            try
            {
                document = _textDecoder.Decode(read.Bytes, Mo2TextDecodingPolicy.StrictUtf8);
            }
            catch (InvalidDataException)
            {
                return new(CreationManifestStatus.Malformed, [], fingerprint,
                    "Skyrim.ccc is not valid UTF-8; no Creation activation was inferred.");
            }

            var entries = ImmutableArray.CreateBuilder<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in document.Lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = line.Text.Trim();
                if (name.Length == 0)
                {
                    continue;
                }
                if (name.Length > 260 || name.Any(char.IsControl) ||
                    name.Contains('/') || name.Contains('\\') || name.Contains(':') ||
                    !Path.GetFileName(name).Equals(name, StringComparison.Ordinal) || !IsPlugin(name) ||
                    !seen.Add(name))
                {
                    return new(CreationManifestStatus.Malformed, [], fingerprint,
                        $"Skyrim.ccc contains an invalid or duplicate plugin entry at line {line.Index + 1}; no Creation activation was inferred.");
                }
                if (entries.Count >= MaximumCreationManifestEntries)
                {
                    return new(CreationManifestStatus.Oversized, [], fingerprint,
                        $"Skyrim.ccc exceeds the {MaximumCreationManifestEntries:N0}-entry safety limit.");
                }
                entries.Add(name);
            }

            return new(CreationManifestStatus.Complete, entries.ToImmutable(), fingerprint,
                $"Skyrim.ccc supplied {entries.Count:N0} authoritative Creation plugin activation entries.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(CreationManifestStatus.Inaccessible, [], string.Empty,
                $"Skyrim.ccc could not be read safely ({exception.GetType().Name}).");
        }
    }

    private static PluginId StablePluginId(string name) =>
        new($"plugin.{Mo2VirtualDataResolver.Hash(name.ToUpperInvariant())[..24]}");

    private Mo2ResolvedEnvironmentData RequireSnapshot(ResolvedSnapshotId id) =>
        _cache.TryGet(id, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException("The resolved environment snapshot is expired or unavailable.");

    private EnvironmentPage<T> Page<T>(IEnumerable<T> source, int offset, int pageSize, ResolvedSnapshotId id)
    {
        if (offset < 0 || pageSize <= 0 || pageSize > _limits.MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Pages must contain 1-{_limits.MaximumPageSize} items.");
        }

        var materialized = source.ToImmutableArray();
        return new(materialized.Skip(offset).Take(pageSize).ToImmutableArray(), offset, materialized.Length, pageSize, id);
    }

    private async Task RunFixedWorkersAsync<T>(
        IReadOnlyCollection<T> items,
        Func<T, Task> action,
        IProgress<ResolvedEnvironmentProgress>? progress,
        EnvironmentObservationStage stage,
        CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
        });
        long completed = 0;
        var workers = Enumerable.Range(0, 4).Select(async _ =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await action(item).ConfigureAwait(false);
                var current = Interlocked.Increment(ref completed);
                if ((current & 0x3f) == 0 || current == items.Count)
                {
                    progress?.Report(new(stage, current, items.Count, $"Observed {current:N0} of {items.Count:N0} bounded binary indexes."));
                }
            }
        }).ToArray();
        foreach (var item in items)
        {
            await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }

        channel.Writer.Complete();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private static bool IsWithin(string path, string root)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPlugin(string path) => Path.GetExtension(path).ToLowerInvariant() is ".esm" or ".esp" or ".esl";
    private static bool IsArchive(string path) => Path.GetExtension(path).ToLowerInvariant() is ".bsa" or ".ba2";
    private static bool IsAssociated(string archive, string plugin)
    {
        var archiveStem = Path.GetFileNameWithoutExtension(archive);
        var pluginStem = Path.GetFileNameWithoutExtension(plugin);
        return archiveStem.Equals(pluginStem, StringComparison.OrdinalIgnoreCase) ||
            archiveStem.StartsWith(pluginStem + ".", StringComparison.OrdinalIgnoreCase) ||
            archiveStem.StartsWith(pluginStem + " - ", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddUnique(List<string> names, string value)
    {
        if (!names.Contains(value, StringComparer.OrdinalIgnoreCase)) names.Add(value);
    }

    private static bool TryReserve(ref long counter, long amount, long limit)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (amount < 0 || current > limit - amount)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current + amount, current) == current)
            {
                return true;
            }
        }
    }

    private static string SafeDisplay(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            ? "Unsupported name"
            : value.Length <= 260 ? value : value[..260];

    private static ArchiveSupportStatus ToArchiveSupport(Mo2ArchiveIndexSnapshot? index) => index switch
    {
        null => ArchiveSupportStatus.Inaccessible,
        { Status: Mo2BinaryObservationStatus.ChangedDuringRead } => ArchiveSupportStatus.ChangedDuringRead,
        { Status: Mo2BinaryObservationStatus.Inaccessible } => ArchiveSupportStatus.Inaccessible,
        { Format: Mo2ArchiveFormat.Ba2 } => ArchiveSupportStatus.UnsupportedFormat,
        { Support: Mo2ArchiveSupport.UnsupportedVersion } => ArchiveSupportStatus.UnsupportedVersion,
        { Support: Mo2ArchiveSupport.UnsupportedFormat } => ArchiveSupportStatus.UnsupportedFormat,
        { Support: Mo2ArchiveSupport.Malformed } => ArchiveSupportStatus.Malformed,
        _ => ArchiveSupportStatus.Supported,
    };

    private sealed record PendingPlugin(
        string Name,
        int DisplayIndex,
        bool Enabled,
        PluginActivationProvenance ActivationProvenance,
        int? SourcePriority,
        string? Provider,
        Mo2PluginHeaderSnapshot? Header);

    private enum CreationManifestStatus
    {
        Complete,
        Missing,
        Malformed,
        Inaccessible,
        Oversized,
        ChangedDuringRead,
    }

    private sealed record CreationManifestObservation(
        CreationManifestStatus Status,
        ImmutableArray<string> Entries,
        string Fingerprint,
        string Detail)
    {
        public bool IsPartial => Status is not CreationManifestStatus.Complete and not CreationManifestStatus.Missing;
    }

    private sealed record PendingArchive(
        ResolvedArchiveEntry Entry,
        ImmutableArray<Mo2ArchiveMember> Members,
        int Precedence,
        bool Partial,
        bool OrderEstablished)
    {
        public Mo2ArchiveProviderInput Provider => new(Entry, Members, Precedence, OrderEstablished);
    }
}
