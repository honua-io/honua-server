# Security Configuration

This guide covers authentication, authorization, edge security, and related operational settings for Honua Server.

**MVP deferrals**:
- No application-level rate limiting (enforce at the edge).
- No secure-connection allowlist or connection audit trail.

---

## Authentication

**Admin API**: secured with an API key in the `X-API-Key` header.
- Set `HONUA_ADMIN_PASSWORD` in your secret manager.

**OIDC**: optional for the Admin UI and token-based access.
- Configure `Oidc__Enabled` and a provider block (`Oidc__Generic`, `Oidc__AzureAd`, or `Oidc__Google`).
- Verify claims mapping and admin roles in your IdP.

---

## Authorization

- **Admin endpoints** (`/api/v1/admin/*`) require admin authentication.
- **JSON metrics endpoints** (`/api/v1/metrics/*`, `/healthz/metrics`) require admin authentication.
- **Prometheus endpoint** (`/metrics`) should be restricted at the edge to Prometheus/network allowlists.
- **Data APIs** (FeatureServer, OGC, OData, Tiles) can be public or protected based on your access policy.

Authentication schemes:
- **API key** via `X-API-Key` (automation and service access).
- **OIDC** for browser-based Admin UI and token-based access.

---

## Admin UI Deployment

- Served at `/<host>/admin` when enabled via `ServeAdminUI` or `HONUA_SERVE_ADMIN_UI`.
- Admin API calls require authentication (API key or OIDC).
- Restrict `/admin` at the edge (network allowlists or VPN).

---

## Edge Security and Rate Limiting

Honua does not include application-level rate limiting. Enforce limits at the edge where traffic is centralized.

### Templates

| Platform | Template | Purpose |
|---|---|---|
| AWS | `docs/devops/examples/aws-waf-rate-limit.tf` | ALB + WAFv2 rate-limit rules |
| Azure | `docs/devops/examples/azure-application-gateway-waf-rate-limit-policy.json` | Application Gateway WAF custom rules |

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
| `/metrics` | Exempt from rate limits, but restrict at edge to Prometheus/network allowlists |

### AWS (ALB + WAFv2)

1. Copy `docs/devops/examples/aws-waf-rate-limit.tf` into your Terraform stack.
2. Set `alb_arn` to the ALB that fronts Honua.
3. Apply and verify `waf_web_acl_arn` output.

### Azure (Application Gateway + WAF)

1. Use `docs/devops/examples/azure-application-gateway-waf-rate-limit-policy.json` as the WAF policy payload.
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
