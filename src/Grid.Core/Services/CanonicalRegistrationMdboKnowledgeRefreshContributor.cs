using Grid.Core.Models;



namespace Grid.Core.Services;



/// <summary>MDBO registration authority: health registry planning gates game-specific rebuild output.</summary>

public sealed class CanonicalRegistrationMdboKnowledgeRefreshContributor(

    IRegistrationKnowledgeRefreshContributor observationContributor,

    IRegistrationMdboRefreshAuthor refreshAuthor) : IRegistrationKnowledgeRefreshContributor

{

    private readonly IRegistrationKnowledgeRefreshContributor observationContributor =

        observationContributor ?? throw new ArgumentNullException(nameof(observationContributor));

    private readonly IRegistrationMdboRefreshAuthor refreshAuthor =

        refreshAuthor ?? throw new ArgumentNullException(nameof(refreshAuthor));



    public GameId GameId => observationContributor.GameId;



    public RegistrationKnowledgeContributorCapabilities Capabilities => observationContributor.Capabilities;



    public RegistrationKnowledgeRefreshObservation Observe(RegistrationRefreshContext context) =>

        observationContributor.Observe(context);



    public bool IsKnowledgeCurrent(

        RegistrationRefreshContext context,

        RegistrationKnowledgeRefreshObservation observation,

        CanonicalCatalogLoadResult catalog,

        RegistrationRefreshReceipt? lastReceipt) =>

        observationContributor.IsKnowledgeCurrent(context, observation, catalog, lastReceipt);



    public async Task<RegistrationKnowledgeRefreshContribution?> TryRebuildAsync(

        RegistrationRefreshContext context,

        RegistrationKnowledgeRefreshObservation observation,

        CanonicalCatalogLoadResult catalog,

        IProgress<RegistrationRefreshProgress>? progress,

        CancellationToken cancellationToken)

    {

        if (HealthMdboCapabilityRegistryLoader.TryLoadHealthManifest(out _) == false)

            throw new InvalidDataException("Contract 2 registration requires the scripts/health MDBO capability registry.");



        if (refreshAuthor.GameId != GameId)

            throw new InvalidDataException("MDBO refresh author game id does not match the observation contributor.");



        var manifest = CanonicalRelationshipRegistrationCapabilities.Load();

        CanonicalRelationshipRegistrationRealizations.EnsureManifestBindings(manifest);

        _ = CanonicalRelationshipRegistrationMdboComposer.PlannedRegistrationStages();

        _ = CanonicalRelationshipRegistrationMdboComposer.PlanForGoals(

            CanonicalRelationshipRegistrationMdboComposer.LocationContainmentGoalCapability);



        var contribution = await refreshAuthor.TryAuthorRebuildAsync(

            context, observation, catalog, progress, cancellationToken).ConfigureAwait(false);

        if (contribution is null)

            return null;

        if (contribution.Package.Manifest.ValidationStatus != CatalogValidationStatus.Candidate)

            throw new InvalidDataException("MDBO registration refresh must remain Candidate-ready and NOT_PUBLISHED.");



        return contribution with { CandidateReadyWithoutImport = !context.AllowCandidatePackages };

    }

}

