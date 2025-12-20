# Container Security Hardening

This document describes the security hardening measures implemented for the Honua Server container runtime.

## Overview

The Honua Server container image implements multiple security hardening measures to meet production security requirements:

- **Non-root execution**: Container runs as dedicated non-root user
- **Read-only filesystem**: Root filesystem is mounted as read-only
- **Capability dropping**: All unnecessary Linux capabilities are dropped
- **Security options**: Additional security constraints applied

## Non-Root User Configuration

Both Dockerfiles create a dedicated user `honua` with UID/GID 1001:

```dockerfile
# Create non-root user for security
RUN addgroup -g 1001 -S honua && \
    adduser -S honua -G honua -u 1001

# Switch to non-root user
USER honua
```

This ensures the container process never runs with root privileges.

## Read-Only Filesystem

The root filesystem is mounted as read-only to prevent runtime modifications:

```yaml
read_only: true
```

### Writable Paths

The following paths are made writable via volume mounts for runtime data:

| Path | Purpose | Volume |
|------|---------|--------|
| `/tmp/honua-logs` | Application logs | `honua_logs` |
| `/tmp/honua-cache` | Application cache | `honua_cache` |
| `/tmp/dotnet-diagnostics` | .NET runtime diagnostics | `dotnet_diagnostics` |
| `/tmp` | Temporary files | tmpfs (100MB) |

### Tmpfs Configuration

Temporary filesystem is configured with security restrictions:

```yaml
tmpfs:
  - /tmp:noexec,nosuid,size=100m
```

- `noexec`: Prevents execution of files
- `nosuid`: Ignores setuid/setgid bits
- `size=100m`: Limits size to 100MB

## Capability Dropping

All Linux capabilities are dropped for defense in depth:

```yaml
cap_drop:
  - ALL
```

This removes all privileged operations from the container process.

## Security Options

Additional security constraints are applied:

```yaml
security_opt:
  - no-new-privileges:true
```

- `no-new-privileges`: Prevents privilege escalation

## Docker Labels

Security metadata is included in the container image:

```dockerfile
LABEL security.non-root="true"
LABEL security.capabilities.drop="ALL"
LABEL security.read-only-root="true"
```

These labels document the security configuration for compliance and auditing.

## Verification

### Manual Verification

1. **Check non-root execution**:
   ```bash
   docker run --rm honua-server id
   # Should output: uid=1001(honua) gid=1001(honua)
   ```

2. **Verify read-only filesystem**:
   ```bash
   docker run --rm honua-server touch /test
   # Should fail with "Read-only file system"
   ```

3. **Test writable paths**:
   ```bash
   docker run --rm honua-server touch /tmp/honua-logs/test
   # Should succeed
   ```

### CI Verification

The CI pipeline includes automated security verification (see `.github/workflows/` for implementation).

## Production Deployment

When deploying to production, ensure:

1. Use the hardened Docker Compose configuration
2. Monitor container behavior for write attempts to read-only paths
3. Regularly audit volume contents
4. Verify security labels and runtime configuration

## Compliance

This configuration addresses the following security requirements:

- **CIS Docker Benchmark**: 4.1 (non-root user), 5.12 (read-only root filesystem), 5.25 (restricted capabilities)
- **NIST SP 800-190**: Container runtime security recommendations
- **OWASP Container Security**: Defense in depth principles

## Troubleshooting

### Common Issues

1. **Application crashes with permission errors**:
   - Verify writable paths are correctly mounted
   - Check application attempts to write to read-only paths

2. **Performance issues with tmpfs**:
   - Monitor tmpfs usage
   - Adjust size limits if necessary

3. **Log collection problems**:
   - Ensure log volume is properly mounted
   - Verify log directory permissions

### Debug Mode

For development/debugging, you can temporarily disable read-only mode:

```yaml
# DEVELOPMENT ONLY - DO NOT USE IN PRODUCTION
read_only: false
```

Remember to re-enable read-only mode for production deployments.