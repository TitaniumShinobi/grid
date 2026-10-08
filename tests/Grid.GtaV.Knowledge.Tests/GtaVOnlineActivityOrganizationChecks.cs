using Grid.Core.Models;
using Grid.GtaV.Knowledge;

internal static class GtaVOnlineActivityOrganizationChecks
{
    public static Task<int> RunAsync()
    {
        var source = "struct<8> func_19770(int i)\n{\nswitch(i){case 0: StringCopy(&Var0, \"AbCd0\", 32); break;\ncase 1: StringCopy(&Var0, \"AbCd1\", 32); break;\ncase 12: StringCopy(&Var0, \"AbCdC\", 32); break;\ndefault: StringCopy(&Var0, \"DoNotAdmit\", 32); break;} }\nchar* func_19771()\n{ return \"OtherRoot\"; }";
        string? Parse(string member) => GtaVPresentationCorpusIndex.ParseGeneratedLocale(source, "common.rpf!/data/ugc/" + member);
        if (Parse("abcd0_00.ugc") != "en-US" || Parse("abcd1_00.ugc") != "fr-FR" || Parse("abcdc_00.ugc") != "zh-CN") throw new InvalidOperationException("Explicit reference language branches must preserve locale/case-insensitive resource lookup.");
        if (Parse("DoNotAdmit_00.ugc") is not null || Parse("abcd0_01.ugc") is not null || Parse("invented0_00.ugc") is not null) throw new InvalidOperationException("Unknown resource/revision/default cannot imply English locale.");
        var fixture = GtaVPresentationFrozenFixture.Load();
        if (fixture is null) return Task.FromResult(2);
        var batch = new GtaVOnlineActivityOrganizationSecondaryAssertionAdapter(ContentDigest.ComputeSha256("mission-presentation-focused"u8), fixture.Index).Extract(fixture.Origin.Payload, fixture.Scope);
        if (batch.TerminologyAssertions.Length != 52 || batch.TerminologyAssertions.GroupBy(x => x.LanguageTag).Count() != 13 || batch.TerminologyAssertions.Count(x => x.LanguageTag == "en-US") != 4 || batch.SemanticClassifications.Count(x => x.RoleId == CanonicalProjectionSemantics.SelectorPlayerAddressable) != 4) throw new InvalidOperationException("Frozen generated registry must preserve 52 identities across 13 explicit locales with 4 English options.");
        if (batch.RelationshipAssertions.Length != 15 || batch.SemanticClassifications.Count(x => x.RoleId == CanonicalProjectionSemantics.MissionPlaylist) != 3) throw new InvalidOperationException("Frozen playlist membership must preserve all 15 ordered entries and 3 organizations.");
        if (fixture.Index.GeneratedTextDiagnostics.Count(x => x.Reason == "non-English-text-quality-unverified") != 48) throw new InvalidOperationException("All non-English generated source literals must retain explicit unverified text-quality diagnostics.");
        fixture.AssertMixed(batch);
        var payload = fixture.Apply(batch);
        if (!payload.KnowledgeRecords.SequenceEqual(fixture.Origin.Payload.KnowledgeRecords) || fixture.Origin.Payload.TerminologyAssertions.Any(x => !payload.TerminologyAssertions.Contains(x))) throw new InvalidOperationException("Mission presentation changed established identities or historical und names.");
        var leaves = fixture.Project(payload, KnowledgeKind.MissionQuest);
        if (leaves.Select(x => x.KnowledgeRecordId).Distinct().Count() != 4) throw new InvalidOperationException("Frozen English Mission projection must expose exactly four options.");
        Console.WriteLine("PASS  Frozen Mission presentation: 52 locale assertions / 4 English options, 15 ordered playlist edges, identities preserved.");
        return Task.FromResult(7);
    }
}
