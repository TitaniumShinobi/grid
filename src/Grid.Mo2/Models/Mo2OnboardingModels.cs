using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Mo2.Models;

public sealed record Mo2DisconnectRequest(
    InstallationReferenceId ReferenceId,
    InstallationId InstallationId);

public sealed record Mo2DisconnectResult(
    bool Succeeded,
    bool ReferenceWasPresent,
    ImmutableArray<Mo2ValidationIssue> Issues);

public enum Mo2OnboardingPhase
{
    DetectOrBrowse,
    ValidateAndConnect,
    Authorize,
    SelectProfile,
    Observe,
    Audit,
    Complete,
    Canceled,
    Failed,
}

public enum Mo2OnboardingDisposition { Ready, ReviewRequired, Blocked, Incomplete }

public sealed record Mo2AuthorizationRequirement(string Label, string ExactPath, Mo2PathState State);

public sealed record Mo2OnboardingState(
    Mo2OnboardingPhase Phase,
    Mo2OnboardingDisposition Disposition,
    Mo2InstallationReference? Reference,
    Mo2InstallationValidation? Validation,
    ImmutableArray<Mo2ObservedProfile> Profiles,
    ProfileId? SelectedProfileId,
    ImmutableArray<Mo2AuthorizationRequirement> RequiredAuthorizations,
    string Detail,
    bool ConnectionPersisted);

public sealed record Mo2ConnectedObservation(
    Mo2InstallationReference Reference,
    Mo2InstallationValidation Validation,
    Mo2ProfileSnapshot ProfileSnapshot,
    Mo2ObservedProfile SelectedProfile,
    Mo2ModInventorySnapshot InventorySnapshot,
    ResolvedEnvironmentRefreshResult Environment,
    ToolOutputObservationRefreshResult ToolOutputs,
    FidelityAuditResult Audit,
    bool IsStale,
    string Detail);
