using System.Collections.Immutable;

namespace Grid.Core.Models;

public enum RegistrationRefreshMode
{
    Lightweight = 0,
    KnowledgeRebuild = 1,
}

public enum RegistrationRefreshStatus
{
    Completed = 0,
    Partial = 1,
    Skipped = 2,
    Failed = 3,
    Unavailable = 4,
}

public sealed record RegistrationRefreshResourcePaths(
    string? GtaLocationHierarchyWikiPath,
    string? GtaLocationHierarchyNativeTablePath);

public sealed record RegistrationRefreshContext(
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    CatalogPackageId? PinnedPackageId,
    string CatalogStorePath,
    string PreparedNavigationRoot,
    string BindingStorePath,
    string ReceiptDirectory,
    string? ProfileObservationFingerprint,
    bool AllowCandidatePackages,
    RegistrationRefreshResourcePaths ResourcePaths);

public sealed record RegistrationRefreshProgress(string Stage, string Detail, double? Fraction);

public sealed record RegistrationRefreshResult(
    RegistrationRefreshStatus Status,
    RegistrationRefreshMode Mode,
    string Detail,
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    CatalogPackageId? PreviousPackageId,
    CatalogPackageId? PublishedPackageId,
    string? CatalogStoreSha256,
    string? ReceiptPath,
    string? CatalogRollbackPath,
    string? BindingRollbackPath,
    bool PreparedNavigationRebuilt,
    int AdmittedLocationRelationships,
    int RejectedLocationRelationships);

public sealed record RegistrationRefreshReceipt(
    int SchemaVersion,
    DateTimeOffset CompletedUtc,
    RegistrationRefreshStatus Status,
    RegistrationRefreshMode Mode,
    GameId GameId,
    InstallationId InstallationId,
    ProfileId ProfileId,
    string EngineId,
    string EngineVersion,
    CatalogPackageId? PreviousPackageId,
    CatalogPackageId? PublishedPackageId,
    long? CatalogStoreRevision,
    string? CatalogStoreSha256,
    string? ProfileObservationFingerprint,
    ImmutableArray<string> ContributorRevisionIds,
    int AdmittedLocationRelationships,
    int RejectedLocationRelationships,
    string? CatalogRollbackPath,
    string? BindingRollbackPath,
    string Detail)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record LocationRecordCorrelationKey(
    string NativeNamespace,
    string NativeObjectType,
    string NativeExactRepresentation,
    string PrimaryName,
    string LanguageTag = "en-US");

public sealed record LocationContainmentRegistrationClaim(
    LocationRecordCorrelationKey Subject,
    LocationRecordCorrelationKey Parent,
    RelationshipSemanticId SemanticId,
    string SourceNativeRelationshipType,
    string SourceFieldPath,
    CatalogSourceRevisionId AssertingSourceRevisionId);

public sealed record LocationContainmentRegistrationAdmission(
    LocationContainmentRegistrationClaim Claim,
    KnowledgeRecordId SubjectRecordId,
    KnowledgeRecordId ParentRecordId,
    RelationshipAssertion Relationship);

public sealed record LocationContainmentRegistrationRejection(
    LocationContainmentRegistrationClaim Claim,
    string ReasonCode,
    string Detail);

public sealed record LocationContainmentRegistrationResult(
    ImmutableArray<LocationContainmentRegistrationAdmission> Admissions,
    ImmutableArray<LocationContainmentRegistrationRejection> Rejections);
