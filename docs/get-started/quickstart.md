---
type: guide
title: "Quickstart: install, publish, and query"
description: "Run Honua with Docker Compose, publish a dataset, and query it over the protocols - in about ten minutes, on any machine that runs Docker."
resource: "https://hub.docker.com/r/honuaio/honua-server"
---
# Quickstart: install, publish, and query

Honua runs as one container beside PostGIS and Redis. This page starts it with
Docker Compose, publishes a small dataset, and queries it back.

**You need** Docker with Compose 2.23.1 or later, and Python 3.11+ for the last
step. Nothing else: no repository checkout, no compiler, and **no registry
credentials** — the server image and both Python clients are public, and the
Community edition needs no licence.

Everything below is pinned so a run is reproducible: server
`ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9`,
[honua-admin 0.1.8](https://pypi.org/project/honua-admin/0.1.8/), and
[honua-sdk 0.1.11](https://pypi.org/project/honua-sdk/0.1.11/).

For a lasting deployment, continue to
[production Compose](../guides/deploy/docker-compose.md). If you would rather
install the server as a native package than run a container, see
[Linux](linux-packages.md) or [Windows](windows-packages.md).

## 1. Create the project

Make a directory and put this in it as `compose.yaml`:

```yaml
services:
  honua:
    image: ${HONUA_IMAGE:?Set the immutable server digest}
    platform: linux/amd64
    ports:
      - "127.0.0.1:${HONUA_HTTP_PORT:?Set an unused port}:8080"
      - "127.0.0.1:${HONUA_GRPC_PORT:-18081}:8081"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      AllowedHosts: "localhost;127.0.0.1"
      PUBLIC_BASE_URL: "http://localhost:${HONUA_HTTP_PORT}"
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=honua;Username=honua;Password=${POSTGRES_PASSWORD:?Required}"
      ConnectionStrings__Redis: redis:6379
      HONUA_ADMIN_PASSWORD: ${HONUA_ADMIN_PASSWORD:?Required}
      Security__ConnectionEncryption__MasterKey: ${HONUA_MASTER_KEY:?Required}
      Cors__AllowedOrigins__0: "http://localhost:${HONUA_HTTP_PORT}"
      Database__MigrationSafety__ContractApplyPolicy: Gate
      FileStorage__Provider: Local
      FileStorage__LocalStorage__BasePath: /var/lib/honua/storage
    depends_on:
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

Three secrets go in a `.env` file beside `compose.yaml`. Generate them rather
than inventing them.

```bash
cat > .env <<EOF
COMPOSE_PROJECT_NAME=honua-quickstart
HONUA_IMAGE=ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9
HONUA_HTTP_PORT=18080
POSTGRES_PASSWORD=$(openssl rand -hex 32)
HONUA_ADMIN_PASSWORD=Aa1!$(openssl rand -hex 32)
HONUA_MASTER_KEY=$(openssl rand -hex 32)
EOF
```

<details>
<summary>PowerShell</summary>

```powershell
function New-Secret { -join ((1..64) | ForEach-Object { '{0:x}' -f (Get-Random -Max 16) }) }
@"
COMPOSE_PROJECT_NAME=honua-quickstart
HONUA_IMAGE=ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9
HONUA_HTTP_PORT=18080
POSTGRES_PASSWORD=$(New-Secret)
HONUA_ADMIN_PASSWORD=Aa1!$(New-Secret)
HONUA_MASTER_KEY=$(New-Secret)
"@ | Set-Content .env -Encoding Ascii
```

</details>

Both ports bind to loopback only; PostgreSQL and Redis are reachable only on the
project's own network. Change `HONUA_HTTP_PORT` if `18080` is taken, and set
`HONUA_GRPC_PORT` if `18081` is.

Keep `.env`. Losing it means losing the database.

## 3. Start it

```bash
docker compose up -d --wait --wait-timeout 180
```

Check it came up:

```bash
curl -fsS http://localhost:18080/healthz/ready
```

The admin API is not anonymous — this returns `401`, which is the correct answer:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:18080/api/v1/admin/config
```

## 4. Publish a table and query it back

Put a table in the bundled PostGIS. Any SQL client works; the compose project
already has one:

```bash
docker compose exec -T postgres psql -U honua -d honua <<'SQL'
CREATE TABLE places (
  id   serial PRIMARY KEY,
  name text NOT NULL,
  geom geometry(Point, 4326) NOT NULL
);
INSERT INTO places (name, geom) VALUES
  ('west', ST_SetSRID(ST_MakePoint(-157.875, 21.3125), 4326)),
  ('east', ST_SetSRID(ST_MakePoint(-155.0625, 19.6875), 4326));
SQL
```

Install the two public clients, ideally in a virtual environment:

```bash
python -m venv .venv && . .venv/bin/activate     # Windows: .venv\Scripts\activate
pip install 'honua-admin==0.1.8' 'honua-sdk==0.1.11'
```

Save this as `quickstart.py`. It registers that database as a connection,
publishes the table as a layer, and queries it back:

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

print(json.dumps(result["features"], indent=2))
```

```bash
python quickstart.py
```

Two features come back with their geometry, in EPSG:4326.

`admin.discover_tables(connection.connection_id)` lists what else is in that
database, with the schema, geometry column and SRID `publish_layer` wants — which
is how you publish a table you did not create yourself.

The layer is now served over every protocol the deployment advertises: OGC API
Features at `/ogc/features/collections`, GeoServices REST at
`/rest/services/quickstart/FeatureServer/0`, WMS, WFS, vector tiles, and the rest.

```bash
curl -fsS 'http://localhost:18080/ogc/features/collections' -H "X-API-Key: $HONUA_ADMIN_PASSWORD"
```

## What to do next

| | |
| --- | --- |
| **[Publish your own data](first-dataset.md)** | Connect an existing PostGIS database instead of the bundled one. |
| **[Put it on a map](first-map.md)** | A MapLibre map reading the layer you just published. |
| **[Pick a protocol](../concepts/protocols.md)** | What each surface is for, and which clients speak it. |
| **[Deploy it properly](../guides/deploy/docker-compose.md)** | Production Compose: TLS, backups, resource limits. |

## Stop or remove it

```bash
docker compose down              # stop, keep all data
docker compose down --volumes    # stop and permanently delete this project's data
```

`down --volumes` removes this project's three volumes and nothing else. Before
storing anything you cannot lose, read
[backup and recovery](../guides/deploy/backup-and-restore.md) — a restart is not
a backup.

## If something goes wrong

- **`port is already allocated`** — change `HONUA_HTTP_PORT` or `HONUA_GRPC_PORT`
  in `.env` and run `docker compose up -d` again.
- **Readiness times out** — `docker compose logs honua postgres redis`. Do not
  switch to the Development environment or disable preflight to get past a
  startup failure; it is telling you something.
- **A pull fails** — these pins need no credentials, so it is network or proxy
  policy. The optional .NET client uses a different registry; see
  [registry clients](registry-clients.md) only if you need it.
- **Anything else** — [troubleshooting](../guides/deploy/troubleshooting.md).
