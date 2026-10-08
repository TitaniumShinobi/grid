using System.Text.Json;
using Grid.Core.Models;

namespace Grid.App.Services;

/// <summary>
/// Machine-local, account-independent selection of an already installed shared
/// canonical catalog package. This is runtime configuration only; it cannot
/// import packages or modify canonical knowledge.
/// </summary>
public sealed record CanonicalCatalogRuntimeBinding(
    string CatalogStoreFileName,
    bool AllowCandidatePackages,
    CatalogPackageId PackageId);

public sealed class CanonicalCatalogRuntimeBindingStore(string path)
{
    public const int SchemaVersion = 1;
    public const string DefaultCatalogStoreFileName = "shared-canonical-library.v5.json";
    private const long MaximumDocumentBytes = 64 * 1024;
    private readonly string path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("A binding path is required.", nameof(path)) : path);

    public string BindingFilePath => path;

    public CanonicalCatalogRuntimeBinding? Load()
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumDocumentBytes)
            throw new InvalidDataException("The canonical catalog runtime binding has an invalid size.");

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            RequireExactProperties(root, "schemaVersion", "catalogStoreFileName", "allowCandidatePackages", "packageId");
            if (RequiredInt32(root, "schemaVersion") != SchemaVersion)
                throw new InvalidDataException("The canonical catalog runtime binding has an unsupported schema version.");

            var catalogStoreFileName = RequiredString(root, "catalogStoreFileName");
            if (!catalogStoreFileName.Equals(DefaultCatalogStoreFileName, StringComparison.Ordinal) ||
                !Path.GetFileName(catalogStoreFileName).Equals(catalogStoreFileName, StringComparison.Ordinal))
                throw new InvalidDataException("The canonical catalog runtime binding contains an unsupported catalog store coordinate.");
            var allowCandidatePackages = RequiredBoolean(root, "allowCandidatePackages");
            var packageId = new CatalogPackageId(RequiredString(root, "packageId"));
            return new(catalogStoreFileName, allowCandidatePackages, packageId);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The canonical catalog runtime binding is not valid strict JSON.", exception);
        }
    }

    private static void RequireExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The canonical catalog runtime binding must be a JSON object.");
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("The canonical catalog runtime binding contains missing, duplicate, or unsupported fields.");
    }

    private static string RequiredString(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
            throw new InvalidDataException($"The canonical catalog runtime binding is missing string '{propertyName}'.");
        return property.GetString()!;
    }

    private static int RequiredInt32(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var result))
            throw new InvalidDataException($"The canonical catalog runtime binding is missing integer '{propertyName}'.");
        return result;
    }

    private static bool RequiredBoolean(JsonElement value, string propertyName)
    {
        if (!value.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"The canonical catalog runtime binding is missing boolean '{propertyName}'.");
        return property.GetBoolean();
    }
}
