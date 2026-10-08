using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Core.Services;

namespace Grid.App.Services;

public sealed class PowerShellInstalledToolIdentityService(string scriptPath) : IInstalledToolIdentityService
{
    private readonly string scriptPath = Path.GetFullPath(string.IsNullOrWhiteSpace(scriptPath)
        ? throw new ArgumentException("Installed-tool observation script path is required.", nameof(scriptPath))
        : scriptPath);

    public async Task<InstalledToolKnowledge> ResolveAsync(string exactBinaryPath, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(exactBinaryPath)
            ? throw new ArgumentException("An exact executable path is required.", nameof(exactBinaryPath))
            : exactBinaryPath);
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The selected executable is unavailable.", path);
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("GRID's installed-tool observer is unavailable.", scriptPath);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        start.ArgumentList.Add("-ExecutablePath");
        start.ArgumentList.Add(path);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Installed-tool observation could not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Installed-tool observation failed." : error.Trim());

        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        var observedAt = DateTimeOffset.Parse(root.GetProperty("observedAtUtc").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var hash = root.GetProperty("sha256").GetString()!;
        var evidence = root.GetProperty("evidence").EnumerateArray().Select(item => new InstalledToolEvidenceReceipt(
            item.GetProperty("kind").GetString()!,
            item.GetProperty("sourceIdentifier").GetString()!,
            item.GetProperty("claim").GetString()!,
            item.GetProperty("value").GetString()!,
            observedAt)).ToImmutableArray();
        var id = new InstalledToolKnowledgeId($"tool-evidence.{hash[..24].ToLowerInvariant()}");
        var productName = Text(root, "productName");
        var fileVersion = Text(root, "fileVersion");
        var productVersion = Text(root, "productVersion");
        var state = !string.IsNullOrWhiteSpace(fileVersion) || !string.IsNullOrWhiteSpace(productVersion)
            ? InstalledToolIdentityState.VersionResolved
            : InstalledToolIdentityState.Installed;
        return new(
            InstalledToolKnowledge.CurrentSchemaVersion,
            id,
            path,
            hash,
            productName,
            Text(root, "companyName"),
            fileVersion,
            productVersion,
            root.GetProperty("signatureStatus").GetString() ?? "Unknown",
            Text(root, "signatureSubject"),
            Text(root, "signatureThumbprint"),
            null,
            state,
            [],
            evidence,
            ["Canonical ToolID is unresolved.", "No provenance-backed canonical GameID compatibility statement was found."],
            observedAt);
    }

    private static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
}
