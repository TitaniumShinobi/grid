using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class VortexCatalogService(IGridCatalogService inner, IVortexInstallationConnectionStore connections) : IGridCatalogService
{
    public static readonly GameAdapterId AdapterId = new("adapter.vortex");

    public async Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalogTask = inner.GetCatalogAsync(cancellationToken);
        var connectionTask = connections.LoadAsync(cancellationToken);
        await Task.WhenAll(catalogTask, connectionTask).ConfigureAwait(false);
        var catalog = await catalogTask.ConfigureAwait(false);
        var loaded = await connectionTask.ConfigureAwait(false);
        var byInstallation = loaded.Connections.ToDictionary(value => value.InstallationId);
        var games = catalog.Games.ToBuilder();
        var revisionEvidence = new List<string>();
        for (var gameIndex = 0; gameIndex < games.Count; gameIndex++)
        {
            var game = games[gameIndex];
            var installations = game.Installations.ToBuilder();
            var connected = false;
            for (var installationIndex = 0; installationIndex < installations.Count; installationIndex++)
            {
                var installation = installations[installationIndex];
                if (!byInstallation.TryGetValue(installation.Id, out var connection)) continue;
                connected = true;
                installations[installationIndex] = Observe(installation, connection, revisionEvidence);
            }
            if (!connected) continue;
            var adapters = game.Adapters.Any(value => value.Id == AdapterId) ? game.Adapters : game.Adapters.Add(new(AdapterId, "Vortex"));
            var capabilities = game.Capabilities with { Features = game.Capabilities.Features | WorkspaceFeature.Profiles | WorkspaceFeature.ModList };
            games[gameIndex] = game with { Adapters = adapters, Installations = installations.ToImmutable(), Capabilities = capabilities };
        }
        var fingerprint = Hash(string.Join('\n', revisionEvidence.Order(StringComparer.Ordinal)));
        return new($"{catalog.Revision}+vortex.{fingerprint[..16]}", CatalogSourceKind.Adapter, games);
    }

    private static ManagedInstallation Observe(ManagedInstallation installation, VortexInstallationConnection connection, ICollection<string> evidence)
    {
        var observedAt = DateTimeOffset.UtcNow;
        if (!Directory.Exists(connection.StagingRoot))
        {
            evidence.Add($"{installation.Id.Value}:missing:{connection.StagingRoot}");
            var unavailableObservation = new ProfileObservationSummary(ProfileObservationStatus.Unavailable, ManagerProfileState.Unknown, observedAt,
                Hash(connection.StagingRoot), 1, null, null,
                [new("Vortex staging directory", ProfileSourceAvailability.RequiredMissing, ProfileSourceParseStatus.NotParsed, 1)]);
            return installation with
            {
                AdapterId = AdapterId,
                Capabilities = installation.Capabilities with { Features = installation.Capabilities.Features | WorkspaceFeature.Profiles | WorkspaceFeature.ModList },
                Profiles = [CreateProfile(installation.Id, [], unavailableObservation, HealthLevel.Warning, "Vortex staging unavailable")],
                Health = new(HealthLevel.Warning, "Vortex staging unavailable", [])
            };
        }

        var warnings = new List<string>();
        DirectoryInfo[] directories;
        try { directories = new DirectoryInfo(connection.StagingRoot).EnumerateDirectories().Where(IsModDirectory).OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            directories = [];
            warnings.Add($"Staging inventory could not be read ({exception.GetType().Name}).");
        }
        var mods = directories.Select((directory, index) => CreateMod(directory, index, observedAt)).ToImmutableArray();
        var fingerprint = Hash(string.Join('\n', directories.Select(value => $"{value.Name}|{value.LastWriteTimeUtc.Ticks}")));
        evidence.Add($"{installation.Id.Value}:{connection.StagingRoot}:{fingerprint}:{mods.Length}");
        var status = warnings.Count == 0 ? ProfileObservationStatus.Partial : ProfileObservationStatus.Inconsistent;
        var inventoryStatus = warnings.Count == 0 ? ModInventoryObservationStatus.Partial : ModInventoryObservationStatus.Inconsistent;
        var stagingObservation = new ProfileObservationSummary(status, ManagerProfileState.Unknown, observedAt, fingerprint, warnings.Count, null, null,
            [new("Vortex staging inventory", ProfileSourceAvailability.Read, ProfileSourceParseStatus.Parsed, warnings.Count)],
            new(inventoryStatus, observedAt, fingerprint, warnings.Count, 0, mods.Length));
        var label = mods.Length == 1 ? "1 staged mod observed" : $"{mods.Length} staged mods observed";
        return installation with
        {
            AdapterId = AdapterId,
            Capabilities = installation.Capabilities with { Features = installation.Capabilities.Features | WorkspaceFeature.Profiles | WorkspaceFeature.ModList },
            Profiles = [CreateProfile(installation.Id, mods, stagingObservation, warnings.Count == 0 ? HealthLevel.Unknown : HealthLevel.Warning, label)],
            Health = new(warnings.Count == 0 ? HealthLevel.Unknown : HealthLevel.Warning, label, [])
        };
    }

    private static Profile CreateProfile(InstallationId installationId, ImmutableArray<ModEntry> mods, ProfileObservationSummary observation, HealthLevel level, string label) =>
        new(new($"profile.vortex.{Hash(installationId.Value)[..20]}"), installationId, "Vortex staging", mods, [], new(level, label, []),
            EnvironmentEntries: [], Observation: observation, ObservedOutputs: []);

    private static ModEntry CreateMod(DirectoryInfo directory, int index, DateTimeOffset observedAt)
    {
        var fingerprint = Hash($"{directory.FullName}|{directory.LastWriteTimeUtc.Ticks}");
        var identity = Hash(directory.FullName.ToUpperInvariant());
        var inventory = new ModInventoryObservation(ModInventoryAuthority.GridDerived, ModReconciliationState.Unlisted, index, null,
            "Staged; deployment state not established", ModMetadataAvailability.Missing, observedAt, "Vortex staging directory", fingerprint, 0, [], null,
            new(ModUpdateState.Unknown, "No provider update check was performed.", null, false));
        return new(new($"mod.vortex.{identity[..24]}"), directory.Name, "Unknown", "Vortex staging", false, null, HealthLevel.Unknown,
            ModEntryKind.UnlistedDirectory, "Staged", Inventory: inventory);
    }

    private static bool IsModDirectory(DirectoryInfo value) =>
        !value.Name.StartsWith("__vortex", StringComparison.OrdinalIgnoreCase) &&
        !value.Name.Equals(".vortex", StringComparison.OrdinalIgnoreCase);

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
