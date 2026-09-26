using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct InvestigationId
{
    [JsonConstructor]
    public InvestigationId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketId
{
    [JsonConstructor]
    public TicketId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Opaque VVAULT-assigned GRID instance identity. Its presence binds a draft to
/// an already resolved instance; it grants no session or storage authority.
/// </summary>
public readonly record struct VvaultGridInstanceId
{
    [JsonConstructor]
    public VvaultGridInstanceId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketClassId
{
    [JsonConstructor]
    public TicketClassId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketProblemId
{
    [JsonConstructor]
    public TicketProblemId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketTimingId
{
    [JsonConstructor]
    public TicketTimingId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketGoalId
{
    [JsonConstructor]
    public TicketGoalId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketReferenceContextId
{
    [JsonConstructor]
    public TicketReferenceContextId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct TicketAttachmentId
{
    [JsonConstructor]
    public TicketAttachmentId(string value) => Value = TicketIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public enum TicketSelectionProvenance
{
    ExplicitUserSelection,
    ContextInherited,
    DeterministicallyResolved,
    ReferenceProvider,
    LegacyImported,
}

public enum TicketReferenceContextKind
{
    Location,
    MissionOrQuest,
    Item,
    Entity,
}

public enum TicketUserContextResolution
{
    Unresolved,
    Matched,
    Ambiguous,
}

public enum ConfiguredToolIdentityStatus
{
    Unresolved,
    Resolved,
}

public enum ConfiguredToolCompatibilityStatus
{
    Unresolved,
    Resolved,
}

public enum ConfiguredToolIntegrationStatus
{
    Unavailable,
    Available,
}

public enum ConfiguredToolInvestigationCapabilityStatus
{
    None,
    Available,
}

public sealed record TicketClassSelection(
    TicketClassId Id,
    string DisplayName,
    string IconId,
    TicketSelectionProvenance Provenance);

public sealed record TicketProblemSelection(
    TicketProblemId Id,
    TicketClassId ClassId,
    string DisplayName,
    TicketSelectionProvenance Provenance);

public sealed record TicketTimingSelection(
    TicketTimingId Id,
    TicketClassId ClassId,
    string DisplayName,
    TicketSelectionProvenance Provenance);

public sealed record TicketGoalSelection(
    TicketGoalId Id,
    string DisplayName,
    TicketSelectionProvenance Provenance);

public sealed record TicketModSelection(
    ModId ModId,
    string DisplayName,
    TicketSelectionProvenance Provenance);

/// <summary>
/// A canonical GRID-integrated tool selection. Only this record carries a
/// ToolId; configured launch tools use the separate inert context record.
/// </summary>
public sealed record TicketIntegratedToolSelection(
    ToolId ToolId,
    string DisplayName,
    TicketSelectionProvenance Provenance);

/// <summary>
/// User launch configuration selected as investigation context. This record
/// deliberately has no ToolId or capability identity and grants no authority.
/// </summary>
public sealed record TicketConfiguredToolContext(
    UserToolConfigurationId ConfigurationId,
    string DisplayName,
    UserToolScope Scope,
    string? ExecutableReference,
    string? ConfigurationFingerprint,
    bool IsRunnable,
    ConfiguredToolIdentityStatus IdentityStatus,
    ConfiguredToolCompatibilityStatus CompatibilityStatus,
    ConfiguredToolIntegrationStatus IntegrationStatus,
    ConfiguredToolInvestigationCapabilityStatus InvestigationCapabilityStatus,
    TicketSelectionProvenance Provenance);

public sealed record TicketReferenceProvenance(
    string SourceKind,
    string SourceIdentifier,
    string Claim,
    string Confidence,
    bool IsSufficient);

/// <summary>Normalized reference knowledge, never an observed GRID fact.</summary>
public sealed record TicketReferenceContext(
    TicketReferenceContextId Id,
    TicketReferenceContextKind Kind,
    string DisplayName,
    ImmutableArray<string> Aliases,
    GameId? GameId,
    ModId? ModId,
    TicketReferenceProvenance ReferenceProvenance,
    TicketSelectionProvenance SelectionProvenance);

/// <summary>
/// User-supplied row-four context. Its resolution state does not promote it to
/// reference knowledge, evidence, a finding, or a mutation justification.
/// </summary>
public sealed record TicketUserContext(
    TicketReferenceContextKind Kind,
    string Value,
    TicketUserContextResolution Resolution,
    TicketSelectionProvenance Provenance,
    TicketReferenceContextId? MatchedReferenceId = null);

public sealed record TicketAttachmentReference(
    TicketAttachmentId Id,
    string OriginalName,
    string ResourceReference,
    string? MediaType,
    long? SizeBytes,
    TicketSelectionProvenance Provenance);

/// <summary>
/// Versioned, provider-neutral investigation ticket draft. It is an inert data
/// contract: constructing or evaluating it performs no I/O and grants no read,
/// launch, process, or mutation authority.
/// </summary>
public sealed record InvestigationTicketDraft(
    int SchemaVersion,
    InvestigationId InvestigationId,
    TicketId TicketId,
    int Revision,
    TicketClassSelection? Class,
    TicketProblemSelection? Problem,
    TicketGoalSelection? Goal,
    TicketTimingSelection? Timing = null,
    GameId? GameId = null,
    InstallationId? InstallationId = null,
    ProfileId? ProfileId = null,
    TicketSelectionProvenance? GameProvenance = null,
    TicketSelectionProvenance? InstallationProvenance = null,
    TicketSelectionProvenance? ProfileProvenance = null,
    VvaultGridInstanceId? VvaultInstanceId = null,
    ImmutableArray<TicketModSelection> Mods = default,
    ImmutableArray<TicketIntegratedToolSelection> IntegratedTools = default,
    ImmutableArray<TicketConfiguredToolContext> ConfiguredToolContext = default,
    ImmutableArray<TicketReferenceContext> ReferenceContext = default,
    ImmutableArray<TicketUserContext> UserContext = default,
    ImmutableArray<TicketAttachmentReference> Attachments = default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    ImmutableArray<CanonicalSelectorSelection> CanonicalSelections = default)
{
    public const int CurrentSchemaVersion = 2;
}

internal static class TicketIdentifier
{
    public static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Ticket identities must be non-empty.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > 512)
            throw new ArgumentException("Ticket identities must not exceed 512 characters.", parameterName);

        return normalized;
    }
}
