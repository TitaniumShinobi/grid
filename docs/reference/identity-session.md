# Identity & Session (Desktop)

## Sources of truth

- **Persisted session**: `core/features/users/auth/SecureTokenStore.cs`.
  - Windows: `DpapiTokenStore` — access/refresh tokens encrypted with DPAPI
    (CurrentUser) at `%LOCALAPPDATA%\Grid\auth\*.json`.
  - Dev / non-Windows: `InMemoryTokenStore` (sessions do not persist).
- **Runtime authority**: `core/features/users/auth/SessionManager.cs`,
  key `grid-native-bearer`.

## The bearer lifecycle

1. **Native OAuth** completes → AUTH sets `auth_sid` + `auth_rid` cookies on the
   `/oauth/token` response → the client stores the token bag (access + refresh).
2. **Magic email** completes → consume 200 also sets the cookies → the client
   reads `auth_rid` from `Set-Cookie` visible in the response, POSTs a refresh
   grant to `/oauth/token`, stores the resulting bearer.
3. **Restore/session resume** (`RestoreAsync`): refresh grant using the stored
   refresh token; `GET {baseUrl}/api/session` to confirm; refresh the access
   token at `refreshSkewSeconds` before expiry (`TokenBag` is immutable; the
   vault bag is rewritten atomically).
4. **Sign-out** (`PostAsync /api/logout` with bearer) removes the persisted bag.

## The identity snapshot (`AuthSnapshot`)

`core/features/users/auth/AuthState.cs`. The shell reads:

| Field | Meaning |
| --- | --- |
| `Phase` | `Idle → Loaded` (providers) → `SignedOut \| SignedIn \| ConsentRequired` |
| `User` | `SessionUser` (signed-in) |
| `Error` | last non-fatal error message |
| `Providers` | `OAuthProviderOption[]`: `provider`, `label`, `enabled`, `available`, `reason` |
| `MagicEmailLastRequested` | consumed by the shell for the “check your email” state |

`SessionUser` fields (`SessionUser.cs`):
`StableAccountId` (`Uid` fallback `Sub`), `Name` (`GivenIden` fallback `Name`),
`Email`, `AuthProvider` (`auth_provider`), `Picture` (avatar URL/data URL),
`AccountContextReadiness` (`account_context_readiness`).

## Signed-out / signed-in UX contract

- **Signed out**: shell renders the sign-in/magic panel; product panels,
  console, and discovery MUST be disabled/ghosted (this is the surprise-reset
  guard — never show product data for an anonymous/absent identity).
- **Signed in**: bind `StableAccountId`/`Name`/`Email`/`AuthProvider`; owner
  avatar rendered circular (`AvatarImageSource.fromPictureAsync` →
  `BitmapImage`, clipped with `Ellipse`/`PersonPicture`).

## Provider semantics

`microsoft`, `github`, `google` are first-class AUTH providers (native flow).
`magicEmail` is email-only. The hosted UI (`authorizationUiUrl`) owns provider
selection; the desktop passes `primaryProvider` + `connectionKey` (one-time
session secret bound to the PKCE state) so AUTH can finalize back to the
loopback with `?code=&state=`.