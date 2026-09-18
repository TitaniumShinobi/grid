using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Resolves effective plugin-declared NIF winners, reads only those exact loose files or
/// archive members, and turns embedded DDS paths into provenance-preserving dependencies.
/// </summary>
public sealed class Mo2NifTextureDependencyInspector(IMo2RandomAccessFileFactory files)
{
    private readonly Mo2SkyrimAssetInspectionService assets = new(files ?? throw new ArgumentNullException(nameof(files)));

    public async Task<Mo2NifTextureDependencyResult> InspectAsync(
        Mo2NifTextureDependencyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.Limits);
        request.Limits.Validate();
        if (request.PluginAssetReferences.IsDefault)
            throw new ArgumentException("Plugin asset references must be initialized.", nameof(request));

        var issues = ImmutableArray.CreateBuilder<Mo2NifTextureDependencyIssue>();
        var issueLimitReached = false;
        var meshReferences = request.PluginAssetReferences
            .Where(value => value.Kind == Mo2PluginAssetReferenceKind.Mesh)
            .OrderBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RecordOffset)
            .ToImmutableArray();
        var meshReferencesByPath = meshReferences
            .GroupBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var meshPaths = meshReferencesByPath.Keys
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var partial = false;
        if (meshPaths.Length > request.Limits.MaximumMeshes)
        {
            AddIssue("mo2.nif_texture.mesh_limit",
                $"The effective mesh count exceeds the {request.Limits.MaximumMeshes:N0}-mesh inspection limit; only the deterministic prefix was inspected.");
            partial = true;
            meshPaths = meshPaths.Take(request.Limits.MaximumMeshes).ToImmutableArray();
        }

        var chains = new Dictionary<string, ProviderChain>(StringComparer.OrdinalIgnoreCase);
        foreach (var chain in request.Baseline.ProviderChains)
            chains.TryAdd(chain.VirtualPath, chain);
        var physicalFilesByProvider = request.Baseline.PhysicalFiles
            .GroupBy(value => PhysicalProviderKey(value.VirtualPath, value.ProviderName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var archivesById = request.Baseline.Archives
            .GroupBy(value => value.Id)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray());
        var availableVirtualPaths = chains.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targets = ImmutableArray.CreateBuilder<Mo2AssetTarget>();
        foreach (var meshPath in meshPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A path absent from a complete virtual tree is already a direct missing-mesh
            // finding. There are no bytes from which transitive texture claims can be made.
            if (!chains.TryGetValue(meshPath, out var chain)) continue;
            if (chain.WinnerConfidence != ProviderWinnerConfidence.Established ||
                chain.WinningProviderId is not ProviderId winnerId)
            {
                AddIssue("mo2.nif_texture.mesh_winner_unresolved",
                    "The effective NIF provider is unavailable or uncertain; its embedded texture paths were not inferred.", meshPath);
                partial = true;
                continue;
            }

            var winners = chain.Providers.Where(value => value.Id == winnerId && value.IsWinner).ToArray();
            var failure = "The winning provider identity is absent or ambiguous in the resolved provider chain.";
            Mo2AssetTarget? target = null;
            if (winners.Length != 1 || !TryCreateTarget(
                    chain,
                    winners[0],
                    archivesById,
                    physicalFilesByProvider,
                    out target,
                    out failure))
            {
                AddIssue("mo2.nif_texture.mesh_source_unresolved", failure, meshPath);
                partial = true;
                continue;
            }
            targets.Add(target!);
        }

        Mo2AssetInspectionResult? inspection = null;
        var targetedPaths = targets.Select(value => value.VirtualPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (targets.Count > 0)
        {
            inspection = await assets.InspectAsync(new(
                targets.ToImmutable(),
                new(
                    MaximumTargets: request.Limits.MaximumMeshes,
                    MaximumSourceBytes: request.Limits.MaximumSourceBytes,
                    MaximumAggregateBytes: request.Limits.MaximumAggregateBytes)), cancellationToken).ConfigureAwait(false);
        }

        var inspectedByPath = inspection?.Targets.ToDictionary(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Mo2AssetTargetInspection>(StringComparer.OrdinalIgnoreCase);
        var derived = ImmutableArray.CreateBuilder<Mo2PluginAssetReference>();
        foreach (var meshPath in meshPaths)
        {
            if (!inspectedByPath.TryGetValue(meshPath, out var inspected) ||
                inspected.Status != Mo2AssetInspectionStatus.Complete ||
                inspected.Nif is not { Status: Mo2AssetInspectionStatus.Complete } nif)
            {
                if (targetedPaths.Contains(meshPath))
                {
                    var observed = inspected?.Status.ToString() ?? "Unavailable";
                    AddIssue("mo2.nif_texture.mesh_inspection_incomplete",
                        $"The exact NIF could not be parsed completely ({observed}); no texture dependency was inferred from it.", meshPath);
                    partial = true;
                }
                continue;
            }

            var sources = meshReferencesByPath[meshPath];
            foreach (var texturePath in nif.TexturePaths.Order(StringComparer.OrdinalIgnoreCase))
            foreach (var source in sources)
            {
                if (derived.Count >= request.Limits.MaximumTextureReferences)
                {
                    AddIssue("mo2.nif_texture.reference_limit",
                        $"The derived texture-reference count reached the {request.Limits.MaximumTextureReferences:N0}-reference limit.");
                    partial = true;
                    goto ReferenceLimitReached;
                }
                derived.Add(new(
                    source.PluginName,
                    source.RecordSignature,
                    source.RawFormId,
                    source.RecordOffset,
                    "NIF_TEXTURE",
                    texturePath,
                    texturePath,
                    Mo2PluginAssetReferenceKind.Texture,
                    meshPath));
            }
        }

    ReferenceLimitReached:
        var orderedReferences = derived
            .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.DiscoveredThroughVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RecordOffset)
            .ToImmutableArray();
        var missing = orderedReferences
            .Where(value => !availableVirtualPaths.Contains(value.RequiredVirtualPath))
            .GroupBy(value => (value.PluginName, value.RequiredVirtualPath), MissingKeyComparer.Instance)
            .Select(group => new Mo2MissingPluginAssetDependency(
                group.Key.PluginName,
                group.Key.RequiredVirtualPath,
                Mo2PluginAssetReferenceKind.Texture,
                group.Count(),
                group.Take(8).ToImmutableArray()))
            .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var status = partial || inspection is { Status: not Mo2AssetInspectionStatus.Complete }
            || issueLimitReached
            ? Mo2PluginScriptDependencyStatus.Partial
            : Mo2PluginScriptDependencyStatus.Complete;
        var finalIssues = issues.ToImmutable();
        return new(
            status,
            meshReferences.Select(value => value.RequiredVirtualPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            inspectedByPath.Values.Count(value => value.Status == Mo2AssetInspectionStatus.Complete && value.Nif?.Status == Mo2AssetInspectionStatus.Complete),
            orderedReferences.Length,
            orderedReferences,
            missing,
            finalIssues,
            Fingerprint(status, inspectedByPath.Values, orderedReferences, finalIssues));

        void AddIssue(string code, string detail, string? virtualPath = null)
        {
            if (issues.Count >= request.Limits.MaximumIssues)
            {
                issueLimitReached = true;
                partial = true;
                return;
            }
            issues.Add(new(code, detail, virtualPath));
        }
    }

    private static bool TryCreateTarget(
        ProviderChain chain,
        VirtualFileProvider winner,
        IReadOnlyDictionary<ArchiveId, ImmutableArray<ResolvedArchiveEntry>> archivesById,
        IReadOnlyDictionary<string, ImmutableArray<Mo2BaselinePhysicalFile>> physicalFilesByProvider,
        out Mo2AssetTarget? target,
        out string failure)
    {
        target = null;
        failure = "The winning NIF bytes could not be mapped to one exact source file.";
        string? sourcePath;
        string? archiveMember = null;
        var evidenceIds = ImmutableArray.CreateBuilder<string>();
        evidenceIds.Add(winner.Fingerprint);
        if (winner.Kind == VirtualProviderKind.ArchiveMember)
        {
            if (winner.ArchiveId is not ArchiveId archiveId) return false;
            if (!archivesById.TryGetValue(archiveId, out var archives)) return false;
            if (archives.Length != 1 || archives[0].Format != ArchiveFormat.Bsa ||
                archives[0].SupportStatus != ArchiveSupportStatus.Supported) return false;
            var archive = archives[0];
            if (!physicalFilesByProvider.TryGetValue(
                    PhysicalProviderKey(archive.Name, archive.SourceProvider), out var physical)) return false;
            if (physical.Length != 1) return false;
            sourcePath = physical[0].CanonicalPath;
            archiveMember = chain.VirtualPath;
            evidenceIds.Add(archive.Fingerprint);
        }
        else
        {
            if (!physicalFilesByProvider.TryGetValue(
                    PhysicalProviderKey(chain.VirtualPath, winner.SourceName), out var providerFiles)) return false;
            var physical = providerFiles.Where(value => value.Precedence == winner.Precedence)
                .ToArray();
            if (physical.Length != 1) return false;
            sourcePath = physical[0].CanonicalPath;
        }

        target = new(
            chain.VirtualPath,
            Mo2AssetKind.Nif,
            sourcePath,
            archiveMember,
            new(
                winner.SourceName,
                winner.Kind.ToString(),
                winner.ModId?.Value,
                winner.ArchiveId?.Value,
                winner.Precedence,
                winner.IsWinner,
                chain.WinnerConfidence.ToString(),
                chain.ResolutionDetail,
                evidenceIds.ToImmutable()));
        failure = string.Empty;
        return true;
    }

    private static string PhysicalProviderKey(string virtualPath, string providerName) =>
        $"{virtualPath}\u001f{providerName}";

    private static string Fingerprint(
        Mo2PluginScriptDependencyStatus status,
        IEnumerable<Mo2AssetTargetInspection> inspections,
        IEnumerable<Mo2PluginAssetReference> references,
        IEnumerable<Mo2NifTextureDependencyIssue> issues)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(status.ToString());
        foreach (var value in inspections.OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase))
            Append($"I\u001f{value.VirtualPath}\u001f{value.Status}\u001f{value.Sha256}");
        foreach (var value in references)
            Append($"R\u001f{value.PluginName}\u001f{value.RawFormId:X8}\u001f{value.RequiredVirtualPath}\u001f{value.DiscoveredThroughVirtualPath}");
        foreach (var value in issues)
            Append($"E\u001f{value.Code}\u001f{value.VirtualPath}\u001f{value.Detail}");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
            hash.AppendData([0]);
        }
    }

    private sealed class MissingKeyComparer : IEqualityComparer<(string PluginName, string RequiredVirtualPath)>
    {
        public static MissingKeyComparer Instance { get; } = new();
        public bool Equals((string PluginName, string RequiredVirtualPath) left, (string PluginName, string RequiredVirtualPath) right) =>
            left.PluginName.Equals(right.PluginName, StringComparison.OrdinalIgnoreCase) &&
            left.RequiredVirtualPath.Equals(right.RequiredVirtualPath, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string PluginName, string RequiredVirtualPath) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.PluginName),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.RequiredVirtualPath));
    }
}
