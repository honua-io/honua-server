# ADR-0048: Single Auth/Identity/Token Foundation Shared by OIDC SSO and ArcGIS OAuth2

## Status

Accepted

## Context

Honua's enterprise-auth roadmap has three tracks landing in close succession:

- **#348 — OIDC SSO.** The interactive single-sign-on track for
  Entra ID / Okta / Auth0 / generic OIDC.
- **#1242 — ArcGIS OAuth2 named-user.** `oauth2/authorize` + `oauth2/token`
  so ArcGIS Pro "Add Portal" and Field Maps can log a *named user* in against
  Honua's portal facade (#1240).
- **#349 — RBAC.** Per-service/per-layer/per-operation access decisions, of
  which the persistent store (#1374) and canonical resolver (#1375) are the
  load-bearing half.

The risk identified in the #348/#349 assessment is that #1242 and #348 each
build their **own** auth/token system — a second user store, a second token
store, and a second group→role mapping — and we rebuild authentication twice.

A single foundation already exists in-tree and must be the basis for all three
tracks rather than reinvented:

- **Identity / login:** `OidcAuthenticationExtensions`
  (`src/Honua.Hosting/Features/Authentication/OidcAuthenticationExtensions.cs`)
  already wires JWT-bearer validation **and** interactive auth-code+PKCE for
  Entra ID, Google, Okta, Auth0, and a generic OIDC provider, with per-provider
  authority/issuer/audience config, `env:` secret resolution, token-replay
  protection, and a composite policy scheme selecting between JWT,
  admin-session cookie, client certificate, and API key. Provider quirks
  (Okta `groups`, Auth0 namespaced `roles`/`permissions`) are already handled,
  and claims→role mapping runs through `OidcClaimsTransformation`.
- **ArcGIS-shaped tokens:** `IPortalTokenIssuer`
  (`src/Honua.Core/Features/Authorization/Abstractions/IPortalTokenIssuer.cs`)
  + `PortalTokenIssuer`
  (`src/Honua.Hosting/Features/Authentication/PortalTokenIssuer.cs`) mint
  distributed-cache-backed opaque bearer tokens that are URL-safe for the
  `?token=` query parameter ArcGIS clients send, with referer/IP/host binding.
  `PortalTokenAuthenticationHandler` validates them on the request path, and
  `generateToken` is live at
  `src/Honua.Protocols.GeoServices/Sharing/SharingRestEndpoints.cs`.
- **Access decisions:** the RBAC resolver (#1375) over the persistent role
  store (#1374) is the per-operation `(service, layer, operation) → allow/deny`
  seam (see the scope section below), backed by `IRoleStore` and
  `EffectivePermissions` in `Honua.Core`.

If #1242 ships before the RBAC resolver exists, it will hardcode the access
decision and we will rebuild it; therefore RBAC-resolver-first is an explicit
cross-track ordering constraint.

## Decision

**There is exactly one auth/identity/token foundation. All three tracks
compose it; none of them introduces a parallel one.**

1. **Identity is OIDC.** Interactive login and bearer validation are owned by
   the existing `OidcAuthenticationExtensions` middleware and its composite
   policy scheme. New providers are configuration, not new code paths.

2. **ArcGIS tokens are minted by `IPortalTokenIssuer`.** Any Esri-shaped token
   (the `?token=` opaque bearer, the `generateToken` response, and the eventual
   `oauth2/token` response) is produced and validated through the existing
   issuer/handler pair. No second token store.

3. **Authorization is the RBAC resolver.** Per-operation access decisions for
   every protocol are made by the canonical permission resolver (#1375) over
   the persistent role store (#1374). RBAC grants are the **source** of the
   access policies the portal facade exports for item sharing (#1240) — the
   facade reads grants, it does not own a separate policy model.

4. **#1242 is a thin bridge, not a new system.**
   - `oauth2/authorize` delegates to the configured per-provider OIDC
     auth-code+PKCE flow already in `OidcAuthenticationExtensions`.
   - On callback, `oauth2/token` returns the Esri-shaped
     `{ access_token, expires_in, refresh_token }` minted via
     `IPortalTokenIssuer`.
   - #1242 and #1240 MUST NOT introduce a separate user store, a separate
     token store, or a separate group→role mapping. Group→role mapping is
     `OidcClaimsTransformation`; roles/grants live in the persistent role
     store; access decisions are the resolver.

5. **Ordering constraint.** The RBAC persistent store (#1374) and resolver
   (#1375) land **before** #1242 hardcodes access decisions. Sequence:
   RBAC persistent store + resolver → OIDC provider validate/test (#499) →
   #1242 OAuth2 bridge → SCIM (#510) / SAML (#508) write into the same store.

## Consequences

**Easier:**

- One place to reason about identity (OIDC), one place for Esri tokens
  (`IPortalTokenIssuer`), one place for access decisions (the resolver). The
  ArcGIS facade and SSO share the same role/grant truth.
- #1242 becomes a small, testable adapter rather than an auth subsystem.
- SCIM (#510) / SAML (#508) provision into a single durable role store.

**Harder / explicit trade-offs:**

- #1242 cannot proceed until #1374 + #1375 are merged (this ADR makes that an
  accepted dependency, not an accident).
- Any future "quick" parallel token path is now an ADR violation and should be
  rejected in review.

## Scope landed with this ADR

This ADR is recorded alongside the first implementation slice that makes it
real (#1374 + #1375):

- **#1374** — Postgres-backed `IRoleStore` (`PostgresRoleStore`) with an
  idempotent schema migration, replacing `InMemoryRoleStore` as the registered
  implementation (in-memory retained for Community/no-DB mode and tests).
- **#1375** — the canonical `IPermissionResolver`
  (`(principal, service, layer, operation) → allow / deny / requires-auth`)
  over `EffectivePermissions`, wired into the existing enforced read/write
  seam (`AccessPolicyEvaluator`) so today's protocol paths consult
  per-operation grants, **falling back to the coarse `AccessPolicy` when no
  grant matches** (no behavior change for unconfigured services).

Deferred to **#1376**: re-wiring every protocol adapter to call the resolver
with the full per-operation taxonomy (the cross-protocol conformance matrix).
Deferred to **#1242**: the OAuth2 bridge itself.

## Cross-references

- Complements ADR-0024 (Open-Core Edition Model) and the RBAC enforcement model.
- Linked from #348 (OIDC SSO), #349 (RBAC tracking), #1240 (ArcGIS portal
  facade), #1242 (ArcGIS OAuth2 named-user), #1374 (persistent store),
  #1375 (resolver).
