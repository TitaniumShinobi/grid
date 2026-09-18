using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Reconstructs a FOMOD selection vector only from exact candidate-entry hashes
/// and the complete physical-file evidence for the candidate's installed MO2
/// provider lineage. It never reads or mutates an MO2 profile.
/// </summary>
public sealed class Mo2FomodSelectionReconciliationService
{
    public Mo2FomodSelectionReconciliationResult Reconcile(
        Mo2ArchiveInspectionResult inspection,
        ImmutableArray<Mo2InstalledFileEvidence> installedFiles,
        string? installedPrimaryArchiveSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var issues = ImmutableArray.CreateBuilder<Mo2ArchiveInspectionIssue>();
        var providers = (installedFiles.IsDefault ? [] : installedFiles)
            .Select(file => ValidateFile(file))
            .Select(file => file.ProviderName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        var entries = (inspection.Entries.IsDefault ? [] : inspection.Entries)
            .Where(entry => !entry.IsDirectory && entry.Sha256 is not null)
            .ToDictionary(entry => entry.NormalizedPath, StringComparer.OrdinalIgnoreCase);
        var installed = (installedFiles.IsDefault ? [] : installedFiles)
            .GroupBy(file => NormalizePath(file.VirtualPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(file => file.Sha256.ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
                StringComparer.OrdinalIgnoreCase);
        var installedArchiveHash = string.IsNullOrWhiteSpace(installedPrimaryArchiveSha256)
            ? null
            : installedPrimaryArchiveSha256.ToUpperInvariant();
        if (installedArchiveHash is not null &&
            (installedArchiveHash.Length != 64 || installedArchiveHash.Any(character => !Uri.IsHexDigit(character))))
            throw new ArgumentException("The installed primary archive SHA-256 digest is invalid.");

        var groups = ImmutableArray.CreateBuilder<Mo2FomodGroupReconciliation>();
        var vector = ImmutableArray.CreateBuilder<Mo2FomodGroupSelection>();
        foreach (var group in inspection.Fomod.Groups.IsDefault ? [] : inspection.Fomod.Groups)
        {
            var rawOptions = new List<(Mo2FomodPluginOption Option, int Exact, int Different, int Absent)>();
            foreach (var option in group.EffectivePluginOptions)
            {
                var mapped = option.InstallMappings.IsDefault ? [] : option.InstallMappings;
                var exact = 0;
                var different = 0;
                var absent = 0;
                foreach (var mapping in mapped)
                {
                    if (!entries.TryGetValue(mapping.SourcePath, out var candidate) || candidate.Sha256 is null)
                    {
                        issues.Add(new("mo2.repair.fomod.reconciliation_source_missing",
                            "A FOMOD option mapping lacks a verified candidate-entry digest.", mapping.SourcePath));
                        continue;
                    }
                    var sourceExtension = Path.GetExtension(mapping.SourcePath);
                    if (installedArchiveHash is not null &&
                        sourceExtension is not null &&
                        (sourceExtension.Equals(".bsa", StringComparison.OrdinalIgnoreCase) ||
                         sourceExtension.Equals(".ba2", StringComparison.OrdinalIgnoreCase)) &&
                        candidate.Sha256.Equals(installedArchiveHash, StringComparison.OrdinalIgnoreCase)) exact++;
                    else if (!installed.TryGetValue(mapping.DestinationPath, out var observedHashes)) absent++;
                    else if (observedHashes.Contains(candidate.Sha256, StringComparer.OrdinalIgnoreCase)) exact++;
                    else different++;
                }
                rawOptions.Add((option, exact, different, absent));
            }

            var exactOptions = rawOptions.Where(option =>
                    option.Option.InstallMappings.Length > 0 &&
                    option.Exact == option.Option.InstallMappings.Length)
                .Select(option => option.Option)
                .ToImmutableArray();
            var options = ImmutableArray.CreateBuilder<Mo2FomodOptionReconciliation>();
            foreach (var raw in rawOptions)
            {
                var mapped = raw.Option.InstallMappings.IsDefault ? [] : raw.Option.InstallMappings;
                var evidenceExplainedBySelectedAlternative = exactOptions.Length > 0 && mapped.All(mapping =>
                {
                    if (!installed.TryGetValue(mapping.DestinationPath, out var observedHashes)) return true;
                    return exactOptions.Any(selected => selected.InstallMappings.Any(selectedMapping =>
                        selectedMapping.DestinationPath.Equals(mapping.DestinationPath, StringComparison.OrdinalIgnoreCase) &&
                        entries.TryGetValue(selectedMapping.SourcePath, out var selectedEntry) &&
                        selectedEntry.Sha256 is not null &&
                        observedHashes.Contains(selectedEntry.Sha256, StringComparer.OrdinalIgnoreCase)));
                });
                var optionStatus = mapped.Length == 0
                    ? Mo2FomodOptionReconciliationStatus.NoFileEvidence
                    : raw.Exact == mapped.Length
                        ? Mo2FomodOptionReconciliationStatus.Selected
                        : (raw.Exact == 0 && raw.Different == 0) || evidenceExplainedBySelectedAlternative
                            ? Mo2FomodOptionReconciliationStatus.NotSelected
                            : Mo2FomodOptionReconciliationStatus.Ambiguous;
                options.Add(new(raw.Option.Name, optionStatus, mapped.Length, raw.Exact, raw.Different, raw.Absent));
            }

            var optionResults = options.ToImmutable();
            var selected = optionResults
                .Where(option => option.Status == Mo2FomodOptionReconciliationStatus.Selected)
                .Select(option => option.Name)
                .Order(StringComparer.Ordinal)
                .ToImmutableArray();
            var uncertain = optionResults.Any(option => option.Status is
                Mo2FomodOptionReconciliationStatus.Ambiguous or
                Mo2FomodOptionReconciliationStatus.NoFileEvidence);
            var valid = !uncertain && ValidSelection(group.Type, selected.Length, optionResults.Length);
            if (!valid)
            {
                issues.Add(new("mo2.repair.fomod.reconciliation_unresolved",
                    $"Installed file evidence does not prove one valid selection for group '{group.Name}'."));
            }
            else
            {
                vector.Add(new(group.Name, selected));
            }
            groups.Add(new(group.Name, group.Type, valid ? "Complete" : "Unresolved", selected, optionResults));
        }

        if (inspection.Fomod.Status is not (Mo2FomodStatus.SelectionRequired or Mo2FomodStatus.Valid))
        {
            issues.Add(new("mo2.repair.fomod.reconciliation_unavailable",
                "The inspected archive does not contain one supported FOMOD selection surface."));
        }
        if (providers.IsEmpty)
        {
            issues.Add(new("mo2.repair.fomod.reconciliation_provider_missing",
                "No installed provider file evidence was supplied."));
        }

        var status = issues.Count == 0 && vector.Count == groups.Count ? "Complete" : "Unresolved";
        var unsigned = new
        {
            schemaVersion = 1,
            status,
            archiveSha256 = inspection.ArchiveSha256?.ToUpperInvariant(),
            installedPrimaryArchiveSha256 = installedArchiveHash,
            providerNames = providers,
            selectionVector = vector.ToImmutable(),
            groups = groups.ToImmutable(),
            issues = issues.ToImmutable(),
        };
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(unsigned)));
        return new(1, status, inspection.ArchivePath, inspection.ArchiveSha256?.ToUpperInvariant(),
            providers, unsigned.selectionVector, unsigned.groups, unsigned.issues, fingerprint,
            $"fomod-selection.{fingerprint[..24].ToLowerInvariant()}", installedArchiveHash);
    }

    private static Mo2InstalledFileEvidence ValidateFile(Mo2InstalledFileEvidence file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file.VirtualPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.ProviderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(file.Sha256);
        if (file.Sha256.Length != 64 || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Installed file evidence contains an invalid SHA-256 digest.");
        return file;
    }

    private static string NormalizePath(string path) => path.Replace('/', '\\').TrimStart('\\');

    private static bool ValidSelection(string type, int selected, int available) => type switch
    {
        "SelectExactlyOne" => selected == 1,
        "SelectAtMostOne" => selected <= 1,
        "SelectAtLeastOne" => selected >= 1,
        "SelectAny" => true,
        "SelectAll" => selected == available,
        _ => false,
    };
}
