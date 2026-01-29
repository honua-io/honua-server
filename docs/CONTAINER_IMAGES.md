# Container Images

Honua publishes container images to Docker Hub and GHCR for local and non-K8s deployments.

## Registries
- **Docker Hub:** `honuaio/honua-server`
- **GHCR:** `ghcr.io/honua-io/honua-server`

## Tags
- `latest` for trunk builds
- `vX.Y.Z`, `vX.Y`, `vX` for release tags
- `nightly` for nightly JIT builds
- `nightly-aot` for nightly AOT builds

## Publishing Workflow
- **Release + trunk builds:** `.github/workflows/deploy.yml` builds and pushes multi-arch images to GHCR and Docker Hub.
- **Nightly AOT builds:** `.github/workflows/nightly-container-build.yml` publishes `nightly-aot` tags.

## Required Secrets (GitHub)
Configure these repository secrets for Docker Hub publishing:
- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

## Pull Examples
```bash
docker pull honuaio/honua-server:latest
docker pull honuaio/honua-server:v1.2.3
```
