# Security Configuration

This guide covers authentication, authorization, edge security, and related operational settings for Honua Server.

**MVP deferrals**:
- No application-level rate limiting (enforce at the edge).
- No secure-connection allowlist or connection audit trail.

---

## Authentication

**Admin API**: secured with an API key in the `X-API-Key` header.
- Set `HONUA_ADMIN_PASSWORD` in your secret manager.

**Basic compatibility mode** (optional): accept `Authorization: Basic ...` and map the password to the admin API key.
- Enable with `HONUA_ENABLE_BASIC_AUTH_COMPAT=true` (or `Authentication__BasicCompatibility__Enabled=true`).
- Keep `HONUA_REQUIRE_HTTPS_FOR_BASIC_AUTH=true` in production.
- Intended only for legacy client compatibility during migration.

**OIDC**: optional for the Admin UI and token-based access.
- Configure `Oidc__Enabled` and a provider block (`Oidc__Generic`, `Oidc__AzureAd`, or `Oidc__Google`).
- Verify claims mapping and admin roles in your IdP.
- Optional static signing-key mode for controlled environments/tests:
- `Oidc__TokenValidation__SymmetricSigningKey=<shared-secret>`

### Development authentication bypass (Test only)

`HONUA_DEV_AUTH=true` exists exclusively so the in-process integration test
host can short-circuit admin authentication. It is **not** a "non-production"
shortcut: Staging, QA, Preview, and Development environments all enforce
API-key authentication regardless of how `HONUA_DEV_AUTH` is set.

To activate the bypass, **every** condition below must hold:

| Condition | Required value |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | exactly `Test` (case-insensitive) |
| `HONUA_DEV_AUTH` | `true` |
| `HONUA_DEV_AUTH_ALLOW_BYPASS` | `true` (operator acknowledgement) |

If any condition is unmet the bypass is silently inactive and authentication
proceeds as normal. The two-flag design is intentional: an accidentally
leaked `HONUA_DEV_AUTH=true` (for example, copied from a CI lane into a
Staging deployment) cannot, on its own, disable authentication.

**Operational signals**

- When the bypass *is* active you will see warning event `4112` at startup:
  *"SECURITY WARNING: HONUA_DEV_AUTH bypass is ACTIVE for environment 'Test'."*
  If you see this warning anywhere other than a disposable Test environment,
  unset both flags immediately.
- When `HONUA_DEV_AUTH=true` is configured but the bypass is rejected (for
  example because the environment is Staging or the ack flag is missing) the
  server logs warning event `4113` so the misconfiguration is visible.
- Production deployments additionally surface warning event `4111`
  ("Development authentication bypass blocked - production environment
  detected") for every request that attempts the bypass path.

`HONUA_DEV_AUTH` must not be set in Production, Staging, QA, or any other
shared environment. The startup configuration validator emits a hard error
in non-relaxed environments if it is present (see
[`ConfigurationValidationService`](../../src/Honua.Server/Features/Admin/Services/ConfigurationValidationService.cs)).

---

## Authorization

- **Admin endpoints** (`/api/v1/admin/*`) require admin authentication.
- **JSON metrics endpoints** (`/api/v1/metrics/*`, `/healthz/metrics`, `/monitoring/*`) require admin authentication.
- **Prometheus endpoint** (`/metrics`) requires admin authentication and should still be restricted at the edge to Prometheus/network allowlists.
- **Data APIs** (FeatureServer, OGC, OData, Tiles) can be public or protected based on your access policy.
- **Workflow orchestration** operations (cancel, status) are surfaced through the admin operations API and require admin authentication. Workflow runs inherit the admin auth context — there is no per-workflow or per-step credential isolation in the current release.

Authentication schemes:
- **API key** via `X-API-Key` (automation and service access).
- **HTTP Basic compatibility** (optional) for legacy clients, using the Basic password as the API key.
- **OIDC** for browser-based Admin UI and token-based access.

Authentication precedence:
- If OIDC is enabled and `Authorization: Bearer ...` is present, Bearer auth is evaluated first.
- Otherwise, `X-API-Key` is evaluated.
- Basic compatibility is only evaluated when enabled and when no valid `X-API-Key` header is present.

Challenge behavior:
- API key challenges include `WWW-Authenticate: ApiKey ...`.
- When Basic compatibility mode is enabled, challenges also include `WWW-Authenticate: Basic ...`.
- Bearer-token failures return Bearer challenge headers from the JWT handler.

---

## Admin UI Deployment

- The Blazor Admin UI is deployed separately from this server from the `honua-server-admin` repo.
- Admin API calls require authentication (API key or OIDC).
- Restrict the standalone Admin UI and `/api/v1/admin/*` at the edge (network allowlists or VPN).

---

## Edge Security and Rate Limiting

Honua does not include application-level rate limiting. Enforce limits at the edge where traffic is centralized.

### Templates

| Platform | Template | Purpose |
|---|---|---|
| AWS | Use your IaC stack template (WAFv2 Web ACL + ALB association) | ALB + WAFv2 rate-limit rules |
| Azure | `examples/azure-application-gateway-waf-rate-limit-policy.json` | Application Gateway WAF custom rules |

### Recommended Starting Limits

| Endpoint Pattern | Suggested Rate |
|---|---|
| `/rest/services/*/query` | 200 req/min/IP |
| `/rest/services/*/FeatureServer` | 300 req/min/IP |
| `/ogc/features/collections/*/items` | 200 req/min/IP |
| `/odata/*/Features` | 200 req/min/IP |
| `/rest/services/*/MapServer/export` | 100 req/min/IP |
| `/ogc/tiles/collections/*/tiles` | 500 req/min/IP |
| `/api/v1/admin/*` | 30 req/min/IP |
| `/healthz/*` | Exempt |
| `/api/v1/metrics/*` | Exempt (or protect with auth, based on policy) |
| `/monitoring/*` | Exempt (or protect with auth, based on policy) |
| `/metrics` | Exempt from rate limits, require admin auth, and restrict at edge to Prometheus/network allowlists |

### AWS (ALB + WAFv2)

1. Define a WAFv2 Web ACL in your IaC stack.
2. Associate the ACL with the ALB that fronts Honua.
3. Verify Web ACL association and rate-limit rule matches in WAF metrics/logs.

If you use Honua-maintained Terraform, use the separate `honua-terraform` repository.

### Azure (Application Gateway + WAF)

1. Use `examples/azure-application-gateway-waf-rate-limit-policy.json` as the WAF policy payload.
2. Create or update an Application Gateway WAF policy with those custom rules.
3. Associate the WAF policy to the Application Gateway that fronts Honua.

---

## Proxy and Forwarded Headers

When deploying behind ALB/Application Gateway, enable forwarded-header processing and set the public base URL:

```bash
ForwardedHeaders__Enabled=true
ForwardedHeaders__ForwardLimit=2
ForwardedHeaders__KnownProxies__0=10.0.0.10
PUBLIC_BASE_URL=https://gis.example.com
```

`PUBLIC_BASE_URL` and `Public__BaseUrl` are both accepted. `KnownProxies` must contain trusted proxy hops.

`PUBLIC_BASE_URL` also drives absolute-URL emission for HATEOAS links and the OGC API Processes job-submission `Location` header. When unset, the server derives a safe local-origin URL from the connection's local IP and port; the request `Host` header is never reflected, so attacker-controlled hosts cannot steer redirects. Production deployments behind a proxy should set `PUBLIC_BASE_URL` so absolute links match the externally-visible hostname. See [`docs/security/base-url-and-open-redirect-handling.md`](../security/base-url-and-open-redirect-handling.md) for the resolver semantics.

---

## Content Security Policy (CSP)

For the Admin UI, configure CSP at your edge to reduce XSS and injection risks.

1. Start with **report-only** mode.
2. Observe violations via `POST /csp-violation-report`.
3. Enable enforcement once violations stabilize.

---

## Credential Rotation

Rotate these regularly:
- `HONUA_ADMIN_PASSWORD` (API key)
- OIDC client secrets
- Database credentials

**Rotation checklist:**
1. Update the secret in your secret manager.
2. Redeploy or restart services to pick up changes.
3. Verify access using a known admin endpoint.

Encryption-at-rest key rotation is exposed through the compliance framework
admin endpoint (`POST /api/v1/admin/compliance/encryption/rotate-key`); each
rotation is recorded in the audit log as `encryption.key.rotate`. See
[Compliance Framework](compliance-framework.md) for the dashboard, residency
controls, and report export surface.
