#!/usr/bin/env bash
# Validate CI shard routing without starting runtime test runners.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "$1 is required for CI router validation." >&2
    exit 2
  fi
}

require_command jq
require_command python3

echo "Validating ci-shards.json structure..."
jq -e '
  type == "object"
  and (.infrastructure_paths | type == "array")
  and (.unmapped_source_run_all_prefixes | type == "array")
  and (.default_shards_when_no_match | type == "array" and length > 0)
  and (.shards | type == "array" and length > 0)
  and (
    [
      .shards[]
      | (
          (.name | type == "string" and length > 0)
          and (.shard_name | type == "string" and length > 0)
          and (.artifact_suffix | type == "string" and length > 0)
          and (.log_name | type == "string" and length > 0)
          and (.timeout_minutes | type == "number" and . > 0)
          and (.test_timeout_minutes | type == "number" and . > 0)
          and (.filter | type == "string" and length > 0)
          and (.paths | type == "array" and length > 0)
          and (.upload_operator_eval_report | type == "boolean")
          and (.upload_odata_evidence | type == "boolean")
        )
    ]
    | all
  )
  and (([.shards[].name] | length) == ([.shards[].name] | unique | length))
  and (([.shards[].artifact_suffix] | length) == ([.shards[].artifact_suffix] | unique | length))
  and (([.shards[].log_name] | length) == ([.shards[].log_name] | unique | length))
' .github/ci-shards.json >/dev/null

echo "Checking shell script syntax..."
bash -n scripts/ci/*.sh

echo "Checking Python helper syntax..."
python3 -m py_compile scripts/ci/*.py

echo "Checking workflow YAML syntax..."
python3 - <<'PY'
from pathlib import Path
try:
    import yaml
except ModuleNotFoundError:
    print("PyYAML is not installed; GitHub will still parse workflow YAML before running jobs.")
    raise SystemExit(0)

for path in sorted(Path(".github/workflows").glob("*.yml")):
    with path.open("r", encoding="utf-8") as handle:
        yaml.safe_load(handle)
PY

assert_descriptor() {
  local name="$1"
  local changed_files="$2"
  local expected_reason="$3"
  local expected_run_all="$4"
  local expected_shard="$5"
  local descriptor

  descriptor="$(printf '%s\n' "${changed_files}" | scripts/ci/honua-server-targeted-tests.sh --stdin)"
  echo "${name}: ${descriptor}"

  jq -e \
    --arg expected_reason "${expected_reason}" \
    --argjson expected_run_all "${expected_run_all}" \
    '.reason == $expected_reason and .run_all == $expected_run_all' \
    <<< "${descriptor}" >/dev/null

  if [[ -n "${expected_shard}" ]]; then
    jq -e --arg expected_shard "${expected_shard}" \
      '.shards | index($expected_shard)' \
      <<< "${descriptor}" >/dev/null
  fi
}

# Assert that a given changed-file set routes to a targeted subset that EXCLUDES
# an unrelated shard. This is the correctness guard for #1897: a single-protocol
# change must not silently drag in unrelated shards (e.g. a FeatureServer-only
# change must NOT select GeoServices ImageServer), and must not be run_all.
assert_excludes_shard() {
  local name="$1"
  local changed_files="$2"
  local excluded_shard="$3"
  local descriptor

  descriptor="$(printf '%s\n' "${changed_files}" | scripts/ci/honua-server-targeted-tests.sh --stdin)"
  echo "${name}: ${descriptor}"

  # Must be a targeted (non-run_all) descriptor.
  jq -e '.run_all == false and .reason == "targeted"' <<< "${descriptor}" >/dev/null

  # The excluded shard must NOT be present.
  if jq -e --arg s "${excluded_shard}" '.shards | index($s)' <<< "${descriptor}" >/dev/null; then
    echo "::error::${name}: expected shard '${excluded_shard}' to be EXCLUDED but it was selected" >&2
    exit 1
  fi
}

echo "Dry-running shard router cases..."
assert_descriptor \
  "ci-shards-only" \
  ".github/ci-shards.json" \
  "no_path_match" \
  "false" \
  "Core"

assert_descriptor \
  "ci-script-only" \
  "scripts/ci/honua-server-targeted-tests.sh" \
  "no_path_match" \
  "false" \
  "Core"

assert_descriptor \
  "feature-server-slice" \
  "src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerEndpoints.cs" \
  "targeted" \
  "false" \
  "FeatureServer Endpoints"

assert_descriptor \
  "shared-infrastructure" \
  "src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

assert_descriptor \
  "unmapped-source" \
  "src/Honua.Server/Features/NewFeature/NewEndpoint.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# A change to the shared Honua.Core validation pipeline must target the protocol
# shards that exercise it (query/edit/metadata validation) instead of escalating
# to run_all. ResourceValidator/CommonQueryValidator are consumed by the
# GeoServices, OGC API, OGC Classic, OData, STAC, Geometry, Operator Eval, Admin
# and MCP query/edit paths, so a validation-only diff is targeted, not run_all.
assert_descriptor \
  "core-validation-targeted" \
  "src/Honua.Core/Features/Validation/ResourceValidator.cs" \
  "targeted" \
  "false" \
  "OGC API Maps and Tiles"

assert_descriptor \
  "core-validation-targeted-features" \
  "src/Honua.Core/Features/Validation/CommonQueryValidator.cs" \
  "targeted" \
  "false" \
  "OGC API Features"

# ---------------------------------------------------------------------------
# #1897 guard cases: feature PRs must route to a targeted subset, not run_all,
# and must not pull in unrelated shards — while genuinely cross-cutting changes
# still escalate to run_all. These lock in the routing fix against regression.
# ---------------------------------------------------------------------------

# A project add/remove that only touches Honua.sln (no other signal) must NOT
# run all 40 shards. With Honua.sln removed from infrastructure_paths it falls
# to the smoke shard via default_shards_when_no_match.
assert_descriptor \
  "sln-only-not-run-all" \
  "Honua.sln" \
  "no_path_match" \
  "false" \
  "Core"

# A single GeoServices FeatureServer change targets the FeatureServer shards and
# must EXCLUDE the unrelated GeoServices ImageServer shard (correctness: do not
# over-run, but do not under-test the FeatureServer family).
assert_excludes_shard \
  "featureserver-excludes-imageserver" \
  "src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerQueryHandler.cs" \
  "GeoServices ImageServer"

# Conversely, a single GeoServices ImageServer change targets ImageServer and
# must EXCLUDE the FeatureServer Endpoints shard.
assert_excludes_shard \
  "imageserver-excludes-featureserver" \
  "src/Honua.Protocols.GeoServices/ImageServer/ImageServerEndpoints.cs" \
  "FeatureServer Endpoints"

# An OData-only feature change targets the OData shard family (targeted, not
# run_all) and excludes an unrelated protocol shard.
assert_excludes_shard \
  "odata-excludes-wfs" \
  "src/Honua.Protocols.OData/Features/ODataEndpoints.cs" \
  "WFS"

# A Scene-only change targets the Scene shard and excludes FeatureServer.
assert_descriptor \
  "scene-targeted" \
  "src/Honua.Protocols.Scene/SceneServerEndpoints.cs" \
  "targeted" \
  "false" \
  "Scene"
assert_excludes_shard \
  "scene-excludes-featureserver" \
  "src/Honua.Protocols.Scene/SceneServerEndpoints.cs" \
  "FeatureServer Endpoints"

# Cross-cutting safety preserved: the shared test harness (TestKit /
# PostgresFixture / SeedRunner) and the shared canonical query pipeline in
# Honua.Core/Queries still escalate to run_all.
assert_descriptor \
  "testkit-still-run-all" \
  "tests/dotnet/Honua.TestKit/PostgresFixture.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

assert_descriptor \
  "core-query-pipeline-still-run-all" \
  "src/Honua.Core/Queries/FeatureQuery.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

# A brand-new, not-yet-mapped top-level protocol area (e.g. a future
# src/Honua.Protocols.SensorThings/ landing before its shard is added) trips the
# unmapped-source safety net and runs run_all until an owning shard is mapped —
# never a silent skip.
assert_descriptor \
  "new-unmapped-protocol-run-all" \
  "src/Honua.Protocols.SensorThings/SensorThingsEndpoints.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# The shared host-rendering pipeline (Honua.Hosting) is cross-cutting: a change
# there must run_all rather than mis-route to a single render protocol.
assert_descriptor \
  "hosting-rendering-run-all" \
  "src/Honua.Hosting/Features/Rendering/RasterMapRenderingPipeline.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

echo "CI router validation passed."
