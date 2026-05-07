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

section "Honua Server readiness"
curl -fsS "$BASE_URL/healthz/ready" >/dev/null

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
    node --input-type=module <<'EOF'
import { HonuaClient } from "./dist/src/index.js";

const baseUrl = process.env.HONUA_SERVER_BASE_URL;
const apiKey = process.env.HONUA_ADMIN_API_KEY;
const serviceId = process.env.HONUA_SDK_SERVICE_ID;
const layerId = Number(process.env.HONUA_SDK_LAYER_ID);

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

var baseUrl = Environment.GetEnvironmentVariable("HONUA_SERVER_BASE_URL") ?? "http://localhost:5000";
var apiKey = Environment.GetEnvironmentVariable("HONUA_ADMIN_API_KEY") ?? "ci-admin-password";

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
    dotnet run --project "$smoke_project" --configuration Release --no-build
)

section "SDK compatibility complete"
