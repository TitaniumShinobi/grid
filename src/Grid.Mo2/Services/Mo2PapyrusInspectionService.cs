using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>Bounded, read-only Papyrus source and Skyrim bytecode observation.</summary>
public sealed partial class Mo2PapyrusInspectionService
{
    private const uint SkyrimPexMagic = 0xFA57C0DE;

    public Mo2PapyrusInspection InspectPsc(ReadOnlyMemory<byte> content)
    {
        if (content.Length == 0) return Failure(Mo2PapyrusInspectionStatus.Malformed, "PSC", "mo2.papyrus.psc.empty", "The source is empty.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(content.Span); }
        catch (DecoderFallbackException) { return Failure(Mo2PapyrusInspectionStatus.Malformed, "PSC", "mo2.papyrus.psc.encoding", "The source is not valid UTF-8."); }

        var properties = ImmutableArray.CreateBuilder<Mo2PapyrusProperty>();
        var routines = ImmutableArray.CreateBuilder<Mo2PapyrusRoutine>();
        var conditions = ImmutableArray.CreateBuilder<Mo2PapyrusCondition>();
        var calls = ImmutableArray.CreateBuilder<Mo2PapyrusCall>();
        var variables = ImmutableArray.CreateBuilder<Mo2PapyrusVariable>();
        var assignments = ImmutableArray.CreateBuilder<Mo2PapyrusAssignment>();
        string? script = null, extends = null, currentRoutine = null;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = Comment().Replace(lines[i], "").Trim();
            if (line.Length == 0) continue;
            var declaration = Script().Match(line);
            if (declaration.Success) { script = declaration.Groups[1].Value; extends = declaration.Groups[2].Success ? declaration.Groups[2].Value : null; continue; }
            var property = Property().Match(line);
            if (property.Success) { properties.Add(new(property.Groups[2].Value, property.Groups[1].Value, property.Groups[4].Success, NullIfEmpty(property.Groups[3].Value.Trim()))); continue; }
            if (currentRoutine is null)
            {
                var variable = Variable().Match(line);
                if (variable.Success)
                {
                    variables.Add(new(variable.Groups[2].Value, variable.Groups[1].Value, NullIfEmpty(variable.Groups[3].Value.Trim())));
                    continue;
                }
            }
            var routine = Routine().Match(line);
            if (routine.Success)
            {
                currentRoutine = routine.Groups[2].Value;
                routines.Add(new(routine.Groups[1].Value, currentRoutine, i + 1));
                continue;
            }
            if (EndRoutine().IsMatch(line)) { currentRoutine = null; continue; }
            var condition = Condition().Match(line);
            if (condition.Success) conditions.Add(new(condition.Groups[1].Value.Trim(), i + 1));
            var assignment = Assignment().Match(line);
            if (assignment.Success && currentRoutine is not null)
                assignments.Add(new(currentRoutine, string.Empty, assignment.Groups[1].Value, assignment.Groups[2].Value.Trim(), i + 1, i + 1));
            foreach (Match call in Call().Matches(line))
            {
                var method = call.Groups[2].Value;
                if (ControlWords.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
                calls.Add(new(NullIfEmpty(call.Groups[1].Value), method, i + 1, currentRoutine, string.Empty, i + 1, []));
            }
        }
        var issues = script is null ? ImmutableArray.Create(new Mo2AssetIssue("mo2.papyrus.psc.declaration_missing", "No Scriptname declaration was found.")) : [];
        var callArray = calls.ToImmutable();
        var variableArray = variables.ToImmutable();
        var assignmentArray = assignments.ToImmutable();
        return new(script is null ? Mo2PapyrusInspectionStatus.Malformed : Mo2PapyrusInspectionStatus.Complete, "PSC", script, extends,
            properties.ToImmutable(), routines.ToImmutable(), conditions.ToImmutable(), callArray, [], issues, [], Analyze(callArray, assignmentArray, variableArray), variableArray, assignmentArray);
    }

    public Mo2PapyrusInspection InspectPex(ReadOnlyMemory<byte> content)
    {
        if (content.Length < 16 || BinaryPrimitives.ReadUInt32BigEndian(content.Span) != SkyrimPexMagic)
            return Failure(Mo2PapyrusInspectionStatus.Malformed, "PEX", "mo2.papyrus.pex.magic", "The Skyrim Papyrus bytecode magic is absent.");
        try
        {
            var parsed = new SkyrimPexReader(content).Read();
            return new(
                Mo2PapyrusInspectionStatus.Complete,
                "PEX",
                parsed.ScriptName,
                parsed.Extends,
                parsed.Properties,
                parsed.Routines,
                parsed.Conditions,
                parsed.Calls,
                parsed.Symbols,
                [],
                parsed.Instructions,
                Analyze(parsed.Calls, parsed.Assignments, parsed.Variables),
                parsed.Variables,
                parsed.Assignments);
        }
        catch (PexLimitException exception)
        {
            return Failure(Mo2PapyrusInspectionStatus.Oversized, "PEX", "mo2.papyrus.pex.limit", exception.Message);
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException or DecoderFallbackException or ArgumentOutOfRangeException)
        {
            return Failure(Mo2PapyrusInspectionStatus.Malformed, "PEX", "mo2.papyrus.pex.malformed", exception.Message);
        }
    }

    private static Mo2PapyrusStateAnalysis Analyze(
        ImmutableArray<Mo2PapyrusCall> calls,
        ImmutableArray<Mo2PapyrusAssignment> assignments,
        ImmutableArray<Mo2PapyrusVariable> variables)
    {
        var actions = calls
            .Where(call => ReferenceStateMethods.Contains(call.Method, StringComparer.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(call.Receiver))
            .Select(call => new Mo2PapyrusReferenceStateAction(
                call.Routine ?? "<unknown>", call.State ?? string.Empty, call.Receiver!, NormalizeReferenceAction(call.Method),
                call.Instruction ?? call.Line, call.Line))
            .ToImmutableArray();
        var persistence = calls
            .Where(IsPersistenceCall)
            .Select(call => new Mo2PapyrusPersistenceAccess(
                call.Routine ?? "<unknown>", call.State ?? string.Empty, call.Receiver ?? "self", call.Method,
                call.Arguments.IsDefaultOrEmpty ? null : call.Arguments.FirstOrDefault(IsQuoted),
                call.Instruction ?? call.Line, call.Line))
            .ToImmutableArray();
        var controlled = actions.Select(action => action.Target)
            .Where(target => target.Length > 0 && !target.StartsWith("::", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var lifecycle = actions.Select(action => action.Routine)
            .Concat(persistence.Select(access => access.Routine))
            .Where(IsLifecycleRoutine)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var hasEnable = actions.Any(action => action.Action == "Enable");
        var hasDisable = actions.Any(action => action.Action == "Disable");
        var exclusive = controlled.Length >= 2 && hasEnable && hasDisable;
        var lifecycleReapply = lifecycle.Length > 0 && actions.Any(action => lifecycle.Contains(action.Routine, StringComparer.OrdinalIgnoreCase));
        var stateActionRoutines = actions.Select(action => action.Routine).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stateVariableNames = variables.Select(variable => variable.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectionVariables = assignments
            .Where(assignment => stateActionRoutines.Contains(assignment.Routine) &&
                                 stateVariableNames.Contains(assignment.Target) &&
                                 !assignment.Target.StartsWith("::", StringComparison.Ordinal) &&
                                 IsSelectorExpression(assignment.Expression))
            .Select(assignment => assignment.Target)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var status = exclusive && lifecycleReapply && persistence.Length > 0 ? "SelectionReconciliationObserved"
            : lifecycleReapply ? "LifecycleReapplicationObserved"
            : actions.Length > 0 ? "ReferenceStateControlObserved"
            : "NoReferenceStateControlObserved";
        var finding = status switch
        {
            "SelectionReconciliationObserved" => "The script contains an exclusive enabled-reference selection, configuration access, and lifecycle-time state reapplication.",
            "LifecycleReapplicationObserved" => "The script changes reference enabled state from a lifecycle routine; a later lifecycle call can overwrite an earlier visible state.",
            "ReferenceStateControlObserved" when selectionVariables.Length > 0 => $"The script changes reference enabled state and assigns selector variable(s) {string.Join(", ", selectionVariables)}, but no lifecycle-time reconciliation was found.",
            "ReferenceStateControlObserved" => "The script directly changes one or more reference enabled states, but no lifecycle reapplication was proven.",
            _ => "No direct reference Enable/Disable control was proven in this script.",
        };
        var solution = status switch
        {
            "SelectionReconciliationObserved" => "Compare the persisted selector read by the lifecycle routine with the value written by the selection handler. If they differ, persist the selected value before reconciling the exclusive reference group; verify with current save/runtime evidence before changing the script.",
            "LifecycleReapplicationObserved" => "Trace the lifecycle routine's selector inputs and every caller before proposing a change; do not force an initially-disabled reference on independently of the controller.",
            "ReferenceStateControlObserved" when selectionVariables.Length > 0 => "Reapply the selected reference group from a guarded load/reset routine and verify the selected value plus all mutually exclusive references against current save/runtime evidence before changing the script.",
            "ReferenceStateControlObserved" => "Correlate these call sites with the controller's initialization and saved-state inputs before proposing a repair.",
            _ => "Inspect the other scripts attached to the owning quest/reference and correlate them with current plugin and save evidence.",
        };
        return new(status, controlled, lifecycle, actions, persistence, finding, solution, selectionVariables);
    }

    private static bool IsPersistenceCall(Mo2PapyrusCall call)
    {
        var method = call.Method;
        if (!(method.StartsWith("Get", StringComparison.OrdinalIgnoreCase) || method.StartsWith("Set", StringComparison.OrdinalIgnoreCase))) return false;
        var receiver = call.Receiver ?? string.Empty;
        return receiver.Contains("StorageUtil", StringComparison.OrdinalIgnoreCase) ||
               receiver.Contains("JContainers", StringComparison.OrdinalIgnoreCase) ||
               receiver.Equals("JValue", StringComparison.OrdinalIgnoreCase) ||
               receiver.Equals("JMap", StringComparison.OrdinalIgnoreCase) ||
               receiver.Equals("JArray", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("GetGameSetting", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("SetGameSetting", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("GetINI", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("SetINI", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("GetModSetting", StringComparison.OrdinalIgnoreCase) ||
               method.StartsWith("SetModSetting", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleRoutine(string name) => LifecycleRoutines.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith("OnGame", StringComparison.OrdinalIgnoreCase);

    private static bool IsQuoted(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"';
    private static bool IsSelectorExpression(string value) => SimpleIdentifier().IsMatch(value) &&
        !PapyrusLiteralWords.Contains(value, StringComparer.OrdinalIgnoreCase);
    private static string NormalizeReferenceAction(string method) => method.StartsWith("Enable", StringComparison.OrdinalIgnoreCase) ? "Enable" : "Disable";
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private static readonly string[] ControlWords = ["if", "elseif", "while", "return"];
    private static readonly string[] ReferenceStateMethods = ["Enable", "EnableNoWait", "Disable", "DisableNoWait"];
    // SkyUI configuration events occur only while the player interacts with the
    // MCM and therefore cannot prove that state is reconciled after loading a
    // save or reattaching a reference. Keep those UI callbacks out of this set.
    private static readonly string[] LifecycleRoutines = ["OnInit", "OnPlayerLoadGame", "OnCellAttach", "OnLoad", "OnReset", "OnUpdate"];
    private static readonly string[] PapyrusLiteralWords = ["true", "false", "none", "self"];
    private static Mo2PapyrusInspection Failure(Mo2PapyrusInspectionStatus status, string format, string code, string detail) =>
        new(status, format, null, null, [], [], [], [], [], [new(code, detail)], [], null);

    [GeneratedRegex(@";.*$")] private static partial Regex Comment();
    [GeneratedRegex(@"^scriptname\s+(\w+)(?:\s+extends\s+(\w+))?", RegexOptions.IgnoreCase)] private static partial Regex Script();
    [GeneratedRegex(@"^(\w+)\s+property\s+(\w+)(?:\s*=\s*([^\s]+))?(?:\s+(auto(?:readonly)?))?", RegexOptions.IgnoreCase)] private static partial Regex Property();
    [GeneratedRegex(@"^(\w+(?:\[\])?)\s+(\w+)(?:\s*=\s*(.+))?$")] private static partial Regex Variable();
    [GeneratedRegex(@"^(event|function)\s+(\w+)", RegexOptions.IgnoreCase)] private static partial Regex Routine();
    [GeneratedRegex(@"^end(?:event|function)$", RegexOptions.IgnoreCase)] private static partial Regex EndRoutine();
    [GeneratedRegex(@"^(?:if|elseif|while)\s+(.+)$", RegexOptions.IgnoreCase)] private static partial Regex Condition();
    [GeneratedRegex(@"^([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*=\s*(?!=)(.+)$")] private static partial Regex Assignment();
    [GeneratedRegex(@"^[A-Za-z_]\w*$")] private static partial Regex SimpleIdentifier();
    [GeneratedRegex(@"(?:(\w+)\s*\.)?(\w+)\s*\(")] private static partial Regex Call();

    private sealed class SkyrimPexReader(ReadOnlyMemory<byte> content)
    {
        private const int MaximumInstructions = 1_000_000;
        private static readonly int[] FixedArguments = [0, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 3, 3, 3, 3, 3, 1, 2, 2, 3, 2, 3, 1, 3, 3, 3, 2, 2, 3, 3, 4, 4];
        private static readonly bool[] VariableArguments = [false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, false, true, true, true, false, false, false, false, false, false, false, false, false, false];
        private static readonly string[] OpCodeNames = ["nop", "iadd", "fadd", "isub", "fsub", "imul", "fmul", "idiv", "fdiv", "imod", "not", "ineg", "fneg", "assign", "cast", "cmp_eq", "cmp_lt", "cmp_lte", "cmp_gt", "cmp_gte", "jmp", "jmpt", "jmpf", "callmethod", "callparent", "callstatic", "return", "strcat", "propget", "propset", "array_create", "array_length", "array_getelement", "array_setelement", "array_findelement", "array_rfindelement"];

        private readonly ReadOnlyMemory<byte> data = content;
        private readonly List<string> strings = [];
        private readonly Dictionary<string, ushort[]> debugLines = new(StringComparer.OrdinalIgnoreCase);
        private readonly ImmutableArray<Mo2PapyrusProperty>.Builder properties = ImmutableArray.CreateBuilder<Mo2PapyrusProperty>();
        private readonly ImmutableArray<Mo2PapyrusRoutine>.Builder routines = ImmutableArray.CreateBuilder<Mo2PapyrusRoutine>();
        private readonly ImmutableArray<Mo2PapyrusCondition>.Builder conditions = ImmutableArray.CreateBuilder<Mo2PapyrusCondition>();
        private readonly ImmutableArray<Mo2PapyrusCall>.Builder calls = ImmutableArray.CreateBuilder<Mo2PapyrusCall>();
        private readonly ImmutableArray<Mo2PapyrusInstruction>.Builder instructions = ImmutableArray.CreateBuilder<Mo2PapyrusInstruction>();
        private readonly ImmutableArray<Mo2PapyrusVariable>.Builder scriptVariables = ImmutableArray.CreateBuilder<Mo2PapyrusVariable>();
        private readonly ImmutableArray<Mo2PapyrusAssignment>.Builder assignments = ImmutableArray.CreateBuilder<Mo2PapyrusAssignment>();
        private int position;
        private int instructionTotal;
        private string? scriptName;
        private string? extends;

        public ParsedPex Read()
        {
            if (UInt32() != SkyrimPexMagic) throw new InvalidDataException("The file is not big-endian Skyrim PEX bytecode.");
            _ = Byte();
            _ = Byte();
            var gameId = UInt16();
            if (gameId != 1) throw new InvalidDataException($"PEX game id {gameId} is not Skyrim.");
            Skip(8);
            _ = String();
            _ = String();
            _ = String();
            var stringCount = UInt16();
            for (var index = 0; index < stringCount; index++) strings.Add(String());
            ReadDebugInfo();
            var userFlagCount = UInt16();
            for (var index = 0; index < userFlagCount; index++) { _ = StringIndex(); _ = Byte(); }
            var objectCount = UInt16();
            if (objectCount == 0) throw new InvalidDataException("The PEX contains no script objects.");
            for (var index = 0; index < objectCount; index++) ReadObject();
            if (position != data.Length) throw new InvalidDataException("The PEX contains unaccounted trailing bytes.");
            return new(scriptName, extends, properties.ToImmutable(), routines.ToImmutable(), conditions.ToImmutable(), calls.ToImmutable(), strings.ToImmutableArray(), instructions.ToImmutable(), scriptVariables.ToImmutable(), assignments.ToImmutable());
        }

        private void ReadDebugInfo()
        {
            if (Byte() == 0) return;
            Skip(8);
            var count = UInt16();
            for (var index = 0; index < count; index++)
            {
                var objectName = StringIndex();
                var stateName = StringIndex();
                var functionName = StringIndex();
                _ = Byte();
                var lineCount = UInt16();
                var lines = new ushort[lineCount];
                for (var line = 0; line < lines.Length; line++) lines[line] = UInt16();
                debugLines[DebugKey(objectName, stateName, functionName)] = lines;
            }
        }

        private void ReadObject()
        {
            var objectName = StringIndex();
            _ = UInt32();
            var parent = StringIndex();
            _ = StringIndex();
            _ = UInt32();
            _ = StringIndex();
            scriptName ??= objectName;
            extends ??= NullIfEmpty(parent);

            var variables = new Dictionary<string, (string Type, PexValue Value)>(StringComparer.OrdinalIgnoreCase);
            var variableCount = UInt16();
            for (var index = 0; index < variableCount; index++)
            {
                var name = StringIndex();
                var type = StringIndex();
                _ = UInt32();
                var value = Value();
                variables[name] = (type, value);
                scriptVariables.Add(new(name, type, value.Render() == "None" ? null : value.Render()));
            }

            var propertyCount = UInt16();
            for (var index = 0; index < propertyCount; index++)
            {
                var name = StringIndex();
                var type = StringIndex();
                _ = StringIndex();
                _ = UInt32();
                var flags = Byte();
                if ((flags & 4) != 0)
                {
                    var autoVariable = StringIndex();
                    var defaultValue = variables.TryGetValue(autoVariable, out var variable) ? variable.Value.Render() : null;
                    properties.Add(new(name, type, true, defaultValue == "None" ? null : defaultValue));
                }
                else
                {
                    properties.Add(new(name, type, false, null));
                    if ((flags & 1) != 0) ReadFunction(objectName, string.Empty, $"get_{name}");
                    if ((flags & 2) != 0) ReadFunction(objectName, string.Empty, $"set_{name}");
                }
            }

            var stateCount = UInt16();
            for (var stateIndex = 0; stateIndex < stateCount; stateIndex++)
            {
                var state = StringIndex();
                var functionCount = UInt16();
                for (var functionIndex = 0; functionIndex < functionCount; functionIndex++)
                {
                    var routine = StringIndex();
                    ReadFunction(objectName, state, routine);
                }
            }
        }

        private void ReadFunction(string objectName, string state, string routine)
        {
            _ = StringIndex();
            _ = StringIndex();
            _ = UInt32();
            _ = Byte();
            ReadTypedNames();
            ReadTypedNames();
            var count = UInt16();
            instructionTotal = checked(instructionTotal + count);
            if (instructionTotal > MaximumInstructions) throw new PexLimitException($"PEX instruction count exceeds {MaximumInstructions}.");
            var lineNumbers = ResolveDebugLines(objectName, state, routine, count);
            var raw = new List<RawInstruction>(count);
            for (var index = 0; index < count; index++)
            {
                var opcode = Byte();
                if (opcode >= FixedArguments.Length) throw new InvalidDataException($"Unsupported Skyrim opcode 0x{opcode:X2}.");
                var operands = new List<PexValue>(FixedArguments[opcode] + 4);
                for (var argument = 0; argument < FixedArguments[opcode]; argument++) operands.Add(Value());
                if (VariableArguments[opcode])
                {
                    var argumentCount = Value();
                    if (argumentCount.Kind != PexValueKind.Integer || argumentCount.Integer < 0 || argumentCount.Integer > ushort.MaxValue)
                        throw new InvalidDataException("A PEX call contains an invalid variable argument count.");
                    for (var argument = 0; argument < argumentCount.Integer; argument++) operands.Add(Value());
                }
                raw.Add(new(opcode, operands.ToImmutableArray(), lineNumbers[index]));
            }
            routines.Add(new(routine.StartsWith("On", StringComparison.OrdinalIgnoreCase) ? "Event" : "Function", routine, lineNumbers.FirstOrDefault(value => value > 0)));
            ReduceFunction(state, routine, raw);
        }

        private void ReduceFunction(string state, string routine, List<RawInstruction> raw)
        {
            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < raw.Count; index++)
            {
                var item = raw[index];
                var rendered = item.Operands.Select(value => Resolve(value, aliases)).ToImmutableArray();
                instructions.Add(new(routine, state, index, item.Line, OpCodeNames[item.OpCode], rendered));
                switch (item.OpCode)
                {
                    case 13:
                    case 14:
                        assignments.Add(new(routine, state, item.Operands[0].Render(), Resolve(item.Operands[1], aliases), index, item.Line));
                        AssignAlias(item.Operands[0], Resolve(item.Operands[1], aliases), aliases);
                        break;
                    case 21:
                    case 22:
                        conditions.Add(new($"{OpCodeNames[item.OpCode]} {Resolve(item.Operands[0], aliases)}", item.Line));
                        break;
                    case 23:
                    {
                        var method = item.Operands[0].Text ?? item.Operands[0].Render();
                        var receiver = Resolve(item.Operands[1], aliases);
                        var arguments = item.Operands.Skip(3).Select(value => Resolve(value, aliases)).ToImmutableArray();
                        calls.Add(new(receiver, method, item.Line, routine, state, index, arguments));
                        ClearResultAlias(item.Operands[2], aliases);
                        break;
                    }
                    case 24:
                    {
                        var method = item.Operands[0].Text ?? item.Operands[0].Render();
                        calls.Add(new("parent", method, item.Line, routine, state, index, item.Operands.Skip(2).Select(value => Resolve(value, aliases)).ToImmutableArray()));
                        ClearResultAlias(item.Operands[1], aliases);
                        break;
                    }
                    case 25:
                    {
                        var receiver = item.Operands[0].Text ?? item.Operands[0].Render();
                        var method = item.Operands[1].Text ?? item.Operands[1].Render();
                        calls.Add(new(receiver, method, item.Line, routine, state, index, item.Operands.Skip(3).Select(value => Resolve(value, aliases)).ToImmutableArray()));
                        ClearResultAlias(item.Operands[2], aliases);
                        break;
                    }
                    case 28:
                    {
                        var property = item.Operands[0].Text ?? item.Operands[0].Render();
                        var owner = Resolve(item.Operands[1], aliases);
                        AssignAlias(item.Operands[2], owner.Equals("self", StringComparison.OrdinalIgnoreCase) ? property : $"{owner}.{property}", aliases);
                        break;
                    }
                    case 29:
                    {
                        var property = item.Operands[0].Text ?? item.Operands[0].Render();
                        var owner = Resolve(item.Operands[1], aliases);
                        assignments.Add(new(routine, state, owner.Equals("self", StringComparison.OrdinalIgnoreCase) ? property : $"{owner}.{property}", Resolve(item.Operands[2], aliases), index, item.Line));
                        break;
                    }
                }
            }
        }

        private ushort[] ResolveDebugLines(string objectName, string state, string routine, int count)
        {
            if (debugLines.TryGetValue(DebugKey(objectName, state, routine), out var lines) && lines.Length == count) return lines;
            return new ushort[count];
        }

        private void ReadTypedNames()
        {
            var count = UInt16();
            for (var index = 0; index < count; index++) { _ = StringIndex(); _ = StringIndex(); }
        }

        private PexValue Value()
        {
            var kind = (PexValueKind)Byte();
            return kind switch
            {
                PexValueKind.None => new(kind, null, 0, 0, false),
                PexValueKind.Identifier or PexValueKind.String => new(kind, StringIndex(), 0, 0, false),
                PexValueKind.Integer => new(kind, null, unchecked((int)UInt32()), 0, false),
                PexValueKind.Float => new(kind, null, 0, BitConverter.Int32BitsToSingle(unchecked((int)UInt32())), false),
                PexValueKind.Boolean => new(kind, null, 0, 0, Byte() != 0),
                _ => throw new InvalidDataException($"Invalid PEX value type {(byte)kind}.")
            };
        }

        private string StringIndex()
        {
            var index = UInt16();
            return index < strings.Count ? strings[index] : throw new InvalidDataException($"PEX string index {index} is outside the string table.");
        }

        private string String()
        {
            var length = UInt16();
            Ensure(length);
            var result = new UTF8Encoding(false, true).GetString(data.Span.Slice(position, length));
            position += length;
            return result;
        }

        private byte Byte() { Ensure(1); return data.Span[position++]; }
        private ushort UInt16() { Ensure(2); var value = BinaryPrimitives.ReadUInt16BigEndian(data.Span[position..]); position += 2; return value; }
        private uint UInt32() { Ensure(4); var value = BinaryPrimitives.ReadUInt32BigEndian(data.Span[position..]); position += 4; return value; }
        private void Skip(int count) { Ensure(count); position += count; }
        private void Ensure(int count)
        {
            if (count < 0 || position > data.Length - count) throw new InvalidDataException("The PEX is truncated.");
        }
        private static string DebugKey(string objectName, string state, string routine) => $"{objectName}\0{state}\0{routine}";
        private static string Resolve(PexValue value, Dictionary<string, string> aliases) =>
            value.Kind == PexValueKind.Identifier && value.Text is not null && aliases.TryGetValue(value.Text, out var resolved) ? resolved : value.Render();
        private static void AssignAlias(PexValue destination, string value, Dictionary<string, string> aliases)
        {
            if (destination.Kind == PexValueKind.Identifier && destination.Text is not null) aliases[destination.Text] = value;
        }
        private static void ClearResultAlias(PexValue destination, Dictionary<string, string> aliases)
        {
            if (destination.Kind == PexValueKind.Identifier && destination.Text is not null) aliases.Remove(destination.Text);
        }
    }

    private enum PexValueKind : byte { None = 0, Identifier = 1, String = 2, Integer = 3, Float = 4, Boolean = 5 }
    private sealed record PexValue(PexValueKind Kind, string? Text, int Integer, float Float, bool Boolean)
    {
        public string Render() => Kind switch
        {
            PexValueKind.None => "None",
            PexValueKind.Identifier => Text ?? string.Empty,
            PexValueKind.String => $"\"{Text?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"",
            PexValueKind.Integer => Integer.ToString(CultureInfo.InvariantCulture),
            PexValueKind.Float => Float.ToString("R", CultureInfo.InvariantCulture),
            PexValueKind.Boolean => Boolean ? "true" : "false",
            _ => string.Empty,
        };
    }
    private sealed record RawInstruction(byte OpCode, ImmutableArray<PexValue> Operands, int Line);
    private sealed record ParsedPex(
        string? ScriptName,
        string? Extends,
        ImmutableArray<Mo2PapyrusProperty> Properties,
        ImmutableArray<Mo2PapyrusRoutine> Routines,
        ImmutableArray<Mo2PapyrusCondition> Conditions,
        ImmutableArray<Mo2PapyrusCall> Calls,
        ImmutableArray<string> Symbols,
        ImmutableArray<Mo2PapyrusInstruction> Instructions,
        ImmutableArray<Mo2PapyrusVariable> Variables,
        ImmutableArray<Mo2PapyrusAssignment> Assignments);
    private sealed class PexLimitException(string message) : Exception(message);
}
