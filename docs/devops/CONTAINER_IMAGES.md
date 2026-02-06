# Container Images

Honua publishes container images to Docker Hub and GHCR. All deployment options use the same images.

## Registries
- **Docker Hub:** `honuaio/honua-server`
- **GHCR:** `ghcr.io/honua-io/honua-server`

## Tags
- `latest` for trunk builds
- `vX.Y.Z`, `vX.Y`, `vX` for release tags
- `nightly` for nightly JIT builds
- `nightly-aot` for nightly AOT builds

## Tag → Deploy Target Guidance
| Tag | Build | Recommended use | Notes |
| --- | --- | --- | --- |
| `vX.Y.Z` | JIT | Production | Pin exact version for change control. |
| `vX.Y` | JIT | Staging / UAT | Floats patch releases within a minor line. |
| `vX` | JIT | Long-lived test | Floats minor/patch within a major line. |
| `latest` | JIT | Dev / preview | Tracks trunk; not recommended for production. |
| `nightly` | JIT | CI / experiments | Nightly builds from trunk. |
| `nightly-aot` | AOT | Cold-start sensitive / serverless experiments | AOT build; validate compatibility in your environment. |

## Publishing Workflow
- **Release + trunk builds:** `.github/workflows/deploy.yml` builds and pushes multi-arch images to GHCR and Docker Hub.
- **Nightly builds:** `.github/workflows/nightly-container-build.yml` publishes `nightly` (JIT) and `nightly-aot` (AOT) tags.

## Required Secrets (GitHub)
Configure these repository secrets for Docker Hub publishing:
- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

## Pull Examples
```bash
docker pull honuaio/honua-server:latest
docker pull honuaio/honua-server:v1.2.3
```

## Admin UI Assets
- Some images may omit Admin UI static assets for a smaller footprint.
- If you need the Admin UI in your runtime image, confirm the tag or build pipeline includes it.
