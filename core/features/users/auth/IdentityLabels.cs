using System;

namespace Grid.Auth.Features;

/// <summary>Human-readable provider labels for the Grid shell (identity records
/// carry the AUTH provider id; Grid renders localized display names).</summary>
public static class IdentityLabels
{
    private const string MagicEmailLabel = "Magic email";

    public static string ForProviderId(string? provider) => provider switch
    {
        "microsoft" => "Microsoft",
        "github" => "GitHub",
        "google" => "Google",
        "email" => MagicEmailLabel,
        "credentials" => "Email and password",
        null or "" => "Unknown account",
        var p when p.StartsWith("federation:", StringComparison.Ordinal) => "Federated",
        var p => p,
    };
}