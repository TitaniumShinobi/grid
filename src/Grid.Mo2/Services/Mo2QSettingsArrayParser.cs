using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2QSettingsArrayParser
{
    public const string ParserVersion = "grid.mo2.qsettings-array.v1";

    public Mo2QSettingsArrayParseResult Parse(
        Mo2RawTextDocument document,
        string section,
        Mo2ExecutableObservationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        limits ??= new();
        limits.Validate();

        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var fields = ImmutableArray.CreateBuilder<Mo2QSettingsField>();
        string? currentSection = null;
        var sourceOrder = 0;
        var malformed = false;
        var unsupported = false;

        if (document.Lines.Length > limits.MaximumLines)
        {
            return new(
                section,
                null,
                [],
                [],
                [new("mo2.executables.line_limit", "The executable configuration exceeds the supported line count.")],
                ProfileSourceParseStatus.Malformed);
        }

        foreach (var line in document.Lines)
        {
            if (line.Text.Length > limits.MaximumLineCharacters)
            {
                warnings.Add(new(
                    "mo2.executables.line_too_long",
                    "A configuration line exceeds the supported length.",
                    line.Index));
                malformed = true;
                continue;
            }

            var trimmed = line.Text.Trim();
            if (trimmed.Length == 0 || trimmed[0] is ';' or '#')
            {
                continue;
            }

            if (trimmed[0] == '[')
            {
                if (trimmed.Length < 2 || trimmed[^1] != ']')
                {
                    warnings.Add(new("mo2.executables.section_malformed", "A section header is malformed.", line.Index));
                    malformed = true;
                    currentSection = null;
                    continue;
                }

                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = FindAssignmentSeparator(line.Text);
            if (separator <= 0)
            {
                warnings.Add(new("mo2.executables.assignment_malformed", "An executable setting has no valid assignment separator.", line.Index));
                malformed = true;
                continue;
            }

            var rawKey = line.Text[..separator].Trim();
            var rawValue = line.Text[(separator + 1)..];
            var (arrayIndex, key) = SplitArrayKey(rawKey);
            var (logical, support) = DecodeValue(rawValue);
            if (support == Mo2QtValueSupport.UnsupportedVariant)
            {
                unsupported = true;
                warnings.Add(new("mo2.executables.value_unsupported", "A Qt variant is preserved but not interpreted.", line.Index));
            }
            else if (support == Mo2QtValueSupport.Malformed)
            {
                malformed = true;
                warnings.Add(new("mo2.executables.value_malformed", "A Qt value is malformed and was preserved raw.", line.Index));
            }

            fields.Add(new(
                line.Index,
                sourceOrder++,
                arrayIndex,
                key,
                rawKey,
                rawValue,
                logical,
                support));
        }

        var sizeFields = fields
            .Where(field => field.ArrayIndex is null && string.Equals(field.Key, "size", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();
        int? declaredSize = null;
        if (sizeFields.Length > 0)
        {
            var selectedSize = sizeFields[^1];
            if (selectedSize.Support != Mo2QtValueSupport.Supported ||
                !int.TryParse(selectedSize.LogicalValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize) ||
                parsedSize < 0 || parsedSize > limits.MaximumEntries)
            {
                warnings.Add(new("mo2.executables.size_invalid", "The custom executable array size is invalid or exceeds the supported limit.", selectedSize.SourceLineIndex));
                malformed = true;
            }
            else
            {
                declaredSize = parsedSize;
            }

            if (sizeFields.Length > 1)
            {
                warnings.Add(new("mo2.executables.size_duplicate", "The array size is declared more than once; the last supported value is used."));
            }
        }

        var grouped = fields
            .Where(field => field.ArrayIndex is not null)
            .GroupBy(field => field.ArrayIndex!.Value)
            .OrderBy(group => group.Min(field => field.SourceOrder))
            .ToArray();
        if (grouped.Length > limits.MaximumEntries)
        {
            warnings.Add(new("mo2.executables.entry_limit", "The executable array exceeds the supported entry count."));
            malformed = true;
            grouped = grouped.Take(limits.MaximumEntries).ToArray();
        }

        var entries = ImmutableArray.CreateBuilder<Mo2QSettingsArrayEntry>(grouped.Length);
        foreach (var group in grouped)
        {
            var entryWarnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
            var ordered = group.OrderBy(field => field.SourceOrder).ToImmutableArray();
            if (ordered.Length > limits.MaximumFieldsPerEntry)
            {
                entryWarnings.Add(new("mo2.executables.field_limit", "The executable entry exceeds the supported field count."));
                malformed = true;
            }

            foreach (var duplicate in ordered
                .GroupBy(field => field.Key, StringComparer.OrdinalIgnoreCase)
                .Where(candidate => candidate.Count() > 1))
            {
                entryWarnings.Add(new(
                    "mo2.executables.field_duplicate",
                    $"The field '{duplicate.Key}' is declared more than once; all values are preserved and the last supported value is projected."));
            }

            if (declaredSize is { } size && (group.Key <= 0 || group.Key > size))
            {
                entryWarnings.Add(new(
                    "mo2.executables.sparse_index",
                    "The entry index is outside the declared array size and remains preserved."));
            }

            entries.Add(new(
                group.Key,
                ordered[0].SourceOrder,
                ordered.Take(limits.MaximumFieldsPerEntry).ToImmutableArray(),
                entryWarnings.ToImmutable()));
        }

        var status = malformed
            ? ProfileSourceParseStatus.Malformed
            : unsupported
                ? ProfileSourceParseStatus.UnsupportedSyntax
                : ProfileSourceParseStatus.Parsed;
        return new(
            section,
            declaredSize,
            entries.ToImmutable(),
            fields.Where(field => field.ArrayIndex is null).ToImmutableArray(),
            warnings.ToImmutable(),
            status);
    }

    private static int FindAssignmentSeparator(string line)
    {
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            if (!escaped && line[index] == '=')
            {
                return index;
            }

            if (!escaped && line[index] == '\\')
            {
                escaped = true;
            }
            else
            {
                escaped = false;
            }
        }

        return -1;
    }

    private static (int? ArrayIndex, string Key) SplitArrayKey(string rawKey)
    {
        var separator = rawKey.IndexOfAny(['\\', '/']);
        if (separator > 0 &&
            int.TryParse(rawKey.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
            index > 0)
        {
            return (index, rawKey[(separator + 1)..]);
        }

        return (null, rawKey);
    }

    private static (string? Logical, Mo2QtValueSupport Support) DecodeValue(string rawValue)
    {
        if (rawValue.StartsWith('@'))
        {
            const string byteArrayPrefix = "@ByteArray(";
            if (!rawValue.StartsWith(byteArrayPrefix, StringComparison.Ordinal))
            {
                return (null, Mo2QtValueSupport.UnsupportedVariant);
            }

            if (!rawValue.EndsWith(')'))
            {
                return (null, Mo2QtValueSupport.Malformed);
            }

            return (DecodeEscapes(rawValue[byteArrayPrefix.Length..^1]), Mo2QtValueSupport.Supported);
        }

        return (DecodeEscapes(rawValue), Mo2QtValueSupport.Supported);
    }

    private static string DecodeEscapes(string value)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                output.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            output.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => escaped,
            });
        }

        return output.ToString();
    }
}
