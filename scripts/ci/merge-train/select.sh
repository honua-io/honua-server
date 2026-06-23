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

# Real-gate job names: a CI Gate failure caused by ANY of these is never a flake
# (a human must fix it), so the whole PR stays FAIL even under merge-through. The
# pattern is matched with grep -E against each failing leaf job's name.
: "${TRAIN_REAL_GATE_JOB_REGEX:=Build & Format|Analyze C#|\.NET Foundation Tests|Architecture|CI Router|OpenAPI|drift}"
# Aggregator-only job names: these are roll-ups (the required "CI Gate" check and
# the shard fan-in summary). When the ONLY failing jobs are aggregators, a shard
# was cancelled/flaked underneath them — treat that as a flake, not a real break.
: "${TRAIN_AGGREGATOR_JOB_REGEX:=^(CI Gate|Test Suite Summary)$}"

# train_select_run_id_from_rollup <gate-json>: extract the Actions run id from a
# CI Gate CheckRun's detailsUrl (.../actions/runs/<id>/job/<jobid>). Empty if
# unparseable. Test override: TRAIN_SELECT_RUN_ID forces a fixed id.
train_select_run_id_from_rollup() {
  if [[ -n "${TRAIN_SELECT_RUN_ID:-}" ]]; then printf '%s' "${TRAIN_SELECT_RUN_ID}"; return 0; fi
  local gate="$1" url
  url="$(jq -r '.detailsUrl // ""' <<<"${gate}")"
  printf '%s' "${url}" | grep -oE 'runs/[0-9]+' | grep -oE '[0-9]+' | head -1
}

# train_select_run_failed_jobs <run-id>: emit the run's non-successful leaf jobs,
# one per line as "<conclusion><TAB><name>" (conclusion lower-cased, e.g.
# "failure" / "cancelled" / "timed_out"). Live path uses `gh run view --json
# jobs`. Test override: TRAIN_SELECT_FAILED_JOBS_FOR_RUN <cmd> is called with the
# run id and must print the SAME "<conclusion>\t<name>" lines (offline fixtures,
# no network).
train_select_run_failed_jobs() {
  local run_id="$1"
  if [[ -n "${TRAIN_SELECT_FAILED_JOBS_FOR_RUN:-}" ]]; then
    "${TRAIN_SELECT_FAILED_JOBS_FOR_RUN}" "${run_id}"
    return 0
  fi
  [[ -z "${run_id}" ]] && return 0
  gh run view "${run_id}" --json jobs \
    --jq '.jobs[]
          | select(.conclusion=="failure" or .conclusion=="cancelled"
                   or .conclusion=="timed_out" or .conclusion=="startup_failure")
          | (.conclusion) + "\t" + (.name)' \
    2>/dev/null || true
}

# train_select_job_log <run-id> <job-name>: emit the failing job's log text for
# flake-regex matching. Live path uses `gh run view --log-failed`. Test override:
# TRAIN_SELECT_JOB_LOG_FOR <cmd> is called with (run id, job name).
train_select_job_log() {
  local run_id="$1" job="$2"
  if [[ -n "${TRAIN_SELECT_JOB_LOG_FOR:-}" ]]; then
    "${TRAIN_SELECT_JOB_LOG_FOR}" "${run_id}" "${job}"
    return 0
  fi
  [[ -z "${run_id}" ]] && return 0
  gh run view "${run_id}" --log-failed 2>/dev/null || true
}

# train_select_failure_is_flake_only <run-id>: merge-through-flakes classifier.
# Inspects the non-successful leaf jobs of the single latest CI run and decides
# whether a CI Gate FAILURE is flake-only (downgradeable to FLAKE) or a real
# break (FAIL). Returns 0 = flake-only (=> FLAKE), 1 = real break OR
# undeterminable (=> FAIL).
#
# Each input line is "<conclusion>\t<name>". Rules (conservative — default to
# FAIL on any uncertainty):
#   * NO non-successful jobs fetched (jobs unavailable) => FAIL.
#   * ANY failing job whose NAME matches the real-gate regex => FAIL (a human
#     must fix it). Note: only an actual `failure` real-gate job is
#     authoritative; a `cancelled` real-gate job is just runner starvation.
#   * Otherwise every non-successful job must be one of:
#       - an aggregator-only roll-up (CI Gate / Test Suite Summary — a shard
#         under them cancelled/flaked), OR
#       - a CANCELLED / TIMED_OUT / STARTUP_FAILURE shard (runner starvation /
#         cancel-cascade — treated as flake), OR
#       - a `failure` shard whose log matches the flake regex.
#     If even one `failure` shard's log does NOT match (or can't be fetched),
#     => FAIL.
train_select_failure_is_flake_only() {
  local run_id="$1"
  local jobs line concl job
  jobs="$(train_select_run_failed_jobs "${run_id}")"
  # No jobs => can't prove it's a flake; be conservative.
  [[ -z "${jobs}" ]] && return 1

  # A real-gate job that actually FAILED is authoritative: never a flake.
  if printf '%s\n' "${jobs}" \
       | grep -E '^failure'"$(printf '\t')" \
       | grep -Eq "${TRAIN_REAL_GATE_JOB_REGEX}"; then
    return 1
  fi

  # Every remaining non-successful job must be an aggregator roll-up, a
  # cancelled/timed-out/startup-failure shard, or a flake-log `failure` shard.
  while IFS= read -r line; do
    [[ -z "${line}" ]] && continue
    concl="${line%%$'\t'*}"
    job="${line#*$'\t'}"
    if printf '%s' "${job}" | grep -Eq "${TRAIN_AGGREGATOR_JOB_REGEX}"; then
      continue   # aggregator-only failure => underlying shard cancelled/flaked
    fi
    case "${concl}" in
      cancelled|timed_out|startup_failure)
        continue ;;   # runner starvation / cancel-cascade => flake
    esac
    # A `failure` shard job: its log must match the flake regex.
    local log
    log="$(train_select_job_log "${run_id}" "${job}")"
    if ! train_log_is_flake "${log}"; then
      return 1   # real shard failure (or unfetchable log) => not flake-only
    fi
  done <<<"${jobs}"
  return 0
}

# train_select_ci_gate_state <pr-json> [gate-json]: classify the CI Gate check
# for one PR. Emits one of: SUCCESS | FLAKE | FAIL | PENDING | MISSING.
# Reads the statusCheckRollup entry named "CI Gate" (the single required check).
# A failing CI Gate is downgraded to FLAKE only when the failing run's leaf jobs
# are flake-only (aggregator-only roll-ups and/or flake-regex log matches), so
# the train can process its flaky backlog. Real-gate-job failures stay FAIL.
# Bounded: inspects only the SINGLE latest CI run for the PR.
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
    SUCCESS) echo "SUCCESS"; return 0 ;;
    FAILURE|TIMED_OUT|CANCELLED|STARTUP_FAILURE|ACTION_REQUIRED) : ;;
    *) echo "FAIL"; return 0 ;;
  esac

  # CI Gate failed. Merge-through-flakes: downgrade to FLAKE iff the failure is
  # flake-only. Inspect the single latest run's failing leaf jobs.
  local run_id
  run_id="$(train_select_run_id_from_rollup "${gate}")"
  if train_select_failure_is_flake_only "${run_id}"; then
    echo "FLAKE"
  else
    echo "FAIL"
  fi
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
