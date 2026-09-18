using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Core.Models;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public static class Mo2ProfileParsers
{
    private static readonly HashSet<string> ImplicitSkyrimCorePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm",
    };

    public static string? ParseSelectedProfile(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var inGeneral = false;
        foreach (var line in document.Lines)
        {
            var value = line.Text.Trim();
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                inGeneral = value[1..^1].Equals("General", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inGeneral)
            {
                continue;
            }

            var separator = value.IndexOf('=');
            if (separator <= 0 ||
                !value[..separator].Trim().Equals("selected_profile", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var selected = value[(separator + 1)..].Trim();
            if (selected.StartsWith("@ByteArray(", StringComparison.Ordinal) && selected.EndsWith(')'))
            {
                selected = selected[11..^1];
            }

            return selected.Length == 0 ? null : selected;
        }

        return null;
    }

    public static Mo2ModListParseResult ParseModList(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = ImmutableArray.CreateBuilder<Mo2ParsedModListEntry>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var status = ProfileSourceParseStatus.Parsed;

        foreach (var line in document.Lines)
        {
            var value = line.Text.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            var marker = value[0] switch
            {
                '+' => Mo2ModListMarker.Enabled,
                '-' => Mo2ModListMarker.Disabled,
                '*' => Mo2ModListMarker.Foreign,
                _ => Mo2ModListMarker.Unmarked,
            };
            var name = marker == Mo2ModListMarker.Unmarked ? value : value[1..].Trim();
            if (name.Length == 0)
            {
                warnings.Add(new("mo2.modlist.name_missing", "A mod-list marker has no name.", line.Index));
                status = ProfileSourceParseStatus.Malformed;
                continue;
            }

            if (marker == Mo2ModListMarker.Unmarked)
            {
                warnings.Add(new(
                    "mo2.modlist.unmarked_enabled",
                    "An unmarked mod-list row is represented as enabled; verify it in MO2.",
                    line.Index));
            }

            occurrences.TryGetValue(name, out var occurrence);
            occurrences[name] = occurrence + 1;
            entries.Add(new(
                name,
                marker,
                marker != Mo2ModListMarker.Disabled,
                line.Index,
                occurrence));
        }

        return new(entries.ToImmutable(), warnings.ToImmutable(), status);
    }

    public static Mo2PluginStateParseResult ParsePluginStates(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = ImmutableArray.CreateBuilder<Mo2ParsedPluginState>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var status = ProfileSourceParseStatus.Parsed;

        foreach (var line in document.Lines)
        {
            var value = line.Text.Trim();
            if (value.Length == 0 || value.StartsWith('#'))
            {
                continue;
            }

            var enabled = value.StartsWith('*');
            var marker = enabled ? Mo2PluginListMarker.Enabled : Mo2PluginListMarker.Disabled;
            var name = enabled ? value[1..].Trim() : value;
            if (name.Length == 0)
            {
                warnings.Add(new("mo2.plugins.name_missing", "A plugin-list marker has no plugin name.", line.Index));
                status = ProfileSourceParseStatus.Malformed;
                continue;
            }

            occurrences.TryGetValue(name, out var occurrence);
            occurrences[name] = occurrence + 1;
            entries.Add(new(name, enabled, line.Index, occurrence, marker));
        }

        return new(entries.ToImmutable(), warnings.ToImmutable(), status);
    }

    public static Mo2LoadOrderParseResult ParseLoadOrder(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = ImmutableArray.CreateBuilder<Mo2ParsedLoadOrderEntry>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in document.Lines)
        {
            var name = line.Text.Trim();
            if (name.Length == 0 || name.StartsWith('#'))
            {
                continue;
            }

            occurrences.TryGetValue(name, out var occurrence);
            occurrences[name] = occurrence + 1;
            entries.Add(new(name, line.Index, occurrence));
        }

        return new(entries.ToImmutable(), warnings.ToImmutable(), ProfileSourceParseStatus.Parsed);
    }

    public static Mo2ProfileSettingsParseResult ParseProfileSettings(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool? localSaves = null;
        bool? localSettings = null;
        var customOverwrites = ImmutableArray.CreateBuilder<Mo2CustomOverwriteSetting>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var status = ProfileSourceParseStatus.Parsed;
        var section = string.Empty;
        foreach (var line in document.Lines)
        {
            var value = line.Text.Trim();
            if (value.Length == 0 || value.StartsWith('#') || value.StartsWith(';'))
            {
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                section = value[1..^1].Trim();
                continue;
            }

            var separator = value.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = value[..separator].Trim();
            var rawValue = value[(separator + 1)..];
            if (section.Equals("custom_overwrites", StringComparison.OrdinalIgnoreCase))
            {
                var title = DecodeQSettingsKey(key);
                var outputMod = rawValue.Trim();
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(outputMod))
                {
                    warnings.Add(new(
                        "mo2.settings.custom_overwrite_invalid",
                        "A custom-overwrite mapping has an empty executable title or output mod and was retained only as raw evidence.",
                        line.Index));
                    status = ProfileSourceParseStatus.UnsupportedSyntax;
                    continue;
                }

                customOverwrites.Add(new(title, outputMod, line.Index, key, rawValue));
                continue;
            }

            if (!key.Equals("LocalSaves", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("LocalSettings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!bool.TryParse(rawValue.Trim(), out var parsed))
            {
                warnings.Add(new(
                    "mo2.settings.boolean_invalid",
                    $"The {key} setting is not an explicit true/false value and remains unknown.",
                    line.Index));
                status = ProfileSourceParseStatus.UnsupportedSyntax;
                continue;
            }

            if (key.Equals("LocalSaves", StringComparison.OrdinalIgnoreCase))
            {
                localSaves = parsed;
            }
            else
            {
                localSettings = parsed;
            }
        }

        foreach (var duplicate in customOverwrites
            .GroupBy(mapping => mapping.ExecutableTitle, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1))
        {
            warnings.Add(new(
                "mo2.settings.custom_overwrite_duplicate",
                $"Multiple custom-overwrite mappings exist for executable '{duplicate.Key}'. Grid retained every mapping and will treat the association as ambiguous."));
        }

        return new(localSaves, localSettings, warnings.ToImmutable(), status, customOverwrites.ToImmutable());
    }

    private static string DecodeQSettingsKey(string value)
    {
        if (!value.Contains('%', StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    public static Mo2ArchiveListParseResult ParseArchiveLists(Mo2RawTextDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var archives = ImmutableArray.CreateBuilder<string>();
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var status = ProfileSourceParseStatus.Parsed;
        var inArchiveSection = false;
        foreach (var line in document.Lines)
        {
            var value = line.Text.Trim();
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                inArchiveSection = value[1..^1].Equals("Archive", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inArchiveSection || value.Length == 0 || value.StartsWith('#') || value.StartsWith(';'))
            {
                continue;
            }

            var separator = value.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = value[..separator].Trim();
            if (!key.Equals("sResourceArchiveList", StringComparison.OrdinalIgnoreCase) &&
                !key.Equals("sResourceArchiveList2", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var archive in value[(separator + 1)..].Split(','))
            {
                var name = archive.Trim().Trim('"');
                if (name.Length == 0)
                {
                    continue;
                }

                if (name.Any(char.IsControl) || Path.IsPathFullyQualified(name) ||
                    name.Split(['\\', '/']).Any(segment => segment is "." or ".."))
                {
                    warnings.Add(new(
                        "mo2.archive.ini_name_invalid",
                        "An archive-list value contains an unsafe path and was not projected.",
                        line.Index));
                    status = ProfileSourceParseStatus.UnsupportedSyntax;
                    continue;
                }

                archives.Add(name);
            }
        }

        return new(archives.ToImmutable(), warnings.ToImmutable(), status);
    }

    public static ImmutableArray<ModEntry> ProjectMods(
        ProfileId profileId,
        Mo2ModListParseResult parsed)
    {
        var recognized = parsed.Entries.Reverse().ToArray();
        return recognized.Select((entry, priority) =>
        {
            var isSeparator = entry.Name.EndsWith("_separator", StringComparison.Ordinal);
            return new ModEntry(
                StableModId(profileId, entry.Name, entry.Occurrence),
                entry.Name,
                isSeparator ? string.Empty : "Unknown",
                isSeparator
                    ? string.Empty
                    : entry.Marker == Mo2ModListMarker.Foreign ? "MO2 foreign entry" : "MO2 profile",
                !isSeparator && entry.IsEnabled,
                priority,
                HealthLevel.Unknown,
                isSeparator ? ModEntryKind.Separator : ModEntryKind.Mod,
                isSeparator ? string.Empty : "MO2",
                null,
                ModUpdateState.Unknown,
                ModConflictState.Unknown);
        }).ToImmutableArray();
    }

    public static (ImmutableArray<PluginEntry> Plugins, ImmutableArray<Mo2ParseWarning> Warnings) ProjectPlugins(
        ProfileId profileId,
        Mo2PluginStateParseResult pluginStates,
        Mo2LoadOrderParseResult loadOrder)
    {
        var warnings = ImmutableArray.CreateBuilder<Mo2ParseWarning>();
        var states = pluginStates.Entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var loadRows = loadOrder.Entries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var projected = ImmutableArray.CreateBuilder<PluginEntry>();

        for (var sourcePriority = 0; sourcePriority < loadOrder.Entries.Length; sourcePriority++)
        {
            var entry = loadOrder.Entries[sourcePriority];
            if (!states.TryGetValue(entry.Name, out var matchingStates))
            {
                if (!IsImplicitSkyrimPlugin(entry.Name))
                {
                    warnings.Add(new("mo2.loadorder.plugin_unmatched", $"'{entry.Name}' is in loadorder.txt but not plugins.txt.", entry.SourceLineIndex));
                    continue;
                }

                projected.Add(new(
                    StablePluginId(profileId, entry.Name, entry.Occurrence),
                    entry.Name,
                    true,
                    projected.Count,
                    HealthLevel.Unknown,
                    sourcePriority));
                continue;
            }

            if (matchingStates.Length != 1 || loadRows[entry.Name].Length != 1)
            {
                warnings.Add(new("mo2.loadorder.plugin_ambiguous", $"'{entry.Name}' is duplicated and was not projected.", entry.SourceLineIndex));
                continue;
            }

            projected.Add(new(
                StablePluginId(profileId, entry.Name, matchingStates[0].Occurrence),
                entry.Name,
                matchingStates[0].IsEnabled,
                projected.Count,
                HealthLevel.Unknown,
                sourcePriority));
        }

        foreach (var state in pluginStates.Entries.Where(state => !loadRows.ContainsKey(state.Name)))
        {
            warnings.Add(new("mo2.plugins.loadorder_missing", $"'{state.Name}' has no loadorder.txt row and was not projected.", state.SourceLineIndex));
        }

        return (projected.ToImmutable(), warnings.ToImmutable());
    }

    private static bool IsImplicitSkyrimPlugin(string name) =>
        ImplicitSkyrimCorePlugins.Contains(name) ||
        name.StartsWith("cc", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(name) is string extension &&
        (extension.Equals(".esm", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".esp", StringComparison.OrdinalIgnoreCase) ||
         extension.Equals(".esl", StringComparison.OrdinalIgnoreCase));

    public static ProfileId StableProfileId(InstallationReferenceId referenceId, string canonicalProfilePath) =>
        new($"profile.mo2.{StableSuffix(referenceId.Value, canonicalProfilePath)}");

    private static ModId StableModId(ProfileId profileId, string name, int occurrence) =>
        new($"mod.mo2.{StableSuffix(profileId.Value, name.ToUpperInvariant(), occurrence.ToString())}");

    private static PluginId StablePluginId(ProfileId profileId, string name, int occurrence) =>
        new($"plugin.mo2.{StableSuffix(profileId.Value, name.ToUpperInvariant(), occurrence.ToString())}");

    private static string StableSuffix(params string[] components)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components)));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}
