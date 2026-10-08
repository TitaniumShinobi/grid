# Auth Config Schema (Desktop)

## `config/grid.auth.config.json` — desktop, shipped inside the app

Deserialized by `core/features/users/auth/AuthOptions.cs`
(`AuthOptions.FromJson`). JSON schema: `docs/reference/auth-config-schema.json`.

| Property | Type | Default | Notes |
| --- | --- | --- | --- |
| `baseUrl` | string (uri, no trailing `/`) | — | AUTH authority origin |
| `clientId` | string | `grid-windows` | AUTH public client |
| `loopbackHost` | string | `127.0.0.1` | Loopback listener bind host |
| `loopbackPort` | int | `51706` | Must also be in server `allowedOrigins` |
| `loopbackPath` | string | `/grid-natives-callback` | Public-client loopback path |
| `scope` | string[] | `openid profile email` | Requested OAuth scope |
| `sessionCookieName` | string | `auth_sid` | Must equal `AUTH_COOKIE_NAME` |
| `refreshCookieName` | string | `auth_rid` | Must equal `AUTH_REFRESH_COOKIE_NAME` |
| `refreshSkewSeconds` | int | `30` | Refresh the access token this far before expiry |
| `presentationFallback` | array | — | Static provider list used when discovery is unreachable |

The desktop is a **public client**: this file contains **no secrets**.

## `config/grid.auth.server-config.example.json` — AUTH deployment AuthAppConfig

`AUTH_APP_CONFIG_PATH=config/appConfig.json`, `AUTH_APP_CONFIG_PATHS` extra. The
critical desktop-relevant fields:

- `app.id` / `app.name` — product identity (enforced consent banners).
- `allowedOrigins[]` — **must** include `http://127.0.0.1:51706` (magic links
  must land in the native listener) and the product's own origins.
- `publicClients[]` — member with `clientId: "grid-windows"`,
  `authorizationUiUrl`, and `loopback: { hosts: ["127.0.0.1","::1"],
  path: "/grid-natives-callback", allowDynamicPorts: true }`.
- `providers[]` — the provider chips surfaced by the hosted UI
  (`microsoft`, `github`, `google`); `oauth.envPrefix` e.g. `GRID` selects the
  `GRID_MICROSOFT_CLIENT_*` secret vars.
- `docs[]` — consent docs for the enrollment gate.

## Matching rule

Change a cookie name → change both `AUTH_COOKIE_NAME`/`AUTH_REFRESH_COOKIE_NAME`
(server) and `sessionCookieName`/`refreshCookieName` (desktop). Change a loopback
port → change `loopbackPort` (desktop) **and** `allowedOrigins`,
`publicClients[].loopback` (server).