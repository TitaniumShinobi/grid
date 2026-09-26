using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public static class LocationSemanticRoles
{
    public static LocationSemanticVocabularyVersion CurrentVocabularyVersion { get; } = new(1);

    public static LocationSemanticRoleId World { get; } = new("grid.location.role.world");
    public static LocationSemanticRoleId Region { get; } = new("grid.location.role.region");
    public static LocationSemanticRoleId AdministrativeArea { get; } = new("grid.location.role.administrative-area");
    public static LocationSemanticRoleId Settlement { get; } = new("grid.location.role.settlement");
    public static LocationSemanticRoleId District { get; } = new("grid.location.role.district");
    public static LocationSemanticRoleId AreaZone { get; } = new("grid.location.role.area-zone");
    public static LocationSemanticRoleId Route { get; } = new("grid.location.role.route");
    public static LocationSemanticRoleId Landmark { get; } = new("grid.location.role.landmark");
    public static LocationSemanticRoleId Business { get; } = new("grid.location.role.business");
    public static LocationSemanticRoleId Property { get; } = new("grid.location.role.property");
    public static LocationSemanticRoleId Residence { get; } = new("grid.location.role.residence");
    public static LocationSemanticRoleId Facility { get; } = new("grid.location.role.facility");
    public static LocationSemanticRoleId Building { get; } = new("grid.location.role.building");
    public static LocationSemanticRoleId ExteriorPlace { get; } = new("grid.location.role.exterior-place");
    public static LocationSemanticRoleId Interior { get; } = new("grid.location.role.interior");
    public static LocationSemanticRoleId Room { get; } = new("grid.location.role.room");
    public static LocationSemanticRoleId Dungeon { get; } = new("grid.location.role.dungeon");
    public static LocationSemanticRoleId Cave { get; } = new("grid.location.role.cave");
    public static LocationSemanticRoleId Instance { get; } = new("grid.location.role.instance");
    public static LocationSemanticRoleId MissionPlace { get; } = new("grid.location.role.mission-place");
    public static LocationSemanticRoleId TransitNode { get; } = new("grid.location.role.transit-node");
}

public static class LocationRelationshipSemantics
{
    public const int CurrentVocabularyVersion = 1;

    public static RelationshipSemanticId ContainedBy { get; } = new("grid.location.contained-by");
    public static RelationshipSemanticId InteriorOf { get; } = new("grid.location.interior-of");
    public static RelationshipSemanticId InstanceOf { get; } = new("grid.location.instance-of");
    public static RelationshipSemanticId SpatialMemberOf { get; } = new("grid.location.spatial-member-of");
    public static RelationshipSemanticId EntranceTo { get; } = new("grid.location.entrance-to");
    public static RelationshipSemanticId ExitTo { get; } = new("grid.location.exit-to");
    public static RelationshipSemanticId ConnectsTo { get; } = new("grid.location.connects-to");

    public static bool IsStrictHierarchy(RelationshipSemanticId semanticId) =>
        semanticId == ContainedBy || semanticId == InteriorOf || semanticId == InstanceOf;
}

public sealed record SourceNativeLocationTypeAssertion
{
    [JsonConstructor]
    public SourceNativeLocationTypeAssertion(
        SourceNativeLocationTypeAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeIdentifier exactNativeType,
        string sourceFieldPath)
    {
        ArgumentNullException.ThrowIfNull(exactNativeType);
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        var expected = SourceNativeLocationTypeAssertionId.DeriveV1(
            knowledgeRecordId,
            sourceRevisionId,
            exactNativeType,
            sourceFieldPath);
        if (id != expected)
            throw new ArgumentException("Native Location type assertion identity does not match its exact claim.", nameof(id));

        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        ExactNativeType = exactNativeType;
        SourceFieldPath = sourceFieldPath;
    }

    public SourceNativeLocationTypeAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public SourceNativeIdentifier ExactNativeType { get; }
    public string SourceFieldPath { get; }
}

public sealed record LocationSemanticClassificationAssertion
{
    [JsonConstructor]
    public LocationSemanticClassificationAssertion(
        LocationSemanticClassificationAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeLocationTypeAssertionId? sourceNativeTypeAssertionId,
        LocationSemanticRoleId roleId,
        LocationSemanticVocabularyVersion vocabularyVersion,
        string classificationMethodId,
        string classificationMethodVersion,
        string sourceFieldPath)
    {
        classificationMethodId = CanonicalKnowledgeContract.RequireText(classificationMethodId, nameof(classificationMethodId));
        classificationMethodVersion = CanonicalKnowledgeContract.RequireText(
            classificationMethodVersion,
            nameof(classificationMethodVersion));
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        var expected = LocationSemanticClassificationAssertionId.DeriveV1(
            knowledgeRecordId,
            sourceRevisionId,
            sourceNativeTypeAssertionId,
            roleId,
            vocabularyVersion,
            classificationMethodId,
            classificationMethodVersion,
            sourceFieldPath);
        if (id != expected)
            throw new ArgumentException("Location semantic classification identity does not match its exact claim.", nameof(id));

        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        SourceNativeTypeAssertionId = sourceNativeTypeAssertionId;
        RoleId = roleId;
        VocabularyVersion = vocabularyVersion;
        ClassificationMethodId = classificationMethodId;
        ClassificationMethodVersion = classificationMethodVersion;
        SourceFieldPath = sourceFieldPath;
    }

    public LocationSemanticClassificationAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public SourceNativeLocationTypeAssertionId? SourceNativeTypeAssertionId { get; }
    public LocationSemanticRoleId RoleId { get; }
    public LocationSemanticVocabularyVersion VocabularyVersion { get; }
    public string ClassificationMethodId { get; }
    public string ClassificationMethodVersion { get; }
    public string SourceFieldPath { get; }
}

public enum CanonicalRecordLifecycleState
{
    SourceAssertedPresent = 0,
    SourceAssertedModified = 1,
    SourceAssertedDeleted = 2,
    SourceAssertedRemoved = 3,
}

public sealed record CanonicalRecordLifecycleAssertion
{
    [JsonConstructor]
    public CanonicalRecordLifecycleAssertion(
        CanonicalRecordLifecycleAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalRecordLifecycleState state,
        string sourceNativeLifecycleType,
        string sourceFieldPath)
    {
        sourceNativeLifecycleType = CanonicalKnowledgeContract.RequireText(
            sourceNativeLifecycleType,
            nameof(sourceNativeLifecycleType));
        sourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        var expected = CanonicalRecordLifecycleAssertionId.DeriveV1(
            knowledgeRecordId,
            sourceRevisionId,
            state,
            sourceNativeLifecycleType,
            sourceFieldPath);
        if (id != expected)
            throw new ArgumentException("Record lifecycle assertion identity does not match its exact claim.", nameof(id));

        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        State = state;
        SourceNativeLifecycleType = sourceNativeLifecycleType;
        SourceFieldPath = sourceFieldPath;
    }

    public CanonicalRecordLifecycleAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public CanonicalRecordLifecycleState State { get; }
    public string SourceNativeLifecycleType { get; }
    public string SourceFieldPath { get; }
}

public sealed record CorrelatedRelationshipEnvelope
{
    [JsonConstructor]
    public CorrelatedRelationshipEnvelope(
        CorrelatedRelationshipEnvelopeId id,
        KnowledgeRecordId subjectKnowledgeRecordId,
        RelationshipSemanticId semanticId,
        ImmutableArray<KnowledgeRecordId> candidateTargetKnowledgeRecordIds,
        ImmutableArray<EvidenceClaimContentId> inputRelationshipClaimIds,
        ImmutableArray<CorrelationRecordId> correlationRecordIds,
        string methodId,
        string methodVersion,
        CorrelationOutcome outcome)
    {
        if (candidateTargetKnowledgeRecordIds.IsDefault || inputRelationshipClaimIds.IsDefault || correlationRecordIds.IsDefault)
            throw new ArgumentException("Correlated relationship collections must be initialized.");
        if (!Enum.IsDefined(outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        var targets = NormalizeIds(candidateTargetKnowledgeRecordIds, value => value.Value, nameof(candidateTargetKnowledgeRecordIds));
        var claims = NormalizeIds(inputRelationshipClaimIds, value => value.Value, nameof(inputRelationshipClaimIds));
        var correlations = NormalizeIds(correlationRecordIds, value => value.Value, nameof(correlationRecordIds));
        if (claims.IsEmpty && correlations.IsEmpty)
            throw new ArgumentException("A correlated relationship requires independently identifiable inputs.");
        if (outcome == CorrelationOutcome.Correlated && targets.Length != 1)
            throw new ArgumentException("A correlated relationship requires exactly one proven target.", nameof(candidateTargetKnowledgeRecordIds));
        if (outcome == CorrelationOutcome.Ambiguous && targets.Length < 2)
            throw new ArgumentException("An ambiguous relationship requires at least two candidate targets.", nameof(candidateTargetKnowledgeRecordIds));

        methodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        methodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        var expected = CorrelatedRelationshipEnvelopeId.DeriveV1(
            subjectKnowledgeRecordId,
            semanticId,
            targets,
            claims,
            correlations,
            methodId,
            methodVersion,
            outcome);
        if (id != expected)
            throw new ArgumentException("Correlated relationship identity does not match its exact inputs.", nameof(id));

        Id = id;
        SubjectKnowledgeRecordId = subjectKnowledgeRecordId;
        SemanticId = semanticId;
        CandidateTargetKnowledgeRecordIds = targets;
        InputRelationshipClaimIds = claims;
        CorrelationRecordIds = correlations;
        MethodId = methodId;
        MethodVersion = methodVersion;
        Outcome = outcome;
    }

    public CorrelatedRelationshipEnvelopeId Id { get; }
    public KnowledgeRecordId SubjectKnowledgeRecordId { get; }
    public RelationshipSemanticId SemanticId { get; }
    public ImmutableArray<KnowledgeRecordId> CandidateTargetKnowledgeRecordIds { get; }
    public ImmutableArray<EvidenceClaimContentId> InputRelationshipClaimIds { get; }
    public ImmutableArray<CorrelationRecordId> CorrelationRecordIds { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public CorrelationOutcome Outcome { get; }

    private static ImmutableArray<T> NormalizeIds<T>(ImmutableArray<T> values, Func<T, string> identity, string parameterName)
    {
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Any(value => string.IsNullOrWhiteSpace(identity(value))) ||
            ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Canonical identities must be non-empty and distinct.", parameterName);
        return ordered;
    }
}

public enum LocationCoverageStatus
{
    Unsupported = 0,
    Unresolved = 1,
    Partial = 2,
    CompleteForRegisteredSources = 3,
}

public sealed record LocationSourceFamilyDeclaration
{
    public LocationSourceFamilyDeclaration(
        LocationSourceFamilyId sourceFamilyId,
        KnowledgeFormatCoordinate format,
        KnowledgeAdapterRevisionId adapterRevisionId,
        bool isApplicable,
        ImmutableArray<SourceArtifactId> expectedArtifactIds,
        ImmutableArray<CatalogSourceRevisionId> expectedSourceRevisionIds,
        ImmutableArray<LocationSemanticRoleId> expectedSemanticRoles)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceFamilyId.Value, nameof(sourceFamilyId));
        ArgumentNullException.ThrowIfNull(format);
        CanonicalKnowledgeContract.RequireIdentifier(adapterRevisionId.Value, nameof(adapterRevisionId));
        SourceFamilyId = sourceFamilyId;
        Format = format;
        AdapterRevisionId = adapterRevisionId;
        IsApplicable = isApplicable;
        ExpectedArtifactIds = NormalizeIds(expectedArtifactIds, value => value.Value, nameof(expectedArtifactIds));
        ExpectedSourceRevisionIds = NormalizeIds(
            expectedSourceRevisionIds,
            value => value.Value,
            nameof(expectedSourceRevisionIds));
        ExpectedSemanticRoles = NormalizeIds(expectedSemanticRoles, value => value.Value, nameof(expectedSemanticRoles));
    }

    public LocationSourceFamilyId SourceFamilyId { get; }
    public KnowledgeFormatCoordinate Format { get; }
    public KnowledgeAdapterRevisionId AdapterRevisionId { get; }
    public bool IsApplicable { get; }
    public ImmutableArray<SourceArtifactId> ExpectedArtifactIds { get; }
    public ImmutableArray<CatalogSourceRevisionId> ExpectedSourceRevisionIds { get; }
    public ImmutableArray<LocationSemanticRoleId> ExpectedSemanticRoles { get; }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.source-family-id", SourceFamilyId.Value);
        writer.AddString($"{prefix}.format-id", Format.FormatId);
        writer.AddString($"{prefix}.format-version", Format.ExactFormatVersion);
        writer.AddString($"{prefix}.adapter-revision-id", AdapterRevisionId.Value);
        writer.AddInt32($"{prefix}.is-applicable", IsApplicable ? 1 : 0);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.expected-artifacts",
            ExpectedArtifactIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.expected-source-revisions",
            ExpectedSourceRevisionIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.expected-semantic-roles",
            ExpectedSemanticRoles.Select(value => value.Value));
    }

    private static ImmutableArray<T> NormalizeIds<T>(ImmutableArray<T> values, Func<T, string> identity, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", name);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Any(value => string.IsNullOrWhiteSpace(identity(value))) ||
            ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection identities must be non-empty and distinct.", name);
        return ordered;
    }
}

public sealed record LocationCoverageManifest
{
    [JsonConstructor]
    public LocationCoverageManifest(
        LocationCoverageManifestId id,
        KnowledgeSourceScope sourceScope,
        string exactManifestVersion,
        bool isClosed,
        CatalogValidationSummary qcsValidation,
        ImmutableArray<LocationSourceFamilyDeclaration> sourceFamilies)
    {
        ArgumentNullException.ThrowIfNull(sourceScope);
        ArgumentNullException.ThrowIfNull(qcsValidation);
        exactManifestVersion = CanonicalKnowledgeContract.RequireText(exactManifestVersion, nameof(exactManifestVersion));
        if (sourceFamilies.IsDefault || sourceFamilies.Any(value => value is null))
            throw new ArgumentException("Location source families must be initialized and non-null.", nameof(sourceFamilies));
        var ordered = sourceFamilies.OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(value => value.SourceFamilyId).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Location source family identities must be distinct.", nameof(sourceFamilies));
        if (isClosed && qcsValidation.Status != CatalogValidationStatus.Passed)
            throw new ArgumentException("A closed Location coverage manifest requires passed QCS validation.", nameof(qcsValidation));
        if (isClosed && ordered.Any(value => value.IsApplicable &&
                (value.ExpectedArtifactIds.IsEmpty || value.ExpectedSourceRevisionIds.IsEmpty)))
            throw new ArgumentException(
                "A closed manifest requires exact artifact and source-revision closure for every applicable family.",
                nameof(sourceFamilies));

        var expected = LocationCoverageManifestId.DeriveV1(
            sourceScope,
            exactManifestVersion,
            isClosed,
            qcsValidation,
            ordered);
        if (id != expected)
            throw new ArgumentException("Location coverage manifest identity does not match its exact contents.", nameof(id));

        Id = id;
        SourceScope = sourceScope;
        ExactManifestVersion = exactManifestVersion;
        IsClosed = isClosed;
        QcsValidation = qcsValidation;
        SourceFamilies = ordered;
    }

    public LocationCoverageManifestId Id { get; }
    public KnowledgeSourceScope SourceScope { get; }
    public string ExactManifestVersion { get; }
    public bool IsClosed { get; }
    public CatalogValidationSummary QcsValidation { get; }
    public ImmutableArray<LocationSourceFamilyDeclaration> SourceFamilies { get; }
}

public sealed record LocationCoverageExclusion
{
    [JsonConstructor]
    public LocationCoverageExclusion(
        LocationCoverageExclusionId id,
        CatalogSourceRevisionId sourceRevisionId,
        SourceNativeIdentifier nativeIdentity,
        string reasonCode,
        string exclusionRuleId,
        string exclusionRuleVersion,
        string sourceFieldPath,
        ImmutableArray<EvidenceReceiptId> supportingEvidenceReceiptIds)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        ReasonCode = CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode));
        ExclusionRuleId = CanonicalKnowledgeContract.RequireText(exclusionRuleId, nameof(exclusionRuleId));
        ExclusionRuleVersion = CanonicalKnowledgeContract.RequireText(exclusionRuleVersion, nameof(exclusionRuleVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        if (supportingEvidenceReceiptIds.IsDefaultOrEmpty)
            throw new ArgumentException("A coverage exclusion requires authoritative evidence.", nameof(supportingEvidenceReceiptIds));
        var evidence = supportingEvidenceReceiptIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (evidence.Any(value => string.IsNullOrWhiteSpace(value.Value)) || evidence.Distinct().Count() != evidence.Length)
            throw new ArgumentException("Coverage exclusion evidence identities must be non-empty and distinct.", nameof(supportingEvidenceReceiptIds));
        var expected = LocationCoverageExclusionId.DeriveV1(
            sourceRevisionId,
            nativeIdentity,
            ReasonCode,
            ExclusionRuleId,
            ExclusionRuleVersion,
            SourceFieldPath,
            evidence);
        if (id != expected)
            throw new ArgumentException("Location coverage exclusion identity does not match its exact claim.", nameof(id));
        Id = id;
        SourceRevisionId = sourceRevisionId;
        NativeIdentity = nativeIdentity;
        SupportingEvidenceReceiptIds = evidence;
    }

    public LocationCoverageExclusionId Id { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public SourceNativeIdentifier NativeIdentity { get; }
    public string ReasonCode { get; }
    public string ExclusionRuleId { get; }
    public string ExclusionRuleVersion { get; }
    public string SourceFieldPath { get; }
    public ImmutableArray<EvidenceReceiptId> SupportingEvidenceReceiptIds { get; }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.id", Id.Value);
        writer.AddString($"{prefix}.source-revision-id", SourceRevisionId.Value);
        writer.AddExactNativeIdentifier($"{prefix}.native-identity", NativeIdentity);
        writer.AddString($"{prefix}.reason-code", ReasonCode);
        writer.AddString($"{prefix}.exclusion-rule-id", ExclusionRuleId);
        writer.AddString($"{prefix}.exclusion-rule-version", ExclusionRuleVersion);
        writer.AddString($"{prefix}.source-field-path", SourceFieldPath);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.supporting-evidence",
            SupportingEvidenceReceiptIds.Select(value => value.Value));
    }

    internal string GetDeterministicIdentity() => Id.Value;
}

public sealed record LocationSourceFamilyCoverage
{
    public LocationSourceFamilyCoverage(
        LocationSourceFamilyId sourceFamilyId,
        ImmutableArray<SourceArtifactId> artifactIds,
        ImmutableArray<CatalogSourceRevisionId> sourceRevisionIds,
        int discoveredObjectCount,
        int acquiredObjectCount,
        int parsedObjectCount,
        ImmutableArray<KnowledgeRecordId> emittedLocationRecordIds,
        ImmutableArray<UnresolvedSourceAssertionId> unresolvedSourceAssertionIds,
        ImmutableArray<LocationCoverageExclusion> evidenceBackedExclusions,
        int unsupportedObjectCount,
        int parserErrorCount,
        int missingArtifactCount,
        int ambiguousClassificationCount)
    {
        CanonicalKnowledgeContract.RequireIdentifier(sourceFamilyId.Value, nameof(sourceFamilyId));
        ValidateCount(discoveredObjectCount, nameof(discoveredObjectCount));
        ValidateCount(acquiredObjectCount, nameof(acquiredObjectCount));
        ValidateCount(parsedObjectCount, nameof(parsedObjectCount));
        ValidateCount(unsupportedObjectCount, nameof(unsupportedObjectCount));
        ValidateCount(parserErrorCount, nameof(parserErrorCount));
        ValidateCount(missingArtifactCount, nameof(missingArtifactCount));
        ValidateCount(ambiguousClassificationCount, nameof(ambiguousClassificationCount));
        if (evidenceBackedExclusions.IsDefault || evidenceBackedExclusions.Any(value => value is null))
            throw new ArgumentException("Coverage exclusions must be initialized and non-null.", nameof(evidenceBackedExclusions));

        SourceFamilyId = sourceFamilyId;
        ArtifactIds = NormalizeIds(artifactIds, value => value.Value, nameof(artifactIds));
        SourceRevisionIds = NormalizeIds(sourceRevisionIds, value => value.Value, nameof(sourceRevisionIds));
        DiscoveredObjectCount = discoveredObjectCount;
        AcquiredObjectCount = acquiredObjectCount;
        ParsedObjectCount = parsedObjectCount;
        EmittedLocationRecordIds = NormalizeIds(
            emittedLocationRecordIds,
            value => value.Value,
            nameof(emittedLocationRecordIds));
        UnresolvedSourceAssertionIds = NormalizeIds(
            unresolvedSourceAssertionIds,
            value => value.Value,
            nameof(unresolvedSourceAssertionIds));
        EvidenceBackedExclusions = evidenceBackedExclusions
            .OrderBy(value => value.GetDeterministicIdentity(), StringComparer.Ordinal)
            .ToImmutableArray();
        if (EvidenceBackedExclusions.Select(value => value.GetDeterministicIdentity())
            .Distinct(StringComparer.Ordinal).Count() != EvidenceBackedExclusions.Length)
            throw new ArgumentException("Evidence-backed coverage exclusions must be distinct.", nameof(evidenceBackedExclusions));
        UnsupportedObjectCount = unsupportedObjectCount;
        ParserErrorCount = parserErrorCount;
        MissingArtifactCount = missingArtifactCount;
        AmbiguousClassificationCount = ambiguousClassificationCount;
    }

    public LocationSourceFamilyId SourceFamilyId { get; }
    public ImmutableArray<SourceArtifactId> ArtifactIds { get; }
    public ImmutableArray<CatalogSourceRevisionId> SourceRevisionIds { get; }
    public int DiscoveredObjectCount { get; }
    public int AcquiredObjectCount { get; }
    public int ParsedObjectCount { get; }
    public ImmutableArray<KnowledgeRecordId> EmittedLocationRecordIds { get; }
    public ImmutableArray<UnresolvedSourceAssertionId> UnresolvedSourceAssertionIds { get; }
    public ImmutableArray<LocationCoverageExclusion> EvidenceBackedExclusions { get; }
    public int UnsupportedObjectCount { get; }
    public int ParserErrorCount { get; }
    public int MissingArtifactCount { get; }
    public int AmbiguousClassificationCount { get; }

    public bool IsMechanicallyClosed(LocationSourceFamilyDeclaration declaration) =>
        declaration.IsApplicable &&
        ArtifactIds.SequenceEqual(declaration.ExpectedArtifactIds) &&
        SourceRevisionIds.SequenceEqual(declaration.ExpectedSourceRevisionIds) &&
        DiscoveredObjectCount == AcquiredObjectCount &&
        AcquiredObjectCount == ParsedObjectCount &&
        ParsedObjectCount == EmittedLocationRecordIds.Length +
            UnresolvedSourceAssertionIds.Length +
            EvidenceBackedExclusions.Length &&
        UnsupportedObjectCount == 0 &&
        ParserErrorCount == 0 &&
        MissingArtifactCount == 0 &&
        AmbiguousClassificationCount == 0;

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.source-family-id", SourceFamilyId.Value);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, $"{prefix}.artifacts", ArtifactIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.source-revisions",
            SourceRevisionIds.Select(value => value.Value));
        writer.AddInt32($"{prefix}.discovered", DiscoveredObjectCount);
        writer.AddInt32($"{prefix}.acquired", AcquiredObjectCount);
        writer.AddInt32($"{prefix}.parsed", ParsedObjectCount);
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.emitted-records",
            EmittedLocationRecordIds.Select(value => value.Value));
        CanonicalKnowledgePackageEncoding.AddSortedIds(
            writer,
            $"{prefix}.unresolved-assertions",
            UnresolvedSourceAssertionIds.Select(value => value.Value));
        writer.AddInt32($"{prefix}.exclusion-count", EvidenceBackedExclusions.Length);
        for (var index = 0; index < EvidenceBackedExclusions.Length; index++)
            EvidenceBackedExclusions[index].AddTo(writer, $"{prefix}.exclusion.{index}");
        writer.AddInt32($"{prefix}.unsupported", UnsupportedObjectCount);
        writer.AddInt32($"{prefix}.parser-errors", ParserErrorCount);
        writer.AddInt32($"{prefix}.missing-artifacts", MissingArtifactCount);
        writer.AddInt32($"{prefix}.ambiguous-classifications", AmbiguousClassificationCount);
    }

    private static ImmutableArray<T> NormalizeIds<T>(ImmutableArray<T> values, Func<T, string> identity, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", name);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Any(value => string.IsNullOrWhiteSpace(identity(value))) ||
            ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection identities must be non-empty and distinct.", name);
        return ordered;
    }

    private static void ValidateCount(int value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record LocationSemanticCategoryCoverage
{
    public LocationSemanticCategoryCoverage(
        SourceNativeIdentifier? exactNativeType,
        LocationSemanticRoleId? semanticRoleId,
        ImmutableArray<KnowledgeRecordId> recordIds,
        int unclassifiedCandidateCount)
    {
        if (semanticRoleId is { } role) CanonicalKnowledgeContract.RequireIdentifier(role.Value, nameof(semanticRoleId));
        if (unclassifiedCandidateCount < 0) throw new ArgumentOutOfRangeException(nameof(unclassifiedCandidateCount));
        RecordIds = NormalizeIds(recordIds, value => value.Value, nameof(recordIds));
        if (exactNativeType is null && semanticRoleId is null &&
            (RecordIds.IsEmpty || unclassifiedCandidateCount != RecordIds.Length))
            throw new ArgumentException(
                "An explicit unclassified category requires at least one record and counts every listed record as unclassified.");
        ExactNativeType = exactNativeType;
        SemanticRoleId = semanticRoleId;
        UnclassifiedCandidateCount = unclassifiedCandidateCount;
    }

    public SourceNativeIdentifier? ExactNativeType { get; }
    public LocationSemanticRoleId? SemanticRoleId { get; }
    public ImmutableArray<KnowledgeRecordId> RecordIds { get; }
    public int UnclassifiedCandidateCount { get; }
    public bool IsExplicitlyUnclassified => ExactNativeType is null && SemanticRoleId is null;

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddOptionalExactNativeIdentifier($"{prefix}.native-type", ExactNativeType);
        writer.AddOptionalString($"{prefix}.semantic-role", SemanticRoleId?.Value);
        CanonicalKnowledgePackageEncoding.AddSortedIds(writer, $"{prefix}.records", RecordIds.Select(value => value.Value));
        writer.AddInt32($"{prefix}.unclassified-candidates", UnclassifiedCandidateCount);
    }

    private static ImmutableArray<T> NormalizeIds<T>(ImmutableArray<T> values, Func<T, string> identity, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", name);
        var ordered = values.OrderBy(identity, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Select(identity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Collection identities must be distinct.", name);
        return ordered;
    }
}

public sealed record LocationTerminologyCoverage(
    int TotalRecordCount,
    int PrimaryNamedRecordCount,
    int AliasAssertionCount,
    int IdentifierOnlyRecordCount,
    int ConflictingRecordCount)
{
    public LocationTerminologyCoverage Validate()
    {
        ValidateCount(TotalRecordCount, nameof(TotalRecordCount));
        ValidateCount(PrimaryNamedRecordCount, nameof(PrimaryNamedRecordCount));
        ValidateCount(AliasAssertionCount, nameof(AliasAssertionCount));
        ValidateCount(IdentifierOnlyRecordCount, nameof(IdentifierOnlyRecordCount));
        ValidateCount(ConflictingRecordCount, nameof(ConflictingRecordCount));
        if (PrimaryNamedRecordCount + IdentifierOnlyRecordCount != TotalRecordCount)
            throw new ArgumentException("Every Location must be counted as primary-named or identifier-only.");
        if (ConflictingRecordCount > TotalRecordCount)
            throw new ArgumentException("Terminology conflicts cannot exceed total Location records.");
        return this;
    }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        Validate();
        writer.AddInt32($"{prefix}.total-records", TotalRecordCount);
        writer.AddInt32($"{prefix}.primary-named-records", PrimaryNamedRecordCount);
        writer.AddInt32($"{prefix}.aliases", AliasAssertionCount);
        writer.AddInt32($"{prefix}.identifier-only-records", IdentifierOnlyRecordCount);
        writer.AddInt32($"{prefix}.conflicting-records", ConflictingRecordCount);
    }

    private static void ValidateCount(int value, string name)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(name);
    }
}

public sealed record LocationHierarchyCoverage(
    int SourceProvidedEdgeCount,
    int ResolvedTargetCount,
    int UnresolvedTargetCount,
    int ConflictCount,
    bool NotProvidedBySource)
{
    public LocationHierarchyCoverage Validate()
    {
        if (SourceProvidedEdgeCount < 0 || ResolvedTargetCount < 0 || UnresolvedTargetCount < 0 || ConflictCount < 0)
            throw new ArgumentOutOfRangeException(nameof(SourceProvidedEdgeCount));
        if (ResolvedTargetCount + UnresolvedTargetCount != SourceProvidedEdgeCount)
            throw new ArgumentException("Every source-provided hierarchy edge must have a resolved or unresolved target.");
        // Conflicts may come from a separately identified correlation envelope even
        // when the source itself provides no hierarchy edge.
        if (NotProvidedBySource && SourceProvidedEdgeCount != 0)
            throw new ArgumentException("A source cannot both omit hierarchy and provide hierarchy edges.");
        return this;
    }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        Validate();
        writer.AddInt32($"{prefix}.source-provided-edges", SourceProvidedEdgeCount);
        writer.AddInt32($"{prefix}.resolved-targets", ResolvedTargetCount);
        writer.AddInt32($"{prefix}.unresolved-targets", UnresolvedTargetCount);
        writer.AddInt32($"{prefix}.conflicts", ConflictCount);
        writer.AddInt32($"{prefix}.not-provided", NotProvidedBySource ? 1 : 0);
    }
}

public sealed record LocationRelationshipCoverage
{
    public LocationRelationshipCoverage(
        RelationshipSemanticId semanticId,
        int assertionCount,
        int resolvedTargetCount,
        int unresolvedTargetCount,
        int fileVerifiedCount,
        int referenceVerifiedCount,
        int conflictCount)
    {
        CanonicalKnowledgeContract.RequireIdentifier(semanticId.Value, nameof(semanticId));
        if (assertionCount < 0 || resolvedTargetCount < 0 || unresolvedTargetCount < 0 ||
            fileVerifiedCount < 0 || referenceVerifiedCount < 0 || conflictCount < 0)
            throw new ArgumentOutOfRangeException(nameof(assertionCount));
        if (resolvedTargetCount + unresolvedTargetCount != assertionCount)
            throw new ArgumentException("Every relationship assertion must have a resolved or unresolved target.");
        if (fileVerifiedCount > assertionCount || referenceVerifiedCount > assertionCount || conflictCount > assertionCount)
            throw new ArgumentException("Relationship coverage subsets cannot exceed assertion count.");
        SemanticId = semanticId;
        AssertionCount = assertionCount;
        ResolvedTargetCount = resolvedTargetCount;
        UnresolvedTargetCount = unresolvedTargetCount;
        FileVerifiedCount = fileVerifiedCount;
        ReferenceVerifiedCount = referenceVerifiedCount;
        ConflictCount = conflictCount;
    }

    public RelationshipSemanticId SemanticId { get; }
    public int AssertionCount { get; }
    public int ResolvedTargetCount { get; }
    public int UnresolvedTargetCount { get; }
    public int FileVerifiedCount { get; }
    public int ReferenceVerifiedCount { get; }
    public int ConflictCount { get; }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.semantic-id", SemanticId.Value);
        writer.AddInt32($"{prefix}.assertions", AssertionCount);
        writer.AddInt32($"{prefix}.resolved", ResolvedTargetCount);
        writer.AddInt32($"{prefix}.unresolved", UnresolvedTargetCount);
        writer.AddInt32($"{prefix}.file-verified", FileVerifiedCount);
        writer.AddInt32($"{prefix}.reference-verified", ReferenceVerifiedCount);
        writer.AddInt32($"{prefix}.conflicts", ConflictCount);
    }
}

public sealed record LocationUnresolvedCoverage
{
    public LocationUnresolvedCoverage(
        string reasonCode,
        KnowledgeKind? candidateKind,
        LocationSourceFamilyId sourceFamilyId,
        CatalogSourceRevisionId sourceRevisionId,
        int count)
    {
        ReasonCode = CanonicalKnowledgeContract.RequireText(reasonCode, nameof(reasonCode));
        if (candidateKind is { } kind && !Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(candidateKind));
        CanonicalKnowledgeContract.RequireIdentifier(sourceFamilyId.Value, nameof(sourceFamilyId));
        CanonicalKnowledgeContract.RequireIdentifier(sourceRevisionId.Value, nameof(sourceRevisionId));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        CandidateKind = candidateKind;
        SourceFamilyId = sourceFamilyId;
        SourceRevisionId = sourceRevisionId;
        Count = count;
    }

    public string ReasonCode { get; }
    public KnowledgeKind? CandidateKind { get; }
    public LocationSourceFamilyId SourceFamilyId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public int Count { get; }

    internal void AddTo(CanonicalIdentityWriter writer, string prefix)
    {
        writer.AddString($"{prefix}.reason-code", ReasonCode);
        writer.AddOptionalInt64($"{prefix}.candidate-kind", CandidateKind is null ? null : (int)CandidateKind.Value);
        writer.AddString($"{prefix}.source-family-id", SourceFamilyId.Value);
        writer.AddString($"{prefix}.source-revision-id", SourceRevisionId.Value);
        writer.AddInt32($"{prefix}.count", Count);
    }
}

public sealed record LocationCoverageReport
{
    [JsonConstructor]
    public LocationCoverageReport(
        LocationCoverageReportId id,
        LocationCoverageManifest manifest,
        LocationCoverageStatus status,
        ImmutableArray<LocationSourceFamilyCoverage> sourceFamilies,
        ImmutableArray<LocationSemanticCategoryCoverage> semanticCategories,
        LocationTerminologyCoverage terminology,
        LocationHierarchyCoverage hierarchy,
        ImmutableArray<LocationRelationshipCoverage> relationships,
        ImmutableArray<LocationUnresolvedCoverage> unresolved)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(terminology);
        ArgumentNullException.ThrowIfNull(hierarchy);
        if (sourceFamilies.IsDefault || semanticCategories.IsDefault || relationships.IsDefault || unresolved.IsDefault ||
            sourceFamilies.Any(value => value is null) || semanticCategories.Any(value => value is null) ||
            relationships.Any(value => value is null) || unresolved.Any(value => value is null))
            throw new ArgumentException("Location coverage collections must be initialized and non-null.");
        var orderedFamilies = sourceFamilies.OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (orderedFamilies.Select(value => value.SourceFamilyId).Distinct().Count() != orderedFamilies.Length)
            throw new ArgumentException("Location source family coverage must be distinct.", nameof(sourceFamilies));
        var declared = manifest.SourceFamilies.Select(value => value.SourceFamilyId).ToHashSet();
        if (orderedFamilies.Any(value => !declared.Contains(value.SourceFamilyId)))
            throw new ArgumentException("Coverage cannot introduce an undeclared source family.", nameof(sourceFamilies));
        var orderedCategories = semanticCategories
            .OrderBy(value => value.ExactNativeType?.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.ExactNativeType?.ObjectType, StringComparer.Ordinal)
            .ThenBy(value => value.ExactNativeType?.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.SemanticRoleId?.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (orderedCategories.GroupBy(value => (value.ExactNativeType, value.SemanticRoleId)).Any(group => group.Count() > 1))
            throw new ArgumentException("Semantic category coverage coordinates must be distinct.", nameof(semanticCategories));
        var orderedRelationships = relationships.OrderBy(value => value.SemanticId.Value, StringComparer.Ordinal).ToImmutableArray();
        if (orderedRelationships.Select(value => value.SemanticId).Distinct().Count() != orderedRelationships.Length)
            throw new ArgumentException("Relationship coverage semantics must be distinct.", nameof(relationships));
        var orderedUnresolved = unresolved
            .OrderBy(value => value.ReasonCode, StringComparer.Ordinal)
            .ThenBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.CandidateKind)
            .ToImmutableArray();
        terminology.Validate();
        hierarchy.Validate();
        var emittedIds = orderedFamilies.SelectMany(value => value.EmittedLocationRecordIds).Distinct().ToImmutableArray();
        if (emittedIds.Length != orderedFamilies.Sum(value => value.EmittedLocationRecordIds.Length))
            throw new ArgumentException("An emitted Location cannot be counted by more than one source family.", nameof(sourceFamilies));
        if (terminology.TotalRecordCount != emittedIds.Length)
            throw new ArgumentException("Terminology coverage must account for every emitted Location exactly once.", nameof(terminology));
        if (orderedCategories.SelectMany(value => value.RecordIds).Any(value => !emittedIds.Contains(value)))
            throw new ArgumentException("Semantic category coverage can reference only emitted Locations.", nameof(semanticCategories));
        foreach (var unresolvedEntry in orderedUnresolved)
        {
            var family = orderedFamilies.SingleOrDefault(value => value.SourceFamilyId == unresolvedEntry.SourceFamilyId);
            if (family is null || !family.SourceRevisionIds.Contains(unresolvedEntry.SourceRevisionId))
                throw new ArgumentException("Unresolved coverage must reference a covered source family and revision.", nameof(unresolved));
        }
        foreach (var family in orderedFamilies)
        {
            if (orderedUnresolved.Where(value => value.SourceFamilyId == family.SourceFamilyId).Sum(value => value.Count) !=
                family.UnresolvedSourceAssertionIds.Length)
                throw new ArgumentException(
                    "Unresolved reason coverage must account for every unresolved source assertion.",
                    nameof(unresolved));
        }

        var expectedStatus = DetermineStatus(manifest, orderedFamilies, orderedCategories);
        if (status != expectedStatus)
            throw new ArgumentException($"Location coverage status must be mechanically derived as {expectedStatus}.", nameof(status));
        var expectedId = LocationCoverageReportId.DeriveV1(
            manifest.Id,
            status,
            orderedFamilies,
            orderedCategories,
            terminology,
            hierarchy,
            orderedRelationships,
            orderedUnresolved);
        if (id != expectedId)
            throw new ArgumentException("Location coverage report identity does not match its exact contents.", nameof(id));

        Id = id;
        Manifest = manifest;
        Status = status;
        SourceFamilies = orderedFamilies;
        SemanticCategories = orderedCategories;
        Terminology = terminology;
        Hierarchy = hierarchy;
        Relationships = orderedRelationships;
        Unresolved = orderedUnresolved;
    }

    public LocationCoverageReportId Id { get; }
    public LocationCoverageManifest Manifest { get; }
    public LocationCoverageStatus Status { get; }
    public ImmutableArray<LocationSourceFamilyCoverage> SourceFamilies { get; }
    public ImmutableArray<LocationSemanticCategoryCoverage> SemanticCategories { get; }
    public LocationTerminologyCoverage Terminology { get; }
    public LocationHierarchyCoverage Hierarchy { get; }
    public ImmutableArray<LocationRelationshipCoverage> Relationships { get; }
    public ImmutableArray<LocationUnresolvedCoverage> Unresolved { get; }

    public static LocationCoverageReport Create(
        LocationCoverageManifest manifest,
        ImmutableArray<LocationSourceFamilyCoverage> sourceFamilies,
        ImmutableArray<LocationSemanticCategoryCoverage> semanticCategories,
        LocationTerminologyCoverage terminology,
        LocationHierarchyCoverage hierarchy,
        ImmutableArray<LocationRelationshipCoverage> relationships,
        ImmutableArray<LocationUnresolvedCoverage> unresolved)
    {
        var orderedFamilies = sourceFamilies.OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        var orderedCategories = semanticCategories
            .OrderBy(value => value.ExactNativeType?.Namespace, StringComparer.Ordinal)
            .ThenBy(value => value.ExactNativeType?.ObjectType, StringComparer.Ordinal)
            .ThenBy(value => value.ExactNativeType?.ExactRepresentation, StringComparer.Ordinal)
            .ThenBy(value => value.SemanticRoleId?.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var orderedRelationships = relationships.OrderBy(value => value.SemanticId.Value, StringComparer.Ordinal).ToImmutableArray();
        var orderedUnresolved = unresolved
            .OrderBy(value => value.ReasonCode, StringComparer.Ordinal)
            .ThenBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.CandidateKind)
            .ToImmutableArray();
        var status = DetermineStatus(manifest, orderedFamilies, orderedCategories);
        var id = LocationCoverageReportId.DeriveV1(
            manifest.Id,
            status,
            orderedFamilies,
            orderedCategories,
            terminology,
            hierarchy,
            orderedRelationships,
            orderedUnresolved);
        return new(id, manifest, status, orderedFamilies, orderedCategories, terminology, hierarchy, orderedRelationships, orderedUnresolved);
    }

    private static LocationCoverageStatus DetermineStatus(
        LocationCoverageManifest manifest,
        ImmutableArray<LocationSourceFamilyCoverage> sourceFamilies,
        ImmutableArray<LocationSemanticCategoryCoverage> semanticCategories)
    {
        var applicable = manifest.SourceFamilies.Where(value => value.IsApplicable).ToImmutableArray();
        var emittedCount = sourceFamilies.Sum(value => value.EmittedLocationRecordIds.Length);
        var unresolvedCount = sourceFamilies.Sum(value => value.UnresolvedSourceAssertionIds.Length);
        if (applicable.IsEmpty && emittedCount == 0 && unresolvedCount == 0)
            return LocationCoverageStatus.Unsupported;
        if (emittedCount == 0 && unresolvedCount > 0)
            return LocationCoverageStatus.Unresolved;
        if (manifest.IsClosed && manifest.QcsValidation.Status == CatalogValidationStatus.Passed &&
            sourceFamilies.Length == applicable.Length &&
            applicable.All(declaration =>
                sourceFamilies.SingleOrDefault(value => value.SourceFamilyId == declaration.SourceFamilyId)
                    is { } coverage && coverage.IsMechanicallyClosed(declaration)) &&
            applicable.SelectMany(value => value.ExpectedSemanticRoles).Distinct()
                .All(role => semanticCategories.Any(category => category.SemanticRoleId == role)))
            return LocationCoverageStatus.CompleteForRegisteredSources;
        return LocationCoverageStatus.Partial;
    }
}

internal static class LocationCoverageEncoding
{
    public static void AddSourceFamilies(CanonicalIdentityWriter writer, ImmutableArray<LocationSourceFamilyCoverage> values)
    {
        var ordered = values.OrderBy(value => value.SourceFamilyId.Value, StringComparer.Ordinal).ToImmutableArray();
        writer.AddInt32("source-family-count", ordered.Length);
        for (var index = 0; index < ordered.Length; index++) ordered[index].AddTo(writer, $"source-family.{index}");
    }

    public static void AddSemanticCategories(CanonicalIdentityWriter writer, ImmutableArray<LocationSemanticCategoryCoverage> values)
    {
        writer.AddInt32("semantic-category-count", values.Length);
        for (var index = 0; index < values.Length; index++) values[index].AddTo(writer, $"semantic-category.{index}");
    }

    public static void AddRelationships(CanonicalIdentityWriter writer, ImmutableArray<LocationRelationshipCoverage> values)
    {
        writer.AddInt32("relationship-coverage-count", values.Length);
        for (var index = 0; index < values.Length; index++) values[index].AddTo(writer, $"relationship-coverage.{index}");
    }

    public static void AddUnresolved(CanonicalIdentityWriter writer, ImmutableArray<LocationUnresolvedCoverage> values)
    {
        writer.AddInt32("unresolved-coverage-count", values.Length);
        for (var index = 0; index < values.Length; index++) values[index].AddTo(writer, $"unresolved-coverage.{index}");
    }
}

public enum LocationApplicabilityState
{
    Applicable = 0,
    InactiveMod = 1,
    Inapplicable = 2,
    Unknown = 3,
}

[Flags]
public enum LocationConflictState
{
    None = 0,
    Terminology = 1,
    Hierarchy = 2,
    Relationship = 4,
    Classification = 8,
}

public sealed record LocationAuthoritativeParentPath
{
    public LocationAuthoritativeParentPath(
        ImmutableArray<KnowledgeRecordId> parentKnowledgeRecordIds,
        ImmutableArray<EvidenceClaimContentId> relationshipClaimIds)
    {
        if (parentKnowledgeRecordIds.IsDefault || relationshipClaimIds.IsDefault)
            throw new ArgumentException("Parent path collections must be initialized.");
        if (parentKnowledgeRecordIds.Length != relationshipClaimIds.Length)
            throw new ArgumentException("Every authoritative parent edge requires its exact relationship claim.");
        ParentKnowledgeRecordIds = parentKnowledgeRecordIds;
        RelationshipClaimIds = relationshipClaimIds;
    }

    public ImmutableArray<KnowledgeRecordId> ParentKnowledgeRecordIds { get; }
    public ImmutableArray<EvidenceClaimContentId> RelationshipClaimIds { get; }
}

public sealed record LocationSelectorQuery
{
    public LocationSelectorQuery(
        CatalogCompositionId catalogCompositionId,
        KnowledgeRecordId? parentKnowledgeRecordId,
        ImmutableArray<LocationSemanticRoleId> locationSemanticRoleIds,
        string? searchText,
        bool includeIdentifierOnly,
        bool includeInapplicableForInspection)
    {
        CanonicalKnowledgeContract.RequireIdentifier(catalogCompositionId.Value, nameof(catalogCompositionId));
        if (parentKnowledgeRecordId is { } parent)
            CanonicalKnowledgeContract.RequireIdentifier(parent.Value, nameof(parentKnowledgeRecordId));
        if (locationSemanticRoleIds.IsDefault)
            throw new ArgumentException("Location semantic-role filters must be initialized.", nameof(locationSemanticRoleIds));
        var roles = locationSemanticRoleIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (roles.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new ArgumentException("Location semantic-role filters must be distinct.", nameof(locationSemanticRoleIds));
        if (searchText is not null) CanonicalUtf8.Validate(searchText);
        CatalogCompositionId = catalogCompositionId;
        ParentKnowledgeRecordId = parentKnowledgeRecordId;
        LocationSemanticRoleIds = roles;
        SearchText = searchText;
        IncludeIdentifierOnly = includeIdentifierOnly;
        IncludeInapplicableForInspection = includeInapplicableForInspection;
    }

    public CatalogCompositionId CatalogCompositionId { get; }
    public KnowledgeRecordId? ParentKnowledgeRecordId { get; }
    public ImmutableArray<LocationSemanticRoleId> LocationSemanticRoleIds { get; }
    public string? SearchText { get; }
    public bool IncludeIdentifierOnly { get; }
    public bool IncludeInapplicableForInspection { get; }
}

public sealed record LocationSelectorOption
{
    public LocationSelectorOption(
        KnowledgeRecordId knowledgeRecordId,
        ImmutableArray<TerminologyAssertion> exactTerminologyAssertions,
        ImmutableArray<TerminologyAssertion> exactAuthoritativeAliases,
        SourceNativeIdentifier nativeIdentity,
        ImmutableArray<SourceNativeLocationTypeAssertion> nativeTypeAssertions,
        ImmutableArray<LocationSemanticClassificationAssertion> semanticClassifications,
        ImmutableArray<LocationAuthoritativeParentPath> authoritativeParentPaths,
        ImmutableArray<RelationshipAssertion> spatialRelationships,
        ImmutableArray<CorrelationRecordId> correlationIds,
        ImmutableArray<CorrelatedRelationshipEnvelopeId> correlatedRelationshipEnvelopeIds,
        ImmutableArray<EvidenceVerificationKind> evidenceStates,
        KnowledgeSourceScope sourceScope,
        LocationApplicabilityState applicabilityState,
        LocationConflictState conflictState,
        CanonicalResolutionState resolutionState)
    {
        CanonicalKnowledgeContract.RequireIdentifier(knowledgeRecordId.Value, nameof(knowledgeRecordId));
        ArgumentNullException.ThrowIfNull(nativeIdentity);
        ArgumentNullException.ThrowIfNull(sourceScope);
        if (!Enum.IsDefined(applicabilityState)) throw new ArgumentOutOfRangeException(nameof(applicabilityState));
        if (!Enum.IsDefined(resolutionState)) throw new ArgumentOutOfRangeException(nameof(resolutionState));
        const LocationConflictState allConflictFlags = LocationConflictState.Terminology |
                                                       LocationConflictState.Hierarchy |
                                                       LocationConflictState.Relationship |
                                                       LocationConflictState.Classification;
        if ((conflictState & ~allConflictFlags) != 0) throw new ArgumentOutOfRangeException(nameof(conflictState));
        RequireInitialized(exactTerminologyAssertions, nameof(exactTerminologyAssertions));
        RequireInitialized(exactAuthoritativeAliases, nameof(exactAuthoritativeAliases));
        RequireInitialized(nativeTypeAssertions, nameof(nativeTypeAssertions));
        RequireInitialized(semanticClassifications, nameof(semanticClassifications));
        RequireInitialized(authoritativeParentPaths, nameof(authoritativeParentPaths));
        RequireInitialized(spatialRelationships, nameof(spatialRelationships));
        RequireInitialized(correlationIds, nameof(correlationIds));
        RequireInitialized(correlatedRelationshipEnvelopeIds, nameof(correlatedRelationshipEnvelopeIds));
        RequireInitialized(evidenceStates, nameof(evidenceStates));
        if (exactTerminologyAssertions.Any(value => value.KnowledgeRecordId != knowledgeRecordId) ||
            exactAuthoritativeAliases.Any(value => value.KnowledgeRecordId != knowledgeRecordId || value.Role != TerminologyAssertionRole.Alias) ||
            nativeTypeAssertions.Any(value => value.KnowledgeRecordId != knowledgeRecordId) ||
            semanticClassifications.Any(value => value.KnowledgeRecordId != knowledgeRecordId) ||
            spatialRelationships.Any(value => value.SubjectKnowledgeRecordId != knowledgeRecordId))
            throw new ArgumentException("Location selector option claims must belong to its exact record.");

        KnowledgeRecordId = knowledgeRecordId;
        ExactTerminologyAssertions = SortTerminology(exactTerminologyAssertions, nameof(exactTerminologyAssertions));
        ExactAuthoritativeAliases = SortTerminology(exactAuthoritativeAliases, nameof(exactAuthoritativeAliases));
        NativeIdentity = nativeIdentity;
        NativeTypeAssertions = nativeTypeAssertions.OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        SemanticClassifications = semanticClassifications.OrderBy(value => value.RoleId.Value, StringComparer.Ordinal)
            .ThenBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
        AuthoritativeParentPaths = authoritativeParentPaths;
        SpatialRelationships = spatialRelationships.OrderBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
            .ToImmutableArray();
        CorrelationIds = correlationIds.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        CorrelatedRelationshipEnvelopeIds = correlatedRelationshipEnvelopeIds
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        EvidenceStates = evidenceStates.Distinct().Order().ToImmutableArray();
        SourceScope = sourceScope;
        ApplicabilityState = applicabilityState;
        ConflictState = conflictState;
        ResolutionState = resolutionState;
    }

    public KnowledgeRecordId KnowledgeRecordId { get; }
    public ImmutableArray<TerminologyAssertion> ExactTerminologyAssertions { get; }
    public ImmutableArray<TerminologyAssertion> ExactAuthoritativeAliases { get; }
    public SourceNativeIdentifier NativeIdentity { get; }
    public ImmutableArray<SourceNativeLocationTypeAssertion> NativeTypeAssertions { get; }
    public ImmutableArray<LocationSemanticClassificationAssertion> SemanticClassifications { get; }
    public ImmutableArray<LocationAuthoritativeParentPath> AuthoritativeParentPaths { get; }
    public ImmutableArray<RelationshipAssertion> SpatialRelationships { get; }
    public ImmutableArray<CorrelationRecordId> CorrelationIds { get; }
    public ImmutableArray<CorrelatedRelationshipEnvelopeId> CorrelatedRelationshipEnvelopeIds { get; }
    public ImmutableArray<EvidenceVerificationKind> EvidenceStates { get; }
    public KnowledgeSourceScope SourceScope { get; }
    public LocationApplicabilityState ApplicabilityState { get; }
    public LocationConflictState ConflictState { get; }
    public CanonicalResolutionState ResolutionState { get; }

    public bool IsIdentifierOnly => ExactTerminologyAssertions.All(value => value.Role != TerminologyAssertionRole.PrimaryName);

    private static void RequireInitialized<T>(ImmutableArray<T> values, string name)
    {
        if (values.IsDefault || values.Any(value => value is null))
            throw new ArgumentException("Collection must be initialized and non-null.", name);
    }

    private static ImmutableArray<TerminologyAssertion> SortTerminology(
        ImmutableArray<TerminologyAssertion> values,
        string name)
    {
        var ordered = values.OrderBy(value => value.Role)
            .ThenBy(value => value.LanguageTag, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (ordered.Select(EvidenceClaimContentId.DeriveV1).Distinct().Count() != ordered.Length)
            throw new ArgumentException("Terminology assertions must be distinct.", name);
        return ordered;
    }
}

public sealed record LocationSelectorResult
{
    public LocationSelectorResult(
        CatalogRevisionId catalogRevisionId,
        CatalogCompositionId catalogCompositionId,
        LocationCoverageReportId locationCoverageReportId,
        LocationPresentationPolicyId locationPresentationPolicyId,
        string exactPresentationPolicyVersion,
        ImmutableArray<LocationSelectorOption> options)
    {
        CanonicalKnowledgeContract.RequireIdentifier(catalogRevisionId.Value, nameof(catalogRevisionId));
        CanonicalKnowledgeContract.RequireIdentifier(catalogCompositionId.Value, nameof(catalogCompositionId));
        CanonicalKnowledgeContract.RequireIdentifier(locationCoverageReportId.Value, nameof(locationCoverageReportId));
        CanonicalKnowledgeContract.RequireIdentifier(locationPresentationPolicyId.Value, nameof(locationPresentationPolicyId));
        ExactPresentationPolicyVersion = CanonicalKnowledgeContract.RequireText(
            exactPresentationPolicyVersion,
            nameof(exactPresentationPolicyVersion));
        if (options.IsDefault || options.Any(value => value is null))
            throw new ArgumentException("Location selector options must be initialized and non-null.", nameof(options));
        if (options.Select(value => value.KnowledgeRecordId).Distinct().Count() != options.Length)
            throw new ArgumentException("Location selector options must identify distinct records.", nameof(options));
        CatalogRevisionId = catalogRevisionId;
        CatalogCompositionId = catalogCompositionId;
        LocationCoverageReportId = locationCoverageReportId;
        LocationPresentationPolicyId = locationPresentationPolicyId;
        // Ordering is part of the pinned presentation policy and is established by the
        // projector. Do not replace it with identity ordering here.
        Options = options;
    }

    public CatalogRevisionId CatalogRevisionId { get; }
    public CatalogCompositionId CatalogCompositionId { get; }
    public LocationCoverageReportId LocationCoverageReportId { get; }
    public LocationPresentationPolicyId LocationPresentationPolicyId { get; }
    public string ExactPresentationPolicyVersion { get; }
    public ImmutableArray<LocationSelectorOption> Options { get; }
}

public static class CanonicalLocationContractValidator
{
    public static void ValidateStrictHierarchyAcyclic(ImmutableArray<RelationshipAssertion> relationships)
    {
        if (relationships.IsDefault || relationships.Any(value => value is null))
            throw new ArgumentException("Location relationships must be initialized and non-null.", nameof(relationships));

        foreach (var revisionGroup in relationships
                     .Where(value => LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId) &&
                                     value.ResolvedTargetKnowledgeRecordId is not null)
                     .GroupBy(value => value.SourceRevisionId))
        {
            var edges = revisionGroup
                .GroupBy(value => value.SubjectKnowledgeRecordId)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(value => value.ResolvedTargetKnowledgeRecordId!.Value).Distinct().ToImmutableArray());
            var visiting = new HashSet<KnowledgeRecordId>();
            var visited = new HashSet<KnowledgeRecordId>();
            foreach (var node in edges.Keys)
                Visit(node, edges, visiting, visited);
        }
    }

    private static void Visit(
        KnowledgeRecordId node,
        IReadOnlyDictionary<KnowledgeRecordId, ImmutableArray<KnowledgeRecordId>> edges,
        HashSet<KnowledgeRecordId> visiting,
        HashSet<KnowledgeRecordId> visited)
    {
        if (visited.Contains(node)) return;
        if (!visiting.Add(node))
            throw new ArgumentException("Strict Location hierarchy relationships must be acyclic within a source revision.");
        if (edges.TryGetValue(node, out var targets))
        {
            foreach (var target in targets) Visit(target, edges, visiting, visited);
        }
        visiting.Remove(node);
        visited.Add(node);
    }
}
