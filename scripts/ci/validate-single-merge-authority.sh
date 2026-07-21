#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

scan_authorities() {
  local workflows="$1" authority
  authority="${2:-${workflows}/merge-train.yml}"
  [[ ! -e "${workflows}/pr-merge-train.yml" ]] || {
    echo "legacy merge authority still exists" >&2; return 1;
  }
  [[ -f "${authority}" ]] && grep -Fq 'scripts/ci/merge-train/train.sh' "${authority}" || {
    echo "merge-train.yml does not invoke the canonical controller" >&2; return 1;
  }

  local candidates
  candidates="$(find "${workflows}" -maxdepth 1 -type f \( -name '*.yml' -o -name '*.yaml' \) ! -path "${authority}" -print)"
  [[ -z "${candidates}" ]] && return 0
  local forbidden='github(\.rest)?\.pulls\.(merge|updateBranch)|pulls\.(merge|updateBranch)|gh[[:space:]]+api[^#]*(--method[[:space:]]+(PUT|POST)[^#]*)?[^#]*/pulls/[^/[:space:]]+/merge|gh[[:space:]]+pr[[:space:]]+merge|git[[:space:]]+push[^#]*(HEAD:|:refs/heads/|:)trunk([[:space:]]|$)'
  local file found=0
  while IFS= read -r file; do
    [[ -z "${file}" ]] && continue
    if sed '/^[[:space:]]*#/d' "${file}" | grep -EIn "${forbidden}"; then
      echo "forbidden merge operation in ${file}" >&2; found=1
    fi
  done <<<"${candidates}"
  if [[ "${found}" == 1 ]]; then
    echo "a workflow other than merge-train.yml contains merge authority" >&2; return 1
  fi
}

self_test() {
  local scratch; scratch="$(mktemp -d)"; trap 'rm -rf "${scratch}"' RETURN
  mkdir -p "${scratch}/safe"
  printf 'jobs:\n  train:\n    steps:\n      - run: scripts/ci/merge-train/train.sh\n' >"${scratch}/safe/merge-train.yml"
  cat >"${scratch}/safe/read-only.yml" <<'YAML'
jobs:
  inspect:
    steps:
      - run: gh api repos/o/r/pulls/1
      # `git push origin HEAD:trunk` is documentation, not an executable line.
YAML
  scan_authorities "${scratch}/safe" || { echo "negative fixture rejected" >&2; return 1; }

  local pattern n=0
  for pattern in \
    'github.rest.pulls.merge({owner, repo, pull_number: 1})' \
    'github.pulls.merge({owner, repo, pull_number: 1})' \
    'github.rest.pulls.updateBranch({owner, repo, pull_number: 1})' \
    'pulls.updateBranch({pull_number: 1})' \
    'gh pr merge 1 --merge' \
    'gh api --method PUT repos/o/r/pulls/1/merge' \
    'git push origin HEAD:trunk' \
    'git push origin batch:refs/heads/trunk'; do
    n=$((n + 1)); cp -R "${scratch}/safe" "${scratch}/bad-${n}"
    printf 'jobs:\n  bad:\n    steps:\n      - run: %s\n' "${pattern}" >"${scratch}/bad-${n}/other.yml"
    scan_authorities "${scratch}/bad-${n}" >/dev/null 2>&1 \
      && { echo "positive fixture ${n} escaped: ${pattern}" >&2; return 1; }
  done
  echo "single-authority fixtures: ${n} forbidden, 1 safe"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_WORKFLOWS_DIR:-${repo_root}/.github/workflows}"
echo "single merge authority: merge-train.yml"
