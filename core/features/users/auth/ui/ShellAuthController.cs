// Requires WinUI 3 (Microsoft.WindowsAppSDK). UI-only seam between the auth
// feature (core/features/users/auth/GridAuthService.cs) and the Grid shell.
// Excluded from the non-WinUI verification/test build in tests/Grid.Auth.Tests.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Grid.Auth.Features;

/// <summary>
/// The deterministic seam the Grid shell binds to:
///
///  1. Create with the composed <see cref="GridAuthService"/> and a render
///     callback.
///  2. <see cref="Render"/> is invoked on every StateChanged with a snapshot AND
///     the resolved avatar <see cref="Microsoft.UI.Xaml.Media.ImageSource"/>.
///     The shell must:
///       - SignedOut: render the sign-in panel, disable/ghost product controls,
///         block side/right panels and the console, disable product discovery.
///       - SignedIn:  bind StableAccountId, Name, Email, AuthProvider, avatar;
///         enable the shell.
///  3. Wire sign-in commands to <see cref="SignInWith"/>, magic email to
///     <see cref="RequestMagicAsync"/>/<see cref="ConsumeTokenAsync"/>,
///     logout to <see cref="SignOut"/>.
///  4. On consent-required, the shell shows the gate docs and calls
///     <see cref="AcceptConsentAsync"/>.
/// </summary>
public sealed class ShellAuthController : IDisposable
{
    private readonly GridAuthService _service;
    private readonly Action<AuthSnapshot, Microsoft.UI.Xaml.Media.ImageSource?> _render;

    public ShellAuthController(GridAuthService service, Action<AuthSnapshot, Microsoft.UI.Xaml.Media.ImageSource?> render)
    {
        _service = service;
        _render = render;
        _service.StateChanged += OnStateChanged;
    }

    public GridAuthService Service => _service;

    /// <summary>Load provider discovery then restore the persisted session.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _service.BeginSessionRestore();
        await _service.LoadProvidersAsync(ct);
        await _service.RestoreAsync(ct);
    }

    public void SignInWith(string provider, string? prompt = null, bool acceptRequiredConsent = false)
    {
        var ticket = _service.BeginProviderSignInAsync(provider, prompt, acceptRequiredConsent);
        _service.OpenNativeFlowBrowser(ticket);
        _ = CompleteNativeAsync();
    }

    /// <summary>Continue the native flow in the background (browser already
    /// opened). Called by <see cref="SignInWith"/> and by any deep-link
    /// fallback.</summary>
    public Task CompleteNativeAsync(CancellationToken ct = default)
        => _service.CompleteNativeSignInAsync(ct: ct);

    public void CancelFlow() => _service.CancelPendingFlow();

    public async Task RequestMagicAsync(string email, string intent, CancellationToken ct = default)
    {
        await _service.RequestMagicLinkAsync(email, intent, ct);
        // The shell should start WaitMagicAsync in the background after showing
        // "check your email"; AUTH magic links expire after 10 minutes.
    }

    public Task<string?> WaitMagicAsync(CancellationToken ct = default)
        => _service.WaitForMagicTokenAsync(ct: ct);

    public async Task ConsumeTokenAsync(string token, CancellationToken ct = default)
        => await _service.ConsumeMagicAsync(token, ct: ct);

    public async Task AcceptConsentAsync(IReadOnlyDictionary<string, bool> consent, CancellationToken ct = default)
        => await _service.AcceptConsentAndFinalizeAsync(consent, ct);

    public void SignOut() => _ = _service.SignOutAsync();

    public async Task<Microsoft.UI.Xaml.Media.ImageSource?> LoadAvatarAsync(string? picture, CancellationToken ct = default)
        => await AvatarImageSource.FromPictureAsync(picture, ct);

    private async void OnStateChanged(object? sender, AuthSnapshot snapshot)
    {
        var avatar = snapshot.IsSignedIn
            ? await LoadAvatarAsync(snapshot.User?.Picture)
            : null;
        _render(snapshot, avatar);
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        _service.Dispose();
    }
}
