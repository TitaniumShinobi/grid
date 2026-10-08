using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

namespace Grid.App.Services;

public sealed class CanonicalRegistrationRefreshService
{
    private readonly CanonicalRegistrationRefreshOrchestrator orchestrator;
    private readonly string gridDataRoot;
    private readonly CanonicalCatalogRuntimeBindingStore bindingStore;
    private readonly CanonicalTerminologyLocalePreference terminologyLocale;

    public CanonicalRegistrationRefreshService(string gridDataRoot, CanonicalTerminologyLocalePreference terminologyLocale)
    {
        this.gridDataRoot = Path.GetFullPath(gridDataRoot);
        this.terminologyLocale = terminologyLocale;
        var catalogsRoot = Path.Combine(this.gridDataRoot, "catalogs");
        bindingStore = new CanonicalCatalogRuntimeBindingStore(Path.Combine(catalogsRoot, "canonical-runtime-binding.v1.json"));
        var observationContributor = new GtaVEnhancedRegistrationKnowledgeRefreshContributor();
        orchestrator = new CanonicalRegistrationRefreshOrchestrator(
            [new CanonicalRegistrationMdboKnowledgeRefreshContributor(observationContributor, new GtaVRegistrationMdboRefreshAuthor())],
            terminologyLocale);
    }

    public async Task<RegistrationRefreshResult> RefreshAsync(
        GameId gameId,
        InstallationId installationId,
        ProfileId profileId,
        string? profileObservationFingerprint,
        IProgress<RegistrationRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var catalogsRoot = Path.Combine(gridDataRoot, "catalogs");
        Directory.CreateDirectory(catalogsRoot);
        var catalogStorePath = Environment.GetEnvironmentVariable("GRID_CANONICAL_CATALOG_PATH");
        if (string.IsNullOrWhiteSpace(catalogStorePath))
            catalogStorePath = Path.Combine(catalogsRoot, CanonicalCatalogRuntimeBindingStore.DefaultCatalogStoreFileName);
        else
            catalogStorePath = Path.GetFullPath(catalogStorePath);

        var binding = bindingStore.Load();
        var allowCandidate = ParseOptionalBooleanEnvironment("GRID_CANONICAL_ALLOW_CANDIDATE") ??
                             binding?.AllowCandidatePackages ??
                             false;
        CatalogPackageId? pinnedPackageId = null;
        var packageOverride = Environment.GetEnvironmentVariable("GRID_CANONICAL_PACKAGE_ID");
        if (!string.IsNullOrWhiteSpace(packageOverride))
            pinnedPackageId = new CatalogPackageId(packageOverride);
        else if (binding is not null)
            pinnedPackageId = binding.PackageId;

        var resourcePaths = new RegistrationRefreshResourcePaths(
            GtaLocationHierarchyWikiPath: ResolveRepositoryReference(
                "scripts/games/grandtheftautov/catalog/references/vinewood-411759.normalized.txt"),
            GtaLocationHierarchyNativeTablePath: ResolveRepositoryReference(
                "scripts/games/grandtheftautov/catalog/references/cfx-zones-ad60ae80.md"));

        var context = new RegistrationRefreshContext(
            gameId,
            installationId,
            profileId,
            pinnedPackageId,
            catalogStorePath,
            Path.Combine(catalogsRoot, "prepared-canonical"),
            bindingStore.BindingFilePath,
            Path.Combine(gridDataRoot, "evidence", "registration-refresh"),
            profileObservationFingerprint,
            allowCandidate,
            resourcePaths);

        var result = await orchestrator.RefreshAsync(context, progress, cancellationToken).ConfigureAwait(false);
        if (result.Status == RegistrationRefreshStatus.Completed &&
            context.GameId == ProductionGridCatalogService.GrandTheftAutoVEnhancedId)
        {
            var packageId = result.PublishedPackageId ?? context.PinnedPackageId;
            var publicationStore = new RegistrationPreparedNavigationPublicationStore(
                Path.Combine(Path.GetDirectoryName(context.PreparedNavigationRoot)!, "prepared-registration"));
            var needsRegistrationPrepared = publicationStore.Load(context.ProfileId) is not { PackageId: var published } ||
                packageId is not null && published != packageId;
            if (needsRegistrationPrepared && packageId is not null &&
                (result.Mode == RegistrationRefreshMode.KnowledgeRebuild || publicationStore.Load(context.ProfileId) is null))
            {
                var catalog = await new JsonCanonicalKnowledgeCatalogStore(catalogStorePath)
                    .LoadAsync(cancellationToken).ConfigureAwait(false);
                if (catalog.IsValid)
                {
                    await GtaVRegistrationPreparedNavigationRebuild.RebuildAsync(
                        context,
                        catalog,
                        packageId.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        return result;
    }

    private static string? ResolveRepositoryReference(string relativePath)
    {
        var repository = Environment.GetEnvironmentVariable("GRID_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var candidate = Path.Combine(Path.GetFullPath(repository), relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }

        var cursor = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8; depth++)
        {
            var candidate = Path.Combine(cursor, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(cursor);
            if (parent is null) break;
            cursor = parent.FullName;
        }

        return null;
    }

    private static bool? ParseOptionalBooleanEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidDataException($"Environment variable '{name}' must be 'True' or 'False'.");
    }
}
