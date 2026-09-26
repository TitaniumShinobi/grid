using System.Collections.Immutable;
using System.Diagnostics;
using Grid.Core.Models;

internal static class GitBuildProvenanceResolver
{
    private static readonly string[] RequiredInputs =
    [
        "Directory.Build.props",
        "Grid.sln",
        "src/Grid.Core",
        "src/Grid.GtaV.Enrichment.Knowledge",
        "src/Grid.GtaV.Knowledge",
        "eng/catalog/Grid.GtaVEnhanced.Canary",
        "scripts/games/grandtheftautov/catalog/gta_v_enhanced_acquire.py",
        "scripts/games/grandtheftautov/catalog/fivefury.lock.v1.json",
    ];

    public static (string RepositoryRoot, string Commit, ImmutableArray<CatalogCommittedBuildInput> Inputs) Resolve()
    {
        var root = FindRepositoryRoot();
        return Resolve(root);
    }

    public static ResolvedCatalogBuildProvenance ResolveCandidate()
    {
        var root = FindRepositoryRoot();
        var commit = ResolveHead(root);
        var status = RelevantStatus(root);
        if (status.Count == 0)
        {
            var committed = Resolve(root);
            return new ResolvedCatalogBuildProvenance(
                root,
                commit,
                new CatalogBuildProvenance(
                    CatalogBuildProvenance.CurrentSchemaVersion,
                    "grid.gta-v-enhanced.four-kind-canary",
                    "1",
                    commit,
                    committed.Inputs));
        }

        var tracked = RunGit(root, ["ls-files", "-z", "--", .. RequiredInputs])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
        var files = RunGit(root,
                ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", .. RequiredInputs])
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray();
        if (files.IsEmpty)
            throw new InvalidDataException("No exact development build inputs were found.");

        var inputs = ImmutableArray.CreateBuilder<CatalogDevelopmentBuildInput>(files.Length);
        foreach (var file in files)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(absolute))
                throw new InvalidDataException($"A development build input is missing from the worktree: {file}");
            var state = status.GetValueOrDefault(file,
                tracked.Contains(file)
                    ? CatalogDevelopmentBuildInputState.HeadTrackedClean
                    : CatalogDevelopmentBuildInputState.Untracked);
            var digest = ContentDigest.ComputeSha256(File.ReadAllBytes(absolute));
            inputs.Add(new CatalogDevelopmentBuildInput(file, digest, state));
        }
        return new ResolvedCatalogBuildProvenance(
            root,
            commit,
            CatalogBuildProvenance.CreateDevelopment(
                "grid.gta-v-enhanced.four-kind-canary",
                "1",
                commit,
                inputs.ToImmutable()));
    }

    public static string FindRepositoryRoot()
    {
        var root = RunGit(Environment.CurrentDirectory, "rev-parse", "--show-toplevel").Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new InvalidDataException("The canary must run from a Git worktree.");
        return root;
    }

    private static (string RepositoryRoot, string Commit, ImmutableArray<CatalogCommittedBuildInput> Inputs) Resolve(
        string root)
    {
        var commit = ResolveHead(root);

        var statusArguments = new List<string>
        {
            "status", "--porcelain=v1", "-z", "--untracked-files=all", "--",
        };
        statusArguments.AddRange(RequiredInputs);
        var status = RunGit(root, statusArguments.ToArray());
        if (status.Length != 0)
        {
            var paths = status.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Length > 3 ? value[3..] : value)
                .OrderBy(value => value, StringComparer.Ordinal);
            throw new InvalidDataException(
                "Truthful committed-build provenance requires clean tracked inputs. Review and commit: " +
                string.Join(", ", paths));
        }

        var filesArguments = new List<string> { "ls-files", "-z", "--" };
        filesArguments.AddRange(RequiredInputs);
        var files = RunGit(root, filesArguments.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidDataException("No committed build inputs were found.");

        var inputs = ImmutableArray.CreateBuilder<CatalogCommittedBuildInput>(files.Length);
        foreach (var file in files)
        {
            var absolute = Path.GetFullPath(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(absolute))
                throw new InvalidDataException($"Committed build input is missing from the worktree: {file}");
            var objectId = RunGit(root, "rev-parse", $"HEAD:{file}").Trim();
            inputs.Add(new CatalogCommittedBuildInput(file, objectId));
        }

        return (root, commit, inputs.ToImmutable());
    }

    private static string ResolveHead(string root)
    {
        var commit = RunGit(root, "rev-parse", "--verify", "HEAD").Trim();
        if (commit.Length != 40 || commit.Any(value => !Uri.IsHexDigit(value) || char.IsUpper(value)))
            throw new InvalidDataException("Git HEAD is not a full lowercase SHA-1 commit identity.");
        return commit;
    }

    private static Dictionary<string, CatalogDevelopmentBuildInputState> RelevantStatus(string root)
    {
        var arguments = new List<string>
        {
            "status", "--porcelain=v1", "-z", "--untracked-files=all", "--",
        };
        arguments.AddRange(RequiredInputs);
        var values = RunGit(root, arguments.ToArray())
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var result = new Dictionary<string, CatalogDevelopmentBuildInputState>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.Length < 4)
                throw new InvalidDataException("Git returned malformed porcelain status.");
            var index = value[0];
            var worktree = value[1];
            if (index is 'R' or 'C' || worktree is 'R' or 'C')
                throw new InvalidDataException("Renamed/copied build inputs require review before development packaging.");
            var path = NormalizePath(value[3..]);
            var state = (index, worktree) switch
            {
                ('?', '?') => CatalogDevelopmentBuildInputState.Untracked,
                (' ', not ' ') => CatalogDevelopmentBuildInputState.TrackedWorktreeModified,
                (not ' ', ' ') => CatalogDevelopmentBuildInputState.TrackedIndexModified,
                (not ' ', not ' ') => CatalogDevelopmentBuildInputState.TrackedIndexAndWorktreeModified,
                _ => CatalogDevelopmentBuildInputState.HeadTrackedClean,
            };
            if (!result.TryAdd(path, state))
                throw new InvalidDataException($"Git returned duplicate status for build input: {path}");
        }
        return result;
    }

    private static string NormalizePath(string value)
    {
        var result = value.Replace('\\', '/');
        if (result.StartsWith("/", StringComparison.Ordinal) ||
            result.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException($"Git returned a noncanonical build input path: {value}");
        return result;
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            throw new InvalidOperationException("Git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidDataException($"Git provenance command failed: {error.Trim()}");
        return output;
    }
}

internal sealed record ResolvedCatalogBuildProvenance(
    string RepositoryRoot,
    string HeadCommit,
    CatalogBuildProvenance Provenance);
