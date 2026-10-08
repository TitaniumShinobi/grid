using System;
using System.Collections.Generic;

namespace Grid.Auth.Features;

/// <summary>Provider button entry resolved from AUTH discovery
/// (GET /api/auth/config).</summary>
public sealed record OAuthProviderOption
{
    public string Provider { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool Available { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Undisambiguated session phase for the Grid shell.</summary>
public enum AuthPhase
{
    SignedOut,
    SigningIn,
    AwaitingBrowser,
    AwaitingLoopback,
    TokenExchange,
    ConsentRequired,
    SessionRestore,
    SignedIn,
    SigningOut,
    Error,
}

/// <summary>
/// Immutable snapshot the Grid shell binds against. The shell derives
/// "signed in / signed out" from <see cref="IsSignedIn"/>; product controls,
/// panes and the console stay disabled/ghosted while signed out.
/// </summary>
public sealed record AuthSnapshot
{
    public AuthPhase Phase { get; init; } = AuthPhase.SignedOut;
    public SessionUser? User { get; init; }
    public IReadOnlyList<OAuthProviderOption>? Providers { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public bool IsSignedIn => this.Phase == AuthPhase.SignedIn && this.User is not null;

    public static AuthSnapshot SignedOutSnapshot(IReadOnlyList<OAuthProviderOption>? providers)
        => new() { Phase = AuthPhase.SignedOut, Providers = providers };
}