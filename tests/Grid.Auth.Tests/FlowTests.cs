using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth.Tests;

public static class FlowTests
{
    private static readonly HttpClient Poll = new() { Timeout = TimeSpan.FromSeconds(1) };

    public static void Register()
    {
        TestRunner.Add("flow", "session restore publishes resolving before its terminal state", async () =>
        {
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, new FakeHandler());
            var service = new GridAuthService(
                TestData.Options(), api, new InMemoryTokenStore(), new RecordingBrowser());
            var phases = new System.Collections.Generic.List<AuthPhase>();
            service.StateChanged += (_, snapshot) => phases.Add(snapshot.Phase);

            await service.RestoreAsync();

            Assert.True(phases.Count == 2, "restore publishes exactly resolving and terminal states");
            Assert.True(phases[0] == AuthPhase.SessionRestore, "restore starts in SessionRestore");
            Assert.True(phases[1] == AuthPhase.SignedOut, "missing persisted session resolves to SignedOut");
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "restore terminal state is retained");
        });

        TestRunner.Add("flow", "persisted session remains resolving through refresh and identity verification", async () =>
        {
            var store = new InMemoryTokenStore();
            await store.SaveAsync(
                SessionManager.StoreKey,
                TokenBag.FromTokens(
                    "old-at", "old-rt", "Bearer", 300, "grid-windows", "openid",
                    issuedAt: DateTimeOffset.UtcNow.AddSeconds(-280)));
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule(
                "POST", "/oauth/token", 200, TestData.TokenJson("new-at", "new-rt")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var service = new GridAuthService(TestData.Options(), api, store, new RecordingBrowser());
            var phases = new System.Collections.Generic.List<AuthPhase>();
            service.StateChanged += (_, snapshot) => phases.Add(snapshot.Phase);

            await service.RestoreAsync();

            Assert.True(phases.Count == 2, "persisted restore publishes only resolving and authenticated states");
            Assert.True(phases[0] == AuthPhase.SessionRestore, "refresh and /api/me begin behind SessionRestore");
            Assert.True(phases[1] == AuthPhase.SignedIn, "verified persisted session resolves to SignedIn");
            Assert.Equal("account-uid-1", service.Current.User!.StableAccountId, "verified account identity retained");
            Assert.True(handler.Requests.Any(value => value.Request.RequestUri!.AbsolutePath == "/oauth/token"), "refresh completed");
            Assert.True(handler.Requests.Any(value => value.Request.RequestUri!.AbsolutePath == "/api/me"), "identity verification completed");
        });

        TestRunner.Add("flow", "sign out and account switch replace the canonical profile image", async () =>
        {
            const string accountAPicture = "https://identity.grid.test/avatar-a.png";
            const string accountBPicture = "https://identity.grid.test/avatar-b.png";
            var store = new InMemoryTokenStore();
            await store.SaveAsync(
                SessionManager.StoreKey,
                TokenBag.FromTokens(
                    "account-a-at", "account-a-rt", "Bearer", 3600,
                    "grid-windows", "openid", issuedAt: DateTimeOffset.UtcNow));
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule(
                "GET", "/api/me", 200,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    user = new { id = "user-a", sub = "sub-a", uid = "account-a", email = "a@grid.test", name = "Account A", picture = accountAPicture, auth_provider = "provider-a" },
                })));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/revoke", 200, """{"ok":true}"""));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/logout", 200, """{"ok":true}"""));
            var options = TestData.Options(TestData.FreeLoopbackPort());
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var service = new GridAuthService(options, api, store, new RecordingBrowser());
            var snapshots = new System.Collections.Generic.List<AuthSnapshot>();
            service.StateChanged += (_, snapshot) => snapshots.Add(snapshot);

            await service.RestoreAsync();
            Assert.Equal(accountAPicture, service.Current.User!.Picture, "restored account image comes from /api/me");

            await service.SignOutAsync();
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "account image owner is removed on sign out");
            Assert.Null(service.Current.User, "signed-out snapshot retains no previous user");

            handler.Rules.Add(new FakeHandler.Rule(
                "POST", "/oauth/token", 200, TestData.TokenJson("account-b-at", "account-b-rt")));
            handler.Rules.Add(new FakeHandler.Rule(
                "GET", "/api/me", 200,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    ok = true,
                    user = new { id = "user-b", sub = "sub-b", uid = "account-b", email = "b@grid.test", name = "Account B", picture = accountBPicture, auth_provider = "provider-b" },
                })));

            var ticket = service.BeginProviderSignInAsync("provider-b");
            var completion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                await UntilHttpSucceedsAsync($"{options.LoopbackOrigin}{options.LoopbackPath}?code=account-b-code&state={ticket.State}"),
                "account B callback delivered");
            await completion;

            Assert.Equal("account-b", service.Current.User!.StableAccountId, "account switch replaces identity");
            Assert.Equal(accountBPicture, service.Current.User.Picture, "account switch replaces profile image");
            var signedInPictures = snapshots
                .Where(snapshot => snapshot.IsSignedIn)
                .Select(snapshot => snapshot.User!.Picture)
                .ToArray();
            Assert.True(
                signedInPictures.SequenceEqual(new[] { accountAPicture, accountBPicture }),
                "authenticated snapshots expose only each account's canonical image in order");
        });

        TestRunner.Add("flow", "discovery publishes providers and order", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/auth/config", 200, TestData.ConfigJson()));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var service = new GridAuthService(TestData.Options(), api, new InMemoryTokenStore(), new RecordingBrowser());

            var fired = await WaitStateAsync(service, () => service.LoadProvidersAsync(), s => s.Providers is { Count: 3 });
            Assert.True(fired, "StateChanged fired with providers");
            Assert.Equal("microsoft", service.Current.Providers![0].Provider, "provider order from AUTH");
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "still signed out");
            await Task.CompletedTask;
        });

        TestRunner.Add("flow", "discovery failure falls back with error code", async () =>
        {
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/auth/config", 503, "{}"));
            using var api = new GridAuthHttpClient(TestData.Options().BaseUri, handler);
            var options = TestData.Options();
            var service = new GridAuthService(options, api, new InMemoryTokenStore(), new RecordingBrowser());
            var providers = await service.LoadProvidersAsync();
            Assert.True(providers.Count >= 3, "fallback provider list present");
            Assert.Equal("DISCOVERY_FAILED", service.Current.ErrorCode, "error surfaced");
            await Task.CompletedTask;
        });

        TestRunner.Add("flow", "native provider flow end to end over a real loopback listener", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("at-native", "rt-native")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var browser = new RecordingBrowser();
            var service = new GridAuthService(
                options, api, new InMemoryTokenStore(), browser,
                () => new LoopbackListener(options.LoopbackHost, options.LoopbackPort, options.LoopbackPath));

            var ticket = service.BeginProviderSignInAsync("github");
            Assert.NotNull(ticket, "ticket");
            Assert.True(browser.LastUrl is null, "browser not opened yet");

            service.OpenNativeFlowBrowser(ticket);
            Assert.NotNull(browser.LastUrl, "browser opened for native provider flow");

            var completion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));

            var callback = $"{options.LoopbackOrigin}{options.LoopbackPath}?code=native-code&state={ticket.State}";
            var delivered = await UntilHttpSucceedsAsync(callback);
            Assert.True(delivered, "loopback callback delivered");

            var outcome = await completion;
            Assert.True(outcome.Kind == MagicOutcome.SignedIn, "native flow signs in");
            Assert.Equal("Devon", outcome.User!.Name, "session user");
            Assert.True(service.Current.IsSignedIn, "service signed in");
            Assert.Equal("account-uid-1", service.Current.User!.StableAccountId, "stable account id surfaced");

            // Browser received the AUTH authorize URL with PKCE.
            Assert.True(browser.LastUrl!.Contains("/oauth/authorize"), "authorize url opened");
            Assert.True(browser.LastUrl.Contains("code_challenge_method=S256"), "S256 PKCE");
            Assert.True(browser.LastUrl.Contains("client_id=grid-windows"), "public client id");
            var tokenRequest = handler.Requests.Single(value =>
                value.Request.Method == HttpMethod.Post &&
                value.Request.RequestUri!.AbsolutePath == "/oauth/token");
            Assert.True(tokenRequest.Body is not null &&
                        tokenRequest.Body.Contains("code=native-code"),
                "code exchanged");
        });

        TestRunner.Add("flow", "magic token capture posts back to loopback", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            using var api = new GridAuthHttpClient(options.BaseUri, new FakeHandler());
            var service = new GridAuthService(
                options, api, new InMemoryTokenStore(), new RecordingBrowser(),
                () => new LoopbackListener(options.LoopbackHost, options.LoopbackPort, options.LoopbackPath));

            var waiter = service.WaitForMagicTokenAsync(TimeSpan.FromSeconds(15));
            var delivered = await PostMagicAsync($"{options.LoopbackOrigin}/magic-capture", "magicAbc_123");
            Assert.True(delivered, "magic capture posted");
            var token = await waiter;
            Assert.Equal("magicAbc_123", token, "token reached the app");
        });

        TestRunner.Add("flow", "magic consume bootstraps native bearer from refresh cookie", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/request", 202, """{"ok":true,"state":"EMAIL_REQUEST_ACCEPTED"}"""));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/consume", 200, """{"ok":true,"user":{}}""", "auth_sid=ms; Path=/; HttpOnly\nauth_rid=magic-rid; Path=/; HttpOnly"));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("at-magic", "rt-magic")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var store = new InMemoryTokenStore();
            var service = new GridAuthService(options, api, store, new RecordingBrowser());

            var accepted = await service.RequestMagicLinkAsync("devon@grid.test", "login");
            Assert.True(accepted.Ok, "magic requested");

            var outcome = await service.ConsumeMagicAsync("magic-token");
            Assert.True(outcome.Kind == MagicOutcome.SignedIn, "magic consume signs in");
            Assert.True(service.Current.IsSignedIn, "service signed in");
            var bag = await store.LoadAsync(SessionManager.StoreKey);
            Assert.NotNull(bag, "durable bag persisted");
            Assert.Equal("at-magic", bag!.AccessToken, "bootstrapped access token");
            Assert.True(handler.Requests.Any(r => r.Request.RequestUri!.AbsolutePath == "/oauth/token" &&
                (r.Body ?? "").Contains("refresh_token=magic-rid")), "refresh grant used hosted refresh cookie");
        });

        TestRunner.Add("flow", "consent gate consumes then finalizes through consent", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/magic/consume", 403, TestData.ConsentGateJson(), "auth_sid=cs; Path=/; HttpOnly\nauth_rid=c-rid; Path=/; HttpOnly"));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/consent", 200, TestData.MeJson()));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("at-c", "rt-c")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var store = new InMemoryTokenStore();
            var service = new GridAuthService(options, api, store, new RecordingBrowser());

            var outcome = await service.ConsumeMagicAsync("magic-token");
            Assert.True(outcome.Kind == MagicOutcome.ConsentRequired, "consent required surfaced");
            Assert.True(service.Current.Phase == AuthPhase.ConsentRequired, "phase ConsentRequired");
            Assert.Equal("Complete Grid signup before continuing.", outcome.Gate!.Message, "gate message");

            var user = await service.AcceptConsentAndFinalizeAsync(new System.Collections.Generic.Dictionary<string, bool> { ["grid_terms_v1"] = true });
            Assert.Equal("Devon", user.Name, "user finalized");
            Assert.True(service.Current.IsSignedIn, "signed in after consent");
            Assert.True(handler.Requests.Any(r => r.Request.RequestUri!.AbsolutePath == "/api/auth/consent"), "consent accepted");
            var bag = await store.LoadAsync(SessionManager.StoreKey);
            Assert.Equal("at-c", bag!.AccessToken, "durable bag after consent");
        });

        TestRunner.Add("flow", "recoverable authorization failure allows a subsequent sign-in attempt", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 503, """{"error":"server_error"}"""));
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var browser = new RecordingBrowser();
            var service = new GridAuthService(
                options, api, new InMemoryTokenStore(), browser,
                () => new LoopbackListener(options.LoopbackHost, options.LoopbackPort, options.LoopbackPath));

            var ticket = service.BeginProviderSignInAsync("github");
            var failedCompletion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                await UntilHttpSucceedsAsync($"{options.LoopbackOrigin}{options.LoopbackPath}?code=bad-code&state={ticket.State}"),
                "first callback delivered");
            var failedOutcome = await failedCompletion;
            Assert.True(failedOutcome.Kind == MagicOutcome.Cancelled, "failed exchange cancels the attempt");
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "recoverable failure returns to SignedOut");
            Assert.Equal("AUTHORIZATION_FAILED", service.Current.ErrorCode, "authorization failure code surfaced");
            Assert.True(
                service.Current.ErrorMessage?.Contains("retry", StringComparison.OrdinalIgnoreCase) == true,
                "recoverable failure invites retry");

            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("at-retry", "rt-retry")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            var retryTicket = service.BeginProviderSignInAsync("github");
            var retryCompletion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                await UntilHttpSucceedsAsync($"{options.LoopbackOrigin}{options.LoopbackPath}?code=retry-code&state={retryTicket.State}"),
                "retry callback delivered");
            var retryOutcome = await retryCompletion;
            Assert.True(retryOutcome.Kind == MagicOutcome.SignedIn, "retry attempt signs in");
            Assert.True(service.Current.IsSignedIn, "service signed in after retry");
        });

        TestRunner.Add("flow", "cancel pending native flow and oauth decline resolve signed out", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            using var api = new GridAuthHttpClient(options.BaseUri, new FakeHandler());
            var service = new GridAuthService(
                options, api, new InMemoryTokenStore(), new RecordingBrowser(),
                () => new LoopbackListener(options.LoopbackHost, options.LoopbackPort, options.LoopbackPath));

            var ticket = service.BeginProviderSignInAsync("github");
            Assert.True(service.Current.Phase == AuthPhase.SigningIn, "native flow begins in SigningIn");
            var awaiting = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            service.CancelPendingFlow();
            var cancelledOutcome = await awaiting;
            Assert.True(cancelledOutcome.Kind == MagicOutcome.Cancelled, "cancelled completion reports Cancelled");
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "cancel returns to SignedOut");

            var declineTicket = service.BeginProviderSignInAsync("github");
            var declineCompletion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            var declineUrl =
                $"{options.LoopbackOrigin}{options.LoopbackPath}?error=access_denied&error_description=user%20declined&state={declineTicket.State}";
            Assert.True(await UntilHttpSucceedsAsync(declineUrl), "provider decline callback delivered");
            var declineOutcome = await declineCompletion;
            Assert.True(declineOutcome.Kind == MagicOutcome.Cancelled, "provider decline reports Cancelled");
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "decline resolves to SignedOut");
            Assert.Equal("AUTHORIZATION_FAILED", service.Current.ErrorCode, "decline surfaces authorization failure");
            Assert.True(
                service.Current.ErrorMessage?.Contains("retry", StringComparison.OrdinalIgnoreCase) == true,
                "decline outcome invites retry in the app");
        });

        TestRunner.Add("flow", "sign out clears service and store", async () =>
        {
            var port = TestData.FreeLoopbackPort();
            var options = TestData.Options(port);
            var handler = new FakeHandler();
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/token", 200, TestData.TokenJson("at", "rt")));
            handler.Rules.Add(new FakeHandler.Rule("GET", "/api/me", 200, TestData.MeJson()));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/oauth/revoke", 200, """{"ok":true}"""));
            handler.Rules.Add(new FakeHandler.Rule("POST", "/api/auth/logout", 200, """{"ok":true}"""));
            using var api = new GridAuthHttpClient(options.BaseUri, handler);
            var store = new InMemoryTokenStore();
            var service = new GridAuthService(options, api, store, new RecordingBrowser());

            await service.RestoreAsync();
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "no session yet");

            var ticket = service.BeginProviderSignInAsync("github");
            var completion = service.CompleteNativeSignInAsync(TimeSpan.FromSeconds(15));
            Assert.True(await UntilHttpSucceedsAsync($"{options.LoopbackOrigin}{options.LoopbackPath}?code=c&state={ticket.State}"), "callback delivered");
            await completion;

            await service.SignOutAsync();
            Assert.True(service.Current.Phase == AuthPhase.SignedOut, "signed out after SignOutAsync");
            Assert.Null(await store.LoadAsync(SessionManager.StoreKey), "store cleared");
        });

        TestRunner.Add("flow", "GridAuthHost composes the production stack", async () =>
        {
            var options = TestData.Options();
            var service = Grid.Auth.GridAuthHost.Compose(options);
            Assert.NotNull(service, "composed");
            using (service)
            {
            }
            await Task.CompletedTask;
        });
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<bool> UntilHttpSucceedsAsync(string url)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await Poll.GetAsync(url);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                // Listener not up yet â€” the flow starts it asynchronously.
                await Task.Delay(50);
            }
        }
        return false;
    }

    private static async Task<bool> PostMagicAsync(string url, string token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var content = new StringContent("""{"magicToken":"__TOKEN__"}""".Replace("__TOKEN__", token),
                    System.Text.Encoding.UTF8, "application/json");
                using var response = await Poll.PostAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                await Task.Delay(50);
            }
        }
        return false;
    }

    private static async Task<bool> WaitStateAsync(
        GridAuthService service,
        Func<Task> action,
        Func<AuthSnapshot, bool> predicate)
    {
        TaskCompletionSource<bool>? tcs = null;
        void Handler(object? sender, AuthSnapshot snapshot)
        {
            if (predicate(snapshot)) tcs?.TrySetResult(true);
        }
        service.StateChanged += Handler;
        try
        {
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await action();
            return await Task.WhenAny(tcs.Task, Task.Delay(2000)) == tcs.Task;
        }
        finally
        {
            service.StateChanged -= Handler;
        }
    }
}
