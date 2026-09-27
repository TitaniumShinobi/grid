using System.Collections.Immutable;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;

namespace Grid.GtaV.Knowledge;

/// <summary>
/// Enhanced-only preproduction adapter for exact CMapZonesContainer mapzones.xml member bytes.
/// Zone Name values are source-native identities, not display terminology.
/// </summary>
public sealed class GtaVMapZonesKnowledgeAdapter : GtaVEnhancedKnowledgeAdapterBase
{
    public const string ParserIdentity = "grid.gta-v-enhanced.mapzones-xml";
    public const string ParserIdentityVersion = "1";
    public const string FormatId = "rockstar.gta-v.mapzones.cmapzonescontainer-xml";
    public const string ExactFormatVersion = "1";
    public const long MaximumArtifactBytes = 16L * 1024 * 1024;
    public const string NativeLocationType = "CMapZone";
    public const string AreaZoneSemanticRole = "grid.location.role.area-zone";
    public const string ClassificationMethod = "grid.gta-v.mapzones.cmapzone-semantic-role";
    public const string ClassificationMethodVersion = "1";
    public const string CoverageSourceFamily = "rockstar.gta-v.enhanced.mapzones.cmapzone";
    public const string CoverageManifestVersion = "1";

    private static readonly LocationSemanticRoleId AreaZoneRole = new(AreaZoneSemanticRole);
    private static readonly LocationSemanticVocabularyVersion SemanticVocabularyVersion = new(1);
    private static readonly LocationSourceFamilyId MapZonesSourceFamily = new(CoverageSourceFamily);

    public GtaVMapZonesKnowledgeAdapter(ContentDigest adapterArtifactDigest, GtaVSupportedSourceCorpusIndex? corpusIndex = null)
        : base(
            adapterArtifactDigest,
            "grid.gta-v.enhanced.mapzones",
            "mapzones-location-identities-v1",
            FormatId,
            "CMapZonesContainer",
            "mapzones.xml",
            KnowledgeKind.Location,
            MaximumArtifactBytes,
            maximumArtifacts: 8,
            maximumKnowledgeRecords: 10_000,
            corpusIndex)
    {
    }

    private protected override string ParserId => ParserIdentity;
    private protected override string ParserVersion => ParserIdentityVersion;
    private protected override KnowledgeKind KnowledgeKind => KnowledgeKind.Location;
    private protected override string RecordNamespace => "rockstar.gta-v.enhanced.mapzones";
    private protected override string RecordComparisonMethod => "grid.gta-v.map-zone-name.exact-utf8";

    private protected override ImmutableArray<LocationCoverageReport> CreateLocationCoverageReports(
        KnowledgeSourceScope sourceScope,
        ImmutableArray<AdapterBoundCanonicalCatalogRegistration> registrations,
        ImmutableArray<UnresolvedSourceAssertion> unresolvedAssertions)
    {
        if (!unresolvedAssertions.IsEmpty)
            throw new InvalidDataException("Map-zone coverage v1 does not emit unresolved source assertions.");

        var artifacts = registrations
            .SelectMany(value => value.Registration.Artifacts)
            .Select(value => value.Id)
            .Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var revisions = registrations
            .Select(value => value.Registration.SourceRevision.Id)
            .Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var records = registrations
            .SelectMany(value => value.Registration.KnowledgeRecords)
            .Where(value => value.Kind == KnowledgeKind.Location)
            .Select(value => value.Id)
            .Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var nativeType = registrations
            .SelectMany(value => value.Registration.SourceNativeLocationTypeAssertions)
            .Select(value => value.ExactNativeType)
            .Distinct()
            .Single();

        var declaration = new LocationSourceFamilyDeclaration(
            MapZonesSourceFamily,
            Format,
            Descriptor.RevisionId,
            isApplicable: true,
            artifacts,
            revisions,
            [AreaZoneRole]);
        var validation = new CatalogValidationSummary(
            CatalogValidationStatus.Candidate,
            "grid.location.coverage.qcs",
            "1",
            ContentDigest.ComputeSha256("grid.location.coverage.qcs.pending"u8));
        var manifestId = LocationCoverageManifestId.DeriveV1(
            sourceScope,
            CoverageManifestVersion,
            isClosed: false,
            validation,
            [declaration]);
        var manifest = new LocationCoverageManifest(
            manifestId,
            sourceScope,
            CoverageManifestVersion,
            isClosed: false,
            validation,
            [declaration]);
        var familyCoverage = new LocationSourceFamilyCoverage(
            MapZonesSourceFamily,
            artifacts,
            revisions,
            records.Length,
            records.Length,
            records.Length,
            records,
            [],
            [],
            unsupportedObjectCount: 0,
            parserErrorCount: 0,
            missingArtifactCount: 0,
            ambiguousClassificationCount: 0);
        return [LocationCoverageReport.Create(
            manifest,
            [familyCoverage],
            [new LocationSemanticCategoryCoverage(nativeType, AreaZoneRole, records, 0)],
            new LocationTerminologyCoverage(records.Length, 0, 0, records.Length, 0),
            new LocationHierarchyCoverage(0, 0, 0, 0, NotProvidedBySource: true),
            [],
            [])];
    }

    private protected override void AddRecordAssertions(
        ParsedRecord parsedRecord,
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        ImmutableArray<SourceNativeLocationTypeAssertion>.Builder locationNativeTypes,
        ImmutableArray<LocationSemanticClassificationAssertion>.Builder locationSemanticClassifications,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
        if (record.Kind != KnowledgeKind.Location ||
            !string.Equals(parsedRecord.ObjectType, NativeLocationType, StringComparison.Ordinal) ||
            parsedRecord.EvidenceLocations.IsEmpty)
            throw new InvalidDataException("A map-zone record cannot emit the required Location classification claims.");

        var recordLocator = parsedRecord.EvidenceLocations[0].RecordLocator;
        var exactNativeType = SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.mapzones.location-type",
            "location-record-type",
            NativeLocationType,
            "grid.gta-v.location-native-type.exact-utf8",
            1);
        var nativeTypeId = SourceNativeLocationTypeAssertionId.DeriveV1(
            record.Id,
            revisionId,
            exactNativeType,
            recordLocator);
        var nativeType = new SourceNativeLocationTypeAssertion(
            nativeTypeId,
            record.Id,
            revisionId,
            exactNativeType,
            recordLocator);
        locationNativeTypes.Add(nativeType);
        AddExactClaimEvidence(
            evidence,
            bindings,
            revisionId,
            artifact,
            record.Id,
            EvidenceClaimKind.LocationNativeType,
            recordLocator,
            recordLocator,
            EvidenceClaimContentId.DeriveV1(nativeType));

        var classificationId = LocationSemanticClassificationAssertionId.DeriveV1(
            record.Id,
            revisionId,
            nativeType.Id,
            AreaZoneRole,
            SemanticVocabularyVersion,
            ClassificationMethod,
            ClassificationMethodVersion,
            recordLocator);
        var classification = new LocationSemanticClassificationAssertion(
            classificationId,
            record.Id,
            revisionId,
            nativeType.Id,
            AreaZoneRole,
            SemanticVocabularyVersion,
            ClassificationMethod,
            ClassificationMethodVersion,
            recordLocator);
        locationSemanticClassifications.Add(classification);
        AddExactClaimEvidence(
            evidence,
            bindings,
            revisionId,
            artifact,
            record.Id,
            EvidenceClaimKind.LocationSemanticClassification,
            recordLocator,
            recordLocator,
            EvidenceClaimContentId.DeriveV1(classification));
    }

    private protected override ParsedArtifact Parse(FrozenSourceArtifact artifact)
    {
        var document = IndexedXml(artifact, MaximumArtifactBytes);
        var root = document.Root;
        if (root is null || root.Name != XName.Get("CMapZonesContainer"))
            throw new InvalidDataException("Only a CMapZonesContainer root is supported.");
        RejectNamespaces(root);

        var zonesElements = root.Elements("Zones").ToArray();
        if (zonesElements.Length != 1)
            throw new InvalidDataException("A map-zones resource must contain exactly one direct Zones collection.");

        var records = ImmutableArray.CreateBuilder<ParsedRecord>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in zonesElements[0].Elements("Item"))
        {
            var names = item.Elements("Name").ToArray();
            var zoneAreas = item.Elements("ZoneAreas").ToArray();
            var bounds = item.Elements("BoundBox").ToArray();
            if (names.Length != 1 || zoneAreas.Length != 1 || !zoneAreas[0].Elements().Any() || bounds.Length != 1)
                throw new InvalidDataException("Each zone must contain one Name, a non-empty ZoneAreas collection, and one BoundBox.");

            var nativeKey = names[0].Value;
            ValidateNativeIdentity("CMapZone", nativeKey);
            if (!identities.Add(nativeKey))
                throw new InvalidDataException("Duplicate source-native map-zone identities are ambiguous.");

            records.Add(new ParsedRecord(
                "CMapZone",
                nativeKey,
                [new ParsedEvidenceLocation(
                    CreateLocator(artifact.SourceCoordinate.ExactRepresentation, XmlPath(item)),
                    CreateLocator(artifact.SourceCoordinate.ExactRepresentation, XmlPath(names[0]))) ]));
        }

        if (records.Count == 0)
            throw new InvalidDataException("The map-zones resource contains no supported zone records.");
        return new ParsedArtifact(records.ToImmutable(), []);
    }

    private void ValidateNativeIdentity(string objectType, string value)
    {
        try
        {
            _ = SourceNativeIdentifier.FromExactUtf8(
                RecordNamespace,
                objectType,
                value,
                RecordComparisonMethod,
                1);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A map-zone Name is not a valid exact source-native identity.", exception);
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
            throw new InvalidDataException("The map-zones resource is not supported well-formed XML.", exception);
        }
    }

    private static void RejectNamespaces(XElement root)
    {
        if (root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Namespaced map-zones metadata is not supported by parser v1.");
    }
}
