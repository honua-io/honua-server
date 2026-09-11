---
type: guide
title: "Linux: install published packages"
description: "Use Docker Engine with Compose 2.23.1 or later, Python 3.11 or later with venv and pip 22.3+, and a Bash terminal."
---
# Linux: install published packages

Use Docker Engine with Compose 2.23.1 or later, Python 3.11 or later with venv and pip 22.3+,
and a Bash terminal. The configuration runs the same Production image, registry
clients and two-feature journey as the [Windows quickstart](quickstart.md).
Read its [artifact identity and qualification](quickstart.md#artifact-identity-and-qualification)
first. No source checkout, build or GitHub login is needed. This pre-cut profile
selects `linux/amd64`; use an amd64 host for this rehearsal.

## Create an isolated installation

Choose unused loopback ports if 18080 (HTTP) or 18081 (native gRPC) is occupied.
Set `HONUA_GRPC_PORT` in `.env` to override the gRPC default. A new directory, Compose
project, network and three volumes isolate this installation. Keep `.env` private
and retain it with the volumes; recreating it does not rotate database passwords.

```bash
set -euo pipefail
umask 077
Project="honua-$(python3 -c 'import secrets; print(secrets.token_hex(6))')"
mkdir "$Project"
cd "$Project"
Install="$PWD"
python3 - <<'PYTHON'
import secrets
from pathlib import Path
Path('.env').write_text(
    'COMPOSE_PROJECT_NAME=' + Path.cwd().name + '\n'
    'HONUA_IMAGE=ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9\n'
    'HONUA_HTTP_PORT=18080\n'
    'POSTGRES_PASSWORD=' + secrets.token_hex(32) + '\n'
    'HONUA_ADMIN_PASSWORD=Aa1!' + secrets.token_hex(32) + '\n'
    'HONUA_MASTER_KEY=' + secrets.token_hex(32) + '\n')
PYTHON
function dc { docker compose --env-file .env -f compose.yaml "$@"; }
cat > compose.yaml <<'COMPOSE'
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
COMPOSE
dc config --quiet
dc pull
dc up -d --wait --wait-timeout 180
```

## Install clients and verify startup

No activation or system Python package installation is needed. Run the next
block in the same terminal.

```bash
python3 -m venv --without-pip .venv
Python="$Install/.venv/bin/python"
python3 -m pip --python "$Python" install --index-url https://pypi.org/simple --only-binary=:all: 'honua-admin==0.1.8' 'honua-sdk==0.1.11' 'mcp==2.1.1'
python3 -m pip --python "$Python" freeze > installed-packages.txt
set -a
source .env
set +a
export HONUA_BASE_URL="http://localhost:$HONUA_HTTP_PORT"
function wait_honua_ready {
    "$Python" - <<'PYTHON'
import os, time
from honua_admin import HonuaAdminClient
from honua_sdk.errors import HonuaHttpError, HonuaTransportError
with HonuaAdminClient(os.environ['HONUA_BASE_URL'], api_key=os.environ['HONUA_ADMIN_PASSWORD'], timeout=5, max_retries=0) as admin:
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            admin.get_config()
            break
        except HonuaHttpError as error:
            if error.status_code != 429 and error.status_code < 500:
                raise
        except HonuaTransportError:
            pass
        time.sleep(2)
    else:
        raise RuntimeError('Readiness failed; inspect Compose logs')
PYTHON
}
wait_honua_ready
"$Python" - <<'PYTHON'
import os
from honua_admin import HonuaAdminClient
from honua_sdk.errors import HonuaHttpError
base = os.environ['HONUA_BASE_URL']
try:
    with HonuaAdminClient(base) as anonymous:
        anonymous.get_config()
except HonuaHttpError as error:
    if error.status_code != 401:
        raise RuntimeError('Expected anonymous admin denial with HTTP 401') from error
else:
    raise RuntimeError('Anonymous admin access was unexpectedly allowed')
with HonuaAdminClient(base, api_key=os.environ['HONUA_ADMIN_PASSWORD']) as admin:
    admin.get_config()
print('Ready; anonymous admin denied; authenticated admin succeeded')
PYTHON
```

## Import, publish, and query

This uses the published MCP transport, Honua admin client and Honua data client.
It fails on changed values, missing rows, incorrect coordinates or CRS. Keep the
saved layer identity so recovery can query it without re-importing.

```bash
cat > journey.py <<'PYTHON'
import asyncio
import json
import math
import os
import sys
from pathlib import Path
import httpx2
from mcp import ClientSession
from mcp.client.streamable_http import streamable_http_client
from honua_admin import HonuaAdminClient, CreateSecureConnectionRequest, PublishLayerRequest
from honua_sdk import HonuaClient

base = os.environ['HONUA_BASE_URL']
key = os.environ['HONUA_ADMIN_PASSWORD']
state_path = Path('published-layer.json')
expected = {'west': (7, -157.875, 21.3125), 'east': (19, -155.0625, 19.6875)}

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

async def ingest_fixture():
    async with httpx2.AsyncClient(headers={'X-API-Key': key}, timeout=120) as transport:
        async with streamable_http_client(base + '/mcp', http_client=transport) as (read, write):
            async with ClientSession(read, write) as session:
                await session.initialize()
                call = await session.call_tool('honua_ingest_dataset', {
                    'format': 'geojson', 'datasetName': 'windows_points',
                    'data': Path('points.geojson').read_text(encoding='utf-8'), 'sourceSrid': 4326})
                require(not call.is_error, 'Honua MCP ingest failed')
                result = call.structured_content
                require(isinstance(result, dict) and result.get('success'), 'Import did not succeed')
                require(result['rowCount'] == 2 and not result.get('rowErrors'), 'Expected two imported rows without errors')
                return result

if '--verify-only' not in sys.argv:
    if state_path.exists():
        raise RuntimeError('Already published; use --verify-only after restart')
    fixture = {'type': 'FeatureCollection', 'features': [
        {'type': 'Feature', 'properties': {'name': name, 'value': value},
         'geometry': {'type': 'Point', 'coordinates': [lon, lat]}}
        for name, (value, lon, lat) in expected.items()]}
    Path('points.geojson').write_text(json.dumps(fixture), encoding='utf-8')
    with HonuaAdminClient(base, api_key=key) as admin:
        connection = next((c for c in admin.list_connections() if c.name == 'windows-local'), None)
        if connection is None:
            connection = admin.create_connection(CreateSecureConnectionRequest(
                name='windows-local', host='postgres', port=5432, database_name='honua',
                username='honua', password=os.environ['POSTGRES_PASSWORD'],
                ssl_mode='Disable', ssl_required=False))
        result = asyncio.run(ingest_fixture())
        layer = admin.publish_layer(connection.connection_id, PublishLayerRequest(
            schema=result['schema'], table=result['table'], layer_name='windows-points',
            service_name='windows', srid=4326, geometry_column=result['geometryColumn'],
            geometry_type='Point', primary_key=result['primaryKey'], fields_list=['id', 'properties']))
        state_path.write_text(json.dumps({'service': 'windows', 'layer': layer.layer_id}), encoding='utf-8')

state = json.loads(state_path.read_text(encoding='utf-8'))
with HonuaClient(base, api_key=key) as client:
    result = client.query_features(state['service'], state['layer'],
        out_fields=['id', 'properties'], return_geometry=True, extra_params={'outSR': 4326})
require(result['spatialReference']['wkid'] == 4326, 'Unexpected query CRS')
require(result['geometryType'] == 'esriGeometryPoint', 'Expected Point metadata')
require(result['objectIdFieldName'] == 'id', 'Unexpected object ID field')
require(len(result['features']) == 2, 'Expected exactly two published features')
observed = {}
for feature in result['features']:
    attributes, geometry = feature['attributes']['properties'], feature['geometry']
    name = attributes['name']
    require(name not in observed and name in expected, 'Unexpected or duplicate feature name')
    value, lon, lat = expected[name]
    require(attributes['value'] == value, 'Unexpected feature value')
    require(math.isclose(geometry['x'], lon, rel_tol=0, abs_tol=1e-9), 'Unexpected longitude')
    require(math.isclose(geometry['y'], lat, rel_tol=0, abs_tol=1e-9), 'Unexpected latitude')
    observed[name] = True
require(set(observed) == set(expected), 'Missing expected features')
print('Verified 2 published features: names, values, XY ordinates, EPSG:4326')
PYTHON
"$Python" journey.py
```

## Restart and recover

Restart just this server, then read back the persisted layer:

```bash
dc restart honua
wait_honua_ready
"$Python" journey.py --verify-only
```

To return from a new terminal, enter the saved installation directory and run:

```bash
set -euo pipefail
Install="$PWD"
Python="$Install/.venv/bin/python"
function dc { docker compose --env-file .env -f compose.yaml "$@"; }
set -a
source .env
set +a
export HONUA_BASE_URL="http://localhost:$HONUA_HTTP_PORT"
dc up -d --wait --wait-timeout 180
```

Define `wait_honua_ready` again from the startup block, call it, then run the
readback above. Preserve the original `.env` and volumes. See the
[production guide](../guides/deploy/docker-compose.md) for backup and restore;
container recreation alone is not a backup.

## Diagnostics and scoped teardown

```bash
dc ps
dc logs --no-color --tail 150 honua postgres redis
docker image inspect "$HONUA_IMAGE" --format '{{json .RepoDigests}}'
python3 -m pip --python "$Python" freeze
```

For port conflicts, edit `HONUA_HTTP_PORT` or `HONUA_GRPC_PORT` in `.env`,
rerun `dc up -d` and reload
startup variables. For startup errors, retain the digest and error and inspect
logs; do not disable Production preflight. Public registry downloads require no
credentials. Share only redacted logs and package identities, never `.env`.

After a partial import or publication failure, inspect the logs and staging table,
then rerun `journey.py` from this installation. It reuses `windows-local`; MCP
ingest replaces the named staging dataset. After publication saves
`published-layer.json`, use `--verify-only` to read the retained layer.

Stop only this installation, retaining its volumes:

```bash
dc down
```

When you deliberately want to delete this installation's database, Redis and
file-storage volumes, run this from its saved directory:

```bash
dc down --volumes
unset HONUA_ADMIN_PASSWORD POSTGRES_PASSWORD HONUA_MASTER_KEY
```

The private installation directory remains for deliberate retention or deletion.
