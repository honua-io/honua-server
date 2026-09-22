#!/usr/bin/env bash
# Boot a REAL Honua.Server + PostGIS on this runner and leave it running, so the
# post-apply validation lane has a deployed environment to validate against
# (honua-server#4414).
#
# `cloud-post-apply-validation.yml` was workflow_call/workflow_dispatch-only with
# zero callers, so CloudDeploymentValidationTests — which holds the only
# row-count assertion adjacent to the ECS story
# (`featuresProcessed == 1`, `failedFeatures == 0`) — never executed anywhere.
# This script gives that workflow a target it can always reach, for free, with no
# cloud account: the same shape `scripts/ci/server-boot-smoke.sh` proves on every
# PR, except that the server is left up and its credentials are exported for the
# validation run instead of being torn down at readiness.
#
# It writes the resolved connection details to $GITHUB_ENV (when present) and to
# the file named by --env-file, and records the PIDs/container it started in
# $RUNNER_TEMP so the caller's always() teardown can stop them.
#
# Unlike the live-cloud path this target is single-instance and local, so the lane
# it feeds proves the post-apply CONTRACT (health, deploy preflight, admin
# control-plane, staged-import row counts) against a real server — NOT a cloud
# substrate. Anything that needs a real ECS/Lambda/AKS deployment stays behind the
# base_url input.

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ENV_FILE=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --env-file)
            ENV_FILE="${2:-}"
            shift 2
            ;;
        -h|--help)
            sed -n '2,25p' "${BASH_SOURCE[0]}"
            exit 0
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 1
            ;;
    esac
done

STATE_DIR="${RUNNER_TEMP:-/tmp}"
IMAGE="${HONUA_LOCAL_TARGET_POSTGIS_IMAGE:-$(grep -oE 'postgis/postgis:[0-9]+(\.[0-9]+)*-[0-9]+(\.[0-9]+)*' tests/dotnet/Honua.TestKit/PostgresFixture.cs | head -n 1)}"
DB_CONTAINER="honua-post-apply-postgis-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
DB_PORT="${HONUA_LOCAL_TARGET_DB_PORT:-5432}"
DB_NAME="honua_post_apply"
DB_USER="honua_post_apply"
DB_PASSWORD="honua_post_apply"
SERVER_PORT="${HONUA_LOCAL_TARGET_PORT:-5000}"
SERVER_LOG="${STATE_DIR}/honua-post-apply-server.log"
DB_INIT_LOG="${STATE_DIR}/honua-post-apply-postgis.init.log"
SERVER_PID_FILE="${STATE_DIR}/honua-post-apply-server.pid"
DB_CONTAINER_FILE="${STATE_DIR}/honua-post-apply-db.container"
# The admin key the validation run authenticates with. Generated per run so no
# static credential is baked into the repository or the lane.
ADMIN_API_KEY="${HONUA_LOCAL_TARGET_ADMIN_API_KEY:-$(head -c 24 /dev/urandom | base64 | tr -d '=+/' )}"
ADMIN_PASSWORD="LocalPostApply123!"
POSTGIS_INIT_WAIT_SECONDS=120
SERVER_READY_WAIT_SECONDS=150

SERVER_DLL="${HONUA_LOCAL_TARGET_SERVER_DLL:-${REPO_ROOT}/src/Honua.Server/bin/Release/net10.0/Honua.Server.dll}"

if [[ -z "${IMAGE}" ]]; then
    echo "::error::Could not resolve the PostGIS image from PostgresFixture.cs." >&2
    exit 1
fi
if [[ ! -f "${SERVER_DLL}" ]]; then
    echo "::error::${SERVER_DLL} is missing — build src/Honua.Server before booting the local post-apply target." >&2
    exit 1
fi

echo "Starting PostGIS sidecar ${IMAGE} for the local post-apply target."
docker run --detach --rm \
    --name "${DB_CONTAINER}" \
    --publish "0.0.0.0:${DB_PORT}:5432" \
    --env POSTGRES_DB="${DB_NAME}" \
    --env POSTGRES_USER="${DB_USER}" \
    --env POSTGRES_PASSWORD="${DB_PASSWORD}" \
    --env POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL \
    "${IMAGE}" -c max_connections=200 >/dev/null
printf '%s\n' "${DB_CONTAINER}" >"${DB_CONTAINER_FILE}"

rm -f "${DB_INIT_LOG}"
docker logs --follow "${DB_CONTAINER}" >"${DB_INIT_LOG}" 2>&1 &

echo "Waiting for PostGIS initialization on localhost:${DB_PORT}."
for _ in $(seq 1 "${POSTGIS_INIT_WAIT_SECONDS}"); do
    if grep -Fq "PostgreSQL init process complete; ready for start up." "${DB_INIT_LOG}" 2>/dev/null; then
        break
    fi
    if ! timeout 5s docker inspect -f '{{.State.Running}}' "${DB_CONTAINER}" 2>/dev/null | grep -qx true; then
        echo "::error::PostGIS sidecar exited before becoming ready." >&2
        exit 1
    fi
    sleep 1
done
if ! timeout 10s docker exec "${DB_CONTAINER}" pg_isready -p 5432 -U "${DB_USER}" -d "${DB_NAME}" >/dev/null 2>&1; then
    echo "::error::PostGIS sidecar did not expose the initialized database within ${POSTGIS_INIT_WAIT_SECONDS}s." >&2
    exit 1
fi

# Keep the extension set identical to PostgresFixture so the target has the same
# database shape the integration harness proves against.
timeout 20s docker exec "${DB_CONTAINER}" psql -v ON_ERROR_STOP=1 -p 5432 -U "${DB_USER}" -d "${DB_NAME}" \
    -c 'CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;' \
    >/dev/null

rm -f "${SERVER_LOG}"
echo "Starting ${SERVER_DLL} with migrations enabled on port ${SERVER_PORT}."
(
    export ASPNETCORE_URLS="http://127.0.0.1:${SERVER_PORT}"
    export ASPNETCORE_ENVIRONMENT=Test
    export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}"
    export ConnectionStrings__honua="${ConnectionStrings__DefaultConnection}"
    export HONUA_ADMIN_PASSWORD="${ADMIN_PASSWORD}"
    export HONUA_ADMIN_API_KEY="${ADMIN_API_KEY}"
    export HONUA_REGISTER_TEST_INFRASTRUCTURE=true
    # Migrations MUST run: the staged-import row-count cell needs a real schema.
    unset HONUA_SKIP_MIGRATIONS
    export ASPNETCORE_FORWARDEDHEADERS_ENABLED=false
    export Licensing__DevGrantEdition=Pro
    export Security__ConnectionEncryption__MasterKey='test-master-key-that-is-at-least-32-characters-long-for-security'
    export Security__ConnectionEncryption__Salt='dGVzdC1zYWx0LWZvci1lbmNyeXB0aW9uLXRlc3RpbmctcHVycG9zZXM='
    export Database__QueryCache__EnableAutomaticCaching=false
    export Limits__Attachments__AllowedMimeTypes='image/*,application/pdf,text/plain'
    cd "$(dirname "${SERVER_DLL}")"
    exec dotnet "$(basename "${SERVER_DLL}")"
) >"${SERVER_LOG}" 2>&1 &
SERVER_PID=$!
printf '%s\n' "${SERVER_PID}" >"${SERVER_PID_FILE}"

echo "Waiting up to ${SERVER_READY_WAIT_SECONDS}s for /healthz/ready."
ready=false
for _ in $(seq 1 "${SERVER_READY_WAIT_SECONDS}"); do
    if response="$(curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:${SERVER_PORT}/healthz/ready" 2>/dev/null)" && [[ "${response}" == Ready ]]; then
        ready=true
        break
    fi
    server_state="$(ps -o stat= -p "${SERVER_PID}" 2>/dev/null | tr -d ' ' || true)"
    if [[ -z "${server_state}" || "${server_state}" == Z* ]]; then
        echo "::error::Honua.Server exited before becoming ready." >&2
        sed -n '1,240p' "${SERVER_LOG}" >&2 || true
        exit 1
    fi
    sleep 1
done
if [[ "${ready}" != true ]]; then
    echo "::error::Honua.Server did not become ready within ${SERVER_READY_WAIT_SECONDS}s." >&2
    sed -n '1,240p' "${SERVER_LOG}" >&2 || true
    exit 1
fi

emit() {
    local line="$1"
    if [[ -n "${ENV_FILE}" ]]; then
        printf '%s\n' "${line}" >>"${ENV_FILE}"
    fi
    if [[ -n "${GITHUB_ENV:-}" ]]; then
        printf '%s\n' "${line}" >>"${GITHUB_ENV}"
    fi
}

emit "HONUA_CLOUD_TEST_BASE_URL=http://127.0.0.1:${SERVER_PORT}"
emit "HONUA_CLOUD_TEST_ADMIN_API_KEY=${ADMIN_API_KEY}"
emit "HONUA_CLOUD_TEST_EXPECTED_ENVIRONMENT=Test"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_HOST=127.0.0.1"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_PORT=${DB_PORT}"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_NAME=${DB_NAME}"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_USERNAME=${DB_USER}"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_PASSWORD=${DB_PASSWORD}"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_SSL_MODE=Disable"
emit "HONUA_CLOUD_TEST_PUBLISH_DB_SSL_REQUIRED=false"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
        printf 'base_url=http://127.0.0.1:%s\n' "${SERVER_PORT}"
        printf 'server_pid=%s\n' "${SERVER_PID}"
        printf 'db_container=%s\n' "${DB_CONTAINER}"
    } >>"${GITHUB_OUTPUT}"
fi
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    {
        echo '### Local post-apply target'
        echo
        echo "- Base URL: \`http://127.0.0.1:${SERVER_PORT}\`"
        echo "- Database: \`${DB_NAME}\` on \`${IMAGE}\` (migrations applied on boot)"
        echo '- Substrate: single-instance local server, NOT a cloud deployment.'
        echo
    } >>"${GITHUB_STEP_SUMMARY}"
fi

echo "Local post-apply target is ready at http://127.0.0.1:${SERVER_PORT}."
