using System.Collections.Immutable;
using System.Text;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Deterministically projects already-registered canonical assertions. It performs no
/// extraction, discovery, inference, persistence, terminology generation, or web access.
/// </summary>
public static class CanonicalSelectorProjectionEngine
{
    /// <summary>
    /// Creates a projection input from an exact package retained by a successfully validated
    /// shared-catalog load. Runtime callers therefore consume persisted canonical content and do
    /// not need extraction, discovery, or direct package-file access.
    /// </summary>
    public static CanonicalSelectorProjectionInput CreateVerifiedInput(
        CanonicalCatalogLoadResult catalog,
        CatalogPackageId catalogPackageId,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicy policy,
        CanonicalApplicabilityProjection applicability)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (!catalog.IsValid)
            throw new InvalidDataException(
                $"Selector projection requires a valid shared-catalog snapshot: {string.Join("; ", catalog.Issues)}");
        var package = catalog.Snapshot.FindImportedPackage(catalogPackageId) ??
                      throw new InvalidDataException("The selected canonical package is not present in the shared catalog.");
        return CreateVerifiedInput(package, catalogCompositionId, policy, applicability);
    }

    public static CanonicalSelectorProjectionInput CreateVerifiedInput(
        CanonicalCatalogPackage package,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicy policy,
        CanonicalApplicabilityProjection applicability)
    {
        ArgumentNullException.ThrowIfNull(package);
        var verification = CanonicalCatalogPackageKernel.Verify(package);
        if (!verification.IsStructurallyValid)
            throw new InvalidDataException(
                $"Selector projection requires a structurally verified catalog package: {string.Join("; ", verification.Issues)}");
        if (package.Manifest.PackageSchemaVersion is not (
                CatalogPackageManifest.ProjectionContractSchemaVersion or
                CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            throw new InvalidDataException("Selector projection requires an evidence-closed semantic payload.");
        if (catalogCompositionId != applicability.CompositionId)
            throw new ArgumentException("Projection composition and applicability coordinates must match.", nameof(applicability));
        return new CanonicalSelectorProjectionInput(package, catalogCompositionId, policy, applicability);
    }

    public static CanonicalSelectorResult Query(
        CanonicalSelectorProjectionInput input,
        CanonicalSelectorQuery query)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(query);
        if (query.CatalogRevisionId != input.CatalogRevisionId ||
            query.CatalogCompositionId != input.CatalogCompositionId ||
            query.CatalogCompositionId != input.Applicability.CompositionId ||
            query.ProjectionPolicyId != input.Policy.Id ||
            !string.Equals(query.ExactProjectionPolicyVersion, input.Policy.ExactVersion, StringComparison.Ordinal))
            throw new ArgumentException("Selector query coordinates do not match the frozen projection input.", nameof(query));

        ValidateInput(input);
        var graph = BuildGraph(input, query.KnowledgeKind, query.IncludeIdentifierOnly);
        var current = query.CurrentPathId is null
            ? graph.Root
            : graph.ByPath.GetValueOrDefault(query.CurrentPathId.Value) ??
              throw new ArgumentException("The requested navigation path is not present in this projection.", nameof(query));
        var children = Order(current.Children)
            .Where(value => MatchesSearch(value, query.SearchText))
            .Select(ToContract)
            .ToImmutableArray();
        return new CanonicalSelectorResult(
            input.CatalogRevisionId,
            input.CatalogCompositionId,
            query.KnowledgeKind,
            input.Policy.Id,
            input.Policy.ExactVersion,
            graph.Root.PathId,
            current.PathId,
            current.Parent?.PathId,
            ToContract(current),
            children,
            input.CoverageState);
    }

    public static CanonicalNavigationPathId? Back(CanonicalSelectorResult result) =>
        result.ParentPathId;

    public static CanonicalSelectorSelection Select(CanonicalSelectorResult result, CanonicalNavigationPathId pathId)
    {
        var node = result.ImmediateChildren.SingleOrDefault(value => value.PathId == pathId) ??
                   throw new ArgumentException("Only an immediate visible child can be selected.", nameof(pathId));
        if (!node.IsSelectable || node.KnowledgeRecordId is not { } recordId)
            throw new ArgumentException("Organizational nodes cannot be committed as canonical selections.", nameof(pathId));
        return new CanonicalSelectorSelection(
            CanonicalSelectorSelectionKind.CanonicalRecord,
            result.KnowledgeKind,
            result.CatalogRevisionId,
            result.CatalogCompositionId,
            result.ProjectionPolicyId,
            result.ExactProjectionPolicyVersion,
            node.PathId,
            recordId,
            null);
    }

    private static ProjectionGraph BuildGraph(
        CanonicalSelectorProjectionInput input,
        KnowledgeKind kind,
        bool includeIdentifierOnly)
    {
        var rootSemantic = CanonicalProjectionSemantics.Root(kind);
        var rootOrganizationId = CanonicalOrganizationalNodeId.DeriveV1(
            input.Policy.Id, input.Policy.ExactVersion, kind, rootSemantic, null);
        var rootNodeId = CanonicalNavigationNodeId.ForOrganizationalNode(rootOrganizationId);
        var rootPath = CanonicalNavigationPathId.DeriveV1(
            input.CatalogRevisionId, input.CatalogCompositionId, input.Policy.Id,
            input.Policy.ExactVersion, kind, [rootNodeId]);
        var root = BuilderNode.Organizational(
            rootNodeId, rootPath, null, 0, rootOrganizationId, rootSemantic, RootLabel(kind),
            CanonicalNavigationDisplayKind.PolicyLabel, [rootNodeId]);
        var graph = new ProjectionGraph(kind, root);

        var records = input.KnowledgeRecords
            .Where(value => value.Kind == kind && input.Applicability.IsApplicable(value.Id))
            .Where(value => !IsEffectiveDuplicateOrDeleted(value.Id, input.ContributionAssertions))
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!includeIdentifierOnly)
            records = records.Where(record => HasPrimaryTerminology(record.Id, input.TerminologyAssertions)).ToImmutableArray();

        switch (kind)
        {
            case KnowledgeKind.Location:
                ProjectLocations(input, graph, records);
                break;
            case KnowledgeKind.MissionQuest:
                ProjectMissionQuests(input, graph, records);
                break;
            case KnowledgeKind.Item:
                ProjectItems(input, graph, records);
                break;
            case KnowledgeKind.Actor:
                ProjectActors(input, graph, records);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
        graph.Index();
        return graph;
    }

    private static void ProjectLocations(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records)
    {
        var recordMap = records.ToDictionary(value => value.Id);
        var strict = input.RelationshipAssertions.Where(value =>
                recordMap.ContainsKey(value.SubjectKnowledgeRecordId) &&
                LocationRelationshipSemantics.IsStrictHierarchy(value.SemanticId))
            .ToImmutableArray();
        var resolvedParents = strict
            .Where(value => value.ResolvedTargetKnowledgeRecordId is { } target && recordMap.ContainsKey(target))
            .GroupBy(value => value.SubjectKnowledgeRecordId)
            .ToDictionary(
                value => value.Key,
                value => value.Select(item => item.ResolvedTargetKnowledgeRecordId!.Value).Distinct().ToImmutableArray());
        var children = resolvedParents
            .SelectMany(value => value.Value.Select(parent => (Parent: parent, Child: value.Key)))
            .GroupBy(value => value.Parent)
            .ToDictionary(value => value.Key, value => value.Select(item => item.Child).Distinct().ToImmutableArray());

        void AddLocationTree(BuilderNode parent, KnowledgeRecordId recordId, ImmutableHashSet<KnowledgeRecordId> ancestors)
        {
            if (ancestors.Contains(recordId))
                throw new InvalidDataException("Authoritative Location hierarchy contains a cycle.");
            var node = AddRecord(input, graph, parent, recordMap[recordId]);
            if (children.TryGetValue(recordId, out var childIds))
                foreach (var childId in childIds.OrderBy(value => value.Value, StringComparer.Ordinal))
                    AddLocationTree(node, childId, ancestors.Add(recordId));
        }

        foreach (var rootRecord in records.Where(value =>
                     !resolvedParents.TryGetValue(value.Id, out var parents) || parents.IsEmpty))
            AddLocationTree(graph.Root, rootRecord.Id, ImmutableHashSet<KnowledgeRecordId>.Empty);

        var unresolvedIds = strict
            .Where(value => value.ResolvedTargetKnowledgeRecordId is null)
            .Select(value => value.SubjectKnowledgeRecordId)
            .Distinct()
            .OrderBy(value => value.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!unresolvedIds.IsEmpty)
        {
            var unresolved = AddStaticOrganization(
                input, graph, graph.Root, CanonicalProjectionSemantics.LocationUnresolvedHierarchyNode);
            foreach (var recordId in unresolvedIds) AddRecord(input, graph, unresolved, recordMap[recordId]);
        }
    }

    private static void ProjectMissionQuests(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records)
    {
        foreach (var record in records)
        {
            var roles = Roles(record.Id, input.SemanticClassifications);
            var placed = false;
            foreach (var role in roles)
            {
                var semantic = RoleNode(KnowledgeKind.MissionQuest, role);
                if (semantic is null) continue;
                if (role == CanonicalProjectionSemantics.MissionMod)
                {
                    var introductions = input.ContributionAssertions.Where(value =>
                            value.KnowledgeRecordId == record.Id &&
                            value.ContributionKind == CanonicalRecordContributionKind.Introduced &&
                            value.ExactModIdentity is not null && value.ExactModVersion is not null)
                        .OrderBy(value => value.ExactModIdentity!.ExactRepresentation, StringComparer.Ordinal)
                        .ToImmutableArray();
                    foreach (var contribution in introductions)
                    {
                        var modRoot = AddStaticOrganization(input, graph, graph.Root, semantic.Value);
                        var modNode = AddDynamicOrganization(
                            input, graph, modRoot, CanonicalProjectionSemantics.MissionRegisteredModNode,
                            contribution.ExactModIdentity!, contribution.ExactModIdentity!.ExactRepresentation, false);
                        AddRecord(input, graph, modNode, record);
                        placed = true;
                    }
                }
                else
                {
                    var familyRoot = AddStaticOrganization(input, graph, graph.Root, semantic.Value);
                    if (role == CanonicalProjectionSemantics.MissionOnline &&
                        input.Policy.OrganizationalDefinitions.Any(value =>
                            value.KnowledgeKind == KnowledgeKind.MissionQuest &&
                            value.SemanticId == CanonicalProjectionSemantics.MissionActivityFamilyValueNode &&
                            value.ParentSemanticId == CanonicalProjectionSemantics.MissionOnlineNode))
                    {
                        var activities = input.OrganizationalValueAssertions
                            .Where(value => value.KnowledgeRecordId == record.Id &&
                                            value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode)
                            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                            .ToImmutableArray();
                        if (!activities.IsEmpty)
                        {
                            foreach (var activity in activities)
                            {
                                var activityNode = AddDynamicOrganization(
                                    input, graph, familyRoot,
                                    CanonicalProjectionSemantics.MissionActivityFamilyValueNode,
                                    activity.ExactValueIdentity,
                                    activity.VerbatimDisplayValue ?? activity.ExactValueIdentity.ExactRepresentation,
                                    activity.VerbatimDisplayValue is not null);
                                AddRecord(input, graph, activityNode, record);
                            }
                            placed = true;
                            continue;
                        }
                    }
                    AddRecord(input, graph, familyRoot, record);
                    placed = true;
                }
            }
            if (!placed) AddRecord(input, graph, graph.Root, record);
        }
    }

    private static void ProjectItems(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records)
    {
        foreach (var record in records)
        {
            var nodes = Roles(record.Id, input.SemanticClassifications)
                .Select(role => RoleNode(KnowledgeKind.Item, role))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .Distinct()
                .ToImmutableArray();
            if (nodes.IsEmpty)
            {
                AddRecord(input, graph, graph.Root, record);
                continue;
            }

            foreach (var node in nodes)
            {
                var familyNode = AddStaticOrganization(input, graph, graph.Root, node);
                if (node != CanonicalProjectionSemantics.ItemWeaponsNode ||
                    !input.Policy.OrganizationalDefinitions.Any(value =>
                        value.KnowledgeKind == KnowledgeKind.Item &&
                        value.SemanticId == CanonicalProjectionSemantics.ItemSourceCategoryValueNode &&
                        value.ParentSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode))
                {
                    AddRecord(input, graph, familyNode, record);
                    continue;
                }

                var categories = input.OrganizationalValueAssertions
                    .Where(value =>
                        value.KnowledgeRecordId == record.Id &&
                        value.DimensionId == CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode)
                    .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                if (categories.IsEmpty)
                {
                    AddRecord(input, graph, familyNode, record);
                    continue;
                }

                foreach (var category in categories)
                {
                    var valueNode = AddDynamicOrganization(
                        input, graph, familyNode,
                        CanonicalProjectionSemantics.ItemSourceCategoryValueNode,
                        category.ExactValueIdentity,
                        category.VerbatimDisplayValue ?? category.ExactValueIdentity.ExactRepresentation,
                        category.VerbatimDisplayValue is not null);
                    AddRecord(input, graph, valueNode, record);
                }
            }
        }
    }

    private static void ProjectActors(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records)
    {
        foreach (var record in records)
        {
            var roles = Roles(record.Id, input.SemanticClassifications);
            var npc = roles.Contains(CanonicalProjectionSemantics.ActorNpc);
            var player = roles.Contains(CanonicalProjectionSemantics.ActorPlayerCharacter);
            if (npc == player)
            {
                AddRecord(input, graph, graph.Root, record);
                continue;
            }
            if (player)
            {
                AddRecord(input, graph,
                    AddStaticOrganization(input, graph, graph.Root, CanonicalProjectionSemantics.ActorPlayerNode), record);
                continue;
            }
            var npcNode = AddStaticOrganization(input, graph, graph.Root, CanonicalProjectionSemantics.ActorNpcNode);
            var values = input.OrganizationalValueAssertions
                .Where(value => value.KnowledgeRecordId == record.Id &&
                    (value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode ||
                     value.DimensionId == CanonicalProjectionSemantics.ActorFactionDimensionNode))
                .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (values.IsEmpty)
            {
                AddRecord(input, graph, npcNode, record);
                continue;
            }
            foreach (var value in values)
            {
                var dimension = AddStaticOrganization(input, graph, npcNode, value.DimensionId);
                var valueSemantic = value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode
                    ? CanonicalProjectionSemantics.ActorDlcValueNode
                    : CanonicalProjectionSemantics.ActorFactionValueNode;
                var valueNode = AddDynamicOrganization(
                    input, graph, dimension, valueSemantic, value.ExactValueIdentity,
                    value.VerbatimDisplayValue ?? value.ExactValueIdentity.ExactRepresentation,
                    value.VerbatimDisplayValue is not null);
                AddRecord(input, graph, valueNode, record);
            }
        }
    }

    private static BuilderNode AddStaticOrganization(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        BuilderNode parent,
        CanonicalOrganizationalSemanticId semanticId)
    {
        var definition = input.Policy.OrganizationalDefinitions.Single(value =>
            value.KnowledgeKind == graph.Kind && value.SemanticId == semanticId);
        return AddDynamicOrganization(input, graph, parent, semanticId, null, definition.VerbatimPolicyLabel, true);
    }

    private static BuilderNode AddDynamicOrganization(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        BuilderNode parent,
        CanonicalOrganizationalSemanticId semanticId,
        SourceNativeIdentifier? sourceValue,
        string label,
        bool isNamed)
    {
        var organizationId = CanonicalOrganizationalNodeId.DeriveV1(
            input.Policy.Id, input.Policy.ExactVersion, graph.Kind, semanticId, sourceValue);
        var nodeId = CanonicalNavigationNodeId.ForOrganizationalNode(organizationId);
        if (parent.Children.FirstOrDefault(value => value.NodeId == nodeId) is { } existing) return existing;
        var segments = parent.Segments.Add(nodeId);
        var path = CanonicalNavigationPathId.DeriveV1(
            input.CatalogRevisionId, input.CatalogCompositionId, input.Policy.Id,
            input.Policy.ExactVersion, graph.Kind, segments);
        var node = BuilderNode.Organizational(
            nodeId, path, parent, parent.Depth + 1, organizationId, semanticId, label,
            isNamed ? (sourceValue is null ? CanonicalNavigationDisplayKind.PolicyLabel : CanonicalNavigationDisplayKind.SourceValue)
                : CanonicalNavigationDisplayKind.NativeIdentifier,
            segments);
        parent.Children.Add(node);
        return node;
    }

    private static BuilderNode AddRecord(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        BuilderNode parent,
        CanonicalKnowledgeRecord record)
    {
        var nodeId = CanonicalNavigationNodeId.ForCanonicalRecord(record.Id);
        if (parent.Children.FirstOrDefault(value => value.NodeId == nodeId) is { } existing) return existing;
        var segments = parent.Segments.Add(nodeId);
        var path = CanonicalNavigationPathId.DeriveV1(
            input.CatalogRevisionId, input.CatalogCompositionId, input.Policy.Id,
            input.Policy.ExactVersion, graph.Kind, segments);
        var terms = input.TerminologyAssertions.Where(value => value.KnowledgeRecordId == record.Id)
            .OrderBy(value => value.Role)
            .ThenBy(value => value.VerbatimValue, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var primary = terms.Where(value => value.Role == TerminologyAssertionRole.PrimaryName)
            .OrderBy(value => value.VerbatimValue, StringComparer.Ordinal)
            .ThenBy(value => value.LanguageTag, StringComparer.Ordinal)
            .ThenBy(value => value.SourceRevisionId.Value, StringComparer.Ordinal)
            .ThenBy(value => EvidenceClaimContentId.DeriveV1(value).Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var display = primary.IsEmpty ? record.NativeIdentity.ExactRepresentation : primary[0].VerbatimValue;
        var node = BuilderNode.Record(
            nodeId, path, parent, parent.Depth + 1, record.Id, display,
            primary.IsEmpty ? CanonicalNavigationDisplayKind.NativeIdentifier : CanonicalNavigationDisplayKind.CanonicalTerminology,
            terms, record.NativeIdentity,
            primary.Select(value => value.VerbatimValue).Distinct(StringComparer.Ordinal).Skip(1).Any(), segments);
        parent.Children.Add(node);
        return node;
    }

    private static IEnumerable<BuilderNode> Order(IEnumerable<BuilderNode> values) => values
        .OrderBy(value => value.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier ? 1 : 0)
        .ThenBy(value => value.DisplayAnchor, UnicodeScalarStringComparer.Instance)
        .ThenBy(
            value => value.DisplayKind == CanonicalNavigationDisplayKind.NativeIdentifier ? value.NativeIdentity : null,
            SourceNativeIdentifierComparer.Instance)
        .ThenBy(value => value.Kind)
        .ThenBy(
            value => value.RecordId?.Value ?? value.OrganizationalNodeId?.Value ?? value.NodeId.Value,
            StringComparer.Ordinal)
        .ThenBy(value => value.PathId.Value, StringComparer.Ordinal);

    private static bool MatchesSearch(BuilderNode value, string? searchText)
    {
        if (string.IsNullOrEmpty(searchText)) return true;
        if (value.DisplayAnchor.Contains(searchText, StringComparison.Ordinal)) return true;
        if (value.Terminology.Any(assertion =>
                assertion.VerbatimValue.Contains(searchText, StringComparison.Ordinal))) return true;
        return value.Children.Any(child => MatchesSearch(child, searchText));
    }

    private static CanonicalNavigationNode ToContract(BuilderNode value) => new(
        value.NodeId, value.PathId, value.Parent?.PathId, value.Depth, value.Kind,
        value.OrganizationalNodeId, value.OrganizationalSemanticId, value.RecordId,
        value.DisplayAnchor, value.DisplayKind, value.Terminology, value.NativeIdentity,
        value.Kind == CanonicalNavigationNodeKind.CanonicalRecordReference,
        value.Children.Count > 0, value.HasTerminologyConflict);

    private static ImmutableHashSet<CanonicalSemanticRoleId> Roles(
        KnowledgeRecordId id,
        ImmutableArray<CanonicalSemanticClassificationAssertion> values) =>
        values.Where(value => value.KnowledgeRecordId == id).Select(value => value.RoleId).ToImmutableHashSet();

    private static CanonicalOrganizationalSemanticId? RoleNode(
        KnowledgeKind kind,
        CanonicalSemanticRoleId role) => kind switch
    {
        KnowledgeKind.MissionQuest when role == CanonicalProjectionSemantics.MissionDlc => CanonicalProjectionSemantics.MissionDlcNode,
        KnowledgeKind.MissionQuest when role == CanonicalProjectionSemantics.MissionMod => CanonicalProjectionSemantics.MissionModNode,
        KnowledgeKind.MissionQuest when role == CanonicalProjectionSemantics.MissionOnline => CanonicalProjectionSemantics.MissionOnlineNode,
        KnowledgeKind.MissionQuest when role == CanonicalProjectionSemantics.MissionStoryMode => CanonicalProjectionSemantics.MissionStoryNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemArmor => CanonicalProjectionSemantics.ItemArmorNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemClothing => CanonicalProjectionSemantics.ItemClothingNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemClutterProps => CanonicalProjectionSemantics.ItemClutterPropsNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemMagic => CanonicalProjectionSemantics.ItemMagicNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemNature => CanonicalProjectionSemantics.ItemNatureNode,
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemWeapons => CanonicalProjectionSemantics.ItemWeaponsNode,
        _ => null,
    };

    private static bool IsEffectiveDuplicateOrDeleted(
        KnowledgeRecordId id,
        ImmutableArray<CanonicalRecordContributionAssertion> values) =>
        values.Any(value => value.KnowledgeRecordId == id &&
            (value.ContributionKind == CanonicalRecordContributionKind.Deleted ||
             value.ContributionKind == CanonicalRecordContributionKind.Modified));

    private static bool HasPrimaryTerminology(KnowledgeRecordId id, ImmutableArray<TerminologyAssertion> values) =>
        values.Any(value => value.KnowledgeRecordId == id && value.Role == TerminologyAssertionRole.PrimaryName);

    private static string RootLabel(KnowledgeKind kind) => kind switch
    {
        KnowledgeKind.Location => "Location",
        KnowledgeKind.MissionQuest => "Mission/Quest",
        KnowledgeKind.Item => "Item",
        KnowledgeKind.Actor => "Actor",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static void ValidateInput(CanonicalSelectorProjectionInput input)
    {
        var verification = CanonicalCatalogPackageKernel.Verify(input.VerifiedPackage);
        if (!verification.IsStructurallyValid ||
            input.VerifiedPackage.Manifest.PackageSchemaVersion is not (
                CatalogPackageManifest.ProjectionContractSchemaVersion or
                CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            throw new InvalidDataException("Selector projection package is no longer structurally valid.");
        if (input.CatalogRevisionId != input.VerifiedPackage.Manifest.CatalogRevisionId)
            throw new InvalidDataException("Selector projection revision is not the verified package revision.");
        if (!PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V1) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V2) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V3))
            throw new InvalidDataException("The selector projection policy revision is unsupported or its immutable content changed.");
        if (input.KnowledgeRecords.IsDefault || input.TerminologyAssertions.IsDefault ||
            input.RelationshipAssertions.IsDefault || input.SourceRevisions.IsDefault ||
            input.SemanticClassifications.IsDefault || input.ContributionAssertions.IsDefault ||
            input.OrganizationalValueAssertions.IsDefault || input.CrossSourceAssertions.IsDefault)
            throw new ArgumentException("Projection input collections must be initialized.", nameof(input));
        if (input.KnowledgeRecords.Select(value => value.Id).Distinct().Count() != input.KnowledgeRecords.Length)
            throw new InvalidDataException("Projection records must have distinct canonical identities.");
        var revisions = input.SourceRevisions.Select(value => value.Revision.Id).ToHashSet();
        if (input.KnowledgeRecords.Any(value => !revisions.Contains(value.SourceRevisionId)))
            throw new InvalidDataException("Every projected record must retain its verified source revision.");
        var records = input.KnowledgeRecords.ToDictionary(value => value.Id);
        bool HasPermittedAssertionSource(
            KnowledgeRecordId id,
            CatalogSourceRevisionId revisionId,
            CrossSourceCanonicalAssertionKind kind,
            EvidenceClaimContentId claimContentId) =>
            records.TryGetValue(id, out var record) &&
            (record.SourceRevisionId == revisionId || input.CrossSourceAssertions.Any(value =>
                value.TargetKnowledgeRecordId == id &&
                value.TargetOriginSourceRevisionId == record.SourceRevisionId &&
                value.AssertingSourceRevisionId == revisionId &&
                value.AssertionKind == kind &&
                value.UnderlyingClaimContentId == claimContentId));
        if (input.TerminologyAssertions.Any(value =>
                !HasPermittedAssertionSource(
                    value.KnowledgeRecordId,
                    value.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Terminology,
                    EvidenceClaimContentId.DeriveV1(value))) ||
            input.RelationshipAssertions.Any(value =>
                !HasPermittedAssertionSource(
                    value.SubjectKnowledgeRecordId,
                    value.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.Relationship,
                    EvidenceClaimContentId.DeriveV1(value)) ||
                value.ResolvedTargetKnowledgeRecordId is KnowledgeRecordId target && !records.ContainsKey(target)) ||
            input.SemanticClassifications.Any(value =>
                !HasPermittedAssertionSource(
                    value.KnowledgeRecordId,
                    value.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.SemanticClassification,
                    EvidenceClaimContentId.DeriveV1(value))) ||
            input.ContributionAssertions.Any(value =>
                !HasPermittedAssertionSource(
                    value.KnowledgeRecordId,
                    value.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.RecordContribution,
                    EvidenceClaimContentId.DeriveV1(value)) ||
                value.OriginKnowledgeRecordId is KnowledgeRecordId origin && !records.ContainsKey(origin)) ||
            input.OrganizationalValueAssertions.Any(value =>
                !HasPermittedAssertionSource(
                    value.KnowledgeRecordId,
                    value.SourceRevisionId,
                    CrossSourceCanonicalAssertionKind.OrganizationalValue,
                    EvidenceClaimContentId.DeriveV1(value))))
            throw new InvalidDataException("Projection assertions must retain an exact verified origin or cross-source envelope.");
        if (input.Applicability.ApplicableKnowledgeRecordIds.Any(value => !records.ContainsKey(value)) ||
            input.Applicability.SuppressedKnowledgeRecordIds.Any(value => !records.ContainsKey(value)))
            throw new InvalidDataException("Applicability may refer only to records in the canonical projection input.");
    }

    private static bool PolicyEquivalent(
        CanonicalSelectorProjectionPolicy left,
        CanonicalSelectorProjectionPolicy right) =>
        left.Id == right.Id &&
        string.Equals(left.ExactVersion, right.ExactVersion, StringComparison.Ordinal) &&
        left.Control == right.Control &&
        string.Equals(left.OrderingPolicyId, right.OrderingPolicyId, StringComparison.Ordinal) &&
        string.Equals(left.OrderingPolicyVersion, right.OrderingPolicyVersion, StringComparison.Ordinal) &&
        left.OrganizationalDefinitions.Length == right.OrganizationalDefinitions.Length &&
        left.OrganizationalDefinitions.Zip(right.OrganizationalDefinitions).All(pair => pair.First == pair.Second);

    private sealed class ProjectionGraph
    {
        public ProjectionGraph(KnowledgeKind kind, BuilderNode root)
        {
            Kind = kind;
            Root = root;
        }

        public KnowledgeKind Kind { get; }
        public BuilderNode Root { get; }
        public Dictionary<CanonicalNavigationPathId, BuilderNode> ByPath { get; } = [];
        public void Index()
        {
            void Visit(BuilderNode node)
            {
                ByPath.Add(node.PathId, node);
                foreach (var child in node.Children) Visit(child);
            }
            Visit(Root);
        }
    }

    private sealed class BuilderNode
    {
        private BuilderNode() { }
        public required CanonicalNavigationNodeId NodeId { get; init; }
        public required CanonicalNavigationPathId PathId { get; init; }
        public BuilderNode? Parent { get; init; }
        public required int Depth { get; init; }
        public required CanonicalNavigationNodeKind Kind { get; init; }
        public CanonicalOrganizationalNodeId? OrganizationalNodeId { get; init; }
        public CanonicalOrganizationalSemanticId? OrganizationalSemanticId { get; init; }
        public KnowledgeRecordId? RecordId { get; init; }
        public required string DisplayAnchor { get; init; }
        public required CanonicalNavigationDisplayKind DisplayKind { get; init; }
        public ImmutableArray<TerminologyAssertion> Terminology { get; init; } = [];
        public SourceNativeIdentifier? NativeIdentity { get; init; }
        public bool HasTerminologyConflict { get; init; }
        public required ImmutableArray<CanonicalNavigationNodeId> Segments { get; init; }
        public List<BuilderNode> Children { get; } = [];

        public static BuilderNode Organizational(
            CanonicalNavigationNodeId nodeId, CanonicalNavigationPathId pathId, BuilderNode? parent, int depth,
            CanonicalOrganizationalNodeId organizationId, CanonicalOrganizationalSemanticId semanticId,
            string label, CanonicalNavigationDisplayKind displayKind,
            ImmutableArray<CanonicalNavigationNodeId> segments) => new()
        {
            NodeId = nodeId, PathId = pathId, Parent = parent, Depth = depth,
            Kind = CanonicalNavigationNodeKind.Organizational,
            OrganizationalNodeId = organizationId, OrganizationalSemanticId = semanticId,
            DisplayAnchor = label, DisplayKind = displayKind, Segments = segments,
        };

        public static BuilderNode Record(
            CanonicalNavigationNodeId nodeId, CanonicalNavigationPathId pathId, BuilderNode parent, int depth,
            KnowledgeRecordId recordId, string label, CanonicalNavigationDisplayKind displayKind,
            ImmutableArray<TerminologyAssertion> terminology, SourceNativeIdentifier nativeIdentity,
            bool conflict, ImmutableArray<CanonicalNavigationNodeId> segments) => new()
        {
            NodeId = nodeId, PathId = pathId, Parent = parent, Depth = depth,
            Kind = CanonicalNavigationNodeKind.CanonicalRecordReference, RecordId = recordId,
            DisplayAnchor = label, DisplayKind = displayKind, Terminology = terminology,
            NativeIdentity = nativeIdentity, HasTerminologyConflict = conflict, Segments = segments,
        };
    }

    private sealed class UnicodeScalarStringComparer : IComparer<string>
    {
        public static UnicodeScalarStringComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var leftRunes = left.EnumerateRunes().GetEnumerator();
            var rightRunes = right.EnumerateRunes().GetEnumerator();
            while (true)
            {
                var hasLeft = leftRunes.MoveNext();
                var hasRight = rightRunes.MoveNext();
                if (!hasLeft || !hasRight) return hasLeft.CompareTo(hasRight);
                var comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
                if (comparison != 0) return comparison;
            }
        }
    }

    private sealed class SourceNativeIdentifierComparer : IComparer<SourceNativeIdentifier?>
    {
        public static SourceNativeIdentifierComparer Instance { get; } = new();

        public int Compare(SourceNativeIdentifier? left, SourceNativeIdentifier? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var comparison = UnicodeScalarStringComparer.Instance.Compare(left.Namespace, right.Namespace);
            if (comparison != 0) return comparison;
            comparison = UnicodeScalarStringComparer.Instance.Compare(left.ObjectType, right.ObjectType);
            if (comparison != 0) return comparison;
            comparison = UnicodeScalarStringComparer.Instance.Compare(left.ExactRepresentation, right.ExactRepresentation);
            if (comparison != 0) return comparison;
            comparison = left.IdentityBytes.AsSpan().SequenceCompareTo(right.IdentityBytes.AsSpan());
            if (comparison != 0) return comparison;
            comparison = UnicodeScalarStringComparer.Instance.Compare(left.ComparisonMethodId, right.ComparisonMethodId);
            return comparison != 0 ? comparison : left.ComparisonMethodVersion.CompareTo(right.ComparisonMethodVersion);
        }
    }
}
