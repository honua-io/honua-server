# Local Helm Testing with kind

This guide provisions a local kind cluster with ingress-nginx and installs the Honua Helm chart for ingress testing.

## Prereqs
- Docker
- kind
- kubectl
- helm

## Create cluster + ingress

```bash
scripts/k8s/kind-up.sh
```

This creates a kind cluster named `honua` and maps:
- `localhost:8080` -> ingress-nginx HTTP
- `localhost:8443` -> ingress-nginx HTTPS

If those ports are already in use, set custom host ports:

```bash
KIND_HTTP_PORT=18080 KIND_HTTPS_PORT=18443 scripts/k8s/kind-up.sh
```

## Install chart

```bash
scripts/k8s/helm-install.sh
```

By default, this enables the PostgreSQL subchart, ingress, and admin UI, and uses `honua.local` as the host.
The script also sets a development master key required for readiness checks. To override:

```bash
SECURITY_MASTER_KEY="your-32+char-key" scripts/k8s/helm-install.sh
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
scripts/k8s/kind-down.sh
```
