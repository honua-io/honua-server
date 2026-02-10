# Container Security

Baseline hardening guidance for Honua containers.

---

## Recommendations

- Run as a non-root user.
- Use read-only filesystems where possible.
- Drop Linux capabilities you don't need.
- Restrict outbound egress if your environment allows it.
- Scan images as part of CI and release workflows.

---

## Related Docs

- [Security Configuration](SECURITY_CONFIGURATION.md)
- [Container Images](CONTAINER_IMAGES.md)
