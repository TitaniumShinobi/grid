using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Reusable registration-time Location containment resolution. Adapters supply claims; this engine correlates and admits or rejects.
/// </summary>
public static class CanonicalLocationRelationshipRegistrationEngine
{
    public static LocationContainmentRegistrationResult Resolve(
        CanonicalCatalogPayload payload,
        ImmutableArray<LocationContainmentRegistrationClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (claims.IsDefault) throw new ArgumentException("Claims must be initialized.", nameof(claims));

        var admissions = ImmutableArray.CreateBuilder<LocationContainmentRegistrationAdmission>();
        var rejections = ImmutableArray.CreateBuilder<LocationContainmentRegistrationRejection>();
        var admittedKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in claims.OrderBy(value => value.SourceFieldPath, StringComparer.Ordinal)
                     .ThenBy(value => value.Subject.PrimaryName, StringComparer.Ordinal))
        {
            try
            {
                var subjectId = CanonicalLocationRecordCorrelator.ResolveUniqueLocation(payload, claim.Subject);
                var parentId = CanonicalLocationRecordCorrelator.ResolveUniqueLocation(payload, claim.Parent);
                if (subjectId == parentId)
                {
                    rejections.Add(new(claim, "self-containment", "A Location cannot be contained by itself."));
                    continue;
                }

                var duplicateKey = subjectId.Value + "\0" + claim.SemanticId.Value + "\0" + parentId.Value;
                if (!admittedKeys.Add(duplicateKey))
                {
                    rejections.Add(new(claim, "duplicate-relationship", "An identical containment relationship was already admitted."));
                    continue;
                }

                var conflicting = payload.RelationshipAssertions.FirstOrDefault(value =>
                    value.SubjectKnowledgeRecordId == subjectId &&
                    value.SemanticId == claim.SemanticId &&
                    value.ResolvedTargetKnowledgeRecordId is not null &&
                    value.ResolvedTargetKnowledgeRecordId != parentId);
                if (conflicting is not null)
                {
                    rejections.Add(new(claim, "conflicting-relationship",
                        "An existing resolved relationship targets a different parent."));
                    continue;
                }

                var parentNative = payload.KnowledgeRecords.Single(value => value.Id == parentId).NativeIdentity;
                var relationship = new RelationshipAssertion(
                    subjectId,
                    claim.AssertingSourceRevisionId,
                    claim.SemanticId,
                    claim.SourceNativeRelationshipType,
                    claim.SourceFieldPath,
                    parentNative,
                    parentId);
                admissions.Add(new(claim, subjectId, parentId, relationship));
            }
            catch (InvalidDataException exception)
            {
                rejections.Add(new(claim, "correlation-failed", exception.Message));
            }
        }

        return new(admissions.ToImmutable(), rejections.ToImmutable());
    }
}
