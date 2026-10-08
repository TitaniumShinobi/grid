using System.Collections.Immutable;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.Core.Application;

public enum InvestigationTicketRequiredField
{
    Class,
    Problem,
    Goal,
}

public enum InvestigationTicketReadinessStatus
{
    Incomplete,
    Ready,
    Invalid,
}

public sealed record InvestigationTicketReadiness(
    InvestigationTicketReadinessStatus Status,
    bool CanSubmit,
    ImmutableArray<InvestigationTicketRequiredField> MissingRequiredFields,
    ImmutableArray<string> Issues);

/// <summary>
/// Pure validation/readiness policy for the canonical ticket draft. The policy
/// has no service dependencies and cannot perform filesystem, network,
/// account, authorization, VVAULT, or execution work.
/// </summary>
public static class InvestigationTicketDraftPolicy
{
    public static InvestigationTicketReadiness Evaluate(InvestigationTicketDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var missing = ImmutableArray.CreateBuilder<InvestigationTicketRequiredField>();
        if (draft.Class is null) missing.Add(InvestigationTicketRequiredField.Class);
        if (draft.Problem is null) missing.Add(InvestigationTicketRequiredField.Problem);
        if (draft.Goal is null) missing.Add(InvestigationTicketRequiredField.Goal);

        var issues = ImmutableArray.CreateBuilder<string>();
        ValidateEnvelope(draft, issues);
        ValidateStructuredSelections(draft, issues);
        ValidateContextRelationships(draft, issues);
        ValidateCollections(draft, issues);

        var issueValues = issues.ToImmutable();
        var missingValues = missing.ToImmutable();
        var status = issueValues.Length > 0
            ? InvestigationTicketReadinessStatus.Invalid
            : missingValues.Length > 0
                ? InvestigationTicketReadinessStatus.Incomplete
                : InvestigationTicketReadinessStatus.Ready;

        return new InvestigationTicketReadiness(
            status,
            status == InvestigationTicketReadinessStatus.Ready,
            missingValues,
            issueValues);
    }

    public static void Validate(InvestigationTicketDraft draft)
    {
        var readiness = Evaluate(draft);
        if (readiness.Status == InvestigationTicketReadinessStatus.Invalid)
            throw new ArgumentException(string.Join(" ", readiness.Issues), nameof(draft));
    }

    private static void ValidateEnvelope(
        InvestigationTicketDraft draft,
        ImmutableArray<string>.Builder issues)
    {
        if (draft.SchemaVersion != InvestigationTicketDraft.CurrentSchemaVersion)
            issues.Add($"Ticket Draft schema version '{draft.SchemaVersion}' is unsupported.");
        if (draft.Revision < 1)
            issues.Add("Ticket Draft revision must be at least one.");
    }

    private static void ValidateStructuredSelections(
        InvestigationTicketDraft draft,
        ImmutableArray<string>.Builder issues)
    {
        if (draft.Class is not null)
        {
            RequireText(draft.Class.DisplayName, "Class display name", issues);
            RequireText(draft.Class.IconId, "Class icon identity", issues);
        }

        if (draft.Problem is not null)
        {
            RequireText(draft.Problem.DisplayName, "Problem display name", issues);
            if (draft.Class is null || draft.Problem.ClassId != draft.Class.Id)
                issues.Add("Problem must be bound to the selected Class.");
        }

        if (draft.Timing is not null)
        {
            RequireText(draft.Timing.DisplayName, "Timing display name", issues);
            if (draft.Class is null || draft.Timing.ClassId != draft.Class.Id)
                issues.Add("Timing must be bound to the selected Class.");
        }

        if (draft.Goal is not null)
            RequireText(draft.Goal.DisplayName, "Goal display name", issues);
    }

    private static void ValidateContextRelationships(
        InvestigationTicketDraft draft,
        ImmutableArray<string>.Builder issues)
    {
        if (draft.InstallationId is not null && draft.GameId is null)
            issues.Add("Installation requires a canonical Game.");
        if (draft.ProfileId is not null && (draft.GameId is null || draft.InstallationId is null))
            issues.Add("Profile requires a canonical Game and Installation.");

        ValidateProvenancePair(draft.GameId is not null, draft.GameProvenance, "Game", issues);
        ValidateProvenancePair(draft.InstallationId is not null, draft.InstallationProvenance, "Installation", issues);
        ValidateProvenancePair(draft.ProfileId is not null, draft.ProfileProvenance, "Profile", issues);

        var mods = Normalize(draft.Mods);
        if (mods.Length > 0 && (draft.GameId is null || draft.InstallationId is null || draft.ProfileId is null))
            issues.Add("Mod context requires a canonical Game, Installation, and Profile.");

        foreach (var configured in Normalize(draft.ConfiguredToolContext))
        {
            RequireText(configured.DisplayName, "Configured-tool display name", issues);
            if (configured.Provenance != TicketSelectionProvenance.ExplicitUserSelection)
                issues.Add($"Configured tool '{configured.ConfigurationId}' must record explicit user selection provenance.");
            if (draft.GameId is null || draft.InstallationId is null)
            {
                issues.Add($"Configured tool '{configured.ConfigurationId}' requires canonical Game and Installation context.");
                continue;
            }
            if (configured.Scope.GameId != draft.GameId.Value ||
                configured.Scope.InstallationId != draft.InstallationId.Value)
            {
                issues.Add($"Configured tool '{configured.ConfigurationId}' crosses the Ticket Draft Game or Installation boundary.");
            }
            if (configured.Scope.ProfileId is ProfileId scopedProfile && scopedProfile != draft.ProfileId)
                issues.Add($"Configured tool '{configured.ConfigurationId}' crosses the Ticket Draft Profile boundary.");
        }
    }

    private static void ValidateCollections(
        InvestigationTicketDraft draft,
        ImmutableArray<string>.Builder issues)
    {
        EnsureUnique(Normalize(draft.Mods), value => value.ModId.Value, "Mod", issues);
        EnsureUnique(Normalize(draft.IntegratedTools), value => value.ToolId.Value, "integrated ToolID", issues);
        EnsureUnique(Normalize(draft.ConfiguredToolContext), value => value.ConfigurationId.Value, "configured-tool identity", issues);
        EnsureUnique(Normalize(draft.ReferenceContext), value => value.Id.Value, "reference-context identity", issues);
        EnsureUnique(Normalize(draft.Attachments), value => value.Id.Value, "attachment identity", issues);
        EnsureUniqueCanonicalSelections(Normalize(draft.CanonicalSelections), issues);

        foreach (var mod in Normalize(draft.Mods))
            RequireText(mod.DisplayName, "Mod display name", issues);
        foreach (var tool in Normalize(draft.IntegratedTools))
            RequireText(tool.DisplayName, "Integrated-tool display name", issues);

        foreach (var reference in Normalize(draft.ReferenceContext))
        {
            RequireText(reference.DisplayName, "Reference-context display name", issues);
            RequireText(reference.ReferenceProvenance.SourceKind, "Reference source kind", issues);
            RequireText(reference.ReferenceProvenance.SourceIdentifier, "Reference source identity", issues);
            RequireText(reference.ReferenceProvenance.Claim, "Reference claim", issues);
            RequireText(reference.ReferenceProvenance.Confidence, "Reference confidence", issues);
            if (reference.ModId is not null && reference.GameId is null)
                issues.Add($"Reference context '{reference.Id}' cannot be Mod-scoped without a canonical Game scope.");
            if (reference.GameId is GameId scopedGame && draft.GameId is GameId draftGame && scopedGame != draftGame)
                issues.Add($"Reference context '{reference.Id}' crosses the Ticket Draft Game boundary.");
        }

        var scaffoldPaths = new HashSet<(TicketReferenceContextKind Kind, string Path)>();
        foreach (var userContext in Normalize(draft.UserContext))
        {
            RequireText(userContext.Value, "User context value", issues);
            if (userContext.Provenance != TicketSelectionProvenance.ExplicitUserSelection &&
                userContext.Provenance != TicketSelectionProvenance.LegacyImported)
            {
                issues.Add("User context must retain explicit-user or legacy-import provenance.");
            }
            if (userContext.Resolution == TicketUserContextResolution.Unresolved && userContext.MatchedReferenceId is not null)
                issues.Add("Unresolved user context cannot claim a matched reference identity.");
            if (userContext.Resolution == TicketUserContextResolution.Matched && userContext.MatchedReferenceId is null)
                issues.Add("Matched user context requires a reference identity.");
            if (userContext.ScaffoldPathId is { } scaffoldPath)
            {
                if (string.IsNullOrWhiteSpace(scaffoldPath) || scaffoldPath.Length > 512)
                    issues.Add("Scaffold path must be non-empty and at most 512 characters.");
                if (userContext.Resolution != TicketUserContextResolution.Unresolved ||
                    userContext.Provenance != TicketSelectionProvenance.ExplicitUserSelection ||
                    userContext.MatchedReferenceId is not null)
                    issues.Add("Scaffold context must remain explicit, unresolved user context without a matched reference.");
                if (!scaffoldPaths.Add((userContext.Kind, scaffoldPath)))
                    issues.Add("Scaffold selections must be unique within their selector.");
            }
        }

        foreach (var attachment in Normalize(draft.Attachments))
        {
            RequireText(attachment.OriginalName, "Attachment original name", issues);
            RequireText(attachment.ResourceReference, "Attachment resource reference", issues);
            if (attachment.SizeBytes is < 0)
                issues.Add($"Attachment '{attachment.Id}' cannot have a negative size.");
        }

        var canonicalSelections = Normalize(draft.CanonicalSelections);
        if (!canonicalSelections.IsEmpty)
        {
            if (draft.GameId is null)
                issues.Add("Canonical selector selections require a canonical Game.");
            if (draft.GameProvenance is null)
                issues.Add("Canonical selector selections require Game selection provenance.");
        }

        foreach (var selection in canonicalSelections)
        {
            if (selection.SelectionKind != CanonicalSelectorSelectionKind.CanonicalRecord ||
                selection.KnowledgeRecordId is null || selection.SelectedPathId is null)
                issues.Add("Ticket canonical selector selections must retain a canonical record and navigation path.");
            if (!CanonicalSelectorProjectionPolicyResolver.IsSupportedPolicy(
                    selection.ProjectionPolicyId, selection.ProjectionPolicyVersion))
                issues.Add("Ticket canonical selector selection uses an unsupported projection policy.");
            if (draft.GameId is GameId gameId &&
                !CanonicalSelectorProjectionPolicyResolver.MatchesGamePolicy(
                    gameId, selection.KnowledgeKind, selection.ProjectionPolicyId, selection.ProjectionPolicyVersion))
                issues.Add("Ticket canonical selector selection uses a projection policy that does not match the selected Game.");
            var otherKind = selection.KnowledgeKind switch
            {
                KnowledgeKind.Location => TicketReferenceContextKind.Location,
                KnowledgeKind.MissionQuest => TicketReferenceContextKind.MissionOrQuest,
                KnowledgeKind.Item => TicketReferenceContextKind.Item,
                KnowledgeKind.Actor => TicketReferenceContextKind.Entity,
                _ => throw new ArgumentOutOfRangeException(nameof(selection.KnowledgeKind)),
            };
            if (Normalize(draft.UserContext).Any(context =>
                    context.Kind == otherKind && context.Resolution == TicketUserContextResolution.Unresolved))
                issues.Add($"Ticket selector '{selection.KnowledgeKind}' cannot be both canonical and unresolved Other context.");
        }
    }

    private static void ValidateProvenancePair(
        bool hasSelection,
        TicketSelectionProvenance? provenance,
        string name,
        ImmutableArray<string>.Builder issues)
    {
        if (hasSelection && provenance is null)
            issues.Add($"{name} selection requires provenance.");
        if (!hasSelection && provenance is not null)
            issues.Add($"{name} provenance cannot exist without a selection.");
    }

    private static void RequireText(
        string? value,
        string description,
        ImmutableArray<string>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add($"{description} must be non-empty.");
    }

    private static void EnsureUniqueCanonicalSelections(
        ImmutableArray<CanonicalSelectorSelection> values,
        ImmutableArray<string>.Builder issues)
    {
        var seenNonLocationKinds = new HashSet<string>(StringComparer.Ordinal);
        var seenLocationRecords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.KnowledgeKind == KnowledgeKind.Location)
            {
                if (value.KnowledgeRecordId is not { } recordId || value.SelectedPathId is not { } pathId)
                    continue;
                var key = $"{recordId.Value}:{pathId.Value}";
                if (!seenLocationRecords.Add(key))
                    issues.Add("Ticket Draft contains duplicate Location canonical selections.");
                continue;
            }

            if (!seenNonLocationKinds.Add(value.KnowledgeKind.ToString()))
                issues.Add("Ticket Draft contains duplicate canonical selector kind selections.");
        }
    }

    private static void EnsureUnique<T>(
        ImmutableArray<T> values,
        Func<T, string> keySelector,
        string description,
        ImmutableArray<string>.Builder issues)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!identities.Add(keySelector(value)))
                issues.Add($"Ticket Draft contains duplicate {description} selections.");
        }
    }

    private static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values) =>
        values.IsDefault ? [] : values;
}
