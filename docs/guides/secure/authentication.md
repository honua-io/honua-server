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

```bash
BASE=http://localhost:8080
curl -X POST "$BASE/api/v1/admin/api-keys" \
  -H "X-API-Key: $HONUA_ADMIN_PASSWORD" -H "Content-Type: application/json" \
  -d '{"name":"ci-publisher","permissions":[],"expiresAt":null}'
```

The response's `data.key` is shown once — store it immediately. Manage the lifecycle with `POST /api/v1/admin/api-keys/{id}/rotate` (returns a new secret), `POST .../{id}/revoke`, and `GET .../{id}/effective-permissions`.

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

```bash
curl -X POST "$BASE/sharing/rest/generateToken" \
  -d "username=admin&password=$ADMIN_PASSWORD&client=referer&referer=https://app.example.com&expiration=60&f=json"
```

The response is `{ "token": "...", "expires": ..., "ssl": true }`. Tokens are opaque, cached server-side (Redis when enabled), and bound to the supplied referer or client IP — a mismatched binding fails validation. Use them on `/rest/services/...` via `?token=`, `Authorization: Bearer`, or `X-Esri-Authorization: Bearer`. Issuance is HTTPS-only by default; expiry is clamped to `Authentication__PortalToken__MaxExpirationMinutes` (default 14400). An opt-in OAuth2 bridge (`/sharing/rest/oauth2/*`) brokers named-user sign-in to your OIDC provider — register every redirect URI in `Authentication__PortalToken__OAuth2__AllowedRedirectUris` before enabling it.

For non-interactive service-to-service clients, an opt-in OAuth2 `client_credentials` grant (off by default; ADR-0053) exchanges an existing API key for an OAuth2 access token. Enable it with `Authentication__PortalToken__OAuth2__EnableClientCredentials=true`, then `POST /sharing/rest/oauth2/token` with `grant_type=client_credentials`, `client_id=<key-name>`, and `client_secret=<api-key>` (or HTTP Basic). The returned `access_token` is the same opaque, IP-bound portal token, carries the API key's permissions, and has no refresh token (the client re-requests with its secret). With the flag off the grant is rejected with `unsupported_grant_type` — no behaviour change for existing deployments.

### 5. Choose per client

| Client | Use |
|---|---|
| CI, scripts, server-to-server | Scoped API key in `X-API-Key` |
| Browser console / admin UI users | OIDC bearer sign-in |
| ArcGIS Pro, Esri SDKs, legacy Esri apps | `generateToken` portal tokens |
| Native operator clients in locked-down environments | Client certificates — [TLS and mTLS](tls-and-mtls.md) |

A legacy Basic compatibility mode (`HONUA_ENABLE_BASIC_AUTH_COMPAT=true`, with `HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH=true` enforced in production) maps the Basic password to the admin API key; use it only during migrations.

## Verify

```bash
curl -H "X-API-Key: $HONUA_ADMIN_PASSWORD" "$BASE/api/v1/admin/version"
```

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
