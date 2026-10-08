using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.App.Controls;

/// <summary>Local DIF structure from GRID.md. These paths are user context, never canonical record identities.</summary>
public sealed record SelectorScaffoldNode(
    string Key,
    string Label,
    ImmutableArray<SelectorScaffoldNode> Children,
    ImmutableArray<string> SortViews);

public sealed record SelectorScaffoldSelection(KnowledgeKind Kind, string PathId, string DisplayPath);

public static class SelectorScaffoldContract
{
    private static SelectorScaffoldNode N(string key, string label, params SelectorScaffoldNode[] children) =>
        new(key, label, children.ToImmutableArray(), []);

    private static SelectorScaffoldNode Views(SelectorScaffoldNode node, params string[] views) =>
        node with { SortViews = views.ToImmutableArray() };

    private static ImmutableArray<SelectorScaffoldNode> Paths(IEnumerable<SelectorScaffoldNode> nodes, string prefix = "") =>
        nodes.Select(node =>
        {
            var key = prefix.Length == 0 ? node.Key : prefix + "/" + node.Key;
            return node with { Key = key, Children = Paths(node.Children, key) };
        }).ToImmutableArray();

    private static readonly ImmutableArray<SelectorScaffoldNode> Location = Paths([
        N("world", "World", N("continent", "Continent", N("country", "Country", N("state", "State",
            N("county-region", "County/Region", N("city", "City", N("town-neighborhood", "Town/Neighborhood",
                N("street", "Street", N("structure", "Structure", N("room", "Room"))))))))))]);

    private static readonly ImmutableArray<SelectorScaffoldNode> Mission = Paths([
        Views(N("dlc", "DLC", N("job", "Job")), "Storyline", "Job Type"),
        Views(N("mod", "Mod", N("job", "Job")), "Storyline", "Job Type"),
        Views(N("online", "Online", N("job", "Job")), "Storyline", "Job Type"),
        Views(N("story-mode", "Story Mode", N("job", "Job")), "Storyline", "Job Type")]);

    private static readonly ImmutableArray<SelectorScaffoldNode> Item = Paths([
        N("ammunition-projectile", "Ammunition/Projectile", N("ammo-types", "Ammo Types", N("ammo", "Ammo"))),
        N("clutter-props", "Clutter/Props",
            N("equipment", "Equipment", N("equipment-type", "Equipment Type", N("equipment", "Equipment"))),
            N("furniture", "Furniture", N("furniture-type", "Furniture Type", N("furniture", "Furniture"))),
            N("tools", "Tools", N("type", "Type", N("tool-type", "Tool Type", N("tool", "Tool"))))),
        N("consumables", "Consumables",
            N("drinks", "Drinks", N("drink-type", "Drink Type", N("drink", "Drink"))),
            N("drugs", "Drugs", N("drud-type", "Drud Type", N("drug", "Drug"))),
            N("food", "Food", N("food-type", "Food Type", N("food-item", "Food item"))),
            N("ingredients", "Ingredients", N("ingredient-type", "Ingredient Type (Elemental, Faunal, Floral, Mechanical)", N("ingredient", "Ingredient"))),
            N("posions", "Posions", N("posion-type", "Posion Type", N("posion", "Posion"))),
            N("potions", "Potions", N("potion-type", "Potion Type", N("potion", "Potion"))),
            N("soul-gems", "Soul Gems", N("gem-type", "Gem Type"))),
        N("magic", "Magic", N("magic-class", "Magic Class", N("spell", "Spell")),
            N("powers", "Powers", N("ability-class", "Ability Class", N("ability", "Ability")))),
        Views(N("vehicles", "Vehicles", N("model", "Model")), "Vehicle Class", "Manufacturer"),
        Views(N("weapons", "Weapons", N("weapon", "Weapon")), "Weapon Type", "Class"),
        N("wearables", "Wearables",
            N("accessories", "Accessories", N("type-class", "Type/Class", N("item", "Item"))),
            N("head", "Head", N("type-class", "Type/Class", N("item", "Item"))),
            N("top", "Top", N("type-class", "Type/Class", N("item", "Item"))),
            N("bottom", "Bottom", N("type-class", "Type/Class", N("item", "Item"))),
            N("hands", "Hands", N("type-class", "Type/Class", N("item", "Item"))),
            N("feet", "Feet", N("type-class", "Type/Class", N("item", "Item"))),
            N("outfit", "Outfit", N("outfit-class", "Outfit Class", N("outfit", "Outfit"))))]);

    private static readonly ImmutableArray<SelectorScaffoldNode> Actor = Paths([
        N("player-character", "Player Character", N("actor", "Actor"), N("actress", "Actress")),
        N("non-player-character", "Non Player Character", N("faction", "Faction", N("actor", "Actor"), N("actress", "Actress")))]);

    public static string Title(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Location => "Location", KnowledgeKind.MissionQuest => "Mission",
        KnowledgeKind.Item => "Item", KnowledgeKind.Actor => "Actor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ImmutableArray<SelectorScaffoldNode> Roots(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Location => Location, KnowledgeKind.MissionQuest => Mission,
        KnowledgeKind.Item => Item, KnowledgeKind.Actor => Actor,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static IEnumerable<SelectorScaffoldNode> Enumerate(KnowledgeKind kind) => Flatten(Roots(kind));

    private static IEnumerable<SelectorScaffoldNode> Flatten(IEnumerable<SelectorScaffoldNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var descendant in Flatten(node.Children)) yield return descendant;
        }
    }

    public static SelectorScaffoldNode? Resolve(KnowledgeKind kind, string pathId) =>
        Enumerate(kind).SingleOrDefault(node => string.Equals(node.Key, pathId, StringComparison.Ordinal));

    public static string DisplayPath(KnowledgeKind kind, string pathId)
    {
        if (Resolve(kind, pathId) is null) throw new ArgumentException("Unknown scaffold path.", nameof(pathId));
        var labels = new List<string> { Title(kind) };
        var prefix = string.Empty;
        foreach (var segment in pathId.Split('/'))
        {
            prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
            labels.Add(Resolve(kind, prefix)!.Label);
        }
        return string.Join(" → ", labels);
    }
}
