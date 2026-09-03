#!/usr/bin/env bash
# Foundation-test FAMILY runner.
#
# Single source of truth for which test projects, filters and coverage settings
# belong to each parallel `.NET Foundation Tests (<family>)` job in
# .github/workflows/ci.yml. Before this script the whole foundation lane was one
# serial job whose steps ran back to back for ~39 minutes; the families here are
# exactly those steps, regrouped so four runners can execute them concurrently.
# Keeping the mapping in one file (rather than inline in the matrix) means the
# "same tests run after the split" invariant is checkable in one place, and
# `list-all` below lets a guard prove no project was dropped.
#
# Usage:
#   run-foundation-family.sh families              # every family id
#   run-foundation-family.sh projects <family>     # csproj paths to build
#   run-foundation-family.sh list <family>         # human-readable plan
#   run-foundation-family.sh list-all              # every csproj across families
#   run-foundation-family.sh build <family>        # one dotnet build for the family
#   run-foundation-family.sh run <family>          # dotnet test each project
#
# Spec record (one per line, `|`-separated):
#   csproj | trx-basename | filter | env (k=v,k=v) | flags (coverage,advisory)
#
# `coverage` adds the Coverlet XPlat collector + coverlet.runsettings, matching
# the pre-split job where ONLY Honua.Core.Tests collected coverage.
# `advisory` mirrors the pre-split `continue-on-error: true` step: the project
# runs and its failures stay visible, but they do not fail the job (#2949).
set -euo pipefail

# Every path below (csproj paths, Honua.sln, coverlet.runsettings, the generated
# solution filter) is repo-relative, so pin the working directory rather than
# depending on the caller's.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$(cd "${SCRIPT_DIR}/../.." && pwd)"

RESULTS_DIR="${FOUNDATION_RESULTS_DIR:-./tests/TestResults}"
CONFIGURATION="${FOUNDATION_CONFIGURATION:-Release}"

foundation_family_specs() {
  case "$1" in
    # Honua.Core.Tests is the only coverage-collecting project in the lane, and
    # the collector dominates its runtime, so it gets a runner to itself
    # alongside the two small always-on unit suites.
    core)
      cat <<'SPEC'
tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj|core-tests|||coverage
tests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj|load-tests|Tier!=Slow||
tests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj|core-security-tests|||
tests/dotnet/Honua.ControlPlane.Lambda.Tests/Honua.ControlPlane.Lambda.Tests.csproj|control-plane-lambda-tests|||
SPEC
      ;;
    # Honua.Server.Tests' Fast tier plus the three standalone suites that build
    # against the same server closure, so this family's build is shared.
    server)
      cat <<'SPEC'
tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj|server-fast-tier|Tier=Fast||
tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj|architecture-tests|||
tests/dotnet/Honua.Geoprocessing.Testing.Tests/Honua.Geoprocessing.Testing.Tests.csproj|gp-golden-harness-tests|||
tests/dotnet/Honua.Geoprocessing.Cli.Tests/Honua.Geoprocessing.Cli.Tests.csproj|gp-scaffolder-tests|||
SPEC
      ;;
    # #2943's protocol Fast tier: the 8 ADR-0042 protocol-split projects plus
    # Honua.Ai.Tests. scripts/ci/run-server-test-shard.sh appends `&Tier!=Fast`
    # to every server-tests shard filter, so these cases run HERE or nowhere.
    protocols)
      cat <<'SPEC'
tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj|Honua.Protocols.GeoServices-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.OgcApi.Tests/Honua.Protocols.OgcApi.Tests.csproj|Honua.Protocols.OgcApi-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.OgcClassic.Tests/Honua.Protocols.OgcClassic.Tests.csproj|Honua.Protocols.OgcClassic-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.OData.Tests/Honua.Protocols.OData.Tests.csproj|Honua.Protocols.OData-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.Scene.Tests/Honua.Protocols.Scene.Tests.csproj|Honua.Protocols.Scene-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.SensorThings.Tests/Honua.Protocols.SensorThings.Tests.csproj|Honua.Protocols.SensorThings-fast-tier|Tier=Fast||
tests/dotnet/Honua.Protocols.Stac.Tests/Honua.Protocols.Stac.Tests.csproj|Honua.Protocols.Stac-fast-tier|Tier=Fast||
tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj|Honua.Ai-fast-tier|Tier=Fast||
SPEC
      ;;
    # The #2943 orphan-revival provider units, the #2949 advisory Postgres
    # security suite, the Testcontainers MySQL suite and the #58 SQL/warehouse
    # unit subsets. All small, and none of them pull the server closure.
    providers)
      cat <<'SPEC'
tests/dotnet/Honua.Db.DuckDB.Tests/Honua.DuckDB.Tests.csproj|Honua.DuckDB-tests|||
tests/dotnet/Honua.Plugins.Tests/Honua.Plugins.Tests.csproj|Honua.Plugins-tests|||
tests/dotnet/Honua.ArcGisRest.Tests/Honua.ArcGisRest.Tests.csproj|Honua.ArcGisRest-tests|||
tests/dotnet/Honua.Db.Oracle.Tests/Honua.Oracle.Tests.csproj|Honua.Oracle-tests|||
tests/dotnet/Honua.Db.Postgres.Security.Tests/Honua.Postgres.Security.Tests.csproj|postgres-security-tests|||advisory
tests/dotnet/Honua.Db.MySql.Tests/Honua.MySql.Tests.csproj|mysql-tests||HONUA_TEST_MYSQL=1|
tests/dotnet/Honua.Db.SqlServer.Tests/Honua.SqlServer.Tests.csproj|Honua.SqlServer-unit-tests|FullyQualifiedName!~IntegrationTests||
tests/dotnet/Honua.Snowflake.Tests/Honua.Snowflake.Tests.csproj|Honua.Snowflake-unit-tests|FullyQualifiedName!~IntegrationTests||
tests/dotnet/Honua.Db.Redshift.Tests/Honua.Redshift.Tests.csproj|Honua.Redshift-unit-tests|FullyQualifiedName!~IntegrationTests||
tests/dotnet/Honua.Db.Databricks.Tests/Honua.Databricks.Tests.csproj|Honua.Databricks-unit-tests|FullyQualifiedName!~IntegrationTests||
SPEC
      ;;
    *)
      echo "run-foundation-family.sh: unknown family '$1'" >&2
      echo "known families: $(foundation_families | tr '\n' ' ')" >&2
      return 2
      ;;
  esac
}

foundation_families() {
  printf '%s\n' core server protocols providers
}

foundation_projects() {
  foundation_family_specs "$1" | cut -d'|' -f1
}

# Projects the FAMILY-WIDE build may contain. Advisory projects are deliberately
# excluded: the pre-split workflow wrapped the advisory suite's build AND test in
# one `continue-on-error: true` step (#2949), so a compile break there was
# non-gating. Folding it into the shared build would have quietly promoted it to
# a gate — and taken the rest of the family down with it. It is built inside
# foundation_run instead, where the advisory flag still applies.
foundation_build_projects() {
  foundation_family_specs "$1" | awk -F'|' '$5 !~ /advisory/ { print $1 }'
}

foundation_build() {
  local family="$1"
  local slnf projects
  mapfile -t projects < <(foundation_build_projects "${family}")
  # `dotnet build` takes ONE project (MSB1008), so build the family through a
  # generated solution FILTER: MSBuild then walks the family's shared closure
  # once and parallelises across the independent projects, instead of N
  # sequential `dotnet build` calls each re-evaluating the same references.
  # A filter also builds each listed project's P2P closure, which is what makes
  # the `dotnet test --no-build` calls in foundation_run safe.
  # The filter's "path" is resolved relative to the .slnf file, so it has to
  # sit next to Honua.sln at the repository root.
  slnf="$(mktemp -p . "foundation-${family}-XXXXXX.slnf")"
  {
    printf '{\n  "solution": {\n    "path": "Honua.sln",\n    "projects": [\n'
    local first=1 p
    for p in "${projects[@]}"; do
      if ((first)); then first=0; else printf ',\n'; fi
      printf '      "%s"' "${p}"
    done
    printf '\n    ]\n  }\n}\n'
  } >"${slnf}"

  echo "::group::dotnet build (${family}: ${#projects[@]} project(s))"
  local rc=0
  dotnet build "${slnf}" --no-restore --configuration "${CONFIGURATION}" -graphBuild || rc=$?
  echo "::endgroup::"
  rm -f "${slnf}"
  return "${rc}"
}

foundation_run() {
  local family="$1"
  local csproj trx filter envs flags rc failures=0
  mkdir -p "${RESULTS_DIR}"
  while IFS='|' read -r csproj trx filter envs flags; do
    [[ -n "${csproj}" ]] || continue
    local -a cmd=(dotnet test "${csproj}"
      --no-build
      --no-restore
      --configuration "${CONFIGURATION}"
      --logger "trx;LogFileName=${trx}.trx"
      --logger "console;verbosity=minimal"
      --results-directory "${RESULTS_DIR}")
    if [[ -n "${filter}" ]]; then cmd+=(--filter "${filter}"); fi
    if [[ ",${flags}," == *,coverage,* ]]; then
      cmd+=(--collect:"XPlat Code Coverage" --settings coverlet.runsettings)
    fi
    local -a envprefix=() kvs=()
    if [[ -n "${envs}" ]]; then
      local kv
      IFS=',' read -ra kvs <<<"${envs}"
      for kv in "${kvs[@]}"; do envprefix+=("${kv}"); done
    fi

    # Advisory projects are not in the family build (see foundation_build_projects),
    # so build them here where a failure is swallowed exactly as the pre-split
    # `continue-on-error` step swallowed it.
    if [[ ",${flags}," == *,advisory,* ]]; then
      local build_rc=0
      echo "::group::dotnet build ${csproj} (advisory)"
      dotnet build "${csproj}" --no-restore --configuration "${CONFIGURATION}" -graphBuild || build_rc=$?
      echo "::endgroup::"
      if ((build_rc != 0)); then
        echo "::warning::${csproj} failed to BUILD (advisory, non-gating — see #2949); skipping its tests"
        continue
      fi
    fi

    echo "::group::dotnet test ${csproj}${filter:+ --filter ${filter}}"
    rc=0
    if ((${#envprefix[@]})); then
      env "${envprefix[@]}" "${cmd[@]}" || rc=$?
    else
      "${cmd[@]}" || rc=$?
    fi
    echo "::endgroup::"

    if ((rc != 0)); then
      if [[ ",${flags}," == *,advisory,* ]]; then
        echo "::warning::${csproj} failed (advisory, non-gating — see #2949)"
      else
        echo "::error::${csproj} failed (exit ${rc})"
        failures=$((failures + 1))
      fi
    fi
  done < <(foundation_family_specs "${family}")
  return "${failures}"
}

main() {
  local cmd="${1:-}"
  case "${cmd}" in
    families)       foundation_families ;;
    projects)       foundation_projects "${2:?family required}" ;;
    build-projects) foundation_build_projects "${2:?family required}" ;;
    # Number of trx files a complete run of every family must produce — one per
    # claimed project. The fan-in job asserts the merged artifact set matches.
    expected-trx-count) local f n=0; for f in $(foundation_families); do n=$((n + $(foundation_projects "${f}" | grep -c .))); done; printf '%s\n' "${n}" ;;
    list)       foundation_family_specs "${2:?family required}" ;;
    list-all)   local f; for f in $(foundation_families); do foundation_projects "${f}"; done ;;
    build)      foundation_build "${2:?family required}" ;;
    run)        foundation_run "${2:?family required}" ;;
    *)
      echo "usage: $0 {families|projects <family>|build-projects <family>|expected-trx-count|list <family>|list-all|build <family>|run <family>}" >&2
      return 2
      ;;
  esac
}

main "$@"
