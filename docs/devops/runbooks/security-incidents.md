# Security Incidents Runbook

**Alert**: HonuaSecurityIncident
**Severity**: Critical
**Goal**: Contain, assess, and eradicate security issues quickly.

---

## Immediate Actions

- Restrict access (network rules, WAF, or firewall).
- Rotate exposed credentials.
- Preserve logs for investigation.

---

## Diagnose

- Identify the affected surface (Admin API, data APIs, infrastructure).
- Determine scope and timeline of access.
- Validate integrity of data stores and backups.

---

## Mitigate

- Disable compromised credentials and revoke tokens.
- Roll back recent changes if exploit is related to deployment.
- Patch or isolate vulnerable services.

---

## Escalate

Escalate immediately to security leadership and incident response.
