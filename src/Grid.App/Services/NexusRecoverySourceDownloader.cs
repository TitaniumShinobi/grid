using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Grid.Core.Models;
using Grid.Mo2.Models;
using Grid.Mo2.Services;

namespace Grid.App.Services;

public enum NexusSourceAcquisitionDisposition
{
    Downloaded,
    AlreadyPresent,
    BrowserRequired,
    SourceUnresolved,
    Failed,
}

public sealed record NexusSourceAcquisitionResult(
    string ExpectedArchiveLeaf,
    string OfficialFilesUri,
    NexusSourceAcquisitionDisposition Disposition,
    string Detail);

public sealed record NexusSourceAcquisitionSummary(
    ImmutableArray<NexusSourceAcquisitionResult> Results)
{
    public int Downloaded => Results.Count(result => result.Disposition == NexusSourceAcquisitionDisposition.Downloaded);
    public int AlreadyPresent => Results.Count(result => result.Disposition == NexusSourceAcquisitionDisposition.AlreadyPresent);
    public int BrowserRequired => Results.Count(result => result.Disposition == NexusSourceAcquisitionDisposition.BrowserRequired);
    public int SourceUnresolved => Results.Count(result => result.Disposition == NexusSourceAcquisitionDisposition.SourceUnresolved);
    public int Failed => Results.Count(result => result.Disposition == NexusSourceAcquisitionDisposition.Failed);
}

public sealed record NexusSourceAcquisitionProgress(int Completed, int Total, string CurrentArchiveLeaf);

public sealed class NexusRecoverySourceDownloader(
    INexusCredentialStore credentialStore,
    IMo2InstallationReferenceStore referenceStore,
    Mo2OnboardingCoordinator onboarding,
    HttpClient? httpClient = null)
{
    private const long MaximumArchiveBytes = 20L * 1024 * 1024 * 1024;
    private readonly HttpClient http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    public bool HasCredential => credentialStore.IsConfigured;

    public async Task<NexusSourceAcquisitionSummary> AcquireAsync(
        AssistantRepairAvailability repair,
        IProgress<NexusSourceAcquisitionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repair);
        var actions = repair.HumanActionManifest?.ManualAcquisitions ?? [];
        if (actions.IsDefaultOrEmpty) return new([]);
        if (repair.InstallationId is null)
            return Failure(actions, "The recovery task does not identify its connected MO2 installation.");
        var apiKey = credentialStore.LoadApiKey();
        if (apiKey is null)
            return new(actions.Select(action => Result(action, NexusSourceAcquisitionDisposition.BrowserRequired,
                "Connect a Nexus API key in Settings, or use the official Files link.")).ToImmutableArray());

        var load = await referenceStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var references = load.References.Where(reference => reference.InstallationId == repair.InstallationId).ToArray();
        if (references.Length != 1)
            return Failure(actions, "The task's connected MO2 installation is absent or ambiguous.");
        var reference = references[0];
        var validator = onboarding.CreateAuthorizedValidator(reference);
        var validation = await validator.ValidateAsync(new(
            Path.GetDirectoryName(reference.ExecutablePath), reference.InstanceDirectory, reference.GameId), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(validation.DownloadsDirectory))
            return Failure(actions, "The connected MO2 instance has no usable downloads directory.");
        var downloadsRoot = ValidateDownloadsRoot(validation.DownloadsDirectory);

        var results = ImmutableArray.CreateBuilder<NexusSourceAcquisitionResult>();
        var ordered = actions.DistinctBy(action => action.ExpectedArchiveLeaf, StringComparer.OrdinalIgnoreCase).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = ordered[index];
            progress?.Report(new(index, ordered.Length, action.ExpectedArchiveLeaf));
            results.Add(await AcquireOneAsync(action, downloadsRoot, apiKey, cancellationToken).ConfigureAwait(false));
        }
        progress?.Report(new(ordered.Length, ordered.Length, string.Empty));
        return new(results.ToImmutable());
    }

    private async Task<NexusSourceAcquisitionResult> AcquireOneAsync(
        AssistantRecoveryAcquisitionAction action,
        string downloadsRoot,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (!TryParseOfficialFilesUri(action.OfficialFilesUri, out var gameDomain, out var modId) ||
            !IsSafeLeaf(action.ExpectedArchiveLeaf))
            return Result(action, NexusSourceAcquisitionDisposition.SourceUnresolved, "The source route or expected archive name is not exact.");

        var destination = Path.GetFullPath(Path.Combine(downloadsRoot, action.ExpectedArchiveLeaf));
        if (!IsImmediateChild(destination, downloadsRoot))
            return Result(action, NexusSourceAcquisitionDisposition.Failed, "The destination escaped the configured MO2 downloads directory.");
        if (File.Exists(destination))
            return Result(action, NexusSourceAcquisitionDisposition.AlreadyPresent, "The exact archive leaf is already present in MO2 downloads.");

        try
        {
            using var filesRequest = CreateApiRequest($"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files.json", apiKey);
            using var filesResponse = await http.SendAsync(filesRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!filesResponse.IsSuccessStatusCode)
                return Result(action, ClassifyProviderFailure(filesResponse.StatusCode), $"Nexus file lookup returned HTTP {(int)filesResponse.StatusCode}.");
            await using var filesStream = await filesResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var filesDocument = await JsonDocument.ParseAsync(filesStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var matches = ReadExactFileMatches(filesDocument.RootElement, action.ExpectedArchiveLeaf).ToArray();
            if (matches.Length != 1)
                return Result(action, NexusSourceAcquisitionDisposition.SourceUnresolved,
                    matches.Length == 0 ? "Nexus does not list the exact expected archive." : "Nexus lists the expected archive more than once.");
            var file = matches[0];

            using var linkRequest = CreateApiRequest(
                $"https://api.nexusmods.com/v1/games/{gameDomain}/mods/{modId}/files/{file.FileId}/download_link.json", apiKey);
            using var linkResponse = await http.SendAsync(linkRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!linkResponse.IsSuccessStatusCode)
                return Result(action, ClassifyProviderFailure(linkResponse.StatusCode),
                    linkResponse.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                        ? "This Nexus account requires a browser-generated Download with Manager link."
                        : $"Nexus download-link lookup returned HTTP {(int)linkResponse.StatusCode}.");
            await using var linkStream = await linkResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var linkDocument = await JsonDocument.ParseAsync(linkStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!TryReadTrustedDownloadUri(linkDocument.RootElement, out var downloadUri))
                return Result(action, NexusSourceAcquisitionDisposition.SourceUnresolved, "Nexus returned no allowlisted HTTPS download mirror.");

            using var downloadResponse = await http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!downloadResponse.IsSuccessStatusCode)
                return Result(action, NexusSourceAcquisitionDisposition.Failed, $"The Nexus mirror returned HTTP {(int)downloadResponse.StatusCode}.");
            var declaredLength = downloadResponse.Content.Headers.ContentLength;
            if (declaredLength is <= 0 or > MaximumArchiveBytes)
                return Result(action, NexusSourceAcquisitionDisposition.Failed, "The archive size is absent or outside Grid's safe acquisition limit.");
            var exactLength = declaredLength.GetValueOrDefault();
            var temporary = Path.Combine(downloadsRoot, $".grid-{Guid.NewGuid():N}.part");
            try
            {
                await using (var input = await downloadResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await CopyBoundedAsync(input, output, exactLength, cancellationToken).ConfigureAwait(false);
                }
                if (new FileInfo(temporary).Length != exactLength)
                    throw new InvalidDataException("The completed archive length differs from the Nexus response.");
                ValidateArchiveSignature(temporary, action.ExpectedArchiveLeaf);
                File.Move(temporary, destination, overwrite: false);
                try
                {
                    WriteMo2Sidecar(destination + ".meta", gameDomain, modId, file);
                }
                catch
                {
                    File.Delete(destination);
                    throw;
                }
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            return Result(action, NexusSourceAcquisitionDisposition.Downloaded, "Downloaded the exact Nexus file into MO2 downloads; it was not installed or enabled.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            return Result(action, NexusSourceAcquisitionDisposition.Failed, SafeDetail(exception));
        }
    }

    private static HttpRequestMessage CreateApiRequest(string uri, string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("apikey", apiKey);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Grid", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static IEnumerable<NexusFile> ReadExactFileMatches(JsonElement root, string expectedLeaf)
    {
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in files.EnumerateArray())
        {
            if (!item.TryGetProperty("file_id", out var fileIdValue) || !fileIdValue.TryGetInt64(out var fileId) || fileId <= 0 ||
                !item.TryGetProperty("file_name", out var fileNameValue)) continue;
            var fileName = fileNameValue.GetString();
            if (!string.Equals(fileName, expectedLeaf, StringComparison.OrdinalIgnoreCase)) continue;
            var version = item.TryGetProperty("version", out var versionValue) ? versionValue.GetString() ?? string.Empty : string.Empty;
            yield return new(fileId, fileName!, version);
        }
    }

    private static bool TryReadTrustedDownloadUri(JsonElement root, out Uri uri)
    {
        uri = new Uri("https://www.nexusmods.com/");
        if (root.ValueKind != JsonValueKind.Array) return false;
        foreach (var item in root.EnumerateArray())
        {
            if (!item.TryGetProperty("URI", out var value) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var candidate)) continue;
            if (candidate.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(candidate.UserInfo)) continue;
            if (!candidate.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) &&
                !candidate.Host.EndsWith(".nexusmods.com", StringComparison.OrdinalIgnoreCase)) continue;
            uri = candidate;
            return true;
        }
        return false;
    }

    public static bool TryParseOfficialFilesUri(string value, out string gameDomain, out long modId)
    {
        gameDomain = string.Empty;
        modId = 0;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase)) return false;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || !segments[1].Equals("mods", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(segments[2], out modId) || modId <= 0) return false;
        gameDomain = segments[0].ToLowerInvariant();
        return gameDomain.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
    }

    private static string ValidateDownloadsRoot(string value)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || path.StartsWith("\\\\", StringComparison.Ordinal) || !Directory.Exists(path))
            throw new InvalidDataException("The configured MO2 downloads path is not a local existing directory.");
        var cursor = new DirectoryInfo(path);
        while (cursor is not null)
        {
            if ((cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The configured MO2 downloads path contains a reparse point.");
            cursor = cursor.Parent;
        }
        return path;
    }

    private static bool IsSafeLeaf(string value) =>
        !string.IsNullOrWhiteSpace(value) && Path.GetFileName(value).Equals(value, StringComparison.Ordinal) &&
        value.IndexOfAny(['\0', '\r', '\n', '\t']) < 0;

    private static bool IsImmediateChild(string path, string parent) =>
        string.Equals(Path.GetDirectoryName(path), parent, StringComparison.OrdinalIgnoreCase);

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expectedLength, CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total = checked(total + read);
            if (total > expectedLength || total > MaximumArchiveBytes)
                throw new InvalidDataException("The download exceeded its declared or configured size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateArchiveSignature(string path, string leaf)
    {
        Span<byte> header = stackalloc byte[6];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 4) throw new InvalidDataException("The downloaded archive is truncated.");
        var extension = Path.GetExtension(leaf);
        var valid = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            ? header[0] == (byte)'P' && header[1] == (byte)'K'
            : extension.Equals(".7z", StringComparison.OrdinalIgnoreCase)
                ? header.SequenceEqual(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C })
                : extension.Equals(".rar", StringComparison.OrdinalIgnoreCase)
                    ? header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21
                    : false;
        if (!valid) throw new InvalidDataException("The downloaded bytes do not match the expected archive format.");
    }

    private static void WriteMo2Sidecar(string path, string gameDomain, long modId, NexusFile file)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        var gameName = gameDomain.Equals("skyrimspecialedition", StringComparison.OrdinalIgnoreCase) ? "SkyrimSE" : gameDomain;
        var content = $"[General]{Environment.NewLine}gameName={gameName}{Environment.NewLine}modID={modId}{Environment.NewLine}" +
                      $"fileID={file.FileId}{Environment.NewLine}version={file.Version}{Environment.NewLine}repository=Nexus{Environment.NewLine}";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static NexusSourceAcquisitionDisposition ClassifyProviderFailure(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
            ? NexusSourceAcquisitionDisposition.BrowserRequired
            : NexusSourceAcquisitionDisposition.Failed;

    private static NexusSourceAcquisitionResult Result(AssistantRecoveryAcquisitionAction action, NexusSourceAcquisitionDisposition disposition, string detail) =>
        new(action.ExpectedArchiveLeaf, action.OfficialFilesUri, disposition, detail);

    private static NexusSourceAcquisitionSummary Failure(ImmutableArray<AssistantRecoveryAcquisitionAction> actions, string detail) =>
        new(actions.Select(action => Result(action, NexusSourceAcquisitionDisposition.Failed, detail)).ToImmutableArray());

    private static string SafeDetail(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Access to the configured MO2 downloads directory was denied.",
        JsonException => "Nexus returned an invalid response.",
        HttpRequestException => "The Nexus request failed.",
        InvalidDataException => exception.Message,
        _ => "The source could not be acquired.",
    };

    private sealed record NexusFile(long FileId, string FileName, string Version);
}
