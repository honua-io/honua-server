#!/bin/bash

set -euo pipefail

CITE_COMPOSE_FILE="docker/cite/wps20/compose.yml"
CITE_RESULTS_DIR="${HONUA_CITE_WPS20_RESULTS_DIR:-cite-wps20-results}"
CITE_TIMEOUT="${HONUA_CITE_WPS20_TIMEOUT:-2700}"
HONUA_HEALTHCHECK_TIMEOUT="${HONUA_CITE_WPS20_HEALTHCHECK_TIMEOUT:-300}"
HONUA_CITE_WPS20_SERVER_PORT="${HONUA_CITE_WPS20_SERVER_PORT:-8100}"
ECHO_PROCESS_ID="${HONUA_CITE_WPS20_ECHO_PROCESS_ID:-honua.cite.echo}"
PROFILE="basic-async"
CLEANUP=true
INTERACTIVE=false
VERBOSE=false
SKIP_BUILD="${HONUA_CITE_SKIP_BUILD:-false}"
REQUIRE_SERVER_PROVENANCE="${HONUA_CITE_REQUIRE_SERVER_PROVENANCE:-false}"
SERVER_BUILD_MODE="${HONUA_CITE_SERVER_BUILD_MODE:-}"
REQUESTED_SERVER_IMAGE="${HONUA_CITE_REQUESTED_SERVER_IMAGE:-}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --profile) PROFILE="$2"; shift 2 ;;
        --no-cleanup) CLEANUP=false; shift ;;
        --interactive) INTERACTIVE=true; CLEANUP=false; shift ;;
        --verbose) VERBOSE=true; shift ;;
        --skip-build) SKIP_BUILD=true; shift ;;
        --help|-h)
            echo "Usage: $0 [--profile basic-async|basic-sync|all] [--no-cleanup] [--interactive] [--verbose] [--skip-build]"
            exit 0
            ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

case "$PROFILE" in
    basic-async|basic-sync|all) ;;
    *) echo "Unknown WPS CITE profile: $PROFILE" >&2; exit 2 ;;
esac

case "$REQUIRE_SERVER_PROVENANCE" in
    true|false) ;;
    *) echo "HONUA_CITE_REQUIRE_SERVER_PROVENANCE must be true or false" >&2; exit 2 ;;
esac

if [[ -z "$SERVER_BUILD_MODE" ]]; then
    if [[ "$SKIP_BUILD" == "true" ]]; then
        SERVER_BUILD_MODE="local-existing"
    else
        SERVER_BUILD_MODE="source-build"
    fi
fi

if [[ "${GITHUB_ACTIONS:-false}" == "true" ]]; then
    REQUIRE_SERVER_PROVENANCE=true
    if [[ -z "${HONUA_CITE_TESTED_GIT_SHA:-}" ]]; then
        echo "HONUA_CITE_TESTED_GIT_SHA is required in GitHub Actions" >&2
        exit 2
    fi
fi

TESTED_HONUA_GIT_SHA="${HONUA_CITE_TESTED_GIT_SHA:-}"
CHECKED_OUT_HONUA_GIT_SHA="$(git rev-parse HEAD 2>/dev/null || true)"
if [[ -z "$TESTED_HONUA_GIT_SHA" ]]; then
    TESTED_HONUA_GIT_SHA="${CHECKED_OUT_HONUA_GIT_SHA:-unknown}"
    echo "Warning: HONUA_CITE_TESTED_GIT_SHA is unset; using local checkout SHA '$TESTED_HONUA_GIT_SHA'" >&2
fi

if [[ ! "$ECHO_PROCESS_ID" =~ ^[A-Za-z0-9._:-]+$ ]]; then
    echo "Invalid WPS echo process identifier" >&2
    exit 2
fi

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
    echo "Using existing honua-server:latest image"
else
    scripts/docker/build-with-github-packages.sh -t honua-server:latest .
fi

mkdir -p "$CITE_RESULTS_DIR/raw"
rm -rf "$CITE_RESULTS_DIR"/*
mkdir -p "$CITE_RESULTS_DIR/raw"
HONUA_CITE_WPS20_RESULTS_PATH="$(cd "$CITE_RESULTS_DIR" && pwd)"
HONUA_CITE_HOST_UID="$(id -u)"
HONUA_CITE_HOST_GID="$(id -g)"
export HONUA_CITE_WPS20_RESULTS_PATH HONUA_CITE_HOST_UID HONUA_CITE_HOST_GID HONUA_CITE_WPS20_SERVER_PORT

cat > "$CITE_RESULTS_DIR/test-run-props.xml" <<EOF_PROPS
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE properties SYSTEM "http://java.sun.com/dtd/properties.dtd">
<properties version="1.0">
  <comment>Honua WPS 2.0 CITE run</comment>
  <entry key="IUT">http://honua-server:8080/wps</entry>
  <entry key="SERVICE_URL">http://honua-server:8080/wps</entry>
  <entry key="ECHO_PROCESS_ID">${ECHO_PROCESS_ID}</entry>
</properties>
EOF_PROPS

cleanup() {
    local exit_code=$?
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" logs --no-color honua-server > "$CITE_RESULTS_DIR/honua-server.log" 2>&1 || true
    if [[ "$CLEANUP" == "true" ]]; then
        "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test down --remove-orphans --volumes >/dev/null 2>&1 || true
    fi
    return "$exit_code"
}
trap cleanup EXIT

"${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test down --remove-orphans --volumes >/dev/null 2>&1 || true
"${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" up -d postgres redis honua-server

mapfile -t honua_server_container_ids < <(
    "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" ps --all -q honua-server | sed '/^$/d'
)
if [[ "${#honua_server_container_ids[@]}" -ne 1 ]]; then
    echo "Expected exactly one WPS CITE Honua Server container; found ${#honua_server_container_ids[@]}" >&2
    exit 2
fi
HONUA_SERVER_CONTAINER_ID="${honua_server_container_ids[0]}"
HONUA_SERVER_IMAGE_ID="$(docker inspect --format '{{.Image}}' "$HONUA_SERVER_CONTAINER_ID")"
if [[ -z "$HONUA_SERVER_IMAGE_ID" ]]; then
    echo "Unable to identify the WPS CITE Honua Server image" >&2
    exit 2
fi

printf '%s\n' "$TESTED_HONUA_GIT_SHA" > "$CITE_RESULTS_DIR/tested-honua-git-sha.txt.tmp"
mv "$CITE_RESULTS_DIR/tested-honua-git-sha.txt.tmp" "$CITE_RESULTS_DIR/tested-honua-git-sha.txt"
printf '%s\n' "$HONUA_SERVER_IMAGE_ID" > "$CITE_RESULTS_DIR/honua-server-image-id.txt.tmp"
mv "$CITE_RESULTS_DIR/honua-server-image-id.txt.tmp" "$CITE_RESULTS_DIR/honua-server-image-id.txt"
docker image inspect "$HONUA_SERVER_IMAGE_ID" > "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp"
test -s "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp"
mv "$CITE_RESULTS_DIR/honua-server-image-inspect.json.tmp" "$CITE_RESULTS_DIR/honua-server-image-inspect.json"
provenance_args=(
    --tested-git-sha "$TESTED_HONUA_GIT_SHA"
    --checkout-git-sha "${CHECKED_OUT_HONUA_GIT_SHA:-unknown}"
    --server-container-id "$HONUA_SERVER_CONTAINER_ID"
    --server-image-id "$HONUA_SERVER_IMAGE_ID"
    --server-build-mode "$SERVER_BUILD_MODE"
    --requested-server-image "$REQUESTED_SERVER_IMAGE"
    --image-inspect "$CITE_RESULTS_DIR/honua-server-image-inspect.json"
    --output "$CITE_RESULTS_DIR/honua-server-provenance.json"
)
if [[ "$REQUIRE_SERVER_PROVENANCE" == "true" ]]; then
    provenance_args+=(--require-tested-git-sha)
fi
python3 scripts/conformance/cite/write_wps20_provenance.py "${provenance_args[@]}"

HONUA_BASE_URL="http://localhost:${HONUA_CITE_WPS20_SERVER_PORT}"
deadline=$((SECONDS + HONUA_HEALTHCHECK_TIMEOUT))
until curl --silent --fail --max-time 5 "${HONUA_BASE_URL}/healthz/ready" >/dev/null; do
    if (( SECONDS >= deadline )); then
        echo "Timed out waiting for Honua Server" >&2
        exit 2
    fi
    sleep 5
done

CAPABILITIES_URL="${HONUA_BASE_URL}/wps?service=WPS&request=GetCapabilities&version=2.0.0"
DESCRIBE_URL="${HONUA_BASE_URL}/wps?service=WPS&request=DescribeProcess&version=2.0.0&identifier=${ECHO_PROCESS_ID}"
curl --silent --show-error --fail --max-time 20 "$CAPABILITIES_URL" > "$CITE_RESULTS_DIR/capabilities.xml"
curl --silent --show-error --fail --max-time 20 "$DESCRIBE_URL" > "$CITE_RESULTS_DIR/describe-echo.xml"

if [[ "$INTERACTIVE" == "true" ]]; then
    echo "Honua WPS: $CAPABILITIES_URL"
    echo "Run the ETS with: ${COMPOSE_CMD[*]} -f $CITE_COMPOSE_FILE --profile test run --rm cite-runner"
    tail -f /dev/null
fi

"${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test build cite-runner
docker run --rm --entrypoint cat honua-cite-wps20-ets:1.1 /opt/ets/PROVENANCE > "$CITE_RESULTS_DIR/ets-provenance.txt"
docker image inspect honua-cite-wps20-ets:1.1 > "$CITE_RESULTS_DIR/ets-image-inspect.json"

set +e
timeout "$CITE_TIMEOUT" "${COMPOSE_CMD[@]}" -f "$CITE_COMPOSE_FILE" --profile test run --rm cite-runner \
    > "$CITE_RESULTS_DIR/ets-runner.log" 2>&1
ets_exit_code=$?
set -e

if [[ "$ets_exit_code" -eq 124 ]]; then
    echo "WPS CITE ETS timed out after ${CITE_TIMEOUT}s" >&2
fi

set +e
python3 scripts/conformance/cite/parse_wps20_results.py \
    --input "$CITE_RESULTS_DIR/raw" \
    --profile "$PROFILE" \
    --summary "$CITE_RESULTS_DIR/cite-wps20-summary.md" \
    --json "$CITE_RESULTS_DIR/cite-wps20-summary.json" \
    --ets-exit-code "$ets_exit_code"
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
