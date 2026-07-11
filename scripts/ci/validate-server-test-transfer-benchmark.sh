#!/usr/bin/env bash
# Fast structural and synthetic-decision proof for the opt-in hosted benchmark.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CONFIG="${REPO_ROOT}/.github/server-test-transfer-benchmark.json"
REGISTRY="${REPO_ROOT}/.github/server-test-artifact-projects.json"
WORKFLOW="${REPO_ROOT}/.github/workflows/server-test-transfer-benchmark.yml"

jq -e '
  .contract_version == 1 and
  .decision_thresholds.require_initial_time_to_first_test_improvement == true and
  .decision_thresholds.require_runner_minutes_improvement == true and
  ([.profiles[].name] | sort) == ["five-mixed-project", "two-mixed-project", "two-same-project"] and
  ([.profiles[] | .shards | length] | sort) == [2, 2, 5] and
  (.shards | length == 5) and
  ([.shards[].name] | length) == ([.shards[].name] | unique | length) and
  (all(.shards[]; (.project | endswith(".csproj")) and (.artifact_suffix | test("^[a-z0-9-]+$")))) and
  (([.profiles[].shards[]] - [.shards[].name]) | length == 0)
' "${CONFIG}" >/dev/null

while IFS=$'\t' read -r project suffix; do
  jq -e --arg project "${project}" --arg suffix "${suffix}" \
    '.projects[] | select(.csproj == $project and .artifact_suffix == $suffix)' "${REGISTRY}" >/dev/null
done < <(jq -r '.shards[] | [.project, .artifact_suffix] | @tsv' "${CONFIG}")

grep -q 'branches:' "${WORKFLOW}"
grep -q 'ci/2722-hosted-transfer-benchmark' "${WORKFLOW}"
grep -q 'fail-on-cache-miss: true' "${WORKFLOW}"
grep -q 'packages: read' "${WORKFLOW}"
grep -q 'dotnet nuget update source github-honua' "${WORKFLOW}"
grep -q 'github.run_attempt == 1' "${WORKFLOW}"
grep -q 'Artifact benchmark shard /' "${WORKFLOW}"
grep -q 'Cache benchmark shard /' "${WORKFLOW}"
if grep -qE 'pull_request:|schedule:' "${WORKFLOW}"; then
  echo "::error::Benchmark workflow must remain opt-in and outside production PR/nightly orchestration." >&2
  exit 1
fi

bash -n "${SCRIPT_DIR}/benchmark-server-test-transfer.sh"
python3 -m py_compile "${SCRIPT_DIR}/summarize-server-test-transfer-benchmark.py"

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-transfer-summary.XXXXXX")"
cleanup() { rm -rf "${fixture}"; }
trap cleanup EXIT
mkdir -p "${fixture}/metrics" "${fixture}/out"

CONFIG_PATH="${CONFIG}" METRICS_PATH="${fixture}/metrics" python3 - <<'PY'
import json
import os
from pathlib import Path

config = json.loads(Path(os.environ["CONFIG_PATH"]).read_text())
root = Path(os.environ["METRICS_PATH"])
unique = {item["artifact_suffix"]: item for item in config["shards"]}

def write(name, mode, identity, total, **extra):
    value = {
        "contract": "honua.server-test-transfer-benchmark.v1",
        "mode": mode,
        "identity": identity,
        "run_attempt": 1,
        "total_ms": total,
        "restore_ms": 100,
        "build_ms": 700,
        "discovery_ms": 100,
        "integrity_unpack_ms": 100,
        "test_ms": 100,
        "job_elapsed_ms": total,
    }
    value.update(extra)
    (root / name).write_text(json.dumps(value))

for suffix in unique:
    write(f"producer-{suffix}.json", "producer", suffix, 800,
          artifact_total_ms=900, cache_total_ms=900,
          artifact_job_elapsed_ms=900, cache_job_elapsed_ms=900)
for shard in config["shards"]:
    identity = shard["name"]
    write(f"baseline-{identity}.json", "baseline", identity, 1000)
    write(f"artifact-{identity}.json", "consumer-artifact", identity, 300,
          transfer_ms=100, total_with_transfer_ms=400)
    write(f"cache-{identity}.json", "consumer-cache", identity, 300,
          transfer_ms=100, total_with_transfer_ms=400)
PY

"${SCRIPT_DIR}/summarize-server-test-transfer-benchmark.py" \
  --metrics "${fixture}/metrics" --config "${CONFIG}" \
  --output "${fixture}/out/summary.json" --markdown "${fixture}/out/summary.md" >/dev/null
jq -e '.decision == "no-shared-producer" and all(.profiles[]; .complete == true)' \
  "${fixture}/out/summary.json" >/dev/null

echo "Server-test transfer benchmark validation passed."
