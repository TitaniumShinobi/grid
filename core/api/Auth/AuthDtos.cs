using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Grid.Auth.Api;

/// <summary>
/// Data-transfer objects for the AUTH HTTP contract Grid consumes. Field names
/// follow the AUTH wire contract exactly (snake_case preserved with
/// JsonPropertyName where AUTH uses it). Reference:
/// docs/reference/api-endpoints.md in this payload.
/// </summary>
public static class AuthContracts
{
    public const string AuthorizationUrlRelative = "/oauth/authorize";
}

public sealed record AuthAppDto
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? BrandTagline { get; init; }
}

public sealed record AuthCredentialsDto
{
    public bool Enabled { get; init; }
}

public sealed record AuthLegalDocDto
{
    public string Product { get; init; } = "";
    public string DocType { get; init; } = "";
    public string Key { get; init; } = "";
    public string Version { get; init; } = "";
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
    public bool Required { get; init; }
}

public sealed record AuthTurnstileDto
{
    public bool Required { get; init; }
    public bool Enabled { get; init; }
    public string? SiteKey { get; init; }
}

public sealed record ProviderConfigDto
{
    public string Provider { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

public sealed record AuthConfigResponse
{
    public bool Ok { get; init; }
    public AuthAppDto App { get; init; } = new();
    public AuthCredentialsDto? Credentials { get; init; }
    public IReadOnlyList<AuthLegalDocDto> Docs { get; init; } = Array.Empty<AuthLegalDocDto>();
    public AuthTurnstileDto? Turnstile { get; init; }
    public IReadOnlyList<ProviderConfigDto> Providers { get; init; } = Array.Empty<ProviderConfigDto>();
}

public sealed record ProviderCredentialEnvReport
{
    public CredentialPresenceDto? ClientId { get; init; }
    public CredentialPresenceDto? ClientSecret { get; init; }
    public CredentialPresenceDto? Callback { get; init; }
}

public sealed record CredentialPresenceDto
{
    public bool Present { get; init; }
    public string? Key { get; init; }
    public string? Source { get; init; }
    public string? EnvKey { get; init; }
}

public sealed record ProviderStatusResponse
{
    public bool Ok { get; init; }
    public string Provider { get; init; } = "";
    public string? Label { get; init; }
    public bool Enabled { get; init; }
    public bool Available { get; init; }
    public string? Reason { get; init; }
    public ProviderCredentialEnvReport? CredentialEnv { get; init; }
}

public sealed record TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; init; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    public string? Scope { get; init; }
}

public sealed record OAuthUserInfoDto
{
    public string Sub { get; init; } = "";
    public string Uid { get; init; } = "";
    public string Email { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Picture { get; init; }

    [JsonPropertyName("account_context_readiness")]
    public System.Text.Json.JsonElement? AccountContextReadiness { get; init; }
}

public sealed record MagicRequestAccepted
{
    public bool Ok { get; init; }
    public string State { get; init; } = "";
}

public sealed record MagicConsumeSuccess
{
    public bool Ok { get; init; }
    public string Intent { get; init; } = "";
    public Grid.Auth.Features.SessionUser? User { get; init; }
}

public sealed record ConsentEndpointDto
{
    public string Method { get; init; } = "POST";
    public string Url { get; init; } = "";
    public IReadOnlyList<AuthLegalDocDto> Docs { get; init; } = Array.Empty<AuthLegalDocDto>();
}

/// <summary>The AUTH enrollment/consent gate returned on magic consume when the
/// app requires legal docs before the session becomes active.</summary>
public sealed record ConsentGateDto
{
    public bool RequiresProductSignup { get; init; }
    public string AppId { get; init; } = "";
    public string Message { get; init; } = "";
    public ConsentEndpointDto Consent { get; init; } = new();
    public string Continuation { get; init; } = "";
}

public sealed record MagicConsumeResult
{
    public bool IsSuccess { get; init; }
    public MagicConsumeSuccess? Success { get; init; }
    public ConsentGateDto? Gate { get; init; }

    /// <summary>Hosted session cookies (auth_sid/auth_rid) captured from the
    /// consume response; used to bootstrap the native bearer session.</summary>
    public IReadOnlyList<string> SetCookies { get; init; } = Array.Empty<string>();

    public Grid.Auth.Features.SessionUser? User => this.Success?.User;
    public bool ConsentRequired => this.Gate is not null;
}