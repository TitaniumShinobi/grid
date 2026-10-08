using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

if (args.Length != 5)
    throw new ArgumentException("Usage: Grid.CanonicalRuntime.Prepare <source-store> <source-package-id> <isolated-output-directory> <prepared-root> <repository-root>");
var sourcePath = Path.GetFullPath(args[0]);
var sourcePackageId = new CatalogPackageId(args[1]);
var output = Path.GetFullPath(args[2]);
var preparedRoot = Path.GetFullPath(args[3]);
var repository = Path.GetFullPath(args[4]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Preparation output must be new and empty.");
Directory.CreateDirectory(output);
var phases = new List<object>();
var timer = Stopwatch.StartNew();
void Phase(string stage) { phases.Add(new { stage, elapsedMilliseconds = timer.Elapsed.TotalMilliseconds }); Console.WriteLine(stage + ": " + timer.Elapsed); timer.Restart(); }
var sourceDigest = await Digest(sourcePath);
var loaded = await new JsonCanonicalKnowledgeCatalogStore(sourcePath).LoadAsync();
if (!loaded.IsValid) throw new InvalidDataException(string.Join("; ", loaded.Issues));
var origin = loaded.Snapshot.FindImportedPackage(sourcePackageId) ?? throw new InvalidDataException("Pinned package is absent.");
Phase("load-and-validate-original");
var references = Path.Combine(repository, "scripts/games/grandtheftautov/catalog/references");
var index = GtaVLocationHierarchyCorpusIndex.LoadFromRepository(references);
var adapterDigest = ContentDigest.ComputeSha256(File.ReadAllBytes(typeof(GtaVLocationHierarchySecondaryAssertionAdapter).Assembly.Location));
var scope = KnowledgeSourceScope.BaseGame(origin.Manifest.GameScope.GameId, origin.Manifest.GameScope.ExactGameVersion);
var batch = new GtaVLocationHierarchySecondaryAssertionAdapter(adapterDigest, index).Extract(origin.Payload, scope);
if (batch.RelationshipAssertions.Length != index.ExpectedRelationshipCount || !batch.TerminologyAssertions.IsEmpty)
    throw new InvalidDataException("Only pinned hierarchy relationships may be added.");
var originalAcquisition = origin.Payload.AcquisitionReceipts.First();
var method = new AcquisitionMethodCoordinate("grid.gta-v.location-hierarchy.frozen-reference", "1", "grid.canonical-runtime.prepare", "1", adapterDigest);
var receipts = origin.Payload.AcquisitionReceipts.ToBuilder();
var bindings = origin.Payload.ArtifactAcquisitionBindings.ToBuilder();
foreach (var artifact in index.Artifacts)
{
    ImmutableArray<SourceAcquisitionMember> members = [new(artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, artifact.Id)];
    var id = SourceAcquisitionReceiptId.DeriveV1(SourceAcquisitionReceipt.CurrentSchemaVersion, scope.GameId,
        originalAcquisition.DistributionApplicationIdentity, originalAcquisition.DistributionBuildVersion,
        artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, method, members);
    receipts.Add(new(id, SourceAcquisitionReceipt.CurrentSchemaVersion, scope.GameId,
        originalAcquisition.DistributionApplicationIdentity, originalAcquisition.DistributionBuildVersion,
        artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest, method, members));
    bindings.Add(new(artifact.Id, id, artifact.SourceCoordinate, artifact.ExactBytes.Length, artifact.Digest));
}
var payload = batch.ApplyTo(origin.Payload, receipts.ToImmutable(), bindings.ToImmutable());
if (!payload.KnowledgeRecords.SequenceEqual(origin.Payload.KnowledgeRecords) ||
    origin.Payload.RelationshipAssertions.Any(r => !payload.RelationshipAssertions.Contains(r)) ||
    !payload.TerminologyAssertions.SequenceEqual(origin.Payload.TerminologyAssertions))
    throw new InvalidDataException("Historical identities or assertions changed.");
var preservation = VerifyHistoricalPayload(origin.Payload, payload);
var provenance = DevelopmentProvenance(repository);
Console.WriteLine("START candidate structural verification");
var candidate = CanonicalCatalogPackageKernel.CreateV6(origin.Manifest.PackageKind,
    new CatalogGameScope(scope.GameId, origin.Manifest.GameScope.ExactGameVersion, payload.Artifacts.Select(a => a.Id).ToImmutableArray()),
    origin.Manifest.ModScope, origin.Manifest.RequiredBasePackageIds, origin.Manifest.CompositionPolicyVersion, payload,
    new CatalogValidationSummary(CatalogValidationStatus.Candidate, "grid.location-hierarchy.development.structural", "2", ContentDigest.ComputeSha256("eleven-reference-edges:not-release-certified"u8)), provenance);
await using (var checkpoint = new FileStream(Path.Combine(output, "candidate-package.v6.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
    await JsonSerializer.SerializeAsync(checkpoint, candidate, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
Phase("derive-hierarchy-edges-and-verify-candidate");
var outputStore = Path.Combine(output, "shared-canonical-library.v5.json");
File.Copy(sourcePath, outputStore, false);
var store = new JsonCanonicalKnowledgeCatalogStore(outputStore);
Console.WriteLine("START isolated store import (existing store validation)");
var import = await store.ImportPackageAsync(loaded.Snapshot.Revision, candidate);
if (import.Status != CanonicalCatalogImportStatus.Imported) throw new InvalidDataException("Candidate import failed: " + import);
Phase("append-candidate-through-existing-store-import");
Console.WriteLine("START isolated updated store validation");
var validated = await store.LoadAsync();
if (!validated.IsValid || validated.Snapshot.FindImportedPackage(origin.Id) is null) throw new InvalidDataException("Updated store failed validation or lost historical package.");
var package = validated.Snapshot.FindImportedPackage(candidate.Id)!;
var outputDigest = await Digest(outputStore);
Phase("validate-isolated-updated-store");
var composition = new CatalogCompositionId("grid.runtime-catalog-composition.v1.sha256." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("grid.runtime-catalog-composition.v1\0" + package.Id.Value))).ToLowerInvariant());
var applicability = new CanonicalApplicabilityProjection(composition, package.Payload.KnowledgeRecords.Select(r => r.Id).OrderBy(r => r.Value, StringComparer.Ordinal).ToImmutableArray(), [], "grid.runtime-applicability.v1");
var inputs = Enum.GetValues<KnowledgeKind>().ToDictionary(k => k, k => CanonicalSelectorProjectionEngine.CreateVerifiedInput(validated, package.Id, composition,
    k == KnowledgeKind.Location ? CanonicalSelectorProjectionPolicy.LocationPrepared : CanonicalSelectorProjectionPolicy.GtaEnhanced, applicability));
var locale = new CanonicalTerminologyLocalePreference("en-US", []);
var descriptor = await PreparedCanonicalNavigationBuilder.BuildAsync(preparedRoot, validated, package.Id, outputDigest, inputs, locale);
Phase("prepare-authenticated-navigation");
if (await Digest(sourcePath) != sourceDigest) throw new InvalidDataException("Original shared store changed during preparation.");
var report = new { schemaVersion = 1, sourcePath, sourceDigest, originalPackageId = origin.Id, sourceRevision = loaded.Snapshot.Revision,
    outputStore, outputDigest, candidatePackageId = package.Id, catalogRevisionId = package.Manifest.CatalogRevisionId, storeRevision = validated.Snapshot.Revision,
    historicalRecordCount = origin.Payload.KnowledgeRecords.Length, addedRecords = 0, addedRelationships = index.ExpectedRelationshipCount,
    hierarchy = index.Resolve(origin.Payload).Select(r => new { parent = index.ParentName, child = r.ChildName, parentId = r.Parent.Id, childId = r.Child.Id }),
    preparedRoot, descriptor, phases, preservation, releaseCertified = false };
await File.WriteAllTextAsync(Path.Combine(output, "preparation-report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
Console.WriteLine("PREPARED " + package.Id.Value);

static async Task<string> Digest(string path) { await using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant(); }
static CatalogBuildProvenance DevelopmentProvenance(string root)
{
    string Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; var text = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException(error); return text;
    }
    string[] scope = ["Directory.Build.props", "Directory.Build.targets", "src/Grid.Core", "src/Grid.GtaV.Knowledge", "eng/catalog/Grid.CanonicalRuntime.Prepare", "scripts/games/grandtheftautov/catalog/gta_v_enhanced_location_hierarchy_sources.v2.json", "scripts/games/grandtheftautov/catalog/references"];
    var paths = Git(["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", .. scope]).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal).ToArray();
    var tracked = Git(["ls-files", "-z", "--", .. scope]).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var modified = Git(["diff", "--name-only", "-z", "HEAD", "--", .. scope]).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var inputs = paths.Select(path => new CatalogDevelopmentBuildInput(path, ContentDigest.ComputeSha256(File.ReadAllBytes(Path.Combine(root, path))),
        !tracked.Contains(path) ? CatalogDevelopmentBuildInputState.Untracked : modified.Contains(path) ? CatalogDevelopmentBuildInputState.TrackedWorktreeModified : CatalogDevelopmentBuildInputState.HeadTrackedClean)).ToImmutableArray();
    return CatalogBuildProvenance.CreateDevelopment("grid.canonical-runtime.prepare", "1", Git("rev-parse", "HEAD").Trim(), inputs);
}

static object[] VerifyHistoricalPayload(CanonicalCatalogPayload origin, CanonicalCatalogPayload updated)
{
    var results = new List<object>();
    foreach (var property in typeof(CanonicalCatalogPayload).GetProperties())
    {
        if (property.GetValue(origin) is not System.Collections.IEnumerable oldValues ||
            property.GetValue(updated) is not System.Collections.IEnumerable newValues) continue;
        var before = oldValues.Cast<object>().ToArray();
        var after = newValues.Cast<object>().ToArray();
        var retained = after.ToHashSet();
        foreach (var prior in before)
        {
            if (retained.Contains(prior)) continue;
            // Acquisition narrowing reconstructs immutable member arrays. Compare exact content,
            // not their allocation identity, and require the original content-addressed receipt.
            if (prior is SourceAcquisitionReceipt receipt)
            {
                var matching = after.OfType<SourceAcquisitionReceipt>().SingleOrDefault(x => x.Id == receipt.Id);
                if (matching is not null && JsonSerializer.Serialize(matching) == JsonSerializer.Serialize(receipt)) continue;
            }
            throw new InvalidDataException("Historical payload content changed: " + property.Name);
        }
        results.Add(new { collection = property.Name, before = before.Length, after = after.Length, historicalEntriesPreserved = true });
    }
    if (origin.EffectiveCoverage != updated.EffectiveCoverage) throw new InvalidDataException("Coverage truth changed.");
    return results.ToArray();
}
