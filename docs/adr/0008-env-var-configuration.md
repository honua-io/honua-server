# ADR-0008: Environment Variables as Primary Configuration

## Status
Accepted

## Context
Honua will primarily run in Docker containers. Configuration options:
- appsettings.json files
- Environment variables
- External config services (Consul, etc.)

## Decision
Environment variables are the primary configuration method. appsettings.json is for local development only.

**Key variables:**
```bash
ConnectionStrings__DefaultConnection  # Required
HONUA_ADMIN_PASSWORD                  # Optional (empty = no auth in dev)
Cors__AllowedOrigins__0               # Optional
Basemap__Provider                     # Optional (default: openfreemap)
```

**Rationale:**
- Docker/K8s deployments use env vars natively
- Secrets can be injected without file mounts
- 12-factor app compliance
- No config file management in containers

## Consequences

### Positive
- Simple deployment (just set env vars)
- Works with Docker, K8s, cloud services
- Secrets never written to disk
- Easy to override per-environment

### Negative
- Long variable names with `__` separators
- No comments or documentation in config
- Must document all variables

### Notes
- ASP.NET Core maps `__` to `:` in config hierarchy
- Array items use `__0`, `__1`, etc.
- appsettings.json still works for local dev
