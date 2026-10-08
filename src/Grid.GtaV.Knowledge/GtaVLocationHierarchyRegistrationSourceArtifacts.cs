using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

public static class GtaVLocationHierarchyRegistrationSourceArtifacts
{
    public static RegistrationSourceArtifact[] CreateAdmittedSources(GtaVLocationHierarchyCorpusIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return index.Artifacts.Select(Create).OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();

        RegistrationSourceArtifact Create(FrozenSourceArtifact artifact)
        {
            var bytes = artifact.ExactBytes.ToArray();
            var sha256 = artifact.Digest.HexValue;
            return new(
                new RegistrationSource(
                    artifact.Id.Value,
                    artifact.SourceCoordinate.ExactRepresentation,
                    KnowledgeSourceKind.ReferenceProvider,
                    sha256,
                    GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId,
                    GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterVersion,
                    GtaVLocationHierarchyRegistrationRules.SourceFamily),
                bytes);
        }
    }
}
