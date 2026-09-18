using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Grid.Mo2.Models;
using Grid.Mo2.Services;
using Microsoft.Win32;

namespace Grid.Mo2.Infrastructure;

public sealed partial class WindowsMo2EvidenceSource : IWindowsMo2EvidenceSource
{
    private const string GlobalFolderName = "ModOrganizer";
    private readonly IMo2ReadOnlyFileSystem fileSystem;
    private readonly IMo2PathCanonicalizer paths;
    private readonly IWindowsRegistryReader registry;
    private readonly IWindowsShortcutResolver shortcuts;

    public WindowsMo2EvidenceSource(
        IMo2ReadOnlyFileSystem fileSystem,
        IMo2PathCanonicalizer paths,
        IWindowsRegistryReader? registry = null,
        IWindowsShortcutResolver? shortcuts = null)
    {
        this.fileSystem = fileSystem;
        this.paths = paths;
        this.registry = registry ?? new WindowsRegistryReader();
        this.shortcuts = shortcuts ?? new WindowsShortcutResolver();
    }

    public async Task<ImmutableArray<Mo2DiscoveryEvidence>> FindAsync(
        Mo2DiscoveryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var evidence = ImmutableArray.CreateBuilder<Mo2DiscoveryEvidence>();
        if (!paths.TryCanonicalize(options.LocalApplicationDataPath, out var localApplicationData, out _))
        {
            return [];
        }

        var globalRoot = Path.Combine(localApplicationData, GlobalFolderName);

        if (fileSystem.ProbeDirectory(globalRoot) == Mo2PathState.Present)
        {
            foreach (var directory in fileSystem.EnumerateDirectories(globalRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fileSystem.ProbeFile(Path.Combine(directory, "ModOrganizer.ini")) == Mo2PathState.Present)
                {
                    evidence.Add(new(
                        Mo2EvidenceKind.GlobalInstanceRoot,
                        null,
                        directory,
                        "MO2 global instance root",
                        80));
                }
            }
        }

        if (paths.TryCanonicalize(options.SystemDriveRoot, out var systemDriveRoot, out _))
        {
            var defaultApplication = Path.Combine(systemDriveRoot, "Modding", "MO2");
            AddApplicationEvidence(evidence, defaultApplication, Mo2EvidenceKind.InstallerDefault, "MO2 installer default", 60);
        }

        AddProtocolEvidence(evidence, "Software\\Classes\\nxm", "NXM protocol handler");
        AddProtocolEvidence(evidence, "Software\\Classes\\modl", "MODL protocol handler");

        var handlerIni = Path.Combine(globalRoot, "downloadhandler.ini");
        if (fileSystem.ProbeFile(handlerIni) == Mo2PathState.Present)
        {
            try
            {
                var text = await fileSystem.ReadTextAsync(handlerIni, 1024 * 1024, cancellationToken).ConfigureAwait(false);
                foreach (Match match in ModOrganizerPathRegex().Matches(text))
                {
                    AddApplicationEvidence(
                        evidence,
                        Path.GetDirectoryName(match.Value),
                        Mo2EvidenceKind.DownloadHandler,
                        "MO2 download-handler configuration",
                        70);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A stale or inaccessible optional evidence source must not abort bounded discovery.
            }
        }

        foreach (var shortcutPath in EnumerateKnownShortcuts())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = shortcuts.TryResolveTarget(shortcutPath);
            if (target is not null)
            {
                AddApplicationEvidence(
                    evidence,
                    Path.GetDirectoryName(target),
                    Mo2EvidenceKind.Shortcut,
                    "MO2 Start Menu or desktop shortcut",
                    65);
            }
        }

        var currentInstance = registry.ReadCurrentUserValue(
            "Software\\Mod Organizer Team\\Mod Organizer",
            "CurrentInstance");
        if (!string.IsNullOrWhiteSpace(currentInstance))
        {
            AddCurrentInstanceEvidence(evidence, globalRoot, currentInstance, "MO2 current-instance setting", 90);
        }

        var legacyCurrentInstance = registry.ReadCurrentUserValue(
            "Software\\Tannin\\Mod Organizer",
            "CurrentInstance");
        if (!string.IsNullOrWhiteSpace(legacyCurrentInstance))
        {
            AddCurrentInstanceEvidence(evidence, globalRoot, legacyCurrentInstance, "Legacy MO2 current-instance setting", 85);
        }

        return evidence.ToImmutable();
    }

    private void AddProtocolEvidence(
        ImmutableArray<Mo2DiscoveryEvidence>.Builder evidence,
        string registryPath,
        string description)
    {
        var command = registry.ReadCurrentUserValue(registryPath + "\\shell\\open\\command", string.Empty)
            ?? registry.ReadCurrentUserDefaultValue(registryPath + "\\shell\\open\\command");
        var executable = ExtractFirstCommandPath(command);
        if (executable is null)
        {
            return;
        }

        var applicationDirectory = Path.GetDirectoryName(executable);
        AddApplicationEvidence(evidence, applicationDirectory, Mo2EvidenceKind.ProtocolHandler, description, 70);
    }

    private void AddCurrentInstanceEvidence(
        ImmutableArray<Mo2DiscoveryEvidence>.Builder evidence,
        string globalRoot,
        string instanceName,
        string description,
        int rank)
    {
        var name = instanceName.Trim();
        if (name.Length == 0 ||
            name.Any(char.IsControl) ||
            name is "." or ".." ||
            name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, Path.VolumeSeparatorChar]) >= 0)
        {
            return;
        }

        var candidate = Path.Combine(globalRoot, name);
        if (!paths.TryCanonicalize(candidate, out var canonical, out _) ||
            !paths.IsImmediateChildOf(canonical, globalRoot))
        {
            return;
        }

        if (fileSystem.ProbeFile(Path.Combine(canonical, "ModOrganizer.ini")) == Mo2PathState.Present)
        {
            evidence.Add(new(Mo2EvidenceKind.CurrentInstance, null, canonical, description, rank));
        }
    }

    private void AddApplicationEvidence(
        ImmutableArray<Mo2DiscoveryEvidence>.Builder evidence,
        string? applicationDirectory,
        Mo2EvidenceKind kind,
        string description,
        int rank)
    {
        if (string.IsNullOrWhiteSpace(applicationDirectory) ||
            !paths.TryCanonicalize(applicationDirectory, out var canonical, out _))
        {
            return;
        }

        if (fileSystem.ProbeFile(Path.Combine(canonical, "ModOrganizer.exe")) == Mo2PathState.Present)
        {
            var portable = fileSystem.ProbeFile(Path.Combine(canonical, "ModOrganizer.ini")) == Mo2PathState.Present
                ? canonical
                : null;
            evidence.Add(new(kind, canonical, portable, description, rank));
        }
    }

    private static string? ExtractFirstCommandPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);
            return closing > 1 ? trimmed[1..closing] : null;
        }

        var executableEnd = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return executableEnd >= 0 ? trimmed[..(executableEnd + 4)] : null;
    }

    private IEnumerable<string> EnumerateKnownShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            if (!paths.TryCanonicalize(root, out var canonicalRoot, out _))
            {
                continue;
            }

            var exactCandidates = new[]
            {
                Path.Combine(canonicalRoot, "Mod Organizer.lnk"),
                Path.Combine(canonicalRoot, "Programs", "Mod Organizer", "Mod Organizer.lnk"),
            };
            foreach (var candidate in exactCandidates.Where(File.Exists))
            {
                yield return candidate;
            }
        }
    }

    [GeneratedRegex(@"(?i)(?:[A-Z]:\\|\\\\)[^\r\n=]*?ModOrganizer\.exe")]
    private static partial Regex ModOrganizerPathRegex();
}

public sealed class WindowsRegistryReader : IWindowsRegistryReader
{
    public string? ReadCurrentUserDefaultValue(string subKeyPath) =>
        ReadCurrentUserValue(subKeyPath, string.Empty);

    public string? ReadCurrentUserValue(string subKeyPath, string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

public sealed class WindowsShortcutResolver : IWindowsShortcutResolver
{
    public string? TryResolveTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell", throwOnError: false);
            if (type is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                return null;
            }

            shortcut = type.InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            return shortcut?.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
