#!/usr/bin/env bash

set -euo pipefail

BASE_URL="${HONUA_SERVER_BASE_URL:-http://localhost:5000}"
ADMIN_API_KEY="${HONUA_ADMIN_API_KEY:-ci-admin-password}"
SERVICE_ID="${HONUA_SDK_SERVICE_ID:-test_service}"
LAYER_ID="${HONUA_SDK_LAYER_ID:-0}"
JS_DIR="${HONUA_SDK_JS_DIR:-sdk/honua-sdk-js}"
PYTHON_DIR="${HONUA_SDK_PYTHON_DIR:-sdk/honua-sdk-python}"
DOTNET_DIR="${HONUA_SDK_DOTNET_DIR:-sdk/honua-sdk-dotnet}"

ROOT_DIR="$(pwd)"
RESULTS_DIR="${SDK_COMPATIBILITY_RESULTS_DIR:-$ROOT_DIR/results}"
JS_MIGRATION_RESULTS_DIR="$RESULTS_DIR/sdk-js"
PYTHON_MIGRATION_RESULTS_DIR="$RESULTS_DIR/sdk-python"
DOTNET_MIGRATION_RESULTS_DIR="$RESULTS_DIR/sdk-dotnet"
MIGRATION_EVIDENCE_FILE="$RESULTS_DIR/migration-automation.json"
MIGRATION_SERVER_BASE_URL="${HONUA_SDK_MIGRATION_SERVER_BASE_URL:-http://localhost:5001}"
MIGRATION_GEOSERVER_URL="${HONUA_SDK_MIGRATION_GEOSERVER_URL:-http://127.0.0.1:5011/geoserver/rest}"
MIGRATION_GEOSERVER_FIXTURE="${HONUA_SDK_MIGRATION_GEOSERVER_FIXTURE:-tests/dotnet/Honua.Postgres.Tests/Features/Import/Fixtures/GeoServer/CatalogApplySlice.json}"
MIGRATION_ARCGIS_SERVICE_URL="${HONUA_SDK_MIGRATION_ARCGIS_SERVICE_URL:-https://sampleserver6.arcgisonline.com/arcgis/rest/services/ServiceRequest/FeatureServer}"

cleanup_pids=()

section() {
    printf '\n== %s ==\n' "$1"
}

require_dir() {
    local dir="$1"
    local name="$2"

    if [[ ! -d "$dir" ]]; then
        echo "Missing $name checkout at $dir" >&2
        exit 1
    fi
}

url_port() {
    python - "$1" <<'PY'
import sys
from urllib.parse import urlsplit

url = urlsplit(sys.argv[1])
if url.port is not None:
    print(url.port)
elif url.scheme == "https":
    print(443)
else:
    print(80)
PY
}

cleanup() {
    local pid

    for pid in "${cleanup_pids[@]}"; do
        if kill -0 "$pid" >/dev/null 2>&1; then
            kill "$pid" >/dev/null 2>&1 || true
            wait "$pid" >/dev/null 2>&1 || true
        fi
    done
}

trap cleanup EXIT

start_geoserver_fixture() {
    local fixture_path="$ROOT_DIR/$MIGRATION_GEOSERVER_FIXTURE"
    local fixture_port
    local log_path="$RESULTS_DIR/geoserver-fixture.log"

    fixture_port="$(url_port "$MIGRATION_GEOSERVER_URL")"

    if [[ ! -f "$fixture_path" ]]; then
        echo "Missing GeoServer migration fixture at $fixture_path" >&2
        exit 1
    fi

    if curl -fsS "$MIGRATION_GEOSERVER_URL/about/version.xml" >/dev/null 2>&1; then
        return
    fi

    python - "$fixture_path" "$fixture_port" > "$log_path" 2>&1 <<'PY' &
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlsplit

fixture_path = sys.argv[1]
port = int(sys.argv[2])

with open(fixture_path, "r", encoding="utf-8") as handle:
    responses = json.load(handle)["responses"]


class FixtureHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        path = urlsplit(self.path).path
        if path not in responses:
            self.send_response(404)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.end_headers()
            self.wfile.write(f"No fixture response for {path}\n".encode("utf-8"))
            return

        value = responses[path]
        if isinstance(value, str):
            payload = value.encode("utf-8")
            content_type = "application/xml; charset=utf-8" if path.endswith(".xml") else "text/plain; charset=utf-8"
        else:
            payload = json.dumps(value, separators=(",", ":")).encode("utf-8")
            content_type = "application/json; charset=utf-8"

        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, fmt: str, *args: object) -> None:
        sys.stdout.write((fmt % args) + "\n")
        sys.stdout.flush()


ThreadingHTTPServer(("127.0.0.1", port), FixtureHandler).serve_forever()
PY
    cleanup_pids+=("$!")

    for _ in $(seq 1 20); do
        if curl -fsS "$MIGRATION_GEOSERVER_URL/about/version.xml" >/dev/null 2>&1; then
            return
        fi
        sleep 1
    done

    echo "GeoServer fixture did not become ready at $MIGRATION_GEOSERVER_URL" >&2
    tail -n 80 "$log_path" >&2 || true
    exit 1
}

start_migration_honua_server() {
    local log_path="$RESULTS_DIR/honua-server-sdk-migration.log"

    if curl -fsS "$MIGRATION_SERVER_BASE_URL/healthz/ready" >/dev/null 2>&1; then
        return
    fi

    ConnectionStrings__DefaultConnection="Host=localhost;Database=honua_test;Username=honua;Password=honua" \
    ASPNETCORE_URLS="$MIGRATION_SERVER_BASE_URL" \
    ASPNETCORE_ENVIRONMENT="Test" \
    HONUA_REGISTER_TEST_INFRASTRUCTURE="true" \
    HONUA_TEST_ALLOW_UNSAFE_GEOSERVER_URLS="true" \
    HONUA_ADMIN_PASSWORD="$ADMIN_API_KEY" \
    Security__ConnectionEncryption__MasterKey="test-master-key-that-is-at-least-32-characters-long-for-security" \
    Security__ConnectionEncryption__Salt="dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM=" \
    dotnet run --project src/Honua.Server --configuration Release --no-build --no-launch-profile \
        > "$log_path" 2>&1 &
    cleanup_pids+=("$!")

    for _ in $(seq 1 40); do
        if curl -fsS "$MIGRATION_SERVER_BASE_URL/healthz/ready" >/dev/null 2>&1; then
            return
        fi
        sleep 2
    done

    echo "Migration Honua Server did not become ready at $MIGRATION_SERVER_BASE_URL" >&2
    tail -n 120 "$log_path" >&2 || true
    exit 1
}

write_migration_automation_summary() {
    local python_surfaces="$PYTHON_MIGRATION_RESULTS_DIR/migration-surfaces.json"
    local dotnet_surfaces="$DOTNET_MIGRATION_RESULTS_DIR/migration-surfaces.json"

    jq -n '
      [
        { surface: "migration-scan", status: "unsupported", passed: false, linked_ticket: "honua-sdk-python#49", reason: "Python SDK has no public admin migration source scan wrapper yet." },
        { surface: "arcgis-import", status: "unsupported", passed: false, linked_ticket: "honua-sdk-python#49", reason: "Python SDK has no public ArcGIS import start/poll wrapper yet." },
        { surface: "geoserver-dry-run", status: "unsupported", passed: false, linked_ticket: "honua-sdk-python#49", reason: "Python SDK has no public GeoServer dry-run start/poll wrapper yet." },
        { surface: "migration-evidence", status: "unsupported", passed: false, linked_ticket: "honua-sdk-python#49", reason: "Python SDK has no public migration artifact bundle model/API yet." }
      ]
    ' > "$python_surfaces"

    jq -n \
      --slurpfile scan "$DOTNET_MIGRATION_RESULTS_DIR/migration-scan.json" \
      '[
        {
          surface: "migration-scan",
          status: "supported",
          passed: true,
          linked_ticket: "honua-sdk-dotnet#134",
          artifact: {
            kind: "scan",
            source_kind: ($scan[0].sourceKind // "geoserver-rest"),
            artifact_path: "results/sdk-dotnet/migration-scan.json",
            resource_count: ($scan[0].summary.resourceCount // 0)
          }
        },
        { surface: "arcgis-import", status: "unsupported", passed: false, linked_ticket: "honua-sdk-dotnet#134", reason: ".NET SDK has no public ArcGIS import start/poll wrapper yet." },
        { surface: "geoserver-dry-run", status: "unsupported", passed: false, linked_ticket: "honua-sdk-dotnet#134", reason: ".NET SDK has no public GeoServer dry-run start/poll wrapper yet." },
        { surface: "migration-evidence", status: "unsupported", passed: false, linked_ticket: "honua-sdk-dotnet#134", reason: ".NET SDK has migration artifact models but no public artifactSet=all scan/evidence bundle API yet." }
      ]' > "$dotnet_surfaces"

    jq -n \
      --slurpfile js "$JS_MIGRATION_RESULTS_DIR/migration-surfaces.json" \
      --slurpfile python "$python_surfaces" \
      --slurpfile dotnet "$dotnet_surfaces" \
      '{
        migration_automation: {
          required: false,
          status: "supported",
          passed: true,
          reason: "Implemented SDK-backed migration smoke surfaces passed; remaining unsupported entries are explicit per-SDK API gaps."
        },
        migration_automation_by_sdk: {
          js: $js[0],
          python: $python[0],
          dotnet: $dotnet[0]
        }
      }' > "$MIGRATION_EVIDENCE_FILE"
}

mkdir -p \
    "$RESULTS_DIR" \
    "$JS_MIGRATION_RESULTS_DIR" \
    "$PYTHON_MIGRATION_RESULTS_DIR" \
    "$DOTNET_MIGRATION_RESULTS_DIR"

section "Honua Server readiness"
curl -fsS "$BASE_URL/healthz/ready" >/dev/null

section "SDK migration fixture readiness"
start_geoserver_fixture
start_migration_honua_server

section "JavaScript SDK live compatibility"
require_dir "$JS_DIR" "honua-sdk-js"
(
    cd "$JS_DIR"
    npm ci
    npm run build --silent
    HONUA_SERVER_BASE_URL="$BASE_URL" \
    HONUA_ADMIN_API_KEY="$ADMIN_API_KEY" \
    HONUA_SDK_SERVICE_ID="$SERVICE_ID" \
    HONUA_SDK_LAYER_ID="$LAYER_ID" \
    HONUA_SDK_MIGRATION_SERVER_BASE_URL="$MIGRATION_SERVER_BASE_URL" \
    HONUA_SDK_MIGRATION_GEOSERVER_URL="$MIGRATION_GEOSERVER_URL" \
    HONUA_SDK_MIGRATION_ARCGIS_SERVICE_URL="$MIGRATION_ARCGIS_SERVICE_URL" \
    HONUA_SDK_MIGRATION_JS_RESULTS_DIR="$JS_MIGRATION_RESULTS_DIR" \
    node --input-type=module <<'EOF'
import path from "node:path";
import { mkdir, writeFile } from "node:fs/promises";
import { HonuaClient } from "./dist/src/index.js";

const baseUrl = process.env.HONUA_SERVER_BASE_URL;
const apiKey = process.env.HONUA_ADMIN_API_KEY;
const serviceId = process.env.HONUA_SDK_SERVICE_ID;
const layerId = Number(process.env.HONUA_SDK_LAYER_ID);
const migrationBaseUrl = process.env.HONUA_SDK_MIGRATION_SERVER_BASE_URL;
const migrationGeoServerUrl = process.env.HONUA_SDK_MIGRATION_GEOSERVER_URL;
const migrationArcGisServiceUrl = process.env.HONUA_SDK_MIGRATION_ARCGIS_SERVICE_URL;
const migrationResultsDir = process.env.HONUA_SDK_MIGRATION_JS_RESULTS_DIR;

const client = new HonuaClient({ baseUrl, apiKey });
const compatibility = await client.getCompatibility({ refresh: true });
if (!compatibility.serverVersion) {
  throw new Error("JS SDK did not parse admin compatibility metadata.");
}

const services = await client.listServices();
if (!Array.isArray(services.services) ||
    !services.services.some((service) => service.name === serviceId || service.serviceName === serviceId)) {
  throw new Error(`JS SDK did not discover seeded service '${serviceId}'.`);
}

const layer = await client.getLayerMetadata(serviceId, layerId);
if (!Array.isArray(layer.fields) || layer.fields.length === 0) {
  throw new Error("JS SDK did not parse FeatureServer layer metadata.");
}

const query = await client.queryFeatures({
  serviceId,
  layerId,
  where: "1=1",
  outFields: ["*"],
  returnGeometry: true,
});
if (!Array.isArray(query.features) || query.features.length === 0) {
  throw new Error("JS SDK FeatureServer query returned no seeded features.");
}

const collections = await client.listOgcCollections();
if (!Array.isArray(collections.collections) || collections.collections.length === 0) {
  throw new Error("JS SDK did not parse OGC API Features collections.");
}

const migrationClient = new HonuaClient({ baseUrl: migrationBaseUrl, apiKey, timeoutMs: 120_000 });
await mkdir(migrationResultsDir, { recursive: true });

const terminalStatusNames = new Set(["Completed", "Failed", "Cancelled"]);
const geoServerStatusNames = {
  7: "Completed",
  8: "Failed",
  9: "Cancelled",
};
const geoServicesStatusNames = {
  6: "Completed",
  7: "Failed",
  8: "Cancelled",
};

function assertArtifact(value, kind, label) {
  if (!value || value.artifactKind !== kind) {
    throw new Error(`${label} did not return ${kind}.`);
  }
}

function statusName(value, numericNames) {
  if (typeof value === "number") {
    return numericNames[value] ?? String(value);
  }
  return String(value ?? "");
}

async function writeJson(fileName, value) {
  const filePath = path.join(migrationResultsDir, fileName);
  await writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

async function measure(fn) {
  const started = performance.now();
  const value = await fn();
  return { value, durationMs: Math.round(performance.now() - started) };
}

async function pollJob(path, numericStatusNames, label) {
  let last;
  for (let attempt = 1; attempt <= 45; attempt++) {
    last = await migrationClient.request({ path });
    const current = statusName(last.status, numericStatusNames);
    if (terminalStatusNames.has(current)) {
      if (current !== "Completed") {
        throw new Error(`${label} reached ${current}: ${last.errorMessage ?? "no error message"}`);
      }
      return { status: last, pollCount: attempt };
    }
    await new Promise((resolve) => setTimeout(resolve, 2_000));
  }

  throw new Error(`${label} did not reach a terminal status; last status was ${JSON.stringify(last)}`);
}

const migrationSurfaces = [];

const scan = await measure(() =>
  migrationClient.scanMigrationSource({
    sourceKind: "geoserver",
    sourceUrl: migrationGeoServerUrl,
    timeoutSeconds: 30,
    includeStyleContent: false,
  }),
);
assertArtifact(scan.value, "honua.migration.source-inventory", "JS migration scan");
await writeJson("migration-scan.json", scan.value);
migrationSurfaces.push({
  surface: "migration-scan",
  status: "supported",
  passed: true,
  linked_ticket: "honua-sdk-js#105",
  artifact: {
    kind: "scan",
    source_kind: scan.value.sourceKind,
    artifact_path: "results/sdk-js/migration-scan.json",
    resource_count: scan.value.summary?.resourceCount ?? 0,
    duration_ms: scan.durationMs,
  },
});

const evidence = await measure(() =>
  migrationClient.request({
    path: "/api/v1/admin/import/scan",
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      sourceKind: "geoserver",
      sourceUrl: migrationGeoServerUrl,
      timeoutSeconds: 30,
      artifactSet: "all",
      targetServiceName: "sdk-js-migration-smoke",
    }),
  }),
);
assertArtifact(evidence.value.inventory, "honua.migration.source-inventory", "JS migration evidence inventory");
assertArtifact(evidence.value.manifest, "honua.migration.manifest", "JS migration evidence manifest");
assertArtifact(evidence.value.parityEvidence, "honua.migration.parity-evidence-pack", "JS migration evidence parity");
await writeJson("migration-evidence.json", evidence.value);
migrationSurfaces.push({
  surface: "migration-evidence",
  status: "supported",
  passed: true,
  linked_ticket: "honua-sdk-js#105",
  artifact: {
    kind: "evidence-bundle",
    source_kind: evidence.value.inventory.sourceKind,
    artifact_path: "results/sdk-js/migration-evidence.json",
    manifest_kind: evidence.value.manifest.artifactKind,
    parity_kind: evidence.value.parityEvidence.artifactKind,
    duration_ms: evidence.durationMs,
  },
});

const dryRun = await measure(async () => {
  const start = await migrationClient.request({
    path: "/api/v1/admin/import/geoserver/start",
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      geoServerRestUrl: migrationGeoServerUrl,
      dryRun: true,
      importStyles: false,
      autoPublishLayers: false,
      workspaceNames: ["ops"],
      layerNames: ["ops:roads"],
      requestTimeoutSeconds: 30,
      batchSize: 1,
    }),
  });
  if (!start.jobId) {
    throw new Error("JS GeoServer dry-run did not return a job id.");
  }
  const polled = await pollJob(`/api/v1/admin/import/geoserver/jobs/${start.jobId}`, geoServerStatusNames, "JS GeoServer dry-run");
  return { start, ...polled };
});
await writeJson("geoserver-dry-run.json", dryRun.value);
migrationSurfaces.push({
  surface: "geoserver-dry-run",
  status: "supported",
  passed: true,
  linked_ticket: "honua-sdk-js#105",
  artifact: {
    kind: "dry-run-job",
    job_id: dryRun.value.start.jobId,
    terminal_status: statusName(dryRun.value.status.status, geoServerStatusNames),
    artifact_path: "results/sdk-js/geoserver-dry-run.json",
    poll_count: dryRun.value.pollCount,
    duration_ms: dryRun.durationMs,
  },
});

const arcgisImport = await measure(async () => {
  const start = await migrationClient.request({
    path: "/api/v1/admin/import/geoservices/start",
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      serviceUrl: migrationArcGisServiceUrl,
      layerId: 0,
      tableName: `sdk_migration_${Date.now().toString(36)}`,
      whereClause: "1=0",
      batchSize: 1,
      requestTimeoutSeconds: 30,
      maxRetries: 1,
      autoPublish: false,
      overwriteExisting: true,
    }),
  });
  if (!start.jobId) {
    throw new Error("JS ArcGIS import did not return a job id.");
  }
  const polled = await pollJob(`/api/v1/admin/import/geoservices/jobs/${start.jobId}`, geoServicesStatusNames, "JS ArcGIS import");
  return { start, ...polled };
});
await writeJson("arcgis-import.json", arcgisImport.value);
migrationSurfaces.push({
  surface: "arcgis-import",
  status: "supported",
  passed: true,
  linked_ticket: "honua-sdk-js#105",
  artifact: {
    kind: "import-job",
    job_id: arcgisImport.value.start.jobId,
    terminal_status: statusName(arcgisImport.value.status.status, geoServicesStatusNames),
    artifact_path: "results/sdk-js/arcgis-import.json",
    poll_count: arcgisImport.value.pollCount,
    duration_ms: arcgisImport.durationMs,
  },
});

const surfaceOrder = ["migration-scan", "arcgis-import", "geoserver-dry-run", "migration-evidence"];
migrationSurfaces.sort((left, right) => surfaceOrder.indexOf(left.surface) - surfaceOrder.indexOf(right.surface));
await writeJson("migration-surfaces.json", migrationSurfaces);

console.log(`JS SDK compatibility passed for server ${compatibility.serverVersion}.`);
EOF
)

section "Python SDK live compatibility"
require_dir "$PYTHON_DIR" "honua-sdk-python"
(
    cd "$PYTHON_DIR"
    python -m pip install -e packages/honua-sdk -e packages/honua-admin
    HONUA_SERVER_BASE_URL="$BASE_URL" \
    HONUA_ADMIN_API_KEY="$ADMIN_API_KEY" \
    HONUA_SDK_SERVICE_ID="$SERVICE_ID" \
    HONUA_SDK_LAYER_ID="$LAYER_ID" \
    python - <<'PY'
import os

from honua_admin import HonuaAdminClient
from honua_sdk import HonuaClient

base_url = os.environ["HONUA_SERVER_BASE_URL"]
api_key = os.environ["HONUA_ADMIN_API_KEY"]
service_id = os.environ["HONUA_SDK_SERVICE_ID"]
layer_id = int(os.environ["HONUA_SDK_LAYER_ID"])

with HonuaAdminClient(base_url, api_key=api_key) as admin:
    capabilities = admin.get_capabilities()
    if capabilities.compatibility is None or not capabilities.compatibility.server_version:
        raise RuntimeError("Python admin SDK did not parse compatibility metadata.")

with HonuaClient(base_url) as client:
    ready = client.readiness()
    if not isinstance(ready, dict):
        raise RuntimeError("Python SDK readiness response was not JSON.")

    services = client.list_services()
    if not any(
        service.get("name") == service_id or service.get("serviceName") == service_id
        for service in services.get("services", [])
    ):
        raise RuntimeError(f"Python SDK did not discover seeded service {service_id!r}.")

    features = client.query_features(service_id, layer_id)
    if not features.get("features"):
        raise RuntimeError("Python SDK FeatureServer query returned no seeded features.")

print("Python SDK compatibility passed.")
PY
)

section ".NET SDK live compatibility"
require_dir "$DOTNET_DIR" "honua-sdk-dotnet"
(
    case "$DOTNET_DIR" in
        /*) dotnet_admin_project="$DOTNET_DIR/src/Honua.Sdk.Admin/Honua.Sdk.Admin.csproj" ;;
        *) dotnet_admin_project="$ROOT_DIR/$DOTNET_DIR/src/Honua.Sdk.Admin/Honua.Sdk.Admin.csproj" ;;
    esac

    smoke_dir="$(mktemp -d)"
    trap 'rm -rf "$smoke_dir"' EXIT
    dotnet new console --framework net10.0 --output "$smoke_dir" >/dev/null
    smoke_project="$(find "$smoke_dir" -maxdepth 1 -name '*.csproj' -print -quit)"
    dotnet add "$smoke_project" reference "$dotnet_admin_project" >/dev/null
    cat > "$smoke_dir/Program.cs" <<'CS'
using Honua.Sdk.Admin;
using Honua.Sdk.Admin.Models;
using System.Text.Json;

var baseUrl = Environment.GetEnvironmentVariable("HONUA_SERVER_BASE_URL") ?? "http://localhost:5000";
var apiKey = Environment.GetEnvironmentVariable("HONUA_ADMIN_API_KEY") ?? "ci-admin-password";
var migrationBaseUrl = Environment.GetEnvironmentVariable("HONUA_SDK_MIGRATION_SERVER_BASE_URL") ?? baseUrl;
var migrationGeoServerUrl = Environment.GetEnvironmentVariable("HONUA_SDK_MIGRATION_GEOSERVER_URL") ?? "http://127.0.0.1:5011/geoserver/rest";
var migrationResultsDir = Environment.GetEnvironmentVariable("HONUA_SDK_MIGRATION_DOTNET_RESULTS_DIR");

using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);

var client = new HonuaAdminClient(httpClient);
var version = await client.GetVersionAsync();
if (string.IsNullOrWhiteSpace(version.Version))
{
    throw new InvalidOperationException(".NET SDK did not parse admin version metadata.");
}

var capabilities = await client.GetCapabilitiesAsync();
if (string.IsNullOrWhiteSpace(capabilities.ServerVersion))
{
    throw new InvalidOperationException(".NET SDK did not parse admin compatibility metadata.");
}

var compatibility = await client.CheckCompatibilityAsync();
if (!compatibility.IsSupported)
{
    throw new InvalidOperationException($".NET SDK rejected the seeded server: {compatibility.UnsupportedReason}");
}

if (!string.IsNullOrWhiteSpace(migrationResultsDir))
{
    Directory.CreateDirectory(migrationResultsDir);
    using var migrationHttpClient = new HttpClient { BaseAddress = new Uri(migrationBaseUrl) };
    migrationHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-API-Key", apiKey);
    var migrationClient = new HonuaAdminClient(migrationHttpClient);
    var scan = await migrationClient.ScanMigrationSourceAsync(new MigrationInventoryScanRequest
    {
        SourceKind = "geoserver",
        SourceUrl = migrationGeoServerUrl,
        TimeoutSeconds = 30,
        IncludeStyleContent = false
    });

    if (!string.Equals(scan.ArtifactKind, MigrationSourceInventoryArtifact.CurrentArtifactKind, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(".NET SDK did not parse the migration inventory artifact kind.");
    }

    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    await File.WriteAllTextAsync(
        Path.Combine(migrationResultsDir, "migration-scan.json"),
        JsonSerializer.Serialize(scan, jsonOptions) + Environment.NewLine);
}

Console.WriteLine($".NET SDK compatibility passed for server {capabilities.ServerVersion}.");
CS
    dotnet_msbuild_props=(
        -p:RunAnalyzers=false
        -p:TreatWarningsAsErrors=false
        -p:WarningsAsErrors=
        -p:CodeAnalysisTreatWarningsAsErrors=false
    )
    HONUA_SERVER_BASE_URL="$BASE_URL" \
    HONUA_ADMIN_API_KEY="$ADMIN_API_KEY" \
    dotnet build "$smoke_project" --configuration Release "${dotnet_msbuild_props[@]}" >/dev/null
    HONUA_SERVER_BASE_URL="$BASE_URL" \
    HONUA_ADMIN_API_KEY="$ADMIN_API_KEY" \
    HONUA_SDK_MIGRATION_SERVER_BASE_URL="$MIGRATION_SERVER_BASE_URL" \
    HONUA_SDK_MIGRATION_GEOSERVER_URL="$MIGRATION_GEOSERVER_URL" \
    HONUA_SDK_MIGRATION_DOTNET_RESULTS_DIR="$DOTNET_MIGRATION_RESULTS_DIR" \
    dotnet run --project "$smoke_project" --configuration Release --no-build --no-restore
)

section "SDK migration automation evidence"
write_migration_automation_summary
jq -e '
  .migration_automation.status == "supported"
  and (.migration_automation_by_sdk.js | map(select(.status == "supported")) | length == 4)
  and (.migration_automation_by_sdk.dotnet | map(select(.surface == "migration-scan" and .status == "supported")) | length == 1)
' "$MIGRATION_EVIDENCE_FILE" >/dev/null

section "SDK compatibility complete"
