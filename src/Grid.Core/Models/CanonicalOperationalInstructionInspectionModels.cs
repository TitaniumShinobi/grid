namespace Grid.Core.Models;

/// <summary>
/// GR-20 operational-instruction kinds. Values match existing InstructionCategories identifiers.
/// Presence of sourced wording is not safety, compatibility, or execution authority.
/// </summary>
public enum OperationalInstructionKind
{
    Installation = 0,
    Configuration = 1,
    Usage = 2,
    Compatibility = 3,
    Update = 4,
    Uninstall = 5,
    Troubleshooting = 6,
}

/// <summary>
/// Distinguishes an unlocated section from an explicitly empty or absent section.
/// </summary>
public enum OperationalInstructionSectionPresence
{
    Present = 0,
    Missing = 1,
    ProvenAbsence = 2,
    EmptyUnresolved = 3,
}

/// <summary>
/// Caller-identified instruction section. The inspector never invents headings or pointers.
/// A locator beginning with '/' is a JSON pointer; a locator beginning with '#' is an exact markdown ATX heading line.
/// </summary>
public sealed record OperationalInstructionSectionRequest(
    string Id,
    string SourceId,
    string Locator,
    OperationalInstructionKind Kind,
    RegistrationApplicability[] Applicability,
    string? AbsenceMarker = null,
    bool EmptyIsAbsence = false);

/// <summary>
/// Candidate-only admitted guidance. VerbatimText is exact sourced wording when present.
/// </summary>
public sealed record RegisteredOperationalInstruction(
    string Id,
    string SourceId,
    string Locator,
    OperationalInstructionKind Kind,
    string CategoryId,
    OperationalInstructionSectionPresence Presence,
    string VerbatimText,
    string ContentSha256,
    RegistrationApplicability[] Applicability,
    string[] EvidenceIds);

/// <summary>
/// Sibling of CanonicalRegistrationCandidate for GR-20. Evaluate/Verifier do not load this type.
/// BoundCandidateDigest is an optional attachment pointer; this record does not mutate the bound candidate.
/// </summary>
public sealed record CanonicalOperationalInstructionCandidate(
    int FormatVersion,
    string InspectorVersion,
    string InspectionCategory,
    RegistrationSource[] Sources,
    OperationalInstructionSectionRequest[] Requests,
    RegistrationEvidence[] Evidence,
    RegisteredOperationalInstruction[] Instructions,
    RegistrationRuling[] Rulings,
    string? BoundCandidateDigest,
    string PublicationState);

public static class OperationalInstructionInspection
{
    public const string InspectorVersion = "grid.operational-instruction-inspection.v1";
    public const string CategoryId = "GR-20";
    public const int FormatVersion = 1;

    public static string CategoryIdFor(OperationalInstructionKind kind) => kind switch
    {
        OperationalInstructionKind.Installation => InstructionCategories.Installation.Value,
        OperationalInstructionKind.Configuration => InstructionCategories.Configuration.Value,
        OperationalInstructionKind.Usage => InstructionCategories.Usage.Value,
        OperationalInstructionKind.Compatibility => InstructionCategories.LoadOrderCompatibility.Value,
        OperationalInstructionKind.Update => InstructionCategories.Update.Value,
        OperationalInstructionKind.Uninstall => InstructionCategories.Uninstall.Value,
        OperationalInstructionKind.Troubleshooting => InstructionCategories.Troubleshooting.Value,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
