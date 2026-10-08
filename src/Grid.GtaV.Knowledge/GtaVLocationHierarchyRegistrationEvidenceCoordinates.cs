using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Production evidence adapter coordinates bound to MDBO discover-evidence for GTA hierarchy registration.</summary>
public static class GtaVLocationHierarchyRegistrationEvidenceCoordinates
{
    public const string AdapterId = "grid.gta-v.location-hierarchy.registration-evidence";
    public const string AdapterVersion = "1";

    public static bool IsProductionAdapter(IRegistrationEvidenceAdapter adapter) =>
        string.Equals(adapter.Id, AdapterId, StringComparison.Ordinal) &&
        string.Equals(adapter.Version, AdapterVersion, StringComparison.Ordinal);
}
