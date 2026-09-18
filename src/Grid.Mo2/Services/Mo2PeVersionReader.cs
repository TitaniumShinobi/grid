using System.Diagnostics;
using System.Security.Cryptography;
using Grid.Mo2.Models;

namespace Grid.Mo2.Services;

public sealed class Mo2PeVersionReader : IMo2PeVersionReader
{
    private static readonly Version Minimum = new(2, 4, 1);
    private const long MaximumExecutableBytes = 512L * 1024 * 1024;

    public Mo2VersionEvidence Read(string exactExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(exactExecutablePath) ||
            !Path.GetFileName(exactExecutablePath).Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase))
            return new(Mo2VersionEvidenceStatus.Missing, null, string.Empty, "The validated MO2 executable path is unavailable.");

        try
        {
            var file = new FileInfo(exactExecutablePath);
            if (file.Length <= 0 || file.Length > MaximumExecutableBytes)
                return new(Mo2VersionEvidenceStatus.Unverifiable, null, string.Empty, "The MO2 executable size is outside the bounded identity-read policy.");
            var beforeLength = file.Length;
            var beforeWrite = file.LastWriteTimeUtc.Ticks;
            using var stream = new FileStream(exactExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            var identity = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var info = FileVersionInfo.GetVersionInfo(exactExecutablePath);
            file.Refresh();
            if (!file.Exists || file.Length != beforeLength || file.LastWriteTimeUtc.Ticks != beforeWrite)
                return new(Mo2VersionEvidenceStatus.Unverifiable, null, identity, "The MO2 executable changed while version evidence was read.");
            if (string.IsNullOrWhiteSpace(info.ProductVersion) ||
                !Version.TryParse(NumericPrefix(info.ProductVersion), out var version))
                return new(Mo2VersionEvidenceStatus.Malformed, null, identity, "MO2 version evidence is missing or malformed.");

            var product = info.ProductName ?? string.Empty;
            var original = info.OriginalFilename ?? string.Empty;
            if (!product.Contains("Mod Organizer", StringComparison.OrdinalIgnoreCase) ||
                !original.Equals("ModOrganizer.exe", StringComparison.OrdinalIgnoreCase))
                return new(Mo2VersionEvidenceStatus.Unverifiable, version, identity, "The executable does not provide corroborated official MO2 product identity.");

            return version < Minimum
                ? new(Mo2VersionEvidenceStatus.TooOld, version, identity, "MO2 2.4.1 or newer is required for the supported run -e route.")
                : new(Mo2VersionEvidenceStatus.Supported, version, identity, $"MO2 {version} supports run -e.");
        }
        catch (FileNotFoundException)
        {
            return new(Mo2VersionEvidenceStatus.Missing, null, string.Empty, "The validated MO2 executable is missing.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(Mo2VersionEvidenceStatus.Unverifiable, null, string.Empty, "MO2 version evidence could not be read.");
        }
    }

    private static string NumericPrefix(string value)
    {
        var chars = value.Trim().TakeWhile(character => char.IsDigit(character) || character == '.').ToArray();
        return new string(chars).TrimEnd('.');
    }
}
