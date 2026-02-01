# Kubernetes Helper Scripts

This folder contains helper scripts for local Kubernetes ingress testing with the Honua Helm chart.
The workflow uses k3d (Traefik) and optionally deploys a PostGIS database for migrations.

## Quick start (k3d)

```bash
scripts/k8s/k3d-up.sh
INGRESS_CLASS=traefik scripts/k8s/helm-install.sh
```

## PostGIS (recommended for migrations)

Honua migrations require PostGIS. The Bitnami PostgreSQL subchart does not include it.

```bash
scripts/k8s/postgis-up.sh
POSTGRESQL_ENABLED=false \
DEFAULT_CONNECTION_STRING="Host=honua-postgis;Port=5432;Database=honua;Username=honua;Password=honua" \
scripts/k8s/helm-install.sh
```

If you only need ingress testing, you can skip migrations:

```bash
HONUA_SKIP_MIGRATIONS=true scripts/k8s/helm-install.sh
```

## Script summary

- `kind-up.sh` / `kind-down.sh`: Create/delete a kind cluster named `honua` with ingress-nginx.
- `k3d-up.sh` / `k3d-down.sh`: Create/delete a k3d cluster named `honua-k3d` with Traefik.
- `postgis-up.sh` / `postgis-down.sh`: Deploy/teardown a PostGIS instance in `honua` namespace.
- `postgis.yaml`: Manifest used by the PostGIS scripts.
- `helm-install.sh`: Installs/updates the Honua Helm chart with configurable env vars (see below).
- `helm-test.sh`: Runs Helm test hooks for the release.

## helm-install.sh environment variables

- `INGRESS_CLASS` (default: `nginx`) set `traefik` for k3d.
- `INGRESS_HOSTNAME` (default: `honua.local`)
- `INGRESS_PATH` / `INGRESS_PATH_TYPE` (defaults: `/` / `Prefix`)
- `LOCAL_HTTP_PORT` (default: `8080`) used for the curl hint
- `POSTGRESQL_ENABLED` (`true`/`false`)
- `DEFAULT_CONNECTION_STRING` (use when `POSTGRESQL_ENABLED=false`)
- `POSTGRES_IMAGE_REPOSITORY`, `POSTGRES_IMAGE_TAG`, `POSTGRES_IMAGE_DIGEST`
- `HONUA_IMAGE_REPOSITORY`, `HONUA_IMAGE_TAG`, `HONUA_IMAGE_PULL_POLICY`
- `HONUA_SKIP_MIGRATIONS` (`true`/`false`)
- `SECURITY_MASTER_KEY`
- `HONUA_ADMIN_PASSWORD`
- `CHART_PATH`, `RELEASE_NAME`, `NAMESPACE`

## Docs

- `docs/development/k3d-helm.md`
