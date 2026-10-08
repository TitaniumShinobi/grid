using System.Reflection;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed record RelationshipRegistrationCapability(
    string CapabilityId,
    string CapabilityVersion,
    string[] L1Lineage,
    string SemanticParent,
    string Realization,
    string Sufficiency,
    string VerificationContract);

public sealed record RelationshipRegistrationCapabilityManifest(
    int SchemaVersion,
    JsonElement OwnerScope,
    RelationshipRegistrationCapability[] Capabilities,
    JsonElement ModelProposalBoundary);

public static class CanonicalRelationshipRegistrationCapabilities
{
    private const string ResourceName = "Grid.Core.Contracts.canonical-relationship-registration-capabilities.v1.json";

    public static RelationshipRegistrationCapabilityManifest Load() =>
        HealthMdboCapabilityRegistryLoader.LoadAuthoritative();

    public static bool IsSelectable(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        return Load().Capabilities.Any(c => c.CapabilityId == capabilityId);
    }

    public static IReadOnlyList<string> PlannerOrder() =>
        CanonicalMdboDependencyPlanner.PlanExecutionOrder(
            CanonicalMdboCapabilityRegistry.RelationshipRegistration,
            Load().Capabilities.Select(c => c.CapabilityId));
}
