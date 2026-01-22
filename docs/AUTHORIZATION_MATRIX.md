# Authorization Matrix

This matrix captures which endpoints require authentication and which policies apply.

## Authentication Schemes

- **OIDC**: Bearer tokens when `Oidc:Enabled=true`; admin roles are configured via `Oidc:AdminRoles`. Required for browser-based Admin UI.
- **API key (automation only)**: `X-API-Key` header using `HONUA_ADMIN_PASSWORD` (supports `env:` references). Not safe for browser UI clients.
- **Dev/Test bypass**: In Development/Test, `HONUA_DEV_AUTH=true` or an empty admin password bypasses API key auth.

## Endpoint Matrix

| Area | Base path | Read access | Write access | Notes |
| --- | --- | --- | --- | --- |
| Health | `/healthz/*` | Public | N/A | Liveness/Readiness/Metrics health checks are always public. |
| Metrics | `/api/v1/metrics/health` | Public | N/A | Health metrics are public. |
| Metrics (detailed) | `/api/v1/metrics/*` | Auth required | N/A | Requires any authenticated user. |
| Admin | `/api/v1/admin/*` | Admin policy | Admin policy | OIDC admin role for browser UI; API key for automation only. |
| FeatureServer | `/rest/services/*` | Policy-driven | Auth required | Read access follows layer/service access policy; `applyEdits` always requires auth. |
| FeatureServer attachments | `/rest/services/*/attachments/*` | Policy-driven | Auth required | Same rules as FeatureServer reads/writes. |
| OGC Features | `/ogc/features/*` | Policy-driven | Auth required | Read access follows layer access policy; POST/PUT/DELETE require auth. |
| OGC Tiles | `/ogc/tiles/*` | Policy-driven | N/A | Read access follows layer access policy. |
| OData | `/odata/*` | Policy-driven | Auth required | Read access follows layer access policy; POST/PATCH/DELETE require auth. |

## Notes

- **Policy-driven reads**: Read access is controlled by per-layer/per-service access policies in catalog metadata. When no policy is set, authentication is required by default.
- **Admin policy**: Uses `Admin`/`AdminPolicy` roles from API key or OIDC role claims. Admin UI must use OIDC.
- **OIDC role mapping**: Configure `Oidc:ClaimsMapping:RoleClaimType` and `Oidc:AdminRoles` to map admin roles.
