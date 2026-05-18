#!/usr/bin/env bash
# Re-runs the docker/client-compat matrix and overwrites tests/baselines/client-compat
# with the resulting envelopes. Intended for scheduled baseline-refresh PRs;
# do NOT run in regular CI.
#
# Usage:
#   ./scripts/client-compat/refresh-baselines.sh           # all lanes
#   ./scripts/client-compat/refresh-baselines.sh cesium    # one lane
#   ./scripts/client-compat/refresh-baselines.sh gdal cesium  # subset
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${ROOT_DIR}/docker/client-compat/compose.yml"
OUTPUT_DIR="${ROOT_DIR}/docker/client-compat/output"
BASELINE_DIR="${ROOT_DIR}/tests/baselines/client-compat"

# Mirror the lane matrix in .github/workflows/client-interop-nightly.yml.
DEFAULT_LANES=(gdal pyqgis openlayers cesium arcgis-stub)
ALLOWED_LANES=("${DEFAULT_LANES[@]}")

if (( $# == 0 )); then
    LANES=("${DEFAULT_LANES[@]}")
else
    LANES=("$@")
fi

# Reject typos so we don't silently no-op a lane refresh request.
for lane in "${LANES[@]}"; do
    found=0
    for allowed in "${ALLOWED_LANES[@]}"; do
        if [[ "$lane" == "$allowed" ]]; then
            found=1
            break
        fi
    done
    if (( found == 0 )); then
        echo "::error::Unknown lane '$lane'. Allowed: ${ALLOWED_LANES[*]}" >&2
        exit 2
    fi
done

cd "$ROOT_DIR"

echo "Refreshing client-compat baselines via docker compose..."
echo "  Lanes: ${LANES[*]}"
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Run lanes one at a time. Avoid `up --abort-on-container-exit` here: the
# one-shot seed dependency exits before honua and the lane start, and Compose
# treats that as a reason to stop the stack. `run` starts dependencies without
# using seed completion as an abort signal, so evidence written to /output is
# preserved for every lane.
for lane in "${LANES[@]}"; do
    echo
    echo "===> Refreshing lane: $lane"
    docker compose -f "$COMPOSE_FILE" \
        --profile "$lane" \
        build seed honua "$lane"
    if docker compose -f "$COMPOSE_FILE" \
            --profile "$lane" \
            run --no-build --rm \
            "$lane"; then
        echo "lane $lane: ok"
    else
        echo "::warning::lane $lane exited non-zero; any evidence it managed to write will still be copied to baselines."
    fi
    docker compose -f "$COMPOSE_FILE" down --remove-orphans
done

mkdir -p "$BASELINE_DIR"

shopt -s nullglob
for envelope in "$OUTPUT_DIR"/**/*.cert.json; do
    lane_dir="$(basename "$(dirname "$envelope")")"
    target_dir="$BASELINE_DIR/$lane_dir"
    mkdir -p "$target_dir"
    # Drop run_id-prefixed filenames so the baseline is stable.
    name="$(basename "$envelope")"
    stripped="${name#*-}"
    cp "$envelope" "$target_dir/$stripped"
done

echo
echo "Baseline directory now contains:"
find "$BASELINE_DIR" -name '*.cert.json' -printf '  %p\n'
echo
echo "Review the diff and open a baseline-bump PR if accepted."
