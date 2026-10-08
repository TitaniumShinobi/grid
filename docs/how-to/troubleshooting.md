# Troubleshooting

## Broker never lands back in the app

- Check the loopback listener actually bound: `netstat -ano | findstr 51706`.
  `DpapiTokenStore` requires Windows; on non-Windows/headless dev the app uses
  the in-memory store — sessions don’t persist but the flow completes.
- AUTH was not configured for the loopback origin. `req.origin` must pass
  `allowedOrigins`: desktop loopback uses `http://127.0.0.1:51706`; the callback
  path is `/grid-natives-callback`. Both must be on the server
  (config/grid.auth.server-config.example.json).
- PKCE mismatch: verify `codeChallenge`/`code_verifier` are consistent and
  `codeChallengeMethod=S256`. The C# `Pkce` class matches AUTH’s S256 algorithm
  exactly (RFC 7636); the JS oracle in this payload verifies equivalently.
- `state` param: the desktop’s `connectionKey` + `state` must round-trip. If the
  hosted UI changed origin labels, confirm `primaryProvider` + `connectionKey`
  were passed to `/oauth/authorize` from `OAuthNativeFlow.Ticket`.
- The redirect was a `#` hash variant. AUTH finalizes loopback with `?code=&state=`
  as the desktop expects; if a browser event jumps to a `grid://` deep link
  instead, ensure the protocol handler routes `args.Uri` back into
  `CompleteNativeAsync`/`HandleDeepLink`.

## “Magic link not opening”

- **Fragment rule**: browsers never send `#` to the server. The loopback capture
  page at `http://127.0.0.1:51706/grid-natives-callback` reads `#magic_link=` in
  the browser and posts `/magic-capture` to the listener. Verify the browser you
  tested with honors the capture page (some hardened browsers block loopback
  POSTS from a downloaded page — use the fallback mark OK with
  `?magic_token=` query).
- Magic expires after 10 minutes (AUTH). Request a new one and try again.
- `AUTH_MAGIC_DELIVERY_SECRET` must be ≥32 chars or delivery 500s.

## Consent gate

- 403 `ENROLLMENT_REQUIRED` / `missing_consent` is expected for first-time magic
  users. The client surfaces `gate.Consent.Docs`; accept via
  `POST /api/auth/consent?app=…` with `{ consent: { docKey: true } }`, then
  continue with the stored continuation. If the app shows this repeatedly, the
  acceptance POST failed server-side — repeat with docs for the current product.

## Sign-in works but sessions drop on restart

- Ensure `Windows` path uses `DpapiTokenStore` (only one). Glean all windows
  (WinUI process name) rebuilt to pick up the P/Invoke `CryptProtectData` — the
  store is only hosted on the WinUI process.
- `TokenExpiry` vs `refreshSkewSeconds`: the client refreshes before expiry via
  the stored refresh token.

## Build errors

- `ui/` files need WinUI — never include them in the test/verification csproj
  (tests/Grid.Auth.Tests only compiles `core/`).
- If the Grid csproj has `ImplicitUsings` disabled, the payload’s explicit
  `using` lines already cover it. No package adds needed.
- Service uses no `IHttpClientFactory`; `GridAuthHttpClient` manages its own
  `HttpClient`. Do not wrap it in containers that double-dispose.

## Proxy / TLS environment

- `GridAuthHttpClient` inherits system `HttpClient` defaults (use
  `HttpClientHandler` overrides if the enterprise proxy requires them).
- Certificate pinning is out of scope; do not disable TLS for testing.