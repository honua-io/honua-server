#!/usr/bin/env bash
# Classify the complete local pre-PR diff, including committed and working-tree
# changes. The first output line is CI_ONLY, NORMAL, or UNKNOWN; remaining lines
# are the sorted changed paths when they could be determined safely.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

base_ref="${1:-${BASE_REF:-origin/trunk}}"
head_ref="${2:-${HEAD_REF:-HEAD}}"
scratch="$(mktemp -d)"
trap 'rm -rf "${scratch}"' EXIT

unknown() {
  echo "UNKNOWN"
  exit 0
}

git rev-parse --verify --quiet "${base_ref}" >/dev/null 2>&1 || unknown
git rev-parse --verify --quiet "${head_ref}" >/dev/null 2>&1 || unknown

# Keep each command separate so any git failure produces UNKNOWN instead of an
# accidentally empty list that could select the no-build path.
git diff --name-only "${base_ref}...${head_ref}" >"${scratch}/committed" 2>/dev/null || unknown
git diff --name-only >"${scratch}/unstaged" 2>/dev/null || unknown
git diff --cached --name-only >"${scratch}/staged" 2>/dev/null || unknown
git ls-files --others --exclude-standard >"${scratch}/untracked" 2>/dev/null || unknown

LC_ALL=C sort -u \
  "${scratch}/committed" \
  "${scratch}/unstaged" \
  "${scratch}/staged" \
  "${scratch}/untracked" >"${scratch}/changed"

# Match the hosted BUILD_FILES exclusions in ci.yml. In particular,
# .github/actions/setup-dotnet-ci is intentionally not eligible.
classification="CI_ONLY"
while IFS= read -r path; do
  [[ -z "${path}" ]] && continue
  case "${path}" in
    README.md|docs/*|.github/ci-shards.json|scripts/ci/*)
      ;;
    .github/workflows/*.yml)
      # Hosted routing accepts direct workflow files only (`[^/]+.yml`). Bash's
      # case glob crosses slashes, so reject nested paths explicitly.
      workflow_path="${path#.github/workflows/}"
      [[ "${workflow_path}" != */* ]] || classification="NORMAL"
      ;;
    *)
      classification="NORMAL"
      ;;
  esac
  [[ "${classification}" == "CI_ONLY" ]] || break
done <"${scratch}/changed"

# An empty diff is not positive evidence for bypassing normal validation.
[[ -s "${scratch}/changed" ]] || classification="NORMAL"

echo "${classification}"
cat "${scratch}/changed"
