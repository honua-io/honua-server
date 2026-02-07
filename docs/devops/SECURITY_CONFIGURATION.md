# Security Configuration

This guide covers the security-critical configuration surfaces for Honua Server.

**MVP deferrals**:
- No application-level rate limiting (enforce at the edge).
- No secure-connection allowlist or connection audit trail.
- No compliance/audit log storage in the application.

---

## Authentication

**Admin API**: secured with an API key in the `X-API-Key` header.
- Set `HONUA_ADMIN_PASSWORD` in your secret manager.

**OIDC**: optional for the Admin UI and token-based access.
- Configure `Oidc__Enabled` and a provider block (`Oidc__Generic`, `Oidc__AzureAd`, or `Oidc__Google`).
- Verify claims mapping and admin roles in your IdP.

---

## Edge Security

- Terminate TLS at the edge (nginx, ALB, gateway).
- Enforce rate limiting at the edge.
- Restrict admin endpoints to internal networks where possible.

---

## Secrets Management

- Store credentials in a secret manager, not in source control.
- Rotate admin keys and IdP secrets regularly.

---

## Related Docs

- [Credential Rotation](credential-rotation.md)
- [CSP Enhancement](CSP_ENHANCEMENT.md)
- [Authorization Matrix](AUTHORIZATION_MATRIX.md)
