# Authorization Matrix

This page summarizes authentication expectations at a high level. For exact rules, consult your deployment configuration and `/openapi.json`.

---

## High-Level Rules

- **Admin endpoints** (`/api/v1/admin/*`) require admin authentication.
- **Metrics endpoints** may be public or protected depending on environment policy.
- **Data APIs** (FeatureServer, OGC, OData, Tiles) can be public or protected based on your access policy.

---

## Authentication Schemes

- **API key** via `X-API-Key` (automation and service access).
- **OIDC** for browser-based Admin UI and token-based access.

---

## Related Docs

- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Authentication Troubleshooting](troubleshooting/authentication-problems.md)
