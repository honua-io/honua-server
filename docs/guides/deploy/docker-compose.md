# Deploy with Docker Compose

Run a single-node Production configuration from published artifacts on Windows
with Docker Desktop (Linux containers), PowerShell 5.1 or 7 and Python 3.11+.
Compose 2.23.1+ is required. This guide starts with loopback-only access, an
isolated project and persistent storage. Complete the install and readback below
before configuring your public TLS edge. Keep this terminal open.

**Linux:** use the [Linux package installation](../../get-started/linux-packages.md)
for the identical Compose services and import/publish/query journey, then use
the Linux backup/recovery blocks on this page. Both paths use the same pins in
the [customer install manifest](https://honua.io/data/customer-install-manifest.json).

No repository checkout, compiler, Bash, Git, developer helper, or GitHub Packages
credential is needed. The server image and the two PyPI clients below are public.
Community needs no license. Installing Redis does not grant paid capabilities;
this journey uses a small synchronous import and does not require durable jobs.

## Artifact identity and qualification

The commands pin the anonymously published **pre-cut rehearsal** image
`ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9`
(Docker Desktop Linux containers; this journey selects `linux/amd64`, source `5a657b9eaed7cdeac915d584ad58c028a52ca61e`). Its
[registry manifest](https://ghcr.io/v2/honua-io/honua-server/manifests/sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9)
is fetched by `docker pull` below. The control-plane package is
[honua-admin 0.1.8](https://pypi.org/project/honua-admin/0.1.8/); the data-plane
package is [honua-sdk 0.1.11](https://pypi.org/project/honua-sdk/0.1.11/).
The import step invokes Honua's `honua_ingest_dataset` MCP tool using the
published [MCP transport client 2.1.1](https://pypi.org/project/mcp/2.1.1/).

Download the [customer install manifest](https://honua.io/data/customer-install-manifest.json)
for all image and client identities, wheel hashes and direct downloads. Its Honua
client pins come from the release manifest; the server pin comes from the linked
successful pre-cut rehearsal. The [publication record](https://honua.io/data/customer-install-publication.json)
identifies the immutable release-repository source and SHA-256 of the public copy.
The GHCR manifest URL uses the OCI registry protocol; Docker handles its anonymous
bearer-token exchange. It does not require a GitHub account.

**This is not a qualified 2026.1 candidate.** After the cut, replace the server
and compatible client pins together from the signed release lock and repeat this
journey on a clean Windows machine in the Windows licensed lane. Link that
separate qualification record on [#4300](https://github.com/honua-io/honua-server/issues/4300).
Do not substitute the historical 2026.1 release or the moving candidate snapshot.

The [pre-cut Windows receipt](evidence/windows-packages-4300.json)
records successful fresh-volume startup, anonymous denial, authenticated admin
access, import/publish/query, restart readback, container-recreation readback,
and scoped teardown with these packages. It used an existing Windows host with
a new installation directory and virtual environment, not a clean-machine RC
qualification.

## 1. Create a private, isolated installation

Choose an unused loopback port if `18080` is occupied. Keep this PowerShell
session open through verification. Each new installation gets its own Compose
project, network, three volumes, and directory. Do not regenerate credentials
for an existing database.

```powershell
$ErrorActionPreference = 'Stop'
$Project = 'honua-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$Install = Join-Path (Get-Location) $Project
New-Item -ItemType Directory -Path $Install | Out-Null
$acl = Get-Acl -LiteralPath $Install
$acl.SetAccessRuleProtection($true, $false)
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.AddAccessRule($rule)
Set-Acl -LiteralPath $Install -AclObject $acl
Set-Location -LiteralPath $Install
function New-InstallSecret {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
}
$Image = 'ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9'
$Port = 18080
@"
COMPOSE_PROJECT_NAME=$Project
HONUA_IMAGE=$Image
HONUA_HTTP_PORT=$Port
POSTGRES_PASSWORD=$(New-InstallSecret)
HONUA_ADMIN_PASSWORD=Aa1!$(New-InstallSecret)
HONUA_MASTER_KEY=$(New-InstallSecret)
"@ | Set-Content -LiteralPath .env -Encoding Ascii
function dc {
    & docker compose --env-file .env -f compose.yaml @args
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed ($LASTEXITCODE)" }
}
```

Save the following customer configuration verbatim. Only Honua's HTTP port is
published, on loopback; PostgreSQL and Redis are reachable only on this project's
network. The inline SQL initializes a fresh database before the final postmaster
becomes healthy. An existing incompatible database still fails server preflight.

```powershell
@'
services:
  honua:
    image: ${HONUA_IMAGE:?Set the immutable server digest}
    platform: linux/amd64
    ports:
      - "127.0.0.1:${HONUA_HTTP_PORT:?Set an unused port}:8080"
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
'@ | Set-Content -LiteralPath compose.yaml -Encoding Ascii
dc config --quiet
dc pull
dc up -d --wait --wait-timeout 180
```

## 2. Install the registry clients and verify startup

Use an isolated virtual environment. No activation or execution-policy change is
needed. Do not substitute `git+https` installs or local source packages.

```powershell
python -m venv .venv
if ($LASTEXITCODE -ne 0) { throw 'Python virtual environment creation failed' }
$Python = Join-Path $Install '.venv\Scripts\python.exe'
& $Python -m pip install --index-url https://pypi.org/simple --only-binary=:all: 'honua-admin==0.1.8' 'honua-sdk==0.1.11' 'mcp==2.1.1'
if ($LASTEXITCODE -ne 0) { throw 'Registry package installation failed' }
& $Python -m pip freeze | Set-Content -LiteralPath installed-packages.txt -Encoding Ascii
$values = @{}
Get-Content -LiteralPath .env | ForEach-Object {
    $pair = $_ -split '=', 2
    if ($pair.Count -eq 2) { $values[$pair[0]] = $pair[1] }
}
$env:HONUA_BASE_URL = 'http://localhost:' + $values['HONUA_HTTP_PORT']
$env:HONUA_ADMIN_PASSWORD = $values['HONUA_ADMIN_PASSWORD']
$env:POSTGRES_PASSWORD = $values['POSTGRES_PASSWORD']
function Wait-HonuaReady {
    $deadline = (Get-Date).AddMinutes(3)
    do {
        $ready = $false
        try { $ready = (Invoke-WebRequest "$env:HONUA_BASE_URL/healthz/ready" -UseBasicParsing).StatusCode -eq 200 } catch { }
        if (-not $ready) { Start-Sleep -Seconds 2 }
    } until ($ready -or (Get-Date) -ge $deadline)
    if (-not $ready) { throw 'Readiness failed; use the diagnostics below before proceeding' }
}
Wait-HonuaReady
@'
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
'@ | & $Python -
if ($LASTEXITCODE -ne 0) { throw 'Startup/authentication verification failed' }
```

## 3. Import, publish, and query a real fixture

Save this small customer script. Honua's MCP ingest tool loads the small GeoJSON
file through the shared import pipeline. The installed admin client registers
and publishes the connection; the installed data client queries the layer.
Import returns the physical table and column names, with GeoJSON attributes
in its `properties` JSON column. Publish that returned table and declare the
fixture's Point geometry. The expected names, values, longitude/latitude, and CRS are explicit;
verification fails on lost rows, swapped axes, changed values, or missing geometry.

```powershell
@'
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
'@ | Set-Content -LiteralPath journey.py -Encoding Ascii
& $Python journey.py
if ($LASTEXITCODE -ne 0) { throw 'Import/publish/query failed; retain diagnostics' }
```

## 4. Restart and recover

Restart only this installation's server, wait for readiness, then
read back the same persisted layer without importing or publishing it again:

```powershell
dc restart honua
Wait-HonuaReady
& $Python journey.py --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Persisted data verification failed' }
```

To resume from a new PowerShell session, change to the saved installation
directory, set `$Install = (Get-Location).Path`, and define `dc` from step 1.
**First run `dc up -d --wait --wait-timeout 180`** to recreate containers using
the retained volumes and original credentials. Only then run step 2's
variable-loading and readiness blocks. Run
`journey.py --verify-only` afterward. A restart or container recreation is not a
backup restore; follow [backup and recovery](backup-and-restore.md)
before storing irreplaceable data. Retain the private `.env`, database, Redis,
file-storage backup, and exact image identity together. Never delete volumes or
regenerate `.env` to bypass a migration or credential failure.

## Public TLS edge

The installed server binds only `127.0.0.1:18080` (or your selected port).
For a public deployment, put an existing TLS-terminating reverse proxy on the
same host in front of that loopback port, arrange DNS and certificates for your
hostname, and permit access to the proxy through the host firewall. Do not expose
PostgreSQL, Redis or the unauthenticated health probe on a separate public port.
Docker Desktop must be configured to restart after host reboot.

After the proxy is configured, set its hostname, browser origin and actual source
address as seen by Honua. These are deployment inputs, requested interactively;
never trust an entire network or a wildcard. The original loopback route remains
available for local diagnostics and recovery. This example accepts one forwarded
hop; a multi-proxy deployment needs its own explicit trust configuration.

```powershell
$PublicHost = Read-Host 'Public DNS hostname served by your TLS proxy (no scheme or path)'
$BrowserOrigin = Read-Host 'Browser application origin, including https://'
$ProxyAddress = Read-Host 'Exact proxy source IP address as seen by Honua'
if ($PublicHost -notmatch '^[a-zA-Z0-9.-]+$') { throw 'Expected a DNS hostname' }
$ParsedAddress = $null
if (-not [System.Net.IPAddress]::TryParse($ProxyAddress, [ref]$ParsedAddress)) { throw 'Expected a proxy IP address' }
$OriginUri = [Uri]$BrowserOrigin
if ($OriginUri.Scheme -ne 'https' -or $OriginUri.Authority -eq '') { throw 'Expected an HTTPS origin' }
$BrowserOrigin = $OriginUri.GetLeftPart([UriPartial]::Authority)
@"
services:
  honua:
    environment:
      AllowedHosts: "$PublicHost;localhost;127.0.0.1"
      PUBLIC_BASE_URL: "https://$PublicHost"
      Cors__AllowedOrigins__0: "$BrowserOrigin"
      ForwardedHeaders__Enabled: "true"
      ForwardedHeaders__ForwardLimit: "1"
      ForwardedHeaders__KnownProxies__0: "$ProxyAddress"
"@ | Set-Content -LiteralPath edge.yaml -Encoding Ascii
function dc {
    & docker compose --env-file .env -f compose.yaml -f edge.yaml @args
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose failed ($LASTEXITCODE)" }
}
dc config --quiet
dc up -d --wait --wait-timeout 180
Wait-HonuaReady
$LocalBaseUrl = $env:HONUA_BASE_URL
try {
    $env:HONUA_BASE_URL = 'https://' + $PublicHost
    & $Python journey.py --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'Public TLS/authenticated readback failed' }
} finally { $env:HONUA_BASE_URL = $LocalBaseUrl }
```

Retain `edge.yaml` with the installation. On returning to this directory in a new
PowerShell session, use the `dc` definition above if you configured this overlay.
Recheck the proxy source address after replacing the proxy or Docker network.
Never disable certificate validation to make public readback pass. DNS, TLS and
host reboot behavior must also be checked on the actual deployment host.

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

PowerShell: create a private backup directory inside the private installation,
write binary files inside the containers, and copy them to the host. This avoids
PowerShell 5.1 binary redirection corruption. The one-off archive container runs
as root with file ownership/access capabilities to preserve the storage owner;
the running server keeps its unprivileged, dropped-capabilities configuration. `$Backup` is retained for restore.

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

Linux, after the Linux package journey, with the same files and stopped writer:

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

If any Linux backup command fails, retain its error and run `dc start honua` to
resume service after investigation; do not use a partial backup. Store a verified
copy of the private backup off-host using your organization's backup system.

**Restore is destructive to this installation's database.** Use it only when you
intend to replace the database with the saved backup. Stop incoming requests and
Honua first. These commands restore into the same project using the original
credentials; they do not delete volumes or touch other projects. For a fresh
host, restore the saved private installation files first, install the recorded
clients, then start only PostgreSQL and Redis before continuing. File storage
must be empty or restored into a fresh volume when replacing a newer snapshot;
an archive extraction alone does not remove files created after the backup.

PowerShell (enter the full path of the verified backup):

```powershell
$Backup = Read-Host 'Full path to the verified backup directory'
if (-not (Test-Path -LiteralPath (Join-Path $Backup 'database.dump'))) { throw 'Database backup not found' }
if (-not (Test-Path -LiteralPath (Join-Path $Backup 'storage.tar.gz'))) { throw 'Storage backup not found' }
dc stop honua
dc cp (Join-Path $Backup 'database.dump') postgres:/tmp/honua-restore.dump
dc exec -T postgres pg_restore -U honua -d honua --clean --if-exists --exit-on-error /tmp/honua-restore.dump
dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint tar -v "${Backup}:/backup:ro" honua -xzf /backup/storage.tar.gz -C /var/lib/honua/storage
dc start honua
Wait-HonuaReady
& $Python journey.py --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Restored data readback failed; keep this installation out of service' }
```

Linux, using the backup directory chosen above:

```bash
test -f "$Backup/database.dump"
test -f "$Backup/storage.tar.gz"
dc stop honua
dc cp "$Backup/database.dump" postgres:/tmp/honua-restore.dump
dc exec -T postgres pg_restore -U honua -d honua --clean --if-exists --exit-on-error /tmp/honua-restore.dump
dc run --rm --no-deps --user 0 --cap-add DAC_OVERRIDE --cap-add CHOWN --cap-add FOWNER --entrypoint tar -v "$Backup:/backup:ro" honua -xzf /backup/storage.tar.gz -C /var/lib/honua/storage
dc start honua
wait_honua_ready
"$Python" journey.py --verify-only
```

## Upgrade & Rollback

Use the signed release lock's next server digest and compatible clients together.
Retain the previous `.env`, installed-package list and backup. A single-node
Compose upgrade has downtime. Review migration preflight through the published
admin client or the [API explorer](../../reference/openapi-and-explorer.md), then
follow [upgrade and rollback](upgrade-and-rollback.md). Keep the Gate migration
policy; a contract migration needs its specific approval nonce. Never skip
migrations or erase volumes to bypass a failure. N-1 candidate qualification and
clean-Windows validation remain separate release steps.

## Diagnostics and scoped teardown

```powershell
dc ps
dc logs --no-color --tail 150 honua postgres redis
docker image inspect $values['HONUA_IMAGE'] --format '{{json .RepoDigests}}'
& $Python -m pip freeze
```

For support, retain the image digest, installed-package list, HTTP status,
timestamp, and relevant error/correlation ID. Inspect logs before sharing them;
do not send `.env`, full Compose rendering, credentials, or customer records.

- **Port already allocated:** choose a different `HONUA_HTTP_PORT` in `.env`, run
  `dc up -d`, and reload the variables in step 2.
- **Startup exits or readiness times out:** inspect `dc logs`. A published-image
  defect is a failed rehearsal, even if PostgreSQL is healthy. Keep the digest
  and error; do not switch to Development or disable preflight.
- **Registry access fails:** these pins require no package-read credential.
  Check network/proxy policy and retry. The optional NuGet client uses a different
  registry; see [registry clients](../../get-started/registry-clients.md) only if you need .NET.
- **Import partially completed:** inspect the import result and discovered table
  before retrying. MCP ingest replaces its named staging dataset; use a new
  isolated project for a new clean-room rehearsal. After successful publication,
  the saved state makes this recipe refuse another import; use `--verify-only`.

Stop this installation while retaining all data:

```powershell
dc down
```

Only when you intend to permanently discard **this installation's** data, run
the following from its saved directory. It removes this project's containers,
network, and its three project-scoped volumes; it does not prune other projects.

```powershell
dc down --volumes
Remove-Item Env:HONUA_ADMIN_PASSWORD, Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue
```

The private installation folder remains for deliberate retention or deletion.
