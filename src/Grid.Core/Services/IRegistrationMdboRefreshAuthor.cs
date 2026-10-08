using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>MDBO registration candidate authorship for a game-specific refresh path.</summary>
public interface IRegistrationMdboRefreshAuthor
{
    GameId GameId { get; }

    Task<RegistrationKnowledgeRefreshContribution?> TryAuthorRebuildAsync(
        RegistrationRefreshContext context,
        RegistrationKnowledgeRefreshObservation observation,
        CanonicalCatalogLoadResult catalog,
        IProgress<RegistrationRefreshProgress>? progress,
        CancellationToken cancellationToken);
}
