using System.Diagnostics;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;
using Grid.Mo2.Services;

namespace Grid.App.Services;

public sealed record Mo2ModStateMutationResult(
    string ModName,
    bool IsEnabled,
    bool Changed,
    string BackupPath,
    string Detail);

public sealed class Mo2ModStateMutationService
{
    private const int MaximumOutputCharacters = 1024 * 1024;
    private readonly IMo2InstallationReferenceStore references;
    private readonly string entryPoint;
    private readonly string gridDataRoot;
    private readonly string actorId = $"grid-ui-{Environment.UserName}";
    private readonly string sessionId = $"session-{Guid.NewGuid():N}";

    public Mo2ModStateMutationService(
        IMo2InstallationReferenceStore references,
        string engineRoot,
        string gridDataRoot)
    {
        this.references = references ?? throw new ArgumentNullException(nameof(references));
        ArgumentException.ThrowIfNullOrWhiteSpace(engineRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridDataRoot);
        entryPoint = Path.GetFullPath(Path.Combine(
            engineRoot, "scripts", "games", "skyrimspecialedition", "health", "actions",
            "Invoke-GridConfirmedModStateChange.ps1"));
        this.gridDataRoot = Path.GetFullPath(gridDataRoot);
    }

    public bool IsAvailable => OperatingSystem.IsWindows() && File.Exists(entryPoint);

    public async Task<Mo2ModStateMutationResult> SetEnabledAsync(
        InstallationId installationId,
        string profileName,
        string modName,
        bool expectedCurrentState,
        bool desiredState,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("The packaged MO2 mod-state executor is unavailable.");
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modName);
        if (Path.GetFileName(profileName) != profileName || profileName is "." or "..")
            throw new ArgumentException("The MO2 profile must be one exact directory name.", nameof(profileName));
        if (Path.GetFileName(modName) != modName || modName is "." or ".." || modName.EndsWith("_separator", StringComparison.Ordinal))
            throw new ArgumentException("The mod must be one exact non-separator MO2 mod name.", nameof(modName));
        if (expectedCurrentState == desiredState) throw new InvalidOperationException("The mod already has the requested state.");

        var loaded = await references.LoadAsync(cancellationToken).ConfigureAwait(false);
        var matches = loaded.References.Where(reference => reference.InstallationId == installationId).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The selected MO2 installation no longer resolves to one exact saved reference.");
        var reference = matches[0];
        if (reference.GameId != ProductionGridCatalogService.SkyrimSpecialEditionId)
            throw new InvalidOperationException("The selected reference is not the Skyrim Special Edition MO2 adapter context.");

        var transactionRoot = Path.Combine(gridDataRoot, "requests", "mod-state");
        Directory.CreateDirectory(transactionRoot);
        var inputPath = Path.Combine(transactionRoot, $"mod-state-{Guid.NewGuid():N}.json");
        var input = new
        {
            schemaVersion = 1,
            userConfirmed = true,
            confirmedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            installationId = installationId.Value,
            mo2Root = reference.InstanceDirectory,
            profile = profileName,
            modName,
            expectedCurrentState = expectedCurrentState ? "Enabled" : "Disabled",
            desiredState = desiredState ? "Enabled" : "Disabled",
            gridDataRoot,
            actorId,
            sessionId,
        };
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input), cancellationToken).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(entryPoint);
            start.ArgumentList.Add("-InputJsonPath");
            start.ArgumentList.Add(inputPath);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("The MO2 mod-state process could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new TimeoutException("The MO2 mod-state operation exceeded its two-minute limit.");
            }

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (output.Length > MaximumOutputCharacters) throw new InvalidDataException("The MO2 mod-state operation exceeded its bounded output limit.");
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException(Clean(detail));
            }

            using var document = JsonDocument.Parse(output.Trim());
            var root = document.RootElement;
            var execution = root.GetProperty("Execution");
            var state = execution.GetProperty("State").GetString();
            if (!string.Equals(state, desiredState ? "Enabled" : "Disabled", StringComparison.Ordinal))
                throw new InvalidDataException("The MO2 executor did not verify the requested final state.");
            return new(
                modName,
                desiredState,
                execution.GetProperty("Changed").GetBoolean(),
                execution.TryGetProperty("Backup", out var backup) ? backup.GetString() ?? string.Empty : string.Empty,
                execution.GetProperty("Message").GetString() ?? $"{modName} was updated.");
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
        }
    }

    private static string Clean(string value)
    {
        var lines = value.Replace('\r', '\n').Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useful = lines.Where(line => !line.StartsWith("At ", StringComparison.OrdinalIgnoreCase) &&
                                         !line.StartsWith("+", StringComparison.Ordinal) &&
                                         !line.StartsWith("CategoryInfo", StringComparison.OrdinalIgnoreCase) &&
                                         !line.StartsWith("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase));
        var result = string.Join(" ", useful);
        return string.IsNullOrWhiteSpace(result) ? "The MO2 mod-state operation failed." : result;
    }
}
