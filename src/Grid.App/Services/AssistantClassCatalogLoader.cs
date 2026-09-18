using System.Collections.Immutable;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

internal static class AssistantClassCatalogLoader
{
    public static ImmutableArray<AssistantClassOption> Load()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "AssistantClasses");
        if (!Directory.Exists(root)) return [];

        var options = Directory.EnumerateFiles(root, "class.v1.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(Read)
            .ToImmutableArray();
        if (options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != options.Length)
            throw new InvalidDataException("The packaged Class registry contains duplicate stable identities.");
        return options;
    }

    private static AssistantClassOption Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var id = Required(root, "classId");
        var displayName = Required(root, "displayName");
        var iconId = Required(root, "iconId");
        var recipeVersion = Required(root, "recipeVersion");
        var coverage = Required(root, "coverage");
        if (coverage is not ("Registered" or "Unsupported"))
            throw new InvalidDataException($"Class '{id}' has unsupported coverage '{coverage}'.");
        var selectionPolicy = root.GetProperty("selectionPolicy");
        var mods = selectionPolicy.GetProperty("mods");
        var tools = selectionPolicy.GetProperty("tools");
        var allowedToolIds = tools.GetProperty("allowedToolIds").EnumerateArray()
            .Select(value => new ToolId(value.GetString() ?? throw new InvalidDataException($"Class '{id}' has a blank allowed tool identity.")))
            .ToImmutableArray();
        var gameplayCapabilities = root.TryGetProperty("gameplayCapabilities", out var capabilityValues)
            ? capabilityValues.EnumerateArray().Select(value => new AssistantGameplayCapabilityOption(
                Required(value, "capabilityId"), Required(value, "displayName"),
                value.GetProperty("intentPhrases").EnumerateArray()
                    .Select(phrase => phrase.GetString() ?? throw new InvalidDataException($"Class '{id}' has a blank gameplay-capability intent phrase."))
                    .ToImmutableArray())).ToImmutableArray()
            : [];
        return new(id, displayName, iconId, coverage == "Registered", recipeVersion,
            mods.GetProperty("minimum").GetInt32(), tools.GetProperty("minimum").GetInt32(), allowedToolIds, gameplayCapabilities);
    }

    private static string Required(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Class recipe is missing required string '{property}'.");
        return value.GetString()!;
    }
}
