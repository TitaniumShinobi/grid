using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grid.Auth.Api;

namespace Grid.Auth.Features;

/// <summary>Result of a magic-link consume (same shape Grid UI consumes).</summary>
public enum MagicOutcome
{
    SignedIn,
    ConsentRequired,
    Cancelled,
}

public sealed record MagicConsumeOutcome
{
    public MagicOutcome Kind { get; init; }
    public SessionUser? User { get; init; }
    public ConsentGateDto? Gate { get; init; }
}

/// <summary>
/// The single auth surface the Grid shell binds to. Owns the phase state
/// machine (SignedOut → SigningIn → AwaitingBrowser → AwaitingLoopback →
/// TokenExchange → ConsentRequired → SignedIn), the native provider flows, the
/// magic-email lifecycle, session restore/refresh and sign-out. All async;
/// UI frames the user whenever <see cref="Current"/> changes.
///
/// AUTH owns every provider protocol and every secret. Grid owns the four
/// sign-in surfaces (Microsoft, GitHub, Google, magic email), the presentation
/// order and availability states from discovery, and the signed-in identity
/// binding (stable account id, display name, email, provider, avatar).
/// </summary>
public sealed class GridAuthService : IDisposable
{
    public static readonly TimeSpan NativeFlowTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MagicTokenTimeout = TimeSpan.FromMinutes(10);

    private readonly AuthOptions _options;
    private readonly IGridAuthApi _api;
    private readonly SessionManager _sessions;
    private readonly OAuthNativeFlow _flow;
    private readonly IBrowserLauncher _browser;
    private readonly Func<LoopbackListener> _listenerFactory;

    public Func<Task<bool>> ActivateExistingAsync { get; set; } = () => Task.FromResult(false);
    private long _nativeGeneration;
    private readonly SemaphoreSlim _nativeCommit = new(1, 1);
    private LoopbackListener? _completedListener;
    private NativeFlowTicket? _pendingNative;
    private LoopbackListener? _pendingListener;
    private MagicConsumeResult? _pendingMagic;

    public GridAuthService(
        AuthOptions options,
        IGridAuthApi api,
        ITokenStore store,
        IBrowserLauncher? browser = null,
        Func<LoopbackListener>? listenerFactory = null)
    {
        _options = options;
        _api = api;
        _sessions = new SessionManager(options, api, store);
        _flow = new OAuthNativeFlow(options, api);
        _browser = browser ?? new ProcessBrowserLauncher();
        _listenerFactory = listenerFactory ??
            (() => new LoopbackListener(options.LoopbackHost, options.LoopbackPort, options.LoopbackPath));
    }

    public event EventHandler<AuthSnapshot>? StateChanged;

    public AuthSnapshot Current { get; private set; } = AuthSnapshot.SignedOutSnapshot(null);

    public AuthOptions Options => _options;

    // ---- discovery ----------------------------------------------------------

    /// <summary>Fetch provider order + availability from AUTH and update the
    /// shell snapshot. Never throws: on failure it falls back to configured or
    /// hard-coded presentation order with unavailable states.</summary>
    public async Task<IReadOnlyList<OAuthProviderOption>> LoadProvidersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<OAuthProviderOption> providers;
        try
        {
            var config = await _api.GetConfigAsync(ct);
            providers = config.Providers
                .Select(p => new OAuthProviderOption
                {
                    Provider = p.Provider,
                    Label = p.Label,
                    Enabled = p.Enabled,
                    Available = p.Available,
                    Reason = p.Reason,
                })
                .ToList();
        }
        catch (Exception e) when (e is AuthApiException or HttpRequestException or TaskCanceledException)
        {
            providers = FallbackProviders();
            Publish(Current with { Providers = providers, ErrorCode = "DISCOVERY_FAILED" });
            return providers;
        }
        Publish(Current with { Providers = providers });
        return providers;
    }

    // ---- native provider flows (Microsoft / GitHub / Google) ----------------

    /// <summary>Creates the native flow, opens the browser at AUTH's hosted
    /// sign-in, and returns the ticket so the caller can also handle deep-link
    /// fallbacks. Finish with <see cref="CompleteNativeSignInAsync"/>.</summary>
    public NativeFlowTicket BeginProviderSignInAsync(string provider, string? prompt = null, bool acceptRequiredConsent = false)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("provider is required", nameof(provider));
        Interlocked.Increment(ref _nativeGeneration);
        _completedListener?.Dispose(); _completedListener = null;
        var ticket = _flow.CreateTicket(provider, prompt, acceptRequiredConsent);
        _pendingNative = ticket;
        _pendingListener?.Dispose();
        _pendingListener = _listenerFactory();
        _pendingListener.ConfigureDesktop("GRID", () => ActivateExistingAsync());
        _pendingListener.StartAsync().GetAwaiter().GetResult();
        Publish(Current with { Phase = AuthPhase.SigningIn, ErrorCode = null, ErrorMessage = null });
        return ticket;
    }

    public void OpenNativeFlowBrowser(NativeFlowTicket ticket)
        => _browser.Open(ticket.AuthorizeUrl);

    /// <summary>Cancels the in-flight native or magic capture.</summary>
    public void CancelPendingFlow()
    {
        Interlocked.Increment(ref _nativeGeneration);
        _completedListener?.Dispose(); _completedListener = null;
        _pendingListener?.Dispose();
        _pendingListener = null;
        _pendingNative = null;
        Publish(Current with { Phase = AuthPhase.SignedOut });
    }

    /// <summary>Waits for the browser redirect, exchanges the code, and signs
    /// the desktop bearer session in.</summary>
    public async Task<MagicConsumeOutcome> CompleteNativeSignInAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var ticket = _pendingNative;
        var listener = _pendingListener;
        if (ticket is null || listener is null) return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled };
        _pendingNative = null; // Consume once before the first await; repeated completion cannot exchange twice.
        var generation = Interlocked.Read(ref _nativeGeneration);
        bool authenticated = false;
        bool current() => generation == Interlocked.Read(ref _nativeGeneration);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? NativeFlowTimeout);
        var flowToken = deadline.Token;
        try
        {
            if (listener.Origin != _options.LoopbackOrigin) throw new InvalidOperationException("Callback origin changed");
            Publish(Current with { Phase = AuthPhase.AwaitingLoopback });
            var capture = await listener.WaitForCaptureAsync(timeout ?? NativeFlowTimeout, flowToken);
            if (!current()) return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled };
            if (capture is null) { Publish(Current with { Phase = AuthPhase.SignedOut, ErrorCode = "FLOW_TIMEOUT" }); return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled }; }
            // Validate every callback, including denial and transport/persistence errors.
            if (!_flow.IsValidState(ticket, capture.State)) { Publish(Current with { Phase = AuthPhase.SignedOut, ErrorCode = "STATE_MISMATCH" }); return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled }; }
            if (!string.IsNullOrEmpty(capture.Error))
            {
                var enrollment = capture.Error == "enrollment_required";
                Publish(Current with { Phase = enrollment ? AuthPhase.ConsentRequired : AuthPhase.SignedOut,
                    ErrorCode = enrollment ? "ENROLLMENT_REQUIRED" : "AUTHORIZATION_FAILED",
                    ErrorMessage = enrollment ? "Complete signup to continue." : "Sign-in could not complete. Please retry." });
                return new MagicConsumeOutcome { Kind = enrollment ? MagicOutcome.ConsentRequired : MagicOutcome.Cancelled };
            }
            if (capture.Kind != CaptureKind.OAuthCallback || string.IsNullOrEmpty(capture.Code)) throw new InvalidOperationException("Invalid callback");
            Publish(Current with { Phase = AuthPhase.TokenExchange });
            var bag = await _flow.ExchangeAsync(ticket, capture.Code, flowToken);
            var user = await _api.MeAsync(bag.AccessToken, null, flowToken);
            if (string.IsNullOrWhiteSpace(user.StableAccountId)) throw new InvalidOperationException("Missing verified account");
            await _nativeCommit.WaitAsync(flowToken);
            try
            {
                if (!current()) return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled };
                await _sessions.SaveAsync(bag, flowToken);
                if (!current()) { await _sessions.DeleteAsync(CancellationToken.None); return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled }; }
                authenticated = true;
                Publish(new AuthSnapshot { Phase = AuthPhase.SignedIn, User = user, Providers = Current.Providers });
            }
            finally { _nativeCommit.Release(); }
            return new MagicConsumeOutcome { Kind = MagicOutcome.SignedIn, User = user };
        }
        catch (Exception e) when (e is AuthApiException or HttpRequestException or OperationCanceledException or InvalidOperationException or System.IO.IOException or System.Security.Cryptography.CryptographicException)
        {
            if (current()) Publish(Current with { Phase = AuthPhase.SignedOut, ErrorCode = e is OperationCanceledException ? "FLOW_TIMEOUT" : "AUTHORIZATION_FAILED", ErrorMessage = "Sign-in could not complete. Please retry." });
            return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled };
        }
        finally
        {
            if (current())
            {
                listener.CompleteDesktop(authenticated);
                _completedListener = listener;
                listener.RetainCompletionPage();
                _pendingListener = null; _pendingNative = null;
                try { await ActivateExistingAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* Browser Open fallback remains available. */ }
            }
            else listener.Dispose();
        }
    }

    // ---- magic email -----------------------------------------------------------

    /// <summary>Request a magic email (POST /api/auth/magic/request). Grid's
    /// loopback origin must be an AUTH allowed origin; the delivered link lands
    /// on that origin with a #magic_link fragment.</summary>
    public async Task<MagicRequestAccepted> RequestMagicLinkAsync(string email, string intent, CancellationToken ct = default)
    {
        if (intent is not ("login" or "signup"))
        {
            throw new ArgumentException("intent must be login or signup", nameof(intent));
        }
        _pendingMagic = null;
        return await _api.MagicRequestAsync(email.Trim().ToLowerInvariant(), intent, _options.LoopbackOrigin, ct);
    }

    /// <summary>Wait for the delivered magic link to be opened (fragment
    /// capture). Returns the token when the magic capture page delivered it,
    /// or null on timeout / cancellation.</summary>
    public async Task<string?> WaitForMagicTokenAsync(
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        _pendingListener?.Dispose();
        _pendingListener = _listenerFactory();
        await _pendingListener.StartAsync(ct);
        Publish(Current with { Phase = AuthPhase.AwaitingLoopback });
        try
        {
            var deadline = DateTimeOffset.UtcNow + (timeout ?? MagicTokenTimeout);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var capture = await _pendingListener.WaitForCaptureAsync(deadline - DateTimeOffset.UtcNow, ct);
                if (capture is not null && capture.Kind == CaptureKind.MagicToken && !string.IsNullOrEmpty(capture.MagicToken))
                {
                    return capture.MagicToken;
                }
            }
            return null;
        }
        finally
        {
            _pendingListener?.Dispose();
            _pendingListener = null;
        }
    }

    /// <summary>Also accepts a magic token captured elsewhere (e.g. a
    /// <c>grid://</c> deep link or a user-pasted code) for consumption.</summary>
    public async Task<MagicConsumeOutcome> ConsumeMagicAsync(
        string token,
        string? origin = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("token is required", nameof(token));
        Publish(Current with { Phase = AuthPhase.TokenExchange });
        var result = await _api.MagicConsumeAsync(token, origin ?? _options.LoopbackOrigin, ct);
        _pendingMagic = result;
        if (result.IsSuccess && result.User is not null)
        {
            var user = await FinalizeMagicSessionAsync(result, ct);
            return new MagicConsumeOutcome { Kind = MagicOutcome.SignedIn, User = user };
        }
        if (result.IsSuccess)
        {
            // Valid session without a parseable user is still a gate; prefer gate otherwise.
            Publish(Current with { Phase = AuthPhase.ConsentRequired });
            return new MagicConsumeOutcome { Kind = MagicOutcome.ConsentRequired };
        }
        if (result.ConsentRequired && result.Gate is not null)
        {
            Publish(Current with { Phase = AuthPhase.ConsentRequired, User = null });
            return new MagicConsumeOutcome { Kind = MagicOutcome.ConsentRequired, Gate = result.Gate };
        }
        Publish(Current with { Phase = AuthPhase.SignedOut });
        return new MagicConsumeOutcome { Kind = MagicOutcome.Cancelled };
    }

    /// <summary>Accept the required docs (POST /api/auth/consent) and finalize
    /// the magic session into a durable native bearer session.</summary>
    public async Task<SessionUser> AcceptConsentAndFinalizeAsync(
        IReadOnlyDictionary<string, bool> consent,
        CancellationToken ct = default)
    {
        var result = _pendingMagic ?? throw new InvalidOperationException("No pending magic session to finalize.");
        var gate = result.Gate ?? throw new InvalidOperationException("No consent gate available.");
        var cookie = HostedCookieHeader.Build(result.SetCookies, _options.SessionCookieName, _options.RefreshCookieName);
        if (cookie is null)
        {
            throw new AuthApiException(502, "NO_HOSTED_SESSION_COOKIES");
        }
        _ = await _api.ConsentAsync(consent, gate.AppId, cookie, null, ct);
        var user = await FinalizeMagicSessionAsync(result, ct);
        return user;
    }

    private async Task<SessionUser> FinalizeMagicSessionAsync(MagicConsumeResult result, CancellationToken ct)
    {
        try
        {
            var bag = await _sessions.BootstrapFromHostedSessionAsync(result.SetCookies, ct);
            var user = await _api.MeAsync(bag.AccessToken, null, ct);
            Publish(new AuthSnapshot { Phase = AuthPhase.SignedIn, User = user, Providers = Current.Providers });
            return user;
        }
        catch (AuthApiException e)
        {
            Publish(Current with { Phase = AuthPhase.SignedOut, ErrorCode = e.Code, ErrorMessage = e.Description });
            throw;
        }
    }

    // ---- session lifecycle ---------------------------------------------------

    /// <summary>Restore the persisted bearer session on startup.</summary>
    public async Task RestoreAsync(CancellationToken ct = default)
    {
        BeginSessionRestore();
        try
        {
            var user = await _sessions.RestoreAsync(ct);
            Publish(user is null
                ? AuthSnapshot.SignedOutSnapshot(Current.Providers)
                : new AuthSnapshot { Phase = AuthPhase.SignedIn, User = user, Providers = Current.Providers });
        }
        catch (AuthApiException e)
        {
            Publish(Current with { Phase = AuthPhase.SignedOut, ErrorCode = e.Code, ErrorMessage = e.Description });
        }
    }

    /// <summary>
    /// Marks the startup authentication result unresolved before any
    /// prerequisite provider discovery begins. This is a real state-machine
    /// phase: it ends only when <see cref="RestoreAsync"/> publishes the
    /// authenticated or unauthenticated result.
    /// </summary>
    internal void BeginSessionRestore()
    {
        if (Current.Phase == AuthPhase.SessionRestore) return;
        Publish(Current with
        {
            Phase = AuthPhase.SessionRestore,
            User = null,
            ErrorCode = null,
            ErrorMessage = null,
        });
    }

    /// <summary>Re-fetch identity on activation/focus when signed in.</summary>
    public async Task RefreshIdentityAsync(CancellationToken ct = default)
    {
        if (Current.Phase != AuthPhase.SignedIn) return;
        try
        {
            var user = await _sessions.EnsureFreshAsync(ct);
            Publish(new AuthSnapshot { Phase = AuthPhase.SignedIn, User = user, Providers = Current.Providers });
        }
        catch (AuthApiException e) when (e.Status == 401)
        {
            Publish(AuthSnapshot.SignedOutSnapshot(Current.Providers));
        }
    }

    /// <summary>Sign out: revoke refresh + hosted session, clear the store.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        CancelPendingFlow();
        await _nativeCommit.WaitAsync(ct);
        try { await _sessions.SignOutAsync(ct); Publish(AuthSnapshot.SignedOutSnapshot(Current.Providers)); }
        finally { _nativeCommit.Release(); }
    }

    public Task<ProviderStatusResponse> ProviderStatusAsync(string provider, CancellationToken ct = default)
        => _api.GetProviderStatusAsync(provider, ct);

    // ---- internals -----------------------------------------------------------

    private void Publish(AuthSnapshot snapshot)
    {
        Current = snapshot;
        StateChanged?.Invoke(this, snapshot);
    }

    private IReadOnlyList<OAuthProviderOption> FallbackProviders()
    {
        if (_options.PresentationFallback is { Count: > 0 })
        {
            return _options.PresentationFallback;
        }
        return new List<OAuthProviderOption>
        {
            new() { Provider = "microsoft", Label = "Continue with Microsoft", Enabled = true, Available = false, Reason = "Provider is not configured" },
            new() { Provider = "github", Label = "Continue with GitHub", Enabled = true, Available = false, Reason = "Provider is not configured" },
            new() { Provider = "google", Label = "Continue with Google", Enabled = true, Available = false, Reason = "Provider is not configured" },
        };
    }

    public void Dispose() => CancelPendingFlow();
}
