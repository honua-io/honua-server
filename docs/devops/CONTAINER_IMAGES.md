# Container Images

Honua publishes container images to Docker Hub and GHCR. All deployment options use the same images.

## Registries
- **Docker Hub:** `honuaio/honua-server`
- **GHCR:** `ghcr.io/honua-io/honua-server`

## Build variants

Every image is published in two variants:

| Variant | Description |
|---------|-------------|
| **JIT** (default) | Standard .NET runtime. Larger image, JIT-compiled at startup. |
| **AOT** (recommended) | Native ahead-of-time compiled binary. Smaller image, faster startup, lower memory. |

AOT images use the `-aot` suffix on any tag (e.g. `v1.2.3-aot`, `latest-aot`).

## Tags

| Tag | Build | Recommended use | Notes |
| --- | --- | --- | --- |
| `vX.Y.Z-aot` | AOT | Production | Pin exact version, fastest startup. |
| `vX.Y.Z` | JIT | Production (if AOT incompatible) | Pin exact version. |
| `vX.Y-aot` | AOT | Staging / UAT | Floats patch releases within a minor line. |
| `vX.Y` | JIT | Staging / UAT | Floats patch releases within a minor line. |
| `latest-aot` | AOT | Dev / preview | Tracks trunk. |
| `latest` | JIT | Dev / preview | Tracks trunk. |
| `nightly-aot` | AOT | CI / experiments | Nightly builds from trunk. |
| `nightly` | JIT | CI / experiments | Nightly builds from trunk. |

## When to use JIT over AOT

AOT is recommended for all deployments. Use JIT only if you:
- Rely on runtime reflection features not supported by AOT trimming
- Need to debug with full .NET runtime diagnostics
- Encounter a specific AOT compatibility issue

## Publishing workflows

- **Release + trunk builds:** `.github/workflows/deploy.yml` builds and pushes both JIT and AOT multi-arch images to GHCR and Docker Hub.
- **Release tags:** `docker/.github/workflows/release.yml` validates, builds both variants, and creates a GitHub Release.
- **Nightly builds:** `.github/workflows/nightly-container-build.yml` publishes `nightly` (JIT) and `nightly-aot` (AOT) tags.

## Required secrets (GitHub)

Configure these repository secrets for Docker Hub publishing:
- `DOCKERHUB_USERNAME`
- `DOCKERHUB_TOKEN`

## Pull examples

```bash
# AOT (recommended)
docker pull honuaio/honua-server:latest-aot
docker pull honuaio/honua-server:v1.2.3-aot

# JIT
docker pull honuaio/honua-server:latest
docker pull honuaio/honua-server:v1.2.3
```

## Admin UI assets

Some images may omit Admin UI static assets for a smaller footprint.
If you need the Admin UI in your runtime image, confirm the tag or build pipeline includes it.
