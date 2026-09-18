using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2PluginScriptDependencyCheckResult(string Name, Exception? Failure);

static class Mo2PluginScriptDependencyChecks
{
    public static async Task<ImmutableArray<Mo2PluginScriptDependencyCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2PluginScriptDependencyCheckResult>();
        await RunAsync(results, "plugin script dependencies find missing virtual PEX providers", FindsMissingAsync);
        await RunAsync(results, "plugin asset dependencies find exact missing mesh and texture paths", FindsMissingAssetsAsync);
        await RunAsync(results, "plugin dependencies evaluate only the effective winning record", EffectiveRecordWinnerAsync);
        await RunAsync(results, "record conflict provenance is deterministic and bounded", RecordConflictBoundAsync);
        await RunAsync(results, "record catalog is complete, deterministic, and bounded", RecordCatalogAsync);
        await RunAsync(results, "record catalog streams without retention and preserves its fingerprint", StreamsRecordCatalogAsync);
        await RunAsync(results, "plugin dependencies refuse absence claims without complete right-panel order", IncompleteLoadOrderAsync);
        await RunAsync(results, "plugin script dependencies inspect compressed VMAD records", CompressedAsync);
        await RunAsync(results, "plugin script dependencies refuse bounded plugin overflow", RefusesPluginOverflowAsync);
        await RunAsync(results, "plugin script dependencies report bounded deterministic progress", ReportsProgressAsync);
        return results.ToImmutable();
    }

    private static async Task FindsMissingAssetsAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Wearables.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f4d5241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMO", 0x01000110, 0, false,
                    AssetPath("MODL", @"armor\available.nif"),
                    AssetPath("MOD2", @"armor\missing.nif"),
                    AssetPath("ICON", @"interface\icons\missing.dds"))));
        var result = await Service().InspectAsync(new(
            [new("Wearables.esp", plugin, 0, 0, true)],
            [@"meshes\armor\available.nif"],
            new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(3, result.AssetReferences.Length);
        Equal(2, result.MissingAssets.Length);
        var mesh = result.MissingAssets.Single(value => value.Kind == Mo2PluginAssetReferenceKind.Mesh);
        Equal(@"meshes\armor\missing.nif", mesh.RequiredVirtualPath);
        Equal("MOD2", mesh.Samples.Single().SubrecordSignature);
        var texture = result.MissingAssets.Single(value => value.Kind == Mo2PluginAssetReferenceKind.Texture);
        Equal(@"textures\interface\icons\missing.dds", texture.RequiredVirtualPath);
        Equal("ICON", texture.Samples.Single().SubrecordSignature);
        Equal(0, result.Issues.Length);
    }

    private static async Task FindsMissingAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Encounter.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000100, 0, false,
                    Vmad(Script("PresentScript", StringProperty("Message", "hello")), Script("MissingScript"))),
                Mo2Tes4RecordGraphFixtureBuilder.Record("SCEN", 0x01000101, 0, false,
                    Vmad(Script("MissingScript")))));
        var result = await Service().InspectAsync(new(
            [new("Encounter.esp", plugin, 0, 0, true)],
            [@"scripts\presentscript.pex"],
            new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(3, result.References.Length);
        var missing = result.Missing.Single();
        Equal("Encounter.esp", missing.PluginName);
        Equal("MissingScript", missing.ScriptName);
        Equal(@"scripts\MissingScript.pex", missing.RequiredVirtualPath);
        Equal(2, missing.ReferenceCount);
        Equal(2, missing.Samples.Length);
        Equal(0, result.Issues.Length);
    }

    private static async Task EffectiveRecordWinnerAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var source = await fixture.WritePluginAsync(
            "Source.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000120, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("SourceQuest"),
                    Vmad(Script("LosingMissingScript")),
                    AssetPath("MODL", @"losing\missing.nif"))));
        var patch = await fixture.WritePluginAsync(
            "Patch.esp", ["Skyrim.esm", "Source.esp"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000120, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("PatchedQuest"),
                    Vmad(Script("WinningPresentScript")),
                    AssetPath("MODL", @"winning\present.nif"))));
        var result = await Service().InspectAsync(new(
            [new("Source.esp", source, 0, 0, true), new("Patch.esp", patch, 1, 1, true)],
            [@"scripts\WinningPresentScript.pex", @"meshes\winning\present.nif"],
            new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(1, result.References.Length);
        Equal("Patch.esp", result.References.Single().PluginName);
        Equal("WinningPresentScript", result.References.Single().ScriptName);
        Equal(1, result.AssetReferences.Length);
        Equal("Patch.esp", result.AssetReferences.Single().PluginName);
        Equal(0, result.Missing.Length);
        Equal(0, result.MissingAssets.Length);
        var conflict = result.RecordConflicts.Single();
        Equal("QUST", conflict.RecordSignature);
        Equal("Source.esp", conflict.PreviousPlugin);
        Equal("Patch.esp", conflict.WinningPlugin);
        Equal(0, conflict.PreviousLoadOrder!.Value);
        Equal(1, conflict.WinningLoadOrder!.Value);
        Equal(1, conflict.RecordCount);
        Equal(1, conflict.ContentChangedCount);
        Equal(0, conflict.FlagsChangedCount);
        Equal("Source.esp", conflict.Samples.Single().OriginPlugin);
        Equal((uint)0x120, conflict.Samples.Single().LocalFormId);
        Equal(64, conflict.Samples.Single().PreviousDataSha256.Length);
        Equal(64, conflict.Samples.Single().WinnerDataSha256.Length);
        Equal("SourceQuest", conflict.Samples.Single().PreviousEditorId!);
        Equal("PatchedQuest", conflict.Samples.Single().WinnerEditorId!);
        Equal(2, result.RecordProvenance.Length);
        var sourceProvenance = result.RecordProvenance.Single(value => value.PluginName == "Source.esp");
        Equal(1, sourceProvenance.NewRecordCount);
        Equal(0, sourceProvenance.OverrideRecordCount);
        Equal("SourceQuest", sourceProvenance.Samples.Single().EditorId!);
        var patchProvenance = result.RecordProvenance.Single(value => value.PluginName == "Patch.esp");
        Equal(0, patchProvenance.NewRecordCount);
        Equal(1, patchProvenance.OverrideRecordCount);
        Equal("PatchedQuest", patchProvenance.Samples.Single().EditorId!);
        Equal(2, result.RecordCatalog.Length);
        var sourceCatalog = result.RecordCatalog.Single(value => value.PluginName == "Source.esp");
        Equal("QUST", sourceCatalog.RecordSignature);
        Equal("Source.esp", sourceCatalog.OriginPlugin);
        Equal((uint)0x120, sourceCatalog.LocalFormId);
        True(!sourceCatalog.IsOverride);
        var patchCatalog = result.RecordCatalog.Single(value => value.PluginName == "Patch.esp");
        Equal("Source.esp", patchCatalog.OriginPlugin);
        Equal("PatchedQuest", patchCatalog.EditorId!);
        True(patchCatalog.IsOverride);
        var replay = await Service().InspectAsync(new(
            [new("Patch.esp", patch, 1, 1, true), new("Source.esp", source, 0, 0, true)],
            [@"scripts\WinningPresentScript.pex", @"meshes\winning\present.nif"],
            new()));
        Equal(result.SemanticFingerprint, replay.SemanticFingerprint);
        Equal(
            string.Join('|', result.RecordCatalog.Select(value => $"{value.PluginName}:{value.RecordOffset}")),
            string.Join('|', replay.RecordCatalog.Select(value => $"{value.PluginName}:{value.RecordOffset}")));
    }

    private static async Task RecordCatalogAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Catalog.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000122, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("ThirdQuest")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000120, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("FirstQuest")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000121, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("SecondQuest"))));
        var complete = await Service().InspectAsync(new(
            [new("Catalog.esp", plugin, 0, 0, true)], [], new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, complete.Status);
        Equal(3, complete.RecordCatalog.Length);
        Equal(3L, complete.RecordCatalogEntryCount);
        Equal("FirstQuest", complete.RecordCatalog[0].EditorId!);
        Equal("SecondQuest", complete.RecordCatalog[1].EditorId!);
        Equal("ThirdQuest", complete.RecordCatalog[2].EditorId!);

        var bounded = await Service().InspectAsync(new(
            [new("Catalog.esp", plugin, 0, 0, true)], [],
            new(MaximumRecordCatalogEntries: 2)));
        Equal(Mo2PluginScriptDependencyStatus.Partial, bounded.Status);
        Equal(2, bounded.RecordCatalog.Length);
        Equal(2L, bounded.RecordCatalogEntryCount);
        True(bounded.Issues.Any(value => value.Code == "mo2.record_catalog.entry_limit"));
    }

    private static async Task StreamsRecordCatalogAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Streamed.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000122, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("ThirdQuest")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000120, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("FirstQuest")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000121, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("SecondQuest"))));
        var request = new Mo2PluginScriptDependencyRequest(
            [new("Streamed.esp", plugin, 0, 0, true)], [], new());
        var retained = await Service().InspectAsync(request);
        var streamedEntries = new List<Mo2PluginRecordCatalogEntry>();
        var streamed = await Service().InspectAsync(request, emitRecordCatalog: (entry, _) =>
        {
            streamedEntries.Add(entry);
            return ValueTask.CompletedTask;
        });

        Equal(Mo2PluginScriptDependencyStatus.Complete, streamed.Status);
        Equal(0, streamed.RecordCatalog.Length);
        Equal(3L, streamed.RecordCatalogEntryCount);
        Equal(3, streamedEntries.Count);
        Equal("ThirdQuest", streamedEntries[0].EditorId!);
        Equal(retained.SemanticFingerprint, streamed.SemanticFingerprint);

        streamedEntries.Clear();
        var bounded = await Service().InspectAsync(
            request with { Limits = new(MaximumRecordCatalogEntries: 2) },
            emitRecordCatalog: (entry, _) =>
            {
                streamedEntries.Add(entry);
                return ValueTask.CompletedTask;
            });
        Equal(Mo2PluginScriptDependencyStatus.Partial, bounded.Status);
        Equal(0, bounded.RecordCatalog.Length);
        Equal(2L, bounded.RecordCatalogEntryCount);
        Equal(2, streamedEntries.Count);
        True(bounded.Issues.Any(value => value.Code == "mo2.record_catalog.entry_limit"));
    }

    private static async Task RecordConflictBoundAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var source = await fixture.WritePluginAsync(
            "Source.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000120, 0, false, Vmad(Script("One"))),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000121, 0, false, Vmad(Script("Two")))));
        var patch = await fixture.WritePluginAsync(
            "Patch.esp", ["Skyrim.esm", "Source.esp"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000120, 0, false, Vmad(Script("Three"))),
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000121, 0, false, Vmad(Script("Four")))));
        var latePatch = await fixture.WritePluginAsync(
            "LatePatch.esp", ["Skyrim.esm", "Source.esp", "Patch.esp"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x01000120, 0, false, Vmad(Script("Five")))));
        var result = await Service().InspectAsync(new(
            [new("Source.esp", source, 0, 0, true), new("Patch.esp", patch, 1, 1, true), new("LatePatch.esp", latePatch, 2, 2, true)],
            [],
            new(MaximumRecordConflictGroups: 1)));

        Equal(Mo2PluginScriptDependencyStatus.Partial, result.Status);
        Equal(1, result.RecordConflicts.Length);
        Equal(2, result.RecordConflicts.Single().RecordCount);
        True(result.Issues.Any(value => value.Code == "mo2.record_conflict.group_limit"));
    }

    private static async Task IncompleteLoadOrderAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Unordered.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000120, 0, false,
                    Vmad(Script("CandidateScript")))));
        var result = await Service().InspectAsync(new(
            [new("Unordered.esp", plugin, 0, null, true)], [], new()));

        Equal(Mo2PluginScriptDependencyStatus.Partial, result.Status);
        True(result.Issues.Any(value => value.Code == "mo2.script_dependency.load_order_incomplete"));
    }

    private static async Task CompressedAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Compressed.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f464e49,
                Mo2Tes4RecordGraphFixtureBuilder.Record("INFO", 0x00000200, 0, true,
                    Vmad(Script("CompressedSceneFragment")))));
        var result = await Service().InspectAsync(new(
            [new("Compressed.esp", plugin, 0, 0, true)], [], new()));

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal("CompressedSceneFragment", result.Missing.Single().ScriptName);
    }

    private static async Task RefusesPluginOverflowAsync()
    {
        var result = await Service().InspectAsync(new(
            [new("One.esp", Path.GetFullPath("One.esp"), 0, 0, true),
             new("Two.esp", Path.GetFullPath("Two.esp"), 1, 1, true)],
            [],
            new(MaximumPlugins: 1)));
        Equal(Mo2PluginScriptDependencyStatus.Refused, result.Status);
        True(result.Issues.Any(value => value.Code == "mo2.script_dependency.plugin_limit"));
        Equal(0L, result.PluginsScanned);
    }

    private static async Task ReportsProgressAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var first = await fixture.WritePluginAsync(
            "First.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000120, 0, false)));
        var second = await fixture.WritePluginAsync(
            "Second.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54535551,
                Mo2Tes4RecordGraphFixtureBuilder.Record("QUST", 0x00000121, 0, false)));
        var progress = new List<Mo2PluginScriptDependencyProgress>();

        var result = await Service().InspectAsync(new(
            [new("First.esp", first, 0, 0, true), new("Second.esp", second, 1, 1, true)],
            [], new()), default, (value, _) =>
            {
                progress.Add(value);
                return ValueTask.CompletedTask;
            });

        Equal(Mo2PluginScriptDependencyStatus.Complete, result.Status);
        Equal(4, progress.Count);
        Equal("1:First.esp:Started:0", $"{progress[0].PluginIndex}:{progress[0].PluginName}:{progress[0].Stage}:{progress[0].PluginsScanned}");
        Equal("1:First.esp:Completed:1", $"{progress[1].PluginIndex}:{progress[1].PluginName}:{progress[1].Stage}:{progress[1].PluginsScanned}");
        Equal("2:Second.esp:Started:1", $"{progress[2].PluginIndex}:{progress[2].PluginName}:{progress[2].Stage}:{progress[2].PluginsScanned}");
        Equal("2:Second.esp:Completed:2", $"{progress[3].PluginIndex}:{progress[3].PluginName}:{progress[3].Stage}:{progress[3].PluginsScanned}");
        True(progress.All(value => value.TotalPlugins == 2));
        True(progress[3].RecordHeadersExamined >= progress[1].RecordHeadersExamined);
        True(progress[3].BytesScanned >= progress[1].BytesScanned);
    }

    private static Mo2PluginScriptDependencyInspector Service() => new(new WindowsRandomAccessFileFactory());

    private static byte[] Vmad(params byte[][] scripts)
    {
        using var data = new MemoryStream();
        WriteUInt16(data, 5);
        WriteUInt16(data, 2);
        WriteUInt16(data, checked((ushort)scripts.Length));
        foreach (var script in scripts) data.Write(script);
        return Subrecord("VMAD", data.ToArray());
    }

    private static byte[] Script(string name, params byte[][] properties)
    {
        using var data = new MemoryStream();
        WriteString(data, name);
        data.WriteByte(0);
        WriteUInt16(data, checked((ushort)properties.Length));
        foreach (var property in properties) data.Write(property);
        return data.ToArray();
    }

    private static byte[] StringProperty(string name, string value)
    {
        using var data = new MemoryStream();
        WriteString(data, name);
        data.WriteByte(2);
        data.WriteByte(1);
        WriteString(data, value);
        return data.ToArray();
    }

    private static byte[] Subrecord(string signature, byte[] payload)
    {
        using var data = new MemoryStream();
        data.Write(Encoding.ASCII.GetBytes(signature));
        WriteUInt16(data, checked((ushort)payload.Length));
        data.Write(payload);
        return data.ToArray();
    }

    private static byte[] AssetPath(string signature, string path) =>
        Subrecord(signature, Encoding.Latin1.GetBytes(path + "\0"));

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt16(stream, checked((ushort)bytes.Length));
        stream.Write(bytes);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static async Task RunAsync(
        ImmutableArray<Mo2PluginScriptDependencyCheckResult>.Builder results,
        string name,
        Func<Task> action)
    {
        try { await action(); results.Add(new(name, null)); }
        catch (Exception exception) { results.Add(new(name, exception)); }
    }

    private static void True(bool value)
    {
        if (!value) throw new InvalidOperationException("Expected true.");
    }

    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
}
