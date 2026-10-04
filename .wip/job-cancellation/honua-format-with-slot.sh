#!/bin/bash
set -euo pipefail
format_slots="${HONUA_BUILD_SLOTS:-2}"
format_slot_dir="${HONUA_BUILD_SLOT_DIR:-/home/mike/honua-io/honua-flow-work/dispatch/state/build-slots}"
mkdir -p "$format_slot_dir"
format_slot_acquired=0
for format_slot_index in $(seq 1 "$format_slots"); do
    exec {format_slot_fd}>"$format_slot_dir/slot-$format_slot_index"
    if flock -n "$format_slot_fd"; then
        format_slot_acquired=1
        break
    fi
    exec {format_slot_fd}>&-
done
if [[ "$format_slot_acquired" == 0 ]]; then
    exec {format_slot_fd}>"$format_slot_dir/slot-1"
    flock -w 2400 "$format_slot_fd"
fi
exec timeout 20m dotnet format "$@"
