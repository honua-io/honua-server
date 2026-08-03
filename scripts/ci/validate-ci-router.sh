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

# Resolve a Python 3 that actually runs (see scripts/ci/lib/python-resolve.sh).
# `require_command python3` is not enough on Windows, where the Microsoft Store
# python3 alias satisfies `command -v` but exits without running (#2886). When
# no interpreter runs, skip the Python-driven checks gracefully — the jq/shell
# validations above and below still run, and CI (where python3 is real) is
# unaffected.
# shellcheck source=scripts/ci/lib/python-resolve.sh
. "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib/python-resolve.sh"
PYTHON_BIN="$(honua_resolve_python || true)"

# CR-safe jq: strip the CRLF the Windows jq binary emits in text mode (no-op on
# Linux). Sourced AFTER require_command above so that guard probes the real
# binary, not this wrapper function.
source "${SCRIPT_DIR}/lib/jq-cr-safe.sh"

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
  and (
    # targeted_override_prefixes (optional) must be well-formed: each entry has a
    # non-empty prefix, a reason, and a non-empty shard list whose names ALL
    # reference real shards (a typo would silently route to no shard).
    (.targeted_override_prefixes // []) | (
      type == "array"
      and ([
        .[]
        | (.prefix | type == "string" and length > 0)
          and (.reason | type == "string" and length > 0)
          and (.shards | type == "array" and length > 0)
      ] | all)
    )
  )
' .github/ci-shards.json >/dev/null

# #2721: the artifact registry must cover the exact unique shard-project set, and
# its package/restore contract must fail closed on RID leakage, tampering and limits.
scripts/ci/validate-server-test-binary-artifacts.sh
scripts/ci/validate-server-test-transfer-benchmark.sh
scripts/ci/validate-server-test-shard-cache.sh

# #3054: the OUTER GitHub job cap must clear the INNER dotnet-test cap by enough
# room for the non-test part of the job (checkout, setup-dotnet, restore or
# binary materialization, artifact uploads). If it does not, the runner cancels
# the job before run-server-test-shard.sh can classify the timeout and upload
# its log/timing/TRX artifacts, and the failure surfaces as an unattributable
# cancellation instead of an actionable capacity signal.
echo "Validating shard timeout budgets..."
budget_violations="$(jq -r '
  (.shard_budget_policy.min_job_overhead_minutes // 10) as $gap
  | .shards[]
  | select((.timeout_minutes - .test_timeout_minutes) < $gap)
  | "  \(.name): timeout_minutes=\(.timeout_minutes) test_timeout_minutes=\(.test_timeout_minutes) (gap \(.timeout_minutes - .test_timeout_minutes) < \($gap))"
' .github/ci-shards.json)"
if [[ -n "${budget_violations}" ]]; then
  echo "::error::shard timeout_minutes must exceed test_timeout_minutes by at least shard_budget_policy.min_job_overhead_minutes" >&2
  printf '%s\n' "${budget_violations}" >&2
  exit 1
fi

echo "Validating shard headroom instrumentation..."
scripts/ci/fixtures/validate-shard-headroom.sh

echo "Validating targeted_override_prefixes reference real shards..."
jq -e '
  ([.shards[].name]) as $names
  | (.targeted_override_prefixes // [])
  | all(.shards[] | . as $s | $names | index($s) != null)
' .github/ci-shards.json >/dev/null \
  || { echo "::error::a targeted_override_prefixes entry references an unknown shard name" >&2; exit 1; }

echo "Checking shell script syntax..."
scripts/ci/validate-shell-syntax.sh

echo "Checking local pre-PR change routing..."
scripts/ci/fixtures/validate-pre-pr-routing.sh

if [[ -n "${PYTHON_BIN}" ]]; then
  echo "Checking Python helper syntax..."
  "${PYTHON_BIN}" -m py_compile scripts/ci/*.py

  echo "Checking Docker integration gate timeout budget..."
  "${PYTHON_BIN}" scripts/ci/validate-docker-gate-timeouts.py

  echo "Checking workflow YAML syntax..."
  "${PYTHON_BIN}" - <<'PY'
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
else
  echo "⚠️  Skipping Python helper/timeout/YAML checks (no working Python 3: tried python3/python/py)"
fi

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

# Assert a targeted descriptor selects exactly the expected shard set. Use this
# for measured cumulative diffs where accidental widening would recreate most
# of the cost that path routing is intended to avoid.
assert_exact_shards() {
  local name="$1"
  local changed_files="$2"
  local expected_shards_json="$3"
  local descriptor

  descriptor="$(printf '%s\n' "${changed_files}" | scripts/ci/honua-server-targeted-tests.sh --stdin)"
  echo "${name}: ${descriptor}"

  jq -e --argjson expected "${expected_shards_json}" '
    .run_all == false
    and .reason == "targeted"
    and ((.shards | sort) == ($expected | sort))
  ' <<< "${descriptor}" >/dev/null
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

# Publishing persistence is a bounded MCP/operator surface. Its canonical
# Core/Postgres models and stores are exercised by the MCP promotion-resource
# tests plus the Core shard's Postgres server-composition proof, so changes in
# that slice target both owners instead of tripping the unmapped-source net.
assert_descriptor \
  "publishing-core-targeted" \
  "src/Honua.Core/Features/Publishing/Domain/PublishingJsonContext.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "publishing-core-includes-mcp" \
  "src/Honua.Core/Features/Publishing/Domain/PublishingJsonContext.cs" \
  "targeted" \
  "false" \
  "MCP"
assert_descriptor \
  "publishing-postgres-targeted" \
  "src/Honua.Postgres/Features/Publishing/PostgresPublishedServiceStore.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "publishing-postgres-includes-mcp" \
  "src/Honua.Postgres/Features/Publishing/PostgresDeploymentStore.cs" \
  "targeted" \
  "false" \
  "MCP"
assert_excludes_shard \
  "publishing-excludes-imageserver" \
  "src/Honua.Postgres/Features/Publishing/PostgresDeploymentStore.cs" \
  "GeoServices ImageServer"

# The promotion migration is selected by the Core shard's migration/startup
# coverage. Keep this exact-file mapping narrow: unrelated future migrations
# remain unmapped and therefore fail safe until their owners are declared.
assert_descriptor \
  "promotion-migration-targeted" \
  "src/Honua.Server/Migrations/082_CreatePromotionStores.sql" \
  "targeted" \
  "false" \
  "Core"

# Client-compat Seed classes and the focused promotion Startup composition test
# are selected by the Core catch-all filter. Claim their paths explicitly so a
# test-only edit does not instantiate every server-test shard.
assert_descriptor \
  "client-compat-seed-targeted" \
  "tests/dotnet/Honua.Server.Tests/Seed/ClientCompatSeedSequenceTests.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "promotion-startup-targeted" \
  "tests/dotnet/Honua.Server.Tests/Startup/PostgresPromotionSurfaceRegistrationTests.cs" \
  "targeted" \
  "false" \
  "Core"

# Do not weaken the global Postgres DI fail-safe. Arbitrary changes to the
# shared registrar can affect every Postgres-backed fixture and must run all.
assert_descriptor \
  "postgres-service-registration-still-run-all" \
  "src/Honua.Postgres/ServiceCollectionExtensions.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

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

# A Geocoding (GeocodeServer) change targets the dedicated Geocoding shard and
# excludes unrelated shards. Before this shard existed, geocoding source lived
# under the unmapped-source net AND its tests matched no shard filter, so any
# geocoding-only PR escalated to run_all while never running geocoding tests in
# a targeted run. This locks in that a geocoding change is now targeted.
assert_descriptor \
  "geocoding-targeted" \
  "src/Honua.Geocoding/Features/Geocoding/Domain/GeocodeMagicKey.cs" \
  "targeted" \
  "false" \
  "Geocoding"
assert_excludes_shard \
  "geocoding-excludes-imageserver" \
  "src/Honua.Server/Features/Geocoding/GeocodingHandler.cs" \
  "GeoServices ImageServer"

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
# src/Honua.Protocols.Wcps/ landing before its shard is added) trips the
# unmapped-source safety net and runs run_all until an owning shard is mapped —
# never a silent skip.
assert_descriptor \
  "new-unmapped-protocol-run-all" \
  "src/Honua.Protocols.Wcps/WcpsEndpoints.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# SensorThings (STA) now HAS a shard (#1899): a change confined to it must route
# to the dedicated SensorThings shard (targeted), not run_all and not a silent
# skip. Before #1899 its tests matched no filter and never ran.
assert_descriptor \
  "sensorthings-targeted" \
  "src/Honua.Protocols.SensorThings/SensorThingsEndpoints.cs" \
  "targeted" \
  "false" \
  "SensorThings"

# The shared host-rendering pipeline (Honua.Hosting) is cross-cutting: a change
# there must run_all rather than mis-route to a single render protocol.
assert_descriptor \
  "hosting-rendering-run-all" \
  "src/Honua.Hosting/Features/Rendering/RasterMapRenderingPipeline.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# #2693: bounded operator paths observed in merge-train child 29142991789
# already had known test owners, but missing path ownership caused the entire
# cumulative descriptor to fall through to unmapped_source_change.
assert_descriptor \
  "hosting-tiles-targets-geometry" \
  "src/Honua.Hosting/Features/Tiles/TilePackageWriter.cs" \
  "targeted" \
  "false" \
  "Geometry Tiles and Terrain"
assert_descriptor \
  "hosting-tiles-includes-imageserver" \
  "src/Honua.Hosting/Features/Tiles/TilePackageWriter.cs" \
  "targeted" \
  "false" \
  "GeoServices ImageServer"
assert_descriptor \
  "routing-feature-targets-naserver" \
  "src/Honua.Routing/Features/Routing/Providers/PgRoutingProvider.cs" \
  "targeted" \
  "false" \
  "GeoServices GPServer and NAServer"
assert_descriptor \
  "routing-feature-includes-server-tests-owner" \
  "src/Honua.Routing/Features/Routing/Providers/PgRoutingProvider.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "imageserver-registry-targets-imageserver" \
  "src/Honua.Server/EndpointRegistry.ImageServer.cs" \
  "targeted" \
  "false" \
  "GeoServices ImageServer"
assert_descriptor \
  "imageserver-registry-includes-governance" \
  "src/Honua.Server/EndpointRegistry.ImageServer.cs" \
  "targeted" \
  "false" \
  "STAC and API Governance"
assert_descriptor \
  "naserver-pgrouting-test-targeted" \
  "tests/dotnet/Honua.Server.Tests/Routing/NAServerPgRoutingEndToEndTests.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "routing-solver-test-targeted" \
  "tests/dotnet/Honua.Server.Tests/Routing/LocationAllocationSolverTests.cs" \
  "targeted" \
  "false" \
  "Core"
assert_descriptor \
  "compact-tile-writer-test-targeted" \
  "tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/Tiles/CompactTilePackageWriterTests.cs" \
  "targeted" \
  "false" \
  "GeoServices ImageServer"
assert_exact_shards \
  "measured-tile-routing-imageserver-batch" \
  "$(printf '%s\n%s\n%s\n%s' \
      'src/Honua.Hosting/Features/Tiles/CompactTilePackageWriter.cs' \
      'src/Honua.Routing/Features/Routing/Domain/RoutingModels.cs' \
      'src/Honua.Server/EndpointRegistry.ImageServer.cs' \
      'tests/dotnet/Honua.Server.Tests/Routing/NAServerPgRoutingEndToEndTests.cs')" \
  '["Core","Geometry Tiles and Terrain","GeoServices ImageServer","GeoServices GPServer and NAServer","STAC and API Governance"]'

# Follow-up proof from draft PR #2700: pure routing tests share the Core owner
# with the real pgRouting fixture and must not widen the canonical NAServer diff.
assert_exact_shards \
  "location-allocation-routing-batch" \
  "$(printf '%s\n%s\n%s' \
      'src/Honua.Routing/Features/Routing/Providers/LocationAllocationSolver.cs' \
      'src/Honua.Protocols.GeoServices/NAServer/NAServerParameterTranslation.cs' \
      'tests/dotnet/Honua.Server.Tests/Routing/LocationAllocationSolverTests.cs')" \
  '["Core","GeoServices GPServer and NAServer"]'

# #2712: migration 083 and the exact pgRouting TestKit fixture are bounded
# companions to the canonical Routing/NAServer slice. Keep other migrations
# and shared TestKit files on their existing fail-safe paths.
assert_exact_shards \
  "routing-profile-migration-owner" \
  "src/Honua.Server/Migrations/083_AddNetworkDatasetTravelProfiles.sql" \
  '["Core"]'
assert_exact_shards \
  "routing-topology-lifecycle-migration-owner" \
  "src/Honua.Server/Migrations/084_CreateNetworkTopologyGenerations.sql" \
  '["Core"]'
assert_exact_shards \
  "network-dataset-admin-exact-owner" \
  "tests/dotnet/Honua.Server.Tests/Features/Admin/NetworkDatasetAdminEndpointsTests.cs" \
  '["Server Features Admin Network and Jobs"]'
assert_descriptor \
  "network-dataset-admin-source-owner" \
  "src/Honua.Server/Features/Admin/NetworkDatasetAdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Network and Jobs"
assert_exact_shards \
  "pgrouting-fixture-owner" \
  "tests/dotnet/Honua.TestKit/PgRoutingFixture.cs" \
  '["Core"]'
assert_exact_shards \
  "routing-profile-cumulative-batch" \
  "$(printf '%s\n%s\n%s\n%s\n%s' \
      'src/Honua.Routing/Features/Routing/Providers/NetworkDatasetRegistry.cs' \
      'src/Honua.Protocols.GeoServices/NAServer/NAServerEndpoints.cs' \
      'src/Honua.Server/Migrations/083_AddNetworkDatasetTravelProfiles.sql' \
      'tests/dotnet/Honua.TestKit/PgRoutingFixture.cs' \
      'tests/dotnet/Honua.Server.Tests/Routing/NetworkDatasetValidationTests.cs')" \
  '["Core","GeoServices GPServer and NAServer"]'
assert_exact_shards \
  "routing-topology-lifecycle-cumulative-batch" \
  "$(printf '%s\n%s\n%s\n%s' \
      'src/Honua.Routing/Features/Routing/Providers/PostgresNetworkDatasetStore.cs' \
      'src/Honua.Server/Migrations/084_CreateNetworkTopologyGenerations.sql' \
      'tests/dotnet/Honua.Server.Tests/Routing/NetworkTopologyLifecycleTests.cs' \
      'tests/dotnet/Honua.Server.Tests/Features/Admin/NetworkDatasetAdminEndpointsTests.cs')" \
  '["Core","GeoServices GPServer and NAServer","Server Features Admin Network and Jobs"]'
assert_descriptor \
  "unknown-migration-still-run-all" \
  "src/Honua.Server/Migrations/999_FutureUnmappedMigration.sql" \
  "unmapped_source_change" \
  "true" \
  "Core"
assert_descriptor \
  "unrelated-testkit-still-run-all" \
  "tests/dotnet/Honua.TestKit/FutureSharedFixture.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

# #2709: the four shards carved from the former Server Features Misc shard
# retained a shared source-path list after their filters diverged. Zarr tests
# remain executable only in the Misc catch-all, so route the Zarr registrar to
# that real owner instead of paying for three filters that cannot discover it.
assert_exact_shards \
  "zarr-server-source-exact-owner" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  '["Server Features Misc"]'
assert_excludes_shard \
  "zarr-server-source-excludes-data-sharing" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  "Server Features Data and Sharing"
assert_excludes_shard \
  "zarr-server-source-excludes-collaboration-content" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  "Server Features Collaboration Mobile and Identity"
assert_excludes_shard \
  "zarr-server-source-excludes-studio-feature-store" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  "Server Features Studio and Feature Store"
assert_excludes_shard \
  "zarr-server-source-excludes-analytics-export-reporting" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  "Server Features Analytics Export and Reporting"
assert_excludes_shard \
  "zarr-server-source-excludes-spec-printing-staticmap" \
  "src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs" \
  "Server Features Spec Printing and Static Maps"

# Representative cumulative diff from #2702: shared raster changes retain the
# ImageServer/coverage/WCS owners, while the server Zarr registrar adds only its
# executable Misc owner.
assert_exact_shards \
  "zarr-point-slice-cumulative-batch" \
  "$(printf '%s\n%s\n%s\n%s\n%s' \
      'src/Honua.Core/Features/Raster/Abstractions/IZarrPointSliceReader.cs' \
      'src/Honua.Protocols.GeoServices/ImageServer/Handlers/ImageServerIdentifyHandler.cs' \
      'src/Honua.Protocols.GeoServices/ImageServer/ImageServerEndpoints.cs' \
      'src/Honua.Server/Features/Protocols/Zarr/ZarrServiceCollectionExtensions.cs' \
      'tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/ImageServer/ImageServerZarrTestFixture.cs')" \
  '["GeoServices ImageServer","OGC API Tiles Coverages and Processes","Server Features Misc","WFS"]'

# Narrowing Zarr must not remove the carved shards' actual feature-area paths.
assert_descriptor \
  "data-enrichment-source-retains-owner" \
  "src/Honua.Server/Features/DataEnrichment/DataEnrichmentServiceCollectionExtensions.cs" \
  "targeted" \
  "false" \
  "Server Features Data and Sharing"
assert_descriptor \
  "collaboration-source-retains-owner" \
  "src/Honua.Server/Features/Collaboration/FeatureLocks/FeatureLockServices.cs" \
  "targeted" \
  "false" \
  "Server Features Collaboration Mobile and Identity"
assert_excludes_shard \
  "collaboration-source-excludes-studio-child" \
  "src/Honua.Server/Features/Collaboration/FeatureLocks/FeatureLockServices.cs" \
  "Server Features Studio and Feature Store"
assert_excludes_shard \
  "collaboration-source-excludes-analytics-child" \
  "src/Honua.Server/Features/Collaboration/FeatureLocks/FeatureLockServices.cs" \
  "Server Features Analytics Export and Reporting"
assert_descriptor \
  "studio-source-retains-owner" \
  "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Studio and Feature Store"
assert_excludes_shard \
  "studio-source-excludes-collaboration-child" \
  "src/Honua.Server/Features/Studio/StudioPackageEndpoints.cs" \
  "Server Features Collaboration Mobile and Identity"
assert_descriptor \
  "reporting-source-retains-owner" \
  "src/Honua.Server/Features/Reporting/AnalysisReportEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Analytics Export and Reporting"
assert_excludes_shard \
  "reporting-source-excludes-studio-child" \
  "src/Honua.Server/Features/Reporting/AnalysisReportEndpoints.cs" \
  "Server Features Studio and Feature Store"
assert_descriptor \
  "spec-source-retains-owner" \
  "src/Honua.Server/Features/Spec/HonuaSpecService.cs" \
  "targeted" \
  "false" \
  "Server Features Spec Printing and Static Maps"

# Keep the safety net for a future unrelated Routing area; #2693 claims only
# the existing Features/Routing slice.
assert_descriptor \
  "unknown-routing-area-still-run-all" \
  "src/Honua.Routing/Features/Future/FutureRouter.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# ---------------------------------------------------------------------------
# targeted_override_prefixes guard (ADR-0037 targeting follow-up): endpoint-
# registration PLUMBING and shared Honua.Hosting FEATURE-AREA dirs route to a
# representative SMOKE / auth subset instead of run_all. The always-on
# architecture/governance guards (EndpointRegistry/OperationRegistry drift +
# coverage, proof-ledger; Honua.Architecture.Tests on the build job) run on
# every PR regardless and catch a registration mistake, so a smoke subset here
# is safe. These lock in the narrowed triggers against regression.
# ---------------------------------------------------------------------------

# EndpointRegistry.cs alone: registration plumbing -> smoke subset, NOT run_all,
# and NOT a silent skip. Includes the API/governance shard.
assert_descriptor \
  "endpoint-registry-plumbing-smoke" \
  "src/Honua.Server/EndpointRegistry.cs" \
  "targeted" \
  "false" \
  "STAC and API Governance"
assert_excludes_shard \
  "endpoint-registry-excludes-wfs-endpoints" \
  "src/Honua.Server/EndpointRegistry.cs" \
  "WFS Endpoints"

# Program.cs (route registration) -> same smoke subset, NOT run_all.
assert_descriptor \
  "program-registration-smoke" \
  "src/Honua.Server/Program.cs" \
  "targeted" \
  "false" \
  "FeatureServer Endpoints"

# Startup/JsonContextRegistration.cs sits under the infrastructure_paths prefix
# src/Honua.Server/Startup/ but the override must WIN so a JSON-context tweak
# routes to the smoke subset instead of forcing run_all.
assert_descriptor \
  "jsoncontext-registration-smoke" \
  "src/Honua.Server/Startup/JsonContextRegistration.cs" \
  "targeted" \
  "false" \
  "OData Core"

# The shared WMTS TopLeftCorner formatter has three bounded consumers. Keep a
# change to that exact file on the classic WMTS, OGC API Tiles, and ImageServer
# owners instead of tripping the unmapped shared-protocol run_all safety net.
assert_exact_shards \
  "shared-wmts-formatter-bounded-owners" \
  "src/Honua.Protocols.Ogc.Shared/Common/WmtsTileMatrixFormatting.cs" \
  '["OGC Classic WMTS","OGC API Tiles Coverages and Processes","GeoServices ImageServer"]'

# An endpoint-ADDING feature PR touches the registration plumbing AND a feature
# dir under a shard's paths: it runs the smoke subset PLUS that feature's owning
# shard (here GeoServices ImageServer), and is targeted, not run_all.
assert_descriptor \
  "endpoint-adding-feature-includes-feature-shard" \
  "$(printf '%s\n%s\n%s' \
      'src/Honua.Server/EndpointRegistry.cs' \
      'src/Honua.Server/Program.cs' \
      'src/Honua.Protocols.GeoServices/ImageServer/ImageServerEndpoints.cs')" \
  "targeted" \
  "false" \
  "GeoServices ImageServer"
assert_descriptor \
  "endpoint-adding-feature-includes-smoke" \
  "$(printf '%s\n%s' \
      'src/Honua.Server/EndpointRegistry.cs' \
      'src/Honua.Protocols.GeoServices/ImageServer/ImageServerEndpoints.cs')" \
  "targeted" \
  "false" \
  "MCP"

# A Honua.Hosting/Features/Authentication/ change -> auth/security shards, NOT
# run_all and NOT a silent skip.
assert_descriptor \
  "hosting-authentication-auth-shards" \
  "src/Honua.Hosting/Features/Authentication/JwtBearerSupport.cs" \
  "targeted" \
  "false" \
  "Infra and Security"
assert_excludes_shard \
  "hosting-authentication-excludes-featureserver" \
  "src/Honua.Hosting/Features/Authentication/JwtBearerSupport.cs" \
  "FeatureServer Endpoints"

# A Honua.Hosting/Features/Security/ change -> same auth/security shards.
assert_descriptor \
  "hosting-security-auth-shards" \
  "src/Honua.Hosting/Features/Security/SecretReferenceResolver.cs" \
  "targeted" \
  "false" \
  "Infra and Security"

# Conservatism guards: the override must NOT widen run_all coverage. A NON-override
# Startup file (DI/host bootstrap core) must STILL run_all, and a generic unmapped
# Honua.Server source file must STILL run_all.
assert_descriptor \
  "startup-bootstrap-core-still-run-all" \
  "src/Honua.Server/Startup/InfrastructureCompositionRoot.cs" \
  "infrastructure_change" \
  "true" \
  "Core"
assert_descriptor \
  "non-override-hosting-feature-still-run-all" \
  "src/Honua.Hosting/Features/Caching/HostResponseCache.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# A registration override mixed with a genuinely cross-cutting Core change still
# escalates to run_all (the Core file is not override-claimed): override must not
# mask a real infrastructure change.
assert_descriptor \
  "override-plus-core-still-run-all" \
  "$(printf '%s\n%s' \
      'src/Honua.Server/Startup/JsonContextRegistration.cs' \
      'src/Honua.Core/Queries/FeatureQuery.cs')" \
  "infrastructure_change" \
  "true" \
  "Core"

# ---------------------------------------------------------------------------
# #1897 src-dir->shard mapping (follow-up to #2035): mapping each feature's
# SOURCE tree to the shard(s) whose filter runs its tests stops the
# unmapped_source_change run_all from firing on normal feature PRs. These lock
# in the four MEASURED regressions (#1939/#1944/#1961/#1963) — each must now be
# targeted AND must include the shard that runs its OWN feature's tests.
# ---------------------------------------------------------------------------

# Capability registry changes are covered by the always-on Core foundation tests,
# the server capability projection tests, and the admin feature-overview projection.
# They must not trigger the 50+ shard unmapped-source fallback.
assert_descriptor \
  "core-capability-registry-targeted" \
  "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs" \
  "targeted" \
  "false" \
  "Server Features Data and Sharing"
assert_descriptor \
  "core-capability-registry-includes-admin-governance" \
  "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Catalog and Configuration"
assert_excludes_shard \
  "core-capability-registry-excludes-unrelated-admin-operations" \
  "src/Honua.Core/Features/Capabilities/CapabilityRegistry.cs" \
  "Server Features Admin Operations"

# #1939 raster: src/Honua.Core/Features/Raster/ is the shared raster pipeline
# adapted by ImageServer + OGC API Coverages + Wcs(WFS). A raster change targets
# those raster-rendering shards (incl. GeoServices ImageServer which runs the
# ImageServer raster-sampling tests), NOT run_all.
assert_descriptor \
  "raster-core-targeted" \
  "src/Honua.Core/Features/Raster/ZarrParser/ZarrMetadataExtractor.cs" \
  "targeted" \
  "false" \
  "GeoServices ImageServer"
assert_excludes_shard \
  "raster-core-excludes-featureserver" \
  "src/Honua.Core/Features/Raster/ZarrParser/ZarrMetadataExtractor.cs" \
  "FeatureServer Endpoints"

# #1944 collaboration: src/Honua.Server/Features/Collaboration/ (and the Core
# slice) is owned by the Server Features Misc catch-all (Features.Collaboration.*
# tests). A collaboration change targets that shard, NOT run_all.
assert_descriptor \
  "collaboration-targeted" \
  "$(printf '%s\n%s' \
      'src/Honua.Core/Features/Collaboration/FeatureLocks/FeatureEditGuard.cs' \
      'src/Honua.Server/Features/Collaboration/FeatureLocks/FeatureLockServices.cs')" \
  "targeted" \
  "false" \
  "Server Features Misc"

# #1961 output-formats: the shared format/geometry host services in
# src/Honua.Hosting/Features/Services/ (GeoParquet writer, GeometryService,
# SpatialReference/Raster helpers) are mapped to every query/format consumer
# shard (FeatureServer/OData/OGC Features/WFS families, Infra & Security which
# runs GeometryService/RasterParsingHelpers tests, Server Features Misc which
# runs Export/FeatureStore, and Core). A change there is targeted across that
# consumer set, NOT run_all — and includes the FeatureServer query shard that
# the #1961 FeatureServerQueryHandler change exercises.
assert_descriptor \
  "hosting-output-format-services-targeted" \
  "$(printf '%s\n%s' \
      'src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs' \
      'src/Honua.Protocols.GeoServices/FeatureServer/FeatureServerQueryHandler.cs')" \
  "targeted" \
  "false" \
  "FeatureServer Query"
# Its own OData query path is included too.
assert_descriptor \
  "hosting-output-format-services-includes-odata" \
  "src/Honua.Hosting/Features/Services/GeoParquetFeatureWriter.cs" \
  "targeted" \
  "false" \
  "OData Core"

# #1963 oauth: the GeoServices Sharing source (src/Honua.Protocols.GeoServices/
# Sharing/) is owned by the Server Features Misc catch-all (Features.Sharing.*),
# the Admin OAuth endpoints by the Admin & Console shard (Features.Admin.*), and
# the Hosting/Features/Authentication override routes to the auth/security
# shards. The combined PR is targeted, NOT run_all, and runs its own tests'
# shards (Server Features Admin Authentication for OAuthClientEndpointsTests).
assert_descriptor \
  "oauth-sharing-source-targeted" \
  "src/Honua.Protocols.GeoServices/Sharing/SharingOAuth2Endpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Misc"
assert_descriptor \
  "oauth-admin-endpoints-targeted" \
  "src/Honua.Server/Features/Admin/OAuthClientEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Authentication"
assert_excludes_shard \
  "oauth-admin-excludes-credentials-child" \
  "src/Honua.Server/Features/Admin/OAuthClientEndpoints.cs" \
  "Server Features Admin Credentials"
assert_descriptor \
  "admin-api-key-targeted" \
  "src/Honua.Server/Features/Admin/AdminApiKeyEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Credentials"
assert_descriptor \
  "tenant-admin-targeted" \
  "src/Honua.Server/Features/Admin/TenantAdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Authorization"
assert_descriptor \
  "shared-admin-endpoints-own-authentication-tests" \
  "src/Honua.Server/Features/Admin/AdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Authentication"
assert_descriptor \
  "identity-admin-owns-platform-provider-tests" \
  "src/Honua.Server/Features/Admin/IdentityAdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Platform and Connections"
assert_descriptor \
  "tile-cache-source-owns-service-tests" \
  "src/Honua.Server/Features/Admin/TileOperations/TileCacheEvictionService.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "configuration-redactor-owns-unit-tests" \
  "src/Honua.Server/Features/Admin/Services/ConfigurationSecretRedactor.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "external-service-discovery-owns-unit-tests" \
  "src/Honua.Server/Features/Admin/Services/ExternalServiceDiscoveryService.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "deploy-control-targeted" \
  "src/Honua.Server/Features/Admin/DeployControlEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Release Control"
assert_descriptor \
  "deploy-control-includes-admin-authorization" \
  "src/Honua.Server/Features/Admin/DeployControlEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Authorization"
assert_descriptor \
  "secure-connection-targeted" \
  "src/Honua.Server/Features/Admin/SecureConnectionEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Platform and Connections"
assert_descriptor \
  "federation-admin-targeted" \
  "src/Honua.Server/Features/Admin/Federation/FederationEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Governance and Sharing"
assert_descriptor \
  "console-session-targeted" \
  "src/Honua.Server/Features/Console/ConsoleSessionEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Console and Alerts"
assert_descriptor \
  "feature-overview-targeted" \
  "src/Honua.Server/Features/Admin/FeatureOverviewEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Catalog and Configuration"
assert_descriptor \
  "alert-admin-targeted" \
  "src/Honua.Server/Features/Admin/AlertAdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "alert-admin-includes-admin-authorization" \
  "src/Honua.Server/Features/Admin/AlertAdminEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Authorization"
assert_descriptor \
  "proposal-source-includes-agentic-ops-owner" \
  "src/Honua.Server/Features/Admin/ProposalEndpoints.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "proposal-model-includes-agentic-ops-owner" \
  "src/Honua.Server/Features/Admin/Models/ProposalModels.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "alert-source-includes-agentic-ops-owner" \
  "src/Honua.Server/Features/Alerts/AlertDispatchBackgroundService.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "alert-core-includes-agentic-ops-owner" \
  "src/Honua.Core/Features/Alerts/Domain/AlertModels.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "alert-postgres-includes-agentic-ops-owner" \
  "src/Honua.Postgres/Features/Alerts/PostgresAlertDispatchStore.cs" \
  "targeted" \
  "false" \
  "Server Features Admin Integrations and Automation"
assert_descriptor \
  "authorization-core-includes-studio-owner" \
  "src/Honua.Core/Features/Authorization/PermissionResolver.cs" \
  "targeted" \
  "false" \
  "Server Features Studio and Feature Store"
assert_descriptor \
  "authorization-postgres-includes-studio-owner" \
  "src/Honua.Postgres/Features/Authorization/PostgresRlsPolicyStore.cs" \
  "targeted" \
  "false" \
  "Server Features Studio and Feature Store"
assert_descriptor \
  "forms-core-includes-studio-owner" \
  "src/Honua.Core/Features/Forms/Packages/FormPackageContracts.cs" \
  "targeted" \
  "false" \
  "Server Features Studio and Feature Store"
assert_descriptor \
  "forms-postgres-includes-studio-owner" \
  "src/Honua.Postgres/Features/Forms/PostgresFormPackageStore.cs" \
  "targeted" \
  "false" \
  "Server Features Studio and Feature Store"

# A Server/Features/Infrastructure FEATURE subdir (ControlPlane/Errors/Helpers)
# maps to Infra and Security (Features.Infrastructure.* tests), NOT run_all —
# while the shared host-wiring subdirs (Hosting/Middleware/Services/Monitoring)
# still escalate via infrastructure_paths (asserted below).
assert_descriptor \
  "infra-feature-controlplane-targeted" \
  "src/Honua.Server/Features/Infrastructure/ControlPlane/DeployWorkflowService.cs" \
  "targeted" \
  "false" \
  "Infra and Security"
# The deploy/coordinated-release/telemetry ControlPlane source actually lives at
# src/Honua.Server/Features/ControlPlane/ (sibling of Features/Infrastructure/),
# while its tests live under tests/.../Features/Infrastructure/ControlPlane/. That
# path is claimed by the Infra and Security shard so a reconciler/telemetry change
# targets it instead of tripping the unmapped-source run_all net.
assert_descriptor \
  "controlplane-source-targeted" \
  "src/Honua.Server/Features/ControlPlane/DeployWorkflowReconciler.cs" \
  "targeted" \
  "false" \
  "Infra and Security"
assert_descriptor \
  "infra-hosting-wiring-still-run-all" \
  "src/Honua.Server/Features/Infrastructure/Hosting/FeatureRegistrationExtensions.cs" \
  "infrastructure_change" \
  "true" \
  "Core"

# Cross-cutting Core feature trees NOT mapped to a shard still run_all (the
# unmapped-source net): e.g. src/Honua.Core/Features/Geometry/ has no owning
# shard and must not be silently narrowed.
assert_descriptor \
  "core-geometry-feature-still-run-all" \
  "src/Honua.Core/Features/Geometry/GeometryOperations.cs" \
  "unmapped_source_change" \
  "true" \
  "Core"

# ---------------------------------------------------------------------------
# #1899 guard: every Honua.Server.Tests class must be claimed by at least one
# shard filter (in the correct test assembly) or it never runs in CI. This is
# the anti-regression check for the coverage hole — a new test class in an
# unmapped namespace fails CI here instead of silently never running.
# ---------------------------------------------------------------------------
if [[ -n "${PYTHON_BIN}" ]]; then
echo "Checking server-test shard coverage (no orphaned test classes)..."
"${PYTHON_BIN}" scripts/ci/check-server-test-shard-coverage.py \
  --assert-owner \
    "Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wps20.Wps20EndpointsTests" \
    "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj" \
    "WFS" \
  --assert-owner \
    "Honua.Protocols.Ogc.Classic.Wps20.Wps20ConformanceEchoTests" \
    "tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj" \
    "WFS" \
  --assert-owner \
    "Honua.Server.Tests.Features.Protocols.GeoServices.Tiles.CompactTilePackageWriterTests" \
    "tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj" \
    "GeoServices ImageServer" \
  --assert-owner \
    "Honua.Server.Tests.Routing.NAServerPgRoutingEndToEndTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Core" \
  --assert-owner \
    "Honua.Server.Tests.Routing.NetworkDatasetValidationTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Core" \
  --assert-owner \
    "Honua.Server.Tests.Routing.PgRoutingProviderIntegrationTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Core" \
  --assert-owner \
    "Honua.Server.Tests.Import.PostgresMigrationStyleApplicatorTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Migration" \
  --assert-owner \
    "Honua.Server.Tests.Features.Protocols.Zarr.DatacubeTileEndpointTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Misc" \
  --assert-owner \
    "Honua.Server.Tests.Features.Protocols.Zarr.ZarrEndpointTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Misc" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.AdminAuthEndpointsTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Authentication" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.AdminAuthorizationTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Authorization" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.PlatformAdminEndpointsTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Platform and Connections" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.TileCacheEvictionServiceTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Integrations and Automation" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.ConfigurationSecretRedactorTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Integrations and Automation" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.ExternalServiceDiscoveryServiceTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Integrations and Automation" \
  --assert-owner \
    "Honua.Server.Tests.Features.Admin.AgenticOpsLoopDeadLetterE2eTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Admin Integrations and Automation" \
  --assert-owner \
    "Honua.Server.Tests.Features.Studio.StudioBridgedFamilyEndpointsTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Studio and Feature Store" \
  --assert-owner \
    "Honua.Server.Tests.Features.Protocols.Future.FutureEndpointTests" \
    "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj" \
    "Server Features Misc"
else
  echo "⚠️  Skipping server-test shard coverage check (no working Python 3: tried python3/python/py)"
fi

echo "CI router validation passed."
