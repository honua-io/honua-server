---
type: guide
title: "Quickstart: install, publish, and query"
description: "Run Honua with Docker Compose, publish a dataset, and query it over the protocols - in about ten minutes, on any machine that runs Docker."
resource: "https://github.com/honua-io/honua-server/pkgs/container/honua-server"
---
# Quickstart: install, publish, and query

The customer Compose package runs Honua beside PostGIS and Redis, plus a stable
management service and a separate Compose executor. The executor only executes
approved proposals; a different principal must approve the proposer's request.
The serving container has no Docker access.

## Install the customer package

Use `honua-compose-quickstart.tar.gz` from your release's install assets. The nightly
candidate build emits the same archive as `honua-compose-quickstart-<candidate-sha7>`.
Release promotion must copy that archive and its checksum into the customer install
assets without replacing its image lock with channel tags. Check the archive checksum,
extract it into a dedicated local directory, and run `python3 quickstart.py` there.
Docker Compose, Python 3.11+, and OpenSSL 3 are required; use WSL on Windows.
No compiler or host Docker CLI bind mounts are needed.

The archive contains `compose.yaml`, `compose.executor.yaml`, `quickstart.py`,
`images.json`, and a README. `images.json` pins the serving and executor images as
distinct `ghcr.io/honua-io/honua-server@sha256:...` references in `server` and
`executor`. Executor discovery tags are `nightly-compose-executor-<candidate-sha7>`;
the install uses its digest, not that tag. Both images use the existing public
GHCR package, so installing does not depend on changing a new package's visibility.
The executor is built from
that exact serving candidate. Initialization validates those pins, creates private
per-install secrets and a local key-ring certificate, and saves them in `.env` and
`secrets/`. Restart with `python3 quickstart.py`; it preserves the credentials and
any approved serving-image override. Keep these files and the manager's durable
Postgres and Redis volumes.

The serving API is at `http://localhost:18080`; the stable management API and MCP
are at `http://localhost:18082`. Its bootstrap credential is
`HONUA_MANAGER_ADMIN_PASSWORD` in the private `.env`, distinct from the serving
admin credential. Create distinct proposer and approver API keys on the management
service. Propose through its MCP endpoint, then approve with the other principal.
Self approval is refused. The operation handle records execution and final readback;
an awaiting-approval proposal has not changed the serving stack.

The executor runs as uid 1002, separately from the servers' uid 1001. Only it gets
the Docker socket and this install directory; only the manager shares its private
Unix control socket. The executor allowlists this Compose project, its `honua`
service, and immutable release images. Docker socket access itself grants host
control, so use a dedicated local Docker host. This is an application scope boundary,
not a Docker daemon authorization boundary.

For convergence to a later release, set the release image, worker image, and version
in `.env` from that release's lock and pull the serving image before approval.
Rollback across an incompatible migration requires the verified pre-migration
backup; unknown schemas and unsupported configuration changes fail closed. The
executor retains backups under `backups/`; file storage needs its own backup.
See [upgrade and rollback](../guides/deploy/upgrade-and-rollback.md).

## Serving configuration reference

The following recipe documents the serving portion included in the package and
its publish/query checks. For approved operations, install the complete package
above, which also starts the management and executor services by default.

Prefer an agent-led walkthrough? Start with [Get started with AI](with-ai.md),
including its [local prerequisite check](with-ai.md#agent-prerequisite-check).

**You need** Docker with Compose 2.23.1 or later, Python 3.11+ with working venv and pip support for the last
step, and OpenSSL 3 with a POSIX shell (`grep`, `cut`, `chmod`) for the key-ring
certificate. On Windows, run that certificate block in WSL or Git Bash. Nothing
else: no repository checkout, no compiler, and **no registry credentials** —
the server image and both Python clients are public. The 2026.1 release runs
with licensing disabled, so there is no licence or edition-selection step.

The server image is the 2026.1 release channel
`ghcr.io/honua-io/honua-server:2026.1-rc` (`ghcr.io/honua-io/honua-server:2026.1` at GA).
Only release promotion moves that tag. The exact digest for a release is
`server.image` in that release's `customer-install-manifest.json`.
Step 2 sets `HONUA_IMAGE` to the channel tag. The public image and the pinned
clients below can be used without registry credentials.

The clients are pinned:
[honua-admin 0.1.12](https://pypi.org/project/honua-admin/0.1.12/) and
[honua-sdk 0.1.19](https://pypi.org/project/honua-sdk/0.1.19/).

For a lasting deployment, continue to
[production Compose](../guides/deploy/docker-compose.md). For a locked-down
single-host install of the same image and pinned clients, with startup and
recovery checks scripted, see [Linux](linux-packages.md) or
[Windows](windows-packages.md).

## 1. Create the project

Make a directory and put this in it as `compose.yaml`:

<!-- docs-validation:install.compose mode=run -->
```yaml
services:
  keyring-init:
    image: redis:7.4-alpine
    user: "0:0"
    entrypoint: ["sh", "-c"]
    command: ["chown 0:1001 /keyring/keyring.pfx && chmod 0640 /keyring/keyring.pfx"]
    volumes:
      - ./secrets:/keyring
    restart: "no"
  honua:
    image: ${HONUA_IMAGE:?Set the server image}
    platform: linux/amd64
    ports:
      - "127.0.0.1:${HONUA_HTTP_PORT:?Set an unused port}:8080"
      - "127.0.0.1:${HONUA_GRPC_PORT:-18081}:8081"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Licensing__Mode: Disabled
      AllowedHosts: "localhost;127.0.0.1"
      PUBLIC_BASE_URL: "http://localhost:${HONUA_HTTP_PORT}"
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=honua;Password=${POSTGRES_PASSWORD:?Required}"
      ConnectionStrings__Redis: redis:6379
      HONUA_ADMIN_PASSWORD: ${HONUA_ADMIN_PASSWORD:?Required}
      AuditLog__ChainVerification__Key: ${HONUA_AUDIT_CHAIN_KEY:?Required}
      Security__ConnectionEncryption__MasterKey: ${HONUA_MASTER_KEY:?Required}
      Operations__SecretChannel__KeyRingCertificatePath: /var/lib/honua/keyring.pfx
      Operations__SecretChannel__KeyRingCertificatePassword: ${HONUA_KEYRING_PASSWORD:?Required}
      Cors__AllowedOrigins__0: "http://localhost:${HONUA_HTTP_PORT}"
      Database__MigrationSafety__ContractApplyPolicy: Gate
      FileStorage__Provider: Local
      FileStorage__LocalStorage__BasePath: /var/lib/honua/storage
    depends_on:
      keyring-init:
        condition: service_completed_successfully
      postgres:
        condition: service_healthy
      redis:
        condition: service_healthy
    restart: unless-stopped
    read_only: true
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    tmpfs:
      - /tmp:noexec,nosuid,size=100m
    volumes:
      - storage:/var/lib/honua/storage
      - ./secrets/keyring.pfx:/var/lib/honua/keyring.pfx:ro
  postgres:
    image: pgrouting/pgrouting:17-3.5-3.7.3
    environment:
      POSTGRES_DB: honua
      POSTGRES_USER: honua
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Required}
    volumes:
      - postgres:/var/lib/postgresql/data
    configs:
      - source: postgis_init
        target: /docker-entrypoint-initdb.d/10-honua-postgis.sql
    healthcheck:
      test: ["CMD-SHELL", "test \"$$(head -n 1 /var/lib/postgresql/data/postmaster.pid 2>/dev/null)\" = \"1\" && pg_isready -U honua -d honua"]
      interval: 5s
      timeout: 5s
      retries: 30
    restart: unless-stopped
  redis:
    image: redis:7.4-alpine
    command: redis-server --appendonly yes --maxmemory 64mb --maxmemory-policy noeviction
    volumes:
      - redis:/data
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 5s
      timeout: 3s
      retries: 30
    restart: unless-stopped
volumes:
  postgres:
  redis:
  storage:
configs:
  postgis_init:
    content: |
      CREATE EXTENSION IF NOT EXISTS postgis;
      CREATE EXTENSION IF NOT EXISTS postgis_topology;
      CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;
      CREATE EXTENSION IF NOT EXISTS postgis_tiger_geocoder;
```

## 2. Set the secrets

Six secrets go in a `.env` file beside `compose.yaml`. Generate them rather
than inventing them.

<!-- docs-validation:install.secrets mode=run -->
```bash
umask 077
cat > .env <<EOF
COMPOSE_PROJECT_NAME=honua-quickstart
HONUA_IMAGE=ghcr.io/honua-io/honua-server:2026.1-rc
HONUA_HTTP_PORT=18080
POSTGRES_PASSWORD=$(openssl rand -hex 32)
HONUA_ADMIN_PASSWORD=Aa1!$(openssl rand -hex 32)
HONUA_MASTER_KEY=$(openssl rand -hex 32)
HONUA_KEYRING_PASSWORD=$(openssl rand -hex 16)
HONUA_AUDIT_CHAIN_KEY=$(openssl rand -base64 32)
HONUA_MANAGER_AUDIT_CHAIN_KEY=$(openssl rand -base64 32)
EOF
```

<details>
<summary>PowerShell</summary>

<!-- docs-validation:install.windows-secrets mode=skip reason="Native Windows alternative" -->
```powershell
function New-Secret { -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) }) }
function New-AuditKey {
    $bytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes); [Convert]::ToBase64String($bytes) } finally { $random.Dispose() }
}
@"
COMPOSE_PROJECT_NAME=honua-quickstart
HONUA_IMAGE=ghcr.io/honua-io/honua-server:2026.1-rc
HONUA_HTTP_PORT=18080
POSTGRES_PASSWORD=$(New-Secret)
HONUA_ADMIN_PASSWORD=Aa1!$(New-Secret)
HONUA_MASTER_KEY=$(New-Secret)
HONUA_KEYRING_PASSWORD=$(New-Secret)
HONUA_AUDIT_CHAIN_KEY=$(New-AuditKey)
HONUA_MANAGER_AUDIT_CHAIN_KEY=$(New-AuditKey)
"@ | Set-Content .env -Encoding Ascii
```

</details>

Both ports bind to loopback only; PostgreSQL and Redis are reachable only on the
project's own network. Change `HONUA_HTTP_PORT` if `18080` is taken, and set
`HONUA_GRPC_PORT` if `18081` is.

Keep `.env`. Losing it means losing the database and audit-chain verification keys. The serving server and optional manager use distinct audit keys; preserve both across restarts and upgrades.

Production stores operation secrets in Redis. The server will not start until a certificate
encrypts that key ring, so a copy of Redis does not hold both the secrets and the keys. This
file is not a license. Generate a local RSA PKCS#12 and keep it private on the host. The
published image runs as uid 1001 and gid 1001. Before the server starts, Compose runs a
one-shot root step that sets the mount to owner root, group 1001, mode `0640`. That mode
has no other-read, and `openssl` cannot replace a root-owned `0640` file. The directory
stays yours, so the block removes `secrets/keyring.pfx` before the export. The export
password still encrypts the PKCS#12.

<!-- docs-validation:install.keyring mode=run -->
```bash
mkdir -p secrets
HONUA_KEYRING_PASSWORD="$(grep '^HONUA_KEYRING_PASSWORD=' .env | cut -d= -f2)"
openssl req -x509 -newkey rsa:2048 \
  -keyout secrets/keyring.pem -out secrets/keyring.crt \
  -days 3650 -nodes -subj "/CN=honua-quickstart"
rm -f secrets/keyring.pfx
openssl pkcs12 -export \
  -inkey secrets/keyring.pem -in secrets/keyring.crt \
  -out secrets/keyring.pfx -passout "pass:${HONUA_KEYRING_PASSWORD}"
rm -f secrets/keyring.pem secrets/keyring.crt
chmod 600 secrets/keyring.pfx
```

On Windows, run that block in WSL or Git Bash. `New-SelfSignedCertificate` plus
`Export-PfxCertificate` produces the same PKCS#12 if you would rather stay in PowerShell;
put the export password in `HONUA_KEYRING_PASSWORD`.

## 3. Start it

<!-- docs-validation:install.start mode=run -->
```bash
docker compose up -d --wait --wait-timeout 180
```

`--wait` holds for container health checks. The preview image's container probe
checks liveness; also open `http://localhost:18080/healthz/ready` (using your
selected port) and wait for `Ready`. The authenticated publish/query below then
verifies the data path. If startup times out,
`docker compose logs honua postgres redis` says why.

The admin API is not anonymous: it needs the `HONUA_ADMIN_PASSWORD` from `.env`,
which the next step reads for you.

## 4. Publish a table and query it back

Put a table in the bundled PostGIS. Any SQL client works; the compose project
already has one. Create it in the `public` schema. The database role is also
named `honua`, and PostgreSQL searches a schema of that name before `public`.
Once the server has started, that `honua` schema holds its catalog, so an
unqualified `CREATE TABLE` would put `places` there. The publish step below
reads `public.places`.

<!-- docs-validation:install.seed mode=run -->
```bash
docker compose exec -T postgres psql -U honua -d honua <<'SQL'
CREATE TABLE public.places (
  id   serial PRIMARY KEY,
  name text NOT NULL,
  geom geometry(Point, 4326) NOT NULL
);
INSERT INTO public.places (name, geom) VALUES
  ('west', ST_SetSRID(ST_MakePoint(-157.875, 21.3125), 4326)),
  ('east', ST_SetSRID(ST_MakePoint(-155.0625, 19.6875), 4326));
SQL
```

Install the two public clients, ideally in a virtual environment:

<!-- docs-validation:install.clients mode=run -->
```bash
python -m venv .venv && . .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install 'honua-admin==0.1.12' 'honua-sdk==0.1.19'
```

Save this as `quickstart.py`. It registers that database as a connection,
publishes the table as a layer, and queries it back:

<!-- docs-validation:install.publish mode=run -->
```python
import json
from honua_admin import HonuaAdminClient, CreateSecureConnectionRequest, PublishLayerRequest
from honua_sdk import HonuaClient

env = dict(line.strip().split("=", 1) for line in open(".env") if "=" in line)
base = "http://localhost:" + env["HONUA_HTTP_PORT"]
key = env["HONUA_ADMIN_PASSWORD"]

with HonuaAdminClient(base, api_key=key) as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="quickstart",
        host="postgres", port=5432, database_name="honua",
        username="honua", password=env["POSTGRES_PASSWORD"],
        ssl_mode="Disable", ssl_required=False))

    layer = admin.publish_layer(connection.connection_id, PublishLayerRequest(
        schema="public", table="places", layer_name="places",
        service_name="quickstart", srid=4326,
        geometry_column="geom", geometry_type="Point",
        primary_key="id", fields_list=["id", "name"]))

with HonuaClient(base, api_key=key) as client:
    result = client.query_features("quickstart", layer.layer_id,
                                   out_fields=["id", "name"], return_geometry=True)

print(f"published service quickstart layer {layer.layer_id}")
print(json.dumps(result["features"], indent=2))
```

<!-- docs-validation:install.query mode=run -->
```bash
python quickstart.py
```

The first line is the service name and layer id. Two features follow, with their
geometry, in EPSG:4326.

`admin.discover_tables(connection.connection_id)` lists what else is in that
database, with the schema, geometry column and SRID `publish_layer` wants — which
is how you publish a table you did not create yourself.

The layer is now served over every protocol the deployment advertises, from that
one publish — OGC API Features at `/ogc/features/collections`, GeoServices REST at
`/rest/services/quickstart/FeatureServer/0`, plus WMS, WFS, vector tiles and the
rest. `admin.list_services()` and `admin.get_capabilities()` report what this
deployment actually exposes, rather than what the list above assumes.

## Approved operations and recovery

The customer package includes [`compose.executor.yaml`](compose.executor.yaml) by
default. The manager has its own database and credentials so its startup cannot
migrate the serving database before approval. Its durable state survives serving
container replacement. Proposals execute only after a different principal approves.
The executor accepts no arbitrary shell command, project, or service from a model.

The package initializes the project directory and backup directory for private group
access, sets the host Docker group in `DOCKER_GID`, and initializes the Unix socket
volume for uid 1002. The Docker client and Compose plugin ship in the dedicated
executor image. Neither HTTP server receives the Docker socket or host binaries.

`HONUA_PRIOR_IMAGE` and `HONUA_PRIOR_MAX_SCHEMA` identify the retained immutable prior
image and the highest migration shipped in that exact image. The package derives the
initial schema from the same candidate source. For an older image without a schema
label, supply its exact shipped schema rather than guessing. A reader-incompatible
rollback restores a verified database backup before starting the prior image. It
uses the image ID recorded in the backup. File storage is not part of this restore.
Contract migration nonces still use `HONUA_MIGRATE_ONLY` and are not bypassed.

The first approved convergence records the verified installed revision as the rollback
baseline. After convergence completes, `honua_propose_rollback` can omit `toRevision`
and `backupId`: it selects the prior serving revision and previews a verified matching
backup. Review the selected revision and backup in the proposal's `diff` and `dryRun`
before a separate principal approves it. Execution verifies the same backup again.

Poll the proposal through the management API or `honua://proposals/{id}`. Both expose
`Completed` or `Failed` after the canonical execution reaches that state; failures
include `resolutionReason`. The proposal list retains the approval lifecycle; use detail
or the MCP resource for execution status. A failed local Compose operation releases
exclusive ownership once its child deployment, if any, is also verified terminal. Restore the failed dependency and
create a new convergence proposal with a new `idempotencyKey`. Active, missing, or
uncertain execution records keep ownership until their state can be established.

## What to do next

| | |
| --- | --- |
| **[Publish your own data](first-dataset.md)** | Connect an existing PostGIS database instead of the bundled one. |
| **[Put it on a map](first-map.md)** | A MapLibre map reading the layer you just published. |
| **[Pick a protocol](../concepts/protocols.md)** | What each surface is for, and which clients speak it. |
| **[Deploy it properly](../guides/deploy/docker-compose.md)** | Production Compose: TLS, backups, resource limits. |

## Stop or remove it

<!-- docs-validation:install.cleanup mode=skip reason="Harness tears down its isolated project on every exit" -->
```bash
docker compose down              # stop, keep all data
docker compose down --volumes    # stop and permanently delete this project's data
```

`down --volumes` removes this project's data, including management state and audit history. Before
storing anything you cannot lose, read
[backup and recovery](../guides/deploy/backup-and-restore.md) — a restart is not
a backup.

## If something goes wrong

- **`port is already allocated`** — change `HONUA_HTTP_PORT` or `HONUA_GRPC_PORT`
  in `.env` and run `docker compose up -d` again.
- **Readiness times out** — `docker compose logs honua postgres redis`. Do not
  switch to the Development environment or disable preflight to get past a
  startup failure; it is telling you something.
- **`KeyRingCertificatePath` is required** — the key-ring PKCS#12 from step 2 is
  missing or not readable. Re-run that `openssl` block. It removes
  `secrets/keyring.pfx` before the export, which is what makes a second run work
  after Compose has set the file to root, group 1001, mode `0640`. If `rm` is
  denied, run `sudo chown "$USER" secrets secrets/keyring.pfx` and then the block
  again. Then `docker compose up` so the root-owned mode `0640` mount is applied
  before the server starts. Do not grant other-read on the file.
- **A pull fails** — distinguish an unpublished tag (`not found`), registry
  authorization failure, unsupported image platform, and network/proxy failure.
  These are intended public artifacts; registry credentials do not fix an
  unpublished tag. The optional .NET client uses a different registry; see
  [registry clients](registry-clients.md) only if you need it.
- **Anything else** — [troubleshooting](../guides/deploy/troubleshooting.md).

## Enable the built-in Studio assistant (optional)

The default quickstart keeps model calls disabled. To use the built-in browser
assistant on Bedrock, follow [Studio assistant on Bedrock](../guides/deploy/studio-assistant-bedrock.md).
The opt-in `docker-compose.studio-ai.yml` overlay sets Studio customer authorization,
the tool-capable provider, exact disclosure approval and stable PostgreSQL admission
identity. Initialize and resume the invocation ledger before submitting a prompt.
Use an updated Console image; older images calling `/api/v1/agent/workspaces/`
require a Console rebuild, since that separate API is absent from Server trunk.
