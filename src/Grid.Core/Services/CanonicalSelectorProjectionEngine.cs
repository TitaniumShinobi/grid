using System.Collections.Immutable;
using System.Collections.Frozen;
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
        return CreateVerifiedInputCore(package, catalogCompositionId, policy, applicability,
            catalog.HasValidatedPackage(package));
    }

    public static CanonicalSelectorProjectionInput CreateVerifiedInput(
        CanonicalCatalogPackage package,
        CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicy policy,
        CanonicalApplicabilityProjection applicability)
        => CreateVerifiedInputCore(package, catalogCompositionId, policy, applicability, false);

    private static CanonicalSelectorProjectionInput CreateVerifiedInputCore(
        CanonicalCatalogPackage package, CatalogCompositionId catalogCompositionId,
        CanonicalSelectorProjectionPolicy policy, CanonicalApplicabilityProjection applicability,
        bool packageAlreadyValidated)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!packageAlreadyValidated)
        {
            CatalogPackageVerificationResult verification;
            using (CanonicalRuntimeDiagnostics.Measure("projection.create-verify"))
                verification = CanonicalCatalogPackageKernel.Verify(package);
            if (!verification.IsStructurallyValid)
                throw new InvalidDataException(
                    $"Selector projection requires a structurally verified catalog package: {string.Join("; ", verification.Issues)}");
        }
        if (package.Manifest.PackageSchemaVersion is not (
                CatalogPackageManifest.ProjectionContractSchemaVersion or
                CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            throw new InvalidDataException("Selector projection requires an evidence-closed semantic payload.");
        if (catalogCompositionId != applicability.CompositionId)
            throw new ArgumentException("Projection composition and applicability coordinates must match.", nameof(applicability));
        var input = new CanonicalSelectorProjectionInput(package, catalogCompositionId, policy, applicability);
        using (CanonicalRuntimeDiagnostics.Measure("projection.validate-input"))
            ValidateInput(input);
        input.InitializeRuntimeState();
        return input;
    }

    public static CanonicalSelectorResult Query(
        CanonicalSelectorProjectionInput input,
        CanonicalSelectorQuery query)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.TerminologyLocale);
        if (query.CatalogRevisionId != input.CatalogRevisionId ||
            query.CatalogCompositionId != input.CatalogCompositionId ||
            query.CatalogCompositionId != input.Applicability.CompositionId ||
            query.ProjectionPolicyId != input.Policy.Id ||
            !string.Equals(query.ExactProjectionPolicyVersion, input.Policy.ExactVersion, StringComparison.Ordinal))
            throw new ArgumentException("Selector query coordinates do not match the frozen projection input.", nameof(query));

        var graph = GetGraph(input, query.KnowledgeKind, query.IncludeIdentifierOnly,
            query.TerminologyLocale, query.InspectionMode);
        var current = graph.ByPath.GetValueOrDefault(query.CurrentPathId ?? graph.RootPath) ??
              throw new ArgumentException("The requested navigation path is not present in this projection.", nameof(query));
        var children = string.IsNullOrEmpty(query.SearchText) ? current.Children : current.Children
            .Where(value => MatchesSearch(graph, value, query.SearchText)).ToImmutableArray();
        return new CanonicalSelectorResult(
            input.CatalogRevisionId,
            input.CatalogCompositionId,
            query.KnowledgeKind,
            input.Policy.Id,
            input.Policy.ExactVersion,
            graph.RootPath,
            current.Node.PathId,
            current.Node.ParentPathId,
            current.Node,
            children,
            input.CoverageState,
            query.TerminologyLocale);
    }

    private static CanonicalProjectionGraph GetGraph(CanonicalSelectorProjectionInput input, KnowledgeKind kind,
        bool includeIdentifierOnly, CanonicalTerminologyLocalePreference locale, bool inspectionMode)
    {
        var state = input.RuntimeState ?? throw new InvalidDataException("Projection input was not validated.");
        return state.GetGraph(CanonicalProjectionGraphKey.Create(kind, includeIdentifierOnly, inspectionMode, locale), () =>
        {
            CanonicalProjectionGraph graph;
            using (CanonicalRuntimeDiagnostics.Measure("projection.graph-build"))
            {
                var built = BuildGraph(input, kind, includeIdentifierOnly, locale, inspectionMode);
                var nodes = built.ByPath.ToDictionary(pair => pair.Key, pair => ToContract(pair.Value));
                graph = new(built.Root.PathId, built.ByPath.ToFrozenDictionary(pair => pair.Key,
                    pair => new CanonicalProjectionGraphNode(nodes[pair.Key],
                        Order(pair.Value.Children).Select(child => nodes[child.PathId]).ToImmutableArray())));
            }
            CanonicalRuntimeDiagnostics.CaptureGraph(kind, includeIdentifierOnly, inspectionMode, locale, graph.Snapshot);
            return graph;
        });
    }

    public static bool ValidateSelection(CanonicalSelectorProjectionInput input, CanonicalSelectorSelection selection,
        CanonicalTerminologyLocalePreference locale)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(locale);
        if (selection.CatalogRevisionId != input.CatalogRevisionId ||
            selection.CatalogCompositionId != input.CatalogCompositionId ||
            selection.ProjectionPolicyId != input.Policy.Id ||
            selection.ProjectionPolicyVersion != input.Policy.ExactVersion || !Enum.IsDefined(selection.KnowledgeKind))
            return false;
        if (selection.SelectionKind == CanonicalSelectorSelectionKind.OtherContext)
            return !string.IsNullOrEmpty(selection.UnresolvedOtherContextId);
        if (selection.SelectionKind != CanonicalSelectorSelectionKind.CanonicalRecord ||
            selection.SelectedPathId is not { } path || selection.KnowledgeRecordId is not { } recordId)
            return false;
        var graph = GetGraph(input, selection.KnowledgeKind, true, locale, false);
        return graph.ByPath.TryGetValue(path, out var value) && value.Node.IsSelectable &&
            value.Node.KnowledgeRecordId == recordId;
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
        bool includeIdentifierOnly,
        CanonicalTerminologyLocalePreference terminologyLocale,
        bool inspectionMode)
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

        ImmutableArray<CanonicalKnowledgeRecord> records;
        using (CanonicalRuntimeDiagnostics.Measure("projection.records-filter"))
        {
            records = input.RuntimeState!.Records(kind);
            if (!inspectionMode && input.Policy.UsesGtaEnhancedRecordAdmission)
                records = records.Where(record =>
                    !GtaSelectorDisplayRules.IsGtaGame(record.GameId) ||
                    IsAdmitted(record, input, terminologyLocale)).ToImmutableArray();
        }

        using (CanonicalRuntimeDiagnostics.Measure("projection.kind-project"))
        {
            switch (kind)
            {
                case KnowledgeKind.Location:
                    ProjectLocations(input, graph, records, terminologyLocale);
                    break;
                case KnowledgeKind.MissionQuest:
                    ProjectMissionQuests(input, graph, records, terminologyLocale);
                    break;
                case KnowledgeKind.Item:
                    ProjectItems(input, graph, records, terminologyLocale);
                    break;
                case KnowledgeKind.Actor:
                    ProjectActors(input, graph, records, terminologyLocale);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
        using (CanonicalRuntimeDiagnostics.Measure("projection.graph-index"))
            graph.Index();
        return graph;
    }

    private static void ProjectLocations(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        var recordMap = records.ToDictionary(value => value.Id);
        var strict = input.RuntimeState!.StrictLocationRelationships.Where(value =>
                recordMap.ContainsKey(value.SubjectKnowledgeRecordId))
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
            var node = AddRecord(input, graph, parent, recordMap[recordId], terminologyLocale);
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
            foreach (var recordId in unresolvedIds)
                AddRecord(input, graph, unresolved, recordMap[recordId], terminologyLocale);
        }
    }

    private static void ProjectMissionQuests(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        foreach (var record in records)
        {
            var roles = input.RuntimeState!.Roles(record.Id);
            var placed = false;
            foreach (var role in roles)
            {
                var semantic = RoleNode(KnowledgeKind.MissionQuest, role);
                if (semantic is null) continue;
                if (role == CanonicalProjectionSemantics.MissionMod)
                {
                    var introductions = input.RuntimeState!.Contributions(record.Id).Where(value =>
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
                        AddRecord(input, graph, modNode, record, terminologyLocale);
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
                        var activities = input.RuntimeState!.Organization(record.Id)
                            .Where(value => value.KnowledgeRecordId == record.Id &&
                                            value.DimensionId == CanonicalProjectionSemantics.MissionActivityFamilyDimensionNode &&
                                            OrganizationMatchesLocale(input, record, value, terminologyLocale))
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
                                AddRecord(input, graph, activityNode, record, terminologyLocale);
                            }
                            placed = true;
                            continue;
                        }
                    }
                    AddRecord(input, graph, familyRoot, record, terminologyLocale);
                    placed = true;
                }
            }
            if (!placed) AddRecord(input, graph, graph.Root, record, terminologyLocale);
        }
    }

    private static void ProjectItems(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        foreach (var record in records)
        {
            var nodes = input.RuntimeState!.Roles(record.Id)
                .Select(role => RoleNode(KnowledgeKind.Item, role))
                .Where(value => value is not null)
                .Select(value => value!.Value)
                .Distinct()
                .ToImmutableArray();
            if (nodes.IsEmpty)
            {
                AddRecord(input, graph, graph.Root, record, terminologyLocale);
                continue;
            }

            foreach (var node in nodes)
            {
                var familyNode = AddStaticOrganization(input, graph, graph.Root, node);
                if (node == CanonicalProjectionSemantics.ItemVehiclesNode)
                {
                    foreach (var category in LocalizedOrganization(input, record,
                                 CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, terminologyLocale))
                    {
                        var categoryNode = AddDynamicOrganization(input, graph, familyNode,
                            CanonicalProjectionSemantics.ItemVehicleClassValueNode,
                            category.ExactValueIdentity, category.VerbatimDisplayValue!, true);
                        AddRecord(input, graph, categoryNode, record, terminologyLocale);
                    }
                    continue;
                }
                if (node != CanonicalProjectionSemantics.ItemWeaponsNode ||
                    !input.Policy.OrganizationalDefinitions.Any(value =>
                        value.KnowledgeKind == KnowledgeKind.Item &&
                        value.SemanticId == CanonicalProjectionSemantics.ItemSourceCategoryValueNode &&
                        value.ParentSemanticId == CanonicalProjectionSemantics.ItemWeaponsNode))
                {
                    AddRecord(input, graph, familyNode, record, terminologyLocale);
                    continue;
                }

                var categories = input.RuntimeState!.Organization(record.Id)
                    .Where(value =>
                        value.KnowledgeRecordId == record.Id &&
                        value.DimensionId == CanonicalProjectionSemantics.ItemSourceCategoryDimensionNode &&
                        OrganizationMatchesLocale(input, record, value, terminologyLocale))
                    .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
                    .ToImmutableArray();
                if (categories.IsEmpty)
                {
                    AddRecord(input, graph, familyNode, record, terminologyLocale);
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
                    AddRecord(input, graph, valueNode, record, terminologyLocale);
                }
            }
        }
    }

    private static void ProjectActors(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        ImmutableArray<CanonicalKnowledgeRecord> records,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        foreach (var record in records)
        {
            var roles = input.RuntimeState!.Roles(record.Id);
            var npc = roles.Contains(CanonicalProjectionSemantics.ActorNpc);
            var player = roles.Contains(CanonicalProjectionSemantics.ActorPlayerCharacter);
            if (!npc && !player)
            {
                AddRecord(input, graph, graph.Root, record, terminologyLocale);
                continue;
            }
            if (player)
            {
                AddRecord(input, graph,
                    AddStaticOrganization(input, graph, graph.Root, CanonicalProjectionSemantics.ActorPlayerNode), record,
                    terminologyLocale);
                continue;
            }
            var npcNode = AddStaticOrganization(input, graph, graph.Root, CanonicalProjectionSemantics.ActorNpcNode);
            var placedUnderNpcTaxonomy = false;
            if (input.Policy.SupportsActorNamedGenericNpcOrganization &&
                (roles.Contains(CanonicalProjectionSemantics.ActorNamedCharacter) ||
                 roles.Contains(CanonicalProjectionSemantics.ActorGenericType)))
            {
                placedUnderNpcTaxonomy = PlaceActorNamedGenericOrganization(
                    input, graph, npcNode, record, roles, terminologyLocale);
            }

            placedUnderNpcTaxonomy |= PlaceActorDlcAndFactionOrganization(
                input, graph, npcNode, record, terminologyLocale);
            if (!placedUnderNpcTaxonomy)
                AddRecord(input, graph, npcNode, record, terminologyLocale);
        }
    }

    private static bool PlaceActorNamedGenericOrganization(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        BuilderNode npcNode,
        CanonicalKnowledgeRecord record,
        ImmutableHashSet<CanonicalSemanticRoleId> roles,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        var subtype = AddStaticOrganization(input, graph, npcNode,
            roles.Contains(CanonicalProjectionSemantics.ActorNamedCharacter)
                ? CanonicalProjectionSemantics.ActorNamedCharacterNode
                : CanonicalProjectionSemantics.ActorGenericTypeNode);
        var categories = LocalizedOrganization(input, record,
            CanonicalProjectionSemantics.ActorSourceCategoryDimensionNode, terminologyLocale);
        if (categories.IsEmpty)
        {
            AddRecord(input, graph, subtype, record, terminologyLocale);
            return true;
        }

        foreach (var category in categories)
        {
            var categoryNode = AddDynamicOrganization(input, graph, subtype,
                CanonicalProjectionSemantics.ActorSourceCategoryValueNode,
                category.ExactValueIdentity, category.VerbatimDisplayValue!, true);
            AddRecord(input, graph, categoryNode, record, terminologyLocale);
        }

        return true;
    }

    private static bool PlaceActorDlcAndFactionOrganization(
        CanonicalSelectorProjectionInput input,
        ProjectionGraph graph,
        BuilderNode npcNode,
        CanonicalKnowledgeRecord record,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        var values = input.RuntimeState!.Organization(record.Id)
            .Where(value => value.KnowledgeRecordId == record.Id &&
                (value.DimensionId == CanonicalProjectionSemantics.ActorDlcDimensionNode ||
                 value.DimensionId == CanonicalProjectionSemantics.ActorFactionDimensionNode) &&
                OrganizationMatchesLocale(input, record, value, terminologyLocale))
            .OrderBy(value => value.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (values.IsEmpty) return false;
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
            AddRecord(input, graph, valueNode, record, terminologyLocale);
        }

        return true;
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
        CanonicalKnowledgeRecord record,
        CanonicalTerminologyLocalePreference terminologyLocale)
    {
        var nodeId = CanonicalNavigationNodeId.ForCanonicalRecord(record.Id);
        if (parent.Children.FirstOrDefault(value => value.NodeId == nodeId) is { } existing) return existing;
        var segments = parent.Segments.Add(nodeId);
        var path = CanonicalNavigationPathId.DeriveV1(
            input.CatalogRevisionId, input.CatalogCompositionId, input.Policy.Id,
            input.Policy.ExactVersion, graph.Kind, segments);
        var terms = input.RuntimeState!.Terminology(record.Id);
        var primary = SelectPreferredTerminology(
                terms.Where(value => value.Role == TerminologyAssertionRole.PrimaryName),
                terminologyLocale)
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

    private static bool MatchesSearch(CanonicalProjectionGraph graph, CanonicalNavigationNode value, string? searchText)
    {
        if (string.IsNullOrEmpty(searchText)) return true;
        if (value.DisplayAnchor.Contains(searchText, StringComparison.Ordinal)) return true;
        if (value.ExactTerminologyAssertions.Any(assertion =>
                assertion.VerbatimValue.Contains(searchText, StringComparison.Ordinal))) return true;
        return graph.ByPath[value.PathId].Children.Any(child => MatchesSearch(graph, child, searchText));
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
        KnowledgeKind.Item when role == CanonicalProjectionSemantics.ItemVehicles => CanonicalProjectionSemantics.ItemVehiclesNode,
        _ => null,
    };

    private static bool IsEffectiveDuplicateOrDeleted(
        KnowledgeRecordId id,
        ImmutableArray<CanonicalRecordContributionAssertion> values) =>
        values.Any(value => value.KnowledgeRecordId == id &&
            (value.ContributionKind == CanonicalRecordContributionKind.Deleted ||
             value.ContributionKind == CanonicalRecordContributionKind.Modified));

    private static bool IsAdmitted(CanonicalKnowledgeRecord record, CanonicalSelectorProjectionInput input,
        CanonicalTerminologyLocalePreference locale)
    {
        var roles = input.RuntimeState!.Roles(record.Id);
        // Existing source-classified geographic zones remain player-addressable. MLOs and
        // rooms do not acquire presentation eligibility merely by having technical text.
        var establishedZone = record.Kind == KnowledgeKind.Location &&
            input.RuntimeState!.EstablishedZones.Contains(record.Id);
        if ((!roles.Contains(CanonicalProjectionSemantics.SelectorPlayerAddressable) && !establishedZone) ||
            roles.Contains(CanonicalProjectionSemantics.MissionPlaylist)) return false;
        var terminology = input.RuntimeState!.Terminology(record.Id);
        if (TryGetResolvedPrimaryDisplayForAdmission(record.Id, terminology, locale, input.Policy, out _))
        {
            return !roles.Contains(CanonicalProjectionSemantics.ItemVehicles) ||
                LocalizedOrganization(input, record, CanonicalProjectionSemantics.ItemVehicleClassDimensionNode, locale).Length == 1;
        }

        var primaryAssertions = terminology.Where(value => value.Role == TerminologyAssertionRole.PrimaryName).ToImmutableArray();
        if (record.Kind == KnowledgeKind.Location && input.Policy.RequiresNamedLocationPresentation)
            return false;
        var preferredPrimaryLabels = SelectPrimaryTerminologyForAdmission(primaryAssertions, locale, input.Policy)
            .Select(value => value.VerbatimValue)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (preferredPrimaryLabels.Length > 1)
            return false;
        if (primaryAssertions.Length > 0 && preferredPrimaryLabels.Length == 0)
            return false;

        return roles.Contains(CanonicalProjectionSemantics.SelectorPlayerAddressable) || establishedZone;
    }

    private static bool OrganizationMatchesLocale(CanonicalSelectorProjectionInput input,
        CanonicalKnowledgeRecord record, CanonicalOrganizationalValueAssertion value,
        CanonicalTerminologyLocalePreference locale)
    {
        if (!input.Policy.UsesGtaEnhancedRecordAdmission || !GtaSelectorDisplayRules.IsGtaGame(record.GameId))
            return true;
        if (value.VerbatimDisplayValue is null) return false;
        var resolved = LocalizedOrganization(input, record, value.DimensionId, locale);
        return resolved.Any(candidate =>
            candidate.ExactValueIdentity == value.ExactValueIdentity &&
            string.Equals(candidate.VerbatimDisplayValue, value.VerbatimDisplayValue, StringComparison.Ordinal));
    }

    private static ImmutableArray<CanonicalOrganizationalValueAssertion> LocalizedOrganization(
        CanonicalSelectorProjectionInput input, CanonicalKnowledgeRecord record,
        CanonicalOrganizationalSemanticId dimension, CanonicalTerminologyLocalePreference locale)
    {
        var candidates = input.RuntimeState!.Organization(record.Id).Where(value =>
            value.KnowledgeRecordId == record.Id &&
            value.DimensionId == dimension &&
            value.VerbatimDisplayValue is not null).ToImmutableArray();
        var resolved = ImmutableArray.CreateBuilder<CanonicalOrganizationalValueAssertion>();
        foreach (var group in candidates.GroupBy(value => value.ExactValueIdentity))
        {
            var distinctLabels = group.Select(value => value.VerbatimDisplayValue!).Distinct(StringComparer.Ordinal).ToArray();
            if (distinctLabels.Length != 1) continue;
            var preferred = SelectPreferredOrganizationalValues(group, locale).ToImmutableArray();
            if (preferred.IsEmpty) continue;
            resolved.Add(preferred.OrderBy(value => value.Id.Value, StringComparer.Ordinal).First());
        }

        return resolved.ToImmutable().OrderBy(value => value.Id.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    private static IEnumerable<CanonicalOrganizationalValueAssertion> SelectPreferredOrganizationalValues(
        IEnumerable<CanonicalOrganizationalValueAssertion> values,
        CanonicalTerminologyLocalePreference preference)
    {
        var assertions = values.ToImmutableArray();
        var exact = assertions.Where(value =>
            string.Equals(value.LanguageTag, preference.RequestedLanguageTag, StringComparison.Ordinal)).ToImmutableArray();
        if (!exact.IsEmpty) return exact;
        foreach (var fallback in preference.ApprovedLanguageFallbackTags)
        {
            var matching = assertions.Where(value =>
                string.Equals(value.LanguageTag, fallback, StringComparison.Ordinal)).ToImmutableArray();
            if (!matching.IsEmpty) return matching;
        }

        return assertions.Where(value => value.LanguageTag is null);
    }

    private static bool AdmissionUsesApprovedLanguageFallback(CanonicalSelectorProjectionPolicy policy) =>
        string.Equals(policy.ExactVersion, "5", StringComparison.Ordinal) ||
        string.Equals(policy.ExactVersion, "6", StringComparison.Ordinal);

    private static IEnumerable<TerminologyAssertion> SelectPrimaryTerminologyForAdmission(
        IEnumerable<TerminologyAssertion> primaryAssertions,
        CanonicalTerminologyLocalePreference locale,
        CanonicalSelectorProjectionPolicy policy) =>
        AdmissionUsesApprovedLanguageFallback(policy)
            ? SelectPreferredTerminology(primaryAssertions, locale)
            : primaryAssertions.Where(value =>
                string.Equals(value.LanguageTag, locale.RequestedLanguageTag, StringComparison.Ordinal));

    private static bool TryGetResolvedPrimaryDisplayForAdmission(
        KnowledgeRecordId id,
        ImmutableArray<TerminologyAssertion> values,
        CanonicalTerminologyLocalePreference locale,
        CanonicalSelectorProjectionPolicy policy,
        out string display)
    {
        var primary = values.Where(value => value.KnowledgeRecordId == id &&
                                            value.Role == TerminologyAssertionRole.PrimaryName);
        var names = SelectPrimaryTerminologyForAdmission(primary, locale, policy)
            .Select(value => value.VerbatimValue)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 1 && !string.IsNullOrWhiteSpace(names[0]))
        {
            display = names[0];
            return true;
        }

        display = string.Empty;
        return false;
    }

    private static bool TryGetResolvedPrimaryDisplay(
        KnowledgeRecordId id,
        ImmutableArray<TerminologyAssertion> values,
        CanonicalTerminologyLocalePreference locale,
        out string display)
    {
        var names = SelectPreferredTerminology(
                values.Where(value => value.KnowledgeRecordId == id &&
                                      value.Role == TerminologyAssertionRole.PrimaryName),
                locale)
            .Select(value => value.VerbatimValue)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (names.Length == 1 && !string.IsNullOrWhiteSpace(names[0]))
        {
            display = names[0];
            return true;
        }

        display = string.Empty;
        return false;
    }

    private static bool HasPrimaryTerminology(
        KnowledgeRecordId id,
        ImmutableArray<TerminologyAssertion> values,
        CanonicalTerminologyLocalePreference terminologyLocale) =>
        SelectPreferredTerminology(
                values.Where(value => value.KnowledgeRecordId == id &&
                                      value.Role == TerminologyAssertionRole.PrimaryName),
                terminologyLocale)
            .Any();

    private static IEnumerable<TerminologyAssertion> SelectPreferredTerminology(
        IEnumerable<TerminologyAssertion> values,
        CanonicalTerminologyLocalePreference preference)
    {
        var assertions = values.ToImmutableArray();
        var exact = assertions.Where(value =>
            string.Equals(value.LanguageTag, preference.RequestedLanguageTag, StringComparison.Ordinal)).ToImmutableArray();
        if (!exact.IsEmpty) return exact;

        foreach (var fallback in preference.ApprovedLanguageFallbackTags)
        {
            var matching = assertions.Where(value =>
                string.Equals(value.LanguageTag, fallback, StringComparison.Ordinal)).ToImmutableArray();
            if (!matching.IsEmpty) return matching;
        }

        // A null tag is the source-native/default coordinate. "und" explicitly means
        // unknown language and cannot satisfy a requested presentation locale.
        return assertions.Where(value => value.LanguageTag is null).ToImmutableArray();
    }

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
        if (input.VerifiedPackage.Manifest.PackageSchemaVersion is not (
                CatalogPackageManifest.ProjectionContractSchemaVersion or
                CatalogPackageManifest.CrossSourceAssertionSchemaVersion or
                CatalogPackageManifest.AdapterProvenanceBoundarySchemaVersion))
            throw new InvalidDataException("Selector projection package is no longer structurally valid.");
        if (input.CatalogRevisionId != input.VerifiedPackage.Manifest.CatalogRevisionId)
            throw new InvalidDataException("Selector projection revision is not the verified package revision.");
        if (!PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V1) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V2) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V3) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.V4) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.GtaEnhanced) &&
            !PolicyEquivalent(input.Policy, CanonicalSelectorProjectionPolicy.LocationPrepared))
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
