# ADR-0053: OAuth2 client-credentials grant for service-to-service named clients

## Status

Proposed (first increment opt-in / off-by-default; phased rollout below).

## Context

Honua already exposes an ArcGIS-compatible OAuth2 surface at
`/sharing/rest/oauth2/{authorize,callback,token}`. As built (ADR-0049, #1242,
#1484) that surface supports two grants for **interactive named users**:

- `authorization_code` + PKCE — the ArcGIS Pro "Add Portal" / Field Maps
  browser flow, with identity delegated to the operator's configured OIDC
  provider via `PortalOAuthBroker`, and
- `refresh_token` — long-lived clients re-mint an access token without a new
  login (90-day, cache-backed, rotated by default).

Parity gap **#1860** asks for full OAuth2 *named-user* authentication. The
interactive half is shipped; the remaining standard OAuth2 grant that real
deployments need — and that ArcGIS clients and downstream automation expect on
this endpoint — is **`client_credentials`**: a *service* (a CI job, an ETL
worker, a backend integration) authenticating *as itself* with no interactive
user and no browser. Today `oauth2/token` explicitly rejects it with
`unsupported_grant_type` (see `PortalOAuthTokenService.ExchangeAsync` and
`SharingOAuth2Tests.Token_UnsupportedGrantType_ReturnsError`).

This ADR records the design for the whole OAuth2 named-user/named-client picture
so the increments compose, and lands with the first conservative increment: the
`client_credentials` grant, gated off by default.

### Constraints that shape the decision

- **ADR-0049 is binding.** There is exactly one auth/identity/token foundation.
  No new user store, no second token store, no parallel group→role mapping. Any
  new grant must mint through `IPortalTokenIssuer` and authenticate against an
  identity that already exists in-tree.
- **Backward compatibility is non-negotiable.** Existing deployments use
  `generateToken` opaque portal tokens, `X-API-Key` API keys, OIDC bearer
  sign-in, and the interactive OAuth2 bridge. None of those paths may change
  behaviour. The new grant must be **opt-in and off by default**.
- **AOT-conscious, Minimal APIs, vertical-slice.** Source-generated JSON, no
  reflection-heavy token libraries on the hot path.

## Decision

### 1. Grant types (target state)

| Grant | Client | Status |
|---|---|---|
| `authorization_code` + PKCE | Interactive named users (ArcGIS Pro, Field Maps, SPAs) | **Shipped** (#1242/#1484) |
| `refresh_token` (rotating) | Long-lived interactive clients | **Shipped** (#1242/#1484) |
| `client_credentials` | Service-to-service / named clients (no user) | **This ADR — first increment** |

No other grant types (implicit, ROPC) are offered: implicit is deprecated by
OAuth 2.1, and ROPC's username/password role is already covered by
`generateToken`.

### 2. `client_credentials` — identity is an existing credential, not a new store

A `client_credentials` request presents `grant_type=client_credentials`,
`client_id`, and `client_secret`. Per ADR-0049 we do **not** introduce a new
client-secret store. Instead the `client_secret` is validated against the
**existing Admin API-key store** (`IAdminApiKeyStore.ValidateAsync`) — the same
durable, hashed, rotatable, revocable, expiry-aware credential that
`X-API-Key` automation already uses. The `client_id` is treated as a
human-readable label and bound to the resolved key record's name (it is not a
second secret; the secret is the only thing that authenticates).

This means:

- A service client is provisioned exactly as today: `POST /api/v1/admin/api-keys`
  mints a key. That key *is* the `client_secret`. No new admin surface, no new
  migration, no new table for the first increment.
- Issued access tokens are the same opaque, cache-backed portal tokens
  `generateToken` mints (via `IPortalTokenIssuer`), so they validate on
  `/rest/services/*` through the unchanged `PortalTokenAuthenticationHandler`.
- Revoking/rotating the API key immediately stops new token issuance; live
  tokens expire on their short TTL (revocation is cache-eviction, as today).

The token is **IP-bound** (`PortalTokenClientType.Ip`), not referer-bound:
service clients have no browser referer, and IP binding matches how the
existing API-key-equivalent automation reaches the request path. No refresh
token is issued for `client_credentials` (RFC 6749 §4.4.3 — the client just
re-requests with its credentials), avoiding a long-lived secondary credential.

### 3. Mapping to ArcGIS client expectations

ArcGIS clients hit `POST /sharing/rest/oauth2/token` with
`grant_type=client_credentials` and `client_id`/`client_secret` and expect the
Esri envelope `{ access_token, expires_in, token_type }`. We return exactly that
(no `refresh_token`, consistent with the grant). `expires_in` is in **seconds**
(the oauth2/token convention) — unchanged from the existing bridge responses.
The endpoint already accepts both form-POST and query-string, so ArcGIS
"App login" registered-application flows work unmodified.

### 4. Token format: opaque + server-side validation (unchanged)

We keep the existing **opaque, cache-backed** token format, not JWT. Rationale:
the whole foundation (ADR-0049) is opaque-token + cache-eviction revocation;
introducing a JWT here would create a second token format and a second
validation path on the request hot-path, violating ADR-0049 and adding key
management we do not need. If a future requirement demands stateless
introspection, that is a separate ADR (see "Deferred"). For now revocation is
synchronous (evict the cache entry) and there is one validator.

### 5. Authorization / scopes

The first increment issues a token carrying the **roles already attached to the
API-key record** projected onto `PortalCredentialPrincipal.Roles`, so the
existing RBAC resolver (#1375) makes the per-operation decision exactly as for
any other principal. A `scope` request parameter is accepted but, in the first
increment, may only **narrow** to roles the key already holds (a request for a
scope the key lacks is ignored, never escalated). Full OAuth2 scope→permission
mapping is deferred (below).

### 6. Coexistence with existing flows (the safety property)

- The grant is gated behind a new flag
  `Authentication:PortalToken:OAuth2:EnableClientCredentials`
  (`PortalOAuth2Options.EnableClientCredentials`), **default `false`**.
- With the flag off, `oauth2/token` returns `unsupported_grant_type` for
  `client_credentials` — **byte-for-byte the current behaviour**. The existing
  `SharingOAuth2Tests.Token_UnsupportedGrantType_ReturnsError` test continues to
  pass unchanged, which is the regression guard for "no behaviour change by
  default".
- API keys, `generateToken`, OIDC, and the interactive OAuth2 grants are
  untouched.

## Security considerations

- **Secret = API key.** Reuses the hardened API-key path: SHA-256 hashed at
  rest, `CryptographicOperations.FixedTimeEquals` comparison, expiry and
  revocation honoured. No new secret-at-rest surface.
- **HTTPS-only.** Issuance obeys the existing `RequireHttps` gate, so the
  `client_secret` is never accepted over plaintext in production.
- **No privilege escalation.** Roles come from the key record; `scope` can only
  narrow. A key with no roles yields a token with no roles.
- **Short TTL, no refresh.** `client_credentials` tokens get the standard
  clamped portal-token TTL and no refresh token, bounding leaked-token blast
  radius; the client re-authenticates with its own secret to renew.
- **IP binding** limits a captured token to the issuing client's address path.
- **Uniform error shape.** Invalid credentials return RFC 6749 §5.2
  `invalid_client` (HTTP 400) with no detail that distinguishes
  "unknown client" from "bad secret".
- **Off by default** means a deployment that has not consciously enabled
  service-to-service OAuth2 has zero new attack surface.

## Phased rollout

1. **Increment 1 (this PR):** `client_credentials` over the existing API-key
   store, opaque token, off by default. Tests prove issuance, validation on the
   request path, credential rejection, and flag-off = unchanged behaviour.
2. **Increment 2 (follow-up):** first-class OAuth2 *client registration* admin
   surface (`client_id`/`client_secret` pairs distinct from human API keys),
   client-type metadata, and `scope` catalogue → permission mapping.
3. **Increment 3 (follow-up):** optional pluggable IdP/OIDC federation for
   `client_credentials` (delegate to an external token endpoint /
   introspection) for operators who centralise machine identity in their IdP.
4. **Increment 4 (follow-up, only if required):** optional JWT access-token
   format + RFC 7662 introspection endpoint for stateless validation, behind its
   own ADR (it reopens the single-validator decision).

## Consequences

**Easier:** closes the standard service-to-service OAuth2 gap with no new store
and no new behaviour by default; ArcGIS "App login" works; one token format and
one validator preserved.

**Harder / explicit trade-offs:** `client_id` is a label, not an independent
credential, until Increment 2 ships a real client registry — adequate for the
machine-credential use case but not a substitute for per-application client
identities. Documented here so it is an accepted scope boundary, not a surprise.

## Cross-references

- Builds on ADR-0049 (single auth/identity/token foundation) — composes the same
  `IPortalTokenIssuer`, no parallel store.
- Implements parity gap #1860 (OAuth2 named-user authentication), first
  increment.
- Follow-ups (filed): #1888 (Increment 2 — first-class client registration +
  scope catalogue), #1889 (Increment 3 — pluggable IdP/OIDC federation for
  client credentials), #1890 (Increment 4 — optional JWT access tokens +
  RFC 7662 introspection, behind its own ADR).
