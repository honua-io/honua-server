---
type: guide
title: "Authenticate clients"
description: "Set up the three ways callers prove who they are: API keys for automation and the admin API, OIDC for browser and console sign-in, and ArcGIS-compatible portal tokens for Esri clients."
resource: "honua://capability/identity.oidc"
resources:
  - "honua://capability/identity.oidc-multi-provider"
---
# Authenticate clients

Set up the three ways callers prove who they are: API keys for automation and the admin API, OIDC for browser and console sign-in, and ArcGIS-compatible portal tokens for Esri clients.

**Prerequisites:** A running Honua server and the ability to set its environment variables. For what authenticated callers are then *allowed* to do, see [Control access](access-control.md); for client-certificate (mTLS) authentication, see [TLS and mTLS](tls-and-mtls.md).

## Client response caching

Authenticated HTTP responses use `Cache-Control: no-store`. HTTP 401/403 responses
and equivalent protocol-formatted errors use the same policy, including Esri
498/499 errors carried as HTTP 200. This prevents compliant HTTP caches from
reusing an earlier response after credentials change. Public anonymous responses
retain their normal endpoint cache policy.

An endpoint may retain `private` and a freshness lifetime alongside `no-store`;
`no-store` takes precedence and prohibits storing the response. `Vary` and ETags
alone cannot protect revocation: a revoked credential still has the same bytes,
and a private cache could otherwise reuse the asset without contacting Honua.

Signed scene-access envelopes also prohibit response storage, including
conditional `304` responses, even though their endpoint-local validation does
not attach a user principal. Public scene requests retain their normal caching.

Portal `generateToken` and OAuth endpoints also return `Cache-Control: no-store`
and `Pragma: no-cache`, including token exchange errors. Their credentials can be
validated inside the endpoint without attaching an authenticated user to the
request, so these routes explicitly prohibit response storage.

An upgrade cannot invalidate responses that a client already cached or data it
downloaded for offline use. Clear a pre-upgrade HTTP cache before verifying the
new behavior, then test login, revocation and anonymous access without clearing
the cache between credential changes.

## Steps

### 1. Set the admin API key

```bash
HONUA_ADMIN_PASSWORD=$ADMIN_PASSWORD
```

This is the root credential for `/api/v1/admin/*` (and the JSON metrics/monitoring endpoints). Clients send it in the `X-API-Key` header; failures return `WWW-Authenticate: ApiKey`. Resolve it from a secret manager and rotate it regularly.

### 2. Create scoped API keys for automation

Don't share the admin password with CI jobs — mint named keys instead:

In the authorized [API explorer](../../reference/openapi-and-explorer.md), run `POST /api/v1/admin/api-keys` with this body:

```json
{
  "name": "ci-publisher",
  "permissions": ["admin:write"],
  "expiresAt": null
}
```

The response's `data.key` is shown once — store it immediately. Manage the lifecycle with `POST /api/v1/admin/api-keys/{id}/rotate` (returns a new secret), `POST .../{id}/revoke`, and `GET .../{id}/effective-permissions`.

Creating, rotating and revoking keys, changing user roles, deprovisioning users and
registering or deleting OAuth clients require full-administrator credential authority:
a key granted `admin`, `*` or `admin:*`, the bootstrap credential, an administrator
session, or an approved operation. An `admin:write` key such as `ci-publisher` above
gets `403` on those routes. Keep credential management out of CI keys; the route list
is in [Credential and identity management](../../reference/admin-api/users-roles-licensing.md#credential-and-identity-management).

Credential expiry denies authentication while preserving the managed key's
metadata, so administrators can still inspect its expired or revoked status.
Redis retains this metadata for 3,650 days after expiry, or after the latest
registry write when there is no future expiry. Internal approval replay
credentials remain short-lived and are removed at their credential expiry.

After upgrading an existing Redis registry, older records retain their original
Redis expiry until they are rewritten by successful authentication, rotation, or
revocation. Rotate or revoke these keys before their original expiry to retain
their metadata. Records already removed by Redis cannot be recovered by this
change; create a replacement key if access is still needed.

An empty permissions array is normalized to `admin:*` for legacy compatibility and
therefore grants full admin access; do not use it for CI. Grant only the operations
the job needs, and prefer a narrower service or layer grant when available.

Data access for a key without a full-admin grant comes only from `read:` grants:
`read:{service}/{layer}` reads one layer, `read:{service}` or `read:{service}/*`
reads every layer of a service, and `read:*` reads every service. `admin:read` and
`admin:approve` on their own grant no service or layer reads. Wildcards are
accepted only for `read:`; `write:` grants must name their service. Access
policies, field masks and row-level filters apply to wildcard reads exactly as to
a named `read:` grant. The full grammar is in
[Users, roles and licensing](../../reference/admin-api/users-roles-licensing.md#grant-grammar).

For the focused Console read/approve client, mint a named key with:

```json
{
  "name": "console-read-approve",
  "permissions": ["admin:read", "admin:approve"],
  "expiresAt": null
}
```

`admin:approve` remains read-level on the general admin surface and adds only
`POST /api/v1/admin/proposals/{proposalId}/approve` and
`POST /api/v1/admin/proposals/{proposalId}/reject`. It does not grant other
mutations. In particular, some read-like workflows use POST and are unavailable
to this key: `connections/test`, `external-services/discover`, and
`import/geoservices/start`. It also grants no service or layer data reads; add
`read:{service}` or `read:*` grants if the client must read data. This scope
ceiling is enforced in both
authentication modes: enabling OIDC (`Oidc:Enabled=true`) rebuilds the admin
policies for composite sign-in but preserves scoped API-key permission
enforcement. Console users who sign in with an operator bearer
token are authorized by operator RBAC instead of this API-key recipe.

Approval and execution remain separate authorities. The Console sends the
read/approve key only to the proposal decision endpoint. After approval, Honua
mints a short-lived, single-use credential bound to the exact approved Admin API
method, path, and sealed tenant, uses it for the replay, and revokes it immediately.
The tenant binding is restored from the persisted credential during authentication
and survives OIDC claim sanitization, including repeated transformation. An
issuer-supplied copy of the internal tenant claim is removed; a caller-supplied
tenant header cannot change the binding. Credentials without an explicit
tenant binding are rejected. An explicit empty binding preserves an invocation
accepted with tenant resolution disabled. The
approver's key and identity headers are never forwarded as execution authority;
`admin:operation:*` grants are server-reserved and cannot be requested through
the API-key creation endpoint. Internal replay credentials are excluded from
public key listings and permission inspection, and cannot be rotated into a
new plaintext credential.

Proposal lists, proposal reads and decisions, and operation-handle reads are
restricted to the tenant recorded when the operation was accepted. A record
owned by another tenant is omitted from lists and returns `404` on direct
access. MCP proposal resources use the same ownership boundary.
Legacy records without a tenant owner are hidden from tenant-scoped callers;
a platform operator can inspect those records and re-propose pending work in
its intended tenant. Idempotency keys are scoped to the accepted tenant.

### 3. Enable OIDC for browser and admin sign-in

```bash
Oidc__Enabled=true
Oidc__Generic__Enabled=true
Oidc__Generic__Authority=https://idp.example.com/realms/honua
Oidc__Generic__ClientId=honua-admin
Oidc__Generic__ClientSecret=$OIDC_CLIENT_SECRET
```

For a separately hosted Console, pin the backend-assisted admin broker's callback:

```bash
Oidc__AdminAuthCallbackUri=https://console.example.com/admin/auth/callback
```

Keep `Public__BaseUrl` at the public API origin. The optional callback must be an
absolute HTTPS URI with the exact `/admin/auth/callback` path and no user information,
query or fragment. Register that same URI on the OIDC app client and route it to
Console's authenticated BFF. The broker uses this deployment setting for both
creation of the authorization URL and authorization-code exchange; request bodies
cannot choose a redirect. Without the setting, the existing `Public:BaseUrl` plus
`/admin/auth/callback` behavior is retained, including an existing public path prefix.
This setting does not alter API links, ASP.NET OIDC middleware callback paths, or
post-logout URLs. State, pending-session cookies and PKCE remain required.

Cognito logout requires separate deployment integration: Console's current logout
clears its local/operator sessions and upstream server cookie jar, but does not
clear an edge proxy's session or navigate a Cognito logout endpoint. The Server
broker's discovery-based logout uses `post_logout_redirect_uri` ending in `/admin`;
Cognito's managed logout requires its documented `client_id` and `logout_uri`
parameters. Configuring this callback alone does not establish global sign-out or
revoke an edge/Cognito login session.

Explicit `Oidc:AdminRoles` and `Oidc:Generic:Scopes` arrays replace their options
constructor defaults. Configuration providers still merge indexed keys: when overriding
an appsettings array through environment variables, override every existing index
(for the two default admin roles, both `Oidc__AdminRoles__0` and
`Oidc__AdminRoles__1`). If the arrays are omitted, their defaults remain. This controls
configured claim normalization; existing shared authorization policies also accept
legacy admin aliases and approved-operation/API-key roles. Replacing these options
does not establish an approver-only policy for all authentication schemes.

Provider blocks exist for `Generic`, `AzureAd`, `Google`, `Okta`, and `Auth0`; OIDC providers can also be managed at runtime via `/api/v1/admin/oidc/providers`. When OIDC is enabled, `Authorization: Bearer` tokens are evaluated before API keys. Map your IdP's admin group to a role in `Oidc__AdminRoles` (default `admin`, `administrator`) — without it, signed-in users hold no admin rights.

An explicitly configured static JWT issuer may set
`Oidc__TokenValidation__SymmetricSigningKey` to an `env:NAME` or
`aws:secretsmanager:<secret-id>` reference. The server resolves it before binding
bearer validation options and refuses startup if the reference cannot resolve or
returns an empty value. The resolved key must contain at least 32 UTF-8 bytes.
Issuer, audience, signature and lifetime validation remain enabled. This key is
a process-lifetime snapshot; restart the deployment when rotating it. Keep the
key value in the secret store, outside Terraform configuration and release
evidence. Deployments without a static key continue using provider metadata and
JWKS discovery.

### 4. Issue ArcGIS-compatible tokens

Esri clients (ArcGIS Pro, Maps SDKs) authenticate against the portal token endpoint, which is always on:

Use `PortalCompat.generateToken` from `@honua/sdk-js/esri-compat` with `username`, `password`, `client: "referer"`, `referer: "https://app.example.com"`, and `expiration: 60`. ArcGIS Pro and other Esri clients discover the same endpoint automatically when they prompt for credentials.

The local admin credential bridge accepts the bootstrap admin password and managed
API keys that already confer full administration. Service/layer keys, narrow admin
or operations grants, and approved-operation replay credentials cannot be exchanged
through this admin bridge: issuance returns the Esri `400` "Unable to generate
token" error. Continue using those keys through their supported API-key transport;
configured OIDC identities use the separate identity-provider bridge. A refused
exchange is not a successful scoped Portal-token workflow.

When upgrading the bridge, address previously issued tokens as well. The fix
prevents new privilege elevation but does not change existing cached token records;
they remain valid until revoked or expired. Revoke tokens that may have been issued
from constrained keys and require affected clients to authenticate again. Upgrade
all token-issuance nodes before resuming those exchanges.

The response is `{ "token": "...", "expires": ..., "ssl": true }`. Tokens are opaque, cached server-side (Redis when enabled), and bound either to the supplied referer (`client=referer`, the default) or to the request's client IP (`client=ip` or `client=requestip`, the Esri SDK default for IP-bound tokens) — a mismatched binding fails validation. Use them on `/rest/services/...` via `?token=`, `Authorization: Bearer`, `X-Esri-Authorization: Bearer`, or a form-encoded POST `token` field. An API key is accepted on the same transports, on the GeoServices surfaces and on every other surface that honours the `token` query parameter (OGC services included), so a client that sends a key as its `token` works without custom headers. The key keeps its own scope, exactly as in `X-API-Key`, and is recorded as an API-key credential; the Admin API still requires `X-API-Key`. Issuance is HTTPS-only by default; expiry is clamped to `Authentication__PortalToken__MaxExpirationMinutes` (default 14400) **and to the expiry of the credential the token was issued from**, so a key that expires in five minutes never yields a ten-day token. The issued token is also tied to that credential for its whole life: revoking or rotating the API key it came from, or rotating the admin password, stops it on the next request without needing the token value. The credential is re-read at most once per `Authentication__PortalToken__SourceRevalidationSeconds` (default 10) per instance, which is also the worst-case delay before a revocation takes effect; set it to `0` to re-read on every request. Tokens issued before an upgrade to this behaviour carry no credential reference and are refused — affected clients simply call `generateToken` again. An opt-in OAuth2 bridge (`/sharing/rest/oauth2/*`) brokers named-user sign-in to your OIDC provider. It needs `Oidc__Enabled=true`, a valid provider (`Oidc__Generic__Enabled`, `Oidc__Generic__Authority`, `Oidc__Generic__ClientId`, and that provider's client secret), and every client redirect registered in `Authentication__PortalToken__OAuth2__AllowedRedirectUris`. The callback validates the IdP token with the OIDC verifier whenever that bridge is configured. `Authentication__PortalCredentialVerifier__UseOidc` is not required for the bridge; it still selects OIDC for `generateToken` password checks. After the redirect URI has been accepted, a later authorization failure is returned to it as `error` and `state`, not as a JSON body on the callback.

Named-user OAuth authorization-code tokens for registered native redirects (loopback
HTTP URLs or custom app schemes) work with `Authorization: Bearer` without a
`Referer` header. PKCE and the exact registered redirect still protect code
redemption. Refreshes preserve this bearer mode, including refresh-token rotation.
Browser redirects retain their original referer binding across refreshes. Existing
refresh tokens created before this behavior retain their legacy binding; native
clients should sign in again after upgrading. A `generateToken` token issued with
`client=referer` is refused when a later request carries a different `Referer`, and
accepted when that request omits the header. A browser OAuth token bound to a
redirect origin still requires the matching `Referer`. `client=ip` still requires
the issuing address.

For non-interactive service-to-service clients, an opt-in OAuth2 `client_credentials` grant (off by default) exchanges an existing API key for an OAuth2 access token. Enable it with `Authentication__PortalToken__OAuth2__EnableClientCredentials=true`, then `POST /sharing/rest/oauth2/token` with `grant_type=client_credentials`, `client_id=<key-name>`, and `client_secret=<api-key>` (or HTTP Basic). The returned `access_token` is the same opaque, IP-bound portal token and has no refresh token (the client re-requests with its secret). With the flag off the grant is rejected with `unsupported_grant_type` — no behaviour change for existing deployments.

The API-key exchange follows the same rule as the admin credential bridge above. A portal token holds roles only, so only a key that confers full administration can be represented; its token carries the `admin` role. Every other key is refused with `unauthorized_client`. That covers service/layer keys, narrow admin or operations grants, approved-operation replay credentials, and keys with any other permission label. A permission label never becomes a token role. Use constrained keys through `X-API-Key`, or register a first-class OAuth2 client (below) for scoped per-application tokens. The upgrade note above applies to tokens previously issued through this grant as well.

For a true per-application client identity (rather than reusing a human API key), register a first-class OAuth2 client. Define your scopes once — `PUT /api/v1/admin/oauth-scopes` with `{ "scope": "features:read", "permissions": ["services:read"] }` — then register the client with `POST /api/v1/admin/oauth-clients` (`name`, `clientType` `confidential`|`public`, `allowedGrantTypes`, `allowedScopes`). The response's `data.clientSecret` is shown once; the stored secret is SHA-256-hashed (never plaintext). Authenticate with the returned `client_id`/`client_secret` at `/sharing/rest/oauth2/token`; the requested `scope` is narrowed to the client's allowed scopes, mapped to permissions via the catalogue, and echoed in the response. Manage clients with `GET /api/v1/admin/oauth-clients`, `GET`/`DELETE .../{id}`. The token endpoint matches a first-class client first and falls back to the API-key path, so both styles coexist.

### 5. Choose per client

| Client | Use |
|---|---|
| CI, scripts, server-to-server | Scoped API key in `X-API-Key` |
| Browser console / admin UI users | OIDC bearer sign-in |
| ArcGIS Pro, Esri SDKs, legacy Esri apps | `generateToken` portal tokens |
| Native operator clients in locked-down environments | Client certificates — [TLS and mTLS](tls-and-mtls.md) |

A legacy Basic compatibility mode (`HONUA_ENABLE_BASIC_AUTH_COMPAT=true`, with `HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH=true` enforced in production) maps the Basic password to the admin API key; use it only during migrations.

### SensorThings observation streams (Preview)

`GET /sta/v1.1/ObservationsStream` requires authentication before either an SSE
handshake or a WebSocket upgrade. Use the configured authentication scheme (for
example, `X-API-Key` for an API client). Anonymous requests receive `401`; enabling
anonymous SensorThings writes does not enable anonymous streaming.

A subscription receives observations only for its resolved tenant and database
schema. Omitting `datastreamId` selects all datastreams within that boundary.
Tenant overrides follow the shared tenant middleware's administrator rules; a
datastream ID is never a cross-tenant identifier.

Concurrent subscriptions are admitted against three nested caps under
`SensorThings:Streaming`, so no single credential or tenant can hold every slot on
a node:

| Setting | Default | Refusal |
|---|---:|---|
| `MaxSessionsPerPrincipal` | 8 | `429` with `Retry-After` |
| `MaxSessionsPerTenant` | 64 | `429` with `Retry-After` |
| `MaxConcurrentSessions` (per node) | 256 | `503` with `Retry-After` |

A per-tenant cap at or above the node cap, or a per-principal cap above the
per-tenant cap, fails startup validation. `RetryAfterSeconds` (default 30) sets the
hint returned with a refusal. A refused request never opens a stream: the problem
response is returned before the SSE headers or the WebSocket upgrade.

Upgrade every observation-stream node together. Scoped fan-out uses a new Redis
channel, so old and updated nodes do not exchange observations during a rolling
upgrade. Reconnect clients to updated nodes; streams remain best-effort, with no
replay guarantee. Disable the old stream endpoints until their nodes are updated.

## Verify

Run `GET /api/v1/admin/version` in the authorized explorer.

```json
{ "success": true, "data": { "version": "..." } }
```

A wrong or missing key returns `401` with a `WWW-Authenticate: ApiKey` challenge.

## Troubleshoot

| Symptom | Fix |
|---|---|
| `401` on admin endpoints | `HONUA_ADMIN_PASSWORD` unset or mismatched; confirm the `X-API-Key` header is present and the process restarted after the change. |
| `generateToken` refuses to issue | Issuance is HTTPS-only by default — front the server with TLS (see [TLS and mTLS](tls-and-mtls.md)). |
| Token works from one host but not another | Portal tokens are bound to `client=referer` or `client=ip`; re-issue with the binding the caller actually presents. |
| `admin:write` or `admin:manage` key gets `403` creating, rotating or revoking API keys, changing user roles, deprovisioning users or managing OAuth clients | Expected: these routes need full-administrator authority. Use a key granted `admin`, `*` or `admin:*`, the bootstrap credential, an administrator session, or submit the change as a proposal for approval. See [Credential and identity management](../../reference/admin-api/users-roles-licensing.md#credential-and-identity-management). |
| OIDC user signed in but gets `403` on admin routes | The IdP token lacks a role matching `Oidc__AdminRoles`; fix the claims mapping in your IdP. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Control access to services and layers](access-control.md)
- [Production security checklist](production-checklist.md)
- [TLS and mTLS](tls-and-mtls.md)
