using System.Collections.Immutable;

namespace Grid.Core.Models;

/// <summary>Authenticated, compact matching coordinates for an immutable prepared generation.</summary>
public sealed record PreparedCanonicalGenerationDescriptor(
    int FormatVersion,
    CatalogPackageId PackageId,
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    long SharedLibraryRevision,
    GameId GameId,
    CatalogValidationStatus ValidationStatus,
    SourceNativeVersion? ExactGameVersion,
    SourceNativeIdentifier? DistributionApplicationIdentity,
    SourceNativeVersion? DistributionBuildVersion,
    string SourceStoreSha256,
    CanonicalTerminologyLocalePreference TerminologyLocale,
    ImmutableArray<PreparedCanonicalKindDescriptor> Kinds,
    bool IsBaseGameOnly = true);

public sealed record PreparedCanonicalKindDescriptor(
    KnowledgeKind KnowledgeKind,
    CanonicalSelectorProjectionPolicyId ProjectionPolicyId,
    string ProjectionPolicyVersion,
    CanonicalNavigationPathId RootPathId,
    KnowledgeCoverageState CoverageState,
    int PathCount,
    int DistinctSelectableRecordCount,
    int IdentifierOnlySelectableRecordCount);

/// <summary>A bounded slice of immediate children; the cursor belongs to this exact generation and path.</summary>
public sealed record PreparedCanonicalNavigationPage(
    CanonicalSelectorResult Result,
    string? ContinuationCursor);

public sealed record PreparedCanonicalNavigationStatistics(
    long PageReads,
    long PageCacheHits,
    long RowsMaterialized,
    long CachedBytes,
    long MaximumCachedBytes);

internal sealed record PreparedCanonicalDirectory(
    PreparedCanonicalGenerationDescriptor Descriptor,
    ImmutableArray<PreparedCanonicalPathEntry> Paths,
    ImmutableArray<PreparedCanonicalPageEntry> Pages);

internal sealed record PreparedCanonicalPathEntry(
    KnowledgeKind Kind,
    CanonicalNavigationPathId PathId,
    CanonicalNavigationPathId? ParentPathId,
    KnowledgeRecordId? RecordId,
    bool IsSelectable,
    string NodePageId,
    ImmutableArray<string> PageIds);

internal sealed record PreparedCanonicalPageEntry(string Id, string Sha256, int ByteLength);

internal sealed record PreparedCanonicalPublicationReceipt(int FormatVersion, string DirectorySha256,
    CatalogPackageId PackageId, string SourceStoreSha256);
