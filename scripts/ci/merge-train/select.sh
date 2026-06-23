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
      --json number,headRefOid,isDraft,mergeable,mergeStateStatus,labels,createdAt,files,author \
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
           --arg author "$(jq -r '.author.login // .author.name // "?"' <<<"${line}")" \
      '{number:$n, headRefOid:$oid, createdAt:$created, gate:$gate, author:$author}'

    count=$((count + 1))
    if [[ "${count}" -ge "${MAX_BATCH}" ]]; then
      train_log "reached MAX_BATCH=${MAX_BATCH}"; break
    fi
  done <<<"${ordered}"
}

# --- Phase 2 gate: overlap-dependency judgment (gated LLM) --------------------
# When two candidate PRs heavily overlap in the files they change, landing the
# newer one first can silently strand a logical dependency. The deterministic
# Phase-1 behavior keeps strict oldest-first ordering; Phase 2 OPTIONALLY asks
# Bedrock, only in the ambiguous (heavy-overlap) case, whether the later PR (B)
# should wait for the earlier PR (A) to land first.
#
# train_pr_overlap_ratio <filesA-newline> <filesB-newline>: emit an integer
# percentage (0-100) of B's files that also appear in A (|A∩B|/|B|). Pure +
# testable; no network.
train_pr_overlap_ratio() {
  local files_a="$1" files_b="$2"
  awk -v a="${files_a}" '
    BEGIN { n = split(a, arr, "\n"); for (i=1;i<=n;i++) if (arr[i]!="") inA[arr[i]]=1 }
    { if ($0 != "") { tot++; if ($0 in inA) hit++ } }
    END { if (tot == 0) { print 0 } else { printf "%d\n", (hit*100)/tot } }
  ' <<<"${files_b}"
}

# train_select_should_wait <prA> <filesA-newline> <prB> <filesB-newline>:
# Decide whether candidate PR B should WAIT for PR A (B is the later/newer one).
# Returns 0 = WAIT (skip B this batch), 1 = proceed (include B).
#
# Deterministic fallback (TRAIN_LLM=0, Bedrock error, or below-threshold
# overlap): NEVER wait — keep oldest-first ordering, i.e. exactly Phase 1.
# The LLM is consulted ONLY when overlap >= TRAIN_OVERLAP_PCT (default 60),
# which is the "ambiguous" condition. Logs prompt-class + decision via train_log.
: "${TRAIN_OVERLAP_PCT:=60}"
train_select_should_wait() {
  local pr_a="$1" files_a="$2" pr_b="$3" files_b="$4"

  # Deterministic guard first: only an ambiguous heavy overlap consults the LLM.
  local ratio; ratio="$(train_pr_overlap_ratio "${files_a}" "${files_b}")"
  if [[ "${ratio}" -lt "${TRAIN_OVERLAP_PCT}" ]]; then
    return 1   # not ambiguous: proceed (Phase-1 ordering)
  fi
  if ! declare -F bedrock_enabled >/dev/null 2>&1 || ! bedrock_enabled; then
    train_log "llm[select.overlap] disabled; fallback=PROCEED (#${pr_b} not held; overlap ${ratio}%)"
    return 1   # fallback: keep oldest-first, include B
  fi

  local sys usr ans
  sys="You order code-review pull requests for a merge train. Two PRs change a heavily overlapping set of files. Answer with exactly one word: YES if PR B should wait for PR A to land first (e.g. B builds on A or would conflict/strand A), or NO if they are independent and B can land now. Output only YES or NO."
  usr="PR A (#${pr_a}) changed files:
$(printf '%s\n' "${files_a}" | sed '/^$/d')

PR B (#${pr_b}) changed files:
$(printf '%s\n' "${files_b}" | sed '/^$/d')

File overlap of B onto A: ${ratio}%. Should PR B wait for PR A to land first? Answer YES or NO."
  ans="$(bedrock_ask "${sys}" "${usr}")"

  if bedrock_is_error "${ans}"; then
    train_log "llm[select.overlap] bedrock error; fallback=PROCEED (#${pr_b} not held)"
    return 1   # fallback on any LLM failure
  fi
  if bedrock_first_word_yes "${ans}"; then
    train_log "llm[select.overlap] decision=WAIT (#${pr_b} waits for #${pr_a}; overlap ${ratio}%)"
    return 0   # hold B this batch
  fi
  train_log "llm[select.overlap] decision=PROCEED (#${pr_b} independent of #${pr_a}; overlap ${ratio}%)"
  return 1
}
