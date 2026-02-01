# Local Helm Testing with k3d

This guide provisions a local k3d cluster with Traefik and installs the Honua Helm chart for ingress testing.

## Prereqs
- Docker
- k3d
- kubectl
- helm

## Create cluster + ingress

```bash
scripts/k8s/k3d-up.sh
```

This creates a k3d cluster named `honua-k3d` and maps:
- `localhost:8080` -> Traefik HTTP
- `localhost:8443` -> Traefik HTTPS

If those ports are already in use, set custom host ports:

```bash
K3D_HTTP_PORT=18080 K3D_HTTPS_PORT=18443 scripts/k8s/k3d-up.sh
```

## Load a local Honua image (optional)

If you have a local image (for example `honua-server:latest`), import it into the cluster:

```bash
k3d image import honua-server:latest -c honua-k3d
```

## PostGIS for migrations (recommended)

Honua migrations require PostGIS. The default Bitnami PostgreSQL subchart does not include it.
For full readiness checks, run the PostGIS manifest and point Honua at it:

```bash
scripts/k8s/postgis-up.sh
```

```bash
INGRESS_CLASS=traefik \
POSTGRESQL_ENABLED=false \
DEFAULT_CONNECTION_STRING="Host=honua-postgis;Port=5432;Database=honua;Username=honua;Password=honua" \
scripts/k8s/helm-install.sh
```

If you only need ingress testing, you can keep the subchart and skip migrations:

```bash
INGRESS_CLASS=traefik HONUA_SKIP_MIGRATIONS=true scripts/k8s/helm-install.sh
```

## Install chart

```bash
INGRESS_CLASS=traefik \
HONUA_IMAGE_REPOSITORY=honua-server \
HONUA_IMAGE_TAG=latest \
HONUA_IMAGE_PULL_POLICY=IfNotPresent \
scripts/k8s/helm-install.sh
```

By default, the script enables the PostgreSQL subchart, ingress, and admin UI, and uses `honua.local` as the host.
The script also sets a development master key required for readiness checks. To override:

```bash
SECURITY_MASTER_KEY="your-32+char-key" scripts/k8s/helm-install.sh
```

If you changed the HTTP port in `k3d-up.sh`, pass the same port for the test hint:

```bash
LOCAL_HTTP_PORT=18080 scripts/k8s/helm-install.sh
```

## Validate ingress

```bash
curl -H "Host: honua.local" http://localhost:8080/healthz/ready
```

## Run Helm tests

```bash
scripts/k8s/helm-test.sh
```

## Cleanup

```bash
scripts/k8s/k3d-down.sh
```
