# AUTH API Endpoints (Desktop Client Contract)

The Grid desktop client (`core/api/Auth/GridAuthHttpClient.cs`) talks only to
these endpoints on the configured AUTH authority. Everything below is enforced
by AUTH-core's `src/app.ts` and `src/auth/oauth.ts`; the client mirrors it.

## Verification

- **Native OAuth (Microsoft / GitHub / Google)**
  - `GET {baseUrl}/oauth/authorize?primaryProvider={provider}&connectionKey={connKey}&state={state}&codeChallenge={pkceChallenge}&codeChallengeMethod=S256&responseType=code&clientId={clientId}&redirectToLoopback={callbackUrl}`
    - No `provider` param: the **hosted authorization UI** decides the
      provider; the loopback redirect is finalized by AUTH with
      `?code=...&state=...` (never `#`).
  - `POST {baseUrl}/oauth/token` — authorization-code + PKCE exchange.
    Form body: `grant_type=authorization_code&code={code}&state={state}&code_verifier={verifier}&redirectToLoopback={callbackUrl}&clientId={clientId}`.
  - `POST {baseUrl}/oauth/token` — **refresh grant** (magic/session bootstrap):
    `grant_type=refresh_token&refreshToken={auth_rid}` → new bearer tokens.
- **Session**
  - `GET {baseUrl}/api/session`
  - `GET {baseUrl}/api/me` — `{ ok, user: { id, uid, sub, name, givenIden, picture, auth_provider, ... } }`
  - `POST {baseUrl}/api/logout`
- **Magic email**
  - `POST {baseUrl}/api/auth/magic/request` — `{ email, intent: 'login'|'signup', origin }`.
    `origin` must be an allowed origin. 202 `{ ok, state:'EMAIL_REQUEST_ACCEPTED' }`;
    **403 `ENROLLMENT_REQUIRED`** when `login` is attempted for a user lacking
    consent or for an unknown email.
  - `POST {baseUrl}/api/auth/magic/deliver` — `{ email, url }`, Bearer
    `AUTH_MAGIC_DELIVERY_SECRET` (≥32 chars). `url` must be an allowed origin,
    pathname `/`, no query, hash exactly `#magic_link=[A-Za-z0-9_-]+`.
  - `POST {baseUrl}/api/auth/magic/consume` — `{ token, origin }`; 200 sets
    `auth_sid`/`auth_rid` cookies and returns `{ ok, user, intent }`, or 403 with
    the consent gate below.
- **Consent gate** (magic consume on an unenrolled/consent-missing account)
  - 403 body: `{ ok:false, state:'ENROLLMENT_REQUIRED', reason:'missing_consent', requiresProductSignup, appId, message, consent:{ method:'POST', url:'/api/auth/consent?app=…', docs } , continuation }`
    — note this 403 **also carries the hosted session Set-Cookie headers**.
  - `POST {baseUrl}/api/auth/consent?app=…` — body `{ consent: { "<docKey>": true } }`;
    200 `{ ok, user: <session> }`.

## Cookie names

- Session: `auth_sid` (`AUTH_COOKIE_NAME`); refresh: `auth_rid`
  (`AUTH_REFRESH_COOKIE_NAME`). The desktop matches these case-insensitively from
  `Set-Cookie` headers via `core/api/Auth/GridAuthHttpClient.cs`.

## Security notes (desktop)

- The desktop holds **no client secret**; PKCE proves possession of the
  authorization code. Never copy a provider secret into `config/`.
- The complete redirect response used to end the loopback flow:
  `grid://auth` deep link (see INSTALL.md for AppxManifest registration) or the
  loopback HTML page; AUTH redirects a desktop user agent directly to the
  loopback callback final with query params when the public client uses
  `redirectToLoopback`.