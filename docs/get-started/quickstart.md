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
`images.json`, a README, and `AGENTS.md`, the operating rules for AI coding agents
working in the install directory (also published as
[Work with AI coding agents](../guides/connect/working-with-ai-agents.md)). `images.json` pins the serving and executor images as
distinct `ghcr.io/honua-io/honua-server@sha256:...` references in `server` and
`executor`. Executor discovery tags are `nightly-compose-executor-<candidate-sha7>`;
the install uses its digest, not that tag. Both images use the existing public
GHCR package, so installing does not depend on changing a new package's visibility.
The executor is built from
that exact serving candidate. Initialization validates those pins, creates private
per-install secrets and a local key-ring certificate, and saves each secret in its
own file under `secrets/` (for example `secrets/honua_admin_password`). `.env` holds
only non-secret settings. Compose mounts the files at `/run/secrets`, so no secret
is a Compose variable and `docker compose config` never prints one. Restart with
`python3 quickstart.py`; it preserves the credentials and any approved
serving-image override. An install from an earlier package kept its secrets in
`.env`: extract the new package's files over it and run `python3 quickstart.py`
once. It moves each secret into its file unchanged and removes it from `.env`.
Keep `secrets/`, `.env` and the manager's durable Postgres and Redis volumes.

The serving API is at `http://localhost:18080`; the stable management API and MCP
are at `http://localhost:18082`. Its bootstrap credential is the private
`secrets/honua_manager_admin_password` file, distinct from the serving admin
credential in `secrets/honua_admin_password`. Create distinct proposer and approver API keys on the management
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
Any coding agent you run in the install directory should follow
[Work with AI coding agents](../guides/connect/working-with-ai-agents.md).

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
# Secrets are files under ./secrets, mounted at /run/secrets. None is a Compose variable,
# so `docker compose config` never prints one. This entrypoint loads each file named by a
# HONUA_SECRET_FILE__<setting> variable into <setting>, then starts the server.
x-load-secrets: &load-secrets
  - bash
  - +x
  - -euc
  - |
    { set +x; } 2>/dev/null
    # Tracing stays off through exec: inherited xtrace (SHELLOPTS, BASH_ENV) would echo every loaded value.
    # BASH_XTRACEFD is left alone; unsetting it would close the descriptor it names.
    for ref in $${!HONUA_SECRET_FILE__@}; do
      value=$$(< "$${!ref}")
      [ -n "$$value" ] || { echo "empty secret file for $${ref#HONUA_SECRET_FILE__}" >&2; exit 1; }
      export "$${ref#HONUA_SECRET_FILE__}=$$value"
      unset "$$ref"
    done
    if [ -n "$${HONUA_DATABASE_PASSWORD_FILE:-}" ]; then
      value=$$(< "$$HONUA_DATABASE_PASSWORD_FILE")
      [ -n "$$value" ] || { echo "empty database password file" >&2; exit 1; }
      export ConnectionStrings__DefaultConnection="$$ConnectionStrings__DefaultConnection;Password=$$value"
      unset HONUA_DATABASE_PASSWORD_FILE
    fi
    unset value ref
    exec "$$@"
  - load-secrets
services:
  keyring-init:
    image: redis:7.4-alpine
    user: "0:0"
    entrypoint: ["sh", "-c"]
    # The servers run as gid 1001. Secret files stay yours; only their group changes.
    command: ["chown 0:1001 /keyring/keyring.pfx && chmod 0640 /keyring/keyring.pfx && for f in /keyring/*; do [ \"$$f\" = /keyring/keyring.pfx ] || chgrp 1001 \"$$f\"; done"]
    volumes:
      - ./secrets:/keyring
    restart: "no"
  honua:
    image: ${HONUA_IMAGE:?Set the server image}
    platform: linux/amd64
    entrypoint: *load-secrets
    command: ["/app/Honua.Server"]
    ports:
      - "127.0.0.1:${HONUA_HTTP_PORT:?Set an unused port}:8080"
      - "127.0.0.1:${HONUA_GRPC_PORT:-18081}:8081"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      Mcp__WorkflowViews__DefaultView: ${HONUA_MCP_DEFAULT_VIEW:-default}
      Licensing__Mode: Disabled
      AllowedHosts: "localhost;127.0.0.1"
      PUBLIC_BASE_URL: "http://localhost:${HONUA_HTTP_PORT}"
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=honua"
      HONUA_DATABASE_PASSWORD_FILE: /run/secrets/postgres_password
      ConnectionStrings__Redis: redis:6379
      HONUA_SECRET_FILE__HONUA_ADMIN_PASSWORD: /run/secrets/honua_admin_password
      HONUA_SECRET_FILE__AuditLog__ChainVerification__Key: /run/secrets/honua_audit_chain_key
      HONUA_SECRET_FILE__Security__ConnectionEncryption__MasterKey: /run/secrets/honua_master_key
      Operations__SecretChannel__KeyRingCertificatePath: /var/lib/honua/keyring.pfx
      HONUA_SECRET_FILE__Operations__SecretChannel__KeyRingCertificatePassword: /run/secrets/honua_keyring_password
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
    secrets: [postgres_password, honua_admin_password, honua_audit_chain_key, honua_master_key, honua_keyring_password]
  postgres:
    image: pgrouting/pgrouting:17-3.5-3.7.3
    environment:
      POSTGRES_DB: honua
      POSTGRES_USER: honua
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
    secrets: [postgres_password]
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
secrets:
  postgres_password:
    file: ./secrets/postgres_password
  honua_admin_password:
    file: ./secrets/honua_admin_password
  honua_audit_chain_key:
    file: ./secrets/honua_audit_chain_key
  honua_master_key:
    file: ./secrets/honua_master_key
  honua_keyring_password:
    file: ./secrets/honua_keyring_password
configs:
  postgis_init:
    content: |
      CREATE EXTENSION IF NOT EXISTS postgis;
      CREATE EXTENSION IF NOT EXISTS postgis_topology;
      CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;
      CREATE EXTENSION IF NOT EXISTS postgis_tiger_geocoder;
```

## 2. Set the secrets

Settings go in a `.env` file beside `compose.yaml`; each of the five secrets goes
in its own file under `secrets/`. Generate the secrets rather than inventing them.
Keep them out of `.env`: Compose would treat them as variables, and
`docker compose config` would print them.

<!-- docs-validation:install.secrets mode=run -->
```bash
umask 027
cat > .env <<EOF
COMPOSE_PROJECT_NAME=honua-quickstart
HONUA_IMAGE=ghcr.io/honua-io/honua-server:2026.1-rc
HONUA_HTTP_PORT=18080
EOF
mkdir -p secrets
openssl rand -hex 32 > secrets/postgres_password
printf 'Aa1!%s' "$(openssl rand -hex 32)" > secrets/honua_admin_password
openssl rand -hex 32 > secrets/honua_master_key
openssl rand -hex 16 > secrets/honua_keyring_password
openssl rand -base64 32 > secrets/honua_audit_chain_key
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
"@ | Set-Content .env -Encoding Ascii
New-Item -ItemType Directory -Force secrets | Out-Null
New-Secret | Set-Content secrets/postgres_password -Encoding Ascii -NoNewline
"Aa1!$(New-Secret)" | Set-Content secrets/honua_admin_password -Encoding Ascii -NoNewline
New-Secret | Set-Content secrets/honua_master_key -Encoding Ascii -NoNewline
New-Secret | Set-Content secrets/honua_keyring_password -Encoding Ascii -NoNewline
New-AuditKey | Set-Content secrets/honua_audit_chain_key -Encoding Ascii -NoNewline
```

</details>

Both ports bind to loopback only; PostgreSQL and Redis are reachable only on the
project's own network. Change `HONUA_HTTP_PORT` if `18080` is taken, and set
`HONUA_GRPC_PORT` if `18081` is.

Keep `secrets/`. Losing it means losing the database and audit-chain verification keys. The serving server and the package's manager use distinct audit keys; preserve both across restarts and upgrades. Before the server starts, the one-shot Compose step sets each secret file's group to 1001, the image's gid, so the server can read it; the files stay yours with mode `0640`.

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
openssl req -x509 -newkey rsa:2048 \
  -keyout secrets/keyring.pem -out secrets/keyring.crt \
  -days 3650 -nodes -subj "/CN=honua-quickstart"
rm -f secrets/keyring.pfx
openssl pkcs12 -export \
  -inkey secrets/keyring.pem -in secrets/keyring.crt \
  -out secrets/keyring.pfx -passout file:secrets/honua_keyring_password
rm -f secrets/keyring.pem secrets/keyring.crt
chmod 600 secrets/keyring.pfx
```

On Windows, run that block in WSL or Git Bash. `New-SelfSignedCertificate` plus
`Export-PfxCertificate` produces the same PKCS#12 if you would rather stay in PowerShell;
use the contents of `secrets/honua_keyring_password` as the export password.

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

The admin API is not anonymous: it needs the admin password in `secrets/honua_admin_password`,
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
secret = lambda name: open(f"secrets/{name}").read().strip()
key = secret("honua_admin_password")

with HonuaAdminClient(base, api_key=key) as admin:
    connection = admin.create_connection(CreateSecureConnectionRequest(
        name="quickstart",
        host="postgres", port=5432, database_name="honua",
        username="honua", password=secret("postgres_password"),
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
After a completed rollback, omit `idempotencyKey` to propose a fresh convergence to
the declared release. Convergence scopes a supplied key to each target; rollback uses
the supplied key directly. Use a new key for each new operation: reusing one returns
the existing proposal instead of starting another.

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

## Use Studio in Console without the assistant

For the repository development stack, run this from the `honua-server` checkout:

<!-- docs-validation:studio.console mode=skip reason="Repository development stack; the public install replay uses the customer package" -->
```bash
python3 scripts/docker/quickstart.py --profile console
```

Open Console at `http://localhost:5174` (or your configured `HONUA_CONSOLE_PORT`)
and use Studio to author maps, save and reopen versions, and request publication.
The repository's `docker-compose.yml` sets `Studio__EmbedJsStudio=true` on Console
and `Studio__EndUserAuthorization__Enabled=true` on Server. Customer sign-in,
resource authorization, draft ownership and separate publication approval still
apply. These settings enable Studio without enabling model calls; no AI overlay,
provider credentials or invocation ledger is needed.

This command uses the repository development Compose file, not the customer
package's `compose.yaml` described above. Configure customer authentication for
Console and Server before using Studio as an end user.

## Enable the built-in Studio assistant (optional)

The default quickstart keeps model calls disabled. To use the built-in browser
assistant on Bedrock, follow [Studio assistant on Bedrock](../guides/deploy/studio-assistant-bedrock.md).
From the repository checkout, add the assistant overlay to the same Console profile:

<!-- docs-validation:studio.assistant mode=skip reason="Optional Bedrock assistant requires operator disclosure approval and invocation admission" -->
```bash
python3 scripts/docker/quickstart.py --profile console -f docker-compose.yml -f docker-compose.studio-ai.yml
```

The opt-in `docker-compose.studio-ai.yml` overlay adds the tool-capable provider,
exact disclosure approval and stable PostgreSQL admission identity to Studio.
Initialize and resume the invocation ledger before submitting a prompt.
Use an updated Console image; older images calling `/api/v1/agent/workspaces/`
require a Console rebuild, since that separate API is absent from Server trunk.
