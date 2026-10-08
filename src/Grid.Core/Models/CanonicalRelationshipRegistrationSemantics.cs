namespace Grid.Core.Models;

/// <summary>Generic Contract 2 relationship semantics. No game-specific types.</summary>
public static class CanonicalRelationshipRegistrationSemantics
{
    public const string ContainedBy = "contained-by";
    public const string Contains = "contains";
    public const string ClassifiedAs = "classified-as";
    public const string ClassifiedUnder = "classified-under";
    public const string BelongsTo = "belongs-to";
    public const string ApplicableTo = "applicable-to";
    public const string AlternateViewMembership = "alternate-view-membership";
    public const string VariantOf = "variant-of";
    public const string AssetOf = "asset-of";
    public const string BasedOn = "based-on";

    public static string Normalize(string semantic) => semantic switch
    {
        Contains => ContainedBy,
        ClassifiedUnder => ClassifiedAs,
        _ => semantic,
    };

    public static bool IsHierarchyEdge(string semantic)
    {
        var normalized = Normalize(semantic);
        return normalized is ContainedBy or ClassifiedAs;
    }

    public static bool IsNavigationParentage(string semantic) => IsHierarchyEdge(semantic);

    public static bool IsNonParentageLink(string semantic)
    {
        var normalized = Normalize(semantic);
        return normalized is BelongsTo or ApplicableTo or AlternateViewMembership
            or VariantOf or AssetOf or BasedOn;
    }

    public static (string ChildId, string ParentId) OrientEndpoints(string semantic, string subjectId, string targetId)
    {
        if (string.Equals(semantic, Contains, StringComparison.Ordinal) ||
            string.Equals(semantic, ClassifiedUnder, StringComparison.Ordinal))
            return (targetId, subjectId);
        return (subjectId, targetId);
    }
}
