---
type: runbook
title: "Deploy with Docker Compose"
description: "Take a single-node Compose install to production: a TLS edge, backups you have restored from, and a rollback path."
---
# Deploy with Docker Compose

This is the **single-node** path: one host you administer, running Compose. It
covers what the quickstart leaves out — terminating TLS in front of it, taking
backups you have actually restored from, and upgrading without losing the
database.

**Deploying to a cloud account instead?** That path is
[honua-iac](https://github.com/honua-io/honua-iac) — public Terraform modules for
ECS/Fargate, Lambda, Azure Container Apps, Azure Functions, EKS and AKS. See
[Deploy on AWS and Azure](cloud-deployments.md) to pick a pattern, then
[the operator deployment guide](https://github.com/honua-io/honua-iac/blob/trunk/docs/operator-deployment.md) to stand it up.
Nothing below applies to those; they do not use Compose.

**Start from the [quickstart](../../get-started/quickstart.md).** It gives you
the `compose.yaml`, the `.env` and a published layer in about ten minutes, on any
machine that runs Docker; everything here assumes that project directory and adds
to it. To install the server as a native package instead of a container, see
[Linux](../../get-started/linux-packages.md) or
[Windows](../../get-started/windows-packages.md).

Commands are `bash`, with a collapsed PowerShell equivalent wherever the two
differ. `dc` is shorthand throughout:

```bash
dc() { docker compose --env-file .env -f compose.yaml "$@"; }
```

Compose 2.23.1 or later is required. Nothing here needs a repository checkout, a
compiler, or a registry credential.

## Public TLS edge

The installed server publishes loopback ports `127.0.0.1:18080` for HTTP/1 REST
and gRPC-Web, and `127.0.0.1:18081` for native HTTP/2 gRPC (or your selected ports).
For a public deployment, put an existing TLS-terminating reverse proxy on the
same host in front of those loopback ports, arrange DNS and certificates for your
hostname, and permit access to the proxy through the host firewall. Do not expose
PostgreSQL, Redis or the unauthenticated health probe on a separate public port.
Configure the Docker daemon to start on boot so the stack comes back after a host restart.

After the proxy is configured, set its hostname, browser origin and actual source
address as seen by Honua. The original loopback route remains
available for local diagnostics and recovery. This example accepts one forwarded
hop; a multi-proxy deployment needs its own explicit trust configuration.

Set three values: the hostname the proxy serves, the browser origin allowed to
call the API, and the proxy's own source address as Honua sees it. Never use a
wildcard or a whole network.

```bash
PUBLIC_HOST=honua.example.com           # DNS name, no scheme or path
BROWSER_ORIGIN=https://app.example.com  # including https://
PROXY_ADDRESS=10.0.0.7                  # exact source IP as Honua sees it

cat > edge.yaml <<EOF
services:
  honua:
    environment:
      AllowedHosts: "${PUBLIC_HOST};localhost;127.0.0.1"
      PUBLIC_BASE_URL: "https://${PUBLIC_HOST}"
      Cors__AllowedOrigins__0: "${BROWSER_ORIGIN}"
      ForwardedHeaders__Enabled: "true"
      ForwardedHeaders__ForwardLimit: "1"
      ForwardedHeaders__KnownProxies__0: "${PROXY_ADDRESS}"
EOF

# the overlay joins every later dc invocation
dc() { docker compose --env-file .env -f compose.yaml -f edge.yaml "$@"; }

dc config --quiet
dc up -d --wait --wait-timeout 180
```

Then read a published layer back through the public hostname, not through
loopback — point `HONUA_BASE_URL` at `https://${PUBLIC_HOST}` and re-run the
query from the [quickstart](../../get-started/quickstart.md#4-publish-a-table-and-query-it-back).
A readback over loopback proves nothing about the edge.


Retain `edge.yaml` with the installation, and redefine `dc` with the overlay in
any new shell. Recheck the proxy source address after replacing the proxy or Docker network.
Never disable certificate validation to make public readback pass. DNS, TLS and
host reboot behavior must also be checked on the actual deployment host.

### Native gRPC at the TLS edge

Enable HTTP/2 on the public TLS listener and route native gRPC requests to the
h2c upstream `127.0.0.1:18081`, preserving the service/method path, authorization
metadata and gRPC trailers. Keep HTTP/1 REST and gRPC-Web on `127.0.0.1:18080`.
A proxy that downgrades the native gRPC upstream to HTTP/1 cannot serve it.

Configure your proxy's native gRPC route by content type (`application/grpc`,
including its `+proto` form), excluding `application/grpc-web` traffic. For NGINX,
use a dedicated native gRPC TLS virtual host with `http2 on` and a `location /`
containing `grpc_pass grpc://127.0.0.1:18081`; add that hostname to `AllowedHosts`,
provide its DNS/certificate, and preserve `Host`, `X-Forwarded-Proto` and the
trusted proxy's `X-Forwarded-For`. The [NGINX gRPC module](https://nginx.org/en/docs/http/ngx_http_grpc_module.html)
documents HTTP/2 forwarding and trailer handling.

Validate the proxy configuration and perform an authenticated native gRPC SDK
query through its public hostname before admitting traffic. The HTTP readback
above verifies the REST route only; TLS/gRPC qualification must use the actual
proxy and deployment host.

## Redis is optional; PostGIS is not

PostGIS owns the catalog, layer metadata and feature data and is required.
### Redis is configured but not entitled

Redis is included with persistent storage, but installing Redis does not grant
paid features. Community can complete the small synchronous import above.
Durable jobs, workflows, queued imports, proposals and shared coordination need
both Redis configuration and the applicable `caching.redis` licence entitlement.
Without the dependency those operations return a typed `dependency-unavailable`
receipt; without the entitlement the receipt is `license-required`.
Do not add a development edition grant to a Production deployment.

To omit Redis, remove `ConnectionStrings__Redis`, the `honua.depends_on.redis`
entry, the `redis` service and the `redis` volume declaration from `compose.yaml`.
Keep a single server instance. Restore those four entries and provide the
appropriate licence before enabling durable operations. A persisted Redis volume
must be retained if it already contains job or workflow state.

## Backup and recovery

A restart or container recreation preserves volumes but is not a backup. Before
an upgrade, stop Honua so it cannot write while database and file-storage snapshots
are taken. This small-install procedure pauses service for the backup. It keeps
Redis out of the backup: deployments using durable jobs/workflows must also follow
the coordinated [backup and restore policy](backup-and-restore.md) before using
this recipe with live customer data.

Write the binary files inside the containers and copy them out, rather than
redirecting binary streams through the shell. The one-off archive container runs
as root with file-ownership capabilities so the storage owner is preserved; the
running server keeps its unprivileged, dropped-capabilities configuration. Keep
`$Backup` — the restore needs it.

Take the backup:

```bash
Backup="$Install/backup-$(date +%Y%m%d-%H%M%S)"
mkdir -m 700 "$Backup"
cp .env compose.yaml journey.py published-layer.json installed-packages.txt "$Backup/"
dc stop honua
dc exec -T postgres pg_dump -U honua -d honua -Fc -f /tmp/honua-backup.dump
dc cp postgres:/tmp/honua-backup.dump "$Backup/database.dump"
dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint tar -v "$Backup:/backup" honua -czf /backup/storage.tar.gz -C /var/lib/honua/storage .
sha256sum "$Backup/database.dump" "$Backup/storage.tar.gz"
dc start honua
wait_honua_ready
"$Python" journey.py --verify-only
```

<details>
<summary>PowerShell</summary>

```powershell
$Backup = Join-Path $Install ('backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $Backup | Out-Null
Copy-Item -LiteralPath .env, compose.yaml, journey.py, published-layer.json, installed-packages.txt -Destination $Backup
if (Test-Path -LiteralPath edge.yaml) { Copy-Item -LiteralPath edge.yaml -Destination $Backup }
dc stop honua
try {
    dc exec -T postgres pg_dump -U honua -d honua -Fc -f /tmp/honua-backup.dump
    dc cp postgres:/tmp/honua-backup.dump (Join-Path $Backup 'database.dump')
    dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint tar -v "${Backup}:/backup" honua -czf /backup/storage.tar.gz -C /var/lib/honua/storage .
    Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $Backup 'database.dump'), (Join-Path $Backup 'storage.tar.gz') | Format-Table
} finally { dc start honua }
Wait-HonuaReady
& $Python journey.py --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Post-backup readback failed' }
```

</details>

If any backup command fails, retain its error and run `dc start honua` to
resume service after investigation; do not use a partial backup. Store a verified
copy of the private backup off-host using your organization's backup system.

**Restore is destructive to this installation's database and file storage.** Use it only when you
intend to replace the database with the saved backup. Stop incoming requests and
Honua first. These commands restore into the same project using the original
credentials. They drop and recreate only the `honua` database in this project
to restore partitioned tables without inherited-constraint conflicts; they do not
delete volumes or touch other projects. For a fresh
host, restore the saved installation files first, install the recorded clients,
then start only PostgreSQL and Redis before continuing. File storage
is cleared inside this project's mounted `/var/lib/honua/storage` directory before
extraction, including hidden files and nested paths. Keep Honua stopped if any
restore command fails; do not resume with a partial database or storage restore.



Restore it, using the backup directory from above:

```bash
test -f "$Backup/database.dump"
test -f "$Backup/storage.tar.gz"
dc stop honua
dc cp "$Backup/database.dump" postgres:/tmp/honua-restore.dump
dc exec -T postgres dropdb -U honua --force --if-exists honua
dc exec -T postgres pg_restore -U honua -d postgres --create --exit-on-error /tmp/honua-restore.dump
dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint sh -v "$Backup:/backup:ro" honua -c 'tar -tzf /backup/storage.tar.gz >/dev/null && find /var/lib/honua/storage -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && tar -xzf /backup/storage.tar.gz -C /var/lib/honua/storage'
dc start honua
wait_honua_ready
"$Python" journey.py --verify-only
```

<details>
<summary>PowerShell</summary>

```powershell
$Backup = 'C:\path\to\the\verified\backup'
if (-not (Test-Path -LiteralPath (Join-Path $Backup 'database.dump'))) { throw 'Database backup not found' }
if (-not (Test-Path -LiteralPath (Join-Path $Backup 'storage.tar.gz'))) { throw 'Storage backup not found' }
dc stop honua
dc cp (Join-Path $Backup 'database.dump') postgres:/tmp/honua-restore.dump
dc exec -T postgres dropdb -U honua --force --if-exists honua
dc exec -T postgres pg_restore -U honua -d postgres --create --exit-on-error /tmp/honua-restore.dump
dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint sh -v "${Backup}:/backup:ro" honua -c 'tar -tzf /backup/storage.tar.gz >/dev/null && find /var/lib/honua/storage -mindepth 1 -maxdepth 1 -exec rm -rf -- {} + && tar -xzf /backup/storage.tar.gz -C /var/lib/honua/storage'
dc start honua
Wait-HonuaReady
& $Python journey.py --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Restored data readback failed; keep this installation out of service' }
```

</details>

## Upgrade & Rollback

Use the signed release lock's next server digest and compatible clients together.
Retain the previous `.env`, installed-package list and backup. A single-node
Compose upgrade has downtime. Review migration preflight through the published
admin client or the [API explorer](../../reference/openapi-and-explorer.md), then
follow [upgrade and rollback](upgrade-and-rollback.md). Keep the Gate migration
policy; a contract migration needs its specific approval nonce. Never skip
migrations or erase volumes to bypass a failure. N-1 candidate qualification remains a separate release step.

## Diagnostics and scoped teardown

```bash
dc ps
dc logs --no-color --tail 150 honua postgres redis
docker image inspect "$(grep '^HONUA_IMAGE=' .env | cut -d= -f2-)" --format '{{json .RepoDigests}}'
pip freeze
```

For support, retain the image digest, installed-package list, HTTP status,
timestamp, and relevant error/correlation ID. Inspect logs before sharing them;
do not send `.env`, full Compose rendering, credentials, or customer records.

- **Port already allocated:** change the conflicting `HONUA_HTTP_PORT` or
  `HONUA_GRPC_PORT` in `.env`, run
  `dc up -d`.
- **Startup exits or readiness times out:** inspect `dc logs`. Keep the image
  digest and the error. Do not switch to the Development environment or disable
  preflight to get past a startup failure; it is telling you something.
- **Registry access fails:** these pins require no package-read credential.
  Check network/proxy policy and retry. The optional NuGet client uses a different
  registry; see [registry clients](../../get-started/registry-clients.md) only if you need .NET.

Stop this installation while retaining all data:

```bash
dc down
```

Only when you intend to permanently discard **this installation's** data, run
the following from its saved directory. It removes this project's containers,
network, and its three project-scoped volumes; it does not prune other projects.

```bash
dc down --volumes
unset HONUA_ADMIN_PASSWORD POSTGRES_PASSWORD
```

The private installation folder remains for deliberate retention or deletion.
