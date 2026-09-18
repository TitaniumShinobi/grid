using System.Collections.Immutable;

namespace Grid.Mo2.Models;

public enum Mo2PapyrusInspectionStatus
{
    Complete,
    MetadataOnly,
    Unsupported,
    Malformed,
    Oversized,
}

public sealed record Mo2PapyrusProperty(string Name, string Type, bool IsAuto, string? DefaultValue);
public sealed record Mo2PapyrusRoutine(string Kind, string Name, int Line);
public sealed record Mo2PapyrusCondition(string Expression, int Line);
public sealed record Mo2PapyrusCall(
    string? Receiver,
    string Method,
    int Line,
    string? Routine = null,
    string? State = null,
    int? Instruction = null,
    ImmutableArray<string> Arguments = default);

public sealed record Mo2PapyrusInstruction(
    string Routine,
    string State,
    int Index,
    int Line,
    string OpCode,
    ImmutableArray<string> Operands);

public sealed record Mo2PapyrusVariable(string Name, string Type, string? DefaultValue);

public sealed record Mo2PapyrusAssignment(
    string Routine,
    string State,
    string Target,
    string Expression,
    int Instruction,
    int Line);

public sealed record Mo2PapyrusReferenceStateAction(
    string Routine,
    string State,
    string Target,
    string Action,
    int Instruction,
    int Line);

public sealed record Mo2PapyrusPersistenceAccess(
    string Routine,
    string State,
    string Receiver,
    string Method,
    string? Key,
    int Instruction,
    int Line);

public sealed record Mo2PapyrusStateAnalysis(
    string Status,
    ImmutableArray<string> ControlledReferences,
    ImmutableArray<string> LifecycleRoutines,
    ImmutableArray<Mo2PapyrusReferenceStateAction> ReferenceStateActions,
    ImmutableArray<Mo2PapyrusPersistenceAccess> PersistenceAccesses,
    string Finding,
    string Solution,
    ImmutableArray<string> SelectionVariables = default);

public sealed record Mo2PapyrusInspection(
    Mo2PapyrusInspectionStatus Status,
    string Format,
    string? ScriptName,
    string? Extends,
    ImmutableArray<Mo2PapyrusProperty> Properties,
    ImmutableArray<Mo2PapyrusRoutine> Routines,
    ImmutableArray<Mo2PapyrusCondition> Conditions,
    ImmutableArray<Mo2PapyrusCall> Calls,
    ImmutableArray<string> Symbols,
    ImmutableArray<Mo2AssetIssue> Issues,
    ImmutableArray<Mo2PapyrusInstruction> Instructions = default,
    Mo2PapyrusStateAnalysis? StateAnalysis = null,
    ImmutableArray<Mo2PapyrusVariable> Variables = default,
    ImmutableArray<Mo2PapyrusAssignment> Assignments = default);
