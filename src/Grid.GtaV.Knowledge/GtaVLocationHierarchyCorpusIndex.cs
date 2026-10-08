using System.Collections.Immutable;

using System.Text;

using System.Text.Json;

using System.Text.RegularExpressions;

using Grid.Core.Models;
using Grid.Core.Services;



namespace Grid.GtaV.Knowledge;



public sealed record GtaVLocationHierarchyResolvedEndpoint(

    string PrimaryName,

    string EntityKey,

    string? NativeZoneCode,

    bool ReferenceOnly,

    CanonicalKnowledgeRecord? CatalogRecord);



public sealed record GtaVLocationHierarchyRow(

    GtaVLocationHierarchyResolvedEndpoint Parent,

    GtaVLocationHierarchyResolvedEndpoint Child,

    string ParentName,

    string ChildName,

    string SourceFieldPath,

    string SourceNativeRelationshipType,

    FrozenSourceArtifact EvidenceArtifact);



public sealed record GtaVLocationHierarchyUnresolvedEdge(

    string SourceFieldPath,

    string SourceNativeRelationshipType,

    FrozenSourceArtifact EvidenceArtifact,

    string? SubjectKey,

    string? TargetKey,

    string Reason);



public sealed record GtaVLocationHierarchyRegistrationResolution(

    ImmutableArray<GtaVLocationHierarchyRow> CorrelatedRows,

    ImmutableArray<GtaVLocationHierarchyUnresolvedEdge> UnresolvedEdges);



public sealed record GtaVLocationHierarchyGeographyJoinBlocker(

    string SourceKey,

    string ParentName,

    ImmutableArray<string> ChildNames,

    ImmutableArray<string> AmbiguousNativeBridgeNames,

    string SectionPath,

    string Grammar);



/// <summary>Exact, bounded reference joins from a pinned v2 source catalog. No coordinate containment or name inference.</summary>

public sealed class GtaVLocationHierarchyCorpusIndex

{

    public const string WikiCoordinate = "https://www.grandtheftwiki.com/index.php?title=Vinewood_in_GTA_V&oldid=411759";

    public const string LosSantosCoordinate = "https://www.grandtheftwiki.com/index.php?title=Los_Santos_in_GTA_V&oldid=419777";

    public const string GrandSenoraCoordinate = "https://www.grandtheftwiki.com/index.php?title=Grand_Senora_Desert&oldid=395939";

    public const string EastLosSantosCoordinate = "https://www.grandtheftwiki.com/index.php?title=East_Los_Santos_in_GTA_V&oldid=409804";

    public const string BlaineCountyCoordinate = "https://www.grandtheftwiki.com/index.php?title=Blaine_County&oldid=406414";

    public const string SanAndreasCoordinate = "https://www.grandtheftwiki.com/index.php?title=San_Andreas_in_GTA_V&oldid=416219";

    public const string VespucciCoordinate = "https://www.grandtheftwiki.com/index.php?title=Vespucci&oldid=396784";

    public const string CfxRevision = "ad60ae80aea6376686bf2e069036476f687ead24";

    public const string CfxCoordinate = "https://raw.githubusercontent.com/citizenfx/fivem-docs/" + CfxRevision + "/content/docs/game-references/zones.md";

    public const string WikiDigest = "d2fc80f7ef152a0f5bb5351c0c073c84639796bba7b47cc661b5d01bcce299fa";

    public const string CfxDigest = "bd305c3daef7b5d7d20278d61ec2a2dd6a4be590ebb8845dd5e6ae8744e69cb1";

    public const string LosSantosDigest = "5d84bda98a1a2fdd07693470550fb788533c9379e1fcb9573ecdc29e3029e90f";

    public const string GrandSenoraDigest = "6e1f02d40c1074ff866093eb2a8d0e9de7615f2c2d760c218add19d06b761008";

    public const string EastLosSantosDigest = "868e9dfae283dbcfdc0b069455aec14b13cf85cc44347c7aac1bb3e061f48934";

    public const string BlaineCountyDigest = "8b0e2c16efdd1a1ea37105a78e2b6f7b357453fb3869f859c65e2dbec63cdf11";

    public const string SanAndreasDigest = "06ecd996cf58966c1261aa55bc0f86fb33fa15fb26c8f4b96ac68af34f87f587";

    public const string VespucciDigest = "1e14a44a25ddb1ca30c74a3637b30a9be78f52ed6fe04352e3365bb2c03c5bbf";

    public const string JoinIndexDigest = "6940671f603436f9d9ad957050d241fe363e0be6111efce55f7d749b0f7734ff";

    /// <summary>Page title section of the pinned Vinewood snapshot, which carries no bracketed section markers.</summary>
    public const string VinewoodPageSection = "Vinewood in GTA V";

    public const string ReferenceGeographyNativeNamespace = "grid.gta-v.location-hierarchy.reference-geography";

    public const string ReferenceGeographyNativeObjectType = "EnglishPrimaryName";

    public static string ResolvePinnedReferenceRevision(string exactCoordinate) => exactCoordinate switch
    {
        WikiCoordinate => "411759",
        LosSantosCoordinate => "419777",
        GrandSenoraCoordinate => "395939",
        EastLosSantosCoordinate => "409804",
        BlaineCountyCoordinate => "406414",
        SanAndreasCoordinate => "416219",
        VespucciCoordinate => "396784",
        CfxCoordinate => CfxRevision,
        _ => throw new InvalidDataException("Unsupported location hierarchy reference coordinate: " + exactCoordinate),
    };



    public static readonly ImmutableDictionary<string, string> NativeBridgeEnglishAliases =

        ImmutableDictionary.CreateRange(StringComparer.Ordinal,

        [

            KeyValuePair.Create("City of Davis", "Davis"),

        ]);



    /// <summary>English labels kept reference-only until a unique CFX/native zone bridge is proven.</summary>

    public static readonly ImmutableHashSet<string> ReferenceOnlyNativeMappingEnglishNames =

        ImmutableHashSet.Create(StringComparer.Ordinal,

            "East Los Santos",

            "Blaine County",

            "Los Santos County",

            "Los Santos",

            "Cape Catfish",

            "Murrieta Oil Field");



    public FrozenSourceArtifact Wiki { get; }

    public FrozenSourceArtifact NativeTable { get; }

    public FrozenSourceArtifact LosSantosWiki { get; }

    public FrozenSourceArtifact GrandSenoraWiki { get; }

    public FrozenSourceArtifact EastLosSantosWiki { get; }

    public FrozenSourceArtifact BlaineCountyWiki { get; }

    public FrozenSourceArtifact SanAndreasWiki { get; }

    public FrozenSourceArtifact VespucciWiki { get; }

    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }

    public int ExpectedRelationshipCount { get; }

    public int ExpectedPackageAssertionRelationshipCount { get; }

    public string ParentName { get; }

    public ImmutableArray<string> ChildNames { get; }

    public ImmutableDictionary<string, string> NameCodes { get; }

    public ImmutableHashSet<string> AmbiguousNativeEnglishNames { get; }

    public ImmutableArray<string> RequiredNativeBridgedEnglishNames { get; }
    public ImmutableArray<string> RequiredEnglishNames => RequiredNativeBridgedEnglishNames;

    public ImmutableArray<GtaVLocationHierarchyGeographyJoinBlocker> GeographyBridgeBlockedRows { get; }

    public bool GeographyPublicationReady => GeographyBridgeBlockedRows.IsEmpty;

    public const string SanAndreasCountySectionPrefix = "Southern San Andreas / ";

    /// <summary>Exact "Southern San Andreas / {county}" section paths of the pinned San Andreas snapshot.</summary>
    public ImmutableArray<string> SanAndreasCountySectionPaths => containmentSectionsBySource["san-andreas"].Keys
        .Where(key => key.StartsWith(SanAndreasCountySectionPrefix, StringComparison.Ordinal) && key.Length > SanAndreasCountySectionPrefix.Length)
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();



    private readonly byte[] joinIndexBytes;

    private readonly ImmutableArray<ContainmentJoinIndexRow> correlatableContainmentRows;

    private readonly ImmutableDictionary<string, ImmutableDictionary<string, string>> containmentSectionsBySource;



    private sealed record ContainmentJoinIndexRow(

        string ParentName,

        ImmutableArray<string> ChildNames,

        string Grammar,

        string SectionPath,

        string SourceKey);



    public GtaVLocationHierarchyCorpusIndex(

        byte[] wiki,

        byte[] nativeTable,

        byte[] losSantosWiki,

        byte[] grandSenoraWiki,

        byte[] eastLosSantosWiki,

        byte[] blaineCountyWiki,

        byte[] sanAndreasWiki,

        byte[] vespucciWiki,

        byte[] joinIndex,

        int expectedRelationshipCount)

    {

        ExpectedRelationshipCount = expectedRelationshipCount;

        Wiki = Frozen(WikiCoordinate, wiki);

        NativeTable = Frozen(CfxCoordinate, nativeTable);

        LosSantosWiki = Frozen(LosSantosCoordinate, losSantosWiki);

        GrandSenoraWiki = Frozen(GrandSenoraCoordinate, grandSenoraWiki);

        EastLosSantosWiki = Frozen(EastLosSantosCoordinate, eastLosSantosWiki);

        BlaineCountyWiki = Frozen(BlaineCountyCoordinate, blaineCountyWiki);

        SanAndreasWiki = Frozen(SanAndreasCoordinate, sanAndreasWiki);

        VespucciWiki = Frozen(VespucciCoordinate, vespucciWiki);

        joinIndexBytes = joinIndex;

        containmentSectionsBySource = ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]

        {

            KeyValuePair.Create("vinewood", ParsePageSection(Encoding.UTF8.GetString(wiki), VinewoodPageSection)),

            KeyValuePair.Create("los-santos", ParseWikiSections(Encoding.UTF8.GetString(losSantosWiki))),

            KeyValuePair.Create("grand-senora", ParseWikiSections(Encoding.UTF8.GetString(grandSenoraWiki))),

            KeyValuePair.Create("east-los-santos", ParseWikiSections(Encoding.UTF8.GetString(eastLosSantosWiki))),

            KeyValuePair.Create("blaine-county", ParseWikiSections(Encoding.UTF8.GetString(blaineCountyWiki))),

            KeyValuePair.Create("san-andreas", ParseWikiSections(Encoding.UTF8.GetString(sanAndreasWiki))),

            KeyValuePair.Create("vespucci", ParseWikiSections(Encoding.UTF8.GetString(vespucciWiki))),

        });

        GtaVPresentationCorpusIndex.RequireDigest(Wiki, WikiDigest);

        GtaVPresentationCorpusIndex.RequireDigest(NativeTable, CfxDigest);

        GtaVPresentationCorpusIndex.RequireDigest(LosSantosWiki, LosSantosDigest);

        GtaVPresentationCorpusIndex.RequireDigest(GrandSenoraWiki, GrandSenoraDigest);

        GtaVPresentationCorpusIndex.RequireDigest(EastLosSantosWiki, EastLosSantosDigest);

        GtaVPresentationCorpusIndex.RequireDigest(BlaineCountyWiki, BlaineCountyDigest);

        GtaVPresentationCorpusIndex.RequireDigest(SanAndreasWiki, SanAndreasDigest);

        GtaVPresentationCorpusIndex.RequireDigest(VespucciWiki, VespucciDigest);

        if (ContentDigest.ComputeSha256(joinIndex).HexValue != JoinIndexDigest)

            throw new InvalidDataException("Location hierarchy join index digest does not match the pinned source catalog.");

        Artifacts = [Wiki, NativeTable, LosSantosWiki, GrandSenoraWiki, EastLosSantosWiki, BlaineCountyWiki, SanAndreasWiki, VespucciWiki];

        (ParentName, ChildNames) = ParseEnumeration(Encoding.UTF8.GetString(wiki));

        var nativeTableText = Encoding.UTF8.GetString(nativeTable);

        NameCodes = ParseNativeTable(nativeTableText);

        AmbiguousNativeEnglishNames = ParseAmbiguousNativeTableNames(nativeTableText);

        var requiredNative = ImmutableArray.CreateBuilder<string>();

        requiredNative.Add(ParentName);

        requiredNative.AddRange(ChildNames);

        var correlatable = ImmutableArray.CreateBuilder<ContainmentJoinIndexRow>();

        var blocked = ImmutableArray.CreateBuilder<GtaVLocationHierarchyGeographyJoinBlocker>();

        foreach (var line in Encoding.UTF8.GetString(joinIndex).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))

        {

            if (!line.StartsWith("CONTAINMENT|", StringComparison.Ordinal)) continue;

            var row = ParseContainmentJoinIndexLine(line);

            if (!TryLocateUpstreamContainment(row.SourceKey, row.SectionPath, row.Grammar, row.ParentName, out var locatedChildren))

                throw new InvalidDataException("Join index must locate an upstream containment assertion: " + row.SectionPath);

            if (!locatedChildren.SequenceEqual(row.ChildNames, StringComparer.Ordinal))

                throw new InvalidDataException("Join index row does not match the located upstream assertion: " + row.SectionPath);

            if (TryGetAmbiguousNativeBridgeNames(row.ParentName, row.ChildNames, out var ambiguousNames))

            {

                blocked.Add(new(row.SourceKey, row.ParentName, row.ChildNames, ambiguousNames, row.SectionPath, row.Grammar));

                continue;

            }

            correlatable.Add(row);

            CollectNativeBridgedNames(row.ParentName, requiredNative);

            foreach (var childName in row.ChildNames)

                CollectNativeBridgedNames(childName, requiredNative);

        }

        correlatableContainmentRows = correlatable.ToImmutable();

        GeographyBridgeBlockedRows = blocked.ToImmutable();

        RequiredNativeBridgedEnglishNames = requiredNative
            .Select(value => value.Trim())
            .Select(ResolveNativeBridgeEnglishName)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();

        foreach (var bridge in RequiredNativeBridgedEnglishNames)
        {
            if (!NameCodes.ContainsKey(bridge))
                throw new InvalidDataException("Native-bridged hierarchy name has no unique native table bridge: " + bridge);
        }

        var uniqueEdgeKeys = new HashSet<(string Parent, string Child)>();
        foreach (var child in ChildNames)
            uniqueEdgeKeys.Add((ParentName, child));
        foreach (var row in correlatableContainmentRows)
        {
            foreach (var child in row.ChildNames)
                uniqueEdgeKeys.Add((row.ParentName, child));
        }
        if (uniqueEdgeKeys.Count != expectedRelationshipCount)
            throw new InvalidDataException("Unique correlatable containment edge count does not match the pinned source catalog.");

        ExpectedPackageAssertionRelationshipCount = expectedRelationshipCount;
    }

    public static string ResolveNativeBridgeEnglishName(string englishName) =>
        NativeBridgeEnglishAliases.TryGetValue(englishName, out var alias) ? alias : englishName;

    private void CollectNativeBridgedNames(string englishName, ImmutableArray<string>.Builder requiredNative)
    {
        if (RequiresReferenceOnlyEndpoint(englishName)) return;
        requiredNative.Add(englishName);
    }

    public bool RequiresReferenceOnlyEndpoint(string englishName) =>
        ReferenceOnlyNativeMappingEnglishNames.Contains(englishName) ||
        !NameCodes.ContainsKey(ResolveNativeBridgeEnglishName(englishName));



    public static GtaVLocationHierarchyCorpusIndex LoadFromRepository(string referencesDirectory)

    {

        var manifestPath = Path.Combine(Path.GetDirectoryName(referencesDirectory)!, "gta_v_enhanced_location_hierarchy_sources.v2.json");

        if (!File.Exists(manifestPath))

            manifestPath = Path.Combine(referencesDirectory, "gta_v_enhanced_location_hierarchy_sources.v2.json");

        if (!File.Exists(manifestPath))

            throw new FileNotFoundException("Location hierarchy v2 source catalog is unavailable.", manifestPath);

        using var json = JsonDocument.Parse(File.ReadAllBytes(manifestPath));

        var root = json.RootElement;

        if (root.GetProperty("schemaVersion").GetInt32() != 2)

            throw new InvalidDataException("Location hierarchy requires source catalog schema version 2.");

        var expected = root.GetProperty("expectedRelationshipCount").GetInt32();

        var wikiPath = Path.Combine(referencesDirectory, "vinewood-411759.normalized.txt");

        var tablePath = Path.Combine(referencesDirectory, "cfx-zones-ad60ae80.md");

        var losSantosPath = Path.Combine(referencesDirectory, "los-santos-419777.normalized.txt");

        var grandSenoraPath = Path.Combine(referencesDirectory, "grand-senora-desert-395939.normalized.txt");

        var eastLosSantosPath = Path.Combine(referencesDirectory, "east-los-santos-409804.normalized.txt");

        var blaineCountyPath = Path.Combine(referencesDirectory, "blaine-county-406414.normalized.txt");

        var sanAndreasPath = Path.Combine(referencesDirectory, "san-andreas-416219.normalized.txt");

        var vespucciPath = Path.Combine(referencesDirectory, "vespucci-396784.normalized.txt");

        var joinPath = Path.Combine(referencesDirectory, "geography-containment.v1.normalized.txt");

        return new(

            File.ReadAllBytes(wikiPath),

            File.ReadAllBytes(tablePath),

            File.ReadAllBytes(losSantosPath),

            File.ReadAllBytes(grandSenoraPath),

            File.ReadAllBytes(eastLosSantosPath),

            File.ReadAllBytes(blaineCountyPath),

            File.ReadAllBytes(sanAndreasPath),

            File.ReadAllBytes(vespucciPath),

            File.ReadAllBytes(joinPath),

            expected);

    }



    public static (string Parent, ImmutableArray<string> Children) ParseEnumeration(string text)

    {

        var matches = Regex.Matches(text, @"([A-Za-z ]+) , ([A-Za-z ]+) , ([A-Za-z ]+) , and ([A-Za-z ]+) are the neighbourhoods that comprise of the larger ([A-Za-z ]+) district\.", RegexOptions.CultureInvariant);

        if (matches.Count != 1) throw new InvalidDataException("Expected exactly one bounded neighbourhood enumeration.");

        var match = matches[0];

        var children = Enumerable.Range(1, 4).Select(i => match.Groups[i].Value.Trim()).ToImmutableArray();

        var parent = match.Groups[5].Value.Trim();

        if (children.Distinct(StringComparer.Ordinal).Count() != 4 || children.Contains(parent, StringComparer.Ordinal))

            throw new InvalidDataException("Ambiguous hierarchy enumeration.");

        return (parent, children);

    }



    public static ImmutableDictionary<string, string> ParseNativeTable(string text)

    {

        var rows = Regex.Matches(text, @"(?m)^\|\s*[0-9]+\s*\|\s*[^|]+\|\s*(?<code>[A-Za-z0-9_]+)\s*\|\s*(?<name>[^|]+?)\s*\|\s*$", RegexOptions.CultureInvariant)

            .Select(m => (Code: m.Groups["code"].Value, Name: m.Groups["name"].Value.Trim())).ToArray();

        if (rows.Length == 0) throw new InvalidDataException("Native zone table is absent.");

        return rows.GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() == 1)

            .ToImmutableDictionary(g => g.Key, g => g.First().Code, StringComparer.Ordinal);

    }



    public static ImmutableHashSet<string> ParseAmbiguousNativeTableNames(string text)

    {

        var rows = Regex.Matches(text, @"(?m)^\|\s*[0-9]+\s*\|\s*[^|]+\|\s*(?<code>[A-Za-z0-9_]+)\s*\|\s*(?<name>[^|]+?)\s*\|\s*$", RegexOptions.CultureInvariant)

            .Select(m => (Code: m.Groups["code"].Value, Name: m.Groups["name"].Value.Trim())).ToArray();

        return rows.GroupBy(x => x.Name, StringComparer.Ordinal)

            .Where(g => g.Select(x => x.Code).Distinct(StringComparer.Ordinal).Count() > 1)

            .Select(g => g.Key)

            .ToImmutableHashSet(StringComparer.Ordinal);

    }



    public ImmutableArray<GtaVLocationHierarchyRow> Resolve(CanonicalCatalogPayload payload)

    {

        var resolution = ResolveForRegistration(payload);

        if (!resolution.UnresolvedEdges.IsEmpty)

            throw new InvalidDataException("Hierarchy endpoint resolution left unresolved edges.");

        if (resolution.CorrelatedRows.Length != ExpectedPackageAssertionRelationshipCount)

            throw new InvalidDataException("Resolved package assertion row count does not match the pinned source catalog.");

        return resolution.CorrelatedRows;

    }



    public GtaVLocationHierarchyRegistrationResolution ResolveForRegistration(CanonicalCatalogPayload payload)

    {

        var rows = ImmutableArray.CreateBuilder<GtaVLocationHierarchyRow>();

        var unresolved = ImmutableArray.CreateBuilder<GtaVLocationHierarchyUnresolvedEdge>();
        var admittedEdges = new HashSet<(string ParentKey, string ChildKey)>();
        var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value;

        if (!TryResolveEndpoint(payload, ParentName, gameId, out var vinewoodParent, out _))

        {

            foreach (var childName in ChildNames)

            {

                unresolved.Add(new(

                    "neighbourhood-enumeration/" + childName,

                    "reference.neighbourhood-of-district",

                    Wiki,

                    null,

                    null,

                    "relationship-endpoint-unresolved"));

            }

        }

        else

        {

            foreach (var childName in ChildNames)

            {

                var fieldPath = "neighbourhood-enumeration/" + childName;

                if (!TryResolveEndpoint(payload, childName, gameId, out var child, out var childReason))
                {
                    unresolved.Add(new(fieldPath, "reference.neighbourhood-of-district", Wiki, null, vinewoodParent.EntityKey, childReason ?? "relationship-endpoint-unresolved"));
                    continue;
                }
                if (!admittedEdges.Add((vinewoodParent.EntityKey, child.EntityKey)))
                    continue;
                rows.Add(new(vinewoodParent, child, ParentName, childName, fieldPath, "reference.neighbourhood-of-district", Wiki));
            }
        }

        foreach (var row in correlatableContainmentRows)
        {
            var evidenceArtifact = ResolveContainmentEvidenceArtifact(row.SourceKey);
            var relationshipType = MapContainmentGrammar(row.Grammar);
            if (!TryResolveEndpoint(payload, row.ParentName, gameId, out var parent, out var parentReason))
            {
                foreach (var childName in row.ChildNames)
                {
                    var fieldPath = row.SectionPath + "/" + childName;
                    unresolved.Add(new(fieldPath, relationshipType, evidenceArtifact, null, null, parentReason ?? "relationship-endpoint-unresolved"));
                }
                continue;
            }
            foreach (var childName in row.ChildNames)
            {
                var fieldPath = row.SectionPath + "/" + childName;
                if (!TryResolveEndpoint(payload, childName, gameId, out var child, out var childReason))
                {
                    unresolved.Add(new(fieldPath, relationshipType, evidenceArtifact, null, parent.EntityKey, childReason ?? "relationship-endpoint-unresolved"));
                    continue;
                }
                if (!admittedEdges.Add((parent.EntityKey, child.EntityKey)))
                    continue;
                rows.Add(new(parent, child, row.ParentName, childName, fieldPath, relationshipType, evidenceArtifact));
            }
        }

        if (rows.Count + unresolved.Count != ExpectedRelationshipCount)

            throw new InvalidDataException("Resolved hierarchy row count does not match the pinned source catalog.");

        return new(rows.ToImmutable(), unresolved.ToImmutable());

    }



    /// <summary>Locates an upstream wiki containment assertion; join-index parent must match upstream content exactly for desert grammar.</summary>

    public static bool LocateUpstreamContainmentAssertion(byte[] wikiSnapshot, string sectionPath, string grammar, string expectedParentName, out ImmutableArray<string> childNames)

    {

        var sections = ParseWikiSections(Encoding.UTF8.GetString(wikiSnapshot));

        return TryLocateUpstreamContainmentCore(sections, sectionPath, grammar, expectedParentName, out childNames);

    }



    private bool TryLocateUpstreamContainment(string sourceKey, string sectionPath, string grammar, string expectedParentName, out ImmutableArray<string> childNames)

    {

        if (!containmentSectionsBySource.TryGetValue(sourceKey, out var sections))

        {

            childNames = default;

            return false;

        }

        return TryLocateUpstreamContainmentCore(sections, sectionPath, grammar, expectedParentName, out childNames);

    }



    private FrozenSourceArtifact ResolveContainmentEvidenceArtifact(string sourceKey) => sourceKey switch

    {

        "vinewood" => Wiki,

        "los-santos" => LosSantosWiki,

        "grand-senora" => GrandSenoraWiki,

        "east-los-santos" => EastLosSantosWiki,

        "blaine-county" => BlaineCountyWiki,

        "san-andreas" => SanAndreasWiki,

        "vespucci" => VespucciWiki,

        _ => throw new InvalidDataException("Unsupported containment source key: " + sourceKey),

    };



    private static bool TryLocateUpstreamContainmentCore(ImmutableDictionary<string, string> sections, string sectionPath, string grammar, string expectedParentName, out ImmutableArray<string> childNames)

    {

        childNames = default;

        if (!sections.TryGetValue(sectionPath, out var body))

            return false;

        switch (grammar)

        {

            case "contains-smaller-neighbourhood":

            {

                var match = Regex.Match(body, @"^(?<parent>.+?) contains the smaller neighbourhood of (?<child>.+?) in the ", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                if (!string.Equals(match.Groups["parent"].Value.Trim(), expectedParentName, StringComparison.Ordinal)) return false;

                childNames = ImmutableArray.Create(match.Groups["child"].Value.Trim());

                return true;

            }

            case "divided-neighbourhoods":

            {

                if (!SectionSubjectEstablishesParent(sectionPath, expectedParentName)) return false;

                var subjectMatch = Regex.Match(body, @"^(?<parent>.+?) is the ", RegexOptions.CultureInvariant);

                if (!subjectMatch.Success) return false;

                if (!string.Equals(subjectMatch.Groups["parent"].Value.Trim(), expectedParentName, StringComparison.Ordinal)) return false;

                var match = Regex.Match(body, @"divided into the neighborhoods of (?<list>.+)\.", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                childNames = ParseNeighborhoodList(match.Groups["list"].Value);

                return childNames.Length > 0;

            }

            case "split-neighbourhoods":

            {

                if (!SectionSubjectEstablishesParent(sectionPath, expectedParentName)) return false;

                var match = Regex.Match(body, @"^(?<parent>.+?) is split into the neighborhoods of (?<list>.+)\.", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                if (!string.Equals(match.Groups["parent"].Value.Trim(), expectedParentName, StringComparison.Ordinal)) return false;

                childNames = ParseNeighborhoodList(match.Groups["list"].Value);

                return childNames.Length > 0;

            }

            case "town-located-inside-desert":

            {

                if (!string.Equals(sectionPath, expectedParentName, StringComparison.Ordinal)) return false;

                var match = Regex.Match(body, @"The town of (?<child>.+?) is located inside the desert", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                childNames = ImmutableArray.Create(match.Groups["child"].Value.Trim());

                return true;

            }

            case "city-located-within-municipality":

            {

                if (!sections.TryGetValue(sectionPath, out var sectionBody)) return false;

                var match = Regex.Match(sectionBody, @"^The city of (?<child>.+?) is located within (?<parent>.+?)\.", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                if (!string.Equals(match.Groups["parent"].Value.Trim(), expectedParentName, StringComparison.Ordinal)) return false;

                childNames = ImmutableArray.Create(match.Groups["child"].Value.Trim());

                return true;

            }

            case "district-in-city":

            {

                var matches = Regex.Matches(body, @"(?:^|\s)(?<child>[A-Z][A-Za-z ]*?) is a district in (?<parent>[A-Z][A-Za-z ]*?) appearing in ", RegexOptions.CultureInvariant);

                if (matches.Count != 1) return false;

                return TryAcceptLeadSubject(sectionPath, matches[0], expectedParentName, out childNames);

            }

            case "financial-center-of-city":

            {

                var match = Regex.Match(body, @"^(?<child>[A-Z][A-Za-z ]*?) is the financial center of (?<parent>[A-Z][A-Za-z ]*?) and its most urbanized area", RegexOptions.CultureInvariant);

                if (!match.Success) return false;

                return TryAcceptLeadSubject(sectionPath, match, expectedParentName, out childNames);

            }

            case "sector-located-in-city":

            {

                var match = Regex.Match(body, @"^(?<child>[A-Z][A-Za-z ]*?) is a geographic sector located in (?:eastern |western |northern |southern )?(?<parent>[A-Z][A-Za-z ]*?), ", RegexOptions.CultureInvariant);

                if (!match.Success || !string.Equals(match.Groups["child"].Value.Trim(), sectionPath, StringComparison.Ordinal)) return false;

                return TryAcceptLeadSubject(sectionPath, match, expectedParentName, out childNames);

            }

            case "neighborhood-in-city":

            {

                var match = Regex.Match(body, @"^(?<child>[A-Z][A-Za-z ]*?) is an? (?:[a-z-]+ )*neighbou?rhood in (?<parent>[A-Z][A-Za-z ]*?), San Andreas appearing in ", RegexOptions.CultureInvariant);

                if (!match.Success || !string.Equals(match.Groups["child"].Value.Trim(), sectionPath, StringComparison.Ordinal)) return false;

                return TryAcceptLeadSubject(sectionPath, match, expectedParentName, out childNames);

            }

            case "desert-located-in-county":

            {

                var match = Regex.Match(body, @"^The (?<child>[A-Z][A-Za-z ]*?) is a desert located in (?<parent>[A-Z][A-Za-z ]*?), San Andreas ", RegexOptions.CultureInvariant);

                if (!match.Success || !string.Equals(match.Groups["child"].Value.Trim(), sectionPath, StringComparison.Ordinal)) return false;

                return TryAcceptLeadSubject(sectionPath, match, expectedParentName, out childNames);

            }

            case "page-section-member-list":

            {

                if (!sections.ContainsKey(expectedParentName)) return false;

                if (!sections.TryGetValue(sectionPath, out body)) return false;

                childNames = ParseNeighborhoodList(body);

                return childNames.Length > 0;

            }

            case "county-section-member-list":

            {

                if (!sections.TryGetValue(sectionPath, out body)) return false;

                if (!string.Equals(sectionPath, expectedParentName, StringComparison.Ordinal)

                    && !sectionPath.EndsWith(" / " + expectedParentName, StringComparison.Ordinal))

                    return false;

                childNames = ParseNeighborhoodList(body);

                return childNames.Length > 0;

            }

            default:

                return false;

        }

    }



    /// <summary>The lead sentence subject is the child and must be the section subject; the named container must equal the join-index parent.</summary>
    private static bool TryAcceptLeadSubject(string sectionPath, Match match, string expectedParentName, out ImmutableArray<string> childNames)
    {
        childNames = default;
        var child = match.Groups["child"].Value.Trim();
        if (!SectionSubjectEstablishesParent(sectionPath, child)) return false;
        if (!string.Equals(match.Groups["parent"].Value.Trim(), expectedParentName, StringComparison.Ordinal)) return false;
        if (string.Equals(child, expectedParentName, StringComparison.Ordinal)) return false;
        childNames = ImmutableArray.Create(child);
        return true;
    }

    private static bool SectionSubjectEstablishesParent(string sectionSubject, string expectedParentName)

        => string.Equals(sectionSubject, expectedParentName, StringComparison.Ordinal)

            || sectionSubject.StartsWith(expectedParentName + " ", StringComparison.Ordinal);



    private static string MapContainmentGrammar(string grammar) => grammar switch

    {

        "contains-smaller-neighbourhood" => "reference.district-smaller-neighbourhood",

        "divided-neighbourhoods" => "reference.district-neighbourhood-division",

        "split-neighbourhoods" => "reference.district-neighbourhood-split",

        "town-located-inside-desert" => "reference.town-inside-desert",

        "district-in-city" => "reference.district-in-city",

        "financial-center-of-city" => "reference.district-area-of-city",

        "sector-located-in-city" => "reference.sector-located-in-city",

        "desert-located-in-county" => "reference.desert-located-in-county",

        "neighborhood-in-city" => "reference.neighborhood-in-city",

        "city-located-within-municipality" => "reference.city-within-municipality",

        "page-section-member-list" => "reference.page-section-member-list",

        "county-section-member-list" => "reference.county-section-member-list",

        _ => throw new InvalidDataException("Unsupported containment grammar: " + grammar),

    };



    private static ContainmentJoinIndexRow ParseContainmentJoinIndexLine(string line)

    {

        var parts = line.Split('|');

        if (parts.Length is not (5 or 6)) throw new InvalidDataException("Malformed containment join index row.");

        return new(

            parts[1].Trim(),

            parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray(),

            parts[3].Trim(),

            parts[4].Trim(),

            parts.Length == 6 ? parts[5].Trim() : "los-santos");

    }



    private bool TryGetAmbiguousNativeBridgeNames(string parentName, ImmutableArray<string> childNames, out ImmutableArray<string> ambiguous)

    {

        var gaps = ImmutableArray.CreateBuilder<string>();

        if (AmbiguousNativeEnglishNames.Contains(parentName) || AmbiguousNativeEnglishNames.Contains(ResolveNativeBridgeEnglishName(parentName)))

            gaps.Add(parentName);

        foreach (var childName in childNames)

        {

            var bridge = ResolveNativeBridgeEnglishName(childName);

            if (AmbiguousNativeEnglishNames.Contains(childName) || AmbiguousNativeEnglishNames.Contains(bridge))

                gaps.Add(childName);

        }

        ambiguous = gaps.ToImmutable();

        return !ambiguous.IsEmpty;

    }



    private static ImmutableArray<string> ParseNeighborhoodList(string list)

    {

        var segments = list.Split(',', StringSplitOptions.TrimEntries);

        if (segments.Length == 0) return ImmutableArray<string>.Empty;

        var last = segments[^1];

        if (last.Contains(" and ", StringComparison.Ordinal))

        {

            var split = last.Split(" and ", 2, StringSplitOptions.TrimEntries);

            return segments[..^1].Concat(split).Where(value => !string.IsNullOrWhiteSpace(value)).ToImmutableArray();

        }

        return segments.Where(value => !string.IsNullOrWhiteSpace(value)).ToImmutableArray();

    }



    /// <summary>Whole-page section keyed by the page title heading; whitespace is collapsed so prose sentences stay contiguous.</summary>
    private static ImmutableDictionary<string, string> ParsePageSection(string text, string pageTitle)
    {
        if (!text.Split('\n').Any(line => string.Equals(line.Trim(), "# " + pageTitle, StringComparison.Ordinal)))
            return ImmutableDictionary<string, string>.Empty;
        return ImmutableDictionary.CreateRange(StringComparer.Ordinal,
            [KeyValuePair.Create(pageTitle, Regex.Replace(text, @"\s+", " ").Trim())]);
    }

    private static ImmutableDictionary<string, string> ParseWikiSections(string text)

    {

        var sections = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        string? current = null;

        var body = new StringBuilder();

        foreach (var rawLine in text.Split('\n'))

        {

            var line = rawLine.Trim();

            if (line.Length >= 2 && line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))

            {

                if (current is not null)

                    sections[current] = body.ToString().Trim();

                current = line[1..^1].Trim();

                body.Clear();

                continue;

            }

            if (current is null) continue;

            if (body.Length > 0) body.Append(' ');

            body.Append(line);

        }

        if (current is not null)

            sections[current] = body.ToString().Trim();

        return sections.ToImmutable();

    }



    private bool TryResolveEndpoint(CanonicalCatalogPayload payload, string name, string gameId, out GtaVLocationHierarchyResolvedEndpoint endpoint, out string? failureReason)

    {

        endpoint = default!;

        failureReason = null;

        if (RequiresReferenceOnlyEndpoint(name))

        {

            endpoint = CreateReferenceEndpoint(name, gameId);

            return true;

        }

        var bridge = ResolveNativeBridgeEnglishName(name);

        if (!NameCodes.TryGetValue(bridge, out var code))
        {
            endpoint = CreateReferenceEndpoint(name, gameId);
            return true;
        }

        if (!TryResolveNativeCatalog(payload, name, bridge, code, out var record, out failureReason))

            return false;

        endpoint = new(name, record.Id.Value, code, false, record);

        return true;

    }



    private static GtaVLocationHierarchyResolvedEndpoint CreateReferenceEndpoint(string primaryName, string gameId)

    {

        var nativeId = primaryName;

        var entityKey = CanonicalRegistrationEncoding.Identity(

            "Location", gameId, ReferenceGeographyNativeNamespace, nativeId);

        return new(primaryName, entityKey, null, true, null);

    }



    private bool TryResolveNativeCatalog(CanonicalCatalogPayload payload, string name, string bridge, string code, out CanonicalKnowledgeRecord record, out string reason)

    {

        record = null!;

        reason = string.Empty;

        var records = payload.KnowledgeRecords.Where(r => r.Kind == KnowledgeKind.Location &&

            r.NativeIdentity.Namespace == "rockstar.gta-v.enhanced.population-zones" && r.NativeIdentity.ObjectType == "NameLabel" &&

            string.Equals(r.NativeIdentity.ExactRepresentation, code, StringComparison.OrdinalIgnoreCase) &&

            payload.TerminologyAssertions.Any(t => t.KnowledgeRecordId == r.Id && t.Role == TerminologyAssertionRole.PrimaryName &&

                t.LanguageTag == "en-US" && t.VerbatimValue == bridge)).ToArray();

        if (records.Length == 0)

        {

            reason = "endpoint-missing";

            return false;

        }

        if (records.Length != 1)

        {

            reason = "endpoint-ambiguous";

            return false;

        }

        var labels = payload.TerminologyAssertions.Where(t => t.KnowledgeRecordId == records[0].Id && t.Role == TerminologyAssertionRole.PrimaryName && t.LanguageTag == "en-US")

            .Select(t => t.VerbatimValue).Distinct(StringComparer.Ordinal).ToArray();

        if (labels.Length != 1)

        {

            reason = "endpoint-terminology-conflict";

            return false;

        }

        if (!string.Equals(labels[0], bridge, StringComparison.Ordinal) && !string.Equals(name, labels[0], StringComparison.Ordinal))

        {

            reason = "endpoint-terminology-conflict";

            return false;

        }

        record = records[0];

        return true;

    }



    private static FrozenSourceArtifact Frozen(string coordinate, byte[] bytes)

    {

        var digest = ContentDigest.ComputeSha256(bytes);

        return new(SourceArtifactId.DeriveV1(digest), digest,

            SourceNativeIdentifier.FromExactUtf8("grid.gta-v.location-hierarchy.reference", "PinnedReference", coordinate, "grid.exact-utf8", 1),

            new KnowledgeFormatCoordinate(GtaVPresentationCorpusIndex.ReferenceFormatId, "1"), bytes.ToImmutableArray(),

            new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero));

    }

}


