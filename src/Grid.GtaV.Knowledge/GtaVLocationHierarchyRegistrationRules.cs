using Grid.Core.Models;



namespace Grid.GtaV.Knowledge;



public static class GtaVLocationHierarchyRegistrationRules

{

    public const string SourceFamily = "gta-v.location-hierarchy.v2";

    public const string WorldAnchor = "Location/world";
    public const string ContinentAnchor = WorldAnchor + "/continent";
    public const string CountryAnchor = ContinentAnchor + "/country";
    public const string StateAnchor = CountryAnchor + "/state";
    public const string CountyRegionAnchor = StateAnchor + "/county-region";
    public const string CityAnchor = CountyRegionAnchor + "/city";
    public const string NeighborhoodAnchor = CityAnchor + "/town-neighborhood";

    public const string CountyRegionClassification = "native:" + CountyRegionAnchor;
    public const string CityClassification = "native:" + CityAnchor;
    public const string NeighborhoodClassification = "native:" + NeighborhoodAnchor;

    /// <summary>L7.5: a neighbourhood a pinned sentence places inside a larger district, kept on the L7 anchor.</summary>
    public const decimal SubNeighborhoodLevel = 7.5m;
    public const string SubNeighborhoodClassification = NeighborhoodClassification + "/sub-neighborhood";

    /// <summary>San Andreas state frame evidence: the pinned San Andreas snapshot names it as the GTA V state.</summary>
    public const string SanAndreasFrameEvidenceId = "evidence:level-frame:san-andreas";
    public const string SanAndreasFrameLocator = "Information";

    public static RegistrationRuleSet Create() => new(

        "grid.gta-v.location-hierarchy.registration-rules.v2",

        [
            new("map:location.county-region", SourceFamily, "Location", CountyRegionClassification, CountyRegionAnchor),
            new("map:location.city", SourceFamily, "Location", CityClassification, CityAnchor),
            new("map:location.town-neighborhood", SourceFamily, "Location", NeighborhoodClassification, NeighborhoodAnchor),
            new("map:location.sub-neighborhood", SourceFamily, "Location", SubNeighborhoodClassification, NeighborhoodAnchor, SubNeighborhoodLevel),
        ],

        // Only San Andreas is framed: the pinned San Andreas snapshot establishes it. No pinned source states a
        // world, continent or country, so those levels are skipped rather than declared.
        [
            new("frame:san-andreas", "Location", StateAnchor, "San Andreas", "en-US", [SanAndreasFrameEvidenceId]),
        ]);

}
