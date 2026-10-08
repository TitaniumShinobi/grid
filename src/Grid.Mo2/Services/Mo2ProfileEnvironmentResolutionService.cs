using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Resolves a user-selected directory to existing MO2 environment and profile
/// evidence without creating a connection or writing manager state.
/// </summary>
public sealed class Mo2ProfileEnvironmentResolutionService(
    IMo2DiscoveryService discovery,
    IMo2InstallationValidator validator,
    IMo2ProfileSnapshotService profiles,
    IMo2ModInventoryService inventory,
    IMo2SessionPathAuthorization profileAuthorizations,
    IMo2ModsPathAuthorization modAuthorizations,
    IMo2PathCanonicalizer paths,
    IMo2ReadOnlyFileSystem fileSystem)
{
    private const int MaximumAncestorDepth = 8;
    private const string ExecutableFileName = "ModOrganizer.exe";
    private const string IniFileName = "ModOrganizer.ini";
    public static readonly GameAdapterId AdapterId = new("adapter.mod-organizer-2");
    private readonly ConcurrentDictionary<string, SemaphoreSlim> observationGates = new(StringComparer.Ordinal);

    public async Task<Mo2ProfileEnvironmentResolution> ResolveAsync(
        string selectedDirectory,
        ImmutableArray<ManagedGame> catalogGames,
        Mo2DiscoveryOptions discoveryOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedDirectory);
        ArgumentNullException.ThrowIfNull(discoveryOptions);
        cancellationToken.ThrowIfCancellationRequested();

        if (!paths.TryCanonicalize(selectedDirectory, out var selected, out var selectedError) ||
            fileSystem.ProbeDirectory(selected) != Mo2PathState.Present)
        {
            throw new DirectoryNotFoundException(selectedError is null
                ? "The selected profile or environment directory does not exist or is inaccessible."
                : $"The selected profile or environment directory is invalid: {selectedError}");
        }

        var supportedGames = catalogGames
            .Where(game => game.Adapters.Any(adapter => adapter.Id == AdapterId))
            .OrderBy(game => game.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (supportedGames.IsEmpty)
        {
            throw new InvalidDataException("No installed GRID game definition exposes the Mod Organizer 2 profile adapter.");
        }

        var localAnchors = FindLocalAnchors(selected);
        var resolutions = ImmutableArray.CreateBuilder<ValidatedTopology>();
        var failureIssues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        foreach (var game in supportedGames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var discovered = await discovery.DiscoverAsync(
                discoveryOptions with { ExpectedGameId = game.Id },
                cancellationToken).ConfigureAwait(false);
            failureIssues.AddRange(discovered.Issues);

            var candidates = BuildCandidates(localAnchors, discovered.Candidates);
            foreach (var candidate in candidates)
            {
                var topology = await TryValidateAsync(
                    selected,
                    game,
                    candidate,
                    failureIssues,
                    cancellationToken).ConfigureAwait(false);
                if (topology is not null && !resolutions.Any(existing =>
                    existing.Game.Id == topology.Game.Id &&
                    string.Equals(existing.Validation.ConnectionKey, topology.Validation.ConnectionKey, StringComparison.Ordinal)))
                {
                    resolutions.Add(topology);
                }
            }
        }

        if (resolutions.Count == 0)
        {
            var blockers = failureIssues
                .Where(issue => issue.Severity == Mo2IssueSeverity.Error)
                .Select(issue => issue.Message)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            throw new InvalidDataException(blockers.Length == 0
                ? "GRID could not deterministically resolve a supported MO2 environment from the selected directory."
                : string.Join(" ", blockers));
        }

        if (resolutions.Count > 1)
        {
            throw new InvalidDataException(
                "The selected directory matches more than one validated MO2 environment. GRID did not choose one arbitrarily.");
        }

        return await ObserveAsync(selected, resolutions[0], cancellationToken)
            .ConfigureAwait(false);
    }

    private ImmutableArray<LocalAnchor> FindLocalAnchors(string selected)
    {
        var result = ImmutableArray.CreateBuilder<LocalAnchor>();
        var current = selected;
        for (var depth = 0; depth < MaximumAncestorDepth && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var hasExecutable = fileSystem.ProbeFile(Path.Combine(current, ExecutableFileName)) == Mo2PathState.Present;
            var hasIni = fileSystem.ProbeFile(Path.Combine(current, IniFileName)) == Mo2PathState.Present;
            if (hasExecutable || hasIni)
            {
                result.Add(new(current, hasExecutable, hasIni));
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || paths.Equals(parent, current))
            {
                break;
            }
            current = parent;
        }
        return result.ToImmutable();
    }

    private ImmutableArray<TopologyCandidate> BuildCandidates(
        ImmutableArray<LocalAnchor> localAnchors,
        ImmutableArray<Mo2DiscoveryCandidate> discovered)
    {
        var result = new Dictionary<string, TopologyCandidate>(StringComparer.Ordinal);
        void Add(string? application, string? instance, Mo2DiscoveryCandidate evidence)
        {
            if (application is null && instance is null)
                return;
            try
            {
                var key = paths.GetIdentityKey(
                    new[] { application, instance }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray());
                result.TryAdd(key, new(application, instance, evidence));
            }
            catch (ArgumentException)
            {
                // Invalid discovery evidence is ignored and never probed further.
            }
        }

        foreach (var anchor in localAnchors)
        {
            var evidence = new Mo2DiscoveryCandidate(
                $"manual:{paths.GetIdentityKey(anchor.Path)}",
                Path.GetFileName(anchor.Path),
                anchor.HasExecutable ? anchor.Path : null,
                anchor.HasIni ? anchor.Path : null,
                Mo2EvidenceKind.ManualSelection,
                "MO2 application or instance markers found at or above the selected directory");
            Add(anchor.HasExecutable ? anchor.Path : null, anchor.HasIni ? anchor.Path : null, evidence);

            if (anchor.HasIni && !anchor.HasExecutable)
            {
                foreach (var item in discovered.Where(item =>
                    item.ApplicationDirectory is not null &&
                    (item.InstancePath is null || paths.Equals(item.InstancePath, anchor.Path))))
                {
                    Add(item.ApplicationDirectory, anchor.Path, item);
                }
            }
            else if (anchor.HasExecutable && !anchor.HasIni)
            {
                foreach (var item in discovered.Where(item =>
                    item.InstancePath is not null &&
                    (item.ApplicationDirectory is null || paths.Equals(item.ApplicationDirectory, anchor.Path))))
                {
                    Add(anchor.Path, item.InstancePath, item);
                }
            }
        }

        foreach (var item in discovered)
        {
            Add(item.ApplicationDirectory, item.InstancePath, item);
        }

        // Global MO2 intentionally stores application and instance evidence in
        // separate locations. The user's selected path supplies the relationship
        // filter; all bounded combinations are validated and ambiguity fails closed.
        var applications = discovered
            .Where(item => item.ApplicationDirectory is not null)
            .GroupBy(item => Path.TrimEndingDirectorySeparator(item.ApplicationDirectory!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var instances = discovered
            .Where(item => item.InstancePath is not null)
            .GroupBy(item => Path.TrimEndingDirectorySeparator(item.InstancePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        foreach (var application in applications)
        {
            foreach (var instance in instances)
            {
                Add(application.ApplicationDirectory, instance.InstancePath, new(
                    $"bounded:{application.Key}:{instance.Key}",
                    instance.DisplayName,
                    application.ApplicationDirectory,
                    instance.InstancePath,
                    Mo2EvidenceKind.ManualSelection,
                    $"{application.EvidenceDescription}; {instance.EvidenceDescription}; constrained by the selected directory"));
            }
        }
        return result.Values.ToImmutableArray();
    }

    private async Task<ValidatedTopology?> TryValidateAsync(
        string selected,
        ManagedGame game,
        TopologyCandidate candidate,
        ImmutableArray<Mo2ValidationIssue>.Builder diagnosticIssues,
        CancellationToken cancellationToken)
    {
        var initialRequest = new Mo2ValidationRequest(candidate.ApplicationDirectory, candidate.InstanceDirectory, game.Id);
        var initial = await validator.ValidateAsync(initialRequest, cancellationToken).ConfigureAwait(false);
        var authorizedPaths = initial.Paths
            .Where(path => path.State == Mo2PathState.AuthorizationRequired && path.CanonicalPath is not null)
            .Select(path => path.CanonicalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var request = initialRequest with { AuthorizedConfiguredPaths = authorizedPaths };
        var validation = authorizedPaths.IsEmpty
            ? initial
            : await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
        diagnosticIssues.AddRange(validation.Issues);
        if (!validation.CanConnect || validation.InstanceKind is null ||
            validation.ExecutablePath is null || validation.InstanceDirectory is null ||
            validation.ProfilesDirectory is null || validation.ModsDirectory is null ||
            !GameMatches(game, validation) || !SelectedPathBelongsToTopology(selected, validation))
        {
            return null;
        }

        var exactProfile = paths.IsImmediateChildOf(selected, validation.ProfilesDirectory)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(selected))
            : null;
        var evidence = candidate.Evidence with
        {
            ApplicationDirectory = validation.ApplicationDirectory,
            InstancePath = validation.InstanceDirectory,
        };
        return new(game, evidence, request, validation, exactProfile);
    }

    private bool SelectedPathBelongsToTopology(string selected, Mo2InstallationValidation validation)
    {
        var roots = new[]
        {
            validation.ApplicationDirectory,
            validation.InstanceDirectory,
            validation.BaseDirectory,
            validation.ModsDirectory,
            validation.ProfilesDirectory,
            validation.DownloadsDirectory,
            validation.OverwriteDirectory,
            validation.GameDirectory,
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Any(root =>
            paths.Equals(selected, root!) || paths.IsWithinRoot(selected, root!));
    }

    private static bool GameMatches(ManagedGame game, Mo2InstallationValidation validation)
    {
        return !string.IsNullOrWhiteSpace(validation.GameName) &&
            !validation.Issues.Any(issue => issue.Code == "mo2.game.mismatch");
    }

    private async Task<Mo2ProfileEnvironmentResolution> ObserveAsync(
        string selected,
        ValidatedTopology topology,
        CancellationToken cancellationToken)
    {
        var validation = topology.Validation;
        var identity = validation.ConnectionKey ?? paths.GetIdentityKey(validation.ExecutablePath!, validation.InstanceDirectory!);
        var suffix = Fingerprint(identity)[..24];
        var reference = new Mo2InstallationReference(
            Mo2InstallationReference.CurrentSchemaVersion,
            new InstallationReferenceId($"reference.mo2.preview.{suffix}"),
            new InstallationId($"installation.mo2.preview.{suffix}"),
            topology.Game.Id,
            AdapterId,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(validation.InstanceDirectory!)),
            validation.InstanceKind!.Value,
            validation.ExecutablePath!,
            validation.InstanceDirectory!);
        var gate = observationGates.GetOrAdd(reference.Id.Value, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profileAuthorizations.AuthorizeProfilesRoot(reference.Id, validation.ProfilesDirectory!);
            modAuthorizations.AuthorizeModsRoot(reference.Id, validation.ModsDirectory!);
            var snapshot = await profiles.ObserveAsync(
                new Mo2ProfileSnapshotRequest(reference, validation),
                cancellationToken).ConfigureAwait(false);
            var inventorySnapshot = await inventory.ObserveAsync(
                new Mo2ModInventoryRequest(reference, validation, snapshot),
                cancellationToken).ConfigureAwait(false);

            var choices = snapshot.Profiles
                .Where(IsReviewable)
                .Where(profile => topology.ExactProfileName is null ||
                    profile.Name.Equals(topology.ExactProfileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .Select(profile => new Mo2ResolvedProfileChoice(
                    profile,
                    inventorySnapshot.Profiles.FirstOrDefault(item =>
                        item.ProfileId == profile.Id &&
                        item.Status is Mo2InventoryObservationStatus.Complete or Mo2InventoryObservationStatus.Partial)))
                .Where(choice => choice.Inventory is not null)
                .ToImmutableArray();
            if (choices.IsEmpty)
            {
                throw new InvalidDataException(topology.ExactProfileName is null
                    ? "The validated MO2 environment contains no reviewable profiles."
                    : "The selected MO2 profile is unavailable, inconsistent, or lacks a valid mod list.");
            }

            var issues = validation.Issues
                .AddRange(snapshot.Issues)
                .AddRange(inventorySnapshot.Issues)
                .Distinct()
                .ToImmutableArray();
            return new(
                selected,
                topology.Candidate,
                topology.Game.Id,
                topology.Game.Name,
                AdapterId,
                topology.ValidationRequest,
                validation,
                snapshot,
                inventorySnapshot,
                choices,
                topology.ExactProfileName is not null,
                issues);
        }
        finally
        {
            profileAuthorizations.Revoke(reference.Id);
            modAuthorizations.Revoke(reference.Id);
            gate.Release();
        }
    }

    private static bool IsReviewable(Mo2ObservedProfile profile)
    {
        if (profile.Observation.Status is ProfileObservationStatus.Unavailable or ProfileObservationStatus.Inconsistent)
            return false;
        var modList = profile.Sources.FirstOrDefault(source => source.Name.Equals("modlist.txt", StringComparison.OrdinalIgnoreCase));
        return modList is not null && modList.Availability == ProfileSourceAvailability.Read &&
            modList.ParseStatus == ProfileSourceParseStatus.Parsed;
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record LocalAnchor(string Path, bool HasExecutable, bool HasIni);

    private sealed record TopologyCandidate(
        string? ApplicationDirectory,
        string? InstanceDirectory,
        Mo2DiscoveryCandidate Evidence);

    private sealed record ValidatedTopology(
        ManagedGame Game,
        Mo2DiscoveryCandidate Candidate,
        Mo2ValidationRequest ValidationRequest,
        Mo2InstallationValidation Validation,
        string? ExactProfileName);
}
