using System.Diagnostics;
using System.Text.Json;

namespace Grid.App.Services;

public sealed record ProviderGameCandidate(
    string GameId,string GameDisplayName,string Edition,string ProviderId,string Status,string InstallRoot,string ExecutablePath);
public sealed record ProviderManagerCandidate(string ProviderId,string Status,string ExecutablePath,bool Running,string ProfileFidelity);
public sealed record ProviderDiscoverySnapshot(
    IReadOnlyList<ProviderGameCandidate> Games,IReadOnlyList<ProviderManagerCandidate> Managers,string ReportPath);

public sealed class GridProviderDiscoveryService(string scriptPath,string reportPath)
{
    private readonly string scriptPath=Path.GetFullPath(scriptPath);
    private readonly string reportPath=Path.GetFullPath(reportPath);

    public async Task<ProviderDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken=default)
    {
        if(!File.Exists(scriptPath))throw new FileNotFoundException("Grid provider discovery is unavailable.",scriptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        var start=new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
        };
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-ExecutionPolicy");start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");start.ArgumentList.Add(scriptPath);start.ArgumentList.Add("-OutputPath");start.ArgumentList.Add(reportPath);
        using var process=Process.Start(start)??throw new InvalidOperationException("Provider discovery could not start.");
        var outputTask=process.StandardOutput.ReadToEndAsync(cancellationToken);var errorTask=process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error=await errorTask.ConfigureAwait(false);_ = await outputTask.ConfigureAwait(false);
        if(process.ExitCode!=0)throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)?"Provider discovery failed.":error.Trim());
        return await ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderDiscoverySnapshot> ReadAsync(CancellationToken cancellationToken=default)
    {
        if(!File.Exists(reportPath))return new([],[],reportPath);
        await using var stream=File.OpenRead(reportPath);using var document=await JsonDocument.ParseAsync(stream,cancellationToken:cancellationToken).ConfigureAwait(false);
        var games=new List<ProviderGameCandidate>();var managers=new List<ProviderManagerCandidate>();
        if(document.RootElement.TryGetProperty("registrationCandidates",out var registrations))foreach(var registration in registrations.EnumerateArray())
        {
            if(!registration.TryGetProperty("observations",out var observations))continue;
            var observation=observations.EnumerateArray().FirstOrDefault();if(observation.ValueKind!=JsonValueKind.Object)continue;
            games.Add(new(
                Text(registration,"gameId"),Text(observation,"gameDisplayName"),Text(registration,"edition"),Text(observation,"providerId"),
                Text(registration,"registrationStatus"),Text(registration,"installRoot"),Text(observation,"executablePath")));
        }
        if(document.RootElement.TryGetProperty("modManagers",out var managerArray))foreach(var manager in managerArray.EnumerateArray())
            managers.Add(new(Text(manager,"providerId"),Text(manager,"status"),Text(manager,"executablePath"),Boolean(manager,"running"),Text(manager,"profileFidelity",false)));
        return new(games.Where(value=>value.Status=="ReadyForReview").ToArray(),managers.Where(value=>value.Status is "Verified" or "InstanceNotResolved").ToArray(),reportPath);
    }
    private static string Text(JsonElement element,string name,bool required=true)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:
        required?throw new InvalidDataException($"Discovery report is missing {name}."):string.Empty;
    private static bool Boolean(JsonElement element,string name)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.True;
}
