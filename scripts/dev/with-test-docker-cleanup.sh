#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "Usage: $0 -- <command> [args...]" >&2
    exit 64
}

if [[ $# -eq 0 ]]; then
    usage
fi

if [[ "${1:-}" == "--" ]]; then
    shift
fi

if [[ $# -eq 0 ]]; then
    usage
fi

HONUA_TEST_RUN_ID="${HONUA_TEST_RUN_ID:-$(tr -d '\n' < /proc/sys/kernel/random/uuid)}"
export HONUA_TEST_RUN_ID
LOCK_FILE="${HONUA_TEST_DOCKER_LOCK_FILE:-/tmp/honua-testcontainers.lock}"
declare -a PREEXISTING_RYUK_IDS=()

docker_ready() {
    command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

acquire_lock() {
    mkdir -p "$(dirname "$LOCK_FILE")"
    exec 9>"$LOCK_FILE"
    flock 9
}

snapshot_existing_ryuk() {
    mapfile -t PREEXISTING_RYUK_IDS < <(docker ps -aq --filter "label=org.testcontainers.ryuk=true")
}

cleanup_stale_stopped_containers() {
    local ids=()
    if ! docker_ready; then
        return 0
    fi

    mapfile -t ids < <(
        docker ps -aq \
            --filter "label=honua.test.owner=honua-server" \
            --filter "status=created" \
            --filter "status=exited" \
            --filter "status=dead"
    )

    if [[ "${#ids[@]}" -eq 0 ]]; then
        return 0
    fi

    echo "Cleaning stale honua test containers..."
    docker rm -fv "${ids[@]}" >/dev/null 2>&1 || true
}

cleanup_current_run_containers() {
    local ids=()
    if ! docker_ready; then
        return 0
    fi

    mapfile -t ids < <(
        docker ps -aq \
            --filter "label=honua.test.owner=honua-server" \
            --filter "label=honua.test.run_id=${HONUA_TEST_RUN_ID}"
    )

    if [[ "${#ids[@]}" -eq 0 ]]; then
        return 0
    fi

    echo "Cleaning honua test containers for run ${HONUA_TEST_RUN_ID}..."
    docker rm -fv "${ids[@]}" >/dev/null 2>&1 || true
}

cleanup_new_ryuk_containers() {
    local ids=()
    local remove_ids=()
    local known

    if ! docker_ready; then
        return 0
    fi

    mapfile -t ids < <(docker ps -aq --filter "label=org.testcontainers.ryuk=true")
    if [[ "${#ids[@]}" -eq 0 ]]; then
        return 0
    fi

    for id in "${ids[@]}"; do
        known=0
        for existing in "${PREEXISTING_RYUK_IDS[@]}"; do
            if [[ "$id" == "$existing" ]]; then
                known=1
                break
            fi
        done
        if [[ "$known" -eq 0 ]]; then
            remove_ids+=("$id")
        fi
    done

    if [[ "${#remove_ids[@]}" -eq 0 ]]; then
        return 0
    fi

    echo "Cleaning Testcontainers Ryuk containers created by this run..."
    docker rm -fv "${remove_ids[@]}" >/dev/null 2>&1 || true
}

if docker_ready; then
    acquire_lock
    cleanup_stale_stopped_containers
    snapshot_existing_ryuk
fi

trap 'cleanup_current_run_containers; cleanup_new_ryuk_containers' EXIT INT TERM

"$@"
