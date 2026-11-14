#!/usr/bin/env bash

honua_wait_scale_test() {
    local container
    container="$(docker compose -f "$1" ps -q nginx)" || return
    if [[ -z "$container" || "$container" == *$'\n'* ]]; then
        echo "Expected exactly one scale-test nginx container" >&2
        return 1
    fi
    python3 "${BASH_SOURCE[0]%/*}/../ci/owned-server.py" wait \
        --container "$container" --container-port 80 \
        --url "${2%/}/healthz/live" \
        --timeout "${HONUA_SCALE_TEST_READY_SECONDS:-300}"
}
