using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2LaunchInvocationBuilder
{
    public Mo2RuntimeKind Classify(Mo2ObservedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var leaf = Path.GetFileName(executable.Binary.CanonicalPath ?? executable.Binary.ConfiguredValue) ?? string.Empty;
        var title = executable.Title.Trim();
        if (leaf.Equals("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase) &&
            title.Contains("skyrim", StringComparison.OrdinalIgnoreCase) &&
            (title.Contains("special edition", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("skyrimse", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("skyrim se", StringComparison.OrdinalIgnoreCase)))
            return Mo2RuntimeKind.SkyrimSpecialEdition;
        if (leaf.Equals("skse64_loader.exe", StringComparison.OrdinalIgnoreCase) &&
            (title.Contains("skse", StringComparison.OrdinalIgnoreCase) ||
             title.Contains("script extender", StringComparison.OrdinalIgnoreCase)))
            return Mo2RuntimeKind.Skse;
        return Mo2RuntimeKind.InspectOnly;
    }

    public Mo2LaunchInvocation Build(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedExecutable executable,
        Mo2VersionEvidence version)
        => BuildCore(reference, validation, executable, version, null);

    public Mo2LaunchInvocation BuildForProfile(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedExecutable executable,
        Mo2VersionEvidence version,
        string profileName)
    {
        ValidateArgument(profileName, "profile name");
        if (profileName is "." or ".." || profileName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidOperationException("The MO2 profile name must be one exact profile-directory leaf.");
        return BuildCore(reference, validation, executable, version, profileName);
    }

    private Mo2LaunchInvocation BuildCore(
        Mo2InstallationReference reference,
        Mo2InstallationValidation validation,
        Mo2ObservedExecutable executable,
        Mo2VersionEvidence version,
        string? profileName)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(executable);
        ArgumentNullException.ThrowIfNull(version);
        if (version.Status != Mo2VersionEvidenceStatus.Supported)
            throw new InvalidOperationException(version.Detail);
        if (!validation.CanConnect || validation.Status != Mo2ValidationStatus.Valid ||
            validation.InstanceKind != reference.InstanceKind)
            throw new InvalidOperationException("The MO2 connection is not freshly valid.");
        if (!Path.GetFullPath(reference.ExecutablePath).Equals(Path.GetFullPath(validation.ExecutablePath ?? string.Empty), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(reference.ExecutablePath).Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The MO2 executable identity changed.");
        if (Classify(executable) == Mo2RuntimeKind.InspectOnly || executable.IsDuplicate ||
            executable.Binary.Availability != Mo2ExecutablePathAvailability.Available)
            throw new InvalidOperationException("The selected executable is inspect-only or ambiguous.");
        ValidateArgument(executable.Title, "configured title");

        var arguments = ImmutableArray.CreateBuilder<string>();
        if (reference.InstanceKind == Mo2InstanceKind.Portable)
        {
            if (profileName is not null) { arguments.Add("-p"); arguments.Add(profileName); }
            arguments.Add("run"); arguments.Add("-e"); arguments.Add(executable.Title);
        }
        else if (reference.InstanceKind == Mo2InstanceKind.Global)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(reference.InstanceDirectory));
            ValidateInstanceLeaf(leaf);
            arguments.Add("-i"); arguments.Add(leaf);
            if (profileName is not null) { arguments.Add("-p"); arguments.Add(profileName); }
            arguments.Add("run"); arguments.Add("-e"); arguments.Add(executable.Title);
        }
        else
        {
            throw new InvalidOperationException("The MO2 instance kind is unsupported for automated launch.");
        }

        var immutableArguments = arguments.ToImmutable();
        var route = string.Join('\n', new[] { reference.ExecutablePath, version.ExecutableIdentity }.Concat(immutableArguments));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route))).ToLowerInvariant();
        return new(reference.ExecutablePath, immutableArguments, version.ExecutableIdentity, fingerprint);
    }

    private static void ValidateInstanceLeaf(string leaf)
    {
        ValidateArgument(leaf, "instance name");
        if (leaf is "." or ".." || leaf.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidOperationException("The global instance directory leaf is invalid.");
    }

    private static void ValidateArgument(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || value.Any(char.IsControl))
            throw new InvalidOperationException($"The {label} cannot be represented safely as an individual argument.");
    }
}
