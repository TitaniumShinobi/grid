using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grid.Core.Models;
using Grid.GtaV.Knowledge;

internal sealed record RegistrationCloudMissionRepresentativePath(
    string KnowledgeRecordId,
    string ActivityFamily,
    string PlayerFacingTitle);

internal sealed record RegistrationCloudMissionCoverage(
    string SourceFamilyId,
    string SourceGrain,
    string ScopeMode,
    bool PaginationComplete,
    DateTimeOffset CapturedAtUtc,
    string? ProviderRegistryRevision,
    int ProviderObjects,
    int MatchedProviderObjects,
    int MatchedTargetRecords,
    int TitledTargetRecords,
    int FamilyOrganizedTargetRecords,
    int OnlineClassifiedTargetRecords,
    int UnmatchedProviderObjects,
    int UnmatchedTargetRecords,
    int MissingTitleObjects,
    int MissingFamilyObjects,
    int AmbiguousProviderObjects,
    int EquivalentDuplicates,
    int TitleConflicts,
    int FamilyConflicts,
    int ReferenceEvidenceReceipts,
    ImmutableArray<RegistrationCloudMissionRepresentativePath> RepresentativePaths)
{
    public static RegistrationCloudMissionCoverage Create(
        GtaVRockstarCloudSnapshotBundle bundle,
        GtaVCloudJobHeaderSecondaryAssertionResult result)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(result);
        var batch = result.Batch;
        var metrics = result.Metrics;
        var onlineIds = batch.SemanticClassifications
            .Where(value => value.RoleId == CanonicalProjectionSemantics.MissionOnline)
            .Select(value => value.KnowledgeRecordId)
            .ToHashSet();
        var titleByContent = batch.TerminologyAssertions
            .Where(value => value.Role == TerminologyAssertionRole.PrimaryName)
            .ToDictionary(value => EvidenceClaimContentId.DeriveV1(value), value => value);
        var familyByContent = batch.OrganizationalValues
            .Where(value => value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode &&
                            value.VerbatimDisplayValue is not null)
            .ToDictionary(value => EvidenceClaimContentId.DeriveV1(value), value => value);
        var titleEnvelopes = batch.CrossSourceAssertions
            .Where(value => value.AssertionKind == CrossSourceCanonicalAssertionKind.Terminology &&
                            titleByContent.ContainsKey(value.UnderlyingClaimContentId))
            .ToLookup(value => (value.TargetKnowledgeRecordId, value.TargetLinkClaimId));
        var familyEnvelopes = batch.CrossSourceAssertions
            .Where(value => value.AssertionKind == CrossSourceCanonicalAssertionKind.OrganizationalValue &&
                            familyByContent.ContainsKey(value.UnderlyingClaimContentId))
            .ToLookup(value => (value.TargetKnowledgeRecordId, value.TargetLinkClaimId));
        var paths = titleEnvelopes
            .Where(group => onlineIds.Contains(group.Key.TargetKnowledgeRecordId) && familyEnvelopes.Contains(group.Key))
            .SelectMany(group => group.SelectMany(titleEnvelope => familyEnvelopes[group.Key].Select(familyEnvelope =>
            {
                var title = titleByContent[titleEnvelope.UnderlyingClaimContentId];
                var family = familyByContent[familyEnvelope.UnderlyingClaimContentId];
                return new RegistrationCloudMissionRepresentativePath(
                    group.Key.TargetKnowledgeRecordId.Value,
                    family.VerbatimDisplayValue!,
                    title.VerbatimValue);
            })))
            .Distinct()
            .OrderBy(value => value.ActivityFamily, StringComparer.Ordinal)
            .ThenBy(value => value.PlayerFacingTitle, StringComparer.Ordinal)
            .ThenBy(value => value.KnowledgeRecordId, StringComparer.Ordinal)
            .Take(3)
            .ToImmutableArray();
        return new RegistrationCloudMissionCoverage(
            GtaVRockstarCloudSnapshotBundleLoader.SourceFamilyId,
            "provider-source+complete-provider-object-identity+native-revision",
            bundle.ScopeMode == GtaVRockstarCloudSnapshotScopeMode.GlobalRegistry
                ? "global-registry"
                : "exact-target-set",
            bundle.Responses.All(value => value.Terminal || value.NextCursor is not null),
            bundle.CapturedAtUtc,
            bundle.ProviderRegistryRevision.ExactRepresentation,
            metrics.ProviderObjectCount,
            metrics.ExactMatchedProviderObjectCount,
            metrics.ExactMatchedTargetRecordCount,
            metrics.TitledTargetRecordCount,
            metrics.FamilyOrganizedTargetRecordCount,
            metrics.OnlineClassifiedTargetRecordCount,
            metrics.UnmatchedProviderObjectCount,
            metrics.UnmatchedTargetRecordCount,
            bundle.Index.Objects.Count(value => value.ExactTitle is null),
            bundle.Index.Objects.Count(value => value.ExactActivityFamilyIdentity is null ||
                                               value.ExactActivityFamilyLabel is null),
            metrics.AmbiguousProviderObjectCount,
            metrics.EquivalentDuplicateCount,
            metrics.TitleConflictTargetRecordCount,
            metrics.FamilyConflictTargetRecordCount,
            batch.ReferenceEvidenceReceipts.Length,
            paths);
    }

    public void Validate()
    {
        static void Nonnegative(int value, string name)
        {
            if (value < 0) throw new InvalidDataException($"Cloud MissionQuest coverage {name} cannot be negative.");
        }

        if (SourceFamilyId != "rockstar.gta-v.enhanced.rockstar-cloud-job-registry" ||
            string.IsNullOrWhiteSpace(SourceGrain) ||
            ScopeMode is not ("global-registry" or "exact-target-set") ||
            !PaginationComplete)
            throw new InvalidDataException("Cloud MissionQuest coverage has invalid source scope or incomplete pagination.");
        foreach (var (value, name) in new[]
                 {
                     (ProviderObjects, nameof(ProviderObjects)),
                     (MatchedProviderObjects, nameof(MatchedProviderObjects)),
                     (MatchedTargetRecords, nameof(MatchedTargetRecords)),
                     (TitledTargetRecords, nameof(TitledTargetRecords)),
                     (FamilyOrganizedTargetRecords, nameof(FamilyOrganizedTargetRecords)),
                     (OnlineClassifiedTargetRecords, nameof(OnlineClassifiedTargetRecords)),
                     (UnmatchedProviderObjects, nameof(UnmatchedProviderObjects)),
                     (UnmatchedTargetRecords, nameof(UnmatchedTargetRecords)),
                     (MissingTitleObjects, nameof(MissingTitleObjects)),
                     (MissingFamilyObjects, nameof(MissingFamilyObjects)),
                     (AmbiguousProviderObjects, nameof(AmbiguousProviderObjects)),
                     (EquivalentDuplicates, nameof(EquivalentDuplicates)),
                     (TitleConflicts, nameof(TitleConflicts)),
                     (FamilyConflicts, nameof(FamilyConflicts)),
                     (ReferenceEvidenceReceipts, nameof(ReferenceEvidenceReceipts)),
                 })
            Nonnegative(value, name);
        if (MatchedProviderObjects + UnmatchedProviderObjects + AmbiguousProviderObjects > ProviderObjects ||
            MatchedTargetRecords > 987 || TitledTargetRecords > MatchedTargetRecords ||
            FamilyOrganizedTargetRecords > MatchedTargetRecords ||
            OnlineClassifiedTargetRecords > MatchedTargetRecords || UnmatchedTargetRecords > 987 ||
            MatchedTargetRecords + UnmatchedTargetRecords > 987 ||
            RepresentativePaths.IsDefault || RepresentativePaths.Length > 3 ||
            RepresentativePaths.Any(value => string.IsNullOrWhiteSpace(value.KnowledgeRecordId) ||
                                             string.IsNullOrWhiteSpace(value.ActivityFamily) ||
                                             string.IsNullOrWhiteSpace(value.PlayerFacingTitle)))
            throw new InvalidDataException("Cloud MissionQuest coverage violates exact corpus accounting.");
        var ordered = RepresentativePaths.OrderBy(value => value.ActivityFamily, StringComparer.Ordinal)
            .ThenBy(value => value.PlayerFacingTitle, StringComparer.Ordinal)
            .ThenBy(value => value.KnowledgeRecordId, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!RepresentativePaths.SequenceEqual(ordered))
            throw new InvalidDataException("Representative cloud MissionQuest paths must be ordinally ordered.");
    }
}

internal static class RegistrationCloudMissionCoverageProjection
{
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    public static JsonObject Apply(
        object baseCoverage,
        GtaRegistrationSourceRegistry registry,
        RegistrationCloudMissionCoverage cloud)
    {
        ArgumentNullException.ThrowIfNull(baseCoverage);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cloud);
        cloud.Validate();
        var original = JsonSerializer.SerializeToNode(baseCoverage, CompactJson) as JsonObject ??
            throw new InvalidDataException("The base registration coverage report is not an object.");
        if (original["schemaVersion"]?.GetValue<int>() != 1 ||
            original["knowledgeKinds"] is not JsonArray kinds)
            throw new InvalidDataException("The base registration coverage report is not schema v1.");
        var mission = kinds.OfType<JsonObject>().SingleOrDefault(value =>
            string.Equals(value["knowledgeKind"]?.GetValue<string>(), "MissionQuest", StringComparison.Ordinal)) ??
            throw new InvalidDataException("The base registration coverage report has no unique MissionQuest row.");

        mission["rockstarCloudJobRegistry"] = JsonSerializer.SerializeToNode(new
        {
            sourceFamilyId = cloud.SourceFamilyId,
            sourceGrain = cloud.SourceGrain,
            scopeMode = cloud.ScopeMode,
            paginationComplete = cloud.PaginationComplete,
            capturedAtUtc = cloud.CapturedAtUtc,
            providerRegistryRevision = cloud.ProviderRegistryRevision,
            providerObjects = cloud.ProviderObjects,
            exactJoin = new
            {
                matchedProviderObjects = cloud.MatchedProviderObjects,
                matchedTargetRecords = cloud.MatchedTargetRecords,
                unmatchedProviderObjects = cloud.UnmatchedProviderObjects,
                unmatchedTargetRecords = cloud.UnmatchedTargetRecords,
                ambiguousProviderObjects = cloud.AmbiguousProviderObjects,
                equivalentDuplicates = cloud.EquivalentDuplicates,
                titleConflicts = cloud.TitleConflicts,
                familyConflicts = cloud.FamilyConflicts,
            },
            coverageDelta = new
            {
                recordsBefore = 1057,
                recordsAfter = 1057,
                titledBefore = 70,
                titledAfter = 70 + cloud.TitledTargetRecords,
                identifierOnlyBefore = 987,
                identifierOnlyAfter = 987 - cloud.TitledTargetRecords,
                activityFamilyOrganizedBefore = 15,
                activityFamilyOrganizedAfter = 15 + cloud.FamilyOrganizedTargetRecords,
                unresolvedSourceAssertionsBefore = 60,
                unresolvedSourceAssertionsAfter = 60,
                unsupportedSourceObjectsBefore = 5,
                unsupportedSourceObjectsAfter = 5,
            },
            titledTargetRecords = cloud.TitledTargetRecords,
            familyOrganizedTargetRecords = cloud.FamilyOrganizedTargetRecords,
            onlineClassifiedTargetRecords = cloud.OnlineClassifiedTargetRecords,
            missingTitleObjects = cloud.MissingTitleObjects,
            missingFamilyObjects = cloud.MissingFamilyObjects,
            referenceEvidenceReceipts = cloud.ReferenceEvidenceReceipts,
            representativePaths = cloud.RepresentativePaths.Select(value => new
            {
                knowledgeRecordId = value.KnowledgeRecordId,
                activityFamily = value.ActivityFamily,
                playerFacingTitle = value.PlayerFacingTitle,
            }).ToArray(),
        }, CompactJson);

        if (original["unsupportedSourceFamilies"] is not JsonArray unsupported)
            throw new InvalidDataException("The base registration coverage report has no unsupported-source ledger.");
        var staleCloudRows = unsupported.OfType<JsonObject>()
            .Where(value => string.Equals(
                value["sourceFamilyId"]?.GetValue<string>(), cloud.SourceFamilyId, StringComparison.Ordinal))
            .ToArray();
        foreach (var row in staleCloudRows) unsupported.Remove(row);
        original["referenceSourceFamilies"] = JsonSerializer.SerializeToNode(new[]
        {
            new
            {
                sourceFamilyId = cloud.SourceFamilyId,
                status = "supported-observed",
                knowledgeKinds = new[] { "MissionQuest" },
                scopeMode = cloud.ScopeMode,
                providerObjects = cloud.ProviderObjects,
            },
        }, CompactJson);

        original.Remove("contentSha256");
        original["registrationSourceRegistrySha256"] = registry.DocumentSha256;
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(original, CompactJson);
        var result = new JsonObject { ["schemaVersion"] = 1, ["contentSha256"] = Convert.ToHexStringLower(SHA256.HashData(bodyBytes)) };
        foreach (var property in original.Where(value => value.Key != "schemaVersion"))
        {
            result[property.Key] = property.Value?.DeepClone();
        }
        return result;
    }
}
