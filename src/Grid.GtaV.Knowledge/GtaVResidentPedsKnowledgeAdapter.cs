using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>Registers the effective mounted resident ped-init PSO as hash-native Actors.</summary>
public sealed class GtaVResidentPedsKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.resident-ped-init-index";
    public const string ParserIdentityVersion = "1";
    public const long MaximumArtifactBytes = 64L * 1024 * 1024;
    private readonly GtaVActorCorpusIndex _actorIndex;

    public GtaVResidentPedsKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVActorCorpusIndex actorIndex)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.resident-ped-actors",
            "resident-ped-name-hash-identities-v1",
            GtaVActorCorpusIndex.ResidentFormatId,
            "CPedModelInfo__InitDataListPso",
            "peds.ymt",
            KnowledgeKind.Actor,
            MaximumArtifactBytes,
            maximumArtifacts: 1,
            maximumKnowledgeRecords: 1_000)
    {
        _actorIndex = actorIndex ?? throw new ArgumentNullException(nameof(actorIndex));
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.Actor;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.resident-ped-model";
    private protected override string RecordComparisonMethod => "grid.gta-v.joaat32-unsigned";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var source = _actorIndex.GetResident(artifact);
        var records = source.Records.Select(value => new ParsedRecord(
            "CPedModelInfo__InitData.NameHash",
            $"0x{value.NameHash:X8}",
            [new ParsedEvidenceLocation(value.RecordLocator, value.NameFieldLocator)]))
            .ToImmutableArray();
        return new ParsedArtifact(records, []);
    }

    private protected override SourceNativeIdentifier CreateNativeIdentity(string objectType, string exactValue)
    {
        if (!string.Equals(objectType, "CPedModelInfo__InitData.NameHash", StringComparison.Ordinal) ||
            exactValue.Length != 10 || !exactValue.StartsWith("0x", StringComparison.Ordinal) ||
            !uint.TryParse(exactValue.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var hash) || hash == 0)
            throw new InvalidDataException("A resident Actor requires one exact nonzero 0xXXXXXXXX Name hash.");
        return GtaVActorCorpusIndex.ResidentIdentity(hash);
    }
}
