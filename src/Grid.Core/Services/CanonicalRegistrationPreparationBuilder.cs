using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Explicit, candidate-only preparation. It never writes a publication pointer or touches runtime bindings.</summary>
public static class CanonicalRegistrationPreparationBuilder
{
    public const string MiscellaneousLabel = "Miscellaneous";

    private sealed class Node(string path, string? parent, string selector, string label, string? entity, bool selectable, string[] evidence, int? fixedOrder, bool trailing = false)
    {
        public bool Trailing { get; } = trailing;
        public string Path { get; } = path;
        public string? Parent { get; } = parent;
        public string Selector { get; } = selector;
        public string Label { get; } = label;
        public string? Entity { get; } = entity;
        public bool Selectable { get; } = selectable;
        public string[] Evidence { get; set; } = evidence;
        public int? FixedOrder { get; } = fixedOrder;
        public List<Node> Children { get; } = [];
        public RegistrationNavigationRow Row => new(Path, Parent, Selector, Label, Entity, Selectable, Children.Count > 0, Evidence);
    }
    public static async Task<RegistrationPreparedCandidate> BuildAsync(string outputRoot, CanonicalRegistrationCandidate candidate,
        RegistrationNavigationContext context, CancellationToken cancellationToken = default,
        IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
    {
        CanonicalRegistrationCandidateVerifier.Verify(candidate, existingPackages);
        candidate = System.Text.Json.JsonSerializer.Deserialize<CanonicalRegistrationCandidate>(CanonicalRegistrationEncoding.Bytes(candidate), CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Candidate snapshot is empty.");
        CanonicalRegistrationCandidateVerifier.Verify(candidate, existingPackages);
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.GameId) || string.IsNullOrWhiteSpace(context.ProfileId) || string.IsNullOrWhiteSpace(context.Locale))
            throw new InvalidDataException("Preparation requires explicit game, profile and locale.");
        cancellationToken.ThrowIfCancellationRequested();
        var checklist = CanonicalRegistrationChecklist.Load();
        if (candidate.ChecklistDigest != checklist.Digest) throw new InvalidDataException("Preparation checklist digest mismatch.");
        var candidateDigest = CanonicalRegistrationEncoding.Digest(candidate);
        var evidence = candidate.Input.Evidence.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var admitted = new Dictionary<string, RegisteredCanonicalEntity>(StringComparer.Ordinal);
        foreach (var entity in candidate.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entity.Kind is RegistrationEntityKind.Variant or RegistrationEntityKind.TechnicalArtifact ||
                (entity.GameId is not null && entity.GameId != context.GameId) ||
                !entity.Applicability.Any(a => a.GameId == context.GameId && (a.ProfileId is null || a.ProfileId == context.ProfileId))) continue;
            var resolved = entity.Names.Where(n => !n.IsAlias && n.Locale == context.Locale && !string.IsNullOrWhiteSpace(n.Value))
                .Select(n => n.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (resolved.Length != 1) continue;
            names.Add(entity.Id, resolved[0]); admitted.Add(entity.Id, entity);
        }
        var relationships = candidate.Relationships.Where(r =>
                CanonicalRelationshipRegistrationSemantics.IsNavigationParentage(r.Semantic) &&
                r.CorrelationOutcome == RegistrationOutcome.Correlated)
            .Where(r => admitted.ContainsKey(r.SubjectId) && admitted.ContainsKey(r.TargetId) && admitted[r.SubjectId].Selector == admitted[r.TargetId].Selector)
            .OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var parents = relationships.GroupBy(r => r.SubjectId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var children = relationships.GroupBy(r => r.TargetId).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        // Reject cycles rather than silently dropping an evidence-backed edge.
        var visiting = new HashSet<string>(StringComparer.Ordinal); var complete = new HashSet<string>(StringComparer.Ordinal);
        void CheckCycle(string id)
        {
            if (complete.Contains(id)) return;
            if (!visiting.Add(id)) throw new InvalidDataException("Applicable navigation relationships contain a cycle.");
            if (children.TryGetValue(id, out var edges)) foreach (var edge in edges) CheckCycle(edge.SubjectId);
            visiting.Remove(id); complete.Add(id);
        }
        foreach (var id in admitted.Keys) CheckCycle(id);
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal); var views = new List<RegistrationPreparedView>();
        Node AddNode(Node? parent, string selector, string label, string identity, string viewId, string? entity = null, bool selectable = false, IEnumerable<string>? references = null, int? fixedOrder = null, bool trailing = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = CanonicalRegistrationEncoding.Path(candidateDigest, context.GameId, context.ProfileId, context.Locale, viewId, parent?.Path, identity);
            if (nodes.TryGetValue(id, out var existing))
            {
                existing.Evidence = CanonicalRegistrationEncoding.Ordered(existing.Evidence.Concat(references ?? []));
                return existing;
            }
            if (nodes.Count >= 1_000_000) throw new InvalidDataException("Prepared path expansion exceeds its bounded generation limit.");
            var node = new Node(id, parent?.Path, selector, label, entity, selectable, CanonicalRegistrationEncoding.Ordered(references ?? []), fixedOrder, trailing);
            nodes.Add(id, node); parent?.Children.Add(node); return node;
        }
        Node Root(string viewId, string selector, string? dimension = null, string? value = null)
        {
            var declared = checklist.Resolve(selector) ?? throw new InvalidDataException("Missing selector checklist root.");
            var root = AddNode(null, selector, declared.Label, selector, viewId);
            views.Add(new(viewId, selector, dimension, value, root.Path)); return root;
        }
        Node Anchor(Node root, string anchor, string viewId)
        {
            var ancestry = new Stack<RegistrationChecklistNode>();
            var current = checklist.Resolve(anchor) ?? throw new InvalidDataException("Unknown admitted anchor.");
            while (current.ParentId is not null)
            {
                if (current.Purpose == "fixed-category") ancestry.Push(current);
                current = checklist.Resolve(current.ParentId) ?? throw new InvalidDataException("Incomplete checklist ancestry.");
            }
            if (current.Selector != root.Selector) throw new InvalidDataException("Cross-selector checklist anchor.");
            var node = root;
            foreach (var category in ancestry) node = AddNode(node, root.Selector, category.Label, category.Id, viewId, fixedOrder: category.Order);
            return node;
        }
        IEnumerable<string> EntityEvidence(RegisteredCanonicalEntity entity) => entity.EvidenceIds
            .Concat(entity.Names.Where(n => !n.IsAlias && n.Locale == context.Locale).SelectMany(n => n.EvidenceIds))
            .Concat(entity.Applicability.Where(a => a.GameId == context.GameId && (a.ProfileId is null || a.ProfileId == context.ProfileId)).SelectMany(a => a.EvidenceIds))
            .Concat(candidate.Relationships.Where(r => r.SubjectId == entity.Id && r.Semantic is "based-on" or "variant-of" or "asset-of").SelectMany(r => r.EvidenceIds));
        // A single source alias that extends the primary name (e.g. "Downtown" -> "Downtown Los Santos") is the
        // source-owned identity; exact collisions are qualified only by an evidence-backed parent entity.
        var labels = admitted.Values.ToDictionary(e => e.Id, e => e.Names
            .Where(n => n.IsAlias && n.Locale == context.Locale && n.Value.StartsWith(names[e.Id] + " ", StringComparison.Ordinal))
            .Select(n => n.Value).Distinct(StringComparer.Ordinal).ToArray() is [var qualified] ? qualified : names[e.Id], StringComparer.Ordinal);
        var colliding = labels.Values.GroupBy(v => v, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        string Label(RegisteredCanonicalEntity entity, Node parent) =>
            colliding.Contains(labels[entity.Id]) && parent.Entity is not null
                ? labels[entity.Id] + " (" + parent.Label + ")" : labels[entity.Id];
        void AddEntity(Node parent, RegisteredCanonicalEntity entity, string viewId, string[]? relationshipEvidence = null, bool descend = true)
        {
            var node = AddNode(parent, entity.Selector, Label(entity, parent), entity.Id, viewId, entity.Id,
                entity.Kind == RegistrationEntityKind.Entity, EntityEvidence(entity).Concat(relationshipEvidence ?? []));
            if (descend && children.TryGetValue(entity.Id, out var descendants))
                foreach (var edge in descendants) AddEntity(node, admitted[edge.SubjectId], viewId, edge.EvidenceIds);
        }
        // Levels are compared numerically so fractional slots (7.5) sit between their integer neighbours;
        // absent integer tiers are skipped and never materialized as placeholder rows.
        int Level(string anchorId) => checklist.SlotLevel(anchorId);
        var frames = (candidate.Input.RuleSet.LevelFrame ?? []).Where(f => f.Locale == context.Locale).ToArray();
        foreach (var selector in checklist.Nodes.Where(n => n.ParentId is null).OrderBy(n => n.Order).Select(n => n.Selector))
        {
            var root = Root(selector, selector);
            // Parentless entities attach below the deepest frame level shallower than their own level.
            var placements = admitted.Values.Where(e => e.Selector == selector && !parents.ContainsKey(e.Id)).OrderBy(e => e.Id, StringComparer.Ordinal)
                .Select(entity =>
                {
                    var level = checklist.SemanticLevel(entity.Anchor, entity.SemanticLevel);
                    var attach = Anchor(root, entity.Anchor, selector);
                    foreach (var frame in frames.Where(f => f.Selector == selector && Level(f.ChecklistAnchor) < level &&
                                 entity.Anchor.StartsWith(f.ChecklistAnchor + "/", StringComparison.Ordinal)))
                        attach = AddNode(attach, selector, frame.Label, "frame:" + frame.Id, selector, references: frame.EvidenceIds);
                    return (Entity: entity, Level: level, Attach: attach);
                }).ToArray();
            foreach (var group in placements.GroupBy(p => p.Attach))
            {
                // Selectables deeper than the shallowest populated level here lack an intermediate parent.
                var populated = group.Where(p => p.Level > 0).Select(p => p.Level).DefaultIfEmpty(0).Min();
                foreach (var placement in group)
                    AddEntity(placement.Level > populated
                        ? AddNode(group.Key, selector, MiscellaneousLabel, "miscellaneous", selector, trailing: true)
                        : group.Key, placement.Entity, selector);
            }
        }
        // Facets are separate views over the same entities, never additional canonical identities.
        // View obligations come from the contract, including scopes where every facet is unresolved.
        var applicableFacets = candidate.Facets.Where(f => admitted.ContainsKey(f.EntityId) && f.Locale == context.Locale).ToArray();
        var facets = checklist.Nodes.Where(anchor => admitted.Values.Any(e => e.Selector == anchor.Selector &&
                (e.Anchor == anchor.Id || e.Anchor.StartsWith(anchor.Id + "/", StringComparison.Ordinal))))
            .SelectMany(anchor => anchor.SortDimensions.Select(d => (Selector: anchor.Selector, Anchor: anchor.Id, Kind: RegistrationFacetKind.AlternateView, Dimension: d))
                .Concat(anchor.FilterDimensions.Select(d => (Selector: anchor.Selector, Anchor: anchor.Id, Kind: RegistrationFacetKind.Filter, Dimension: d))))
            .Distinct().OrderBy(key => key.Selector, StringComparer.Ordinal).ThenBy(key => key.Anchor, StringComparer.Ordinal)
            .ThenBy(key => key.Kind).ThenBy(key => key.Dimension, StringComparer.Ordinal)
            .Select(key => (Key: key, Items: applicableFacets.Where(f => f.Anchor == key.Anchor && f.Kind == key.Kind && f.Dimension == key.Dimension).ToArray()));
        foreach (var group in facets)
        {
            var scope = admitted.Values.Where(e => e.Selector == group.Key.Selector &&
                (e.Anchor == group.Key.Anchor || e.Anchor.StartsWith(group.Key.Anchor + "/", StringComparison.Ordinal)))
                .OrderBy(e => e.Id, StringComparer.Ordinal).ToArray();
            var values = group.Items.Select(f => f.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (group.Key.Kind == RegistrationFacetKind.AlternateView)
            {
                var viewId = "alternate." + CanonicalRegistrationEncoding.Digest(CanonicalRegistrationEncoding.Bytes(new[] { group.Key.Selector, group.Key.Anchor, group.Key.Dimension, context.Locale }));
                var root = Root(viewId, group.Key.Selector, group.Key.Dimension);
                var anchor = Anchor(root, group.Key.Anchor, viewId);
                foreach (var value in values)
                {
                    var matching = group.Items.Where(f => f.Value == value).ToArray();
                    var category = AddNode(anchor, group.Key.Selector, value, "facet:" + value, viewId, references: matching.SelectMany(f => f.EvidenceIds));
                    foreach (var entityId in matching.Select(f => f.EntityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                        AddEntity(category, admitted[entityId], viewId, matching.Where(f => f.EntityId == entityId).SelectMany(f => f.EvidenceIds).ToArray(), false);
                }
                var withoutFacet = scope.Where(e => !group.Items.Any(f => f.EntityId == e.Id)).ToArray();
                if (withoutFacet.Length > 0)
                {
                    var category = AddNode(anchor, group.Key.Selector, "Unresolved", "unresolved", viewId);
                    foreach (var entity in withoutFacet) AddEntity(category, entity, viewId, descend: false);
                }
            }
            else
            {
                foreach (var value in values.Cast<string?>().Append(null))
                {
                    var matching = value is null ? [] : group.Items.Where(f => f.Value == value).ToArray();
                    var entityIds = matching.Select(f => f.EntityId).ToHashSet(StringComparer.Ordinal);
                    var selected = scope.Where(e => value is null ? !group.Items.Any(f => f.EntityId == e.Id) : entityIds.Contains(e.Id)).ToArray();
                    if (selected.Length == 0) continue;
                    var viewId = "filter." + CanonicalRegistrationEncoding.Digest(CanonicalRegistrationEncoding.Bytes(new[] { group.Key.Selector, group.Key.Anchor, group.Key.Dimension, context.Locale, value }));
                    var root = Root(viewId, group.Key.Selector, group.Key.Dimension, value ?? "Uncategorized");
                    var anchor = Anchor(root, group.Key.Anchor, viewId);
                    foreach (var entity in selected) AddEntity(anchor, entity, viewId, matching.Where(f => f.EntityId == entity.Id).SelectMany(f => f.EvidenceIds).ToArray(), false);
                }
            }
        }
        outputRoot = Path.GetFullPath(outputRoot); Directory.CreateDirectory(outputRoot); CanonicalRegistrationPreparedReader.RejectLinks(outputRoot);
        var stage = Path.Combine(outputRoot, ".staging-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var entries = new List<RegistrationPreparedPath>(); var pageDigests = new HashSet<string>(StringComparer.Ordinal);
        async Task<string> Write<T>(T value)
        {
            cancellationToken.ThrowIfCancellationRequested(); var bytes = CanonicalRegistrationEncoding.Bytes(value);
            if (bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Prepared page exceeds bounded reader capacity.");
            var digest = CanonicalRegistrationEncoding.Digest(bytes); var file = Path.Combine(stage, digest + ".json");
            if (!File.Exists(file)) await File.WriteAllBytesAsync(file, bytes, cancellationToken).ConfigureAwait(false);
            return digest;
        }
        foreach (var node in nodes.Values.OrderBy(n => n.Path, StringComparer.Ordinal))
        {
            var ordered = node.Children.OrderBy(n => n.FixedOrder ?? int.MaxValue).ThenBy(n => n.Trailing).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.Label, StringComparer.Ordinal).ThenBy(n => n.Path, StringComparer.Ordinal).Select(n => n.Row).ToArray();
            var pages = new List<string>();
            for (var offset = 0; offset < Math.Max(1, ordered.Length); offset += CanonicalRegistrationPreparedReader.PageSize)
            {
                var digest = await Write(new RegistrationPreparedPage(node.Path, node.Row, ordered.Skip(offset).Take(CanonicalRegistrationPreparedReader.PageSize).ToArray(), null)).ConfigureAwait(false);
                pages.Add(digest); pageDigests.Add(digest);
            }
            var nodeDigest = await Write(new RegistrationPreparedNode(node.Row, node.Evidence.Select(id => evidence[id]).ToArray())).ConfigureAwait(false);
            entries.Add(new(node.Path, node.Parent, node.Selector, node.Entity, node.Selectable, nodeDigest, pages.ToArray()));
        }
        var descriptor = new RegistrationPreparedDescriptor(1, candidateDigest, checklist.Digest, context,
            views.OrderBy(v => v.Id, StringComparer.Ordinal).ToArray(), entries.ToArray(), CanonicalRegistrationEncoding.Ordered(pageDigests), CanonicalRegistrationEncoding.NotPublished);
        var descriptorBytes = CanonicalRegistrationEncoding.Bytes(descriptor);
        if (descriptorBytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("Candidate descriptor exceeds reader bounds.");
        var generationDigest = CanonicalRegistrationEncoding.Digest(descriptorBytes);
        await File.WriteAllBytesAsync(Path.Combine(stage, "descriptor.json"), descriptorBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = Path.Combine(outputRoot, generationDigest);
        if (Directory.Exists(destination))
        {
            using var existing = await CanonicalRegistrationPreparedReader.OpenAsync(destination, generationDigest, context, cancellationToken).ConfigureAwait(false);
            // Reuse only after checking the existing content files, not merely the descriptor.
            foreach (var path in descriptor.Paths)
            {
                string? cursor = null;
                do
                {
                    var page = await existing.GetChildrenAsync(path.PathId, cursor, cancellationToken).ConfigureAwait(false);
                    cursor = page.ContinuationCursor;
                } while (cursor is not null);
                await existing.GetEvidenceAsync(path.PathId, cancellationToken).ConfigureAwait(false);
            }
            // The unused stage remains inert for inspection. No publication pointer is written.
        }
        else Directory.Move(stage, destination);
        return new(destination, generationDigest, descriptor);
    }
}
