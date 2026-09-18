using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

internal static class AssistantToolCatalogLoader
{
    public static ImmutableArray<AssistantToolOption> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RequestEngine", "scripts", "games", "skyrimspecialedition", "health", "tool-registry.v1.json");
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("tools").EnumerateArray()
            .Where(tool => tool.GetProperty("observationMode").GetString() is "InProcessRead" or "ExistingOutputRead")
            .Select(tool => new AssistantToolOption(
                new ToolId(tool.GetProperty("toolId").GetString()!),
                tool.GetProperty("displayName").GetString()!,
                AvailabilityState.Available,
                null))
            .OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }
}
