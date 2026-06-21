#!/bin/bash

# Cloud-Native-Geospatial (CNG) conformance runner for Honua Server.
#
# Validates honua's PRODUCED cloud-native artifacts against the canonical
# first-party validators, failing on any validator non-zero exit:
#
#   GeoParquet 1.1.0  FeatureServer f=parquet   -> gpq validate
#   FlatGeobuf        FeatureServer f=fgb        -> ogrinfo -al -so (read-back)
#   PMTiles v3        PMTilesWriter (generated)  -> pmtiles verify
#   3D Tiles 1.1      Tileset+GLB (generated)    -> 3d-tiles-validator (+ gltf_validator)
#
# GeoParquet and FlatGeobuf are fetched live from a store-backed FeatureServer
# ('cng', seeded into PostgreSQL); PMTiles and 3D Tiles are produced by driving
# honua's own writers through the bundled artifact generator.
#
# Carve-outs (honua consumes/transcodes these, it does not PRODUCE conformant
# output, so they are intentionally NOT gated here): COG (exportImage emits
# plain GeoTIFF), Zarr / GeoZarr, COPC. See docs/cng-status.md.

set -uo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$REPO_ROOT"

CNG_COMPOSE_FILE="docker/cng/compose.yml"
CNG_SEED_FILE="docker/cng/seed.sql"
RESULTS_DIR="${CNG_RESULTS_DIR:-cng-results}"
ARTIFACTS_DIR="$RESULTS_DIR/artifacts"
SUMMARY_FILE="$RESULTS_DIR/cng-summary.md"
HONUA_CNG_SERVER_PORT="${HONUA_CNG_SERVER_PORT:-8094}"
export HONUA_CNG_SERVER_PORT
BASE_URL="http://localhost:${HONUA_CNG_SERVER_PORT}"
SERVER_HEALTH_TIMEOUT="${SERVER_HEALTH_TIMEOUT:-300}"
SKIP_BUILD="${HONUA_CNG_SKIP_BUILD:-false}"
CLEANUP="${HONUA_CNG_CLEANUP:-true}"

# Per-format pass/fail accumulators (0 = pass, 1 = fail, 2 = skipped/not run).
GEOPARQUET_STATUS=2
FLATGEOBUF_STATUS=2
PMTILES_STATUS=2
TILES_STATUS=2
GEOPARQUET_DETAIL="not run"
FLATGEOBUF_DETAIL="not run"
PMTILES_DETAIL="not run"
TILES_DETAIL="not run"

echo -e "${BLUE}Cloud-Native-Geospatial (CNG) Conformance${NC}"
echo "==========================================="

if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
else
    COMPOSE_CMD="docker compose"
fi

mkdir -p "$RESULTS_DIR" "$ARTIFACTS_DIR"

cleanup() {
    if [[ "$CLEANUP" == "true" ]]; then
        echo -e "\n${YELLOW}Cleaning up CNG containers...${NC}"
        $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    fi
}
trap cleanup EXIT

wait_for_health() {
    local svc="$1" timeout="$2" start now elapsed
    start=$(date +%s)
    while true; do
        if $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" ps "$svc" | grep -q "healthy"; then
            return 0
        fi
        now=$(date +%s); elapsed=$((now - start))
        if [[ $elapsed -gt $timeout ]]; then
            echo -e "${RED}Timeout waiting for ${svc} to become healthy${NC}"
            $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" logs "$svc" || true
            return 1
        fi
        echo "Waiting for ${svc}... (${elapsed}s)"
        sleep 5
    done
}

# --- Tool installation ----------------------------------------------------

install_tools() {
    echo -e "${YELLOW}Installing CNG validators...${NC}"

    if ! command -v gpq &> /dev/null; then
        echo "Installing gpq (GeoParquet validator)..."
        go install github.com/planetlabs/gpq@latest
        export PATH="$PATH:$(go env GOPATH)/bin"
    fi

    if ! command -v pmtiles &> /dev/null; then
        echo "Installing go-pmtiles..."
        go install github.com/protomaps/go-pmtiles@latest
        export PATH="$PATH:$(go env GOPATH)/bin"
    fi

    if ! command -v ogrinfo &> /dev/null; then
        echo "Installing GDAL (ogrinfo)..."
        sudo apt-get update -qq && sudo apt-get install -y gdal-bin >/dev/null
    fi

    if ! command -v 3d-tiles-validator &> /dev/null; then
        echo "Installing 3d-tiles-validator (chains gltf-validator)..."
        npm install -g 3d-tiles-validator >/dev/null 2>&1 || npm install 3d-tiles-validator >/dev/null 2>&1
    fi
}

# --- Bring up the store-backed honua stack --------------------------------

bring_up_stack() {
    if [[ "$SKIP_BUILD" != "true" ]]; then
        echo -e "${YELLOW}Building Honua Server image...${NC}"
        $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" build honua-server
    fi

    echo -e "${YELLOW}Starting PostgreSQL + Redis...${NC}"
    $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" up -d postgres redis
    wait_for_health postgres 120 || return 1
    wait_for_health redis 60 || return 1

    # Start honua once to run migrations, then seed the CNG service additively.
    echo -e "${YELLOW}Starting Honua Server (migrations)...${NC}"
    $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" up -d honua-server
    wait_for_health honua-server "$SERVER_HEALTH_TIMEOUT" || return 1

    echo -e "${YELLOW}Stopping Honua Server to seed CNG data...${NC}"
    $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" stop honua-server

    echo -e "${YELLOW}Seeding CNG conformance service...${NC}"
    local pg
    pg=$($COMPOSE_CMD -f "$CNG_COMPOSE_FILE" ps -q postgres)
    docker cp "$CNG_SEED_FILE" "$pg":/tmp/cng-seed.sql
    docker exec -i "$pg" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cng -f /tmp/cng-seed.sql >/dev/null

    echo -e "${YELLOW}Restarting Honua Server...${NC}"
    $COMPOSE_CMD -f "$CNG_COMPOSE_FILE" up -d honua-server
    wait_for_health honua-server "$SERVER_HEALTH_TIMEOUT" || return 1

    echo -e "${GREEN}Honua Server healthy at ${BASE_URL}${NC}"
}

# --- Validators -----------------------------------------------------------

validate_geoparquet() {
    echo -e "\n${BLUE}[GeoParquet] FeatureServer f=parquet -> gpq validate${NC}"
    local out="$ARTIFACTS_DIR/cng.parquet"
    local url="${BASE_URL}/rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=parquet"
    local code
    code=$(curl -sS -o "$out" -w "%{http_code}" "$url")
    if [[ "$code" != "200" ]]; then
        GEOPARQUET_STATUS=1
        GEOPARQUET_DETAIL="FeatureServer returned HTTP ${code} for f=parquet (expected 200)"
        echo -e "${RED}${GEOPARQUET_DETAIL}${NC}"
        head -c 500 "$out"; echo
        return
    fi
    echo "Fetched $(wc -c < "$out") bytes"
    if gpq validate "$out" 2>&1 | tee "$RESULTS_DIR/gpq-validate.log"; then
        GEOPARQUET_STATUS=0
        GEOPARQUET_DETAIL="gpq validate passed (FeatureServer f=parquet)"
        echo -e "${GREEN}GeoParquet valid${NC}"
    else
        GEOPARQUET_STATUS=1
        GEOPARQUET_DETAIL="gpq validate reported non-conformant GeoParquet"
        echo -e "${RED}${GEOPARQUET_DETAIL}${NC}"
    fi
}

validate_flatgeobuf() {
    echo -e "\n${BLUE}[FlatGeobuf] FeatureServer f=fgb -> ogrinfo read-back${NC}"
    local out="$ARTIFACTS_DIR/cng.fgb"
    local url="${BASE_URL}/rest/services/cng/FeatureServer/1000/query?where=1=1&outFields=*&f=fgb"
    local code
    code=$(curl -sS -o "$out" -w "%{http_code}" "$url")
    if [[ "$code" != "200" ]]; then
        FLATGEOBUF_STATUS=1
        FLATGEOBUF_DETAIL="FeatureServer returned HTTP ${code} for f=fgb (expected 200)"
        echo -e "${RED}${FLATGEOBUF_DETAIL}${NC}"
        head -c 500 "$out"; echo
        return
    fi
    echo "Fetched $(wc -c < "$out") bytes"
    # ogrinfo with VERIFY_BUFFERS forces FlatGeobuf to fully decode every feature
    # buffer instead of trusting the spatial index, so a malformed payload fails.
    if OGR_FLATGEOBUF_VERIFY_BUFFERS=YES ogrinfo -al -so "$out" 2>&1 | tee "$RESULTS_DIR/ogrinfo-fgb.log" | grep -q "Feature Count"; then
        FLATGEOBUF_STATUS=0
        FLATGEOBUF_DETAIL="ogrinfo read-back succeeded (FeatureServer f=fgb)"
        echo -e "${GREEN}FlatGeobuf valid${NC}"
    else
        FLATGEOBUF_STATUS=1
        FLATGEOBUF_DETAIL="ogrinfo failed to read back the FlatGeobuf payload"
        echo -e "${RED}${FLATGEOBUF_DETAIL}${NC}"
    fi
}

validate_pmtiles() {
    echo -e "\n${BLUE}[PMTiles] PMTilesWriter -> pmtiles verify${NC}"
    local pmt="$ARTIFACTS_DIR/honua.pmtiles"
    if [[ ! -f "$pmt" ]]; then
        PMTILES_STATUS=1
        PMTILES_DETAIL="artifact generator did not emit honua.pmtiles"
        echo -e "${RED}${PMTILES_DETAIL}${NC}"
        return
    fi
    if pmtiles verify "$pmt" 2>&1 | tee "$RESULTS_DIR/pmtiles-verify.log"; then
        PMTILES_STATUS=0
        PMTILES_DETAIL="pmtiles verify passed (honua PMTilesWriter)"
        echo -e "${GREEN}PMTiles valid${NC}"
    else
        PMTILES_STATUS=1
        PMTILES_DETAIL="pmtiles verify reported a malformed archive"
        echo -e "${RED}${PMTILES_DETAIL}${NC}"
    fi
}

validate_3dtiles() {
    echo -e "\n${BLUE}[3D Tiles] Tileset+GLB -> 3d-tiles-validator${NC}"
    local tileset="$ARTIFACTS_DIR/3dtiles/tileset.json"
    if [[ ! -f "$tileset" ]]; then
        TILES_STATUS=1
        TILES_DETAIL="artifact generator did not emit 3dtiles/tileset.json"
        echo -e "${RED}${TILES_DETAIL}${NC}"
        return
    fi
    # 3d-tiles-validator validates the tileset.json AND chains gltf-validator over
    # each referenced GLB content. Write a structured JSON report and treat any
    # entry with ERROR severity (or a non-zero numErrors) as a failure — the CLI
    # exit code alone is not a documented pass/fail contract.
    local report="$RESULTS_DIR/3d-tiles-validator.json"
    npx --yes 3d-tiles-validator --tilesetFile "$tileset" --reportFile "$report" \
        2>&1 | tee "$RESULTS_DIR/3d-tiles-validator.log"

    if [[ ! -f "$report" ]]; then
        TILES_STATUS=1
        TILES_DETAIL="3d-tiles-validator did not produce a report (toolchain failure)"
        echo -e "${RED}${TILES_DETAIL}${NC}"
        return
    fi

    local num_errors
    num_errors=$(jq -r '[.. | objects | select(.severity? == "ERROR")] | length' "$report" 2>/dev/null)
    num_errors="${num_errors:-1}"
    if [[ "$num_errors" == "0" ]]; then
        TILES_STATUS=0
        TILES_DETAIL="3d-tiles-validator passed (honua tileset + GLB, 0 errors)"
        echo -e "${GREEN}3D Tiles valid${NC}"
    else
        TILES_STATUS=1
        TILES_DETAIL="3d-tiles-validator reported ${num_errors} error(s)"
        echo -e "${RED}${TILES_DETAIL}${NC}"
    fi
}

# --- Orchestration --------------------------------------------------------

install_tools

echo -e "${YELLOW}Generating honua-produced PMTiles + 3D Tiles artifacts...${NC}"
dotnet run --project scripts/conformance/cng/artifact-gen/Honua.Cng.ArtifactGen.csproj \
    -c Release -- "$ARTIFACTS_DIR" 2>&1 | tee "$RESULTS_DIR/artifact-gen.log"

if ! bring_up_stack; then
    echo -e "${RED}Failed to bring up the CNG stack; live-format validation cannot run${NC}"
else
    validate_geoparquet
    validate_flatgeobuf
fi

validate_pmtiles
validate_3dtiles

# --- Summary --------------------------------------------------------------

status_label() {
    case "$1" in
        0) echo "PASS" ;;
        1) echo "FAIL" ;;
        *) echo "NOT RUN" ;;
    esac
}

cat > "$SUMMARY_FILE" << EOF
# Cloud-Native-Geospatial (CNG) Conformance Results

**Execution Date**: $(date)
**Honua Server Version**: $(git describe --tags --always 2>/dev/null || echo "unknown")

## Per-format results

| Format | Source | Validator | Result | Detail |
|---|---|---|---|---|
| GeoParquet 1.1.0 | FeatureServer \`f=parquet\` | \`gpq validate\` | $(status_label $GEOPARQUET_STATUS) | $GEOPARQUET_DETAIL |
| FlatGeobuf | FeatureServer \`f=fgb\` | \`ogrinfo -al -so\` | $(status_label $FLATGEOBUF_STATUS) | $FLATGEOBUF_DETAIL |
| PMTiles v3 | \`PMTilesWriter\` | \`pmtiles verify\` | $(status_label $PMTILES_STATUS) | $PMTILES_DETAIL |
| 3D Tiles 1.1 | \`TilesetDocumentWriter\` + \`GeometryTileBuilder\` | \`3d-tiles-validator\` + \`gltf_validator\` | $(status_label $TILES_STATUS) | $TILES_DETAIL |

## Carve-outs (not gated)

honua consumes/transcodes these formats but does not PRODUCE conformant output,
so they are intentionally excluded from this lane:

- **COG** — exportImage emits plain GeoTIFF; \`format=cog\` is rejected.
- **Zarr / GeoZarr** — read/transcode only.
- **COPC** — read/transcode only.
EOF

echo -e "\n${BLUE}Summary written to ${SUMMARY_FILE}${NC}"
cat "$SUMMARY_FILE"

# Fail the lane if any format that ran did not pass. A format left "not run"
# because the stack failed to start is treated as a failure too.
OVERALL=0
for s in $GEOPARQUET_STATUS $FLATGEOBUF_STATUS $PMTILES_STATUS $TILES_STATUS; do
    if [[ "$s" != "0" ]]; then
        OVERALL=1
    fi
done

if [[ $OVERALL -eq 0 ]]; then
    echo -e "\n${GREEN}All CNG conformance validators passed.${NC}"
else
    echo -e "\n${RED}One or more CNG conformance validators failed or did not run.${NC}"
fi

exit $OVERALL
