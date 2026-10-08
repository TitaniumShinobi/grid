using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed record RegistrationRefreshFingerprint(string EngineVersion, string ChecklistDigest, string RuleDigest,
    string[] SourceDigests, string[] AdapterVersions, string[] ContextDigests);
public sealed record RegistrationCandidateRefreshPlan(bool IsStale, string[] Reasons, string[] Stages, string PublicationState);

/// <summary>Pure future-refresh handoff. This service has no file, process, registration or publication side effects.</summary>
public static class CanonicalRegistrationRefreshPlanner
{
    public static RegistrationCandidateRefreshPlan Plan(RegistrationRefreshFingerprint? previous, RegistrationRefreshFingerprint current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var reasons = new List<string>();
        if (previous is null) reasons.Add("no-verified-generation");
        else
        {
            if (previous.EngineVersion != current.EngineVersion) reasons.Add("engine-changed");
            if (previous.ChecklistDigest != current.ChecklistDigest) reasons.Add("checklist-changed");
            if (previous.RuleDigest != current.RuleDigest) reasons.Add("rules-changed");
            if (!Same(previous.SourceDigests, current.SourceDigests)) reasons.Add("sources-changed");
            if (!Same(previous.AdapterVersions, current.AdapterVersions)) reasons.Add("adapters-changed");
            if (!Same(previous.ContextDigests, current.ContextDigests)) reasons.Add("applicability-changed");
        }
        return new(reasons.Count > 0, reasons.ToArray(), reasons.Count == 0 ? ["reuse-verified-generation"] :
            ["register-candidate", "verify-canonical-candidate", "prepare-candidate", "verify-prepared-candidate",
                "stage-generation-and-binding", "publish-atomic-pointer", "retain-previous-generation-for-rollback", "write-publication-receipt"],
            CanonicalRegistrationEncoding.NotPublished);
    }
    private static bool Same(string[] left, string[] right) => CanonicalRegistrationEncoding.Ordered(left).SequenceEqual(CanonicalRegistrationEncoding.Ordered(right));
}
