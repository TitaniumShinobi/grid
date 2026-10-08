if (args.SequenceEqual(new[] { "--location-hierarchy" }))
{
    GtaVLocationHierarchyChecks.Run();
    return;
}

if (args.Contains("--plan2", StringComparer.Ordinal))
{
    var catalog = Path.Combine(GitBuildProvenanceResolver.FindRepositoryRoot(), "scripts", "games", "grandtheftautov", "catalog");
    var registry = GtaRegistrationSourceRegistry.Load(
        Path.Combine(catalog, "gta_v_enhanced_registration_sources.v6.json"),
        Path.Combine(catalog, "gta_v_enhanced_source_families.v2.json"),
        Path.Combine(catalog, "gta_v_enhanced_actor_source_families.v1.json"),
        Path.Combine(catalog, "gta_v_enhanced_spatial_source_families.v1.json"));
    if (registry.SchemaVersion != 6) throw new InvalidDataException("Plan 2 requires its pinned v6 source registry.");
    var focused = await GtaVRouteKnowledgeChecks.RunAsync();
    focused += await GtaVMountedItemRegistrationChecks.RunAsync();
    focused += await GtaVActorPresentationChecks.RunAsync();
    focused += await GtaVOnlineActivityOrganizationChecks.RunAsync();
    focused += await GtaVOnlineActivityRegistryChecks.RunAsync();
    focused += await GtaVSecondaryAssertionChecks.RunAsync();
    Console.WriteLine($"All {focused} focused Plan 2 GTA knowledge checks passed.");
    return;
}

var checks = await GtaVWeaponsMetaKnowledgeChecks.RunAsync();
checks += await GtaVEnhancedSourceKnowledgeChecks.RunAsync();
checks += await GtaVSecondaryAssertionChecks.RunAsync();
checks += await GtaVEnrichmentIntegrationChecks.RunAsync();
checks += await GtaVCorpusIndexChecks.RunAsync();
checks += await GtaVOnlineActivityRegistryChecks.RunAsync();
checks += GtaVRockstarCloudSnapshotBundleChecks.Run();
checks += await GtaVMountedActorRegistrationChecks.RunAsync();
checks += await GtaVMountedSpatialRegistrationChecks.RunAsync();
checks += await GtaVAcquisitionAndGitProvenanceChecks.RunAsync();
checks += await GtaVRouteKnowledgeChecks.RunAsync();
checks += await GtaVMountedItemRegistrationChecks.RunAsync();
checks += await GtaVActorPresentationChecks.RunAsync();
checks += await GtaVOnlineActivityOrganizationChecks.RunAsync();
Console.WriteLine($"All {checks} GTA V knowledge checks passed.");
