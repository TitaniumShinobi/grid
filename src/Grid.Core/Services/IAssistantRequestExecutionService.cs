using System.Collections.Immutable;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Infrastructure boundary for deterministic request execution. Implementations own
/// persistence and process/filesystem authority; Core owns only request and UI state.
/// </summary>
public interface IAssistantRequestExecutionService
{
    bool IsAvailable { get; }

    // Retained for binary/source compatibility with existing hosts. Submission
    // readiness is governed by the selected Class recipe, not this global flag.
    bool RequiresToolSelection { get; }

    Task<ImmutableArray<AssistantTaskRecord>> LoadTasksAsync(CancellationToken cancellationToken = default);

    Task<AssistantAuthorizationReview> PrepareAsync(
        AssistantRequestDraft request,
        CancellationToken cancellationToken = default);

    Task<AssistantAuthorizationGrant> GrantAsync(
        AssistantAuthorizationReview authorization,
        CancellationToken cancellationToken = default);

    Task<AssistantExecutionResult> ExecuteAsync(
        AssistantAuthorizationGrant grant,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default);

    Task<AssistantExecutionResult> ResumeAsync(
        string taskId,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default);

    Task<AssistantAuthorizationReview> PrepareActionAsync(
        AssistantCanonicalRequest? parentRequest,
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        CancellationToken cancellationToken = default);

    Task<AssistantTaskRecord> ExecuteActionAsync(
        string taskId,
        AssistantCaseAction action,
        ImmutableArray<AssistantAttachmentDraft> attachments,
        IProgress<AssistantExecutionProgress> progress,
        CancellationToken cancellationToken = default);
}
