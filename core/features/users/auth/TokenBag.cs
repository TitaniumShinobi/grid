using System;

namespace Grid.Auth.Features;

/// <summary>
/// An OAuth bearer token bag from POST /oauth/token. <see cref="IssuedAt"/> is
/// captured locally at exchange time; AUTH returns expires_in seconds.
/// </summary>
public sealed record TokenBag
{
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresInSeconds { get; init; }
    public DateTimeOffset IssuedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ClientId { get; init; } = "";
    public string? Scope { get; init; }
    public string? Sub { get; init; }
    public string? Uid { get; init; }

    public DateTimeOffset ExpiresAt => this.IssuedAt.AddSeconds(this.ExpiresInSeconds);

    public bool IsExpired(DateTimeOffset now)
        => now >= this.ExpiresAt;

    public bool NeedsRefresh(DateTimeOffset now, int skewSeconds)
        => now >= this.ExpiresAt.AddSeconds(-skewSeconds);

    public static TokenBag FromTokens(
        string accessToken,
        string? refreshToken,
        string tokenType,
        int expiresInSeconds,
        string clientId,
        string? scope,
        string? sub = null,
        string? uid = null,
        DateTimeOffset? issuedAt = null)
        => new()
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            TokenType = string.IsNullOrEmpty(tokenType) ? "Bearer" : tokenType,
            ExpiresInSeconds = expiresInSeconds,
            ClientId = clientId,
            Scope = scope,
            Sub = sub,
            Uid = uid,
            IssuedAt = issuedAt ?? DateTimeOffset.UtcNow,
        };
}