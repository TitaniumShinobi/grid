using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Grid.Auth.Features;

/// <summary>
/// Desktop auth configuration for Grid. Loaded from the Grid-shipped config
/// (config/grid.auth.config.json â€” see the example in this payload). This is
/// the ONLY desktop-side configuration; provider credentials NEVER live here.
/// The desktop is a public native client: no client secret exists at all.
/// </summary>
public sealed class AuthOptions
{
    /// <summary>Grid-shipped settings file relative to the app base directory.</summary>
    public const string DefaultConfigFile = "config/grid.auth.config.json";

    /// <summary>AUTH authority origin (no trailing slash).</summary>
    public string BaseUrl { get; init; } = "";

    /// <summary>Public client id registered in AUTH <c>publicClients[]</c>.</summary>
    public string ClientId { get; init; } = "grid-windows";

    /// <summary>Loopback bind host. Keep on the loopback interface only.</summary>
    public string LoopbackHost { get; init; } = "127.0.0.1";

    /// <summary>
    /// Loopback port used for native callbacks and magic-email capture. This
    /// origin must be present in the AUTH deployment's <c>allowedOrigins</c> and
    /// matches the AUTH public client's loopback policy path below.
    /// </summary>
    public int LoopbackPort { get; init; } = 51706;

    /// <summary>Loopback path the native browser returns to (must match the
    /// AUTH public client loopback path).</summary>
    public string LoopbackPath { get; init; } = "/grid-natives-callback";

    /// <summary>OIDC scope set requested on /oauth/authorize and token grants.</summary>
    public IReadOnlyList<string> Scope { get; init; } = new[] { "openid", "profile", "email" };

    /// <summary>
    /// Presentation order for the Grid shell when AUTH discovery is not yet
    /// available (fallback only). Empty = rely entirely on AUTH.
    /// </summary>
    public IReadOnlyList<OAuthProviderOption>? PresentationFallback { get; init; }

    /// <summary>Hosted access cookie name (AUTH AUTH_COOKIE_NAME, default auth_sid).</summary>
    public string SessionCookieName { get; init; } = "auth_sid";

    /// <summary>Hosted refresh cookie name (AUTH AUTH_REFRESH_COOKIE_NAME, default auth_rid).</summary>
    public string RefreshCookieName { get; init; } = "auth_rid";

    /// <summary>Refresh a bearer when fewer than this many seconds remain.</summary>
    public int RefreshSkewSeconds { get; init; } = 30;

    public Uri BaseUri => new(this.BaseUrl);
    public string LoopbackOrigin => $"http://{this.LoopbackHost}:{this.LoopbackPort}";
    public string RedirectUri => $"{this.LoopbackOrigin}{this.LoopbackPath}";

    public static AuthOptions FromJson(string json, JsonSerializerOptions? options = null)
    {
        var result = JsonSerializer.Deserialize<AuthOptions>(json, options ?? SerializerOptions()) ??
            throw new InvalidOperationException("grid.auth.config.json is empty or invalid.");
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (!Uri.TryCreate(this.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("AuthOptions.BaseUrl must be an absolute http(s) origin.");
        }
        if (string.IsNullOrWhiteSpace(this.ClientId)) throw new InvalidOperationException("AuthOptions.ClientId is required.");
        if (this.LoopbackHost != "127.0.0.1" && this.LoopbackHost != "localhost" && this.LoopbackHost != "::1")
        {
            throw new InvalidOperationException("AuthOptions.LoopbackHost must be a loopback host (127.0.0.1, localhost, ::1).");
        }
        if (this.LoopbackPort is < 1 or > 65535)
            throw new InvalidOperationException("AuthOptions.LoopbackPort is outside the valid port range.");
        if (!this.LoopbackPath.StartsWith('/')) throw new InvalidOperationException("AuthOptions.LoopbackPath must start with '/'.");
        if (this.Scope is null || this.Scope.Count == 0)
            throw new InvalidOperationException("AuthOptions.Scope must contain at least openid.");
    }

    internal static JsonSerializerOptions SerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        return options;
    }
}
