#!/usr/bin/env bash
# Orchestrator for the honua-server optimistic batch merge train (Phase 1).
#
# Wires the eight sourceable steps together. DRY-RUN BY DEFAULT (TRAIN_APPLY=0):
# real local git assembly + real CI-status reads + real shard computation, but
# NO pushes/merges/comments/issue-writes (those route through train_side_effect
# and are only logged). MAX_BATCH defaults to 3.
#
# Phases (also the resume points written to the state issue before each
# side-effecting step):
#   select -> assemble -> smart-ci -> [forward-fix] -> [classify-flake] ->
#   [attribute -> rebuild] -> land -> done
#
# Usage:
#   TRAIN_APPLY=0 scripts/ci/merge-train/train.sh            # shadow / dry-run
#   TRAIN_APPLY=1 scripts/ci/merge-train/train.sh            # live (human-gated)

set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
. "${TRAIN_DIR}/lib.sh"
# shellcheck source=select.sh
. "${TRAIN_DIR}/select.sh"
# shellcheck source=assemble.sh
. "${TRAIN_DIR}/assemble.sh"
# shellcheck source=smart-ci.sh
. "${TRAIN_DIR}/smart-ci.sh"
# shellcheck source=forward-fix.sh
. "${TRAIN_DIR}/forward-fix.sh"
# shellcheck source=classify-flake.sh
. "${TRAIN_DIR}/classify-flake.sh"
# shellcheck source=attribute.sh
. "${TRAIN_DIR}/attribute.sh"
# shellcheck source=land.sh
. "${TRAIN_DIR}/land.sh"
# shellcheck source=state.sh
. "${TRAIN_DIR}/state.sh"

train_require git jq gh || { train_err "missing prerequisites"; exit 2; }

# Scratch files for the run.
TRAIN_WORK="$(mktemp -d)"
trap 'rm -rf "${TRAIN_WORK}"' EXIT
export TRAIN_INCLUDED_FILE="${TRAIN_WORK}/included.tsv"
export TRAIN_SKIPPED_FILE="${TRAIN_WORK}/skipped.tsv"
export TRAIN_RUN_ID_FILE="${TRAIN_WORK}/run_id"
# Instrumentation sinks (additive observability — no decision logic reads these).
export TRAIN_TIMINGS_FILE="${TRAIN_WORK}/timings.kv"
export TRAIN_METRICS_KV="${TRAIN_WORK}/metrics.kv"
: >"${TRAIN_TIMINGS_FILE}"; : >"${TRAIN_METRICS_KV}"
# Where the machine-readable metrics doc is written (uploaded as an artifact).
: "${TRAIN_METRICS_OUT:=${TRAIN_REPO_ROOT}/merge-train-metrics.json}"
# Run timestamp: prefer one injected by the workflow; else generate.
: "${TRAIN_RUN_TIMESTAMP:=$(date -u +%Y-%m-%dT%H:%M:%SZ)}"

# _train_mode_label: human label for the run mode.
_train_mode_label() { [[ "${TRAIN_APPLY}" == "1" ]] && echo LIVE || echo DRY-RUN; }

# _emit_metrics <outcome> <trunk_sha> <last_landed> [shard_descriptor]: render the
# machine-readable metrics JSON to TRAIN_METRICS_OUT (best-effort; never fatal).
_emit_metrics() {
  local outcome="$1" trunk_sha="$2" last_landed="$3" shard_desc="${4:-}"
  train_metrics_render "${TRAIN_RUN_TIMESTAMP}" "$(_train_mode_label)" \
    "${trunk_sha}" "${last_landed}" "${outcome}" "${shard_desc}" \
    >"${TRAIN_METRICS_OUT}" 2>/dev/null \
    && train_log "metrics written: ${TRAIN_METRICS_OUT} (outcome=${outcome})" \
    || train_warn "could not write metrics JSON"
}

main() {
  train_log "mode: $(_train_mode_label) MAX_BATCH=${MAX_BATCH} run=${TRAIN_RUN_TIMESTAMP}"

  # --- select ----------------------------------------------------------------
  train_group select "pick ready PRs (oldest-first, capped at ${MAX_BATCH})"
  train_step_begin select
  local selected
  selected="$(train_select | jq -s '.')"
  local prs
  prs="$(jq -r '.[].number' <<<"${selected}")"
  local n_selected; n_selected="$(jq 'length' <<<"${selected}")"
  train_metric_set selected "${n_selected}"
  # candidates = total open PRs the selector evaluated (selected ones surface in
  # the batch; the rest were skipped at select for draft/hold/conflict/CI-gate).
  # TRAIN_CANDIDATE_COUNT lets the caller inject the raw count; else fall back to
  # the selected count so the metric is never larger than reality.
  train_metric_set candidates "${TRAIN_CANDIDATE_COUNT:-${n_selected}}"
  train_metric_set skipped_select "$(( ${TRAIN_CANDIDATE_COUNT:-${n_selected}} - n_selected ))"
  train_step_end select >/dev/null
  if [[ -z "${prs}" ]]; then
    train_decision "no ready PRs; nothing to do"
    train_endgroup
    _emit_metrics "nothing-ready" "" "" ""
    _dashboard "" "${selected}" "" "" "no ready PRs — nothing to select"
    return 0
  fi
  train_decision "candidate batch (oldest-first, capped): $(tr '\n' ' ' <<<"${prs}")"
  train_endgroup

  # --- trunk base ------------------------------------------------------------
  git -C "${TRAIN_REPO_ROOT}" fetch --quiet "${TRAIN_REMOTE}" "${TRAIN_BASE_BRANCH}"
  local trunk_sha trunk_sha7
  trunk_sha="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  trunk_sha7="${trunk_sha:0:7}"
  train_log "trunk base: ${trunk_sha} (${trunk_sha7})"

  # --- assemble (transactional detect-and-skip) ------------------------------
  train_group assemble "merge PR heads onto a fresh branch off ${TRAIN_BASE_BRANCH}"
  train_step_begin assemble
  # state: write BEFORE assembling (resumable at assemble phase).
  _write_state "" "${trunk_sha}" "" "assemble" "" 0 0
  local batch
  # shellcheck disable=SC2086
  batch="$(train_assemble "${trunk_sha7}" ${prs})"
  train_log "batch branch: ${batch}"

  local included skipped
  included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
  skipped="$(cut -f1 "${TRAIN_SKIPPED_FILE}" | tr '\n' ' ')"
  train_metric_set included "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
  train_metric_set skipped_conflict "$(grep -c . "${TRAIN_SKIPPED_FILE}" 2>/dev/null || echo 0)"
  for pr in $(tr ',' ' ' <<<"${included}"); do
    [[ -n "${pr}" ]] && train_decision "INCLUDE #${pr} (assembled clean)"
  done
  for pr in ${skipped}; do
    [[ -n "${pr}" ]] && train_decision "SKIP #${pr} (conflict; aborted clean)"
  done
  train_step_end assemble >/dev/null
  train_endgroup

  if [[ -z "${included}" ]]; then
    train_annotate_warn "no PRs assembled cleanly; nothing to land"
    _emit_metrics "nothing-ready" "${trunk_sha}" "" ""
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "" "all candidates skipped on conflict — nothing to land"
    return 0
  fi

  # state: landing labels + phase smart-ci before CI side effects.
  _write_state "${batch}" "${trunk_sha}" "${included}" "smart-ci" "" 0 0
  local pr
  for pr in $(tr ',' ' ' <<<"${included}"); do
    train_side_effect gh pr edit "${pr}" --add-label "${TRAIN_LABEL_LANDING}"
  done

  # --- smart-ci + forward-fix + flake + attribute loop -----------------------
  train_group smart-ci "compute shard subset for the batch's cumulative diff"
  train_step_begin smart-ci
  local shard_descriptor
  shard_descriptor="$(train_smart_ci_shards "${batch}")"
  train_metric_set smartci_shard_count "$(jq -r '(.shards // []) | length' <<<"${shard_descriptor}" 2>/dev/null || echo 0)"
  train_decision "smart-CI shard subset: ${shard_descriptor}"

  local gate fwdfix=0 flake_reruns=0
  gate="$(train_smart_ci_run "${batch}")"
  train_step_end smart-ci >/dev/null
  train_endgroup

  if [[ "${TRAIN_APPLY}" != "1" ]]; then
    train_log "dry-run: stopping before CI gate evaluation/land (no pushes happened)"
    _emit_metrics "dry-run" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "dry-run: stopped before CI gate/land (no pushes happened)"
    return 0
  fi

  # Live-mode gate handling (forward-fix -> flake -> attribute -> land).
  train_group ci-gate "evaluate CI Gate; forward-fix / classify-flake / attribute"
  train_step_begin ci-gate
  while [[ "${gate}" != "SUCCESS" ]]; do
    local run_id; run_id="$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null || echo "")"
    local failing; failing="$(train_failing_jobs "${run_id}")"

    # forward-fix: only when the SOLE failure is the format-verify step.
    if train_is_format_only_failure "${failing}"; then
      _write_state "${batch}" "${trunk_sha}" "${included}" "forward-fix" "${run_id}" "${fwdfix}" "${flake_reruns}"
      if train_forward_fix "${batch}" "${fwdfix}"; then
        fwdfix=$((fwdfix + 1))
        train_metric_set forward_fixes "${fwdfix}"
        train_decision "forward-fix #${fwdfix} applied (dotnet format); re-running CI"
        gate="$(train_smart_ci_run "${batch}")"
        continue
      fi
    fi

    # classify-flake BEFORE attribute.
    _write_state "${batch}" "${trunk_sha}" "${included}" "classify-flake" "${run_id}" "${fwdfix}" "${flake_reruns}"
    if train_classify_flake "${run_id}" "${flake_reruns}"; then
      flake_reruns=$((flake_reruns + 1))
      train_metric_set flake_reruns "${flake_reruns}"
      train_decision "flake signature matched; rerun #${flake_reruns} of failed jobs"
      # Re-poll the same run after rerun.
      while :; do
        local st; st="$(gh run view "${run_id}" --json status --jq '.status' 2>/dev/null || echo "")"
        [[ "${st}" == "completed" ]] && break; sleep 30
      done
      gate="$(gh run view "${run_id}" --json jobs \
        --jq '[.jobs[] | select(.name=="CI Gate")][0].conclusion // "missing"' | tr '[:lower:]' '[:upper:]')"
      continue
    fi

    # attribute (real failure).
    _write_state "${batch}" "${trunk_sha}" "${included}" "attribute" "${run_id}" "${fwdfix}" "${flake_reruns}"
    local culprits; culprits="$(train_attribute "${failing}" "${TRAIN_INCLUDED_FILE}")"
    if [[ "${culprits}" == "ESCALATE_BATCH" ]]; then
      train_metric_inc escalated "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
      train_annotate_warn "escalating entire batch; manual triage required"
      for pr in $(tr ',' ' ' <<<"${included}"); do
        train_side_effect gh pr edit "${pr}" --remove-label "${TRAIN_LABEL_LANDING}"
      done
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "escalated-batch" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "ESCALATED whole batch: CI failure not attributable to a member diff"
      return 0
    fi
    # Drop culprits, rebuild minus them, re-CI.
    local remaining="${included}"
    for pr in ${culprits}; do
      train_drop_pr "${pr}" "real CI failure attributed to its diff"
      train_metric_inc attribution_drops
      train_metric_inc escalated
      train_notice "DROP #${pr}: real CI failure attributed to its diff; rebuilding batch without it"
      remaining="$(tr ',' '\n' <<<"${remaining}" | grep -vx "${pr}" | tr '\n' ',' | sed 's/,$//')"
    done
    if [[ -z "${remaining}" ]]; then
      train_annotate_warn "all members dropped; batch empty"
      train_step_end ci-gate >/dev/null; train_endgroup
      _emit_metrics "all-dropped" "${trunk_sha}" "" "${shard_descriptor}"
      _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
        "all batch members dropped on attribution — batch empty"
      return 0
    fi
    included="${remaining}"
    _write_state "" "${trunk_sha}" "${included}" "assemble" "" "${fwdfix}" "${flake_reruns}"
    # shellcheck disable=SC2086
    batch="$(train_assemble "${trunk_sha7}" $(tr ',' ' ' <<<"${included}"))"
    included="$(cut -f1 "${TRAIN_INCLUDED_FILE}" | tr '\n' ',' | sed 's/,$//')"
    gate="$(train_smart_ci_run "${batch}")"
  done
  train_step_end ci-gate >/dev/null
  train_endgroup

  # --- land (FF-only CAS) ----------------------------------------------------
  train_group land "fast-forward-only CAS push of the CI-green batch onto ${TRAIN_BASE_BRANCH}"
  train_step_begin land
  _write_state "${batch}" "${trunk_sha}" "${included}" "land" "$(cat "${TRAIN_RUN_ID_FILE}" 2>/dev/null)" "${fwdfix}" "${flake_reruns}"
  local rc=0
  train_land "${batch}" "${trunk_sha}" "${TRAIN_INCLUDED_FILE}" || rc=$?
  if [[ "${rc}" == "10" ]]; then
    train_annotate_warn "land deferred: trunk moved; the next scheduled run re-assembles"
    train_step_end land >/dev/null; train_endgroup
    _emit_metrics "trunk-moved-reassemble" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "land deferred: trunk moved (FF-CAS) — next run re-assembles"
    return 0
  fi
  if [[ "${rc}" != "0" ]]; then
    train_err "land failed (rc=${rc})"
    train_step_end land >/dev/null; train_endgroup
    _emit_metrics "land-error" "${trunk_sha}" "" "${shard_descriptor}"
    _dashboard "${batch}" "${selected}" "${trunk_sha}" "${shard_descriptor}" \
      "land FAILED (rc=${rc})"
    return "${rc}"
  fi

  local new_trunk; new_trunk="$(git -C "${TRAIN_REPO_ROOT}" rev-parse "${TRAIN_REMOTE}/${TRAIN_BASE_BRANCH}")"
  train_metric_set landed "$(grep -c . "${TRAIN_INCLUDED_FILE}" 2>/dev/null || echo 0)"
  _write_state "" "${new_trunk}" "" "done" "" 0 0 "${batch_landed:-${new_trunk}}"
  train_notice "LANDED batch ${batch} ($(tr ',' ' ' <<<"${included}")); trunk now ${new_trunk:0:7}"
  train_step_end land >/dev/null
  train_endgroup
  _emit_metrics "landed" "${new_trunk}" "${new_trunk}" "${shard_descriptor}"
  _dashboard "${batch}" "${selected}" "${new_trunk}" "${shard_descriptor}" \
    "LANDED ${included} via FF-CAS; trunk now ${new_trunk:0:7}"
  # Persistent over-time dashboard (state issue): aggregate after a successful land.
  train_aggregate_update "${new_trunk}" "${new_trunk}"
}

# _write_state branch trunk_base included-csv phase run_id fwdfix flake [last_landed]
_write_state() {
  local body="${TRAIN_WORK}/state.md"
  train_state_render "$1" "$2" "$3" "$4" "$5" "$6" "$7" "${8:-null}" >"${body}"
  train_state_write "${body}"
}

# _pr_decision <pr>: classify a candidate PR's outcome for the dashboard table.
# Reads the included/skipped scratch files (decision logic already ran). Returns
# "included" / "skipped (conflict)" / "escalated".
_pr_decision() {
  local pr="$1"
  if grep -qE "^${pr}	" "${TRAIN_INCLUDED_FILE}" 2>/dev/null; then
    echo "✅ included"
  elif grep -qE "^${pr}	" "${TRAIN_SKIPPED_FILE}" 2>/dev/null; then
    echo "⏭️ skipped — assemble conflict (aborted clean)"
  else
    echo "⏹️ not in batch"
  fi
}

# _dashboard <batch> <selected-json> <trunk_sha> <shard_descriptor> <outcome-note>
# The headline deliverable: a Markdown report appended to $GITHUB_STEP_SUMMARY
# (and mirrored to stdout for local dry-runs). Reads only the scratch files /
# metrics the decision logic already produced — it makes NO decisions.
_dashboard() {
  local batch="$1" selected="$2" trunk_sha="$3" shard_descriptor="$4" note="$5"
  local mode; mode="$(_train_mode_label)"

  train_summary "## 🚂 Merge Train — ${mode} run"
  train_summary ""
  train_summary "| | |"
  train_summary "|---|---|"
  train_summary "| **Mode** | ${mode} ($([[ "${TRAIN_APPLY}" == "1" ]] && echo 'writes ACT' || echo 'no pushes/merges/comments')) |"
  train_summary "| **Run timestamp** | ${TRAIN_RUN_TIMESTAMP} |"
  train_summary "| **Trunk base** | \`${trunk_sha:-<none>}\` ${trunk_sha:+(${trunk_sha:0:7})} |"
  train_summary "| **Batch branch** | \`${batch:-<none>}\` |"
  train_summary "| **MAX_BATCH** | ${MAX_BATCH} |"
  train_summary "| **Outcome** | ${note:-—} |"
  train_summary ""

  # --- candidates table ------------------------------------------------------
  train_summary "### Candidates"
  train_summary ""
  if [[ "$(jq 'length' <<<"${selected}")" -gt 0 ]]; then
    train_summary "| PR | Author | CI-Gate | Decision |"
    train_summary "|---|---|---|---|"
    local rows; rows="$(jq -r '.[] | "\(.number)\t\(.author // "?")\t\(.gate)"' <<<"${selected}")"
    local num author gate decision
    while IFS=$'\t' read -r num author gate; do
      [[ -z "${num}" ]] && continue
      decision="$(_pr_decision "${num}")"
      train_summary "| #${num} | ${author} | ${gate} | ${decision} |"
    done <<<"${rows}"
  else
    train_summary "_No PRs were eligible for selection this run._"
  fi
  train_summary ""

  # --- batch -----------------------------------------------------------------
  local included_csv=""
  [[ -s "${TRAIN_INCLUDED_FILE}" ]] && included_csv="$(cut -f1 "${TRAIN_INCLUDED_FILE}" 2>/dev/null | sed 's/^/#/' | tr '\n' ' ')"
  train_summary "### Batch"
  train_summary ""
  train_summary "- **Branch:** \`${batch:-<none>}\`"
  train_summary "- **Included PRs:** ${included_csv:-_(none)_}"
  if [[ -s "${TRAIN_SKIPPED_FILE}" ]]; then
    local skipped_csv; skipped_csv="$(cut -f1 "${TRAIN_SKIPPED_FILE}" | sed 's/^/#/' | tr '\n' ' ')"
    train_summary "- **Skipped (conflict):** ${skipped_csv}"
  fi
  train_summary ""

  # --- smart-CI shard set ----------------------------------------------------
  train_summary "### Smart-CI shard set"
  train_summary ""
  if [[ -n "${shard_descriptor}" ]] && jq -e . >/dev/null 2>&1 <<<"${shard_descriptor}"; then
    local run_all reason shards
    run_all="$(jq -r '.run_all' <<<"${shard_descriptor}")"
    reason="$(jq -r '.reason // ""' <<<"${shard_descriptor}")"
    shards="$(jq -r '(.shards // []) | join(", ")' <<<"${shard_descriptor}")"
    if [[ "${run_all}" == "true" ]]; then
      train_summary "- **run_all:** \`true\` — ${reason:-full matrix}"
    else
      local shard_n; shard_n="$(jq -r '(.shards // []) | length' <<<"${shard_descriptor}")"
      train_summary "- **Targeted (run_all=false), ${shard_n} shards:** ${shards:-_(none)_}"
      [[ -n "${reason}" ]] && train_summary "- **Reason:** ${reason}"
    fi
  else
    train_summary "_(not computed — no batch reached smart-CI)_"
  fi
  train_summary ""

  # --- gate / heals / land ---------------------------------------------------
  train_summary "### Run actions"
  train_summary ""
  train_summary "| Metric | Count |"
  train_summary "|---|---|"
  train_summary "| Candidates selected | $(train_metric_get selected 0) |"
  train_summary "| Included | $(train_metric_get included 0) |"
  train_summary "| Skipped (conflict) | $(train_metric_get skipped_conflict 0) |"
  train_summary "| Forward-fixes applied | $(train_metric_get forward_fixes 0) |"
  train_summary "| Flake reruns | $(train_metric_get flake_reruns 0) |"
  train_summary "| Attribution drops | $(train_metric_get attribution_drops 0) |"
  train_summary "| Escalated | $(train_metric_get escalated 0) |"
  train_summary "| Landed | $(train_metric_get landed 0) |"
  train_summary ""

  # --- per-phase timings -----------------------------------------------------
  if [[ -s "${TRAIN_TIMINGS_FILE:-/dev/null}" ]]; then
    train_summary "### Per-phase timings"
    train_summary ""
    train_summary "| Phase | Seconds |"
    train_summary "|---|---|"
    awk -F= 'NF>=2 {t[$1]=$2} END {for (k in t) print k"\t"t[k]}' "${TRAIN_TIMINGS_FILE}" \
      | while IFS=$'\t' read -r ph secs; do train_summary "| ${ph} | ${secs} |"; done
    train_summary ""
  fi

  train_summary "_Machine-readable metrics: \`merge-train-metrics.json\` (workflow artifact). Aggregate over-time dashboard: the **Merge Train State** issue._"
}

main "$@"
