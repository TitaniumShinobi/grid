using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed record Mo2ResolvedStateLimits(
    Mo2ContentObservationLimits Content,
    Mo2PluginHeaderLimits PluginHeaders,
    Mo2BsaIndexLimits Archives,
    int MaximumPlugins = 65_536,
    int MaximumArchives = 16_384,
    long MaximumVirtualPaths = 2_000_000,
    long MaximumProviderLinks = 8_000_000,
    long MaximumTotalArchiveMembers = 2_000_000,
    long MaximumArchiveIndexBytes = 512L * 1024 * 1024,
    int MaximumConcurrency = 4,
    int DefaultPageSize = 200,
    int MaximumPageSize = 1_000)
{
    public static Mo2ResolvedStateLimits Default { get; } = new(new(), new(), new());

    public void Validate()
    {
        Content.Validate();
        PluginHeaders.Validate();
        Archives.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPlugins);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchives);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumVirtualPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProviderLinks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTotalArchiveMembers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumArchiveIndexBytes);
        if (MaximumConcurrency != 4)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumConcurrency), "Resolved observations use exactly four readers.");
        }

        if (DefaultPageSize <= 0 || MaximumPageSize < DefaultPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultPageSize));
        }
    }
}

public sealed record Mo2LooseProviderInput(
    VirtualProviderKind Kind,
    string SourceName,
    ModId? ModId,
    int Precedence,
    Mo2ContentTreeObservation Tree);

public sealed record Mo2ArchiveProviderInput(
    ResolvedArchiveEntry Archive,
    ImmutableArray<Mo2ArchiveMember> Members,
    int Precedence,
    bool OrderEstablished = true);

public sealed record Mo2VirtualDataResolution(
    ImmutableArray<VirtualDataEntry> Entries,
    ImmutableDictionary<VirtualPathId, ProviderChain> Chains,
    ImmutableArray<EnvironmentDiscrepancy> Discrepancies,
    long ProviderCount,
    bool IsPartial);

public sealed class Mo2VirtualDataResolver
{
    public Mo2VirtualDataResolution Resolve(
        ResolvedSnapshotId snapshotId,
        IEnumerable<Mo2LooseProviderInput> looseInputs,
        IEnumerable<Mo2ArchiveProviderInput> archiveInputs,
        Mo2ResolvedStateLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(looseInputs);
        ArgumentNullException.ThrowIfNull(archiveInputs);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        var discrepancies = ImmutableArray.CreateBuilder<EnvironmentDiscrepancy>();
        var providers = new Dictionary<string, List<VirtualFileProvider>>(StringComparer.OrdinalIgnoreCase);
        var partial = false;
        long providerCount = 0;

        foreach (var input in looseInputs.OrderBy(input => input.Precedence))
        {
            if (input.Tree.IsPartial || input.Tree.State != Mo2PathState.Present)
            {
                partial = true;
            }

            foreach (var file in input.Tree.Entries.Where(entry => entry.Kind == Mo2ContentEntryKind.File))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReserve(file.VirtualPath))
                {
                    break;
                }

                AddProvider(file.VirtualPath, new(
                    ProviderIdFor(snapshotId, file.VirtualPath, input.Kind, input.SourceName, providerCount),
                    input.Kind,
                    SafeName(input.SourceName),
                    input.ModId,
                    null,
                    input.Precedence,
                    false,
                    input.Kind switch
                    {
                        VirtualProviderKind.BaseGameLooseFile => "Base-game Data loose file.",
                        VirtualProviderKind.ModLooseFile => "Enabled mod loose file; higher MO2 priority supersedes lower loose providers.",
                        _ => "MO2 overwrite loose file; highest loose provider.",
                    },
                    Fingerprint(file.IdentityKey, file.Length?.ToString(), file.LastWriteTimeUtcTicks.ToString()),
                    []));
            }
        }

        foreach (var input in archiveInputs.OrderBy(input => input.Precedence))
        {
            foreach (var member in input.Members)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReserve(member.VirtualPath))
                {
                    break;
                }

                AddProvider(member.VirtualPath, new(
                    ProviderIdFor(snapshotId, member.VirtualPath, VirtualProviderKind.ArchiveMember, input.Archive.Name, providerCount),
                    VirtualProviderKind.ArchiveMember,
                    SafeName(input.Archive.Name),
                    null,
                    input.Archive.Id,
                    input.Precedence,
                    false,
                    "Archive member ordered by complete activation evidence.",
                    Fingerprint(input.Archive.Fingerprint, member.NameHash.ToString(), member.SourceOrder.ToString()),
                    input.OrderEstablished ? [] : ["Archive activation order is incomplete."]));
            }
        }

        var entries = ImmutableArray.CreateBuilder<VirtualDataEntry>(providers.Count);
        var chains = ImmutableDictionary.CreateBuilder<VirtualPathId, ProviderChain>();
        foreach (var pair in providers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = new VirtualPathId($"virtual.{Hash(pair.Key)[..24]}");
            var ordered = pair.Value
                .OrderBy(provider => provider.Precedence)
                .ThenBy(provider => provider.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var hasLoose = ordered.Any(provider => provider.Kind != VirtualProviderKind.ArchiveMember);
            var hasArchive = ordered.Any(provider => provider.Kind == VirtualProviderKind.ArchiveMember);
            var archiveOrderComplete = ordered
                .Where(provider => provider.Kind == VirtualProviderKind.ArchiveMember)
                .All(provider => provider.Warnings.IsEmpty);
            var confidence = hasLoose && hasArchive
                ? ProviderWinnerConfidence.Uncertain
                : hasArchive && !archiveOrderComplete
                    ? ProviderWinnerConfidence.Uncertain
                    : ProviderWinnerConfidence.Established;
            ProviderId? winnerId = confidence == ProviderWinnerConfidence.Established
                ? ordered[^1].Id
                : null;
            var chainDiscrepancies = ImmutableArray<EnvironmentDiscrepancy>.Empty;
            if (confidence == ProviderWinnerConfidence.Uncertain)
            {
                var mixedProviders = hasLoose && hasArchive;
                var discrepancy = new EnvironmentDiscrepancy(
                    new($"discrepancy.{Hash($"mixed:{pair.Key}")[..24]}"),
                    EnvironmentDiscrepancyKind.ResolutionUncertain,
                    EnvironmentDiscrepancySeverity.Warning,
                    mixedProviders ? "Loose/archive precedence is not established" : "Archive activation order is incomplete",
                    mixedProviders
                        ? "Grid observed both loose and archive providers but will not guess a universal winner without complete game and invalidation evidence."
                        : "Grid preserved every archive provider but did not claim a winner because activation-order evidence is incomplete.");
                chainDiscrepancies = [discrepancy];
                discrepancies.Add(discrepancy);
            }

            var marked = ordered.Select(provider => provider with { IsWinner = winnerId == provider.Id }).ToImmutableArray();
            var detail = confidence == ProviderWinnerConfidence.Established
                ? marked[^1].Reason
                : "Every provider is preserved; no effective winner is claimed.";
            chains[id] = new(snapshotId, id, pair.Key, marked, winnerId, confidence, detail, chainDiscrepancies);
            entries.Add(new(
                id,
                pair.Key,
                marked.Length,
                winnerId,
                winnerId is null ? null : marked.First(provider => provider.Id == winnerId).SourceName,
                confidence,
                chainDiscrepancies.Length));
        }

        return new(entries.ToImmutable(), chains.ToImmutable(), discrepancies.ToImmutable(), providerCount, partial);

        bool TryReserve(string virtualPath)
        {
            if (!providers.ContainsKey(virtualPath) && providers.Count >= limits.MaximumVirtualPaths ||
                providerCount >= limits.MaximumProviderLinks)
            {
                if (!partial)
                {
                    discrepancies.Add(new(
                        new($"discrepancy.{Hash("provider-limit")[..24]}"),
                        EnvironmentDiscrepancyKind.SafetyLimitExceeded,
                        EnvironmentDiscrepancySeverity.Warning,
                        "Resolved Data safety budget reached",
                        "The snapshot is partial and no omitted provider is presented as resolved."));
                }

                partial = true;
                return false;
            }

            return true;
        }

        void AddProvider(string virtualPath, VirtualFileProvider provider)
        {
            if (!providers.TryGetValue(virtualPath, out var list))
            {
                list = [];
                providers.Add(virtualPath, list);
            }

            list.Add(provider);
            providerCount++;
        }
    }

    private static ProviderId ProviderIdFor(
        ResolvedSnapshotId snapshot,
        string path,
        VirtualProviderKind kind,
        string source,
        long sequence) =>
        new($"provider.{Hash($"{snapshot.Value}:{path}:{kind}:{source}:{sequence}")[..24]}");

    internal static string Fingerprint(params string?[] values) => Hash(string.Join('\n', values));

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string SafeName(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)
            ? "Untrusted provider"
            : value.Length <= 260 ? value : value[..260];
}
