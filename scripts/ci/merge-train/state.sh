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
#       "phase": "select|assemble|smart-ci|forward-fix|preexisting-filter|classify-timeout|timeout-retry-intent|flake-retry-intent|rerun-command-failed|classify-flake|autofix|attribute|land|done",
#       "run_id": <id|null>, "fwdfix_attempts": <n>, "flake_reruns": <n>, "timeout_reruns": <n>,
#       "rerun_kind": "timeout|flake|null", "rerun_base_attempt": <n|null>
#     },
#     "config": { "max_batch": <n>, "flake_signatures": "<regex>" },
#     "last_landed_trunk": "<sha|null>"
#   }
#
# Issue title: "Merge Train State". We find-or-create by the train:state label.

TRAIN_STATE_TITLE="${TRAIN_STATE_TITLE:-Merge Train State}"

# train_state_render <branch> <trunk_base> <included-csv> <phase> <run_id> \
#   <fwdfix> <flake_reruns> <last_landed> [timeout_reruns]: emit the state body.
train_state_render() {
  local branch="$1" trunk_base="$2" included_csv="$3" phase="$4" \
        run_id="$5" fwdfix="$6" flake_reruns="$7" last_landed="$8" timeout_reruns="${9:-0}" \
        rerun_kind="${10:-}" rerun_base_attempt="${11:-null}"

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
    --argjson tr "${timeout_reruns:-0}" \
    --arg rk "${rerun_kind}" \
    --argjson rba "${rerun_base_attempt:-null}" \
    --argjson mb "${MAX_BATCH}" \
    --arg flake "${TRAIN_FLAKE_REGEX}" \
    --argjson last "${last_json}" \
    '{
      active_batch: {
        branch: $branch, trunk_base: $tb, included: $inc, phase: $phase,
        run_id: $rid, fwdfix_attempts: $fwd, flake_reruns: $fr, timeout_reruns: $tr,
        rerun_kind: (if ($rk|length) > 0 then $rk else null end),
        rerun_base_attempt: $rba
      },
      config: { max_batch: $mb, flake_signatures: $flake },
      last_landed_trunk: $last
    }')"

  printf 'Machine-managed state for the optimistic batch merge train. Do not edit by hand.\n\n```json\n%s\n```\n' "${json}"
}

# train_state_issue_number: find the state issue number (or empty). READ-ONLY.
train_state_issue_number() {
  if [[ -n "${TRAIN_STATE_ISSUE_OVERRIDE:-}" ]]; then
    echo "${TRAIN_STATE_ISSUE_OVERRIDE}"; return 0
  fi
  gh issue list --label "${TRAIN_LABEL_STATE}" --state open \
    --json number --jq '.[0].number // empty' 2>/dev/null || echo ""
}

# train_state_write <body-file>: create-or-update the state issue. Side-effecting.
train_state_write() {
  local body_file="$1"
  local num
  num="$(train_state_issue_number)"
  if [[ -n "${num}" ]]; then
    train_side_effect gh issue edit "${num}" --body-file "${body_file}"
  else
    train_side_effect gh issue create --title "${TRAIN_STATE_TITLE}" \
      --label "${TRAIN_LABEL_STATE}" --body-file "${body_file}"
  fi
}

# train_state_read: emit the parsed JSON block of the state issue (or empty).
# READ-ONLY; used on startup to resume.
train_state_read() {
  local num body
  num="$(train_state_issue_number)"
  [[ -z "${num}" ]] && return 0
  if [[ -n "${TRAIN_STATE_BODY_OVERRIDE:-}" ]]; then
    body="${TRAIN_STATE_BODY_OVERRIDE}"
  else
    body="$(gh issue view "${num}" --json body --jq '.body' 2>/dev/null || echo "")"
  fi
  # Extract the fenced ```json ... ``` block.
  printf '%s\n' "${body}" | awk '
    /^```json/ { inblk=1; next }
    /^```/     { if (inblk) { inblk=0 } ; next }
    inblk      { print }
  '
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
    local num; num="$(train_state_issue_number)"
    [[ -z "${num}" ]] && return 0
    body="$(gh issue view "${num}" --json body --jq '.body' 2>/dev/null || echo "")"
  fi
  printf '%s\n' "${body}" | awk '
    /^```json aggregate/ { inblk=1; next }
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
  prev="$(train_aggregate_block)"
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
  local state_json; state_json="$(train_state_read)"
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
