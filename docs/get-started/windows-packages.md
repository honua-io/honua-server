# Install on Windows from published packages

Run these blocks in order in **Windows PowerShell 5.1 or PowerShell 7**, from a
directory where you can create a private installation folder. You need Docker
Desktop running Linux containers, Docker Compose **2.23.1+**, and Python **3.11+**
on `PATH`. This local, single-node Community installation uses Production startup
validation, loopback HTTP, and isolated persistent storage. For a public hostname
and TLS, use [production deployment](../guides/deploy/docker-compose.md).

No repository checkout, compiler, Bash, Git, developer helper, or GitHub Packages
credential is needed. The server image and the two PyPI clients below are public.
Community needs no license. Installing Redis does not grant paid capabilities;
this journey uses a small synchronous import and does not require durable jobs.

## Artifact identity and qualification

The commands pin the anonymously published **pre-cut rehearsal** image
`ghcr.io/honua-io/honua-server@sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9`
(Linux amd64, source `5a657b9eaed7cdeac915d584ad58c028a52ca61e`). Its
[registry manifest](https://ghcr.io/v2/honua-io/honua-server/manifests/sha256:273b4c616e806b8ac2809946659986960a1803e55bda79d99db5f3955b6c30b9)
is fetched by `docker pull` below. The control-plane package is
[honua-admin 0.1.8](https://pypi.org/project/honua-admin/0.1.8/); the data-plane
package is [honua-sdk 0.1.11](https://pypi.org/project/honua-sdk/0.1.11/).

**This is not a qualified 2026.1 candidate.** At the cut, obtain the immutable
server digest and compatible client versions from the signed customer release
lock supplied with the release, update these three pins together, and repeat
this entire journey on a clean Windows machine. The
[historical 2026.1 manifest download](https://github.com/honua-io/honua-release/releases/download/honua-2026.1/finalized-manifest.yaml)
belongs to the superseded prerelease; do not use it as the new candidate lock.
Exact-candidate qualification remains tracked in
[#4300](https://github.com/honua-io/honua-server/issues/4300).

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
      test: ["CMD-SHELL", "test \"$(head -n 1 /var/lib/postgresql/data/postmaster.pid 2>/dev/null)\" = \"1\" && pg_isready -U honua -d honua"]
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
& $Python -m pip install --index-url https://pypi.org/simple --only-binary=:all: 'honua-admin==0.1.8' 'honua-sdk==0.1.11'
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
$deadline = (Get-Date).AddMinutes(3)
do {
    $ready = $false
    try { $ready = (Invoke-WebRequest "$env:HONUA_BASE_URL/healthz/ready" -UseBasicParsing).StatusCode -eq 200 } catch { }
    if (-not $ready) { Start-Sleep -Seconds 2 }
} until ($ready -or (Get-Date) -ge $deadline)
if (-not $ready) { throw 'Readiness failed; use the diagnostics below before proceeding' }
@'
import os
import httpx
from honua_admin import HonuaAdminClient
base = os.environ['HONUA_BASE_URL']
assert httpx.get(base + '/api/v1/admin/config').status_code == 401
with HonuaAdminClient(base, api_key=os.environ['HONUA_ADMIN_PASSWORD']) as admin:
    admin.get_config()
print('Ready; anonymous admin denied; authenticated admin succeeded')
'@ | & $Python -
if ($LASTEXITCODE -ne 0) { throw 'Startup/authentication verification failed' }
```

## 3. Import, publish, and query a real fixture

Save this small customer script. The installed admin client registers and
publishes the connection; `httpx` uploads the file because admin 0.1.8 has no
file-upload wrapper. The installed data client queries the published layer.
File import creates `public.imported_windows_points`, with GeoJSON attributes
in its `properties` JSON column. Publish that physical table and declare the
fixture's Point geometry. The expected names, values, longitude/latitude, and CRS are explicit;
verification fails on lost rows, swapped axes, changed values, or missing geometry.

```powershell
@'
import json
import math
import os
import sys
from pathlib import Path
import httpx
from honua_admin import HonuaAdminClient, CreateSecureConnectionRequest, PublishLayerRequest
from honua_sdk import HonuaClient

base = os.environ['HONUA_BASE_URL']
key = os.environ['HONUA_ADMIN_PASSWORD']
state_path = Path('published-layer.json')
expected = {'west': (7, -157.875, 21.3125), 'east': (19, -155.0625, 19.6875)}

if '--verify-only' not in sys.argv:
    if state_path.exists():
        raise RuntimeError('Already published; use --verify-only after restart')
    fixture = {'type': 'FeatureCollection', 'features': [
        {'type': 'Feature', 'properties': {'name': name, 'value': value},
         'geometry': {'type': 'Point', 'coordinates': [lon, lat]}}
        for name, (value, lon, lat) in expected.items()]}
    Path('points.geojson').write_text(json.dumps(fixture), encoding='utf-8')
    with httpx.Client(base_url=base, headers={'X-API-Key': key}, timeout=120) as upload:
        with open('points.geojson', 'rb') as data:
            response = upload.post('/api/v1/admin/import/upload',
                files={'file': ('points.geojson', data, 'application/geo+json')},
                data={'tableName': 'windows_points', 'targetSchema': 'public',
                      'targetSrid': '4326', 'overwriteExisting': 'false'})
        response.raise_for_status()
        assert response.status_code == 200, 'Small fixture must complete synchronously'
        result = response.json()
        assert result['success'] and result['featureCount'] == 2, result
    with HonuaAdminClient(base, api_key=key) as admin:
        connection = admin.create_connection(CreateSecureConnectionRequest(
            name='windows-local', host='postgres', port=5432, database_name='honua',
            username='honua', password=os.environ['POSTGRES_PASSWORD'],
            ssl_mode='Disable', ssl_required=False))
        layer = admin.publish_layer(connection.connection_id, PublishLayerRequest(
            schema='public', table='imported_windows_points', layer_name='windows-points',
            service_name='windows', srid=4326, geometry_column='geometry',
            geometry_type='Point', primary_key='id', fields_list=['id', 'properties']))
        state_path.write_text(json.dumps({'service': 'windows', 'layer': layer.layer_id}), encoding='utf-8')

state = json.loads(state_path.read_text(encoding='utf-8'))
with HonuaClient(base, api_key=key) as client:
    result = client.query_features(state['service'], state['layer'],
        out_fields=['id', 'properties'], return_geometry=True, extra_params={'outSR': 4326})
assert result['spatialReference']['wkid'] == 4326, result
assert result['geometryType'] == 'esriGeometryPoint', result
assert result['objectIdFieldName'] == 'id', result
assert len(result['features']) == 2, result
observed = {}
for feature in result['features']:
    attributes, geometry = feature['attributes']['properties'], feature['geometry']
    name = attributes['name']
    assert name not in observed and name in expected, feature
    value, lon, lat = expected[name]
    assert attributes['value'] == value, feature
    assert math.isclose(geometry['x'], lon, abs_tol=1e-9), feature
    assert math.isclose(geometry['y'], lat, abs_tol=1e-9), feature
    observed[name] = True
assert set(observed) == set(expected)
print('Verified 2 published features: names, values, XY ordinates, EPSG:4326')
'@ | Set-Content -LiteralPath journey.py -Encoding Ascii
& $Python journey.py
if ($LASTEXITCODE -ne 0) { throw 'Import/publish/query failed; retain diagnostics' }
```

## 4. Restart and recover

Restart only this installation's server, wait for readiness as in step 2, then
read back the same persisted layer without importing or publishing it again:

```powershell
dc restart honua
# Repeat the readiness loop in step 2, then:
& $Python journey.py --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Persisted data verification failed' }
```

To resume from a new PowerShell session, change to the saved installation
directory, set `$Install = (Get-Location).Path`, define `dc` from step 1, and run
step 2's variable-loading and readiness blocks. `dc up -d --wait` recreates
containers using the retained volumes and original credentials. Run
`journey.py --verify-only` afterward. A restart or container recreation is not a
backup restore; follow [backup and recovery](../guides/deploy/backup-and-restore.md)
before storing irreplaceable data. Retain the private `.env`, database, Redis,
file-storage backup, and exact image identity together. Never delete volumes or
regenerate `.env` to bypass a migration or credential failure.

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
  Check network/proxy policy and retry. For an explicitly private replacement
  image only, use your organization's approved GHCR account and a token scoped
  to `read:packages` with `docker login ghcr.io --username YOUR_LOGIN`, entering
  the token at the password prompt. Never paste it into this guide or `.env`.
- **Import partially completed:** inspect the import result and discovered table
  before retrying. The recipe refuses overwrites; use a new isolated project for
  a new clean-room rehearsal.

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
