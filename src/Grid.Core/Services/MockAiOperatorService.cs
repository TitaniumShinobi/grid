using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

public sealed class MockAiOperatorService : IAiOperatorService
{
    public Task<OperatorServiceResponse> RespondAsync(
        OperatorRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var context = request.Context;
        var hasContext = context.Key.GameId is not null;
        var wantsProposal = request.UserText.Contains("proposal", StringComparison.OrdinalIgnoreCase) ||
            request.UserText.Contains("action", StringComparison.OrdinalIgnoreCase) ||
            request.UserText.Contains("draft", StringComparison.OrdinalIgnoreCase);
        var action = wantsProposal
            ? context.AvailableActions.FirstOrDefault(candidate =>
                candidate.Availability != OperatorActionAvailability.Unavailable)
            : null;

        var evidenceDetail = hasContext
            ? $"The supplied mock snapshot identifies {context.GameName}, {context.InstallationName ?? "no installation"}, and {context.ProfileName ?? "no profile"}."
            : "The supplied structured snapshot contains no selected game, installation, or profile.";
        var inferenceDetail = context.Subjects.IsEmpty
            ? "No selected workspace subject is available, so a subject-specific conclusion would be unsupported."
            : $"The represented selection may make {context.Subjects[0].Name} the most relevant starting point; this is an inference, not observed filesystem state.";
        var warningDetail = context.Permissions.CanExecute
            ? "Execution permission is represented, but this mock service still has no execution route."
            : "This mock operator cannot inspect files, approve proposals, execute actions, or change external state.";

        var response = new OperatorServiceResponse(
            hasContext
                ? "I can organize the supplied represented context and draft a typed proposal for a deterministic route."
                : "Select a managed game context to provide structured evidence. No external inspection occurred.",
            ImmutableArray.Create(
                new OperatorStatement(OperatorContentKind.Evidence, "Supplied evidence", evidenceDetail),
                new OperatorStatement(OperatorContentKind.Inference, "Bounded inference", inferenceDetail),
                new OperatorStatement(OperatorContentKind.Warning, "Operator boundary", warningDetail)),
            action?.Id);
        return Task.FromResult(response);
    }
}
