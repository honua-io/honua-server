#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MANIFEST="$ROOT_DIR/docs/developer/sdk-compatibility-versions.json"
MATRIX_BUILDER="$ROOT_DIR/scripts/ci/build-sdk-compatibility-matrix.sh"
RUNNER="$ROOT_DIR/scripts/ci/run-sdk-server-compatibility.sh"
REPORTER="$ROOT_DIR/scripts/ci/generate-sdk-compatibility-table.sh"
WORKFLOW="$ROOT_DIR/.github/workflows/sdk-server-compatibility.yml"
RELEASE_MANIFEST="$ROOT_DIR/docs/developer/sdk-release-certification.json"
RELEASE_BUILDER="$ROOT_DIR/scripts/ci/build-sdk-release-certification.py"
HEAD_SHA="1111111111111111111111111111111111111111"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

fail() {
    echo "sdk compatibility test failed: $*" >&2
    exit 1
}

full_matrix="$(bash "$MATRIX_BUILDER" "$MANIFEST" "$HEAD_SHA" "" false)"
jq -e '
  (.include | length) == 9
  and ([.include[] | select(.migration_automation_required == true)] | length) == 1
  and any(.include[];
    .server_label == "current"
    and .sdk_label == "sdk-previous-1"
    and .migration_automation_required == false)
  and any(.include[];
    .server_label == "trunk-previous-1"
    and .sdk_label == "sdk-current"
    and .migration_automation_required == false)
' <<<"$full_matrix" >/dev/null || fail "full matrix capability classification is not truthful"

override_sha="2222222222222222222222222222222222222222"
current_matrix="$(bash "$MATRIX_BUILDER" "$MANIFEST" "$HEAD_SHA" "$override_sha" true)"
jq -e --arg override "$override_sha" '
  (.include | length) == 1
  and .include[0].server_label == "current"
  and .include[0].sdk_label == "sdk-current"
  and .include[0].server_checkout_ref == $override
  and .include[0].migration_automation_required == true
' <<<"$current_matrix" >/dev/null || fail "current_only must retain one strict current x current cell"

printf '%s\n' "$current_matrix" > "$TMP_DIR/current-matrix.json"
mkdir -p "$TMP_DIR/current-results/cell"
jq -n '{
  server_label: "current",
  sdk_label: "sdk-current",
  passed: true,
  exit_code: 0
}' > "$TMP_DIR/current-results/cell/compat-result.json"
bash "$REPORTER" "$MANIFEST" "$TMP_DIR/current-results" "$TMP_DIR/current-report" "$TMP_DIR/current-matrix.json"
jq -e '
  .total_cells == 1
  and .supported_cells == 1
  and .passed == true
  and (.regressions | length) == 0
  and ([.cells[] | select(.status == "not-run")] | length) == 8
' "$TMP_DIR/current-report/sdk-compatibility-summary.json" >/dev/null \
  || fail "current_only report must evaluate only the selected cell"
grep -Fq '| `current` | PASS | NOT RUN | NOT RUN |' "$TMP_DIR/current-report/sdk-compatibility-matrix.md" \
  || fail "current_only table must distinguish intentionally unrun cells"

bash "$REPORTER" "$MANIFEST" "$TMP_DIR/empty-results" "$TMP_DIR/full-report"
jq -e '
  .total_cells == 9
  and .supported_cells == 9
  and .passed == false
  and (.regressions | length) == 9
  and ([.cells[] | select(.status == "not-run")] | length) == 0
' "$TMP_DIR/full-report/sdk-compatibility-summary.json" >/dev/null \
  || fail "full report must retain complete 3x3 enforcement"

historical_checkout="$TMP_DIR/historical-checkout"
preserved_assets="$TMP_DIR/compat-assets"
mkdir -p "$historical_checkout" "$preserved_assets/scripts/ci" "$preserved_assets/tests/python/shared"
cp "$RUNNER" "$TMP_DIR/run-sdk-server-compatibility.sh"
cp "$ROOT_DIR/scripts/ci/owned-server.py" "$preserved_assets/scripts/ci/owned-server.py"
cp "$ROOT_DIR/tests/python/shared/process_ports.py" "$preserved_assets/tests/python/shared/process_ports.py"

preserved_port="$(
    cd "$historical_checkout" && \
    CI=true python3 "$preserved_assets/scripts/ci/owned-server.py" port --variable HONUA_SDK_MIGRATION_SERVER_PORT --ci-default 5001
)"
[[ "$preserved_port" == "5001" ]] \
  || fail "preserved owned-server.py must resolve shared.process_ports outside the repository root"

historical_results="$TMP_DIR/historical-results"
(
    cd "$historical_checkout"
    SDK_COMPATIBILITY_CLASSIFY_ONLY=true \
    HONUA_SDK_MIGRATION_AUTOMATION_REQUIRED=false \
    HONUA_SDK_COMPAT_SERVER_LABEL=trunk-previous-1 \
    HONUA_SDK_COMPAT_SDK_LABEL=sdk-previous-1 \
    HONUA_SDK_OWNED_SERVER_SCRIPT="$preserved_assets/scripts/ci/owned-server.py" \
    SDK_COMPATIBILITY_RESULTS_DIR="$historical_results" \
    bash "$TMP_DIR/run-sdk-server-compatibility.sh"
)

jq -e '
  .migration_automation.required == false
  and .migration_automation.status == "not-applicable"
  and .migration_automation.passed == true
  and all(.migration_automation_by_sdk[][]; .status == "not-applicable" and .passed == true)
' "$historical_results/migration-automation.json" >/dev/null \
  || fail "historical runner evidence must record additive migration surfaces as not applicable"

if (
    cd "$historical_checkout"
    SDK_COMPATIBILITY_CLASSIFY_ONLY=true \
    HONUA_SDK_MIGRATION_AUTOMATION_REQUIRED=true \
    HONUA_SDK_OWNED_SERVER_SCRIPT="$preserved_assets/scripts/ci/owned-server.py" \
    SDK_COMPATIBILITY_RESULTS_DIR="$TMP_DIR/current-results" \
    bash "$TMP_DIR/run-sdk-server-compatibility.sh" >/dev/null 2>&1
); then
    fail "current capability cell must not pass through classify-only"
fi

grep -Fq 'compatibility-source/scripts/ci/owned-server.py' "$WORKFLOW" \
  || fail "workflow must preserve owned-server.py from the compatibility checkout"
grep -Fq 'compatibility-source/tests/python/shared/process_ports.py' "$WORKFLOW" \
  || fail "workflow must preserve process_ports.py alongside owned-server.py"
grep -Fq 'HONUA_SDK_OWNED_SERVER_SCRIPT: ${{ runner.temp }}/compat-assets/scripts/ci/owned-server.py' "$WORKFLOW" \
  || fail "workflow must pass the preserved owned-server.py script to historical server checkouts"

grep -Fq 'compatibility-source/tests/dotnet/Honua.Db.Postgres.Tests/Features/Import/Fixtures/GeoServer/CatalogApplySlice.json' "$WORKFLOW" \
  || fail "workflow must source the GeoServer fixture from the compatibility checkout"
grep -Fq 'HONUA_SDK_MIGRATION_GEOSERVER_FIXTURE: ${{ runner.temp }}/geoserver-catalog-apply-slice.json' "$WORKFLOW" \
  || fail "workflow must pass the preserved fixture to historical server checkouts"
grep -Fq 'Licensing__DevGrantEdition="Enterprise"' "$RUNNER" \
  || fail "the strict migration harness must grant the Enterprise entitlement it exercises"
grep -Fq '8: "Completed"' "$RUNNER" \
  || fail "the ArcGIS import harness must recognize the current completed status"
grep -Fq '9: "NeedsReview"' "$RUNNER" \
  || fail "the ArcGIS import harness must treat operator review as a terminal non-success status"
grep -Fq '11: "Cancelled"' "$RUNNER" \
  || fail "the ArcGIS import harness must recognize the current cancelled status"
grep -Fq 'rest/services/Wildfire/FeatureServer}' "$RUNNER" \
  || fail "the ArcGIS import source must be a relationship-free layer that single-layer import completes at full fidelity"
if grep -Fq 'rest/services/ServiceRequest/FeatureServer' "$RUNNER"; then
    fail "ServiceRequest layer 0 declares a composite relationship; single-layer import correctly ends in NeedsReview"
fi
grep -Fq "global-json-file: sdk/honua-sdk-dotnet/global.json" "$WORKFLOW" \
  || fail "workflow must install the .NET SDK pinned by the honua-sdk-dotnet checkout"
grep -Fq "run: python3 scripts/ci/prepare-dotnet-toolchain.py" "$WORKFLOW" \
  || fail "workflow must provision the honua-sdk-dotnet workload set before the .NET leg"
grep -Fq 'cp "$dotnet_global_json" "$smoke_dir/global.json"' "$RUNNER" \
  || fail "the .NET smoke app must resolve the same SDK and workload set as the SDK checkout"
sed -n '/^write_migration_automation_summary()/,/^write_migration_automation_not_applicable_summary()/p' "$RUNNER" \
  | grep -Fq 'required: true' \
  || fail "strict current capability evidence must record migration automation as required"

release_results="$TMP_DIR/release-results"
mkdir -p "$release_results"
jq -n '{js:{installed:true},python:{installed:true},dotnet:{installed:false,trace:"NU1101: Honua.Sdk 1.6.1 not found on nuget.org"}}' > "$release_results/install-results.json"
python3 "$RELEASE_BUILDER" --manifest "$RELEASE_MANIFEST" --results-dir "$release_results" \
  --output "$release_results/fragment.json" --producer-source-sha "$HEAD_SHA"
jq -e '
  .schema == "honua.protocol-certification-fragment/v1"
  and .operation_scope == {complete:true,expected:99,observed:99}
  and (.observations | length) == 99
  and ([.observations[] | select(.client_id == "dotnet" and .result == "fail" and (.gap | contains("Honua.Sdk 1.6.1")))] | length) == 33
  and all(.observations[]; .image_digest == "sha256:373aa1fdf1bd4153df9cb21e25e43dfc463c0e194fcac13b40a39c4bb390eb72")
  and all(.observations[]; .producer_source_sha == "1111111111111111111111111111111111111111")
  and ([.observations[] | select(.capability_key == "serve.ogc-api-features") | .surface] | unique) == ["ogc-api-features"]
  and ([.observations[] | select(.capability_key == "serve.geoservices-featureserver") | .surface] | unique) == ["feature-server"]
' "$release_results/fragment.json" >/dev/null || fail "release fragment must materialize all 99 cells and retain the unavailable .NET coordinate"

grep -Fq 'dotnet new console --framework net11.0' "$RUNNER" \
  || fail "the smoke consumer must support the current net11-only SDK projects"

echo "SDK compatibility matrix and runner tests passed."
