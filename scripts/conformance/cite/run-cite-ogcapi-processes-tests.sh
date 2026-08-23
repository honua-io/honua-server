#!/bin/bash

set -euo pipefail

CITE_COMPOSE_FILE="docker/cite/ogc-api-processes/compose.yml"
CITE_CONFIG_FILE="docker/cite/ogc-api-processes/config/test-run-props.xml"
CITE_RESULTS_DIR="${HONUA_CITE_OGCAPI_PROCESSES_RESULTS_DIR:-cite-ogcapi-processes-results}"
CITE_TIMEOUT="${HONUA_CITE_OGCAPI_PROCESSES_TIMEOUT:-2700}"
HEALTHCHECK_TIMEOUT="${HONUA_CITE_OGCAPI_PROCESSES_HEALTHCHECK_TIMEOUT:-300}"
HONUA_CITE_OGCAPI_PROCESSES_SERVER_PORT="${HONUA_CITE_OGCAPI_PROCESSES_SERVER_PORT:-8101}"
PROFILE="diagnostic"
CLEANUP=true
VERBOSE=false
SKIP_BUILD="${HONUA_CITE_SKIP_BUILD:-false}"
SKIP_ETS_BUILD="${HONUA_CITE_SKIP_ETS_BUILD:-false}"
REQUIRE_SERVER_PROVENANCE="${HONUA_CITE_REQUIRE_SERVER_PROVENANCE:-false}"
SERVER_BUILD_MODE="${HONUA_CITE_SERVER_BUILD_MODE:-}"
REQUESTED_SERVER_IMAGE="${HONUA_CITE_REQUESTED_SERVER_IMAGE:-}"
ETS_IMAGE="honua-cite-ogcapi-processes10-ets:1.4-pinned"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --profile) PROFILE="$2"; shift 2 ;;
        --no-cleanup) CLEANUP=false; shift ;;
        --verbose) VERBOSE=true; shift ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --help|-h)
            echo "Usage: $0 [--profile diagnostic] [--no-cleanup] [--verbose] [--skip-build]"
            exit 0
            ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ "$PROFILE" != "diagnostic" ]]; then
    echo "Unknown OGC API Processes CITE profile: $PROFILE" >&2
    exit 2
fi
case "$REQUIRE_SERVER_PROVENANCE" in
    true|false) ;;
    *) echo "HONUA_CITE_REQUIRE_SERVER_PROVENANCE must be true or false" >&2; exit 2 ;;
esac
case "$SKIP_ETS_BUILD" in
    true|false) ;;
    *) echo "HONUA_CITE_SKIP_ETS_BUILD must be true or false" >&2; exit 2 ;;
esac

if [[ -z "$SERVER_BUILD_MODE" ]]; then
    if [[ "$SKIP_BUILD" == "true" ]]; then
        SERVER_BUILD_MODE="local-existing"
    else
        SERVER_BUILD_MODE="source-build"
    fi
fi

CHECKED_OUT_HONUA_GIT_SHA="$(git rev-parse HEAD 2>/dev/null || true)"
TESTED_HONUA_GIT_SHA="${HONUA_CITE_TESTED_GIT_SHA:-$CHECKED_OUT_HONUA_GIT_SHA}"
if [[ "$SERVER_BUILD_MODE" == "local-existing" ]]; then
    REQUIRE_SERVER_PROVENANCE=true
    if [[ ! "${HONUA_CITE_TESTED_GIT_SHA:-}" =~ ^[0-9a-f]{40}$ ]]; then
        echo "Local-existing CITE images require HONUA_CITE_TESTED_GIT_SHA as a full SHA" >&2
        exit 2
    fi
fi
if [[ "${GITHUB_ACTIONS:-false}" == "true" ]]; then
    REQUIRE_SERVER_PROVENANCE=true
    if [[ ! "$TESTED_HONUA_GIT_SHA" =~ ^[0-9a-f]{40}$ ]]; then
        echo "HONUA_CITE_TESTED_GIT_SHA must be a full SHA in GitHub Actions" >&2
        exit 2
    fi
fi

command -v curl >/dev/null || { echo "curl is required" >&2; exit 2; }
command -v docker >/dev/null || { echo "Docker is required" >&2; exit 2; }
command -v python3 >/dev/null || { echo "Python 3 is required" >&2; exit 2; }
if docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD=(docker compose)
elif command -v docker-compose >/dev/null; then
    COMPOSE_CMD=(docker-compose)
else
    echo "Docker Compose is required" >&2
    exit 2
fi

if [[ "$SKIP_BUILD" == "true" ]]; then
    docker image inspect honua-server:latest >/dev/null 2>&1 || {
        echo "HONUA_CITE_SKIP_BUILD=true requires honua-server:latest" >&2
        exit 2
    }
else
    scripts/docker/build-with-github-packages.sh -t honua-server:latest .
fi

mkdir -p "$CITE_RESULTS_DIR"
if find "$CITE_RESULTS_DIR" -mindepth 1 -print -quit | grep -q .; then
    echo "Results directory must be empty: $CITE_RESULTS_DIR" >&2
    exit 2
fi
mkdir -p "$CITE_RESULTS_DIR/raw"
cp "$CITE_CONFIG_FILE" "$CITE_RESULTS_DIR/test-run-props.xml"
HONUA_CITE_OGCAPI_PROCESSES_RESULTS_PATH="$(cd "$CITE_RESULTS_DIR" && pwd)"
HONUA_CITE_HOST_UID="$(id -u)"
HONUA_CITE_HOST_GID="$(id -g)"
export HONUA_CITE_OGCAPI_PROCESSES_RESULTS_PATH HONUA_CITE_HOST_UID HONUA_CITE_HOST_GID
export HONUA_CITE_OGCAPI_PROCESSES_SERVER_PORT

cleanup() {
    local exit_code=$?
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" logs --no-color honua-server \
        > "$CITE_RESULTS_DIR/honua-server.log" 2>&1 || true
    if [[ "$CLEANUP" == "true" ]]; then
        "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test down \
            --remove-orphans --volumes >/dev/null 2>&1 || true
    fi
    return "$exit_code"
}
trap cleanup EXIT

"${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test down \
    --remove-orphans --volumes >/dev/null 2>&1 || true
"${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" up -d postgres redis honua-server

mapfile -t server_ids < <(
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" ps --all -q honua-server | sed '/^$/d'
)
if [[ "${#server_ids[@]}" -ne 1 ]]; then
    echo "Expected exactly one Honua Server container; found ${#server_ids[@]}" >&2
    exit 2
fi
SERVER_CONTAINER_ID="${server_ids[0]}"
SERVER_IMAGE_ID="$(docker inspect --format '{{.Image}}' "$SERVER_CONTAINER_ID")"
docker image inspect "$SERVER_IMAGE_ID" > "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp"
test -s "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp"
mv "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp" \
    "$CITE_RESULTS_DIR/honua-server-image-inspect.json"

provenance_args=(
    --tested-git-sha "$TESTED_HONUA_GIT_SHA"
    --checkout-git-sha "$CHECKED_OUT_HONUA_GIT_SHA"
    --server-container-id "$SERVER_CONTAINER_ID"
    --server-image-id "$SERVER_IMAGE_ID"
    --server-build-mode "$SERVER_BUILD_MODE"
    --requested-server-image "$REQUESTED_SERVER_IMAGE"
    --image-inspect "$CITE_RESULTS_DIR/honua-server-image-inspect.json"
    --output "$CITE_RESULTS_DIR/honua-server-provenance.json"
)
if [[ "$REQUIRE_SERVER_PROVENANCE" == "true" ]]; then
    provenance_args+=(--require-tested-git-sha)
fi
python3 scripts/conformance/cite/write_wps20_provenance.py "${provenance_args[@]}"

HONUA_BASE_URL="http://localhost:${HONUA_CITE_OGCAPI_PROCESSES_SERVER_PORT}"
deadline=$((SECONDS + HEALTHCHECK_TIMEOUT))
until curl --silent --fail --max-time 5 "${HONUA_BASE_URL}/healthz/ready" >/dev/null; do
    if (( SECONDS >= deadline )); then
        echo "Timed out waiting for Honua Server" >&2
        exit 2
    fi
    sleep 5
done

curl --silent --show-error --fail --max-time 20 \
    -H 'Accept: application/json' "${HONUA_BASE_URL}/ogc/processes" \
    > "$CITE_RESULTS_DIR/landing-page.json"
curl --silent --show-error --fail --max-time 20 \
    -H 'Accept: application/json' "${HONUA_BASE_URL}/ogc/processes/conformance" \
    > "$CITE_RESULTS_DIR/conformance.json"
curl --silent --show-error --fail --max-time 20 \
    -H 'Accept: application/json' "${HONUA_BASE_URL}/ogc/processes/processes" \
    > "$CITE_RESULTS_DIR/process-list.json"

if [[ "$SKIP_ETS_BUILD" == "true" ]]; then
    docker image inspect "$ETS_IMAGE" >/dev/null 2>&1 || {
        echo "HONUA_CITE_SKIP_ETS_BUILD=true requires $ETS_IMAGE" >&2
        exit 2
    }
    ets_provenance="$(docker run --rm --entrypoint cat "$ETS_IMAGE" /opt/ets/PROVENANCE)"
    grep -Fxq "commit=75abd1f37fc3aad95163fdce2e33e393b1ba5a88" <<< "$ets_provenance" || {
        echo "Existing ETS image provenance does not match the pinned commit" >&2
        exit 2
    }
else
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test build cite-runner
fi
docker run --rm --entrypoint cat "$ETS_IMAGE" /opt/ets/PROVENANCE \
    > "$CITE_RESULTS_DIR/ets-provenance.txt"
docker image inspect "$ETS_IMAGE" > "$CITE_RESULTS_DIR/ets-image-inspect.json"

STARTED_AT="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
set +e
timeout "$CITE_TIMEOUT" "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" \
    --profile test run --rm cite-runner > "$CITE_RESULTS_DIR/ets-runner.log" 2>&1
ets_exit_code=$?
set -e
COMPLETED_AT="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
RUN_URL=""
if [[ -n "${GITHUB_SERVER_URL:-}" && -n "${GITHUB_REPOSITORY:-}" && -n "${GITHUB_RUN_ID:-}" ]]; then
    RUN_URL="${GITHUB_SERVER_URL}/${GITHUB_REPOSITORY}/actions/runs/${GITHUB_RUN_ID}"
fi

set +e
python3 scripts/conformance/cite/parse_ogcapi_processes_results.py \
    --input "$CITE_RESULTS_DIR/raw" \
    --provenance "$CITE_RESULTS_DIR/honua-server-provenance.json" \
    --config "$CITE_RESULTS_DIR/test-run-props.xml" \
    --summary "$CITE_RESULTS_DIR/cite-ogcapi-processes-summary.md" \
    --json "$CITE_RESULTS_DIR/cite-ogcapi-processes-diagnostic.json" \
    --ets-exit-code "$ets_exit_code" \
    --started-at "$STARTED_AT" \
    --completed-at "$COMPLETED_AT" \
    --run-url "$RUN_URL"
parse_exit_code=$?
set -e

if [[ "$VERBOSE" == "true" || "$parse_exit_code" -ne 0 ]]; then
    cat "$CITE_RESULTS_DIR/ets-runner.log"
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" logs --no-color --tail=100 honua-server || true
fi
if [[ "$ets_exit_code" -eq 124 ]]; then
    exit 124
fi
exit "$parse_exit_code"
