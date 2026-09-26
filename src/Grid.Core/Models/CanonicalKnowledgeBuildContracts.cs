using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public enum KnowledgeCoverageState
{
    Complete = 0,
    Partial = 1,
    Unresolved = 2,
    Unsupported = 3,
}

public enum KnowledgeSourceScopeKind
{
    BaseGame = 0,
    ModExtension = 1,
}

public sealed record KnowledgeSourceScope
{
    public KnowledgeSourceScope(
        GameId gameId,
        SourceNativeVersion? exactGameVersion,
        KnowledgeSourceScopeKind scopeKind,
        SourceNativeIdentifier? exactModIdentity,
        SourceNativeVersion? exactModVersion)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        if (!Enum.IsDefined(scopeKind)) throw new ArgumentOutOfRangeException(nameof(scopeKind));
        if (scopeKind == KnowledgeSourceScopeKind.BaseGame &&
            (exactModIdentity is not null || exactModVersion is not null))
            throw new ArgumentException("A base-game source scope cannot carry mod identity or version.");
        if (scopeKind == KnowledgeSourceScopeKind.ModExtension &&
            (exactModIdentity is null || exactModVersion is null))
            throw new ArgumentException("A mod-extension source scope requires exact mod identity and version.");

        GameId = gameId;
        ExactGameVersion = exactGameVersion;
        ScopeKind = scopeKind;
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
    }

    public GameId GameId { get; }
    public SourceNativeVersion? ExactGameVersion { get; }
    public KnowledgeSourceScopeKind ScopeKind { get; }
    public SourceNativeIdentifier? ExactModIdentity { get; }
    public SourceNativeVersion? ExactModVersion { get; }

    public static KnowledgeSourceScope BaseGame(GameId gameId, SourceNativeVersion? exactGameVersion) =>
        new(gameId, exactGameVersion, KnowledgeSourceScopeKind.BaseGame, null, null);

    public static KnowledgeSourceScope ModExtension(
        GameId gameId,
        SourceNativeVersion? exactGameVersion,
        SourceNativeIdentifier exactModIdentity,
        SourceNativeVersion exactModVersion) =>
        new(gameId, exactGameVersion, KnowledgeSourceScopeKind.ModExtension, exactModIdentity, exactModVersion);
}

public sealed record KnowledgeFormatCoordinate
{
    public KnowledgeFormatCoordinate(string formatId, string exactFormatVersion)
    {
        FormatId = SupportedKnowledgeFormat.RequireStrictText(formatId, nameof(formatId));
        ExactFormatVersion = SupportedKnowledgeFormat.RequireStrictText(
            exactFormatVersion,
            nameof(exactFormatVersion));
    }

    public string FormatId { get; }
    public string ExactFormatVersion { get; }
}

public sealed record SourceArtifactFormatBinding
{
    public SourceArtifactFormatBinding(SourceArtifactId artifactId, KnowledgeFormatCoordinate format)
    {
        CanonicalKnowledgeContract.RequireIdentifier(artifactId.Value, nameof(artifactId));
        ArgumentNullException.ThrowIfNull(format);
        ArtifactId = artifactId;
        Format = format;
    }

    public SourceArtifactId ArtifactId { get; }
    public KnowledgeFormatCoordinate Format { get; }
}

public sealed record KnowledgeAdapterRevisionCoordinate
{
    public KnowledgeAdapterRevisionCoordinate(
        KnowledgeAdapterRevisionId id,
        KnowledgeAdapterId adapterId,
        string exactAdapterVersion,
        ContentDigest adapterArtifactDigest,
        int adapterContractVersion,
        string mappingRulesVersion,
        KnowledgeAdapterSemanticContractDigest? semanticContractDigest = null)
    {
        ExactAdapterVersion = SupportedKnowledgeFormat.RequireStrictText(
            exactAdapterVersion,
            nameof(exactAdapterVersion));
        if (adapterContractVersion <= 0) throw new ArgumentOutOfRangeException(nameof(adapterContractVersion));
        MappingRulesVersion = SupportedKnowledgeFormat.RequireStrictText(
            mappingRulesVersion,
            nameof(mappingRulesVersion));
        var derived = id.AlgorithmVersion switch
        {
            KnowledgeAdapterRevisionId.LegacyAlgorithmVersion when semanticContractDigest is null =>
                KnowledgeAdapterRevisionId.DeriveV1(
                    adapterId,
                    ExactAdapterVersion,
                    adapterArtifactDigest,
                    adapterContractVersion,
                    MappingRulesVersion),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion when semanticContractDigest is not null =>
                KnowledgeAdapterRevisionId.DeriveV2(
                    adapterId,
                    ExactAdapterVersion,
                    adapterContractVersion,
                    MappingRulesVersion,
                    semanticContractDigest.Value),
            _ => throw new ArgumentException(
                "Adapter revision identity algorithm and semantic-contract coordinate do not agree.",
                nameof(semanticContractDigest)),
        };
        if (derived != id)
            throw new ArgumentException("Adapter revision identity does not match its exact coordinates.", nameof(id));

        Id = id;
        AdapterId = adapterId;
        AdapterArtifactDigest = adapterArtifactDigest;
        AdapterContractVersion = adapterContractVersion;
        SemanticContractDigest = semanticContractDigest;
    }

    public KnowledgeAdapterRevisionId Id { get; }
    public KnowledgeAdapterId AdapterId { get; }
    public string ExactAdapterVersion { get; }
    public ContentDigest AdapterArtifactDigest { get; }
    public int AdapterContractVersion { get; }
    public string MappingRulesVersion { get; }
    public KnowledgeAdapterSemanticContractDigest? SemanticContractDigest { get; }
}

public sealed record KnowledgeAdapterResourceLimits
{
    public KnowledgeAdapterResourceLimits(
        long maximumArtifactBytes,
        int maximumArtifacts,
        int maximumKnowledgeRecords,
        int maximumRelationships)
    {
        if (maximumArtifactBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArtifactBytes));
        if (maximumArtifacts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumArtifacts));
        if (maximumKnowledgeRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maximumKnowledgeRecords));
        if (maximumRelationships <= 0) throw new ArgumentOutOfRangeException(nameof(maximumRelationships));
        MaximumArtifactBytes = maximumArtifactBytes;
        MaximumArtifacts = maximumArtifacts;
        MaximumKnowledgeRecords = maximumKnowledgeRecords;
        MaximumRelationships = maximumRelationships;
    }

    public long MaximumArtifactBytes { get; }
    public int MaximumArtifacts { get; }
    public int MaximumKnowledgeRecords { get; }
    public int MaximumRelationships { get; }
}

public sealed record SupportedKnowledgeFormat
{
    public SupportedKnowledgeFormat(
        string formatId,
        string exactFormatVersion,
        ImmutableArray<string> containerKinds,
        ImmutableArray<string> resourceObjectTypes,
        ImmutableArray<KnowledgeKind> supportedKnowledgeKinds,
        bool supportsTerminology,
        bool supportsRelationships,
        bool supportsHierarchy)
    {
        FormatId = RequireStrictText(formatId, nameof(formatId));
        ExactFormatVersion = RequireStrictText(exactFormatVersion, nameof(exactFormatVersion));
        ContainerKinds = NormalizeTextSet(containerKinds, nameof(containerKinds));
        ResourceObjectTypes = NormalizeTextSet(resourceObjectTypes, nameof(resourceObjectTypes));
        if (supportedKnowledgeKinds.IsDefault)
            throw new ArgumentException("Supported knowledge kinds must be initialized.", nameof(supportedKnowledgeKinds));
        if (supportedKnowledgeKinds.Any(value => !Enum.IsDefined(value)) ||
            supportedKnowledgeKinds.Distinct().Count() != supportedKnowledgeKinds.Length)
            throw new ArgumentException("Supported knowledge kinds must be valid and distinct.", nameof(supportedKnowledgeKinds));
        SupportedKnowledgeKinds = supportedKnowledgeKinds.Order().ToImmutableArray();
        SupportsTerminology = supportsTerminology;
        SupportsRelationships = supportsRelationships;
        SupportsHierarchy = supportsHierarchy;
    }

    public string FormatId { get; }
    public string ExactFormatVersion { get; }
    public ImmutableArray<string> ContainerKinds { get; }
    public ImmutableArray<string> ResourceObjectTypes { get; }
    public ImmutableArray<KnowledgeKind> SupportedKnowledgeKinds { get; }
    public bool SupportsTerminology { get; }
    public bool SupportsRelationships { get; }
    public bool SupportsHierarchy { get; }

    internal static string RequireStrictText(string? value, string parameterName)
    {
        var result = CanonicalKnowledgeContract.RequireText(value, parameterName);
        CanonicalUtf8.Validate(result);
        return result;
    }

    internal static ImmutableArray<string> NormalizeTextSet(ImmutableArray<string> values, string parameterName)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", parameterName);
        var ordered = values.Select(value => RequireStrictText(value, parameterName))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection values must be distinct.", parameterName);
        return ordered;
    }
}

public sealed record GameKnowledgeAdapterDescriptor
{
    public GameKnowledgeAdapterDescriptor(
        KnowledgeAdapterId adapterId,
        string exactAdapterVersion,
        ContentDigest adapterArtifactDigest,
        int adapterContractVersion,
        string mappingRulesVersion,
        ImmutableArray<GameId> supportedGameIds,
        ImmutableArray<SupportedKnowledgeFormat> supportedFormats,
        KnowledgeAdapterResourceLimits resourceLimits,
        int identityAlgorithmVersion = 0)
    {
        CanonicalKnowledgeContract.RequireIdentifier(adapterId.Value, nameof(adapterId));
        ExactAdapterVersion = SupportedKnowledgeFormat.RequireStrictText(exactAdapterVersion, nameof(exactAdapterVersion));
        if (adapterContractVersion <= 0) throw new ArgumentOutOfRangeException(nameof(adapterContractVersion));
        MappingRulesVersion = SupportedKnowledgeFormat.RequireStrictText(mappingRulesVersion, nameof(mappingRulesVersion));
        if (supportedGameIds.IsDefaultOrEmpty)
            throw new ArgumentException("At least one supported GameId is required.", nameof(supportedGameIds));
        if (supportedFormats.IsDefaultOrEmpty)
            throw new ArgumentException("At least one supported format is required.", nameof(supportedFormats));
        ArgumentNullException.ThrowIfNull(resourceLimits);

        var games = supportedGameIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (games.Any(value => string.IsNullOrWhiteSpace(value.Value)) || games.Distinct().Count() != games.Length)
            throw new ArgumentException("Supported GameIds must be valid and distinct.", nameof(supportedGameIds));
        var formats = supportedFormats
            .OrderBy(value => value.FormatId, StringComparer.Ordinal)
            .ThenBy(value => value.ExactFormatVersion, StringComparer.Ordinal)
            .ToImmutableArray();
        if (formats.Any(value => value is null) ||
            formats.Select(value => (value.FormatId, value.ExactFormatVersion)).Distinct().Count() != formats.Length)
            throw new ArgumentException("Supported format identity/version coordinates must be distinct.", nameof(supportedFormats));

        AdapterId = adapterId;
        AdapterArtifactDigest = adapterArtifactDigest;
        AdapterContractVersion = adapterContractVersion;
        SupportedGameIds = games;
        SupportedFormats = formats;
        ResourceLimits = resourceLimits;
        var semanticContractDigest = KnowledgeAdapterSemanticContractDigest.DeriveV1(
            games,
            formats,
            resourceLimits);
        var effectiveIdentityAlgorithmVersion =
            identityAlgorithmVersion == 0
                ? KnowledgeAdapterRevisionId.LegacyAlgorithmVersion
                : identityAlgorithmVersion;
        var revisionId = effectiveIdentityAlgorithmVersion switch
        {
            KnowledgeAdapterRevisionId.LegacyAlgorithmVersion => KnowledgeAdapterRevisionId.DeriveV1(
                adapterId,
                ExactAdapterVersion,
                adapterArtifactDigest,
                adapterContractVersion,
                MappingRulesVersion),
            KnowledgeAdapterRevisionId.CurrentAlgorithmVersion => KnowledgeAdapterRevisionId.DeriveV2(
                adapterId,
                ExactAdapterVersion,
                adapterContractVersion,
                MappingRulesVersion,
                semanticContractDigest),
            _ => throw new ArgumentOutOfRangeException(nameof(identityAlgorithmVersion)),
        };
        Revision = new KnowledgeAdapterRevisionCoordinate(
            revisionId,
            adapterId,
            ExactAdapterVersion,
            adapterArtifactDigest,
            adapterContractVersion,
            MappingRulesVersion,
            effectiveIdentityAlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion
                ? semanticContractDigest
                : null);
    }

    public KnowledgeAdapterId AdapterId { get; }
    public string ExactAdapterVersion { get; }
    public ContentDigest AdapterArtifactDigest { get; }
    public int AdapterContractVersion { get; }
    public string MappingRulesVersion { get; }
    public ImmutableArray<GameId> SupportedGameIds { get; }
    public ImmutableArray<SupportedKnowledgeFormat> SupportedFormats { get; }
    public KnowledgeAdapterResourceLimits ResourceLimits { get; }
    public KnowledgeAdapterRevisionCoordinate Revision { get; }
    public KnowledgeAdapterRevisionId RevisionId => Revision.Id;
    public int IdentityAlgorithmVersion => RevisionId.AlgorithmVersion;

    public bool Supports(GameId gameId, KnowledgeFormatCoordinate format) =>
        SupportedGameIds.Contains(gameId) && SupportedFormats.Any(value =>
            string.Equals(value.FormatId, format.FormatId, StringComparison.Ordinal) &&
            string.Equals(value.ExactFormatVersion, format.ExactFormatVersion, StringComparison.Ordinal));
}

public sealed record FrozenSourceArtifact
{
    public FrozenSourceArtifact(
        SourceArtifactId id,
        ContentDigest digest,
        SourceNativeIdentifier sourceCoordinate,
        KnowledgeFormatCoordinate declaredFormat,
        ImmutableArray<byte> exactBytes)
        : this(
            id,
            digest,
            sourceCoordinate,
            declaredFormat,
            exactBytes,
            DateTimeOffset.UnixEpoch)
    {
    }

    [JsonConstructor]
    public FrozenSourceArtifact(
        SourceArtifactId id,
        ContentDigest digest,
        SourceNativeIdentifier sourceCoordinate,
        KnowledgeFormatCoordinate declaredFormat,
        ImmutableArray<byte> exactBytes,
        DateTimeOffset observedAtUtc)
    {
        if (SourceArtifactId.DeriveV1(digest) != id)
            throw new ArgumentException("Frozen artifact identity does not match its exact bytes digest.", nameof(id));
        ArgumentNullException.ThrowIfNull(sourceCoordinate);
        ArgumentNullException.ThrowIfNull(declaredFormat);
        if (exactBytes.IsDefaultOrEmpty)
            throw new ArgumentException("Frozen artifact bytes must be initialized and non-empty.", nameof(exactBytes));
        if (ContentDigest.ComputeSha256(exactBytes.AsSpan()) != digest)
            throw new ArgumentException("Frozen artifact digest does not match its exact bytes.", nameof(exactBytes));
        if (observedAtUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("Frozen artifact observation time must use UTC offset zero.", nameof(observedAtUtc));
        Id = id;
        Digest = digest;
        SourceCoordinate = sourceCoordinate;
        DeclaredFormat = declaredFormat;
        ExactBytes = exactBytes.ToImmutableArray();
        ObservedAtUtc = observedAtUtc;
    }

    public SourceArtifactId Id { get; }
    public ContentDigest Digest { get; }
    public SourceNativeIdentifier SourceCoordinate { get; }
    public KnowledgeFormatCoordinate DeclaredFormat { get; }
    public SourceArtifactFormatBinding FormatBinding => new(Id, DeclaredFormat);
    public ImmutableArray<byte> ExactBytes { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

public sealed record PreproductionSourceDiscoveryRequest
{
    public PreproductionSourceDiscoveryRequest(
        GameId gameId,
        SourceNativeVersion? exactGameVersion,
        SourceNativeIdentifier? exactModIdentity,
        SourceNativeVersion? exactModVersion,
        ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        if (artifacts.IsDefaultOrEmpty)
            throw new ArgumentException("Discovery requires at least one frozen artifact.", nameof(artifacts));
        if (artifacts.Any(value => value is null) || artifacts.Select(value => value.Id).Distinct().Count() != artifacts.Length)
            throw new ArgumentException("Frozen discovery artifacts must be non-null and distinct.", nameof(artifacts));
        if ((exactModIdentity is null) != (exactModVersion is null))
            throw new ArgumentException("Mod source discovery requires both exact mod identity and exact mod version.");
        GameId = gameId;
        ExactGameVersion = exactGameVersion;
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
        Artifacts = artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    public GameId GameId { get; }
    public SourceNativeVersion? ExactGameVersion { get; }
    public SourceNativeIdentifier? ExactModIdentity { get; }
    public SourceNativeVersion? ExactModVersion { get; }
    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }
    public KnowledgeSourceScope SourceScope => ExactModIdentity is null
        ? KnowledgeSourceScope.BaseGame(GameId, ExactGameVersion)
        : KnowledgeSourceScope.ModExtension(GameId, ExactGameVersion, ExactModIdentity, ExactModVersion!);
}

public sealed record DiscoveredKnowledgeSource
{
    public DiscoveredKnowledgeSource(
        CatalogSourceRecord source,
        SourceNativeVersion? nativeRevision,
        ImmutableArray<SourceArtifactFormatBinding> artifactFormats)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (artifactFormats.IsDefaultOrEmpty || artifactFormats.Any(value => value is null) ||
            artifactFormats.Select(value => value.ArtifactId).Distinct().Count() != artifactFormats.Length)
            throw new ArgumentException("Discovered source artifact formats must be non-empty and distinct.", nameof(artifactFormats));
        Source = source;
        NativeRevision = nativeRevision;
        ArtifactFormats = artifactFormats.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    public CatalogSourceRecord Source { get; }
    public SourceNativeVersion? NativeRevision { get; }
    public ImmutableArray<SourceArtifactFormatBinding> ArtifactFormats { get; }
    public ImmutableArray<SourceArtifactId> ArtifactIds =>
        ArtifactFormats.Select(value => value.ArtifactId).ToImmutableArray();
}

public sealed record UnsupportedKnowledgeArtifact(
    SourceArtifactId ArtifactId,
    string ReasonCode);

public sealed record KnowledgeBuildIssue(string Code, string Detail);

public sealed record SourceDiscoveryResult
{
    public SourceDiscoveryResult(
        ImmutableArray<DiscoveredKnowledgeSource> sourceCandidates,
        ImmutableArray<UnsupportedKnowledgeArtifact> unsupportedArtifacts,
        KnowledgeCoverageState coverageState,
        ImmutableArray<KnowledgeBuildIssue> issues)
    {
        if (sourceCandidates.IsDefault || unsupportedArtifacts.IsDefault || issues.IsDefault)
            throw new ArgumentException("Discovery result collections must be initialized.");
        if (!Enum.IsDefined(coverageState)) throw new ArgumentOutOfRangeException(nameof(coverageState));
        if (sourceCandidates.Any(value => value is null) ||
            unsupportedArtifacts.Any(value => value is null) ||
            issues.Any(value => value is null))
            throw new ArgumentException("Discovery result values must be non-null.");
        if (coverageState == KnowledgeCoverageState.Unsupported && !sourceCandidates.IsEmpty)
            throw new ArgumentException("Unsupported discovery cannot expose source candidates.", nameof(sourceCandidates));
        SourceCandidates = sourceCandidates;
        UnsupportedArtifacts = unsupportedArtifacts;
        CoverageState = coverageState;
        Issues = issues;
    }

    public ImmutableArray<DiscoveredKnowledgeSource> SourceCandidates { get; }
    public ImmutableArray<UnsupportedKnowledgeArtifact> UnsupportedArtifacts { get; }
    public KnowledgeCoverageState CoverageState { get; }
    public ImmutableArray<KnowledgeBuildIssue> Issues { get; }
}

public sealed record PreproductionKnowledgeExtractionRequest
{
    public PreproductionKnowledgeExtractionRequest(
        GameId gameId,
        SourceNativeVersion? exactGameVersion,
        SourceNativeIdentifier? exactModIdentity,
        SourceNativeVersion? exactModVersion,
        GameKnowledgeAdapterDescriptor adapterDescriptor,
        ImmutableArray<FrozenSourceArtifact> artifacts)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        ArgumentNullException.ThrowIfNull(adapterDescriptor);
        if (artifacts.IsDefaultOrEmpty)
            throw new ArgumentException("Extraction requires at least one frozen artifact.", nameof(artifacts));
        if (artifacts.Any(value => value is null) || artifacts.Select(value => value.Id).Distinct().Count() != artifacts.Length)
            throw new ArgumentException("Frozen extraction artifacts must be non-null and distinct.", nameof(artifacts));
        if ((exactModIdentity is null) != (exactModVersion is null))
            throw new ArgumentException("Mod extraction requires both exact mod identity and exact mod version.");
        if (!adapterDescriptor.SupportedGameIds.Contains(gameId))
            throw new ArgumentException("The adapter revision does not declare the requested GameId.", nameof(gameId));
        if (artifacts.Any(value => !adapterDescriptor.Supports(gameId, value.DeclaredFormat)))
            throw new ArgumentException(
                "Every extraction artifact must declare an exact format ID/version tuple supported by this adapter revision.",
                nameof(artifacts));
        GameId = gameId;
        ExactGameVersion = exactGameVersion;
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
        AdapterDescriptor = adapterDescriptor;
        Artifacts = artifacts.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    public GameId GameId { get; }
    public SourceNativeVersion? ExactGameVersion { get; }
    public SourceNativeIdentifier? ExactModIdentity { get; }
    public SourceNativeVersion? ExactModVersion { get; }
    public GameKnowledgeAdapterDescriptor AdapterDescriptor { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId => AdapterDescriptor.RevisionId;
    public ImmutableArray<FrozenSourceArtifact> Artifacts { get; }
    public KnowledgeSourceScope SourceScope => ExactModIdentity is null
        ? KnowledgeSourceScope.BaseGame(GameId, ExactGameVersion)
        : KnowledgeSourceScope.ModExtension(GameId, ExactGameVersion, ExactModIdentity, ExactModVersion!);
}

public sealed record AdapterBoundCanonicalCatalogRegistration
{
    public AdapterBoundCanonicalCatalogRegistration(
        CanonicalCatalogRegistration registration,
        GameKnowledgeAdapterDescriptor adapterDescriptor,
        KnowledgeSourceScope sourceScope,
        ImmutableArray<SourceArtifactFormatBinding> artifactFormats)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(adapterDescriptor);
        ArgumentNullException.ThrowIfNull(sourceScope);
        if (artifactFormats.IsDefaultOrEmpty || artifactFormats.Any(value => value is null))
            throw new ArgumentException("Registration artifact formats must be initialized and non-empty.", nameof(artifactFormats));
        var orderedFormats = artifactFormats.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (orderedFormats.Select(value => value.ArtifactId).Distinct().Count() != orderedFormats.Length ||
            !orderedFormats.Select(value => value.ArtifactId).SequenceEqual(
                registration.SourceRevision.ArtifactIds.OrderBy(value => value.Value, StringComparer.Ordinal)))
            throw new ArgumentException("Registration artifact formats must bind every source-revision artifact exactly once.", nameof(artifactFormats));
        if (orderedFormats.Any(value => !adapterDescriptor.Supports(sourceScope.GameId, value.Format)))
            throw new ArgumentException("A registration artifact format is not declared by the exact adapter revision.", nameof(artifactFormats));
        if (CatalogSourceRevisionId.DeriveV2(
                registration.Source.Id,
                registration.SourceRevision.NativeRevision,
                registration.SourceRevision.ArtifactIds,
                adapterDescriptor.RevisionId) != registration.SourceRevision.Id)
            throw new ArgumentException("Registration source revision is not bound to the supplied adapter revision.", nameof(registration));
        if (registration.KnowledgeRecords.Any(value =>
                value.GameId != sourceScope.GameId ||
                value.GameVersion != sourceScope.ExactGameVersion ||
                value.ModVersion != sourceScope.ExactModVersion))
            throw new ArgumentException("Registration knowledge records do not match their exact source scope.", nameof(registration));
        Registration = registration;
        AdapterDescriptor = adapterDescriptor;
        SourceScope = sourceScope;
        ArtifactFormats = orderedFormats;
    }

    public CanonicalCatalogRegistration Registration { get; }
    public GameKnowledgeAdapterDescriptor AdapterDescriptor { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId => AdapterDescriptor.RevisionId;
    public KnowledgeSourceScope SourceScope { get; }
    public ImmutableArray<SourceArtifactFormatBinding> ArtifactFormats { get; }
}

public sealed record CanonicalCorrelationEnvelope
{
    public CanonicalCorrelationEnvelope(
        CorrelationRecordId id,
        CorrelationRecord record,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (supportingEvidenceReceiptIds.IsDefault)
            throw new ArgumentException("Supporting evidence identities must be initialized.", nameof(supportingEvidenceReceiptIds));
        var ordered = supportingEvidenceReceiptIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Distinct().Count() != ordered.Length)
            throw new ArgumentException("Supporting evidence identities must be distinct.", nameof(supportingEvidenceReceiptIds));
        if (CorrelationRecordId.DeriveV1(record, ordered) != id)
            throw new ArgumentException("Correlation identity does not match its deterministic content.", nameof(id));
        Id = id;
        Record = record;
        SupportingEvidenceReceiptIds = ordered;
    }

    public CorrelationRecordId Id { get; }
    public CorrelationRecord Record { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }
}

public sealed record UnresolvedSourceAssertion
{
    public UnresolvedSourceAssertion(
        UnresolvedSourceAssertionId id,
        GameId gameId,
        CatalogSourceRevisionId sourceRevisionId,
        KnowledgeAdapterRevisionId adapterRevisionId,
        SourceNativeIdentifier nativeIdentity,
        KnowledgeKind? candidateKind,
        string reasonCode,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        reasonCode = SupportedKnowledgeFormat.RequireStrictText(reasonCode, nameof(reasonCode));
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("Supporting evidence identities must be non-empty.", nameof(supportingEvidenceReceiptIds));
        var ordered = supportingEvidenceReceiptIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Distinct().Count() != ordered.Length)
            throw new ArgumentException("Supporting evidence identities must be distinct.", nameof(supportingEvidenceReceiptIds));
        if (UnresolvedSourceAssertionId.DeriveV1(
                gameId,
                sourceRevisionId,
                adapterRevisionId,
                nativeIdentity,
                candidateKind,
                reasonCode,
                ordered) != id)
            throw new ArgumentException("Unresolved assertion identity does not match its deterministic content.", nameof(id));
        Id = id;
        GameId = gameId;
        SourceRevisionId = sourceRevisionId;
        AdapterRevisionId = adapterRevisionId;
        NativeIdentity = nativeIdentity;
        CandidateKind = candidateKind;
        ReasonCode = reasonCode;
        SupportingEvidenceReceiptIds = ordered;
    }

    public UnresolvedSourceAssertionId Id { get; }
    public GameId GameId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId { get; }
    public SourceNativeIdentifier NativeIdentity { get; }
    public KnowledgeKind? CandidateKind { get; }
    public string ReasonCode { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }
}

public sealed record KnowledgeExtractionResult
{
    public KnowledgeExtractionResult(
        ImmutableArray<AdapterBoundCanonicalCatalogRegistration> canonicalRegistrations,
        ImmutableArray<CanonicalCorrelationEnvelope> correlationCandidates,
        ImmutableArray<UnresolvedSourceAssertion> unresolvedSourceAssertions,
        KnowledgeCoverageState coverageState,
        ImmutableArray<KnowledgeBuildIssue> issues)
    {
        if (canonicalRegistrations.IsDefault || correlationCandidates.IsDefault ||
            unresolvedSourceAssertions.IsDefault || issues.IsDefault)
            throw new ArgumentException("Extraction result collections must be initialized.");
        if (!Enum.IsDefined(coverageState)) throw new ArgumentOutOfRangeException(nameof(coverageState));
        if (canonicalRegistrations.Any(value => value is null) ||
            correlationCandidates.Any(value => value is null) ||
            unresolvedSourceAssertions.Any(value => value is null) ||
            issues.Any(value => value is null))
            throw new ArgumentException("Extraction result values must be non-null.");
        if (coverageState == KnowledgeCoverageState.Unsupported &&
            (!canonicalRegistrations.IsEmpty || !correlationCandidates.IsEmpty || !unresolvedSourceAssertions.IsEmpty))
            throw new ArgumentException(
                "Unsupported extraction cannot emit canonical registrations, correlations, or unresolved assertions.");
        CanonicalRegistrations = canonicalRegistrations;
        CorrelationCandidates = correlationCandidates;
        UnresolvedSourceAssertions = unresolvedSourceAssertions;
        CoverageState = coverageState;
        Issues = issues;
    }

    public ImmutableArray<AdapterBoundCanonicalCatalogRegistration> CanonicalRegistrations { get; }
    public ImmutableArray<CanonicalCorrelationEnvelope> CorrelationCandidates { get; }
    public ImmutableArray<UnresolvedSourceAssertion> UnresolvedSourceAssertions { get; }
    public KnowledgeCoverageState CoverageState { get; }
    public ImmutableArray<KnowledgeBuildIssue> Issues { get; }
    public ImmutableArray<LocationCoverageReport> LocationCoverageReports { get; init; } = [];
}
