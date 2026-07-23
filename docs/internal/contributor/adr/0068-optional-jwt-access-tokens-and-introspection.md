# ADR-0068: Optional JWT access tokens + RFC 7662 introspection for the OAuth2 surface

## Status

Accepted. Implements ADR-0053 Increment 4 (#1890). Gated and opt-in; the default
opaque-token path is unchanged.

## Context

ADR-0049/0053 fixed a single token foundation: every Esri-shaped token (the
`?token=` bearer, the `generateToken` response, and the `oauth2/token` response) is
an **opaque, distributed-cache-backed** value minted through `IPortalTokenIssuer`,
validated by one request-path validator (`PortalTokenAuthenticationHandler`), and
revoked synchronously by cache eviction. ADR-0053 §4 deliberately kept JWT out of
scope because a second token format would add a second validation path and
signing-key management on the request hot-path.

Parity gap **#1890** asks for two things some integrations need without abandoning
that model:

1. an **optional JWT access-token format** for callers that want stateless
   validation of the token they hold, and
2. an **RFC 7662 token introspection endpoint** so a resource server can ask Honua
   whether a presented token is active.

Per ADR-0053, reopening the single-format/single-validator decision requires its
own ADR. This is it.

## Decision

### 1. JWT is opt-in and keeps the single request-path validator

`Authentication:PortalToken:OAuth2:Jwt:EnableJwtAccessTokens` (default `false`).
When enabled, the OAuth2 token endpoint mints an **HMAC-SHA256-signed JWT** instead
of the opaque token, but the JWT's `jti` **is** the opaque cache reference: the
issuer still records the token record in the distributed cache keyed by that `jti`
(`PortalJwtAccessTokenService` calls `IPortalTokenIssuer.IssueAsync` to mint the
reference, then signs a JWT carrying it).

This preserves the two ADR-0053 §4 invariants that a self-contained JWT would
otherwise break:

- **Revocation stays synchronous.** Evicting the cache entry stops the token, the
  same as for an opaque token — there is no independently-valid stateless token
  living past revocation.
- **One request-path validator.** `PortalTokenAuthenticationHandler` is unchanged;
  it resolves the presented value (or, for a JWT, its `jti`) against the cache. The
  signature is only consulted at the introspection endpoint, which is off the hot
  path.

The signing key (`Jwt:SigningKey`, ≥ 32 bytes, `env:`-resolvable) is required when
JWT issuance is enabled; `Jwt:Issuer`/`Jwt:Audience` are stamped and validated.

### 2. RFC 7662 introspection endpoint

`POST /sharing/rest/oauth2/introspect`, gated by
`Authentication:PortalToken:OAuth2:Jwt:EnableIntrospection` (default `false`) and
**admin-authorized** (RFC 7662 §2.1 requires the endpoint be protected). With the
flag off it returns 404 so it is never a silent surface.

The endpoint accepts either token format: a JWT is verified offline
(signature/issuer/audience/lifetime), its `jti` extracted, and that reference
confirmed live in the cache; an opaque token is looked up directly. A token that is
unknown, expired, revoked (evicted), or a forged/expired JWT returns
`{ "active": false }` and nothing else (RFC 7662 §2.2). An active token returns
`active`, `sub`, `username`, `scope` (the token's roles), `token_type`, and `exp`.

Introspection uses a new `IPortalTokenIssuer.IntrospectAsync` that resolves the
cache record **without** the referer/IP binding check `ValidateAsync` applies: the
introspecting party is a trusted resource server, not the bound client, so binding
cannot match — active/expired/revoked is decided solely by cache presence and
lifetime.

### 3. No behaviour change by default

Both flags default off. With them off the OAuth2 surface issues the same opaque
token it always has, the introspection route 404s, and no signing key is required.
The existing `SharingOAuth2Tests` continue to pass unchanged, which is the
regression guard for "no behaviour change by default".

## Security considerations

- **No revocation gap.** Because the JWT's `jti` is the cache reference, a revoked
  JWT introspects (and validates on the request path) as inactive the instant the
  cache entry is evicted — JWT does not reintroduce the classic "valid signature
  outlives revocation" problem.
- **Symmetric key hygiene.** HMAC-SHA256 with a ≥ 32-byte operator-supplied key,
  `env:`-resolvable so it is not committed. JWT issuance is refused if the key is
  missing/short.
- **Protected introspection.** The endpoint is admin-authorized and off by default;
  an inactive token leaks nothing beyond `active=false`.
- **HTTPS.** Issuance still obeys the existing `RequireHttps` gate.

## Consequences

**Easier:** integrations that want stateless validation or a standards introspection
endpoint get them, without the default deployment changing or gaining attack
surface.

**Harder / explicit trade-offs:** JWT adds signing-key management when enabled, and a
second (offline) verification path — but only at the introspection endpoint, not on
the request hot path, so ADR-0053 §4's single-request-validator property holds.

## Cross-references

- Builds on ADR-0049 (single auth/identity/token foundation) and ADR-0053
  (OAuth2 client-credentials grant; this is its Increment 4).
- Implements parity gap #1890.
