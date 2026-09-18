using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Reduces a complete, bounded archive inspection to exact Data-relative
/// requirements. It accepts only archive-root and explicit Data-directory
/// mappings; installer choices and guessed component folders remain unresolved.
/// </summary>
public sealed class Mo2ArchiveRequirementMatcher
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public Mo2ArchiveRequirementMatchResult Match(
        Mo2ArchiveInspectionResult inspection,
        IEnumerable<Mo2ArchiveRequiredFile> requirements)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(requirements);

        var normalizedRequirements = requirements
            .Select(value => new Mo2ArchiveRequiredFile(
                ValidateLeaf(value.PluginName, nameof(value.PluginName)),
                NormalizeRelative(value.RequiredVirtualPath)))
            .DistinctBy(value => (value.PluginName.ToUpperInvariant(), value.RequiredVirtualPath.ToUpperInvariant()))
            .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var complete = inspection.Status == Mo2ArchiveInspectionStatus.Complete;
        var issues = inspection.Issues.ToBuilder();
        var entrySetSha256 = complete ? HashEntrySet(inspection.Entries) : null;
        var candidates = new Dictionary<string, List<(Mo2ArchiveEntryInspection Entry, string Rule)>>(PathComparer);
        if (complete)
        {
            foreach (var entry in inspection.Entries.Where(value => !value.IsDirectory &&
                         !value.IsEncrypted && !value.IsLink && value.Sha256 is not null))
            {
                AddCandidate(entry.NormalizedPath, entry, "ArchiveRoot");
                const string dataPrefix = "Data\\";
                if (entry.NormalizedPath.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    AddCandidate(entry.NormalizedPath[dataPrefix.Length..], entry, "ExplicitDataDirectory");
                }
            }
        }

        var matches = ImmutableArray.CreateBuilder<Mo2ArchiveRequiredFileMatch>();
        var ambiguous = 0;
        foreach (var requirement in normalizedRequirements)
        {
            if (!candidates.TryGetValue(requirement.RequiredVirtualPath, out var found)) continue;
            var unique = found
                .DistinctBy(value => value.Entry.NormalizedPath, PathComparer)
                .ToArray();
            if (unique.Length != 1)
            {
                ambiguous++;
                issues.Add(new(
                    "mo2.repair.archive.requirement_ambiguous",
                    "More than one archive entry could map to the same required Data-relative path.",
                    requirement.RequiredVirtualPath));
                continue;
            }

            var candidate = unique[0];
            matches.Add(new(
                requirement.PluginName,
                requirement.RequiredVirtualPath,
                candidate.Entry.ArchivePath,
                candidate.Entry.NormalizedPath,
                candidate.Rule,
                candidate.Entry.ExpandedSize,
                candidate.Entry.Sha256!));
        }

        var orderedMatches = matches
            .OrderBy(value => value.PluginName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.RequiredVirtualPath, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var unsigned = new
        {
            schemaVersion = 1,
            status = complete ? "Complete" : inspection.Status.ToString(),
            archiveSha256 = inspection.ArchiveSha256,
            entrySetSha256,
            requirements = normalizedRequirements.Select(value => new
            {
                value.PluginName,
                value.RequiredVirtualPath,
            }),
            matches = orderedMatches.Select(value => new
            {
                value.PluginName,
                value.RequiredVirtualPath,
                value.NormalizedArchivePath,
                value.MappingRule,
                value.ExpandedSize,
                value.Sha256,
            }),
        };
        var evidenceHash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned));
        var evidenceId = $"archive-content.{Convert.ToHexString(evidenceHash)[..24].ToLowerInvariant()}";

        return new(
            2,
            complete ? "Complete" : inspection.Status.ToString(),
            inspection.ArchivePath,
            inspection.ArchiveLength,
            inspection.ArchiveSha256,
            inspection.Format,
            inspection.Entries.Count(value => !value.IsDirectory),
            entrySetSha256,
            normalizedRequirements.Length,
            orderedMatches.Length,
            ambiguous,
            orderedMatches,
            inspection.Fomod.Status,
            issues.ToImmutable(),
            evidenceId);

        void AddCandidate(string virtualPath, Mo2ArchiveEntryInspection entry, string rule)
        {
            var normalized = NormalizeRelative(virtualPath);
            if (!candidates.TryGetValue(normalized, out var list))
            {
                list = [];
                candidates.Add(normalized, list);
            }
            list.Add((entry, rule));
        }
    }

    private static string HashEntrySet(ImmutableArray<Mo2ArchiveEntryInspection> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries.OrderBy(value => value.NormalizedPath, PathComparer)
                     .ThenBy(value => value.Ordinal))
        {
            var line = string.Join('\u001f',
                entry.NormalizedPath,
                entry.IsDirectory ? "D" : "F",
                entry.ExpandedSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.Sha256 ?? string.Empty) + "\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string ValidateLeaf(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new ArgumentException($"{name} must be one safe leaf name.");
        return value;
    }

    private static string NormalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Replace('/', '\\').Normalize(NormalizationForm.FormC);
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('\\') || normalized.Contains(':'))
            throw new ArgumentException("Required paths must be safe Data-relative paths.");
        var segments = normalized.Split('\\');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)))
            throw new ArgumentException("Required paths must be safe Data-relative paths.");
        return string.Join('\\', segments);
    }
}
