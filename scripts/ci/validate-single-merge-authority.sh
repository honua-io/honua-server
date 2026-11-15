#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

is_allowlisted() {
  case "$1" in
    scripts/ci/validate-single-merge-authority.sh)
      return 0 ;;
    *) return 1 ;;
  esac
}

is_dispatch_allowlisted() {
  case "$1" in
    scripts/ci/validate-single-merge-authority.sh)
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

logical_shell_source() {
  # Remove shell comments only when # occurs outside quotes, collapse shell continuations, then
  # delimit real lines. This catches evasive wrapping without joining unrelated
  # commands later in the workflow.
  awk '
    function without_comment(s, out, i, c, prev, sq, dq, esc) {
      out = ""; sq = 0; dq = 0; esc = 0
      for (i = 1; i <= length(s); i++) {
        c = substr(s, i, 1); prev = (i == 1 ? "" : substr(s, i - 1, 1))
        if (esc) { out = out c; esc = 0; continue }
        if (c == "\\" && !sq) { out = out c; esc = 1; continue }
        if (c == "\047" && !dq) { sq = !sq; out = out c; continue }
        if (c == "\042" && !sq) { dq = !dq; out = out c; continue }
        if (c == "#" && !sq && !dq && (i == 1 || prev ~ /[[:space:]]/)) break
        out = out c
      }
      return out
    }
    { line = without_comment($0) }
    line ~ /^[[:space:]]*$/ { next }
    line ~ /\\[[:space:]]*$/ { sub(/\\[[:space:]]*$/, "", line); printf "%s", line; next }
    { printf "%s\n", line }
  ' "$1"
}

normalized_source() {
  logical_shell_source "$1" | awk 'NF { printf "%s ; ", $0 }'
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
    [[ ! "${selector}" =~ ^[0-9]+$ ]] || return 0
    [[ "${base,,}" != merge-train.yml && "${lower}" != "merge train" ]] || return 0
    rest="${rest#*"${match}"}"
  done
  return 1
}

# Canonicalize the command verb without evaluating source. Git and GitHub CLI
# accept global options before their subcommand, so raw adjacency regexes are
# insufficient (for example, `git -C repo push` and `gh -R o/r pr merge`).
canonical_cli_source() {
  awk '
    BEGIN { RS="[;\n]" }
    function dequote(value) {
      gsub(/\$\047/, "", value)
      gsub(/["'"'"'`]/, "", value)
      return value
    }
    {
      gsub(/[|&()]/, " ")
      has_ansi = 0; ansi_command = 0
      for (i = 1; i <= NF; i++) {
        raw[i] = $i
        token[i] = dequote($i)
      }
      # `command`/`env`/`exec`/etc. run their argument as the command verb
      # unchanged, so an ANSI-C-quoted verb behind one of these wrappers (and
      # any of their own leading -flags or env VAR=val assignments) is just as
      # much "the command name" as a literal first token; skip past them to
      # find the logically-first token before checking for an ANSI-C verb.
      first_logical = 1
      while (first_logical <= NF) {
        w = tolower(token[first_logical])
        gsub(/\\/, "", w); sub(/^.*\//, "", w); sub(/\.exe$/, "", w)
        if (w ~ /^(command|builtin|env|exec|nohup|nice|setsid|stdbuf|time|sudo)$/) {
          first_logical++
          while (first_logical <= NF && (token[first_logical] ~ /^-/ || token[first_logical] ~ /^[A-Za-z_][A-Za-z0-9_]*=/)) first_logical++
          continue
        }
        break
      }
      for (i = 1; i <= NF; i++) {
        if (raw[i] ~ /^[A-Za-z0-9_.\/\\-]*\$\047/ && raw[i] !~ /=/) {
          has_ansi = 1
          if ((i == first_logical && raw[i] !~ /=/) ||
              (i > 1 && raw[i - 1] ~ /=/ && raw[i] !~ /=/)) ansi_command = 1
        }
      }
      recognized_cli = 0
      for (i = 1; i <= NF; i++) {
        cmd = tolower(token[i])
        pathcmd = cmd
        gsub(/\\/, "/", pathcmd)
        sub(/^.*\//, "", pathcmd)
        sub(/\.exe$/, "", pathcmd)
        escapedcmd = cmd
        gsub(/\\/, "", escapedcmd)
        sub(/^.*\//, "", escapedcmd)
        sub(/\.exe$/, "", escapedcmd)
        if (pathcmd == "git" || pathcmd == "gh") cmd = pathcmd
        else if (escapedcmd == "git" || escapedcmd == "gh") cmd = escapedcmd
        else continue
        recognized_cli = 1
        j = i + 1
        while (j <= NF && token[j] ~ /^-/) {
          opt = token[j]
          if ((cmd == "git" && opt ~ /^(-C|-c|--git-dir|--work-tree|--namespace|--exec-path)$/) ||
              (cmd == "gh" && opt ~ /^(-R|--repo|--hostname|--config)$/)) j += 2
          else j++
        }
        verb = tolower(token[j])
        gsub(/\\/, "", verb)
        gsub(/^["'"'"'`]+|["'"'"'`,]+$/, "", verb)
        if (cmd == "git" && verb == "push") {
          printf "git push"
          for (k = j + 1; k <= NF; k++) { part = token[k]; gsub(/\\/, "", part); printf " %s", part }
          print ""
        } else if (cmd == "gh" && (verb == "pr" || verb == "api")) {
          printf "gh %s", verb
          for (k = j + 1; k <= NF; k++) { part = token[k]; gsub(/\\/, "", part); printf " %s", part }
          print ""
        }
      }
      if (ansi_command || (has_ansi && recognized_cli)) print "forbidden ansi-c command token"
      delete token; delete raw
    }
  '
}

source_has_ansi_construct() {
  awk '
    BEGIN { RS=";" }
    {
      sq=0; dq=0; esc=0
      for (i=1; i<=length($0); i++) {
        c=substr($0,i,1); nextc=substr($0,i+1,1)
        if (esc) { esc=0; continue }
        if (c=="\\" && !sq) { esc=1; continue }
        if (c=="$" && nextc=="\047" && !sq && !dq) { found=1; exit }
        if (c=="\047" && !dq) { sq=!sq; continue }
        if (c=="\042" && !sq) { dq=!dq; continue }
      }
    }
    END { exit(found ? 0 : 1) }
  ' <<<"$1"
}

source_has_forbidden_authority() {
  local source="$1" reject_ansi="${2:-0}" canonical
  [[ "${reject_ansi}" == "1" ]] && source_has_ansi_construct "${source}" && return 0
  local api_forbidden='github(\.rest)?\.pulls\.(merge|updateBranch)|pulls\.(merge|updateBranch)|mergePullRequest|enablePullRequestAutoMerge|updatePullRequestBranch'
  local cli_forbidden='gh[[:space:]]+pr[[:space:]]+merge|gh[[:space:]]+api[^#;|&]*/pulls/[^/[:space:]]+/(merge|update-branch)|git[[:space:]]+push[^#;|&]*(HEAD:)?(refs/heads/)?trunk|git[[:space:]]+push[[:space:]]+[^[:space:];]+[[:space:]]+"?(HEAD:)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|git[[:space:]]+push[^#;|&]*[[:alnum:]_./-]+:(refs/heads/)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?'
  grep -Eiq "${api_forbidden}" <<<"${source}" && return 0
  canonical="$(canonical_cli_source <<<"${source}")"
  grep -Fq 'forbidden ansi-c command token' <<<"${canonical}" && return 0
  grep -Eiq "${cli_forbidden}" <<<"${canonical}"
}

scan_authorities() {
  local root="$1" workflows
  workflows="${root}/.github/workflows"
  [[ ! -e "${workflows}/pr-merge-train.yml" ]] || {
    echo "legacy merge authority still exists" >&2; return 1;
  }
  # The batch merge train was retired (simplify A4); GitHub ruleset auto-merge
  # is the only landing path, so no repository workflow or script may merge.
  [[ ! -e "${workflows}/merge-train.yml" && ! -e "${root}/scripts/ci/merge-train" ]] || {
    echo "retired merge-train authority still exists" >&2; return 1;
  }

  # Dispatching the live-capable train is itself a merge authority regardless
  # of how train_apply is spelled or valued. Canonical locations are separately
  # allowlisted; every other executable source must be unable to wake this flow.
  local live_dispatch="gh[[:space:]]+api[^#;|&]*/actions/workflows/([^/[:space:]]*/)?merge-train\.yml/dispatches|createWorkflowDispatch[^)]*merge-train\.yml"
  # A variable workflow selector cannot be proven non-authoritative statically.
  # Reject it everywhere except the explicit dispatch authority allowlist.
  # For JS calls, quoted and computed workflow_id keys are equivalent to the
  # bare key and must not hide a dynamic selector. The REST dispatch endpoint's
  # workflow_id path segment accepts either a filename or a numeric workflow
  # ID (GitHub API docs), and gh api never resolves that ID to a name for us,
  # so a numeric or interpolated segment is just as unprovable as a bare CLI
  # variable and must be rejected the same way.
  local dynamic_dispatch="createWorkflowDispatch[^)]*(\[[[:space:]]*[\"']workflow_id[\"'][[:space:]]*\]|[\"']?workflow_id[\"']?)[[:space:]]*:[[:space:]]*[$]?[A-Za-z_][A-Za-z0-9_]*|createWorkflowDispatch[^)]*[{,][[:space:]]*workflow_id[[:space:],}]|gh[[:space:]]+api[^#;|&]*/actions/workflows/([0-9]+|[^/[:space:]]*[$][^/[:space:]]*)/dispatches"
  local file rel source found=0 candidates reject_ansi
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
    # This workflow may request ruleset-governed auto-merge, never merge directly.
    if [[ "${rel}" == ".github/workflows/auto-merge-ready.yml" ]]; then
      source="${source//enablePullRequestAutoMerge/allowedRulesetAutoMergeRequest}"
    fi
    reject_ansi=0
    case "${rel}" in *.sh|*.bash|*.zsh|*.yml|*.yaml) reject_ansi=1 ;; esac
    # Report the ANSI-C rejection in its own words. It is a blanket ban on
    # $'...' in shell sources -- the construct can spell a command verb past
    # this scanner -- so a file with no merge primitive at all can trip it, and
    # "merge-capable primitive" sends the reader hunting for a push that is not
    # there.
    if [[ "${reject_ansi}" == "1" ]] && source_has_ansi_construct "${source}"; then
      echo "forbidden ANSI-C quoting (\$'...') in ${rel}; rewrite it without \$'...'" >&2; found=1
    elif source_has_forbidden_authority "${source}" "${reject_ansi}"; then
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
    echo "merge authority exists in repository source; only ruleset auto-merge may land" >&2; return 1;
  }
}

self_test() {
  local scratch; scratch="$(mktemp -d)"; trap 'rm -rf "${scratch}"' RETURN
  mkdir -p "${scratch}/.github/workflows"
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
  printf 'jobs:\n  enable:\n    steps:\n      - run: enablePullRequestAutoMerge(input: {})\n' >"${scratch}/.github/workflows/auto-merge-ready.yml"
  scan_authorities "${scratch}" || { echo "safe fixture rejected" >&2; return 1; }
  printf '      - run: mergePullRequest(input: {})\n' >>"${scratch}/.github/workflows/auto-merge-ready.yml"
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "direct merge escaped auto-merge exception" >&2; return 1; }
  sed -i '$d' "${scratch}/.github/workflows/auto-merge-ready.yml"

  printf 'jobs:\n  train:\n    steps:\n      - run: echo retired\n' >"${scratch}/.github/workflows/merge-train.yml"
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "retired merge-train workflow escaped" >&2; return 1; }
  rm -f "${scratch}/.github/workflows/merge-train.yml"

  local fixture n=0
  fixtures=(
    'github.rest.pulls.merge({owner, repo, pull_number: 1})'
    'github.pulls.merge({owner, repo, pull_number: 1})'
    'github.rest.pulls.updateBranch({owner, repo, pull_number: 1})'
    'pulls.updateBranch({pull_number: 1})'
    'mergePullRequest(input: {pullRequestId: $id})'
    'enablePullRequestAutoMerge(input: {pullRequestId: $id})'
    'updatePullRequestBranch(input: {pullRequestId: $id})'
    'gh pr merge 1 --merge'
    'gh -R o/r pr merge 1 --merge'
    'gh api --method PUT repos/o/r/pulls/1/merge'
    $'gh api \\\n      --method PUT \\\n      repos/o/r/pulls/${pr}/merge'
    $'gh pr \\\n      merge 1 --merge'
    'git push origin HEAD:trunk'
    'git push origin HEAD:refs/heads/trunk'
    'git -C /tmp/repo push origin HEAD:trunk'
    '/usr/bin/GIT.EXE push origin HEAD:trunk'
    'C:\\tools\\Gh.ExE pr merge 1 --merge'
    $'g'"'"'h'"'"' p'"'"'r'"'"' m'"'"'e'"'"'r'"'"'g'"'"'e 1 --merge'
    $'/usr/bin/g'"'"'i'"'"'t push origin HEAD:trunk'
    $'g$'"'"'h'"'"' p$'"'"'r'"'"' m$'"'"'erge'"'"' 1 --merge'
    $'$'"'"'\\x67\\x68'"'"' $'"'"'\\x70\\x72'"'"' $'"'"'\\x6d\\x65\\x72\\x67\\x65'"'"' 1 --merge'
    $'$'"'"'\\147\\150'"'"' $'"'"'\\160\\162'"'"' $'"'"'\\155\\145\\162\\147\\145'"'"' 1 --merge'
    $'$'"'"'\\x67\\x68\\n'"'"' pr merge 1 --merge'
    $'command $'"'"'\\x67\\x68'"'"' pr merge 1 --merge'
    $'wrapped=$'"'"'\\x67\\x68'"'"'\ncommand "$wrapped" pr merge 1 --merge'
    'g\h p\r m\e\r\g\e 1 --merge'
    $'printf "literal # still data"; gh pr merge 1 --merge'
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
    'gh workflow run 123456 -f train_apply=true'
    'gh workflow run --repo honua-io/honua-server 123456 -f train_apply=true'
    'gh api --method POST repos/o/r/actions/workflows/123456/dispatches -f inputs[train_apply]=true'
    'gh api --method POST repos/o/r/actions/workflows/${flow_id}/dispatches -f inputs[train_apply]=true'
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
  n=$((n + 1))
  cat >"${scratch}/.github/workflows/other.yml" <<'YAML'
jobs:
  bad:
    steps:
      - run: |
          $\
'\x67\x68' pr merge 1 --merge
YAML
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "continued ANSI-C fixture escaped" >&2; return 1; }
  rm -f "${scratch}/.github/workflows/other.yml"
  echo "single-authority fixtures: ${n} forbidden, 1 safe, 1 retired train"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_ROOT:-${repo_root}}"
echo "single merge authority: GitHub ruleset auto-merge (no repository merge authority)"
