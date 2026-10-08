using System.Collections.Immutable;

using System.Diagnostics;

using Grid.Core.Models;



namespace Grid.GtaV.Knowledge;



public static class GtaVLocationHierarchyRegistrationRefreshSupport

{

    internal static GtaVLocationHierarchyCorpusIndex LoadIndex(RegistrationRefreshContext? context)

    {

        var references = ResolveReferencesDirectory(context);

        return GtaVLocationHierarchyCorpusIndex.LoadFromRepository(references);

    }



    /// <summary>The corpus-driven v3 Location corpus when its manifest is present; null keeps the legacy v2 path.</summary>
    public static GtaVLocationRegistrationCorpus? TryLoadCorpus(RegistrationRefreshContext? context)
    {
        string references;
        try { references = ResolveReferencesDirectory(context); }
        catch (FileNotFoundException) { return null; }
        return GtaVLocationRegistrationCorpus.IsAvailable(references)
            ? GtaVLocationRegistrationCorpus.LoadFromRepository(references)
            : null;
    }

    internal static string ResolveReferencesDirectory(RegistrationRefreshContext? context)

    {

        if (context is not null)

        {

            var wiki = context.ResourcePaths.GtaLocationHierarchyWikiPath;

            if (!string.IsNullOrWhiteSpace(wiki))

                return Path.GetDirectoryName(Path.GetFullPath(wiki))!;

        }



        var repository = Environment.GetEnvironmentVariable("GRID_REPOSITORY_ROOT");

        if (!string.IsNullOrWhiteSpace(repository))

        {

            var root = Path.Combine(Path.GetFullPath(repository), "scripts", "games", "grandtheftautov", "catalog", "references");

            if (Directory.Exists(root)) return root;

        }



        var baseDir = AppContext.BaseDirectory;

        var bundled = Path.Combine(baseDir, "RegistrationReferences", "gta-v", "location-hierarchy");

        if (File.Exists(Path.Combine(bundled, "vinewood-411759.normalized.txt"))) return bundled;



        throw new FileNotFoundException("Approved GTA Location hierarchy reference sources are unavailable.");

    }



    private const string ReferenceGeographySourceFamilyId = "grid.gta-v.location-hierarchy.reference-geography";

    public static CanonicalCatalogPayload StripPriorReferenceHierarchy(CanonicalCatalogPayload payload)

    {

        var hierarchyAdapter = new KnowledgeAdapterId("grid.gta-v.enhanced.presentation-location-hierarchy");

        var removedRelationships = payload.RelationshipAssertions

            .Where(value => value.SemanticId == LocationRelationshipSemantics.ContainedBy &&

                value.SourceNativeRelationshipType.StartsWith("reference.", StringComparison.Ordinal))

            .ToImmutableArray();

        if (removedRelationships.IsEmpty &&

            payload.AdapterDescriptors.All(value => value.AdapterId != hierarchyAdapter))

            return payload;



        var removedRelationshipContent = removedRelationships

            .Select(value => EvidenceClaimContentId.DeriveV1(value).Value)

            .ToHashSet(StringComparer.Ordinal);

        var removedAssertions = payload.CrossSourceAssertions

            .Where(value => value.AssertionKind == CrossSourceCanonicalAssertionKind.Relationship &&

                (removedRelationshipContent.Contains(value.UnderlyingClaimContentId.Value) ||

                 value.TargetLinkMethodId == "grid.gta-v.location-hierarchy.pinned-exact-name-join"))

            .ToImmutableArray();

        var removedLinkIds = removedAssertions.Select(value => value.TargetLinkClaimId).ToHashSet();



        var removedAdapterRevisionIds = payload.AdapterDescriptors

            .Where(value => value.AdapterId == hierarchyAdapter)

            .Select(value => value.RevisionId)

            .ToHashSet();

        var removedSourceRevisionIds = payload.SourceRevisions

            .Where(value => removedAdapterRevisionIds.Contains(value.AdapterRevisionId))

            .Select(value => value.Revision.Id)

            .ToHashSet();

        foreach (var relationship in removedRelationships)

            removedSourceRevisionIds.Add(relationship.SourceRevisionId);

        removedAssertions = removedAssertions
            .Concat(payload.CrossSourceAssertions.Where(value =>
                removedSourceRevisionIds.Contains(value.AssertingSourceRevisionId) ||
                removedSourceRevisionIds.Contains(value.TargetOriginSourceRevisionId)))
            .DistinctBy(value => value.Id)
            .ToImmutableArray();
        removedLinkIds = removedAssertions.Select(value => value.TargetLinkClaimId)
            .Concat(payload.CrossSourceTargetLinkClaims
                .Where(value =>
                    removedSourceRevisionIds.Contains(value.AssertingSourceRevisionId) ||
                    removedSourceRevisionIds.Contains(value.TargetOriginSourceRevisionId))
                .Select(value => value.Id))
            .ToHashSet();

        var retainedSourceRevisions = payload.SourceRevisions

            .Where(value => !removedSourceRevisionIds.Contains(value.Revision.Id))

            .ToImmutableArray();

        var retainedAdapterRevisionIds = retainedSourceRevisions

            .Select(value => value.AdapterRevisionId)

            .ToHashSet();

        var retainedArtifactIds = retainedSourceRevisions

            .SelectMany(value => value.Revision.ArtifactIds)

            .ToHashSet();

        var removedArtifactIds = payload.SourceRevisions

            .Where(value => removedSourceRevisionIds.Contains(value.Revision.Id))

            .SelectMany(value => value.Revision.ArtifactIds)

            .Where(id => !retainedArtifactIds.Contains(id))

            .ToHashSet();

        var artifacts = payload.Artifacts

            .Where(value => !removedArtifactIds.Contains(value.Id))

            .ToImmutableArray();

        var artifactAcquisitionBindings = payload.ArtifactAcquisitionBindings

            .Where(value => !removedArtifactIds.Contains(value.ArtifactId))

            .ToImmutableArray();

        return new CanonicalCatalogPayload(

            payload.EffectiveCoverage,

            payload.AdapterDescriptors

                .Where(value => value.AdapterId != hierarchyAdapter && retainedAdapterRevisionIds.Contains(value.RevisionId))

                .ToImmutableArray(),

            payload.Sources
                .Where(source => retainedSourceRevisions.Any(value => value.Revision.SourceId == source.Id))
                .ToImmutableArray(),

            artifacts,

            retainedSourceRevisions,

            // Reference geography records belong to the stripped revisions; registration re-materializes them.
            payload.KnowledgeRecords
                .Where(value => !(removedSourceRevisionIds.Contains(value.SourceRevisionId) &&
                    (value.NativeIdentity.Namespace == GtaVLocationHierarchyCorpusIndex.ReferenceGeographyNativeNamespace ||
                     value.NativeIdentity.Namespace == GtaVLocationRegistrationRules.ReferenceSubjectNamespace)))
                .ToImmutableArray(),

            payload.TerminologyAssertions

                .Where(value => !removedSourceRevisionIds.Contains(value.SourceRevisionId))

                .ToImmutableArray(),

            payload.RelationshipAssertions

                .Where(value => !removedRelationshipContent.Contains(EvidenceClaimContentId.DeriveV1(value).Value))

                .ToImmutableArray(),

            payload.FileEvidenceReceipts

                .Where(value => !removedSourceRevisionIds.Contains(value.Receipt.SourceRevisionId))

                .ToImmutableArray(),

            payload.ReferenceEvidenceReceipts

                .Where(value => !removedSourceRevisionIds.Contains(value.Receipt.SourceRevisionId))

                .ToImmutableArray(),

            payload.EvidenceBindings

                .Where(value => !removedSourceRevisionIds.Contains(value.SourceRevisionId))

                .ToImmutableArray(),

            payload.CorrelationEnvelopes,

            payload.UnresolvedSourceAssertions,

            payload.AcquisitionReceipts,

            artifactAcquisitionBindings)

        {

            SourceNativeLocationTypeAssertions = payload.SourceNativeLocationTypeAssertions,

            LocationSemanticClassificationAssertions = payload.LocationSemanticClassificationAssertions,

            RecordLifecycleAssertions = payload.RecordLifecycleAssertions,

            CorrelatedRelationshipEnvelopes = payload.CorrelatedRelationshipEnvelopes,

            LocationCoverageReports = payload.LocationCoverageReports
                .Where(value => !value.Manifest.SourceFamilies.Any(family =>
                    family.SourceFamilyId.Value == ReferenceGeographySourceFamilyId))
                .ToImmutableArray(),

            SemanticClassificationAssertions = payload.SemanticClassificationAssertions

                .Where(value => !removedSourceRevisionIds.Contains(value.SourceRevisionId))

                .ToImmutableArray(),

            RecordContributionAssertions = payload.RecordContributionAssertions

                .Where(value => !removedSourceRevisionIds.Contains(value.SourceRevisionId))

                .ToImmutableArray(),

            OrganizationalValueAssertions = payload.OrganizationalValueAssertions

                .Where(value => !removedSourceRevisionIds.Contains(value.SourceRevisionId))

                .ToImmutableArray(),

            InstructionAssertions = payload.InstructionAssertions,

            InstructionEvidenceBindings = payload.InstructionEvidenceBindings,

            InstructionConflictGroups = payload.InstructionConflictGroups,

            CrossSourceAssertions = payload.CrossSourceAssertions

                .Where(value => !removedAssertions.Any(removed => removed.Id == value.Id))

                .ToImmutableArray(),

            CrossSourceTargetLinkClaims = payload.CrossSourceTargetLinkClaims

                .Where(value => !removedLinkIds.Contains(value.Id))

                .ToImmutableArray(),

            UnresolvedCrossSourceClaimContents = payload.UnresolvedCrossSourceClaimContents,

            UnresolvedCrossSourceEvidenceBindings = payload.UnresolvedCrossSourceEvidenceBindings,

            UnresolvedCrossSourceAssertions = payload.UnresolvedCrossSourceAssertions,

        };

    }



    internal static CatalogBuildProvenance BuildRegistrationRefreshProvenance(string contributorId, string contributorVersion)

    {

        var root = ResolveRepositoryRoot();

        string Git(params string[] arguments)

        {

            var start = new ProcessStartInfo("git")

            {

                WorkingDirectory = root,

                RedirectStandardOutput = true,

                RedirectStandardError = true,

                UseShellExecute = false,

                CreateNoWindow = true,

            };

            foreach (var argument in arguments)

                start.ArgumentList.Add(argument);

            using var process = Process.Start(start) ?? throw new InvalidDataException("Git is unavailable for registration refresh provenance.");

            var text = process.StandardOutput.ReadToEnd();

            var error = process.StandardError.ReadToEnd();

            process.WaitForExit();

            if (process.ExitCode != 0)

                throw new InvalidDataException(error);

            return text;

        }



        string[] scope =

        [

            "src/Grid.GtaV.Knowledge/GtaVRegistrationMdboRefreshAuthor.cs",

            "src/Grid.GtaV.Knowledge/GtaVLocationHierarchyRegistrationEvidenceAdapter.cs",

            "src/Grid.GtaV.Knowledge/GtaVLocationHierarchyCorpusIndex.cs",

            "src/Grid.GtaV.Knowledge/GtaVLocationHierarchySecondaryAssertionAdapter.cs",

            "src/Grid.GtaV.Knowledge/GtaVLocationHierarchyRegistrationCandidatePackageProjector.cs",

            "scripts/games/grandtheftautov/catalog/gta_v_enhanced_location_hierarchy_sources.v2.json",
            "scripts/games/grandtheftautov/catalog/gta_v_enhanced_location_hierarchy_sources.v3.json",
            "scripts/games/grandtheftautov/catalog/gta_v_enhanced_location_discovery.v1.json",
            "src/Grid.GtaV.Knowledge/GtaVLocationRegistrationCorpus.cs",
            "src/Grid.GtaV.Knowledge/GtaVLocationEvidenceGrammar.cs",
            "src/Grid.GtaV.Knowledge/GtaVLocationRegistrationResolver.cs",
            "src/Grid.GtaV.Knowledge/GtaVLocationRegistrationRules.cs",
            "src/Grid.GtaV.Knowledge/GtaVLocationRegistrationEvidenceAdapter.cs",

            "scripts/games/grandtheftautov/catalog/references",

        ];

        var paths = Git(["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", .. scope])

            .Split('\0', StringSplitOptions.RemoveEmptyEntries)

            .Distinct()

            .Order(StringComparer.Ordinal)

            .ToArray();

        var tracked = Git(["ls-files", "-z", "--", .. scope])

            .Split('\0', StringSplitOptions.RemoveEmptyEntries)

            .ToHashSet(StringComparer.Ordinal);

        var modified = Git(["diff", "--name-only", "-z", "HEAD", "--", .. scope])

            .Split('\0', StringSplitOptions.RemoveEmptyEntries)

            .ToHashSet(StringComparer.Ordinal);

        var inputs = paths.Select(path => new CatalogDevelopmentBuildInput(

                path,

                ContentDigest.ComputeSha256(File.ReadAllBytes(Path.Combine(root, path))),

                !tracked.Contains(path)

                    ? CatalogDevelopmentBuildInputState.Untracked

                    : modified.Contains(path)

                        ? CatalogDevelopmentBuildInputState.TrackedWorktreeModified

                        : CatalogDevelopmentBuildInputState.HeadTrackedClean))

            .ToImmutableArray();

        return CatalogBuildProvenance.CreateDevelopment(contributorId, contributorVersion, Git("rev-parse", "HEAD").Trim(), inputs);

    }



    private static string ResolveRepositoryRoot()

    {

        var fromEnvironment = Environment.GetEnvironmentVariable("GRID_REPOSITORY_ROOT");

        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(Path.Combine(fromEnvironment, ".git")))

            return Path.GetFullPath(fromEnvironment);

        for (var directory = AppContext.BaseDirectory; !string.IsNullOrEmpty(directory); directory = Path.GetDirectoryName(directory)!)

        {

            if (Directory.Exists(Path.Combine(directory, ".git")))

                return directory;

        }



        throw new InvalidDataException("Repository root is unavailable for registration refresh provenance.");

    }

}

