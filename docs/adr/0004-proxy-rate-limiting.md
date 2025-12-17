# ADR-0004: Proxy-Based Rate Limiting

## Status
Accepted

## Context
MVP needs protection against abuse and runaway queries. Options:
- ASP.NET Core Rate Limiting middleware
- Reverse proxy rate limiting (nginx, Traefik)
- API Gateway (Kong, YARP)

## Decision
Defer rate limiting to the reverse proxy layer for MVP.

**Rationale:**
- Standard pattern for containerized deployments
- Battle-tested implementations (nginx, Traefik)
- Keeps application code simple
- More flexible (per-path, per-IP rules at infra level)
- No additional application dependencies

## Consequences

### Positive
- Zero application complexity
- Flexible configuration at deployment time
- Works with any proxy (nginx, Traefik, HAProxy, cloud LBs)
- Easier to adjust limits without code changes

### Negative
- Requires proxy in production (direct container access unprotected)
- No per-user or per-API-key limits (requires app-level)
- Documentation must explain proxy setup

### Future
Beta may add `Microsoft.AspNetCore.RateLimiting` if per-user limits needed for authenticated endpoints.
