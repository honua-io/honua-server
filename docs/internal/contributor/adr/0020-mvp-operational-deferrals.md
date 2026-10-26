# ADR-0020: MVP Operational Deferrals

## Status
Accepted

## Context
The MVP scope accumulated operational/enterprise features that increase configuration surface area,
storage requirements, and runtime complexity without unlocking core MVP value. Recent changes removed
application-level rate limiting and simplified secure connections, while the compliance/audit logging
stack introduces dashboards, monitoring services, and persistent audit storage.

## Decision
Defer the following operational/enterprise features to post-MVP:
- Application-level rate limiting (edge-only per ADR-0004; templates tracked in issue #243).
- Secure-connection host allowlist and connection audit trail (keep encrypted credentials or secret references).
- Security compliance framework (middleware, dashboards, monitoring, audit log storage).

## Consequences

### Positive
- Smaller runtime surface area and fewer dependencies.
- Reduced configuration and operational burden for MVP.
- Clearer MVP security model focused on encryption and secret references.

### Negative
- No built-in compliance reporting or audit trail in MVP.
- Governance for connection destinations is deferred.
- Edge rate limiting required for production protections.

### Mitigation
- Document deferrals in MVP guidance and roadmap.
- Provide edge rate limiting templates (nginx/ALB/WAF).
- Reintroduce compliance/audit features in GA when demand justifies.

## Update (2026-10)

Application-level rate limiting has since shipped as an opt-in limiter (#355). It is off by
default (`RateLimiting:Enabled`), partitions by tenant, user or API key, and returns `429` with
`Retry-After`. Edge enforcement (nginx/ALB/WAF, ADR-0004) remains the default posture, and the
opt-in limiter does not replace it. The other two deferrals still stand:

- there is no secure-connection allowlist or connection audit trail;
- there is no compliance framework, audit-log storage or compliance dashboard.
