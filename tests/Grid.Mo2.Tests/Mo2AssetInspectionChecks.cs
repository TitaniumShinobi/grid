using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

static class Mo2AssetInspectionChecks
{
    public static async Task<ImmutableArray<Mo2AssetInspectionCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2AssetInspectionCheckResult>();
        await RunAsync(results, "asset reader materializes one exact uncompressed BSA member", ExactBsaMemberAsync);
        await RunAsync(results, "asset reader decodes bounded Skyrim BSA LZ4 members", CompressedBsaMemberAsync);
        await RunAsync(results, "asset reader refuses unsafe, missing, and oversized BSA members", BsaRefusalAsync);
        await RunAsync(results, "NIF validator checks structure and emits bounded texture paths", NifValidationAsync);
        await RunAsync(results, "DDS validator checks legacy and DX10 mip payloads", DdsValidationAsync);
        await RunAsync(results, "asset inspection retains provider evidence and deterministic ordering", InspectionAsync);
        await RunAsync(results, "asset inspection parses and hashes each BSA once per batch", BsaBatchReuseAsync);
        await RunAsync(results, "winning NIFs expose missing embedded texture dependencies", NifTextureDependencyAsync);
        await RunAsync(results, "NIF texture fanout uses one case-insensitive mesh group without dropping sources", NifTextureFanoutAsync);
        await RunAsync(results, "winning BSA NIF members expose missing embedded textures", BsaNifTextureDependencyAsync);
        await RunAsync(results, "NIF texture inspection fails closed on uncertain providers", NifTextureUncertainProviderAsync);
        await RunAsync(results, "Papyrus source and bytecode inspection is bounded and explicit", PapyrusInspectionAsync);
        await RunAsync(results, "SPID inspection distinguishes broken source plugins from optional filters", SpidInspectionAsync);
        var liveNifPath = Environment.GetEnvironmentVariable("GRID_TEST_NIF_PATH");
        if (!string.IsNullOrWhiteSpace(liveNifPath))
        {
            await RunAsync(results, "configured live NIF passes the bounded structural validator", () => LiveNifAsync(liveNifPath));
        }
        var liveBsaPath = Environment.GetEnvironmentVariable("GRID_TEST_BSA_PATH");
        var liveBsaMember = Environment.GetEnvironmentVariable("GRID_TEST_BSA_MEMBER");
        if (!string.IsNullOrWhiteSpace(liveBsaPath) && !string.IsNullOrWhiteSpace(liveBsaMember))
        {
            await RunAsync(results, "configured live BSA yields one bounded exact member", () => LiveBsaAsync(liveBsaPath, liveBsaMember));
        }
        return results.ToImmutable();
    }

    private static async Task ExactBsaMemberAsync()
    {
        using var fixture = new AssetFixture();
        var content = Encoding.ASCII.GetBytes("exact-member");
        var archive = fixture.Write("uncompressed.bsa", AssetFixture.Bsa("meshes/fixture.nif", content));
        var result = await fixture.Service.ReadBsaMemberAsync(
            archive,
            "meshes\\fixture.nif",
            new(MaximumArchiveBytesHashed: 1024 * 1024));

        Equal(Mo2AssetInspectionStatus.Complete, result.Status);
        Equal(false, result.Compressed);
        Equal("meshes\\fixture.nif", result.ExactMemberPath);
        Equal(Convert.ToHexString(SHA256.HashData(content)), result.MemberSha256);
        True(result.ArchiveSha256?.Length == 64);
        True(result.Content.AsSpan().SequenceEqual(content));
    }

    private static async Task CompressedBsaMemberAsync()
    {
        using var fixture = new AssetFixture();
        var content = Enumerable.Range(0, 80).Select(value => checked((byte)value)).ToArray();
        var archive = fixture.Write("compressed.bsa", AssetFixture.Bsa("textures/fixture.dds", content, compressed: true));
        var result = await fixture.Service.ReadBsaMemberAsync(
            archive,
            "textures\\fixture.dds",
            new(MaximumArchiveBytesHashed: 0));

        Equal(Mo2AssetInspectionStatus.Complete, result.Status);
        Equal(true, result.Compressed);
        Equal(content.LongLength, result.UnpackedBytes);
        Equal(null, result.ArchiveSha256);
        True(result.Content.AsSpan().SequenceEqual(content));
    }

    private static async Task BsaRefusalAsync()
    {
        using var fixture = new AssetFixture();
        var archive = fixture.Write("bounded.bsa", AssetFixture.Bsa("meshes/fixture.nif", new byte[64]));
        var unsafePath = await fixture.Service.ReadBsaMemberAsync(archive, "..\\fixture.nif", new());
        Equal(Mo2AssetInspectionStatus.Malformed, unsafePath.Status);
        Contains(unsafePath.Issues, value => value.Code == "mo2.asset.member_path_invalid");

        var missing = await fixture.Service.ReadBsaMemberAsync(archive, "meshes\\missing.nif", new());
        Equal(Mo2AssetInspectionStatus.Missing, missing.Status);

        var oversized = await fixture.Service.ReadBsaMemberAsync(
            archive,
            "meshes\\fixture.nif",
            new(MaximumSourceBytes: 32, MaximumAggregateBytes: 32));
        Equal(Mo2AssetInspectionStatus.Oversized, oversized.Status);
    }

    private static Task NifValidationAsync()
    {
        using var fixture = new AssetFixture();
        var nif = AssetFixture.Nif(
            "textures\\architecture\\fixture.dds",
            "landscape/trees/fixture_n.dds");
        var result = fixture.Service.InspectNif(nif, new());
        Equal(Mo2AssetInspectionStatus.Complete, result.Status);
        Equal(0x14020007u, result.Version);
        Equal(12u, result.UserVersion);
        Equal(100u, result.BethesdaVersion);
        Equal(1, result.BlockCount);
        Equal(2, result.TexturePaths.Length);
        Contains(result.TexturePaths, value => value == "textures\\landscape\\trees\\fixture_n.dds");

        var truncated = fixture.Service.InspectNif(nif.AsMemory(0, nif.Length - 1), new());
        Equal(Mo2AssetInspectionStatus.Malformed, truncated.Status);
        var unsafeTexture = fixture.Service.InspectNif(AssetFixture.Nif("..\\outside.dds"), new());
        Equal(Mo2AssetInspectionStatus.Malformed, unsafeTexture.Status);
        var limited = fixture.Service.InspectNif(nif, new(MaximumNifStrings: 1));
        Equal(Mo2AssetInspectionStatus.Oversized, limited.Status);
        return Task.CompletedTask;
    }

    private static Task DdsValidationAsync()
    {
        using var fixture = new AssetFixture();
        var legacy = fixture.Service.InspectDds(AssetFixture.Dds(4, 4, 1, "DXT1"), new());
        Equal(Mo2AssetInspectionStatus.Complete, legacy.Status);
        Equal("BC1", legacy.Format);
        Equal(8L, legacy.ExpectedPayloadBytes);

        var dx10 = fixture.Service.InspectDds(AssetFixture.Dds(8, 8, 2, "DX10", dxgiFormat: 77), new());
        Equal(Mo2AssetInspectionStatus.Complete, dx10.Status);
        Equal("DXGI_77_BC3", dx10.Format);
        Equal(80L, dx10.ExpectedPayloadBytes);

        var truncated = AssetFixture.Dds(8, 8, 1, "DXT5")[..^1];
        Equal(Mo2AssetInspectionStatus.Malformed, fixture.Service.InspectDds(truncated, new()).Status);
        return Task.CompletedTask;
    }

    private static async Task InspectionAsync()
    {
        using var fixture = new AssetFixture();
        var nifPath = fixture.Write("fixture.nif", AssetFixture.Nif("textures/fixture.dds"));
        var ddsBytes = AssetFixture.Dds(4, 4, 1, "DXT1");
        var ddsPath = fixture.Write("fixture.dds", ddsBytes);
        var provider = new Mo2AssetProviderEvidence(
            "Fixture provider", "ModLooseFile", "mod.fixture", null, 10, true, "Established", "Fixture evidence.", ["evidence.fixture"]);
        var request = new Mo2AssetInspectionRequest(
            [
                new("textures/fixture.dds", Mo2AssetKind.Dds, ddsPath, null, provider, new string('0', 64)),
                new("meshes/fixture.nif", Mo2AssetKind.Nif, nifPath, null, provider),
            ],
            new(MaximumSourceBytes: 1024 * 1024, MaximumAggregateBytes: 2 * 1024 * 1024));

        var result = await fixture.Service.InspectAsync(request);
        Equal(2, result.Targets.Length);
        Equal("meshes\\fixture.nif", result.Targets[0].VirtualPath);
        Equal("Fixture provider", result.Targets[0].Provider.SourceName);
        Equal(Mo2AssetInspectionStatus.DigestMismatch, result.Targets[1].Status);
        True(result.Targets[1].Dds?.Status == Mo2AssetInspectionStatus.Complete);
    }

    private static async Task BsaBatchReuseAsync()
    {
        using var fixture = new AssetFixture();
        var nif = AssetFixture.Nif("textures/batch.dds");
        var archive = fixture.Write("batch.bsa", AssetFixture.Bsa("meshes/batch.nif", nif));
        var provider = new Mo2AssetProviderEvidence(
            "Fixture archive", "ArchiveMember", "mod.fixture", "archive.fixture", 10, true,
            "Established", "Fixture evidence.", ["evidence.fixture"]);
        var limits = new Mo2AssetInspectionLimits(
            MaximumSourceBytes: 1024 * 1024,
            MaximumAggregateBytes: 2 * 1024 * 1024,
            MaximumArchiveBytesHashed: 1024 * 1024);

        var singleReads = new CountingRandomAccessFileFactory();
        var single = await new Mo2SkyrimAssetInspectionService(singleReads).InspectAsync(new(
            [new("meshes/alpha.nif", Mo2AssetKind.Nif, archive, "meshes/batch.nif", provider)], limits));
        Equal(Mo2AssetInspectionStatus.Complete, single.Status);

        var batchReads = new CountingRandomAccessFileFactory();
        var batch = await new Mo2SkyrimAssetInspectionService(batchReads).InspectAsync(new(
            [
                new("meshes/zulu.nif", Mo2AssetKind.Nif, archive, "meshes/batch.nif", provider),
                new("meshes/alpha.nif", Mo2AssetKind.Nif, archive, "meshes/batch.nif", provider),
            ], limits));

        Equal(Mo2AssetInspectionStatus.Complete, batch.Status);
        Equal(2, batch.Targets.Length);
        Equal("meshes\\alpha.nif", batch.Targets[0].VirtualPath);
        Equal(2, batchReads.OpenCount);
        Equal(singleReads.BytesRead + nif.LongLength, batchReads.BytesRead);
    }

    private static async Task NifTextureDependencyAsync()
    {
        using var fixture = new AssetFixture();
        var nifPath = fixture.Write("wearable.nif", AssetFixture.Nif(@"textures\armor\missing_d.dds"));
        var snapshotId = new ResolvedSnapshotId("resolved.fixture");
        var providerId = new ProviderId("provider.fixture");
        var provider = new VirtualFileProvider(
            providerId, VirtualProviderKind.ModLooseFile, "Wearables", new("mod.wearables"), null,
            50, true, "Fixture loose winner.", "provider-fingerprint", []);
        var chain = new ProviderChain(
            snapshotId, new("virtual.mesh"), @"meshes\armor\wearable.nif", [provider], providerId,
            ProviderWinnerConfidence.Established, "Fixture winner is established.", []);
        var snapshot = Snapshot(snapshotId);
        var baseline = new Mo2ResolvedBaselineSnapshot(
            snapshot, [], [], [chain],
            [new(@"meshes\armor\wearable.nif", nifPath, VirtualProviderKind.ModLooseFile,
                "Wearables", 50, new FileInfo(nifPath).Length, File.GetLastWriteTimeUtc(nifPath).Ticks)]);
        var source = new Mo2PluginAssetReference(
            "Wearables.esp", "ARMO", 0x01000110, 128, "MODL", @"armor\wearable.nif",
            @"meshes\armor\wearable.nif", Mo2PluginAssetReferenceKind.Mesh);

        var result = await new Mo2NifTextureDependencyInspector(new WindowsRandomAccessFileFactory())
            .InspectAsync(new(baseline, [source], new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(1, result.MeshesReferenced);
        Equal(1, result.MeshesInspected);
        Equal(1, result.TextureReferences);
        var missing = result.Missing.Single();
        Equal(@"textures\armor\missing_d.dds", missing.RequiredVirtualPath);
        Equal(@"meshes\armor\wearable.nif", missing.Samples.Single().DiscoveredThroughVirtualPath!);
        Equal("NIF_TEXTURE", missing.Samples.Single().SubrecordSignature);
        Equal(64, result.SemanticFingerprint.Length);
    }

    private static async Task NifTextureUncertainProviderAsync()
    {
        using var fixture = new AssetFixture();
        var snapshotId = new ResolvedSnapshotId("resolved.uncertain");
        var provider = new VirtualFileProvider(
            new("provider.uncertain"), VirtualProviderKind.ArchiveMember, "Fixture.bsa", null,
            new("archive.fixture"), 1, false, "Fixture uncertain provider.", "uncertain-fingerprint", []);
        var chain = new ProviderChain(
            snapshotId, new("virtual.uncertain"), @"meshes\uncertain.nif", [provider], null,
            ProviderWinnerConfidence.Uncertain, "No winner.", []);
        var baseline = new Mo2ResolvedBaselineSnapshot(Snapshot(snapshotId), [], [], [chain], []);
        var source = new Mo2PluginAssetReference(
            "Fixture.esp", "STAT", 0x01000120, 256, "MODL", "uncertain.nif",
            @"meshes\uncertain.nif", Mo2PluginAssetReferenceKind.Mesh);

        var result = await new Mo2NifTextureDependencyInspector(new WindowsRandomAccessFileFactory())
            .InspectAsync(new(baseline, [source], new()));

        Equal(Mo2PluginScriptDependencyStatus.Partial, result.Status);
        Equal(0, result.MeshesInspected);
        Equal(0, result.Missing.Length);
        True(result.Issues.Any(value => value.Code == "mo2.nif_texture.mesh_winner_unresolved"));
    }

    private static async Task NifTextureFanoutAsync()
    {
        using var fixture = new AssetFixture();
        var nifPath = fixture.Write("fanout.nif", AssetFixture.Nif(@"textures\armor\fanout_d.dds"));
        var snapshotId = new ResolvedSnapshotId("resolved.fanout");
        var providerId = new ProviderId("provider.fanout");
        var provider = new VirtualFileProvider(
            providerId, VirtualProviderKind.ModLooseFile, "Fanout", new("mod.fanout"), null,
            51, true, "Fixture loose winner.", "fanout-provider-fingerprint", []);
        var meshPath = @"meshes\armor\fanout.nif";
        var chain = new ProviderChain(
            snapshotId, new("virtual.fanout"), meshPath, [provider], providerId,
            ProviderWinnerConfidence.Established, "Fixture winner is established.", []);
        var baseline = new Mo2ResolvedBaselineSnapshot(
            Snapshot(snapshotId), [], [], [chain],
            [new(meshPath, nifPath, VirtualProviderKind.ModLooseFile,
                "Fanout", 51, new FileInfo(nifPath).Length, File.GetLastWriteTimeUtc(nifPath).Ticks)]);
        ImmutableArray<Mo2PluginAssetReference> sources =
        [
            new("Zulu.esp", "ARMO", 0x02000110, 256, "MODL", @"armor\fanout.nif",
                meshPath, Mo2PluginAssetReferenceKind.Mesh),
            new("Alpha.esp", "ARMO", 0x01000110, 128, "MODL", @"armor\fanout.nif",
                meshPath.ToUpperInvariant(), Mo2PluginAssetReferenceKind.Mesh),
        ];

        var result = await new Mo2NifTextureDependencyInspector(new WindowsRandomAccessFileFactory())
            .InspectAsync(new(baseline, sources, new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(1, result.MeshesReferenced);
        Equal(1, result.MeshesInspected);
        Equal(2, result.TextureReferences);
        Equal(2, result.Missing.Length);
        Equal("Alpha.esp", result.References[0].PluginName);
        Equal("Zulu.esp", result.References[1].PluginName);
    }

    private static async Task BsaNifTextureDependencyAsync()
    {
        using var fixture = new AssetFixture();
        var memberPath = @"meshes\armor\archive_wearable.nif";
        var archivePath = fixture.Write("Fixture.bsa",
            AssetFixture.Bsa(memberPath, AssetFixture.Nif(@"textures\armor\archive_missing_d.dds")));
        var snapshotId = new ResolvedSnapshotId("resolved.archive");
        var archiveId = new ArchiveId("archive.fixture");
        var providerId = new ProviderId("provider.archive");
        var provider = new VirtualFileProvider(
            providerId, VirtualProviderKind.ArchiveMember, "Fixture.bsa", null, archiveId,
            12, true, "Fixture archive winner.", "archive-provider-fingerprint", []);
        var chain = new ProviderChain(
            snapshotId, new("virtual.archive-mesh"), memberPath, [provider], providerId,
            ProviderWinnerConfidence.Established, "Archive winner is established.", []);
        var archive = new ResolvedArchiveEntry(
            archiveId, "Fixture.bsa", ArchiveFormat.Bsa, 105, ArchiveSupportStatus.Supported,
            ArchiveActivationState.Active, ArchiveActivationProvenance.EnabledPluginAssociation, null,
            "Wearables", 1, "archive-fingerprint", DateTimeOffset.UnixEpoch, []);
        var baseline = new Mo2ResolvedBaselineSnapshot(
            Snapshot(snapshotId), [], [archive], [chain],
            [new("Fixture.bsa", archivePath, VirtualProviderKind.ModLooseFile, "Wearables", 12,
                new FileInfo(archivePath).Length, File.GetLastWriteTimeUtc(archivePath).Ticks)]);
        var source = new Mo2PluginAssetReference(
            "Wearables.esp", "ARMO", 0x01000130, 320, "MODL", @"armor\archive_wearable.nif",
            memberPath, Mo2PluginAssetReferenceKind.Mesh);

        var result = await new Mo2NifTextureDependencyInspector(new WindowsRandomAccessFileFactory())
            .InspectAsync(new(baseline, [source], new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(1, result.MeshesInspected);
        Equal(@"textures\armor\archive_missing_d.dds", result.Missing.Single().RequiredVirtualPath);
        Equal(memberPath, result.Missing.Single().Samples.Single().DiscoveredThroughVirtualPath!);
    }

    private static ResolvedEnvironmentSnapshot Snapshot(ResolvedSnapshotId snapshotId) => new(
        snapshotId,
        new(new("game.skyrim-special-edition"), new("installation.fixture"), new("profile.fixture"), "catalog.fixture", null),
        new(ResolvedEnvironmentStatus.Complete, DateTimeOffset.UnixEpoch, "snapshot-fingerprint", 0, 0, 1, 1, 0),
        [],
        false);

    private static Task PapyrusInspectionAsync()
    {
        var service = new Mo2PapyrusInspectionService();
        var source = Encoding.UTF8.GetBytes("Scriptname FixtureScript extends ObjectReference\nObjectReference Property Hatch Auto\nEvent OnActivate(ObjectReference akActionRef)\nif akActionRef == Game.GetPlayer()\nHatch.Activate(akActionRef, false)\nendif\nEndEvent");
        var psc = service.InspectPsc(source);
        Equal(Mo2PapyrusInspectionStatus.Complete, psc.Status);
        Equal("FixtureScript", psc.ScriptName);
        Contains(psc.Properties, value => value.Name == "Hatch");
        Contains(psc.Routines, value => value.Name.Equals("OnActivate", StringComparison.OrdinalIgnoreCase));
        Contains(psc.Calls, value => value.Receiver == "Hatch" && value.Method == "Activate");

        var mcmUiSource = Encoding.UTF8.GetBytes("Scriptname FixtureMcm extends Quest\nObjectReference Property Hatch Auto\nInt SelectedInterior = 0\nEvent OnConfigOpen(Int index)\nSetToggleOptionValue(1, true)\nHatch.Disable()\nSelectedInterior = index\nEndEvent");
        var mcmUi = service.InspectPsc(mcmUiSource);
        Equal("ReferenceStateControlObserved", mcmUi.StateAnalysis?.Status);
        Equal(0, mcmUi.StateAnalysis?.PersistenceAccesses.Length);
        Contains(mcmUi.Variables, value => value.Name == "SelectedInterior" && value.DefaultValue == "0");
        Contains(mcmUi.Assignments, value => value.Target == "SelectedInterior" && value.Expression == "index");
        Contains(mcmUi.StateAnalysis!.SelectionVariables, value => value == "SelectedInterior");

        var loadRepairSource = Encoding.UTF8.GetBytes("Scriptname FixtureRepair extends Quest\nObjectReference Property Hatch Auto\nEvent OnPlayerLoadGame()\nHatch.Enable()\nEndEvent");
        var loadRepair = service.InspectPsc(loadRepairSource);
        Equal("LifecycleReapplicationObserved", loadRepair.StateAnalysis?.Status);
        Contains(loadRepair.StateAnalysis!.LifecycleRoutines, value => value == "OnPlayerLoadGame");

        var pex = service.InspectPex(AssetFixture.PexWithReferenceSelection());
        Equal(Mo2PapyrusInspectionStatus.Complete, pex.Status);
        Equal("FixtureScript", pex.ScriptName);
        Equal("Quest", pex.Extends);
        Contains(pex.Properties, value => value.Name == "HatchAlternate" && value.IsAuto);
        Contains(pex.Routines, value => value.Name == "OnGameReload");
        Contains(pex.Calls, value => value.Receiver == "HatchAlternate" && value.Method == "Enable" && value.Routine == "OnGameReload");
        Contains(pex.Calls, value => value.Receiver == "HatchOriginal" && value.Method == "Disable" && value.Routine == "OnGameReload");
        Equal("SelectionReconciliationObserved", pex.StateAnalysis?.Status);
        Contains(pex.StateAnalysis!.PersistenceAccesses, value => value.Method == "GetIntValue" && value.Key == "\"Fixture.Interior\"");

        var malformedPex = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(malformedPex, 0xFA57C0DE);
        Equal(Mo2PapyrusInspectionStatus.Malformed, service.InspectPex(malformedPex).Status);
        return Task.CompletedTask;
    }

    private static Task SpidInspectionAsync()
    {
        var decoder = new Mo2TextDecoder();
        var disabledText = Encoding.UTF8.GetBytes(
            "; optional filter absence must not become a source error\n" +
            "Outfit = 0x800~Panties.esp|NONE|0x123~Optional Filter.esp|NONE|F|NONE|100\n");
        var enabledText = Encoding.UTF8.GetBytes("Spell = 0x812~Effects.esl|ActorTypeNPC|NONE|NONE|NONE|NONE|100\n");
        var documents = ImmutableArray.Create(
            new Mo2SpidDistributionDocument("Panties_DISTR.ini", "Panties provider", new string('A', 64),
                decoder.Decode(ImmutableArray.Create(disabledText), Mo2TextDecodingPolicy.StrictUtf8)),
            new Mo2SpidDistributionDocument("Effects_DISTR.ini", "Effects provider", new string('B', 64),
                decoder.Decode(ImmutableArray.Create(enabledText), Mo2TextDecodingPolicy.StrictUtf8)));
        var plugins = ImmutableArray.Create(
            new PluginEntry(new PluginId("plugin.panties"), "Panties.esp", false, null, HealthLevel.Warning),
            new PluginEntry(new PluginId("plugin.effects"), "Effects.esl", true, 12, HealthLevel.Healthy));

        var result = new Mo2SpidDistributionInspector().Inspect(documents, plugins, new());
        Equal(Mo2SpidDistributionStatus.Complete, result.Status);
        Equal(2, result.DocumentsScanned);
        Equal(2L, result.RulesScanned);
        Equal(2, result.SourcePluginReferences.Length);
        Equal(1, result.SourcePluginReferences.Count(value => value.Status == Mo2SpidPluginReferenceStatus.Disabled));
        Equal(0, result.SourcePluginReferences.Count(value => value.PluginName == "Optional Filter.esp"));
        Equal(1, result.Issues.Count(value => value.Code == "mo2.spid.source_plugin_disabled"));

        var replay = new Mo2SpidDistributionInspector().Inspect(documents.Reverse().ToImmutableArray(), plugins, new());
        Equal(result.SemanticFingerprint, replay.SemanticFingerprint);
        return Task.CompletedTask;
    }

    private static Task LiveNifAsync(string exactPath)
    {
        using var fixture = new AssetFixture();
        var bytes = File.ReadAllBytes(exactPath);
        var result = fixture.Service.InspectNif(bytes, new());
        Equal(Mo2AssetInspectionStatus.Complete, result.Status);
        True(result.TexturePaths.Length > 0);
        return Task.CompletedTask;
    }

    private static async Task LiveBsaAsync(string archivePath, string memberPath)
    {
        using var fixture = new AssetFixture();
        var result = await fixture.Service.ReadBsaMemberAsync(
            archivePath,
            memberPath,
            new(MaximumArchiveBytesHashed: 0));
        if (result.Status != Mo2AssetInspectionStatus.Complete)
        {
            throw new InvalidOperationException($"Expected Complete, got {result.Status}: {string.Join("; ", result.Issues.Select(value => $"{value.Code}={value.Detail}"))}");
        }
        True(result.Content.Length > 0);
        True(result.MemberSha256?.Length == 64);
        Equal(Mo2AssetInspectionStatus.Complete, fixture.Service.InspectNif(result.Content.AsMemory(), new()).Status);
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2AssetInspectionCheckResult>.Builder results,
        string name,
        Func<Task> check)
    {
        try
        {
            await check();
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

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate)) throw new InvalidOperationException("Expected matching value.");
    }
}

sealed record Mo2AssetInspectionCheckResult(string Name, Exception? Failure);

sealed class CountingRandomAccessFileFactory : IMo2RandomAccessFileFactory
{
    private readonly IMo2RandomAccessFileFactory _inner = new WindowsRandomAccessFileFactory();

    public int OpenCount { get; private set; }
    public long BytesRead { get; private set; }

    public async Task<IMo2RandomAccessFile> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        OpenCount++;
        return new CountingRandomAccessFile(
            await _inner.OpenReadAsync(path, cancellationToken),
            count => BytesRead += count);
    }

    private sealed class CountingRandomAccessFile(
        IMo2RandomAccessFile inner,
        Action<int> recordRead) : IMo2RandomAccessFile
    {
        public long Length => inner.Length;
        public Mo2RandomAccessStamp InitialStamp => inner.InitialStamp;

        public async ValueTask<int> ReadAsync(
            long offset,
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(offset, buffer, cancellationToken);
            recordRead(read);
            return read;
        }

        public ValueTask<Mo2RandomAccessStamp> GetCurrentStampAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetCurrentStampAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

sealed class AssetFixture : IDisposable
{
    public AssetFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"grid-mo2-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Service = new(new WindowsRandomAccessFileFactory());
    }

    public string Root { get; }
    public Mo2SkyrimAssetInspectionService Service { get; }

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static byte[] Bsa(string virtualPath, byte[] content, bool compressed = false)
    {
        var normalized = virtualPath.Replace('/', '\\');
        var folder = Encoding.Latin1.GetBytes((Path.GetDirectoryName(normalized) ?? string.Empty) + "\0");
        var file = Encoding.Latin1.GetBytes((Path.GetFileName(normalized) ?? throw new InvalidDataException()) + "\0");
        byte[] stored;
        if (compressed)
        {
            var lz4 = Lz4Frame(content);
            stored = new byte[4 + lz4.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(stored, checked((uint)content.Length));
            lz4.CopyTo(stored, 4);
        }
        else
        {
            stored = content;
        }

        const int headerSize = 36;
        const int folderRecordSize = 24;
        const int fileRecordSize = 16;
        var folderNameBytes = folder.Length;
        var indexLength = headerSize + folderRecordSize + 1 + folderNameBytes + fileRecordSize + file.Length;
        var output = new byte[indexLength + stored.Length];
        "BSA\0"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), 105);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8, 4), headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12, 4), compressed ? 0x7u : 0x3u);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), checked((uint)folderNameBytes));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(28, 4), checked((uint)file.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(32, 4), 0x3);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(headerSize, 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(headerSize + 8, 4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(headerSize + 16, 8), headerSize + folderRecordSize);
        var position = headerSize + folderRecordSize;
        output[position++] = checked((byte)folder.Length);
        folder.CopyTo(output, position);
        position += folder.Length;
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(position, 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(position + 8, 4), checked((uint)stored.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(position + 12, 4), checked((uint)indexLength));
        position += fileRecordSize;
        file.CopyTo(output, position);
        stored.CopyTo(output, indexLength);
        return output;
    }

    public static byte[] Nif(params string[] strings)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        WriteUInt32(output, 0x14020007);
        output.WriteByte(1);
        WriteUInt32(output, 12);
        WriteUInt32(output, 1);
        WriteUInt32(output, 100);
        output.WriteByte(0);
        output.WriteByte(0);
        output.WriteByte(0);
        WriteUInt16(output, 1);
        WriteSizedString(output, "NiNode");
        WriteUInt16(output, 0);
        WriteUInt32(output, 4);
        WriteUInt32(output, checked((uint)strings.Length));
        WriteUInt32(output, checked((uint)(strings.Length == 0 ? 0 : strings.Max(value => Encoding.UTF8.GetByteCount(value)))));
        foreach (var value in strings) WriteSizedString(output, value);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0x47524944);
        WriteUInt32(output, 1);
        WriteUInt32(output, 0);
        return output.ToArray();
    }

    public static byte[] Dds(uint width, uint height, uint mipCount, string fourCc, uint dxgiFormat = 0)
    {
        var blockBytes = fourCc == "DXT1" ? 8 : 16;
        long payload = 0;
        var w = width;
        var h = height;
        for (var index = 0; index < Math.Max(1u, mipCount); index++)
        {
            payload += (long)Math.Max(1u, (w + 3) / 4) * Math.Max(1u, (h + 3) / 4) * blockBytes;
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        var header = fourCc == "DX10" ? 148 : 128;
        var result = new byte[checked(header + (int)payload)];
        "DDS "u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), 0x0002100F);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), mipCount);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(80, 4), 4);
        Encoding.ASCII.GetBytes(fourCc).CopyTo(result, 84);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(108, 4), 0x401008);
        if (fourCc == "DX10") BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(128, 4), dxgiFormat);
        return result;
    }

    public static byte[] PexWithReferenceSelection()
    {
        string[] strings =
        [
            "FixtureScript", "Quest", "", "::HatchOriginal_var", "ObjectReference", "::HatchAlternate_var",
            "::SelectedInterior_var", "Int", "HatchOriginal", "HatchAlternate", "SelectedInterior", "OnGameReload",
            "None", "::temp0", "::temp1", "self", "Enable", "Disable", "StorageUtil", "GetIntValue",
            "Fixture.Interior", "::nonevar",
        ];
        var index = strings.Select((value, position) => (value, position))
            .ToDictionary(item => item.value, item => checked((ushort)item.position), StringComparer.Ordinal);
        using var output = new MemoryStream();
        WriteBigEndianUInt32(output, 0xFA57C0DE);
        output.WriteByte(3);
        output.WriteByte(9);
        WriteBigEndianUInt16(output, 1);
        WriteBigEndianUInt64(output, 0);
        WritePexString(output, "FixtureScript.psc");
        WritePexString(output, "fixture");
        WritePexString(output, "fixture");
        WriteBigEndianUInt16(output, checked((ushort)strings.Length));
        foreach (var value in strings) WritePexString(output, value);

        output.WriteByte(1);
        WriteBigEndianUInt64(output, 0);
        WriteBigEndianUInt16(output, 1);
        WriteIndex("FixtureScript");
        WriteIndex("");
        WriteIndex("OnGameReload");
        output.WriteByte(0);
        WriteBigEndianUInt16(output, 6);
        for (ushort line = 100; line < 106; line++) WriteBigEndianUInt16(output, line);
        WriteBigEndianUInt16(output, 0);

        WriteBigEndianUInt16(output, 1);
        WriteIndex("FixtureScript");
        WriteBigEndianUInt32(output, 0);
        WriteIndex("Quest");
        WriteIndex("");
        WriteBigEndianUInt32(output, 0);
        WriteIndex("");

        WriteBigEndianUInt16(output, 3);
        WriteVariable("::HatchOriginal_var", "ObjectReference", 0, null);
        WriteVariable("::HatchAlternate_var", "ObjectReference", 0, null);
        WriteVariable("::SelectedInterior_var", "Int", 3, 0);

        WriteBigEndianUInt16(output, 3);
        WriteAutoProperty("HatchOriginal", "ObjectReference", "::HatchOriginal_var");
        WriteAutoProperty("HatchAlternate", "ObjectReference", "::HatchAlternate_var");
        WriteAutoProperty("SelectedInterior", "Int", "::SelectedInterior_var");

        WriteBigEndianUInt16(output, 1);
        WriteIndex("");
        WriteBigEndianUInt16(output, 1);
        WriteIndex("OnGameReload");
        WriteIndex("None");
        WriteIndex("");
        WriteBigEndianUInt32(output, 0);
        output.WriteByte(0);
        WriteBigEndianUInt16(output, 0);
        WriteBigEndianUInt16(output, 2);
        WriteTypedName("::temp0", "ObjectReference");
        WriteTypedName("::temp1", "ObjectReference");
        WriteBigEndianUInt16(output, 6);

        WriteInstruction(28, Id("HatchAlternate"), Id("self"), Id("::temp0"));
        WriteCallMethod("Enable", "::temp0");
        WriteInstruction(28, Id("HatchOriginal"), Id("self"), Id("::temp1"));
        WriteCallMethod("Disable", "::temp1");
        output.WriteByte(25);
        WriteValue(Id("StorageUtil"));
        WriteValue(Id("GetIntValue"));
        WriteValue(Id("::nonevar"));
        WriteValue((3, (object)1));
        WriteValue((2, (object)"Fixture.Interior"));
        WriteInstruction(26, (0, null!));
        return output.ToArray();

        (byte Kind, object Value) Id(string value) => (1, value);
        void WriteIndex(string value) => WriteBigEndianUInt16(output, index[value]);
        void WriteTypedName(string name, string type) { WriteIndex(name); WriteIndex(type); }
        void WriteVariable(string name, string type, byte valueKind, object? value)
        {
            WriteIndex(name); WriteIndex(type); WriteBigEndianUInt32(output, 0); WriteValue((valueKind, value!));
        }
        void WriteAutoProperty(string name, string type, string variable)
        {
            WriteIndex(name); WriteIndex(type); WriteIndex(""); WriteBigEndianUInt32(output, 0); output.WriteByte(4); WriteIndex(variable);
        }
        void WriteInstruction(byte opcode, params (byte Kind, object Value)[] operands)
        {
            output.WriteByte(opcode);
            foreach (var operand in operands) WriteValue(operand);
        }
        void WriteCallMethod(string method, string receiver)
        {
            output.WriteByte(23);
            WriteValue(Id(method));
            WriteValue(Id(receiver));
            WriteValue(Id("::nonevar"));
            WriteValue((3, (object)0));
        }
        void WriteValue((byte Kind, object Value) value)
        {
            output.WriteByte(value.Kind);
            switch (value.Kind)
            {
                case 0: break;
                case 1:
                case 2: WriteIndex((string)value.Value); break;
                case 3: WriteBigEndianUInt32(output, unchecked((uint)(int)value.Value)); break;
                default: throw new InvalidOperationException("Unsupported fixture PEX value.");
            }
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] Lz4Literal(byte[] content)
    {
        using var output = new MemoryStream();
        output.WriteByte((byte)(Math.Min(15, content.Length) << 4));
        if (content.Length >= 15)
        {
            var remaining = content.Length - 15;
            while (remaining >= 255)
            {
                output.WriteByte(255);
                remaining -= 255;
            }

            output.WriteByte(checked((byte)remaining));
        }

        output.Write(content);
        return output.ToArray();
    }

    private static byte[] Lz4Frame(byte[] content)
    {
        var block = Lz4Literal(content);
        using var output = new MemoryStream();
        WriteUInt32(output, 0x184D2204);
        output.WriteByte(0x68); // Version 1, independent blocks, declared content size.
        output.WriteByte(0x40); // 64 KiB maximum block.
        Span<byte> contentSize = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(contentSize, checked((ulong)content.Length));
        output.Write(contentSize);
        output.WriteByte(0); // Header checksum is structurally present.
        WriteUInt32(output, checked((uint)block.Length));
        output.Write(block);
        WriteUInt32(output, 0);
        return output.ToArray();
    }

    private static void WriteSizedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(stream, checked((uint)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBigEndianUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBigEndianUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteBigEndianUInt64(Stream stream, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WritePexString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBigEndianUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }
}
