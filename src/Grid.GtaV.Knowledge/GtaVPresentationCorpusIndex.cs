using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

public sealed record GtaVPresentationText(FrozenSourceArtifact Artifact, GtaVIndexedGxt2Entry Entry);
public sealed record GtaVActorPresentationRow(int Case, uint ModelHash, int Category, string LabelKey, string CategoryLabelKey);
public sealed record GtaVVehicleClassPresentationRow(string EnumName, int Index, string LabelKey);
public sealed record GtaVGeneratedTextDiagnostic(string Coordinate, string Locale, string Reason, SourceArtifactId ArtifactId);

/// <summary>Bounded static tables from exact, revision-pinned reference bytes. No model-name inference.</summary>
public sealed class GtaVPresentationCorpusIndex
{
    public const string ReferenceFormatId = "grid.gta-v.presentation-reference-utf8";
    public const string GxtFormatId = "rockstar.gta-v.gxt2-binary";
    public const string ReferenceRevision = "7a800e34172bc50f13b07884ca55942028e8c8d0";
    private readonly GtaVSupportedSourceCorpusIndex sourceIndex;
    private readonly ImmutableDictionary<string, string> generatedLocales;
    public ImmutableDictionary<string, FrozenSourceArtifact> Artifacts { get; }
    public ImmutableArray<GtaVActorPresentationRow> Actors { get; }
    public ImmutableDictionary<string, GtaVVehicleClassPresentationRow> VehicleClasses { get; }
    public ImmutableArray<GtaVGeneratedTextDiagnostic> GeneratedTextDiagnostics { get; }
    public FrozenSourceArtifact VehicleClassTable => Artifacts.Values.Single(x => x.SourceCoordinate.ExactRepresentation == "https://gtamods.com/mediawiki/index.php?title=Vehicles.meta&oldid=19769#vehicleClass");
    public FrozenSourceArtifact VehicleClassNative => Artifacts["https://raw.githubusercontent.com/citizenfx/natives/7263f2118982857a08787f81045fbdc3defa1d70/VEHICLE/GetVehicleClass.md"];
    public FrozenSourceArtifact Director => Reference("director_mode.c");
    public FrozenSourceArtifact Freemode => Reference("freemode.c");
    public FrozenSourceArtifact RaceCreator => Reference("fm_race_creator.c");
    public FrozenSourceArtifact LanguageEnum => Artifacts["https://raw.githubusercontent.com/citizenfx/natives/6e8b306e5b2d9bef343a456f89712319e87f9bf4/LOCALIZATION/GetCurrentLanguage.md"];

    public GtaVPresentationCorpusIndex(IEnumerable<FrozenSourceArtifact> artifacts)
    {
        Artifacts = artifacts.ToImmutableDictionary(x => x.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);
        sourceIndex = new GtaVSupportedSourceCorpusIndex(Artifacts.Values);
        RequireDigest(Artifacts["x64b.rpf!/data/lang/american_rel.rpf!/dir_mnu.gxt2"], "17cefe7e906f32e04b8ffde288674c6ead1d3078a6f7bfd0956253b45f2dd9a8");
        RequireDigest(Artifacts["x64b.rpf!/data/lang/american_rel.rpf!/global.gxt2"], "3a2f26f398812b3852df8ebe97d1720b9c2810a669844eeb53579a75ca0e6418");
        var director = Text(Director);
        RequireDigest(Director, "f310a45e1522343628b35c0e88a21358a1cdec5138fc00712fe055abd3f47400");
        RequireDigest(Freemode, "cc6becd2109c1b6ea7615858b7916db95e83b2d777f7246c5d3a7bbf3738174b");
        RequireDigest(RaceCreator, "5cb33206a4bfa51b9b5c8f78600e3fb19ecfd0def5948ac2aeac379ef6242798");
        RequireDigest(LanguageEnum, "ca949f3b8c3163fff4823a1bbe2c1d190db5864ee52767e0357fd208b995163b");
        generatedLocales = ParseGeneratedLocaleMap(Text(Freemode));
        Actors = ParseActors(director);
        VehicleClasses = ParseVehicleClasses(Text(VehicleClassTable));
        RequireDigest(VehicleClassTable, "ac330d3a795019978d4eeccd878980b25443775b6e3504d03a59248e1561503e");
        RequireDigest(VehicleClassNative, "e6da9b42addcbf4b7527a7c02752d5d96bb94feb7f514f730fda8a07f47e71b2");
        if (!Text(VehicleClassNative).Contains("VEH_CLASS_", StringComparison.Ordinal)) throw new InvalidDataException("Missing class localization construction.");
        var diagnostics = ImmutableArray.CreateBuilder<GtaVGeneratedTextDiagnostic>();
        foreach (var artifact in Artifacts.Values.Where(x => x.DeclaredFormat.FormatId == GtaVUgcMissionKnowledgeAdapter.FormatId))
        {
            var coordinate = artifact.SourceCoordinate.ExactRepresentation;
            var locale = GeneratedLocale(coordinate);
            if (locale is null) continue;
            using var json = JsonDocument.Parse(artifact.ExactBytes.AsMemory());
            var text = json.RootElement.GetProperty("mission").GetProperty("gen").GetProperty("nm").GetString()!;
            var reason = text.Contains('\uFFFD') ? "source-text-contains-replacement-character" : locale != "en-US" ? "non-English-text-quality-unverified" : null;
            if (reason is not null) diagnostics.Add(new(coordinate, locale, reason, artifact.Id));
        }
        GeneratedTextDiagnostics = diagnostics.OrderBy(x => x.Coordinate, StringComparer.Ordinal).ToImmutableArray();
    }

    public FrozenSourceArtifact Reference(string path) => Artifacts.Values.Single(x => x.SourceCoordinate.ExactRepresentation == $"https://raw.githubusercontent.com/root-cause/v-decompiled-scripts/{ReferenceRevision}/{path}");
    public static string Text(FrozenSourceArtifact source) => new UTF8Encoding(false, true).GetString(source.ExactBytes.AsSpan());
    public static void RequireDigest(FrozenSourceArtifact source, string digest)
    {
        if (source.Digest != ContentDigest.ComputeSha256(source.ExactBytes.AsSpan()) ||
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source.ExactBytes.AsSpan())).ToLowerInvariant() != digest)
            throw new InvalidDataException("Presentation source differs from its approved immutable pin.");
    }

    public GtaVPresentationText? ResolveText(string key, bool director = false)
    {
        var names = director ? new[] { "dir_mnu.gxt2", "global.gxt2" } : new[] { "global.gxt2" };
        var matches = new List<GtaVPresentationText>();
        foreach (var name in names)
        {
            var artifact = Artifacts.Values.Single(x => x.SourceCoordinate.ExactRepresentation == "x64b.rpf!/data/lang/american_rel.rpf!/" + name);
            if (sourceIndex.GetGxt2(artifact, 4 * 1024 * 1024).TryGetValue(Hash(key), out var value)) matches.Add(new(artifact, value));
        }
        return matches.Select(x => x.Entry.Text).Distinct(StringComparer.Ordinal).Count() == 1 ? matches[0] : null;
    }

    public static uint Hash(string value)
    {
        uint h = 0;
        foreach (var b in Encoding.UTF8.GetBytes(value.ToLowerInvariant())) { h += b; h += h << 10; h ^= h >> 6; }
        h += h << 3; h ^= h >> 11; h += h << 15; return h;
    }

    public static ImmutableDictionary<string, GtaVVehicleClassPresentationRow> ParseVehicleClasses(string html)
    {
        var result = ImmutableDictionary.CreateBuilder<string, GtaVVehicleClassPresentationRow>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(html, @"<tr>\s*<td>\s*([0-9]+)\s*</td>\s*<td>\s*(VC_[A-Z_]+)\s*</td>", RegexOptions.CultureInvariant))
        {
            var number = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value;
            if (!result.TryAdd(name, new(name, number, "VEH_CLASS_" + number))) throw new InvalidDataException("Duplicate vehicle class reference row.");
        }
        if (result.Count != 23 || result.Values.Select(x => x.Index).Distinct().Count() != 23) throw new InvalidDataException("Vehicle class reference table is incomplete or ambiguous.");
        return result.ToImmutable();
    }

    public static string Function(string source, int number)
    {
        var match = Regex.Match(source, @"(?m)^[^\r\n]*\bfunc_" + number + @"\([^\r\n]*\)[^\r\n]*\r?\n\{");
        if (!match.Success) throw new InvalidDataException("Required bounded reference function absent.");
        int start = source.IndexOf('{', match.Index), depth = 1, at = start + 1;
        for (; at < source.Length && depth != 0; at++) { if (source[at] == '{') depth++; else if (source[at] == '}') depth--; }
        if (depth != 0) throw new InvalidDataException("Unbalanced bounded reference function.");
        return source[start..at];
    }

    public static ImmutableArray<GtaVActorPresentationRow> ParseActors(string director)
    {
        var labels = Regex.Matches(Regex.Replace(Function(director, 167), @"/\*.*?\*/", "", RegexOptions.Singleline), "case ([0-9]+):\\s*return \"([^\"]+)\"\\s*;\\s*break;")
            .ToDictionary(x => int.Parse(x.Groups[1].Value), x => x.Groups[2].Value);
        var models = Regex.Matches(Function(director, 861), "case ([0-9]+):\\s*return joaat\\(\"([^\"]+)\"\\);\\s*break;")
            .ToDictionary(x => int.Parse(x.Groups[1].Value), x => Hash(x.Groups[2].Value));
        var categories = Function(director, 903);
        int[] admitted = [35, 37, 38, 39, 41, 42, 44, 46, 47, 48, 49, 50];
        var result = ImmutableArray.CreateBuilder<GtaVActorPresentationRow>();
        foreach (var category in admitted)
        {
            var match = Regex.Match(categories, @"case " + category + @":(?<body>.*?)(?=\n\s*case |\z)", RegexOptions.Singleline);
            if (!match.Success || !labels.TryGetValue(category, out var categoryLabel)) throw new InvalidDataException("Missing Director category.");
            foreach (var actorCase in Regex.Matches(match.Groups["body"].Value, @"func_90[456]\(([0-9]+),").Select(x => int.Parse(x.Groups[1].Value)).Distinct())
                if (models.TryGetValue(actorCase, out var model) && labels.TryGetValue(actorCase, out var label)) result.Add(new(actorCase, model, category, label, categoryLabel));
        }
        return result.OrderBy(x => x.Case).ThenBy(x => x.Category).ToImmutableArray();
    }

    /// <summary>Only explicit source language branches admitted; never infer language from prose.</summary>
    public bool IsEnglishGeneratedCoordinate(string coordinate) => GeneratedLocale(coordinate) == "en-US";
    public string? GeneratedLocale(string coordinate)
    {
        var member = coordinate[(coordinate.LastIndexOf('/') + 1)..];
        if (Artifacts.Values.Count(x => x.DeclaredFormat.FormatId == GtaVUgcMissionKnowledgeAdapter.FormatId &&
                x.SourceCoordinate.ExactRepresentation.EndsWith("/" + member, StringComparison.OrdinalIgnoreCase)) != 1) return null;
        return ResolveGeneratedLocale(generatedLocales, coordinate);
    }
    public static string? ParseGeneratedLocale(string source, string coordinate)
        => ResolveGeneratedLocale(ParseGeneratedLocaleMap(source), coordinate);
    private static ImmutableDictionary<string, string> ParseGeneratedLocaleMap(string source)
    {
        var function = Function(source, 19770);
        string[] locales = ["en-US", "fr-FR", "de-DE", "it-IT", "es-ES", "pt-BR", "pl-PL", "ru-RU", "ko-KR", "zh-TW", "ja-JP", "es-MX", "zh-CN"];
        var resourceLocales = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cayo = Regex.Match(Function(source, 19771), "return \"([^\"]+)\";");
        foreach (Match branch in Regex.Matches(function, @"case ([0-9]+):(?<body>.*?)(?=\n\s*case |\n\s*default:|\z)", RegexOptions.Singleline))
        {
            int language = int.Parse(branch.Groups[1].Value);
            if (language >= locales.Length) continue;
            foreach (Match literal in Regex.Matches(branch.Groups["body"].Value, "StringCopy\\(&Var0, \"([A-Za-z0-9_-]+)\", 32\\);"))
                resourceLocales[literal.Groups[1].Value] = locales[language];
            var suffix = Regex.Match(branch.Groups["body"].Value, "StringConCat\\(&Var0, \"([0-9ABC])\", 32\\)");
            if (suffix.Success && cayo.Success) resourceLocales[cayo.Groups[1].Value + suffix.Groups[1].Value] = locales[language];
        }
        return resourceLocales.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }
    private static string? ResolveGeneratedLocale(IReadOnlyDictionary<string, string> resourceLocales, string coordinate)
    {
        var member = coordinate[(coordinate.LastIndexOf('/') + 1)..];
        return coordinate.StartsWith("update/update2.rpf!/common/data/ugc/", StringComparison.Ordinal) || coordinate.StartsWith("common.rpf!/data/ugc/", StringComparison.Ordinal)
            ? member.EndsWith("_00.ugc", StringComparison.Ordinal) && resourceLocales.TryGetValue(member[..^7], out var locale) ? locale : null : null;
    }
}
