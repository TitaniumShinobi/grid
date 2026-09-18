using System.Collections.Immutable;
using System.Globalization;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public static class Mo2MetaIniParser
{
    private static readonly HashSet<string> SupportedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "newestVersion", "ignoredVersion", "category", "gameName", "modid",
        "installationFile", "notes", "comments", "repository", "lastNexusQuery",
        "lastNexusUpdate", "nexusFileStatus", "fileTime", "fileCategory",
        "nexusCategory", "nexusDescription", "author", "uploader", "uploaderUrl",
        "url", "hasCustomURL", "nexusLastModified", "tracked", "endorsed",
        "converted", "validated",
    };

    public static Mo2CategoryCatalog ParseCategories(
        Mo2ProfileSourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var categories = ImmutableArray.CreateBuilder<Mo2CategoryDefinition>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        if (source.RawDocument is null)
        {
            return new([], source.Warnings, source);
        }

        var ids = new HashSet<int>();
        foreach (var line in source.RawDocument.Lines)
        {
            var value = line.Text.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            var parts = value.Split('|');
            if (parts.Length is not (3 or 4) ||
                !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                string.IsNullOrWhiteSpace(parts[1]))
            {
                warnings.Add(new("mo2.categories.syntax_unsupported", "A categories.dat row could not be interpreted and was preserved raw.", line.Index));
                continue;
            }

            int? parentId = null;
            var parentComponent = parts.Length >= 4 ? parts[3] : parts.Length >= 3 ? parts[2] : null;
            if (parentComponent is not null &&
                int.TryParse(parentComponent.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parent))
            {
                parentId = parent;
            }

            if (!ids.Add(id))
            {
                warnings.Add(new("mo2.categories.duplicate_id", $"Category ID {id} appears more than once; the first definition is used.", line.Index));
                continue;
            }

            categories.Add(new(id, DecodeQtString(parts[1].Trim()), parentId, line.Text, line.Index));
        }

        return new(categories.ToImmutable(), source.Warnings.AddRange(warnings), source);
    }

    public static (Mo2NormalizedModMetadata? Metadata, ImmutableArray<Mo2ParseWarning> Warnings)
        ParseMetadata(Mo2RawTextDocument document, Mo2CategoryCatalog? categories)
    {
        ArgumentNullException.ThrowIfNull(document);
        var raw = ImmutableArray.CreateBuilder<Mo2RawIniValue>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "General";

        foreach (var line in document.Lines)
        {
            var trimmed = line.Text.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }

            var separator = line.Text.IndexOf('=');
            if (separator <= 0)
            {
                warnings.Add(new("mo2.mod.meta.syntax_unsupported", "A meta.ini row has unsupported syntax and was preserved raw.", line.Index));
                raw.Add(new(section, string.Empty, line.Text, line.Index, false));
                continue;
            }

            var key = line.Text[..separator].Trim();
            var rawValue = line.Text[(separator + 1)..];
            var supported = section.Equals("General", StringComparison.OrdinalIgnoreCase) &&
                SupportedKeys.Contains(key);
            raw.Add(new(section, key, rawValue, line.Index, supported));
            if (!supported)
            {
                continue;
            }

            if (rawValue.TrimStart().StartsWith("@Variant(", StringComparison.Ordinal))
            {
                warnings.Add(new("mo2.mod.meta.qt_variant_unsupported", $"The metadata key '{key}' uses unsupported Qt variant syntax and remains raw.", line.Index));
                continue;
            }

            if (!values.TryAdd(key, DecodeQtString(rawValue)))
            {
                warnings.Add(new("mo2.mod.meta.duplicate_key", $"The metadata key '{key}' appears more than once; the first value is used.", line.Index));
            }
        }

        var categoryIds = ParseCategoryIds(Get(values, "category"), warnings);
        var categoryMap = categories?.Categories.ToDictionary(category => category.Id) ?? [];
        var categoryNames = categoryIds
            .Where(categoryMap.ContainsKey)
            .Select(id => categoryMap[id].Name)
            .ToImmutableArray();

        long? modId = null;
        var rawModId = Get(values, "modid");
        if (!string.IsNullOrWhiteSpace(rawModId) &&
            (!long.TryParse(rawModId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedModId) || parsedModId < 0))
        {
            warnings.Add(new("mo2.mod.meta.mod_id_invalid", "The local Nexus mod identifier is malformed and remains unavailable."));
        }
        else if (!string.IsNullOrWhiteSpace(rawModId))
        {
            modId = long.Parse(rawModId, CultureInfo.InvariantCulture);
        }

        var providerTime = ParseProviderTime(values, warnings);
        return (new(
            EmptyToNull(Get(values, "version")),
            EmptyToNull(Get(values, "newestVersion")),
            EmptyToNull(Get(values, "ignoredVersion")),
            categoryIds,
            categoryNames,
            EmptyToNull(Get(values, "gameName")),
            modId,
            EmptyToNull(Get(values, "installationFile")),
            EmptyToNull(Get(values, "notes")),
            EmptyToNull(Get(values, "comments")),
            EmptyToNull(Get(values, "repository")),
            providerTime,
            EmptyToNull(Get(values, "nexusFileStatus")),
            raw.ToImmutable()), warnings.ToImmutable());
    }

    private static ImmutableArray<int> ParseCategoryIds(
        string? value,
        ImmutableArray<Mo2ParseWarning>.Builder warnings)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<int>();
        foreach (var component in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(component, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id >= 0)
            {
                result.Add(id);
            }
            else if (component == "-1")
            {
                // MO2 uses -1 as the explicit no-category sentinel.
            }
            else
            {
                warnings.Add(new("mo2.mod.meta.category_invalid", "A category identifier is malformed and was preserved only as a raw value."));
            }
        }

        return result.Distinct().ToImmutableArray();
    }

    private static DateTimeOffset? ParseProviderTime(
        IReadOnlyDictionary<string, string> values,
        ImmutableArray<Mo2ParseWarning>.Builder warnings)
    {
        var value = EmptyToNull(Get(values, "lastNexusUpdate")) ?? EmptyToNull(Get(values, "lastNexusQuery"));
        if (value is null)
        {
            return null;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        warnings.Add(new("mo2.mod.meta.provider_time_invalid", "The provider timestamp is malformed and remains unavailable."));
        return null;
    }

    private static string DecodeQtString(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }
        if (trimmed.StartsWith("@ByteArray(", StringComparison.Ordinal) && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[11..^1];
        }

        var decoded = new System.Text.StringBuilder(trimmed.Length);
        for (var index = 0; index < trimmed.Length; index++)
        {
            if (trimmed[index] != '\\' || index + 1 >= trimmed.Length)
            {
                decoded.Append(trimmed[index]);
                continue;
            }

            var escaped = trimmed[++index];
            var known = escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                _ => '\0',
            };
            if (known == '\0')
            {
                decoded.Append('\\').Append(escaped);
            }
            else
            {
                decoded.Append(known);
            }
        }

        return decoded.ToString();
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
