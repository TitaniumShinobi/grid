using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

public static class HttpClientContractTests
{
    public static void Register()
    {
        TestRunner.Add("http", "GET /api/auth/config parses discovery", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/auth/config", 200, TestData.ConfigJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var config = await api.GetConfigAsync();
            Assert.Equal(3, config.Providers.Count, "providers");
            Assert.Equal("microsoft", config.Providers[0].Provider, "first provider");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "authorization_code + PKCE form is submitted", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var parameters = new CodeExchangeParams("code-1", "grid-windows", "http://127.0.0.1:51706/grid-natives-callback", "pkce-verifier");
            var tokens = await api.ExchangeCodeAsync(parameters);
            Assert.Equal("at-abc", tokens.AccessToken, "access token");
            var body = handler.LastRequestBody!;
            Assert.True(body.Contains("grant_type=authorization_code"), "grant type");
            Assert.True(body.Contains("code=code-1"), "code");
            Assert.True(body.Contains("code_verifier=pkce-verifier"), "verifier");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "refresh grant request shape", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var tokens = await api.RefreshAsync(new RefreshParams("rt-1", "grid-windows"));
            Assert.Equal("at-abc", tokens.AccessToken, "access token");
            Assert.True(handler.LastRequestBody!.Contains("grant_type=refresh_token"), "refresh grant");
            Assert.True(handler.LastRequestBody.Contains("refresh_token=rt-1"), "refresh token");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "GET /api/me attaches bearer and unwraps user", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var user = await api.MeAsync("at-abc");
            Assert.Equal("github", user.AuthProvider, "auth_provider");
            Assert.True(handler.LastRequest!.Headers.Authorization?.Scheme == "Bearer", "bearer scheme");
            Assert.Equal("at-abc", handler.LastRequest.Headers.Authorization!.Parameter, "bearer token");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "magic request posts email/intent/origin", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/request", 202, """{"ok":true,"state":"EMAIL_REQUEST_ACCEPTED"}"""));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var accepted = await api.MagicRequestAsync("devon@grid.test", "login", "http://127.0.0.1:51706");
            Assert.True(accepted.Ok, "accepted");
            Assert.Equal("EMAIL_REQUEST_ACCEPTED", accepted.State, "state");
            var body = handler.LastRequestBody!;
            Assert.True(body.Contains("\"intent\":\"login\""), "intent");
            Assert.True(body.Contains("\"origin\":\"http://127.0.0.1:51706\""), "origin");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "magic consume success captures cookies + user", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/consume", 200, """{"ok":true,"user":{"id":"u","sub":"s","uid":"acc","email":"d@g.test","name":"D","auth_provider":"email"}}""", "auth_sid=s1; Path=/; HttpOnly\nauth_rid=r1; Path=/; HttpOnly"));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var result = await api.MagicConsumeAsync("magic-token", "http://127.0.0.1:51706");
            Assert.True(result.IsSuccess, "success");
            Assert.NotNull(result.User, "user parsed");
            Assert.Equal("acc", result.User!.StableAccountId, "stable id");
            Assert.True(result.SetCookies.Count >= 2, "set-cookie captured");
            Assert.True(handler.LastRequestBody!.Contains("\"token\":\"magic-token\""), "token body");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "magic consume 403 consent gate parsed with cookies", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/consume", 403, TestData.ConsentGateJson(), "auth_sid=s1; Path=/; HttpOnly\nauth_rid=r1; Path=/; HttpOnly"));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var result = await api.MagicConsumeAsync("magic-token", "http://127.0.0.1:51706");
            Assert.True(!result.IsSuccess, "not success");
            Assert.True(result.ConsentRequired, "consent required");
            Assert.Equal("grid", result.Gate!.AppId, "gate appId");
            Assert.True(result.SetCookies.Count >= 2, "gate response carries hosted cookies");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "magic consume error maps to AuthApiException code", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/consume", 400, """{"ok":false,"code":"MAGIC_LINK_INVALID"}"""));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var caught = false;
            try
            {
                _ = await api.MagicConsumeAsync("bad", "http://127.0.0.1:51706");
            }
            catch (AuthApiException e)
            {
                Assert.Equal(400, e.Status, "status");
                Assert.Equal("MAGIC_LINK_INVALID", e.Code, "code");
                caught = true;
            }
            Assert.True(caught, "error throws AuthApiException");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "consent posts cookie + document consents", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/consent", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var user = await api.ConsentAsync(new System.Collections.Generic.Dictionary<string, bool> { ["grid_terms_v1"] = true }, "grid", "auth_sid=s1; auth_rid=r1");
            Assert.Equal("Devon", user.Name, "user from consent");
            Assert.Equal("/api/auth/consent", handler.LastRequest!.RequestUri!.AbsolutePath, "consent path");
            Assert.True(handler.LastRequest.RequestUri.Query.Contains("app=grid"), "app query");
            Assert.True(handler.LastRequest.Headers.TryGetValues("Cookie", out _), "cookie attached");
            Assert.True(handler.LastRequestBody!.Contains("\"grid_terms_v1\":true"), "consent body");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "provider status envelope", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/auth/providers/github/status", 200, """{"ok":true,"provider":"github","enabled":true,"available":true}"""));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var status = await api.GetProviderStatusAsync("github");
            Assert.Equal("github", status.Provider, "provider");
            Assert.True(status.Available, "available");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "non-JSON error still yields AuthApiException", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 503, "bad gateway (html)"));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var caught = false;
            try
            {
                _ = await api.MeAsync("at");
            }
            catch (AuthApiException e)
            {
                Assert.Equal(503, e.Status, "status");
                Assert.True(e.Code.StartsWith("HTTP_"), "HTTP_ fallback code");
                caught = true;
            }
            Assert.True(caught, "error throws AuthApiException");
            await Task.CompletedTask;
        });

        TestRunner.Add("http", "api errors omit bearer token from exception surface", async () =>
        {
            const string token = "secret-access-token-must-not-appear-in-errors";
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 401, """{"ok":false,"code":"UNAUTHORIZED"}"""));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            try
            {
                _ = await api.MeAsync(token);
                Assert.True(false, "expected AuthApiException");
            }
            catch (AuthApiException e)
            {
                Assert.True(!e.Message.Contains(token, StringComparison.Ordinal), "message omits bearer");
                Assert.True(!e.ToString().Contains(token, StringComparison.Ordinal), "stringify omits bearer");
            }
            await Task.CompletedTask;
        });
    }
}