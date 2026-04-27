#!/usr/bin/env bash
# Re-runs the docker/client-compat matrix and overwrites tests/baselines/client-compat
# with the resulting envelopes. Intended for scheduled baseline-refresh PRs;
# do NOT run in regular CI.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${ROOT_DIR}/docker/client-compat/compose.yml"
OUTPUT_DIR="${ROOT_DIR}/docker/client-compat/output"
BASELINE_DIR="${ROOT_DIR}/tests/baselines/client-compat"

cd "$ROOT_DIR"

echo "Refreshing client-compat baselines via docker compose..."
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

docker compose -f "$COMPOSE_FILE" --profile matrix up \
    --abort-on-container-exit \
    --build || true
docker compose -f "$COMPOSE_FILE" down --remove-orphans

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

echo "Baseline directory now contains:"
find "$BASELINE_DIR" -name '*.cert.json' -printf '  %p\n'
echo
echo "Review the diff and open a baseline-bump PR if accepted."
