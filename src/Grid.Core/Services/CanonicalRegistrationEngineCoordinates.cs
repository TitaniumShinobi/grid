using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Stable registration-engine coordinates used to decide whether verified knowledge must be rebuilt.
/// </summary>
public static class CanonicalRegistrationEngineCoordinates
{
    public const string EngineId = "grid.registration-engine";
    public const string EngineVersion = "2";

    public const string LocationRelationshipMethodId = "grid.location-relationship.registration.v1";
    public const string LocationRelationshipMethodVersion = "1";

    public static readonly ContentDigest LocationRelationshipSemanticContractDigest =
        ContentDigest.ComputeSha256("grid.location-relationship.registration.v1:contained-by:exact-native-and-primary-name"u8);
}
