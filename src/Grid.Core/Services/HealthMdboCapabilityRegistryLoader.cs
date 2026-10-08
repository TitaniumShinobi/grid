using System.Reflection;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Startup;

namespace Grid.Core.Services;

/// <summary>Loads Contract 2 MDBO capabilities from the scripts/health registry (authoritative).</summary>
public static class HealthMdboCapabilityRegistryLoader
{
    public const string RelativeManifestPath = "scripts/health/mdbo-capabilities.v1.json";
    private const string EmbeddedResourceName = "Grid.Core.Contracts.canonical-relationship-registration-capabilities.v1.json";

    public static RelationshipRegistrationCapabilityManifest LoadAuthoritative()
    {
        if (TryLoadHealthManifest(out var health))
            return health;
        return LoadEmbeddedMirror();
    }

    public static bool TryLoadHealthManifest(out RelationshipRegistrationCapabilityManifest manifest)
    {
        manifest = null!;
        var path = ResolveManifestPath();
        if (path is null || !File.Exists(path))
            return false;
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("capabilities", out var capabilitiesElement))
        {
            var capabilities = JsonSerializer.Deserialize<RelationshipRegistrationCapability[]>(
                capabilitiesElement.GetRawText(), CanonicalRegistrationEncoding.Json)
                ?? throw new InvalidDataException("Health MDBO capability manifest is empty.");
            var healthManifest = new RelationshipRegistrationCapabilityManifest(
                document.RootElement.GetProperty("schemaVersion").GetInt32(),
                document.RootElement.GetProperty("ownerScope"),
                capabilities,
                document.RootElement.TryGetProperty("modelProposalBoundary", out var boundary)
                    ? boundary
                    : default);
            ValidateAgainstEmbeddedMirror(healthManifest);
            manifest = healthManifest;
            return true;
        }

        var legacyManifest = JsonSerializer.Deserialize<RelationshipRegistrationCapabilityManifest>(json, CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Health MDBO capability manifest is empty.");
        ValidateAgainstEmbeddedMirror(legacyManifest);
        manifest = legacyManifest;
        return true;
    }

    public static string? ResolveManifestPath()
    {
        var repository = StartupRegressionPolicy.TryResolveRepositoryRoot();
        if (!string.IsNullOrWhiteSpace(repository))
        {
            var candidate = Path.Combine(repository, RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
        }

        var probe = AppContext.BaseDirectory;
        for (var depth = 0; depth < 12; depth++)
        {
            var candidate = Path.Combine(probe, RelativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(probe)?.FullName;
            if (string.IsNullOrWhiteSpace(parent))
                break;
            probe = parent;
        }

        return null;
    }

    private static RelationshipRegistrationCapabilityManifest LoadEmbeddedMirror()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidDataException("Relationship registration capability mirror is missing.");
        return JsonSerializer.Deserialize<RelationshipRegistrationCapabilityManifest>(stream, CanonicalRegistrationEncoding.Json)
            ?? throw new InvalidDataException("Relationship registration capability mirror is empty.");
    }

    private static void ValidateAgainstEmbeddedMirror(RelationshipRegistrationCapabilityManifest health)
    {
        var mirror = LoadEmbeddedMirror();
        if (health.Capabilities.Length != mirror.Capabilities.Length)
            throw new InvalidDataException("Health MDBO registry capability count does not match the embedded mirror.");
        var mirrorById = mirror.Capabilities.ToDictionary(c => c.CapabilityId, StringComparer.Ordinal);
        foreach (var capability in health.Capabilities)
        {
            if (!mirrorById.TryGetValue(capability.CapabilityId, out var expected))
                throw new InvalidDataException("Health MDBO registry exposes unknown capability: " + capability.CapabilityId);
            if (!string.Equals(capability.Realization, expected.Realization, StringComparison.Ordinal) ||
                !string.Equals(capability.SemanticParent, expected.SemanticParent, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Health MDBO registry diverges from mirror for: " + capability.CapabilityId);
            }
        }
    }
}
