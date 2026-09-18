using System.Collections.Immutable;
using System.Security.Cryptography;
using Grid.Mo2.Infrastructure;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2Tes4RecordGraphCheckResult(string Name, Exception? Failure);

static class Mo2Tes4RecordGraphChecks
{
    private static readonly Mo2Tes4CanonicalRecordKey World = new("Skyrim.esm", 0x3c);
    private static readonly Mo2Tes4CanonicalRecordKey Cell = new("Skyrim.esm", 0x8e62);
    private static readonly Mo2Tes4CanonicalRecordKey Tree = new("College.esp", 0x28d20e);
    private static readonly Mo2Tes4CanonicalRecordKey Parent = new("College.esp", 0x2da6dc);

    public static async Task<ImmutableArray<Mo2Tes4RecordGraphCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2Tes4RecordGraphCheckResult>();
        await RunAsync(results, "TES4 graph reads compressed override state and traverses links", CompressedGraphAsync);
        await RunAsync(results, "TES4 graph discovers bounded spatial cell references", CellScopeAsync);
        await RunAsync(results, "TES4 graph maps compact light self records", LightPluginAsync);
        await RunAsync(results, "TES4 graph preserves deleted persistent placement", PersistentDeletedAsync);
        await RunAsync(results, "TES4 graph traverses spell effect visual dependencies", SpellEffectVisualGraphAsync);
        await RunAsync(results, "TES4 graph traverses actor package and outfit dependencies", ActorDependencyGraphAsync);
        await RunAsync(results, "TES4 graph rejects truncated actor configuration", MalformedActorConfigurationAsync);
        await RunAsync(results, "TES4 graph reports malformed typed references without inventing targets", MalformedTypedReferenceAsync);
        await RunAsync(results, "TES4 graph refuses record-header budget exhaustion", HeaderLimitAsync);
        return results.ToImmutable();
    }

    private static async Task CompressedGraphAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var inputs = await BuildCollegeFixtureAsync(fixture);
        var request = Request(inputs, [Tree]);
        var result = await Service().CollectAsync(request);

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        var chain = result.Chains.Single(value => Same(value.Target, Tree));
        Equal(2, chain.Records.Length);
        Equal("Patch.esp", chain.Winner!.PluginName);
        True(chain.Winner.IsInitiallyDisabled);
        False(chain.Winner.IsDeleted);
        Equal(Mo2Tes4CellPlacement.Temporary, chain.Winner.Placement);
        True(Same(chain.Winner.Worldspace!, World));
        True(Same(chain.Winner.Cell!, Cell));
        True(Same(chain.Winner.BaseObject!, new("Skyrim.esm", 0x5c070)));
        True(Same(chain.Winner.EnableParent!.Parent, Parent));
        Equal((uint)1, chain.Winner.EnableParent.Flags);
        True(Same(chain.Winner.LinkedReferences.Single().Reference, Parent));
        True(Same(chain.Winner.LinkedReferences.Single().Keyword!, new("Skyrim.esm", 0x1000)));
        Equal(116214f, chain.Winner.Transform!.X);
        Equal(110878f, chain.Winner.Transform.Y);
        Equal(.5f, chain.Winner.Scale!.Value);
        Contains(result.TraversedKeys, value => Same(value, Parent));
        Equal(1, result.Chains.Single(value => Same(value.Target, Parent)).Records.Length);
        var baseObject = result.Chains.Single(value => Same(value.Target, new("Skyrim.esm", 0x5c070))).Winner!;
        Equal("TreePineForestSnow03", baseObject.EditorId!);
        Equal(@"meshes\landscape\trees\treepineforestsnow03.nif", baseObject.ModelPath!);
        foreach (var plugin in result.Plugins)
        {
            var path = inputs.Single(value => value.Name == plugin.Name).CanonicalPath;
            Equal(path, plugin.CanonicalPath);
            Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant(), plugin.RawSha256);
            Equal(64, plugin.RawSha256.Length);
        }
    }

    private static async Task CellScopeAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var inputs = await BuildCollegeFixtureAsync(fixture);
        var scope = new Mo2Tes4CellScopeRequest(World, [Cell], [new(116214, 110878, -7784)], Radius: 128);
        var result = await Service().CollectAsync(Request(inputs, [Cell]) with { CellScope = scope });

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        var cellChain = result.Chains.Single(value => Same(value.Target, Cell));
        Equal(3, cellChain.Records.Length);
        Equal("Patch.esp", cellChain.Winner!.PluginName);
        Contains(result.DiscoveredKeys, value => Same(value, Tree));
        Contains(result.DiscoveredKeys, value => Same(value, Parent));
        False(result.DiscoveredKeys.Any(value => Same(value, new("College.esp", 0x2fffff))));
        True(result.CellScopeDiscoveries.All(value => Same(value.Cell, Cell) && Same(value.Worldspace, World)));
        Equal(result.SemanticFingerprint, (await Service().CollectAsync(Request(inputs.Reverse().ToImmutableArray(), [Cell]) with { CellScope = scope })).SemanticFingerprint);
    }

    private static async Task LightPluginAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var plugin = await fixture.WritePluginAsync(
            "Light.esl", ["Skyrim.esm"], 0x200,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x52464449,
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x01000abc, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x00000001)),
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x7f00dead, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x00000001))));
        var input = new Mo2Tes4PluginInput("Light.esl", plugin, 0, 0, true);
        var target = new Mo2Tes4CanonicalRecordKey("Light.esl", 0xabc);
        var result = await Service().CollectAsync(Request([input], [target]));

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        True(result.Plugins.Single().HasLightFlag);
        True(Same(result.Chains.Single(value => Same(value.Target, target)).Winner!.Key, target));
        False(result.Issues.Any(value => value.Code == "mo2.tes4.light_formid_range"));
        False(result.Issues.Any(value => value.Code == "mo2.tes4.formid_master_index"));
    }

    private static async Task HeaderLimitAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var inputs = await BuildCollegeFixtureAsync(fixture);
        var limits = new Mo2Tes4RecordGraphLimits(MaximumRecordHeaders: 1);
        var result = await Service().CollectAsync(Request(inputs, [Tree]) with { Limits = limits });

        Equal(Mo2Tes4RecordGraphStatus.Refused, result.Status);
        Contains(result.Issues, value => value.Code == "mo2.tes4.record_header_limit");
    }

    private static async Task PersistentDeletedAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var target = new Mo2Tes4CanonicalRecordKey("Persistent.esp", 0x1234);
        var reference = Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x01001234, 0x20, false,
            Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x00000001),
            Mo2Tes4RecordGraphFixtureBuilder.Transform(1, 2, 3));
        var plugin = await fixture.WritePluginAsync(
            "Persistent.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x444c5257,
                Mo2Tes4RecordGraphFixtureBuilder.Group(1, 0x0000003c,
                    Mo2Tes4RecordGraphFixtureBuilder.Group(6, 0x00008e62,
                        Mo2Tes4RecordGraphFixtureBuilder.Group(8, 0x00008e62, reference)))));
        var result = await Service().CollectAsync(Request([new("Persistent.esp", plugin, 0, 0, true)], [target]));

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        var winner = result.Chains.Single(value => Same(value.Target, target)).Winner!;
        True(winner.IsDeleted);
        Equal(Mo2Tes4CellPlacement.Persistent, winner.Placement);
        True(Same(winner.Worldspace!, World));
        True(Same(winner.Cell!, Cell));
    }

    private static async Task SpellEffectVisualGraphAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var spell = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x43324);
        var effect = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x12345);
        var light = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x23456);
        var projectile = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x34567);
        var castingArt = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x45678);
        var hitArt = new Mo2Tes4CanonicalRecordKey("Skyrim.esm", 0x56789);
        var plugin = await fixture.WritePluginAsync(
            "Skyrim.esm", [], 1,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4c455053,
                Mo2Tes4RecordGraphFixtureBuilder.Record("SPEL", 0x00043324, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("Candlelight"),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("EFID", 0x00012345))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4645474d,
                Mo2Tes4RecordGraphFixtureBuilder.Record("MGEF", 0x00012345, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("LightFFSelf"),
                    Mo2Tes4RecordGraphFixtureBuilder.MagicEffectData(
                        castingLight: 0x00023456,
                        projectile: 0x00034567,
                        castingArt: 0x00045678,
                        hitEffectArt: 0x00056789))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4847494c,
                Mo2Tes4RecordGraphFixtureBuilder.Record("LIGH", 0x00023456, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("CandlelightLight"))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4a4f5250,
                Mo2Tes4RecordGraphFixtureBuilder.Record("PROJ", 0x00034567, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("CandlelightProjectile"))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f545241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARTO", 0x00045678, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("CandlelightCastingArt"),
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"meshes\magic\candlelightcasting.nif")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARTO", 0x00056789, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("CandlelightHitArt"),
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"meshes\magic\candlelighthit.nif"))));
        var result = await Service().CollectAsync(Request([new("Skyrim.esm", plugin, 0, 0, true)], [spell]));

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        var spellWinner = result.Chains.Single(value => Same(value.Target, spell)).Winner!;
        var spellReference = spellWinner.RecordReferences.Single();
        Equal(Mo2Tes4RecordReferenceKind.SpellEffect, spellReference.Kind);
        Equal("EFID", spellReference.SubrecordSignature);
        True(Same(spellReference.Target, effect));
        var effectReferences = result.Chains.Single(value => Same(value.Target, effect)).Winner!.RecordReferences;
        Reference(effectReferences, Mo2Tes4RecordReferenceKind.MagicEffectCastingLight, light, 48);
        Reference(effectReferences, Mo2Tes4RecordReferenceKind.MagicEffectProjectile, projectile, 96);
        Reference(effectReferences, Mo2Tes4RecordReferenceKind.MagicEffectCastingArt, castingArt, 116);
        Reference(effectReferences, Mo2Tes4RecordReferenceKind.MagicEffectHitEffectArt, hitArt, 120);
        Equal(@"meshes\magic\candlelightcasting.nif", result.Chains.Single(value => Same(value.Target, castingArt)).Winner!.ModelPath!);
        Equal(result.SemanticFingerprint, (await Service().CollectAsync(Request([new("Skyrim.esm", plugin, 0, 0, true)], [spell]))).SemanticFingerprint);
    }

    private static async Task ActorDependencyGraphAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var actor = new Mo2Tes4CanonicalRecordKey("Encounters.esp", 0x22879a);
        var package = new Mo2Tes4CanonicalRecordKey("Encounters.esp", 0x1001);
        var outfit = new Mo2Tes4CanonicalRecordKey("Encounters.esp", 0x1002);
        var armor = new Mo2Tes4CanonicalRecordKey("Encounters.esp", 0x1004);
        var armature = new Mo2Tes4CanonicalRecordKey("Encounters.esp", 0x1005);
        var plugin = await fixture.WritePluginAsync(
            "Encounters.esp", ["Skyrim.esm"], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x5f43504e,
                Mo2Tes4RecordGraphFixtureBuilder.Record("NPC_", 0x0122879a, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("_SetteWIBardF02"),
                    Mo2Tes4RecordGraphFixtureBuilder.ActorConfiguration(
                        Mo2Tes4ActorTemplateFlags.UseAiPackages |
                        Mo2Tes4ActorTemplateFlags.UseModelAnimation |
                        Mo2Tes4ActorTemplateFlags.UseInventory),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("TPLT", 0x00001234),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("PKID", 0x01001001),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("DOFT", 0x01001002),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("SOFT", 0x01001003))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4b434150,
                Mo2Tes4RecordGraphFixtureBuilder.Record("PACK", 0x01001001, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("BardPerformancePackage"))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x5446544f,
                Mo2Tes4RecordGraphFixtureBuilder.Record("OTFT", 0x01001002, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("BardOutfit"),
                    Mo2Tes4RecordGraphFixtureBuilder.FormArraySubrecord("INAM", 0x01001004))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4f4d5241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMO", 0x01001004, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("BardClothes"),
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("MODL", 0x01001005))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x414d5241,
                Mo2Tes4RecordGraphFixtureBuilder.Record("ARMA", 0x01001005, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("BardClothesAA"))));
        var result = await Service().CollectAsync(Request([new("Encounters.esp", plugin, 0, 0, true)], [actor]));

        Equal(Mo2Tes4RecordGraphStatus.Complete, result.Status);
        var actorWinner = result.Chains.Single(value => Same(value.Target, actor)).Winner!;
        Equal(
            Mo2Tes4ActorTemplateFlags.UseAiPackages |
            Mo2Tes4ActorTemplateFlags.UseModelAnimation |
            Mo2Tes4ActorTemplateFlags.UseInventory,
            actorWinner.ActorTemplateFlags!.Value);
        var actorReferences = actorWinner.RecordReferences;
        Reference(actorReferences, Mo2Tes4RecordReferenceKind.ActorPackage, package);
        Reference(actorReferences, Mo2Tes4RecordReferenceKind.ActorDefaultOutfit, outfit);
        Reference(result.Chains.Single(value => Same(value.Target, outfit)).Winner!.RecordReferences, Mo2Tes4RecordReferenceKind.OutfitItem, armor);
        Reference(result.Chains.Single(value => Same(value.Target, armor)).Winner!.RecordReferences, Mo2Tes4RecordReferenceKind.ArmorArmature, armature);
        Contains(result.TraversedKeys, value => Same(value, armature));
    }

    private static async Task MalformedTypedReferenceAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var spell = new Mo2Tes4CanonicalRecordKey("Broken.esp", 0x1000);
        var plugin = await fixture.WritePluginAsync(
            "Broken.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x4c455053,
                Mo2Tes4RecordGraphFixtureBuilder.Record("SPEL", 0x00001000, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.RawSubrecord("EFID", 1, 2, 3))));
        var result = await Service().CollectAsync(Request([new("Broken.esp", plugin, 0, 0, true)], [spell]));

        Equal(Mo2Tes4RecordGraphStatus.Partial, result.Status);
        Equal(0, result.Chains.Single(value => Same(value.Target, spell)).Winner!.RecordReferences.Length);
        Contains(result.Issues, value => value.Code == "mo2.tes4.reference_subrecord_size");
    }

    private static async Task MalformedActorConfigurationAsync()
    {
        using var fixture = new Mo2Tes4RecordGraphFixtureBuilder();
        var actor = new Mo2Tes4CanonicalRecordKey("Broken.esp", 0x1000);
        var plugin = await fixture.WritePluginAsync(
            "Broken.esp", [], 0,
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x5f43504e,
                Mo2Tes4RecordGraphFixtureBuilder.Record("NPC_", 0x00001000, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.RawSubrecord("ACBS", new byte[18]))));
        var result = await Service().CollectAsync(Request([new("Broken.esp", plugin, 0, 0, true)], [actor]));

        Equal(Mo2Tes4RecordGraphStatus.Partial, result.Status);
        True(result.Chains.Single(value => Same(value.Target, actor)).Winner!.ActorTemplateFlags is null);
        Contains(result.Issues, value => value.Code == "mo2.tes4.npc_acbs_size");
    }

    private static async Task<ImmutableArray<Mo2Tes4PluginInput>> BuildCollegeFixtureAsync(Mo2Tes4RecordGraphFixtureBuilder fixture)
    {
        var skyrim = await fixture.WritePluginAsync(
            "Skyrim.esm", [], 1,
            WorldGroup(
                Mo2Tes4RecordGraphFixtureBuilder.Record("WRLD", 0x0000003c, 0, false, Mo2Tes4RecordGraphFixtureBuilder.EditorId("Tamriel")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("CELL", 0x00008e62, 0, false, Mo2Tes4RecordGraphFixtureBuilder.EditorId("WinterholdCollegeExterior"))),
            Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x54415453,
                Mo2Tes4RecordGraphFixtureBuilder.Record("STAT", 0x0005c070, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.EditorId("TreePineForestSnow03"),
                    Mo2Tes4RecordGraphFixtureBuilder.ModelPath(@"meshes\landscape\trees\treepineforestsnow03.nif"))));
        var college = await fixture.WritePluginAsync(
            "College.esp", ["Skyrim.esm"], 0,
            WorldGroup(
                Mo2Tes4RecordGraphFixtureBuilder.Record("CELL", 0x00008e62, 0, true, Mo2Tes4RecordGraphFixtureBuilder.EditorId("WinterholdCollegeExterior")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x0128d20e, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x0005c070),
                    Mo2Tes4RecordGraphFixtureBuilder.Transform(116214, 110878, -7784)),
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x012da6dc, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x000f13c4),
                    Mo2Tes4RecordGraphFixtureBuilder.Transform(116220, 110880, -7780)),
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x012fffff, 0, false,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x000f13c4),
                    Mo2Tes4RecordGraphFixtureBuilder.Transform(130000, 130000, 0))));
        var patch = await fixture.WritePluginAsync(
            "Patch.esp", ["Skyrim.esm", "College.esp"], 0,
            WorldGroup(
                Mo2Tes4RecordGraphFixtureBuilder.Record("CELL", 0x00008e62, 0, true, Mo2Tes4RecordGraphFixtureBuilder.EditorId("WinterholdCollegeExterior")),
                Mo2Tes4RecordGraphFixtureBuilder.Record("REFR", 0x0128d20e, 0x800, true,
                    Mo2Tes4RecordGraphFixtureBuilder.FormSubrecord("NAME", 0x0005c070),
                    Mo2Tes4RecordGraphFixtureBuilder.EnableParent(0x012da6dc, 1),
                    Mo2Tes4RecordGraphFixtureBuilder.LinkedReference(0x00001000, 0x012da6dc),
                    Mo2Tes4RecordGraphFixtureBuilder.Transform(116214, 110878, -7784, 1, 2, 3),
                    Mo2Tes4RecordGraphFixtureBuilder.Scale(.5f))));
        return
        [
            new("Skyrim.esm", skyrim, 0, 0, true),
            new("College.esp", college, 1, 1, true),
            new("Patch.esp", patch, 2, 2, true),
        ];
    }

    private static byte[] WorldGroup(params byte[][] records)
    {
        var worldRecord = records.FirstOrDefault(value => value.AsSpan(0, 4).SequenceEqual("WRLD"u8));
        var cellRecord = records.FirstOrDefault(value => value.AsSpan(0, 4).SequenceEqual("CELL"u8));
        var references = records.Where(value => !ReferenceEquals(value, worldRecord) && !ReferenceEquals(value, cellRecord)).ToArray();
        var temporary = Mo2Tes4RecordGraphFixtureBuilder.Group(9, 0x00008e62, references);
        var cellChildren = Mo2Tes4RecordGraphFixtureBuilder.Group(6, 0x00008e62, temporary);
        var worldChildren = Mo2Tes4RecordGraphFixtureBuilder.Group(1, 0x0000003c,
            [.. (cellRecord is null ? Array.Empty<byte[]>() : [cellRecord]), cellChildren]);
        return Mo2Tes4RecordGraphFixtureBuilder.Group(0, 0x444c5257,
            [.. (worldRecord is null ? Array.Empty<byte[]>() : [worldRecord]), worldChildren]);
    }

    private static Mo2Tes4RecordGraphCollector Service() => new(new WindowsRandomAccessFileFactory());
    private static Mo2Tes4RecordGraphRequest Request(ImmutableArray<Mo2Tes4PluginInput> plugins, ImmutableArray<Mo2Tes4CanonicalRecordKey> targets) => new(plugins, targets, new());
    private static bool Same(Mo2Tes4CanonicalRecordKey left, Mo2Tes4CanonicalRecordKey right) => left.LocalFormId == right.LocalFormId && left.OriginPlugin.Equals(right.OriginPlugin, StringComparison.OrdinalIgnoreCase);
    private static void Reference(ImmutableArray<Mo2Tes4RecordReference> references, Mo2Tes4RecordReferenceKind kind, Mo2Tes4CanonicalRecordKey target, int? recordDataOffset = null)
    {
        Contains(references, value => value.Kind == kind && Same(value.Target, target) && (recordDataOffset is null || value.RecordDataOffset == recordDataOffset));
    }
    private static async Task RunAsync(ImmutableArray<Mo2Tes4RecordGraphCheckResult>.Builder results, string name, Func<Task> action)
    {
        try { await action(); results.Add(new(name, null)); }
        catch (Exception exception) { results.Add(new(name, exception)); }
    }
    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true."); }
    private static void False(bool value) => True(!value);
    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
    private static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate)) throw new InvalidOperationException("Expected a matching item.");
    }
}
