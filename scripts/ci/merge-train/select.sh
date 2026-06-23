#!/usr/bin/env bash
# Step 1: select — pick the ready PRs for the next batch.
#
# Ready = non-draft, labels exclude train:hold / train:escalated (and the
# pre-existing `hold` opt-out), mergeable == MERGEABLE, and the CI Gate check
# is SUCCESS OR a flake-only failure. Ordered oldest-createdAt first, capped at
# MAX_BATCH.
#
# READ-ONLY: this step never mutates anything (only gh ... --json reads), so it
# runs identically in dry-run and live mode.

# train_select_ci_gate_state <pr-json>: classify the CI Gate check for one PR.
# Emits one of: SUCCESS | FLAKE | FAIL | PENDING | MISSING.
# Reads the statusCheckRollup entry named "CI Gate" (the single required check).
# A failing CI Gate is downgraded to FLAKE only when the failing run's logs
# match the flake regex (caller decides whether to actually rerun).
train_select_ci_gate_state() {
  local rollup_json="$1"
  local gate
  gate="$(jq -c '[.[] | select(.name == "CI Gate")] | first // empty' <<<"${rollup_json}")"
  if [[ -z "${gate}" || "${gate}" == "null" ]]; then
    echo "MISSING"; return 0
  fi
  local status conclusion
  status="$(jq -r '.status // ""' <<<"${gate}")"
  conclusion="$(jq -r '.conclusion // ""' <<<"${gate}")"
  if [[ "${status}" != "COMPLETED" ]]; then
    echo "PENDING"; return 0
  fi
  case "${conclusion}" in
    SUCCESS) echo "SUCCESS" ;;
    FAILURE|TIMED_OUT|CANCELLED|STARTUP_FAILURE|ACTION_REQUIRED) echo "FAIL" ;;
    *) echo "FAIL" ;;
  esac
}

# train_pr_has_hold_label <labels-json>: true if any opt-out label present.
train_pr_has_hold_label() {
  local labels_json="$1"
  jq -e --arg hold "${TRAIN_LABEL_HOLD}" \
        --arg esc "${TRAIN_LABEL_ESCALATED}" \
        --arg legacy "${TRAIN_LEGACY_HOLD_LABEL}" \
    'any(.[]; .name == $hold or .name == $esc or .name == $legacy)' \
    <<<"${labels_json}" >/dev/null 2>&1
}

# train_select: emit the selected batch as JSON lines (one object per PR):
#   {number, headRefOid, createdAt, gate}
# Honors MAX_BATCH. Caller pipes through `jq -s .` if it wants an array.
#
# Inputs (overridable for testing):
#   TRAIN_PR_LIST_JSON — if set, used verbatim instead of calling gh (fixtures).
train_select() {
  local pr_list
  if [[ -n "${TRAIN_PR_LIST_JSON:-}" ]]; then
    pr_list="${TRAIN_PR_LIST_JSON}"
  else
    pr_list="$(gh pr list --base "${TRAIN_BASE_BRANCH}" --state open \
      --json number,headRefOid,isDraft,mergeable,mergeStateStatus,labels,createdAt,files \
      --limit 100)"
  fi

  # Oldest createdAt first.
  local ordered
  ordered="$(jq -c 'sort_by(.createdAt) | .[]' <<<"${pr_list}")"

  local count=0
  local line
  while IFS= read -r line; do
    [[ -z "${line}" ]] && continue
    local number isDraft mergeable labels
    number="$(jq -r '.number' <<<"${line}")"
    isDraft="$(jq -r '.isDraft' <<<"${line}")"
    mergeable="$(jq -r '.mergeable' <<<"${line}")"
    labels="$(jq -c '.labels // []' <<<"${line}")"

    if [[ "${isDraft}" == "true" ]]; then
      train_log "skip #${number}: draft"; continue
    fi
    if train_pr_has_hold_label "${labels}"; then
      train_log "skip #${number}: hold/escalated label"; continue
    fi
    if [[ "${mergeable}" != "MERGEABLE" ]]; then
      train_log "skip #${number}: mergeable=${mergeable}"; continue
    fi

    # CI Gate state. In fixture mode the caller may inject a `gate` field.
    local gate rollup
    gate="$(jq -r '.gate // empty' <<<"${line}")"
    if [[ -z "${gate}" ]]; then
      if [[ -n "${TRAIN_ROLLUP_JSON_FOR_PR:-}" ]]; then
        rollup="$("${TRAIN_ROLLUP_JSON_FOR_PR}" "${number}")"
      else
        rollup="$(gh pr view "${number}" --json statusCheckRollup \
          --jq '.statusCheckRollup' 2>/dev/null || echo '[]')"
      fi
      gate="$(train_select_ci_gate_state "${rollup}")"
    fi

    case "${gate}" in
      SUCCESS|FLAKE) : ;;
      *) train_log "skip #${number}: CI Gate=${gate}"; continue ;;
    esac

    jq -nc --argjson n "${number}" \
           --arg oid "$(jq -r '.headRefOid' <<<"${line}")" \
           --arg created "$(jq -r '.createdAt' <<<"${line}")" \
           --arg gate "${gate}" \
      '{number:$n, headRefOid:$oid, createdAt:$created, gate:$gate}'

    count=$((count + 1))
    if [[ "${count}" -ge "${MAX_BATCH}" ]]; then
      train_log "reached MAX_BATCH=${MAX_BATCH}"; break
    fi
  done <<<"${ordered}"
}
