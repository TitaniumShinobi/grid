using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

/// <summary>Scriptable HttpMessageHandler driving the REAL GridAuthHttpClient
/// in contract tests â€” no API-level fakes, so the HTTP boundary is exercised
/// end to end.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    public sealed record Rule(string Method, string Path, int Status, string Json, string? SetCookies = null)
    {
        public bool Matches(HttpRequestMessage request)
            => Method == request.Method.ToString() &&
               (Path == "*" || Path == request.RequestUri!.AbsolutePath);
    }

    public List<Rule> Rules { get; } = new();
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();
    public HttpRequestMessage? LastRequest => Requests.Count == 0 ? null : Requests[^1].Request;
    public string? LastRequestBody => Requests.Count == 0 ? null : Requests[^1].Body;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));

        var rule = Rules.LastOrDefault(r => r.Matches(request)) ??
            throw new InvalidOperationException($"No fake rule for {request.Method} {request.RequestUri}");
        var response = new HttpResponseMessage((HttpStatusCode)rule.Status)
        {
            Content = new StringContent(rule.Json, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(rule.SetCookies))
        {
            foreach (var cookie in SplitSetCookies(rule.SetCookies))
            {
                response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
            }
        }
        return response;
    }

    private static IEnumerable<string> SplitSetCookies(string value)
        => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Browser launcher that records the URL instead of spawning a process.</summary>
public sealed class RecordingBrowser : IBrowserLauncher
{
    public string? LastUrl { get; private set; }

    public void Open(string url) => LastUrl = url;
}

public static class TestData
{
    public static AuthOptions Options(int loopbackPort = 51706) => new()
    {
        BaseUrl = "https://signin.grid.test",
        ClientId = "grid-windows",
        LoopbackHost = "127.0.0.1",
        LoopbackPort = loopbackPort,
        LoopbackPath = "/grid-natives-callback",
    };

    public static SessionUser User(string email = "devon@grid.test", string provider = "github")
        => new()
        {
            Id = "user-1",
            Sub = "sub-github-123",
            Uid = "account-uid-1",
            Email = email,
            Name = "Devon",
            Picture = "data:image/png;base64,aGVsbG8=",
            AuthProvider = provider,
        };

    public static string ConfigJson()
        => """{"ok":true,"app":{"id":"grid","name":"Grid"},"credentials":{"enabled":true},"docs":[],"turnstile":{"required":false,"enabled":false},"providers":[{"provider":"microsoft","label":"Continue with Microsoft","enabled":true,"available":true},{"provider":"github","label":"Continue with GitHub","enabled":true,"available":true},{"provider":"google","label":"Continue with Google","enabled":true,"available":false}]}""";

    public static string TokenJson(string accessToken = "at-abc", string refreshToken = "rt-xyz", int expiresIn = 3600)
        => $$"""{"access_token":"{{accessToken}}","refresh_token":"{{refreshToken}}","token_type":"Bearer","expires_in":{{expiresIn}},"scope":"openid profile email"}""";

    public static string MeJson()
        => """{"ok":true,"user":{"id":"user-1","sub":"sub-github-123","uid":"account-uid-1","email":"devon@grid.test","name":"Devon","picture":"data:image/png;base64,aGVsbG8=","auth_provider":"github"}}""";

    public static string ConsentGateJson(string appId = "grid")
        => $$"""{"ok":false,"state":"ENROLLMENT_REQUIRED","reason":"missing_consent","requiresProductSignup":true,"appId":"{{appId}}","message":"Complete Grid signup before continuing.","consent":{"method":"POST","url":"/api/auth/consent?app={{appId}}","docs":[{"product":"grid","docType":"terms","key":"grid_terms_v1","version":"2026.09.1","label":"Grid Terms","url":"https://grid.app/legal/terms","required":true}]},"continuation":"http://127.0.0.1:51706/return"}""";

    public static List<string> HostedSessionCookies(string sid = "sid-1", string rid = "rid-1")
        => new()
        {
            $"auth_sid={sid}; Path=/; HttpOnly; Secure; SameSite=Lax",
            $"auth_rid={rid}; Path=/; HttpOnly; Secure; SameSite=Lax",
        };

    /// <summary>Reserve a free TCP port on the loopback interface.</summary>
    public static int FreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
