namespace Grid.Core.Services;

/// <summary>Production realization and adapter coordinates for Contract 2 relationship registration.</summary>
public static class CanonicalRelationshipRegistrationProductionBindings
{
    public const string ProductionEvidenceAdapterId = "grid.gta-v.location-hierarchy.registration-evidence";
    public const string ProductionEvidenceAdapterVersion = "1";

    /// <summary>Version 2 is the corpus-driven Location registration; version 1 remains the pinned legacy proof.</summary>
    public const string ProductionLocationRegistrationAdapterVersion = "2";

    public static void EnsureProductionEvidenceAdapter(IRegistrationEvidenceAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (!string.Equals(adapter.Id, ProductionEvidenceAdapterId, StringComparison.Ordinal) ||
            adapter.Version is not (ProductionEvidenceAdapterVersion or ProductionLocationRegistrationAdapterVersion))
            throw new InvalidDataException(
                "Production registration requires the bound hierarchy evidence adapter: " + ProductionEvidenceAdapterId);
    }
}
