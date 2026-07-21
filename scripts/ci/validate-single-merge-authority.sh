#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

is_allowlisted() {
  case "$1" in
    scripts/ci/merge-train/land.sh|scripts/ci/merge-train/smart-ci.sh|scripts/ci/merge-train/fixtures/validate-merge-train.sh|scripts/ci/validate-single-merge-authority.sh)
      return 0 ;;
    *) return 1 ;;
  esac
}

is_safe_exclusion() {
  case "$1" in
    node_modules/*|vendor/*|third_party/*|artifacts/*|dist/*|coverage/*|*/bin/*|*/obj/*|*.min.js|*.generated.*)
      return 0 ;;
    *) return 1 ;;
  esac
}

normalized_source() {
  # Remove comment-only/inline-comment text, collapse shell continuations, then
  # delimit real lines. This catches evasive wrapping without joining unrelated
  # commands later in the workflow.
  awk '
    /^[[:space:]]*#/ { next }
    { sub(/[[:space:]]+#.*$/, "") }
    /\\[[:space:]]*$/ { sub(/\\[[:space:]]*$/, ""); printf "%s ", $0; next }
    { printf "%s ; ", $0 }
  ' "$1"
}

scan_authorities() {
  local root="$1" workflows
  workflows="${root}/.github/workflows"
  [[ ! -e "${workflows}/pr-merge-train.yml" ]] || {
    echo "legacy merge authority still exists" >&2; return 1;
  }
  [[ -f "${workflows}/merge-train.yml" ]] &&
    grep -Fq 'scripts/ci/merge-train/train.sh' "${workflows}/merge-train.yml" || {
      echo "merge-train.yml does not invoke the canonical controller" >&2; return 1;
    }

  local forbidden='github(\.rest)?\.pulls\.(merge|updateBranch)|pulls\.(merge|updateBranch)|mergePullRequest|updatePullRequestBranch|gh[[:space:]]+pr[[:space:]]+merge|gh[[:space:]]+api[^#;|&]*/pulls/[^/[:space:]]+/(merge|update-branch)|git[[:space:]]+push[^#;|&]*(HEAD:)?(refs/heads/)?trunk|git[[:space:]]+push[[:space:]]+[^[:space:];]+[[:space:]]+"?(HEAD:)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|git[[:space:]]+push[^#;|&]*[[:alnum:]_./-]+:(refs/heads/)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?'
  local file rel source found=0 candidates
  if git -C "${root}" rev-parse --git-dir >/dev/null 2>&1; then
    candidates="$(git -C "${root}" ls-files | grep -E '\.(yml|yaml|sh|bash|zsh|ps1|js|mjs|cjs|ts|py)$')"
  else
    candidates="$(cd "${root}" && find . -type f | sed 's#^\./##' | grep -E '\.(yml|yaml|sh|bash|zsh|ps1|js|mjs|cjs|ts|py)$')"
  fi
  while IFS= read -r file; do
    file="${root}/${file}"
    rel="${file#${root}/}"
    is_safe_exclusion "${rel}" && continue
    is_allowlisted "${rel}" && continue
    source="$(normalized_source "${file}")"
    if grep -Eiq "${forbidden}" <<<"${source}"; then
      echo "forbidden merge-capable primitive in ${rel}" >&2; found=1
    fi
  done <<<"${candidates}"
  [[ "${found}" == 0 ]] || {
    echo "merge authority exists outside the explicit batch-train allowlist" >&2; return 1;
  }
}

self_test() {
  local scratch; scratch="$(mktemp -d)"; trap 'rm -rf "${scratch}"' RETURN
  mkdir -p "${scratch}/.github/workflows" "${scratch}/scripts/ci"
  printf 'jobs:\n  train:\n    steps:\n      - run: scripts/ci/merge-train/train.sh\n' >"${scratch}/.github/workflows/merge-train.yml"
  cat >"${scratch}/.github/workflows/read-only.yml" <<'YAML'
jobs:
  inspect:
    steps:
      - run: gh api repos/o/r/pulls/1
      # `git push origin HEAD:trunk` is documentation, not executable.
      - run: git push origin HEAD:refs/heads/automation/report-${GITHUB_RUN_ID}
YAML
  scan_authorities "${scratch}" || { echo "safe fixture rejected" >&2; return 1; }

  local fixture n=0
  fixtures=(
    'github.rest.pulls.merge({owner, repo, pull_number: 1})'
    'github.pulls.merge({owner, repo, pull_number: 1})'
    'github.rest.pulls.updateBranch({owner, repo, pull_number: 1})'
    'pulls.updateBranch({pull_number: 1})'
    'mergePullRequest(input: {pullRequestId: $id})'
    'updatePullRequestBranch(input: {pullRequestId: $id})'
    'gh pr merge 1 --merge'
    'gh api --method PUT repos/o/r/pulls/1/merge'
    $'gh api \\\n      --method PUT \\\n      repos/o/r/pulls/${pr}/merge'
    $'gh pr \\\n      merge 1 --merge'
    'git push origin HEAD:trunk'
    'git push origin batch:refs/heads/trunk'
    $'target=trunk\n      git push origin HEAD:${target}'
    $'target=refs/heads/trunk\n      git push origin HEAD:${target}'
    'git push origin batch:${target}'
    'git push origin HEAD:refs/heads/${target}'
  )
  for fixture in "${fixtures[@]}"; do
    n=$((n + 1))
    printf 'jobs:\n  bad:\n    steps:\n      - run: |\n        %s\n' "${fixture}" >"${scratch}/.github/workflows/other.yml"
    scan_authorities "${scratch}" >/dev/null 2>&1 \
      && { echo "forbidden fixture ${n} escaped" >&2; return 1; }
  done
  rm -f "${scratch}/.github/workflows/other.yml"
  echo "single-authority fixtures: ${n} forbidden, 1 safe"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_ROOT:-${repo_root}}"
echo "single merge authority: merge-train.yml"
