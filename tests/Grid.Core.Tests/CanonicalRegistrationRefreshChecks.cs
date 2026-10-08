using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

internal static class CanonicalRegistrationRefreshChecks
{
    public static int Run()
    {
        var checks = 0;
        var empty = CanonicalLocationRelationshipRegistrationEngine.Resolve(
            CanonicalCatalogPackageImportChecks.CreateProjectionPackage("registration-refresh", 0x71).Payload,
            []);
        Assert(empty.Admissions.IsEmpty && empty.Rejections.IsEmpty,
            "No claims produces no admitted or rejected relationships.");
        checks++;

        Assert(CanonicalRegistrationEngineCoordinates.EngineVersion == "2",
            "Registration engine version coordinate is pinned.");
        checks++;

        Assert(CanonicalRegistrationEngineCoordinates.LocationRelationshipMethodId ==
               "grid.location-relationship.registration.v1",
            "Location relationship registration method id is stable.");
        checks++;

        Console.WriteLine($"PASS: {checks} registration refresh / location resolution checks.");
        return checks;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
