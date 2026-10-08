using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Projects a verified registration candidate into importable catalog payload assertions without re-authoring containment.</summary>
public static class GtaVLocationHierarchyRegistrationCandidatePackageProjector
{
    public static GtaVSecondaryAssertionBatch Project(
        ContentDigest adapterDigest,
        GtaVLocationHierarchyCorpusIndex index,
        CanonicalCatalogPayload baselinePayload,
        KnowledgeSourceScope scope,
        CanonicalRegistrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(baselinePayload);
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("Registration candidate must remain NOT_PUBLISHED.");
        var correlated = candidate.Relationships
            .Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated)
            .ToArray();
        if (correlated.Length != index.ExpectedRelationshipCount)
            throw new InvalidDataException("Registration candidate correlated relationship count does not match the pinned hierarchy catalog.");
        return new GtaVLocationHierarchySecondaryAssertionAdapter(adapterDigest, index)
            .ProjectFromCandidate(baselinePayload, scope, candidate, index);
    }
}
