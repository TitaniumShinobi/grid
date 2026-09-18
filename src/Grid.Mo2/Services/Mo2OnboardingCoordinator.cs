using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2OnboardingCoordinator(
    IMo2DiscoveryService discovery,
    IMo2ConnectionService connections,
    Func<Mo2InstallationReference, IMo2InstallationValidator> validatorFactory,
    IMo2SessionPathAuthorization profileAuthorizations,
    IMo2ModsPathAuthorization modAuthorizations,
    IMo2SessionContentPathAuthorization contentAuthorizations,
    IMo2ExecutablePathAuthorization executableAuthorizations,
    IMo2PathCanonicalizer paths)
{
    private readonly object authorizationGate = new();
    private readonly Dictionary<InstallationReferenceId, HashSet<string>> authorizedConfiguredPaths = [];
    private Mo2OnboardingState state = new(
        Mo2OnboardingPhase.DetectOrBrowse, Mo2OnboardingDisposition.Incomplete, null, null, [], null, [],
        "Detect an MO2 installation or browse to one.", false);

    public Mo2OnboardingState State => state;

    public IMo2InstallationValidator CreateAuthorizedValidator(Mo2InstallationReference reference) =>
        new AuthorizedValidator(this, reference);

    public void RegisterExactAuthorization(InstallationReferenceId referenceId, string userSelectedExactPath)
    {
        if (!paths.TryCanonicalize(userSelectedExactPath, out var canonical, out _))
        {
            throw new UnauthorizedAccessException("The selected authorization path is not a canonical local path.");
        }

        lock (authorizationGate)
        {
            if (!authorizedConfiguredPaths.TryGetValue(referenceId, out var authorizations))
            {
                authorizations = new(StringComparer.OrdinalIgnoreCase);
                authorizedConfiguredPaths.Add(referenceId, authorizations);
            }

            authorizations.Add(canonical);
        }
    }

    public Task<Mo2DiscoveryResult> DetectAsync(Mo2DiscoveryOptions options, CancellationToken cancellationToken = default) =>
        discovery.DiscoverAsync(options, cancellationToken);

    public async Task<Mo2OnboardingState> ConnectAsync(Mo2ConnectionRequest request, CancellationToken cancellationToken = default)
    {
        state = state with { Phase = Mo2OnboardingPhase.ValidateAndConnect, Detail = "Validating and connecting." };
        var result = await connections.ConnectAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Reference is null)
            return state = state with { Phase = Mo2OnboardingPhase.Failed, Validation = result.Validation, Detail = result.Issues.FirstOrDefault()?.Message ?? "Connection failed." };
        foreach (var authorizedPath in request.Validation.EffectiveAuthorizedConfiguredPaths)
        {
            RegisterExactAuthorization(result.Reference.Id, authorizedPath);
        }
        return await ResumeAsync(result.Reference, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Mo2DisconnectResult> DisconnectAsync(
        Mo2DisconnectRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await connections.DisconnectAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            profileAuthorizations.Revoke(request.ReferenceId);
            modAuthorizations.Revoke(request.ReferenceId);
            contentAuthorizations.Revoke(request.ReferenceId);
            executableAuthorizations.Revoke(request.ReferenceId);
            lock (authorizationGate)
            {
                authorizedConfiguredPaths.Remove(request.ReferenceId);
            }
        }

        return result;
    }

    public async Task<Mo2OnboardingState> ResumeAsync(Mo2InstallationReference reference, CancellationToken cancellationToken = default)
    {
        var validation = await validatorFactory(reference).ValidateAsync(
            new(Path.GetDirectoryName(reference.ExecutablePath), reference.InstanceDirectory, reference.GameId,
                GetAuthorizations(reference.Id)), cancellationToken).ConfigureAwait(false);
        var required = ExactRequirements(validation);
        state = new(
            required.IsEmpty ? Mo2OnboardingPhase.SelectProfile : Mo2OnboardingPhase.Authorize,
            Mo2OnboardingDisposition.Incomplete, reference, validation, [], null, required,
            required.IsEmpty ? "Select the MO2 profile to observe." : "Approve only the listed exact session paths.", true);
        return state;
    }

    public async Task<Mo2OnboardingState> AuthorizeRequiredAsync(
        string requirementLabel,
        string userSelectedExactPath,
        CancellationToken cancellationToken = default)
    {
        if (state.Reference is null || state.Validation is null) throw new InvalidOperationException("No connected reference is active.");
        var requirement = state.RequiredAuthorizations.SingleOrDefault(value =>
            value.Label.Equals(requirementLabel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The requested authorization is not currently required.");
        if (!paths.TryCanonicalize(userSelectedExactPath, out var selected, out _) ||
            !paths.TryCanonicalize(requirement.ExactPath, out var expected, out _) ||
            !paths.Equals(selected, expected))
            throw new UnauthorizedAccessException("The user-selected path does not exactly match the requested authorization.");

        cancellationToken.ThrowIfCancellationRequested();
        RegisterExactAuthorization(state.Reference.Id, expected);
        switch (requirement.Label)
        {
            case "Profiles directory": profileAuthorizations.AuthorizeProfilesRoot(state.Reference.Id, expected); break;
            case "Mods directory": modAuthorizations.AuthorizeModsRoot(state.Reference.Id, expected); break;
            case "Game directory": contentAuthorizations.AuthorizeRoot(state.Reference.Id, Mo2ContentRootKind.GameDirectory, expected); break;
            case "Overwrite directory": contentAuthorizations.AuthorizeRoot(state.Reference.Id, Mo2ContentRootKind.Overwrite, expected); break;
            case "MO2 executable": executableAuthorizations.AuthorizePath(state.Reference.Id, expected); break;
        }
        return await ResumeAsync(state.Reference, cancellationToken).ConfigureAwait(false);
    }

    public Mo2OnboardingState SetProfiles(ImmutableArray<Mo2ObservedProfile> profiles, ProfileId? selectedProfileId = null)
    {
        ProfileId? selected = selectedProfileId is ProfileId id && profiles.Any(profile => profile.Id == id) ? id : null;
        return state = state with
        {
            Phase = Mo2OnboardingPhase.SelectProfile,
            Profiles = profiles,
            SelectedProfileId = selected,
            Detail = selected is null ? "Select a profile; Grid will not change selected_profile." : "Profile selected for observation only."
        };
    }

    public Mo2OnboardingState Complete(ProfileId profileId, FidelityAuditReadiness readiness)
    {
        if (!state.Profiles.Any(profile => profile.Id == profileId)) throw new ArgumentException("The selected profile is unavailable.", nameof(profileId));
        var disposition = readiness switch
        {
            FidelityAuditReadiness.Ready => Mo2OnboardingDisposition.Ready,
            FidelityAuditReadiness.RequiresAcknowledgement => Mo2OnboardingDisposition.ReviewRequired,
            _ => Mo2OnboardingDisposition.Blocked,
        };
        return state = state with { Phase = Mo2OnboardingPhase.Complete, SelectedProfileId = profileId, Disposition = disposition, Detail = disposition.ToString() };
    }

    public Mo2OnboardingState Cancel() => state = state with
    {
        Phase = Mo2OnboardingPhase.Canceled,
        Disposition = Mo2OnboardingDisposition.Incomplete,
        Detail = state.ConnectionPersisted ? "Onboarding canceled; the reconnectable reference was retained." : "Onboarding canceled.",
    };

    private static ImmutableArray<Mo2AuthorizationRequirement> ExactRequirements(Mo2InstallationValidation validation) =>
        validation.Paths.Where(path => path.State == Mo2PathState.AuthorizationRequired && path.CanonicalPath is not null)
            .Select(path => new Mo2AuthorizationRequirement(path.Label, path.CanonicalPath!, path.State)).ToImmutableArray();

    private ImmutableArray<string> GetAuthorizations(InstallationReferenceId referenceId)
    {
        lock (authorizationGate)
        {
            return authorizedConfiguredPaths.TryGetValue(referenceId, out var authorizations)
                ? authorizations.Order(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
                : [];
        }
    }

    private IMo2InstallationValidator GetBaseValidator(Mo2InstallationReference reference) =>
        validatorFactory(reference);

    private sealed class AuthorizedValidator(
        Mo2OnboardingCoordinator owner,
        Mo2InstallationReference reference) : IMo2InstallationValidator
    {
        public Task<Mo2InstallationValidation> ValidateAsync(
            Mo2ValidationRequest request,
            CancellationToken cancellationToken = default) =>
            owner.GetBaseValidator(reference).ValidateAsync(
                request with
                {
                    AuthorizedConfiguredPaths = request.EffectiveAuthorizedConfiguredPaths
                        .Concat(owner.GetAuthorizations(reference.Id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToImmutableArray(),
                },
                cancellationToken);
    }
}
