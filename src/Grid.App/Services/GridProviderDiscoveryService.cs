using System.Diagnostics;
using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

public sealed record ProviderGameCandidate(
    string GameId,string GameDisplayName,string Edition,string ProviderId,string Status,string InstallRoot,string ExecutablePath);
public sealed record ProviderManagerCandidate(
    string ProviderId,
    string Status,
    string ExecutablePath,
    string? ApplicationRoot,
    string? InstanceRoot,
    IReadOnlyList<string> ProfileRoots,
    IReadOnlyList<string> Profiles,
    bool Running,
    string ProfileFidelity);
public sealed record ProviderDiscoverySnapshot(
    IReadOnlyList<ProviderGameCandidate> Games,IReadOnlyList<ProviderManagerCandidate> Managers,string ReportPath);

public sealed class GridProviderDiscoveryService(string scriptPath,string reportPath)
{
    private readonly string scriptPath=Path.GetFullPath(scriptPath);
    private readonly string reportPath=Path.GetFullPath(reportPath);
    private readonly string gameDefinitionRoot=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scriptPath)!,"..","games"));

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
                NormalizeCatalogGameId(Text(registration,"gameId")),Text(observation,"gameDisplayName"),Text(registration,"edition"),Text(observation,"providerId"),
                Text(registration,"registrationStatus"),Text(registration,"installRoot"),Text(observation,"executablePath")));
        }
        if(document.RootElement.TryGetProperty("modManagers",out var managerArray))foreach(var manager in managerArray.EnumerateArray())
            managers.Add(new(
                Text(manager,"providerId"),
                Text(manager,"status"),
                Text(manager,"executablePath"),
                NullableText(manager,"applicationRoot"),
                NullableText(manager,"instanceRoot"),
                StringOrArray(manager,"profilesRoot"),
                StringOrArray(manager,"profiles"),
                Boolean(manager,"running"),
                Text(manager,"profileFidelity",false)));
        return new(games.Where(value=>value.Status=="ReadyForReview").ToArray(),managers.Where(value=>value.Status is "Verified" or "InstanceNotResolved").ToArray(),reportPath);
    }

    public async Task<ProviderGameCandidate> ResolveManualGameAsync(
        GameId gameId,
        string gameDisplayName,
        string selectedDirectory,
        CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(selectedDirectory)
            ? throw new ArgumentException("A game directory is required.",nameof(selectedDirectory))
            : selectedDirectory));
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("The selected game directory does not exist.");
        if(!Directory.Exists(gameDefinitionRoot))throw new InvalidOperationException("GRID's game-installation definitions are unavailable.");

        foreach(var definitionPath in Directory.EnumerateFiles(gameDefinitionRoot,"discovery.steam.v1.json",SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            await using var stream=File.OpenRead(definitionPath);
            using var document=await JsonDocument.ParseAsync(stream,cancellationToken:cancellationToken).ConfigureAwait(false);
            if(!document.RootElement.TryGetProperty("applications",out var applications)||applications.ValueKind!=JsonValueKind.Array)continue;
            foreach(var application in applications.EnumerateArray())
            {
                var definitionGameId=NormalizeCatalogGameId(Text(application,"gameId"));
                if(!definitionGameId.Equals(gameId.Value,StringComparison.Ordinal))continue;
                var required=StringArray(application,"requiredFiles");
                var conflicting=StringArray(application,"conflictingFiles");
                if(required.Length==0)throw new InvalidDataException($"The installation definition for {gameDisplayName} has no required files.");
                var requiredPaths=required.Select(value=>SafeChild(root,value)).ToArray();
                var missing=requiredPaths.Where(value=>!File.Exists(value)).Select(Path.GetFileName).ToArray();
                if(missing.Length>0)throw new InvalidDataException($"The selected directory is not a valid {gameDisplayName} installation. Missing: {string.Join(", ",missing)}.");
                var conflicts=conflicting.Select(value=>SafeChild(root,value)).Where(File.Exists).Select(Path.GetFileName).ToArray();
                if(conflicts.Length>0)throw new InvalidDataException($"The selected directory is ambiguous for {gameDisplayName}. Conflicting files: {string.Join(", ",conflicts)}.");
                return new(gameId.Value,gameDisplayName,Text(application,"edition"),"manual","ReadyForReview",root,requiredPaths[0]);
            }
        }
        throw new InvalidOperationException($"GRID has no installation definition for {gameDisplayName}.");
    }

    public static string NormalizeCatalogGameId(string value)=>value.Trim().ToLowerInvariant() switch
    {
        "game.skyrim-special-edition" or "skyrim-special-edition" or "skyrimspecialedition"=>"game.skyrim-special-edition",
        "game.grandtheftautov-legacy" or "grandtheftautov-legacy"=>"game.grandtheftautov-legacy",
        "game.grandtheftautov-enhanced" or "grandtheftautov-enhanced"=>"game.grandtheftautov-enhanced",
        var normalized when normalized.StartsWith("game.",StringComparison.Ordinal)=>normalized,
        var normalized=>$"game.{normalized}",
    };

    private static string[] StringArray(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Array
            ? value.EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String).Select(item=>item.GetString()!).Where(item=>!string.IsNullOrWhiteSpace(item)).ToArray()
            : [];

    private static string[] StringOrArray(JsonElement element,string name)
    {
        if(!element.TryGetProperty(name,out var value)||value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return [];
        if(value.ValueKind==JsonValueKind.String)
        {
            var text=value.GetString();
            return string.IsNullOrWhiteSpace(text)?[]:[text];
        }
        return value.ValueKind==JsonValueKind.Array
            ? value.EnumerateArray().Where(item=>item.ValueKind==JsonValueKind.String).Select(item=>item.GetString()!).Where(item=>!string.IsNullOrWhiteSpace(item)).ToArray()
            : [];
    }

    private static string SafeChild(string root,string relative)
    {
        if(string.IsNullOrWhiteSpace(relative)||Path.IsPathFullyQualified(relative))throw new InvalidDataException("A game-installation definition contains an unsafe required path.");
        var full=Path.GetFullPath(Path.Combine(root,relative));
        if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A game-installation definition escapes the selected directory.");
        return full;
    }
    private static string Text(JsonElement element,string name,bool required=true)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString()!:
        required?throw new InvalidDataException($"Discovery report is missing {name}."):string.Empty;
    private static string? NullableText(JsonElement element,string name)=>
        element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(value.GetString())?value.GetString():null;
    private static bool Boolean(JsonElement element,string name)=>element.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.True;
}
