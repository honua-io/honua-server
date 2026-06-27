#!/usr/bin/env bash
# Optimistic trunk merge-coordinator — Phase 1 deterministic decision engine.
# Owned by ADR-0055. NO LLM: every decision here is a pure function of the
# latest CI run state for a PR's head sha plus the flake-signature config.
#
# It replaces the human who ran `gh` to babysit PRs. For each open non-draft
# PR -> trunk it fetches the latest CI run for the head sha, buckets every
# FAILED job into {real-gate, affected-shard, known-flake}, and decides ONE
# action:
#
#   skip          latest run still in-progress/queued — re-evaluate next tick.
#   merge         real gates green AND affected shards green-IGNORING-flakes AND
#                 the PR is mergeable -> squash-merge. Merges THROUGH known
#                 flakes with NO rerun (the throughput win).
#   escalate      a REAL gate failed (build/format/drift/foundation/router) OR a
#                 NON-flake shard failed OR the rerun cap was hit on a cancelled
#                 run -> label `ci-needs-attention` + comment the failed jobs.
#                 NEVER auto-merges.
#   rerun-full    the latest run was CANCELLED (concurrency supersede / runner
#                 starvation, NOT a code signal) AND rerun-count(head-sha) <
#                 cap(2) -> re-run the whole run once. NEVER rerun for flakes.
#   freshen-once  PR is BEHIND trunk and has not already been freshened onto the
#                 current trunk tip -> update the branch ONCE. Never re-freshen a
#                 PR that is already current (tracked via the compare `ahead`
#                 status, NOT a re-push) — re-freshening churns CI because
#                 ci.yml has concurrency cancel-in-progress.
#
# The decision core (decide_action) is a pure function over a small JSON state
# document so scripts/ci/validate-merge-coordinator.sh can unit-test it with
# mock states and zero network. The gh/gh-api plumbing lives in the collectors
# and the actuators, which are skipped entirely in dry-run.
#
# Usage:
#   merge-coordinator.sh --pr <n>          coordinate a single PR
#   merge-coordinator.sh --sweep           coordinate every open non-draft PR
#   merge-coordinator.sh --decide-only <state.json>   print the decision for a
#                                          pre-built state document (used by the
#                                          unit tests; no network)
#
# Environment:
#   COORDINATOR_DRY_RUN=true   log decisions, take NO action (merge/label/rerun/
#                              freshen are all logged, not executed). DEFAULT in
#                              CI until the human flips it live.
#   COORDINATOR_RERUN_CAP      max full reruns per head sha (default 2).
#   GH_REPO / GITHUB_REPOSITORY  owner/name (default honua-io/honua-server).
#
# Exit codes: 0 ok; 2 usage/config error.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
SIGNATURES_FILE="${COORDINATOR_SIGNATURES_FILE:-${SCRIPT_DIR}/ci-flake-signatures.json}"

DRY_RUN="${COORDINATOR_DRY_RUN:-true}"
RERUN_CAP="${COORDINATOR_RERUN_CAP:-2}"
REPO="${GH_REPO:-${GITHUB_REPOSITORY:-honua-io/honua-server}}"

ATTENTION_LABEL="ci-needs-attention"

# ----------------------------------------------------------------------------
# Job-name classification. Names come from .github/workflows/ci.yml job `name:`
# fields (verified against a live CI run). REAL GATES are the land-fast set from
# ADR-0055: a failure in any of these blocks the merge and escalates, regardless
# of flake signatures. Everything matching the Server-Tests wrapper is an
# affected-shard job (subject to flake bucketing). Other jobs (browser/python/
# js/mcp/docker/aot/postgres-compat/package-compat) are NON-gating for the
# land-fast decision — they are heavy cross-protocol checks whose fallout is
# caught by post-merge trunk convergence, not by blocking the merge.
# ----------------------------------------------------------------------------

# Extended-regex matching a job name that is a BLOCKING real gate.
REAL_GATE_REGEX='^(Build & Format Check|CI Router Validation|\.NET Foundation Tests|Detect Targeted Server-Tests Shards|PR Mergeability & Freshness|Validate PR Template Compliance)$'

# Extended-regex matching an affected-shard job.
SHARD_REGEX='^Server Tests \(.*\)$'

log() { printf '%s\n' "$*" >&2; }

require_jq() {
  command -v jq >/dev/null 2>&1 || { log "jq is required"; exit 2; }
}

# ----------------------------------------------------------------------------
# PURE DECISION CORE
#
# decide_action <state-json>  ->  prints "<action>\t<reason>" on stdout.
#
# The state document schema (all fields optional; sane defaults applied):
# {
#   "run_status":    "completed" | "in_progress" | "queued" | "",   # latest run
#   "run_conclusion":"success" | "failure" | "cancelled" | "" ,     # latest run
#   "mergeable":     true | false,         # github mergeable (no conflicts)
#   "behind":        true | false,         # PR branch behind trunk
#   "freshened":     true | false,         # already freshened onto current trunk
#   "rerun_count":   <int>,                # full reruns already spent on this sha
#   "jobs": [ {"name": "...", "conclusion": "success|failure|cancelled|skipped",
#              "flake": true|false } ]      # flake=true when a failed job matched
#                                           # a signature (computed by the caller)
# }
#
# Precedence (first match wins):
#   1. run in-progress/queued            -> skip
#   2. run cancelled                     -> rerun-full (if under cap) else escalate
#   3. any REAL-GATE job failed          -> escalate
#   4. any AFFECTED-SHARD job failed AND not a flake -> escalate
#   5. PR behind trunk AND not freshened -> freshen-once
#   6. PR not mergeable (conflicts)      -> escalate
#   7. otherwise (gates green, shards green-ignoring-flakes, mergeable) -> merge
#      (merge-through-flake when at least one failed job was a flake)
# ----------------------------------------------------------------------------
decide_action() {
  local state="$1"
  local cap="${2:-$RERUN_CAP}"

  local run_status run_conclusion mergeable behind freshened rerun_count
  run_status="$(jq -r '.run_status // ""' <<<"$state")"
  run_conclusion="$(jq -r '.run_conclusion // ""' <<<"$state")"
  mergeable="$(jq -r 'if .mergeable == false then "false" else "true" end' <<<"$state")"
  behind="$(jq -r 'if .behind == true then "true" else "false" end' <<<"$state")"
  freshened="$(jq -r 'if .freshened == true then "true" else "false" end' <<<"$state")"
  rerun_count="$(jq -r '.rerun_count // 0' <<<"$state")"

  # 1. Still running — wait.
  if [[ "$run_status" == "in_progress" || "$run_status" == "queued" || ( "$run_status" != "completed" && -z "$run_conclusion" ) ]]; then
    printf 'skip\tlatest CI run is %s; re-evaluate next tick\n' "${run_status:-pending}"
    return 0
  fi

  # 2. Cancelled run is NOT a code signal (concurrency supersede / runner
  #    starvation). Re-run the whole thing once; never count a cancel as a fail.
  if [[ "$run_conclusion" == "cancelled" ]]; then
    if (( rerun_count < cap )); then
      printf 'rerun-full\tlatest run cancelled (rerun %d/%d)\n' "$((rerun_count + 1))" "$cap"
    else
      printf 'escalate\tlatest run cancelled and rerun cap (%d) reached — needs a human\n' "$cap"
    fi
    return 0
  fi

  # Bucket failed jobs.
  local failed_real failed_shard_real failed_flake
  failed_real="$(jq -r --arg re "$REAL_GATE_REGEX" \
    '[.jobs[]? | select((.conclusion=="failure") and (.name|test($re)))] | length' <<<"$state")"
  failed_shard_real="$(jq -r --arg sh "$SHARD_REGEX" \
    '[.jobs[]? | select((.conclusion=="failure") and (.name|test($sh)) and (.flake != true))] | length' <<<"$state")"
  failed_flake="$(jq -r \
    '[.jobs[]? | select((.conclusion=="failure") and (.flake == true))] | length' <<<"$state")"

  # 3. Real gate failed — never merge, escalate.
  if (( failed_real > 0 )); then
    local names
    names="$(jq -r --arg re "$REAL_GATE_REGEX" \
      '[.jobs[]? | select((.conclusion=="failure") and (.name|test($re))) | .name] | join(", ")' <<<"$state")"
    printf 'escalate\treal-gate failure: %s\n' "$names"
    return 0
  fi

  # 4. A non-flake affected-shard failed — real test regression, escalate.
  if (( failed_shard_real > 0 )); then
    local names
    names="$(jq -r --arg sh "$SHARD_REGEX" \
      '[.jobs[]? | select((.conclusion=="failure") and (.name|test($sh)) and (.flake != true)) | .name] | join(", ")' <<<"$state")"
    printf 'escalate\tnon-flake shard failure: %s\n' "$names"
    return 0
  fi

  # 5. Behind trunk and not yet freshened -> freshen exactly once.
  if [[ "$behind" == "true" && "$freshened" != "true" ]]; then
    printf 'freshen-once\tPR behind trunk; updating branch once\n'
    return 0
  fi

  # 6. Conflicts (can't merge, freshen won't help) -> escalate.
  if [[ "$mergeable" == "false" ]]; then
    printf 'escalate\tPR not mergeable (conflicts) — needs a human\n'
    return 0
  fi

  # 7. Land-fast: gates green, shards green-ignoring-flakes, mergeable.
  if (( failed_flake > 0 )); then
    local sigs
    sigs="$(jq -r '[.jobs[]? | select((.conclusion=="failure") and (.flake==true)) | .name] | join(", ")' <<<"$state")"
    printf 'merge\tmerge-through-flake (ignored: %s)\n' "$sigs"
  else
    printf 'merge\tall real gates and affected shards green\n'
  fi
  return 0
}

# ----------------------------------------------------------------------------
# FLAKE BUCKETING — given a failed job name + its log text, is it a known flake?
# is_known_flake <job-name> <log-text> -> "true"/"false"
# ----------------------------------------------------------------------------
is_known_flake() {
  local job_name="$1" log_text="$2"
  local n
  n="$(jq -r --arg name "$job_name" --arg log "$log_text" '
    [ .signatures[]
      | select(($name | test(.shardPattern)) and ($log | test(.logPattern)))
    ] | length' "$SIGNATURES_FILE" 2>/dev/null || echo 0)"
  [[ "${n:-0}" -gt 0 ]] && echo "true" || echo "false"
}

flake_label_for() {
  local job_name="$1" log_text="$2"
  jq -r --arg name "$job_name" --arg log "$log_text" '
    [ .signatures[]
      | select(($name | test(.shardPattern)) and ($log | test(.logPattern)))
      | .label ] | first // ""' "$SIGNATURES_FILE" 2>/dev/null || echo ""
}

# ----------------------------------------------------------------------------
# COLLECTORS — build the state document for a PR from live gh data.
# ----------------------------------------------------------------------------

# Latest CI run id for a head sha (most recent created), or empty.
latest_run_id() {
  local head_sha="$1"
  gh api "repos/${REPO}/actions/runs?head_sha=${head_sha}&per_page=20" \
    --jq '[.workflow_runs[] | select(.name=="CI")] | sort_by(.created_at) | last | .id // empty' 2>/dev/null
}

# Download a job's log text (best-effort; empty on failure).
job_log_text() {
  local run_id="$1" job_id="$2"
  gh api "repos/${REPO}/actions/jobs/${job_id}/logs" 2>/dev/null || true
}

# rerun-count for a head sha is tracked via a `ci-rerun-N` label on the PR so it
# resets automatically when a new commit lands (label removed at freshen/merge,
# and a new sha simply has no such label yet).
rerun_count_from_labels() {
  local labels_json="$1"
  jq -r '[.[] | select(.name|test("^ci-rerun-[0-9]+$")) | (.name|sub("^ci-rerun-";"")|tonumber)] | max // 0' <<<"$labels_json"
}

# Build the full state JSON for one PR. Echoes the state document.
build_state_for_pr() {
  local pr="$1"
  local meta
  meta="$(gh api "repos/${REPO}/pulls/${pr}" 2>/dev/null)" || { log "PR ${pr}: cannot fetch metadata"; return 1; }

  local head_sha mergeable
  head_sha="$(jq -r '.head.sha' <<<"$meta")"
  mergeable="$(jq -r 'if .mergeable == false then false else true end' <<<"$meta")"

  # behind? compare base...head; "behind"/"diverged" means PR is behind trunk.
  local cmp behind
  cmp="$(gh api "repos/${REPO}/compare/trunk...${head_sha}" --jq '.status' 2>/dev/null || echo "")"
  if [[ "$cmp" == "behind" || "$cmp" == "diverged" ]]; then behind=true; else behind=false; fi

  local labels_json rerun_count freshened
  labels_json="$(jq -c '[.labels[] | {name: .name}]' <<<"$meta")"
  rerun_count="$(rerun_count_from_labels "$labels_json")"
  # freshened: we recorded a freshen for THIS sha via a `ci-freshened` label that
  # is cleared on every new commit. Presence => already freshened onto current
  # trunk; do not re-freshen.
  if jq -e '.[] | select(.name=="ci-freshened")' <<<"$labels_json" >/dev/null 2>&1; then
    freshened=true
  else
    freshened=false
  fi

  local run_id run_status run_conclusion jobs_json
  run_id="$(latest_run_id "$head_sha")"
  if [[ -z "$run_id" ]]; then
    run_status="" ; run_conclusion="" ; jobs_json="[]"
  else
    local run_json
    run_json="$(gh api "repos/${REPO}/actions/runs/${run_id}" 2>/dev/null)"
    run_status="$(jq -r '.status // ""' <<<"$run_json")"
    run_conclusion="$(jq -r '.conclusion // ""' <<<"$run_json")"

    # Collect jobs; for each FAILED job, download its log and bucket flake-ness.
    local raw_jobs
    raw_jobs="$(gh api "repos/${REPO}/actions/runs/${run_id}/jobs?per_page=100" \
      --jq '[.jobs[] | {name, status, conclusion, id}]' 2>/dev/null || echo '[]')"

    jobs_json='[]'
    local count
    count="$(jq 'length' <<<"$raw_jobs")"
    local i
    for (( i=0; i<count; i++ )); do
      local jname jconcl jid flake="false" flabel=""
      jname="$(jq -r ".[$i].name" <<<"$raw_jobs")"
      jconcl="$(jq -r ".[$i].conclusion // \"\"" <<<"$raw_jobs")"
      jid="$(jq -r ".[$i].id" <<<"$raw_jobs")"
      if [[ "$jconcl" == "failure" ]]; then
        local logtxt
        logtxt="$(job_log_text "$run_id" "$jid")"
        flake="$(is_known_flake "$jname" "$logtxt")"
        if [[ "$flake" == "true" ]]; then
          flabel="$(flake_label_for "$jname" "$logtxt")"
        fi
      fi
      jobs_json="$(jq -c --arg n "$jname" --arg c "$jconcl" --argjson f "$flake" --arg l "$flabel" \
        '. + [{name:$n, conclusion:$c, flake:$f, flake_label:$l}]' <<<"$jobs_json")"
    done
  fi

  jq -nc \
    --arg pr "$pr" \
    --arg head "$head_sha" \
    --arg rid "${run_id:-}" \
    --arg rs "$run_status" \
    --arg rc "$run_conclusion" \
    --argjson merg "$mergeable" \
    --argjson beh "$behind" \
    --argjson fr "$freshened" \
    --argjson rerun "$rerun_count" \
    --argjson jobs "$jobs_json" \
    '{pr:($pr|tonumber), head_sha:$head, run_id:$rid, run_status:$rs, run_conclusion:$rc,
      mergeable:$merg, behind:$beh, freshened:$fr, rerun_count:$rerun, jobs:$jobs}'
}

# ----------------------------------------------------------------------------
# ACTUATORS — perform (or, in dry-run, log) the chosen action.
# ----------------------------------------------------------------------------

ensure_label() {
  local name="$1" color="$2" desc="$3"
  gh api "repos/${REPO}/labels/${name}" >/dev/null 2>&1 && return 0
  gh api -X POST "repos/${REPO}/labels" -f "name=${name}" -f "color=${color}" -f "description=${desc}" >/dev/null 2>&1 || true
}

act_merge() {
  local pr="$1" reason="$2"
  if [[ "$DRY_RUN" == "true" ]]; then
    log "[dry-run] would squash-merge PR #${pr} (${reason})"
    return 0
  fi
  log "merging PR #${pr} (squash): ${reason}"
  gh pr merge "$pr" --repo "$REPO" --squash --auto=false || {
    log "PR #${pr}: merge failed"; return 1; }
}

act_escalate() {
  local pr="$1" reason="$2"
  if [[ "$DRY_RUN" == "true" ]]; then
    log "[dry-run] would escalate PR #${pr}: label ${ATTENTION_LABEL} + comment (${reason})"
    return 0
  fi
  ensure_label "$ATTENTION_LABEL" "B60205" "Coordinator: CI failed on a real gate/shard; needs a human."
  gh pr edit "$pr" --repo "$REPO" --add-label "$ATTENTION_LABEL" >/dev/null 2>&1 || true
  gh pr comment "$pr" --repo "$REPO" --body \
"Merge-coordinator: **escalated** — ${reason}

This PR will not auto-merge. A real gate or non-flake test failed, or the rerun cap was reached. Resolve the failure (or rebase) and the coordinator will re-evaluate on the next CI run." >/dev/null 2>&1 || true
}

act_rerun_full() {
  local pr="$1" run_id="$2" new_count="$3" reason="$4"
  if [[ "$DRY_RUN" == "true" ]]; then
    log "[dry-run] would re-run run ${run_id} for PR #${pr} and set ci-rerun-${new_count} (${reason})"
    return 0
  fi
  log "re-running run ${run_id} for PR #${pr}: ${reason}"
  gh api -X POST "repos/${REPO}/actions/runs/${run_id}/rerun" >/dev/null 2>&1 || \
    gh run rerun "$run_id" --repo "$REPO" >/dev/null 2>&1 || true
  # Track rerun-count via label so it resets on the next commit.
  ensure_label "ci-rerun-${new_count}" "FBCA04" "Coordinator: full rerun #${new_count} for current head sha."
  gh pr edit "$pr" --repo "$REPO" --add-label "ci-rerun-${new_count}" >/dev/null 2>&1 || true
}

act_freshen_once() {
  local pr="$1" reason="$2"
  if [[ "$DRY_RUN" == "true" ]]; then
    log "[dry-run] would update-branch PR #${pr} once and set ci-freshened (${reason})"
    return 0
  fi
  log "freshening PR #${pr}: ${reason}"
  gh api -X PUT "repos/${REPO}/pulls/${pr}/update-branch" >/dev/null 2>&1 || \
    log "PR #${pr}: update-branch failed (may need a token with write access)"
  ensure_label "ci-freshened" "0E8A16" "Coordinator: branch freshened onto current trunk (cleared on new commits)."
  gh pr edit "$pr" --repo "$REPO" --add-label "ci-freshened" >/dev/null 2>&1 || true
}

# ----------------------------------------------------------------------------
# Orchestration for one PR: build state, decide, act. Echoes a TSV decision row:
#   pr<TAB>head<TAB>decision<TAB>reason
# ----------------------------------------------------------------------------
coordinate_pr() {
  local pr="$1"
  local state
  if ! state="$(build_state_for_pr "$pr")"; then
    printf '%s\t%s\t%s\t%s\n' "$pr" "-" "error" "could not build state"
    return 0
  fi

  local decision_line action reason
  decision_line="$(decide_action "$state")"
  action="$(cut -f1 <<<"$decision_line")"
  reason="$(cut -f2- <<<"$decision_line")"

  local head_sha run_id rerun_count
  head_sha="$(jq -r '.head_sha' <<<"$state")"
  run_id="$(jq -r '.run_id // ""' <<<"$state")"
  rerun_count="$(jq -r '.rerun_count // 0' <<<"$state")"

  case "$action" in
    merge)        act_merge "$pr" "$reason" ;;
    escalate)     act_escalate "$pr" "$reason" ;;
    rerun-full)   act_rerun_full "$pr" "$run_id" "$((rerun_count + 1))" "$reason" ;;
    freshen-once) act_freshen_once "$pr" "$reason" ;;
    skip)         : ;;
  esac

  printf '%s\t%s\t%s\t%s\n' "$pr" "${head_sha:0:12}" "$action" "$reason"
}

list_open_prs() {
  gh api "repos/${REPO}/pulls?state=open&base=trunk&per_page=100" \
    --jq '.[] | select(.draft==false) | .number' 2>/dev/null
}

main() {
  require_jq
  [[ -f "$SIGNATURES_FILE" ]] || { log "flake signatures not found: ${SIGNATURES_FILE}"; exit 2; }

  local mode="" pr=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --pr)          mode="pr"; pr="${2:-}"; shift 2 ;;
      --sweep)       mode="sweep"; shift ;;
      --decide-only) mode="decide-only"; pr="${2:-}"; shift 2 ;;
      -h|--help)     grep -E '^#( |$)' "$0" | sed 's/^#\s\?//'; exit 0 ;;
      *)             log "unknown argument: $1"; exit 2 ;;
    esac
  done

  if [[ "$mode" == "decide-only" ]]; then
    # pr here is a path to a state-json file (test hook); no network.
    local state; state="$(cat "$pr")"
    decide_action "$state"
    return 0
  fi

  log "merge-coordinator: repo=${REPO} dry_run=${DRY_RUN} rerun_cap=${RERUN_CAP}"
  printf 'PR\tHEAD\tDECISION\tWHY\n'

  case "$mode" in
    pr)
      [[ -n "$pr" ]] || { log "--pr requires a number"; exit 2; }
      coordinate_pr "$pr"
      ;;
    sweep)
      local p
      while read -r p; do
        [[ -n "$p" ]] || continue
        coordinate_pr "$p"
      done < <(list_open_prs)
      ;;
    *)
      log "specify --pr <n>, --sweep, or --decide-only <state.json>"; exit 2 ;;
  esac
}

# Only run main when executed directly (so the unit test can source the
# functions without triggering a run).
if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  main "$@"
fi
