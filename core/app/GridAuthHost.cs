using System;
using Grid.Auth.Api;
using Grid.Auth.Features;

namespace Grid.Auth;

/// <summary>
/// Composition root for Grid auth. Call once at app startup and hand the
/// resulting <see cref="GridAuthService"/> to the shell. Explicit composition —
/// no DI container required — so the payload has zero package dependencies.
/// </summary>
public static class GridAuthHost
{
    /// <summary>
    /// Compose the auth stack. Overrides are for tests and host wiring:
    ///   <paramref name="store"/> — production default is DPAPI on Windows
    ///   (CurrentUser) and an in-memory store elsewhere (dev only).
    ///   <paramref name="api"/>   — production wiring uses AUTH from
    ///   <paramref name="options"/>.BaseUrl.
    ///   <paramref name="browser"/> — production default launches the system
    ///   browser for the native flow.
    /// </summary>
    public static GridAuthService Compose(
        AuthOptions options,
        ITokenStore? store = null,
        IGridAuthApi? api = null,
        IBrowserLauncher? browser = null,
        Func<LoopbackListener>? listenerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var effectiveApi = api ?? CreateDefaultApi(options);
        var effectiveStore = store ?? CreateDefaultStore();
        return new GridAuthService(options, effectiveApi, effectiveStore, browser, listenerFactory);
    }

    /// <summary>Default HTTP boundary: a GridAuthHttpClient against the configured AUTH origin.</summary>
    public static GridAuthHttpClient CreateDefaultApi(AuthOptions options)
        => new(options.BaseUri);

    /// <summary>Default secure store: DPAPI (CurrentUser) on Windows; for
    /// non-Windows dev builds falls back to the in-memory store so the app can
    /// still boot (sessions do not persist there — dev only).</summary>
    public static ITokenStore CreateDefaultStore()
        => OperatingSystem.IsWindows()
            ? new DpapiTokenStore()
            : new InMemoryTokenStore();

    /// <summary>Load options from the Grid-shipped config file.</summary>
    public static AuthOptions LoadOptions(string configPath)
        => AuthOptions.FromJson(System.IO.File.ReadAllText(configPath));
}