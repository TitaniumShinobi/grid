using System;
using System.Linq;
using System.Threading.Tasks;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

public static class StoreAndSessionTests
{
    public static void Register()
    {
        TestRunner.Add("token-store", "in-memory store round-trip", async () =>
        {
            var store = new InMemoryTokenStore();
            var bag = TokenBag.FromTokens("at", "rt", "Bearer", 3600, "grid-windows", "openid", "sub-1", "uid-1", DateTimeOffset.UtcNow);
            await store.SaveAsync("grid-native-bearer", bag);
            var loaded = await store.LoadAsync("grid-native-bearer");
            Assert.NotNull(loaded, "loaded");
            Assert.Equal("at", loaded!.AccessToken, "access token");
            await store.DeleteAsync("grid-native-bearer");
            Assert.Null(await store.LoadAsync("grid-native-bearer"), "deleted");
            await Task.CompletedTask;
        });

        TestRunner.Add("token-bag", "NeedsRefresh honors skew", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var bag = TokenBag.FromTokens("at", "rt", "Bearer", 100, "grid-windows", "openid", issuedAt: now);
            // 95s later: 5s remain, skew 30s => needs refresh.
            Assert.True(bag.NeedsRefresh(now.AddSeconds(95), 30), "within skew refreshes");
            Assert.True(!bag.NeedsRefresh(now.AddSeconds(50), 30), "plenty of time -> no refresh");
            Assert.True(bag.IsExpired(now.AddSeconds(200)), "expired after lifetime");
            await Task.CompletedTask;
        });

        TestRunner.Add("set-cookie", "parser extracts cookie value case-insensitively", async () =>
        {
            var cookies = TestData.HostedSessionCookies("sid-1", "rid-1");
            Assert.True(SetCookieParser.TryGetValue(cookies, "auth_sid", out var sid), "sid present");
            Assert.Equal("sid-1", sid, "sid value");
            Assert.True(SetCookieParser.TryGetValue(cookies, "AUTH_RID", out var rid), "rid present (case-insensitive)");
            Assert.Equal("rid-1", rid, "rid value");
            await Task.CompletedTask;
        });

        TestRunner.Add("set-cookie", "hosted cookie header builder", async () =>
        {
            var header = HostedCookieHeader.Build(TestData.HostedSessionCookies("s1", "r1"), "auth_sid", "auth_rid");
            Assert.Equal("auth_sid=s1; auth_rid=r1", header, "joined header");
            Assert.Null(HostedCookieHeader.Build(Array.Empty<string>(), "auth_sid", "auth_rid"), "empty -> null");
            await Task.CompletedTask;
        });

        TestRunner.Add("session", "RestoreAsync refreshes an expiring bag", async () =>
        {
            var store = new InMemoryTokenStore();
            var old = TokenBag.FromTokens("old-at", "old-rt", "Bearer", 300, "grid-windows", "openid", issuedAt: DateTimeOffset.UtcNow.AddSeconds(-280));
            await store.SaveAsync(SessionManager.StoreKey, old);

            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("new-at", "new-rt"), "auth_rid=session-rid; Path=/; HttpOnly"));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(new Uri("https://signin.grid.test"), handler);
            var manager = new SessionManager(TestData.Options(), api, store);

            var user = await manager.RestoreAsync();
            Assert.NotNull(user, "restored user");
            Assert.Equal("Devon", user!.Name, "user name");
            var refreshed = await store.LoadAsync(SessionManager.StoreKey);
            Assert.Equal("new-at", refreshed!.AccessToken, "refreshed bag persisted");
            await Task.CompletedTask;
        });

        TestRunner.Add("session", "RestoreAsync clears store on 401", async () =>
        {
            var store = new InMemoryTokenStore();
            var bag = TokenBag.FromTokens("at", "rt", "Bearer", 3600, "grid-windows", "openid", issuedAt: DateTimeOffset.UtcNow);
            await store.SaveAsync(SessionManager.StoreKey, bag);

            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 401, """{"ok":false,"error":"unauthorized"}"""));
            using var api = new GridAuthHttpClient(new Uri("https://signin.grid.test"), handler);
            var manager = new SessionManager(TestData.Options(), api, store);

            var user = await manager.RestoreAsync();
            Assert.Null(user, "no session after 401");
            Assert.Null(await store.LoadAsync(SessionManager.StoreKey), "store cleared on 401");
            await Task.CompletedTask;
        });

        TestRunner.Add("session", "BootstrapFromHostedSessionAsync exchanges refresh cookie", async () =>
        {
            var store = new InMemoryTokenStore();
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("boot-at", "boot-rt")));
            using var api = new GridAuthHttpClient(new Uri("https://signin.grid.test"), handler);
            var manager = new SessionManager(TestData.Options(), api, store);

            var bag = await manager.BootstrapFromHostedSessionAsync(TestData.HostedSessionCookies("s", "magic-rid"));
            Assert.Equal("boot-at", bag.AccessToken, "bootstrapped bag");
            var persisted = await store.LoadAsync(SessionManager.StoreKey);
            Assert.Equal("boot-at", persisted!.AccessToken, "persisted");
            // The refresh grant must have used the magic auth_rid cookie value.
            Assert.True(handler.LastRequestBody!.Contains("magic-rid"), "refresh cookie used as refresh token");
            await Task.CompletedTask;
        });

        TestRunner.Add("session", "BootstrapFromHostedSessionAsync requires refresh cookie", async () =>
        {
            var store = new InMemoryTokenStore();
            using var api = new GridAuthHttpClient(new Uri("https://signin.grid.test"), new FakeHandler());
            var manager = new SessionManager(TestData.Options(), api, store);
            var caught = false;
            try
            {
                _ = await manager.BootstrapFromHostedSessionAsync(new[] { "auth_sid=only; Path=/; HttpOnly" });
            }
            catch (AuthApiException e)
            {
                Assert.Equal(502, e.Status, "bootstrap missing refresh cookie status");
                caught = true;
            }
            Assert.True(caught, "bootstrap without refresh cookie throws");
            await Task.CompletedTask;
        });

        TestRunner.Add("session", "SignOutAsync revokes then clears", async () =>
        {
            var store = new InMemoryTokenStore();
            var bag = TokenBag.FromTokens("at", "rt", "Bearer", 3600, "grid-windows", "openid", issuedAt: DateTimeOffset.UtcNow);
            await store.SaveAsync(SessionManager.StoreKey, bag);

            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/revoke", 200, """{"ok":true}"""));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/logout", 200, """{"ok":true}"""));
            using var api = new GridAuthHttpClient(new Uri("https://signin.grid.test"), handler);
            var manager = new SessionManager(TestData.Options(), api, store);

            await manager.SignOutAsync();
            Assert.Null(await store.LoadAsync(SessionManager.StoreKey), "store cleared after sign out");
            Assert.True(handler.Requests.Any(r => r.Request.RequestUri!.AbsolutePath == "/oauth/revoke"), "refresh token revoked");
            await Task.CompletedTask;
        });
    }
}