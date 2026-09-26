using System.Collections.Immutable;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public enum GtaVKnowledgeEdition
{
    Legacy = 0,
    Enhanced = 1,
}

/// <summary>
/// Preproduction-only deterministic adapter for exact frozen CWeaponInfoBlob weapons.meta bytes.
/// Archive acquisition is deliberately outside this adapter.
/// </summary>
public sealed class GtaVWeaponsMetaKnowledgeAdapter : IGameKnowledgeAdapter
{
    public const string ParserId = "grid.gta-v.weapons-meta-xml";
    public const string ParserVersion = "1";
    public const string FormatId = "rockstar.gta-v.weapons-meta.cweaponinfoblob-xml";
    public const string ExactFormatVersion = "1";
    public const long MaximumArtifactBytes = 64L * 1024 * 1024;

    private const string SourceComparisonMethod = "grid.gta-v.resource-coordinate.exact-utf8";
    private const string RecordComparisonMethod = "grid.gta-v.meta-name.exact-utf8";
    private const string AmmoRelationshipSemantic = "rockstar.gta-v.weapon-ammo-reference";
    private const string AmmoRelationshipType = "CWeaponInfo/AmmoInfo@ref";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly GameId _gameId;
    private readonly string _editionNamespace;

    public GtaVWeaponsMetaKnowledgeAdapter(
        GtaVKnowledgeEdition edition,
        ContentDigest adapterArtifactDigest)
    {
        if (!Enum.IsDefined(edition)) throw new ArgumentOutOfRangeException(nameof(edition));

        Edition = edition;
        _gameId = edition switch
        {
            GtaVKnowledgeEdition.Legacy => ProductionGridCatalogService.GrandTheftAutoVLegacyId,
            GtaVKnowledgeEdition.Enhanced => ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
            _ => throw new ArgumentOutOfRangeException(nameof(edition)),
        };
        _editionNamespace = edition == GtaVKnowledgeEdition.Legacy
            ? "rockstar.gta-v.legacy"
            : "rockstar.gta-v.enhanced";

        var editionId = edition == GtaVKnowledgeEdition.Legacy ? "legacy" : "enhanced";
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId($"grid.gta-v.{editionId}.weapons-meta"),
            "1",
            adapterArtifactDigest,
            1,
            "weapons-meta-items-ammo-relations-v1",
            [_gameId],
            [new SupportedKnowledgeFormat(
                FormatId,
                ExactFormatVersion,
                ["loose-file", "rpf7-member"],
                ["CWeaponInfoBlob"],
                [KnowledgeKind.Item],
                supportsTerminology: false,
                supportsRelationships: true,
                supportsHierarchy: false)],
            new KnowledgeAdapterResourceLimits(
                MaximumArtifactBytes,
                maximumArtifacts: 64,
                maximumKnowledgeRecords: 100_000,
                maximumRelationships: 100_000));
    }

    public GtaVKnowledgeEdition Edition { get; }
    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public static KnowledgeFormatCoordinate Format { get; } = new(FormatId, ExactFormatVersion);

    public SourceNativeIdentifier CreateSourceCoordinate(string exactCoordinate)
    {
        ValidateResourceCoordinate(exactCoordinate);
        return SourceNativeIdentifier.FromExactUtf8(
            _editionNamespace + ".resource-coordinate",
            "CWeaponInfoBlob",
            exactCoordinate,
            SourceComparisonMethod,
            1);
    }

    public Task<SourceDiscoveryResult> DiscoverAsync(
        PreproductionSourceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.GameId != _gameId)
            return Task.FromResult(UnsupportedDiscovery(request.Artifacts, "gta.game.unsupported"));
        if (request.Artifacts.Length > Descriptor.ResourceLimits.MaximumArtifacts)
            return Task.FromResult(UnsupportedDiscovery(request.Artifacts, "gta.artifact-count.limit-exceeded"));

        var candidates = ImmutableArray.CreateBuilder<DiscoveredKnowledgeSource>();
        var unsupported = ImmutableArray.CreateBuilder<UnsupportedKnowledgeArtifact>();
        var issues = ImmutableArray.CreateBuilder<KnowledgeBuildIssue>();

        foreach (var artifact in OrderArtifacts(request.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDeclaredFormat(artifact.DeclaredFormat) || !IsAcceptedCoordinate(artifact.SourceCoordinate))
            {
                unsupported.Add(new(artifact.Id, "gta.format-or-coordinate.unsupported"));
                continue;
            }

            try
            {
                if (ParseWeaponsMeta(artifact.ExactBytes.AsSpan(), artifact.SourceCoordinate.ExactRepresentation).IsEmpty)
                {
                    unsupported.Add(new(artifact.Id, "gta.weapons-meta.no-supported-records"));
                    continue;
                }
            }
            catch (InvalidDataException)
            {
                unsupported.Add(new(artifact.Id, "gta.weapons-meta.malformed-or-ambiguous"));
                continue;
            }

            var source = CreateSource(request.SourceScope, artifact.SourceCoordinate);
            candidates.Add(new DiscoveredKnowledgeSource(source, null, [artifact.FormatBinding]));
        }

        if (candidates.Count == 0)
        {
            issues.Add(new("gta.weapons-meta.unsupported", "No exact supported CWeaponInfoBlob weapons.meta source was discovered."));
            return Task.FromResult(new SourceDiscoveryResult(
                [],
                unsupported.ToImmutable(),
                KnowledgeCoverageState.Unsupported,
                issues.ToImmutable()));
        }

        issues.Add(new("gta.coverage.item-only", "This adapter provides partial Item coverage only."));
        return Task.FromResult(new SourceDiscoveryResult(
            candidates.ToImmutable(),
            unsupported.ToImmutable(),
            KnowledgeCoverageState.Partial,
            issues.ToImmutable()));
    }

    public Task<KnowledgeExtractionResult> ExtractAsync(
        PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.GameId != _gameId || request.AdapterRevisionId != Descriptor.RevisionId)
            return Task.FromResult(UnsupportedExtraction("gta.adapter-or-game.unsupported"));
        if (request.Artifacts.Length > Descriptor.ResourceLimits.MaximumArtifacts)
            return Task.FromResult(UnsupportedExtraction("gta.artifact-count.limit-exceeded"));

        var registrations = ImmutableArray.CreateBuilder<AdapterBoundCanonicalCatalogRegistration>();
        var issues = ImmutableArray.CreateBuilder<KnowledgeBuildIssue>();
        foreach (var artifact in OrderArtifacts(request.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDeclaredFormat(artifact.DeclaredFormat) || !IsAcceptedCoordinate(artifact.SourceCoordinate))
                continue;

            try
            {
                registrations.Add(CreateRegistration(request.SourceScope, artifact));
            }
            catch (InvalidDataException)
            {
                issues.Add(new("gta.weapons-meta.malformed-or-ambiguous", artifact.Id.Value));
            }
        }

        if (registrations.Count == 0)
            return Task.FromResult(UnsupportedExtraction(
                "gta.weapons-meta.unsupported",
                issues.ToImmutable()));

        issues.Add(new("gta.coverage.location.unsupported", "No Location classification is established by weapons.meta."));
        issues.Add(new("gta.coverage.mission-quest.unsupported", "No MissionQuest classification is established by weapons.meta."));
        issues.Add(new("gta.coverage.actor.unsupported", "No Actor classification is established by weapons.meta."));
        return Task.FromResult(new KnowledgeExtractionResult(
            registrations.ToImmutable(),
            [],
            [],
            KnowledgeCoverageState.Partial,
            issues.ToImmutable()));
    }

    private AdapterBoundCanonicalCatalogRegistration CreateRegistration(
        KnowledgeSourceScope sourceScope,
        FrozenSourceArtifact artifact)
    {
        var parsedRecords = ParseWeaponsMeta(artifact.ExactBytes.AsSpan(), artifact.SourceCoordinate.ExactRepresentation);
        if (parsedRecords.IsEmpty)
            throw new InvalidDataException("The weapons.meta resource contains no supported CWeaponInfo or CAmmoInfo records.");
        if (parsedRecords.Length > Descriptor.ResourceLimits.MaximumKnowledgeRecords)
            throw new InvalidDataException("The weapons.meta resource exceeds the adapter record limit.");

        var source = CreateSource(sourceScope, artifact.SourceCoordinate);
        var revisionId = CatalogSourceRevisionId.DeriveV2(
            source.Id,
            null,
            [artifact.Id],
            Descriptor.RevisionId);
        var sourceRevision = new CatalogSourceRevisionRecord(revisionId, source.Id, null, [artifact.Id]);

        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>(parsedRecords.Length);
        var identities = new Dictionary<ParsedRecordKey, CanonicalKnowledgeRecord>();
        foreach (var parsed in parsedRecords)
        {
            var nativeIdentity = CreateRecordIdentity(parsed.ObjectType, parsed.NativeKey);
            var nativeRecordId = NativeRecordIdentityId.DeriveV1(_gameId, nativeIdentity);
            var recordId = KnowledgeRecordId.DeriveV1(
                _gameId,
                sourceScope.ExactGameVersion,
                sourceScope.ExactModVersion,
                revisionId,
                KnowledgeKind.Item,
                nativeRecordId);
            var record = new CanonicalKnowledgeRecord(
                recordId,
                _gameId,
                sourceScope.ExactGameVersion,
                sourceScope.ExactModVersion,
                revisionId,
                KnowledgeKind.Item,
                nativeRecordId,
                nativeIdentity);
            if (!identities.TryAdd(new ParsedRecordKey(parsed.ObjectType, parsed.NativeKey), record))
                throw new InvalidDataException($"The weapons.meta resource contains duplicate {parsed.ObjectType} identity content.");
            records.Add(record);
        }

        var evidence = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        foreach (var parsed in parsedRecords)
        {
            var record = identities[new ParsedRecordKey(parsed.ObjectType, parsed.NativeKey)];
            AddFileEvidence(
                evidence,
                bindings,
                revisionId,
                artifact,
                record.Id,
                EvidenceClaimKind.KnowledgeIdentity,
                parsed.RecordLocator,
                parsed.IdentityFieldLocator,
                null);
        }

        var relationships = ImmutableArray.CreateBuilder<RelationshipAssertion>();
        foreach (var parsed in parsedRecords.Where(value => value.AmmoReference is not null))
        {
            var subject = identities[new ParsedRecordKey(parsed.ObjectType, parsed.NativeKey)];
            var targetIdentity = CreateRecordIdentity("CAmmoInfo", parsed.AmmoReference!);
            identities.TryGetValue(new ParsedRecordKey("CAmmoInfo", parsed.AmmoReference!), out var resolvedTarget);
            var relationship = new RelationshipAssertion(
                subject.Id,
                revisionId,
                new RelationshipSemanticId(AmmoRelationshipSemantic),
                AmmoRelationshipType,
                parsed.AmmoFieldLocator!,
                targetIdentity,
                resolvedTarget?.Id);
            relationships.Add(relationship);
            AddFileEvidence(
                evidence,
                bindings,
                revisionId,
                artifact,
                subject.Id,
                EvidenceClaimKind.Relationship,
                parsed.RecordLocator,
                parsed.AmmoFieldLocator!,
                EvidenceClaimContentId.DeriveV1(relationship));
        }

        if (relationships.Count > Descriptor.ResourceLimits.MaximumRelationships)
            throw new InvalidDataException("The weapons.meta resource exceeds the adapter relationship limit.");

        var registration = new CanonicalCatalogRegistration(
            source,
            [new SourceArtifactRecord(artifact.Id, artifact.Digest)],
            sourceRevision,
            records.ToImmutable(),
            [],
            relationships.ToImmutable(),
            evidence.ToImmutable(),
            [],
            bindings.ToImmutable());
        return new AdapterBoundCanonicalCatalogRegistration(
            registration,
            Descriptor,
            sourceScope,
            [artifact.FormatBinding]);
    }

    private CatalogSourceRecord CreateSource(
        KnowledgeSourceScope scope,
        SourceNativeIdentifier nativeIdentity)
    {
        var kind = scope.ScopeKind == KnowledgeSourceScopeKind.BaseGame
            ? KnowledgeSourceKind.LocalGameDistribution
            : KnowledgeSourceKind.LocalModArtifact;
        return new(CatalogSourceId.DeriveV1(kind, nativeIdentity), kind, nativeIdentity);
    }

    private void AddFileEvidence(
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        KnowledgeRecordId recordId,
        EvidenceClaimKind claimKind,
        string nativeRecordLocator,
        string fieldLocator,
        EvidenceClaimContentId? claimContentId)
    {
        var receipt = new FileEvidenceReceipt(
            revisionId,
            artifact.Id,
            artifact.Digest,
            ParserId,
            ParserVersion,
            nativeRecordLocator,
            fieldLocator,
            null,
            null,
            null,
            artifact.ObservedAtUtc);
        var receiptId = EvidenceReceiptId.DeriveV1(receipt);
        evidence.Add(new(receiptId, receipt));
        bindings.Add(new(
            EvidenceBindingId.DeriveV2(
                receiptId,
                claimKind,
                recordId,
                revisionId,
                fieldLocator,
                claimContentId),
            receiptId,
            claimKind,
            recordId,
            revisionId,
            fieldLocator,
            claimContentId));
    }

    private SourceNativeIdentifier CreateRecordIdentity(string objectType, string nativeKey) =>
        SourceNativeIdentifier.FromExactUtf8(
            _editionNamespace + ".weapons.meta",
            objectType,
            nativeKey,
            RecordComparisonMethod,
            1);

    private bool IsAcceptedCoordinate(SourceNativeIdentifier coordinate)
    {
        if (!string.Equals(coordinate.Namespace, _editionNamespace + ".resource-coordinate", StringComparison.Ordinal) ||
            !string.Equals(coordinate.ObjectType, "CWeaponInfoBlob", StringComparison.Ordinal) ||
            !string.Equals(coordinate.ComparisonMethodId, SourceComparisonMethod, StringComparison.Ordinal) ||
            coordinate.ComparisonMethodVersion != 1)
            return false;
        try
        {
            ValidateResourceCoordinate(coordinate.ExactRepresentation);
            return coordinate.IdentityBytes.AsSpan().SequenceEqual(
                StrictUtf8.GetBytes(coordinate.ExactRepresentation));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsDeclaredFormat(KnowledgeFormatCoordinate format) =>
        string.Equals(format.FormatId, FormatId, StringComparison.Ordinal) &&
        string.Equals(format.ExactFormatVersion, ExactFormatVersion, StringComparison.Ordinal);

    private static IOrderedEnumerable<FrozenSourceArtifact> OrderArtifacts(
        ImmutableArray<FrozenSourceArtifact> artifacts) =>
        artifacts
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ThenBy(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.DeclaredFormat.FormatId, StringComparer.Ordinal)
            .ThenBy(value => value.DeclaredFormat.ExactFormatVersion, StringComparer.Ordinal);

    private static SourceDiscoveryResult UnsupportedDiscovery(
        ImmutableArray<FrozenSourceArtifact> artifacts,
        string reasonCode) =>
        new(
            [],
            artifacts.Select(value => new UnsupportedKnowledgeArtifact(value.Id, reasonCode)).ToImmutableArray(),
            KnowledgeCoverageState.Unsupported,
            [new KnowledgeBuildIssue(reasonCode, "The adapter cannot inspect this discovery request.")]);

    private static KnowledgeExtractionResult UnsupportedExtraction(
        string issueCode,
        ImmutableArray<KnowledgeBuildIssue> priorIssues = default)
    {
        var issues = priorIssues.IsDefault
            ? ImmutableArray<KnowledgeBuildIssue>.Empty
            : priorIssues;
        return new([], [], [], KnowledgeCoverageState.Unsupported,
            issues.Add(new(issueCode, "No canonical output was emitted.")));
    }

    private static ImmutableArray<ParsedWeaponsRecord> ParseWeaponsMeta(
        ReadOnlySpan<byte> bytes,
        string resourceCoordinate)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumArtifactBytes)
            throw new InvalidDataException("The frozen weapons.meta artifact has an invalid size.");

        XDocument document;
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumArtifactBytes,
                IgnoreComments = false,
                IgnoreProcessingInstructions = false,
                IgnoreWhitespace = false,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException or DecoderFallbackException)
        {
            throw new InvalidDataException("The frozen resource is not a supported well-formed weapons.meta XML document.", exception);
        }

        if (document.Root is null || document.Root.Name != XName.Get("CWeaponInfoBlob"))
            throw new InvalidDataException("Only a CWeaponInfoBlob weapons.meta root is supported.");
        if (document.Root.DescendantsAndSelf().Any(value => value.Name.Namespace != XNamespace.None) ||
            document.Root.DescendantsAndSelf().Attributes().Any(value => value.Name.Namespace != XNamespace.None))
            throw new InvalidDataException("Namespaced GTA metadata is not supported by the v1 parser.");

        var records = ImmutableArray.CreateBuilder<ParsedWeaponsRecord>();
        foreach (var item in document.Descendants("Item"))
        {
            var typeAttribute = item.Attribute("type");
            if (typeAttribute is null ||
                (typeAttribute.Value != "CWeaponInfo" && typeAttribute.Value != "CAmmoInfo"))
                continue;

            var nameElements = item.Elements("Name").ToArray();
            if (nameElements.Length != 1)
                throw new InvalidDataException($"Each {typeAttribute.Value} record must contain exactly one direct Name field.");
            var nativeKey = nameElements[0].Value;
            try
            {
                _ = SourceNativeIdentifier.FromExactUtf8(
                    "grid.gta-v.validation",
                    typeAttribute.Value,
                    nativeKey,
                    RecordComparisonMethod,
                    1);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException($"A {typeAttribute.Value} Name field is not a valid exact source-native identifier.", exception);
            }

            string? ammoReference = null;
            string? ammoFieldLocator = null;
            if (typeAttribute.Value == "CWeaponInfo")
            {
                var ammoElements = item.Elements("AmmoInfo").ToArray();
                if (ammoElements.Length > 1)
                    throw new InvalidDataException("A CWeaponInfo record contains more than one direct AmmoInfo field.");
                if (ammoElements.Length == 1)
                {
                    var referenceAttribute = ammoElements[0].Attribute("ref") ??
                        throw new InvalidDataException("A CWeaponInfo AmmoInfo field must contain an exact ref attribute.");
                    ammoReference = referenceAttribute.Value;
                    try
                    {
                        _ = SourceNativeIdentifier.FromExactUtf8(
                            "grid.gta-v.validation",
                            "CAmmoInfo",
                            ammoReference,
                            RecordComparisonMethod,
                            1);
                    }
                    catch (ArgumentException exception)
                    {
                        throw new InvalidDataException("A CWeaponInfo AmmoInfo ref is not a valid exact source-native identifier.", exception);
                    }
                    ammoFieldLocator = CreateLocator(resourceCoordinate, GetElementPath(ammoElements[0]) + "/@ref");
                }
            }

            records.Add(new(
                typeAttribute.Value,
                nativeKey,
                CreateLocator(resourceCoordinate, GetElementPath(item)),
                CreateLocator(resourceCoordinate, GetElementPath(nameElements[0])),
                ammoReference,
                ammoFieldLocator));
        }

        return records.ToImmutable();
    }

    private static string GetElementPath(XElement element)
    {
        var components = element.AncestorsAndSelf().Reverse().Select(value =>
        {
            var index = value.Parent is null
                ? 1
                : value.ElementsBeforeSelf(value.Name).Count() + 1;
            return $"/{value.Name.LocalName}[{index}]";
        });
        return string.Concat(components);
    }

    private static string CreateLocator(string resourceCoordinate, string xmlPath) =>
        $"{(resourceCoordinate.Contains("!/", StringComparison.Ordinal) ? "rpf7-member" : "loose-file")}:{resourceCoordinate}#{xmlPath}";

    private static void ValidateResourceCoordinate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A source-native resource coordinate is required.", nameof(value));
        _ = StrictUtf8.GetBytes(value);
        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Contains('#') ||
            value.Any(char.IsControl))
            throw new ArgumentException("The resource coordinate must be a relative forward-slash source coordinate.", nameof(value));
        if (!value.EndsWith("weapons.meta", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only exact weapons.meta source coordinates are supported.", nameof(value));

        var archiveParts = value.Split("!/", StringSplitOptions.None);
        if (archiveParts.Length > 1 && archiveParts.Take(archiveParts.Length - 1)
                .Any(part => !part.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Each archive component of an RPF member coordinate must be an exact .rpf name.", nameof(value));
        for (var partIndex = 0; partIndex < archiveParts.Length; partIndex++)
        {
            var part = archiveParts[partIndex];
            var segments = part.Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('!')))
                throw new ArgumentException("The resource coordinate contains an invalid segment.", nameof(value));
            var isArchiveComponent = archiveParts.Length > 1 && partIndex < archiveParts.Length - 1;
            if (!isArchiveComponent && segments.Take(segments.Length - 1)
                    .Any(segment => segment.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("An RPF member coordinate must use an explicit !/ archive boundary.", nameof(value));
        }
    }

    private sealed record ParsedWeaponsRecord(
        string ObjectType,
        string NativeKey,
        string RecordLocator,
        string IdentityFieldLocator,
        string? AmmoReference,
        string? AmmoFieldLocator);

    private readonly record struct ParsedRecordKey(string ObjectType, string NativeKey);
}
