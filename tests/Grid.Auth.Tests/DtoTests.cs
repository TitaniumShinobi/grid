using System;
using System.Text.Json;
using System.Threading.Tasks;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

public static class DtoTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static void Register()
    {
        TestRunner.Add("dtos", "TokenResponse snake_case contract", async () =>
        {
            var token = JsonSerializer.Deserialize<TokenResponse>(TestData.TokenJson(), Web);
            Assert.NotNull(token, "token");
            Assert.Equal("at-abc", token!.AccessToken, "access_token");
            Assert.Equal("rt-xyz", token.RefreshToken, "refresh_token");
            Assert.Equal("Bearer", token.TokenType, "token_type");
            Assert.Equal(3600, token.ExpiresIn, "expires_in");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "SessionUser auth_provider + stable account id", async () =>
        {
            var json = """{"id":"user-1","sub":"sub-github-123","uid":"account-uid-1","email":"devon@grid.test","name":"Devon","picture":"data:image/png;base64,aGVsbG8=","auth_provider":"github"}""";
            var user = JsonSerializer.Deserialize<SessionUser>(json, Web);
            Assert.NotNull(user, "user");
            Assert.Equal("github", user!.AuthProvider, "auth_provider snake_case mapping");
            Assert.Equal("account-uid-1", user.StableAccountId, "StableAccountId prefers Uid");
            Assert.Equal("Devon", user.Name, "name");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "SessionUser falls back to sub for stable id", async () =>
        {
            var json = """{"id":"u","sub":"sub-only","uid":"","email":"a@b.test","name":"A","auth_provider":"microsoft"}""";
            var user = JsonSerializer.Deserialize<SessionUser>(json, Web);
            Assert.NotNull(user, "user");
            Assert.Equal("sub-only", user!.StableAccountId, "StableAccountId falls back to Sub");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "AuthConfigResponse discovery contract", async () =>
        {
            var config = JsonSerializer.Deserialize<AuthConfigResponse>(TestData.ConfigJson(), Web);
            Assert.NotNull(config, "config");
            Assert.Equal("grid", config!.App.Id, "app.id");
            Assert.Equal(3, config.Providers.Count, "provider count");
            Assert.Equal("google", config.Providers[2].Provider, "third provider google");
            Assert.True(!config.Providers[2].Available, "unavailable reflects config");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "consent gate shape", async () =>
        {
            var gate = JsonSerializer.Deserialize<ConsentGateDto>(TestData.ConsentGateJson(), Web);
            Assert.NotNull(gate, "gate");
            Assert.Equal("grid", gate!.AppId, "appId");
            Assert.True(gate.RequiresProductSignup, "requiresProductSignup");
            Assert.Equal("POST", gate.Consent.Method, "consent.method");
            Assert.True(gate.Consent.Url.Contains("app=grid"), "consent url");
            Assert.True(gate.Consent.Docs.Count == 1 && gate.Consent.Docs[0].Key == "grid_terms_v1", "docs surfaced");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "AuthApiException code extraction", async () =>
        {
            using var doc = JsonDocument.Parse("""{"ok":false,"code":"ENROLLMENT_REQUIRED","error":"asdf"}""");
            Assert.Equal("ENROLLMENT_REQUIRED", AuthApiException.CodeFromBody(doc, "fallback", 403), "code wins over error");
            using var doc2 = JsonDocument.Parse("""{"ok":false,"error":"invalid_grant"}""");
            Assert.Equal("invalid_grant", AuthApiException.CodeFromBody(doc2, "fallback", 400), "error fallback");
            await Task.CompletedTask;
        });

        TestRunner.Add("dtos", "account_context_readiness preserved on OAuthUserInfoDto", async () =>
        {
            var json = """{"sub":"s","uid":"u","email":"a@b.test","name":"A","account_context_readiness":{"readiness":"ready"}}""";
            var dto = JsonSerializer.Deserialize<OAuthUserInfoDto>(json, Web);
            Assert.NotNull(dto, "dto");
            var readiness = dto!.AccountContextReadiness;
            Assert.True(readiness.HasValue, "field present");
            if (!readiness.HasValue)
                throw new InvalidOperationException("account_context_readiness is required by this test.");
            Assert.Equal("ready", readiness.Value.GetProperty("readiness").GetString(), "readiness value");
            await Task.CompletedTask;
        });
    }
}
