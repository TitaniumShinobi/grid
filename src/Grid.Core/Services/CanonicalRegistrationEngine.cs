using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Offline, evidence-only registration. No discovery network, runtime binding or publisher is reachable here.</summary>
public static class CanonicalRegistrationEngine
{
    public static async Task<CanonicalRegistrationCandidate> RegisterAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        CancellationToken cancellationToken = default)
        => await RegisterWithLegacyAsync(adapter, sources, rules, [], cancellationToken).ConfigureAwait(false);

    public static Task<CanonicalRegistrationCandidate> RegisterAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        IReadOnlyList<OperationalInstructionSectionRequest> instructionSections,
        CancellationToken cancellationToken = default)
        => RegisterCoreAsync(adapter, sources, rules, [], instructionSections, cancellationToken);

    public static async Task<CanonicalRegistrationCandidate> RegisterWithLegacyAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        IReadOnlyList<CanonicalCatalogPackage> existingPackages, CancellationToken cancellationToken = default)
        => await RegisterCoreAsync(adapter, sources, rules, existingPackages, null, cancellationToken).ConfigureAwait(false);

    private static async Task<CanonicalRegistrationCandidate> RegisterCoreAsync(IRegistrationEvidenceAdapter adapter,
        IReadOnlyList<RegistrationSourceArtifact> sources, RegistrationRuleSet rules,
        IReadOnlyList<CanonicalCatalogPackage> existingPackages,
        IReadOnlyList<OperationalInstructionSectionRequest>? instructionSections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = sources.Select(s => new RegistrationSourceArtifact(s.Source, s.Bytes.ToArray()))
            .OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();
        foreach (var item in frozen)
            if (item.Source.AdapterId != adapter.Id || item.Source.AdapterVersion != adapter.Version ||
                CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256)
                throw new InvalidDataException("Source bytes or adapter coordinates do not match the admitted manifest.");
        rules = System.Text.Json.JsonSerializer.Deserialize<RegistrationRuleSet>(CanonicalRegistrationEncoding.Bytes(rules), CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Mapping rules are empty.");
        var discovered = await adapter.DiscoverAsync(frozen, cancellationToken).ConfigureAwait(false);
        if (!discovered.OrderBy(s => s.Id, StringComparer.Ordinal).SequenceEqual(frozen.Select(s => s.Source)))
            throw new InvalidDataException("Discovery must account for exactly the admitted source set.");
        var evidence = await adapter.ExtractAsync(frozen, cancellationToken).ConfigureAwait(false);
        if (frozen.Any(item => CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256))
            throw new InvalidDataException("An adapter modified its admitted source bytes.");
        if (instructionSections is { Count: > 0 })
        {
            var inspection = CanonicalOperationalInstructionInspector.Inspect(frozen, instructionSections);
            evidence = evidence with
            {
                Evidence = evidence.Evidence.Concat(inspection.Evidence).ToArray(),
                Instructions = inspection.Instructions.Select(OperationalInstructionInspection.ToClaim).ToArray(),
            };
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Evaluate(Normalize(new(frozen.Select(s => s.Source).ToArray(), rules, evidence)), existingPackages);
    }

    internal static RegistrationInput Normalize(RegistrationInput input)
    {
        static string[] E(string[] values) => CanonicalRegistrationEncoding.Ordered(values);
        static RegistrationName[] Names(IEnumerable<RegistrationName> values) => values
            .Select(n => n with { EvidenceIds = E(n.EvidenceIds) }).OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray();
        static RegistrationApplicability[] Applies(IEnumerable<RegistrationApplicability> values) => values
            .Select(a => a with { EvidenceIds = E(a.EvidenceIds) }).OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray();
        static RegistrationOrigin[] Origins(IEnumerable<RegistrationOrigin> values) => values
            .Select(a => a with { EvidenceIds = E(a.EvidenceIds) }).OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray();
        return new(input.Sources.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray(),
            input.RuleSet with
            {
                Rules = input.RuleSet.Rules.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                LevelFrame = input.RuleSet.LevelFrame?.Select(f => f with { EvidenceIds = E(f.EvidenceIds) })
                    .OrderBy(f => f.Selector, StringComparer.Ordinal).ThenBy(f => f.ChecklistAnchor, StringComparer.Ordinal).ToArray(),
            },
            new(input.Evidence.Evidence.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
                input.Evidence.Entities.Select(e => e with { Names = Names(e.Names), Applicability = Applies(e.Applicability),
                    Origins = Origins(e.Origins), EvidenceIds = E(e.EvidenceIds) }).OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(),
                input.Evidence.Relationships.Select(r => r with { EvidenceIds = E(r.EvidenceIds) }).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(),
                input.Evidence.Facets.Select(f => f with { EvidenceIds = E(f.EvidenceIds) }).OrderBy(f => f.Id, StringComparer.Ordinal).ToArray(),
                input.Evidence.Instructions is null or { Length: 0 } ? null :
                    input.Evidence.Instructions.Select(i => i with
                    {
                        Applicability = Applies(i.Applicability),
                        EvidenceIds = E(i.EvidenceIds),
                    }).OrderBy(i => i.Id, StringComparer.Ordinal).ToArray()));
    }

    internal static CanonicalRegistrationCandidate Evaluate(RegistrationInput input, IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
    {
        var checklist = CanonicalRegistrationChecklist.Load();
        var legacy = new Dictionary<string, CanonicalCatalogPackage>(StringComparer.Ordinal);
        foreach (var package in existingPackages ?? [])
        {
            if (!CanonicalCatalogPackageKernel.Verify(package).IsStructurallyValid || !legacy.TryAdd(package.Id.Value, package))
                throw new InvalidDataException("Legacy package reference is invalid or duplicated.");
        }
        RequireText(input.RuleSet.Version);
        Unique(input.Sources.Select(s => s.Id), "source");
        Unique(input.RuleSet.Rules.Select(r => r.Id), "rule");
        Unique(input.Evidence.Evidence.Select(e => e.Id), "evidence");
        Unique(input.Evidence.Entities.Select(e => e.Key), "entity claim");
        Unique(input.Evidence.Relationships.Select(e => e.Id), "relationship claim");
        Unique(input.Evidence.Facets.Select(e => e.Id), "facet claim");
        Unique((input.Evidence.Instructions ?? []).Select(e => e.Id), "instruction claim");
        var sources = input.Sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var source in sources.Values)
        {
            RequireText(source.AdapterId); RequireText(source.AdapterVersion); RequireText(source.Family);
            if (!Uri.TryCreate(source.Uri, UriKind.Absolute, out _) || !Enum.IsDefined(source.Kind) ||
                !CanonicalRegistrationEncoding.IsDigest(source.Sha256)) throw new InvalidDataException("Invalid source coordinate.");
        }
        var evidence = input.Evidence.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var e in evidence.Values)
        {
            if (!sources.ContainsKey(e.SourceId) || string.IsNullOrWhiteSpace(e.Locator) || !Enum.IsDefined(e.Basis))
                throw new InvalidDataException("Evidence lacks an exact source, locator or verification basis.");
            var sourceKind = sources[e.SourceId].Kind;
            if (sourceKind is KnowledgeSourceKind.ReferenceProvider or KnowledgeSourceKind.OfficialProvider or KnowledgeSourceKind.ModProvider &&
                e.Basis != EvidenceVerificationKind.ReferenceVerified ||
                sourceKind is KnowledgeSourceKind.LocalGameDistribution or KnowledgeSourceKind.LocalModArtifact or KnowledgeSourceKind.PluginRecordFile &&
                e.Basis != EvidenceVerificationKind.FileVerified)
                throw new InvalidDataException("Evidence verification basis is incompatible with its source kind.");
        }
        foreach (var rule in input.RuleSet.Rules)
        {
            RequireText(rule.SourceFamily); RequireText(rule.SourceClassification);
            var anchor = checklist.Resolve(rule.ChecklistAnchor);
            if (anchor is null || anchor.Selector != rule.Selector)
                throw new InvalidDataException("Mapping rule references an unknown or foreign fixed root.");
            if (rule.SemanticLevel is { } semantic && (anchor.Purpose != "entity-slot" ||
                semantic <= checklist.SlotLevel(anchor.Id) || semantic >= checklist.SlotLevel(anchor.Id) + 1))
                throw new InvalidDataException("Fractional semantic level must lie strictly between its entity-slot anchor and the next slot.");
        }
        Unique((input.RuleSet.LevelFrame ?? []).Select(f => f.Id), "level frame");
        foreach (var frame in (input.RuleSet.LevelFrame ?? []).GroupBy(f => f.Selector, StringComparer.Ordinal))
        {
            string? previous = null;
            foreach (var node in frame)
            {
                RequireText(node.Label); RequireText(node.Locale);
                var anchor = checklist.Resolve(node.ChecklistAnchor);
                if (anchor is null || anchor.Selector != node.Selector || anchor.Purpose != "entity-slot" ||
                    (previous is not null && !node.ChecklistAnchor.StartsWith(previous + "/", StringComparison.Ordinal)) ||
                    node.EvidenceIds.Any(id => !evidence.ContainsKey(id)))
                    throw new InvalidDataException("Level frame must be a nested entity-slot chain with admitted evidence.");
                previous = node.ChecklistAnchor;
            }
        }
        bool Backed(string[] ids) => ids.Length > 0 && ids.All(evidence.ContainsKey);
        var rulings = new List<RegistrationRuling>();
        void Rule(string id, string stage, RegistrationOutcome outcome, string reason, string[] ids) =>
            rulings.Add(new(id, stage, outcome, reason, CanonicalRegistrationEncoding.Ordered(ids)));
        var pending = new List<RegisteredCanonicalEntity>();
        foreach (var entity in input.Evidence.Entities)
        {
            if (!sources.TryGetValue(entity.SourceId, out var source) || !Backed(entity.EvidenceIds) ||
                !entity.EvidenceIds.Any(id => evidence[id].SourceId == entity.SourceId) ||
                string.IsNullOrWhiteSpace(entity.NativeNamespace) || string.IsNullOrWhiteSpace(entity.NativeId) ||
                !Enum.IsDefined(entity.Kind))
            { Rule(entity.Key, "identity", RegistrationOutcome.Rejected, "identity-evidence-missing", entity.EvidenceIds); continue; }
            string? establishedId = null;
            if (entity.ExistingRecordId is not null || entity.ExistingPackageId is not null)
            {
                var record = entity.ExistingPackageId is not null && legacy.TryGetValue(entity.ExistingPackageId, out var package)
                    ? package.Payload.KnowledgeRecords.SingleOrDefault(r => r.Id.Value == entity.ExistingRecordId) : null;
                if (record is null || record.NativeIdentity.Namespace != entity.NativeNamespace ||
                    record.NativeIdentity.ExactRepresentation != entity.NativeId || record.GameId.Value != entity.GameId ||
                    record.Kind.ToString() != entity.Selector)
                { Rule(entity.Key, "identity", RegistrationOutcome.Unresolved, "verified-legacy-package-reference-required", entity.EvidenceIds); continue; }
                establishedId = record.Id.Value;
            }
            var matches = input.RuleSet.Rules.Where(r => r.SourceFamily == source.Family &&
                r.Selector == entity.Selector && r.SourceClassification == entity.SourceClassification)
                .Select(r => (r.ChecklistAnchor, r.SemanticLevel)).Distinct().ToArray();
            if (matches.Length != 1)
            { Rule(entity.Key, "classification", matches.Length > 1 ? RegistrationOutcome.Ambiguous : RegistrationOutcome.Unresolved,
                matches.Length > 1 ? "multiple-fixed-root-mappings" : "no-fixed-root-mapping", entity.EvidenceIds); continue; }
            var shared = entity.Selector is "Tool" or "Mod";
            if (!shared && string.IsNullOrWhiteSpace(entity.GameId))
            { Rule(entity.Key, "identity", RegistrationOutcome.Unresolved, "game-scope-required", entity.EvidenceIds); continue; }
            var names = entity.Names.Where(n => !string.IsNullOrWhiteSpace(n.Value) && !string.IsNullOrWhiteSpace(n.Locale) && Backed(n.EvidenceIds)).ToArray();
            var applicability = entity.Applicability.Where(a => !string.IsNullOrWhiteSpace(a.GameId) &&
                (a.ProfileId is null || !string.IsNullOrWhiteSpace(a.ProfileId)) && Backed(a.EvidenceIds) &&
                (shared || a.GameId == entity.GameId)).ToArray();
            var origins = entity.Origins.Where(o => !string.IsNullOrWhiteSpace(o.Kind) &&
                !string.IsNullOrWhiteSpace(o.NativeNamespace) && !string.IsNullOrWhiteSpace(o.NativeId) && Backed(o.EvidenceIds)).ToArray();
            if (names.Length != entity.Names.Length || applicability.Length != entity.Applicability.Length || origins.Length != entity.Origins.Length)
                Rule(entity.Key, "metadata", RegistrationOutcome.Unresolved, "optional-metadata-evidence-incomplete", entity.EvidenceIds);
            if (!names.Any(n => !n.IsAlias)) Rule(entity.Key, "terminology", RegistrationOutcome.Unresolved, "primary-name-unresolved", entity.EvidenceIds);
            if (applicability.Length == 0) Rule(entity.Key, "applicability", RegistrationOutcome.Unresolved, "applicability-unresolved", entity.EvidenceIds);
            var identity = establishedId ?? CanonicalRegistrationEncoding.Identity(entity.Selector, shared ? null : entity.GameId, entity.NativeNamespace, entity.NativeId);
            pending.Add(new(identity, [entity.Key], entity.Selector, entity.NativeNamespace, entity.NativeId,
                shared ? null : entity.GameId, matches[0].ChecklistAnchor, entity.Kind, names, applicability, origins, entity.EvidenceIds,
                entity.ExistingPackageId, matches[0].SemanticLevel));
        }
        var entities = new List<RegisteredCanonicalEntity>();
        foreach (var group in pending.GroupBy(e => e.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            if (group.Select(e => (e.Anchor, e.Kind, e.SemanticLevel)).Distinct().Count() > 1)
            {
                foreach (var e in group) Rule(e.SourceKeys[0], "identity", RegistrationOutcome.Ambiguous, "conflicting-identity-classification", e.EvidenceIds);
                continue;
            }
            var combined = first with
            {
                SourceKeys = CanonicalRegistrationEncoding.Ordered(group.SelectMany(e => e.SourceKeys)),
                Names = Distinct(group.SelectMany(e => e.Names)),
                Applicability = Distinct(group.SelectMany(e => e.Applicability)),
                Origins = Distinct(group.SelectMany(e => e.Origins)),
                EvidenceIds = CanonicalRegistrationEncoding.Ordered(group.SelectMany(e => e.EvidenceIds)),
            };
            entities.Add(combined);
            foreach (var key in combined.SourceKeys) Rule(key, "entity", RegistrationOutcome.Correlated,
                first.Kind is RegistrationEntityKind.TechnicalArtifact or RegistrationEntityKind.Variant ? "technical-evidence-retained-not-selectable" : "identity-and-root-established", combined.EvidenceIds);
            foreach (var conflict in combined.Names.Where(n => !n.IsAlias).GroupBy(n => n.Locale).Where(g => g.Select(n => n.Value).Distinct().Count() > 1))
                Rule(combined.Id + ":" + conflict.Key, "terminology", RegistrationOutcome.Ambiguous, "conflicting-primary-names", CanonicalRegistrationEncoding.Ordered(conflict.SelectMany(n => n.EvidenceIds)));
        }
        var byKey = entities.SelectMany(e => e.SourceKeys.Select(k => (Key: k, Entity: e))).ToDictionary(x => x.Key, x => x.Entity, StringComparer.Ordinal);
        EvidenceVerificationKind RelationshipBasis(string[] ids)
        {
            var bases = ids.Select(id => evidence[id].Basis).Distinct().ToArray();
            return bases.Length == 1 ? bases[0] : EvidenceVerificationKind.FileVerified;
        }
        bool TryResolveRelationshipSource(RegistrationRelationshipClaim relationship, out string sourceId)
        {
            sourceId = string.Empty;
            if (!Backed(relationship.EvidenceIds))
                return false;
            var relationshipEvidence = relationship.EvidenceIds
                .Select(id => evidence[id])
                .ToArray();
            var evidenceSources = relationshipEvidence
                .Select(item => item.SourceId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (evidenceSources.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(relationship.SourceFieldPath))
                return false;
            if (!string.IsNullOrWhiteSpace(relationship.SourceId))
            {
                if (!relationshipEvidence.Any(item =>
                        string.Equals(item.Locator, relationship.SourceFieldPath, StringComparison.Ordinal) &&
                        string.Equals(item.SourceId, relationship.SourceId, StringComparison.Ordinal)))
                    return false;
                sourceId = relationship.SourceId!;
                return sources.ContainsKey(sourceId);
            }
            var matching = relationshipEvidence
                .Where(item => string.Equals(item.Locator, relationship.SourceFieldPath, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1)
                return false;
            sourceId = matching[0].SourceId;
            return sources.ContainsKey(sourceId);
        }
        var possible = new List<(RegistrationRelationshipClaim Claim, RegisteredCanonicalEntity Subject, RegisteredCanonicalEntity Target, string Semantic)>();
        foreach (var relationship in input.Evidence.Relationships)
        {
            var semantic = CanonicalRelationshipRegistrationSemantics.Normalize(relationship.Semantic);
            if (!Backed(relationship.EvidenceIds))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Rejected, "relationship-evidence-missing", relationship.EvidenceIds); continue; }
            if (string.IsNullOrWhiteSpace(relationship.SubjectKey) || string.IsNullOrWhiteSpace(relationship.TargetKey))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Unresolved, "relationship-endpoint-unresolved", relationship.EvidenceIds); continue; }
            if (!TryResolveRelationshipSource(relationship, out var relationshipSource))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Rejected, "relationship-source-mismatch", relationship.EvidenceIds); continue; }
            if (!byKey.TryGetValue(relationship.SubjectKey, out var subject) || !byKey.TryGetValue(relationship.TargetKey, out var target))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Unresolved, "relationship-endpoint-unresolved", relationship.EvidenceIds); continue; }
            if (semantic is not (CanonicalRelationshipRegistrationSemantics.ContainedBy or CanonicalRelationshipRegistrationSemantics.ClassifiedAs
                or CanonicalRelationshipRegistrationSemantics.BelongsTo or CanonicalRelationshipRegistrationSemantics.ApplicableTo
                or CanonicalRelationshipRegistrationSemantics.AlternateViewMembership or CanonicalRelationshipRegistrationSemantics.VariantOf
                or CanonicalRelationshipRegistrationSemantics.AssetOf or CanonicalRelationshipRegistrationSemantics.BasedOn))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Unresolved, "relationship-semantic-unsupported", relationship.EvidenceIds); continue; }
            if (semantic == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership)
            {
                possible.Add((relationship, subject, target, semantic));
                continue;
            }
            if (CanonicalRelationshipRegistrationSemantics.IsHierarchyEdge(semantic) &&
                (subject.Selector != target.Selector || (subject.GameId is not null && target.GameId is not null && subject.GameId != target.GameId) ||
                 subject.Id == target.Id ||
                 subject.Kind is RegistrationEntityKind.TechnicalArtifact or RegistrationEntityKind.Variant ||
                 target.Kind is RegistrationEntityKind.TechnicalArtifact or RegistrationEntityKind.Variant))
            { Rule(relationship.Id, "relationship", RegistrationOutcome.Rejected, "invalid-semantic-parent", relationship.EvidenceIds); continue; }
            possible.Add((relationship, subject, target, semantic));
        }
        var graph = possible.Where(r => CanonicalRelationshipRegistrationSemantics.IsHierarchyEdge(r.Claim.Semantic))
            .Select(r =>
            {
                var (child, parent) = CanonicalRelationshipRegistrationSemantics.OrientEndpoints(
                    r.Claim.Semantic, r.Subject.Id, r.Target.Id);
                return (Child: child, Parent: parent);
            }).GroupBy(r => r.Child).ToDictionary(g => g.Key, g => g.Select(r => r.Parent).Distinct().ToArray());
        bool Reaches(string from, string to)
        {
            var seen = new HashSet<string>(); var queue = new Queue<string>(); queue.Enqueue(from);
            while (queue.TryDequeue(out var next)) { if (next == to) return true; if (seen.Add(next) && graph.TryGetValue(next, out var parents)) foreach (var parent in parents) queue.Enqueue(parent); }
            return false;
        }
        var relationships = new List<RegisteredCanonicalRelationship>();
        foreach (var relationship in possible)
        {
            var (child, parent) = CanonicalRelationshipRegistrationSemantics.OrientEndpoints(
                relationship.Claim.Semantic, relationship.Subject.Id, relationship.Target.Id);
            var cycle = CanonicalRelationshipRegistrationSemantics.IsHierarchyEdge(relationship.Claim.Semantic) && Reaches(parent, child);
            var outcome = cycle ? RegistrationOutcome.Rejected :
                relationship.Claim.Semantic == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership
                    ? RegistrationOutcome.Correlated : RegistrationOutcome.Correlated;
            var reason = cycle ? "cyclic-parentage" :
                relationship.Claim.Semantic == CanonicalRelationshipRegistrationSemantics.AlternateViewMembership
                    ? "alternate-view-separate-from-containment" : "exact-endpoints-and-evidence";
            Rule(relationship.Claim.Id, "relationship", cycle ? RegistrationOutcome.Rejected : outcome, reason, relationship.Claim.EvidenceIds);
            if (cycle) continue;
            if (!TryResolveRelationshipSource(relationship.Claim, out var admittedSource))
            { Rule(relationship.Claim.Id, "relationship", RegistrationOutcome.Rejected, "relationship-source-mismatch", relationship.Claim.EvidenceIds); continue; }
            var storedSubject = child;
            var storedTarget = parent;
            if (!CanonicalRelationshipRegistrationSemantics.IsHierarchyEdge(relationship.Claim.Semantic))
            {
                storedSubject = relationship.Subject.Id;
                storedTarget = relationship.Target.Id;
            }
            relationships.Add(new(relationship.Claim.Id, storedSubject, storedTarget, relationship.Claim.Semantic,
                admittedSource, relationship.Claim.SourceNativeRelationshipType, relationship.Claim.SourceFieldPath,
                RelationshipBasis(relationship.Claim.EvidenceIds), RegistrationOutcome.Correlated, reason, relationship.Claim.EvidenceIds));
        }
        var facets = new List<RegisteredCanonicalFacet>();
        foreach (var facet in input.Evidence.Facets)
        {
            if (!byKey.TryGetValue(facet.EntityKey, out var entity) || !Backed(facet.EvidenceIds) ||
                string.IsNullOrWhiteSpace(facet.Value) || string.IsNullOrWhiteSpace(facet.Locale) || !Enum.IsDefined(facet.Kind))
            { Rule(facet.Id, "facet", RegistrationOutcome.Unresolved, "facet-evidence-or-entity-unresolved", facet.EvidenceIds); continue; }
            var anchor = checklist.Resolve(entity.Anchor);
            while (anchor is not null && !(facet.Kind == RegistrationFacetKind.AlternateView ? anchor.SortDimensions : anchor.FilterDimensions).Contains(facet.Dimension, StringComparer.Ordinal))
                anchor = anchor.ParentId is null ? null : checklist.Resolve(anchor.ParentId);
            if (anchor is null)
            { Rule(facet.Id, "facet", RegistrationOutcome.Rejected, "dimension-outside-fixed-contract", facet.EvidenceIds); continue; }
            facets.Add(new(facet.Id, entity.Id, facet.Kind, anchor.Id, facet.Dimension, facet.Value, facet.Locale, facet.EvidenceIds));
            Rule(facet.Id, "facet", RegistrationOutcome.Correlated, "facet-separate-from-parentage", facet.EvidenceIds);
        }
        foreach (var alias in entities.SelectMany(e => e.Names.Where(n => n.IsAlias).Select(n => (Entity: e, Name: n)))
                     .GroupBy(x => (x.Entity.Selector, x.Name.Locale, x.Name.Value)))
            if (alias.Select(a => a.Entity.Id).Distinct().Count() > 1)
                Rule(CanonicalRegistrationEncoding.Digest(alias.Key.ToString()), "alias", RegistrationOutcome.Ambiguous,
                    "alias-multiple-identities", CanonicalRegistrationEncoding.Ordered(alias.SelectMany(a => a.Name.EvidenceIds)));
        var instructions = new List<RegisteredOperationalInstruction>();
        foreach (var claim in input.Evidence.Instructions ?? [])
        {
            if (!Enum.IsDefined(claim.Kind) || !Enum.IsDefined(claim.Presence) ||
                !sources.ContainsKey(claim.SourceId) || string.IsNullOrWhiteSpace(claim.Locator) ||
                claim.VerbatimText is null || !CanonicalRegistrationEncoding.IsDigest(claim.ContentSha256))
            { Rule(claim.Id, "instruction", RegistrationOutcome.Rejected, "instruction-evidence-missing", claim.EvidenceIds); continue; }
            if (claim.ContentSha256 != OperationalInstructionInspection.ContentDigest(claim.VerbatimText))
                throw new InvalidDataException("Instruction content digest does not match retained wording.");
            if (!Backed(claim.EvidenceIds) ||
                !claim.EvidenceIds.Any(id => evidence[id].SourceId == claim.SourceId &&
                    string.Equals(evidence[id].Locator, claim.Locator, StringComparison.Ordinal)))
            { Rule(claim.Id, "instruction", RegistrationOutcome.Rejected, "instruction-evidence-missing", claim.EvidenceIds); continue; }
            var applicability = (claim.Applicability ?? [])
                .Where(a => !string.IsNullOrWhiteSpace(a.GameId) &&
                    (a.ProfileId is null || !string.IsNullOrWhiteSpace(a.ProfileId)) && Backed(a.EvidenceIds)).ToArray();
            if (applicability.Length != (claim.Applicability?.Length ?? 0))
                Rule(claim.Id, "instruction", RegistrationOutcome.Unresolved, "optional-metadata-evidence-incomplete", claim.EvidenceIds);
            instructions.Add(new(claim.Id, claim.SourceId, claim.Locator, claim.Kind,
                OperationalInstructionInspection.CategoryIdFor(claim.Kind), claim.Presence, claim.VerbatimText,
                claim.ContentSha256, applicability, claim.EvidenceIds));
            var reason = claim.Presence switch
            {
                OperationalInstructionSectionPresence.Present => "instruction-section-admitted",
                OperationalInstructionSectionPresence.Missing => "instruction-section-missing",
                OperationalInstructionSectionPresence.ProvenAbsence => "instruction-section-absent",
                OperationalInstructionSectionPresence.EmptyUnresolved => "instruction-section-empty",
                _ => "instruction-evidence-missing",
            };
            Rule(claim.Id, "instruction",
                claim.Presence == OperationalInstructionSectionPresence.Present
                    ? RegistrationOutcome.Correlated : RegistrationOutcome.Unresolved,
                reason, claim.EvidenceIds);
        }
        foreach (var group in instructions
                     .Where(item => item.Presence == OperationalInstructionSectionPresence.Present &&
                                    item.VerbatimText.Length > 0)
                     .GroupBy(item => string.Join('\u001f', item.Kind.ToString(),
                         string.Join('\u001e', item.Applicability.Select(value => value.GameId + "\u001d" + (value.ProfileId ?? ""))))))
        {
            if (group.Select(item => item.VerbatimText).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            foreach (var item in group)
                Rule(item.Id, "instruction", RegistrationOutcome.Ambiguous, "instruction-guidance-conflicting", item.EvidenceIds);
        }
        var orderedRelationships = relationships.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray();
        var orderedRulings = rulings.OrderBy(r => r.Stage, StringComparer.Ordinal).ThenBy(r => r.ClaimId, StringComparer.Ordinal)
            .ThenBy(r => r.Reason, StringComparer.Ordinal).ToArray();
        var orderedInstructions = instructions.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
        var candidate = new CanonicalRegistrationCandidate(1, CanonicalRegistrationEncoding.EngineVersion, checklist.Version, checklist.Digest,
            CanonicalRegistrationEncoding.Digest(input.RuleSet), input, entities.ToArray(), orderedRelationships,
            facets.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray(), orderedRulings, CanonicalRegistrationEncoding.NotPublished,
            orderedInstructions.Length == 0 ? null : orderedInstructions);
        CanonicalRelationshipGraphValidator.Validate(candidate);
        return candidate;
    }

    public static string[] ResolveAlias(CanonicalRegistrationCandidate candidate, string selector, string value, string locale) =>
        CanonicalRegistrationEncoding.Ordered(candidate.Entities.Where(e => e.Selector == selector &&
            e.Names.Any(n => n.Locale == locale && n.Value == value)).Select(e => e.Id));

    public static CanonicalRegistrationRunReceipt CreateReceipt(CanonicalRegistrationCandidate candidate,
        IReadOnlyList<RegistrationPreparedCandidate> prepared, IReadOnlyList<CanonicalCatalogPackage>? existingPackages = null)
    {
        CanonicalRegistrationCandidateVerifier.Verify(candidate, existingPackages);
        var digest = CanonicalRegistrationEncoding.Digest(candidate);
        if (prepared.Any(p => p.Descriptor.CandidateDigest != digest || p.Descriptor.PublicationState != CanonicalRegistrationEncoding.NotPublished))
            throw new InvalidDataException("Prepared receipt coordinates differ from the canonical candidate.");
        var identityRulings = candidate.Rulings.Where(r => r.Stage is "identity" or "classification").ToArray();
        var aliasGroups = candidate.Entities.SelectMany(e => e.Names.Where(n => n.IsAlias).Select(n => (e.Id, e.Selector, n.Locale, n.Value)))
            .GroupBy(a => (a.Selector, a.Locale, a.Value));
        return new(candidate.EngineVersion, candidate.ChecklistVersion, candidate.ChecklistDigest, candidate.RuleDigest,
            candidate.Input.Sources, prepared.Select(p => p.Descriptor.Context).Distinct().OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray(),
            candidate.Input.Evidence.Entities.Length, candidate.Entities.Length,
            identityRulings.Count(r => r.Outcome == RegistrationOutcome.Unresolved),
            identityRulings.Count(r => r.Outcome is RegistrationOutcome.Rejected or RegistrationOutcome.Ambiguous),
            candidate.Relationships.Length, candidate.Input.Evidence.Relationships.Length - candidate.Relationships.Length,
            candidate.Rulings.Count(r => r.Stage == "relationship" && r.Outcome == RegistrationOutcome.Unresolved),
            Counts(candidate.Rulings.Where(r => r.Stage == "relationship").Select(r => r.Outcome.ToString())),
            Counts(candidate.Entities.Select(e => e.Selector)), Counts(candidate.Entities.Select(e => e.Anchor)),
            candidate.Entities.Count(e => e.Kind == RegistrationEntityKind.Category),
            CanonicalRegistrationEncoding.Ordered(candidate.Facets.Where(f => f.Kind == RegistrationFacetKind.AlternateView).Select(f => f.Dimension)),
            CanonicalRegistrationEncoding.Ordered(candidate.Facets.Where(f => f.Kind == RegistrationFacetKind.Filter).Select(f => f.Dimension)),
            aliasGroups.Count(g => g.Select(a => a.Id).Distinct().Count() == 1),
            candidate.Rulings.Count(r => r.Outcome == RegistrationOutcome.Ambiguous), candidate.Rulings, "PASSED", digest,
            CanonicalRegistrationEncoding.Ordered(prepared.Select(p => p.Digest)), CanonicalRegistrationEncoding.NotPublished);
    }

    internal static bool Hierarchy(string semantic) =>
        CanonicalRelationshipRegistrationSemantics.IsHierarchyEdge(CanonicalRelationshipRegistrationSemantics.Normalize(semantic));
    private static T[] Distinct<T>(IEnumerable<T> values) => values.GroupBy(CanonicalRegistrationEncoding.Digest)
        .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.First()).ToArray();
    private static RegistrationCount[] Counts(IEnumerable<string> values) => values.GroupBy(v => v, StringComparer.Ordinal)
        .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RegistrationCount(g.Key, g.Count())).ToArray();
    private static void RequireText(string value) { if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Required registration coordinate is empty."); }
    private static void Unique(IEnumerable<string> values, string kind)
    { var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var value in values) { RequireText(value); if (!seen.Add(value)) throw new InvalidDataException("Duplicate " + kind + " identity."); } }
}
