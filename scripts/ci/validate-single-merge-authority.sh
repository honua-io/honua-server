#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

is_allowlisted() {
  case "$1" in
    scripts/ci/merge-train/land.sh|scripts/ci/merge-train/fixtures/validate-merge-train.sh|scripts/ci/validate-single-merge-authority.sh)
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
  local api_forbidden='github(\.rest)?\.pulls\.(merge|updateBranch)|pulls\.(merge|updateBranch)|mergePullRequest|updatePullRequestBranch'
  local cli_forbidden='gh[[:space:]]+pr[[:space:]]+merge|gh[[:space:]]+api[^#;|&]*/pulls/[^/[:space:]]+/(merge|update-branch)|git[[:space:]]+push[^#;|&]*(HEAD:)?(refs/heads/)?trunk|git[[:space:]]+push[[:space:]]+[^[:space:];]+[[:space:]]+"?(HEAD:)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?|git[[:space:]]+push[^#;|&]*[[:alnum:]_./-]+:(refs/heads/)?\$\{?[A-Za-z_][A-Za-z0-9_]*\}?'
  grep -Eiq "${api_forbidden}" <<<"${source}" && return 0
  canonical="$(canonical_cli_source <<<"${source}")"
  grep -Fq 'forbidden ansi-c command token' <<<"${canonical}" && return 0
  grep -Eiq "${cli_forbidden}" <<<"${canonical}"
}

function_source() {
  local file="$1" function_name="$2"
  normalized_function_source "${file}" | awk -v wanted="${function_name}" '
    function declaration(s, is_function, name, rest) {
      sub(/^[[:space:]]*/, "", s)
      is_function = sub(/^function[[:space:]]+/, "", s)
      if (match(s, /^[A-Za-z_][A-Za-z0-9_]*/) == 0) return ""
      name = substr(s, RSTART, RLENGTH); rest = substr(s, RLENGTH + 1)
      sub(/^[[:space:]]*/, "", rest)
      if (rest ~ /^\([[:space:]]*\)[[:space:]]*\{/) return name
      if (is_function && rest ~ /^\{/) return name
      return ""
    }
    function brace_delta(s, i, c, previous, sq, dq, esc, delta) {
      sq = 0; dq = 0; esc = 0; delta = 0
      for (i = 1; i <= length(s); i++) {
        c = substr(s, i, 1); previous = (i == 1 ? "" : substr(s, i - 1, 1))
        if (esc) { esc = 0; continue }
        if (c == "\\" && !sq) { esc = 1; continue }
        if (c == "\047" && !dq) { sq = !sq; continue }
        if (c == "\042" && !sq) { dq = !dq; continue }
        if (c == "#" && !sq && !dq && (i == 1 || previous ~ /[[:space:]]/)) break
        if (!sq && !dq && c == "{") delta++
        if (!sq && !dq && c == "}") delta--
      }
      return delta
    }
    { declared = declaration($0) }
    declared == wanted && !found { found=1; depth=0 }
    found && !complete {
      print
      depth += brace_delta($0)
      if (depth == 0) complete=1
    }
  '
}

# Join only a syntactically complete shell function header with a following
# brace-only line. This is lexical normalization; no source is evaluated.
normalized_function_source() {
  logical_shell_source "$1" | awk '
    function header(s) {
      sub(/^[[:space:]]*/, "", s)
      return s ~ /^(function[[:space:]]+)?[A-Za-z_][A-Za-z0-9_]*[[:space:]]*(\([[:space:]]*\))?[[:space:]]*$/ &&
        (s ~ /^function[[:space:]]+/ || s ~ /\([[:space:]]*\)[[:space:]]*$/)
    }
    NR == 1 { previous = $0; next }
    {
      current = $0
      if (header(previous) && current ~ /^[[:space:]]*\{/) {
        print previous " " current
        previous = ""
        next
      }
      if (previous != "") print previous
      previous = current
    }
    END { if (previous != "") print previous }
  '
}

# Prove the post-CAS finalizer and every same-file helper reachable from it are
# observation-only with respect to trunk. This closes helper-indirection gaps
# while keeping the one intentional pre-CAS/CAS push authority in land.sh.
scan_post_cas_call_graph() {
  local file="$1" root="train_finalize_landed_members" current body candidate heredoc_surface
  local -a functions queue
  mapfile -t functions < <(normalized_function_source "${file}" | awk '
    function declaration(s, is_function, name, rest) {
      sub(/^[[:space:]]*/, "", s)
      is_function = sub(/^function[[:space:]]+/, "", s)
      if (match(s, /^[A-Za-z_][A-Za-z0-9_]*/) == 0) return ""
      name = substr(s, RSTART, RLENGTH); rest = substr(s, RLENGTH + 1)
      sub(/^[[:space:]]*/, "", rest)
      if (rest ~ /^\([[:space:]]*\)[[:space:]]*\{/) return name
      if (is_function && rest ~ /^\{/) return name
      return ""
    }
    { name = declaration($0); if (name != "") print name }
  ')
  queue=("${root}")
  declare -A seen=()
  while [[ "${#queue[@]}" -gt 0 ]]; do
    current="${queue[0]}"; queue=("${queue[@]:1}")
    [[ -z "${seen[${current}]:-}" ]] || continue
    seen["${current}"]=1
    body="$(function_source "${file}" "${current}")"
    [[ -n "${body}" ]] || { echo "missing post-CAS function ${current}" >&2; return 1; }
    heredoc_surface="${body//<<</ }"
    if grep -Fq '<<' <<<"${heredoc_surface}"; then
      echo "post-CAS heredoc reachable through ${current}; refusing ambiguous call-graph proof" >&2
      return 1
    fi
    if source_has_forbidden_authority "${body}" 1; then
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

# smart-ci may publish only an immutable train batch ref to the same remote ref.
# Prove that narrow primitive structurally rather than allowlisting the file.
scan_smart_ci_authority() {
  local file="$1" body source canonical body_canonical non_push line
  local -a pushes body_pushes
  body="$(function_source "${file}" train_smart_ci_run)"
  [[ -n "${body}" ]] || { echo "missing train_smart_ci_run" >&2; return 1; }
  grep -Fq '[[ "${batch}" != train/batch/* && "${batch}" != train/attribute-probe/* ]]' <<<"${body}" || {
    echo "smart-ci batch namespace guard missing" >&2; return 1;
  }
  source="$(normalized_source "${file}")"
  canonical="$(canonical_cli_source <<<"${source}")"
  body_canonical="$(canonical_cli_source <<<"${body}")"
  mapfile -t pushes < <(grep -Ei '^git[[:space:]]+push([[:space:]]|$)' <<<"${canonical}" || true)
  mapfile -t body_pushes < <(grep -Ei '^git[[:space:]]+push([[:space:]]|$)' <<<"${body_canonical}" || true)
  [[ "${#pushes[@]}" == 2 && "${#body_pushes[@]}" == 2 ]] || {
    echo "smart-ci must contain exactly two pushes inside train_smart_ci_run" >&2; return 1;
  }
  for line in "${pushes[@]}"; do
    grep -Eq '^git[[:space:]]+push[[:space:]]+[^[:space:]]+[[:space:]]+\$\{batch\}:\$\{batch\}[[:space:]]*$' <<<"${line}" || {
      echo "smart-ci push is not a batch self-ref push" >&2; return 1;
    }
  done
  non_push="$(grep -Eiv '^git[[:space:]]+push([[:space:]]|$)' <<<"${canonical}" || true)"
  source_has_forbidden_authority "${non_push}" 0 && {
    echo "smart-ci contains another merge-capable primitive" >&2; return 1;
  }
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

  # Dispatching the live-capable train is itself a merge authority regardless
  # of how train_apply is spelled or valued. Canonical locations are separately
  # allowlisted; every other executable source must be unable to wake this flow.
  local live_dispatch="gh[[:space:]]+workflow[[:space:]]+run[[:space:]]+([\"']?([^[:space:]\"']*/)?merge-train\.yml[\"']?|[\"']Merge[[:space:]]+Train[\"'])|gh[[:space:]]+api[^#;|&]*/actions/workflows/([^/[:space:]]*/)?merge-train\.yml/dispatches|createWorkflowDispatch[^)]*merge-train\.yml"
  # A variable workflow selector cannot be proven non-authoritative statically.
  # Reject it everywhere except the explicit dispatch authority allowlist.
  # Outside the allowlist, reject variable selectors and any invocation whose
  # selector is preceded by a flag. Inherited flags (-R/--repo, --hostname, and
  # future flags) make positional parsing unsafe; static selectors in the first
  # position remain provably non-authoritative unless live_dispatch matches.
  # For JS calls, quoted and computed workflow_id keys are equivalent to the
  # bare key and must not hide a dynamic selector.
  local dynamic_dispatch="gh[[:space:]]+workflow[[:space:]]+run[[:space:]]+(-[^[:space:];]*|[^[:space:];]*[$][^[:space:];]*)|createWorkflowDispatch[^)]*(\[[[:space:]]*[\"']workflow_id[\"'][[:space:]]*\]|[\"']?workflow_id[\"']?)[[:space:]]*:[[:space:]]*[$]?[A-Za-z_][A-Za-z0-9_]*|createWorkflowDispatch[^)]*[{,][[:space:]]*workflow_id[[:space:],}]"
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
    if [[ "${rel}" == "scripts/ci/merge-train/smart-ci.sh" ]]; then
      scan_smart_ci_authority "${file}" || found=1
      continue
    fi
    source="$(normalized_source "${file}")"
    reject_ansi=0
    case "${rel}" in *.sh|*.bash|*.zsh|*.yml|*.yaml) reject_ansi=1 ;; esac
    if source_has_forbidden_authority "${source}" "${reject_ansi}"; then
      echo "forbidden merge-capable primitive in ${rel}" >&2; found=1
    fi
    if ! is_dispatch_allowlisted "${rel}" && grep -Eiq "${live_dispatch}" <<<"${source}"; then
      echo "forbidden live merge-train dispatch in ${rel}" >&2; found=1
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
  cat >"${scratch}/scripts/ci/merge-train/land.sh" <<'SH'
train_land_pr_info() { gh pr view "$1"; }
train_finalize_landed_members() { train_land_pr_info "$1"; }
train_land() { git push origin batch:trunk; }
SH
  cat >"${scratch}/scripts/ci/merge-train/smart-ci.sh" <<'SH'
train_smart_ci_run() {
  local batch="$1"
  if [[ "${batch}" != train/batch/* && "${batch}" != train/attribute-probe/* ]]; then return 1; fi
  train_side_effect git push "${TRAIN_REMOTE}" "${batch}:${batch}"
  git push "${TRAIN_REMOTE}" "${batch}:${batch}"
}
SH
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
      # `git push origin HEAD:trunk` is documentation, not executable.
      - run: git push origin HEAD:refs/heads/automation/report-${GITHUB_RUN_ID}
YAML
  scan_authorities "${scratch}" || { echo "safe fixture rejected" >&2; return 1; }

  printf '\ngit push origin HEAD:trunk\n' >>"${scratch}/scripts/ci/merge-train/smart-ci.sh"
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "smart-ci trunk push escaped" >&2; return 1; }
  sed -i '$d' "${scratch}/scripts/ci/merge-train/smart-ci.sh"

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
  cat >"${scratch}/scripts/ci/merge-train/land.sh" <<'SH'
  function train_post_cas_writer { gh -R o/r pr merge 1 --merge; }
  train_finalize_landed_members ()
  {
    nested_same_line() { gh pr merge 1 --merge; }
    nested_next_line()
    { git push origin HEAD:trunk; }
    nested_same_line
    nested_next_line
    train_post_cas_writer
  }
train_land() { git push origin batch:trunk; }
SH
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "post-CAS helper indirection escaped" >&2; return 1; }
  local heredoc_fixture
  for heredoc_fixture in \
    $'cat <<EOF\n}\nEOF' \
    $'cat <<'"'"'EOF'"'"'\n}\nEOF' \
    $'cat <<-EOF\n\t}\n\tEOF'; do
    cat >"${scratch}/scripts/ci/merge-train/land.sh" <<SH
train_finalize_landed_members() {
  ${heredoc_fixture}
  hidden_writer
}
hidden_writer() { gh pr merge 1 --merge; }
train_land() { git push origin batch:trunk; }
SH
    scan_authorities "${scratch}" >/dev/null 2>&1 \
      && { echo "post-CAS heredoc fixture escaped" >&2; return 1; }
  done
  cat >"${scratch}/scripts/ci/merge-train/land.sh" <<'SH'
train_finalize_landed_members() {
  cat <\
<EOF
}
EOF
  hidden_writer
}
hidden_writer() { gh pr merge 1 --merge; }
train_land() { git push origin batch:trunk; }
SH
  scan_authorities "${scratch}" >/dev/null 2>&1 \
    && { echo "post-CAS continued heredoc fixture escaped" >&2; return 1; }
  for heredoc_fixture in \
    $'cat <<\\EOF\n}\nEOF' \
    $'cat <<$'"'"'EOF'"'"'\n}\nEOF'; do
    cat >"${scratch}/scripts/ci/merge-train/land.sh" <<SH
train_finalize_landed_members() {
  ${heredoc_fixture}
}
train_land() { git push origin batch:trunk; }
SH
    scan_authorities "${scratch}" >/dev/null 2>&1 \
      && { echo "post-CAS escaped heredoc fixture escaped" >&2; return 1; }
  done
  echo "single-authority fixtures: ${n} forbidden, 1 safe, 1 scoped smart-ci, 1 transitive, 6 heredoc"
}

if [[ "${1:-}" == "--self-test" ]]; then self_test; exit; fi
scan_authorities "${MERGE_AUTHORITY_ROOT:-${repo_root}}"
echo "single merge authority: merge-train.yml"
