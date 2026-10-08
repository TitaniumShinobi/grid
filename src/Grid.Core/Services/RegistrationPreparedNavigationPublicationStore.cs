using System.Text.Json;
using Grid.Core.Models;

namespace Grid.Core.Services;

/// <summary>Profile-scoped publication pointer for immutable registration-prepared navigation generations.</summary>
public sealed class RegistrationPreparedNavigationPublicationStore(string root)
{
    public const string PublicationFileName = "publication.v1.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public string Root => Path.GetFullPath(root);

    public string ProfileDirectory(ProfileId profileId) =>
        Path.Combine(Root, profileId.Value);

    public string PublicationPath(ProfileId profileId) =>
        Path.Combine(ProfileDirectory(profileId), PublicationFileName);

    public RegistrationPreparedNavigationPublication? Load(ProfileId profileId)
    {
        var path = PublicationPath(profileId);
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        var publication = JsonSerializer.Deserialize<RegistrationPreparedNavigationPublication>(bytes, Json)
            ?? throw new InvalidDataException("Registration prepared publication receipt is empty.");
        publication.Validate();
        return publication;
    }

    public void Publish(ProfileId profileId, RegistrationPreparedNavigationPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        publication.Validate();
        var directory = ProfileDirectory(profileId);
        Directory.CreateDirectory(directory);
        PreparedCanonicalNavigationStore.RejectReparsePoint(directory);
        var temporary = Path.Combine(directory, ".publication-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(publication, Json));
        var destination = PublicationPath(profileId);
        PreparedCanonicalNavigationStore.RejectReparsePoint(destination);
        File.Move(temporary, destination, overwrite: true);
    }
}

public sealed record RegistrationPreparedNavigationPublication(
    int SchemaVersion,
    string GenerationDigest,
    CatalogPackageId PackageId,
    GameId GameId,
    ProfileId ProfileId,
    string Locale,
    string CandidateDigest)
{
    public const int CurrentSchemaVersion = 1;

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException("Registration prepared publication schema is unsupported.");
        if (!CanonicalRegistrationEncoding.IsDigest(GenerationDigest))
            throw new InvalidDataException("Registration prepared generation digest is invalid.");
        if (!CanonicalRegistrationEncoding.IsDigest(CandidateDigest))
            throw new InvalidDataException("Registration prepared candidate digest is invalid.");
        if (string.IsNullOrWhiteSpace(Locale))
            throw new InvalidDataException("Registration prepared publication requires a locale.");
    }
}
