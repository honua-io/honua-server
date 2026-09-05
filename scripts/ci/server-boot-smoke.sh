#!/usr/bin/env bash
# Boot the already-built Honua.Server against the same PostGIS shape used by
# the integration harness. This is intentionally an out-of-process check:
# WebApplicationFactory tests can replace the migration runner and therefore
# cannot catch a real host startup or migration failure.

set -Eeuo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${HONUA_PR_GATE_REPO_ROOT:-$(cd "${SCRIPT_DIR}/../.." && pwd)}"
cd "${REPO_ROOT}"

IMAGE="${HONUA_PR_GATE_POSTGIS_IMAGE:-$(grep -oE 'postgis/postgis:[0-9]+(\.[0-9]+)*-[0-9]+(\.[0-9]+)*' tests/dotnet/Honua.TestKit/PostgresFixture.cs | head -n 1)}"
DB_CONTAINER="honua-pr-gate-postgis-${GITHUB_RUN_ID:-local}-${GITHUB_RUN_ATTEMPT:-1}"
SERVER_PORT="${HONUA_SERVER_BOOT_SMOKE_PORT:-5000}"
DB_PORT="${HONUA_PR_GATE_POSTGRES_PORT:-5432}"
SERVER_LOG="${HONUA_SERVER_BOOT_SMOKE_LOG:-${RUNNER_TEMP:-/tmp}/honua-server-boot-smoke.log}"
DB_LOG="${HONUA_SERVER_BOOT_SMOKE_DB_LOG:-${RUNNER_TEMP:-/tmp}/honua-server-boot-smoke-postgis.log}"
DB_INIT_LOG="${HONUA_SERVER_BOOT_SMOKE_DB_INIT_LOG:-${DB_LOG}.init}"
SERVER_DLL="${HONUA_SERVER_BOOT_SMOKE_DLL:-${REPO_ROOT}/src/Honua.Server/bin/Release/net10.0/Honua.Server.dll}"
# Keep the smoke below the workflow's 175-second wrapper while allowing the
# image's first-boot init to absorb the occasional slow Docker daemon.
POSTGIS_INIT_WAIT_SECONDS=75
SERVER_READY_WAIT_SECONDS=90
SERVER_PID=""
DB_LOG_FOLLOW_PID=""

dump_diagnostics() {
    echo "----- Honua Server boot smoke log (${SERVER_LOG}) -----" >&2
    sed -n '1,240p' "${SERVER_LOG}" >&2 2>/dev/null || true
    echo "----- PostGIS sidecar log (${DB_LOG}) -----" >&2
    if [[ -f "${DB_LOG}" ]]; then
        sed -n '1,240p' "${DB_LOG}" >&2
    else
        docker logs "${DB_CONTAINER}" >&2 2>/dev/null || true
    fi
}

cleanup() {
    local exit_code=$?
    if [[ -n "${SERVER_PID}" ]] && kill -0 "${SERVER_PID}" 2>/dev/null; then
        kill "${SERVER_PID}" 2>/dev/null || true
        wait "${SERVER_PID}" 2>/dev/null || true
    fi
    if [[ -n "${DB_LOG_FOLLOW_PID}" ]] && kill -0 "${DB_LOG_FOLLOW_PID}" 2>/dev/null; then
        kill "${DB_LOG_FOLLOW_PID}" 2>/dev/null || true
        wait "${DB_LOG_FOLLOW_PID}" 2>/dev/null || true
    fi
    if docker inspect "${DB_CONTAINER}" >/dev/null 2>&1; then
        docker logs "${DB_CONTAINER}" >"${DB_LOG}" 2>&1 || true
        docker rm --force "${DB_CONTAINER}" >/dev/null 2>&1 || true
    fi
    if (( exit_code != 0 )); then
        dump_diagnostics
    fi
    return "${exit_code}"
}
trap cleanup EXIT

if [[ -z "${IMAGE}" ]]; then
    echo "::error::Could not resolve the PostGIS image from PostgresFixture.cs." >&2
    exit 1
fi
if [[ ! -f "${SERVER_DLL}" ]]; then
    echo "::error::The lean gate did not produce ${SERVER_DLL}; refusing to build a second time." >&2
    exit 1
fi

if ! docker image inspect "${IMAGE}" >/dev/null 2>&1; then
    echo "::error::PostGIS image ${IMAGE} was not present after the overlapped pre-pull." >&2
    echo "The smoke refuses an inline pull so its added required-gate cost stays bounded." >&2
    exit 1
fi

echo "Starting PostGIS sidecar ${IMAGE}."
docker run --detach --rm \
    --name "${DB_CONTAINER}" \
    --publish "0.0.0.0:${DB_PORT}:5432" \
    --env POSTGRES_DB=honua_test \
    --env POSTGRES_USER=test \
    --env POSTGRES_PASSWORD=test \
    --env POSTGIS_GDAL_ENABLED_DRIVERS=ENABLE_ALL \
    "${IMAGE}" -c max_connections=200 >/dev/null

rm -f "${DB_INIT_LOG}"
docker logs --follow "${DB_CONTAINER}" >"${DB_INIT_LOG}" 2>&1 &
DB_LOG_FOLLOW_PID=$!

echo "Waiting for PostGIS initialization on localhost:${DB_PORT}."
for _ in $(seq 1 "${POSTGIS_INIT_WAIT_SECONDS}"); do
    # The marker is emitted only after the image's temporary init server has
    # shut down. Follow the log once instead of fetching the complete log on
    # every poll; repeated Docker API calls made cold-daemon startup consume
    # the smoke's budget on busy runners.
    if grep -Fq "PostgreSQL init process complete; ready for start up." "${DB_INIT_LOG}" 2>/dev/null; then
        break
    fi
    if ! timeout 5s docker inspect -f '{{.State.Running}}' "${DB_CONTAINER}" 2>/dev/null | grep -qx true; then
        echo "::error::PostGIS sidecar exited before becoming ready." >&2
        exit 1
    fi
    sleep 1
done
if ! grep -Fq "PostgreSQL init process complete; ready for start up." "${DB_INIT_LOG}" 2>/dev/null; then
    echo "::error::PostGIS sidecar initialization did not complete within ${POSTGIS_INIT_WAIT_SECONDS}s." >&2
    exit 1
fi
if ! timeout 10s docker exec "${DB_CONTAINER}" pg_isready -p 5432 -U test -d honua_test >/dev/null 2>&1; then
    echo "::error::PostGIS sidecar did not expose the initialized database." >&2
    exit 1
fi

# Keep the extensions identical to PostgresFixture, including the extensions
# needed by migrations that do not belong to PostGIS itself. Keep this list
# identical to PostgresFixture so the smoke exercises the same database shape
# as the integration harness.
timeout 10s docker exec "${DB_CONTAINER}" psql -v ON_ERROR_STOP=1 -p 5432 -U test -d honua_test \
    -c 'CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS postgis_raster; CREATE EXTENSION IF NOT EXISTS unaccent; CREATE EXTENSION IF NOT EXISTS pgcrypto;' \
    >/dev/null
extensions="$(timeout 10s docker exec "${DB_CONTAINER}" psql -p 5432 -U test -d honua_test -Atc \
    "SELECT count(*) FROM pg_extension WHERE extname IN ('postgis', 'postgis_raster');" \
    2>/dev/null | tr -d '[:space:]' || true)"
if [[ "${extensions}" != 2 ]]; then
    echo "::error::PostGIS sidecar is missing the expected PostGIS extensions after provisioning." >&2
    exit 1
fi

rm -f "${SERVER_LOG}"
echo "Starting ${SERVER_DLL} with migrations enabled."
(
    export ASPNETCORE_URLS="http://127.0.0.1:${SERVER_PORT}"
    export ASPNETCORE_ENVIRONMENT=Test
    export ConnectionStrings__DefaultConnection="Host=127.0.0.1;Port=${DB_PORT};Database=honua_test;Username=test;Password=test"
    export ConnectionStrings__honua="${ConnectionStrings__DefaultConnection}"
    export HONUA_DEV_AUTH=true
    export HONUA_DEV_AUTH_ALLOW_BYPASS=true
    export HONUA_ADMIN_PASSWORD='ClientCompatAdmin123!'
    export HONUA_REGISTER_TEST_INFRASTRUCTURE=true
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

echo "Waiting up to ${SERVER_READY_WAIT_SECONDS}s for /healthz/ready."
for _ in $(seq 1 "${SERVER_READY_WAIT_SECONDS}"); do
    if response="$(curl --silent --show-error --fail --max-time 2 "http://127.0.0.1:${SERVER_PORT}/healthz/ready" 2>/dev/null)" && [[ "${response}" == Ready ]]; then
        echo "Honua.Server booted, migrations completed, and readiness is Ready."
        exit 0
    fi

    server_state="$(ps -o stat= -p "${SERVER_PID}" 2>/dev/null | tr -d ' ' || true)"
    if [[ -z "${server_state}" || "${server_state}" == Z* ]]; then
        echo "::error::Honua.Server exited before becoming ready." >&2
        exit 1
    fi
    sleep 1
done

echo "::error::Honua.Server did not become ready within ${SERVER_READY_WAIT_SECONDS}s." >&2
exit 1
