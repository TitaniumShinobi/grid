using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public enum CanonicalSelectorDropdownPlacement { AttachedBelow = 0 }
public enum CanonicalSelectorNavigationMode { ReplaceCurrentLevel = 0 }
public enum CanonicalSelectorOverflowBehavior { VerticalScroll = 0 }
public enum CanonicalSelectorOtherPlacement { RootOnly = 0 }

public sealed record CanonicalSelectorControlContract
{
    public const int CurrentContractVersion = 1;

    public CanonicalSelectorControlContract(
        int contractVersion,
        int closedHeightDip,
        CanonicalSelectorDropdownPlacement dropdownPlacement,
        CanonicalSelectorNavigationMode navigationMode,
        CanonicalSelectorOverflowBehavior overflowBehavior,
        CanonicalSelectorOtherPlacement otherPlacement,
        int otherFieldHeightDip,
        int otherFieldPreferredWidthDip,
        bool expandedTreeAllowed,
        bool detachedInspectorAllowed)
    {
        if (contractVersion != CurrentContractVersion) throw new ArgumentOutOfRangeException(nameof(contractVersion));
        if (closedHeightDip != 28 || otherFieldHeightDip != 28 || otherFieldPreferredWidthDip != 84)
            throw new ArgumentException("The v1 selector and unresolved-context control metrics are fixed at 28/28/84 DIP.");
        if (dropdownPlacement != CanonicalSelectorDropdownPlacement.AttachedBelow ||
            navigationMode != CanonicalSelectorNavigationMode.ReplaceCurrentLevel ||
            overflowBehavior != CanonicalSelectorOverflowBehavior.VerticalScroll ||
            otherPlacement != CanonicalSelectorOtherPlacement.RootOnly ||
            expandedTreeAllowed || detachedInspectorAllowed)
            throw new ArgumentException("The v1 selector requires attached, replace-level, root-Other navigation without expanded or detached surfaces.");
        ContractVersion = contractVersion;
        ClosedHeightDip = closedHeightDip;
        DropdownPlacement = dropdownPlacement;
        NavigationMode = navigationMode;
        OverflowBehavior = overflowBehavior;
        OtherPlacement = otherPlacement;
        OtherFieldHeightDip = otherFieldHeightDip;
        OtherFieldPreferredWidthDip = otherFieldPreferredWidthDip;
        ExpandedTreeAllowed = expandedTreeAllowed;
        DetachedInspectorAllowed = detachedInspectorAllowed;
    }

    public int ContractVersion { get; }
    public int ClosedHeightDip { get; }
    public CanonicalSelectorDropdownPlacement DropdownPlacement { get; }
    public CanonicalSelectorNavigationMode NavigationMode { get; }
    public CanonicalSelectorOverflowBehavior OverflowBehavior { get; }
    public CanonicalSelectorOtherPlacement OtherPlacement { get; }
    public int OtherFieldHeightDip { get; }
    public int OtherFieldPreferredWidthDip { get; }
    public bool ExpandedTreeAllowed { get; }
    public bool DetachedInspectorAllowed { get; }
    public bool CurrentNodeIsDropdownHeader => true;
    public bool BodyContainsImmediateChildrenOnly => true;
    public int BackNavigationLevelCount => 1;
    public bool OtherSelectionClosesDropdown => true;
    public bool OtherFieldIsSingleLine => true;

    public static CanonicalSelectorControlContract V1 { get; } = new(
        CurrentContractVersion, 28, CanonicalSelectorDropdownPlacement.AttachedBelow,
        CanonicalSelectorNavigationMode.ReplaceCurrentLevel, CanonicalSelectorOverflowBehavior.VerticalScroll,
        CanonicalSelectorOtherPlacement.RootOnly, 28, 84, false, false);
}

public static class CanonicalProjectionSemantics
{
    public static CanonicalSemanticRoleId SelectorPlayerAddressable { get; } = new("grid.selector.player-addressable");
    public static CanonicalSemanticRoleId MissionPlaylist { get; } = new("grid.missionquest.playlist");
    public static CanonicalSemanticRoleId ItemVehicles { get; } = new("grid.item.family.vehicles");
    public static CanonicalSemanticRoleId ActorNamedCharacter { get; } = new("grid.actor.type.named-character");
    public static CanonicalSemanticRoleId ActorGenericType { get; } = new("grid.actor.type.generic");
    public static CanonicalOrganizationalSemanticId ItemVehiclesNode { get; } = new("grid.selector.item.vehicles");
    public static CanonicalOrganizationalSemanticId ItemVehicleClassDimensionNode { get; } = new("grid.selector.item.dimension.vehicle-class");
    public static CanonicalOrganizationalSemanticId ItemVehicleClassValueNode { get; } = new("grid.selector.item.vehicle-class-value");
    public static CanonicalOrganizationalSemanticId ActorNamedCharacterNode { get; } = new("grid.selector.actor.named-character");
    public static CanonicalOrganizationalSemanticId ActorGenericTypeNode { get; } = new("grid.selector.actor.generic-type");
    public static CanonicalOrganizationalSemanticId ActorSourceCategoryDimensionNode { get; } = new("grid.selector.actor.dimension.source-category");
    public static CanonicalOrganizationalSemanticId ActorSourceCategoryValueNode { get; } = new("grid.selector.actor.source-category-value");
    public static CanonicalSemanticRoleId MissionDlc { get; } = new("grid.missionquest.family.dlc");
    public static CanonicalSemanticRoleId MissionMod { get; } = new("grid.missionquest.family.mod");
    public static CanonicalSemanticRoleId MissionOnline { get; } = new("grid.missionquest.family.online");
    public static CanonicalSemanticRoleId MissionStoryMode { get; } = new("grid.missionquest.family.story-mode");
    public static CanonicalSemanticRoleId ItemArmor { get; } = new("grid.item.family.armor");
    public static CanonicalSemanticRoleId ItemClothing { get; } = new("grid.item.family.clothing");
    public static CanonicalSemanticRoleId ItemClutterProps { get; } = new("grid.item.family.clutter-props");
    public static CanonicalSemanticRoleId ItemMagic { get; } = new("grid.item.family.magic");
    public static CanonicalSemanticRoleId ItemNature { get; } = new("grid.item.family.nature");
    public static CanonicalSemanticRoleId ItemWeapons { get; } = new("grid.item.family.weapons");
    public static CanonicalSemanticRoleId ActorNpc { get; } = new("grid.actor.role.npc");
    public static CanonicalSemanticRoleId ActorPlayerCharacter { get; } = new("grid.actor.role.player-character");

    public static CanonicalOrganizationalSemanticId Root(KnowledgeKind kind) =>
        new($"grid.selector.root.{kind.ToString().ToLowerInvariant()}");
    public static CanonicalOrganizationalSemanticId MissionDlcNode { get; } = new("grid.selector.missionquest.dlc");
    public static CanonicalOrganizationalSemanticId MissionModNode { get; } = new("grid.selector.missionquest.mod");
    public static CanonicalOrganizationalSemanticId MissionOnlineNode { get; } = new("grid.selector.missionquest.online");
    public static CanonicalOrganizationalSemanticId MissionStoryNode { get; } = new("grid.selector.missionquest.story-mode");
    public static CanonicalOrganizationalSemanticId MissionRegisteredModNode { get; } = new("grid.selector.missionquest.registered-mod");
    public static CanonicalOrganizationalSemanticId MissionActivityFamilyDimensionNode { get; } = new("grid.selector.missionquest.dimension.activity-family");
    public static CanonicalOrganizationalSemanticId MissionActivityFamilyValueNode { get; } = new("grid.selector.missionquest.activity-family-value");
    public static CanonicalOrganizationalSemanticId ItemArmorNode { get; } = new("grid.selector.item.armor");
    public static CanonicalOrganizationalSemanticId ItemClothingNode { get; } = new("grid.selector.item.clothing");
    public static CanonicalOrganizationalSemanticId ItemClutterPropsNode { get; } = new("grid.selector.item.clutter-props");
    public static CanonicalOrganizationalSemanticId ItemMagicNode { get; } = new("grid.selector.item.magic");
    public static CanonicalOrganizationalSemanticId ItemNatureNode { get; } = new("grid.selector.item.nature");
    public static CanonicalOrganizationalSemanticId ItemWeaponsNode { get; } = new("grid.selector.item.weapons");
    public static CanonicalOrganizationalSemanticId ItemSourceCategoryDimensionNode { get; } = new("grid.selector.item.dimension.source-category");
    public static CanonicalOrganizationalSemanticId ItemSourceCategoryValueNode { get; } = new("grid.selector.item.source-category-value");
    public static CanonicalOrganizationalSemanticId ActorNpcNode { get; } = new("grid.selector.actor.npc");
    public static CanonicalOrganizationalSemanticId ActorPlayerNode { get; } = new("grid.selector.actor.player-character");
    public static CanonicalOrganizationalSemanticId ActorDlcDimensionNode { get; } = new("grid.selector.actor.dimension.dlc");
    public static CanonicalOrganizationalSemanticId ActorFactionDimensionNode { get; } = new("grid.selector.actor.dimension.faction");
    public static CanonicalOrganizationalSemanticId ActorDlcValueNode { get; } = new("grid.selector.actor.dlc-value");
    public static CanonicalOrganizationalSemanticId ActorFactionValueNode { get; } = new("grid.selector.actor.faction-value");
    public static CanonicalOrganizationalSemanticId LocationUnresolvedHierarchyNode { get; } = new("grid.selector.location.unresolved-hierarchy");
}

public sealed record CanonicalOrganizationalDefinition(
    CanonicalOrganizationalSemanticId SemanticId,
    KnowledgeKind KnowledgeKind,
    string VerbatimPolicyLabel,
    CanonicalSemanticRoleId? RequiredRoleId,
    CanonicalOrganizationalSemanticId? ParentSemanticId)
{
    public string VerbatimPolicyLabel { get; } = Validate(VerbatimPolicyLabel);
    private static string Validate(string value)
    {
        value = CanonicalKnowledgeContract.RequireText(value, nameof(value));
        CanonicalUtf8.Validate(value);
        return value;
    }
}

public sealed record CanonicalSelectorProjectionPolicy
{
    public CanonicalSelectorProjectionPolicy(
        CanonicalSelectorProjectionPolicyId id,
        string exactVersion,
        CanonicalSelectorControlContract control,
        string orderingPolicyId,
        string orderingPolicyVersion,
        ImmutableArray<CanonicalOrganizationalDefinition> organizationalDefinitions)
    {
        Id = id;
        ExactVersion = CanonicalKnowledgeContract.RequireText(exactVersion, nameof(exactVersion));
        ArgumentNullException.ThrowIfNull(control);
        Control = control;
        OrderingPolicyId = CanonicalKnowledgeContract.RequireText(orderingPolicyId, nameof(orderingPolicyId));
        OrderingPolicyVersion = CanonicalKnowledgeContract.RequireText(orderingPolicyVersion, nameof(orderingPolicyVersion));
        if (organizationalDefinitions.IsDefault || organizationalDefinitions.Any(value => value is null))
            throw new ArgumentException("Organizational definitions must be initialized.", nameof(organizationalDefinitions));
        OrganizationalDefinitions = organizationalDefinitions
            .OrderBy(value => value.KnowledgeKind)
            .ThenBy(value => value.SemanticId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (OrganizationalDefinitions.Select(value => (value.KnowledgeKind, value.SemanticId)).Distinct().Count() !=
            OrganizationalDefinitions.Length)
            throw new ArgumentException("Organizational definitions must be distinct.", nameof(organizationalDefinitions));
    }

    public CanonicalSelectorProjectionPolicyId Id { get; }
    public string ExactVersion { get; }
    public CanonicalSelectorControlContract Control { get; }
    public string OrderingPolicyId { get; }
    public string OrderingPolicyVersion { get; }
    public ImmutableArray<CanonicalOrganizationalDefinition> OrganizationalDefinitions { get; }

    private static ImmutableArray<CanonicalOrganizationalDefinition> V1Definitions { get; } =
    [
        new(CanonicalProjectionSemantics.MissionDlcNode, KnowledgeKind.MissionQuest, "DLC", CanonicalProjectionSemantics.MissionDlc, null),
        new(CanonicalProjectionSemantics.MissionModNode, KnowledgeKind.MissionQuest, "Mod", CanonicalProjectionSemantics.MissionMod, null),
        new(CanonicalProjectionSemantics.MissionOnlineNode, KnowledgeKind.MissionQuest, "Online", CanonicalProjectionSemantics.MissionOnline, null),
        new(CanonicalProjectionSemantics.MissionStoryNode, KnowledgeKind.MissionQuest, "Story Mode", CanonicalProjectionSemantics.MissionStoryMode, null),
        new(CanonicalProjectionSemantics.ItemArmorNode, KnowledgeKind.Item, "Armor", CanonicalProjectionSemantics.ItemArmor, null),
        new(CanonicalProjectionSemantics.ItemClothingNode, KnowledgeKind.Item, "Clothing", CanonicalProjectionSemantics.ItemClothing, null),
        new(CanonicalProjectionSemantics.ItemClutterPropsNode, KnowledgeKind.Item, "Clutter/Props", CanonicalProjectionSemantics.ItemClutterProps, null),
        new(CanonicalProjectionSemantics.ItemMagicNode, KnowledgeKind.Item, "Magic", CanonicalProjectionSemantics.ItemMagic, null),
        new(CanonicalProjectionSemantics.ItemNatureNode, KnowledgeKind.Item, "Nature", CanonicalProjectionSemantics.ItemNature, null),
        new(CanonicalProjectionSemantics.ItemWeaponsNode, KnowledgeKind.Item, "Weapons", CanonicalProjectionSemantics.ItemWeapons, null),
        new(CanonicalProjectionSemantics.ActorNpcNode, KnowledgeKind.Actor, "NPC", CanonicalProjectionSemantics.ActorNpc, null),
        new(CanonicalProjectionSemantics.ActorPlayerNode, KnowledgeKind.Actor, "Player Character", CanonicalProjectionSemantics.ActorPlayerCharacter, null),
        new(CanonicalProjectionSemantics.ActorDlcDimensionNode, KnowledgeKind.Actor, "DLC", null, CanonicalProjectionSemantics.ActorNpcNode),
        new(CanonicalProjectionSemantics.ActorFactionDimensionNode, KnowledgeKind.Actor, "Faction", null, CanonicalProjectionSemantics.ActorNpcNode),
        new(CanonicalProjectionSemantics.LocationUnresolvedHierarchyNode, KnowledgeKind.Location, "Unresolved hierarchy", null, null),
    ];

    public static CanonicalSelectorProjectionPolicy V1 { get; } = new(
        new("grid.canonical-selector-projection"), "1", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        V1Definitions);

    public static CanonicalSelectorProjectionPolicy V2 { get; } = new(
        new("grid.canonical-selector-projection"), "2", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        V1Definitions.Add(new(
            CanonicalProjectionSemantics.ItemSourceCategoryValueNode,
            KnowledgeKind.Item,
            "Source category",
            null,
            CanonicalProjectionSemantics.ItemWeaponsNode)));

    public static CanonicalSelectorProjectionPolicy V3 { get; } = new(
        new("grid.canonical-selector-projection"), "3", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        V2.OrganizationalDefinitions.Add(new(
            CanonicalProjectionSemantics.MissionActivityFamilyValueNode,
            KnowledgeKind.MissionQuest,
            "Activity family",
            null,
            CanonicalProjectionSemantics.MissionOnlineNode)));

    public static CanonicalSelectorProjectionPolicy V4 { get; } = new(
        new("grid.canonical-selector-projection"), "4", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        V3.OrganizationalDefinitions.AddRange(new CanonicalOrganizationalDefinition[] {
            new(CanonicalProjectionSemantics.ItemVehiclesNode, KnowledgeKind.Item, "Vehicles", CanonicalProjectionSemantics.ItemVehicles, null),
            new(CanonicalProjectionSemantics.ItemVehicleClassValueNode, KnowledgeKind.Item, "Vehicle class", null, CanonicalProjectionSemantics.ItemVehiclesNode),
            new(CanonicalProjectionSemantics.ActorNamedCharacterNode, KnowledgeKind.Actor, "Named Characters", CanonicalProjectionSemantics.ActorNamedCharacter, CanonicalProjectionSemantics.ActorNpcNode),
            new(CanonicalProjectionSemantics.ActorGenericTypeNode, KnowledgeKind.Actor, "Generic Actor Types", CanonicalProjectionSemantics.ActorGenericType, CanonicalProjectionSemantics.ActorNpcNode),
            new(CanonicalProjectionSemantics.ActorSourceCategoryValueNode, KnowledgeKind.Actor, "Source category", null, CanonicalProjectionSemantics.ActorGenericTypeNode),
        }));

    private static ImmutableArray<CanonicalOrganizationalDefinition> GtaEnhancedSelectorDefinitions { get; } =
        V1Definitions
            .Where(value => value.KnowledgeKind is not KnowledgeKind.Item ||
                            value.SemanticId == CanonicalProjectionSemantics.ItemWeaponsNode ||
                            value.SemanticId == CanonicalProjectionSemantics.ItemClothingNode)
            .Concat(V2.OrganizationalDefinitions.Where(value =>
                value.SemanticId == CanonicalProjectionSemantics.ItemSourceCategoryValueNode))
            .Concat(V3.OrganizationalDefinitions.Where(value =>
                value.SemanticId == CanonicalProjectionSemantics.MissionActivityFamilyValueNode))
            .Concat(V4.OrganizationalDefinitions.Where(value =>
                value.SemanticId == CanonicalProjectionSemantics.ItemVehiclesNode ||
                value.SemanticId == CanonicalProjectionSemantics.ItemVehicleClassValueNode ||
                value.SemanticId == CanonicalProjectionSemantics.ActorNamedCharacterNode ||
                value.SemanticId == CanonicalProjectionSemantics.ActorGenericTypeNode ||
                value.SemanticId == CanonicalProjectionSemantics.ActorSourceCategoryValueNode))
            .DistinctBy(value => (value.KnowledgeKind, value.SemanticId))
            .OrderBy(value => value.KnowledgeKind)
            .ThenBy(value => value.SemanticId.Value, StringComparer.Ordinal)
            .ToImmutableArray();

    /// <summary>GTA-specific fourth-row taxonomy (Mission/Location/Actor org + GTA item families only).</summary>
    public static CanonicalSelectorProjectionPolicy GtaEnhanced { get; } = new(
        new("grid.canonical-selector-projection"), "5", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        GtaEnhancedSelectorDefinitions);

    public static CanonicalSelectorProjectionPolicy Current => V4;

    /// <summary>Prepared Location presentation requires resolved localized names; other kinds retain their policy.</summary>
    public static CanonicalSelectorProjectionPolicy LocationPrepared { get; } = new(
        new("grid.canonical-selector-projection"), "6", CanonicalSelectorControlContract.V1,
        "grid.selector.order.exact-unicode-scalar", "1",
        GtaEnhancedSelectorDefinitions);

    public bool RequiresNamedLocationPresentation => string.Equals(ExactVersion, "6", StringComparison.Ordinal);

    public bool UsesGtaEnhancedRecordAdmission =>
        string.Equals(ExactVersion, "4", StringComparison.Ordinal) ||
        string.Equals(ExactVersion, "5", StringComparison.Ordinal) ||
        string.Equals(ExactVersion, "6", StringComparison.Ordinal);

    public bool SupportsActorNamedGenericNpcOrganization =>
        OrganizationalDefinitions.Any(value => value.SemanticId == CanonicalProjectionSemantics.ActorNamedCharacterNode);
}

public sealed record CanonicalSemanticClassificationAssertion
{
    [JsonConstructor]
    public CanonicalSemanticClassificationAssertion(
        CanonicalSemanticClassificationAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalSemanticRoleId roleId,
        string vocabularyId,
        string vocabularyVersion,
        string classificationMethodId,
        string classificationMethodVersion,
        string sourceFieldPath)
    {
        VocabularyId = CanonicalKnowledgeContract.RequireText(vocabularyId, nameof(vocabularyId));
        VocabularyVersion = CanonicalKnowledgeContract.RequireText(vocabularyVersion, nameof(vocabularyVersion));
        ClassificationMethodId = CanonicalKnowledgeContract.RequireText(classificationMethodId, nameof(classificationMethodId));
        ClassificationMethodVersion = CanonicalKnowledgeContract.RequireText(classificationMethodVersion, nameof(classificationMethodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        var expected = CanonicalSemanticClassificationAssertionId.DeriveV1(
            knowledgeRecordId, sourceRevisionId, roleId, VocabularyId, VocabularyVersion,
            ClassificationMethodId, ClassificationMethodVersion, SourceFieldPath);
        if (id != expected) throw new ArgumentException("Classification assertion ID does not match its exact claim.", nameof(id));
        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        RoleId = roleId;
    }

    public CanonicalSemanticClassificationAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public CanonicalSemanticRoleId RoleId { get; }
    public string VocabularyId { get; }
    public string VocabularyVersion { get; }
    public string ClassificationMethodId { get; }
    public string ClassificationMethodVersion { get; }
    public string SourceFieldPath { get; }
}

public enum CanonicalRecordContributionKind { Introduced = 0, Modified = 1, Deleted = 2 }

public sealed record CanonicalRecordContributionAssertion
{
    [JsonConstructor]
    public CanonicalRecordContributionAssertion(
        CanonicalRecordContributionAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalRecordContributionKind contributionKind,
        KnowledgeRecordId? originKnowledgeRecordId,
        SourceNativeIdentifier? exactModIdentity,
        SourceNativeVersion? exactModVersion,
        string methodId,
        string methodVersion,
        string sourceFieldPath)
    {
        if (!Enum.IsDefined(contributionKind)) throw new ArgumentOutOfRangeException(nameof(contributionKind));
        if (exactModIdentity is null != (exactModVersion is null))
            throw new ArgumentException("Mod identity and version must be supplied together.");
        if (contributionKind == CanonicalRecordContributionKind.Modified && originKnowledgeRecordId is null)
            throw new ArgumentException("A modification must retain its origin record.", nameof(originKnowledgeRecordId));
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        var expected = CanonicalRecordContributionAssertionId.DeriveV1(
            knowledgeRecordId, sourceRevisionId, contributionKind, originKnowledgeRecordId,
            exactModIdentity, exactModVersion, MethodId, MethodVersion, SourceFieldPath);
        if (id != expected) throw new ArgumentException("Contribution assertion ID does not match its exact claim.", nameof(id));
        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        ContributionKind = contributionKind;
        OriginKnowledgeRecordId = originKnowledgeRecordId;
        ExactModIdentity = exactModIdentity;
        ExactModVersion = exactModVersion;
    }

    public CanonicalRecordContributionAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public CanonicalRecordContributionKind ContributionKind { get; }
    public KnowledgeRecordId? OriginKnowledgeRecordId { get; }
    public SourceNativeIdentifier? ExactModIdentity { get; }
    public SourceNativeVersion? ExactModVersion { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record CanonicalOrganizationalValueAssertion
{
    [JsonConstructor]
    public CanonicalOrganizationalValueAssertion(
        CanonicalOrganizationalValueAssertionId id,
        KnowledgeRecordId knowledgeRecordId,
        CatalogSourceRevisionId sourceRevisionId,
        CanonicalOrganizationalSemanticId dimensionId,
        SourceNativeIdentifier exactValueIdentity,
        string? verbatimDisplayValue,
        string methodId,
        string methodVersion,
        string sourceFieldPath,
        string? languageTag = null)
    {
        ArgumentNullException.ThrowIfNull(exactValueIdentity);
        if (verbatimDisplayValue is not null) CanonicalUtf8.Validate(verbatimDisplayValue);
        MethodId = CanonicalKnowledgeContract.RequireText(methodId, nameof(methodId));
        MethodVersion = CanonicalKnowledgeContract.RequireText(methodVersion, nameof(methodVersion));
        SourceFieldPath = CanonicalKnowledgeContract.RequireText(sourceFieldPath, nameof(sourceFieldPath));
        LanguageTag = languageTag is null ? null : new CanonicalTerminologyLocalePreference(languageTag, []).RequestedLanguageTag;
        var expected = languageTag is null
            ? CanonicalOrganizationalValueAssertionId.DeriveV1(knowledgeRecordId, sourceRevisionId, dimensionId,
                exactValueIdentity, verbatimDisplayValue, MethodId, MethodVersion, SourceFieldPath)
            : CanonicalOrganizationalValueAssertionId.DeriveV2(knowledgeRecordId, sourceRevisionId, dimensionId,
                exactValueIdentity, verbatimDisplayValue, MethodId, MethodVersion, SourceFieldPath, languageTag);
        if (id != expected) throw new ArgumentException("Organizational value assertion ID does not match its exact claim.", nameof(id));
        Id = id;
        KnowledgeRecordId = knowledgeRecordId;
        SourceRevisionId = sourceRevisionId;
        DimensionId = dimensionId;
        ExactValueIdentity = exactValueIdentity;
        VerbatimDisplayValue = verbatimDisplayValue;
    }

    public CanonicalOrganizationalValueAssertionId Id { get; }
    public KnowledgeRecordId KnowledgeRecordId { get; }
    public CatalogSourceRevisionId SourceRevisionId { get; }
    public CanonicalOrganizationalSemanticId DimensionId { get; }
    public SourceNativeIdentifier ExactValueIdentity { get; }
    public string? VerbatimDisplayValue { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LanguageTag { get; }
    public string MethodId { get; }
    public string MethodVersion { get; }
    public string SourceFieldPath { get; }
}

public sealed record CanonicalApplicabilityProjection
{
    public CanonicalApplicabilityProjection(
        CatalogCompositionId compositionId,
        ImmutableArray<KnowledgeRecordId> applicableKnowledgeRecordIds,
        ImmutableArray<KnowledgeRecordId> suppressedKnowledgeRecordIds,
        string exactPolicyVersion)
    {
        CompositionId = compositionId;
        ExactPolicyVersion = CanonicalKnowledgeContract.RequireText(exactPolicyVersion, nameof(exactPolicyVersion));
        ApplicableKnowledgeRecordIds = Normalize(applicableKnowledgeRecordIds, nameof(applicableKnowledgeRecordIds));
        SuppressedKnowledgeRecordIds = Normalize(suppressedKnowledgeRecordIds, nameof(suppressedKnowledgeRecordIds));
        if (ApplicableKnowledgeRecordIds.Intersect(SuppressedKnowledgeRecordIds).Any())
            throw new ArgumentException("A record cannot be both applicable and suppressed.");
    }

    public CatalogCompositionId CompositionId { get; }
    public ImmutableArray<KnowledgeRecordId> ApplicableKnowledgeRecordIds { get; }
    public ImmutableArray<KnowledgeRecordId> SuppressedKnowledgeRecordIds { get; }
    public string ExactPolicyVersion { get; }
    public bool IsApplicable(KnowledgeRecordId id) => ApplicableKnowledgeRecordIds.Contains(id);

    private static ImmutableArray<KnowledgeRecordId> Normalize(ImmutableArray<KnowledgeRecordId> values, string name)
    {
        if (values.IsDefault) throw new ArgumentException("Collection must be initialized.", name);
        var ordered = values.OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray();
        if (ordered.Distinct().Count() != ordered.Length) throw new ArgumentException("Record IDs must be distinct.", name);
        return ordered;
    }
}

public enum CanonicalNavigationNodeKind { Organizational = 0, CanonicalRecordReference = 1 }
public enum CanonicalNavigationDisplayKind { PolicyLabel = 0, CanonicalTerminology = 1, NativeIdentifier = 2, SourceValue = 3 }

public sealed record CanonicalRecordNavigationReference(
    CanonicalNavigationPathId PathId,
    KnowledgeRecordId KnowledgeRecordId,
    ImmutableArray<string> SupportingProjectionAssertionIds);

public sealed record CanonicalNavigationNode
{
    public CanonicalNavigationNode(
        CanonicalNavigationNodeId nodeId,
        CanonicalNavigationPathId pathId,
        CanonicalNavigationPathId? parentPathId,
        int depth,
        CanonicalNavigationNodeKind nodeKind,
        CanonicalOrganizationalNodeId? organizationalNodeId,
        CanonicalOrganizationalSemanticId? organizationalSemanticId,
        KnowledgeRecordId? knowledgeRecordId,
        string displayAnchor,
        CanonicalNavigationDisplayKind displayKind,
        ImmutableArray<TerminologyAssertion> exactTerminologyAssertions,
        SourceNativeIdentifier? nativeIdentity,
        bool isSelectable,
        bool canDescend,
        bool hasTerminologyConflict)
    {
        if (depth < 0) throw new ArgumentOutOfRangeException(nameof(depth));
        ArgumentNullException.ThrowIfNull(displayAnchor);
        CanonicalUtf8.Validate(displayAnchor);
        if (exactTerminologyAssertions.IsDefault || exactTerminologyAssertions.Any(value => value is null))
            throw new ArgumentException("Terminology assertions must be initialized.", nameof(exactTerminologyAssertions));
        if (nodeKind == CanonicalNavigationNodeKind.Organizational &&
            (organizationalNodeId is null || organizationalSemanticId is null || knowledgeRecordId is not null || isSelectable))
            throw new ArgumentException("Organizational nodes cannot carry or select a canonical record.");
        if (nodeKind == CanonicalNavigationNodeKind.CanonicalRecordReference &&
            (knowledgeRecordId is null || organizationalNodeId is not null || organizationalSemanticId is not null))
            throw new ArgumentException("Canonical record nodes require exactly one KnowledgeRecordId.");
        NodeId = nodeId;
        PathId = pathId;
        ParentPathId = parentPathId;
        Depth = depth;
        NodeKind = nodeKind;
        OrganizationalNodeId = organizationalNodeId;
        OrganizationalSemanticId = organizationalSemanticId;
        KnowledgeRecordId = knowledgeRecordId;
        DisplayAnchor = displayAnchor;
        DisplayKind = displayKind;
        ExactTerminologyAssertions = exactTerminologyAssertions;
        NativeIdentity = nativeIdentity;
        IsSelectable = isSelectable;
        CanDescend = canDescend;
        HasTerminologyConflict = hasTerminologyConflict;
    }

    public CanonicalNavigationNodeId NodeId { get; }
    public CanonicalNavigationPathId PathId { get; }
    public CanonicalNavigationPathId? ParentPathId { get; }
    public int Depth { get; }
    public CanonicalNavigationNodeKind NodeKind { get; }
    public CanonicalOrganizationalNodeId? OrganizationalNodeId { get; }
    public CanonicalOrganizationalSemanticId? OrganizationalSemanticId { get; }
    public KnowledgeRecordId? KnowledgeRecordId { get; }
    public string DisplayAnchor { get; }
    public CanonicalNavigationDisplayKind DisplayKind { get; }
    public ImmutableArray<TerminologyAssertion> ExactTerminologyAssertions { get; }
    public SourceNativeIdentifier? NativeIdentity { get; }
    public bool IsSelectable { get; }
    public bool CanDescend { get; }
    public bool HasTerminologyConflict { get; }
}

public sealed record CanonicalTerminologyLocalePreference
{
    public CanonicalTerminologyLocalePreference(
        string requestedLanguageTag,
        ImmutableArray<string> approvedLanguageFallbackTags)
    {
        RequestedLanguageTag = ValidatePresentationLanguageTag(
            requestedLanguageTag, nameof(requestedLanguageTag));
        if (approvedLanguageFallbackTags.IsDefault)
            throw new ArgumentException("Approved language fallbacks must be initialized.",
                nameof(approvedLanguageFallbackTags));

        var seen = new HashSet<string>(StringComparer.Ordinal) { RequestedLanguageTag };
        var fallbacks = ImmutableArray.CreateBuilder<string>(approvedLanguageFallbackTags.Length);
        foreach (var value in approvedLanguageFallbackTags)
        {
            var fallback = ValidatePresentationLanguageTag(value, nameof(approvedLanguageFallbackTags));
            if (!seen.Add(fallback))
                throw new ArgumentException(
                    "Requested and fallback language tags must be distinct.",
                    nameof(approvedLanguageFallbackTags));
            fallbacks.Add(fallback);
        }
        ApprovedLanguageFallbackTags = fallbacks.ToImmutable();
    }

    public string RequestedLanguageTag { get; }
    public ImmutableArray<string> ApprovedLanguageFallbackTags { get; }

    private static string ValidatePresentationLanguageTag(string value, string parameterName)
    {
        value = CanonicalKnowledgeContract.RequireText(value, parameterName);
        CanonicalUtf8.Validate(value);
        if (string.Equals(value, "und", StringComparison.OrdinalIgnoreCase) ||
            value[0] == '-' || value[^1] == '-' || value.Contains("--", StringComparison.Ordinal) ||
            value.Any(character => character != '-' && !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException(
                "A presentation language must be an explicit language tag other than 'und'.",
                parameterName);
        return value;
    }
}

public sealed record CanonicalSelectorQuery(
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    KnowledgeKind KnowledgeKind,
    CanonicalSelectorProjectionPolicyId ProjectionPolicyId,
    string ExactProjectionPolicyVersion,
    CanonicalNavigationPathId? CurrentPathId,
    string? SearchText,
    bool IncludeIdentifierOnly,
    bool InspectionMode,
    CanonicalTerminologyLocalePreference TerminologyLocale);

public sealed record CanonicalSelectorResult(
    CatalogRevisionId CatalogRevisionId,
    CatalogCompositionId CatalogCompositionId,
    KnowledgeKind KnowledgeKind,
    CanonicalSelectorProjectionPolicyId ProjectionPolicyId,
    string ExactProjectionPolicyVersion,
    CanonicalNavigationPathId RootPathId,
    CanonicalNavigationPathId CurrentPathId,
    CanonicalNavigationPathId? ParentPathId,
    CanonicalNavigationNode CurrentNode,
    ImmutableArray<CanonicalNavigationNode> ImmediateChildren,
    KnowledgeCoverageState CoverageState,
    CanonicalTerminologyLocalePreference TerminologyLocale);

public enum CanonicalSelectorSelectionKind { CanonicalRecord = 0, OtherContext = 1 }

public sealed record CanonicalSelectorSelection
{
    public CanonicalSelectorSelection(
        CanonicalSelectorSelectionKind selectionKind,
        KnowledgeKind knowledgeKind,
        CatalogRevisionId catalogRevisionId,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicyId projectionPolicyId,
        string projectionPolicyVersion,
        CanonicalNavigationPathId? selectedPathId,
        KnowledgeRecordId? knowledgeRecordId,
        string? unresolvedOtherContextId)
    {
        ProjectionPolicyVersion = CanonicalKnowledgeContract.RequireText(projectionPolicyVersion, nameof(projectionPolicyVersion));
        if (selectionKind == CanonicalSelectorSelectionKind.CanonicalRecord &&
            (selectedPathId is null || knowledgeRecordId is null || unresolvedOtherContextId is not null))
            throw new ArgumentException("A canonical selection requires path and record IDs only.");
        if (selectionKind == CanonicalSelectorSelectionKind.OtherContext &&
            (selectedPathId is not null || knowledgeRecordId is not null || string.IsNullOrEmpty(unresolvedOtherContextId)))
            throw new ArgumentException("An Other selection requires only an unresolved ticket-context ID.");
        SelectionKind = selectionKind;
        KnowledgeKind = knowledgeKind;
        CatalogRevisionId = catalogRevisionId;
        CatalogCompositionId = catalogCompositionId;
        ProjectionPolicyId = projectionPolicyId;
        SelectedPathId = selectedPathId;
        KnowledgeRecordId = knowledgeRecordId;
        UnresolvedOtherContextId = unresolvedOtherContextId;
    }

    public CanonicalSelectorSelectionKind SelectionKind { get; }
    public KnowledgeKind KnowledgeKind { get; }
    public CatalogRevisionId CatalogRevisionId { get; }
    public CatalogCompositionId CatalogCompositionId { get; }
    public CanonicalSelectorProjectionPolicyId ProjectionPolicyId { get; }
    public string ProjectionPolicyVersion { get; }
    public CanonicalNavigationPathId? SelectedPathId { get; }
    public KnowledgeRecordId? KnowledgeRecordId { get; }
    public string? UnresolvedOtherContextId { get; }
}

/// <summary>
/// Immutable projection material taken from one structurally verified semantic package.
/// The internal constructor prevents callers from pairing free-floating assertions with an
/// unrelated or unverified evidence set. Use <see cref="Services.CanonicalSelectorProjectionEngine.CreateVerifiedInput"/>.
/// </summary>
public sealed class CanonicalSelectorProjectionInput
{
    internal CanonicalSelectorProjectionInput(
        CanonicalCatalogPackage verifiedPackage,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicy policy,
        CanonicalApplicabilityProjection applicability)
    {
        ArgumentNullException.ThrowIfNull(verifiedPackage);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(applicability);
        VerifiedPackage = verifiedPackage;
        CatalogRevisionId = verifiedPackage.Manifest.CatalogRevisionId;
        CatalogCompositionId = catalogCompositionId;
        Policy = policy;
        CoverageState = verifiedPackage.Payload.EffectiveCoverage;
        KnowledgeRecords = verifiedPackage.Payload.KnowledgeRecords;
        TerminologyAssertions = verifiedPackage.Payload.TerminologyAssertions;
        RelationshipAssertions = verifiedPackage.Payload.RelationshipAssertions;
        SourceRevisions = verifiedPackage.Payload.SourceRevisions;
        SemanticClassifications = verifiedPackage.Payload.SemanticClassificationAssertions;
        ContributionAssertions = verifiedPackage.Payload.RecordContributionAssertions;
        OrganizationalValueAssertions = verifiedPackage.Payload.OrganizationalValueAssertions;
        CrossSourceAssertions = verifiedPackage.Payload.CrossSourceAssertions;
        Applicability = applicability;
    }

    public CanonicalCatalogPackage VerifiedPackage { get; }
    public CatalogRevisionId CatalogRevisionId { get; }
    public CatalogCompositionId CatalogCompositionId { get; }
    public CanonicalSelectorProjectionPolicy Policy { get; }
    public KnowledgeCoverageState CoverageState { get; }
    public ImmutableArray<CanonicalKnowledgeRecord> KnowledgeRecords { get; }
    public ImmutableArray<TerminologyAssertion> TerminologyAssertions { get; }
    public ImmutableArray<RelationshipAssertion> RelationshipAssertions { get; }
    public ImmutableArray<AdapterBoundCatalogSourceRevisionRecord> SourceRevisions { get; }
    public ImmutableArray<CanonicalSemanticClassificationAssertion> SemanticClassifications { get; }
    public ImmutableArray<CanonicalRecordContributionAssertion> ContributionAssertions { get; }
    public ImmutableArray<CanonicalOrganizationalValueAssertion> OrganizationalValueAssertions { get; }
    public ImmutableArray<CrossSourceCanonicalAssertion> CrossSourceAssertions { get; }
    public CanonicalApplicabilityProjection Applicability { get; }
    internal Services.CanonicalSelectorProjectionState? RuntimeState { get; private set; }
    internal void InitializeRuntimeState() => RuntimeState = new(this);
}
