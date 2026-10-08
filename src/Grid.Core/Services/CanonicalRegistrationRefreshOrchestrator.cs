using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class CanonicalRegistrationRefreshOrchestrator(
    IEnumerable<IRegistrationKnowledgeRefreshContributor> contributors,
    CanonicalTerminologyLocalePreference terminologyLocale)
{
    private readonly IRegistrationKnowledgeRefreshContributor[] contributorList = contributors.ToArray();

    public async Task<RegistrationRefreshResult> RefreshAsync(
        RegistrationRefreshContext context,
        IProgress<RegistrationRefreshProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        progress?.Report(new("observe", "Observing canonical registration coordinates.", 0.05));
        if (!File.Exists(context.CatalogStorePath))
        {
            return Unavailable(context, "The canonical knowledge store is not installed on this machine.");
        }

        var contributor = contributorList.FirstOrDefault(value => value.GameId == context.GameId);
        var storeSha256 = await DigestFileAsync(context.CatalogStorePath, cancellationToken).ConfigureAwait(false);
        var catalog = await new JsonCanonicalKnowledgeCatalogStore(context.CatalogStorePath)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!catalog.IsValid)
        {
            return Failed(context, null, "The canonical knowledge store failed validation.");
        }

        var observation = (contributor?.Observe(context) ??
            new RegistrationKnowledgeRefreshObservation(
                context.GameId,
                context.PinnedPackageId,
                storeSha256,
                catalog.Snapshot.Revision,
                context.ProfileObservationFingerprint)) with
        {
            CatalogStoreSha256 = storeSha256,
            CatalogStoreRevision = catalog.Snapshot.Revision,
        };
        var receiptStore = new JsonRegistrationRefreshReceiptStore(context.ReceiptDirectory);
        var lastReceipt = await receiptStore.LoadLatestAsync(context.GameId, context.ProfileId, cancellationToken)
            .ConfigureAwait(false);

        var current = contributor is not null &&
            contributor.IsKnowledgeCurrent(context, observation, catalog, lastReceipt);
        if (current)
        {
            progress?.Report(new("lightweight", "Verified registration knowledge is current.", 0.4));
            if (context.PinnedPackageId is { } packageId)
            {
                await ReloadPreparedNavigationAsync(context, packageId, storeSha256, cancellationToken)
                    .ConfigureAwait(false);
            }
            var receipt = WriteReceipt(context, observation, RegistrationRefreshStatus.Completed,
                RegistrationRefreshMode.Lightweight, null, context.PinnedPackageId, storeSha256,
                catalog.Snapshot.Revision, 0, 0, null, null,
                "Registration knowledge is current; prepared navigation was reloaded.");
            await receiptStore.WriteAsync(receipt, cancellationToken).ConfigureAwait(false);
            return new(RegistrationRefreshStatus.Completed, RegistrationRefreshMode.Lightweight, receipt.Detail,
                context.GameId, context.InstallationId, context.ProfileId, context.PinnedPackageId,
                context.PinnedPackageId, storeSha256, ReceiptPath(receiptStore, context), null, null, true, 0, 0);
        }

        if (contributor is null)
        {
            progress?.Report(new("lightweight", "No registration contributor is registered for this game.", 1));
            return new(RegistrationRefreshStatus.Skipped, RegistrationRefreshMode.Lightweight,
                "This game has no in-process registration refresh contributor; environment-only refresh applies.",
                context.GameId, context.InstallationId, context.ProfileId, context.PinnedPackageId,
                context.PinnedPackageId, storeSha256, null, null, null, false, 0, 0);
        }

        progress?.Report(new("rebuild", "Registration inputs changed; rebuilding verified knowledge.", 0.2));
        var contribution = await contributor.TryRebuildAsync(
            context, observation, catalog, progress, cancellationToken).ConfigureAwait(false);
        if (contribution is null)
        {
            return Failed(context, context.PinnedPackageId, "Knowledge rebuild produced no verified package.");
        }

        progress?.Report(new("verify", "Verifying rebuilt canonical package.", 0.55));
        var verification = CanonicalCatalogPackageKernel.Verify(contribution.Package);
        if (!verification.IsStructurallyValid)
        {
            return Failed(context, contribution.PreviousPackageId,
                "Rebuilt package failed structural verification: " + string.Join("; ", verification.Issues));
        }

        if (contribution.CandidateReadyWithoutImport)
        {
            progress?.Report(new("candidate-ready", "MDBO registration candidate is ready and remains NOT_PUBLISHED.", 0.85));
            var candidateDetail =
                $"MDBO registration produced a candidate-ready package without catalog import or runtime binding publication " +
                $"(admitted {contribution.AdmittedLocationRelationships}, rejected {contribution.RejectedLocationRelationships}).";
            var candidateReceipt = WriteReceipt(
                context,
                observation,
                RegistrationRefreshStatus.Completed,
                RegistrationRefreshMode.KnowledgeRebuild,
                contribution.PreviousPackageId,
                null,
                storeSha256,
                catalog.Snapshot.Revision,
                contribution.AdmittedLocationRelationships,
                contribution.RejectedLocationRelationships,
                null,
                null,
                candidateDetail,
                contribution.ContributorRevisionIds);
            await receiptStore.WriteAsync(candidateReceipt, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("completed", candidateDetail, 1));
            return new(RegistrationRefreshStatus.Completed, RegistrationRefreshMode.KnowledgeRebuild, candidateDetail,
                context.GameId, context.InstallationId, context.ProfileId, contribution.PreviousPackageId,
                null, storeSha256, ReceiptPath(receiptStore, context), null, null, false,
                contribution.AdmittedLocationRelationships, contribution.RejectedLocationRelationships);
        }

        if (context.AllowCandidatePackages == false &&
            contribution.Package.Manifest.ValidationStatus == CatalogValidationStatus.Candidate)
        {
            return Failed(context, contribution.PreviousPackageId,
                "Candidate packages are not permitted for runtime publication on this machine.");
        }

        progress?.Report(new("import", "Importing verified package into the canonical library.", 0.65));
        var store = new JsonCanonicalKnowledgeCatalogStore(context.CatalogStorePath);
        var import = await store.ImportPackageAsync(
                catalog.Snapshot.Revision,
                contribution.Package,
                retireImportedPackageId: null,
                cancellationToken)
            .ConfigureAwait(false);
        if (import.Status is not (CanonicalCatalogImportStatus.Imported or CanonicalCatalogImportStatus.Unchanged))
        {
            return Failed(context, contribution.PreviousPackageId, "Package import failed: " + import.Detail);
        }

        var validated = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!validated.IsValid || validated.Snapshot.FindImportedPackage(contribution.Package.Id) is null)
        {
            return Failed(context, contribution.PreviousPackageId, "Imported package is absent after reload.");
        }

        progress?.Report(new("prepare", "Regenerating prepared DIF navigation.", 0.8));
        var publishedStoreSha256 = await DigestFileAsync(context.CatalogStorePath, cancellationToken).ConfigureAwait(false);
        await RebuildPreparedNavigationAsync(
            validated,
            contribution.Package.Id,
            publishedStoreSha256,
            context,
            cancellationToken).ConfigureAwait(false);

        progress?.Report(new("publish", "Publishing runtime package binding.", 0.9));
        var publication = PublishBinding(context, contribution.Package.Id, context.AllowCandidatePackages);
        var receiptDetail =
            $"Knowledge rebuild imported {contribution.AdmittedLocationRelationships} Location relationships " +
            $"and rejected {contribution.RejectedLocationRelationships} ambiguous claims.";
        var completedReceipt = WriteReceipt(
            context,
            observation,
            RegistrationRefreshStatus.Completed,
            RegistrationRefreshMode.KnowledgeRebuild,
            contribution.PreviousPackageId,
            contribution.Package.Id,
            publishedStoreSha256,
            validated.Snapshot.Revision,
            contribution.AdmittedLocationRelationships,
            contribution.RejectedLocationRelationships,
            publication.CatalogRollbackPath,
            publication.BindingRollbackPath,
            receiptDetail,
            contribution.ContributorRevisionIds);
        await receiptStore.WriteAsync(completedReceipt, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("completed", receiptDetail, 1));

        return new(RegistrationRefreshStatus.Completed, RegistrationRefreshMode.KnowledgeRebuild, receiptDetail,
            context.GameId, context.InstallationId, context.ProfileId, contribution.PreviousPackageId,
            contribution.Package.Id, publishedStoreSha256, ReceiptPath(receiptStore, context),
            publication.CatalogRollbackPath, publication.BindingRollbackPath, true,
            contribution.AdmittedLocationRelationships, contribution.RejectedLocationRelationships);
    }

    private async Task ReloadPreparedNavigationAsync(
        RegistrationRefreshContext context,
        CatalogPackageId packageId,
        string storeSha256,
        CancellationToken cancellationToken)
    {
        if (context.PinnedPackageId != packageId) return;
        var catalog = await new JsonCanonicalKnowledgeCatalogStore(context.CatalogStorePath)
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!catalog.IsValid) return;
        await RebuildPreparedNavigationAsync(catalog, packageId, storeSha256, context, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RebuildPreparedNavigationAsync(
        CanonicalCatalogLoadResult validated,
        CatalogPackageId packageId,
        string storeSha256,
        RegistrationRefreshContext context,
        CancellationToken cancellationToken)
    {
        var package = validated.Snapshot.FindImportedPackage(packageId) ??
            throw new InvalidDataException("Prepared rebuild requires the published package.");
        var composition = new CatalogCompositionId(
            "grid.runtime-catalog-composition.v1.sha256." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "grid.runtime-catalog-composition.v1\0" + package.Id.Value))).ToLowerInvariant());
        var applicability = new CanonicalApplicabilityProjection(
            composition,
            package.Payload.KnowledgeRecords.Select(value => value.Id).OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray(),
            [],
            "grid.runtime-applicability.v1");
        var inputs = Enum.GetValues<KnowledgeKind>().ToDictionary(kind => kind, kind =>
            CanonicalSelectorProjectionEngine.CreateVerifiedInput(
                validated,
                package.Id,
                composition,
                kind == KnowledgeKind.Location
                    ? CanonicalSelectorProjectionPolicy.LocationPrepared
                    : CanonicalSelectorProjectionPolicy.GtaEnhanced,
                applicability));
        await PreparedCanonicalNavigationBuilder.BuildAsync(
            context.PreparedNavigationRoot,
            validated,
            package.Id,
            storeSha256,
            inputs,
            terminologyLocale,
            cancellationToken).ConfigureAwait(false);
    }

    private static (string? CatalogRollbackPath, string? BindingRollbackPath) PublishBinding(
        RegistrationRefreshContext context,
        CatalogPackageId packageId,
        bool allowCandidate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(context.BindingStorePath)!);
        var binding = new
        {
            schemaVersion = 1,
            catalogStoreFileName = Path.GetFileName(context.CatalogStorePath),
            allowCandidatePackages = allowCandidate,
            packageId = packageId.Value,
        };
        var bindingJson = JsonSerializer.Serialize(binding) + Environment.NewLine;
        var bindingTemporary = context.BindingStorePath + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(bindingTemporary, bindingJson, Encoding.UTF8);
        string? bindingRollback = null;
        if (File.Exists(context.BindingStorePath))
        {
            bindingRollback = context.BindingStorePath + ".rollback-" +
                DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            File.Copy(context.BindingStorePath, bindingRollback, overwrite: true);
        }
        File.Move(bindingTemporary, context.BindingStorePath, overwrite: true);
        return (null, bindingRollback);
    }

    private static RegistrationRefreshReceipt WriteReceipt(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        RegistrationRefreshStatus status,
        RegistrationRefreshMode mode,
        CatalogPackageId? previousPackageId,
        CatalogPackageId? publishedPackageId,
        string storeSha256,
        long storeRevision,
        int admitted,
        int rejected,
        string? catalogRollback,
        string? bindingRollback,
        string detail,
        ImmutableArray<string>? contributorRevisionIds = null) =>
        new(
            RegistrationRefreshReceipt.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            status,
            mode,
            context.GameId,
            context.InstallationId,
            context.ProfileId,
            CanonicalRegistrationEngineCoordinates.EngineId,
            CanonicalRegistrationEngineCoordinates.EngineVersion,
            previousPackageId,
            publishedPackageId,
            storeRevision,
            storeSha256,
            observation.ProfileObservationFingerprint,
            contributorRevisionIds ?? [],
            admitted,
            rejected,
            catalogRollback,
            bindingRollback,
            detail);

    private static string? ReceiptPath(JsonRegistrationRefreshReceiptStore store, RegistrationRefreshContext context) =>
        Path.Combine(store.DirectoryPath,
            context.GameId.Value.Replace('.', '_') + "." + context.ProfileId.Value.Replace('.', '_') +
            ".registration-refresh.v1.json");

    private static RegistrationRefreshResult Unavailable(RegistrationRefreshContext context, string detail) =>
        new(RegistrationRefreshStatus.Unavailable, RegistrationRefreshMode.Lightweight, detail,
            context.GameId, context.InstallationId, context.ProfileId, context.PinnedPackageId, null, null, null, null, null, false, 0, 0);

    private static RegistrationRefreshResult Failed(
        RegistrationRefreshContext context,
        CatalogPackageId? previous,
        string detail) =>
        new(RegistrationRefreshStatus.Failed, RegistrationRefreshMode.KnowledgeRebuild, detail,
            context.GameId, context.InstallationId, context.ProfileId, previous, null, null, null, null, null, false, 0, 0);

    private static async Task<string> DigestFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
