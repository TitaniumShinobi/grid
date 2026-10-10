using System.Text;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>
/// Bounded GR-20 inspection of explicitly identified instruction sections in admitted sources.
/// Candidate-only. Does not publish, refresh, project DIF, or execute sourced guidance.
/// </summary>
public static class CanonicalOperationalInstructionInspector
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static CanonicalOperationalInstructionCandidate Inspect(
        IReadOnlyList<RegistrationSourceArtifact> sources,
        IReadOnlyList<OperationalInstructionSectionRequest> requests,
        CanonicalRegistrationCandidate? boundCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(requests);
        var frozen = sources.Select(item => new RegistrationSourceArtifact(item.Source, item.Bytes.ToArray()))
            .OrderBy(item => item.Source.Id, StringComparer.Ordinal).ToArray();
        foreach (var item in frozen)
            if (CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256)
                throw new InvalidDataException("Source bytes or adapter coordinates do not match the admitted manifest.");

        if (boundCandidate is not null &&
            (boundCandidate.FormatVersion != 1 ||
             boundCandidate.PublicationState != CanonicalRegistrationEncoding.NotPublished))
            throw new InvalidDataException("Operational-instruction inspection can bind only a NOT_PUBLISHED registration candidate.");

        var sourceMap = new Dictionary<string, RegistrationSourceArtifact>(StringComparer.Ordinal);
        foreach (var item in frozen)
        {
            RequireText(item.Source.Id);
            if (!sourceMap.TryAdd(item.Source.Id, item))
                throw new InvalidDataException("Duplicate source identity.");
            if (!Uri.TryCreate(item.Source.Uri, UriKind.Absolute, out _) || !Enum.IsDefined(item.Source.Kind) ||
                !CanonicalRegistrationEncoding.IsDigest(item.Source.Sha256))
                throw new InvalidDataException("Invalid source coordinate.");
        }

        Unique(requests.Select(request => request.Id), "instruction section request");
        var extracted = new List<(OperationalInstructionSectionRequest Request, RegisteredOperationalInstruction Instruction, RegistrationEvidence Evidence)>();
        foreach (var request in requests.OrderBy(value => value.Id, StringComparer.Ordinal))
        {
            RequireText(request.Id);
            RequireText(request.SourceId);
            RequireText(request.Locator);
            if (!Enum.IsDefined(request.Kind))
                throw new InvalidDataException("Instruction section kind is invalid.");
            if (!sourceMap.TryGetValue(request.SourceId, out var artifact))
                throw new InvalidDataException("Instruction section request refers to an unadmitted source.");
            if (request.Applicability is null || request.Applicability.Any(value => value is null ||
                string.IsNullOrWhiteSpace(value.GameId) ||
                (value.ProfileId is not null && string.IsNullOrWhiteSpace(value.ProfileId)) ||
                value.EvidenceIds is null))
                throw new InvalidDataException("Instruction applicability requires a game and admitted evidence coordinates.");

            var basis = BasisFor(artifact.Source.Kind);
            var evidenceId = request.Id + ":evidence";
            var (presence, verbatim) = ReadSection(artifact.Bytes, request);
            var evidence = new RegistrationEvidence(evidenceId, request.SourceId, request.Locator, basis);
            var applicability = request.Applicability
                .Select(value => value with { EvidenceIds = CanonicalRegistrationEncoding.Ordered(value.EvidenceIds.Append(evidenceId)) })
                .OrderBy(CanonicalRegistrationEncoding.Digest, StringComparer.Ordinal).ToArray();
            extracted.Add((request, new RegisteredOperationalInstruction(
                request.Id,
                request.SourceId,
                request.Locator,
                request.Kind,
                OperationalInstructionInspection.CategoryIdFor(request.Kind),
                presence,
                verbatim,
                OperationalInstructionInspection.ContentDigest(verbatim),
                applicability,
                [evidenceId]), evidence));
        }

        foreach (var item in frozen)
            if (CanonicalRegistrationEncoding.Digest(item.Bytes) != item.Source.Sha256)
                throw new InvalidDataException("An inspector modified its admitted source bytes.");

        var rulings = new List<RegistrationRuling>();
        foreach (var item in extracted)
        {
            var reason = item.Instruction.Presence switch
            {
                OperationalInstructionSectionPresence.Present => "instruction-section-admitted",
                OperationalInstructionSectionPresence.Missing => "instruction-section-missing",
                OperationalInstructionSectionPresence.ProvenAbsence => "instruction-section-absent",
                OperationalInstructionSectionPresence.EmptyUnresolved => "instruction-section-empty",
                _ => throw new InvalidDataException("Instruction section presence is invalid."),
            };
            var outcome = item.Instruction.Presence == OperationalInstructionSectionPresence.Present
                ? RegistrationOutcome.Correlated
                : RegistrationOutcome.Unresolved;
            rulings.Add(new(item.Request.Id, "instruction", outcome, reason, [item.Evidence.Id]));
        }

        foreach (var group in extracted
                     .Where(item => item.Instruction.Presence == OperationalInstructionSectionPresence.Present &&
                                    item.Instruction.VerbatimText.Length > 0)
                     .GroupBy(item => ConflictKey(item.Instruction)))
        {
            var texts = group.Select(item => item.Instruction.VerbatimText).Distinct(StringComparer.Ordinal).ToArray();
            if (texts.Length < 2) continue;
            foreach (var item in group)
                rulings.Add(new(item.Request.Id, "instruction", RegistrationOutcome.Ambiguous,
                    "instruction-guidance-conflicting", [item.Evidence.Id]));
        }

        return new(
            OperationalInstructionInspection.FormatVersion,
            OperationalInstructionInspection.InspectorVersion,
            OperationalInstructionInspection.CategoryId,
            frozen.Select(item => item.Source).ToArray(),
            requests.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            extracted.Select(item => item.Evidence).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            extracted.Select(item => item.Instruction).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray(),
            rulings.OrderBy(value => value.Stage, StringComparer.Ordinal)
                .ThenBy(value => value.ClaimId, StringComparer.Ordinal)
                .ThenBy(value => value.Reason, StringComparer.Ordinal).ToArray(),
            boundCandidate is null ? null : CanonicalRegistrationEncoding.Digest(boundCandidate),
            CanonicalRegistrationEncoding.NotPublished);
    }

    public static void Verify(
        CanonicalOperationalInstructionCandidate candidate,
        IReadOnlyList<RegistrationSourceArtifact> sources,
        CanonicalRegistrationCandidate? boundCandidate = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.FormatVersion != OperationalInstructionInspection.FormatVersion ||
            candidate.InspectorVersion != OperationalInstructionInspection.InspectorVersion ||
            candidate.InspectionCategory != OperationalInstructionInspection.CategoryId ||
            candidate.PublicationState != CanonicalRegistrationEncoding.NotPublished)
            throw new InvalidDataException("Unsupported or published operational-instruction candidate.");
        var expected = Inspect(sources, candidate.Requests, boundCandidate);
        if (!CanonicalRegistrationEncoding.Bytes(candidate).AsSpan()
                .SequenceEqual(CanonicalRegistrationEncoding.Bytes(expected)))
            throw new InvalidDataException("Operational-instruction candidate failed deterministic verification.");
    }

    private static (OperationalInstructionSectionPresence Presence, string Verbatim) ReadSection(
        byte[] bytes, OperationalInstructionSectionRequest request)
    {
        var locator = request.Locator;
        if (locator.StartsWith("/", StringComparison.Ordinal))
            return ReadJsonPointer(bytes, request);
        if (locator.StartsWith("#", StringComparison.Ordinal))
            return ReadMarkdownHeading(bytes, request);
        throw new InvalidDataException("Instruction section locator must be an explicit JSON pointer or markdown heading.");
    }

    private static (OperationalInstructionSectionPresence Presence, string Verbatim) ReadMarkdownHeading(
        byte[] bytes, OperationalInstructionSectionRequest request)
    {
        var text = Decode(bytes);
        var lines = text.Split('\n');
        var heading = request.Locator;
        var index = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (string.Equals(lines[i], heading, StringComparison.Ordinal))
            {
                if (index >= 0)
                    throw new InvalidDataException("Instruction heading locator matched more than one exact line.");
                index = i;
            }
        }
        if (index < 0)
            return (OperationalInstructionSectionPresence.Missing, string.Empty);

        var level = HeadingLevel(heading);
        var end = lines.Length;
        for (var i = index + 1; i < lines.Length; i++)
        {
            var candidateLevel = HeadingLevel(lines[i]);
            if (candidateLevel > 0 && candidateLevel <= level)
            {
                end = i;
                break;
            }
        }

        var body = string.Join('\n', lines[(index + 1)..end]).Trim('\n');
        return Classify(body, request);
    }

    private static (OperationalInstructionSectionPresence Presence, string Verbatim) ReadJsonPointer(
        byte[] bytes, OperationalInstructionSectionRequest request)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("JSON instruction locator requires parseable source bytes.", exception);
        }

        using (document)
        {
            if (!TryGetPointer(document.RootElement, request.Locator, out var element))
                return (OperationalInstructionSectionPresence.Missing, string.Empty);
            if (element.ValueKind == JsonValueKind.Null)
                return Classify(string.Empty, request, provenAbsence: request.EmptyIsAbsence);
            if (element.ValueKind == JsonValueKind.String)
                return Classify(element.GetString() ?? string.Empty, request);
            if (element.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("JSON instruction section must be a string, object, or null.");

            var absent = element.TryGetProperty("absent", out var absentElement) &&
                         absentElement.ValueKind == JsonValueKind.True;
            var text = element.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;
            return Classify(text, request, provenAbsence: absent);
        }
    }

    private static bool TryGetPointer(JsonElement root, string pointer, out JsonElement value)
    {
        value = root;
        if (pointer == "/") return false;
        var tokens = pointer[1..].Split('/');
        foreach (var raw in tokens)
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(token, out value))
                    return false;
                continue;
            }
            if (value.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) &&
                index >= 0 && index < value.GetArrayLength())
            {
                value = value[index];
                continue;
            }
            return false;
        }
        return true;
    }

    private static (OperationalInstructionSectionPresence Presence, string Verbatim) Classify(
        string verbatim, OperationalInstructionSectionRequest request, bool provenAbsence = false)
    {
        if (provenAbsence ||
            (!string.IsNullOrEmpty(request.AbsenceMarker) &&
             string.Equals(verbatim, request.AbsenceMarker, StringComparison.Ordinal)))
            return (OperationalInstructionSectionPresence.ProvenAbsence, verbatim);
        if (verbatim.Length == 0)
            return request.EmptyIsAbsence
                ? (OperationalInstructionSectionPresence.ProvenAbsence, verbatim)
                : (OperationalInstructionSectionPresence.EmptyUnresolved, verbatim);
        return (OperationalInstructionSectionPresence.Present, verbatim);
    }

    private static string Decode(byte[] bytes)
    {
        var text = StrictUtf8.GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static int HeadingLevel(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#') level++;
        return level > 0 && level < line.Length && line[level] == ' ' ? level : 0;
    }

    private static EvidenceVerificationKind BasisFor(KnowledgeSourceKind kind) =>
        kind is KnowledgeSourceKind.ReferenceProvider or KnowledgeSourceKind.OfficialProvider or KnowledgeSourceKind.ModProvider
            ? EvidenceVerificationKind.ReferenceVerified
            : EvidenceVerificationKind.FileVerified;

    private static string ConflictKey(RegisteredOperationalInstruction instruction) =>
        string.Join('\u001f', instruction.Kind.ToString(),
            string.Join('\u001e', instruction.Applicability.Select(value => value.GameId + "\u001d" + (value.ProfileId ?? ""))));

    private static void RequireText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Required instruction coordinate is empty.");
    }

    private static void Unique(IEnumerable<string> values, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireText(value);
            if (!seen.Add(value)) throw new InvalidDataException("Duplicate " + kind + " identity.");
        }
    }
}
