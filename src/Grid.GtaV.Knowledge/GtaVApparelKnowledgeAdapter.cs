using Grid.Core.Models;
namespace Grid.GtaV.Knowledge;

public sealed class GtaVApparelKnowledgeAdapter(ContentDigest digest, GtaVItemCorpusIndex index,
    CanonicalCatalogPayload? historical = null)
    : GtaVMountedItemKnowledgeAdapter("apparel", digest, index, historical);
