# Credential Rotation Procedures

This document outlines how to rotate sensitive credentials without downtime.

## Rotation Targets

- **Admin API key** (`HONUA_ADMIN_PASSWORD`)
- **OIDC client secrets** (`Oidc:*:ClientSecret`)
- **Database credentials** (`ConnectionStrings__DefaultConnection`)
- **Cache credentials** (Redis connection string)
- **Storage credentials** (object storage access keys)

## Recommended Cadence

- **Quarterly** for production credentials
- **After incident** for any suspected exposure

## General Rotation Steps

1. **Create new credential** in secret provider
2. **Update configuration** to reference new secret (use `env:` or provider refs)
3. **Deploy** updated configuration
4. **Verify** functionality (health + smoke tests)
5. **Revoke** old credential after validation

## Admin API Key Rotation

```bash
# Set a new secret reference
HONUA_ADMIN_PASSWORD=env:HONUA_ADMIN_PASSWORD_VALUE
HONUA_ADMIN_PASSWORD_VALUE="new-admin-key"
```

- Update client integrations that use `X-API-Key`
- Verify access to `/api/v1/admin/*`

## OIDC Client Secret Rotation

- Update provider secret in IdP
- Update `Oidc__*__ClientSecret` reference
- Restart services
- Validate login flow

## Database Credential Rotation

- Create new DB user with required permissions
- Update connection string secret reference (or rotate the secret value in the provider)
- Restart application instances
- Decommission old user after validation

## Cloud Secret Manager Rotation

- **AWS Secrets Manager**: rotate the secret value and keep the ref stable; use `versionStage` or `versionId` if you need pinning.
- **Azure Key Vault**: create a new version in the vault; update the ref only if you pin a version.

## Verification Checklist

- `/healthz/ready` is healthy
- Admin endpoints accessible with new credentials
- No authentication errors in logs
