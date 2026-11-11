#!/usr/bin/env bash
# Decide whether the shared lean gate (./.github/actions/lean-gate) compiles the
# whole solution or only the projects this change can affect, and emit a
# solution filter (.slnf) describing the latter.
#
# WHY A SOLUTION FILTER, NOT A LIST OF PROJECTS
#   `dotnet build a.csproj b.csproj` is not a thing: MSBuild rejects it with
#   MSB1008 ("Only one project can be specified"). A .slnf names a subset of an
#   existing solution, builds in one MSBuild invocation with full cross-project
#   parallelism, and still pulls in every out-of-filter <ProjectReference> a
#   listed project needs — which is exactly the semantics the gate wants.
#
# WHAT GOES IN THE FILTER
#   The union of:
#     1. compute-affected-projects.sh — the changed projects plus their
#        reverse-dependency (consumer) closure, and
#     2. the root projects of GATE_TEST_GROUP — the leaf test projects that
#        group's `dotnet test --no-build` steps execute (or the server host the
#        producer boots). They must be compiled even for a diff that does not
#        reach them or those steps fail with "assembly not found".
#   BUILD_SCOPE=test-roots skips the diff and builds only the group's roots:
#   a parallel test job whose compile coverage another job already provides.
#   Forward dependencies are NOT enumerated here; MSBuild resolves them from
#   <ProjectReference> when it builds the filtered set.
#
# SAFETY NET / MISS-RISK CONTRACT
#   Anything that could invalidate the affected-projects assumption forces a
#   full build: compute-affected-projects.sh force-fulls on Directory.*.props,
#   Honua.sln, global.json, NuGet.config, .github/ and scripts/ci/, and this
#   script force-fulls whenever it cannot compute a trustworthy diff base (a
#   non-pull_request event, a checkout too shallow to hold the base commit, or
#   any failure of the underlying script). A residual affected-closure miss is
#   caught by the merge train's full-solution build BEFORE the change lands, so
#   it is fixed forward and never reaches trunk unverified.
#
# Inputs (env):
#   BUILD_SCOPE        'full' (default), 'affected' or 'test-roots'.
#   GATE_TEST_GROUP    'all' (default), 'architecture', 'unit-fast',
#                      'server-fast', 'server-governance', 'producer' or
#                      'analyzers'.
#   GITHUB_EVENT_NAME  Event driving the caller; only 'pull_request' has a
#                      trustworthy diff base.
#   GATE_BASE_REF      Optional explicit base ref (tests use this). Defaults to
#                      HEAD^1 — on a pull_request the checked-out ref is
#                      refs/pull/N/merge, whose first parent IS the current base
#                      branch tip, which is a better base than a possibly-stale
#                      pull_request.base.sha. Requires fetch-depth >= 2.
#   GATE_FILTER_PATH   Where to write the .slnf (default "${RUNNER_TEMP}/lean-gate.slnf").
#
# Outputs:
#   Appends `mode=` (full|affected|test-roots|none) and `filter=` (path, empty
#   when full or none; none means the diff reaches no project) to
#   $GITHUB_OUTPUT when set, and always prints a human-readable summary.
#
# Exit codes: 0 always, unless invoked outside a git repository.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

SOLUTION="Honua.sln"

# The leaf test projects the lean gate's `dotnet test --no-build` steps run.
# scripts/ci/fixtures/validate-lean-gate.py asserts these lists are exactly the
# set of projects those steps name, so adding a test step without adding it here
# is a red gate, not a mystery "assembly not found" at run time.
SERVER_TEST_PROJECTS=(
  "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
)
ARCHITECTURE_TEST_PROJECTS=(
  "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"
  "tests/dotnet/Honua.Architecture.SourceTests/Honua.Architecture.SourceTests.csproj"
  # Projection generators run tests from these assemblies before verification.
  "tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj"
  "tests/dotnet/Honua.Worker.Gdal.Tests/Honua.Worker.Gdal.Tests.csproj"
  "tests/dotnet/Honua.Ai.UnitTests/Honua.Ai.UnitTests.csproj"
)

# The catalog is validated before either build mode. The same project set is
# consumed by the no-build Fast smoke; failure must stop the gate, not narrow it.
unit_fast_projects="$(python3 scripts/ci/test-projects.py gate-fast-projects)"
UNIT_FAST_TEST_PROJECTS=()
if [[ -n "${unit_fast_projects}" ]]; then
  mapfile -t UNIT_FAST_TEST_PROJECTS <<< "${unit_fast_projects}"
fi

# The producer boots this host after its build.
SERVER_HOST_PROJECT="src/Honua.Server/Honua.Server.csproj"

test_group="${GATE_TEST_GROUP:-all}"
case "${test_group}" in
  all) ROOT_PROJECTS=("${SERVER_TEST_PROJECTS[@]}" "${ARCHITECTURE_TEST_PROJECTS[@]}" "${UNIT_FAST_TEST_PROJECTS[@]}") ;;
  # Projection regeneration exports the admin parity manifest from Server.Tests.
  architecture) ROOT_PROJECTS=("${ARCHITECTURE_TEST_PROJECTS[@]}" "${SERVER_TEST_PROJECTS[@]}") ;;
  unit-fast) ROOT_PROJECTS=("${UNIT_FAST_TEST_PROJECTS[@]}") ;;
  server-fast|server-governance) ROOT_PROJECTS=("${SERVER_TEST_PROJECTS[@]}") ;;
  producer) ROOT_PROJECTS=("${SERVER_HOST_PROJECT}") ;;
  analyzers) ROOT_PROJECTS=() ;;
  *) echo "::error::Unknown lean gate test group '${test_group}'" >&2; exit 1 ;;
esac

build_scope="${BUILD_SCOPE:-full}"
event_name="${GITHUB_EVENT_NAME:-}"
filter_path="${GATE_FILTER_PATH:-${RUNNER_TEMP:-/tmp}/lean-gate.slnf}"

emit() {
  local mode="$1" filter="$2" reason="$3"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo "mode=${mode}"
      echo "filter=${filter}"
    } >> "${GITHUB_OUTPUT}"
  fi
  echo "::notice::Lean gate build scope: ${mode} (${reason})"
}

full() {
  emit "full" "" "$1"
  exit 0
}

# Writes the solution filter for the given projects and emits it under a mode.
write_filter() {
  local mode="$1" reason="$2"
  shift 2
  local solution_text project windows_path
  local -a selected=()
  solution_text="$(cat "${SOLUTION}")"
  for project in "$@"; do
    windows_path="${project//\//\\}"
    if [[ "${solution_text}" != *"\"${windows_path}\""* ]]; then
      if [[ " ${ROOT_PROJECTS[*]} " == *" ${project} "* ]]; then
        full "root project '${project}' is missing from ${SOLUTION}"
      fi
      echo "::debug::skipping '${project}': not a member of ${SOLUTION}"
      continue
    fi
    selected+=("${windows_path}")
  done

  if [[ ${#selected[@]} -eq 0 ]]; then
    [[ "${test_group}" == analyzers ]] && { emit "none" "" "the diff reaches no solution project"; exit 0; }
    full "no solution projects selected"
  fi

  mapfile -t selected < <(printf '%s\n' "${selected[@]}" | sort -u)
  mkdir -p "$(dirname "${filter_path}")"
  {
    echo '{'
    echo '  "solution": {'
    printf '    "path": "%s",\n' "${REPO_ROOT}/${SOLUTION}"
    echo '    "projects": ['
    local index separator
    for index in "${!selected[@]}"; do
      separator=','
      [[ "${index}" -eq $(( ${#selected[@]} - 1 )) ]] && separator=''
      printf '      "%s"%s\n' "${selected[${index}]//\\/\\\\}" "${separator}"
    done
    echo '    ]'
    echo '  }'
    echo '}'
  } > "${filter_path}"

  echo "Lean gate solution filter (${#selected[@]} of $(grep -c '^Project(' "${SOLUTION}") solution projects):"
  printf '  %s\n' "${selected[@]//\\//}"
  emit "${mode}" "${filter_path}" "${#selected[@]} projects, ${reason}"
  exit 0
}

case "${build_scope}" in
  affected) ;;
  test-roots) write_filter "test-roots" "roots of test group ${test_group}" "${ROOT_PROJECTS[@]}" ;;
  *) full "caller requested build-scope=${build_scope}" ;;
esac

# Only a pull_request has a merge ref whose first parent is the base branch.
# merge_group batches, workflow_dispatch and schedule do not, and the composite's
# callers deliberately keep those on `full` anyway.
base_ref="${GATE_BASE_REF:-}"
if [[ -z "${base_ref}" ]]; then
  if [[ "${event_name}" != "pull_request" ]]; then
    full "event '${event_name:-unknown}' has no trustworthy diff base"
  fi
  base_ref="HEAD^1"
fi

if ! git cat-file -e "${base_ref}^{commit}" 2>/dev/null; then
  full "base ref '${base_ref}' is not present in this checkout (fetch-depth too shallow)"
fi

affected=""
if ! affected="$(BASE_REF="${base_ref}" HEAD_REF="HEAD" scripts/ci/compute-affected-projects.sh 2>&1)"; then
  full "compute-affected-projects.sh failed; refusing to narrow the gate"
fi

# Drop the script's stderr warnings; only csproj paths and the ALL token matter.
affected="$(printf '%s\n' "${affected}" | grep -E '(\.csproj$|^ALL$)' || true)"

if printf '%s\n' "${affected}" | grep -qx 'ALL'; then
  full "shared build infrastructure changed (compute-affected-projects.sh reported ALL)"
fi

# Union the diff closure with the roots the group's later steps need.
declare -A wanted=()
while IFS= read -r project; do
  [[ -z "${project}" ]] && continue
  wanted["${project}"]=1
done <<< "${affected}"
for project in "${ROOT_PROJECTS[@]}"; do
  wanted["${project}"]=1
done

# A .slnf can only name projects the solution already contains. Anything else
# (the docker/ custom-code harness, conformance generators) is not built by the
# full-solution build either, so write_filter skips it, except a missing root,
# which is a signal and falls back to full.
write_filter "affected" "test group ${test_group}" "${!wanted[@]}"
