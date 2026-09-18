using System.Text.Json.Serialization;

namespace Grid.Core.Models;

public readonly record struct GameId
{
    [JsonConstructor]
    public GameId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GameAdapterId
{
    [JsonConstructor]
    public GameAdapterId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct InstallationId
{
    [JsonConstructor]
    public InstallationId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct InstallationReferenceId
{
    [JsonConstructor]
    public InstallationReferenceId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ProfileId
{
    public ProfileId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ModId
{
    public ModId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct PluginId
{
    public PluginId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ToolId
{
    public ToolId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct LaunchTargetId
{
    public LaunchTargetId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct EnvironmentEntryId
{
    public EnvironmentEntryId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ResolvedSnapshotId
{
    public ResolvedSnapshotId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ArchiveId
{
    public ArchiveId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct VirtualPathId
{
    public VirtualPathId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ProviderId
{
    public ProviderId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ObservedExecutableId
{
    public ObservedExecutableId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct GeneratedOutputId
{
    public GeneratedOutputId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ExternalObservationSnapshotId
{
    public ExternalObservationSnapshotId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct FidelityAuditId
{
    public FidelityAuditId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct ExternalLaunchSessionId
{
    public ExternalLaunchSessionId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct LaunchApprovalId
{
    public LaunchApprovalId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct LaunchEventId
{
    public LaunchEventId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct EnvironmentDiscrepancyId
{
    public EnvironmentDiscrepancyId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct DeterministicActionId
{
    public DeterministicActionId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct OperatorMessageId
{
    public OperatorMessageId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct OperatorProposalId
{
    public OperatorProposalId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct OperatorAuditId
{
    public OperatorAuditId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct HistoryEntryId
{
    public HistoryEntryId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

public readonly record struct DiagnosticsSelectionId
{
    public DiagnosticsSelectionId(string value) => Value = DomainIdentifier.Require(value, nameof(value));

    public string Value { get; }

    public override string ToString() => Value;
}

internal static class DomainIdentifier
{
    public static string Require(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Identifiers must be non-empty.", parameterName);
        }

        return value;
    }

    public static void Ensure(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{description} must be non-empty.");
        }
    }
}
