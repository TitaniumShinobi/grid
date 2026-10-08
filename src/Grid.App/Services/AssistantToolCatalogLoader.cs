using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.Core.Models;

namespace Grid.App.Services;

internal static class AssistantToolCatalogLoader
{
    private const string RegistryFileName = "tool-registry.v2.json";
    private static readonly Regex ToolIdPattern = new("^grid\\.tool\\.[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex GameIdPattern = new("^game\\.[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex CapabilityIdPattern = new("^grid\\.[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static ImmutableArray<AssistantToolOption> Load()
    {
        var gamesRoot = Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts", "games");
        if (!Directory.Exists(gamesRoot)) return [];

        var tools = ImmutableArray.CreateBuilder<AssistantToolOption>();
        var toolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(gamesRoot, RegistryFileName, SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2)
                throw new InvalidDataException($"Tool registry '{path}' does not use schema version 2.");

            foreach (var tool in root.GetProperty("tools").EnumerateArray())
            {
                var toolId = tool.GetProperty("toolId").GetString();
                var displayName = tool.GetProperty("displayName").GetString();
                if (tool.GetProperty("schemaVersion").GetInt32() != 2 ||
                    string.IsNullOrWhiteSpace(toolId) || !ToolIdPattern.IsMatch(toolId) ||
                    string.IsNullOrWhiteSpace(displayName) || !toolIds.Add(toolId))
                    throw new InvalidDataException($"Tool registry '{path}' contains an incomplete or duplicate ToolID.");

                var rootCapabilityIds = tool.GetProperty("rootCapabilityIds").EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!)
                    .ToImmutableArray();
                if (rootCapabilityIds.IsEmpty ||
                    rootCapabilityIds.Any(item => !CapabilityIdPattern.IsMatch(item)) ||
                    rootCapabilityIds.Distinct(StringComparer.Ordinal).Count() != rootCapabilityIds.Length)
                    throw new InvalidDataException($"Tool '{toolId}' has invalid root capability identities.");
                var rootCapabilitySet = rootCapabilityIds.ToHashSet(StringComparer.Ordinal);

                var compatibility = tool.GetProperty("compatibility").EnumerateArray().Select(value =>
                {
                    var gameId = value.GetProperty("gameId").GetString();
                    var provenance = value.GetProperty("provenance").GetString();
                    var evidence = value.GetProperty("evidenceCapabilityIds").EnumerateArray()
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Select(item => item!)
                        .ToImmutableArray();
                    if (string.IsNullOrWhiteSpace(gameId) || !GameIdPattern.IsMatch(gameId) ||
                        string.IsNullOrWhiteSpace(provenance) || evidence.IsEmpty ||
                        evidence.Any(item => !CapabilityIdPattern.IsMatch(item) || !rootCapabilitySet.Contains(item)) ||
                        evidence.Distinct(StringComparer.Ordinal).Count() != evidence.Length)
                        throw new InvalidDataException($"Tool '{toolId}' has incomplete canonical game compatibility evidence.");
                    return new AssistantToolGameCompatibility(new GameId(gameId), evidence, provenance);
                }).ToImmutableArray();
                if (compatibility.IsEmpty || compatibility.Select(value => value.GameId).Distinct().Count() != compatibility.Length)
                    throw new InvalidDataException($"Tool '{toolId}' must have unique canonical game compatibility evidence.");

                var observationMode = tool.GetProperty("observationMode").GetString();
                if (observationMode is not ("InProcessRead" or "ExistingOutputRead")) continue;
                tools.Add(new AssistantToolOption(
                    new ToolId(toolId),
                    displayName,
                    AvailabilityState.Available,
                    "Registered read-only collector; external evidence still requires exact authorization.",
                    compatibility));
            }
        }

        return tools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    }
}
