using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grid.Auth.Api;

namespace Grid.Auth.Features;

/// <summary>Everything needed to open a native browser flow and later finish it.</summary>
public sealed record NativeFlowTicket
{
    public string Provider { get; init; } = "";
    public string AuthorizeUrl { get; init; } = "";
    public string State { get; init; } = "";
    public string Verifier { get; init; } = "";
    public string CodeChallenge { get; init; } = "";
    public string RedirectUri { get; init; } = "";
}

/// <summary>
/// Builds the AUTH native authorize URL and exchanges the resulting code.
/// Provider protocol (Microsoft/GitHub/Google) is fully owned by AUTH — Grid
/// only forwards its public-client PKCE request; the hosted sign-in page does
/// provider selection. No OAuth client secret exists in Grid.
/// </summary>
public sealed class OAuthNativeFlow
{
    private readonly AuthOptions _options;
    private readonly IGridAuthApi _api;

    public OAuthNativeFlow(AuthOptions options, IGridAuthApi api)
    {
        _options = options;
        _api = api;
    }

    /// <summary>Create a fresh native flow (opaque state, S256 verifier),
    /// ready for the shell to open <see cref="NativeFlowTicket.AuthorizeUrl"/>.</summary>
    public NativeFlowTicket CreateTicket(string provider, string? prompt = null, bool acceptRequiredConsent = false)
    {
        var state = Pkce.GenerateState();
        var verifier = Pkce.GenerateVerifier();
        var challenge = Pkce.ComputeChallenge(verifier);
        var query = new List<string>
        {
            "response_type=code",
            $"client_id={Uri.EscapeDataString(_options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(_options.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
            $"scope={Uri.EscapeDataString(string.Join(' ', _options.Scope))}",
            $"provider={Uri.EscapeDataString(provider)}",
        };
        if (acceptRequiredConsent)
        {
            query.Add("accept_required_consent=true");
        }
        if (!string.IsNullOrEmpty(prompt))
        {
            query.Add($"prompt={Uri.EscapeDataString(prompt)}");
        }
        var authorizeUrl = $"{_options.BaseUrl.TrimEnd('/')}/oauth/authorize?{string.Join('&', query)}";
        return new NativeFlowTicket
        {
            Provider = provider,
            AuthorizeUrl = authorizeUrl,
            State = state,
            Verifier = verifier,
            CodeChallenge = challenge,
            RedirectUri = _options.RedirectUri,
        };
    }

    public bool IsValidState(NativeFlowTicket ticket, string? state)
        => string.Equals(ticket.State, state, StringComparison.Ordinal);

    /// <summary>Exchange the loopback code for a token bag (POST /oauth/token).</summary>
    public async Task<TokenBag> ExchangeAsync(NativeFlowTicket ticket, string code, CancellationToken ct = default)
    {
        var response = await _api.ExchangeCodeAsync(new CodeExchangeParams(code, _options.ClientId, ticket.RedirectUri, ticket.Verifier), ct);
        return TokenBag.FromTokens(
            response.AccessToken,
            response.RefreshToken,
            response.TokenType,
            response.ExpiresIn,
            _options.ClientId,
            response.Scope);
    }
}