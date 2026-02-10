# Authentication Troubleshooting

Use this guide to resolve authentication and authorization issues for Honua Server.

**Scope**: API key and OIDC checks that commonly break production access.

---

## Quick Diagnostics

**Public health**:
```bash
curl -v http://localhost:8080/healthz/ready
```

**Admin endpoint with API key**:
```bash
curl -H "X-API-Key: your-admin-key" http://localhost:8080/api/v1/admin/config
```

**Admin endpoint with OIDC token**:
```bash
curl -H "Authorization: Bearer <jwt>" http://localhost:8080/api/v1/admin/config
```

If all three fail, start by validating configuration and service logs.

---

## API Key Issues (401)

**Check**:
- `HONUA_ADMIN_PASSWORD` is set and non-empty.
- Requests include the `X-API-Key` header.
- The service was restarted after configuration changes.

**Common fix**:
```bash
export HONUA_ADMIN_PASSWORD="your-secure-key"
```

---

## OIDC Issues (401/403)

**Check**:
- `Oidc:Enabled` is true.
- At least one provider is configured (`Oidc:Generic`, `Oidc:AzureAd`, or `Oidc:Google`).
- The issuer/authority URL matches the provider's discovery document.
- System time is in sync (token lifetimes are strict).

**Authorization (403)**:
- Admin access is role-based. Ensure your token includes an admin role.
- Adjust role mapping with `Oidc:ClaimsMapping:RoleClaimType` if your IdP uses a custom claim.
- Set admin role names via `Oidc:AdminRoles`.

---

## After Changes

Always restart the service after modifying authentication config or secrets.

---

## Security Notes

- Keep API keys in a secret manager, not in code.
- Rotate keys regularly.
- Use TLS and restrict admin endpoints at the edge.

---

## Related Docs

- [Security Configuration](../SECURITY_CONFIGURATION.md)
- [Deployment Scenarios](../DEPLOYMENT_SCENARIOS.md)
