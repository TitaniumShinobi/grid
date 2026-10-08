using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public sealed record GtaVNativeZone(string Code, string EnglishName, CanonicalKnowledgeRecord Record, ImmutableArray<string> CaseVariantCodes);

public sealed record GtaVLocationNode(
    string Key,
    string PrimaryName,
    ImmutableArray<string> Aliases,
    GtaVNativeZone? Native,
    ImmutableArray<GtaVLocationPageFacts> Pages,
    decimal? Level,
    string LevelBasis,
    ImmutableArray<GtaVLocationLevelVote> LevelVotes);

public sealed record GtaVLocationEdge(
    string ChildKey,
    string ParentKey,
    ImmutableArray<GtaVLocationEvidenceSpan> Evidence)
{
    public GtaVLocationEvidenceSpan Primary => Evidence[0];
}

/// <summary>One coverage decision: examined surface, outcome and reason.</summary>
public sealed record GtaVLocationLedgerEntry(string Surface, string Subject, string Outcome, string Reason, string? Coordinate, string? Locator);

public sealed record GtaVLocationResolution(
    ImmutableArray<GtaVLocationNode> Nodes,
    ImmutableArray<GtaVLocationEdge> Edges,
    ImmutableArray<GtaVLocationLedgerEntry> Ledger,
    ImmutableArray<GtaVNativeZone> NativeZones,
    ImmutableArray<GtaVLocationNode> UnresolvedNodes);

/// <summary>
/// Resolves generic grammar statements into Location nodes and containment edges. Native population-zone records
/// are every node's first identity source; reference pages join by unique exact English name.
/// </summary>
public static class GtaVLocationRegistrationResolver
{
    public const string PopulationZoneNamespace = "rockstar.gta-v.enhanced.population-zones";
    public const string NativeZoneDefaultBasis = "native-population-zone-area";

    public static ImmutableArray<GtaVNativeZone> NativeZones(CanonicalCatalogPayload payload, List<GtaVLocationLedgerEntry>? ledger = null)
    {
        var names = payload.TerminologyAssertions
            .Where(t => t.Role == TerminologyAssertionRole.PrimaryName && t.LanguageTag == "en-US")
            .GroupBy(t => t.KnowledgeRecordId.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(t => t.VerbatimValue).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var zones = ImmutableArray.CreateBuilder<GtaVNativeZone>();
        // Zone codes hash case-insensitively in the game, so case variants are one zone.
        foreach (var group in payload.KnowledgeRecords
                     .Where(r => r.Kind == KnowledgeKind.Location && r.NativeIdentity.Namespace == PopulationZoneNamespace && r.NativeIdentity.ObjectType == "NameLabel")
                     .GroupBy(r => r.NativeIdentity.ExactRepresentation.ToUpperInvariant(), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var records = group.OrderBy(r => r.Id.Value, StringComparer.Ordinal).ToArray();
            var labels = records.SelectMany(r => names.GetValueOrDefault(r.Id.Value) ?? []).Distinct(StringComparer.Ordinal).ToArray();
            var codes = records.Select(r => r.NativeIdentity.ExactRepresentation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            if (labels.Length != 1)
            {
                ledger?.Add(new("native-zone", group.Key, "unresolved", labels.Length == 0 ? "no-english-presentation-name" : "conflicting-english-names", null, "NameLabel/" + string.Join(",", codes)));
                continue;
            }
            zones.Add(new(records[0].NativeIdentity.ExactRepresentation, labels[0], records[0], codes));
            if (codes.Length > 1)
                ledger?.Add(new("native-zone", group.Key, "resolved", "case-variant-codes-merged:" + string.Join(",", codes), null, "NameLabel/" + records[0].NativeIdentity.ExactRepresentation));
        }
        return zones.ToImmutable();
    }

    public static GtaVLocationResolution Resolve(GtaVLocationRegistrationCorpus corpus, CanonicalCatalogPayload payload)
    {
        var ledger = new List<GtaVLocationLedgerEntry>();
        var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value;
        var markers = corpus.ProductScopeMarkers;
        var zones = NativeZones(payload, ledger);
        var zonesByName = zones.GroupBy(z => z.EnglishName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var facts = corpus.Pages.Select(p => GtaVLocationEvidenceGrammar.Parse(p, markers)).ToArray();

        // Identity: pages sharing a subject name are one place; an alias joins only a unique other subject.
        var parent = Enumerable.Range(0, facts.Length).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        var bySubject = Enumerable.Range(0, facts.Length).GroupBy(i => facts[i].SubjectName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var group in bySubject.Values) foreach (var i in group.Skip(1)) Union(group[0], i);
        for (var i = 0; i < facts.Length; i++)
        {
            foreach (var alias in facts[i].Aliases)
            {
                if (!bySubject.TryGetValue(alias, out var others)) continue;
                var distinct = others.Select(Find).Distinct().ToArray();
                if (distinct.Length == 1) Union(i, distinct[0]);
                else ledger.Add(new("reference-identity", facts[i].SubjectName, "ambiguous", "alias-matches-multiple-subjects:" + alias, facts[i].Page.Coordinate, "lead/0/bold"));
            }
        }

        // Native join: a page group binds a native zone by a unique exact English name, subjects before aliases.
        var groups = Enumerable.Range(0, facts.Length).GroupBy(Find).Select(g => g.Select(i => facts[i]).ToArray()).ToList();
        var nativeForGroup = new Dictionary<int, GtaVNativeZone>();
        var ambiguousGroups = new HashSet<int>();
        for (var g = 0; g < groups.Count; g++)
        {
            var subjects = groups[g].Select(f => f.SubjectName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var aliases = groups[g].SelectMany(f => f.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var tier in new[] { subjects, aliases })
            {
                var hits = tier.Where(zonesByName.ContainsKey).SelectMany(n => zonesByName[n]).DistinctBy(z => z.Code).ToArray();
                if (hits.Length == 1) { nativeForGroup[g] = hits[0]; break; }
                if (hits.Length > 1)
                {
                    ambiguousGroups.Add(g);
                    ledger.Add(new("native-join", subjects[0], "ambiguous", "english-name-matches-native-zones:" + string.Join(",", hits.Select(h => h.Code)),
                        groups[g][0].Page.Coordinate, "subject"));
                    break;
                }
            }
        }
        // Several page groups bound to one zone are one place.
        foreach (var byZone in nativeForGroup.GroupBy(p => p.Value.Code).Where(x => x.Count() > 1).ToArray())
        {
            var keep = byZone.Min(p => p.Key);
            foreach (var other in byZone.Where(p => p.Key != keep).Select(p => p.Key).ToArray())
            {
                groups[keep] = [.. groups[keep], .. groups[other]];
                groups[other] = [];
                nativeForGroup.Remove(other);
            }
        }

        var nodes = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        var nodeByName = new Dictionary<string, List<MutableNode>>(StringComparer.OrdinalIgnoreCase);
        var nodeByPage = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        void IndexName(string name, MutableNode node)
        {
            if (!nodeByName.TryGetValue(name, out var list)) nodeByName[name] = list = [];
            if (!list.Contains(node)) list.Add(node);
        }
        foreach (var zone in zones)
        {
            var node = new MutableNode(zone.Record.Id.Value, zone.EnglishName, zone);
            nodes[node.Key] = node;
            IndexName(zone.EnglishName, node);
        }
        var referenceNodeByPrimary = new Dictionary<string, MutableNode>(StringComparer.Ordinal);
        for (var g = 0; g < groups.Count; g++)
        {
            if (groups[g].Length == 0) continue;
            var pages = groups[g].OrderBy(f => f.Page.ReferenceSourceId, StringComparer.Ordinal).ThenBy(f => f.Page.Title, StringComparer.Ordinal).ToArray();
            if (ambiguousGroups.Contains(g))
            {
                foreach (var f in pages)
                    ledger.Add(new("reference-page", f.Page.Title, "ambiguous", "native-identity-ambiguous", f.Page.Coordinate, "subject"));
                continue;
            }
            MutableNode node;
            if (nativeForGroup.TryGetValue(g, out var zone)) node = nodes[zone.Record.Id.Value];
            else
            {
                var primary = pages.GroupBy(f => f.SubjectName, StringComparer.Ordinal)
                    .OrderByDescending(x => x.Count()).ThenBy(x => x.Key.Length).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
                if (!referenceNodeByPrimary.TryGetValue(primary, out node!))
                    referenceNodeByPrimary[primary] = node = new MutableNode(string.Empty, primary, null);
            }
            foreach (var f in pages)
            {
                node.Pages.Add(f);
                nodeByPage[f.Page.ReferenceSourceId + "\n" + f.Page.Title] = node;
                foreach (var alias in f.Page.RedirectAliases) nodeByPage.TryAdd(f.Page.ReferenceSourceId + "\n" + alias, node);
                IndexName(f.SubjectName, node);
                foreach (var alias in f.Aliases) { node.Aliases.Add(alias); IndexName(alias, node); }
                if (!f.SubjectName.Equals(node.PrimaryName, StringComparison.Ordinal)) node.Aliases.Add(f.SubjectName);
                ledger.Add(new("reference-page", f.Page.Title, "resolved", node.Native is null ? "reference-geography-identity" : "native-zone-join:" + node.Native.Code,
                    f.Page.Coordinate, "subject"));
            }
        }
        foreach (var node in referenceNodeByPrimary.Values)
        {
            node.Key = GtaVLocationRegistrationRules.ReferenceEntityKey(gameId, GtaVLocationRegistrationRules.IdentityPage(node.Pages));
            nodes[node.Key] = node;
        }

        MutableNode? ResolveLink(GtaVLocationReferencePage page, GtaVLocationWikiLink link, out string reason)
        {
            reason = string.Empty;
            if (nodeByPage.TryGetValue(page.ReferenceSourceId + "\n" + link.Target, out var byPage)) return byPage;
            foreach (var candidate in new[] { GtaVLocationEvidenceGrammar.SubjectName(link.Target, markers), link.Label })
            {
                if (!nodeByName.TryGetValue(candidate, out var list)) continue;
                if (list.Count == 1) return list[0];
                reason = "link-name-matches-multiple-nodes:" + candidate;
                return null;
            }
            reason = "link-target-not-admitted-location";
            return null;
        }
        MutableNode? ResolveName(string name, out string reason)
        {
            reason = string.Empty;
            if (nodeByName.TryGetValue(name, out var list))
            {
                if (list.Count == 1) return list[0];
                reason = "name-matches-multiple-nodes";
                return null;
            }
            reason = "container-name-not-admitted-location";
            return null;
        }

        // Statements to raw edges.
        var raw = new Dictionary<(string Child, string Parent), List<GtaVLocationEvidenceSpan>>();
        foreach (var node in nodes.Values)
        {
            foreach (var f in node.Pages)
            {
                foreach (var vote in GtaVLocationEvidenceGrammar.LevelVotes(f)) node.Votes.Add(vote);
                foreach (var statement in GtaVLocationEvidenceGrammar.Statements(f, markers))
                {
                    var span = new GtaVLocationEvidenceSpan(f.Page, statement.Locator, statement.Grammar);
                    MutableNode? child = statement.SubjectIsChild ? node : null;
                    MutableNode? container = statement.SubjectIsParent ? node : null;
                    var reason = string.Empty;
                    if (child is null && statement.ChildLink is { } childLink) child = ResolveLink(f.Page, childLink, out reason);
                    if (container is null && statement.ParentLink is { } parentLink) container = ResolveLink(f.Page, parentLink, out reason);
                    if (container is null && statement.ParentName is { } parentName) container = ResolveName(parentName, out reason);
                    var label = (statement.ChildLink?.Target ?? node.PrimaryName) + " -> " + (statement.ParentLink?.Target ?? statement.ParentName ?? node.PrimaryName);
                    if (child is null || container is null)
                    {
                        ledger.Add(new("statement", label, "unresolved", reason, f.Page.Coordinate, statement.Locator));
                        continue;
                    }
                    if (ReferenceEquals(child, container))
                    {
                        ledger.Add(new("statement", label, "rejected", "self-containment", f.Page.Coordinate, statement.Locator));
                        continue;
                    }
                    if (statement.MemberNoun is { } memberNoun && statement.SubjectIsParent && GtaVLocationPlaceVocabulary.TryHeadLevel(memberNoun, out var memberLevel, out var noun))
                        child.Votes.Add(new(memberLevel, 1, "member-of-heading:" + noun, span));
                    var edgeKey = (child.Key, container.Key);
                    if (!raw.TryGetValue(edgeKey, out var spans)) raw[edgeKey] = spans = [];
                    spans.Add(span);
                }
            }
        }

        // Levels: weighted votes; native zones default to an area when no reference states otherwise.
        foreach (var node in nodes.Values)
        {
            if (node.Votes.Count == 0)
            {
                if (node.Native is not null) { node.Level = 7m; node.LevelBasis = NativeZoneDefaultBasis; }
                else { node.LevelBasis = "no-level-evidence"; }
                continue;
            }
            var tally = node.Votes.GroupBy(v => v.Level).Select(g => (Level: g.Key, Weight: g.Sum(v => v.Weight), Max: g.Max(v => v.Weight)))
                .OrderByDescending(t => t.Weight).ThenByDescending(t => t.Max).ThenBy(t => t.Level).ToArray();
            node.Level = tally[0].Level;
            node.LevelBasis = string.Join(";", node.Votes.Where(v => v.Level == tally[0].Level).Select(v => v.Signal).Distinct().Order(StringComparer.Ordinal));
            if (tally.Length > 1)
                ledger.Add(new("level", node.PrimaryName, tally.Length > 1 && tally[1].Weight == tally[0].Weight ? "ambiguous" : "resolved",
                    "level-votes:" + string.Join(",", tally.Select(t => t.Level.ToString(CultureInfo.InvariantCulture) + "=" + t.Weight.ToString(CultureInfo.InvariantCulture))),
                    null, null));
        }

        // Level order: a container sits strictly above its member. An equal-level member nests half a level below
        // its container; an inverted edge contradicts the level evidence and is rejected.
        var edges = raw.Select(p => (Child: p.Key.Child, Parent: p.Key.Parent, Spans: p.Value)).ToList();
        foreach (var edge in edges.ToArray())
        {
            var child = nodes[edge.Child]; var container = nodes[edge.Parent];
            if (child.Level is null || container.Level is null)
            {
                ledger.Add(new("edge", child.PrimaryName + " -> " + container.PrimaryName, "unresolved", "endpoint-level-unresolved", edge.Spans[0].Page.Coordinate, edge.Spans[0].Locator));
                edges.Remove(edge);
            }
        }
        for (var pass = 0; pass < 32; pass++)
        {
            var changed = false;
            foreach (var edge in edges.OrderBy(e => e.Child, StringComparer.Ordinal).ThenBy(e => e.Parent, StringComparer.Ordinal))
            {
                var child = nodes[edge.Child]; var container = nodes[edge.Parent];
                if (child.Level != container.Level || container.Level >= 10m) continue;
                var level = container.Level!.Value;
                child.Level = level == Math.Floor(level) ? level + 0.5m : Math.Ceiling(level);
                if (!child.LevelBasis.Contains(";nested-below-equal-level-container", StringComparison.Ordinal))
                    child.LevelBasis += ";nested-below-equal-level-container";
                changed = true;
            }
            if (!changed) break;
        }
        foreach (var edge in edges.ToArray())
        {
            var child = nodes[edge.Child]; var container = nodes[edge.Parent];
            if (child.Level > container.Level) continue;
            ledger.Add(new("edge", child.PrimaryName + " -> " + container.PrimaryName, "rejected",
                "level-order-contradiction:" + child.Level!.Value.ToString(CultureInfo.InvariantCulture) + "<=" + container.Level!.Value.ToString(CultureInfo.InvariantCulture),
                edge.Spans[0].Page.Coordinate, edge.Spans[0].Locator));
            edges.Remove(edge);
        }

        // Transitive reduction: an edge implied by a longer evidenced path is recorded, not published twice.
        var adjacency = edges.GroupBy(e => e.Child).ToDictionary(g => g.Key, g => g.Select(e => e.Parent).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        bool ReachesWithout(string from, string to, (string, string) skip)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal); var stack = new Stack<string>(); stack.Push(from);
            while (stack.TryPop(out var next))
            {
                if (!adjacency.TryGetValue(next, out var parents)) continue;
                foreach (var p in parents)
                {
                    if ((next, p) == skip) continue;
                    if (p == to) return true;
                    if (seen.Add(p)) stack.Push(p);
                }
            }
            return false;
        }
        var reduced = new List<GtaVLocationEdge>();
        foreach (var edge in edges.OrderBy(e => e.Child, StringComparer.Ordinal).ThenBy(e => e.Parent, StringComparer.Ordinal))
        {
            var spans = edge.Spans.DistinctBy(s => (s.Page.Coordinate, s.Locator))
                .OrderBy(s => GrammarRank(s.Grammar)).ThenBy(s => s.Page.ReferenceSourceId, StringComparer.Ordinal)
                .ThenBy(s => s.Page.Title, StringComparer.Ordinal).ThenBy(s => s.Locator, StringComparer.Ordinal).ToImmutableArray();
            if (ReachesWithout(edge.Child, edge.Parent, (edge.Child, edge.Parent)))
            {
                adjacency[edge.Child].Remove(edge.Parent);
                ledger.Add(new("edge", nodes[edge.Child].PrimaryName + " -> " + nodes[edge.Parent].PrimaryName, "resolved",
                    "implied-by-more-specific-evidenced-path", spans[0].Page.Coordinate, spans[0].Locator));
                continue;
            }
            reduced.Add(new(edge.Child, edge.Parent, spans));
        }

        var finalNodes = nodes.Values.Select(n => n.Freeze()).ToArray();
        foreach (var n in finalNodes.Where(n => n.Level is null))
            ledger.Add(new("node", n.PrimaryName, "unresolved", n.LevelBasis, n.Pages.FirstOrDefault()?.Page.Coordinate, "subject"));
        return new(
            finalNodes.Where(n => n.Level is not null).OrderBy(n => n.Key, StringComparer.Ordinal).ToImmutableArray(),
            reduced.ToImmutableArray(),
            ledger.ToImmutableArray(),
            zones,
            finalNodes.Where(n => n.Level is null).OrderBy(n => n.Key, StringComparer.Ordinal).ToImmutableArray());
    }

    private static int GrammarRank(string grammar) => grammar switch
    {
        GtaVLocationEvidenceGrammar.InfoboxAddressChain => 0,
        GtaVLocationEvidenceGrammar.LeadCopularContainer => 1,
        GtaVLocationEvidenceGrammar.LeadLocatedIn => 2,
        GtaVLocationEvidenceGrammar.LeadContainsMembers => 3,
        GtaVLocationEvidenceGrammar.SectionSelfLocation => 4,
        GtaVLocationEvidenceGrammar.SectionMemberList => 5,
        GtaVLocationEvidenceGrammar.SectionNestedMember => 6,
        _ => 7,
    };

    /// <summary>Deterministic native zone inventory admitted as the registration source for native identities.</summary>
    public static byte[] NativeInventoryBytes(ImmutableArray<GtaVNativeZone> zones)
    {
        var text = new StringBuilder("# grid.gta-v.location-registration.native-zone-inventory.v1\n");
        foreach (var zone in zones.OrderBy(z => z.Code, StringComparer.Ordinal))
            text.Append("NameLabel/").Append(zone.Code).Append('|').Append(zone.EnglishName).Append('|').Append(zone.Record.Id.Value)
                .Append('|').Append(string.Join(",", zone.CaseVariantCodes)).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private sealed class MutableNode(string key, string primaryName, GtaVNativeZone? native)
    {
        public string Key { get; set; } = key;
        public string PrimaryName { get; } = primaryName;
        public GtaVNativeZone? Native { get; } = native;
        public SortedSet<string> Aliases { get; } = new(StringComparer.Ordinal);
        public List<GtaVLocationPageFacts> Pages { get; } = [];
        public List<GtaVLocationLevelVote> Votes { get; } = [];
        public decimal? Level { get; set; }
        public string LevelBasis { get; set; } = string.Empty;

        public GtaVLocationNode Freeze() => new(Key, PrimaryName,
            Aliases.Where(a => !a.Equals(PrimaryName, StringComparison.Ordinal)).ToImmutableArray(), Native,
            Pages.ToImmutableArray(), Level, LevelBasis, Votes.ToImmutableArray());
    }
}
