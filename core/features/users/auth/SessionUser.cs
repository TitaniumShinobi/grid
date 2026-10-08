using System.Text.Json.Serialization;

namespace Grid.Auth.Features;

/// <summary>Verified identity proof bound into tokens (AUTH VerifiedProviderIdentity).</summary>
public sealed record ProviderIdentity
{
    public string Provider { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public string? VerifiedEmail { get; init; }
}

/// <summary>
/// The authenticated identity payload Grid consumes (GET /api/me user object and
/// the /oauth/userinfo subset). Field names mirror the AUTH contract exactly,
/// including the snake_case auth_provider field.
/// </summary>
public sealed record SessionUser
{
    public string Id { get; init; } = "";
    public string Sub { get; init; } = "";
    public string Uid { get; init; } = "";
    public string? LifeUserId { get; init; }
    public string? VvaultUserId { get; init; }
    public string? SupabaseUserId { get; init; }
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Picture { get; init; }

    [JsonPropertyName("auth_provider")]
    public string AuthProvider { get; init; } = "";

    public ProviderIdentity? ProviderIdentity { get; init; }

    /// <summary>STABLE account identity: prefer Uid, then Sub. Never key Grid
    /// state on Id / LifeUserId / VvaultUserId.</summary>
    public string StableAccountId => !string.IsNullOrEmpty(this.Uid) ? this.Uid : this.Sub;
}