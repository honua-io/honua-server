#!/bin/bash

# Client compatibility server setup (WSL side).
# Builds image, starts Docker Compose, seeds data, and keeps the server
# running so Windows desktop clients can connect to http://localhost:8080.
#
# Usage:
#   ./scripts/client-compat/client-compat-server.sh              # start server
#   ./scripts/client-compat/client-compat-server.sh --teardown    # stop everything

set -euo pipefail

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

COMPOSE_FILE="docker/client-compat-compose.yml"
SEED_FILE="docker/client-compat-seed.sql"
HONUA_HEALTHCHECK_TIMEOUT=180
POSTGRES_HEALTHCHECK_TIMEOUT=60

echo -e "${BLUE}Client Compatibility Server Setup${NC}"
echo "===================================="

# ── Teardown mode ───────────────────────────────────────────────────────────
if [[ "${1:-}" == "--teardown" ]]; then
    echo -e "${YELLOW}Tearing down client-compat environment...${NC}"
    docker compose -f "$COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true
    echo -e "${GREEN}Done${NC}"
    exit 0
fi

# ── Prerequisites ───────────────────────────────────────────────────────────
if ! command -v docker &> /dev/null; then
    echo -e "${RED}Docker not found${NC}"
    exit 1
fi

if command -v docker-compose &> /dev/null; then
    COMPOSE_CMD="docker-compose"
else
    COMPOSE_CMD="docker compose"
fi

# ── Build image ─────────────────────────────────────────────────────────────
echo -e "${YELLOW}Building Honua Server image...${NC}"
if ! scripts/docker/build-with-github-packages.sh -t honua-server:latest . > /dev/null 2>&1; then
    echo -e "${RED}Docker build failed${NC}"
    exit 1
fi
echo -e "${GREEN}Image built${NC}"

# ── Clean start ─────────────────────────────────────────────────────────────
$COMPOSE_CMD -f "$COMPOSE_FILE" down --remove-orphans --volumes 2>/dev/null || true

# ── Start Postgres ──────────────────────────────────────────────────────────
echo -e "${YELLOW}Starting Postgres...${NC}"
$COMPOSE_CMD -f "$COMPOSE_FILE" up -d postgres

start_time=$(date +%s)
while true; do
    elapsed=$(( $(date +%s) - start_time ))
    if [[ $elapsed -gt $POSTGRES_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Postgres health check timed out${NC}"
        $COMPOSE_CMD -f "$COMPOSE_FILE" logs postgres
        exit 1
    fi
    if $COMPOSE_CMD -f "$COMPOSE_FILE" ps postgres | grep -q "healthy"; then
        break
    fi
    echo "  Waiting for Postgres... (${elapsed}s)"
    sleep 3
done
echo -e "${GREEN}Postgres is healthy${NC}"

# ── Start Honua (runs migrations) ──────────────────────────────────────────
echo -e "${YELLOW}Starting Honua Server (migrations will run)...${NC}"
$COMPOSE_CMD -f "$COMPOSE_FILE" up -d honua-server

start_time=$(date +%s)
while true; do
    elapsed=$(( $(date +%s) - start_time ))
    if [[ $elapsed -gt $HONUA_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Honua Server health check timed out${NC}"
        $COMPOSE_CMD -f "$COMPOSE_FILE" logs honua-server
        exit 1
    fi
    if $COMPOSE_CMD -f "$COMPOSE_FILE" ps honua-server | grep -q "healthy"; then
        break
    fi
    echo "  Waiting for Honua Server... (${elapsed}s)"
    sleep 5
done
echo -e "${GREEN}Honua Server is healthy${NC}"

# ── Seed data ───────────────────────────────────────────────────────────────
echo -e "${YELLOW}Seeding compat database...${NC}"
$COMPOSE_CMD -f "$COMPOSE_FILE" stop honua-server

POSTGRES_CONTAINER=$($COMPOSE_CMD -f "$COMPOSE_FILE" ps -q postgres)
if [[ -z "$POSTGRES_CONTAINER" ]]; then
    echo -e "${RED}Postgres container not found${NC}"
    exit 1
fi

docker cp "$SEED_FILE" "$POSTGRES_CONTAINER":/tmp/client-compat-seed.sql
docker exec -i "$POSTGRES_CONTAINER" psql -v ON_ERROR_STOP=1 -U postgres -d honua_client_compat -f /tmp/client-compat-seed.sql >/dev/null
echo -e "${GREEN}Database seeded${NC}"

# ── Restart server ──────────────────────────────────────────────────────────
$COMPOSE_CMD -f "$COMPOSE_FILE" up -d honua-server

start_time=$(date +%s)
while true; do
    elapsed=$(( $(date +%s) - start_time ))
    if [[ $elapsed -gt $HONUA_HEALTHCHECK_TIMEOUT ]]; then
        echo -e "${RED}Honua Server health check timed out after seeding${NC}"
        $COMPOSE_CMD -f "$COMPOSE_FILE" logs honua-server
        exit 1
    fi
    if $COMPOSE_CMD -f "$COMPOSE_FILE" ps honua-server | grep -q "healthy"; then
        break
    fi
    echo "  Waiting for Honua Server... (${elapsed}s)"
    sleep 5
done

# ── Verify endpoints ───────────────────────────────────────────────────────
echo -e "${YELLOW}Verifying endpoints...${NC}"

verify_endpoint() {
    local name="$1" url="$2"
    if curl -sf "$url" > /dev/null 2>&1; then
        echo -e "  ${GREEN}[OK]${NC} $name"
    else
        echo -e "  ${RED}[FAIL]${NC} $name ($url)"
    fi
}

verify_endpoint "Health"            "http://localhost:8080/healthz/ready"
verify_endpoint "FeatureServer"     "http://localhost:8080/rest/services/compat/FeatureServer?f=json"
verify_endpoint "MapServer"         "http://localhost:8080/rest/services/compat/MapServer?f=json"
verify_endpoint "WMS Capabilities"  "http://localhost:8080/rest/services/compat/MapServer/WMS?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0"
verify_endpoint "WMTS Capabilities" "http://localhost:8080/rest/services/compat/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0"
verify_endpoint "OGC Features"      "http://localhost:8080/ogc/features/collections"
verify_endpoint "OData Service Doc" "http://localhost:8080/odata"
verify_endpoint "OData \$metadata"  "http://localhost:8080/odata/\$metadata"

# ── Summary ─────────────────────────────────────────────────────────────────
echo ""
echo -e "${GREEN}Server is ready for client testing!${NC}"
echo ""
echo "  Endpoints accessible from Windows:"
echo "    FeatureServer:  http://localhost:8080/rest/services/compat/FeatureServer"
echo "    MapServer:      http://localhost:8080/rest/services/compat/MapServer"
echo "    WMS 1.3:        http://localhost:8080/rest/services/compat/MapServer/WMS"
echo "    WMTS 1.0:       http://localhost:8080/rest/services/compat/MapServer/WMTS"
echo "    OGC Features:   http://localhost:8080/ogc/features"
echo "    OData v4:       http://localhost:8080/odata"
echo "    Admin password: compat-admin-password"
echo ""
echo "  Run client tests from PowerShell:"
echo "    .\\scripts\\client-compat\\run-client-compat-tests.ps1 -BaseUrl http://localhost:8080"
echo ""
echo "  Teardown when done:"
echo "    ./scripts/client-compat/client-compat-server.sh --teardown"
echo ""
echo -e "${BLUE}Layers seeded:${NC}"
echo "    0  Cities      (1200 pts, SRID 4326, pagination stress)"
echo "    1  Rivers      (50 lines, SRID 4326)"
echo "    2  Counties    (200 polygons, SRID 4326)"
echo "    3  Sensors     (100 multipoints, SRID 4326)"
echo "    4  Pipelines   (30 multilines, SRID 4326)"
echo "    5  Parcels     (80 multipolygons, SRID 4326)"
echo "    6  WebMercPts  (50 pts, SRID 3857, CRS transform)"
echo "    7  UTMParcels  (50 polygons, SRID 32610, CRS mix)"
echo "    8  Events      (200 pts, temporal fields, 5 null-geom)"
