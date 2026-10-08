using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum CatalogPackageKind
{
    BaseGameCatalog = 0,
    ModCatalogExtension = 1,
}

public enum CatalogValidationStatus
{
    Candidate = 0,
    Passed = 1,
    Rejected = 2,
}

public sealed record CatalogGameScope
{
    public CatalogGameScope(
        GameId gameId,
        SourceNativeVersion? exactGameVersion,
        ImmutableArray<SourceArtifactId> artifactFingerprints)
    {
        CanonicalKnowledgeContract.RequireIdentifier(gameId.Value, nameof(gameId));
        if (artifactFingerprints.IsDefault)
            throw new ArgumentException("Game artifact fingerprints must be initialized.", nameof(artifactFingerprints));
        if (exactGameVersion is null && artifactFingerprints.IsEmpty)
            throw new ArgumentException("A game scope requires an exact version or artifact fingerprint.");
        GameId = gameId;
        ExactGameVersion = exactGameVersion;
        ArtifactFingerprints = NormalizeIds(artifactFingerprints, value => value.Value, nameof(artifactFingerprints));
    }

    public GameId GameId { get; }
    public SourceNativeVersion? ExactGameVersion { get; }
    public ImmutableArray<SourceArtifactId> ArtifactFingerprints { get; }

    internal static ImmutableArray<T> NormalizeIds<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string parameterName)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", parameterName);
        if (values.Any(value => string.IsNullOrWhiteSpace(identity(value))))
            throw new ArgumentException("Collection identities must be non-empty.", parameterName);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection identities must be distinct.", parameterName);
        return ordered;
    }
}

public sealed record CatalogModScope
{
    public CatalogModScope(
        SourceNativeIdentifier exactModIdentity,
        SourceNativeVersion exactModVersion,
        ImmutableArray<SourceArtifactId> artifactFingerprints)
    {
        ArgumentNullException.ThrowIfNull(exactModIdentity);
        ArgumentNullException.ThrowIfNull(exactModVersion);
        if (artifactFingerprints.IsDefault)
            throw new ArgumentException("Mod artifact fingerprints must be initialized.", nameof(artifactFingerprints));
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
        ArtifactFingerprints = CatalogGameScope.NormalizeIds(
            artifactFingerprints,
            value => value.Value,
            nameof(artifactFingerprints));
    }

    public SourceNativeIdentifier ExactModIdentity { get; }
    public SourceNativeVersion ExactModVersion { get; }
    public ImmutableArray<SourceArtifactId> ArtifactFingerprints { get; }
}

public sealed record AdapterBoundCatalogSourceRevisionRecord
{
    public AdapterBoundCatalogSourceRevisionRecord(
        CatalogSourceRevisionRecord revision,
        KnowledgeAdapterRevisionId adapterRevisionId,
        KnowledgeSourceScope sourceScope,
        ImmutableArray<SourceArtifactFormatBinding> artifactFormats)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(sourceScope);
        if (artifactFormats.IsDefaultOrEmpty || artifactFormats.Any(value => value is null))
            throw new ArgumentException("Source-revision artifact formats must be initialized and non-empty.", nameof(artifactFormats));
        var orderedFormats = artifactFormats.OrderBy(value => value.ArtifactId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (orderedFormats.Select(value => value.ArtifactId).Distinct().Count() != orderedFormats.Length ||
            !orderedFormats.Select(value => value.ArtifactId).SequenceEqual(
                revision.ArtifactIds.OrderBy(value => value.Value, StringComparer.Ordinal)))
            throw new ArgumentException("Source-revision artifact formats must bind every artifact exactly once.", nameof(artifactFormats));
        CanonicalKnowledgeContract.RequireIdentifier(adapterRevisionId.Value, nameof(adapterRevisionId));
        if (CatalogSourceRevisionId.DeriveV2(
                revision.SourceId,
                revision.NativeRevision,
                revision.ArtifactIds,
                adapterRevisionId) != revision.Id)
            throw new ArgumentException("Source revision is not bound to the supplied adapter revision.", nameof(revision));
        Revision = revision;
        AdapterRevisionId = adapterRevisionId;
        SourceScope = sourceScope;
        ArtifactFormats = orderedFormats;
    }

    public CatalogSourceRevisionRecord Revision { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId { get; }
    public KnowledgeSourceScope SourceScope { get; }
    public ImmutableArray<SourceArtifactFormatBinding> ArtifactFormats { get; }
}

public sealed record CatalogBuildProvenance
{
    public const int LegacySchemaVersion = 1;
    public const int CurrentSchemaVersion = 2;
    public const int DevelopmentSchemaVersion = 3;
    public const int AdapterBuildReceiptSchemaVersion = 4;

    public CatalogBuildProvenance(
        string buildSystemId,
        string exactBuildSystemVersion,
        string sourceCommit)
        : this(
            LegacySchemaVersion,
            buildSystemId,
            exactBuildSystemVersion,
            sourceCommit,
            [])
    {
    }

    public CatalogBuildProvenance(
        int provenanceSchemaVersion,
        string buildSystemId,
        string exactBuildSystemVersion,
        string sourceCommit,
        ImmutableArray<CatalogCommittedBuildInput> committedBuildInputs)
        : this(
            provenanceSchemaVersion,
            buildSystemId,
            exactBuildSystemVersion,
            sourceCommit,
            committedBuildInputs,
            [],
            [])
    {
    }

    [System.Text.Json.Serialization.JsonConstructor]
    public CatalogBuildProvenance(
        int provenanceSchemaVersion,
        string buildSystemId,
        string exactBuildSystemVersion,
        string sourceCommit,
        ImmutableArray<CatalogCommittedBuildInput> committedBuildInputs,
        ImmutableArray<CatalogDevelopmentBuildInput> developmentBuildInputs,
        ImmutableArray<CatalogAdapterBuildProvenanceReceipt> adapterBuildReceipts = default)
    {
        if (provenanceSchemaVersion is not (
                LegacySchemaVersion or CurrentSchemaVersion or DevelopmentSchemaVersion or AdapterBuildReceiptSchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(provenanceSchemaVersion));
        BuildSystemId = SupportedKnowledgeFormat.RequireStrictText(buildSystemId, nameof(buildSystemId));
        ExactBuildSystemVersion = SupportedKnowledgeFormat.RequireStrictText(
            exactBuildSystemVersion,
            nameof(exactBuildSystemVersion));
        SourceCommit = SupportedKnowledgeFormat.RequireStrictText(sourceCommit, nameof(sourceCommit));
        if (committedBuildInputs.IsDefault)
            throw new ArgumentException("Committed build inputs must be initialized.", nameof(committedBuildInputs));
        if (developmentBuildInputs.IsDefault)
        {
            if (provenanceSchemaVersion == DevelopmentSchemaVersion)
                throw new ArgumentException("Development build inputs must be initialized.", nameof(developmentBuildInputs));
            // Historical schema-v1/v2 JSON has no developmentBuildInputs property.
            developmentBuildInputs = [];
        }
        if (adapterBuildReceipts.IsDefault)
        {
            if (provenanceSchemaVersion == AdapterBuildReceiptSchemaVersion)
                throw new ArgumentException("Adapter build receipts must be initialized.", nameof(adapterBuildReceipts));
            // Historical schema-v1/v2/v3 JSON has no adapterBuildReceipts property.
            adapterBuildReceipts = [];
        }
        if (provenanceSchemaVersion == LegacySchemaVersion && !committedBuildInputs.IsEmpty)
            throw new ArgumentException("Legacy build provenance cannot carry committed input closure.", nameof(committedBuildInputs));
        if (provenanceSchemaVersion != DevelopmentSchemaVersion && !developmentBuildInputs.IsEmpty)
            throw new ArgumentException("Only development build provenance may carry worktree input closure.", nameof(developmentBuildInputs));
        if (provenanceSchemaVersion is CurrentSchemaVersion or AdapterBuildReceiptSchemaVersion)
        {
            if ((SourceCommit.Length is not (40 or 64)) ||
                SourceCommit.Any(value => !Uri.IsHexDigit(value) || char.IsUpper(value)))
                throw new ArgumentException("Committed build provenance requires a full lowercase Git commit identity.", nameof(sourceCommit));
            if (committedBuildInputs.IsEmpty || committedBuildInputs.Any(value => value is null))
                throw new ArgumentException("Committed build provenance requires non-empty input closure.", nameof(committedBuildInputs));
        }
        if (provenanceSchemaVersion == DevelopmentSchemaVersion)
        {
            if ((SourceCommit.Length is not (40 or 64)) ||
                SourceCommit.Any(value => !Uri.IsHexDigit(value) || char.IsUpper(value)))
                throw new ArgumentException("Development build provenance requires the full lowercase HEAD commit identity.", nameof(sourceCommit));
            if (!committedBuildInputs.IsEmpty)
                throw new ArgumentException("Development provenance cannot masquerade as committed input closure.", nameof(committedBuildInputs));
            if (developmentBuildInputs.IsEmpty || developmentBuildInputs.Any(value => value is null))
                throw new ArgumentException("Development build provenance requires non-empty exact worktree input closure.", nameof(developmentBuildInputs));
            if (developmentBuildInputs.All(value => value.State == CatalogDevelopmentBuildInputState.HeadTrackedClean))
                throw new ArgumentException("Development build provenance requires at least one dirty or untracked build input.", nameof(developmentBuildInputs));
        }
        if (provenanceSchemaVersion == AdapterBuildReceiptSchemaVersion)
        {
            if (adapterBuildReceipts.IsEmpty || adapterBuildReceipts.Any(value => value is null))
                throw new ArgumentException(
                    "Reviewed adapter build provenance requires non-empty artifact receipts.",
                    nameof(adapterBuildReceipts));
        }
        else if (!adapterBuildReceipts.IsEmpty)
        {
            throw new ArgumentException(
                "Historical build provenance schemas cannot carry adapter build receipts.",
                nameof(adapterBuildReceipts));
        }
        var ordered = committedBuildInputs
            .OrderBy(value => value.RepositoryRelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        if (ordered.Select(value => value.RepositoryRelativePath).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Committed build input paths must be distinct.", nameof(committedBuildInputs));
        var orderedDevelopment = developmentBuildInputs
            .OrderBy(value => value.RepositoryRelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
        if (orderedDevelopment.Select(value => value.RepositoryRelativePath).Distinct(StringComparer.Ordinal).Count() !=
            orderedDevelopment.Length)
            throw new ArgumentException("Development build input paths must be distinct.", nameof(developmentBuildInputs));
        var orderedAdapterBuildReceipts = adapterBuildReceipts
            .OrderBy(value => value.AdapterArtifactDigest.HexValue, StringComparer.Ordinal)
            .ToImmutableArray();
        if (orderedAdapterBuildReceipts.Select(value => value.AdapterArtifactDigest).Distinct().Count() !=
            orderedAdapterBuildReceipts.Length)
            throw new ArgumentException(
                "Adapter build artifact digests must be distinct.",
                nameof(adapterBuildReceipts));
        ProvenanceSchemaVersion = provenanceSchemaVersion;
        CommittedBuildInputs = ordered;
        DevelopmentBuildInputs = orderedDevelopment;
        AdapterBuildReceipts = orderedAdapterBuildReceipts;
    }

    public int ProvenanceSchemaVersion { get; }
    public string BuildSystemId { get; }
    public string ExactBuildSystemVersion { get; }
    public string SourceCommit { get; }
    public ImmutableArray<CatalogCommittedBuildInput> CommittedBuildInputs { get; }
    public ImmutableArray<CatalogDevelopmentBuildInput> DevelopmentBuildInputs { get; }
    public ImmutableArray<CatalogAdapterBuildProvenanceReceipt> AdapterBuildReceipts { get; }

    public bool IsDevelopment => ProvenanceSchemaVersion == DevelopmentSchemaVersion;

    public static CatalogBuildProvenance CreateDevelopment(
        string buildSystemId,
        string exactBuildSystemVersion,
        string headCommit,
        ImmutableArray<CatalogDevelopmentBuildInput> exactWorktreeInputs) =>
        new(
            DevelopmentSchemaVersion,
            buildSystemId,
            exactBuildSystemVersion,
            headCommit,
            [],
            exactWorktreeInputs,
            []);

    public CatalogBuildProvenance WithAdapterBuildReceipts(
        ImmutableArray<CatalogAdapterBuildProvenanceReceipt> adapterBuildReceipts)
    {
        if (ProvenanceSchemaVersion != CurrentSchemaVersion || IsDevelopment)
            throw new InvalidOperationException(
                "Only committed schema-v2 provenance can be strengthened with reviewed adapter build receipts.");
        return new CatalogBuildProvenance(
            AdapterBuildReceiptSchemaVersion,
            BuildSystemId,
            ExactBuildSystemVersion,
            SourceCommit,
            CommittedBuildInputs,
            [],
            adapterBuildReceipts);
    }
}

public enum CatalogBuildMetadataPresence
{
    Absent = 0,
    Present = 1,
}

/// <summary>
/// Immutable build evidence for an exact adapter assembly. These coordinates are package provenance only and
/// intentionally do not participate in KnowledgeAdapterRevisionId v2.
/// </summary>
public sealed record CatalogAdapterBuildProvenanceReceipt
{
    public const int CurrentSchemaVersion = 1;

    [System.Text.Json.Serialization.JsonConstructor]
    public CatalogAdapterBuildProvenanceReceipt(
        int receiptSchemaVersion,
        ContentDigest adapterArtifactDigest,
        string assemblyName,
        string exactAssemblyVersion,
        string assemblyInformationalVersion,
        string moduleVersionId,
        string targetFramework,
        string buildConfiguration,
        string buildPlatform,
        string compilerId,
        string exactCompilerVersion,
        string sdkId,
        string exactSdkVersion,
        bool deterministicBuild,
        ContentDigest portablePdbDigest,
        CatalogBuildMetadataPresence sourceLinkPresence,
        ContentDigest? sourceLinkContentDigest,
        ContentDigest compilationOptionsContentDigest,
        ContentDigest compilationReferencesContentDigest)
    {
        if (receiptSchemaVersion != CurrentSchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(receiptSchemaVersion));
        if (!Enum.IsDefined(sourceLinkPresence))
            throw new ArgumentOutOfRangeException(nameof(sourceLinkPresence));
        if ((sourceLinkPresence == CatalogBuildMetadataPresence.Present) != (sourceLinkContentDigest is not null))
            throw new ArgumentException(
                "SourceLink presence must agree with its exact content digest.",
                nameof(sourceLinkContentDigest));
        var parsedMvid = Guid.TryParseExact(moduleVersionId, "D", out var mvid) ? mvid : Guid.Empty;
        if (parsedMvid == Guid.Empty || !string.Equals(moduleVersionId, parsedMvid.ToString("D"), StringComparison.Ordinal))
            throw new ArgumentException("MVID must be a nonempty lowercase D-format GUID.", nameof(moduleVersionId));

        ReceiptSchemaVersion = receiptSchemaVersion;
        AdapterArtifactDigest = adapterArtifactDigest;
        AssemblyName = SupportedKnowledgeFormat.RequireStrictText(assemblyName, nameof(assemblyName));
        ExactAssemblyVersion = SupportedKnowledgeFormat.RequireStrictText(exactAssemblyVersion, nameof(exactAssemblyVersion));
        AssemblyInformationalVersion = SupportedKnowledgeFormat.RequireStrictText(
            assemblyInformationalVersion,
            nameof(assemblyInformationalVersion));
        ModuleVersionId = moduleVersionId;
        TargetFramework = SupportedKnowledgeFormat.RequireStrictText(targetFramework, nameof(targetFramework));
        BuildConfiguration = SupportedKnowledgeFormat.RequireStrictText(buildConfiguration, nameof(buildConfiguration));
        BuildPlatform = SupportedKnowledgeFormat.RequireStrictText(buildPlatform, nameof(buildPlatform));
        CompilerId = SupportedKnowledgeFormat.RequireStrictText(compilerId, nameof(compilerId));
        ExactCompilerVersion = SupportedKnowledgeFormat.RequireStrictText(
            exactCompilerVersion,
            nameof(exactCompilerVersion));
        SdkId = SupportedKnowledgeFormat.RequireStrictText(sdkId, nameof(sdkId));
        ExactSdkVersion = SupportedKnowledgeFormat.RequireStrictText(exactSdkVersion, nameof(exactSdkVersion));
        DeterministicBuild = deterministicBuild;
        PortablePdbDigest = portablePdbDigest;
        SourceLinkPresence = sourceLinkPresence;
        SourceLinkContentDigest = sourceLinkContentDigest;
        CompilationOptionsContentDigest = compilationOptionsContentDigest;
        CompilationReferencesContentDigest = compilationReferencesContentDigest;
    }

    public int ReceiptSchemaVersion { get; }
    public ContentDigest AdapterArtifactDigest { get; }
    public string AssemblyName { get; }
    public string ExactAssemblyVersion { get; }
    public string AssemblyInformationalVersion { get; }
    public string ModuleVersionId { get; }
    public string TargetFramework { get; }
    public string BuildConfiguration { get; }
    public string BuildPlatform { get; }
    public string CompilerId { get; }
    public string ExactCompilerVersion { get; }
    public string SdkId { get; }
    public string ExactSdkVersion { get; }
    public bool DeterministicBuild { get; }
    public ContentDigest PortablePdbDigest { get; }
    public CatalogBuildMetadataPresence SourceLinkPresence { get; }
    public ContentDigest? SourceLinkContentDigest { get; }
    public ContentDigest CompilationOptionsContentDigest { get; }
    public ContentDigest CompilationReferencesContentDigest { get; }
}

public sealed record CatalogValidationSummary
{
    public CatalogValidationSummary(
        CatalogValidationStatus status,
        string policyId,
        string exactPolicyVersion,
        ContentDigest resultDigest)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        Status = status;
        PolicyId = SupportedKnowledgeFormat.RequireStrictText(policyId, nameof(policyId));
        ExactPolicyVersion = SupportedKnowledgeFormat.RequireStrictText(exactPolicyVersion, nameof(exactPolicyVersion));
        ResultDigest = resultDigest;
    }

    public CatalogValidationStatus Status { get; }
    public string PolicyId { get; }
    public string ExactPolicyVersion { get; }
    public ContentDigest ResultDigest { get; }
}

public sealed record CanonicalCatalogPayload
{
    public CanonicalCatalogPayload(
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<GameKnowledgeAdapterDescriptor> adapterDescriptors,
        ImmutableArray<CatalogSourceRecord> sources,
        ImmutableArray<SourceArtifactRecord> artifacts,
        ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> sourceRevisions,
        ImmutableArray<CanonicalKnowledgeRecord> knowledgeRecords,
        ImmutableArray<TerminologyAssertion> terminologyAssertions,
        ImmutableArray<RelationshipAssertion> relationshipAssertions,
        ImmutableArray<CatalogFileEvidenceReceipt> fileEvidenceReceipts,
        ImmutableArray<CatalogReferenceEvidenceReceipt> referenceEvidenceReceipts,
        ImmutableArray<EvidenceBinding> evidenceBindings,
        ImmutableArray<CanonicalCorrelationEnvelope> correlationEnvelopes,
        ImmutableArray<UnresolvedSourceAssertion> unresolvedSourceAssertions)
        : this(
            effectiveCoverage,
            adapterDescriptors,
            sources,
            artifacts,
            sourceRevisions,
            knowledgeRecords,
            terminologyAssertions,
            relationshipAssertions,
            fileEvidenceReceipts,
            referenceEvidenceReceipts,
            evidenceBindings,
            correlationEnvelopes,
            unresolvedSourceAssertions,
            [],
            [])
    {
    }

    [System.Text.Json.Serialization.JsonConstructor]
    public CanonicalCatalogPayload(
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<GameKnowledgeAdapterDescriptor> adapterDescriptors,
        ImmutableArray<CatalogSourceRecord> sources,
        ImmutableArray<SourceArtifactRecord> artifacts,
        ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> sourceRevisions,
        ImmutableArray<CanonicalKnowledgeRecord> knowledgeRecords,
        ImmutableArray<TerminologyAssertion> terminologyAssertions,
        ImmutableArray<RelationshipAssertion> relationshipAssertions,
        ImmutableArray<CatalogFileEvidenceReceipt> fileEvidenceReceipts,
        ImmutableArray<CatalogReferenceEvidenceReceipt> referenceEvidenceReceipts,
        ImmutableArray<EvidenceBinding> evidenceBindings,
        ImmutableArray<CanonicalCorrelationEnvelope> correlationEnvelopes,
        ImmutableArray<UnresolvedSourceAssertion> unresolvedSourceAssertions,
        ImmutableArray<SourceAcquisitionReceipt> acquisitionReceipts,
        ImmutableArray<SourceArtifactAcquisitionBinding> artifactAcquisitionBindings)
    {
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        RequireInitialized(adapterDescriptors, nameof(adapterDescriptors));
        if (adapterDescriptors.IsEmpty)
            throw new ArgumentException("A package payload requires at least one adapter descriptor.", nameof(adapterDescriptors));
        RequireInitialized(sources, nameof(sources));
        RequireInitialized(artifacts, nameof(artifacts));
        RequireInitialized(sourceRevisions, nameof(sourceRevisions));
        RequireInitialized(knowledgeRecords, nameof(knowledgeRecords));
        RequireInitialized(terminologyAssertions, nameof(terminologyAssertions));
        RequireInitialized(relationshipAssertions, nameof(relationshipAssertions));
        RequireInitialized(fileEvidenceReceipts, nameof(fileEvidenceReceipts));
        RequireInitialized(referenceEvidenceReceipts, nameof(referenceEvidenceReceipts));
        RequireInitialized(evidenceBindings, nameof(evidenceBindings));
        RequireInitialized(correlationEnvelopes, nameof(correlationEnvelopes));
        RequireInitialized(unresolvedSourceAssertions, nameof(unresolvedSourceAssertions));
        RequireInitialized(acquisitionReceipts, nameof(acquisitionReceipts));
        RequireInitialized(artifactAcquisitionBindings, nameof(artifactAcquisitionBindings));

        EffectiveCoverage = effectiveCoverage;
        AdapterDescriptors = SortDistinct(
            adapterDescriptors,
            value => value.RevisionId.Value,
            nameof(adapterDescriptors));
        Sources = SortDistinct(sources, value => value.Id.Value, nameof(sources));
        Artifacts = SortDistinct(artifacts, value => value.Id.Value, nameof(artifacts));
        SourceRevisions = SortDistinct(sourceRevisions, value => value.Revision.Id.Value, nameof(sourceRevisions));
        KnowledgeRecords = SortDistinct(knowledgeRecords, value => value.Id.Value, nameof(knowledgeRecords));
        TerminologyAssertions = SortDistinct(
            terminologyAssertions,
            value => EvidenceClaimContentId.DeriveV1(value).Value,
            nameof(terminologyAssertions));
        RelationshipAssertions = SortDistinct(
            relationshipAssertions,
            value => EvidenceClaimContentId.DeriveV1(value).Value,
            nameof(relationshipAssertions));
        FileEvidenceReceipts = SortDistinct(fileEvidenceReceipts, value => value.Id.Value, nameof(fileEvidenceReceipts));
        ReferenceEvidenceReceipts = SortDistinct(referenceEvidenceReceipts, value => value.Id.Value, nameof(referenceEvidenceReceipts));
        EvidenceBindings = SortDistinct(evidenceBindings, value => value.Id.Value, nameof(evidenceBindings));
        CorrelationEnvelopes = SortDistinct(correlationEnvelopes, value => value.Id.Value, nameof(correlationEnvelopes));
        UnresolvedSourceAssertions = SortDistinct(
            unresolvedSourceAssertions,
            value => value.Id.Value,
            nameof(unresolvedSourceAssertions));
        AcquisitionReceipts = SortDistinct(
            acquisitionReceipts,
            value => value.Id.Value,
            nameof(acquisitionReceipts));
        ArtifactAcquisitionBindings = SortDistinct(
            artifactAcquisitionBindings,
            value => value.ArtifactId.Value,
            nameof(artifactAcquisitionBindings));
    }

    public KnowledgeCoverageState EffectiveCoverage { get; }
    public ImmutableArray<GameKnowledgeAdapterDescriptor> AdapterDescriptors { get; }
    public ImmutableArray<CatalogSourceRecord> Sources { get; }
    public ImmutableArray<SourceArtifactRecord> Artifacts { get; }
    public ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> SourceRevisions { get; }
    public ImmutableArray<CanonicalKnowledgeRecord> KnowledgeRecords { get; init; }
    public ImmutableArray<TerminologyAssertion> TerminologyAssertions { get; }
    public ImmutableArray<RelationshipAssertion> RelationshipAssertions { get; init; }
    public ImmutableArray<CatalogFileEvidenceReceipt> FileEvidenceReceipts { get; }
    public ImmutableArray<CatalogReferenceEvidenceReceipt> ReferenceEvidenceReceipts { get; }
    public ImmutableArray<EvidenceBinding> EvidenceBindings { get; }
    public ImmutableArray<CanonicalCorrelationEnvelope> CorrelationEnvelopes { get; }
    public ImmutableArray<UnresolvedSourceAssertion> UnresolvedSourceAssertions { get; }
    public ImmutableArray<SourceAcquisitionReceipt> AcquisitionReceipts { get; }
    public ImmutableArray<SourceArtifactAcquisitionBinding> ArtifactAcquisitionBindings { get; }

    /// <summary>
    /// Schema-v4 Location claim sets. Earlier package schemas leave these collections empty and
    /// are never reinterpreted as satisfying the frozen Location contract.
    /// </summary>
    public ImmutableArray<SourceNativeLocationTypeAssertion> SourceNativeLocationTypeAssertions { get; init; } = [];
    public ImmutableArray<LocationSemanticClassificationAssertion> LocationSemanticClassificationAssertions { get; init; } = [];
    public ImmutableArray<CanonicalRecordLifecycleAssertion> RecordLifecycleAssertions { get; init; } = [];
    public ImmutableArray<CorrelatedRelationshipEnvelope> CorrelatedRelationshipEnvelopes { get; init; } = [];
    public ImmutableArray<LocationCoverageReport> LocationCoverageReports { get; init; } = [];

    /// <summary>Schema-v5 selector-classification and source-backed Instructions assertions.</summary>
    public ImmutableArray<CanonicalSemanticClassificationAssertion> SemanticClassificationAssertions { get; init; } = [];
    public ImmutableArray<CanonicalRecordContributionAssertion> RecordContributionAssertions { get; init; } = [];
    public ImmutableArray<CanonicalOrganizationalValueAssertion> OrganizationalValueAssertions { get; init; } = [];
    public ImmutableArray<InstructionAssertion> InstructionAssertions { get; init; } = [];
    public ImmutableArray<InstructionEvidenceBinding> InstructionEvidenceBindings { get; init; } = [];
    public ImmutableArray<InstructionConflictGroup> InstructionConflictGroups { get; init; } = [];

    /// <summary>Schema-v6 envelopes that attach typed claims from a distinct authoritative revision.</summary>
    public ImmutableArray<CrossSourceCanonicalAssertion> CrossSourceAssertions { get; init; } = [];
    public ImmutableArray<CrossSourceTargetLinkClaim> CrossSourceTargetLinkClaims { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceClaimContent> UnresolvedCrossSourceClaimContents { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceEvidenceBinding> UnresolvedCrossSourceEvidenceBindings { get; init; } = [];
    public ImmutableArray<UnresolvedCrossSourceAssertion> UnresolvedCrossSourceAssertions { get; init; } = [];

    private static void RequireInitialized<T>(ImmutableArray<T> values, string parameterName)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", parameterName);
    }

    private static ImmutableArray<T> SortDistinct<T>(
        ImmutableArray<T> values,
        Func<T, string> identity,
        string parameterName)
    {
        if (values.Any(value => value is null))
            throw new ArgumentException("Collection values must be non-null.", parameterName);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection identities must be distinct.", parameterName);
        return ordered;
    }
}

public sealed record CatalogPackageManifest
{
    public const int LegacySchemaVersion = 2;
    public const int CurrentSchemaVersion = 3;
    public const int LocationContractSchemaVersion = 4;
    public const int ProjectionContractSchemaVersion = 5;
    public const int CrossSourceAssertionSchemaVersion = 6;
    public const int AdapterProvenanceBoundarySchemaVersion = 7;

    public CatalogPackageManifest(
        int packageSchemaVersion,
        CatalogRevisionId catalogRevisionId,
        CatalogPackageKind packageKind,
        CatalogGameScope gameScope,
        CatalogModScope? modScope,
        KnowledgeCoverageState effectiveCoverage,
        ImmutableArray<KnowledgeAdapterRevisionCoordinate> adapterRevisions,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        ImmutableArray<CatalogPackageId> requiredBasePackageIds,
        string compositionPolicyVersion,
        CatalogPayloadDigest payloadDigest,
        CatalogValidationStatus validationStatus,
        string validationPolicyId,
        string validationPolicyVersion,
        ContentDigest validationResultDigest,
        CatalogBuildProvenance buildProvenance)
    {
        if (packageSchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion or LocationContractSchemaVersion or ProjectionContractSchemaVersion or CrossSourceAssertionSchemaVersion or AdapterProvenanceBoundarySchemaVersion))
            throw new ArgumentOutOfRangeException(nameof(packageSchemaVersion));
        if (!Enum.IsDefined(packageKind)) throw new ArgumentOutOfRangeException(nameof(packageKind));
        if (!Enum.IsDefined(validationStatus)) throw new ArgumentOutOfRangeException(nameof(validationStatus));
        if (!Enum.IsDefined(effectiveCoverage) || effectiveCoverage == KnowledgeCoverageState.Unsupported)
            throw new ArgumentOutOfRangeException(nameof(effectiveCoverage));
        ArgumentNullException.ThrowIfNull(gameScope);
        ArgumentNullException.ThrowIfNull(buildProvenance);
        if (packageSchemaVersion == LegacySchemaVersion && buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.LegacySchemaVersion)
            throw new ArgumentException("Schema-v2 packages require legacy build provenance.", nameof(buildProvenance));
        if (packageSchemaVersion is CurrentSchemaVersion or LocationContractSchemaVersion or ProjectionContractSchemaVersion &&
            buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.CurrentSchemaVersion)
            throw new ArgumentException("Schema-v3 and schema-v4 packages require committed build provenance closure.", nameof(buildProvenance));
        if (packageSchemaVersion == CrossSourceAssertionSchemaVersion &&
            buildProvenance.ProvenanceSchemaVersion is not (
                CatalogBuildProvenance.CurrentSchemaVersion or
                CatalogBuildProvenance.DevelopmentSchemaVersion or
                CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion))
            throw new ArgumentException(
                "Schema-v6 packages require committed, reviewed-adapter-build, or explicitly developmental provenance closure.",
                nameof(buildProvenance));
        if (packageSchemaVersion == AdapterProvenanceBoundarySchemaVersion &&
            buildProvenance.ProvenanceSchemaVersion != CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion)
            throw new ArgumentException(
                "Schema-v7 packages require reviewed adapter-build provenance closure.",
                nameof(buildProvenance));
        if (buildProvenance.IsDevelopment && validationStatus != CatalogValidationStatus.Candidate)
            throw new ArgumentException("Development-provenance packages must remain Candidate and cannot be approved or published.", nameof(validationStatus));
        if (adapterRevisions.IsDefaultOrEmpty)
            throw new ArgumentException("A package requires at least one adapter revision coordinate.", nameof(adapterRevisions));
        if (adapterRevisions.Any(value => value is null))
            throw new ArgumentException("Adapter revision coordinates must be non-null.", nameof(adapterRevisions));
        if (sourceRevisionIds.IsDefaultOrEmpty)
            throw new ArgumentException("A package requires at least one source revision.", nameof(sourceRevisionIds));
        if (requiredBasePackageIds.IsDefault)
            throw new ArgumentException("Required base package identities must be initialized.", nameof(requiredBasePackageIds));
        if (packageKind == CatalogPackageKind.BaseGameCatalog && (modScope is not null || !requiredBasePackageIds.IsEmpty))
            throw new ArgumentException("A base-game package cannot declare a mod scope or required base package.");
        if (packageKind == CatalogPackageKind.ModCatalogExtension && (modScope is null || requiredBasePackageIds.IsEmpty))
            throw new ArgumentException("A mod extension requires an exact mod scope and base package identity.");

        PackageSchemaVersion = packageSchemaVersion;
        CatalogRevisionId = catalogRevisionId;
        PackageKind = packageKind;
        GameScope = gameScope;
        ModScope = modScope;
        EffectiveCoverage = effectiveCoverage;
        AdapterRevisions = CatalogGameScope.NormalizeIds(
            adapterRevisions,
            value => value.Id.Value,
            nameof(adapterRevisions));
        SourceRevisionIds = CatalogGameScope.NormalizeIds(
            sourceRevisionIds,
            value => value.Value,
            nameof(sourceRevisionIds));
        RequiredBasePackageIds = CatalogGameScope.NormalizeIds(
            requiredBasePackageIds,
            value => value.Value,
            nameof(requiredBasePackageIds));
        CompositionPolicyVersion = SupportedKnowledgeFormat.RequireStrictText(
            compositionPolicyVersion,
            nameof(compositionPolicyVersion));
        PayloadDigest = payloadDigest;
        ValidationStatus = validationStatus;
        ValidationPolicyId = SupportedKnowledgeFormat.RequireStrictText(validationPolicyId, nameof(validationPolicyId));
        ValidationPolicyVersion = SupportedKnowledgeFormat.RequireStrictText(
            validationPolicyVersion,
            nameof(validationPolicyVersion));
        ValidationResultDigest = validationResultDigest;
        BuildProvenance = buildProvenance;
    }

    public int PackageSchemaVersion { get; }
    public CatalogRevisionId CatalogRevisionId { get; }
    public CatalogPackageKind PackageKind { get; }
    public CatalogGameScope GameScope { get; }
    public CatalogModScope? ModScope { get; }
    public KnowledgeCoverageState EffectiveCoverage { get; }
    public ImmutableArray<KnowledgeAdapterRevisionCoordinate> AdapterRevisions { get; }
    public ImmutableArray<KnowledgeAdapterRevisionId> AdapterRevisionIds =>
        AdapterRevisions.Select(value => value.Id).ToImmutableArray();
    public ImmutableArray<CatalogSourceRevisionId> SourceRevisionIds { get; }
    public ImmutableArray<CatalogPackageId> RequiredBasePackageIds { get; }
    public string CompositionPolicyVersion { get; }
    public CatalogPayloadDigest PayloadDigest { get; }
    public CatalogValidationStatus ValidationStatus { get; }
    public string ValidationPolicyId { get; }
    public string ValidationPolicyVersion { get; }
    public ContentDigest ValidationResultDigest { get; }
    public CatalogBuildProvenance BuildProvenance { get; }
}

public sealed record CanonicalCatalogPackage(
    CatalogPackageId Id,
    CatalogPackageManifest Manifest,
    CanonicalCatalogPayload Payload,
    CatalogValidationSummary ValidationSummary);

public sealed record CatalogPackageVerificationResult(ImmutableArray<string> Issues)
{
    /// <summary>Indicates structural integrity only; it is not publication or distribution approval.</summary>
    public bool IsStructurallyValid => Issues.IsEmpty;
}

internal static class CanonicalKnowledgePackageEncoding
{
    public static void AddSortedIds(CanonicalIdentityWriter writer, string prefix, IEnumerable<string> values)
    {
        var ordered = values.OrderBy(value => value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Any(string.IsNullOrWhiteSpace) || ordered.Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Canonical identity collections must contain distinct non-empty values.", prefix);
        writer.AddInt32($"{prefix}.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
            writer.AddString($"{prefix}.{index}", ordered[index]);
    }

    public static void AddGameScope(CanonicalIdentityWriter writer, string prefix, CatalogGameScope scope)
    {
        writer.AddString($"{prefix}.game-id", scope.GameId.Value);
        AddOptionalExactNativeVersion(writer, $"{prefix}.game-version", scope.ExactGameVersion);
        AddSortedIds(writer, $"{prefix}.artifacts", scope.ArtifactFingerprints.Select(value => value.Value));
    }

    public static void AddOptionalModScope(CanonicalIdentityWriter writer, string prefix, CatalogModScope? scope)
    {
        if (scope is null)
        {
            writer.AddOptionalString(prefix, null);
            return;
        }
        writer.AddExactNativeIdentifier($"{prefix}.identity", scope.ExactModIdentity);
        AddOptionalExactNativeVersion(writer, $"{prefix}.version", scope.ExactModVersion);
        AddSortedIds(writer, $"{prefix}.artifacts", scope.ArtifactFingerprints.Select(value => value.Value));
    }

    public static CatalogPayloadDigest DerivePayloadDigest(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter("catalog-payload", CatalogPayloadDigest.PreviousAlgorithmVersion);
        AddBasePayload(writer, payload, includeAdapterArtifactDigest: true);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    private static void AddBasePayload(
        CanonicalIdentityWriter writer,
        CanonicalCatalogPayload payload,
        bool includeAdapterArtifactDigest)
    {
        writer.AddInt32("effective-coverage", (int)payload.EffectiveCoverage);
        AddAdapterDescriptors(
            writer,
            "adapter-descriptors",
            payload.AdapterDescriptors,
            includeAdapterArtifactDigest);

        writer.AddInt32("sources.count", payload.Sources.Length);
        for (var index = 0; index < payload.Sources.Length; index++)
        {
            var value = payload.Sources[index];
            writer.AddString($"sources.{index}.id", value.Id.Value);
            writer.AddInt32($"sources.{index}.kind", (int)value.Kind);
            writer.AddExactNativeIdentifier($"sources.{index}.native", value.NativeIdentity);
        }

        writer.AddInt32("artifacts.count", payload.Artifacts.Length);
        for (var index = 0; index < payload.Artifacts.Length; index++)
        {
            var value = payload.Artifacts[index];
            writer.AddString($"artifacts.{index}.id", value.Id.Value);
            writer.AddContentDigest($"artifacts.{index}.digest", value.Digest);
        }

        writer.AddInt32("source-revisions.count", payload.SourceRevisions.Length);
        for (var index = 0; index < payload.SourceRevisions.Length; index++)
        {
            var value = payload.SourceRevisions[index];
            writer.AddString($"source-revisions.{index}.id", value.Revision.Id.Value);
            writer.AddString($"source-revisions.{index}.source-id", value.Revision.SourceId.Value);
            AddOptionalExactNativeVersion(writer, $"source-revisions.{index}.native", value.Revision.NativeRevision);
            AddSortedIds(writer, $"source-revisions.{index}.artifacts", value.Revision.ArtifactIds.Select(item => item.Value));
            writer.AddString($"source-revisions.{index}.adapter", value.AdapterRevisionId.Value);
            AddSourceScope(writer, $"source-revisions.{index}.scope", value.SourceScope);
            writer.AddInt32($"source-revisions.{index}.artifact-formats.count", value.ArtifactFormats.Length);
            for (var formatIndex = 0; formatIndex < value.ArtifactFormats.Length; formatIndex++)
            {
                var binding = value.ArtifactFormats[formatIndex];
                writer.AddString($"source-revisions.{index}.artifact-formats.{formatIndex}.artifact", binding.ArtifactId.Value);
                AddFormat(writer, $"source-revisions.{index}.artifact-formats.{formatIndex}.format", binding.Format);
            }
        }

        writer.AddInt32("records.count", payload.KnowledgeRecords.Length);
        for (var index = 0; index < payload.KnowledgeRecords.Length; index++)
        {
            var value = payload.KnowledgeRecords[index];
            writer.AddString($"records.{index}.id", value.Id.Value);
            writer.AddString($"records.{index}.game-id", value.GameId.Value);
            AddOptionalExactNativeVersion(writer, $"records.{index}.game-version", value.GameVersion);
            AddOptionalExactNativeVersion(writer, $"records.{index}.mod-version", value.ModVersion);
            writer.AddString($"records.{index}.source-revision", value.SourceRevisionId.Value);
            writer.AddInt32($"records.{index}.kind", (int)value.Kind);
            writer.AddString($"records.{index}.native-record-id", value.NativeRecordIdentityId.Value);
            writer.AddExactNativeIdentifier($"records.{index}.native", value.NativeIdentity);
        }

        writer.AddInt32("terminology.count", payload.TerminologyAssertions.Length);
        for (var index = 0; index < payload.TerminologyAssertions.Length; index++)
        {
            var value = payload.TerminologyAssertions[index];
            writer.AddString($"terminology.{index}.claim-id", EvidenceClaimContentId.DeriveV1(value).Value);
            writer.AddString($"terminology.{index}.record-id", value.KnowledgeRecordId.Value);
            writer.AddString($"terminology.{index}.source-revision", value.SourceRevisionId.Value);
            writer.AddInt32($"terminology.{index}.role", (int)value.Role);
            writer.AddString($"terminology.{index}.value", value.VerbatimValue);
            writer.AddString($"terminology.{index}.field", value.SourceFieldPath);
            writer.AddOptionalString($"terminology.{index}.language", value.LanguageTag);
            AddOptionalExactNativeIdentifier(writer, $"terminology.{index}.native-string", value.NativeStringIdentifier);
        }

        writer.AddInt32("relationships.count", payload.RelationshipAssertions.Length);
        for (var index = 0; index < payload.RelationshipAssertions.Length; index++)
        {
            var value = payload.RelationshipAssertions[index];
            writer.AddString($"relationships.{index}.claim-id", EvidenceClaimContentId.DeriveV1(value).Value);
            writer.AddString($"relationships.{index}.subject", value.SubjectKnowledgeRecordId.Value);
            writer.AddString($"relationships.{index}.source-revision", value.SourceRevisionId.Value);
            writer.AddString($"relationships.{index}.semantic", value.SemanticId.Value);
            writer.AddString($"relationships.{index}.native-type", value.SourceNativeRelationshipType);
            writer.AddString($"relationships.{index}.field", value.SourceFieldPath);
            writer.AddExactNativeIdentifier($"relationships.{index}.target", value.SourceNativeTarget);
            writer.AddOptionalString($"relationships.{index}.resolved-target", value.ResolvedTargetKnowledgeRecordId?.Value);
        }

        AddFileEvidence(writer, payload.FileEvidenceReceipts);
        AddReferenceEvidence(writer, payload.ReferenceEvidenceReceipts);
        AddBindings(writer, payload.EvidenceBindings);
        AddCorrelations(writer, payload.CorrelationEnvelopes);
        AddUnresolved(writer, payload.UnresolvedSourceAssertions);
    }

    public static CatalogPayloadDigest DerivePayloadDigestV3(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter("catalog-payload", CatalogPayloadDigest.CurrentAlgorithmVersion);
        writer.AddString("v2-semantic-digest", DerivePayloadDigest(payload).Value);
        AddAcquisitionReceipts(writer, payload.AcquisitionReceipts);
        AddArtifactAcquisitionBindings(writer, payload.ArtifactAcquisitionBindings);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    public static CatalogPayloadDigest DerivePayloadDigestV4(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter(
            "catalog-payload",
            CatalogPayloadDigest.LocationContractAlgorithmVersion);
        writer.AddString("v3-semantic-digest", DerivePayloadDigestV3(payload).Value);
        AddLocationNativeTypes(writer, payload.SourceNativeLocationTypeAssertions);
        AddLocationClassifications(writer, payload.LocationSemanticClassificationAssertions);
        AddRecordLifecycleAssertions(writer, payload.RecordLifecycleAssertions);
        AddCorrelatedRelationships(writer, payload.CorrelatedRelationshipEnvelopes);
        AddLocationCoverageReports(writer, payload.LocationCoverageReports);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    public static CatalogPayloadDigest DerivePayloadDigestV5(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter(
            "catalog-payload",
            CatalogPayloadDigest.ProjectionContractAlgorithmVersion);
        writer.AddString("v4-semantic-digest", DerivePayloadDigestV4(payload).Value);
        AddSemanticClassifications(writer, payload.SemanticClassificationAssertions);
        AddRecordContributions(writer, payload.RecordContributionAssertions);
        AddOrganizationalValues(writer, payload.OrganizationalValueAssertions);
        AddInstructions(writer, payload.InstructionAssertions);
        AddInstructionBindings(writer, payload.InstructionEvidenceBindings);
        AddInstructionConflicts(writer, payload.InstructionConflictGroups);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    public static CatalogPayloadDigest DerivePayloadDigestV6(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter(
            "catalog-payload",
            CatalogPayloadDigest.CrossSourceAssertionAlgorithmVersion);
        writer.AddString("v5-semantic-digest", DerivePayloadDigestV5(payload).Value);
        AddCrossSourceTargetLinkClaims(writer, payload.CrossSourceTargetLinkClaims);
        AddCrossSourceAssertions(writer, payload.CrossSourceAssertions);
        AddUnresolvedCrossSourceClaims(writer, payload.UnresolvedCrossSourceClaimContents);
        AddUnresolvedCrossSourceBindings(writer, payload.UnresolvedCrossSourceEvidenceBindings);
        AddUnresolvedCrossSourceAssertions(writer, payload.UnresolvedCrossSourceAssertions);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    public static CatalogPayloadDigest DerivePayloadDigestV7(CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var writer = new CanonicalIdentityWriter(
            "catalog-payload",
            CatalogPayloadDigest.AdapterProvenanceBoundaryAlgorithmVersion);
        AddBasePayload(writer, payload, includeAdapterArtifactDigest: false);
        AddAcquisitionReceipts(writer, payload.AcquisitionReceipts);
        AddArtifactAcquisitionBindings(writer, payload.ArtifactAcquisitionBindings);
        AddLocationNativeTypes(writer, payload.SourceNativeLocationTypeAssertions);
        AddLocationClassifications(writer, payload.LocationSemanticClassificationAssertions);
        AddRecordLifecycleAssertions(writer, payload.RecordLifecycleAssertions);
        AddCorrelatedRelationships(writer, payload.CorrelatedRelationshipEnvelopes);
        AddLocationCoverageReports(writer, payload.LocationCoverageReports);
        AddSemanticClassifications(writer, payload.SemanticClassificationAssertions);
        AddRecordContributions(writer, payload.RecordContributionAssertions);
        AddOrganizationalValues(writer, payload.OrganizationalValueAssertions);
        AddInstructions(writer, payload.InstructionAssertions);
        AddInstructionBindings(writer, payload.InstructionEvidenceBindings);
        AddInstructionConflicts(writer, payload.InstructionConflictGroups);
        AddCrossSourceTargetLinkClaims(writer, payload.CrossSourceTargetLinkClaims);
        AddCrossSourceAssertions(writer, payload.CrossSourceAssertions);
        AddUnresolvedCrossSourceClaims(writer, payload.UnresolvedCrossSourceClaimContents);
        AddUnresolvedCrossSourceBindings(writer, payload.UnresolvedCrossSourceEvidenceBindings);
        AddUnresolvedCrossSourceAssertions(writer, payload.UnresolvedCrossSourceAssertions);
        return CatalogPayloadDigest.FromCanonicalWriter(writer);
    }

    public static CatalogPackageId DerivePackageId(CatalogPackageManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var algorithmVersion = manifest.PackageSchemaVersion switch
        {
            CatalogPackageManifest.LegacySchemaVersion => CatalogPackageId.PreviousAlgorithmVersion,
            CatalogPackageManifest.CurrentSchemaVersion => CatalogPackageId.CurrentAlgorithmVersion,
            CatalogPackageManifest.LocationContractSchemaVersion => CatalogPackageId.LocationContractAlgorithmVersion,
            CatalogPackageManifest.ProjectionContractSchemaVersion => CatalogPackageId.ProjectionContractAlgorithmVersion,
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion => CatalogPackageId.CrossSourceAssertionAlgorithmVersion,
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion => CatalogPackageId.AdapterProvenanceBoundaryAlgorithmVersion,
            _ => throw new ArgumentOutOfRangeException(nameof(manifest)),
        };
        var writer = new CanonicalIdentityWriter("catalog-package", algorithmVersion);
        writer.AddInt32("schema-version", manifest.PackageSchemaVersion);
        writer.AddString("catalog-revision-id", manifest.CatalogRevisionId.Value);
        writer.AddInt32("package-kind", (int)manifest.PackageKind);
        AddGameScope(writer, "game-scope", manifest.GameScope);
        AddOptionalModScope(writer, "mod-scope", manifest.ModScope);
        writer.AddInt32("effective-coverage", (int)manifest.EffectiveCoverage);
        AddAdapterRevisionCoordinates(writer, "adapter-revisions", manifest.AdapterRevisions);
        AddSortedIds(writer, "source-revisions", manifest.SourceRevisionIds.Select(value => value.Value));
        AddSortedIds(writer, "required-base-packages", manifest.RequiredBasePackageIds.Select(value => value.Value));
        writer.AddString("composition-policy-version", manifest.CompositionPolicyVersion);
        writer.AddString("payload-digest", manifest.PayloadDigest.Value);
        writer.AddInt32("validation-status", (int)manifest.ValidationStatus);
        writer.AddString("validation-policy-id", manifest.ValidationPolicyId);
        writer.AddString("validation-policy-version", manifest.ValidationPolicyVersion);
        writer.AddContentDigest("validation-result-digest", manifest.ValidationResultDigest);
        writer.AddString("build.system-id", manifest.BuildProvenance.BuildSystemId);
        writer.AddString("build.system-version", manifest.BuildProvenance.ExactBuildSystemVersion);
        writer.AddString("build.source-commit", manifest.BuildProvenance.SourceCommit);
        if (manifest.PackageSchemaVersion is CatalogPackageManifest.CurrentSchemaVersion or
            CatalogPackageManifest.LocationContractSchemaVersion or
            CatalogPackageManifest.ProjectionContractSchemaVersion or
            CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
            CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion)
        {
            writer.AddInt32("build.provenance-schema-version", manifest.BuildProvenance.ProvenanceSchemaVersion);
            writer.AddInt32("build.inputs.count", manifest.BuildProvenance.CommittedBuildInputs.Length);
            for (var index = 0; index < manifest.BuildProvenance.CommittedBuildInputs.Length; index++)
            {
                var input = manifest.BuildProvenance.CommittedBuildInputs[index];
                writer.AddString($"build.inputs.{index}.path", input.RepositoryRelativePath);
                writer.AddString($"build.inputs.{index}.git-blob", input.GitBlobObjectId);
            }
            if (manifest.PackageSchemaVersion == CatalogPackageManifest.CrossSourceAssertionSchemaVersion &&
                manifest.BuildProvenance.ProvenanceSchemaVersion == CatalogBuildProvenance.DevelopmentSchemaVersion)
            {
                writer.AddInt32("build.development-inputs.count", manifest.BuildProvenance.DevelopmentBuildInputs.Length);
                for (var index = 0; index < manifest.BuildProvenance.DevelopmentBuildInputs.Length; index++)
                {
                    var input = manifest.BuildProvenance.DevelopmentBuildInputs[index];
                    writer.AddString($"build.development-inputs.{index}.path", input.RepositoryRelativePath);
                    writer.AddContentDigest($"build.development-inputs.{index}.content", input.ExactContentDigest);
                    writer.AddInt32($"build.development-inputs.{index}.state", (int)input.State);
                }
            }
            if (manifest.PackageSchemaVersion is (CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                    CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion) &&
                manifest.BuildProvenance.ProvenanceSchemaVersion ==
                    CatalogBuildProvenance.AdapterBuildReceiptSchemaVersion)
            {
                writer.AddInt32("build.adapter-receipts.count", manifest.BuildProvenance.AdapterBuildReceipts.Length);
                for (var index = 0; index < manifest.BuildProvenance.AdapterBuildReceipts.Length; index++)
                {
                    var receipt = manifest.BuildProvenance.AdapterBuildReceipts[index];
                    var prefix = $"build.adapter-receipts.{index}";
                    writer.AddInt32($"{prefix}.schema-version", receipt.ReceiptSchemaVersion);
                    writer.AddContentDigest($"{prefix}.artifact", receipt.AdapterArtifactDigest);
                    writer.AddString($"{prefix}.assembly-name", receipt.AssemblyName);
                    writer.AddString($"{prefix}.assembly-version", receipt.ExactAssemblyVersion);
                    writer.AddString($"{prefix}.assembly-informational-version", receipt.AssemblyInformationalVersion);
                    writer.AddString($"{prefix}.mvid", receipt.ModuleVersionId);
                    writer.AddString($"{prefix}.target-framework", receipt.TargetFramework);
                    writer.AddString($"{prefix}.configuration", receipt.BuildConfiguration);
                    writer.AddString($"{prefix}.platform", receipt.BuildPlatform);
                    writer.AddString($"{prefix}.compiler-id", receipt.CompilerId);
                    writer.AddString($"{prefix}.compiler-version", receipt.ExactCompilerVersion);
                    writer.AddString($"{prefix}.sdk-id", receipt.SdkId);
                    writer.AddString($"{prefix}.sdk-version", receipt.ExactSdkVersion);
                    writer.AddInt32($"{prefix}.deterministic", receipt.DeterministicBuild ? 1 : 0);
                    writer.AddContentDigest($"{prefix}.portable-pdb", receipt.PortablePdbDigest);
                    writer.AddInt32($"{prefix}.source-link-presence", (int)receipt.SourceLinkPresence);
                    writer.AddOptionalContentDigest($"{prefix}.source-link", receipt.SourceLinkContentDigest);
                    writer.AddContentDigest($"{prefix}.compilation-options", receipt.CompilationOptionsContentDigest);
                    writer.AddContentDigest($"{prefix}.compilation-references", receipt.CompilationReferencesContentDigest);
                }
            }
        }
        return CatalogPackageId.FromCanonicalWriter(writer);
    }

    private static void AddLocationNativeTypes(
        CanonicalIdentityWriter writer,
        ImmutableArray<SourceNativeLocationTypeAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("location-native-types.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"location-native-types.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.record", value.KnowledgeRecordId.Value);
            writer.AddString($"{prefix}.source-revision", value.SourceRevisionId.Value);
            writer.AddExactNativeIdentifier($"{prefix}.native-type", value.ExactNativeType);
            writer.AddString($"{prefix}.field", value.SourceFieldPath);
        }
    }

    private static void AddSemanticClassifications(
        CanonicalIdentityWriter writer,
        ImmutableArray<CanonicalSemanticClassificationAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("semantic-classifications.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"semantic-classifications.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.claim", EvidenceClaimContentId.DeriveV1(value).Value);
        }
    }

    private static void AddRecordContributions(
        CanonicalIdentityWriter writer,
        ImmutableArray<CanonicalRecordContributionAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("record-contributions.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            writer.AddString($"record-contributions.{index}.id", value.Id.Value);
            writer.AddString($"record-contributions.{index}.claim", EvidenceClaimContentId.DeriveV1(value).Value);
        }
    }

    private static void AddOrganizationalValues(
        CanonicalIdentityWriter writer,
        ImmutableArray<CanonicalOrganizationalValueAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("organizational-values.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            writer.AddString($"organizational-values.{index}.id", value.Id.Value);
            writer.AddString($"organizational-values.{index}.claim", EvidenceClaimContentId.DeriveV1(value).Value);
        }
    }

    private static void AddInstructions(
        CanonicalIdentityWriter writer,
        ImmutableArray<InstructionAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("instructions.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"instructions.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.claim", EvidenceClaimContentId.DeriveV1(value).Value);
            writer.AddInt32($"{prefix}.retention", (int)value.Content.RetentionMode);
            writer.AddOptionalString($"{prefix}.verbatim", value.Content.VerbatimText);
            writer.AddInt32($"{prefix}.fields.count", value.Content.StructuredFields.Length);
            for (var fieldIndex = 0; fieldIndex < value.Content.StructuredFields.Length; fieldIndex++)
            {
                writer.AddString($"{prefix}.fields.{fieldIndex}.key", value.Content.StructuredFields[fieldIndex].ExactKey);
                writer.AddString($"{prefix}.fields.{fieldIndex}.value", value.Content.StructuredFields[fieldIndex].ExactValue);
            }
            writer.AddContentDigest($"{prefix}.content-digest", value.Content.ContentDigest);
            AddSortedIds(writer, $"{prefix}.records", value.ApplicableRecordIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.packages", value.ApplicablePackageIds.Select(item => item.Value));
        }
    }

    private static void AddInstructionBindings(
        CanonicalIdentityWriter writer,
        ImmutableArray<InstructionEvidenceBinding> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("instruction-bindings.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"instruction-bindings.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.receipt", value.EvidenceReceiptId.Value);
            writer.AddString($"{prefix}.assertion", value.InstructionAssertionId.Value);
            writer.AddString($"{prefix}.revision", value.SourceRevisionId.Value);
            writer.AddString($"{prefix}.locator", value.ExactLocator);
            writer.AddString($"{prefix}.claim", value.ClaimContentId.Value);
            if (value.Id.AlgorithmVersion >= InstructionEvidenceBindingId.EvidenceClassAlgorithmVersion)
                writer.AddInt32($"{prefix}.verification", (int)value.Verification!.Value);
        }
    }

    private static void AddInstructionConflicts(
        CanonicalIdentityWriter writer,
        ImmutableArray<InstructionConflictGroup> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("instruction-conflicts.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            writer.AddString($"instruction-conflicts.{index}.id", value.Id.Value);
            AddSortedIds(writer, $"instruction-conflicts.{index}.members", value.MemberIds.Select(item => item.Value));
            writer.AddString($"instruction-conflicts.{index}.method", value.MethodId);
            writer.AddString($"instruction-conflicts.{index}.version", value.MethodVersion);
        }
    }

    private static void AddCrossSourceAssertions(
        CanonicalIdentityWriter writer,
        ImmutableArray<CrossSourceCanonicalAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("cross-source-assertions.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"cross-source-assertions.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.target-record", value.TargetKnowledgeRecordId.Value);
            writer.AddString($"{prefix}.target-origin-revision", value.TargetOriginSourceRevisionId.Value);
            writer.AddString($"{prefix}.target-native-record", value.TargetNativeRecordIdentityId.Value);
            writer.AddString($"{prefix}.asserting-revision", value.AssertingSourceRevisionId.Value);
            writer.AddInt32($"{prefix}.assertion-kind", (int)value.AssertionKind);
            writer.AddString($"{prefix}.claim", value.UnderlyingClaimContentId.Value);
            writer.AddString($"{prefix}.target-link-claim", value.TargetLinkClaimId.Value);
            writer.AddInt32($"{prefix}.link-kind", (int)value.TargetLinkKind);
            writer.AddExactNativeIdentifier($"{prefix}.link-key", value.ExactLinkKey);
            writer.AddString($"{prefix}.link-method-id", value.TargetLinkMethodId);
            writer.AddString($"{prefix}.link-method-version", value.TargetLinkMethodVersion);
            AddSortedIds(writer, $"{prefix}.receipts", value.SupportingEvidenceReceiptIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.bindings", value.SupportingEvidenceBindingIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.instruction-bindings", value.SupportingInstructionEvidenceBindingIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.correlations", value.CorrelationRecordIds.Select(item => item.Value));
        }
    }

    private static void AddCrossSourceTargetLinkClaims(
        CanonicalIdentityWriter writer,
        ImmutableArray<CrossSourceTargetLinkClaim> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("cross-source-target-link-claims.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"cross-source-target-link-claims.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.target-record", value.TargetKnowledgeRecordId.Value);
            writer.AddString($"{prefix}.target-origin-revision", value.TargetOriginSourceRevisionId.Value);
            writer.AddString($"{prefix}.target-native-record", value.TargetNativeRecordIdentityId.Value);
            writer.AddExactNativeIdentifier($"{prefix}.target-coordinate", value.ExactTargetCoordinate);
            writer.AddString($"{prefix}.target-coordinate-artifact", value.TargetCoordinateArtifactId.Value);
            writer.AddString($"{prefix}.target-coordinate-field", value.TargetCoordinateFieldPath);
            writer.AddString($"{prefix}.asserting-revision", value.AssertingSourceRevisionId.Value);
            writer.AddExactNativeIdentifier($"{prefix}.asserting-coordinate", value.ExactAssertingCoordinate);
            writer.AddString($"{prefix}.asserting-coordinate-artifact", value.AssertingCoordinateArtifactId.Value);
            writer.AddString($"{prefix}.asserting-coordinate-field", value.AssertingCoordinateFieldPath);
            writer.AddInt32($"{prefix}.link-kind", (int)value.LinkKind);
            writer.AddString($"{prefix}.method-id", value.MethodId);
            writer.AddString($"{prefix}.method-version", value.MethodVersion);
        }
    }

    private static void AddUnresolvedCrossSourceClaims(
        CanonicalIdentityWriter writer,
        ImmutableArray<UnresolvedCrossSourceClaimContent> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("unresolved-cross-source-claims.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"unresolved-cross-source-claims.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.asserting-revision", value.AssertingSourceRevisionId.Value);
            writer.AddExactNativeIdentifier($"{prefix}.asserting-subject", value.ExactAssertingSubject);
            writer.AddInt32($"{prefix}.kind", (int)value.AssertionKind);
            writer.AddString($"{prefix}.field", value.SourceFieldPath);
            switch (value.AssertionKind)
            {
                case CrossSourceCanonicalAssertionKind.Terminology:
                    writer.AddInt32($"{prefix}.role", (int)value.Terminology!.Role);
                    writer.AddString($"{prefix}.verbatim", value.Terminology.VerbatimValue);
                    writer.AddOptionalString($"{prefix}.language", value.Terminology.LanguageTag);
                    writer.AddOptionalExactNativeIdentifier($"{prefix}.native-string", value.Terminology.NativeStringIdentifier);
                    break;
                case CrossSourceCanonicalAssertionKind.Relationship:
                    writer.AddString($"{prefix}.semantic", value.Relationship!.SemanticId.Value);
                    writer.AddString($"{prefix}.native-type", value.Relationship.SourceNativeRelationshipType);
                    writer.AddExactNativeIdentifier($"{prefix}.native-target", value.Relationship.SourceNativeTarget);
                    break;
                case CrossSourceCanonicalAssertionKind.LocationNativeType:
                    writer.AddExactNativeIdentifier($"{prefix}.native-type", value.LocationNativeType!.ExactNativeType);
                    break;
                case CrossSourceCanonicalAssertionKind.LocationSemanticClassification:
                    writer.AddOptionalExactNativeIdentifier($"{prefix}.source-native-type", value.LocationSemanticClassification!.ExactSourceNativeType);
                    writer.AddString($"{prefix}.role", value.LocationSemanticClassification.RoleId.Value);
                    writer.AddInt32($"{prefix}.vocabulary", value.LocationSemanticClassification.VocabularyVersion.Value);
                    writer.AddString($"{prefix}.method", value.LocationSemanticClassification.ClassificationMethodId);
                    writer.AddString($"{prefix}.method-version", value.LocationSemanticClassification.ClassificationMethodVersion);
                    break;
                case CrossSourceCanonicalAssertionKind.RecordLifecycle:
                    writer.AddInt32($"{prefix}.state", (int)value.RecordLifecycle!.State);
                    writer.AddString($"{prefix}.native-type", value.RecordLifecycle.SourceNativeLifecycleType);
                    break;
                case CrossSourceCanonicalAssertionKind.SemanticClassification:
                    writer.AddString($"{prefix}.role", value.SemanticClassification!.RoleId.Value);
                    writer.AddString($"{prefix}.vocabulary", value.SemanticClassification.VocabularyId);
                    writer.AddString($"{prefix}.vocabulary-version", value.SemanticClassification.VocabularyVersion);
                    writer.AddString($"{prefix}.method", value.SemanticClassification.ClassificationMethodId);
                    writer.AddString($"{prefix}.method-version", value.SemanticClassification.ClassificationMethodVersion);
                    break;
                case CrossSourceCanonicalAssertionKind.RecordContribution:
                    writer.AddInt32($"{prefix}.contribution", (int)value.RecordContribution!.ContributionKind);
                    writer.AddOptionalExactNativeIdentifier($"{prefix}.origin", value.RecordContribution.OriginNativeIdentity);
                    writer.AddOptionalExactNativeIdentifier($"{prefix}.mod", value.RecordContribution.ExactModIdentity);
                    writer.AddOptionalNativeVersion($"{prefix}.mod-version", value.RecordContribution.ExactModVersion);
                    writer.AddString($"{prefix}.method", value.RecordContribution.MethodId);
                    writer.AddString($"{prefix}.method-version", value.RecordContribution.MethodVersion);
                    break;
                case CrossSourceCanonicalAssertionKind.OrganizationalValue:
                    writer.AddString($"{prefix}.dimension", value.OrganizationalValue!.DimensionId.Value);
                    writer.AddExactNativeIdentifier($"{prefix}.value", value.OrganizationalValue.ExactValueIdentity);
                    writer.AddOptionalString($"{prefix}.display", value.OrganizationalValue.VerbatimDisplayValue);
                    writer.AddString($"{prefix}.method", value.OrganizationalValue.MethodId);
                    writer.AddString($"{prefix}.method-version", value.OrganizationalValue.MethodVersion);
                    break;
                case CrossSourceCanonicalAssertionKind.InstructionApplicability:
                    writer.AddString($"{prefix}.instruction", value.InstructionApplicability!.InstructionAssertionId.Value);
                    break;
            }
        }
    }

    private static void AddUnresolvedCrossSourceBindings(
        CanonicalIdentityWriter writer,
        ImmutableArray<UnresolvedCrossSourceEvidenceBinding> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("unresolved-cross-source-bindings.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"unresolved-cross-source-bindings.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.receipt", value.EvidenceReceiptId.Value);
            writer.AddString($"{prefix}.revision", value.AssertingSourceRevisionId.Value);
            writer.AddString($"{prefix}.claim", value.ClaimContentId.Value);
            writer.AddOptionalString($"{prefix}.target-attempt", value.TargetAttemptId?.Value);
            writer.AddInt32($"{prefix}.kind", (int)value.AssertionKind);
            writer.AddString($"{prefix}.locator", value.ExactLocator);
            writer.AddInt32($"{prefix}.verification", (int)value.Verification);
        }
    }

    private static void AddUnresolvedCrossSourceAssertions(
        CanonicalIdentityWriter writer,
        ImmutableArray<UnresolvedCrossSourceAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("unresolved-cross-source-assertions.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"unresolved-cross-source-assertions.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.claim", value.ClaimContentId.Value);
            writer.AddString($"{prefix}.revision", value.AssertingSourceRevisionId.Value);
            writer.AddString($"{prefix}.adapter", value.AdapterRevisionId.Value);
            writer.AddInt32($"{prefix}.link-kind", (int)value.AttemptedLinkKind);
            writer.AddString($"{prefix}.method", value.AttemptedLinkMethodId);
            writer.AddString($"{prefix}.method-version", value.AttemptedLinkMethodVersion);
            writer.AddInt32($"{prefix}.outcome", (int)value.Outcome);
            writer.AddString($"{prefix}.reason", value.ReasonCode);
            AddSortedIds(writer, $"{prefix}.candidates", value.CandidateTargetIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.receipts", value.SupportingEvidenceReceiptIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.bindings", value.SupportingEvidenceBindingIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.correlations", value.CorrelationRecordIds.Select(item => item.Value));
        }
    }

    private static void AddLocationClassifications(
        CanonicalIdentityWriter writer,
        ImmutableArray<LocationSemanticClassificationAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("location-classifications.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"location-classifications.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.record", value.KnowledgeRecordId.Value);
            writer.AddString($"{prefix}.source-revision", value.SourceRevisionId.Value);
            writer.AddOptionalString($"{prefix}.native-type", value.SourceNativeTypeAssertionId?.Value);
            writer.AddString($"{prefix}.role", value.RoleId.Value);
            writer.AddInt32($"{prefix}.vocabulary-version", value.VocabularyVersion.Value);
            writer.AddString($"{prefix}.method-id", value.ClassificationMethodId);
            writer.AddString($"{prefix}.method-version", value.ClassificationMethodVersion);
            writer.AddString($"{prefix}.field", value.SourceFieldPath);
        }
    }

    private static void AddRecordLifecycleAssertions(
        CanonicalIdentityWriter writer,
        ImmutableArray<CanonicalRecordLifecycleAssertion> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("record-lifecycle.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"record-lifecycle.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.record", value.KnowledgeRecordId.Value);
            writer.AddString($"{prefix}.source-revision", value.SourceRevisionId.Value);
            writer.AddInt32($"{prefix}.state", (int)value.State);
            writer.AddString($"{prefix}.native-type", value.SourceNativeLifecycleType);
            writer.AddString($"{prefix}.field", value.SourceFieldPath);
        }
    }

    private static void AddCorrelatedRelationships(
        CanonicalIdentityWriter writer,
        ImmutableArray<CorrelatedRelationshipEnvelope> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("correlated-relationships.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            var prefix = $"correlated-relationships.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddString($"{prefix}.subject", value.SubjectKnowledgeRecordId.Value);
            writer.AddString($"{prefix}.semantic", value.SemanticId.Value);
            AddSortedIds(writer, $"{prefix}.candidate-targets", value.CandidateTargetKnowledgeRecordIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.input-claims", value.InputRelationshipClaimIds.Select(item => item.Value));
            AddSortedIds(writer, $"{prefix}.correlations", value.CorrelationRecordIds.Select(item => item.Value));
            writer.AddString($"{prefix}.method-id", value.MethodId);
            writer.AddString($"{prefix}.method-version", value.MethodVersion);
            writer.AddInt32($"{prefix}.outcome", (int)value.Outcome);
        }
    }

    private static void AddLocationCoverageReports(
        CanonicalIdentityWriter writer,
        ImmutableArray<LocationCoverageReport> values)
    {
        var ordered = values.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("location-coverage.count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var value = ordered[index];
            writer.AddString($"location-coverage.{index}.id", value.Id.Value);
            writer.AddString($"location-coverage.{index}.manifest-id", value.Manifest.Id.Value);
        }
    }

    private static void AddAcquisitionReceipts(
        CanonicalIdentityWriter writer,
        ImmutableArray<SourceAcquisitionReceipt> values)
    {
        writer.AddInt32("acquisition-receipts.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var prefix = $"acquisition-receipts.{index}";
            writer.AddString($"{prefix}.id", value.Id.Value);
            writer.AddInt32($"{prefix}.schema-version", value.ReceiptSchemaVersion);
            writer.AddString($"{prefix}.game-id", value.GameId.Value);
            writer.AddOptionalExactNativeIdentifier($"{prefix}.distribution-application", value.DistributionApplicationIdentity);
            AddOptionalExactNativeVersion(writer, $"{prefix}.distribution-build", value.DistributionBuildVersion);
            writer.AddExactNativeIdentifier($"{prefix}.container", value.ContainerCoordinate);
            writer.AddInt64($"{prefix}.container-byte-length", value.ContainerByteLength);
            writer.AddContentDigest($"{prefix}.container-digest", value.ContainerDigest);
            value.AcquisitionMethod.AddTo(writer, $"{prefix}.method");
            writer.AddInt32($"{prefix}.members.count", value.Members.Length);
            for (var memberIndex = 0; memberIndex < value.Members.Length; memberIndex++)
                value.Members[memberIndex].AddTo(writer, $"{prefix}.members.{memberIndex}");
        }
    }

    private static void AddArtifactAcquisitionBindings(
        CanonicalIdentityWriter writer,
        ImmutableArray<SourceArtifactAcquisitionBinding> values)
    {
        writer.AddInt32("artifact-acquisition-bindings.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var prefix = $"artifact-acquisition-bindings.{index}";
            writer.AddString($"{prefix}.artifact", value.ArtifactId.Value);
            writer.AddString($"{prefix}.receipt", value.AcquisitionReceiptId.Value);
            writer.AddExactNativeIdentifier($"{prefix}.member-coordinate", value.MemberCoordinate);
            writer.AddInt64($"{prefix}.member-byte-length", value.MemberByteLength);
            writer.AddContentDigest($"{prefix}.member-digest", value.MemberDigest);
        }
    }

    private static void AddAdapterDescriptors(
        CanonicalIdentityWriter writer,
        string prefix,
        ImmutableArray<GameKnowledgeAdapterDescriptor> values,
        bool includeAdapterArtifactDigest)
    {
        writer.AddInt32($"{prefix}.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            AddAdapterRevisionCoordinate(
                writer,
                $"{prefix}.{index}.revision",
                value.Revision,
                includeAdapterArtifactDigest);
            AddSortedIds(writer, $"{prefix}.{index}.games", value.SupportedGameIds.Select(item => item.Value));
            writer.AddInt32($"{prefix}.{index}.formats.count", value.SupportedFormats.Length);
            for (var formatIndex = 0; formatIndex < value.SupportedFormats.Length; formatIndex++)
            {
                var format = value.SupportedFormats[formatIndex];
                var formatPrefix = $"{prefix}.{index}.formats.{formatIndex}";
                writer.AddString($"{formatPrefix}.id", format.FormatId);
                writer.AddString($"{formatPrefix}.version", format.ExactFormatVersion);
                AddSortedIds(writer, $"{formatPrefix}.containers", format.ContainerKinds);
                AddSortedIds(writer, $"{formatPrefix}.objects", format.ResourceObjectTypes);
                AddSortedIds(writer, $"{formatPrefix}.knowledge-kinds", format.SupportedKnowledgeKinds.Select(item => ((int)item).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                writer.AddInt32($"{formatPrefix}.terminology", format.SupportsTerminology ? 1 : 0);
                writer.AddInt32($"{formatPrefix}.relationships", format.SupportsRelationships ? 1 : 0);
                writer.AddInt32($"{formatPrefix}.hierarchy", format.SupportsHierarchy ? 1 : 0);
            }
            writer.AddInt64($"{prefix}.{index}.limits.artifact-bytes", value.ResourceLimits.MaximumArtifactBytes);
            writer.AddInt32($"{prefix}.{index}.limits.artifacts", value.ResourceLimits.MaximumArtifacts);
            writer.AddInt32($"{prefix}.{index}.limits.records", value.ResourceLimits.MaximumKnowledgeRecords);
            writer.AddInt32($"{prefix}.{index}.limits.relationships", value.ResourceLimits.MaximumRelationships);
        }
    }

    private static void AddAdapterRevisionCoordinates(
        CanonicalIdentityWriter writer,
        string prefix,
        ImmutableArray<KnowledgeAdapterRevisionCoordinate> values)
    {
        writer.AddInt32($"{prefix}.count", values.Length);
        for (var index = 0; index < values.Length; index++)
            AddAdapterRevisionCoordinate(writer, $"{prefix}.{index}", values[index]);
    }

    private static void AddAdapterRevisionCoordinate(
        CanonicalIdentityWriter writer,
        string prefix,
        KnowledgeAdapterRevisionCoordinate value,
        bool includeAdapterArtifactDigest = true)
    {
        writer.AddString($"{prefix}.id", value.Id.Value);
        writer.AddString($"{prefix}.adapter-id", value.AdapterId.Value);
        writer.AddString($"{prefix}.adapter-version", value.ExactAdapterVersion);
        if (includeAdapterArtifactDigest)
            writer.AddContentDigest($"{prefix}.artifact-digest", value.AdapterArtifactDigest);
        writer.AddInt32($"{prefix}.contract-version", value.AdapterContractVersion);
        writer.AddString($"{prefix}.mapping-rules-version", value.MappingRulesVersion);
        // Preserve the schema-v1 byte stream exactly. V2 adds an explicit algorithm marker and
        // semantic-contract digest while retaining the artifact digest as package provenance.
        if (value.Id.AlgorithmVersion == KnowledgeAdapterRevisionId.CurrentAlgorithmVersion)
        {
            writer.AddInt32($"{prefix}.identity-algorithm-version", value.Id.AlgorithmVersion);
            writer.AddString(
                $"{prefix}.semantic-contract-digest",
                value.SemanticContractDigest?.Value ??
                throw new InvalidDataException("A v2 adapter coordinate requires its semantic-contract digest."));
        }
    }

    private static void AddSourceScope(
        CanonicalIdentityWriter writer,
        string prefix,
        KnowledgeSourceScope value)
    {
        writer.AddString($"{prefix}.game-id", value.GameId.Value);
        AddOptionalExactNativeVersion(writer, $"{prefix}.game-version", value.ExactGameVersion);
        writer.AddInt32($"{prefix}.kind", (int)value.ScopeKind);
        AddOptionalExactNativeIdentifier(writer, $"{prefix}.mod-identity", value.ExactModIdentity);
        AddOptionalExactNativeVersion(writer, $"{prefix}.mod-version", value.ExactModVersion);
    }

    private static void AddFormat(
        CanonicalIdentityWriter writer,
        string prefix,
        KnowledgeFormatCoordinate value)
    {
        writer.AddString($"{prefix}.id", value.FormatId);
        writer.AddString($"{prefix}.version", value.ExactFormatVersion);
    }

    private static void AddFileEvidence(
        CanonicalIdentityWriter writer,
        ImmutableArray<CatalogFileEvidenceReceipt> values)
    {
        writer.AddInt32("file-evidence.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var receipt = value.Receipt;
            writer.AddString($"file-evidence.{index}.id", value.Id.Value);
            writer.AddString($"file-evidence.{index}.source-revision", receipt.SourceRevisionId.Value);
            writer.AddString($"file-evidence.{index}.artifact", receipt.SourceArtifactId.Value);
            writer.AddContentDigest($"file-evidence.{index}.digest", receipt.ArtifactDigest);
            writer.AddString($"file-evidence.{index}.parser-id", receipt.ParserId);
            writer.AddString($"file-evidence.{index}.parser-version", receipt.ParserVersion);
            writer.AddString($"file-evidence.{index}.record", receipt.NativeRecordLocator);
            if (value.Id.AlgorithmVersion >= EvidenceReceiptId.CurrentAlgorithmVersion)
                writer.AddExactNativeIdentifier(
                    $"file-evidence.{index}.native-object",
                    receipt.NativeObjectIdentity ?? throw new InvalidDataException(
                        "A v2 FILE evidence receipt must retain its complete native object identity."));
            writer.AddString($"file-evidence.{index}.field", receipt.SourceFieldPath);
            writer.AddOptionalInt64($"file-evidence.{index}.offset", receipt.ByteOffset);
            writer.AddOptionalInt64($"file-evidence.{index}.length", receipt.ByteLength);
            writer.AddOptionalContentDigest($"file-evidence.{index}.interpreted", receipt.InterpretedBytesDigest);
        }
    }

    private static void AddReferenceEvidence(
        CanonicalIdentityWriter writer,
        ImmutableArray<CatalogReferenceEvidenceReceipt> values)
    {
        writer.AddInt32("reference-evidence.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            var receipt = value.Receipt;
            writer.AddString($"reference-evidence.{index}.id", value.Id.Value);
            writer.AddString($"reference-evidence.{index}.provider", receipt.ProviderCatalogSourceId.Value);
            writer.AddString($"reference-evidence.{index}.source-revision", receipt.SourceRevisionId.Value);
            writer.AddString($"reference-evidence.{index}.artifact", receipt.ResponseArtifactId.Value);
            writer.AddContentDigest($"reference-evidence.{index}.digest", receipt.ResponseContentDigest);
            AddOptionalExactNativeIdentifier(writer, $"reference-evidence.{index}.object", receipt.NativeObjectIdentity);
            AddOptionalExactNativeVersion(writer, $"reference-evidence.{index}.revision", receipt.NativeRevisionIdentity);
            writer.AddString($"reference-evidence.{index}.field", receipt.ResponseFieldPath);
        }
    }

    private static void AddBindings(CanonicalIdentityWriter writer, ImmutableArray<EvidenceBinding> values)
    {
        writer.AddInt32("bindings.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            writer.AddString($"bindings.{index}.id", value.Id.Value);
            writer.AddString($"bindings.{index}.receipt", value.EvidenceReceiptId.Value);
            writer.AddInt32($"bindings.{index}.kind", (int)value.ClaimKind);
            writer.AddString($"bindings.{index}.record", value.KnowledgeRecordId.Value);
            writer.AddString($"bindings.{index}.source-revision", value.SourceRevisionId.Value);
            writer.AddString($"bindings.{index}.locator", value.ClaimLocator);
            writer.AddOptionalString($"bindings.{index}.content", value.ClaimContentId?.Value);
        }
    }

    private static void AddCorrelations(
        CanonicalIdentityWriter writer,
        ImmutableArray<CanonicalCorrelationEnvelope> values)
    {
        writer.AddInt32("correlations.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            writer.AddString($"correlations.{index}.id", value.Id.Value);
            AddSortedIds(writer, $"correlations.{index}.members", value.Record.MemberIds.Select(item => item.Value));
            writer.AddString($"correlations.{index}.method-id", value.Record.MethodId);
            writer.AddString($"correlations.{index}.method-version", value.Record.MethodVersion);
            writer.AddInt32($"correlations.{index}.outcome", (int)value.Record.Outcome);
            AddSortedIds(
                writer,
                $"correlations.{index}.evidence",
                value.SupportingEvidenceReceiptIds.Select(item => item.Value));
        }
    }

    private static void AddUnresolved(
        CanonicalIdentityWriter writer,
        ImmutableArray<UnresolvedSourceAssertion> values)
    {
        writer.AddInt32("unresolved.count", values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index];
            writer.AddString($"unresolved.{index}.id", value.Id.Value);
            writer.AddString($"unresolved.{index}.game-id", value.GameId.Value);
            writer.AddString($"unresolved.{index}.source-revision", value.SourceRevisionId.Value);
            writer.AddString($"unresolved.{index}.adapter-revision", value.AdapterRevisionId.Value);
            writer.AddExactNativeIdentifier($"unresolved.{index}.native", value.NativeIdentity);
            writer.AddOptionalInt64($"unresolved.{index}.candidate-kind", value.CandidateKind is null ? null : (int)value.CandidateKind.Value);
            writer.AddString($"unresolved.{index}.reason", value.ReasonCode);
            AddSortedIds(
                writer,
                $"unresolved.{index}.evidence",
                value.SupportingEvidenceReceiptIds.Select(item => item.Value));
        }
    }

    private static void AddOptionalExactNativeIdentifier(
        CanonicalIdentityWriter writer,
        string prefix,
        SourceNativeIdentifier? value)
    {
        if (value is null)
        {
            writer.AddOptionalString(prefix, null);
            return;
        }
        writer.AddExactNativeIdentifier(prefix, value);
    }

    internal static void AddOptionalExactNativeVersion(
        CanonicalIdentityWriter writer,
        string prefix,
        SourceNativeVersion? value)
    {
        if (value is null)
        {
            writer.AddOptionalString(prefix, null);
            return;
        }
        writer.AddString($"{prefix}.namespace", value.Namespace);
        writer.AddString($"{prefix}.exact-representation", value.ExactRepresentation);
        writer.AddBytes($"{prefix}.identity-bytes", value.IdentityBytes.AsSpan());
        writer.AddString($"{prefix}.comparison-method-id", value.ComparisonMethodId);
        writer.AddInt32($"{prefix}.comparison-method-version", value.ComparisonMethodVersion);
    }
}
