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

is_dispatch_allowlisted() {
  case "$1" in
    .github/workflows/merge-train.yml|scripts/ci/merge-train/recovery.sh|scripts/ci/merge-train/fixtures/validate-merge-train.sh|scripts/ci/validate-single-merge-authority.sh)
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

tokenize_workflow_args() {
  local input="$1" char quote="" token="" started=0 i
  GH_WORKFLOW_TOKENS=()
  for ((i = 0; i < ${#input}; i++)); do
    char="${input:i:1}"
    if [[ -n "${quote}" ]]; then
      if [[ "${char}" == "${quote}" ]]; then
        quote=""
      elif [[ "${char}" == '\\' && "${quote}" == '"' ]]; then
        i=$((i + 1)); [[ ${i} -lt ${#input} ]] || return 1
        token+="${input:i:1}"
      else
        token+="${char}"
      fi
      continue
    fi
    case "${char}" in
      "'"|'"') quote="${char}"; started=1 ;;
      '\\')
        i=$((i + 1)); [[ ${i} -lt ${#input} ]] || return 1
        token+="${input:i:1}"; started=1 ;;
      ' '|$'\t'|$'\r'|$'\n')
        if [[ "${started}" == 1 ]]; then
          GH_WORKFLOW_TOKENS+=("${token}"); token=""; started=0
        fi ;;
      *) token+="${char}"; started=1 ;;
    esac
  done
  [[ -z "${quote}" ]] || return 1
  [[ "${started}" == 0 ]] || GH_WORKFLOW_TOKENS+=("${token}")
}

workflow_run_selector() {
  tokenize_workflow_args "$1" || return 1
  local i=0 token
  while ((i < ${#GH_WORKFLOW_TOKENS[@]})); do
    token="${GH_WORKFLOW_TOKENS[i]}"
    case "${token}" in
      --)
        i=$((i + 1)); ((i < ${#GH_WORKFLOW_TOKENS[@]})) || return 1
        printf '%s\n' "${GH_WORKFLOW_TOKENS[i]}"; return 0 ;;
      -R|--repo|--hostname|-r|--ref|-f|--raw-field|-F|--field)
        i=$((i + 2)); ((i <= ${#GH_WORKFLOW_TOKENS[@]})) || return 1 ;;
      --repo=*|--hostname=*|--ref=*|--raw-field=*|--field=*|-R?*|-r?*|-f?*|-F?*)
        i=$((i + 1)) ;;
      --json|--help)
        i=$((i + 1)) ;;
      -*) return 1 ;;
      *) printf '%s\n' "${token}"; return 0 ;;
    esac
  done
  return 1
}

has_forbidden_workflow_run() {
  local rest="$1" args selector base lower match
  while [[ "${rest}" =~ (^|[[:space:];\|\&])gh[[:space:]]+workflow[[:space:]]+run[[:space:]]+([^;\|\&]*) ]]; do
    match="${BASH_REMATCH[0]}"; args="${BASH_REMATCH[2]}"
    selector="$(workflow_run_selector "${args}")" || return 0
    base="${selector##*/}"; lower="${selector,,}"
    case "${selector}" in *'$'*|*'`'*) return 0 ;; esac
    [[ "${base,,}" != merge-train.yml && "${lower}" != "merge train" ]] || return 0
    rest="${rest#*"${match}"}"
  done
  return 1
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
  # Dispatching the live-capable train is itself a merge authority regardless
  # of how train_apply is spelled or valued. Canonical locations are separately
  # allowlisted; every other executable source must be unable to wake this flow.
  local live_dispatch="gh[[:space:]]+api[^#;|&]*/actions/workflows/([^/[:space:]]*/)?merge-train\.yml/dispatches|createWorkflowDispatch[^)]*merge-train\.yml"
  # A variable workflow selector cannot be proven non-authoritative statically.
  # Reject it everywhere except the explicit dispatch authority allowlist.
  # For JS calls, quoted and computed workflow_id keys are equivalent to the
  # bare key and must not hide a dynamic selector.
  local dynamic_dispatch="createWorkflowDispatch[^)]*(\[[[:space:]]*[\"']workflow_id[\"'][[:space:]]*\]|[\"']?workflow_id[\"']?)[[:space:]]*:[[:space:]]*[$]?[A-Za-z_][A-Za-z0-9_]*|createWorkflowDispatch[^)]*[{,][[:space:]]*workflow_id[[:space:],}]"
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
    source="$(normalized_source "${file}")"
    if ! is_allowlisted "${rel}" && grep -Eiq "${forbidden}" <<<"${source}"; then
      echo "forbidden merge-capable primitive in ${rel}" >&2; found=1
    fi
    if ! is_dispatch_allowlisted "${rel}" && grep -Eiq "${live_dispatch}" <<<"${source}"; then
      echo "forbidden live merge-train dispatch in ${rel}" >&2; found=1
    fi
    if ! is_dispatch_allowlisted "${rel}" && has_forbidden_workflow_run "${source}"; then
      echo "forbidden live or dynamic workflow dispatch in ${rel}" >&2; found=1
    fi
    if ! is_dispatch_allowlisted "${rel}" && grep -Eiq "${dynamic_dispatch}" <<<"${source}"; then
      echo "forbidden dynamic workflow dispatch in ${rel}" >&2; found=1
    fi
  done <<<"${candidates}"
  [[ "${found}" == 0 ]] || {
    echo "merge authority exists outside the explicit batch-train allowlist" >&2; return 1;
  }
}

self_test() {
  local scratch; scratch="$(mktemp -d)"; trap 'rm -rf "${scratch}"' RETURN
  mkdir -p "${scratch}/.github/workflows" "${scratch}/scripts/ci/merge-train"
  printf 'jobs:\n  train:\n    steps:\n      - run: scripts/ci/merge-train/train.sh\n      - run: gh workflow run merge-train.yml -f train_apply=true\n' >"${scratch}/.github/workflows/merge-train.yml"
  cat >"${scratch}/scripts/ci/merge-train/recovery.sh" <<'SH'
gh workflow run merge-train.yml --repo "${GITHUB_REPOSITORY}" --ref trunk \
  -f train_apply=true -f max_batch="${MAX_BATCH}" -f recovery_key="${key}"
gh api --method POST repos/o/r/actions/workflows/merge-train.yml/dispatches \
  -f inputs[train_apply]=${mode}
SH
  cat >"${scratch}/.github/workflows/read-only.yml" <<'YAML'
jobs:
  inspect:
    steps:
      - run: gh api repos/o/r/pulls/1
      - run: gh workflow run -R o/r docs.yml
      - run: gh workflow run --repo o/r lint.yml
      - run: gh workflow run --hostname github.example --repo o/r audit.yml
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
    'gh workflow run merge-train.yml -f train_apply=true'
    $'gh workflow run merge-train.yml \\\n+      -f train_apply=true'
    'gh api --method POST repos/o/r/actions/workflows/merge-train.yml/dispatches -f inputs[train_apply]=true'
    'github.rest.actions.createWorkflowDispatch({workflow_id: "merge-train.yml", inputs: {train_apply: true}})'
    'gh workflow run merge-train.yml -f train_apply=${mode}'
    'gh workflow run "merge-train.yml" -f train_apply=${mode}'
    "gh workflow run 'merge-train.yml' -f train_apply=false"
    'gh workflow run "Merge Train" -f train_apply=${mode}'
    "gh workflow run 'Merge Train' -f train_apply=false"
    $'flow=\'Merge Train\'\n      gh workflow run "$flow" -f train_apply=${mode}'
    $'flow=merge-train.yml\n      gh workflow run "${flow}" -f train_apply=false'
    'gh workflow run $flow -f train_apply=false'
    'gh workflow run -R honua-io/honua-server merge-train.yml -f train_apply=true'
    'gh workflow run --repo honua-io/honua-server merge-train.yml -f train_apply=true'
    'gh workflow run --hostname github.example --repo honua-io/honua-server merge-train.yml -f train_apply=true'
    'gh workflow run -R honua-io/honua-server $flow -f train_apply=true'
    $'github.rest.actions.createWorkflowDispatch({\n        owner,\n        repo,\n        workflow_id: "merge-train.yml",\n        ref: "trunk",\n        inputs: {\n          train_apply: mode\n        }\n      })'
    $'github.rest.actions.createWorkflowDispatch({\n        owner,\n        repo,\n        workflow_id: flow,\n        ref: "trunk"\n      })'
    $'github.rest.actions.createWorkflowDispatch({\n        owner,\n        repo,\n        "workflow_id": flow,\n        ref: "trunk"\n      })'
    $'github.rest.actions.createWorkflowDispatch({\n        owner,\n        repo,\n        ["workflow_id"]: flow,\n        ref: "trunk"\n      })'
  )
  for fixture in "${fixtures[@]}"; do
    n=$((n + 1))
    printf 'jobs:\n  bad:\n    steps:\n      - run: |\n        %s\n' "${fixture}" >"${scratch}/.github/workflows/other.yml"
    scan_authorities "${scratch}" >/dev/null 2>&1 \
      && { echo "forbidden fixture ${n} escaped: ${fixture}" >&2; return 1; }
  done
  rm -f "${scratch}/.github/workflows/other.yml"
  echo "single-authority fixtures: ${n} forbidden, 1 safe"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_ROOT:-${repo_root}}"
echo "single merge authority: merge-train.yml"
