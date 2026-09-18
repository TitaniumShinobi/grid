using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

/// <summary>
/// Bounded, offline inspection of winning SPID *_DISTR.ini files. The parser
/// deliberately treats only a missing or disabled plugin in the distributed
/// source-form field as a blocking rule defect. References in later filter
/// fields can be optional compatibility filters and are not promoted to errors.
/// </summary>
public sealed partial class Mo2SpidDistributionInspector
{
    public Mo2SpidDistributionInspection Inspect(
        ImmutableArray<Mo2SpidDistributionDocument> documents,
        ImmutableArray<PluginEntry> plugins,
        Mo2SpidDistributionLimits limits)
    {
        limits.Validate();
        if (documents.Length > limits.MaximumDocuments)
            return Limited("mo2.spid.document_limit", $"SPID document count exceeds {limits.MaximumDocuments}.");

        var pluginStates = plugins
            .GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Any(value => value.IsEnabled),
                StringComparer.OrdinalIgnoreCase);
        var references = ImmutableArray.CreateBuilder<Mo2SpidSourcePluginReference>();
        var issues = ImmutableArray.CreateBuilder<Mo2SpidDistributionIssue>();
        long linesScanned = 0;
        long rulesScanned = 0;

        foreach (var document in documents
                     .OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.ProviderName, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeIdentity(document.VirtualPath) || !IsSafeIdentity(document.ProviderName) ||
                !Sha256Pattern().IsMatch(document.Sha256))
            {
                issues.Add(new("mo2.spid.document_identity_invalid", "The SPID document identity is malformed.",
                    document.VirtualPath, document.ProviderName));
                continue;
            }

            foreach (var raw in document.Text.Lines)
            {
                linesScanned++;
                if (linesScanned > limits.MaximumLines)
                    return Limited("mo2.spid.line_limit", $"SPID line count exceeds {limits.MaximumLines}.", references, issues, documents.Length, linesScanned, rulesScanned);
                if (raw.Text.Length > limits.MaximumLineCharacters)
                {
                    issues.Add(new("mo2.spid.line_too_long", "The distribution line exceeds the configured character limit.",
                        document.VirtualPath, document.ProviderName, raw.Index + 1));
                    continue;
                }

                var line = StripComment(raw.Text).Trim();
                if (line.Length == 0) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0 || separator == line.Length - 1)
                {
                    issues.Add(new("mo2.spid.rule_malformed", "The active distribution line has no non-empty type/value separator.",
                        document.VirtualPath, document.ProviderName, raw.Index + 1));
                    continue;
                }

                var distributionType = line[..separator].Trim();
                if (!DistributionTypePattern().IsMatch(distributionType))
                {
                    issues.Add(new("mo2.spid.type_malformed", "The active distribution type is malformed.",
                        document.VirtualPath, document.ProviderName, raw.Index + 1));
                    continue;
                }

                rulesScanned++;
                var value = line[(separator + 1)..].Trim();
                var pipe = value.IndexOf('|');
                var sourceField = (pipe < 0 ? value : value[..pipe]).Trim();
                foreach (Match match in FormPluginPattern().Matches(sourceField))
                {
                    if (references.Count >= limits.MaximumReferences)
                        return Limited("mo2.spid.reference_limit", $"SPID source-plugin reference count exceeds {limits.MaximumReferences}.", references, issues, documents.Length, linesScanned, rulesScanned);
                    var pluginName = match.Groups["plugin"].Value.Trim();
                    var referenceStatus = !pluginStates.TryGetValue(pluginName, out var enabled)
                        ? Mo2SpidPluginReferenceStatus.Missing
                        : enabled ? Mo2SpidPluginReferenceStatus.Enabled : Mo2SpidPluginReferenceStatus.Disabled;
                    var observed = new Mo2SpidSourcePluginReference(
                        document.VirtualPath, document.ProviderName, raw.Index + 1, distributionType,
                        match.Value, pluginName, match.Groups["form"].Value, referenceStatus);
                    references.Add(observed);
                    if (referenceStatus != Mo2SpidPluginReferenceStatus.Enabled)
                    {
                        var code = referenceStatus == Mo2SpidPluginReferenceStatus.Disabled
                            ? "mo2.spid.source_plugin_disabled"
                            : "mo2.spid.source_plugin_missing";
                        var detail = referenceStatus == Mo2SpidPluginReferenceStatus.Disabled
                            ? $"The rule distributes '{match.Value}', but plugin '{pluginName}' is installed and disabled in the selected profile."
                            : $"The rule distributes '{match.Value}', but plugin '{pluginName}' is absent from the selected profile's resolved plugin inventory.";
                        issues.Add(new(code, detail, document.VirtualPath, document.ProviderName, raw.Index + 1, pluginName));
                    }
                }
            }
        }

        var status = issues.Any(value => value.Code is "mo2.spid.document_identity_invalid" or "mo2.spid.line_too_long" or "mo2.spid.rule_malformed" or "mo2.spid.type_malformed")
            ? Mo2SpidDistributionStatus.Partial
            : Mo2SpidDistributionStatus.Complete;
        return new(status, documents.Length, linesScanned, rulesScanned, references.ToImmutable(), issues.ToImmutable(),
            Fingerprint(documents, references));
    }

    private static Mo2SpidDistributionInspection Limited(
        string code,
        string detail,
        ImmutableArray<Mo2SpidSourcePluginReference>.Builder? references = null,
        ImmutableArray<Mo2SpidDistributionIssue>.Builder? issues = null,
        int documents = 0,
        long lines = 0,
        long rules = 0)
    {
        var issueValues = issues?.ToImmutable() ?? [];
        issueValues = issueValues.Add(new(code, detail, string.Empty, string.Empty));
        return new(Mo2SpidDistributionStatus.LimitExceeded, documents, lines, rules,
            references?.ToImmutable() ?? [], issueValues, Fingerprint([], references ?? ImmutableArray.CreateBuilder<Mo2SpidSourcePluginReference>()));
    }

    private static string Fingerprint(
        IEnumerable<Mo2SpidDistributionDocument> documents,
        IEnumerable<Mo2SpidSourcePluginReference> references)
    {
        var text = new StringBuilder();
        foreach (var document in documents.OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase))
            text.Append(document.VirtualPath).Append('\u001f').Append(document.ProviderName).Append('\u001f').Append(document.Sha256.ToUpperInvariant()).Append('\u001e');
        foreach (var reference in references.OrderBy(value => value.VirtualPath, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Line).ThenBy(value => value.SourceToken, StringComparer.OrdinalIgnoreCase))
            text.Append(reference.VirtualPath).Append('\u001f').Append(reference.Line).Append('\u001f').Append(reference.SourceToken).Append('\u001f').Append(reference.Status).Append('\u001e');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string StripComment(string value)
    {
        var separator = value.IndexOf(';');
        return separator < 0 ? value : value[..separator];
    }

    private static bool IsSafeIdentity(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1_024 && !value.Any(char.IsControl);

    [GeneratedRegex("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DistributionTypePattern();

    [GeneratedRegex(@"(?i)(?<form>[-+!]?(?:0x)?[0-9a-f]+)~(?<plugin>[^|,;]+?\.(?:esm|esp|esl))", RegexOptions.CultureInvariant)]
    private static partial Regex FormPluginPattern();
}
