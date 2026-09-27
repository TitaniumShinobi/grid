using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Grid.Core.Models;

internal static class GtaVAcquisitionAndGitProvenanceChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Check failed: " + message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "grid-gta-acquisition-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sourceFamilyManifest = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity();
            Assert(sourceFamilyManifest == (
                    "grid.gta-v-enhanced.source-families",
                    2,
                    "a477091b010d4a80958a77a35247a520cf98b8df4d8b8c27c76c979e8be03338"),
                "Python acquisition and the C# loader share one exact source-family manifest identity.");
            VerifySourceFamilyManifestTupleClosure(root);
            checks += 3;
            var fixture = CreateAcquisitionFixture(Path.Combine(root, "v2"), schemaVersion: 2);
            var valid = await GtaAcquisitionReceiptLoader.LoadAsync(fixture.ReceiptPath, fixture.GameRoot);
            Assert(valid.Receipts.Length == 4 && valid.Members.Count == 1102 && valid.Bindings.Length == 1102,
                "The exact synthetic acquisition closure validates all containers and members.");
            var legacy = CreateAcquisitionFixture(Path.Combine(root, "v1"), schemaVersion: 1);
            var legacyValid = await GtaAcquisitionReceiptLoader.LoadAsync(legacy.ReceiptPath, legacy.GameRoot);
            Assert(legacyValid.Receipts.Length == 4 && legacyValid.Members.Count == 1061,
                "A schema-v1 receipt remains readable with its original whole-manifest semantics.");
            var historicalV2 = CreateAcquisitionFixture(
                Path.Combine(root, "historical-v2"), schemaVersion: 2, bindSourceManifest: false);
            var historicalV2Valid = await GtaAcquisitionReceiptLoader.LoadAsync(
                historicalV2.ReceiptPath, historicalV2.GameRoot);
            Assert(historicalV2Valid.Receipts.Length == 4 && historicalV2Valid.Members.Count == 1061,
                "A historical schema-v2 receipt remains readable without reinterpretation as the current closure.");

            var manifestPath = fixture.ManifestPath;
            var originalManifest = await File.ReadAllBytesAsync(manifestPath);
            await File.WriteAllTextAsync(manifestPath, Manifest("999999999"), new UTF8Encoding(false));
            _ = await GtaAcquisitionReceiptLoader.LoadAsync(fixture.ReceiptPath, fixture.GameRoot);
            Assert(true, "Schema v2 tolerates volatile Steam manifest fields outside the bound stable coordinates.");
            await File.WriteAllBytesAsync(manifestPath, originalManifest);

            foreach (var invalidManifest in new[]
                     {
                         Manifest("100").Replace("\"3240220\"", "\"3240221\"", StringComparison.Ordinal),
                         Manifest("100").Replace("\"25261616\"", "\"25261617\"", StringComparison.Ordinal),
                         Manifest("100").Replace("Grand Theft Auto V Enhanced", "Wrong Install", StringComparison.Ordinal),
                         Manifest("100").Replace("\t\"buildid\"\t\t\"25261616\"\r\n", "", StringComparison.Ordinal),
                         Manifest("100") + "\t\"appid\"\t\t\"3240220\"\r\n",
                         Manifest("100").Replace("\"AppState\"", "\"WrongState\"", StringComparison.Ordinal),
                         Manifest("100").Replace("\"appid\"", "\"AppId\"", StringComparison.Ordinal),
                         NestedWrongSectionManifest(),
                     })
            {
                await File.WriteAllTextAsync(manifestPath, invalidManifest, new UTF8Encoding(false));
                await AssertThrowsAsync<InvalidDataException>(() =>
                    GtaAcquisitionReceiptLoader.LoadAsync(fixture.ReceiptPath, fixture.GameRoot));
                checks++;
            }
            await File.WriteAllBytesAsync(manifestPath, originalManifest);

            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["sha256"] = new string('0', 64));
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["members"]![0]!["memberCoordinate"] = "common.rpf!/wrong/member.meta");
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["members"]![0]!["sha256"] = new string('0', 64));
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["members"]![0]!["byteLength"] = 999999L);
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["acquisitionTool"]!["artifactSha256"] = new string('f', 64));
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["platformObservation"]!["buildIdFieldPath"] = "/Wrong/buildid");
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["sourceFamilyManifest"]!["documentSha256"] = new string('0', 64));
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["members"]!.AsArray().RemoveAt(0));
            checks++;
            await AssertRejectedAsync(fixture, document =>
                document["containers"]![0]!["members"]!.AsArray().Add(new JsonObject
                {
                    ["memberCoordinate"] = "common.rpf!/extra.bin",
                    ["memberPath"] = "extra.bin",
                    ["byteLength"] = 1,
                    ["sha256"] = new string('0', 64),
                }));
            checks++;
            await AssertRejectedAsync(fixture, document =>
            {
                var members = document["containers"]![0]!["members"]!.AsArray();
                members.Add(members[0]!.DeepClone());
            });
            checks++;
            await AssertRejectedAsync(fixture, document =>
            {
                var containers = document["containers"]!.AsArray();
                var first = containers[0]!.DeepClone();
                containers.RemoveAt(0);
                containers.Add(first);
            });
            checks++;

            var frozenPath = fixture.FirstFrozenMemberPath;
            var original = await File.ReadAllBytesAsync(frozenPath);
            await File.WriteAllBytesAsync(frozenPath, [.. original, (byte)0xff]);
            await AssertThrowsAsync<InvalidDataException>(() =>
                GtaAcquisitionReceiptLoader.LoadAsync(fixture.ReceiptPath, fixture.GameRoot));
            await File.WriteAllBytesAsync(frozenPath, original);
            checks++;

            Assert(typeof(GitBuildProvenanceResolver).GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .Where(value => value.Name == nameof(GitBuildProvenanceResolver.Resolve))
                    .All(value => value.GetParameters().Length == 0),
                "Git provenance accepts no caller-supplied commit identity.");
            VerifyGitClosure(Path.Combine(root, "git"), Assert);

            Console.WriteLine("PASS  GTA V acquisition receipt and committed-build provenance.");
            return checks;
        }
        finally
        {
            DeleteFixtureDirectory(root);
        }
    }

    private static async Task AssertRejectedAsync(
        AcquisitionFixture fixture,
        Action<JsonObject> mutate)
    {
        var original = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ReceiptPath))!.AsObject();
        var changed = original.DeepClone().AsObject();
        mutate(changed);
        WriteReceipt(fixture.ReceiptPath, changed);
        try
        {
            await AssertThrowsAsync<Exception>(() =>
                GtaAcquisitionReceiptLoader.LoadAsync(fixture.ReceiptPath, fixture.GameRoot));
        }
        finally
        {
            WriteReceipt(fixture.ReceiptPath, original);
        }
    }

    private static void VerifySourceFamilyManifestTupleClosure(string root)
    {
        Directory.CreateDirectory(root);
        var repositoryRoot = GitBuildProvenanceResolver.FindRepositoryRoot();
        var sourcePath = Path.Combine(
            repositoryRoot,
            "scripts",
            "games",
            "grandtheftautov",
            "catalog",
            "gta_v_enhanced_source_families.v2.json");
        var original = JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
        var cases = new[]
        {
            (Property: "sourceFamilyId", Value: "rockstar.gta-v.enhanced.mapzones"),
            (Property: "formatId", Value: "rockstar.gta-v.mapzones.cmapzonescontainer-xml"),
            (Property: "exactFormatVersion", Value: "999"),
        };
        for (var index = 0; index < cases.Length; index++)
        {
            var changed = original.DeepClone().AsObject();
            var firstMember = changed["containers"]![0]!["members"]![0]!.AsObject();
            firstMember[cases[index].Property] = cases[index].Value;
            var path = Path.Combine(root, $"source-family-manifest-tamper-{index}.json");
            File.WriteAllText(path, changed.ToJsonString(), new UTF8Encoding(false));
            try
            {
                _ = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity(path);
                throw new InvalidOperationException("Tampered source-family manifest tuple was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected: known-but-wrong family/format and nonempty wrong versions fail closed.
            }
        }
        var familyCases = new[]
        {
            (Property: "knowledgeKinds", Value: (JsonNode)new JsonArray("Actor")),
            (Property: "status", Value: (JsonNode)"indexed-unconsumed"),
        };
        for (var index = 0; index < familyCases.Length; index++)
        {
            var changed = original.DeepClone().AsObject();
            var family = changed["sourceFamilies"]!.AsArray()
                .Select(value => value!.AsObject())
                .Single(value => value["sourceFamilyId"]!.GetValue<string>() ==
                    "rockstar.gta-v.enhanced.weapons-meta");
            family[familyCases[index].Property] = familyCases[index].Value.DeepClone();
            if (familyCases[index].Property == "status") family["reasonCode"] = "tampered-policy";
            var path = Path.Combine(root, $"source-family-policy-tamper-{index}.json");
            File.WriteAllText(path, changed.ToJsonString(), new UTF8Encoding(false));
            try
            {
                _ = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity(path);
                throw new InvalidOperationException("Tampered source-family manifest policy was accepted.");
            }
            catch (InvalidDataException)
            {
                // Expected: v1 family roles and statuses are part of the exact approved registry.
            }
        }
    }

    private static AcquisitionFixture CreateAcquisitionFixture(
        string root,
        int schemaVersion,
        bool bindSourceManifest = true)
    {
        var steamApps = Path.Combine(root, "steamapps");
        var gameRoot = Path.Combine(steamApps, "common", "Grand Theft Auto V Enhanced");
        var acquisitionRoot = Path.Combine(root, "acquisition");
        Directory.CreateDirectory(gameRoot);
        Directory.CreateDirectory(acquisitionRoot);

        var manifestPath = Path.Combine(steamApps, "appmanifest_3240220.acf");
        var manifestBytes = Encoding.UTF8.GetBytes(Manifest("100"));
        File.WriteAllBytes(manifestPath, manifestBytes);

        var containers = new JsonArray();
        string? firstFrozen = null;
        var updateMembers = new List<string>
        {
            "common/data/ai/ambientpedmodelsets.meta",
            "common/data/gen9_exclusive_assets_peds.meta",
            "common/data/levels/gta5/popzone.ipl",
        };
        if (schemaVersion == 2 && bindSourceManifest)
            updateMembers.Add("x64/data/cdimages/scaleform_generic.rpf!/hud.gfx");
        updateMembers.Add("x64/patch/data/lang/american_rel.rpf");
        updateMembers.Add("x64/patch/data/lang/american_rel.rpf!/global.gxt2");

        foreach (var specification in new[]
                 {
                     (Coordinate: "common.rpf", Members: new[]
                     {
                         "data/ai/weapons.meta",
                         "data/levels/gta5/mapzones.xml",
                     }.Concat(schemaVersion == 2 && bindSourceManifest
                         ? Enumerable.Range(0, 40).Select(index => $"data/ugc/fixture-{index:D4}.ugc")
                         : []).OrderBy(value => value, StringComparer.Ordinal).ToArray()),
                     (Coordinate: "update/update.rpf", Members: updateMembers.ToArray()),
                     (Coordinate: "update/update2.rpf", Members: Enumerable.Range(0, 1052)
                         .Select(index => $"common/data/ugc/fixture-{index:D4}.ugc")
                         .ToArray()),
                     (Coordinate: "x64b.rpf", Members: new[]
                     {
                         "data/lang/american_rel.rpf",
                         "data/lang/american_rel.rpf!/global.gxt2",
                     }),
                 })
        {
            var containerBytes = Encoding.UTF8.GetBytes("container:" + specification.Coordinate);
            var containerPath = Path.Combine(gameRoot, specification.Coordinate.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(containerPath)!);
            File.WriteAllBytes(containerPath, containerBytes);
            var members = new JsonArray();
            foreach (var memberPath in specification.Members)
            {
                var exactBytes = Encoding.UTF8.GetBytes(specification.Coordinate + "!/" + memberPath);
                var frozenPath = Path.Combine(
                    acquisitionRoot,
                    "members",
                    specification.Coordinate.Replace('/', Path.DirectorySeparatorChar),
                    memberPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(frozenPath)!);
                File.WriteAllBytes(frozenPath, exactBytes);
                firstFrozen ??= frozenPath;
                members.Add(new JsonObject
                {
                    ["memberCoordinate"] = specification.Coordinate + "!/" + memberPath,
                    ["memberPath"] = memberPath,
                    ["byteLength"] = exactBytes.LongLength,
                    ["sha256"] = Sha256(exactBytes),
                });
            }
            containers.Add(new JsonObject
            {
                ["containerCoordinate"] = specification.Coordinate,
                ["byteLength"] = containerBytes.LongLength,
                ["sha256"] = Sha256(containerBytes),
                ["members"] = members,
            });
        }

        var receipt = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["gameId"] = "game.grandtheftautov-enhanced",
            ["gameVersionNamespace"] = "valve.steam.app.3240220.build-id",
            ["gameVersion"] = "25261616",
            ["platformObservation"] = new JsonObject
            {
                ["provider"] = "valve.steam",
                ["appId"] = "3240220",
                ["buildId"] = "25261616",
                ["manifestCoordinate"] = "steamapps/appmanifest_3240220.acf",
                ["manifestByteLength"] = manifestBytes.LongLength,
                ["manifestSha256"] = Sha256(manifestBytes),
                ["appIdFieldPath"] = "/AppState/appid",
                ["buildIdFieldPath"] = "/AppState/buildid",
                ["installDirFieldPath"] = "/AppState/installdir",
            },
            ["observedAtUtc"] = "2026-09-25T01:24:20.699830Z",
            ["acquisitionTool"] = new JsonObject
            {
                ["toolId"] = "fivefury.rpf-read",
                ["exactVersion"] = "0.5.1",
                ["artifactSha256"] = "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c",
                ["sourceRevision"] = "75bd2b8b99838bf8ec49afa2a82ba03f557cd584",
                ["methodId"] = "grid.gta-v-enhanced.rpf-member-acquisition",
                ["methodVersion"] = schemaVersion == 1 ? "2" : "4",
                ["receiptSchemaVersion"] = schemaVersion,
            },
            ["containers"] = containers,
        };
        receipt["schemaVersion"] = schemaVersion;
        if (schemaVersion == 2)
        {
            receipt["platformObservation"]!["installDir"] = "Grand Theft Auto V Enhanced";
            if (bindSourceManifest)
            {
                var sourceManifest = GtaAcquisitionReceiptLoader.SourceFamilyManifestIdentity();
                receipt["sourceFamilyManifest"] = new JsonObject
                {
                    ["manifestId"] = sourceManifest.ManifestId,
                    ["schemaVersion"] = sourceManifest.SchemaVersion,
                    ["documentSha256"] = sourceManifest.DocumentSha256,
                };
            }
        }
        var receiptPath = Path.Combine(acquisitionRoot, $"gta-v-enhanced-acquisition-receipt.v{schemaVersion}.json");
        WriteReceipt(receiptPath, receipt);
        return new AcquisitionFixture(gameRoot, manifestPath, receiptPath, firstFrozen!);
    }

    private static string Manifest(string lastPlayed) =>
        "\"AppState\"\r\n{\r\n" +
        "\t\"appid\"\t\t\"3240220\"\r\n" +
        "\t\"buildid\"\t\t\"25261616\"\r\n" +
        "\t\"installdir\"\t\t\"Grand Theft Auto V Enhanced\"\r\n" +
        $"\t\"LastPlayed\"\t\t\"{lastPlayed}\"\r\n" +
        "}\r\n";

    private static string NestedWrongSectionManifest() =>
        "\"AppState\"\r\n{\r\n" +
        "\t\"WrongState\"\r\n\t{\r\n" +
        "\t\t\"appid\"\t\t\"3240220\"\r\n" +
        "\t\t\"buildid\"\t\t\"25261616\"\r\n" +
        "\t\t\"installdir\"\t\t\"Grand Theft Auto V Enhanced\"\r\n" +
        "\t}\r\n}\r\n";

    private static void VerifyGitClosure(string root, Action<bool, string> assert)
    {
        Directory.CreateDirectory(root);
        foreach (var path in new[]
                 {
                     "Directory.Build.props",
                     "Grid.sln",
                     "src/Grid.Core/input.cs",
                     "src/Grid.GtaV.Enrichment.Knowledge/input.cs",
                     "src/Grid.GtaV.Knowledge/input.cs",
                     "eng/catalog/Grid.GtaVEnhanced.Canary/input.cs",
                     "scripts/games/grandtheftautov/catalog/gta_v_enhanced_acquire.py",
                     "scripts/games/grandtheftautov/catalog/fivefury.lock.v1.json",
                 })
        {
            var absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, path, new UTF8Encoding(false, true));
        }
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "grid-tests@example.invalid");
        RunGit(root, "config", "user.name", "GRID Tests");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "fixture");

        var previous = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = root;
            var clean = GitBuildProvenanceResolver.Resolve();
            assert(clean.Commit.Length == 40 && clean.Inputs.Length == 8,
                "A clean committed implementation closure derives its HEAD and blob identities.");
            File.WriteAllText(Path.Combine(root, "src", "Grid.Core", "untracked.cs"), "untracked");
            var rejected = false;
            try { _ = GitBuildProvenanceResolver.Resolve(); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "An untracked relevant implementation input fails committed-build provenance.");
            var untrackedCandidate = GitBuildProvenanceResolver.ResolveCandidate();
            assert(untrackedCandidate.Provenance.IsDevelopment &&
                   untrackedCandidate.Provenance.DevelopmentBuildInputs.Any(value =>
                       value.RepositoryRelativePath == "src/Grid.Core/untracked.cs" &&
                       value.State == CatalogDevelopmentBuildInputState.Untracked &&
                       value.ExactContentDigest == ContentDigest.ComputeSha256("untracked"u8)),
                "A Candidate-only development closure binds exact untracked bytes and state without claiming a commit contains them.");
            File.Delete(Path.Combine(root, "src", "Grid.Core", "untracked.cs"));
            File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "staged-different");
            RunGit(root, "add", "Directory.Build.props");
            rejected = false;
            try { _ = GitBuildProvenanceResolver.Resolve(); }
            catch (InvalidDataException) { rejected = true; }
            assert(rejected, "A staged relevant implementation input that differs from HEAD fails provenance.");
            var stagedCandidate = GitBuildProvenanceResolver.ResolveCandidate();
            assert(stagedCandidate.Provenance.IsDevelopment &&
                   stagedCandidate.Provenance.DevelopmentBuildInputs.Any(value =>
                       value.RepositoryRelativePath == "Directory.Build.props" &&
                       value.State == CatalogDevelopmentBuildInputState.TrackedIndexModified &&
                       value.ExactContentDigest == ContentDigest.ComputeSha256("staged-different"u8)),
                "A Candidate-only development closure binds exact staged bytes and state.");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    private static void RunGit(string root, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Git fixture command failed: {error} {output}");
    }

    private static void WriteReceipt(string path, JsonObject document)
    {
        document.Remove("receiptDocumentSha256");
        document["receiptDocumentSha256"] = Sha256(CanonicalBytes(document, skipDigest: true));
        File.WriteAllBytes(path, [.. CanonicalBytes(document), (byte)'\n']);
    }

    private static byte[] CanonicalBytes(JsonNode node, bool skipDigest = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, node, skipDigest);
        return stream.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonNode node, bool skipDigest)
    {
        if (node is JsonObject valueObject)
        {
            writer.WriteStartObject();
            foreach (var property in valueObject.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                if (skipDigest && property.Key == "receiptDocumentSha256") continue;
                writer.WritePropertyName(property.Key);
                WriteCanonical(writer, property.Value!, skipDigest);
            }
            writer.WriteEndObject();
        }
        else if (node is JsonArray valueArray)
        {
            writer.WriteStartArray();
            foreach (var item in valueArray) WriteCanonical(writer, item!, skipDigest);
            writer.WriteEndArray();
        }
        else
        {
            node.WriteTo(writer);
        }
    }

    private static string Sha256(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static void DeleteFixtureDirectory(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(path, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed record AcquisitionFixture(
        string GameRoot,
        string ManifestPath,
        string ReceiptPath,
        string FirstFrozenMemberPath);
}
