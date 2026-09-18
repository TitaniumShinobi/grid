using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Application;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2CatalogService(
    IGridCatalogService inner,
    IMo2InstallationReferenceStore referenceStore,
    Func<Mo2InstallationReference, IMo2InstallationValidator> validatorFactory,
    IMo2ProfileSnapshotService? profileSnapshotService = null,
    IMo2ModInventoryService? modInventoryService = null,
    Mo2ResolvedStateCache? resolvedStateCache = null,
    Mo2WorkspaceToolOutputQueryService? toolOutputService = null,
    bool enforceProductionIsolation = false) : IGridCatalogService
{
    public async Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalogTask = inner.GetCatalogAsync(cancellationToken);
        var referencesTask = referenceStore.LoadAsync(cancellationToken);
        await Task.WhenAll(catalogTask, referencesTask).ConfigureAwait(false);
        var catalog = await catalogTask.ConfigureAwait(false);
        if (enforceProductionIsolation)
        {
            EnsureProductionBase(catalog);
        }
        var loaded = await referencesTask.ConfigureAwait(false);
        if (loaded.References.IsEmpty)
        {
            return loaded.Issues.Any(issue => issue.Severity == Mo2IssueSeverity.Error)
                ? AddReferenceStoreAdvisory(catalog, loaded.Issues)
                : catalog;
        }

        var games = catalog.Games.ToBuilder();
        var revisionEvidence = ImmutableArray.CreateBuilder<string>();
        for (var gameIndex = 0; gameIndex < games.Count; gameIndex++)
        {
            var game = games[gameIndex];
            var references = loaded.References.Where(reference => reference.GameId == game.Id).ToArray();
            if (references.Length == 0)
            {
                continue;
            }

            var installations = game.Installations.ToBuilder();
            var adapters = game.Adapters.ToBuilder();
            foreach (var reference in references)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!adapters.Any(adapter => adapter.Id == reference.AdapterId))
                    {
                        adapters.Add(new(reference.AdapterId, "Mod Organizer 2 · Read only"));
                    }

                    var validation = await validatorFactory(reference).ValidateAsync(
                        new(Path.GetDirectoryName(reference.ExecutablePath), reference.InstanceDirectory, reference.GameId),
                        cancellationToken).ConfigureAwait(false);
                    var profileSnapshot = profileSnapshotService is null
                        ? null
                        : await profileSnapshotService.ObserveAsync(
                            new(reference, validation),
                            cancellationToken).ConfigureAwait(false);
                    Mo2ModInventorySnapshot? inventorySnapshot = null;
                    if (profileSnapshot is not null && modInventoryService is not null)
                    {
                        inventorySnapshot = await modInventoryService.ObserveAsync(
                            new(reference, validation, profileSnapshot),
                            cancellationToken).ConfigureAwait(false);
                        profileSnapshot = AttachInventory(profileSnapshot, inventorySnapshot);
                    }

                    var toolOutputSnapshots = new Dictionary<ProfileId, ToolOutputObservationSnapshot>();
                    if (profileSnapshot is not null && toolOutputService is not null)
                    {
                        foreach (var profile in profileSnapshot.Profiles)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var snapshot = toolOutputService.FindLatest(reference.InstallationId, profile.Id);
                            if (snapshot is not null)
                            {
                                toolOutputSnapshots[profile.Id] = snapshot;
                                revisionEvidence.Add($"{profile.Id.Value}:tools:{snapshot.Summary.Fingerprint}");
                            }
                        }
                    }

                    var observationStates = string.Join(
                        ',',
                        validation.Paths.Select(observation => $"{observation.Label}:{observation.State}"));
                    var issueStates = string.Join(
                        ',',
                        validation.Issues.Select(issue =>
                            $"{issue.Code}:{issue.Severity}:{issue.Message}:{issue.PathLabel}"));
                    revisionEvidence.Add(
                        $"{reference.Id.Value}:{reference.InstallationId.Value}:{reference.GameId.Value}:" +
                        $"{reference.AdapterId.Value}:{reference.DisplayName}:{reference.InstanceKind}:" +
                        $"{validation.Status}:{validation.SelectionKind}:{validation.InstanceKind}:" +
                        $"{observationStates}:{issueStates}:{profileSnapshot?.Status}:{profileSnapshot?.Revision}:" +
                        $"{inventorySnapshot?.Status}:{inventorySnapshot?.Revision}");
                    var resolvedEvidence = profileSnapshot?.Profiles
                        .Select(profile => resolvedStateCache?.FindProfileSummary(reference.InstallationId, profile.Id)?.Fingerprint)
                        .Where(fingerprint => fingerprint is not null)
                        .Order(StringComparer.Ordinal)
                        .ToArray() ?? [];
                    revisionEvidence.Add(string.Join('|', resolvedEvidence));
                    installations.Add(CreateInstallation(reference, validation, profileSnapshot, resolvedStateCache, toolOutputSnapshots));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    var failed = FailedValidation(reference, exception);
                    revisionEvidence.Add($"{reference.Id.Value}:isolated:{exception.GetType().Name}");
                    installations.Add(CreateInstallation(reference, failed, null, resolvedStateCache,
                        new Dictionary<ProfileId, ToolOutputObservationSnapshot>()));
                }
            }

            games[gameIndex] = game with
            {
                Adapters = adapters.ToImmutable(),
                Installations = installations.ToImmutable(),
            };
        }

        var revisionInput = string.Join('\n', revisionEvidence.Order(StringComparer.Ordinal));
        var revisionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput)))
            .ToLowerInvariant()[..16];
        return new GridCatalogSnapshot(
            $"{catalog.Revision}+mo2.{revisionHash}",
            catalog.SourceKind == CatalogSourceKind.Mock ? CatalogSourceKind.Mixed : CatalogSourceKind.Adapter,
            games);
    }

    private static void EnsureProductionBase(GridCatalogSnapshot catalog)
    {
        if (catalog.SourceKind == CatalogSourceKind.Mock ||
            catalog.Games.SelectMany(game => game.Installations).Any(installation =>
                installation.Metadata.Provenance == InstallationProvenanceKind.Mock))
        {
            throw new InvalidOperationException("The production MO2 catalog cannot wrap or mix deterministic mock data.");
        }
    }

    private static Mo2InstallationValidation FailedValidation(Mo2InstallationReference reference, Exception exception) => new(
        Mo2ValidationStatus.Inaccessible,
        reference.InstanceKind == Mo2InstanceKind.Portable ? Mo2SelectionKind.PortableInstance : Mo2SelectionKind.GlobalInstance,
        false,
        reference.InstanceKind,
        Path.GetDirectoryName(reference.ExecutablePath),
        reference.ExecutablePath,
        reference.InstanceDirectory,
        Path.Combine(reference.InstanceDirectory, "ModOrganizer.ini"),
        null, null, null, null, null, null, null, null,
        [],
        [new("mo2.reference.observation_failed", Mo2IssueSeverity.Error,
            $"This connected reference failed independently during observation ({exception.GetType().Name}).")]);

    private static GridCatalogSnapshot AddReferenceStoreAdvisory(
        GridCatalogSnapshot catalog,
        ImmutableArray<Mo2ValidationIssue> issues)
    {
        var issueStates = issues
            .Where(issue => issue.Severity == Mo2IssueSeverity.Error)
            .Select(issue => $"{issue.Code}:{issue.Severity}:{issue.Message}:{issue.PathLabel}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var games = catalog.Games
            .Select(game => game.Adapters.Any(adapter =>
                adapter.Id.Value.Equals("adapter.mod-organizer-2", StringComparison.Ordinal))
                ? game with
                {
                    Health = new(
                        HealthLevel.Warning,
                        "MO2 connection references unavailable",
                        game.Health.Advisories.AddRange(issues.Select(issue => new Advisory(
                            issue.Code,
                            "MO2 connection references unavailable",
                            issue.Message,
                            HealthLevel.Warning)))),
                }
                : game)
            .ToImmutableArray();
        var revisionInput = string.Join('\n', issueStates);
        var revisionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput)))
            .ToLowerInvariant()[..16];
        return new GridCatalogSnapshot(
            $"{catalog.Revision}+mo2.store.{revisionHash}",
            catalog.SourceKind == CatalogSourceKind.Mock ? CatalogSourceKind.Mixed : CatalogSourceKind.Adapter,
            games);
    }

    private static ManagedInstallation CreateInstallation(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ProfileSnapshot? profileSnapshot,
        Mo2ResolvedStateCache? resolvedStateCache,
        IReadOnlyDictionary<ProfileId, ToolOutputObservationSnapshot> toolOutputSnapshots)
    {
        var requiresConfiguredPathAuthorization =
            validation.Paths.Any(path => path.State == Mo2PathState.AuthorizationRequired);
        var hasNonAuthorizationErrors = validation.Issues.Any(issue =>
            issue.Severity == Mo2IssueSeverity.Error &&
            issue.Code != "mo2.path.authorization_required");
        var authorizationOnly = requiresConfiguredPathAuthorization && !hasNonAuthorizationErrors;
        var availability = validation.Status switch
        {
            Mo2ValidationStatus.Valid => InstallationAvailability.Available,
            _ when authorizationOnly => InstallationAvailability.Available,
            Mo2ValidationStatus.Inaccessible => InstallationAvailability.Unavailable,
            _ => InstallationAvailability.Missing,
        };
        var level = validation.Status switch
        {
            Mo2ValidationStatus.Valid => HealthLevel.Unknown,
            _ when authorizationOnly => HealthLevel.Advisory,
            Mo2ValidationStatus.Incomplete => HealthLevel.Advisory,
            _ => HealthLevel.Warning,
        };
        if (profileSnapshot?.Status == ProfileObservationStatus.Inconsistent)
        {
            level = HealthLevel.Warning;
        }
        else if (profileSnapshot?.Status is ProfileObservationStatus.Partial or ProfileObservationStatus.Unavailable &&
            level == HealthLevel.Unknown)
        {
            level = HealthLevel.Advisory;
        }
        var profiles = profileSnapshot?.Profiles
            .OrderBy(profile => profile.ManagerState == ManagerProfileState.Active ? 0 : 1)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .Select(profile => ToCoreProfile(
                reference.InstallationId,
                profile,
                resolvedStateCache?.FindProfileSummary(reference.InstallationId, profile.Id),
                toolOutputSnapshots.GetValueOrDefault(profile.Id)))
            .ToImmutableArray() ?? [];
        var capabilities = CreateCapabilities(profileSnapshot, toolOutputSnapshots.Values);
        var installationToolOutput = toolOutputSnapshots.Values
            .OrderByDescending(snapshot => snapshot.Summary.ObservedAtUtc)
            .Select(snapshot => snapshot.Summary)
            .FirstOrDefault();
        var observedExecutables = toolOutputSnapshots.Values
            .OrderByDescending(snapshot => snapshot.Summary.ObservedAtUtc)
            .Select(snapshot => snapshot.Executables)
            .FirstOrDefault();
        var detail = profileSnapshot is not null && profileSnapshot.Status != ProfileObservationStatus.Unavailable
            ? $"Connected read-only MO2 reference · {profiles.Length} profile observation(s) loaded · no external files changed"
            : profileSnapshot is not null
                ? "Connected read-only MO2 reference · profile content requires exact session authorization or is unavailable · no profile files were changed"
            : authorizationOnly
            ? "Connected read-only MO2 reference · configured external paths require explicit session reauthorization and were not checked · external files unchanged"
            : validation.Status == Mo2ValidationStatus.Valid
                ? "Connected read-only MO2 reference · profile observation is unavailable in this host · external files unchanged"
                : validation.Issues.FirstOrDefault(issue => issue.Severity == Mo2IssueSeverity.Error)?.Message
                    ?? "Connected reference requires validation.";
        var snapshotIssues = profileSnapshot?.Issues ?? [];
        var advisories = validation.Issues
            .AddRange(snapshotIssues)
            .Where(issue => issue.Severity != Mo2IssueSeverity.Information)
            .Take(5)
            .Select(issue => new Advisory(issue.Code, "MO2 connection", issue.Message, level))
            .ToImmutableArray();

        return new(
            reference.InstallationId,
            reference.GameId,
            reference.AdapterId,
            reference.DisplayName,
            InstallationKind.External,
            WorkspaceAccessMode.ReadOnly,
            capabilities,
            profiles,
            [],
            [],
            new(level, validation.Status.ToString(), advisories),
            profileSnapshot is null
                ? ProfileFeature.None
                : ProfileFeature.Saves | ProfileFeature.ConfigurationFiles,
            new(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(reference.InstanceDirectory)),
                availability,
                detail,
                InstallationProvenanceKind.ConnectedReference,
                reference.Id,
                installationToolOutput,
                validation.ConnectionKey),
            observedExecutables.IsDefault ? [] : observedExecutables);
    }

    private static WorkspaceCapabilities CreateCapabilities(
        Mo2ProfileSnapshot? snapshot,
        IEnumerable<ToolOutputObservationSnapshot> toolOutputSnapshots) =>
        snapshot is null
            ? new(WorkspaceFeature.Health, EnvironmentTabCapability.None)
            : new(
                WorkspaceFeature.Profiles | WorkspaceFeature.ModList | WorkspaceFeature.Health,
                EnvironmentTabCapability.Plugins |
                EnvironmentTabCapability.Archives |
                EnvironmentTabCapability.Data |
                EnvironmentTabCapability.Saves |
                EnvironmentTabCapability.Downloads |
                (toolOutputSnapshots.Any(snapshot => !snapshot.Outputs.IsEmpty)
                    ? EnvironmentTabCapability.Outputs
                    : EnvironmentTabCapability.None));

    private static Profile ToCoreProfile(
        InstallationId installationId,
        Mo2ObservedProfile observed,
        ResolvedEnvironmentSummary? environment,
        ToolOutputObservationSnapshot? toolOutputs)
    {
        var level = observed.Observation.Status switch
        {
            ProfileObservationStatus.Complete => HealthLevel.Healthy,
            ProfileObservationStatus.Partial => HealthLevel.Advisory,
            ProfileObservationStatus.Inconsistent => HealthLevel.Warning,
            _ => HealthLevel.Warning,
        };
        var features = ProfileFeature.None;
        if (observed.Observation.LocalSavesEnabled == true)
        {
            features |= ProfileFeature.Saves;
        }

        if (observed.Observation.LocalSettingsEnabled == true)
        {
            features |= ProfileFeature.ConfigurationFiles;
        }

        var advisories = observed.Sources
            .SelectMany(source => source.Warnings)
            .Take(10)
            .Select(warning => new Advisory(warning.Code, "MO2 profile observation", warning.Message, level))
            .ToImmutableArray();
        var outputEntries = toolOutputs?.Outputs.Select(output => new EnvironmentEntry(
            new($"mo2-output.{output.Id.Value}"),
            EnvironmentTabCapability.Outputs,
            output.Name,
            $"{output.Kind} · {output.LocationKind} · {output.FileCount:N0} files · {output.TotalBytes:N0} bytes",
            $"{output.Availability} · {output.EnabledState} · {output.FingerprintStrength} · {output.Change}",
            output.WarningCount == 0 ? HealthLevel.Healthy : HealthLevel.Advisory,
            output.ModId is ModId modId ? [modId] : [],
            output.Id)).ToImmutableArray() ?? [];
        return new(
            observed.Id,
            installationId,
            observed.Name,
            observed.Inventory is null ? [] : observed.Mods,
            observed.Plugins,
            new(level, observed.Observation.Status.ToString(), advisories),
            features,
            ProfileLifecycleState.Available,
            outputEntries,
            null,
            default,
            observed.Observation with { Environment = environment, ToolOutputs = toolOutputs?.Summary },
            toolOutputs?.Outputs ?? []);
    }

    private static Mo2ProfileSnapshot AttachInventory(
        Mo2ProfileSnapshot profileSnapshot,
        Mo2ModInventorySnapshot inventorySnapshot)
    {
        var inventoryByProfile = inventorySnapshot.Profiles.ToDictionary(profile => profile.ProfileId);
        var profiles = profileSnapshot.Profiles.Select(profile =>
        {
            if (!inventoryByProfile.TryGetValue(profile.Id, out var inventory))
            {
                var authorizationRequired = inventorySnapshot.Issues.Any(issue =>
                    issue.Code == "mo2.mods.authorization_required");
                var unavailableSummary = new ModInventorySummary(
                    authorizationRequired
                        ? ModInventoryObservationStatus.AuthorizationRequired
                        : ModInventoryObservationStatus.Unavailable,
                    inventorySnapshot.ObservedAtUtc,
                    inventorySnapshot.Revision,
                    inventorySnapshot.Issues.Length,
                    0,
                    0);
                return profile with
                {
                    Mods = [],
                    Observation = profile.Observation with { Inventory = unavailableSummary },
                };
            }

            var mods = inventory.Entries
                .Select((entry, displayOrder) => ToCoreMod(entry, displayOrder, inventorySnapshot.ObservedAtUtc, inventory.Fingerprint))
                .ToImmutableArray();
            var summary = new ModInventorySummary(
                ToCoreStatus(inventory.Status),
                inventorySnapshot.ObservedAtUtc,
                inventory.Fingerprint,
                inventory.Warnings.Length,
                inventory.Entries.Count(entry => entry.SourceLineIndex is not null),
                inventory.Entries.Count(entry => entry.SourceLineIndex is null));
            return profile with
            {
                Mods = mods,
                Inventory = inventory,
                Observation = profile.Observation with { Inventory = summary },
            };
        }).ToImmutableArray();
        return profileSnapshot with
        {
            Profiles = profiles,
            ModInventory = inventorySnapshot,
            Revision = $"{profileSnapshot.Revision}+inventory.{inventorySnapshot.Revision[..16]}",
        };
    }

    private static ModEntry ToCoreMod(
        Mo2ReconciledMod entry,
        int displayOrder,
        DateTimeOffset observedAt,
        string inventoryFingerprint)
    {
        var metadata = entry.Directory?.Metadata.Metadata;
        var update = DeriveUpdate(metadata);
        var kind = entry.Reconciliation switch
        {
            Mo2ModReconciliationState.Separator => ModEntryKind.Separator,
            Mo2ModReconciliationState.Foreign => ModEntryKind.Foreign,
            Mo2ModReconciliationState.Backup => ModEntryKind.Backup,
            Mo2ModReconciliationState.Unlisted => ModEntryKind.UnlistedDirectory,
            Mo2ModReconciliationState.Missing when IsSeparatorName(entry.Name) => ModEntryKind.Separator,
            Mo2ModReconciliationState.Missing when IsBackupName(entry.Name) => ModEntryKind.Backup,
            _ => ModEntryKind.Mod,
        };
        var authority = entry.SourceLineIndex is null
            ? ModInventoryAuthority.GridDerived
            : ModInventoryAuthority.ManagerAuthoritative;
        var warnings = entry.Warnings
            .AddRange(entry.Directory?.Warnings ?? [])
            .Select(warning => SafeDisplay(warning.Message, 512) ?? "Inventory warning details are not display-safe.")
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (entry.Reconciliation == Mo2ModReconciliationState.Foreign)
        {
            warnings = warnings.Add("Foreign game content has no mod-directory metadata in this observation.");
        }
        var coreMetadata = metadata is null ? null : new ModMetadataSummary(
            SafeDisplay(metadata.Version, 128),
            SafeDisplay(metadata.NewestVersion, 128),
            SafeDisplay(metadata.IgnoredVersion, 128),
            metadata.CategoryIds,
            metadata.CategoryNames.Select(name => SafeDisplay(name, 128)).Where(name => name is not null).Select(name => name!).ToImmutableArray(),
            SafeDisplay(metadata.NexusGameName, 128),
            metadata.NexusModId,
            SafeLeafName(metadata.InstallationFile),
            entry.Directory?.CreatedAtUtc,
            SafeDisplay(metadata.Notes, 4096),
            SafeDisplay(metadata.Comments, 4096),
            SafeDisplay(metadata.Repository, 256),
            metadata.ProviderUpdatedAtUtc,
            SafeDisplay(metadata.ProviderStatus, 128),
            metadata.RawValues
                .Where(value => value.IsSupported && !string.IsNullOrWhiteSpace(value.Key))
                .Select(value => (
                    Key: SafeDisplay(value.Key, 128),
                    Raw: SafeDisplay(value.RawValue, 512),
                    Normalized: SafeDisplay(NormalizeMetadataValue(metadata, value.Key), 512)))
                .Where(value => value.Key is not null && value.Raw is not null)
                .Select(value => new ModMetadataRawValue(value.Key!, value.Raw!, value.Normalized))
                .ToImmutableArray());
        if (update.State == ModUpdateState.UpdateAvailable && coreMetadata?.NewestVersion is null)
        {
            update = (ModUpdateState.Unknown, "Local update metadata is not display-safe, so update state remains unknown; no network check was performed.");
        }
        var metadataAvailability = entry.Directory is null
            ? entry.Reconciliation == Mo2ModReconciliationState.Foreign
                ? ModMetadataAvailability.NotApplicable
                : ModMetadataAvailability.Missing
            : entry.Directory.Metadata.Availability switch
            {
                Mo2MetadataAvailability.Available => ModMetadataAvailability.Available,
                Mo2MetadataAvailability.Missing or Mo2MetadataAvailability.NotApplicable => ModMetadataAvailability.Missing,
                Mo2MetadataAvailability.Inaccessible => ModMetadataAvailability.Inaccessible,
                Mo2MetadataAvailability.Oversized => ModMetadataAvailability.Oversized,
                Mo2MetadataAvailability.Malformed => ModMetadataAvailability.Malformed,
                Mo2MetadataAvailability.ChangedDuringRead => ModMetadataAvailability.Inconsistent,
                _ => ModMetadataAvailability.Inconsistent,
            };
        var fingerprint = Fingerprint(
            inventoryFingerprint,
            entry.Id.Value,
            entry.Directory?.Metadata.Source.RawFingerprint ?? string.Empty,
            entry.Reconciliation.ToString());
        var observation = new ModInventoryObservation(
            authority,
            ToCoreReconciliation(entry.Reconciliation),
            displayOrder,
            entry.SourceOrder,
            Marker(entry.Marker),
            metadataAvailability,
            observedAt,
            entry.SourceLineIndex is null ? "mods directory" : "modlist.txt",
            fingerprint,
            warnings.Length,
            warnings,
            coreMetadata,
            new(update.State, update.Detail, metadata?.ProviderUpdatedAtUtc, false));

        var isSeparator = kind == ModEntryKind.Separator;
        var category = coreMetadata?.CategoryNames.Length > 0
            ? string.Join(", ", coreMetadata.CategoryNames)
            : coreMetadata?.CategoryIds.Length > 0
                ? string.Join(", ", coreMetadata.CategoryIds.Select(id => $"ID {id}"))
                : "Unavailable";
        return new(
            entry.Id,
            SafeDisplay(entry.Name, 1024) ?? "[unsupported mod name]",
            isSeparator ? string.Empty : coreMetadata?.InstalledVersion ?? string.Empty,
            isSeparator ? string.Empty : authority == ModInventoryAuthority.GridDerived ? "Grid-derived directory" : "MO2 profile",
            !isSeparator && entry.IsEnabled,
            entry.Mo2Priority,
            warnings.IsEmpty ? HealthLevel.Unknown : HealthLevel.Advisory,
            kind,
            isSeparator ? string.Empty : category,
            update.State == ModUpdateState.UpdateAvailable ? coreMetadata?.NewestVersion : null,
            update.State,
            ModConflictState.Unknown,
            observation);
    }

    private static (ModUpdateState State, string Detail) DeriveUpdate(Mo2NormalizedModMetadata? metadata)
    {
        if (metadata is null)
        {
            return (ModUpdateState.Unknown, "No supported local update metadata is available; no network check was performed.");
        }

        if (!string.IsNullOrWhiteSpace(metadata.IgnoredVersion) &&
            !string.IsNullOrWhiteSpace(metadata.NewestVersion) &&
            metadata.IgnoredVersion.Equals(metadata.NewestVersion, StringComparison.OrdinalIgnoreCase))
        {
            return (ModUpdateState.Ignored, "The cached MO2 newest version matches the explicitly ignored version; no network check was performed.");
        }

        if (string.IsNullOrWhiteSpace(metadata.Version) || string.IsNullOrWhiteSpace(metadata.NewestVersion))
        {
            return (ModUpdateState.Unknown, "Installed or cached newest-version evidence is absent; no network check was performed.");
        }

        if (!TryNumericVersion(metadata.Version, out var installed) ||
            !TryNumericVersion(metadata.NewestVersion, out var newest))
        {
            return (ModUpdateState.Error, "Local version metadata cannot be compared safely; no network check was performed.");
        }

        var comparison = CompareVersions(installed, newest);
        return comparison < 0
            ? (ModUpdateState.UpdateAvailable, "Cached local MO2 metadata reports a newer version; it may be stale and was not checked online.")
            : comparison == 0
                ? (ModUpdateState.Current, "Cached local MO2 metadata matches the installed version; it may be stale and was not checked online.")
                : (ModUpdateState.Unknown, "The installed version is newer than cached provider metadata, so local update state remains unknown; no network check was performed.");
    }

    private static bool TryNumericVersion(string value, out ImmutableArray<int> components)
    {
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var builder = ImmutableArray.CreateBuilder<int>();
        foreach (var component in text.Split('.'))
        {
            if (!int.TryParse(component, out var parsed) || parsed < 0)
            {
                components = [];
                return false;
            }

            builder.Add(parsed);
        }

        components = builder.ToImmutable();
        return !components.IsEmpty;
    }

    private static int CompareVersions(ImmutableArray<int> left, ImmutableArray<int> right)
    {
        for (var index = 0; index < Math.Max(left.Length, right.Length); index++)
        {
            var comparison = (index < left.Length ? left[index] : 0)
                .CompareTo(index < right.Length ? right[index] : 0);
            if (comparison != 0) return comparison;
        }

        return 0;
    }

    private static string? NormalizeMetadataValue(Mo2NormalizedModMetadata metadata, string key) => key.ToLowerInvariant() switch
    {
        "version" => metadata.Version,
        "newestversion" => metadata.NewestVersion,
        "ignoredversion" => metadata.IgnoredVersion,
        "gamename" => metadata.NexusGameName,
        "modid" => metadata.NexusModId?.ToString(),
        "installationfile" => metadata.InstallationFile,
        "notes" => metadata.Notes,
        "comments" => metadata.Comments,
        "repository" => metadata.Repository,
        "nexusfilestatus" => metadata.ProviderStatus,
        _ => null,
    };

    private static string? SafeLeafName(string? value)
    {
        var safe = SafeDisplay(value, 1024);
        if (safe is null)
        {
            return null;
        }

        var normalized = safe.Replace('\\', '/');
        return SafeDisplay(normalized[(normalized.LastIndexOf('/') + 1)..], 260);
    }

    private static string? SafeDisplay(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            value.Any(character => char.IsControl(character)))
        {
            return null;
        }

        return value.Trim();
    }

    private static string Marker(Mo2ModListMarker marker) => marker switch
    {
        Mo2ModListMarker.Enabled => "+",
        Mo2ModListMarker.Disabled => "-",
        Mo2ModListMarker.Foreign => "*",
        _ => string.Empty,
    };

    private static bool IsSeparatorName(string name) =>
        name.EndsWith("_separator", StringComparison.Ordinal);

    private static bool IsBackupName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            name,
            "^.*backup[0-9]*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static ModReconciliationState ToCoreReconciliation(Mo2ModReconciliationState state) => state switch
    {
        Mo2ModReconciliationState.Matched => ModReconciliationState.Matched,
        Mo2ModReconciliationState.Separator => ModReconciliationState.Separator,
        Mo2ModReconciliationState.Foreign => ModReconciliationState.Foreign,
        Mo2ModReconciliationState.Backup => ModReconciliationState.Backup,
        Mo2ModReconciliationState.Missing => ModReconciliationState.Missing,
        Mo2ModReconciliationState.Duplicate => ModReconciliationState.Duplicate,
        Mo2ModReconciliationState.Ambiguous => ModReconciliationState.Ambiguous,
        Mo2ModReconciliationState.Unlisted => ModReconciliationState.Unlisted,
        Mo2ModReconciliationState.Inaccessible => ModReconciliationState.Inaccessible,
        Mo2ModReconciliationState.AuthorizationRequired => ModReconciliationState.AuthorizationRequired,
        _ => ModReconciliationState.Inconsistent,
    };

    private static ModInventoryObservationStatus ToCoreStatus(Mo2InventoryObservationStatus status) => status switch
    {
        Mo2InventoryObservationStatus.Complete => ModInventoryObservationStatus.Complete,
        Mo2InventoryObservationStatus.Partial => ModInventoryObservationStatus.Partial,
        Mo2InventoryObservationStatus.Inconsistent => ModInventoryObservationStatus.Inconsistent,
        _ => ModInventoryObservationStatus.Unavailable,
    };

    private static string Fingerprint(params string[] components) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components)))).ToLowerInvariant();
}
