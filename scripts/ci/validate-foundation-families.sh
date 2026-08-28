#!/usr/bin/env bash
# Guard: the `.NET Foundation Tests (<family>)` matrix in .github/workflows/ci.yml
# and the family definitions in scripts/ci/run-foundation-family.sh must stay in
# sync, and every project a family claims must exist exactly once.
#
# Why this exists (#3567). The foundation lane used to be ONE serial job, so
# "did every test project run?" was answerable by reading one list of steps.
# After the split it is answerable only if:
#
#   A. every family the runner script defines is instantiated by the workflow
#      matrix — a family present in the script but missing from the matrix runs
#      NOWHERE, and nothing else in CI would notice (the mirror of the #1899
#      orphaned-shard hole that scripts/ci/check-server-test-shard-coverage.py
#      guards for server-tests shards);
#   B. every matrix entry names a family the script actually knows, otherwise
#      the job fails on a typo only after burning a runner;
#   C. every claimed csproj exists on disk, so a rename cannot silently turn a
#      family into a no-op; and
#   D. no csproj is claimed by two families, which would double its cost and
#      quietly diverge the two runs' filters.
#
# Offline: no network, no dotnet, no gh. Runs inside scripts/ci/validate-ci-router.sh.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

RUNNER="scripts/ci/run-foundation-family.sh"
WORKFLOW=".github/workflows/ci.yml"
failures=0

fail() {
  echo "::error::$*" >&2
  echo "  FAIL: $*" >&2
  failures=$((failures + 1))
}

script_families="$("${RUNNER}" families | sort)"

# Matrix families, read from the `dotnet-foundation-family-tests` job block:
# from its `strategy:` line up to the following `services:` key at job level.
matrix_families="$(
  awk '
    /^  dotnet-foundation-family-tests:/ { injob = 1 }
    injob && /^  [a-z0-9_-]+:/ && !/^  dotnet-foundation-family-tests:/ { injob = 0 }
    injob && /^          - family: / { print $3 }
  ' "${WORKFLOW}" | sort
)"

if [[ -z "${matrix_families}" ]]; then
  fail "no '- family:' entries found in the dotnet-foundation-family-tests matrix in ${WORKFLOW}"
fi

if [[ "${script_families}" != "${matrix_families}" ]]; then
  fail "family drift between ${RUNNER} and ${WORKFLOW}"
  echo "    runner script: $(tr '\n' ' ' <<<"${script_families}")" >&2
  echo "    ci.yml matrix: $(tr '\n' ' ' <<<"${matrix_families}")" >&2
fi

all_projects="$("${RUNNER}" list-all)"

while read -r csproj; do
  [[ -n "${csproj}" ]] || continue
  if [[ ! -f "${csproj}" ]]; then
    fail "foundation family claims a project that does not exist: ${csproj}"
  fi
done <<<"${all_projects}"

duplicates="$(sort <<<"${all_projects}" | uniq -d)"
if [[ -n "${duplicates}" ]]; then
  while read -r dup; do
    [[ -n "${dup}" ]] || continue
    fail "project claimed by more than one foundation family: ${dup}"
  done <<<"${duplicates}"
fi

# Every family must claim at least one project: an empty family is a job that
# starts a runner, builds nothing and passes.
while read -r family; do
  [[ -n "${family}" ]] || continue
  count="$("${RUNNER}" projects "${family}" | grep -c . || true)"
  if [[ "${count}" -eq 0 ]]; then
    fail "foundation family '${family}' claims no projects"
  fi
done <<<"${script_families}"

if ((failures > 0)); then
  echo "Foundation family validation FAILED (${failures} problem(s))." >&2
  exit 1
fi

project_count="$(grep -c . <<<"${all_projects}")"
echo "Foundation family validation passed ($(wc -w <<<"${script_families}") families, ${project_count} projects)."
