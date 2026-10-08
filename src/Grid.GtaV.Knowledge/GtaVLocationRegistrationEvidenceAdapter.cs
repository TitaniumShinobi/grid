using System.Collections.Immutable;
using System.Globalization;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.GtaV.Knowledge;

/// <summary>Admitted registration sources: every pinned corpus artifact plus the payload-derived native zone inventory.</summary>
public static class GtaVLocationRegistrationSourceArtifacts
{
    public const string AdapterVersion = CanonicalRelationshipRegistrationProductionBindings.ProductionLocationRegistrationAdapterVersion;
    public const string InventoryUriPrefix = "grid-catalog://game.grandtheftautov-enhanced/location-registration/native-zone-inventory/";

    public static RegistrationSourceArtifact[] CreateAdmittedSources(GtaVLocationRegistrationCorpus corpus, CanonicalCatalogPayload payload)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        var sources = corpus.Artifacts.Select(artifact => new RegistrationSourceArtifact(
            new RegistrationSource(artifact.Id.Value, artifact.SourceCoordinate.ExactRepresentation, KnowledgeSourceKind.ReferenceProvider,
                artifact.Digest.HexValue, GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId, AdapterVersion,
                GtaVLocationRegistrationRules.SourceFamily),
            artifact.ExactBytes.ToArray())).ToList();
        sources.Add(Inventory(payload));
        return sources.OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();
    }

    public static RegistrationSourceArtifact Inventory(CanonicalCatalogPayload payload)
    {
        var bytes = GtaVLocationRegistrationResolver.NativeInventoryBytes(GtaVLocationRegistrationResolver.NativeZones(payload));
        var digest = ContentDigest.ComputeSha256(bytes);
        return new(new RegistrationSource("grid.location-registration.native-zone-inventory.sha256." + digest.HexValue,
            InventoryUriPrefix + digest.HexValue, KnowledgeSourceKind.FrozenRepositoryDataset, digest.HexValue,
            GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId, AdapterVersion, GtaVLocationRegistrationRules.SourceFamily), bytes);
    }
}

/// <summary>
/// Corpus-driven production Location registration evidence. Native population zones are always registered;
/// reference pages add identity joins, levels and containment through generic grammars only.
/// </summary>
public sealed class GtaVLocationRegistrationEvidenceAdapter(
    CanonicalCatalogPayload baselinePayload,
    GtaVLocationRegistrationCorpus corpus,
    ProfileId profileId,
    CatalogPackageId originPackageId) : IRegistrationEvidenceAdapter
{
    public const string IdentityLocator = "#subject";

    public string Id => GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId;
    public string Version => GtaVLocationRegistrationSourceArtifacts.AdapterVersion;

    public GtaVLocationResolution? Resolution { get; private set; }

    public Task<RegistrationEvidenceSet> ExtractAsync(IReadOnlyList<RegistrationSourceArtifact> sources, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CanonicalRelationshipRegistrationProductionBindings.EnsureProductionEvidenceAdapter(this);
        var admitted = sources.OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();
        var expected = GtaVLocationRegistrationSourceArtifacts.CreateAdmittedSources(corpus, baselinePayload);
        if (admitted.Length != expected.Length || !admitted.Zip(expected).All(p => p.First.Source.Id == p.Second.Source.Id &&
                p.First.Source.Sha256 == p.Second.Source.Sha256 && p.First.Bytes.AsSpan().SequenceEqual(p.Second.Bytes)))
            throw new InvalidDataException("Admitted Location registration sources do not match the pinned corpus and native inventory.");
        var inventory = admitted.Single(s => s.Source.Kind == KnowledgeSourceKind.FrozenRepositoryDataset);
        var cfx = corpus.NativeTable;
        var cfxCodes = corpus.NativeNameCodes.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value;

        var resolution = Resolution = GtaVLocationRegistrationResolver.Resolve(corpus, baselinePayload);
        var evidence = new Dictionary<string, RegistrationEvidence>(StringComparer.Ordinal);
        string Span(FrozenSourceArtifact artifact, string locator)
        {
            var id = "ev:" + artifact.Id.Value + "#" + locator;
            evidence.TryAdd(id, new(id, artifact.Id.Value, locator, EvidenceVerificationKind.ReferenceVerified));
            return id;
        }
        var entities = new List<RegistrationEntityClaim>();
        var identityEvidence = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var node in resolution.Nodes.Concat(resolution.UnresolvedNodes))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identity = new List<string>();
            foreach (var page in node.Pages) identity.Add(Span(page.Page.Artifact, IdentityLocator));
            var levelEvidence = node.LevelVotes.Where(v => v.Level == node.Level && v.Span is not null)
                .Select(v => Span(v.Span!.Page.Artifact, v.Span.Locator)).Distinct(StringComparer.Ordinal).ToArray();
            var classification = node.Level is { } level ? GtaVLocationRegistrationRules.Classification(level) : GtaVLocationRegistrationRules.UnresolvedClassification;
            RegistrationEntityClaim claim;
            if (node.Native is { } zone)
            {
                var nativeId = "ev:native-zone#NameLabel/" + zone.Code;
                evidence.TryAdd(nativeId, new(nativeId, inventory.Source.Id, "NameLabel/" + zone.Code, EvidenceVerificationKind.FileVerified));
                identity.Insert(0, nativeId);
                if (cfxCodes.Contains(zone.Code)) identity.Add(Span(cfx, "Zones/" + zone.Code.ToUpperInvariant()));
                var ids = identity.ToArray();
                claim = new(node.Key, inventory.Source.Id, zone.Record.NativeIdentity.Namespace, zone.Record.NativeIdentity.ExactRepresentation,
                    "Location", gameId, classification, RegistrationEntityKind.Entity, Names(node, ids),
                    [new(gameId, profileId.Value, ids)], [new("Game", zone.Record.NativeIdentity.Namespace, zone.Code, ids)],
                    ids.Concat(levelEvidence).Distinct(StringComparer.Ordinal).ToArray(), zone.Record.Id.Value, originPackageId.Value);
            }
            else
            {
                var ids = identity.ToArray();
                var identityPage = GtaVLocationRegistrationRules.IdentityPage(node.Pages);
                claim = new(node.Key, identityPage.Artifact.Id.Value, GtaVLocationRegistrationRules.ReferenceSubjectNamespace, identityPage.Coordinate,
                    "Location", gameId, classification, RegistrationEntityKind.Entity, Names(node, ids),
                    [new(gameId, profileId.Value, ids)], [new("Reference", GtaVLocationRegistrationRules.ReferenceSubjectNamespace, identityPage.Coordinate, ids)],
                    ids.Concat(levelEvidence).Distinct(StringComparer.Ordinal).ToArray());
            }
            identityEvidence[node.Key] = identity.ToArray();
            entities.Add(claim);
        }

        var relationships = new List<RegistrationRelationshipClaim>();
        foreach (var edge in resolution.Edges)
        {
            var spans = edge.Evidence.Select(s => Span(s.Page.Artifact, s.Locator)).Distinct(StringComparer.Ordinal).ToArray();
            relationships.Add(new("edge:" + Short(edge.ChildKey) + ">" + Short(edge.ParentKey), edge.ChildKey, edge.ParentKey,
                CanonicalRelationshipRegistrationSemantics.ContainedBy, spans, edge.Primary.Page.Artifact.Id.Value,
                "reference." + edge.Primary.Grammar, edge.Primary.Locator));
        }
        // Unresolved containment statements stay visible as unresolved claims, never as guessed edges.
        var ordinal = 0;
        foreach (var entry in resolution.Ledger.Where(e => e.Surface == "statement" && e.Outcome == "unresolved" && e.Coordinate is not null && e.Locator is not null))
        {
            var page = corpus.Pages.First(p => p.Coordinate == entry.Coordinate);
            var id = Span(page.Artifact, entry.Locator!);
            relationships.Add(new("edge:unresolved:" + (ordinal++).ToString("D6", CultureInfo.InvariantCulture), string.Empty, string.Empty,
                CanonicalRelationshipRegistrationSemantics.ContainedBy, [id], page.Artifact.Id.Value, "reference.unresolved:" + entry.Reason, entry.Locator));
        }
        return Task.FromResult(new RegistrationEvidenceSet(
            evidence.Values.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
            entities.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(),
            relationships.ToArray(),
            []));
    }

    private static RegistrationName[] Names(GtaVLocationNode node, string[] evidenceIds) =>
        [new(node.PrimaryName, "en-US", false, evidenceIds), .. node.Aliases.Select(a => new RegistrationName(a, "en-US", true, evidenceIds))];

    private static string Short(string key) => key.Length > 24 ? key[^24..] : key;
}

/// <summary>Projects a verified v3 registration candidate into importable payload assertions.</summary>
public static class GtaVLocationRegistrationCandidatePackageProjector
{
    public static string RevisionVersion(GtaVLocationRegistrationCorpus corpus) =>
        "3+location-corpus.sha256." + corpus.ManifestDigest.HexValue;

    public static GtaVSecondaryAssertionBatch Project(ContentDigest adapterDigest, GtaVLocationRegistrationCorpus corpus,
        CanonicalCatalogPayload origin, KnowledgeSourceScope scope, CanonicalRegistrationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("Registration candidate must remain NOT_PUBLISHED.");
        var descriptor = GtaVLocationHierarchyEvidenceBuilder.DescriptorFor("location-hierarchy", adapterDigest, KnowledgeKind.Location);
        var builder = new GtaVLocationHierarchyEvidenceBuilder(origin, scope, descriptor, corpus.Artifacts, corpus.ResolvePinnedRevision, RevisionVersion(corpus));
        var evidenceById = candidate.Input.Evidence.Evidence.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var artifacts = corpus.Artifacts.ToDictionary(a => a.Id.Value, StringComparer.Ordinal);
        var records = new Dictionary<string, CanonicalKnowledgeRecord>(StringComparer.Ordinal);
        var entities = candidate.Entities.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var entity in candidate.Entities.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var record = builder.EnsureRegistrationEntity(entity, evidenceById);
            if (!string.Equals(record.Id.Value, entity.Id, StringComparison.Ordinal))
                throw new InvalidDataException("Projected knowledge record id must match the registration candidate entity id.");
            records[entity.Id] = record;
        }
        foreach (var relationship in candidate.Relationships.Where(r => r.CorrelationOutcome == RegistrationOutcome.Correlated)
                     .OrderBy(r => r.SourceFieldPath, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(relationship.SourceId) || string.IsNullOrWhiteSpace(relationship.SourceFieldPath) ||
                string.IsNullOrWhiteSpace(relationship.SourceNativeRelationshipType))
                throw new InvalidDataException("Correlated Location relationship lacks source provenance.");
            var child = records[relationship.SubjectId];
            var parent = records[relationship.TargetId];
            var artifact = artifacts.GetValueOrDefault(relationship.SourceId)
                ?? throw new InvalidDataException("Location relationship source artifact is absent from the pinned corpus.");
            // A published relationship binding must sit at the relationship's own field path; spans at other
            // locators corroborate the registration ruling and stay in its evidence, not in the claim binding.
            var supporting = relationship.EvidenceIds.Select(id => evidenceById[id])
                .Where(e => artifacts.ContainsKey(e.SourceId) && string.Equals(e.Locator, relationship.SourceFieldPath, StringComparison.Ordinal))
                .Select(e => new GtaVPresentationEvidence(artifacts[e.SourceId], e.Locator, true))
                .ToArray();
            var primary = new GtaVPresentationEvidence(artifact, relationship.SourceFieldPath!, true);
            var link = string.Equals(entities[relationship.SubjectId].NativeNamespace, GtaVLocationRegistrationRules.ReferenceSubjectNamespace, StringComparison.Ordinal)
                ? builder.LinkReferenceEndpoint(child, new GtaVPresentationEvidence(artifact, relationship.SourceFieldPath + "/child", true))
                : builder.Link(child, primary);
            builder.AddRelationship(child, link, new RelationshipAssertion(child.Id, builder.RevisionId, LocationRelationshipSemantics.ContainedBy,
                relationship.SourceNativeRelationshipType!, relationship.SourceFieldPath!, parent.NativeIdentity, parent.Id), supporting);
        }
        return builder.Build();
    }
}
