# Credential Rotation

Rotate credentials regularly to reduce blast radius and comply with security policies.

---

## What to Rotate

- `HONUA_ADMIN_PASSWORD` (API key)
- OIDC client secrets
- Database credentials

---

## Rotation Checklist

1. Update the secret in your secret manager.
2. Redeploy or restart services to pick up changes.
3. Verify access using a known admin endpoint.

---

## Related Docs

- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Authentication Troubleshooting](troubleshooting/authentication-problems.md)
