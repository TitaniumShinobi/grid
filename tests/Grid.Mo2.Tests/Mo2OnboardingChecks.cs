using System.Collections.Immutable;
using Grid.Core.Services;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

sealed record Mo2OnboardingCheckResult(string Name, Exception? Failure);

static class Mo2OnboardingChecks
{
    public static async Task<ImmutableArray<Mo2OnboardingCheckResult>> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<Mo2OnboardingCheckResult>();
        var connected = new Mo2OnboardingState(Mo2OnboardingPhase.Canceled, Mo2OnboardingDisposition.Incomplete,
            null, null, [], null, [], "Onboarding canceled; the reconnectable reference was retained.", true);
        if (!connected.ConnectionPersisted || connected.Disposition != Mo2OnboardingDisposition.Incomplete)
            results.Add(new("onboarding cancellation retains connection state", new InvalidOperationException()));
        else results.Add(new("onboarding cancellation retains connection state", null));

        try
        {
            using var fixture = new Fixture();
            var app = fixture.CreateApplication("Authorized-App");
            var content = fixture.CreateContentRoot("Authorized-Content");
            var instance = fixture.CreateGlobal("Authorized-Instance", content);
            var discovery = new Mo2DiscoveryService(
                new StaticEvidenceSource([]),
                fixture.ReferenceStore,
                fixture.Paths);
            var connection = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
            var coordinator = new Mo2OnboardingCoordinator(
                discovery,
                connection,
                _ => fixture.Validator,
                new Mo2SessionPathAuthorization(fixture.Paths),
                new Mo2SessionModsPathAuthorization(fixture.Paths),
                new Mo2SessionContentPathAuthorization(fixture.Paths),
                new Mo2SessionExecutablePathAuthorization(fixture.Paths),
                fixture.Paths);
            var validationRequest = await fixture.AuthorizedRequestAsync(app, instance);
            var state = await coordinator.ConnectAsync(new(validationRequest, Fixture.Mo2Adapter, "Authorized"));
            if (state.Reference is null || !state.ConnectionPersisted)
                throw new InvalidOperationException("The reference was not persisted.");
            var revalidated = await coordinator.CreateAuthorizedValidator(state.Reference).ValidateAsync(
                new(app, instance, Fixture.SkyrimGame));
            if (!revalidated.CanConnect)
                throw new InvalidOperationException("Reference-scoped session grants were not reused.");
            var unrelated = fixture.Reference(app, instance, "Unrelated", unique: 77);
            var unrelatedValidation = await coordinator.CreateAuthorizedValidator(unrelated).ValidateAsync(
                new(app, instance, Fixture.SkyrimGame));
            if (unrelatedValidation.CanConnect)
                throw new InvalidOperationException("An exact session grant leaked to another reference identity.");
            results.Add(new("onboarding reuses exact grants only for the connected reference", null));
        }
        catch (Exception exception) { results.Add(new("onboarding reuses exact grants only for the connected reference", exception)); }

        try
        {
            using var fixture = new Fixture();
            var app = fixture.CreateApplication("Disconnect-App");
            var instance = fixture.CreateGlobal("Disconnect-Instance");
            var connection = new Mo2ConnectionService(fixture.Validator, fixture.ReferenceStore, fixture.Paths);
            var request = await fixture.AuthorizedRequestAsync(app, instance);
            var connectedResult = await connection.ConnectAsync(new(request, Fixture.Mo2Adapter, "Disconnect fixture"));
            if (!connectedResult.Succeeded || connectedResult.Reference is null)
                throw new InvalidOperationException("Fixture connection was not persisted.");
            var disconnected = await connection.DisconnectAsync(new(
                connectedResult.Reference.Id,
                connectedResult.Reference.InstallationId));
            var loaded = await fixture.ReferenceStore.LoadAsync();
            if (!disconnected.Succeeded || !loaded.References.IsEmpty || !Directory.Exists(app) || !Directory.Exists(instance))
                throw new InvalidOperationException("Disconnect changed more than the Grid reference or failed to remove it.");
            var repeated = await connection.DisconnectAsync(new(
                connectedResult.Reference.Id,
                connectedResult.Reference.InstallationId));
            if (repeated.Succeeded || repeated.ReferenceWasPresent)
                throw new InvalidOperationException("A stale disconnect identifier did not fail safely.");
            results.Add(new("disconnect removes only Grid's persisted reference and stale IDs fail safely", null));
        }
        catch (Exception exception) { results.Add(new("disconnect removes only Grid's persisted reference and stale IDs fail safely", exception)); }

        try
        {
            using var fixture = new Fixture();
            var service = new Mo2CatalogService(
                new MockGridCatalogService(),
                fixture.ReferenceStore,
                _ => fixture.Validator,
                enforceProductionIsolation: true);
            try
            {
                _ = await service.GetCatalogAsync();
                throw new InvalidOperationException("Production isolation accepted a mock base catalog.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("cannot wrap or mix", StringComparison.Ordinal))
            {
            }
            results.Add(new("production MO2 composition rejects a mock base catalog", null));
        }
        catch (Exception exception) { results.Add(new("production MO2 composition rejects a mock base catalog", exception)); }

        try
        {
            using var fixture = new Fixture();
            var app = fixture.CreateApplication();
            var first = fixture.Reference(app, fixture.CreateGlobal("Broken"), "Broken");
            var second = fixture.Reference(app, fixture.CreateGlobal("Healthy"), "Healthy");
            await fixture.ReferenceStore.SaveAsync([first, second]);
            var service = new Mo2CatalogService(new MockGridCatalogService(), fixture.ReferenceStore,
                reference => reference.Id == first.Id ? throw new IOException("isolated fixture failure") : fixture.Validator);
            var catalog = await service.GetCatalogAsync();
            var installations = catalog.Games.Single(game => game.Id == Fixture.SkyrimGame).Installations;
            if (installations.Count(item => item.Metadata.Provenance == Grid.Core.Models.InstallationProvenanceKind.ConnectedReference) != 2 ||
                !installations.Any(item => item.Id == second.InstallationId))
                throw new InvalidOperationException("One failed reference collapsed another reference.");
            results.Add(new("connected reference failures are isolated", null));
        }
        catch (Exception exception) { results.Add(new("connected reference failures are isolated", exception)); }
        return results.ToImmutable();
    }
}
