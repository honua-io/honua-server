# Admin UI

Operational notes for deploying the Honua Admin UI.

---

## Basics

- The Admin UI is served at `/<host>/admin` when enabled.
- Enable/disable with `ServeAdminUI` or `HONUA_SERVE_ADMIN_UI`.
- Admin API calls require authentication (API key or OIDC).

---

## Recommended Setup

- Use OIDC for browser access.
- Restrict `/admin` at the edge (network allowlists or VPN).
- Terminate TLS at the edge.

---

## Related Docs

- [Admin UI User Guides](../user/admin-ui/README.md)
- [Security Configuration](SECURITY_CONFIGURATION.md)
