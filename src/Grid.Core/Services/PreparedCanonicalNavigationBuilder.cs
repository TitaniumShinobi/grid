using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Explicit preparation only. Never invoked by selector queries or ordinary App startup.</summary>
public static class PreparedCanonicalNavigationBuilder
{
    public static async Task<PreparedCanonicalGenerationDescriptor> BuildAsync(
        string root,
        CanonicalCatalogLoadResult validatedCatalog,
        CatalogPackageId packageId,
        string sourceStoreSha256,
        IReadOnlyDictionary<KnowledgeKind, CanonicalSelectorProjectionInput> inputs,
        CanonicalTerminologyLocalePreference locale,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validatedCatalog);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(locale);
        cancellationToken.ThrowIfCancellationRequested();
        var package = validatedCatalog.Snapshot.FindImportedPackage(packageId);
        if (package is null || !validatedCatalog.HasValidatedPackage(package))
            throw new InvalidDataException("Preparation requires the exact successfully validated catalog package object.");
        if (package.Manifest.PackageKind != CatalogPackageKind.BaseGameCatalog ||
            package.Manifest.ModScope is not null || !package.Manifest.RequiredBasePackageIds.IsEmpty ||
            package.Payload.SourceRevisions.Any(value => value.SourceScope.ScopeKind != KnowledgeSourceScopeKind.BaseGame) ||
            package.Manifest.ValidationStatus == CatalogValidationStatus.Rejected)
            throw new InvalidDataException("Prepared navigation supports only an eligible base-game package.");
        if (!PreparedCanonicalNavigationStore.IsDigest(sourceStoreSha256))
            throw new ArgumentException("An exact source-store SHA-256 digest is required.", nameof(sourceStoreSha256));
        if (inputs.Count != Enum.GetValues<KnowledgeKind>().Length ||
            Enum.GetValues<KnowledgeKind>().Any(kind => !inputs.ContainsKey(kind)))
            throw new InvalidDataException("Preparation must preserve each existing selector kind.");
        var composition = inputs.First().Value.CatalogCompositionId;
        foreach (var input in inputs.Values)
        {
            if (!ReferenceEquals(input.VerifiedPackage, package) || input.RuntimeState is null ||
                input.CatalogCompositionId != composition || input.CatalogRevisionId != package.Manifest.CatalogRevisionId)
                throw new InvalidDataException("Prepared inputs must own this exact validated package and composition.");
        }

        root = Path.GetFullPath(root);
        PreparedCanonicalNavigationStore.RejectReparsePoint(root);
        Directory.CreateDirectory(root);
        var stage = Path.Combine(root, ".preparing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        // Interrupted preparation deliberately leaves an inert, unpublished staging directory.
        var paths = ImmutableArray.CreateBuilder<PreparedCanonicalPathEntry>();
        var pages = new SortedDictionary<string, PreparedCanonicalPageEntry>(StringComparer.Ordinal);
        var kinds = ImmutableArray.CreateBuilder<PreparedCanonicalKindDescriptor>();
        foreach (var pair in inputs.OrderBy(value => value.Key))
        {
            var kind = pair.Key;
            var input = pair.Value;
            var pending = new Queue<CanonicalNavigationPathId?>();
            var visited = new HashSet<CanonicalNavigationPathId>();
            var selectable = new HashSet<KnowledgeRecordId>();
            var identifiers = new HashSet<KnowledgeRecordId>();
            pending.Enqueue(null);
            CanonicalNavigationPathId? rootPath = null;
            while (pending.TryDequeue(out var path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Location normal presentation is named-only. Other kinds retain their existing mode.
                var result = CanonicalSelectorProjectionEngine.Query(input, new(
                    input.CatalogRevisionId, input.CatalogCompositionId, kind, input.Policy.Id,
                    input.Policy.ExactVersion, path, null, kind != KnowledgeKind.Location, false, locale));
                if (!visited.Add(result.CurrentPathId)) continue;
                rootPath ??= result.RootPathId;
                var node = result.CurrentNode;
                var nodeBytes = JsonSerializer.SerializeToUtf8Bytes(node, PreparedCanonicalNavigationStore.Json);
                if (nodeBytes.Length > PreparedCanonicalNavigationStore.MaximumPageBytes)
                    throw new InvalidDataException("Prepared node exceeds the bounded page limit.");
                var nodeDigest = PreparedCanonicalNavigationStore.Digest(nodeBytes);
                if (pages.TryAdd(nodeDigest, new(nodeDigest, nodeDigest, nodeBytes.Length)))
                    await File.WriteAllBytesAsync(Path.Combine(stage, nodeDigest + ".page.json"), nodeBytes, cancellationToken)
                        .ConfigureAwait(false);
                if (node.IsSelectable && node.KnowledgeRecordId is { } record)
                {
                    selectable.Add(record);
                    if (node.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier) identifiers.Add(record);
                }
                var pageIds = ImmutableArray.CreateBuilder<string>();
                var pageCount = Math.Max(1, (result.ImmediateChildren.Length + PreparedCanonicalNavigationStore.PageSize - 1) /
                    PreparedCanonicalNavigationStore.PageSize);
                for (var index = 0; index < pageCount; index++)
                {
                    var sliced = result with { ImmediateChildren = result.ImmediateChildren
                        .Skip(index * PreparedCanonicalNavigationStore.PageSize)
                        .Take(PreparedCanonicalNavigationStore.PageSize).ToImmutableArray() };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(sliced, PreparedCanonicalNavigationStore.Json);
                    if (bytes.Length > PreparedCanonicalNavigationStore.MaximumPageBytes)
                        throw new InvalidDataException("Prepared navigation page exceeds the bounded page limit.");
                    var digest = PreparedCanonicalNavigationStore.Digest(bytes);
                    pageIds.Add(digest);
                    if (pages.TryAdd(digest, new(digest, digest, bytes.Length)))
                        await File.WriteAllBytesAsync(Path.Combine(stage, digest + ".page.json"), bytes, cancellationToken)
                            .ConfigureAwait(false);
                }
                paths.Add(new(kind, node.PathId, node.ParentPathId, node.KnowledgeRecordId,
                    node.IsSelectable, nodeDigest, pageIds.ToImmutable()));
                // Leaves are also persisted for direct selection labels and evidence lookup.
                foreach (var child in result.ImmediateChildren) pending.Enqueue(child.PathId);
            }
            kinds.Add(new(kind, input.Policy.Id, input.Policy.ExactVersion, rootPath!.Value,
                input.CoverageState, visited.Count, selectable.Count, identifiers.Count));
        }
        var coordinates = package.Payload.AcquisitionReceipts
            .Where(receipt => receipt.GameId == package.Manifest.GameScope.GameId &&
                receipt.DistributionApplicationIdentity is not null && receipt.DistributionBuildVersion is not null)
            .Select(receipt => (Application: receipt.DistributionApplicationIdentity!, Build: receipt.DistributionBuildVersion!))
            .Distinct().ToArray();
        var descriptor = new PreparedCanonicalGenerationDescriptor(1, packageId, package.Manifest.CatalogRevisionId,
            composition, validatedCatalog.Snapshot.Revision, package.Manifest.GameScope.GameId,
            package.Manifest.ValidationStatus, package.Manifest.GameScope.ExactGameVersion,
            coordinates.Length == 1 ? coordinates[0].Application : null,
            coordinates.Length == 1 ? coordinates[0].Build : null,
            sourceStoreSha256.ToLowerInvariant(), locale, kinds.ToImmutable());
        var directory = new PreparedCanonicalDirectory(descriptor,
            paths.OrderBy(value => value.Kind).ThenBy(value => value.PathId.Value, StringComparer.Ordinal).ToImmutableArray(),
            pages.Values.ToImmutableArray());
        var directoryBytes = JsonSerializer.SerializeToUtf8Bytes(directory, PreparedCanonicalNavigationStore.Json);
        if (directoryBytes.Length > PreparedCanonicalNavigationStore.MaximumDirectoryBytes)
            throw new InvalidDataException("Prepared navigation directory exceeds its size limit.");
        var directoryDigest = PreparedCanonicalNavigationStore.Digest(directoryBytes);
        await File.WriteAllBytesAsync(Path.Combine(stage, "directory.json"), directoryBytes, cancellationToken)
            .ConfigureAwait(false);
        var target = Path.Combine(root, directoryDigest);
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(target))
        {
            PreparedCanonicalNavigationStore.RejectReparsePoint(target);
            var existing = await PreparedCanonicalNavigationStore.ReadBoundedAsync(Path.Combine(target, "directory.json"),
                PreparedCanonicalNavigationStore.MaximumDirectoryBytes, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(directoryBytes)))
                throw new InvalidDataException("Existing immutable generation does not match preparation.");
            foreach (var page in pages.Values)
            {
                var bytes = await PreparedCanonicalNavigationStore.ReadBoundedAsync(
                    Path.Combine(target, page.Id + ".page.json"), page.ByteLength, cancellationToken).ConfigureAwait(false);
                if (bytes.Length != page.ByteLength || PreparedCanonicalNavigationStore.Digest(bytes) != page.Sha256)
                    throw new InvalidDataException("Existing immutable generation has a corrupt page.");
            }
        }
        else Directory.Move(stage, target);

        var receipt = new PreparedCanonicalPublicationReceipt(1, directoryDigest, packageId, descriptor.SourceStoreSha256);
        var protectedReceipt = PreparedCanonicalReceiptProtection.Protect(
            JsonSerializer.SerializeToUtf8Bytes(receipt, PreparedCanonicalNavigationStore.Json));
        var temporaryPointer = Path.Combine(root, ".publication-" + Guid.NewGuid().ToString("N"));
        await using (var output = new FileStream(temporaryPointer, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough | FileOptions.Asynchronous))
        {
            await output.WriteAsync(protectedReceipt, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var pointer = Path.Combine(root, PreparedCanonicalNavigationStore.PublicationFileName);
        PreparedCanonicalNavigationStore.RejectReparsePoint(pointer);
        var previous = Path.Combine(root, "previous.receipt");
        PreparedCanonicalNavigationStore.RejectReparsePoint(previous);
        if (File.Exists(pointer)) File.Copy(pointer, previous, overwrite: true);
        File.Move(temporaryPointer, pointer, overwrite: true);
        return descriptor;
    }
}
