# Deploy on Kubernetes

This guide starts with one coordinated Honua replica and a persistent volume.
That is the safe Kubernetes baseline. Horizontal scaling is a separate,
atomic topology change: `MultiNode`, Redis, and shared cloud file storage must
be enabled together.

The authoritative chart contract lives in
[`honua-helm`](https://github.com/honua-io/honua-helm/blob/trunk/docs/values-contract.md).
The examples below target chart source commit
[`68fcb72f03aab74adc812df48f8f63677c829877`](https://github.com/honua-io/honua-helm/commit/68fcb72f03aab74adc812df48f8f63677c829877).
Pin a published chart version instead when one is available; do not deploy a
moving branch.

## Prerequisites

- A Kubernetes cluster with `kubectl` and Helm 3 access.
- PostGIS and Redis endpoints reachable from the cluster. Redis is required by
  the chart for non-development deployments, including the single-node path.
- A default StorageClass for the single-node persistent volume.
- An OpenTelemetry collector reachable at the endpoint used below.
- An ingress controller and a TLS secret named `honua-tls`.
- A reviewed Honua container digest. Production examples deliberately do not
  use `latest`, `latest-aot`, or another moving tag.

Set and validate the immutable inputs first:

```bash
export HONUA_HELM_REF=68fcb72f03aab74adc812df48f8f63677c829877
export HONUA_IMAGE_DIGEST='sha256:replace-with-64-lowercase-hex-characters'

if ! [[ "$HONUA_IMAGE_DIGEST" =~ ^sha256:[0-9a-f]{64}$ ]]; then
  echo "HONUA_IMAGE_DIGEST must be an immutable sha256 digest" >&2
  exit 1
fi

git clone https://github.com/honua-io/honua-helm.git
git -C honua-helm checkout --detach "$HONUA_HELM_REF"
helm dependency build honua-helm/honua
export CHART_PATH="$PWD/honua-helm/honua"
```

## Single-node baseline

Create the namespace, a single-writer persistent volume claim, and the runtime
Secret. Put real values in the temporary file; the examples are placeholders.
The Redis password must not contain `,` or `=` when written in the
StackExchange.Redis connection-string form shown here.

```bash
kubectl create namespace honua --dry-run=client -o yaml | kubectl apply -f -

kubectl -n honua apply -f - <<'EOF'
apiVersion: v1
kind: PersistentVolumeClaim
metadata:
  name: honua-storage
spec:
  accessModes: [ReadWriteOnce]
  resources:
    requests:
      storage: 20Gi
EOF

umask 077
cat > honua-runtime.env <<'EOF'
ConnectionStrings__DefaultConnection=Host=db.example.com;Database=honua;Username=honua;Password=replace-me;SSL Mode=Require
ConnectionStrings__redis=redis.example.com:6380,ssl=true,password=replace-me
HONUA_ADMIN_PASSWORD=Replace-With-A-Strong-Admin-Password1!
Security__ConnectionEncryption__MasterKey=replace-with-at-least-32-random-characters
EOF
kubectl -n honua create secret generic honua-runtime \
  --from-env-file=honua-runtime.env \
  --dry-run=client -o yaml | kubectl apply -f -
rm -f honua-runtime.env
```

Write `honua-values.yaml`. `config.env` and `secret` are the current chart
surfaces; top-level `env` and `envFrom` are not chart values. The chart already
owns the live, ready, and startup probe definitions, so they do not need to be
repeated here.

<!-- BEGIN KUBERNETES_SINGLE_NODE_VALUES -->
```yaml
replicaCount: 1

image:
  repository: ghcr.io/honua-io/honua-server
  tag: ""
  digest: "" # supplied by --set-string at install time
  pullPolicy: IfNotPresent

config:
  env:
    ASPNETCORE_ENVIRONMENT: "Production"
    Deployment__Mode: "SingleInstance"
    Public__BaseUrl: "https://honua.example.com"
    HONUA_OBSERVABILITY: "true"
    HONUA_OPENTELEMETRY: "true"
    FileStorage__Provider: "Local"
    FileStorage__LocalStorage__BasePath: "/var/lib/honua/storage"
    Limits__Connections__MinConnectionPoolSize: "5"
    Limits__Connections__MaxConnectionPoolSize: "40"
    Limits__Connections__MaxConcurrentQueries: "40"

observability:
  otlpEndpoint: "http://otel-collector.observability.svc:4317"
  otlpProtocol: "grpc"

secret:
  create: false
  name: honua-runtime

extraVolumes:
  - name: storage
    persistentVolumeClaim:
      claimName: honua-storage
extraVolumeMounts:
  - name: storage
    mountPath: /var/lib/honua/storage

autoscaling:
  enabled: false

# Recreate avoids old and new server versions overlapping during inline
# database migrations. It causes planned downtime during an upgrade.
strategy:
  type: Recreate

resources:
  requests:
    cpu: 500m
    memory: 512Mi
  limits:
    cpu: "2"
    memory: 2Gi

ingress:
  enabled: true
  className: nginx
  hosts:
    - host: honua.example.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: honua-tls
      hosts: [honua.example.com]
```
<!-- END KUBERNETES_SINGLE_NODE_VALUES -->

Install with the digest supplied separately so an unset pin fails before Helm:

```bash
helm upgrade --install honua "$CHART_PATH" \
  --namespace honua \
  --values honua-values.yaml \
  --set-string image.digest="$HONUA_IMAGE_DIGEST" \
  --atomic --wait --timeout 10m
```

The PVC makes local files survive pod replacement, but it remains a
single-writer, single-replica topology. Do not run `kubectl scale` on this
deployment. Change to the complete multi-node contract below instead.

## Multi-node topology

Multi-node operation is not just `replicaCount: 2`. It requires all of these
at the same time:

1. `config.env.Deployment__Mode: MultiNode`.
2. A reachable `ConnectionStrings__redis` in the runtime Secret for shared
   coordination and durable jobs.
3. `FileStorage__Provider: AwsS3` or `AzureBlob`; `Local` storage is invalid in
   `MultiNode`, even if a particular PersistentVolume supports multiple mounts.
4. Replica or HPA settings, plus a total database-pool budget that stays below
   the database server's connection limit.

The S3 example uses workload identity. Configure the pod's cloud identity with
bucket access; do not put long-lived cloud keys in `config.env`. Replace the
single-node values file with this complete file rather than layering only its
replica settings:

<!-- BEGIN KUBERNETES_MULTI_NODE_VALUES -->
```yaml
replicaCount: 2

image:
  repository: ghcr.io/honua-io/honua-server
  tag: ""
  digest: "" # supplied by --set-string at install time
  pullPolicy: IfNotPresent

config:
  env:
    ASPNETCORE_ENVIRONMENT: "Production"
    Deployment__Mode: "MultiNode"
    Public__BaseUrl: "https://honua.example.com"
    HONUA_OBSERVABILITY: "true"
    HONUA_OPENTELEMETRY: "true"
    Cache__EnableFallback: "false"
    FileStorage__Provider: "AwsS3"
    FileStorage__AwsS3__BucketName: "honua-production"
    FileStorage__AwsS3__Region: "us-west-2"
    Limits__Connections__MinConnectionPoolSize: "5"
    Limits__Connections__MaxConnectionPoolSize: "40"
    Limits__Connections__MaxConcurrentQueries: "40"

observability:
  otlpEndpoint: "http://otel-collector.observability.svc:4317"
  otlpProtocol: "grpc"

# This existing Secret must contain ConnectionStrings__DefaultConnection,
# ConnectionStrings__redis, HONUA_ADMIN_PASSWORD, and
# Security__ConnectionEncryption__MasterKey.
secret:
  create: false
  name: honua-runtime

autoscaling:
  enabled: true
  minReplicas: 2
  maxReplicas: 6
  targetCPUUtilizationPercentage: 70
  targetMemoryUtilizationPercentage: 0

# Keep the migration-safe rollout default until the target release has explicit
# forward/backward migration compatibility evidence.
strategy:
  type: Recreate

resources:
  requests:
    cpu: 1
    memory: 2Gi
  limits:
    cpu: "4"
    memory: 8Gi

ingress:
  enabled: true
  className: nginx
  hosts:
    - host: honua.example.com
      paths:
        - path: /
          pathType: Prefix
  tls:
    - secretName: honua-tls
      hosts: [honua.example.com]
```
<!-- END KUBERNETES_MULTI_NODE_VALUES -->

For Azure Blob, set `FileStorage__Provider: AzureBlob`, put
`FileStorage__AzureBlob__ConnectionString` in the runtime Secret, and set
`FileStorage__AzureBlob__ContainerName` in `config.env`.

Apply the same digest-pinned command used for the single-node install. The
chart fails closed when more than one replica or HPA is combined with
`SingleInstance`.

## Upgrade and roll back

Back up PostGIS and file storage before every upgrade. The chart's default
`Recreate` strategy prevents old and new Honua versions from serving together
while inline migrations run; a multi-node upgrade therefore has planned
downtime.

Use `RollingUpdate` only when the target migration set has been reviewed as
forward and backward compatible across both images. In that case, apply this
overlay to the complete multi-node values and keep the drain delay shorter than
the termination grace period:

```yaml
strategy:
  type: RollingUpdate
  rollingUpdate:
    maxSurge: 0
    maxUnavailable: 1
lifecycle:
  preStop:
    exec:
      command: ["/bin/sh", "-c", "sleep 5"]
terminationGracePeriodSeconds: 60
```

Then run the digest-pinned `helm upgrade --install ... --atomic --wait` command.
`--atomic` restores Kubernetes resources after a failed upgrade; it does not
undo a database migration or object-store write.

Capture and verify each revision:

```bash
kubectl -n honua rollout status deployment/honua-honua --timeout=600s
helm -n honua test honua
kubectl -n honua get configmap honua-honua-release-info -o yaml
helm -n honua history honua
```

Application rollback comes first when the previous image remains compatible
with the migrated schema:

```bash
helm -n honua rollback honua <known-good-revision> --wait --timeout 10m
kubectl -n honua rollout status deployment/honua-honua --timeout=600s
helm -n honua test honua
```

Helm cannot downgrade the database schema. If the previous image is not
compatible, stop writes and restore the matching PostGIS backup and file-store
snapshot, or roll forward. See [Upgrade and roll back](upgrade-and-rollback.md)
and [Back up and restore](backup-and-restore.md).

After rotating an externally managed Secret, restart the Deployment: Helm
cannot checksum data it does not own.

```bash
kubectl -n honua rollout restart deployment/honua-honua
kubectl -n honua rollout status deployment/honua-honua --timeout=600s
```

## Troubleshoot

- **Helm rejects multiple replicas** — keep one `SingleInstance` replica, or
  apply the complete `MultiNode` values with Redis and cloud file storage.
- **Pods never become ready** — inspect the preflight Job and pod logs. Common
  causes are unreachable PostGIS/Redis, missing Secret keys, or an invalid
  storage provider configuration.
- **Telemetry validation fails at render time** — configure
  `observability.otlpEndpoint` (as shown), enable a supported Prometheus scrape
  target, or explicitly disable both observability flags.
- **Files disappear after pod replacement** — the single-node PVC was not
  mounted at `FileStorage__LocalStorage__BasePath`, or the multi-node cloud
  bucket/container points at the wrong location.
- **Admin calls return 401** — confirm the runtime Secret key names. Do not
  print secret values with `kubectl exec ... printenv` during diagnosis.
- **Native gRPC is unreachable** — this chart contract publishes the HTTP and
  gRPC-Web listener on port 8080. Native h2c gRPC on port 8081 needs a
  separately managed Service/Ingress until the chart exposes that listener.

## Next steps

- [Monitor Honua Server](monitoring.md)
- [Scale and tune performance](scaling-and-performance.md)
- [Deploy on AWS and Azure](cloud-deployments.md)
