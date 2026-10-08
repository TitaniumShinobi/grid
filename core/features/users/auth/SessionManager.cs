using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grid.Auth.Api;

namespace Grid.Auth.Features;

/// <summary>
/// Persists and maintains the desktop bearer session: restore from the secure
/// token store, refresh pre-expiry, fetch identity (GET /api/me), and sign out.
/// Also bootstraps a durable native bearer session from the hosted magic-email
/// session cookies (auth_sid / auth_rid): AUTH returns the magic tokens as
/// HttpOnly cookies, which native code may still read from the Set-Cookie
/// headers; the refresh cookie is exchanged for a full bearer bag at
/// /oauth/token (public-client refresh grant).
/// </summary>
public sealed class SessionManager
{
    public const string StoreKey = "grid-native-bearer";

    private readonly AuthOptions _options;
    private readonly IGridAuthApi _api;
    private readonly ITokenStore _store;

    public SessionManager(AuthOptions options, IGridAuthApi api, ITokenStore store)
    {
        _options = options;
        _api = api;
        _store = store;
    }

    public async Task<TokenBag?> LoadAsync(CancellationToken ct = default)
        => await _store.LoadAsync(StoreKey, ct);

    public async Task<TokenBag> SaveAsync(TokenBag bag, CancellationToken ct = default)
    {
        await _store.SaveAsync(StoreKey, bag, ct);
        return bag;
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await _store.DeleteAsync(StoreKey, ct);

    /// <summary>Restore best-effort: unexpired bag → identity; expired bag →
    /// refresh → identity; otherwise null (and store cleared).</summary>
    public async Task<SessionUser?> RestoreAsync(CancellationToken ct = default)
    {
        var bag = await LoadAsync(ct);
        if (bag is null) return null;
        try
        {
            if (bag.NeedsRefresh(DateTimeOffset.UtcNow, _options.RefreshSkewSeconds))
            {
                bag = await RefreshBagAsync(bag, ct);
                await SaveAsync(bag, ct);
            }
            return await _api.MeAsync(bag.AccessToken, null, ct);
        }
        catch (AuthApiException e) when (e.Status == 401)
        {
            await DeleteAsync(ct);
            return null;
        }
        catch (AuthApiException)
        {
            // Network/5xx: keep the stored bag, surface the error to the caller.
            throw;
        }
    }

    /// <summary>Fetch the user with the stored bearer, refreshing first if the
    /// bag is still valid but nearing expiry. Throws AuthApiException 401 when
    /// there is no usable session.</summary>
    public async Task<SessionUser> EnsureFreshAsync(CancellationToken ct = default)
    {
        var bag = await LoadAsync(ct) ?? throw SessionMissing();
        if (bag.NeedsRefresh(DateTimeOffset.UtcNow, _options.RefreshSkewSeconds))
        {
            bag = await RefreshBagAsync(bag, ct);
            await SaveAsync(bag, ct);
        }
        return await _api.MeAsync(bag.AccessToken, null, ct);
    }

    /// <summary>Exchange a hosted magic refresh cookie for a native bearer bag
    /// and persist it. Returns the bag.</summary>
    public async Task<TokenBag> BootstrapFromHostedSessionAsync(
        IReadOnlyList<string> setCookieHeaders,
        CancellationToken ct = default)
    {
        if (!SetCookieParser.TryGetValue(setCookieHeaders, _options.RefreshCookieName, out var refreshToken))
        {
            throw new AuthApiException(502, "NO_HOSTED_REFRESH_COOKIE");
        }
        var response = await _api.RefreshAsync(new RefreshParams(refreshToken, _options.ClientId), ct);
        var bag = TokenBag.FromTokens(
            response.AccessToken,
            response.RefreshToken,
            response.TokenType,
            response.ExpiresIn,
            _options.ClientId,
            response.Scope);
        return await SaveAsync(bag, ct);
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var bag = await LoadAsync(ct);
        if (bag is not null)
        {
            try
            {
                if (!string.IsNullOrEmpty(bag.RefreshToken))
                {
                    await _api.RevokeTokenAsync(bag.RefreshToken, ct);
                }
                await _api.SignOutAsync(bag.AccessToken, null, ct);
            }
            catch
            {
                // Best-effort revocation; local deletion is authoritative for Grid.
            }
        }
        await DeleteAsync(ct);
    }

    private async Task<TokenBag> RefreshBagAsync(TokenBag bag, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(bag.RefreshToken))
        {
            throw new AuthApiException(401, "NO_REFRESH_TOKEN");
        }
        var response = await _api.RefreshAsync(new RefreshParams(bag.RefreshToken, bag.ClientId), ct);
        return TokenBag.FromTokens(
            response.AccessToken,
            response.RefreshToken,
            response.TokenType,
            response.ExpiresIn,
            bag.ClientId,
            response.Scope ?? bag.Scope);
    }

    private static AuthApiException SessionMissing() => new(401, "NO_SESSION");
}