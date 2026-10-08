using System;
using System.Collections.Generic;
using Grid.Auth.Api;

namespace Grid.Auth.Features;

/// <summary>Builds the Cookie header for cookie-authenticated AUTH calls from
/// Set-Cookie headers captured on the magic flow (auth_sid / auth_rid).</summary>
public static class HostedCookieHeader
{
    public static string? Build(IReadOnlyList<string> setCookieHeaders, string sessionCookieName, string refreshCookieName)
    {
        var parts = new List<string>(2);
        if (SetCookieParser.TryGetValue(setCookieHeaders, sessionCookieName, out var sid))
        {
            parts.Add($"{sessionCookieName}={sid}");
        }
        if (SetCookieParser.TryGetValue(setCookieHeaders, refreshCookieName, out var rid))
        {
            parts.Add($"{refreshCookieName}={rid}");
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }
}