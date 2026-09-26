using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

public abstract class GtaVEnhancedKnowledgeAdapterBase : IGameKnowledgeAdapter
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string SourceComparisonMethod = "grid.gta-v.resource-coordinate.exact-utf8";
    private readonly string _sourceObjectType;
    private readonly string _requiredCoordinateSuffix;

    private protected GtaVEnhancedKnowledgeAdapterBase(
        ContentDigest adapterArtifactDigest,
        string adapterId,
        string mappingRulesVersion,
        string formatId,
        string sourceObjectType,
        string requiredCoordinateSuffix,
        KnowledgeKind knowledgeKind,
        long maximumArtifactBytes,
        int maximumArtifacts,
        int maximumKnowledgeRecords)
    {
        _sourceObjectType = sourceObjectType;
        _requiredCoordinateSuffix = requiredCoordinateSuffix;
        Format = new KnowledgeFormatCoordinate(formatId, "1");
        Descriptor = new GameKnowledgeAdapterDescriptor(
            new KnowledgeAdapterId(adapterId),
            "1",
            adapterArtifactDigest,
            1,
            mappingRulesVersion,
            [ProductionGridCatalogService.GrandTheftAutoVEnhancedId],
            [new SupportedKnowledgeFormat(
                formatId,
                "1",
                ["rpf7-member"],
                [sourceObjectType],
                [knowledgeKind],
                supportsTerminology: false,
                supportsRelationships: false,
                supportsHierarchy: false)],
            new KnowledgeAdapterResourceLimits(
                maximumArtifactBytes,
                maximumArtifacts,
                maximumKnowledgeRecords,
                maximumRelationships: 1),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion);
    }

    public GameKnowledgeAdapterDescriptor Descriptor { get; }
    public KnowledgeFormatCoordinate Format { get; }

    private protected abstract string ParserId { get; }
    private protected abstract string ParserVersion { get; }
    private protected abstract KnowledgeKind KnowledgeKind { get; }
    private protected abstract string RecordNamespace { get; }
    private protected abstract string RecordComparisonMethod { get; }
    private protected abstract ParsedArtifact Parse(FrozenSourceArtifact artifact);

    public SourceNativeIdentifier CreateSourceCoordinate(string exactCoordinate)
    {
        ValidateResourceCoordinate(exactCoordinate, _requiredCoordinateSuffix);
        return SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.resource-coordinate",
            _sourceObjectType,
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

        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId)
            return Task.FromResult(UnsupportedDiscovery(request.Artifacts, "gta.game.unsupported"));
        if (request.Artifacts.Length > Descriptor.ResourceLimits.MaximumArtifacts)
            return Task.FromResult(UnsupportedDiscovery(request.Artifacts, "gta.artifact-count.limit-exceeded"));

        var candidates = ImmutableArray.CreateBuilder<DiscoveredKnowledgeSource>();
        var unsupported = ImmutableArray.CreateBuilder<UnsupportedKnowledgeArtifact>();
        foreach (var artifact in OrderArtifacts(request.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAcceptedArtifact(artifact))
            {
                unsupported.Add(new(artifact.Id, "gta.format-or-coordinate.unsupported"));
                continue;
            }

            try
            {
                var parsed = Parse(artifact);
                if (parsed.Records.IsEmpty && parsed.UnresolvedAssertions.IsEmpty)
                {
                    unsupported.Add(new(artifact.Id, "gta.source.no-supported-records"));
                    continue;
                }

                candidates.Add(new DiscoveredKnowledgeSource(
                    CreateSource(request.SourceScope, artifact.SourceCoordinate),
                    null,
                    [artifact.FormatBinding]));
            }
            catch (InvalidDataException)
            {
                unsupported.Add(new(artifact.Id, "gta.source.malformed-or-ambiguous"));
            }
        }

        if (candidates.Count == 0)
            return Task.FromResult(new SourceDiscoveryResult(
                [],
                unsupported.ToImmutable(),
                KnowledgeCoverageState.Unsupported,
                [new KnowledgeBuildIssue("gta.source.unsupported", "No exact supported source was discovered.")]));

        return Task.FromResult(new SourceDiscoveryResult(
            candidates.ToImmutable(),
            unsupported.ToImmutable(),
            KnowledgeCoverageState.Partial,
            [new KnowledgeBuildIssue("gta.coverage.partial", "This adapter provides bounded partial coverage for one knowledge kind.")]));
    }

    public Task<KnowledgeExtractionResult> ExtractAsync(
        PreproductionKnowledgeExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.GameId != ProductionGridCatalogService.GrandTheftAutoVEnhancedId ||
            request.AdapterRevisionId != Descriptor.RevisionId)
            return Task.FromResult(UnsupportedExtraction("gta.adapter-or-game.unsupported"));
        if (request.Artifacts.Length > Descriptor.ResourceLimits.MaximumArtifacts)
            return Task.FromResult(UnsupportedExtraction("gta.artifact-count.limit-exceeded"));

        var registrations = ImmutableArray.CreateBuilder<AdapterBoundCanonicalCatalogRegistration>();
        var unresolved = ImmutableArray.CreateBuilder<UnresolvedSourceAssertion>();
        var issues = ImmutableArray.CreateBuilder<KnowledgeBuildIssue>();
        foreach (var artifact in OrderArtifacts(request.Artifacts))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsAcceptedArtifact(artifact))
                continue;

            try
            {
                var output = CreateRegistration(request.SourceScope, artifact);
                registrations.Add(output.Registration);
                unresolved.AddRange(output.UnresolvedAssertions);
            }
            catch (InvalidDataException)
            {
                issues.Add(new("gta.source.malformed-or-ambiguous", artifact.Id.Value));
            }
        }

        if (registrations.Count == 0)
            return Task.FromResult(UnsupportedExtraction("gta.source.unsupported", issues.ToImmutable()));

        issues.Add(new("gta.coverage.partial", $"Only bounded {KnowledgeKind} coverage is established by this adapter."));
        var registrationValues = registrations.ToImmutable();
        var unresolvedValues = unresolved.ToImmutable();
        return Task.FromResult(new KnowledgeExtractionResult(
            registrationValues,
            [],
            unresolvedValues,
            KnowledgeCoverageState.Partial,
            issues.ToImmutable())
        {
            LocationCoverageReports = CreateLocationCoverageReports(
                request.SourceScope,
                registrationValues,
                unresolvedValues),
        });
    }

    private protected virtual ImmutableArray<LocationCoverageReport> CreateLocationCoverageReports(
        KnowledgeSourceScope sourceScope,
        ImmutableArray<AdapterBoundCanonicalCatalogRegistration> registrations,
        ImmutableArray<UnresolvedSourceAssertion> unresolvedAssertions) => [];

    private RegistrationOutput CreateRegistration(KnowledgeSourceScope scope, FrozenSourceArtifact artifact)
    {
        var parsed = Parse(artifact);
        if (parsed.Records.IsEmpty && parsed.UnresolvedAssertions.IsEmpty)
            throw new InvalidDataException("The source contains no supported or unresolved source-native assertions.");
        if (parsed.Records.Length > Descriptor.ResourceLimits.MaximumKnowledgeRecords)
            throw new InvalidDataException("The source exceeds the adapter record limit.");

        var source = CreateSource(scope, artifact.SourceCoordinate);
        var revisionId = CatalogSourceRevisionId.DeriveV2(source.Id, null, [artifact.Id], Descriptor.RevisionId);
        var revision = new CatalogSourceRevisionRecord(revisionId, source.Id, null, [artifact.Id]);
        var records = ImmutableArray.CreateBuilder<CanonicalKnowledgeRecord>(parsed.Records.Length);
        var evidence = ImmutableArray.CreateBuilder<CatalogFileEvidenceReceipt>();
        var bindings = ImmutableArray.CreateBuilder<EvidenceBinding>();
        var locationNativeTypes = ImmutableArray.CreateBuilder<SourceNativeLocationTypeAssertion>();
        var locationSemanticClassifications = ImmutableArray.CreateBuilder<LocationSemanticClassificationAssertion>();

        foreach (var parsedRecord in parsed.Records.OrderBy(value => value.ObjectType, StringComparer.Ordinal)
                     .ThenBy(value => value.NativeKey, StringComparer.Ordinal))
        {
            var nativeIdentity = CreateNativeIdentity(parsedRecord.ObjectType, parsedRecord.NativeKey);
            var nativeRecordId = NativeRecordIdentityId.DeriveV1(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                nativeIdentity);
            var recordId = KnowledgeRecordId.DeriveV1(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                scope.ExactGameVersion,
                scope.ExactModVersion,
                revisionId,
                KnowledgeKind,
                nativeRecordId);
            var record = new CanonicalKnowledgeRecord(
                recordId,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                scope.ExactGameVersion,
                scope.ExactModVersion,
                revisionId,
                KnowledgeKind,
                nativeRecordId,
                nativeIdentity);
            records.Add(record);

            foreach (var location in parsedRecord.EvidenceLocations
                         .OrderBy(value => value.FieldLocator, StringComparer.Ordinal))
            {
                AddIdentityEvidence(evidence, bindings, revisionId, artifact, record.Id, location);
            }

            AddRecordAssertions(
                parsedRecord,
                record,
                revisionId,
                artifact,
                locationNativeTypes,
                locationSemanticClassifications,
                evidence,
                bindings);

        }

        var unresolved = ImmutableArray.CreateBuilder<UnresolvedSourceAssertion>();
        foreach (var parsedUnresolved in parsed.UnresolvedAssertions
                     .OrderBy(value => value.NativeIdentity.ExactRepresentation, StringComparer.Ordinal))
        {
            var receipt = CreateReceipt(
                revisionId,
                artifact,
                parsedUnresolved.RecordLocator,
                parsedUnresolved.FieldLocator);
            var receiptId = EvidenceReceiptId.DeriveV1(receipt);
            evidence.Add(new(receiptId, receipt));
            var evidenceIds = ImmutableArray.Create(receiptId);
            var id = UnresolvedSourceAssertionId.DeriveV1(
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                revisionId,
                Descriptor.RevisionId,
                parsedUnresolved.NativeIdentity,
                parsedUnresolved.CandidateKind,
                parsedUnresolved.ReasonCode,
                evidenceIds);
            unresolved.Add(new UnresolvedSourceAssertion(
                id,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                revisionId,
                Descriptor.RevisionId,
                parsedUnresolved.NativeIdentity,
                parsedUnresolved.CandidateKind,
                parsedUnresolved.ReasonCode,
                evidenceIds));
        }

        var registration = new CanonicalCatalogRegistration(
            source,
            [new SourceArtifactRecord(artifact.Id, artifact.Digest)],
            revision,
            records.ToImmutable(),
            [],
            [],
            evidence.ToImmutable(),
            [],
            bindings.ToImmutable())
        {
            SourceNativeLocationTypeAssertions = locationNativeTypes.ToImmutable(),
            LocationSemanticClassificationAssertions = locationSemanticClassifications.ToImmutable(),
        };
        return new RegistrationOutput(
            new AdapterBoundCanonicalCatalogRegistration(
                registration,
                Descriptor,
                scope,
                [artifact.FormatBinding]),
            unresolved.ToImmutable());
    }

    private protected virtual void AddRecordAssertions(
        ParsedRecord parsedRecord,
        CanonicalKnowledgeRecord record,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        ImmutableArray<SourceNativeLocationTypeAssertion>.Builder locationNativeTypes,
        ImmutableArray<LocationSemanticClassificationAssertion>.Builder locationSemanticClassifications,
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings)
    {
    }

    private protected void AddExactClaimEvidence(
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        KnowledgeRecordId recordId,
        EvidenceClaimKind claimKind,
        string recordLocator,
        string fieldLocator,
        EvidenceClaimContentId claimContentId)
    {
        var receipt = CreateReceipt(revisionId, artifact, recordLocator, fieldLocator);
        var receiptId = EvidenceReceiptId.DeriveV1(receipt);
        if (!evidence.Any(value => value.Id == receiptId))
            evidence.Add(new(receiptId, receipt));
        bindings.Add(new EvidenceBinding(
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

    private void AddIdentityEvidence(
        ImmutableArray<CatalogFileEvidenceReceipt>.Builder evidence,
        ImmutableArray<EvidenceBinding>.Builder bindings,
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        KnowledgeRecordId recordId,
        ParsedEvidenceLocation location)
    {
        var receipt = CreateReceipt(revisionId, artifact, location.RecordLocator, location.FieldLocator);
        var receiptId = EvidenceReceiptId.DeriveV1(receipt);
        evidence.Add(new(receiptId, receipt));
        bindings.Add(new EvidenceBinding(
            EvidenceBindingId.DeriveV2(
                receiptId,
                EvidenceClaimKind.KnowledgeIdentity,
                recordId,
                revisionId,
                location.FieldLocator,
                null),
            receiptId,
            EvidenceClaimKind.KnowledgeIdentity,
            recordId,
            revisionId,
            location.FieldLocator,
            null));
    }

    private FileEvidenceReceipt CreateReceipt(
        CatalogSourceRevisionId revisionId,
        FrozenSourceArtifact artifact,
        string recordLocator,
        string fieldLocator) =>
        new(
            revisionId,
            artifact.Id,
            artifact.Digest,
            ParserId,
            ParserVersion,
            recordLocator,
            fieldLocator,
            null,
            null,
            null,
            artifact.ObservedAtUtc);

    private SourceNativeIdentifier CreateNativeIdentity(string objectType, string exactValue) =>
        SourceNativeIdentifier.FromExactUtf8(
            RecordNamespace,
            objectType,
            exactValue,
            RecordComparisonMethod,
            1);

    private CatalogSourceRecord CreateSource(KnowledgeSourceScope scope, SourceNativeIdentifier nativeIdentity)
    {
        var kind = scope.ScopeKind == KnowledgeSourceScopeKind.BaseGame
            ? KnowledgeSourceKind.LocalGameDistribution
            : KnowledgeSourceKind.LocalModArtifact;
        return new CatalogSourceRecord(CatalogSourceId.DeriveV1(kind, nativeIdentity), kind, nativeIdentity);
    }

    private bool IsAcceptedArtifact(FrozenSourceArtifact artifact)
    {
        if (!string.Equals(artifact.DeclaredFormat.FormatId, Format.FormatId, StringComparison.Ordinal) ||
            !string.Equals(artifact.DeclaredFormat.ExactFormatVersion, Format.ExactFormatVersion, StringComparison.Ordinal) ||
            !string.Equals(artifact.SourceCoordinate.Namespace, "rockstar.gta-v.enhanced.resource-coordinate", StringComparison.Ordinal) ||
            !string.Equals(artifact.SourceCoordinate.ObjectType, _sourceObjectType, StringComparison.Ordinal) ||
            !string.Equals(artifact.SourceCoordinate.ComparisonMethodId, SourceComparisonMethod, StringComparison.Ordinal) ||
            artifact.SourceCoordinate.ComparisonMethodVersion != 1)
            return false;

        try
        {
            ValidateResourceCoordinate(artifact.SourceCoordinate.ExactRepresentation, _requiredCoordinateSuffix);
            return artifact.SourceCoordinate.IdentityBytes.AsSpan().SequenceEqual(
                StrictUtf8.GetBytes(artifact.SourceCoordinate.ExactRepresentation));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private protected static string DecodeStrictUtf8(ReadOnlySpan<byte> bytes, long maximumBytes)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes)
            throw new InvalidDataException("The frozen source artifact has an invalid size.");
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The frozen source is not well-formed UTF-8.", exception);
        }
    }

    private protected static string CreateLocator(string resourceCoordinate, string fieldPath) =>
        $"rpf7-member:{resourceCoordinate}#{fieldPath}";

    private protected static string XmlPath(System.Xml.Linq.XElement element)
    {
        var components = element.AncestorsAndSelf().Reverse().Select(value =>
        {
            var index = value.Parent is null ? 1 : value.ElementsBeforeSelf(value.Name).Count() + 1;
            return $"/{value.Name.LocalName}[{index}]";
        });
        return string.Concat(components);
    }

    private static void ValidateResourceCoordinate(string value, string requiredSuffix)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A source-native resource coordinate is required.", nameof(value));
        _ = StrictUtf8.GetBytes(value);
        if (Path.IsPathRooted(value) || value.Contains('\\') || value.Contains(':') || value.Contains('#') ||
            value.Any(char.IsControl) || !value.EndsWith(requiredSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The resource coordinate is not supported by this adapter.", nameof(value));

        var archiveParts = value.Split("!/", StringSplitOptions.None);
        if (archiveParts.Length < 2 || archiveParts.Take(archiveParts.Length - 1)
                .Any(part => !part.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("An explicit RPF member boundary is required.", nameof(value));
        for (var partIndex = 0; partIndex < archiveParts.Length; partIndex++)
        {
            var segments = archiveParts[partIndex].Split('/');
            if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains('!')))
                throw new ArgumentException("The resource coordinate contains an invalid segment.", nameof(value));
            var isArchive = partIndex < archiveParts.Length - 1;
            if (!isArchive && segments.Take(segments.Length - 1)
                    .Any(segment => segment.EndsWith(".rpf", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Nested RPF members require explicit archive boundaries.", nameof(value));
        }
    }

    private static IOrderedEnumerable<FrozenSourceArtifact> OrderArtifacts(ImmutableArray<FrozenSourceArtifact> artifacts) =>
        artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ThenBy(value => value.SourceCoordinate.ExactRepresentation, StringComparer.Ordinal);

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
        var issues = priorIssues.IsDefault ? ImmutableArray<KnowledgeBuildIssue>.Empty : priorIssues;
        return new([], [], [], KnowledgeCoverageState.Unsupported,
            issues.Add(new KnowledgeBuildIssue(issueCode, "No canonical output was emitted.")));
    }

    private protected sealed record ParsedArtifact(
        ImmutableArray<ParsedRecord> Records,
        ImmutableArray<ParsedUnresolvedAssertion> UnresolvedAssertions);

    private protected sealed record ParsedRecord(
        string ObjectType,
        string NativeKey,
        ImmutableArray<ParsedEvidenceLocation> EvidenceLocations);

    private protected sealed record ParsedEvidenceLocation(string RecordLocator, string FieldLocator);

    private protected sealed record ParsedUnresolvedAssertion(
        SourceNativeIdentifier NativeIdentity,
        KnowledgeKind? CandidateKind,
        string ReasonCode,
        string RecordLocator,
        string FieldLocator);

    private sealed record RegistrationOutput(
        AdapterBoundCanonicalCatalogRegistration Registration,
        ImmutableArray<UnresolvedSourceAssertion> UnresolvedAssertions);
}
