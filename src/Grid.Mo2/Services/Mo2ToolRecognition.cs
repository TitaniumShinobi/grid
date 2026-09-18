using System.Collections.Immutable;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public static class Mo2ToolRecognition
{
    private sealed record Rule(
        Mo2RecognizedToolFamily Family,
        ImmutableArray<string> TitleTokens,
        ImmutableArray<string> BinaryTokens,
        ImmutableArray<string> ProductTokens,
        ImmutableArray<string> ArtifactTokens);

    private static readonly ImmutableArray<Rule> Rules =
    [
        RuleFor(Mo2RecognizedToolFamily.Skse, ["skse", "script extender"], ["skse_loader"], ["skyrim script extender"], ["skse"]),
        RuleFor(Mo2RecognizedToolFamily.SkyrimLauncher, ["skyrim launcher"], ["skyrimlauncher"], ["skyrim launcher"], []),
        RuleFor(Mo2RecognizedToolFamily.SseEdit, ["sseedit", "xedit"], ["sseedit", "xedit"], ["sseedit", "xedit"], ["sseedit cache"]),
        RuleFor(Mo2RecognizedToolFamily.ZEdit, ["zedit"], ["zedit"], ["zedit"], ["zedit"]),
        RuleFor(Mo2RecognizedToolFamily.Synthesis, ["synthesis"], ["synthesis"], ["synthesis"], ["synthesis.esp"]),
        RuleFor(Mo2RecognizedToolFamily.Pandora, ["pandora"], ["pandora"], ["pandora"], ["pandora_engine"]),
        RuleFor(Mo2RecognizedToolFamily.Nemesis, ["nemesis"], ["nemesis"], ["nemesis"], ["nemesis_engine"]),
        RuleFor(Mo2RecognizedToolFamily.Fnis, ["fnis", "generatefnisforusers"], ["generatefnisforusers", "fnis"], ["fnis"], ["tools\\generatefnis_for_users"]),
        RuleFor(Mo2RecognizedToolFamily.Loot, ["loot"], ["loot"], ["loot"], ["loot"]),
        RuleFor(Mo2RecognizedToolFamily.BodySlide, ["bodyslide", "outfit studio"], ["bodyslide", "outfitstudio"], ["bodyslide", "outfit studio"], ["calientetools\\bodyslide"]),
        RuleFor(Mo2RecognizedToolFamily.TexGen, ["texgen"], ["texgen"], ["texgen"], ["textures\\terrain"]),
        RuleFor(Mo2RecognizedToolFamily.DynDoLod, ["dyndolod"], ["dyndolod"], ["dyndolod"], ["dyndolod.esp", "dyndolod.esm"]),
        RuleFor(Mo2RecognizedToolFamily.XLodGen, ["xlodgen", "sse terrain tamriel"], ["xlodgen", "sse terrain tamriel"], ["xlodgen"], ["meshes\\terrain"]),
        RuleFor(Mo2RecognizedToolFamily.WryeBash, ["wrye bash", "bash"], ["wrye bash", "wrye bash launcher"], ["wrye bash"], ["bashed patch"]),
        RuleFor(Mo2RecognizedToolFamily.CreationKit, ["creation kit"], ["creationkit"], ["creation kit"], []),
    ];

    public static Mo2ToolRecognitionResult Recognize(Mo2ToolRecognitionEvidence input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var title = Normalize(input.Title);
        var binary = Normalize(input.BinaryFileName);
        var product = Normalize(input.ProductName);
        var artifacts = input.ArtifactSignals.IsDefault
            ? []
            : input.ArtifactSignals.Select(Normalize).Where(value => value.Length > 0).ToImmutableArray();

        var candidates = Rules.Select(rule =>
        {
            var evidence = ImmutableArray.CreateBuilder<string>();
            if (Matches(title, rule.TitleTokens)) evidence.Add("configured title");
            if (Matches(binary, rule.BinaryTokens)) evidence.Add("canonical binary leaf");
            if (Matches(product, rule.ProductTokens)) evidence.Add("bounded product metadata");
            if (artifacts.Any(value => Matches(value, rule.ArtifactTokens))) evidence.Add("observed artifact signature");
            return (rule.Family, Evidence: evidence.ToImmutable());
        })
        .Where(candidate => candidate.Evidence.Length > 0)
        .OrderByDescending(candidate => candidate.Evidence.Length)
        .ThenBy(candidate => candidate.Family)
        .ToArray();

        if (candidates.Length == 0)
        {
            return new(input.ExecutableId, input.Title, Mo2RecognizedToolFamily.Unknown, Mo2RecognitionConfidence.Unknown, []);
        }

        var best = candidates[0];
        if (candidates.Length > 1 && candidates[1].Evidence.Length == best.Evidence.Length)
        {
            return new(
                input.ExecutableId,
                input.Title,
                Mo2RecognizedToolFamily.Unknown,
                Mo2RecognitionConfidence.Candidate,
                ["multiple tool families matched the same amount of evidence"]);
        }

        var confidence = best.Evidence.Length >= 2
            ? Mo2RecognitionConfidence.Corroborated
            : Mo2RecognitionConfidence.Candidate;
        return new(input.ExecutableId, input.Title, best.Family, confidence, best.Evidence);
    }

    public static Mo2GeneratedOutputKind OutputKind(Mo2RecognizedToolFamily family) => family switch
    {
        Mo2RecognizedToolFamily.BodySlide => Mo2GeneratedOutputKind.BodySlide,
        Mo2RecognizedToolFamily.Pandora => Mo2GeneratedOutputKind.BehaviorGeneration,
        Mo2RecognizedToolFamily.Nemesis => Mo2GeneratedOutputKind.BehaviorGeneration,
        Mo2RecognizedToolFamily.Fnis => Mo2GeneratedOutputKind.BehaviorGeneration,
        Mo2RecognizedToolFamily.Synthesis => Mo2GeneratedOutputKind.Synthesis,
        Mo2RecognizedToolFamily.WryeBash => Mo2GeneratedOutputKind.WryeBash,
        Mo2RecognizedToolFamily.TexGen => Mo2GeneratedOutputKind.TexGen,
        Mo2RecognizedToolFamily.DynDoLod => Mo2GeneratedOutputKind.DynDoLod,
        Mo2RecognizedToolFamily.XLodGen => Mo2GeneratedOutputKind.XLodGen,
        _ => Mo2GeneratedOutputKind.UserDesignatedMod,
    };

    private static Rule RuleFor(
        Mo2RecognizedToolFamily family,
        string[] title,
        string[] binary,
        string[] product,
        string[] artifact) =>
        new(family, [.. title], [.. binary], [.. product], [.. artifact]);

    private static bool Matches(string value, ImmutableArray<string> tokens) =>
        value.Length > 0 && tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().Replace('/', '\\').ToLowerInvariant();
}
