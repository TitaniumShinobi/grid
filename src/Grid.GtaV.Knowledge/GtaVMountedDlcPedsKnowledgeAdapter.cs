using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>Registers exact mounted DLC peds.meta InitDatas objects as source-family Actors.</summary>
public sealed class GtaVMountedDlcPedsKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.mounted-dlc-peds-xml-index";
    public const string ParserIdentityVersion = "1";
    public const long MaximumArtifactBytes = 64L * 1024 * 1024;
    private readonly GtaVActorCorpusIndex _actorIndex;

    public GtaVMountedDlcPedsKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVActorCorpusIndex actorIndex)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.mounted-dlc-ped-actors",
            "mounted-dlc-ped-name-identities-v1",
            GtaVActorCorpusIndex.DlcXmlFormatId,
            "CPedModelInfo__InitDataListXml",
            "peds.meta",
            KnowledgeKind.Actor,
            MaximumArtifactBytes,
            maximumArtifacts: 256,
            maximumKnowledgeRecords: 10_000)
    {
        _actorIndex = actorIndex ?? throw new ArgumentNullException(nameof(actorIndex));
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.Actor;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.mounted-dlc-ped-model";
    private protected override string RecordComparisonMethod => "grid.gta-v.ped-model-name.exact-utf8";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var source = _actorIndex.GetDlc(artifact);
        var records = source.Records.Select(value => new ParsedRecord(
            "CPedModelInfo__InitData.Name",
            value.NameExact,
            [new ParsedEvidenceLocation(value.RecordLocator, value.NameFieldLocator)]))
            .ToImmutableArray();
        return new ParsedArtifact(records, []);
    }
}
