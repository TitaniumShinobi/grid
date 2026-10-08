using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>A parsed normalized reference extract: infobox, lead prose, place-headed sections and categories.</summary>
public sealed record GtaVLocationPageFacts(
    GtaVLocationReferencePage Page,
    string? InfoboxHeader,
    ImmutableDictionary<string, string> InfoboxFields,
    string Lead,
    ImmutableArray<GtaVLocationPageSection> Sections,
    ImmutableArray<string> Categories,
    string SubjectName,
    ImmutableArray<string> Aliases);

public sealed record GtaVLocationPageSection(int Level, string Heading, string HeadingPath, string Body);

public sealed record GtaVLocationWikiLink(string Target, string Label, int Index);

/// <summary>A located evidence span inside one pinned page extract.</summary>
public sealed record GtaVLocationEvidenceSpan(GtaVLocationReferencePage Page, string Locator, string Grammar);

public sealed record GtaVLocationLevelVote(decimal Level, int Weight, string Signal, GtaVLocationEvidenceSpan? Span);

/// <summary>One generic place-word vocabulary; no game place name appears here.</summary>
public static class GtaVLocationPlaceVocabulary
{
    private static readonly ImmutableDictionary<string, decimal> LevelByNoun = Build(
        (2m, ["continent"]),
        (3m, ["country", "nation"]),
        (4m, ["state", "province", "territory"]),
        (5m, ["county", "region"]),
        (6m, ["city", "municipality", "metropolis", "desert", "wilderness", "range"]),
        (7m, ["town", "village", "hamlet", "settlement", "community", "neighborhood", "neighbourhood", "district",
              "borough", "suburb", "area", "quarter", "locality", "sector", "island", "islands", "peninsula", "beach",
              "bay", "cove", "canyon", "valley", "hill", "hills", "mountain", "peak", "lake", "reservoir", "river",
              "forest", "park", "coast", "field", "fields", "zone", "neighborhoods", "neighbourhoods", "districts",
              "areas", "towns", "suburbs", "communities", "beaches", "parks", "mountains"]),
        (8m, ["street", "streets", "road", "roads", "avenue", "boulevard", "highway", "freeway", "drive", "lane",
              "route", "trail", "tunnel", "bridge", "interstate", "expressway", "tunnels", "bridges"]),
        (9m, ["building", "buildings", "structure", "structures", "business", "businesses", "store", "stores", "shop",
              "shops", "restaurant", "restaurants", "bar", "club", "nightclub", "hotel", "motel", "house", "houses",
              "home", "homes", "mansion", "residence", "safehouse", "safehouses", "apartment", "apartments", "tower",
              "towers", "skyscraper", "bank", "station", "stations", "hospital", "hospitals", "church", "airport",
              "airfield", "airstrip", "port", "harbor", "harbour", "dock", "docks", "base", "prison", "penitentiary",
              "landmark", "landmarks", "stadium", "arena", "compound", "ranch", "farm", "factory", "plant",
              "lighthouse", "dam", "casino", "cemetery", "school", "university", "mall", "market", "marina", "office",
              "offices", "garage", "warehouse", "warehouses", "studio", "studios", "theater", "theatre", "racetrack",
              "track", "facility", "complex", "headquarters", "estate", "property", "properties", "resort", "camp",
              "quarry", "mine", "cave", "plaza", "square", "yard", "terminal", "monument", "statue", "pier",
              "observatory", "laboratory", "lab", "museum", "gallery", "gym", "cinema", "motel", "trailer", "bunker",
              "hangar", "clubhouse", "nightclubs", "arcade", "penthouse", "yacht", "submarine"]),
        (10m, ["room", "rooms", "interior", "interiors", "floor", "basement", "vault"]));

    /// <summary>Plural sub-place headings and noun phrases whose bulleted members are places inside the subject.</summary>
    public static readonly ImmutableHashSet<string> MemberNouns = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "neighborhoods", "neighbourhoods", "districts", "areas", "towns", "cities", "counties", "communities", "suburbs",
        "regions", "states", "places", "landmarks", "streets", "roads", "buildings", "businesses", "stores", "shops",
        "restaurants", "structures", "properties", "settlements", "villages", "islands", "beaches", "parks", "zones",
        "attractions", "sites", "points", "geography", "interiors", "rooms", "facilities", "venues", "residences",
        "homes", "houses", "safehouses", "subdivisions", "sections", "parts", "boroughs", "localities", "sectors");

    /// <summary>"Location"-style singular headings describe where the subject itself is, never its members.</summary>
    public static readonly ImmutableHashSet<string> SelfLocationNouns = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "location", "locale", "whereabouts", "position", "situation");

    private static ImmutableDictionary<string, decimal> Build(params (decimal Level, string[] Nouns)[] groups)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var (level, nouns) in groups)
            foreach (var noun in nouns)
                builder.TryAdd(noun, level);
        return builder.ToImmutable();
    }

    public static IEnumerable<string> Words(string text) =>
        Regex.Matches(text, "[A-Za-z]+", RegexOptions.CultureInvariant).Select(m => m.Value);

    public static bool TryLevel(string noun, out decimal level) => LevelByNoun.TryGetValue(noun, out level);

    /// <summary>Level of a noun phrase from its head noun, the final word; a place word used as a modifier
    /// ("state police agency") names no level.</summary>
    public static bool TryHeadLevel(string phrase, out decimal level, out string noun)
    {
        level = 0; noun = string.Empty;
        var head = Words(phrase).LastOrDefault();
        if (head is null || !LevelByNoun.TryGetValue(head, out level)) return false;
        noun = head.ToLowerInvariant();
        return true;
    }

    public static bool HasMemberNoun(string heading) => Words(heading).Any(MemberNouns.Contains) ||
        Regex.IsMatch(heading, @"(?i)\bplaces?\s+of\s+interest\b");

    public static bool IsSelfLocationHeading(string heading)
    {
        var words = Words(heading).ToArray();
        return words.Length > 0 && words.All(w => SelfLocationNouns.Contains(w) || w.Equals("and", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Generic MediaWiki extract grammars. Every rule applies to any page; nothing names a specific place.</summary>
public static class GtaVLocationEvidenceGrammar
{
    public const string InfoboxAddressChain = "infobox-place-field-chain";
    public const string LeadCopularContainer = "lead-copular-head-in-container";
    public const string LeadLocatedIn = "lead-located-in-container";
    public const string LeadContainsMembers = "lead-contains-member-places";
    public const string SectionMemberList = "section-member-list";
    public const string SectionNestedMember = "section-nested-member-list";
    public const string SectionSelfLocation = "section-self-location";
    public const string CategoryNounInContainer = "category-noun-in-container";

    private static readonly ImmutableHashSet<string> PlaceFields = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        "location", "city", "county", "state", "country", "district", "neighborhood", "neighbourhood", "area", "region",
        "island", "street", "address", "borough", "town", "place", "territory", "province");

    private static readonly Regex Marker = new(@"^\[\[(?<marker>INFOBOX|LEAD|CATEGORIES|SECTION:(?<level>[0-9]):(?<heading>.*))\]\]$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Link = new(@"\[\[(?<target>[^\]\|#]*)(?:#[^\]\|]*)?(?:\|(?<label>[^\]]*))?\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex NonArticleNamespace = new(@"^(?:File|Image|Category|wp|w|wikipedia|Template|User|Help|Special|Media|commons)\s*:",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Copula = new(@"\b(?:is|was|are|were)\s+(?:a|an|the|one\s+of\s+the)\s+", RegexOptions.CultureInvariant);
    private static readonly Regex Located = new(@"\b(?:is|are|was|were)\s+(?:also\s+)?(?:located|situated|set|positioned)\s+(?:(?:just|directly|far|in\s+the\s+\w+\s+of)\s+)?(?<prep>in|within|inside|on|at)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HeadCut = new(@"\b(?:in|on|at|of|within|inside|near|located|situated|found|that|which|who|where|appearing|featured|owned|run|operated|and\s+is|known|seen|used|from|off|between|along|for|to|by)\b|[,;(]",
        RegexOptions.CultureInvariant);
    private static readonly Regex ContainerPrep = new(@"^\s*(?:in|within|inside|on)\b", RegexOptions.CultureInvariant);
    private static readonly Regex ClauseCut = new(@"\b(?:appearing|appears|featured|features|owned|run|operated|that|which|who|where|known|seen|used|and\s+is|and\s+was|serving|bordering|borders|between|near|north|south|east|west|northeast|northwest|southeast|southwest|adjacent|next\s+to|close\s+to)\b|[;(]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex Contains = new(@"\b(?:contains|includes|comprises|encompasses|consists\s+of|is\s+(?:divided|split|subdivided|made\s+up)\s+(?:into|of))\s+(?:the\s+|several\s+|many\s+|a\s+number\s+of\s+|two\s+|three\s+|four\s+|five\s+)?(?:[a-z-]+\s+){0,3}?(?<noun>[a-z]+)\s+(?:of\s+|such\s+as\s+|including\s+|like\s+)?",
        RegexOptions.CultureInvariant);

    public static GtaVLocationPageFacts Parse(GtaVLocationReferencePage page, ImmutableArray<string> productMarkers)
    {
        var text = page.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var markers = Marker.Matches(text).ToArray();
        string? infobox = null; var lead = string.Empty; var categories = ImmutableArray<string>.Empty;
        var sections = ImmutableArray.CreateBuilder<GtaVLocationPageSection>();
        var path = new List<(int Level, string Heading)>();
        for (var i = 0; i < markers.Length; i++)
        {
            var start = markers[i].Index + markers[i].Length;
            var end = i + 1 < markers.Length ? markers[i + 1].Index : text.Length;
            var body = text[start..end].Trim('\n', ' ');
            var marker = markers[i].Groups["marker"].Value;
            if (marker == "INFOBOX") infobox = body;
            else if (marker == "LEAD") lead = body;
            else if (marker == "CATEGORIES")
                categories = body.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToImmutableArray();
            else
            {
                var level = int.Parse(markers[i].Groups["level"].Value, CultureInfo.InvariantCulture);
                var heading = markers[i].Groups["heading"].Value.Trim();
                while (path.Count > 0 && path[^1].Level >= level) path.RemoveAt(path.Count - 1);
                path.Add((level, heading));
                sections.Add(new(level, heading, string.Join(" / ", path.Select(p => p.Heading)), body));
            }
        }
        var fields = infobox is null ? ImmutableDictionary<string, string>.Empty : InfoboxFields(infobox);
        string? header = null;
        if (infobox is not null)
        {
            var match = Regex.Match(infobox, @"^\{\{\s*infobox[\s_]*(?<header>[^|\n}]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success) header = match.Groups["header"].Value.Trim();
        }
        var subject = SubjectName(page.Title, productMarkers);
        var aliases = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match bold in Regex.Matches(FirstSentence(lead), @"'''(?<name>[^']+?)'''", RegexOptions.CultureInvariant))
            AddAlias(aliases, StripMarkup(bold.Groups["name"].Value), subject);
        if (fields.TryGetValue("name", out var infoboxName)) AddAlias(aliases, StripMarkup(infoboxName), subject);
        foreach (var redirect in page.RedirectAliases) AddAlias(aliases, SubjectName(redirect, productMarkers), subject);
        return new(page, header, fields, lead, sections.ToImmutable(), categories, subject, aliases.ToImmutableArray());
    }

    private static void AddAlias(SortedSet<string> aliases, string value, string subject)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length is > 1 and < 80 && !value.Equals(subject, StringComparison.Ordinal) && !value.Contains('{') && !value.Contains('<'))
            aliases.Add(value);
    }

    /// <summary>Page title minus disambiguators: trailing "(...)" and a trailing " in &lt;product marker&gt;".</summary>
    public static string SubjectName(string title, ImmutableArray<string> productMarkers)
    {
        var value = Regex.Replace(title, @"\s*\([^)]*\)\s*$", string.Empty, RegexOptions.CultureInvariant).Trim();
        foreach (var marker in productMarkers)
        {
            var suffix = " in " + marker;
            if (value.EndsWith(suffix, StringComparison.Ordinal)) value = value[..^suffix.Length].Trim();
        }
        return value;
    }

    public static ImmutableDictionary<string, string> InfoboxFields(string infobox)
    {
        var body = infobox.StartsWith("{{", StringComparison.Ordinal) ? infobox[2..] : infobox;
        if (body.EndsWith("}}", StringComparison.Ordinal)) body = body[..^2];
        var parts = new List<string>(); var current = new StringBuilder(); var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (i + 1 < body.Length && (body.AsSpan(i, 2).SequenceEqual("{{") || body.AsSpan(i, 2).SequenceEqual("[[")))
            { depth++; current.Append(body, i, 2); i++; continue; }
            if (i + 1 < body.Length && (body.AsSpan(i, 2).SequenceEqual("}}") || body.AsSpan(i, 2).SequenceEqual("]]")))
            { depth--; current.Append(body, i, 2); i++; continue; }
            if (body[i] == '|' && depth == 0) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(body[i]);
        }
        parts.Add(current.ToString());
        var fields = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            fields[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return fields.ToImmutable();
    }

    public static ImmutableArray<GtaVLocationWikiLink> Links(string text)
    {
        var links = ImmutableArray.CreateBuilder<GtaVLocationWikiLink>();
        foreach (Match match in Link.Matches(text))
        {
            var target = match.Groups["target"].Value.Trim().Replace('_', ' ');
            if (target.Length == 0 || target.StartsWith(':') || NonArticleNamespace.IsMatch(target)) continue;
            target = char.ToUpperInvariant(target[0]) + target[1..];
            var label = match.Groups["label"].Success ? StripMarkup(match.Groups["label"].Value) : target;
            links.Add(new(target, label, match.Index));
        }
        return links.ToImmutable();
    }

    public static string StripMarkup(string text)
    {
        text = Regex.Replace(text, @"\{\{[^{}]*\}\}", string.Empty);
        text = Regex.Replace(text, @"\[\[(?:[^|\]]+)\|([^\]]+)\]\]", "$1");
        text = Regex.Replace(text, @"\[\[([^\]]+)\]\]", "$1");
        text = Regex.Replace(text, @"'{2,}", string.Empty);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>The lead's first sentence with link depth respected: a period inside [[...]] or {{...}} does not end it.</summary>
    public static string FirstSentence(string lead) => Sentences(lead).FirstOrDefault() ?? string.Empty;

    public static IEnumerable<string> Sentences(string text)
    {
        var depth = 0; var start = 0;
        text = Regex.Replace(text, @"\s+", " ");
        for (var i = 0; i < text.Length; i++)
        {
            if (i + 1 < text.Length && (text.AsSpan(i, 2).SequenceEqual("[[") || text.AsSpan(i, 2).SequenceEqual("{{"))) { depth++; i++; continue; }
            if (i + 1 < text.Length && (text.AsSpan(i, 2).SequenceEqual("]]") || text.AsSpan(i, 2).SequenceEqual("}}"))) { depth = Math.Max(0, depth - 1); i++; continue; }
            if (depth == 0 && text[i] == '.' && (i + 1 == text.Length || (text[i + 1] == ' ' && i + 2 < text.Length && (char.IsUpper(text[i + 2]) || text[i + 2] == '[' || text[i + 2] == '\''))))
            {
                var sentence = text[start..(i + 1)].Trim();
                if (sentence.Length > 0) yield return sentence;
                start = i + 1;
            }
        }
        var rest = text[start..].Trim();
        if (rest.Length > 0) yield return rest;
    }

    /// <summary>Descriptor noun phrase of a copular sentence ("X is a fictional state in ..."), cut at its first preposition.</summary>
    public static bool TryCopularHead(string sentence, out string head, out string remainder)
    {
        head = string.Empty; remainder = string.Empty;
        var plain = Regex.Replace(sentence, @"\{\{[^{}]*\}\}", string.Empty);
        var match = Copula.Match(plain);
        if (!match.Success) return false;
        var after = plain[(match.Index + match.Length)..];
        var cut = HeadCut.Match(StripLinksPreservingLength(after));
        head = StripMarkup(cut.Success ? after[..cut.Index] : after);
        remainder = cut.Success ? after[cut.Index..] : string.Empty;
        return head.Length > 0;
    }

    /// <summary>Replaces link and template bodies with spaces so cut patterns never fire inside link text.</summary>
    private static string StripLinksPreservingLength(string text)
    {
        var chars = text.ToCharArray(); var depth = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            if (i + 1 < chars.Length && ((chars[i] == '[' && chars[i + 1] == '[') || (chars[i] == '{' && chars[i + 1] == '{'))) { depth++; i++; continue; }
            if (i + 1 < chars.Length && ((chars[i] == ']' && chars[i + 1] == ']') || (chars[i] == '}' && chars[i + 1] == '}'))) { depth = Math.Max(0, depth - 1); i++; continue; }
            if (depth > 0) chars[i] = ' ';
        }
        return new string(chars);
    }

    /// <summary>Links of a container clause ("in [[A]], [[B]]") up to the first clause cut.</summary>
    public static (ImmutableArray<GtaVLocationWikiLink> Links, bool CommaChain) ContainerClause(string remainder)
    {
        var masked = StripLinksPreservingLength(remainder);
        var cut = ClauseCut.Match(masked);
        var clause = cut.Success ? remainder[..cut.Index] : remainder;
        var maskedClause = cut.Success ? masked[..cut.Index] : masked;
        var sentenceEnd = maskedClause.IndexOf('.');
        if (sentenceEnd >= 0) clause = clause[..sentenceEnd];
        var links = Links(clause);
        if (links.Length < 2) return (links, false);
        // A pure address chain separates consecutive links by commas only.
        var chain = true;
        for (var i = 1; i < links.Length && chain; i++)
        {
            var prevEnd = clause.IndexOf("]]", links[i - 1].Index, StringComparison.Ordinal) + 2;
            var between = StripMarkup(clause[prevEnd..links[i].Index]).Trim();
            chain = between == ",";
        }
        return (links, chain);
    }

    public static bool IsProductLink(GtaVLocationWikiLink link, ImmutableArray<string> markers) =>
        markers.Any(m => link.Target.Equals(m, StringComparison.OrdinalIgnoreCase) || link.Label.Equals(m, StringComparison.OrdinalIgnoreCase)) ||
        markers.Any(m => link.Target.StartsWith(m + " ", StringComparison.OrdinalIgnoreCase) && !link.Target.Contains(" in ", StringComparison.Ordinal));

    /// <summary>Raw containment statements of one page, before endpoint resolution.</summary>
    public sealed record Statement(string Grammar, string Locator, GtaVLocationWikiLink? ChildLink, GtaVLocationWikiLink? ParentLink,
        string? ParentName, bool SubjectIsChild, bool SubjectIsParent, string? MemberNoun);

    public static IEnumerable<Statement> Statements(GtaVLocationPageFacts facts, ImmutableArray<string> markers)
    {
        // Infobox place fields: one address chain per alternative (line, <br>, bullet); within a chain the
        // subject lies inside each link of the first segment and each segment inside the next (", " or " in ").
        // Coordinated links in one segment ("A and B", "A/B") are siblings, not a chain.
        foreach (var (field, value) in facts.InfoboxFields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!PlaceFields.Contains(field)) continue;
            var i = 0;
            foreach (var chain in InfoboxChains(value, markers))
            {
                for (var s = 0; s < chain.Count; s++)
                {
                    foreach (var link in chain[s])
                    {
                        var locator = "infobox/" + field.ToLowerInvariant() + "/" + i.ToString(CultureInfo.InvariantCulture);
                        i++;
                        if (s == 0)
                            yield return new(InfoboxAddressChain, locator, null, link, null, true, false, null);
                        else
                            foreach (var child in chain[s - 1])
                                yield return new(InfoboxAddressChain, locator + (chain[s - 1].Count > 1 ? "/" + child.Index.ToString(CultureInfo.InvariantCulture) : string.Empty),
                                    child, link, null, false, false, null);
                    }
                }
            }
        }

        var sentenceIndex = 0;
        foreach (var sentence in Sentences(facts.Lead))
        {
            var prefix = "lead/" + sentenceIndex.ToString(CultureInfo.InvariantCulture);
            var subjectSentence = sentenceIndex == 0 || StartsWithSubject(sentence, facts);
            if (sentenceIndex == 0 && TryCopularHead(sentence, out _, out var remainder) && ContainerPrep.IsMatch(StripLinksPreservingLength(remainder)))
            {
                foreach (var statement in ClauseStatements(LeadCopularContainer, prefix, remainder, markers)) yield return statement;
            }
            if (subjectSentence)
            {
                var located = Located.Match(StripLinksPreservingLength(sentence));
                if (located.Success)
                    foreach (var statement in ClauseStatements(LeadLocatedIn, prefix + "/located", sentence[(located.Index + located.Length - located.Groups["prep"].Length)..], markers))
                        yield return statement;
                var contains = Contains.Match(StripLinksPreservingLength(sentence));
                if (contains.Success && GtaVLocationPlaceVocabulary.TryLevel(contains.Groups["noun"].Value, out _))
                {
                    var members = Links(ClauseUntilSentenceEnd(sentence[(contains.Index + contains.Length)..])).Where(l => !IsProductLink(l, markers)).ToArray();
                    for (var i = 0; i < members.Length; i++)
                        yield return new(LeadContainsMembers, prefix + "/contains/" + i.ToString(CultureInfo.InvariantCulture),
                            members[i], null, null, false, true, contains.Groups["noun"].Value);
                }
            }
            sentenceIndex++;
        }

        foreach (var section in facts.Sections)
        {
            var heading = section.Heading;
            var locatorBase = "section/" + section.HeadingPath;
            if (GtaVLocationPlaceVocabulary.IsSelfLocationHeading(heading))
            {
                var first = Sentences(section.Body).FirstOrDefault() ?? string.Empty;
                var located = Located.Match(StripLinksPreservingLength(first));
                if (located.Success)
                    foreach (var statement in ClauseStatements(SectionSelfLocation, locatorBase + "/located", first[(located.Index + located.Length - located.Groups["prep"].Length)..], markers))
                        yield return statement;
                continue;
            }
            var memberHeading = GtaVLocationPlaceVocabulary.HasMemberNoun(heading) ||
                facts.Sections.Any(s => s.Level < section.Level && section.HeadingPath.StartsWith(s.HeadingPath + " / ", StringComparison.Ordinal) &&
                                        GtaVLocationPlaceVocabulary.HasMemberNoun(s.Heading));
            if (!memberHeading) continue;
            var noun = GtaVLocationPlaceVocabulary.Words(section.HeadingPath).LastOrDefault(w => GtaVLocationPlaceVocabulary.MemberNouns.Contains(w));
            var bulletIndex = 0;
            var stack = new List<(int Depth, GtaVLocationWikiLink Link)>();
            foreach (var raw in section.Body.Split('\n'))
            {
                var line = raw.TrimEnd();
                var bullets = Regex.Match(line, @"^(?<b>[*#]+)\s*(?<rest>.*)$");
                if (!bullets.Success) continue;
                var depth = bullets.Groups["b"].Length;
                var rest = bullets.Groups["rest"].Value;
                // The bullet's place is its first article link; trailing links describe it and are not members.
                var link = Links(rest).FirstOrDefault(l => !IsProductLink(l, markers));
                var locator = locatorBase + "/" + bulletIndex.ToString(CultureInfo.InvariantCulture);
                bulletIndex++;
                while (stack.Count > 0 && stack[^1].Depth >= depth) stack.RemoveAt(stack.Count - 1);
                if (link is null) continue;
                if (!rest.TrimStart('\'', ' ').StartsWith("[[", StringComparison.Ordinal))
                { stack.Add((depth, link)); continue; }
                yield return stack.Count == 0
                    ? new(SectionMemberList, locator, link, null, null, false, true, noun)
                    : new(SectionNestedMember, locator, link, stack[^1].Link, null, false, false, noun);
                stack.Add((depth, link));
            }
        }

        foreach (var category in facts.Categories)
        {
            var reduced = category;
            foreach (var marker in markers)
            {
                reduced = Regex.Replace(reduced, @"\s+in\s+(?:the\s+)?" + Regex.Escape(marker) + @"(?![\w])", string.Empty, RegexOptions.CultureInvariant);
                reduced = Regex.Replace(reduced, "^" + Regex.Escape(marker) + @"\s+", string.Empty, RegexOptions.CultureInvariant);
            }
            var match = Regex.Match(reduced, @"^(?<noun>.+?)\s+(?:in|of)\s+(?:the\s+)?(?<container>[A-Z].+)$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var noun = match.Groups["noun"].Value;
            if (!GtaVLocationPlaceVocabulary.Words(noun).Any(w => GtaVLocationPlaceVocabulary.MemberNouns.Contains(w) || w.Equals("locations", StringComparison.OrdinalIgnoreCase)))
                continue;
            yield return new(CategoryNounInContainer, "category/" + category, null, null, match.Groups["container"].Value.Trim(), true, false, noun);
        }
    }

    private static IEnumerable<List<List<GtaVLocationWikiLink>>> InfoboxChains(string value, ImmutableArray<string> markers)
    {
        var normalized = Regex.Replace(value, @"(?i)<br\s*/?>", "\n", RegexOptions.CultureInvariant);
        foreach (var raw in normalized.Split('\n'))
        {
            var alternative = raw.Trim().TrimStart('*', '#', ' ');
            if (alternative.Length == 0) continue;
            var masked = StripLinksPreservingLength(alternative);
            var chain = new List<List<GtaVLocationWikiLink>>();
            var start = 0;
            foreach (var separator in InfoboxSegmentSeparator.Matches(masked).Cast<Match?>().Append(null))
            {
                var end = separator?.Index ?? alternative.Length;
                var segment = alternative[start..end];
                // Link indexes stay relative to the whole value so locators remain unique and stable.
                var offset = Math.Max(0, normalized.IndexOf(alternative, StringComparison.Ordinal)) + start;
                var links = Links(segment).Where(l => !IsProductLink(l, markers))
                    .Select(l => l with { Index = l.Index + offset }).ToList();
                if (links.Count > 0) chain.Add(links);
                if (separator is not null) start = separator.Index + separator.Length;
            }
            if (chain.Count > 0) yield return chain;
        }
    }

    private static readonly Regex InfoboxSegmentSeparator = new(@",|\s+in\s+|;", RegexOptions.CultureInvariant);

    private static bool StartsWithSubject(string sentence, GtaVLocationPageFacts facts)
    {
        var plain = StripMarkup(sentence);
        if (Regex.IsMatch(plain, @"^(?:It|The\s+(?:area|district|neighbou?rhood|city|town|county|state|island|region|village|community|building|park|beach)|This\s+\w+)\b", RegexOptions.CultureInvariant))
            return true;
        return new[] { facts.SubjectName }.Concat(facts.Aliases)
            .Any(n => plain.StartsWith(n + " ", StringComparison.Ordinal) || plain.StartsWith("The " + n + " ", StringComparison.Ordinal));
    }

    private static string ClauseUntilSentenceEnd(string text)
    {
        var masked = StripLinksPreservingLength(text);
        var end = masked.IndexOf('.');
        return end >= 0 ? text[..end] : text;
    }

    private static IEnumerable<Statement> ClauseStatements(string grammar, string prefix, string remainder, ImmutableArray<string> markers)
    {
        var (links, chain) = ContainerClause(remainder);
        var places = links.Where(l => !IsProductLink(l, markers)).ToArray();
        for (var i = 0; i < places.Length; i++)
        {
            var locator = prefix + "/" + i.ToString(CultureInfo.InvariantCulture);
            yield return chain && i > 0
                ? new(grammar, locator, places[i - 1], places[i], null, false, false, null)
                : new(grammar, locator, null, places[i], null, true, false, null);
        }
    }

    public static IEnumerable<GtaVLocationLevelVote> LevelVotes(GtaVLocationPageFacts facts)
    {
        if (TryCopularHead(FirstSentence(facts.Lead), out var head, out _) && GtaVLocationPlaceVocabulary.TryHeadLevel(head, out var level, out var noun))
            yield return new(level, 3, "lead-head-noun:" + noun, new(facts.Page, "lead/0/head", LeadCopularContainer));
        if (facts.InfoboxFields.TryGetValue("type", out var type) && GtaVLocationPlaceVocabulary.TryHeadLevel(StripMarkup(type), out level, out noun))
            yield return new(level, 2, "infobox-type:" + noun, new(facts.Page, "infobox/type", InfoboxAddressChain));
        if (facts.InfoboxHeader is { Length: > 0 } header && GtaVLocationPlaceVocabulary.TryHeadLevel(header, out level, out noun))
            yield return new(level, 2, "infobox-header:" + noun, new(facts.Page, "infobox/header", InfoboxAddressChain));
        foreach (var category in facts.Categories)
        {
            var noun0 = Regex.Match(category, @"^(?<noun>.+?)\s+(?:in|of)\s+", RegexOptions.CultureInvariant);
            if (noun0.Success && GtaVLocationPlaceVocabulary.TryHeadLevel(noun0.Groups["noun"].Value, out level, out noun))
                yield return new(level, 1, "category-noun:" + noun, new(facts.Page, "category/" + category, CategoryNounInContainer));
        }
    }
}
