using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Classifies one already-inspected archive relative to exact installed plugin
/// evidence. The result is evidence, not an update recommendation or repair
/// authorization.
/// </summary>
public sealed class Mo2ArchiveCandidateAssessmentService
{
    public Mo2ArchiveCandidateAssessmentResult Assess(Mo2ArchiveCandidateAssessmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(request.PrimaryPluginName, nameof(request.PrimaryPluginName));
        ValidateHash(request.InstalledPrimaryPluginSha256, nameof(request.InstalledPrimaryPluginSha256));
        if (!string.IsNullOrWhiteSpace(request.InstalledPrimaryArchiveSha256))
            ValidateHash(request.InstalledPrimaryArchiveSha256, nameof(request.InstalledPrimaryArchiveSha256));

        var entries = request.Inspection.Entries.IsDefault ? [] : request.Inspection.Entries;
        var candidatePlugin = ResolvePrimaryPluginEntry(entries, request.PrimaryPluginName,
            request.PrimaryPluginEntryPath);
        var candidateArchive = ResolvePrimaryArchiveEntry(entries, request.PrimaryPluginName,
            request.PrimaryArchiveEntryPath, request.InstalledPrimaryArchiveSha256);
        var pairStatus = (candidatePlugin is not null, candidateArchive is not null) switch
        {
            (true, true) => Mo2ArchivePayloadPairStatus.Complete,
            (true, false) => Mo2ArchivePayloadPairStatus.PluginOnly,
            (false, true) => Mo2ArchivePayloadPairStatus.ArchiveOnly,
            _ => Mo2ArchivePayloadPairStatus.Missing,
        };

        var selectionRequired = request.Inspection.Status == Mo2ArchiveInspectionStatus.Rejected &&
            request.Inspection.Fomod.Status == Mo2FomodStatus.SelectionRequired &&
            request.Inspection.Issues.Length > 0 &&
            request.Inspection.Issues.All(issue => issue.Code == "mo2.repair.fomod.selection_required");
        var inspectionUsable = request.Inspection.Status == Mo2ArchiveInspectionStatus.Complete || selectionRequired;
        var samePlugin = candidatePlugin?.Sha256 is not null &&
            candidatePlugin.Sha256.Equals(request.InstalledPrimaryPluginSha256, StringComparison.OrdinalIgnoreCase);
        var versionRelation = CompareVersions(request.InstalledVersion, request.CandidateVersion);
        var role = !inspectionUsable
            ? Mo2ArchiveCandidateRole.Rejected
            : pairStatus != Mo2ArchivePayloadPairStatus.Complete
                ? Mo2ArchiveCandidateRole.IncompleteCandidate
                : samePlugin
                    ? Mo2ArchiveCandidateRole.ExactRestoration
                    : versionRelation switch
                    {
                        Mo2ArchiveVersionRelation.Newer => Mo2ArchiveCandidateRole.CompleteUpdateCandidate,
                        Mo2ArchiveVersionRelation.Older => Mo2ArchiveCandidateRole.CompleteRollbackCandidate,
                        _ => Mo2ArchiveCandidateRole.CompleteAlternativeCandidate,
                    };
        var mixingPolicy = !inspectionUsable || pairStatus != Mo2ArchivePayloadPairStatus.Complete
            ? Mo2ArchiveMixingPolicy.CandidateUnusable
            : samePlugin
                ? Mo2ArchiveMixingPolicy.InstalledPluginCompatibleWithCandidateAssets
                : Mo2ArchiveMixingPolicy.CandidatePluginAndAssetsRequiredTogether;

        var dependentAssessments = (request.DependentPlugins.IsDefault ? [] : request.DependentPlugins)
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(value => AssessDependent(entries, value))
            .ToImmutableArray();
        var compatibilityStatus = role switch
        {
            Mo2ArchiveCandidateRole.Rejected => Mo2ArchiveCandidateCompatibilityStatus.CandidateRejected,
            Mo2ArchiveCandidateRole.IncompleteCandidate => Mo2ArchiveCandidateCompatibilityStatus.CandidateIncomplete,
            Mo2ArchiveCandidateRole.ExactRestoration => Mo2ArchiveCandidateCompatibilityStatus.NotRequiredForExactRestoration,
            _ => Mo2ArchiveCandidateCompatibilityStatus.RequiresDependentCompatibilityEvidence,
        };
        var requiredEvidence = BuildRequiredEvidence(role, dependentAssessments, selectionRequired);

        var unsigned = new
        {
            schemaVersion = 2,
            archiveSha256 = request.Inspection.ArchiveSha256?.ToUpperInvariant(),
            primaryPluginName = request.PrimaryPluginName,
            installedPrimaryPluginSha256 = request.InstalledPrimaryPluginSha256.ToUpperInvariant(),
            candidatePrimaryPluginSha256 = candidatePlugin?.Sha256?.ToUpperInvariant(),
            candidatePrimaryArchiveSha256 = candidateArchive?.Sha256?.ToUpperInvariant(),
            installedPrimaryArchiveSha256 = NormalizeOptional(request.InstalledPrimaryArchiveSha256)?.ToUpperInvariant(),
            installedVersion = NormalizeOptional(request.InstalledVersion),
            candidateVersion = NormalizeOptional(request.CandidateVersion),
            versionRelation,
            pairStatus,
            role,
            mixingPolicy,
            compatibilityStatus,
            dependentAssessments,
            requiredEvidence,
            candidatePrimaryPluginEntryPath = candidatePlugin?.NormalizedPath,
            candidatePrimaryArchiveEntryPath = candidateArchive?.NormalizedPath,
            selectionRequired,
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned)));

        return new(
            2,
            selectionRequired ? "SelectionRequired" : inspectionUsable ? "Complete" : "Rejected",
            request.Inspection.ArchivePath,
            request.Inspection.ArchiveSha256?.ToUpperInvariant(),
            request.PrimaryPluginName,
            request.InstalledPrimaryPluginSha256.ToUpperInvariant(),
            candidatePlugin?.Sha256?.ToUpperInvariant(),
            candidateArchive?.Sha256?.ToUpperInvariant(),
            NormalizeOptional(request.InstalledVersion),
            NormalizeOptional(request.CandidateVersion),
            versionRelation,
            pairStatus,
            role,
            mixingPolicy,
            compatibilityStatus,
            dependentAssessments,
            requiredEvidence,
            fingerprint,
            $"archive-candidate.{fingerprint[..24].ToLowerInvariant()}",
            candidatePlugin?.NormalizedPath,
            candidateArchive?.NormalizedPath,
            NormalizeOptional(request.InstalledPrimaryArchiveSha256)?.ToUpperInvariant());
    }

    private static Mo2BundledPluginAssessment AssessDependent(
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        Mo2InstalledDependentPluginEvidence evidence)
    {
        ValidateName(evidence.Name, nameof(evidence.Name));
        ValidateHash(evidence.Sha256, nameof(evidence.Sha256));
        var entry = string.IsNullOrWhiteSpace(evidence.CandidateEntryPath)
            ? FindUniqueLeafEntry(entries, evidence.Name)
            : FindExactEntry(entries, NormalizeEntryPath(evidence.CandidateEntryPath));
        var entryPath = entry?.NormalizedPath ??
            (string.IsNullOrWhiteSpace(evidence.CandidateEntryPath)
                ? null
                : NormalizeEntryPath(evidence.CandidateEntryPath));
        if (entry?.Sha256 is null)
        {
            return new(evidence.Name, evidence.Sha256.ToUpperInvariant(), entryPath, null,
                Mo2BundledPluginDisposition.NotBundled);
        }

        var disposition = entry.Sha256.Equals(evidence.Sha256, StringComparison.OrdinalIgnoreCase)
            ? Mo2BundledPluginDisposition.AlreadyMatchesCandidate
            : Mo2BundledPluginDisposition.ReplacementRequired;
        return new(evidence.Name, evidence.Sha256.ToUpperInvariant(), entryPath,
            entry.Sha256.ToUpperInvariant(), disposition);
    }

    private static ImmutableArray<string> BuildRequiredEvidence(
        Mo2ArchiveCandidateRole role,
        ImmutableArray<Mo2BundledPluginAssessment> dependents,
        bool selectionRequired)
    {
        var required = ImmutableArray.CreateBuilder<string>();
        if (role is not (Mo2ArchiveCandidateRole.CompleteUpdateCandidate or
            Mo2ArchiveCandidateRole.CompleteRollbackCandidate or
            Mo2ArchiveCandidateRole.CompleteAlternativeCandidate))
        {
            if (selectionRequired) required.Add("FomodSelectionVector");
            return required.ToImmutable();
        }

        required.Add("DependentPluginCompatibilityMatrix");
        if (selectionRequired) required.Add("FomodSelectionVector");
        if (dependents.Any(value => value.Disposition == Mo2BundledPluginDisposition.ReplacementRequired))
            required.Add("BundledPluginWinnerReplacement");
        if (dependents.Any(value => value.Disposition == Mo2BundledPluginDisposition.NotBundled))
            required.Add("UnbundledDependentPluginCompatibility");
        return required.ToImmutable();
    }

    private static Mo2ArchiveEntryInspection? FindExactEntry(
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        string normalizedPath)
    {
        var matches = entries.Where(value => !value.IsDirectory &&
            value.NormalizedPath.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"Archive entry '{normalizedPath}' is ambiguous.");
        return matches.SingleOrDefault();
    }

    private static Mo2ArchiveEntryInspection? ResolvePrimaryPluginEntry(
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        string pluginName,
        string? explicitPath) =>
        string.IsNullOrWhiteSpace(explicitPath)
            ? FindUniqueLeafEntry(entries, pluginName)
            : FindExactEntry(entries, NormalizeEntryPath(explicitPath));

    private static Mo2ArchiveEntryInspection? ResolvePrimaryArchiveEntry(
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        string pluginName,
        string? explicitPath,
        string? installedArchiveSha256)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return FindExactEntry(entries, NormalizeEntryPath(explicitPath));

        var pluginStem = Path.GetFileNameWithoutExtension(pluginName);
        var archives = entries.Where(value => !value.IsDirectory && value.Sha256 is not null &&
                Path.GetExtension(value.NormalizedPath) is { } extension &&
                (extension.Equals(".bsa", StringComparison.OrdinalIgnoreCase) ||
                 extension.Equals(".ba2", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (!string.IsNullOrWhiteSpace(installedArchiveSha256))
        {
            var installedMatches = archives.Where(value => value.Sha256!.Equals(
                installedArchiveSha256, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (installedMatches.Length == 1) return installedMatches[0];
        }
        var exactStem = archives.Where(value =>
                Path.GetFileNameWithoutExtension(value.NormalizedPath)
                    .Equals(pluginStem, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (exactStem.Length == 1) return exactStem[0];
        return archives.Length == 1 ? archives[0] : null;
    }

    private static Mo2ArchiveEntryInspection? FindUniqueLeafEntry(
        ImmutableArray<Mo2ArchiveEntryInspection> entries,
        string leafName)
    {
        var matches = entries.Where(value => !value.IsDirectory && value.Sha256 is not null &&
            Path.GetFileName(value.NormalizedPath).Equals(leafName, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Mo2ArchiveVersionRelation CompareVersions(string? installed, string? candidate)
    {
        installed = NormalizeOptional(installed);
        candidate = NormalizeOptional(candidate);
        if (installed is null || candidate is null) return Mo2ArchiveVersionRelation.Unavailable;
        if (installed.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return Mo2ArchiveVersionRelation.Same;
        if (TryParseVersion(installed, out var installedVersion) && TryParseVersion(candidate, out var candidateVersion))
        {
            var comparison = candidateVersion.CompareTo(installedVersion);
            return comparison > 0 ? Mo2ArchiveVersionRelation.Newer : comparison < 0
                ? Mo2ArchiveVersionRelation.Older : Mo2ArchiveVersionRelation.Same;
        }
        return Mo2ArchiveVersionRelation.DifferentUnordered;
    }

    private static bool TryParseVersion(string value, out Version version) =>
        Version.TryParse(value.Trim().TrimStart('v', 'V'), out version!);

    private static string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Archive entry paths are required.");
        var normalized = value.Replace('/', '\\').TrimStart('\\');
        if (Path.IsPathRooted(normalized) || normalized.Split('\\').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException($"Archive entry path '{value}' is invalid.");
        return normalized;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            throw new ArgumentException("A bounded plugin name is required.", parameterName);
    }

    private static void ValidateHash(string value, string parameterName)
    {
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 digest is required.", parameterName);
    }
}
