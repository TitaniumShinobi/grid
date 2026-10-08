using System.Globalization;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// v3 Location rules: each evidenced semantic level maps to its checklist entity slot; half levels sit strictly
/// between slots. No setting frame is declared: every geography level is an evidenced entity.
/// </summary>
public static class GtaVLocationRegistrationRules
{
    public const string SourceFamily = "gta-v.location-registration.v3";
    public const string UnresolvedClassification = "level:unresolved";
    public const string RuleSetId = "grid.gta-v.location-registration.rules.v3";

    /// <summary>
    /// A reference Location's native identity is the pinned page revision that establishes it. Its published record
    /// is bound to that revision and persisted records are immutable by id, so the id changes exactly when the
    /// identity evidence changes; the place name is presentation.
    /// </summary>
    public const string ReferenceSubjectNamespace = "grid.gta-v.location-registration.reference-page-subject";

    public static GtaVLocationReferencePage IdentityPage(IEnumerable<GtaVLocationPageFacts> pages) =>
        pages.Select(p => p.Page).OrderBy(p => p.Artifact.Id.Value, StringComparer.Ordinal).First();

    public static string ReferenceEntityKey(string gameId, GtaVLocationReferencePage identityPage) =>
        CanonicalRegistrationEncoding.Identity("Location", gameId, ReferenceSubjectNamespace, identityPage.Coordinate);

    /// <summary>Adapter digest from semantic coordinates rather than build bytes, so rebuilding the same mapping
    /// reproduces the same revisions and immutable records.</summary>
    public static ContentDigest SemanticAdapterDigest() => ContentDigest.ComputeSha256(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",
        SourceFamily, RuleSetId, CanonicalRelationshipRegistrationProductionBindings.ProductionLocationRegistrationAdapterVersion,
        CanonicalRegistrationEngineCoordinates.EngineId, CanonicalRegistrationEngineCoordinates.EngineVersion)));

    public static readonly string[] SlotAnchors =
    [
        "Location/world",
        "Location/world/continent",
        "Location/world/continent/country",
        "Location/world/continent/country/state",
        "Location/world/continent/country/state/county-region",
        "Location/world/continent/country/state/county-region/city",
        "Location/world/continent/country/state/county-region/city/town-neighborhood",
        "Location/world/continent/country/state/county-region/city/town-neighborhood/street",
        "Location/world/continent/country/state/county-region/city/town-neighborhood/street/structure",
        "Location/world/continent/country/state/county-region/city/town-neighborhood/street/structure/room",
    ];

    public static string Classification(decimal level) => "level:" + level.ToString("0.#", CultureInfo.InvariantCulture);

    public static RegistrationRuleSet Create()
    {
        var rules = new List<RegistrationMappingRule>();
        for (var slot = 1; slot <= SlotAnchors.Length; slot++)
        {
            var anchor = SlotAnchors[slot - 1];
            var name = anchor[(anchor.LastIndexOf('/') + 1)..];
            rules.Add(new("map:location.l" + slot.ToString(CultureInfo.InvariantCulture) + "." + name, SourceFamily, "Location", Classification(slot), anchor));
            if (slot < SlotAnchors.Length)
            {
                var half = slot + 0.5m;
                rules.Add(new("map:location.l" + half.ToString("0.0", CultureInfo.InvariantCulture) + "." + name, SourceFamily, "Location",
                    Classification(half), anchor, half));
            }
        }
        return new(RuleSetId, rules.ToArray());
    }
}
