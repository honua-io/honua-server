# Security Incidents Runbook

**Alert**: HonuaSecurityIncident
**Severity**: Critical
**Response Time**: < 5 minutes

## Symptoms
- Unexpected admin access
- Suspicious authentication failures
- Abuse patterns or data exfiltration indicators

## Immediate Actions

### 1. Contain (0-5 minutes)
- Block suspicious IPs at the edge/WAF
- Disable affected credentials if known
- Notify security team

### 2. Preserve Evidence (5-10 minutes)
```bash
# Collect recent logs
kubectl logs --since=30m -l app=honua-server -n honua-production > incident-logs.txt
```

## Diagnostics

### Review Audit Logs
- Search for authorization failures/successes on admin or metrics endpoints
- Correlate with user IDs and IPs

### Validate Credential Usage
- Check API key usage logs
- Review OIDC token issuer/audience mismatches

## Common Actions

### Rotate Credentials
- Rotate `HONUA_ADMIN_PASSWORD`
- Rotate OIDC client secrets
- Rotate database credentials if exposed

### Reduce Attack Surface
- Tighten rate limits
- Disable anonymous read access by setting layer/service access policies (e.g., `allowAnonymous=false` or restricting tenants/roles)
- Restrict admin endpoints to a management network

## Escalation

Escalate immediately if:
- Data exfiltration is suspected
- Admin credentials compromised
- Multiple systems affected

## Recovery

- Restore clean configuration
- Re-enable services in stages
- Document incident details and timeline
