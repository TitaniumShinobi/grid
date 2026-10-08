using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

public enum CanonicalRuntimeMatchState
{
    Exact,
    MissingCatalog,
    MissingPackage,
    UnsupportedProvider,
    InsufficientEvidence,
    VersionMismatch,
    Ambiguous,
    InvalidCatalog,
    Preparing,
    Unavailable,
}

public sealed record CanonicalRuntimeMatch(
    CanonicalRuntimeMatchState State,
    string Detail,
    CatalogPackageId? PackageId,
    CatalogRevisionId? CatalogRevisionId,
    long? SharedLibraryRevision,
    CatalogCompositionId? CatalogCompositionId,
    SourceNativeVersion? ObservedGameVersion,
    CatalogValidationStatus? ValidationStatus,
    CanonicalSelectorProjectionInput? ProjectionInput)
{
    internal PreparedCanonicalNavigationStore? PreparedState { get; init; }
    internal InstallationId? RegisteredInstallationId { get; init; }
    public bool IsExact => State == CanonicalRuntimeMatchState.Exact &&
        (ProjectionInput is not null || PreparedState is { IsInvalid: false });
}

/// <summary>
/// Read-only runtime matcher for already-imported canonical packages. It observes only the
/// bounded provider build coordinate needed to select a package; it has no adapter, source
/// extraction, registration, web, or catalog-write capability.
/// </summary>
public sealed partial class CanonicalCatalogRuntimeService
{
    private const long MaximumSteamManifestBytes = 1024 * 1024;
    private const string ApplicabilityPolicyVersion = "grid.runtime-applicability.v1";
    private readonly string storePath;
    private readonly string runtimeBindingPath;
    private bool allowCandidatePackages;
    private CatalogPackageId? pinnedPackageId;
    private readonly CanonicalTerminologyLocalePreference terminologyLocale;
    private readonly bool usePreparedNavigation;
    private PreparedCanonicalNavigationStore? prepared;
    private readonly Dictionary<ProfileId, (RegistrationPreparedNavigationPublication Publication, CanonicalRegistrationPreparedReader Reader, RegistrationPreparedNavigationComposer Composer)> registrationPreparedByProfile = new();
    private long loadGeneration;
    private CanonicalRuntimeMatchState preparedState = CanonicalRuntimeMatchState.Preparing;
    private string preparedDetail = "Prepared canonical navigation is opening.";
    private readonly object projectionInputCacheGate = new();
    private readonly Dictionary<RuntimeMatchCacheKey, CanonicalRuntimeMatch> exactMatchCache = [];
    private readonly HashSet<CanonicalRuntimeMatch> currentExactMatches =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CatalogPackageId, CanonicalSelectorProjectionInput> projectionInputCache = [];
    private readonly ConcurrentDictionary<CanonicalSelectorProjectionInput,
        ConcurrentDictionary<SelectorQueryCacheKey, Lazy<CanonicalSelectorResult>>> selectorQueryCaches = new();
    private CanonicalCatalogLoadResult catalog = new(CanonicalKnowledgeCatalogSnapshot.Empty, []);
    private GridCatalogSnapshot runtimeCatalog = new("runtime.empty", CatalogSourceKind.Adapter, []);

    public CanonicalCatalogRuntimeService(
        string storePath,
        CanonicalTerminologyLocalePreference terminologyLocale,
        bool allowCandidatePackages = false,
        CatalogPackageId? pinnedPackageId = null,
        bool usePreparedNavigation = true,
        string? runtimeBindingPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);
        this.storePath = Path.GetFullPath(storePath);
        this.runtimeBindingPath = string.IsNullOrWhiteSpace(runtimeBindingPath)
            ? Path.Combine(Path.GetDirectoryName(this.storePath)!, "canonical-runtime-binding.v1.json")
            : Path.GetFullPath(runtimeBindingPath);
        this.allowCandidatePackages = allowCandidatePackages;
        this.pinnedPackageId = pinnedPackageId;
        this.terminologyLocale = terminologyLocale ?? throw new ArgumentNullException(nameof(terminologyLocale));
        this.usePreparedNavigation = usePreparedNavigation;
    }

    public async Task SynchronizePublishedBindingAsync(
        GridCatalogSnapshot observedCatalog,
        CancellationToken cancellationToken = default)
    {
        ApplyPublishedBindingFromDisk();
        await LoadAsync(observedCatalog, cancellationToken).ConfigureAwait(false);
    }

    public string StorePath => storePath;
    public bool AllowsCandidatePackages => allowCandidatePackages;
    public CatalogPackageId? PinnedPackageId => pinnedPackageId;
    public CanonicalTerminologyLocalePreference TerminologyLocale => terminologyLocale;
    public string PreparedRoot => Path.Combine(Path.GetDirectoryName(storePath)!, "prepared-canonical");
    public string PreparedRegistrationRoot => Path.Combine(Path.GetDirectoryName(storePath)!, "prepared-registration");
    public bool IsPreparing => usePreparedNavigation && preparedState == CanonicalRuntimeMatchState.Preparing;
    public bool IsReadinessUnresolved => usePreparedNavigation && preparedState is
        CanonicalRuntimeMatchState.Preparing or CanonicalRuntimeMatchState.Unavailable;
    public bool UsesPreparedNavigation => usePreparedNavigation;
    public PreparedCanonicalNavigationStatistics? PreparedStatistics => prepared?.Statistics;
    public long RuntimeSourceCatalogLoads { get; private set; }

    public async Task LoadAsync(GridCatalogSnapshot observedCatalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observedCatalog);
        ApplyPublishedBindingFromDisk();
        if (usePreparedNavigation)
        {
            await LoadPreparedAsync(observedCatalog, cancellationToken).ConfigureAwait(false);
            return;
        }
        RuntimeSourceCatalogLoads++;
        var loadedCatalog = await new JsonCanonicalKnowledgeCatalogStore(storePath)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        lock (projectionInputCacheGate)
        {
            // Publish the applicability observation and verified library as one state change.
            // Until this point callers see the complete prior state; afterwards no handle
            // from that state remains current.
            runtimeCatalog = observedCatalog;
            catalog = loadedCatalog;
            exactMatchCache.Clear();
            currentExactMatches.Clear();
            projectionInputCache.Clear();
            selectorQueryCaches.Clear();
        }
    }

    private async Task LoadPreparedAsync(GridCatalogSnapshot observedCatalog, CancellationToken cancellationToken)
    {
        var generation = Interlocked.Increment(ref loadGeneration);
        lock (projectionInputCacheGate)
        {
            if (prepared is null) preparedState = CanonicalRuntimeMatchState.Preparing;
        }
        PreparedCanonicalNavigationStore? replacement = null;
        var nextState = CanonicalRuntimeMatchState.Exact;
        var detail = "Prepared canonical navigation is ready.";
        try
        {
            if (pinnedPackageId is not { } package)
                throw new InvalidDataException("Prepared navigation requires an explicit installed package binding.");
            replacement = await PreparedCanonicalNavigationStore.OpenAsync(
                PreparedRoot, package, terminologyLocale, cancellationToken).ConfigureAwait(false);
            foreach (var kind in replacement.Descriptor.Kinds)
            {
                var policy = CanonicalSelectorProjectionPolicyResolver.ResolveForGame(
                    replacement.Descriptor.GameId, kind.KnowledgeKind);
                if (kind.ProjectionPolicyId != policy.Id || kind.ProjectionPolicyVersion != policy.ExactVersion)
                    throw new InvalidDataException("Prepared navigation policy does not match the installed App.");
            }
        }
        catch (OperationCanceledException) { replacement?.Dispose(); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or CryptographicException or ArgumentException or System.Text.Json.JsonException)
        {
            replacement?.Dispose();
            replacement = null;
            nextState = exception is FileNotFoundException or DirectoryNotFoundException
                ? CanonicalRuntimeMatchState.Unavailable : CanonicalRuntimeMatchState.InvalidCatalog;
            detail = $"Prepared navigation failed closed ({exception.GetType().Name}): {exception.Message}";
        }
        lock (projectionInputCacheGate)
        {
            if (generation != loadGeneration || cancellationToken.IsCancellationRequested)
            {
                replacement?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }
            var previous = prepared;
            prepared = replacement ?? previous;
            preparedState = nextState;
            preparedDetail = detail;
            runtimeCatalog = observedCatalog;
            exactMatchCache.Clear();
            currentExactMatches.Clear();
            projectionInputCache.Clear();
            selectorQueryCaches.Clear();
            if (replacement is not null)
            {
                foreach (var entry in registrationPreparedByProfile.Values)
                    entry.Reader.Dispose();
                registrationPreparedByProfile.Clear();
                previous?.Dispose();
            }
        }
    }

    public CanonicalRuntimeMatch Match(
        GameId gameId,
        InstallationId? installationId,
        ProfileId? profileId)
    {
        var key = new RuntimeMatchCacheKey(gameId, installationId, profileId, pinnedPackageId);
        lock (projectionInputCacheGate)
        {
            if (exactMatchCache.TryGetValue(key, out var cachedMatch))
            {
                if (CachedMatchStillHasExactProviderBuild(cachedMatch, gameId, installationId))
                    return cachedMatch;
                exactMatchCache.Remove(key);
                currentExactMatches.Remove(cachedMatch);
            }

            var match = MatchCore(gameId, installationId, profileId);
            if (!match.IsExact) return match;
            if (exactMatchCache.TryGetValue(key, out var cached)) return cached;
            exactMatchCache.Add(key, match);
            currentExactMatches.Add(match);
            return match;
        }
    }

    private bool CachedMatchStillHasExactProviderBuild(
        CanonicalRuntimeMatch match,
        GameId gameId,
        InstallationId? installationId)
    {
        var installation = runtimeCatalog.Games.FirstOrDefault(value => value.Id == gameId)?
            .Installations.FirstOrDefault(value => value.Id == installationId);
        if (installation is null) return false;
        var observation = match.PreparedState is { } reader
            ? ObserveExactProviderBuild(reader.Descriptor, installation)
            : match.ProjectionInput is { } input
                ? ObserveExactProviderBuild(input.VerifiedPackage, installation)
                : (CanonicalRuntimeMatchState.InsufficientEvidence, (SourceNativeVersion?)null);
        return observation.Item1 == CanonicalRuntimeMatchState.Exact &&
               observation.Item2 == match.ObservedGameVersion;
    }

    private CanonicalRuntimeMatch MatchCore(
        GameId gameId,
        InstallationId? installationId,
        ProfileId? profileId)
    {
        if (usePreparedNavigation) return MatchPrepared(gameId, installationId, profileId);
        if (!catalog.IsValid)
            return Failure(CanonicalRuntimeMatchState.InvalidCatalog,
                $"The shared canonical library failed closed: {string.Join("; ", catalog.Issues)}");
        if (catalog.Snapshot.ImportedPackages.IsEmpty)
            return Failure(CanonicalRuntimeMatchState.MissingCatalog,
                "No imported canonical packages are installed in the shared library.");
        var game = runtimeCatalog.Games.FirstOrDefault(value => value.Id == gameId);
        var installation = game?.Installations.FirstOrDefault(value => value.Id == installationId);
        var profile = installation?.Profiles.FirstOrDefault(value => value.Id == profileId);
        if (installation is null || profile is null || installation.GameId != gameId ||
            installation.Metadata.Availability != InstallationAvailability.Available ||
            installation.Kind != InstallationKind.External ||
            installation.Metadata.Provenance != InstallationProvenanceKind.ConnectedReference ||
            string.IsNullOrWhiteSpace(installation.Metadata.LocationDisplay) ||
            profile.Origin != ProfileOrigin.Grid ||
            !profile.Mods.IsEmpty ||
            !profile.Plugins.IsEmpty)
            return Failure(CanonicalRuntimeMatchState.InsufficientEvidence,
                "An available registered installation and its empty default GRID profile are required for exact base-catalog matching; mod-extension profiles are unsupported.");

        var candidates = SelectEligiblePackages(gameId, out var pinnedFailure);
        if (pinnedFailure is not null)
            return pinnedFailure;
        if (candidates.IsEmpty)
            return Failure(CanonicalRuntimeMatchState.MissingPackage,
                "No evidence-closed canonical package is installed for this GameId.");

        var exact = ImmutableArray.CreateBuilder<(CanonicalCatalogPackage Package, SourceNativeVersion Version)>();
        var observedAnyVersion = false;
        foreach (var package in candidates)
        {
            var observation = ObserveExactProviderBuild(package, installation);
            if (observation.State == CanonicalRuntimeMatchState.UnsupportedProvider)
                continue;
            if (observation.Version is not null) observedAnyVersion = true;
            if (observation.State == CanonicalRuntimeMatchState.Exact && observation.Version is not null)
                exact.Add((package, observation.Version));
        }

        if (exact.Count == 0)
            return Failure(observedAnyVersion
                    ? CanonicalRuntimeMatchState.VersionMismatch
                    : CanonicalRuntimeMatchState.InsufficientEvidence,
                observedAnyVersion
                    ? "The observed installation build has no exact canonical package match."
                    : "The installation did not yield the exact provider build coordinate required by the package.");
        if (exact.Count != 1)
            return Failure(CanonicalRuntimeMatchState.Ambiguous,
                "More than one canonical package matches the exact installation build.");

        var selected = exact[0];
        var compositionId = DeriveBaseCompositionId(selected.Package.Id);
        var applicable = selected.Package.Payload.KnowledgeRecords
            .Select(value => value.Id)
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var applicability = new CanonicalApplicabilityProjection(
            compositionId, applicable, [], ApplicabilityPolicyVersion);
        var input = GetOrCreateProjectionInput(selected.Package, compositionId, applicability);
        return new(
            CanonicalRuntimeMatchState.Exact,
            $"Matched exact provider build '{selected.Version.ExactRepresentation}' to canonical package '{selected.Package.Id.Value}'.",
            selected.Package.Id,
            selected.Package.Manifest.CatalogRevisionId,
            catalog.Snapshot.Revision,
            compositionId,
            selected.Version,
            selected.Package.Manifest.ValidationStatus,
            input);
    }

    private CanonicalRuntimeMatch MatchPrepared(GameId gameId, InstallationId? installationId, ProfileId? profileId)
    {
        if (preparedState != CanonicalRuntimeMatchState.Exact || prepared is null)
            return Failure(preparedState, preparedDetail);
        if (prepared.IsInvalid)
            return Failure(CanonicalRuntimeMatchState.InvalidCatalog, "Prepared generation integrity failed.");
        var descriptor = prepared.Descriptor;
        var game = runtimeCatalog.Games.FirstOrDefault(value => value.Id == gameId);
        var installation = game?.Installations.FirstOrDefault(value => value.Id == installationId);
        var profile = installation?.Profiles.FirstOrDefault(value => value.Id == profileId);
        if (installation is null || profile is null || installation.GameId != gameId ||
            installation.Metadata.Availability != InstallationAvailability.Available ||
            installation.Kind != InstallationKind.External ||
            installation.Metadata.Provenance != InstallationProvenanceKind.ConnectedReference ||
            string.IsNullOrWhiteSpace(installation.Metadata.LocationDisplay) ||
            profile.Origin != ProfileOrigin.Grid || !profile.Mods.IsEmpty || !profile.Plugins.IsEmpty)
            return Failure(CanonicalRuntimeMatchState.InsufficientEvidence,
                "An available registered installation and its empty default GRID profile are required for exact base-catalog matching; mod-extension profiles are unsupported.");
        if (!descriptor.IsBaseGameOnly || descriptor.GameId != gameId || descriptor.PackageId != pinnedPackageId)
            return Failure(CanonicalRuntimeMatchState.MissingPackage, "The prepared package does not match the selected Game and binding.");
        if (descriptor.ValidationStatus != CatalogValidationStatus.Passed &&
            !(allowCandidatePackages && descriptor.ValidationStatus == CatalogValidationStatus.Candidate))
            return Failure(CanonicalRuntimeMatchState.MissingPackage, "The prepared package validation status is not eligible.");
        var observation = ObserveExactProviderBuild(descriptor, installation);
        if (observation.State != CanonicalRuntimeMatchState.Exact)
            return Failure(observation.State, "The installed provider build does not match the prepared package.");
        return new(CanonicalRuntimeMatchState.Exact,
            $"Matched exact provider build '{observation.Version!.ExactRepresentation}' to prepared canonical package '{descriptor.PackageId.Value}'.",
            descriptor.PackageId, descriptor.CatalogRevisionId, descriptor.SharedLibraryRevision,
            descriptor.CatalogCompositionId, observation.Version, descriptor.ValidationStatus, null)
            { PreparedState = prepared, RegisteredInstallationId = installation.Id };
    }

    /// <summary>
    /// Revalidates UI-emitted selector coordinates against the currently matched immutable
    /// projection. Canonical records must still resolve to the exact projected path and record.
    /// Other-context selections carry no path by contract, so only their frozen projection
    /// coordinates and semantic kind can be revalidated here.
    /// </summary>
    public bool ValidateSelection(CanonicalRuntimeMatch match, CanonicalSelectorSelection selection, ProfileId? profileId = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(selection);
        if (match.PreparedState is { } preparedReader)
        {
            try
            {
                if (selection.KnowledgeKind == KnowledgeKind.Location &&
                    profileId is { } validationProfile &&
                    selection.SelectedPathId is { } selectedPath &&
                    selection.KnowledgeRecordId is { } knowledgeRecordId &&
                    TryGetRegistrationComposer(preparedReader.Descriptor, validationProfile) is { } composer &&
                    composer.ValidateSelection(selectedPath, knowledgeRecordId))
                {
                    return IsCurrentExactMatch(match) &&
                        CachedMatchStillHasExactProviderBuild(match, preparedReader.Descriptor.GameId, match.RegisteredInstallationId);
                }
                return IsCurrentExactMatch(match) &&
                   CachedMatchStillHasExactProviderBuild(match, preparedReader.Descriptor.GameId, match.RegisteredInstallationId) &&
                   preparedReader.ValidateSelectionAsync(selection).GetAwaiter().GetResult() &&
                   IsCurrentExactMatch(match) &&
                   CachedMatchStillHasExactProviderBuild(match, preparedReader.Descriptor.GameId, match.RegisteredInstallationId);
            }
            catch (Exception exception) when (exception is ObjectDisposedException or InvalidDataException)
            { return false; }
        }
        if (!IsCurrentExactMatch(match) || match.ProjectionInput is not { } input ||
            selection.CatalogRevisionId != input.CatalogRevisionId ||
            selection.CatalogCompositionId != input.CatalogCompositionId ||
            selection.ProjectionPolicyId != input.Policy.Id ||
            !string.Equals(selection.ProjectionPolicyVersion, input.Policy.ExactVersion, StringComparison.Ordinal) ||
            !Enum.IsDefined(selection.KnowledgeKind))
            return false;

        if (selection.SelectionKind == CanonicalSelectorSelectionKind.OtherContext)
            return !string.IsNullOrEmpty(selection.UnresolvedOtherContextId);
        if (selection.SelectionKind != CanonicalSelectorSelectionKind.CanonicalRecord ||
            selection.SelectedPathId is not { } selectedPathId ||
            selection.KnowledgeRecordId is not { } recordId)
            return false;

        var pending = new Stack<CanonicalNavigationPathId?>();
        var visited = new HashSet<CanonicalNavigationPathId>();
        pending.Push(null);
        while (pending.Count > 0)
        {
            var result = Query(match, selection.KnowledgeKind, pending.Pop());
            if (!visited.Add(result.CurrentPathId)) continue;
            foreach (var child in result.ImmediateChildren)
            {
                if (child.PathId == selectedPathId)
                    return child.IsSelectable && child.KnowledgeRecordId == recordId;
                if (child.CanDescend) pending.Push(child.PathId);
            }
        }

        return false;
    }

    public CanonicalSelectorResult Query(
        CanonicalRuntimeMatch match,
        KnowledgeKind kind,
        CanonicalNavigationPathId? currentPathId = null,
        bool includeIdentifierOnly = true)
    {
        ArgumentNullException.ThrowIfNull(match);
        if (match.PreparedState is not null)
            return QueryPageAsync(match, kind, currentPathId).GetAwaiter().GetResult().Result;
        CanonicalSelectorProjectionInput input;
        ConcurrentDictionary<SelectorQueryCacheKey, Lazy<CanonicalSelectorResult>> cache;
        lock (projectionInputCacheGate)
        {
            if (!match.IsExact || !currentExactMatches.Contains(match) || match.ProjectionInput is null)
                throw new InvalidOperationException(
                    "Canonical selector query requires the current exact verified runtime match.");
            input = match.ProjectionInput;
            // Cache insertion shares the reload lock: an old input cannot re-enter the
            // service's cache after that generation has been cleared.
            cache = selectorQueryCaches.GetOrAdd(input, static _ => new());
        }

        var key = new SelectorQueryCacheKey(kind, currentPathId, includeIdentifierOnly);
        var pending = cache.GetOrAdd(key, _ => new Lazy<CanonicalSelectorResult>(() =>
            CanonicalSelectorProjectionEngine.Query(input, new(
                input.CatalogRevisionId,
                input.CatalogCompositionId,
                kind,
                input.Policy.Id,
                input.Policy.ExactVersion,
                currentPathId,
                null,
                includeIdentifierOnly,
                false,
                terminologyLocale)), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var result = pending.Value;
            if (!IsCurrentExactMatch(match))
                throw new InvalidOperationException(
                    "Canonical selector query became stale during catalog reload.");
            return result;
        }
        catch
        {
            // Do not turn a failed validation/query into durable cached state.
            cache.TryRemove(key, out _);
            throw;
        }
    }

    public async Task<PreparedCanonicalNavigationPage> QueryPageAsync(CanonicalRuntimeMatch match,
        KnowledgeKind kind, CanonicalNavigationPathId? path = null, string? cursor = null,
        ProfileId? profileId = null, CancellationToken cancellationToken = default)
    {
        if (!IsCurrentExactMatch(match)) throw new InvalidOperationException("Canonical runtime match is stale.");
        var reader = match.PreparedState ?? throw new InvalidOperationException("Prepared navigation is unavailable.");
        if (!CachedMatchStillHasExactProviderBuild(match, reader.Descriptor.GameId, match.RegisteredInstallationId))
            throw new InvalidOperationException("Canonical provider build changed before navigation.");
        if (kind == KnowledgeKind.Location && profileId is { } queryProfile &&
            await TryQueryRegistrationPreparedAsync(match, reader.Descriptor, queryProfile, path, cursor, cancellationToken)
                .ConfigureAwait(false) is { } registrationPage)
        {
            if (!IsCurrentExactMatch(match)) throw new InvalidOperationException("Canonical runtime match became stale during navigation.");
            return registrationPage;
        }
        var page = await reader.ReadLevelAsync(kind, path, cursor, cancellationToken).ConfigureAwait(false);
        if (!IsCurrentExactMatch(match) || !CachedMatchStillHasExactProviderBuild(
                match, reader.Descriptor.GameId, match.RegisteredInstallationId))
            throw new InvalidOperationException("Canonical runtime match became stale during navigation.");
        return page;
    }

    public string? GetSelectionLabel(CanonicalRuntimeMatch match, KnowledgeKind kind, CanonicalNavigationPathId path, ProfileId? profileId = null)
    {
        if (!IsCurrentExactMatch(match)) return null;
        if (match.PreparedState is { } reader)
        {
            try
            {
                if (kind == KnowledgeKind.Location && profileId is { } labelProfile &&
                    TryGetRegistrationComposer(reader.Descriptor, labelProfile) is { } composer)
                {
                    var label = composer.GetDisplayAnchor(path);
                    if (label is not null) return IsCurrentExactMatch(match) ? label : null;
                }
                var node = reader.ReadNodeAsync(kind, path).GetAwaiter().GetResult();
                return IsCurrentExactMatch(match) ? node.DisplayAnchor : null;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                CryptographicException or ArgumentException or ObjectDisposedException or System.Text.Json.JsonException)
            { return null; }
        }
        return Query(match, kind, path).CurrentNode.DisplayAnchor;
    }

    private async Task<PreparedCanonicalNavigationPage?> TryQueryRegistrationPreparedAsync(
        CanonicalRuntimeMatch match,
        PreparedCanonicalGenerationDescriptor descriptor,
        ProfileId profileId,
        CanonicalNavigationPathId? path,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (TryGetRegistrationComposer(descriptor, profileId) is not { } composer) return null;
        return await composer.QueryAsync(path, cursor, cancellationToken).ConfigureAwait(false);
    }

    private RegistrationPreparedNavigationComposer? TryGetRegistrationComposer(
        PreparedCanonicalGenerationDescriptor descriptor,
        ProfileId profileId)
    {
        lock (projectionInputCacheGate)
        {
            if (registrationPreparedByProfile.TryGetValue(profileId, out var cached) &&
                cached.Publication.PackageId == descriptor.PackageId)
                return cached.Composer;
        }
        var publication = new RegistrationPreparedNavigationPublicationStore(PreparedRegistrationRoot).Load(profileId);
        if (publication is null || publication.PackageId != descriptor.PackageId) return null;
        try
        {
            var directory = Path.Combine(PreparedRegistrationRoot, publication.GenerationDigest);
            var reader = CanonicalRegistrationPreparedReader.OpenAsync(
                directory,
                publication.GenerationDigest,
                new RegistrationNavigationContext(publication.GameId.Value, publication.ProfileId.Value, publication.Locale))
                .GetAwaiter().GetResult();
            var composer = new RegistrationPreparedNavigationComposer(reader, descriptor);
            lock (projectionInputCacheGate)
            {
                if (registrationPreparedByProfile.TryGetValue(profileId, out var existing))
                    existing.Reader.Dispose();
                registrationPreparedByProfile[profileId] = (publication, reader, composer);
            }
            return composer;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or KeyNotFoundException)
        {
            return null;
        }
    }

    private void ApplyPublishedBindingFromDisk()
    {
        var binding = new CanonicalCatalogRuntimeBindingStore(runtimeBindingPath).Load();
        if (binding is null) return;
        var pinChanged = pinnedPackageId != binding.PackageId;
        var candidateChanged = allowCandidatePackages != binding.AllowCandidatePackages;
        if (!pinChanged && !candidateChanged) return;
        allowCandidatePackages = binding.AllowCandidatePackages;
        pinnedPackageId = binding.PackageId;
        lock (projectionInputCacheGate)
        {
            prepared?.Dispose();
            prepared = null;
            foreach (var entry in registrationPreparedByProfile.Values)
                entry.Reader.Dispose();
            registrationPreparedByProfile.Clear();
            preparedState = CanonicalRuntimeMatchState.Preparing;
            preparedDetail = "Prepared canonical navigation is opening.";
            exactMatchCache.Clear();
            currentExactMatches.Clear();
            projectionInputCache.Clear();
            selectorQueryCaches.Clear();
        }
    }

    private bool IsCurrentExactMatch(CanonicalRuntimeMatch match)
    {
        lock (projectionInputCacheGate)
        {
            return match.IsExact && currentExactMatches.Contains(match);
        }
    }

    private CanonicalSelectorProjectionInput GetOrCreateProjectionInput(
        CanonicalCatalogPackage package,
        CatalogCompositionId compositionId,
        CanonicalApplicabilityProjection applicability)
    {
        lock (projectionInputCacheGate)
        {
            if (projectionInputCache.TryGetValue(package.Id, out var existing)) return existing;
            var policy = CanonicalSelectorProjectionPolicyResolver.ResolveForCatalogPackage(package);
            var verified = CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                catalog,
                package.Id,
                compositionId,
                policy,
                applicability);
            projectionInputCache.Add(package.Id, verified);
            return verified;
        }
    }

    private ImmutableArray<CanonicalCatalogPackage> SelectEligiblePackages(
        GameId gameId,
        out CanonicalRuntimeMatch? pinnedFailure)
    {
        pinnedFailure = null;
        if (pinnedPackageId is { } requiredPackageId)
        {
            var pinned = catalog.Snapshot.FindImportedPackage(requiredPackageId);
            if (pinned is null)
            {
                pinnedFailure = Failure(CanonicalRuntimeMatchState.MissingPackage,
                    $"The pinned canonical package '{requiredPackageId.Value}' is not installed.");
                return [];
            }

            var issue = GetEligibilityIssue(pinned, gameId);
            if (issue is not null)
            {
                pinnedFailure = Failure(issue.Value.State,
                    $"The pinned canonical package '{requiredPackageId.Value}' was rejected: {issue.Value.Detail}");
                return [];
            }

            return [pinned];
        }

        return catalog.Snapshot.ImportedPackages
            .Where(package => GetEligibilityIssue(package, gameId) is null)
            .OrderBy(package => package.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private (CanonicalRuntimeMatchState State, string Detail)? GetEligibilityIssue(
        CanonicalCatalogPackage package,
        GameId gameId)
    {
        if (!catalog.HasValidatedPackage(package))
        {
            using (CanonicalRuntimeDiagnostics.Measure("runtime.eligibility-verify"))
            {
                var verification = CanonicalCatalogPackageKernel.Verify(package);
                if (!verification.IsStructurallyValid)
                    return (CanonicalRuntimeMatchState.InvalidCatalog,
                        $"structural verification failed ({string.Join("; ", verification.Issues)}).");
            }
        }

        if (package.Manifest.PackageKind != CatalogPackageKind.BaseGameCatalog ||
            package.Manifest.ModScope is not null ||
            !package.Manifest.RequiredBasePackageIds.IsEmpty ||
            package.Payload.SourceRevisions.Any(value =>
                value.SourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame))
            return (CanonicalRuntimeMatchState.MissingPackage,
                "it is not an exact base-game-only package.");
        if (package.Manifest.GameScope.GameId != gameId)
            return (CanonicalRuntimeMatchState.MissingPackage,
                "its exact GameId does not match the registered installation.");
        if (package.Manifest.ValidationStatus != CatalogValidationStatus.Passed &&
            !(allowCandidatePackages && package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate))
            return (CanonicalRuntimeMatchState.MissingPackage,
                package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate
                    ? "Candidate status requires explicit Candidate opt-in."
                    : $"validation status '{package.Manifest.ValidationStatus}' is not runtime-eligible.");
        if (package.Manifest.PackageSchemaVersion is not (
                CatalogPackageManifest.ProjectionContractSchemaVersion or
                CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            return (CanonicalRuntimeMatchState.MissingPackage,
                "its schema does not contain the evidence-closed selector projection contract.");
        return null;
    }

    private static (CanonicalRuntimeMatchState State, SourceNativeVersion? Version) ObserveExactProviderBuild(
        CanonicalCatalogPackage package,
        ManagedInstallation installation)
    {
        var exactVersion = package.Manifest.GameScope.ExactGameVersion;
        if (exactVersion is null) return (CanonicalRuntimeMatchState.InsufficientEvidence, null);
        var coordinates = package.Payload.AcquisitionReceipts
            .Where(receipt => receipt.GameId == package.Manifest.GameScope.GameId &&
                              receipt.DistributionApplicationIdentity is not null &&
                              receipt.DistributionBuildVersion is not null)
            .Select(receipt => (Application: receipt.DistributionApplicationIdentity!, Build: receipt.DistributionBuildVersion!))
            .Distinct()
            .ToImmutableArray();
        if (coordinates.Length != 1) return (CanonicalRuntimeMatchState.InsufficientEvidence, null);
        var coordinate = coordinates[0];
        return ObserveExactProviderBuild(exactVersion, coordinate.Application, coordinate.Build, installation);
    }

    private static (CanonicalRuntimeMatchState State, SourceNativeVersion? Version) ObserveExactProviderBuild(
        PreparedCanonicalGenerationDescriptor descriptor, ManagedInstallation installation) =>
        ObserveExactProviderBuild(descriptor.ExactGameVersion, descriptor.DistributionApplicationIdentity,
            descriptor.DistributionBuildVersion, installation);

    private static (CanonicalRuntimeMatchState State, SourceNativeVersion? Version) ObserveExactProviderBuild(
        SourceNativeVersion? exactVersion, SourceNativeIdentifier? application,
        SourceNativeVersion? build, ManagedInstallation installation)
    {
        if (exactVersion is null || application is null || build is null)
            return (CanonicalRuntimeMatchState.InsufficientEvidence, null);
        var coordinate = (Application: application, Build: build);
        if (!string.Equals(coordinate.Application.Namespace, "valve.steam", StringComparison.Ordinal) ||
            !string.Equals(coordinate.Application.ObjectType, "Application", StringComparison.Ordinal) ||
            !string.Equals(installation.Metadata.ProviderId, "steam", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(installation.Metadata.ProviderId, "valve.steam", StringComparison.OrdinalIgnoreCase))
            return (CanonicalRuntimeMatchState.UnsupportedProvider, null);
        if (!coordinate.Application.ExactRepresentation.All(char.IsAsciiDigit))
            return (CanonicalRuntimeMatchState.InsufficientEvidence, null);
        var expectedBuildNamespace =
            $"valve.steam.app.{coordinate.Application.ExactRepresentation}.build-id";
        if (!string.Equals(coordinate.Application.ComparisonMethodId, "valve.steam.app-id.exact-utf8", StringComparison.Ordinal) ||
            coordinate.Application.ComparisonMethodVersion != 1 ||
            !string.Equals(coordinate.Build.Namespace, expectedBuildNamespace, StringComparison.Ordinal) ||
            !string.Equals(coordinate.Build.ComparisonMethodId, "valve.steam.build-id.exact-utf8", StringComparison.Ordinal) ||
            coordinate.Build.ComparisonMethodVersion != 1 ||
            !string.Equals(exactVersion.ComparisonMethodId, coordinate.Build.ComparisonMethodId, StringComparison.Ordinal) ||
            exactVersion.ComparisonMethodVersion != coordinate.Build.ComparisonMethodVersion)
            return (CanonicalRuntimeMatchState.InsufficientEvidence, null);

        var manifest = TryReadSteamManifest(
            installation.Metadata.LocationDisplay!,
            coordinate.Application.ExactRepresentation);
        if (manifest is null) return (CanonicalRuntimeMatchState.InsufficientEvidence, null);
        var observedApplication = SourceNativeIdentifier.FromExactUtf8(
            coordinate.Application.Namespace,
            coordinate.Application.ObjectType,
            manifest.ApplicationId,
            coordinate.Application.ComparisonMethodId,
            coordinate.Application.ComparisonMethodVersion);
        if (observedApplication != coordinate.Application)
            return (CanonicalRuntimeMatchState.VersionMismatch, null);
        var observed = SourceNativeVersion.FromExactUtf8(
            coordinate.Build.Namespace,
            manifest.BuildId,
            coordinate.Build.ComparisonMethodId,
            coordinate.Build.ComparisonMethodVersion);
        var exact = observed == coordinate.Build && observed == exactVersion;
        return (exact ? CanonicalRuntimeMatchState.Exact : CanonicalRuntimeMatchState.VersionMismatch, observed);
    }

    private static SteamManifestObservation? TryReadSteamManifest(string installRoot, string appId)
    {
        try
        {
            var root = new DirectoryInfo(Path.GetFullPath(installRoot));
            var steamApps = root.Parent?.Parent;
            if (steamApps is null || !string.Equals(root.Parent?.Name, "common", StringComparison.OrdinalIgnoreCase))
                return null;
            var manifestPath = Path.Combine(steamApps.FullName, $"appmanifest_{appId}.acf");
            var info = new FileInfo(manifestPath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumSteamManifestBytes) return null;
            var text = File.ReadAllText(manifestPath, new UTF8Encoding(false, true));
            var applicationIds = SteamApplicationIdRegex().Matches(text)
                .Select(match => match.Groups["value"].Value)
                .ToArray();
            var buildIds = SteamBuildIdRegex().Matches(text)
                .Select(match => match.Groups["value"].Value)
                .ToArray();
            var installDirectories = SteamInstallDirectoryRegex().Matches(text)
                .Select(match => match.Groups["value"].Value)
                .ToArray();
            if (applicationIds.Length != 1 || buildIds.Length != 1 || installDirectories.Length != 1 ||
                !string.Equals(applicationIds[0], appId, StringComparison.Ordinal) ||
                !string.Equals(installDirectories[0], root.Name, StringComparison.OrdinalIgnoreCase))
                return null;
            return new(applicationIds[0], buildIds[0], installDirectories[0]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
        {
            return null;
        }
    }

    private static CatalogCompositionId DeriveBaseCompositionId(CatalogPackageId packageId)
    {
        var payload = Encoding.UTF8.GetBytes("grid.runtime-catalog-composition.v1\0" + packageId.Value);
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return new CatalogCompositionId($"grid.runtime-catalog-composition.v1.sha256.{digest}");
    }

    private static CanonicalRuntimeMatch Failure(CanonicalRuntimeMatchState state, string detail) =>
        new(state, detail, null, null, null, null, null, null, null);

    private sealed record SteamManifestObservation(string ApplicationId, string BuildId, string InstallDirectory);

    private readonly record struct SelectorQueryCacheKey(
        KnowledgeKind KnowledgeKind,
        CanonicalNavigationPathId? CurrentPathId,
        bool IncludeIdentifierOnly);

    private readonly record struct RuntimeMatchCacheKey(
        GameId GameId,
        InstallationId? InstallationId,
        ProfileId? ProfileId,
        CatalogPackageId? PinnedPackageId);

    [GeneratedRegex("\\\"appid\\\"\\s+\\\"(?<value>[0-9]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SteamApplicationIdRegex();

    [GeneratedRegex("\\\"buildid\\\"\\s+\\\"(?<value>[0-9]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SteamBuildIdRegex();

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"(?<value>[^\\\"\\r\\n]+)\\\"", RegexOptions.CultureInvariant)]
    private static partial Regex SteamInstallDirectoryRegex();
}
