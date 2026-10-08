using Grid.Core.Models;
namespace Grid.GtaV.Knowledge;

public sealed class GtaVMountedVehicleKnowledgeAdapter(ContentDigest digest, GtaVItemCorpusIndex index,
    CanonicalCatalogPayload? historical = null)
    : GtaVMountedItemKnowledgeAdapter("vehicles", digest, index, historical);
