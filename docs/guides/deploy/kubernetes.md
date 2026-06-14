# Deploy on Kubernetes

You'll run Honua on Kubernetes with the Helm chart: secrets sourced from the cluster, probes wired to the health endpoints, an ingress terminating TLS, and horizontal scaling of stateless replicas.

**Prerequisites:** A cluster with `kubectl` and `helm` access, a PostGIS database reachable from the cluster (managed Postgres recommended), and an ingress controller. Redis is required only for durable jobs, queued imports, and workflows.

The Helm chart lives in the separate [honua-helm](https://github.com/honua-io/honua-helm) repository — follow its README for the current chart path and values schema. The steps below cover what any values file must wire up.

## Steps

1. Create the namespace and a secret holding the required credentials. The master key must be at least 32 characters.

```bash
kubectl create namespace honua
kubectl -n honua create secret generic honua-secrets \
  --from-literal=ConnectionStrings__DefaultConnection='Host=db.example.com;Database=honua;Username=honua;Password=replace-me;SSL Mode=Require' \
  --from-literal=HONUA_ADMIN_PASSWORD='replace-with-strong-admin-password' \
  --from-literal=Security__ConnectionEncryption__MasterKey='replace-with-random-string-of-32-plus-characters'
```

2. Write a values file. All configuration is environment variables (the chart maps `env` and `envFrom` onto the container), and probes must point at `/healthz/live` and `/healthz/ready`.

```bash
cat > honua-values.yaml <<'EOF'
replicaCount: 2
image:
  repository: ghcr.io/honua-io/honua-server
  tag: latest-aot
envFrom:
  - secretRef:
      name: honua-secrets
env:
  - name: HONUA_OBSERVABILITY
    value: "true"
  - name: Cors__AllowedOrigins__0
    value: https://app.example.com
livenessProbe:
  httpGet: { path: /healthz/live, port: 8080 }
readinessProbe:
  httpGet: { path: /healthz/ready, port: 8080 }
resources:
  requests: { cpu: 500m, memory: 512Mi }
  limits: { cpu: "2", memory: 2Gi }
EOF
```

3. Install the chart from the honua-helm repository (its README documents the current chart path under the cloned repo).

```bash
git clone https://github.com/honua-io/honua-helm.git
CHART_PATH=$(find honua-helm -name Chart.yaml -path '*honua*' | head -1 | xargs dirname)
helm upgrade --install honua "$CHART_PATH" --namespace honua --values honua-values.yaml
```

4. Expose the service through your ingress with TLS. Honua does not terminate TLS — terminate at the ingress; port 8080 is HTTP/1 (REST, gRPC-Web), port 8081 is h2c gRPC and needs an ingress that can forward HTTP/2 cleartext to the backend.

```bash
HONUA_HOST=honua.example.com
kubectl -n honua create ingress honua \
  --class=nginx \
  --rule="${HONUA_HOST}/*=honua:8080,tls=honua-tls"
```

## Verify

```bash
kubectl -n honua rollout status deployment/honua-server --timeout=300s
kubectl -n honua port-forward svc/honua 18080:8080 &
sleep 2 && curl -s http://127.0.0.1:18080/healthz/ready
```

Expected output: `Ready` (and `deployment "honua-server" successfully rolled out`).

## Scaling notes

- Replicas are stateless; scale with `kubectl scale deployment/honua-server --replicas=N` or an HPA on CPU.
- Multi-replica deployments should set `ConnectionStrings__Redis` so caches and durable job state are shared; without Redis each replica falls back to an in-memory cache and job/workflow endpoints return `503`.
- Keep `Limits__Connections__MaxConnectionPoolSize × replicas` under your database `max_connections` — see [Scale and tune performance](scaling-and-performance.md).
- Rolling updates are safe with backward-compatible migrations; see [Upgrade and roll back](upgrade-and-rollback.md).

## Troubleshoot

- **Pods never become ready** — `kubectl logs` the pod; the most common causes are an unreachable database and a master key shorter than 32 characters (both fail at startup).
- **Admin calls return 401** — confirm the secret is mounted (`kubectl -n honua exec deploy/honua-server -- printenv HONUA_ADMIN_PASSWORD`).
- **Browser requests blocked by CORS** — the permissive dev CORS policy is force-disabled when `KUBERNETES_SERVICE_HOST` is present; set `Cors__AllowedOrigins__0` explicitly.
- **gRPC clients fail through the ingress** — native gRPC needs h2c to port 8081; use a gRPC-capable ingress annotation or a separate route.

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Upgrade and roll back](upgrade-and-rollback.md)
- [Deploy on AWS and Azure](cloud-deployments.md)
