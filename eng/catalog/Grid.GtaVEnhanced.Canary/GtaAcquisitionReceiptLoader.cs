using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.GtaV.Knowledge;

internal sealed record ValidatedGtaAcquisition(
    SourceNativeVersion GameVersion,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<SourceAcquisitionReceipt> Receipts,
    ImmutableArray<SourceArtifactAcquisitionBinding> Bindings,
    ImmutableDictionary<string, FrozenMember> Members,
    ImmutableArray<SourceArtifactId> ContainerFingerprints);

internal sealed record FrozenMember(
    string Coordinate,
    ImmutableArray<byte> Bytes,
    ContentDigest Digest,
    SourceArtifactId ArtifactId,
    long ByteLength);

internal static class GtaAcquisitionReceiptLoader
{
    private const string SourceFamilyManifestRelativePath =
        "scripts/games/grandtheftautov/catalog/gta_v_enhanced_source_families.v2.json";
    private const string ExpectedSourceFamilyManifestSha256 =
        "a477091b010d4a80958a77a35247a520cf98b8df4d8b8c27c76c979e8be03338";
    private const string LegacySourceFamilyManifestSha256 =
        "426e2559aa88b79420d65508db0c8955795805dca6c37b90993bc88b62d81be4";
    private const string ExpectedGameId = "game.grandtheftautov-enhanced";
    private const string ExpectedAppId = "3240220";
    private const string ExpectedToolVersion = "0.5.1";
    private const string ExpectedToolDigest = "529142f9ffa08443e424d0ef32ddc47e56e1b826698736aaff3341a9ba15972c";
    private const string ExpectedToolRevision = "75bd2b8b99838bf8ec49afa2a82ba03f557cd584";
    private const string ExpectedMethodId = "grid.gta-v-enhanced.rpf-member-acquisition";
    private const string LegacyReceiptV1MethodVersion = "2";
    private const string ExpectedMethodVersion = "4";

    public static async Task<ValidatedGtaAcquisition> LoadAsync(
        string receiptPath,
        string gameRoot,
        CancellationToken cancellationToken = default)
    {
        receiptPath = Path.GetFullPath(receiptPath);
        gameRoot = Path.GetFullPath(gameRoot);
        var bytes = await File.ReadAllBytesAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 64,
        });
        var root = document.RootElement;
        var receiptSchemaVersion = root.GetProperty("schemaVersion").GetInt32();
        Require(receiptSchemaVersion is 1 or 2, "Unsupported acquisition receipt schema.");
        Require(root.GetProperty("gameId").GetString() == ExpectedGameId, "Acquisition receipt GameId mismatch.");
        VerifyDocumentDigest(root);
        var sourceManifest = LoadSourceFamilyManifest();
        var closureVersion = VerifySourceFamilyManifestBinding(root, receiptSchemaVersion, sourceManifest);
        VerifyMemberCoordinateClosure(root, sourceManifest, closureVersion);

        var platform = root.GetProperty("platformObservation");
        Require(platform.GetProperty("provider").GetString() == "valve.steam", "Acquisition provider mismatch.");
        Require(platform.GetProperty("appId").GetString() == ExpectedAppId, "Steam app identity mismatch.");
        var buildId = RequireText(platform.GetProperty("buildId"), "Steam build ID");
        Require(root.GetProperty("gameVersion").GetString() == buildId, "Receipt build coordinates disagree.");
        var observedAtText = RequireText(root.GetProperty("observedAtUtc"), "observation time");
        var hasObservedAt = DateTimeOffset.TryParse(
            observedAtText,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var observedAt);
        Require(observedAtText.EndsWith('Z') && hasObservedAt && observedAt.Offset == TimeSpan.Zero,
            "Acquisition observation time must be valid round-trip UTC text.");
        VerifySteamManifest(platform, gameRoot, receiptSchemaVersion);

        var tool = root.GetProperty("acquisitionTool");
        Require(tool.GetProperty("toolId").GetString() == "fivefury.rpf-read", "Acquisition tool ID mismatch.");
        Require(tool.GetProperty("exactVersion").GetString() == ExpectedToolVersion, "FiveFury version mismatch.");
        Require(tool.GetProperty("artifactSha256").GetString() == ExpectedToolDigest, "FiveFury artifact digest mismatch.");
        Require(tool.GetProperty("sourceRevision").GetString() == ExpectedToolRevision, "FiveFury source revision mismatch.");
        Require(tool.GetProperty("methodId").GetString() == ExpectedMethodId, "Acquisition method mismatch.");
        var methodVersion = receiptSchemaVersion == 1
            ? LegacyReceiptV1MethodVersion
            : ExpectedMethodVersion;
        Require(tool.GetProperty("methodVersion").GetString() == methodVersion, "Acquisition method version mismatch.");
        Require(tool.GetProperty("receiptSchemaVersion").GetInt32() == receiptSchemaVersion,
            "Acquisition tool receipt schema mismatch.");

        var method = new AcquisitionMethodCoordinate(
            ExpectedMethodId,
            methodVersion,
            "fivefury.rpf-read",
            ExpectedToolVersion,
            new ContentDigest(ContentDigest.Sha256Algorithm, ExpectedToolDigest));
        var appIdentity = SourceNativeIdentifier.FromExactUtf8(
            "valve.steam",
            "Application",
            ExpectedAppId,
            "valve.steam.app-id.exact-utf8",
            1);
        var gameVersion = SourceNativeVersion.FromExactUtf8(
            "valve.steam.app.3240220.build-id",
            buildId,
            "valve.steam.build-id.exact-utf8",
            1);

        var receiptDirectory = Path.GetDirectoryName(receiptPath) ??
            throw new InvalidDataException("Acquisition receipt has no directory.");
        var receipts = ImmutableArray.CreateBuilder<SourceAcquisitionReceipt>();
        var bindings = ImmutableArray.CreateBuilder<SourceArtifactAcquisitionBinding>();
        var frozenMembers = ImmutableDictionary.CreateBuilder<string, FrozenMember>(StringComparer.Ordinal);
        var fingerprints = ImmutableArray.CreateBuilder<SourceArtifactId>();
        var seenContainers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var coordinate = RequireText(container.GetProperty("containerCoordinate"), "container coordinate");
            Require(seenContainers.Add(coordinate), "Duplicate acquisition container coordinate.");
            ValidateRelativeCoordinate(coordinate);
            var containerLength = container.GetProperty("byteLength").GetInt64();
            var containerDigest = new ContentDigest(
                ContentDigest.Sha256Algorithm,
                RequireText(container.GetProperty("sha256"), "container digest"));
            var containerPath = ResolveWithin(gameRoot, coordinate);
            var actualContainer = await ComputeDigestAsync(containerPath, cancellationToken).ConfigureAwait(false);
            Require(actualContainer.Length == containerLength && actualContainer.Digest == containerDigest,
                $"Container digest or length mismatch: {coordinate}");
            fingerprints.Add(SourceArtifactId.DeriveV1(containerDigest));

            var members = ImmutableArray.CreateBuilder<SourceAcquisitionMember>();
            foreach (var member in container.GetProperty("members").EnumerateArray())
            {
                var memberPath = RequireText(member.GetProperty("memberPath"), "member path");
                ValidateRelativeCoordinate(memberPath);
                var memberCoordinateText = RequireText(member.GetProperty("memberCoordinate"), "member coordinate");
                Require(memberCoordinateText == $"{coordinate}!/{memberPath}", "Member coordinate does not bind its container.");
                var memberLength = member.GetProperty("byteLength").GetInt64();
                var memberDigest = new ContentDigest(
                    ContentDigest.Sha256Algorithm,
                    RequireText(member.GetProperty("sha256"), "member digest"));
                var artifactId = SourceArtifactId.DeriveV1(memberDigest);
                var frozenPath = ResolveWithin(
                    receiptDirectory,
                    "members/" + coordinate + "/" + memberPath);
                var exactBytes = (await File.ReadAllBytesAsync(frozenPath, cancellationToken).ConfigureAwait(false)).ToImmutableArray();
                Require(exactBytes.Length == memberLength && ContentDigest.ComputeSha256(exactBytes.AsSpan()) == memberDigest,
                    $"Frozen member digest or length mismatch: {memberCoordinateText}");
                Require(frozenMembers.TryAdd(
                    memberCoordinateText,
                    new FrozenMember(memberCoordinateText, exactBytes, memberDigest, artifactId, memberLength)),
                    "Duplicate acquisition member coordinate.");
                var memberCoordinate = CreateMemberCoordinate(memberCoordinateText);
                members.Add(new SourceAcquisitionMember(memberCoordinate, memberLength, memberDigest, artifactId));
            }

            var containerCoordinate = SourceNativeIdentifier.FromExactUtf8(
                "rockstar.gta-v.enhanced.container-coordinate",
                "Rpf7Container",
                coordinate,
                "grid.gta-v.container-coordinate.exact-utf8",
                1);
            var receiptId = SourceAcquisitionReceiptId.DeriveV1(
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity,
                gameVersion,
                containerCoordinate,
                containerLength,
                containerDigest,
                method,
                members.ToImmutable());
            var receipt = new SourceAcquisitionReceipt(
                receiptId,
                SourceAcquisitionReceipt.CurrentSchemaVersion,
                ProductionGridCatalogService.GrandTheftAutoVEnhancedId,
                appIdentity,
                gameVersion,
                containerCoordinate,
                containerLength,
                containerDigest,
                method,
                members.ToImmutable());
            receipts.Add(receipt);
            foreach (var memberValue in receipt.Members)
                bindings.Add(new SourceArtifactAcquisitionBinding(
                    memberValue.ArtifactId,
                    receipt.Id,
                    memberValue.MemberCoordinate,
                    memberValue.ByteLength,
                    memberValue.Digest));
        }

        var expectedMemberCount = closureVersion switch { 2 => 1102, 1 => 1062, _ => 1061 };
        Require(receipts.Count == 4 && frozenMembers.Count == expectedMemberCount,
            $"The Enhanced canary acquisition must contain exactly four containers and {expectedMemberCount:N0} members.");
        return new ValidatedGtaAcquisition(
            gameVersion,
            observedAt,
            receipts.ToImmutable(),
            bindings.ToImmutable(),
            frozenMembers.ToImmutable(),
            fingerprints.Distinct().OrderBy(value => value.Value, StringComparer.Ordinal).ToImmutableArray());
    }

    internal static (string ManifestId, int SchemaVersion, string DocumentSha256) SourceFamilyManifestIdentity()
    {
        var manifest = LoadSourceFamilyManifest();
        return (manifest.ManifestId, manifest.SchemaVersion, manifest.DocumentSha256);
    }

    internal static (string ManifestId, int SchemaVersion, string DocumentSha256) SourceFamilyManifestIdentity(
        string manifestPath)
    {
        var manifest = LoadSourceFamilyManifest(manifestPath);
        return (manifest.ManifestId, manifest.SchemaVersion, manifest.DocumentSha256);
    }

    private static SourceFamilyManifest LoadSourceFamilyManifest(string? manifestPath = null)
    {
        var path = manifestPath;
        if (path is null)
        {
            var repositoryRoot = GitBuildProvenanceResolver.FindRepositoryRoot();
            path = Path.Combine(
                repositoryRoot,
                SourceFamilyManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        path = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32,
        });
        var root = document.RootElement;
        RequireExactProperties(root,
            ["schemaVersion", "manifestId", "gameId", "steamAppId", "steamBuildId", "sourceFamilies", "containers"],
            []);
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        var manifestId = RequireText(root.GetProperty("manifestId"), "source-family manifest ID");
        Require(schemaVersion == 2 &&
                manifestId == "grid.gta-v-enhanced.source-families" &&
                root.GetProperty("gameId").GetString() == ExpectedGameId &&
                root.GetProperty("steamAppId").GetString() == ExpectedAppId &&
                root.GetProperty("steamBuildId").GetString() == "25261616",
            "Source-family manifest identity mismatch.");

        var validStatuses = new HashSet<string>(
            ["supported", "indexed-unconsumed", "diagnostic", "unsupported-declared"],
            StringComparer.Ordinal);
        var validKinds = new HashSet<string>(["Location", "MissionQuest", "Item", "Actor"], StringComparer.Ordinal);
        var familyStatus = new Dictionary<string, string>(StringComparer.Ordinal);
        var familyIds = new List<string>();
        foreach (var family in root.GetProperty("sourceFamilies").EnumerateArray())
        {
            RequireExactProperties(family,
                ["sourceFamilyId", "status", "knowledgeKinds"],
                ["reasonCode", "potentialCoverage"]);
            var familyId = RequireText(family.GetProperty("sourceFamilyId"), "source family ID");
            var status = RequireText(family.GetProperty("status"), "source family status");
            Require(validStatuses.Contains(status), "Unsupported source-family status.");
            var kinds = family.GetProperty("knowledgeKinds").EnumerateArray()
                .Select(value => RequireText(value, "source family knowledge kind"))
                .ToArray();
            Require(kinds.Length != 0 &&
                    kinds.SequenceEqual(kinds.OrderBy(value => value, StringComparer.Ordinal)) &&
                    kinds.Distinct(StringComparer.Ordinal).Count() == kinds.Length &&
                    kinds.All(validKinds.Contains),
                "Source-family knowledge kinds must be nonempty, unique, known, and ordinally sorted.");
            Require(status == "supported" || family.TryGetProperty("reasonCode", out _),
                "Nonsupported source families require a reason code.");
            if (family.TryGetProperty("reasonCode", out var reason))
                _ = RequireText(reason, "source family reason code");
            if (family.TryGetProperty("potentialCoverage", out var potential))
                _ = RequireText(potential, "source family potential coverage");
            Require(familyStatus.TryAdd(familyId, status), "Duplicate source-family ID.");
            familyIds.Add(familyId);
        }
        Require(familyIds.Count != 0 &&
                familyIds.SequenceEqual(familyIds.OrderBy(value => value, StringComparer.Ordinal)),
            "Source families must be ordinally sorted.");

        var containers = ImmutableArray.CreateBuilder<SourceContainerDeclaration>();
        var containerCoordinates = new List<string>();
        var fixedCount = 0;
        var dynamicCount = 0;
        foreach (var container in root.GetProperty("containers").EnumerateArray())
        {
            var coordinate = RequireText(container.GetProperty("containerCoordinate"), "source container coordinate");
            ValidateRelativeCoordinate(coordinate);
            var mode = RequireText(container.GetProperty("memberMode"), "source member mode");
            containerCoordinates.Add(coordinate);
            if (mode is "fixed" or "fixed-and-dynamic-prefix")
            {
                RequireExactProperties(container,
                    mode == "fixed"
                        ? ["containerCoordinate", "memberMode", "members"]
                        : ["containerCoordinate", "memberMode", "members", "dynamicMembers"], []);
                var members = ImmutableArray.CreateBuilder<string>();
                foreach (var member in container.GetProperty("members").EnumerateArray())
                {
                    RequireExactProperties(member,
                        ["memberPath", "sourceFamilyId", "formatId", "exactFormatVersion"], []);
                    var memberPath = RequireText(member.GetProperty("memberPath"), "source member path");
                    ValidateRelativeCoordinate(memberPath);
                    var familyId = RequireText(member.GetProperty("sourceFamilyId"), "member source family ID");
                    Require(familyStatus.TryGetValue(familyId, out var status) && status != "unsupported-declared",
                        "Acquired member references an invalid source family.");
                    var formatId = RequireText(member.GetProperty("formatId"), "source member format ID");
                    var formatVersion = RequireText(
                        member.GetProperty("exactFormatVersion"), "source member format version");
                    var expected = ExpectedFixedSourceDeclaration(coordinate, memberPath);
                    Require(expected is not null &&
                            familyId == expected.Value.FamilyId &&
                            formatId == expected.Value.FormatId &&
                            formatVersion == expected.Value.FormatVersion,
                        $"Fixed source coordinate has an unapproved family or format tuple: {coordinate}!/{memberPath}");
                    members.Add(memberPath);
                }
                var exactMembers = members.ToImmutable();
                Require(!exactMembers.IsEmpty &&
                        exactMembers.SequenceEqual(exactMembers.OrderBy(value => value, StringComparer.Ordinal)) &&
                        exactMembers.Distinct(StringComparer.Ordinal).Count() == exactMembers.Length,
                    "Fixed source members must be unique and ordinally sorted.");
                fixedCount += exactMembers.Length;
                if (mode == "fixed-and-dynamic-prefix")
                {
                    var dynamic = ParseDynamicDeclaration(container.GetProperty("dynamicMembers"), coordinate, familyStatus);
                    dynamicCount++;
                    containers.Add(new SourceContainerDeclaration(
                        coordinate, exactMembers, dynamic.Prefix, dynamic.Suffix, dynamic.Count));
                }
                else
                {
                    containers.Add(new SourceContainerDeclaration(coordinate, exactMembers, null, null, 0));
                }
            }
            else if (mode == "dynamic-prefix")
            {
                RequireExactProperties(container, ["containerCoordinate", "memberMode", "dynamicMembers"], []);
                var dynamic = ParseDynamicDeclaration(container.GetProperty("dynamicMembers"), coordinate, familyStatus);
                dynamicCount++;
                containers.Add(new SourceContainerDeclaration(
                    coordinate, ImmutableArray<string>.Empty, dynamic.Prefix, dynamic.Suffix, dynamic.Count));
            }
            else
            {
                throw new InvalidDataException("Unsupported source member mode.");
            }
        }
        Require(containers.Count == 4 && fixedCount == 10 && dynamicCount == 2 &&
                containerCoordinates.SequenceEqual(containerCoordinates.OrderBy(value => value, StringComparer.Ordinal)) &&
                containerCoordinates.Distinct(StringComparer.Ordinal).Count() == containerCoordinates.Count,
            "Source-family manifest requires four ordered containers, ten fixed members, and two dynamic UGC families.");

        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical)) WriteCanonical(writer, root);
        var documentSha256 = Convert.ToHexStringLower(SHA256.HashData(canonical.ToArray()));
        Require(documentSha256 == ExpectedSourceFamilyManifestSha256,
            "Source-family manifest content does not match the approved v2 registry.");
        return new SourceFamilyManifest(
            manifestId,
            schemaVersion,
            documentSha256,
            containers.ToImmutable());
    }

    private static (string FamilyId, string FormatId, string FormatVersion)? ExpectedFixedSourceDeclaration(
        string containerCoordinate,
        string memberPath) =>
        (containerCoordinate, memberPath) switch
        {
            ("common.rpf", "data/ai/weapons.meta") =>
                ("rockstar.gta-v.enhanced.weapons-meta",
                    "rockstar.gta-v.weapons-meta.cweaponinfoblob-xml", "1"),
            ("common.rpf", "data/levels/gta5/mapzones.xml") =>
                ("rockstar.gta-v.enhanced.mapzones",
                    "rockstar.gta-v.mapzones.cmapzonescontainer-xml", "1"),
            ("update/update.rpf", "common/data/ai/ambientpedmodelsets.meta") =>
                ("rockstar.gta-v.enhanced.ambient-ped-model-sets",
                    "rockstar.gta-v.ambient-ped-model-sets-xml", "1"),
            ("update/update.rpf", "common/data/gen9_exclusive_assets_peds.meta") =>
                ("rockstar.gta-v.enhanced.gen9-exclusive-peds",
                    "rockstar.gta-v.gen9-exclusive-assets-peds-xml", "1"),
            ("update/update.rpf", "common/data/levels/gta5/popzone.ipl") =>
                ("rockstar.gta-v.enhanced.population-zones",
                    "rockstar.gta-v.population-zones-ipl", "1"),
            ("update/update.rpf", "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx") =>
                ("rockstar.gta-v.enhanced.hud-gfx", "rockstar.scaleform.gfx-v8", "1"),
            ("update/update.rpf", "x64/patch/data/lang/american_rel.rpf") =>
                ("rockstar.gta-v.enhanced.nested-localization-containers",
                    "rockstar.rpf7-container", "1"),
            ("update/update.rpf", "x64/patch/data/lang/american_rel.rpf!/global.gxt2") =>
                ("rockstar.gta-v.enhanced.patch-american-localization",
                    "rockstar.gta-v.gxt2-binary", "1"),
            ("x64b.rpf", "data/lang/american_rel.rpf") =>
                ("rockstar.gta-v.enhanced.nested-localization-containers",
                    "rockstar.rpf7-container", "1"),
            ("x64b.rpf", "data/lang/american_rel.rpf!/global.gxt2") =>
                ("rockstar.gta-v.enhanced.base-american-localization",
                    "rockstar.gta-v.gxt2-binary", "1"),
            _ => null,
        };

    private static (string Prefix, string Suffix, int Count) ParseDynamicDeclaration(
        JsonElement dynamic,
        string coordinate,
        IReadOnlyDictionary<string, string> familyStatus)
    {
        RequireExactProperties(dynamic,
            ["prefix", "suffix", "exactCount", "sourceFamilyId", "formatId", "exactFormatVersion"], []);
        var prefix = RequireText(dynamic.GetProperty("prefix"), "dynamic source prefix");
        Require(prefix.EndsWith('/') && !prefix.Contains('\\') &&
                !prefix.Split('/').Any(segment => segment is "." or ".."),
            "Dynamic source prefix is noncanonical.");
        var suffix = RequireText(dynamic.GetProperty("suffix"), "dynamic source suffix");
        var exactCount = dynamic.GetProperty("exactCount").GetInt32();
        var familyId = RequireText(dynamic.GetProperty("sourceFamilyId"), "dynamic source family ID");
        var formatId = RequireText(dynamic.GetProperty("formatId"), "dynamic source format ID");
        var formatVersion = RequireText(dynamic.GetProperty("exactFormatVersion"), "dynamic source format version");
        var valid = coordinate switch
        {
            "common.rpf" => prefix == "data/ugc/" && exactCount == 40 &&
                            familyId == "rockstar.gta-v.enhanced.online-activity-registry",
            "update/update2.rpf" => prefix == "common/data/ugc/" && exactCount == 1052 &&
                                     familyId == "rockstar.gta-v.enhanced.ugc-missions",
            _ => false,
        };
        Require(valid && suffix == ".ugc" && familyStatus.GetValueOrDefault(familyId) == "supported" &&
                formatId == "rockstar.gta-v.ugc.mission-json" && formatVersion == "1",
            "Dynamic UGC source declaration mismatch.");
        return (prefix, suffix, exactCount);
    }

    private static int VerifySourceFamilyManifestBinding(
        JsonElement root,
        int receiptSchemaVersion,
        SourceFamilyManifest manifest)
    {
        if (!root.TryGetProperty("sourceFamilyManifest", out var binding))
            return 0;
        Require(receiptSchemaVersion == 2, "Only schema-v2 receipts can bind a source-family manifest.");
        RequireExactProperties(binding, ["manifestId", "schemaVersion", "documentSha256"], []);
        var manifestId = binding.GetProperty("manifestId").GetString();
        var schema = binding.GetProperty("schemaVersion").GetInt32();
        var digest = binding.GetProperty("documentSha256").GetString();
        if (manifestId == manifest.ManifestId && schema == manifest.SchemaVersion && digest == manifest.DocumentSha256)
            return 2;
        Require(manifestId == "grid.gta-v-enhanced.source-families" && schema == 1 &&
                digest == LegacySourceFamilyManifestSha256,
            "Receipt source-family manifest binding mismatch.");
        return 1;
    }

    private static void VerifyMemberCoordinateClosure(
        JsonElement root,
        SourceFamilyManifest manifest,
        int closureVersion)
    {
        var receivedContainers = root.GetProperty("containers").EnumerateArray().ToArray();
        Require(receivedContainers.Length == manifest.Containers.Length,
            "Acquisition receipt container count mismatch.");
        var total = 0;
        for (var index = 0; index < manifest.Containers.Length; index++)
        {
            var expected = manifest.Containers[index];
            var received = receivedContainers[index];
            var coordinate = RequireText(received.GetProperty("containerCoordinate"), "container coordinate");
            Require(coordinate == expected.Coordinate, "Receipt containers are missing, extra, or reordered.");
            var members = received.GetProperty("members").EnumerateArray().ToArray();
            var paths = members.Select(member => RequireText(member.GetProperty("memberPath"), "member path")).ToArray();
            var coordinates = members.Select(member => RequireText(member.GetProperty("memberCoordinate"), "member coordinate"))
                .ToArray();
            Require(coordinates.SequenceEqual(coordinates.OrderBy(value => value, StringComparer.Ordinal)) &&
                    coordinates.Distinct(StringComparer.Ordinal).Count() == coordinates.Length,
                "Receipt members must be unique and ordinally sorted.");
            Require(members.Zip(paths).All(pair =>
                    pair.First.GetProperty("memberCoordinate").GetString() == $"{coordinate}!/{pair.Second}"),
                "Receipt member coordinate mismatch.");
            if (expected.DynamicPrefix is not null)
            {
                var dynamicCount = closureVersion == 2
                    ? expected.DynamicCount
                    : expected.Coordinate == "update/update2.rpf" ? 1052 : 0;
                var dynamicPaths = paths.Where(path =>
                        path.StartsWith(expected.DynamicPrefix, StringComparison.Ordinal) &&
                        path.EndsWith(expected.DynamicSuffix!, StringComparison.Ordinal))
                    .ToArray();
                var fixedPaths = paths.Except(dynamicPaths, StringComparer.Ordinal).ToArray();
                Require(dynamicPaths.Length == dynamicCount && fixedPaths.SequenceEqual(expected.FixedMembers),
                    "Receipt UGC closure does not match the source-family manifest.");
            }
            else
            {
                var expectedMembers = expected.FixedMembers;
                if (closureVersion == 0)
                    expectedMembers = expectedMembers
                        .Where(path => path != "x64/data/cdimages/scaleform_generic.rpf!/hud.gfx")
                        .ToImmutableArray();
                Require(paths.SequenceEqual(expectedMembers),
                    $"Receipt fixed-member closure mismatch: {coordinate}");
            }
            total += paths.Length;
        }
        Require(total == (closureVersion switch { 2 => 1102, 1 => 1062, _ => 1061 }),
            "Acquisition member closure count mismatch.");
    }

    private static void RequireExactProperties(
        JsonElement value,
        IEnumerable<string> required,
        IEnumerable<string> optional)
    {
        Require(value.ValueKind == JsonValueKind.Object, "Manifest value must be an object.");
        var requiredSet = required.ToHashSet(StringComparer.Ordinal);
        var allowed = requiredSet.Concat(optional).ToHashSet(StringComparer.Ordinal);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        Require(requiredSet.All(name => actual.Contains(name, StringComparer.Ordinal)) &&
                actual.All(allowed.Contains) &&
                actual.Distinct(StringComparer.Ordinal).Count() == actual.Length,
            "Manifest object has missing, duplicate, or unsupported fields.");
    }

    private sealed record SourceContainerDeclaration(
        string Coordinate,
        ImmutableArray<string> FixedMembers,
        string? DynamicPrefix,
        string? DynamicSuffix,
        int DynamicCount);

    private sealed record SourceFamilyManifest(
        string ManifestId,
        int SchemaVersion,
        string DocumentSha256,
        ImmutableArray<SourceContainerDeclaration> Containers);

    private static void VerifySteamManifest(JsonElement expected, string gameRoot, int receiptSchemaVersion)
    {
        var manifestCoordinate = RequireText(expected.GetProperty("manifestCoordinate"), "Steam manifest coordinate");
        Require(manifestCoordinate == $"steamapps/appmanifest_{ExpectedAppId}.acf", "Steam manifest coordinate mismatch.");
        var manifest = Path.GetFullPath(Path.Combine(Path.GetFullPath(gameRoot), "..", "..", $"appmanifest_{ExpectedAppId}.acf"));
        var bytes = File.ReadAllBytes(manifest);
        var historicalLength = expected.GetProperty("manifestByteLength").GetInt64();
        var historicalDigest = RequireText(expected.GetProperty("manifestSha256"), "Steam manifest digest");
        Require(historicalLength > 0 && Regex.IsMatch(historicalDigest, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant),
            "Historical Steam manifest observation is invalid.");
        if (receiptSchemaVersion == 1)
        {
            Require(bytes.LongLength == historicalLength, "Steam manifest length mismatch.");
            Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == historicalDigest,
                "Steam manifest digest mismatch.");
            return;
        }

        Require(RequireText(expected.GetProperty("provider"), "Steam provider") == "valve.steam",
            "Acquisition provider mismatch.");
        Require(RequireText(expected.GetProperty("appIdFieldPath"), "Steam app ID field path") == "/AppState/appid" &&
                RequireText(expected.GetProperty("buildIdFieldPath"), "Steam build ID field path") == "/AppState/buildid" &&
                RequireText(expected.GetProperty("installDirFieldPath"), "Steam install directory field path") ==
                "/AppState/installdir",
            "Stable Steam manifest field coordinates mismatch.");
        var fields = ParseSteamManifestStableFields(bytes);
        Require(fields.AppId == RequireText(expected.GetProperty("appId"), "Steam app ID") &&
                fields.BuildId == RequireText(expected.GetProperty("buildId"), "Steam build ID") &&
                fields.InstallDir == RequireText(expected.GetProperty("installDir"), "Steam install directory"),
            "Stable Steam app/build/install observation does not match the local app manifest.");
        Require(fields.AppId == ExpectedAppId &&
                fields.InstallDir == Path.GetFileName(Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar)),
            "Steam manifest app or installation identity mismatch.");
    }

    private static (string AppId, string BuildId, string InstallDir) ParseSteamManifestStableFields(byte[] bytes)
    {
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Steam manifest is not strict UTF-8.", exception);
        }
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => value.Length != 0)
            .ToArray();
        if (lines.Length < 3 || lines[0] != "\"AppState\"" || lines[1] != "{")
            throw new InvalidDataException("Steam manifest requires one exact AppState root object.");

        var stableNames = new[] { "appid", "buildid", "installdir" };
        var stable = new Dictionary<string, string>(StringComparer.Ordinal);
        var depth = 1;
        var expectObjectOpen = false;
        var rootClosed = false;
        for (var index = 2; index < lines.Length; index++)
        {
            var line = lines[index];
            if (rootClosed)
                throw new InvalidDataException("Steam manifest has content after the AppState root.");
            if (line == "{")
            {
                if (!expectObjectOpen)
                    throw new InvalidDataException("Steam manifest has an unbound object opening.");
                depth++;
                expectObjectOpen = false;
                continue;
            }
            if (expectObjectOpen)
                throw new InvalidDataException("Steam manifest object name is not followed by an opening brace.");
            if (line == "}")
            {
                depth--;
                if (depth < 0) throw new InvalidDataException("Steam manifest has an unmatched closing brace.");
                if (depth == 0) rootClosed = true;
                continue;
            }

            var pair = Regex.Match(line, "\\A\"([^\"\\r\\n]+)\"\\s+\"([^\"\\r\\n]*)\"\\z",
                RegexOptions.CultureInvariant);
            if (pair.Success)
            {
                var name = pair.Groups[1].Value;
                var stableName = stableNames.SingleOrDefault(value =>
                    string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
                if (stableName is not null &&
                    (!string.Equals(name, stableName, StringComparison.Ordinal) ||
                     depth != 1 || stable.ContainsKey(stableName) || pair.Groups[2].Value.Length == 0))
                    throw new InvalidDataException(
                        $"Steam manifest requires exactly one nonempty direct exact-case {stableName} field.");
                if (stableName is not null) stable.Add(stableName, pair.Groups[2].Value);
                continue;
            }

            var objectName = Regex.Match(line, "\\A\"([^\"\\r\\n]+)\"\\z", RegexOptions.CultureInvariant);
            if (!objectName.Success)
                throw new InvalidDataException($"Steam manifest has unsupported structure at line {index + 1}.");
            if (stableNames.Any(value =>
                    string.Equals(value, objectName.Groups[1].Value, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A stable Steam identity field cannot be an object.");
            expectObjectOpen = true;
        }
        if (expectObjectOpen || depth != 0 || !rootClosed)
            throw new InvalidDataException("Steam manifest AppState object is not closed.");
        if (stable.Count != stableNames.Length)
            throw new InvalidDataException("Steam manifest is missing a stable AppState identity field.");
        return (stable["appid"], stable["buildid"], stable["installdir"]);
    }

    private static void VerifyDocumentDigest(JsonElement root)
    {
        var expected = RequireText(root.GetProperty("receiptDocumentSha256"), "receipt document digest");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, root, skipReceiptDigest: true);
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        Require(actual == expected, "Acquisition receipt document digest mismatch.");
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value, bool skipReceiptDigest = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (skipReceiptDigest && property.NameEquals("receiptDocumentSha256")) continue;
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static SourceNativeIdentifier CreateMemberCoordinate(string value) =>
        SourceNativeIdentifier.FromExactUtf8(
            "rockstar.gta-v.enhanced.rpf-member-coordinate",
            "Rpf7Member",
            value,
            "grid.gta-v.rpf-member-coordinate.exact-utf8",
            1);

    private static string ResolveWithin(string root, string relative)
    {
        ValidateRelativeCoordinate(relative);
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Acquisition coordinate escaped its bounded root.");
        return result;
    }

    private static void ValidateRelativeCoordinate(string value)
    {
        Require(!string.IsNullOrEmpty(value) && !value.Contains('\\') && !value.StartsWith("/", StringComparison.Ordinal),
            "Acquisition coordinates must be normalized relative paths.");
        Require(!value.Split('/').Any(segment => segment is "" or "." or ".."),
            "Acquisition coordinates cannot contain empty or traversal segments.");
    }

    private static async Task<(long Length, ContentDigest Digest)> ComputeDigestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            length += read;
            hash.AppendData(buffer, 0, read);
        }
        return (length, new ContentDigest(ContentDigest.Sha256Algorithm, Convert.ToHexStringLower(hash.GetHashAndReset())));
    }

    private static string RequireText(JsonElement value, string name)
    {
        var result = value.GetString();
        if (string.IsNullOrEmpty(result) || result.Trim() != result)
            throw new InvalidDataException($"{name} is missing or noncanonical.");
        _ = new UTF8Encoding(false, true).GetBytes(result);
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
