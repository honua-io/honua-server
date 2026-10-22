#!/usr/bin/env bash
# One licensed capture: recording proxy + ArcPy driver against one upstream (#3558).
#   run_capture.sh <run-name> <upstream-origin> <proxy-port> <service-path> <variable> [proxy args...]
# Writes runs/<run-name>/{exchanges.jsonl,shapes.json,requests.json,pro-receipt.json}. Raw bytes go to
# $HONUA_3558_RAW/<run-name> (outside the repository) for the A/B comparison.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
bundle="$(dirname "$here")"
name="$1" upstream="$2" port="$3" service="$4" variable="$5"
shift 5
pro_python="${HONUA_3558_PRO_PYTHON:-$LOCALAPPDATA/Programs/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe}"
raw="${HONUA_3558_RAW:?set HONUA_3558_RAW to a directory outside the repository}/$name"
run="$bundle/runs/$name"
rm -rf "$run" "$raw"
mkdir -p "$run" "$raw"
"$pro_python" -I "$here/capture_proxy.py" --upstream "$upstream" --port "$port" \
  --log "$run/exchanges.jsonl" --raw-dir "$raw" "$@" &
proxy=$!
trap 'kill $proxy 2>/dev/null || true' EXIT
for _ in $(seq 30); do
  curl -s -o /dev/null "http://127.0.0.1:$port/" && break
  sleep 1
done
rm -f "$run/exchanges.jsonl"
"$pro_python" "$here/drive_pro.py" --service-url "http://127.0.0.1:$port$service" \
  --variable "$variable" --output "$run/pro-receipt.json" --scratch "$raw/scratch" 2>"$raw/driver.stderr"
kill $proxy 2>/dev/null || true
"$pro_python" -I "$here/compact.py" "$run"
