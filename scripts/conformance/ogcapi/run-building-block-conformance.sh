#!/usr/bin/env bash
# Run every vendored OGC API building-block validator against one exact server
# image. This lane is separate from the official ETS runs because CQL2, MVT,
# TMS 2.0, and Maps do not all have an official CITE suite.

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/docker/cite/ogc-api-features/compose.yml"
COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-honua-ogcapi-building-blocks}"
SERVER_IMAGE="${HONUA_CONFORMANCE_SERVER_IMAGE:-honua-server:latest}"
SOURCE_SHA="${HONUA_CONFORMANCE_SOURCE_SHA:-$(git -C "$REPO_ROOT" rev-parse HEAD)}"
SERVER_PORT="${HONUA_CONFORMANCE_SERVER_PORT:-8091}"
BASE_URL="${HONUA_BASE_URL:-http://127.0.0.1:${SERVER_PORT}}"
RESULTS_DIR="${HONUA_CONFORMANCE_RESULTS_DIR:-ogcapi-conformance-results}"
COLLECTION_ID="${HONUA_CONFORMANCE_COLLECTION_ID:-}"

export COMPOSE_PROJECT_NAME
export HONUA_CITE_FEATURES_SERVER_PORT="$SERVER_PORT"
export HONUA_CITE_SERVER_IMAGE="$SERVER_IMAGE"

# Create the artifact directory before any prerequisite check so a setup
# failure still leaves a stable artifact target for the always-run upload step.
mkdir -p "$RESULTS_DIR"

if [[ ! "$SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]]; then
    echo "HONUA_CONFORMANCE_SOURCE_SHA must be a full 40-character SHA" >&2
    exit 2
fi

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    echo "Docker Compose is required" >&2
    exit 2
fi

if ! docker image inspect "$SERVER_IMAGE" >/dev/null 2>&1; then
    echo "candidate image is not available locally: $SERVER_IMAGE" >&2
    exit 2
fi

IMAGE_REVISION="$(docker image inspect "$SERVER_IMAGE" --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}')"
if [[ "$IMAGE_REVISION" != "$SOURCE_SHA" ]]; then
    echo "candidate image revision '$IMAGE_REVISION' does not match source '$SOURCE_SHA'" >&2
    exit 1
fi

rm -f "$RESULTS_DIR"/candidate.json "$RESULTS_DIR"/conformance.json \
    "$RESULTS_DIR"/validator-status.tsv "$RESULTS_DIR"/summary.md \
    "$RESULTS_DIR"/*.log

compose() {
    docker compose -f "$COMPOSE_FILE" "$@"
}

cleanup() {
    compose down --remove-orphans --volumes >/dev/null 2>&1 || true
}
trap cleanup EXIT

wait_for_service() {
    local service="$1"
    local timeout_seconds="${2:-300}"
    local started elapsed status
    started="$(date +%s)"
    while true; do
        status="$(compose ps --format '{{.Service}} {{.State}} {{.Health}}' "$service" 2>/dev/null || true)"
        if [[ "$status" == *" healthy"* ]]; then
            return 0
        fi
        if [[ "$status" == *"exited"* || "$status" == *"restarting"* ]]; then
            echo "${service} did not become healthy: ${status}" >&2
            compose logs --tail=100 "$service" >&2 || true
            return 1
        fi
        elapsed=$(( $(date +%s) - started ))
        if (( elapsed >= timeout_seconds )); then
            echo "timed out waiting for ${service} (${elapsed}s)" >&2
            compose logs --tail=100 "$service" >&2 || true
            return 1
        fi
        sleep 5
    done
}

wait_for_endpoint() {
    local url="$1"
    local timeout_seconds="${2:-120}"
    local started elapsed
    started="$(date +%s)"
    while true; do
        if curl --silent --show-error --fail --max-time 10 "$url" >/dev/null 2>&1; then
            return 0
        fi
        elapsed=$(( $(date +%s) - started ))
        if (( elapsed >= timeout_seconds )); then
            echo "timed out waiting for ${url}" >&2
            compose logs --tail=100 honua-server >&2 || true
            return 1
        fi
        sleep 5
    done
}

echo "== OGC API building-block conformance =="
echo "candidate source: $SOURCE_SHA"
echo "candidate image:  $SERVER_IMAGE"
echo "image revision:   $IMAGE_REVISION"

compose down --remove-orphans --volumes >/dev/null 2>&1 || true
compose up -d postgres
wait_for_service postgres 180

# Start once for migrations, then seed the same database before the server
# caches the catalog. The seed is the existing deterministic Features CITE
# fixture, shared by the CQL2 and tile validators.
compose up -d honua-server
wait_for_service honua-server 300
compose stop honua-server

postgres_container="$(compose ps -q postgres)"
if [[ -z "$postgres_container" ]]; then
    echo "Postgres container was not created" >&2
    exit 1
fi
docker cp "$REPO_ROOT/docker/cite/ogc-api-features/seed.sql" "$postgres_container:/tmp/ogcapi-building-blocks-seed.sql"
docker exec "$postgres_container" psql -v ON_ERROR_STOP=1 -U postgres -d honua_cite \
    -f /tmp/ogcapi-building-blocks-seed.sql >/dev/null

compose up -d honua-server
wait_for_service honua-server 300
wait_for_endpoint "$BASE_URL/ogc/features/conformance"
wait_for_endpoint "$BASE_URL/ogc/features/collections"

curl --silent --show-error --fail "$BASE_URL/ogc/features/collections" \
    > "$RESULTS_DIR/collections.json"
if [[ -z "$COLLECTION_ID" ]]; then
    COLLECTION_ID="$(python3 - "$RESULTS_DIR/collections.json" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as stream:
    collections = json.load(stream).get("collections", [])
if not collections or not isinstance(collections[0].get("id"), str):
    raise SystemExit("the candidate returned no usable OGC Features collection")
print(collections[0]["id"])
PY
)"
fi
if [[ -z "$COLLECTION_ID" ]]; then
    echo "candidate collection ID is empty" >&2
    exit 1
fi

curl --silent --show-error --fail "$BASE_URL/ogc/features/conformance" \
    > "$RESULTS_DIR/conformance.json"
cat > "$RESULTS_DIR/candidate.json" <<EOF
{
  "sourceSha": "$SOURCE_SHA",
  "image": "$SERVER_IMAGE",
  "imageRevision": "$IMAGE_REVISION",
  "baseUrl": "$BASE_URL",
  "collectionId": "$COLLECTION_ID"
}
EOF

# Keep the declaration boundary executable. The building-block validators
# validate live queryables and exercise CQL2/filter behavior, but they are not a
# complete ETS-equivalent class suite. Only queryables is therefore advertised;
# this check prevents a future endpoint edit from silently widening the public
# claim without widening the evidence lane.
python3 - "$RESULTS_DIR/conformance.json" <<'PY'
import json
import sys

expected = {
    "http://www.opengis.net/spec/ogcapi-features-3/1.0/conf/queryables",
}
target_prefixes = (
    "http://www.opengis.net/spec/ogcapi-features-2/",
    "http://www.opengis.net/spec/ogcapi-features-3/",
    "http://www.opengis.net/spec/ogcapi-features-4/",
    "http://www.opengis.net/spec/cql2/1.0/conf/",
)

with open(sys.argv[1], encoding="utf-8") as stream:
    declaration = json.load(stream)
actual = {
    value
    for value in declaration.get("conformsTo", [])
    if isinstance(value, str) and value.startswith(target_prefixes)
}
if actual != expected:
    print("targeted Features/CQL2 declaration does not match the evidenced set", file=sys.stderr)
    print(f"  missing: {sorted(expected - actual)}", file=sys.stderr)
    print(f"  extra:   {sorted(actual - expected)}", file=sys.stderr)
    raise SystemExit(1)
print(f"declaration boundary valid ({len(actual)} evidenced Part 3/CQL2 classes)")
PY

run_validator() {
    local name="$1"
    shift
    local log_file="$RESULTS_DIR/${name}.log"
    local exit_code
    echo "-- ${name} --"
    set +e
    "$@" >"$log_file" 2>&1
    exit_code=$?
    set -e
    printf '%s\t%s\n' "$name" "$exit_code" >> "$RESULTS_DIR/validator-status.tsv"
    cat "$log_file"
    if (( exit_code != 0 )); then
        echo "${name} failed with exit code ${exit_code}" >&2
    fi
    return 0
}

: > "$RESULTS_DIR/validator-status.tsv"
run_validator cql2 \
    python3 "$HERE/cql2_validator.py" \
    --base-url "$BASE_URL" --collection "$COLLECTION_ID" --geom-field shape --category-field category
run_validator mvt \
    node "$HERE/mvt_validator.mjs" --base-url "$BASE_URL" --collection "$COLLECTION_ID"
run_validator maps \
    node "$HERE/maps_validator.mjs" --base-url "$BASE_URL" --collection "$COLLECTION_ID"
run_validator schemathesis \
    bash "$HERE/run-schemathesis.sh" --base-url "$BASE_URL" --max-examples "${SCHEMATHESIS_MAX_EXAMPLES:-15}"

overall_status=0
while IFS=$'\t' read -r name exit_code; do
    if [[ "$exit_code" != "0" ]]; then
        overall_status=1
    fi
done < "$RESULTS_DIR/validator-status.tsv"

cat > "$RESULTS_DIR/summary.md" <<EOF
# OGC API building-block conformance

- Candidate source SHA: \`$SOURCE_SHA\`
- Candidate image: \`$SERVER_IMAGE\`
- Image revision label: \`$IMAGE_REVISION\`
- Features conformance declaration: \`conformance.json\`

## Validators

| Validator | Exit code |
| --- | ---: |
$(sed -e 's/^/| `/; s/\t/` | /; s/$/ |/' "$RESULTS_DIR/validator-status.tsv")

The lane runs the complete existing validator set. A validator failure is
blocking; no validator is treated as optional or skipped.
EOF

if (( overall_status != 0 )); then
    echo "OGC API building-block conformance FAILED"
    exit "$overall_status"
fi
echo "OGC API building-block conformance PASSED"
