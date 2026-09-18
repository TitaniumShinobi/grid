# Grid provider discovery v1

Grid discovers stores, mod managers, games, editions, profiles, and active provider processes without assuming a drive letter. Discovery is read-only and produces reviewable registration candidates; it does not register, relocate, or change external data.

## Initial providers

- Steam: registry/process roots, declared Steam libraries, application manifests, and game-definition verification.
- Mod Organizer 2: explicit or active executable roots, portable or selected instance configuration, and profile directories.
- Vortex: explicit or active executable roots and bounded profile-root observations. Authoritative Vortex profile and managed-game state remains a later bridge-adapter responsibility.
- Manual fallback: the existing installation path picker remains the fallback when deterministic discovery cannot establish a candidate.

## GTA V identities

Steam application 271590 is GTA V Legacy and requires `GTA5.exe`. Steam application 3240220 is GTA V Enhanced and requires `GTA5_Enhanced.exe`. Each edition remains a distinct installation. Observations resolving to the same canonical installation root are represented as one registration candidate with multiple provider relationships.

## Authority

`Invoke-GridProviderDiscovery.ps1` reads provider metadata and can optionally write its Grid-owned JSON report. External provider, manager, profile, and game state is never modified by discovery. Registration remains a separate reviewed action.

## Desktop registration flow

The production Add Game command runs the packaged discovery script and presents only `ReadyForReview` installation candidates. Users may select either GTA V edition, select both, rescan, or browse to a directory containing the expected edition executable. The review surface displays the exact installation paths and observed manager relationships before registration.

Approved registrations are stored at `%LOCALAPPDATA%\Grid\connections\game-installations.v1.json`. They decorate the production catalog as external, read-only installations and persist across restart. Disconnect removes only the selected Grid-owned registration. MO2 connections continue to use their existing independent reference store.
