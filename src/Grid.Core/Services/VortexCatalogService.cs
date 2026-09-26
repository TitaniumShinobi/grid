using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

                if (!byInstallation.TryGetValue(installation.Id, out var connection))
                {
                    connection = TryDiscoverDeploymentConnection(installation, revisionEvidence);
                    if (connection is null) continue;
                }

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

    private static VortexInstallationConnection? TryDiscoverDeploymentConnection(
        ManagedInstallation installation,
        ICollection<string> evidence)
    {
        var installationRoot = installation.Metadata.LocationDisplay;
        if (string.IsNullOrWhiteSpace(installationRoot) ||
            !Path.IsPathFullyQualified(installationRoot) ||
            !Directory.Exists(installationRoot))
        {
            return null;
        }

        string canonicalRoot;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installationRoot));
        }
        catch
        {
            return null;
        }

        string[] deploymentFiles;
        try
        {
            deploymentFiles = Directory
                .EnumerateFiles(canonicalRoot, "vortex.deployment.json", SearchOption.AllDirectories)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            evidence.Add($"{installation.Id.Value}:vortex-discovery:{exception.GetType().Name}");
            return null;
        }

        var matches = new List<string>();

        foreach (var deploymentFile in deploymentFiles)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(deploymentFile));
                var root = document.RootElement;

                if (!root.TryGetProperty("targetPath", out var targetElement) ||
                    targetElement.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("stagingPath", out var stagingElement) ||
                    stagingElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var targetValue = targetElement.GetString();
                var stagingValue = stagingElement.GetString();
                if (string.IsNullOrWhiteSpace(targetValue) ||
                    string.IsNullOrWhiteSpace(stagingValue) ||
                    !Path.IsPathFullyQualified(targetValue) ||
                    !Path.IsPathFullyQualified(stagingValue))
                {
                    continue;
                }

                var targetPath = Path.GetFullPath(targetValue);
                var stagingPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingValue));

                var relative = Path.GetRelativePath(canonicalRoot, targetPath);
                var contained =
                    !Path.IsPathRooted(relative) &&
                    !relative.Equals("..", StringComparison.Ordinal) &&
                    !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

                if (!contained || !Directory.Exists(stagingPath))
                    continue;

                matches.Add(stagingPath);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                JsonException or
                ArgumentException or
                NotSupportedException)
            {
                evidence.Add($"{installation.Id.Value}:vortex-metadata:{exception.GetType().Name}");
            }
        }

        var distinct = matches
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length != 1)
        {
            if (distinct.Length > 1)
                evidence.Add($"{installation.Id.Value}:vortex-discovery:ambiguous:{distinct.Length}");
            return null;
        }

        evidence.Add($"{installation.Id.Value}:vortex-auto:{Hash(distinct[0])}");
        return new(
            VortexInstallationConnection.CurrentSchemaVersion,
            installation.Id,
            distinct[0],
            DateTimeOffset.UtcNow);
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
        var deploymentSources = ReadDeploymentSources(installation, connection, warnings);

        ImmutableArray<ModEntry> mods;
        string fingerprint;

        if (deploymentSources.Length > 0)
        {
            mods = deploymentSources
                .Select((source, index) => CreateManagedMod(source, index, observedAt))
                .ToImmutableArray();
            fingerprint = Hash(string.Join('\n', deploymentSources.Select(value => $"{value.Source}|{value.SourceOrder}")));
        }
        else
        {
            DirectoryInfo[] directories;
            try
            {
                directories = new DirectoryInfo(connection.StagingRoot)
                    .EnumerateDirectories()
                    .Where(IsModDirectory)
                    .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                directories = [];
                warnings.Add($"Staging inventory could not be read ({exception.GetType().Name}).");
            }

            mods = directories
                .Select((directory, index) => CreateMod(directory, index, observedAt))
                .ToImmutableArray();
            fingerprint = Hash(string.Join('\n', directories.Select(value => $"{value.Name}|{value.LastWriteTimeUtc.Ticks}")));
        }
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
            EnvironmentEntries: [], Observation: observation, ObservedOutputs: [], Origin: ProfileOrigin.Vortex);

    private sealed record VortexDeploymentSource(string Source, int SourceOrder);
    private static VortexDeploymentSource[] ReadDeploymentSources(
        ManagedInstallation installation,
        VortexInstallationConnection connection,
        ICollection<string> warnings)
    {
        var installationRoot = installation.Metadata.LocationDisplay;
        if (string.IsNullOrWhiteSpace(installationRoot) ||
            !Path.IsPathFullyQualified(installationRoot) ||
            !Directory.Exists(installationRoot))
            return [];

        try
        {
            var sources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sourceOrder = 0;

            foreach (var deploymentFile in Directory.EnumerateFiles(
                installationRoot,
                "vortex.deployment.json",
                SearchOption.AllDirectories))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(deploymentFile));
                var root = document.RootElement;

                if (!root.TryGetProperty("stagingPath", out var stagingElement) ||
                    stagingElement.ValueKind != JsonValueKind.String)
                    continue;

                var stagingValue = stagingElement.GetString();
                if (string.IsNullOrWhiteSpace(stagingValue))
                    continue;

                var metadataStaging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingValue));
                var connectionStaging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(connection.StagingRoot));

                if (!StringComparer.OrdinalIgnoreCase.Equals(metadataStaging, connectionStaging))
                    continue;

                if (!root.TryGetProperty("files", out var files) ||
                    files.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var file in files.EnumerateArray())
                {
                    if (!file.TryGetProperty("source", out var sourceElement) ||
                        sourceElement.ValueKind != JsonValueKind.String)
                        continue;

                    var source = sourceElement.GetString();
                    if (string.IsNullOrWhiteSpace(source) ||
                        source.StartsWith("__", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!sources.ContainsKey(source))
                        sources.Add(source, sourceOrder);
                    sourceOrder++;
                }
            }

            return sources
                .OrderBy(value => value.Value)
                .Select(value => new VortexDeploymentSource(value.Key, value.Value))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            JsonException or
            ArgumentException or
            NotSupportedException)
        {
            warnings.Add($"Vortex deployment metadata could not be read ({exception.GetType().Name}).");
            return [];
        }
    }

    private static ModEntry CreateManagedMod(VortexDeploymentSource source, int index, DateTimeOffset observedAt)
    {
        var identity = Hash(source.Source.ToUpperInvariant());
        var fingerprint = Hash($"{source.Source}|{source.SourceOrder}");

        var inventory = new ModInventoryObservation(
            ModInventoryAuthority.ManagerAuthoritative,
            ModReconciliationState.Managed,
            index,
            source.SourceOrder,
            "Managed by Vortex; manager priority not represented",
            ModMetadataAvailability.Missing,
            observedAt,
            "Vortex deployment metadata",
            fingerprint,
            0,
            [],
            null,
            new(ModUpdateState.Unknown, "No provider update check was performed.", null, false));

        return new(
            new($"mod.vortex.{identity[..24]}"),
            source.Source,
            "Unknown",
            "Vortex",
            true,
            null,
            HealthLevel.Unknown,
            ModEntryKind.Mod,
            "Managed",
            Inventory: inventory);
    }
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
