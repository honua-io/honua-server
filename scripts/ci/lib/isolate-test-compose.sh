#!/usr/bin/env bash

honua_isolate_test_compose() {
    local allocation name value
    allocation="$(python3 "${BASH_SOURCE[0]%/*}/../owned-server.py" compose-env --file "$1")" || return
    while IFS='=' read -r name value; do
        export "$name=$value"
    done <<< "$allocation"
}
