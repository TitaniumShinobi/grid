using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Auth.Api;

public sealed record CodeExchangeParams(string Code, string ClientId, string RedirectUri, string CodeVerifier);
public sealed record RefreshParams(string RefreshToken, string ClientId);

/// <summary>
/// The AUTH HTTP boundary for Grid. Every call is a documented endpoint
/// (docs/reference/api-endpoints.md); errors become <see cref="AuthApiException"/>
/// carrying the AUTH error code so Grid UI can render copy. No client secret is
/// ever sent this layer or any other.
/// </summary>
public interface IGridAuthApi
{
    Task<AuthConfigResponse> GetConfigAsync(CancellationToken ct = default);
    Task<ProviderStatusResponse> GetProviderStatusAsync(string provider, CancellationToken ct = default);
    Task<TokenResponse> ExchangeCodeAsync(CodeExchangeParams parameters, CancellationToken ct = default);
    Task<TokenResponse> RefreshAsync(RefreshParams parameters, CancellationToken ct = default);
    Task RevokeTokenAsync(string token, CancellationToken ct = default);
    Task<OAuthUserInfoDto> UserInfoAsync(string accessToken, CancellationToken ct = default);
    Task<Grid.Auth.Features.SessionUser> MeAsync(string? accessToken, string? cookieHeader = null, CancellationToken ct = default);
    Task<MagicRequestAccepted> MagicRequestAsync(string email, string intent, string origin, CancellationToken ct = default);
    Task<MagicConsumeResult> MagicConsumeAsync(string token, string origin, CancellationToken ct = default);
    Task<Grid.Auth.Features.SessionUser> ConsentAsync(System.Collections.Generic.IReadOnlyDictionary<string, bool> consent, string appId, string? cookieHeader = null, string? accessToken = null, CancellationToken ct = default);
    Task SignOutAsync(string? accessToken = null, string? cookieHeader = null, CancellationToken ct = default);
}

public sealed class GridAuthHttpClient : IGridAuthApi, IDisposable
{
    private const string JsonContentType = "application/json";

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;

    public GridAuthHttpClient(Uri baseUrl, HttpMessageHandler handler)
        : this(baseUrl, new HttpClient(handler, disposeHandler: true))
    {
    }

    public GridAuthHttpClient(Uri baseUrl, HttpClient? client = null)
    {
        if (baseUrl is null) throw new ArgumentNullException(nameof(baseUrl));
        _json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _http = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = TrimmedBase(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress ??= TrimmedBase(baseUrl);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Grid-Auth/1.0");
    }

    public async Task<AuthConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        using var res = await _http.GetAsync("/api/auth/config", ct);
        await EnsureRequestOkAsync(res, ct);
        return await ReadJsonAsync<AuthConfigResponse>(res, ct);
    }

    public async Task<ProviderStatusResponse> GetProviderStatusAsync(string provider, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"/api/auth/providers/{Uri.EscapeDataString(provider)}/status", ct);
        await EnsureRequestOkAsync(res, ct);
        return await ReadJsonAsync<ProviderStatusResponse>(res, ct);
    }

    public async Task<TokenResponse> ExchangeCodeAsync(CodeExchangeParams p, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = p.Code,
            ["client_id"] = p.ClientId,
            ["redirect_uri"] = p.RedirectUri,
            ["code_verifier"] = p.CodeVerifier,
        };
        return await PostFormAsync<TokenResponse>("/oauth/token", form, ct);
    }

    public async Task<TokenResponse> RefreshAsync(RefreshParams p, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = p.RefreshToken,
            ["client_id"] = p.ClientId,
        };
        return await PostFormAsync<TokenResponse>("/oauth/token", form, ct);
    }

    public async Task RevokeTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return;
        using var res = await _http.PostAsync("/oauth/revoke",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        // revoke is best-effort by contract; drop the result.
    }

    public async Task<OAuthUserInfoDto> UserInfoAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/oauth/userinfo");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await _http.SendAsync(req, ct);
        await EnsureRequestOkAsync(res, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OAuthUserInfoDto>(body, _json) ?? new OAuthUserInfoDto();
    }

    public async Task<Grid.Auth.Features.SessionUser> MeAsync(string? accessToken, string? cookieHeader = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        AttachAuth(req, accessToken, cookieHeader);
        using var res = await _http.SendAsync(req, ct);
        await EnsureRequestOkAsync(res, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var user = doc.RootElement.TryGetProperty("user", out var u)
            ? u.Deserialize<Grid.Auth.Features.SessionUser>(_json)
            : null;
        return user ?? throw new AuthApiException(res.StatusCode == System.Net.HttpStatusCode.OK ? 502 : (int)res.StatusCode, "INVALID_SESSION_RESPONSE");
    }

    public async Task<MagicRequestAccepted> MagicRequestAsync(string email, string intent, string origin, CancellationToken ct = default)
    {
        var payload = new { email, intent, origin };
        using var res = await PostJsonAsync("/api/auth/magic/request", payload, ct);
        await EnsureRequestOkAsync(res, ct);
        return await ReadJsonAsync<MagicRequestAccepted>(res, ct);
    }

    public async Task<MagicConsumeResult> MagicConsumeAsync(string token, string origin, CancellationToken ct = default)
    {
        var payload = new { token, origin };
        using var res = await PostJsonAsync("/api/auth/magic/consume", payload, ct);
        var setCookies = ExtractSetCookies(res);
        var status = (int)res.StatusCode;
        var text = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);

        if (res.IsSuccessStatusCode)
        {
            var success = doc.RootElement.Deserialize<MagicConsumeSuccess>(_json);
            return new MagicConsumeResult
            {
                IsSuccess = true,
                Success = success,
                SetCookies = setCookies,
            };
        }

        // Consent gate: 403 { ok:false, state:'ENROLLMENT_REQUIRED', reason:'missing_consent', ... }
        if (status == 403 &&
            doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("state", out var st) &&
            st.ValueKind == JsonValueKind.String &&
            st.GetString() == "ENROLLMENT_REQUIRED")
        {
            var gate = doc.RootElement.Deserialize<ConsentGateDto>(_json) ?? new ConsentGateDto();
            return new MagicConsumeResult { IsSuccess = false, Gate = gate, SetCookies = setCookies };
        }

        var code = AuthApiException.CodeFromBody(doc, "MAGIC_CONSUME_FAILED", status);
        var description = DocTryGet(doc.RootElement, "error");
        throw new AuthApiException(status, code, description);
    }

    public async Task<Grid.Auth.Features.SessionUser> ConsentAsync(
        IReadOnlyDictionary<string, bool> consent,
        string appId,
        string? cookieHeader = null,
        string? accessToken = null,
        CancellationToken ct = default)
    {
        var url = $"/api/auth/consent?app={Uri.EscapeDataString(appId)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { consent }, _json), Encoding.UTF8, JsonContentType),
        };
        AttachAuth(req, accessToken, cookieHeader);
        using var res = await _http.SendAsync(req, ct);
        await EnsureRequestOkAsync(res, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var user = doc.RootElement.TryGetProperty("user", out var u)
            ? u.Deserialize<Grid.Auth.Features.SessionUser>(_json)
            : null;
        return user ?? throw new AuthApiException(502, "INVALID_CONSENT_RESPONSE");
    }

    public async Task SignOutAsync(string? accessToken = null, string? cookieHeader = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        AttachAuth(req, accessToken, cookieHeader);
        using var res = await _http.SendAsync(req, ct);
        // best-effort clear by contract
    }

    // ---- request plumbing --------------------------------------------------

    private async Task<T> PostFormAsync<T>(string path, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var res = await _http.PostAsync(path, new FormUrlEncodedContent(form), ct);
        await EnsureRequestOkAsync(res, ct);
        return await ReadJsonAsync<T>(res, ct);
    }

    private Task<HttpResponseMessage> PostJsonAsync(string path, object payload, CancellationToken ct)
        => _http.PostAsync(path, new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, JsonContentType), ct);

    private static void AttachAuth(HttpRequestMessage req, string? accessToken, string? cookieHeader)
    {
        if (!string.IsNullOrEmpty(accessToken))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }
        if (!string.IsNullOrEmpty(cookieHeader))
        {
            req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }
    }

    private static async Task EnsureRequestOkAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        string code;
        string? description = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            code = AuthApiException.CodeFromBody(doc, null!, (int)res.StatusCode);
            description = DocTryGet(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            code = $"HTTP_{res.StatusCode:D}";
        }
        throw new AuthApiException((int)res.StatusCode, code, description);
    }

    private static string? DocTryGet(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }
        return null;
    }

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body, _json) ?? throw new AuthApiException(502, "INVALID_RESPONSE");
    }

    private static IReadOnlyList<string> ExtractSetCookies(HttpResponseMessage res)
    {
        if (res.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return values.ToList();
        }
        return Array.Empty<string>();
    }

    private static Uri TrimmedBase(Uri baseUrl)
    {
        var s = baseUrl.OriginalString.TrimEnd('/');
        return new Uri(s, UriKind.Absolute);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Set-Cookie parsing for the hosted magic session bootstrap.</summary>
public static class SetCookieParser
{
    public static bool TryGetValue(IReadOnlyList<string> setCookieHeaders, string name, out string value)
    {
        value = "";
        foreach (var header in setCookieHeaders)
        {
            var pair = header.Split(';')[0].Trim();
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (string.Equals(pair[..eq], name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair[(eq + 1)..];
                return !string.IsNullOrEmpty(value);
            }
        }
        return false;
    }
}
