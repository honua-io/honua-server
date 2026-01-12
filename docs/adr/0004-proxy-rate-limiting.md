# ADR-0004: Proxy/Edge Rate Limiting

## Status
Accepted

## Context
The MVP needs protection against abuse and runaway queries. Application-level rate limiting added
complexity (configuration, testing, Redis options) and still enforced limits per instance rather than
globally across a load balancer.

## Decision
Remove application-level rate limiting from Honua Server. Rate limiting is enforced at the edge
(nginx/ALB/API gateway) where limits are global, centralized, and aligned with deployment topology.

## Consequences

### Positive
- Simplifies the application pipeline and configuration surface.
- Avoids per-instance limits and Redis dependencies.
- Edge providers can add WAF/DDoS protections alongside rate limits.

### Negative
- Requires proxy configuration for production environments.
- Rate limit headers/429 behavior is determined by the edge, not the app.

### Mitigation
- Provide deployment templates for nginx and AWS ALB rate limiting (tracked in issue #243).
