namespace Grid.Core.Services;

/// <summary>Runtime view of Contract 2 capabilities loaded from scripts/health/mdbo-capabilities.v1.json.</summary>
public sealed class CanonicalMdboCapabilityRegistry
{
    public const string RegistrationEngineRoot = "grid.registration-engine";

    private readonly Dictionary<string, RelationshipRegistrationCapability> capabilitiesById;

    private CanonicalMdboCapabilityRegistry(IEnumerable<RelationshipRegistrationCapability> capabilities)
    {
        capabilitiesById = new Dictionary<string, RelationshipRegistrationCapability>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            if (!capabilitiesById.TryAdd(capability.CapabilityId, capability))
                throw new InvalidDataException("Duplicate MDBO capability id: " + capability.CapabilityId);
        }
        foreach (var capability in capabilitiesById.Values)
            ValidateParent(capability);
    }

    public static CanonicalMdboCapabilityRegistry RelationshipRegistration { get; } =
        new(CanonicalRelationshipRegistrationCapabilities.Load().Capabilities);

    public IReadOnlyCollection<RelationshipRegistrationCapability> Capabilities => capabilitiesById.Values;

    public RelationshipRegistrationCapability Require(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        if (!capabilitiesById.TryGetValue(capabilityId, out var capability))
            throw new InvalidDataException("MDBO capability is not registered: " + capabilityId);
        return capability;
    }

    public bool IsRegistered(string capabilityId) =>
        !string.IsNullOrWhiteSpace(capabilityId) && capabilitiesById.ContainsKey(capabilityId);

    public bool TryGetDependencyCapabilityId(RelationshipRegistrationCapability capability, out string? dependencyId)
    {
        dependencyId = null;
        var parent = capability.SemanticParent;
        if (string.IsNullOrWhiteSpace(parent) ||
            string.Equals(parent, RegistrationEngineRoot, StringComparison.Ordinal))
            return false;
        if (!capabilitiesById.ContainsKey(parent))
            throw new InvalidDataException(
                $"MDBO capability '{capability.CapabilityId}' references unknown semantic parent '{parent}'.");
        dependencyId = parent;
        return true;
    }

    private void ValidateParent(RelationshipRegistrationCapability capability)
    {
        var parent = capability.SemanticParent;
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException("MDBO capability lacks semanticParent: " + capability.CapabilityId);
        if (string.Equals(parent, RegistrationEngineRoot, StringComparison.Ordinal))
            return;
        if (!capabilitiesById.ContainsKey(parent))
            throw new InvalidDataException(
                $"MDBO capability '{capability.CapabilityId}' semantic parent '{parent}' is not registered.");
    }
}
