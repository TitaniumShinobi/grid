using Grid.Core.Models;

using Grid.Core.Services;



namespace Grid.GtaV.Knowledge;



/// <summary>Production IRegistrationEvidenceAdapter bound to MDBO discover-evidence for GTA hierarchy registration.</summary>

public sealed class GtaVLocationHierarchyRegistrationEvidenceAdapter(

    CanonicalCatalogPayload baselinePayload,

    GtaVLocationHierarchyCorpusIndex index,

    ProfileId profileId,

    CatalogPackageId originPackageId) : IRegistrationEvidenceAdapter

{

    private readonly CanonicalCatalogPayload baselinePayload =

        baselinePayload ?? throw new ArgumentNullException(nameof(baselinePayload));

    private readonly GtaVLocationHierarchyCorpusIndex index =

        index ?? throw new ArgumentNullException(nameof(index));

    private readonly ProfileId profileId = profileId;

    private readonly CatalogPackageId originPackageId = originPackageId;



    public string Id => GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterId;

    public string Version => GtaVLocationHierarchyRegistrationEvidenceCoordinates.AdapterVersion;



    public Task<RegistrationEvidenceSet> ExtractAsync(

        IReadOnlyList<RegistrationSourceArtifact> sources,

        CancellationToken cancellationToken = default)

    {

        cancellationToken.ThrowIfCancellationRequested();

        CanonicalRelationshipRegistrationProductionBindings.EnsureProductionEvidenceAdapter(this);

        if (sources.Count == 0)

            throw new InvalidDataException("Production hierarchy registration requires admitted source artifacts.");



        var admitted = sources.OrderBy(s => s.Source.Id, StringComparer.Ordinal).ToArray();

        var expected = GtaVLocationHierarchyRegistrationSourceArtifacts.CreateAdmittedSources(index);

        if (admitted.Length != expected.Length ||

            !admitted.Zip(expected).All(pair =>

                pair.First.Source.Id == pair.Second.Source.Id &&

                pair.First.Source.Sha256 == pair.Second.Source.Sha256 &&

                pair.First.Bytes.SequenceEqual(pair.Second.Bytes)))

            throw new InvalidDataException("Admitted hierarchy sources do not match the pinned corpus index.");



        var nativeSource = admitted.Single(s => s.Source.Id == index.NativeTable.Id.Value);

        var gameId = ProductionGridCatalogService.GrandTheftAutoVEnhancedId.Value;

        var resolution = index.ResolveForRegistration(baselinePayload);

        var evidence = new List<RegistrationEvidence>();

        var entities = new Dictionary<string, RegistrationEntityClaim>(StringComparer.Ordinal);

        var relationships = new List<RegistrationRelationshipClaim>();

        var sanAndreasSource = admitted.Single(s => s.Source.Id == index.SanAndreasWiki.Id.Value);
        evidence.Add(new(GtaVLocationHierarchyRegistrationRules.SanAndreasFrameEvidenceId, sanAndreasSource.Source.Id,
            GtaVLocationHierarchyRegistrationRules.SanAndreasFrameLocator, EvidenceVerificationKind.ReferenceVerified));
        var levels = ClassifyLevels(resolution.CorrelatedRows, index, sanAndreasSource.Source.Id, evidence);



        foreach (var row in resolution.CorrelatedRows)

        {

            cancellationToken.ThrowIfCancellationRequested();

            var source = admitted.Single(s => s.Source.Id == row.EvidenceArtifact.Id.Value);

            var child = row.Child;

            var parent = row.Parent;

            var relationshipEvidenceId = "evidence:relationship:" + row.SourceFieldPath;

            var childEndpointEvidenceId = "evidence:endpoint:" + row.SourceFieldPath + ":child";

            var parentEndpointEvidenceId = "evidence:endpoint:" + row.SourceFieldPath + ":parent";



            evidence.Add(new(relationshipEvidenceId, source.Source.Id, row.SourceFieldPath,

                EvidenceVerificationKind.ReferenceVerified));

            evidence.Add(new(childEndpointEvidenceId, source.Source.Id, row.SourceFieldPath + "/child",

                EvidenceVerificationKind.ReferenceVerified));

            evidence.Add(new(parentEndpointEvidenceId, source.Source.Id, row.SourceFieldPath + "/parent",

                EvidenceVerificationKind.ReferenceVerified));



            var relationshipEvidenceIds = new List<string> { relationshipEvidenceId };

            var childEvidenceIds = new List<string> { childEndpointEvidenceId };

            var parentEvidenceIds = new List<string> { parentEndpointEvidenceId };



            if (!child.ReferenceOnly && child.NativeZoneCode is not null)

            {

                var childNativeBridgeId = "evidence:native-bridge:" + row.SourceFieldPath + ":child";

                evidence.Add(new(childNativeBridgeId, nativeSource.Source.Id, "Zones/" + child.NativeZoneCode,

                    EvidenceVerificationKind.ReferenceVerified));

                childEvidenceIds.Add(childNativeBridgeId);

                relationshipEvidenceIds.Add(childNativeBridgeId);

            }

            if (!parent.ReferenceOnly && parent.NativeZoneCode is not null)

            {

                var parentNativeBridgeId = "evidence:native-bridge:" + row.SourceFieldPath + ":parent";

                evidence.Add(new(parentNativeBridgeId, nativeSource.Source.Id, "Zones/" + parent.NativeZoneCode,

                    EvidenceVerificationKind.ReferenceVerified));

                parentEvidenceIds.Add(parentNativeBridgeId);

                relationshipEvidenceIds.Add(parentNativeBridgeId);

            }



            if (!entities.ContainsKey(child.EntityKey))
                entities.Add(child.EntityKey, CreateEntityClaim(child, gameId, source.Source.Id, childEvidenceIds.ToArray(), profileId, originPackageId, levels[child.EntityKey]));

            if (!entities.ContainsKey(parent.EntityKey))
                entities.Add(parent.EntityKey, CreateEntityClaim(parent, gameId, source.Source.Id, parentEvidenceIds.ToArray(), profileId, originPackageId, levels[parent.EntityKey]));

            relationships.Add(new(

                "edge:" + row.SourceFieldPath,

                child.EntityKey,

                parent.EntityKey,

                CanonicalRelationshipRegistrationSemantics.ContainedBy,

                relationshipEvidenceIds.ToArray(),

                source.Source.Id,

                row.SourceNativeRelationshipType,

                row.SourceFieldPath));

        }



        foreach (var pending in resolution.UnresolvedEdges)

        {

            cancellationToken.ThrowIfCancellationRequested();

            var source = admitted.Single(s => s.Source.Id == pending.EvidenceArtifact.Id.Value);

            var relationshipEvidenceId = "evidence:relationship:" + pending.SourceFieldPath;

            evidence.Add(new(relationshipEvidenceId, source.Source.Id, pending.SourceFieldPath,

                EvidenceVerificationKind.ReferenceVerified));

            relationships.Add(new(

                "edge:unresolved:" + pending.SourceFieldPath,

                pending.SubjectKey ?? string.Empty,

                pending.TargetKey ?? string.Empty,

                CanonicalRelationshipRegistrationSemantics.ContainedBy,

                [relationshipEvidenceId],

                source.Source.Id,

                pending.SourceNativeRelationshipType,

                pending.SourceFieldPath));

        }



        if (relationships.Count != index.ExpectedRelationshipCount)

            throw new InvalidDataException("Production hierarchy evidence produced an unexpected relationship count.");



        return Task.FromResult(new RegistrationEvidenceSet(

            evidence.ToArray(),

            entities.Values.OrderBy(e => e.Key, StringComparer.Ordinal).ToArray(),

            relationships.ToArray(),

            []));

    }



    private sealed record EntityLevel(string Classification, string[] LevelEvidenceIds, RegistrationName[] QualifiedNames);

    /// <summary>Assigns each endpoint its semantic mold level from the containment grammar and San Andreas county sections.</summary>
    private static Dictionary<string, EntityLevel> ClassifyLevels(
        IEnumerable<GtaVLocationHierarchyRow> rows,
        GtaVLocationHierarchyCorpusIndex index,
        string sanAndreasSourceId,
        List<RegistrationEvidence> evidence)
    {
        var rank = new Dictionary<string, int>(StringComparer.Ordinal);
        var levelEvidence = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var qualified = new Dictionary<string, RegistrationName>(StringComparer.Ordinal);
        var namesByKey = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Raise(string key, int level) => rank[key] = Math.Max(rank.GetValueOrDefault(key), level);
        const int City = 1, County = 2;
        // Each child's sub-level comes from its own subdivision sentence, never from its parent's depth,
        // so one mis-levelled district cannot shift its whole branch.
        var subdivided = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Raise(row.Child.EntityKey, 0);
            Raise(row.Parent.EntityKey, 0);
            (namesByKey.TryGetValue(row.Parent.EntityKey, out var parentNames) ? parentNames : namesByKey[row.Parent.EntityKey] = new(StringComparer.Ordinal)).Add(row.ParentName);
            switch (row.SourceNativeRelationshipType)
            {
                case "reference.county-section-member-list":
                    Raise(row.Parent.EntityKey, County);
                    if (row.ChildName.StartsWith("City of ", StringComparison.Ordinal)) Raise(row.Child.EntityKey, City);
                    break;
                case "reference.town-inside-desert":
                    Raise(row.Parent.EntityKey, County);
                    break;
                case "reference.city-within-municipality":
                    Raise(row.Parent.EntityKey, City);
                    Raise(row.Child.EntityKey, City);
                    break;
                case "reference.neighbourhood-of-district":
                case "reference.district-neighbourhood-division":
                case "reference.district-neighbourhood-split":
                case "reference.district-smaller-neighbourhood":
                    if (subdivided.Add(row.Child.EntityKey))
                        (levelEvidence.TryGetValue(row.Child.EntityKey, out var subIds) ? subIds : levelEvidence[row.Child.EntityKey] = [])
                            .Add("evidence:relationship:" + row.SourceFieldPath);
                    break;
            }
            var separator = row.SourceFieldPath.LastIndexOf('/');
            var section = separator > 0 ? row.SourceFieldPath[..separator] : string.Empty;
            if (section.StartsWith(row.ParentName + " ", StringComparison.Ordinal) && !section.Contains(" / ", StringComparison.Ordinal))
                qualified.TryAdd(row.Parent.EntityKey, new(section, "en-US", true, ["evidence:endpoint:" + row.SourceFieldPath + ":parent"]));
        }
        foreach (var sectionPath in index.SanAndreasCountySectionPaths)
        {
            var county = sectionPath[GtaVLocationHierarchyCorpusIndex.SanAndreasCountySectionPrefix.Length..];
            foreach (var key in namesByKey.Where(pair => pair.Value.Contains(county)).Select(pair => pair.Key))
            {
                if (rank[key] >= County) continue;
                var evidenceId = "evidence:level:" + sectionPath;
                if (!evidence.Any(e => e.Id == evidenceId))
                    evidence.Add(new(evidenceId, sanAndreasSourceId, sectionPath, EvidenceVerificationKind.ReferenceVerified));
                Raise(key, County);
                (levelEvidence.TryGetValue(key, out var ids) ? ids : levelEvidence[key] = []).Add(evidenceId);
            }
        }
        return rank.ToDictionary(pair => pair.Key, pair => new EntityLevel(
            pair.Value switch
            {
                County => GtaVLocationHierarchyRegistrationRules.CountyRegionClassification,
                City => GtaVLocationHierarchyRegistrationRules.CityClassification,
                _ when subdivided.Contains(pair.Key) => GtaVLocationHierarchyRegistrationRules.SubNeighborhoodClassification,
                _ => GtaVLocationHierarchyRegistrationRules.NeighborhoodClassification,
            },
            levelEvidence.TryGetValue(pair.Key, out var ids) ? ids.ToArray() : [],
            qualified.TryGetValue(pair.Key, out var name) ? [name] : []), StringComparer.Ordinal);
    }

    private static RegistrationEntityClaim CreateEntityClaim(

        GtaVLocationHierarchyResolvedEndpoint endpoint,

        string gameId,

        string sourceId,

        string[] endpointEvidenceIds,

        ProfileId profileId,

        CatalogPackageId originPackageId,

        EntityLevel level)

    {

        var evidenceIds = endpointEvidenceIds.Concat(level.LevelEvidenceIds).ToArray();

        RegistrationName[] names = [new(endpoint.PrimaryName, "en-US", false, endpointEvidenceIds), .. level.QualifiedNames];

        if (endpoint.ReferenceOnly)

        {

            return new(

                endpoint.EntityKey,

                sourceId,

                GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace,

                endpoint.PrimaryName,

                "Location",

                gameId,

                level.Classification,

                RegistrationEntityKind.Entity,

                names,

                [new(gameId, profileId.Value, endpointEvidenceIds)],

                [new("Reference", GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace, endpoint.PrimaryName, endpointEvidenceIds)],

                evidenceIds);

        }



        var record = endpoint.CatalogRecord ?? throw new InvalidDataException("Native endpoint lacks catalog record.");

        return new(

            endpoint.EntityKey,

            sourceId,

            record.NativeIdentity.Namespace,

            record.NativeIdentity.ExactRepresentation,

            "Location",

            gameId,

            level.Classification,

            RegistrationEntityKind.Entity,

            names,

            [new(gameId, profileId.Value, endpointEvidenceIds)],

            [new("Game", record.NativeIdentity.Namespace, endpoint.NativeZoneCode!, endpointEvidenceIds)],

            evidenceIds,

            record.Id.Value,

            originPackageId.Value);

    }

}


