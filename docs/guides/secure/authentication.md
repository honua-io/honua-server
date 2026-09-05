# Authenticate clients

Set up the three ways callers prove who they are: API keys for automation and the admin API, OIDC for browser and console sign-in, and ArcGIS-compatible portal tokens for Esri clients.

**Prerequisites:** A running Honua server and the ability to set its environment variables. For what authenticated callers are then *allowed* to do, see [Control access](access-control.md); for client-certificate (mTLS) authentication, see [TLS and mTLS](tls-and-mtls.md).

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

An empty permissions array is normalized to `admin:*` for legacy compatibility and
therefore grants full admin access; do not use it for CI. Grant only the operations
the job needs, and prefer a narrower service or layer grant when available.

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
`import/geoservices/start`. This scope ceiling is enforced in both
authentication modes: enabling OIDC (`Oidc:Enabled=true`) rebuilds the admin
policies for composite sign-in but preserves scoped API-key permission
enforcement. Console users who sign in with an operator bearer
token are authorized by operator RBAC instead of this API-key recipe.

Approval and execution remain separate authorities. The Console sends the
read/approve key only to the proposal decision endpoint. After approval, Honua
mints a short-lived, single-use credential bound to the exact approved Admin API
method and path, uses it for the replay, and revokes it immediately. The
approver's key and identity headers are never forwarded as execution authority;
`admin:operation:*` grants are server-reserved and cannot be requested through
the API-key creation endpoint.

### 3. Enable OIDC for browser and admin sign-in

```bash
Oidc__Enabled=true
Oidc__Generic__Enabled=true
Oidc__Generic__Authority=https://idp.example.com/realms/honua
Oidc__Generic__ClientId=honua-admin
Oidc__Generic__ClientSecret=$OIDC_CLIENT_SECRET
```

Provider blocks exist for `Generic`, `AzureAd`, `Google`, `Okta`, and `Auth0`; OIDC providers can also be managed at runtime via `/api/v1/admin/oidc/providers`. When OIDC is enabled, `Authorization: Bearer` tokens are evaluated before API keys. Map your IdP's admin group to a role in `Oidc__AdminRoles` (default `admin`, `administrator`) — without it, signed-in users hold no admin rights.

### 4. Issue ArcGIS-compatible tokens

Esri clients (ArcGIS Pro, Maps SDKs) authenticate against the portal token endpoint, which is always on:

Use `PortalCompat.generateToken` from `@honua/sdk-js/esri-compat` with `username`, `password`, `client: "referer"`, `referer: "https://app.example.com"`, and `expiration: 60`. ArcGIS Pro and other Esri clients discover the same endpoint automatically when they prompt for credentials.

The response is `{ "token": "...", "expires": ..., "ssl": true }`. Tokens are opaque, cached server-side (Redis when enabled), and bound either to the supplied referer (`client=referer`, the default) or to the request's client IP (`client=ip` or `client=requestip`, the Esri SDK default for IP-bound tokens) — a mismatched binding fails validation. Use them on `/rest/services/...` via `?token=`, `Authorization: Bearer`, `X-Esri-Authorization: Bearer`, or a form-encoded POST `token` field. Issuance is HTTPS-only by default; expiry is clamped to `Authentication__PortalToken__MaxExpirationMinutes` (default 14400). An opt-in OAuth2 bridge (`/sharing/rest/oauth2/*`) brokers named-user sign-in to your OIDC provider — register every redirect URI in `Authentication__PortalToken__OAuth2__AllowedRedirectUris` before enabling it.

For non-interactive service-to-service clients, an opt-in OAuth2 `client_credentials` grant (off by default; ADR-0053) exchanges an existing API key for an OAuth2 access token. Enable it with `Authentication__PortalToken__OAuth2__EnableClientCredentials=true`, then `POST /sharing/rest/oauth2/token` with `grant_type=client_credentials`, `client_id=<key-name>`, and `client_secret=<api-key>` (or HTTP Basic). The returned `access_token` is the same opaque, IP-bound portal token, carries the API key's permissions, and has no refresh token (the client re-requests with its secret). With the flag off the grant is rejected with `unsupported_grant_type` — no behaviour change for existing deployments.

For a true per-application client identity (rather than reusing a human API key), register a first-class OAuth2 client (ADR-0053 Increment 2). Define your scopes once — `PUT /api/v1/admin/oauth-scopes` with `{ "scope": "features:read", "permissions": ["services:read"] }` — then register the client with `POST /api/v1/admin/oauth-clients` (`name`, `clientType` `confidential`|`public`, `allowedGrantTypes`, `allowedScopes`). The response's `data.clientSecret` is shown once; the stored secret is SHA-256-hashed (never plaintext). Authenticate with the returned `client_id`/`client_secret` at `/sharing/rest/oauth2/token`; the requested `scope` is narrowed to the client's allowed scopes, mapped to permissions via the catalogue, and echoed in the response. Manage clients with `GET /api/v1/admin/oauth-clients`, `GET`/`DELETE .../{id}`. The token endpoint matches a first-class client first and falls back to the API-key path, so both styles coexist.

### 5. Choose per client

| Client | Use |
|---|---|
| CI, scripts, server-to-server | Scoped API key in `X-API-Key` |
| Browser console / admin UI users | OIDC bearer sign-in |
| ArcGIS Pro, Esri SDKs, legacy Esri apps | `generateToken` portal tokens |
| Native operator clients in locked-down environments | Client certificates — [TLS and mTLS](tls-and-mtls.md) |

A legacy Basic compatibility mode (`HONUA_ENABLE_BASIC_AUTH_COMPAT=true`, with `HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH=true` enforced in production) maps the Basic password to the admin API key; use it only during migrations.

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
| OIDC user signed in but gets `403` on admin routes | The IdP token lacks a role matching `Oidc__AdminRoles`; fix the claims mapping in your IdP. |

More general failures: [Troubleshooting](../deploy/troubleshooting.md).

## Next steps

- [Control access to services and layers](access-control.md)
- [Production security checklist](production-checklist.md)
- [TLS and mTLS](tls-and-mtls.md)
