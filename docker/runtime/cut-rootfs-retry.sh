#!/bin/sh
set -eu
root=$1
shift
staging_dir=$(mktemp -d)
trap 'rm -rf "$staging_dir"' EXIT
for attempt in 1 2 3; do
    attempt_root="$staging_dir/$attempt"
    mkdir -p "$attempt_root/var/lib/dpkg"
    if chisel-wrapper --generate-dpkg-status "$attempt_root/var/lib/dpkg/status" -- \
        --release ubuntu-26.04 --ignore=unstable --root "$attempt_root" \
        base-files_base base-files_chisel base-files_release-info ca-certificates_data \
        libc6_libs libgcc-s1_libs libssl3t64_libs libstdc++6_libs "$@"; then
        # Only publish a complete cut; failed cuts can leave files and symlinks.
        test -s "$attempt_root/var/lib/dpkg/status"
        mkdir -p "$root"
        cp -a "$attempt_root/." "$root/"
        exit 0
    else
        status=$?
    fi
    echo "Chisel cut failed (attempt $attempt/3, exit $status)" >&2
    rm -rf "$attempt_root"
    if [ "$attempt" -lt 3 ]; then
        sleep "$((attempt * 5))"
    fi
done
exit "$status"
