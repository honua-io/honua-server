# Container Images

Guidance for using Honua container images safely in production.

---

## Recommendations

- Pin to a specific version tag or digest.
- Avoid `latest` in production.
- Scan images regularly for vulnerabilities.

---

## Where Images Live

Images are published via CI to public registries (Docker Hub + GHCR). Use the registry and tag that match your deployment pipeline.

---

## Related Docs

- [Container Security](CONTAINER_SECURITY.md)
- [Deployment Scenarios](DEPLOYMENT_SCENARIOS.md)
