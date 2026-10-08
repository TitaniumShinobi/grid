# How to set up AUTH and verify the desktop integration

## 1. Stand up AUTH

```bash
cd auth
cp export/grid-auth-install/config/grid.auth.server-config.example.json testConfig/appConfig.json
cp export/grid-auth-install/config/grid.auth.env.example ./.env
# add real provider secrets + AUTH_MAGIC_DELIVERY_SECRET to .env (or service env)
npm run dev   # AUTH on 127.0.0.1:3100
```

Expected contract checks (identity via `curl`); the desktop targets the same
endpoints:

```bash
BASE=http://127.0.0.1:3100
curl -s "$BASE/oauth/authorize?primaryProvider=github&state=test&codeChallenge=abc&codeChallengeMethod=S256&responseType=code&clientId=grid-windows&redirectToLoopback=http%3A%2F%2F127.0.0.1%3A51706%2Fgrid-natives-callback" 
# -> 302 to the hosted authorization UI
curl -s "$BASE/api/config"      # provider + docs discovery
curl -s -X POST "$BASE/api/auth/magic/request" -H 'Content-Type: application/json' -d '{"email":"me@example.com","intent":"login","origin":"http://127.0.0.1:51706"}'
curl -s -X POST "$BASE/api/auth/magic/consume" -H 'Content-Type: application/json' -d '{"token":"...","origin":"http://127.0.0.1:51706"}'  # 200 sets auth_sid/auth_rid
```

## 2. Desktop config

Copy `config/grid.auth.config.example.json` → `config/grid.auth.config.json`;
keep `baseUrl` pointing at the AUTH authority (http://127.0.0.1:3100 for local
testing).

## 3. Run the in-payload test runner (Windows, real compile)

```powershell
dotnet run --project tests/Grid.Auth.Tests  # net9.0, zero NuGet packages
```

Covers: PKCE S256 derivation vs RFC 7636 vectors, base64url round-trips and
padding, DTO JSON contract (camelCase, auth_provider, account_context_readiness),
in-memory token store, refresh-skew math, HTTP contract via `FakeHandler`
(headers/cookies/errors), native-flow state binding, session manager sign-out,
magic consume → refresh bootstrap, and consent-gate continuation.

Net-local serves JS-equivalence oracle outputs (see `docs/how-to/` in this
file); the C# runner asserts the same RFC vectors our `verify/` JS uses so the
two agree.

## 4. Live smoke on Windows

1. Launch the built Grid app; confirm signing panel appears and product panels
   stay hidden.
2. `Continue with GitHub` → system browser → authorize → returns to the app.
3. Confirm owner panel binds `StableAccountId`, `Name`, `Email`, `auth_provider`,
   circular avatar.
4. Restart the app → still signed in (DPAPI store) → signed out → restart shows
   sign-in panel only.
5. Magic email → consent gate (if unenrolled) → accept terms → signed in via
   refresh bootstrap.