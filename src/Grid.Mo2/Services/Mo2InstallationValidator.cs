using System.Collections.Immutable;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2InstallationValidator : IMo2InstallationValidator
{
    private const string IniFileName = "ModOrganizer.ini";
    private const string ExecutableFileName = "ModOrganizer.exe";
    private readonly IMo2ReadOnlyFileSystem fileSystem;
    private readonly IMo2IniReader iniReader;
    private readonly IMo2PathCanonicalizer paths;
    private readonly string globalInstancesRoot;

    public Mo2InstallationValidator(
        IMo2ReadOnlyFileSystem fileSystem,
        IMo2IniReader iniReader,
        IMo2PathCanonicalizer paths,
        string globalInstancesRoot)
    {
        this.fileSystem = fileSystem;
        this.iniReader = iniReader;
        this.paths = paths;
        if (!paths.TryCanonicalize(globalInstancesRoot, out this.globalInstancesRoot, out var error))
        {
            throw new ArgumentException($"Invalid global-instance root: {error}", nameof(globalInstancesRoot));
        }
    }

    public async Task<Mo2InstallationValidation> ValidateAsync(
        Mo2ValidationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = ImmutableArray.CreateBuilder<Mo2ValidationIssue>();
        var observations = ImmutableArray.CreateBuilder<Mo2PathObservation>();

        var applicationDirectory = CanonicalizeApplication(request.ApplicationDirectory, issues);
        var instanceDirectory = CanonicalizeDirectory(request.InstancePath, "instance", issues);
        if (applicationDirectory is null && instanceDirectory is null)
        {
            return Result(Mo2ValidationStatus.Invalid, Mo2SelectionKind.Invalid, issues, observations);
        }

        var authorizedConfiguredPaths = CanonicalizeAuthorizations(request.EffectiveAuthorizedConfiguredPaths, issues);

        if (applicationDirectory is null && instanceDirectory is not null &&
            Path.GetFileName(instanceDirectory).Equals(ExecutableFileName, StringComparison.OrdinalIgnoreCase))
        {
            applicationDirectory = Path.GetDirectoryName(instanceDirectory);
            instanceDirectory = null;
        }

        var executablePath = applicationDirectory is null ? null : Path.Combine(applicationDirectory, ExecutableFileName);
        if (executablePath is not null)
        {
            ObserveRequired("MO2 executable", executablePath, file: true, issues, observations);
        }

        if (instanceDirectory is null && applicationDirectory is not null)
        {
            var portableIni = Path.Combine(applicationDirectory, IniFileName);
            if (fileSystem.ProbeFile(portableIni) == Mo2PathState.Present)
            {
                instanceDirectory = applicationDirectory;
            }
            else
            {
                observations.Add(new("Instance configuration", portableIni, fileSystem.ProbeFile(portableIni)));
                issues.Add(new(
                    "mo2.instance.required",
                    Mo2IssueSeverity.Error,
                    "The MO2 application is valid, but a portable or global instance must also be selected.",
                    "Instance configuration"));
                return Result(
                    DetermineStatus(issues, observations),
                    Mo2SelectionKind.ApplicationDirectory,
                    issues,
                    observations,
                    applicationDirectory: applicationDirectory,
                    executablePath: executablePath);
            }
        }

        var iniPath = instanceDirectory is null ? null : Path.Combine(instanceDirectory, IniFileName);
        if (iniPath is null || fileSystem.ProbeFile(iniPath) != Mo2PathState.Present)
        {
            if (instanceDirectory is not null)
            {
                var classified = ClassifyNonInstanceDirectory(instanceDirectory, observations);
                issues.Add(new(
                    "mo2.instance.ini_missing",
                    Mo2IssueSeverity.Error,
                    classified switch
                    {
                        Mo2SelectionKind.BaseDirectory => "This is an MO2 base/content directory, not the instance directory containing ModOrganizer.ini.",
                        Mo2SelectionKind.GameDirectory => "This is the configured game directory, not an MO2 application or instance directory.",
                        _ => "ModOrganizer.ini was not found in the selected instance directory.",
                    },
                    "Instance configuration"));
                observations.Add(new("Instance configuration", iniPath, iniPath is null ? Mo2PathState.Invalid : fileSystem.ProbeFile(iniPath)));
                return Result(
                    DetermineStatus(issues, observations),
                    classified,
                    issues,
                    observations,
                    applicationDirectory: applicationDirectory,
                    executablePath: executablePath,
                    instanceDirectory: instanceDirectory,
                    iniPath: iniPath);
            }

            return Result(Mo2ValidationStatus.Invalid, Mo2SelectionKind.Invalid, issues, observations);
        }

        observations.Add(new("Instance configuration", iniPath, Mo2PathState.Present));
        var isPortable = applicationDirectory is not null && paths.Equals(applicationDirectory, instanceDirectory!);
        var isGlobal = paths.IsImmediateChildOf(instanceDirectory!, globalInstancesRoot);
        Mo2InstanceKind? instanceKind = isPortable ? Mo2InstanceKind.Portable : isGlobal ? Mo2InstanceKind.Global : null;
        var selectionKind = isPortable
            ? Mo2SelectionKind.PortableInstance
            : isGlobal
                ? Mo2SelectionKind.GlobalInstance
                : Mo2SelectionKind.InstanceDirectory;

        if (instanceKind is null)
        {
            issues.Add(new(
                "mo2.instance.kind_unknown",
                Mo2IssueSeverity.Error,
                "The directory has instance configuration but is neither portable nor in MO2's global-instance root."));
        }

        if (applicationDirectory is null)
        {
            issues.Add(new(
                "mo2.application.required",
                Mo2IssueSeverity.Error,
                "Select the MO2 application directory containing ModOrganizer.exe before connecting."));
        }

        Mo2IniDocument ini;
        try
        {
            ini = await iniReader.ReadAsync(iniPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            issues.Add(new(
                "mo2.ini.unreadable",
                Mo2IssueSeverity.Error,
                exception is UnauthorizedAccessException
                    ? "The instance configuration could not be read because access was denied."
                    : exception is InvalidDataException
                        ? "The instance configuration is malformed or exceeds safe read limits."
                        : "The instance configuration is unavailable."));
            return Result(
                DetermineStatus(issues, observations),
                selectionKind,
                issues,
                observations,
                instanceKind,
                applicationDirectory,
                executablePath,
                instanceDirectory,
                iniPath);
        }

        var baseDirectory = ResolveBaseDirectory(ini.Get("Settings", "base_directory"), instanceDirectory!, issues);
        var modsDirectory = ResolveConfiguredDirectory(ini.Get("Settings", "mod_directory"), baseDirectory, "mods", issues);
        var profilesDirectory = ResolveConfiguredDirectory(ini.Get("Settings", "profiles_directory"), baseDirectory, "profiles", issues);
        var downloadsDirectory = ResolveConfiguredDirectory(ini.Get("Settings", "download_directory"), baseDirectory, "downloads", issues);
        var overwriteDirectory = ResolveConfiguredDirectory(ini.Get("Settings", "overwrite_directory"), baseDirectory, "overwrite", issues);
        var gameDirectory = ResolveAbsolutePath(ini.Get("General", "gamePath"), "game path", issues);
        var gameName = ini.Get("General", "gameName")?.Trim();

        ObserveConfiguredRequired("Base directory", baseDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        ObserveConfiguredRequired("Mods directory", modsDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        ObserveConfiguredRequired("Profiles directory", profilesDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        ObserveConfiguredRequired("Downloads directory", downloadsDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        ObserveConfiguredRequired("Overwrite directory", overwriteDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        ObserveConfiguredRequired("Game directory", gameDirectory, false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);

        if (gameDirectory is not null)
        {
            ObserveConfiguredRequired("Skyrim executable", Path.Combine(gameDirectory, "SkyrimSE.exe"), true, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
            ObserveConfiguredRequired("Skyrim Data directory", Path.Combine(gameDirectory, "Data"), false, applicationDirectory, instanceDirectory, authorizedConfiguredPaths, issues, observations);
        }

        if (string.IsNullOrWhiteSpace(gameName))
        {
            issues.Add(new("mo2.game.name_missing", Mo2IssueSeverity.Error, "The instance configuration does not identify its managed game."));
        }
        else if (request.ExpectedGameId.Value.Equals("game.skyrim-special-edition", StringComparison.Ordinal) &&
            !IsSkyrimSpecialEditionName(gameName))
        {
            issues.Add(new("mo2.game.mismatch", Mo2IssueSeverity.Error, "The MO2 instance is not configured for Skyrim Special Edition."));
        }

        string? connectionKey = null;
        if (applicationDirectory is not null && instanceDirectory is not null)
        {
            connectionKey = paths.GetIdentityKey(applicationDirectory, instanceDirectory);
        }

        var status = DetermineStatus(issues, observations);
        return Result(
            status,
            selectionKind,
            issues,
            observations,
            instanceKind,
            applicationDirectory,
            executablePath,
            instanceDirectory,
            iniPath,
            baseDirectory,
            modsDirectory,
            profilesDirectory,
            downloadsDirectory,
            overwriteDirectory,
            gameDirectory,
            gameName,
            connectionKey);
    }

    private string? CanonicalizeApplication(string? value, ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        if (Path.GetFileName(candidate).Equals(ExecutableFileName, StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.GetDirectoryName(candidate) ?? candidate;
        }

        return CanonicalizeDirectory(candidate, "application", issues);
    }

    private string? CanonicalizeDirectory(
        string? value,
        string label,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (paths.TryCanonicalize(value, out var canonical, out var error))
        {
            return canonical;
        }

        issues.Add(new($"mo2.{label}.path_invalid", Mo2IssueSeverity.Error, $"The {label} path is invalid: {error}"));
        return null;
    }

    private string? ResolveBaseDirectory(
        string? configured,
        string instanceDirectory,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return instanceDirectory;
        }

        return ResolveAbsolutePath(configured, "base directory", issues);
    }

    private string? ResolveConfiguredDirectory(
        string? configured,
        string? baseDirectory,
        string defaultName,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (baseDirectory is null)
        {
            return null;
        }

        var value = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(baseDirectory, defaultName)
            : configured.Replace("%BASE_DIR%", baseDirectory, StringComparison.Ordinal);
        return ResolveAbsolutePath(value, $"{defaultName} directory", issues);
    }

    private string? ResolveAbsolutePath(
        string? value,
        string label,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new($"mo2.{label.Replace(' ', '_')}.missing", Mo2IssueSeverity.Error, $"The {label} is not configured."));
            return null;
        }

        if (value.Contains('%', StringComparison.Ordinal))
        {
            issues.Add(new("mo2.path.token_unresolved", Mo2IssueSeverity.Error, $"The {label} contains an unsupported path token."));
            return null;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            issues.Add(new("mo2.path.relative", Mo2IssueSeverity.Error, $"The {label} must resolve to an absolute path."));
            return null;
        }

        if (paths.TryNormalizeLexically(value, out var canonical, out var error))
        {
            return canonical;
        }

        issues.Add(new("mo2.path.invalid", Mo2IssueSeverity.Error, $"The {label} is invalid: {error}"));
        return null;
    }

    private void ObserveRequired(
        string label,
        string? path,
        bool file,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        ImmutableArray<Mo2PathObservation>.Builder observations)
    {
        if (path is null)
        {
            return;
        }

        var state = file ? fileSystem.ProbeFile(path) : fileSystem.ProbeDirectory(path);
        observations.Add(new(label, path, state));
        if (state != Mo2PathState.Present)
        {
            issues.Add(new(
                state is Mo2PathState.Inaccessible or Mo2PathState.Unavailable ? "mo2.path.inaccessible" : "mo2.path.missing",
                Mo2IssueSeverity.Error,
                state is Mo2PathState.Inaccessible or Mo2PathState.Unavailable
                    ? $"The {label} is unavailable or access was denied."
                    : $"The {label} is missing or has the wrong type.",
                label));
        }
    }

    private void ObserveConfiguredRequired(
        string label,
        string? path,
        bool file,
        string? applicationDirectory,
        string? instanceDirectory,
        ImmutableArray<string> authorizedConfiguredPaths,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        ImmutableArray<Mo2PathObservation>.Builder observations)
    {
        if (path is null)
        {
            return;
        }

        var isWithinSelectedRoot = IsWithinRoot(path, applicationDirectory) || IsWithinRoot(path, instanceDirectory);
        var isExactlyAuthorized = authorizedConfiguredPaths.Any(authorized => PathEquals(path, authorized));
        if (!isWithinSelectedRoot && !isExactlyAuthorized)
        {
            observations.Add(new(label, path, Mo2PathState.AuthorizationRequired));
            issues.Add(new(
                "mo2.path.authorization_required",
                Mo2IssueSeverity.Error,
                $"Grid needs explicit read authorization for the configured {label}; the path was resolved but not checked.",
                label));
            return;
        }

        if (!paths.TryCanonicalize(path, out var canonicalPath, out var error))
        {
            observations.Add(new(label, path, Mo2PathState.Invalid));
            issues.Add(new(
                "mo2.path.invalid",
                Mo2IssueSeverity.Error,
                $"The configured {label} is invalid or unavailable: {error}",
                label));
            return;
        }

        var physicalPathIsWithinSelectedRoot =
            IsWithinRoot(canonicalPath, applicationDirectory) || IsWithinRoot(canonicalPath, instanceDirectory);
        var physicalPathIsExactlyAuthorized =
            authorizedConfiguredPaths.Any(authorized => PathEquals(canonicalPath, authorized));
        if (!physicalPathIsWithinSelectedRoot && !physicalPathIsExactlyAuthorized)
        {
            observations.Add(new(label, canonicalPath, Mo2PathState.AuthorizationRequired));
            issues.Add(new(
                "mo2.path.authorization_required",
                Mo2IssueSeverity.Error,
                $"Grid needs explicit read authorization for the resolved configured {label}; the target was not checked.",
                label));
            return;
        }

        ObserveRequired(label, canonicalPath, file, issues, observations);
    }

    private ImmutableArray<string> CanonicalizeAuthorizations(
        ImmutableArray<string> configuredPaths,
        ImmutableArray<Mo2ValidationIssue>.Builder issues)
    {
        if (configuredPaths.IsEmpty)
        {
            return [];
        }

        if (configuredPaths.Length > 32)
        {
            issues.Add(new(
                "mo2.authorization.too_many_paths",
                Mo2IssueSeverity.Error,
                "The configured-path authorization contains too many entries."));
            return [];
        }

        var canonical = ImmutableArray.CreateBuilder<string>(configuredPaths.Length);
        foreach (var configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath) ||
                !paths.TryNormalizeLexically(configuredPath, out var canonicalPath, out _))
            {
                issues.Add(new(
                    "mo2.authorization.path_invalid",
                    Mo2IssueSeverity.Error,
                    "A configured-path authorization is invalid and was ignored."));
                continue;
            }

            if (!canonical.Any(existing => PathEquals(existing, canonicalPath)))
            {
                canonical.Add(canonicalPath);
            }
        }

        return canonical.ToImmutable();
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(left),
            Path.TrimEndingDirectorySeparator(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWithinRoot(string path, string? root)
    {
        if (root is null)
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private Mo2SelectionKind ClassifyNonInstanceDirectory(
        string directory,
        ImmutableArray<Mo2PathObservation>.Builder observations)
    {
        var skyrim = Path.Combine(directory, "SkyrimSE.exe");
        if (fileSystem.ProbeFile(skyrim) == Mo2PathState.Present)
        {
            observations.Add(new("Skyrim executable", skyrim, Mo2PathState.Present));
            return Mo2SelectionKind.GameDirectory;
        }

        var contentDirectories = new[] { "mods", "profiles", "downloads", "overwrite" }
            .Count(name => fileSystem.ProbeDirectory(Path.Combine(directory, name)) == Mo2PathState.Present);
        return contentDirectories >= 2 ? Mo2SelectionKind.BaseDirectory : Mo2SelectionKind.Invalid;
    }

    private static Mo2ValidationStatus DetermineStatus(
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        ImmutableArray<Mo2PathObservation>.Builder observations)
    {
        if (observations.Any(item => item.State is Mo2PathState.Inaccessible or Mo2PathState.Unavailable))
        {
            return Mo2ValidationStatus.Inaccessible;
        }

        var nonAuthorizationErrors = issues
            .Where(issue => issue.Severity == Mo2IssueSeverity.Error &&
                issue.Code != "mo2.path.authorization_required")
            .ToArray();
        if (nonAuthorizationErrors.Length > 0)
        {
            return nonAuthorizationErrors.Any(issue =>
                issue.Code.Contains("missing", StringComparison.Ordinal) ||
                issue.Code.EndsWith("required", StringComparison.Ordinal))
                ? Mo2ValidationStatus.Incomplete
                : Mo2ValidationStatus.Invalid;
        }

        if (observations.Any(item => item.State == Mo2PathState.AuthorizationRequired))
        {
            return Mo2ValidationStatus.Incomplete;
        }

        return Mo2ValidationStatus.Valid;
    }

    private static bool IsSkyrimSpecialEditionName(string name) =>
        name.Equals("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SkyrimSE", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Skyrim SE", StringComparison.OrdinalIgnoreCase);

    private static Mo2InstallationValidation Result(
        Mo2ValidationStatus status,
        Mo2SelectionKind selectionKind,
        ImmutableArray<Mo2ValidationIssue>.Builder issues,
        ImmutableArray<Mo2PathObservation>.Builder observations,
        Mo2InstanceKind? instanceKind = null,
        string? applicationDirectory = null,
        string? executablePath = null,
        string? instanceDirectory = null,
        string? iniPath = null,
        string? baseDirectory = null,
        string? modsDirectory = null,
        string? profilesDirectory = null,
        string? downloadsDirectory = null,
        string? overwriteDirectory = null,
        string? gameDirectory = null,
        string? gameName = null,
        string? connectionKey = null) =>
        new(
            status,
            selectionKind,
            status == Mo2ValidationStatus.Valid,
            instanceKind,
            applicationDirectory,
            executablePath,
            instanceDirectory,
            iniPath,
            baseDirectory,
            modsDirectory,
            profilesDirectory,
            downloadsDirectory,
            overwriteDirectory,
            gameDirectory,
            gameName,
            connectionKey,
            observations.ToImmutable(),
            issues.ToImmutable());
}
