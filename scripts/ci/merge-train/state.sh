#!/usr/bin/env bash
# Step 8: state — persist the train's progress to a "Merge Train State" GitHub
# issue (label train:state) carrying a fenced JSON block, written BEFORE each
# side-effecting step so a crash is resumable: on startup the train reads the
# phase and resumes. Per-PR labels (train:landing / train:escalated / train:hold)
# also carry transient state.
#
# The JSON shape:
#   {
#     "active_batch": {
#       "branch": "...", "trunk_base": "<sha>", "included": [<pr>...],
#       "phase": one of TRAIN_STATE_PHASES (below — the canonical list),
#       "run_id": <id|null>, "fwdfix_attempts": <n>, "flake_reruns": <n>,
#       "included_heads": [{"number":<pr>,"head":"<sha>"}], "batch_sha": "<sha|null>",
#       "timeout_reruns": <n>, "timeout_reruns_total": <n>,
#       "rerun_kind": "timeout|flake|null", "rerun_base_attempt": <n|null>
#     },
#     "config": { "max_batch": <n>, "flake_signatures": "<regex>" },
#     "last_landed_trunk": "<sha|null>"
#   }
#
# Issue title: "Merge Train State". We find-or-create by the train:state label.

TRAIN_STATE_TITLE="${TRAIN_STATE_TITLE:-Merge Train State}"

# TRAIN_STATE_PHASES is the ONLY list of phases a persisted state may carry, and
# the single source of truth for two things that must never drift apart:
#   1. the train_state_read schema below (it accepts exactly these), and
#   2. the startup-recovery dispatch table TRAIN_PHASE_RECOVERY (train.sh).
# A phase accepted here but unclassified there is a repo-wide merge deadlock:
# the train persists a state it cannot recover from, so every later dispatch
# fails closed before selection and the machine-managed state issue has to be
# hand-edited (#3045, hit twice on `attribute`). The drift guard in
# fixtures/validate-timeout-retry.sh fails the build when the two disagree.
TRAIN_STATE_PHASES=(
  select
  assemble
  smart-ci
  forward-fix
  preexisting-filter
  classify-timeout
  timeout-retry-intent
  timeout-retry-requesting
  timeout-retry-accepted
  timeout-retry-rejected
  flake-retry-intent
  flake-retry-requesting
  flake-retry-accepted
  flake-retry-rejected
  rerun-command-failed
  classify-flake
  autofix
  attribute
  ci-incomplete
  land
  pre-land-cleanup
  post-land-finalize
  trunk-moved-reassemble
  requeue
  done
)

# train_state_phases_json: TRAIN_STATE_PHASES as a compact JSON array. Computed
# on demand rather than at source time so sourcing state.sh never depends on jq
# being installed (train.sh checks prerequisites after sourcing).
train_state_phases_json() {
  printf '%s\n' "${TRAIN_STATE_PHASES[@]}" \
    | jq -Rsc 'split("\n") | map(select(length > 0))'
}

# train_state_render <branch> <trunk_base> <included-csv> <phase> <run_id> \
#   <fwdfix> <flake_reruns> <last_landed> [included_heads] [batch_sha]
#   [timeout_reruns] [rerun_kind] [rerun_base_attempt] [timeout_reruns_total]:
#   emit the fenced-JSON issue body.
train_state_render() {
  local branch="$1" trunk_base="$2" included_csv="$3" phase="$4" \
        run_id="$5" fwdfix="$6" flake_reruns="$7" last_landed="$8" \
        included_heads="${9:-[]}" batch_sha="${10:-}" timeout_reruns="${11:-0}" \
        rerun_kind="${12:-}" rerun_base_attempt="${13:-null}" timeout_reruns_total="${14:-${11:-0}}"

  local included_json
  if [[ -z "${included_csv}" ]]; then
    included_json="[]"
  else
    included_json="$(printf '%s' "${included_csv}" \
      | jq -Rc 'split(",") | map(select(length>0) | tonumber)')"
  fi
  local run_id_json="null"; [[ -n "${run_id}" && "${run_id}" != "null" ]] && run_id_json="${run_id}"
  local last_json="null"; [[ -n "${last_landed}" && "${last_landed}" != "null" ]] && last_json="\"${last_landed}\""

  local json
  json="$(jq -n \
    --arg branch "${branch}" \
    --arg tb "${trunk_base}" \
    --argjson inc "${included_json}" \
    --arg phase "${phase}" \
    --argjson rid "${run_id_json}" \
    --argjson fwd "${fwdfix:-0}" \
    --argjson fr "${flake_reruns:-0}" \
    --argjson heads "${included_heads}" \
    --arg batch_sha "${batch_sha}" \
    --argjson tr "${timeout_reruns:-0}" \
    --argjson trt "${timeout_reruns_total:-0}" \
    --arg rk "${rerun_kind}" \
    --argjson rba "${rerun_base_attempt:-null}" \
    --argjson mb "${MAX_BATCH}" \
    --arg flake "${TRAIN_FLAKE_REGEX}" \
    --argjson last "${last_json}" \
    '{
      active_batch: {
        branch: $branch, trunk_base: $tb, included: $inc, phase: $phase,
        run_id: $rid, fwdfix_attempts: $fwd, flake_reruns: $fr,
        included_heads: $heads,
        batch_sha: (if ($batch_sha|length) > 0 then $batch_sha else null end),
        timeout_reruns: $tr,
        timeout_reruns_total: $trt,
        rerun_kind: (if ($rk|length) > 0 then $rk else null end),
        rerun_base_attempt: $rba
      },
      config: { max_batch: $mb, flake_signatures: $flake },
      last_landed_trunk: $last
    }')"

  printf 'Machine-managed state for the optimistic batch merge train. Do not edit by hand.\n\n```json\n%s\n```\n' "${json}"
}

# train_state_issue_number: emit the sole state issue number, or empty only
# after a successful lookup proves none exists. API errors and duplicates fail.
train_state_issue_number() {
  if [[ -n "${TRAIN_STATE_ISSUE_OVERRIDE:-}" ]]; then
    echo "${TRAIN_STATE_ISSUE_OVERRIDE}"; return 0
  fi
  if [[ -n "${TRAIN_STATE_ISSUE_LIST_CMD:-}" ]]; then
    "${TRAIN_STATE_ISSUE_LIST_CMD}"
    return
  fi
  if [[ -n "${TRAIN_STATE_ISSUE_NUMBER:-}" ]]; then
    local pinned
    [[ "${TRAIN_STATE_ISSUE_NUMBER}" =~ ^[0-9]+$ ]] || return 2
    pinned="$(gh issue view "${TRAIN_STATE_ISSUE_NUMBER}" --json number,state,labels \
      --jq '.number as $n | select(.state=="OPEN" and any(.labels[]; .name=="'"${TRAIN_LABEL_STATE}"'")) | $n' \
      2>/dev/null)" || return 1
    [[ "${pinned}" == "${TRAIN_STATE_ISSUE_NUMBER}" ]] || {
      train_err "configured state issue #${TRAIN_STATE_ISSUE_NUMBER} is missing, closed, or lacks ${TRAIN_LABEL_STATE}"
      return 2
    }
    printf '%s\n' "${pinned}"
    return 0
  fi
  local rows count
  rows="$(gh issue list --label "${TRAIN_LABEL_STATE}" --state open --limit 100 \
    --json number --jq '.[].number' 2>/dev/null)" || return 1
  count="$(printf '%s\n' "${rows}" | sed '/^$/d' | wc -l | tr -d ' ')"
  [[ "${count}" -le 1 ]] || {
    train_err "multiple open ${TRAIN_LABEL_STATE} issues found; refusing ambiguous state authority"
    return 2
  }
  printf '%s\n' "${rows}" | sed '/^$/d'
}

# train_state_write <body-file>: create-or-update the state issue. Side-effecting.
train_state_write() {
  local body_file="$1"
  local num lookup_rc=0
  num="$(train_state_issue_number)" || lookup_rc=$?
  [[ "${lookup_rc}" == "0" ]] || return "${lookup_rc}"
  if [[ -n "${num}" ]]; then
    train_side_effect gh issue edit "${num}" --body-file "${body_file}"
  else
    if [[ "${TRAIN_APPLY:-0}" == "1" ]]; then
      train_err "live state creation is disabled; configure the pre-provisioned TRAIN_STATE_ISSUE_NUMBER"
      return 2
    fi
    train_side_effect gh issue create --title "${TRAIN_STATE_TITLE}" \
      --label "${TRAIN_LABEL_STATE}" --body-file "${body_file}"
  fi
}

# train_state_body <issue-number>: emit the state issue body verbatim. Shared by
# train_state_read and the operator salvage path so both observe the same bytes.
# Returns 2 when the body cannot be fetched.
train_state_body() {
  local num="$1"
  if [[ -n "${TRAIN_STATE_BODY_OVERRIDE:-}" ]]; then
    printf '%s' "${TRAIN_STATE_BODY_OVERRIDE}"
    return 0
  fi
  if [[ -n "${TRAIN_STATE_ISSUE_VIEW_CMD:-}" ]]; then
    "${TRAIN_STATE_ISSUE_VIEW_CMD}" "${num}" || return 2
    return 0
  fi
  gh issue view "${num}" --json body --jq '.body' 2>/dev/null || return 2
}

# train_state_salvage: last-resort reader for a state body the schema REJECTS —
# exactly the shape an emergency hand edit produces (the live `active_batch:
# null` repair that made train_state_read fail outright). train_state_read
# cannot serve the operator reset here, so this recovers whatever is still
# legible and normalizes it to the fields the reset needs:
#   {active_batch:{phase,trunk_base,included,timeout_reruns_total},
#    last_landed_trunk}
# It REFUSES (2) whenever the RAW TEXT shows any sign of durable land intent — a
# land-family phase or a batch SHA — because that state must reconcile against
# trunk rather than be discarded. The refusal works on raw text so it still
# holds when no part of the body parses as JSON. Returns 1 when no state issue
# exists, 2 on refusal or lookup/fetch failure.
train_state_salvage() {
  local num body block lookup_rc=0
  num="$(train_state_issue_number)" || lookup_rc=$?
  [[ "${lookup_rc}" == "0" ]] || return "${lookup_rc}"
  [[ -z "${num}" ]] && return 1
  body="$(train_state_body "${num}")" || return 2
  if grep -Eq '"phase"[[:space:]]*:[[:space:]]*"(land|pre-land-cleanup|post-land-finalize)"' <<<"${body}"; then
    return 2
  fi
  if grep -Eq '"batch_sha"[[:space:]]*:[[:space:]]*"[0-9a-fA-F]{40}"' <<<"${body}"; then
    return 2
  fi
  block="$(printf '%s\n' "${body}" | awk '
    /^```json[[:space:]]*$/ { inblk=1; next }
    /^```[[:space:]]*$/     { inblk=0; next }
    inblk                   { print }
  ')"
  jq -sc '
    (.[0] // {}) as $raw
    | (if ($raw | type) == "object" then $raw else {} end) as $s
    | (if ($s.active_batch | type) == "object" then $s.active_batch else {} end) as $ab
    | {
        active_batch: {
          phase:      (if ($ab.phase      | type) == "string" then $ab.phase      else ""   end),
          trunk_base: (if ($ab.trunk_base | type) == "string" then $ab.trunk_base else ""   end),
          included:   [ (if ($ab.included | type) == "array" then $ab.included[] else empty end)
                        | select(type == "number") ],
          timeout_reruns_total:
            (if ($ab.timeout_reruns_total | type) == "number" then $ab.timeout_reruns_total else 0 end)
        },
        last_landed_trunk:
          (if ($s.last_landed_trunk | type) == "string" then $s.last_landed_trunk else null end)
      }' <<<"${block}" 2>/dev/null \
    || jq -nc '{active_batch:{phase:"",trunk_base:"",included:[],timeout_reruns_total:0},last_landed_trunk:null}'
}

# train_state_read: emit the parsed JSON block of the state issue (or empty).
# READ-ONLY; used on startup to resume.
train_state_read() {
  local num body json lookup_rc=0 body_rc=0
  num="$(train_state_issue_number)" || lookup_rc=$?
  [[ "${lookup_rc}" == "0" ]] || return "${lookup_rc}"
  [[ -z "${num}" ]] && return 0
  body="$(train_state_body "${num}")" || body_rc=$?
  [[ "${body_rc}" == "0" ]] || return 2
  # An existing issue must contain exactly one parseable machine-state block.
  # Fence markers may have trailing whitespace, but no other suffix.
  # Empty output is reserved for a confirmed absent issue.
  json="$(printf '%s\n' "${body}" | awk '
    /^```json[[:space:]]*$/ {
      openers++
      if (inblk) invalid=1
      inblk=1
      next
    }
    /^```[[:space:]]*$/ {
      if (inblk) { closers++; inblk=0 }
      next
    }
    inblk { print }
    END {
      if (invalid || inblk || openers != 1 || closers != 1) exit 3
    }
  ')" || return 3
  [[ -n "${json}" ]] || return 3
  local phases_json
  phases_json="$(train_state_phases_json)" || return 3
  jq -se --argjson phases "${phases_json}" '
    length == 1 and (.[0] |
      type == "object" and
      (.active_batch | type == "object") and
      (.active_batch.branch | type == "string") and
      (.active_batch.trunk_base | type == "string") and
      (.active_batch.included | type == "array" and all(.[]; type == "number")) and
      (.active_batch.phase | type == "string" and IN($phases[])) and
      (.active_batch.run_id == null or (.active_batch.run_id | type == "number" or type == "string")) and
      (.active_batch.fwdfix_attempts == null or (.active_batch.fwdfix_attempts | type == "number")) and
      (.active_batch.flake_reruns == null or (.active_batch.flake_reruns | type == "number")) and
      (
        if (.active_batch.phase | IN("land","pre-land-cleanup","post-land-finalize")) then
          (.active_batch.included_heads | type == "array" and length > 0) and
          (.active_batch.batch_sha | type == "string" and test("^[0-9a-fA-F]{40}$"))
        else
          (.active_batch.included_heads == null or (.active_batch.included_heads | type == "array")) and
          (.active_batch.batch_sha == null or
            (.active_batch.batch_sha | type == "string" and test("^[0-9a-fA-F]{40}$")))
        end
      ) and
      (.active_batch.timeout_reruns == null or (.active_batch.timeout_reruns | type == "number")) and
      (.active_batch.timeout_reruns_total == null or (.active_batch.timeout_reruns_total | type == "number")) and
      (.active_batch.rerun_kind == null or (.active_batch.rerun_kind | type == "string" and IN("timeout","flake"))) and
      (.active_batch.rerun_base_attempt == null or (.active_batch.rerun_base_attempt | type == "number")) and
      (.config == null or ((.config | type == "object") and
        (.config.max_batch == null or (.config.max_batch | type == "number")) and
        (.config.flake_signatures == null or (.config.flake_signatures | type == "string")))) and
      (.last_landed_trunk == null or (.last_landed_trunk | type == "string"))
    )
  ' <<<"${json}" >/dev/null || return 3
  jq -sc '.[0]' <<<"${json}"
}

# --- persistent over-time dashboard (the founder's "dashboard") ---------------
# Aggregate metrics accumulate across LIVE runs in a second fenced block
# (```json aggregate```) inside the SAME Merge Train State issue, with a
# human-readable Markdown dashboard rendered above it. We chose the issue over a
# committed docs file because it needs no commit/push per run (lower friction,
# and the train must stay FF-CAS-clean about what it pushes to trunk). In
# dry-run the dashboard renders to the Step Summary but is NOT persisted.

# train_aggregate_block: emit the parsed ```json aggregate``` block (or empty).
train_aggregate_block() {
  local body
  if [[ -n "${TRAIN_AGG_BODY_OVERRIDE:-}" ]]; then
    body="${TRAIN_AGG_BODY_OVERRIDE}"
  else
    local num lookup_rc=0
    num="$(train_state_issue_number)" || lookup_rc=$?
    [[ "${lookup_rc}" == "0" ]] || return "${lookup_rc}"
    [[ -z "${num}" ]] && return 0
    body="$(gh issue view "${num}" --json body --jq '.body' 2>/dev/null)" || return 2
  fi
  printf '%s\n' "${body}" | awk '
    /^```json aggregate[[:space:]]*$/ { inblk=1; next }
    /^```/               { if (inblk) { inblk=0 }; next }
    inblk                { print }
  '
}

# train_aggregate_merge <prev-json> : read this run's per-run counters
# (TRAIN_METRICS_KV) plus a time-to-land sample, fold them into prev, emit the
# new aggregate JSON. Pure-ish (reads metric counters + env); no I/O writes.
train_aggregate_merge() {
  local prev="${1:-}" ttl_sample="${2:-}"
  [[ -z "${prev}" ]] && prev='{}'

  local landed_now flake_now timeout_now esc_now batches_inc
  landed_now="$(train_metric_get landed 0)"
  flake_now="$(train_metric_get flake_reruns 0)"
  timeout_now="$(train_metric_get timeout_reruns 0)"
  esc_now="$(train_metric_get escalated 0)"
  # A "batch" counts when at least one PR landed this run.
  batches_inc=0; [[ "${landed_now}" -gt 0 ]] && batches_inc=1

  jq -n \
    --argjson prev "${prev}" \
    --argjson landed_now "${landed_now}" \
    --argjson flake_now "${flake_now}" \
    --argjson timeout_now "${timeout_now}" \
    --argjson esc_now "${esc_now}" \
    --argjson batches_inc "${batches_inc}" \
    --arg ttl "${ttl_sample}" \
    --arg now "${TRAIN_RUN_TIMESTAMP:-}" \
    '
    ($prev.totals // {}) as $t
    | ($prev.ttl_samples // []) as $samp
    | (if ($ttl|length) > 0 then ($samp + [($ttl|tonumber)]) else $samp end) as $samp2
    | ($samp2 | sort) as $sorted
    | (if ($sorted|length) > 0 then $sorted[ (($sorted|length)/2) | floor ] else null end) as $median
    | {
        schema: "honua.merge-train.aggregate/v1",
        updated: $now,
        totals: {
          batches:         (($t.batches // 0) + $batches_inc),
          prs_landed:      (($t.prs_landed // 0) + $landed_now),
          flake_reruns:    (($t.flake_reruns // 0) + $flake_now),
          timeout_reruns:  (($t.timeout_reruns // 0) + $timeout_now),
          escalations:     (($t.escalations // 0) + $esc_now),
          runs:            (($t.runs // 0) + 1)
        },
        median_time_to_land_seconds: $median,
        ttl_samples: ($samp2 | .[-50:])
      }'
}

# train_aggregate_dashboard_md <agg-json> <trunk_sha> <last_landed> : render the
# human Markdown dashboard for the state issue (and Step Summary).
train_aggregate_dashboard_md() {
  local agg="$1" trunk="$2" last="$3"
  local batches prs flake timeout esc runs median rate
  batches="$(jq -r '.totals.batches // 0' <<<"${agg}")"
  prs="$(jq -r '.totals.prs_landed // 0' <<<"${agg}")"
  flake="$(jq -r '.totals.flake_reruns // 0' <<<"${agg}")"
  timeout="$(jq -r '.totals.timeout_reruns // 0' <<<"${agg}")"
  esc="$(jq -r '.totals.escalations // 0' <<<"${agg}")"
  runs="$(jq -r '.totals.runs // 0' <<<"${agg}")"
  median="$(jq -r '.median_time_to_land_seconds // "—"' <<<"${agg}")"
  # flake-rerun rate = flake reruns / batches (guard div-by-zero).
  if [[ "${batches}" -gt 0 ]]; then
    rate="$(jq -rn --argjson f "${flake}" --argjson b "${batches}" '($f/$b)|.*100|floor/100')"
  else
    rate="0"
  fi

  printf '## Merge Train — aggregate dashboard\n\n'
  printf '| Metric | Value |\n|---|---|\n'
  printf '| Total batches landed | %s |\n' "${batches}"
  printf '| PRs landed | %s |\n' "${prs}"
  printf '| Train runs | %s |\n' "${runs}"
  printf '| Median time-to-land | %s |\n' "$([[ "${median}" == "—" || "${median}" == "null" ]] && echo "—" || echo "${median}s")"
  printf '| Flake-rerun rate (reruns/batch) | %s |\n' "${rate}"
  printf '| Timeout failed-job reruns | %s |\n' "${timeout}"
  printf '| Escalations | %s |\n' "${esc}"
  printf '| Current trunk SHA | `%s` |\n' "${trunk:-—}"
  printf '| Last-landed SHA | `%s` |\n' "${last:-—}"
  printf '| Live flake signatures | `%s` |\n' "${TRAIN_FLAKE_REGEX}"
  printf '\n'
}

# train_aggregate_update <trunk_sha> <last_landed> [ttl_sample_seconds]:
# fold this run into the persisted aggregate and write it (with the machine-state
# block preserved) back to the state issue. Side-effecting (gated by TRAIN_APPLY
# via train_state_write); in dry-run it renders to the Step Summary only.
train_aggregate_update() {
  local trunk="$1" last="$2" ttl="${3:-}"
  local prev agg
  prev="$(train_aggregate_block)" || return 1
  agg="$(train_aggregate_merge "${prev}" "${ttl}")"

  # Render the human dashboard (always emitted to the Step Summary for visibility).
  local md; md="$(train_aggregate_dashboard_md "${agg}" "${trunk}" "${last}")"
  if train_have train_summary; then
    while IFS= read -r line; do train_summary "${line}"; done <<<"${md}"
  fi

  # Persist only in LIVE mode (dry-run never writes the issue).
  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_log "dry-run: aggregate dashboard rendered (not persisted to state issue)"
    return 0
  fi

  # Read the current machine-state block back so we re-emit a complete body.
  local state_json; state_json="$(train_state_read)" || {
    train_err "could not read machine state while updating aggregate; preserving existing state"
    return 1
  }
  [[ -z "${state_json}" ]] && state_json='{}'

  local body="${TRAIN_AGG_BODY_FILE:-$(mktemp)}"
  {
    printf 'Machine-managed state for the optimistic batch merge train. Do not edit by hand.\n\n'
    printf '%s' "${md}"
    printf '\n```json\n%s\n```\n' "${state_json}"
    printf '\n```json aggregate\n%s\n```\n' "${agg}"
  } >"${body}"
  train_state_write "${body}"
}
