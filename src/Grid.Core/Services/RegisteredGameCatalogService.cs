using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class RegisteredGameCatalogService(IGridCatalogService inner,IGameInstallationRegistrationStore store):IGridCatalogService
{
    public async Task<GridCatalogSnapshot> GetCatalogAsync(CancellationToken cancellationToken=default)
    {
        var catalogTask=inner.GetCatalogAsync(cancellationToken);var registrationsTask=store.LoadAsync(cancellationToken);
        await Task.WhenAll(catalogTask,registrationsTask).ConfigureAwait(false);
        var catalog=await catalogTask.ConfigureAwait(false);var loaded=await registrationsTask.ConfigureAwait(false);
        var games=catalog.Games.ToBuilder();var evidence=new List<string>();
        for(var index=0;index<games.Count;index++)
        {
            var game=games[index];var matches=loaded.Registrations.Where(value=>value.GameId==game.Id).ToArray();if(matches.Length==0)continue;
            var adapters=game.Adapters.ToBuilder();var installations=game.Installations.ToBuilder();
            foreach(var registration in matches)
            {
                if(!adapters.Any(value=>value.Id==registration.AdapterId))adapters.Add(new(registration.AdapterId,"Provider discovery"));
                var available=Directory.Exists(registration.InstallRoot)&&File.Exists(registration.ExecutablePath);
                installations.Add(new(registration.InstallationId,registration.GameId,registration.AdapterId,registration.DisplayName,
                    InstallationKind.External,WorkspaceAccessMode.ReadOnly,game.Capabilities,[],[],[],
                    new(available?HealthLevel.Unknown:HealthLevel.Warning,available?"Registered installation":"Installation unavailable",[]),
                    ProfileFeature.None,
                    new(registration.InstallRoot,available?InstallationAvailability.Available:InstallationAvailability.Missing,
                        available?$"{registration.Edition} · {registration.ProviderId}":"The registered path is currently unavailable.",
                        InstallationProvenanceKind.ConnectedReference,registration.ReferenceId),[]));
                evidence.Add($"{registration.InstallationId.Value}:{registration.InstallRoot}:{available}");
            }
            games[index]=game with{Adapters=adapters.ToImmutable(),Installations=installations.ToImmutable()};
        }
        var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',evidence.Order())))).ToLowerInvariant()[..16];
        return new($"{catalog.Revision}+games.{hash}",CatalogSourceKind.Adapter,games);
    }
}
