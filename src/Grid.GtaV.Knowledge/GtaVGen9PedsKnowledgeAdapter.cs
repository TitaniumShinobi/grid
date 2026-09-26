using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Enhanced-only preproduction adapter for gen9_exclusive_assets_peds.meta member bytes.
/// PedModelName values are source-native Actor identities, never display terminology.
/// </summary>
public sealed class GtaVGen9PedsKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.gen9-exclusive-peds-xml";
    public const string ParserIdentityVersion = "1";
    public const string FormatId = "rockstar.gta-v.gen9-exclusive-assets-peds-xml";
    public const string ExactFormatVersion = "1";
    public const long MaximumArtifactBytes = 16L * 1024 * 1024;

    public GtaVGen9PedsKnowledgeAdapter(ContentDigest adapterArtifactDigest)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.gen9-exclusive-peds",
            "ped-model-actor-identities-v1",
            FormatId,
            "Gen9ExclusiveAssetsDataPeds",
            "gen9_exclusive_assets_peds.meta",
            KnowledgeKind.Actor,
            MaximumArtifactBytes,
            maximumArtifacts: 8,
            maximumKnowledgeRecords: 10_000)
    {
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.Actor;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.gen9-exclusive-peds";
    private protected override string RecordComparisonMethod => "grid.gta-v.ped-model-name.exact-utf8";

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var document = ParseXml(DecodeStrictUtf8(artifact.ExactBytes.AsSpan(), MaximumArtifactBytes));
        var root = document.Root;
        if (root is null || root.Name != XName.Get("Gen9ExclusiveAssetsDataPeds"))
            throw new InvalidDataException("Only a Gen9ExclusiveAssetsDataPeds root is supported.");
        RejectNamespaces(root);

        var pedData = root.Elements("PedData").ToArray();
        if (pedData.Length != 1)
            throw new InvalidDataException("The peds resource must contain exactly one direct PedData collection.");

        var occurrences = new Dictionary<string, ImmutableArray<ParsedEvidenceLocation>.Builder>(StringComparer.Ordinal);
        foreach (var item in pedData[0].Elements("Item"))
        {
            var modelNames = item.Elements("PedModelName").ToArray();
            if (modelNames.Length != 1)
                throw new InvalidDataException("Each PedData entry must contain exactly one direct PedModelName.");

            var nativeKey = modelNames[0].Value;
            ValidateNativeIdentity(nativeKey);
            if (!occurrences.TryGetValue(nativeKey, out var locations))
            {
                locations = ImmutableArray.CreateBuilder<ParsedEvidenceLocation>();
                occurrences.Add(nativeKey, locations);
            }
            locations.Add(new ParsedEvidenceLocation(
                CreateLocator(artifact.SourceCoordinate.ExactRepresentation, XmlPath(item)),
                CreateLocator(artifact.SourceCoordinate.ExactRepresentation, XmlPath(modelNames[0]))));
        }

        if (occurrences.Count == 0)
            throw new InvalidDataException("The peds resource contains no supported actor identities.");

        var records = occurrences
            .OrderBy(value => value.Key, StringComparer.Ordinal)
            .Select(value => new ParsedRecord(
                "PedModelName",
                value.Key,
                value.Value.OrderBy(location => location.FieldLocator, StringComparer.Ordinal).ToImmutableArray()))
            .ToImmutableArray();
        return new ParsedArtifact(records, []);
    }

    private void ValidateNativeIdentity(string value)
    {
        try
        {
            _ = SourceNativeIdentifier.FromExactUtf8(
                RecordNamespace,
                "PedModelName",
                value,
                RecordComparisonMethod,
                1);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A PedModelName is not a valid exact source-native identity.", exception);
        }
    }

    private static XDocument ParseXml(string text)
    {
        try
        {
            using var textReader = new StringReader(text);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidDataException("The peds resource is not supported well-formed XML.", exception);
        }
    }

    private static void RejectNamespaces(XElement root)
    {
        if (root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Namespaced peds metadata is not supported by parser v1.");
    }
}
