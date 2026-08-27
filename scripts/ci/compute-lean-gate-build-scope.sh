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
#     2. REQUIRED_TEST_PROJECTS — the leaf test projects the gate's later
#        `dotnet test --no-build` steps execute. They must be compiled even for
#        a docs-only diff or those steps fail with "assembly not found".
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
#   BUILD_SCOPE        'full' (default) or 'affected'.
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
#   Appends `mode=` (full|affected) and `filter=` (path, empty when full) to
#   $GITHUB_OUTPUT when set, and always prints a human-readable summary.
#
# Exit codes: 0 always, unless invoked outside a git repository.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

SOLUTION="Honua.sln"

# The leaf test projects the lean gate's `dotnet test --no-build` steps run.
# scripts/ci/fixtures/validate-lean-gate.py asserts this list is exactly the set
# of projects those steps name, so adding a test step without adding it here is
# a red gate, not a mystery "assembly not found" at run time.
REQUIRED_TEST_PROJECTS=(
  "tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"
  "tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"
  "tests/dotnet/Honua.Ai.Tests/Honua.Ai.Tests.csproj"
)

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

if [[ "${build_scope}" != "affected" ]]; then
  full "caller requested build-scope=${build_scope}"
fi

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

# Union the diff closure with the leaf test projects the --no-build steps need.
declare -A wanted=()
while IFS= read -r project; do
  [[ -z "${project}" ]] && continue
  wanted["${project}"]=1
done <<< "${affected}"
for project in "${REQUIRED_TEST_PROJECTS[@]}"; do
  wanted["${project}"]=1
done

# A .slnf can only name projects the solution already contains. Anything else
# (the docker/ custom-code harness, conformance generators) is not built by the
# full-solution build either, so dropping it changes nothing — but a project
# that should be in the solution and is not is a signal, so say so and keep the
# gate honest by falling back to full.
solution_text="$(cat "${SOLUTION}")"
declare -a selected=()
for project in "${!wanted[@]}"; do
  windows_path="${project//\//\\}"
  if [[ "${solution_text}" != *"\"${windows_path}\""* ]]; then
    if [[ " ${REQUIRED_TEST_PROJECTS[*]} " == *" ${project} "* ]]; then
      full "required test project '${project}' is missing from ${SOLUTION}"
    fi
    echo "::debug::skipping '${project}': not a member of ${SOLUTION}"
    continue
  fi
  selected+=("${windows_path}")
done

if [[ ${#selected[@]} -eq 0 ]]; then
  full "no solution projects selected"
fi

mapfile -t selected < <(printf '%s\n' "${selected[@]}" | sort -u)

mkdir -p "$(dirname "${filter_path}")"
{
  echo '{'
  echo '  "solution": {'
  printf '    "path": "%s",\n' "${REPO_ROOT}/${SOLUTION}"
  echo '    "projects": ['
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
emit "affected" "${filter_path}" "${#selected[@]} projects"
