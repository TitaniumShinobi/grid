using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2RepairArchiveCheckResult(string Name, Exception? Failure);

static class Mo2RepairArchiveChecks
{
    public static async Task<ImmutableArray<Mo2RepairArchiveCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2RepairArchiveCheckResult>();
        await RunAsync(results, "archive inspection hashes members and extracts only to empty staging", FullExtractionAsync);
        await RunAsync(results, "archive inspection rejects traversal and ADS entries", UnsafePathsAsync);
        await RunAsync(results, "archive inspection rejects case and Unicode path collisions", PathCollisionsAsync);
        await RunAsync(results, "archive inspection enforces entry, depth, and compression bounds", ResourceLimitsAsync);
        await RunAsync(results, "archive inspection rejects truncated and digest-mismatched artifacts", IntegrityAsync);
        await RunAsync(results, "FOMOD selection deterministically maps only chosen files", FomodSelectionAsync);
        await RunAsync(results, "FOMOD duplicate group labels are qualified by install step", DuplicateFomodGroupNamesAsync);
        await RunAsync(results, "installed file hashes reconstruct exact FOMOD choices including an empty SelectAny group", FomodReconciliationAsync);
        await RunAsync(results, "single release-folder wrapper preserves FOMOD semantics", WrappedFomodSelectionAsync);
        await RunAsync(results, "FOMOD conditional logic fails closed", UnsupportedFomodAsync);
        await RunAsync(results, "staging refuses preexisting content without overwriting it", ExistingStagingAsync);
        await RunAsync(results, "archive requirements match only unambiguous Data-relative paths", RequirementMatchingAsync);
        await RunAsync(results, "archive candidate assessment separates restoration from update compatibility", CandidateAssessmentAsync);
        return results.ToImmutable();
    }

    private static async Task FullExtractionAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(("textures/a.dds", "alpha"), ("meshes/a.nif", "mesh"));
        var staging = fixture.Path("stage");
        var result = await Service().InspectAsync(Request(archive, Mo2ArchiveExtractionMode.FullArchive, staging));

        Equal(Mo2ArchiveInspectionStatus.Extracted, result.Status);
        Equal(2, result.Entries.Count(entry => !entry.IsDirectory));
        True(result.Entries.Where(entry => !entry.IsDirectory).All(entry => entry.Sha256?.Length == 64));
        Equal("alpha", await File.ReadAllTextAsync(Path.Combine(staging, "textures", "a.dds")));
        Equal("mesh", await File.ReadAllTextAsync(Path.Combine(staging, "meshes", "a.nif")));
        Equal(2, result.InstallMappings.Length);
    }

    private static async Task UnsafePathsAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(("../escape.txt", "escape"), ("safe/file.txt:stream", "ads"));
        var result = await Service().InspectAsync(Request(archive));

        Equal(Mo2ArchiveInspectionStatus.Rejected, result.Status);
        Contains(result.Issues, issue => issue.Code == "mo2.repair.archive.unsafe_path" &&
            issue.EntryPath == "../escape.txt");
        Contains(result.Issues, issue => issue.Code == "mo2.repair.archive.unsafe_path" &&
            issue.EntryPath == "safe/file.txt:stream");
        False(File.Exists(fixture.Path("escape.txt")));
    }

    private static async Task PathCollisionsAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(
            ("Data/FILE.txt", "one"),
            ("data/file.txt", "two"),
            ("unicode/caf\u00e9.txt", "three"),
            ("unicode/cafe\u0301.txt", "four"));
        var result = await Service().InspectAsync(Request(archive));

        Equal(Mo2ArchiveInspectionStatus.Rejected, result.Status);
        Equal(2, result.Issues.Count(issue => issue.Code == "mo2.repair.archive.path_collision"));
    }

    private static async Task ResourceLimitsAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(
            ("a/b/c/file.txt", "depth"),
            ("bomb.bin", new string('x', 128 * 1024)),
            ("third.txt", "entry"));
        var limits = new Mo2ArchiveInspectionLimits(
            MaximumEntries: 2,
            MaximumDepth: 2,
            MaximumCompressionRatio: 2,
            MaximumExpandedBytes: 1024 * 1024);
        var result = await Service().InspectAsync(Request(archive) with { Limits = limits });

        Equal(Mo2ArchiveInspectionStatus.Rejected, result.Status);
        Contains(result.Issues, issue => issue.Code == "mo2.repair.archive.unsafe_path");
        Contains(result.Issues, issue => issue.Code == "mo2.repair.archive.compression_ratio_limit");
        Contains(result.Issues, issue => issue.Code == "mo2.repair.archive.entry_limit");
    }

    private static async Task IntegrityAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(("file.txt", "content"));
        var mismatch = await Service().InspectAsync(Request(archive) with
        {
            ExpectedSha256 = new string('0', 64),
        });
        Equal(Mo2ArchiveInspectionStatus.Rejected, mismatch.Status);
        Contains(mismatch.Issues, issue => issue.Code == "mo2.repair.archive.digest_mismatch");

        var truncated = fixture.Path("truncated.zip");
        var bytes = await File.ReadAllBytesAsync(archive);
        await File.WriteAllBytesAsync(truncated, bytes[..(bytes.Length / 2)]);
        var invalid = await Service().InspectAsync(Request(truncated));
        Equal(Mo2ArchiveInspectionStatus.Rejected, invalid.Status);
        Contains(invalid.Issues, issue => issue.Code == "mo2.repair.archive.integrity_failure");
    }

    private static async Task FomodSelectionAsync()
    {
        using var fixture = new ArchiveFixture();
        const string info = """
            <fomod><Name>Fixture</Name><Author>Grid</Author><Version>1.2.3</Version></fomod>
            """;
        const string module = """
            <config>
              <requiredInstallFiles><file source="common/readme.txt" destination="docs/readme.txt" priority="1" /></requiredInstallFiles>
              <installSteps><installStep><optionalFileGroups><group name="Texture" type="SelectExactlyOne"><plugins>
                <plugin name="Blue"><files><folder source="options/blue" destination="textures" priority="5" /></files></plugin>
                <plugin name="Red"><files><folder source="options/red" destination="textures" priority="5" /></files></plugin>
              </plugins></group></optionalFileGroups></installStep></installSteps>
            </config>
            """;
        var archive = fixture.CreateZip(
            ("fomod/info.xml", info),
            ("fomod/ModuleConfig.xml", module),
            ("common/readme.txt", "readme"),
            ("options/blue/color.dds", "blue"),
            ("options/red/color.dds", "red"));
        var staging = fixture.Path("fomod-stage");
        var request = Request(archive, Mo2ArchiveExtractionMode.FomodSelection, staging) with
        {
            FomodSelections = [new("Texture", ["Blue"])],
        };
        var first = await Service().InspectAsync(request);

        Equal(Mo2ArchiveInspectionStatus.Extracted, first.Status);
        Equal(Mo2FomodStatus.Valid, first.Fomod.Status);
        Equal("Fixture", first.Fomod.Name!);
        Equal("blue", await File.ReadAllTextAsync(Path.Combine(staging, "textures", "color.dds")));
        Equal("readme", await File.ReadAllTextAsync(Path.Combine(staging, "docs", "readme.txt")));
        False(File.Exists(Path.Combine(staging, "options", "red", "color.dds")));
        Equal("Texture", first.Fomod.SelectionVector.Single().GroupName);
        Equal("Blue", first.Fomod.SelectionVector.Single().PluginNames.Single());

        var second = await Service().InspectAsync(request with { StagingDirectory = fixture.Path("fomod-stage-2") });
        Equal(
            string.Join('|', first.InstallMappings.Select(mapping => $"{mapping.SourcePath}>{mapping.DestinationPath}")),
            string.Join('|', second.InstallMappings.Select(mapping => $"{mapping.SourcePath}>{mapping.DestinationPath}")));
        Equal(Mo2ArchiveInspectionStatus.Extracted, second.Status);
    }

    private static async Task UnsupportedFomodAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(
            ("fomod/ModuleConfig.xml", "<config><conditionalFileInstalls /></config>"),
            ("file.txt", "content"));
        var result = await Service().InspectAsync(Request(archive));

        Equal(Mo2ArchiveInspectionStatus.Rejected, result.Status);
        Equal(Mo2FomodStatus.Unsupported, result.Fomod.Status);
        Contains(result.Issues, issue => issue.Code == "mo2.repair.fomod.logic_unsupported");
    }

    private static async Task DuplicateFomodGroupNamesAsync()
    {
        using var fixture = new ArchiveFixture();
        const string module = """
            <config><installSteps>
              <installStep name="Texture Resolution"><optionalFileGroups>
                <group name="Options" type="SelectExactlyOne"><plugins>
                  <plugin name="2K"><files><folder source="2k" destination="textures" /></files></plugin>
                  <plugin name="4K"><files><folder source="4k" destination="textures" /></files></plugin>
                </plugins></group>
              </optionalFileGroups></installStep>
              <installStep name="Optionals"><optionalFileGroups>
                <group name="Options" type="SelectAny"><plugins>
                  <plugin name="No flame"><files><folder source="noflame" destination="textures" /></files></plugin>
                </plugins></group>
              </optionalFileGroups></installStep>
            </installSteps></config>
            """;
        var archive = fixture.CreateZip(
            ("fomod/ModuleConfig.xml", module),
            ("2k/base.dds", "2k"),
            ("4k/base.dds", "4k"),
            ("noflame/effect.dds", "off"));
        var staging = fixture.Path("duplicate-group-stage");
        var result = await Service().InspectAsync(Request(
            archive, Mo2ArchiveExtractionMode.FomodSelection, staging) with
        {
            FomodSelections =
            [
                new("Texture Resolution / Options", ["2K"]),
                new("Optionals / Options", ["No flame"]),
            ],
        });

        Equal(Mo2ArchiveInspectionStatus.Extracted, result.Status);
        Equal(Mo2FomodStatus.Valid, result.Fomod.Status);
        True(result.Fomod.Groups.Any(group => group.Name == "Texture Resolution / Options"));
        True(result.Fomod.Groups.Any(group => group.Name == "Optionals / Options"));
        Equal("2k", await File.ReadAllTextAsync(Path.Combine(staging, "textures", "base.dds")));
        Equal("off", await File.ReadAllTextAsync(Path.Combine(staging, "textures", "effect.dds")));
    }

    private static async Task FomodReconciliationAsync()
    {
        using var fixture = new ArchiveFixture();
        const string module = """
            <config><installSteps>
              <installStep name="Texture Resolution"><optionalFileGroups>
                <group name="Options" type="SelectExactlyOne"><plugins>
                  <plugin name="2K Resolution"><files><folder source="2k" destination="textures" /></files></plugin>
                  <plugin name="4K Resolution"><files><folder source="4k" destination="textures" /></files></plugin>
                </plugins></group>
              </optionalFileGroups></installStep>
              <installStep name="Optionals"><optionalFileGroups>
                <group name="Options" type="SelectAny"><plugins>
                  <plugin name="No flame effect"><files><file source="noflame/effect.dds" destination="textures/effect.dds" /></files></plugin>
                </plugins></group>
              </optionalFileGroups></installStep>
            </installSteps></config>
            """;
        var archive = fixture.CreateZip(
            ("fomod/ModuleConfig.xml", module),
            ("2k/base.dds", "2k"),
            ("4k/base.dds", "4k"),
            ("noflame/effect.dds", "off"));
        var inspection = await Service().InspectAsync(Request(archive));
        Equal(Mo2ArchiveInspectionStatus.Rejected, inspection.Status);
        Equal(Mo2FomodStatus.SelectionRequired, inspection.Fomod.Status);

        var result = new Mo2FomodSelectionReconciliationService().Reconcile(
            inspection,
            [new("textures/base.dds", "Tournament", Hash("4k"))]);

        Equal("Complete", result.Status);
        Equal(2, result.SelectionVector.Length);
        Equal("4K Resolution", result.SelectionVector.Single(selection =>
            selection.GroupName == "Texture Resolution / Options").PluginNames.Single());
        Equal(0, result.SelectionVector.Single(selection =>
            selection.GroupName == "Optionals / Options").PluginNames.Length);
        Equal(Mo2FomodOptionReconciliationStatus.NotSelected,
            result.Groups.Single(group => group.Name == "Optionals / Options").Options.Single().Status);

        var changed = new Mo2FomodSelectionReconciliationService().Reconcile(
            inspection,
            [
                new("textures/base.dds", "Tournament", Hash("4k")),
                new("textures/effect.dds", "Tournament", Hash("locally-modified")),
            ]);
        Equal("Unresolved", changed.Status);
        Equal(Mo2FomodOptionReconciliationStatus.Ambiguous,
            changed.Groups.Single(group => group.Name == "Optionals / Options").Options.Single().Status);
    }

    private static async Task WrappedFomodSelectionAsync()
    {
        using var fixture = new ArchiveFixture();
        const string info = "<fomod><Name>Wrapped Fixture</Name><Version>6.2</Version></fomod>";
        const string module = """
            <config><requiredInstallFiles><folder source="00 Core" destination="" priority="1" /></requiredInstallFiles></config>
            """;
        var archive = fixture.CreateZip(
            ("Release Folder/fomod/info.xml", info),
            ("Release Folder/fomod/ModuleConfig.xml", module),
            ("Release Folder/00 Core/meshes/fixture.nif", "mesh"));
        var staging = fixture.Path("wrapped-stage");
        var result = await Service().InspectAsync(Request(
            archive, Mo2ArchiveExtractionMode.FomodSelection, staging));

        Equal(Mo2ArchiveInspectionStatus.Extracted, result.Status);
        Equal(Mo2FomodStatus.Valid, result.Fomod.Status);
        Equal("Wrapped Fixture", result.Fomod.Name!);
        Equal("mesh", await File.ReadAllTextAsync(Path.Combine(staging, "meshes", "fixture.nif")));
        True(result.Entries.Any(entry => entry.NormalizedPath == "00 Core\\meshes\\fixture.nif"));
        False(result.Entries.Any(entry => entry.NormalizedPath.StartsWith("Release Folder", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task ExistingStagingAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(("file.txt", "new"));
        var staging = fixture.Path("existing-stage");
        Directory.CreateDirectory(staging);
        var protectedPath = Path.Combine(staging, "keep.txt");
        await File.WriteAllTextAsync(protectedPath, "keep");
        var result = await Service().InspectAsync(Request(
            archive, Mo2ArchiveExtractionMode.FullArchive, staging));

        Equal(Mo2ArchiveInspectionStatus.Rejected, result.Status);
        Contains(result.Issues, issue => issue.Code == "mo2.repair.staging.not_empty");
        Equal("keep", await File.ReadAllTextAsync(protectedPath));
        False(File.Exists(Path.Combine(staging, "file.txt")));
    }

    private static async Task RequirementMatchingAsync()
    {
        using var fixture = new ArchiveFixture();
        var archive = fixture.CreateZip(
            ("scripts/direct.pex", "direct"),
            ("Data/scripts/data.pex", "data"),
            ("option/scripts/guessed.pex", "guess"));
        var inspection = await Service().InspectAsync(Request(archive));
        var result = new Mo2ArchiveRequirementMatcher().Match(inspection,
        [
            new("Fixture.esp", "scripts\\direct.pex"),
            new("Fixture.esp", "scripts\\data.pex"),
            new("Fixture.esp", "scripts\\guessed.pex"),
        ]);

        Equal("Complete", result.Status);
        Equal(3, result.RequiredFileCount);
        Equal(2, result.MatchedRequiredFileCount);
        Equal(0, result.AmbiguousRequiredFileCount);
        True(result.Matches.Any(value => value.RequiredVirtualPath == "scripts\\direct.pex" && value.MappingRule == "ArchiveRoot"));
        True(result.Matches.Any(value => value.RequiredVirtualPath == "scripts\\data.pex" && value.MappingRule == "ExplicitDataDirectory"));
        False(result.Matches.Any(value => value.RequiredVirtualPath == "scripts\\guessed.pex"));
        True(result.EntrySetSha256?.Length == 64);
        True(result.EvidenceId?.StartsWith("archive-content.", StringComparison.Ordinal) == true);
    }

    private static async Task CandidateAssessmentAsync()
    {
        using var fixture = new ArchiveFixture();
        var exactArchive = fixture.CreateZip(
            ("main/MysticismMagic.esp", "installed-primary"),
            ("main/MysticismMagic.bsa", "restored-assets"));
        var exactInspection = await Service().InspectAsync(Request(exactArchive));
        var installedPrimary = Hash("installed-primary");
        var assessmentService = new Mo2ArchiveCandidateAssessmentService();
        var exact = assessmentService.Assess(new(
            exactInspection,
            "MysticismMagic.esp",
            "main/MysticismMagic.esp",
            "main/MysticismMagic.bsa",
            installedPrimary,
            "2.4.2",
            "2.4.2",
            [new("UltimateCollege_Mysticism_Patch.esp", Hash("college-patch"), null)]));

        Equal(Mo2ArchiveCandidateRole.ExactRestoration, exact.CandidateRole);
        Equal(Mo2ArchivePayloadPairStatus.Complete, exact.PayloadPairStatus);
        Equal(Mo2ArchiveMixingPolicy.InstalledPluginCompatibleWithCandidateAssets, exact.MixingPolicy);
        Equal(Mo2ArchiveCandidateCompatibilityStatus.NotRequiredForExactRestoration, exact.CompatibilityStatus);
        Equal(0, exact.RequiredEvidence.Length);
        True(exact.EvidenceId.StartsWith("archive-candidate.", StringComparison.Ordinal));

        var autoDiscovered = assessmentService.Assess(new(
            exactInspection,
            "MysticismMagic.esp",
            null,
            null,
            installedPrimary,
            "2.4.2",
            "2.4.2",
            []));
        Equal(Mo2ArchiveCandidateRole.ExactRestoration, autoDiscovered.CandidateRole);
        Equal("main\\MysticismMagic.esp", autoDiscovered.CandidatePrimaryPluginEntryPath ?? string.Empty);
        Equal("main\\MysticismMagic.bsa", autoDiscovered.CandidatePrimaryArchiveEntryPath ?? string.Empty);

        var updateArchive = fixture.CreateZip(
            ("main/MysticismMagic.esp", "updated-primary"),
            ("main/MysticismMagic.bsa", "updated-assets"),
            ("module jump/MysticismJumpSpells.esp", "current-jump"),
            ("module ordinator/MysticOrdinator.esp", "updated-ordinator"));
        var updateInspection = await Service().InspectAsync(Request(updateArchive));
        var update = assessmentService.Assess(new(
            updateInspection,
            "MysticismMagic.esp",
            "main/MysticismMagic.esp",
            "main/MysticismMagic.bsa",
            installedPrimary,
            "2.4.2",
            "2.5.0",
            [
                new("MysticismJumpSpells.esp", Hash("current-jump"), "module jump/MysticismJumpSpells.esp"),
                new("MysticOrdinator.esp", Hash("old-ordinator"), "module ordinator/MysticOrdinator.esp"),
                new("UltimateCollege_Mysticism_Patch.esp", Hash("college-patch"), null),
            ]));

        Equal(Mo2ArchiveCandidateRole.CompleteUpdateCandidate, update.CandidateRole);
        Equal(Mo2ArchiveVersionRelation.Newer, update.VersionRelation);
        Equal(Mo2ArchiveMixingPolicy.CandidatePluginAndAssetsRequiredTogether, update.MixingPolicy);
        Equal(Mo2ArchiveCandidateCompatibilityStatus.RequiresDependentCompatibilityEvidence, update.CompatibilityStatus);
        Equal(Mo2BundledPluginDisposition.AlreadyMatchesCandidate,
            update.DependentPlugins.Single(value => value.Name == "MysticismJumpSpells.esp").Disposition);
        Equal(Mo2BundledPluginDisposition.ReplacementRequired,
            update.DependentPlugins.Single(value => value.Name == "MysticOrdinator.esp").Disposition);
        Equal(Mo2BundledPluginDisposition.NotBundled,
            update.DependentPlugins.Single(value => value.Name == "UltimateCollege_Mysticism_Patch.esp").Disposition);
        True(update.RequiredEvidence.Contains("DependentPluginCompatibilityMatrix"));
        True(update.RequiredEvidence.Contains("BundledPluginWinnerReplacement"));
        True(update.RequiredEvidence.Contains("UnbundledDependentPluginCompatibility"));

        const string selectionModule = """
            <config><installSteps><installStep><optionalFileGroups>
              <group name="Main File" type="SelectExactlyOne"><plugins>
                <plugin name="Install"><files><folder source="main" destination="" /></files></plugin>
              </plugins></group>
            </optionalFileGroups></installStep></installSteps></config>
            """;
        var selectionArchive = fixture.CreateZip(
            ("fomod/ModuleConfig.xml", selectionModule),
            ("main/MysticismMagic.esp", "updated-primary"),
            ("main/MysticismMagic.bsa", "updated-assets"));
        var selectionInspection = await Service().InspectAsync(Request(selectionArchive));
        Equal(Mo2ArchiveInspectionStatus.Rejected, selectionInspection.Status);
        Equal(Mo2FomodStatus.SelectionRequired, selectionInspection.Fomod.Status);
        var variantArchive = fixture.CreateZip(
            ("fomod/ModuleConfig.xml", selectionModule),
            ("core/MysticismMagic.esp", "updated-primary"),
            ("assets-2k/MysticismMagic.bsa", "2k-assets"),
            ("assets-4k/MysticismMagic.bsa", "4k-assets"));
        var variantInspection = await Service().InspectAsync(Request(variantArchive));
        var reconciledVariant = assessmentService.Assess(new(
            variantInspection,
            "MysticismMagic.esp",
            "core/MysticismMagic.esp",
            null,
            installedPrimary,
            "2.4.2",
            "2.5.0",
            [],
            Hash("4k-assets")));
        Equal("assets-4k\\MysticismMagic.bsa", reconciledVariant.CandidatePrimaryArchiveEntryPath ?? string.Empty);
        Equal(Hash("4k-assets"), reconciledVariant.InstalledPrimaryArchiveSha256 ?? string.Empty);
        var selectionAssessment = assessmentService.Assess(new(
            selectionInspection,
            "MysticismMagic.esp",
            "main/MysticismMagic.esp",
            "main/MysticismMagic.bsa",
            installedPrimary,
            "2.4.2",
            "2.5.0",
            []));
        Equal("SelectionRequired", selectionAssessment.Status);
        Equal(Mo2ArchiveCandidateRole.CompleteUpdateCandidate, selectionAssessment.CandidateRole);
        True(selectionAssessment.RequiredEvidence.Contains("FomodSelectionVector"));

        var incompleteArchive = fixture.CreateZip(("main/MysticismMagic.esp", "updated-primary"));
        var incompleteInspection = await Service().InspectAsync(Request(incompleteArchive));
        var incomplete = assessmentService.Assess(new(
            incompleteInspection,
            "MysticismMagic.esp",
            "main/MysticismMagic.esp",
            "main/MysticismMagic.bsa",
            installedPrimary,
            "2.4.2",
            "2.5.0",
            []));
        Equal(Mo2ArchiveCandidateRole.IncompleteCandidate, incomplete.CandidateRole);
        Equal(Mo2ArchiveMixingPolicy.CandidateUnusable, incomplete.MixingPolicy);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Mo2RepairArchiveService Service() => new();

    private static Mo2ArchiveInspectionRequest Request(
        string archive,
        Mo2ArchiveExtractionMode mode = Mo2ArchiveExtractionMode.None,
        string? staging = null) =>
        new(archive, null, mode, staging, [], new());

    private static async Task RunAsync(
        ImmutableArray<Mo2RepairArchiveCheckResult>.Builder results,
        string name,
        Func<Task> action)
    {
        try
        {
            await action();
            results.Add(new(name, null));
        }
        catch (Exception exception)
        {
            results.Add(new(name, exception));
        }
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }

    private static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate)) throw new InvalidOperationException("Expected a matching item.");
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "Grid.Mo2.Archive.Tests", Guid.NewGuid().ToString("N"));

        public ArchiveFixture() => Directory.CreateDirectory(root);

        public string Path(string relative) => System.IO.Path.Combine(root, relative);

        public string CreateZip(params (string Path, string Content)[] entries)
        {
            var path = Path($"fixture-{Guid.NewGuid():N}.zip");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
            foreach (var item in entries)
            {
                var entry = archive.CreateEntry(item.Path, CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(item.Content);
            }
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
