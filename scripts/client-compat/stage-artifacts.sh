#!/usr/bin/env bash
set -euo pipefail

artifact_root="${1:-artifacts}"
output_root="${2:-docker/client-compat/output}"

mkdir -p "$output_root"
shopt -s nullglob

artifact_dirs=("$artifact_root"/*/)
if (( ${#artifact_dirs[@]} == 0 )); then
    echo "::error::No lane artifacts downloaded; the artifact filter or upstream upload contract is broken." >&2
    exit 1
fi

empty_lanes=()
total_cert_count=0
for dir in "${artifact_dirs[@]}"; do
    name="$(basename "$dir")"
    lane="${name##*evidence-client-compat-}"
    if [[ -z "$lane" || "$lane" == "$name" ]]; then
        echo "::error::Unexpected lane artifact name '$name'; expected '*evidence-client-compat-<lane>'." >&2
        exit 1
    fi

    lane_output="$output_root/$lane"
    mkdir -p "$lane_output"
    cp -R "$dir". "$lane_output/"

    cert_count=$(find "$lane_output" -type f -name '*.cert.json' | wc -l)
    total_cert_count=$((total_cert_count + cert_count))
    echo "::group::lane=$lane staged ($cert_count cert envelope(s))"
    find "$lane_output" -maxdepth 1 -type f -printf '%f\n' | sort
    echo "::endgroup::"

    if (( cert_count == 0 )); then
        empty_lanes+=("$lane")
    fi
done

if (( total_cert_count == 0 )); then
    echo "::error::Downloaded ${#artifact_dirs[@]} successful upload(s), but staged zero .cert.json envelopes. This is an upstream lane or artifact-contract failure, not 386 client-observation gaps." >&2
    exit 1
fi

if (( ${#empty_lanes[@]} > 0 )); then
    echo "::error::${#empty_lanes[@]} lane artifact(s) contained no .cert.json envelopes: ${empty_lanes[*]}." >&2
    exit 1
fi

find "$output_root" -type f -name '*.cert.json' -printf '%p\n' | sort
