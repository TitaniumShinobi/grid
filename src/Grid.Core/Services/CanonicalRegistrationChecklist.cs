using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Grid.Core.Services;

public sealed record RegistrationChecklistNode(
    string Id,
    string Selector,
    string? ParentId,
    string Label,
    int Order,
    bool Terminal,
    bool AllowsEntities,
    bool AllowsSpecialization,
    string Purpose,
    string[] SortDimensions,
    string[] FilterDimensions,
    string[] EvidenceRequirements,
    bool RequiresProfile,
    bool RequiresGame);

public sealed record RegistrationChecklistDocument(string Version, RegistrationChecklistNode[] Nodes);

/// <summary>
/// The frozen universal registration checklist. Slots describe required evidence, not populated
/// game facts. Game/profile requirements scope navigation membership, not Tool or Mod identity.
/// </summary>
public sealed class CanonicalRegistrationChecklist
{
    private const string ResourceName = "Grid.Core.Contracts.canonical-registration-checklist.v1.json";
    private static readonly string[] SelectorIds = ["Tool", "Mod", "Location", "MissionQuest", "Item", "Actor"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ImmutableArray<RegistrationChecklistNode> nodes;
    private readonly FrozenDictionary<string, RegistrationChecklistNode> byId;

    public string Version { get; }
    public string Digest { get; }

    // Defensive array copies keep a caller from mutating the loaded contract through record arrays.
    public ImmutableArray<RegistrationChecklistNode> Nodes => nodes.Select(Copy).ToImmutableArray();

    private CanonicalRegistrationChecklist(RegistrationChecklistDocument document, string digest)
    {
        Version = document.Version;
        Digest = digest;
        nodes = document.Nodes.Select(Copy).ToImmutableArray();
        byId = nodes.ToFrozenDictionary(node => node.Id, StringComparer.Ordinal);
    }

    public static CanonicalRegistrationChecklist Load()
    {
        using var stream = typeof(CanonicalRegistrationChecklist).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("The embedded canonical registration checklist is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Parse(bytes.ToArray());
    }

    public static CanonicalRegistrationChecklist Parse(ReadOnlySpan<byte> utf8)
    {
        var text = StrictUtf8.GetString(utf8);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var normalizedBytes = StrictUtf8.GetBytes(text);
        var document = JsonSerializer.Deserialize<RegistrationChecklistDocument>(normalizedBytes,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new InvalidDataException("The registration checklist is empty.");
        Validate(document);
        return new(document, Convert.ToHexString(SHA256.HashData(normalizedBytes)).ToLowerInvariant());
    }

    public RegistrationChecklistNode? Resolve(string id) =>
        byId.TryGetValue(id, out var node) ? Copy(node) : null;

    /// <summary>Entity-slot depth of an anchor (Location/world = 1 ... room = 10); 0 outside an entity-slot chain.</summary>
    public int SlotLevel(string anchorId)
    {
        var level = 0;
        for (var current = byId.GetValueOrDefault(anchorId); current is not null;
             current = current.ParentId is null ? null : byId.GetValueOrDefault(current.ParentId))
            if (current.Purpose == "entity-slot") level++;
        return level;
    }

    /// <summary>Semantic level of a registered entity: its fractional rule level when declared, else its slot depth.</summary>
    public decimal SemanticLevel(string anchorId, decimal? semanticLevel) => semanticLevel ?? SlotLevel(anchorId);

    private static RegistrationChecklistNode Copy(RegistrationChecklistNode node) => node with
    {
        SortDimensions = [.. node.SortDimensions],
        FilterDimensions = [.. node.FilterDimensions],
        EvidenceRequirements = [.. node.EvidenceRequirements],
    };

    private static void Validate(RegistrationChecklistDocument document)
    {
        if (document.Version != "1" || document.Nodes is null || document.Nodes.Length != 98)
            throw new InvalidDataException("Checklist v1 requires exactly 98 authored nodes.");
        var map = new Dictionary<string, RegistrationChecklistNode>(StringComparer.Ordinal);
        foreach (var node in document.Nodes)
        {
            if (node is null || string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Label) ||
                !SelectorIds.Contains(node.Selector, StringComparer.Ordinal) ||
                !Regex.IsMatch(node.Id, @"^(Tool|Mod|Location|MissionQuest|Item|Actor)(/[a-z0-9]+(?:-[a-z0-9]+)*)*$",
                    RegexOptions.CultureInvariant) || !map.TryAdd(node.Id, node))
                throw new InvalidDataException("Checklist nodes require unique, valid identities and labels.");
            if (node.Order < 0 || !node.RequiresGame || !node.RequiresProfile || !node.AllowsSpecialization ||
                node.Purpose is not ("fixed-root" or "fixed-category" or "entity-slot" or "specialization-slot"))
                throw new InvalidDataException("Checklist node semantics are invalid.");
            RequireValues(node.SortDimensions, allowEmpty: true);
            RequireValues(node.FilterDimensions, allowEmpty: true);
            RequireValues(node.EvidenceRequirements, allowEmpty: false);
        }

        var roots = document.Nodes.Where(node => node.ParentId is null).OrderBy(node => node.Order).ToArray();
        if (!roots.Select(node => node.Id).SequenceEqual(SelectorIds, StringComparer.Ordinal))
            throw new InvalidDataException("Checklist requires the six authored selector roots in order.");
        var children = document.Nodes.Where(node => node.ParentId is not null)
            .GroupBy(node => node.ParentId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

        foreach (var node in document.Nodes)
        {
            var root = node.ParentId is null;
            if (root ? node.Id != node.Selector || node.Purpose != "fixed-root" :
                !map.TryGetValue(node.ParentId!, out var parent) || parent.Selector != node.Selector ||
                !node.Id.StartsWith(node.ParentId + "/", StringComparison.Ordinal) ||
                node.Id.LastIndexOf('/') != node.ParentId!.Length || node.Purpose == "fixed-root")
                throw new InvalidDataException("Checklist parent or selector coordinates do not match.");
            if (node.Terminal == children.ContainsKey(node.Id))
                throw new InvalidDataException("Checklist terminal state disagrees with its authored children.");
            var expectsEntities = node.Purpose == "entity-slot" || (root && (node.Selector is "Tool" or "Mod"));
            if (node.AllowsEntities != expectsEntities)
                throw new InvalidDataException("Checklist entity admission does not match its slot purpose.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = node;
            while (true)
            {
                if (!seen.Add(current.Id)) throw new InvalidDataException("Checklist parent cycle detected.");
                if (current.ParentId is null) break;
                if (!map.TryGetValue(current.ParentId, out var next))
                    throw new InvalidDataException("Checklist parent is absent.");
                current = next;
            }
        }
        foreach (var siblings in children.Values.Append(roots))
            if (!siblings.Select(node => node.Order).Order().SequenceEqual(Enumerable.Range(0, siblings.Length)))
                throw new InvalidDataException("Checklist sibling ordering is not contiguous and unique.");
        if (document.Nodes.Count(node => node.Terminal) != 33 ||
            document.Nodes.Sum(node => node.SortDimensions.Length) != 12 ||
            document.Nodes.SelectMany(node => node.SortDimensions).Distinct(StringComparer.Ordinal).Count() != 6 ||
            document.Nodes.Sum(node => node.FilterDimensions.Length) != 1 ||
            !map["Mod"].FilterDimensions.SequenceEqual(["Category"], StringComparer.Ordinal))
            throw new InvalidDataException("Checklist terminal, sort, or filter dimensions do not match v1.");
    }

    private static void RequireValues(string[]? values, bool allowEmpty)
    {
        if (values is null || (!allowEmpty && values.Length == 0) || values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new InvalidDataException("Checklist dimensions and evidence requirements must be distinct nonempty values.");
    }
}
