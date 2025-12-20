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
# Connection & Security
ConnectionStrings__DefaultConnection  # Required
HONUA_ADMIN_PASSWORD                  # Optional (empty = no auth in dev)
Cors__AllowedOrigins__0               # Optional
Basemap__Provider                     # Optional (default: openfreemap)

# Resource Limits (Issue #63 - Shared limits configuration)
Limits__Query__MaxRecordCount         # Default: 2000, Range: 100-10000
Limits__Query__DefaultRecordCount     # Default: 1000, Range: 100+
Limits__Query__MaxOffset              # Default: 100000, Range: 1000+
Limits__Query__QueryTimeout           # Default: 00:00:30, Range: 00:00:05+

Limits__Geometry__MaxVertices         # Default: 10000, Range: 1000+
Limits__Geometry__MaxPolygons         # Default: 100, Range: 1+
Limits__Geometry__MaxCoordinateValue  # Default: 180, Range: 0.1+

Limits__Edits__MaxPayloadSize         # Default: 10485760 (10MB), Range: 1MB+
Limits__Edits__MaxFeaturesPerRequest  # Default: 1000, Range: 1+
Limits__Edits__MaxAttachmentSize      # Default: 52428800 (50MB), Range: 1MB+

Limits__Attachments__AllowedMimeTypes # Default: "image/*,application/pdf"
Limits__Attachments__MaxFileCount     # Default: 10, Range: 1+

Limits__Tiles__MaxZoomLevel           # Default: 18, Range: 1-22
Limits__Tiles__CacheSize              # Default: 1073741824 (1GB), Range: 1MB+

Limits__Connections__MaxConcurrent    # Default: 100, Range: 1+
Limits__Connections__RequestTimeout   # Default: 00:01:00, Range: 00:00:05+
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
