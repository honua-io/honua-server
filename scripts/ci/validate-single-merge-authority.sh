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

# Canonicalize the command verb without evaluating source. Git and GitHub CLI
# accept global options before their subcommand, so raw adjacency regexes are
# insufficient (for example, `git -C repo push` and `gh -R o/r pr merge`).
canonical_cli_source() {
  awk '
    BEGIN { RS="[;\n]" }
    {
      gsub(/[|&()]/, " ")
      for (i = 1; i <= NF; i++) token[i] = $i
      for (i = 1; i <= NF; i++) {
        cmd = token[i]
        gsub(/^["'"'"'`]+|["'"'"'`]+$/, "", cmd)
        if (cmd != "git" && cmd != "gh") continue
        j = i + 1
        while (j <= NF && token[j] ~ /^-/) {
          opt = token[j]
          if ((cmd == "git" && opt ~ /^(-C|-c|--git-dir|--work-tree|--namespace|--exec-path)$/) ||
              (cmd == "gh" && opt ~ /^(-R|--repo|--hostname|--config)$/)) j += 2
          else j++
        }
        verb = token[j]
        gsub(/^["'"'"'`]+|["'"'"'`,]+$/, "", verb)
        if (cmd == "git" && verb == "push") {
          printf "git push"
          for (k = j + 1; k <= NF; k++) printf " %s", token[k]
          print ""
        } else if (cmd == "gh" && (verb == "pr" || verb == "api")) {
          printf "gh %s", verb
          for (k = j + 1; k <= NF; k++) printf " %s", token[k]
          print ""
        }
      }
      delete token
    }
  '
}

source_has_forbidden_authority() {
  local source="$1" canonical
  local api_forbidden='github(\.rest)?\.pulls\.(merge|updateBranch)|pulls\.(merge|updateBranch)|mergePullRequest|updatePullRequestBranch'
  local cli_forbidden='gh[[:space:]]+pr[[:space:]]+merge|gh[[:space:]]+api[^#;|&]*/pulls/[^/[:space:]]+/(merge|update-branch)|git[[:space:]]+push[^#;|&]*(HEAD:)?(refs/heads/)?trunk|git[[:space:]]+push[[:space:]]+[^[:space:];]+[[:space:]]+"?(HEAD:)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|git[[:space:]]+push[^#;|&]*[[:alnum:]_./-]+:(refs/heads/)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?'
  grep -Eiq "${api_forbidden}" <<<"${source}" && return 0
  canonical="$(canonical_cli_source <<<"${source}")"
  grep -Eiq "${cli_forbidden}" <<<"${canonical}"
}

function_source() {
  local file="$1" function_name="$2"
  awk -v wanted="${function_name}" '
    $0 ~ "^" wanted "\\(\\)[[:space:]]*\\{" { found=1 }
    found && $0 !~ "^" wanted "\\(\\)[[:space:]]*\\{" &&
      $0 ~ "^[A-Za-z_][A-Za-z0-9_]*\\(\\)[[:space:]]*\\{" { exit }
    found { print }
  ' "${file}"
}

# Prove the post-CAS finalizer and every same-file helper reachable from it are
# observation-only with respect to trunk. This closes helper-indirection gaps
# while keeping the one intentional pre-CAS/CAS push authority in land.sh.
scan_post_cas_call_graph() {
  local file="$1" root="train_finalize_landed_members" current body candidate
  local -a functions queue
  mapfile -t functions < <(awk 'match($0,/^([A-Za-z_][A-Za-z0-9_]*)\(\)[[:space:]]*\{/,m){print m[1]}' "${file}")
  queue=("${root}")
  declare -A seen=()
  while [[ "${#queue[@]}" -gt 0 ]]; do
    current="${queue[0]}"; queue=("${queue[@]:1}")
    [[ -z "${seen[${current}]:-}" ]] || continue
    seen["${current}"]=1
    body="$(function_source "${file}" "${current}")"
    [[ -n "${body}" ]] || { echo "missing post-CAS function ${current}" >&2; return 1; }
    if source_has_forbidden_authority "${body}"; then
      echo "post-CAS merge-capable primitive reachable through ${current}" >&2
      return 1
    fi
    for candidate in "${functions[@]}"; do
      [[ -z "${seen[${candidate}]:-}" ]] || continue
      grep -Eq "(^|[^[:alnum:]_])${candidate}([^[:alnum:]_]|$)" <<<"${body}" && queue+=("${candidate}")
    done
  done
  return 0
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

  scan_post_cas_call_graph "${root}/scripts/ci/merge-train/land.sh" || return 1

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
    if source_has_forbidden_authority "${source}"; then
      echo "forbidden merge-capable primitive in ${rel}" >&2; found=1
    fi
  done <<<"${candidates}"
  [[ "${found}" == 0 ]] || {
    echo "merge authority exists outside the explicit batch-train allowlist" >&2; return 1;
  }
}

self_test() {
  local scratch; scratch="$(mktemp -d)"; trap 'rm -rf "${scratch}"' RETURN
  mkdir -p "${scratch}/.github/workflows" "${scratch}/scripts/ci/merge-train"
  printf 'jobs:\n  train:\n    steps:\n      - run: scripts/ci/merge-train/train.sh\n' >"${scratch}/.github/workflows/merge-train.yml"
  cat >"${scratch}/scripts/ci/merge-train/land.sh" <<'SH'
train_land_pr_info() { gh pr view "$1"; }
train_finalize_landed_members() { train_land_pr_info "$1"; }
train_land() { git push origin batch:trunk; }
SH
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
    'gh -R o/r pr merge 1 --merge'
    'gh api --method PUT repos/o/r/pulls/1/merge'
    $'gh api \\\n      --method PUT \\\n      repos/o/r/pulls/${pr}/merge'
    $'gh pr \\\n      merge 1 --merge'
    'git push origin HEAD:trunk'
    'git -C /tmp/repo push origin HEAD:trunk'
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
  cat >"${scratch}/scripts/ci/merge-train/land.sh" <<'SH'
train_post_cas_writer() { gh -R o/r pr merge 1 --merge; }
train_finalize_landed_members() { train_post_cas_writer; }
train_land() { git push origin batch:trunk; }
SH
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "post-CAS helper indirection escaped" >&2; return 1; }
  echo "single-authority fixtures: ${n} forbidden, 1 safe, 1 transitive"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_ROOT:-${repo_root}}"
echo "single merge authority: merge-train.yml"
