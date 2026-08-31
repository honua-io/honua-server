#!/usr/bin/env bash
# Shared helpers for the honua-server optimistic batch merge train (Phase 1).
#
# This file is SOURCEABLE: it defines functions and constants but performs no
# work at source time. Every step lives in its own file (select.sh, assemble.sh,
# ...) and is independently testable; train.sh is the orchestrator.
#
# DRY-RUN CONTRACT (the safety bar):
#   TRAIN_APPLY (default 0) gates ALL state-mutating side effects.
#     - Real LOCAL git (fetch/merge/branch/abort) and real READ-ONLY GitHub
#       reads (gh pr list/view, gh run view) ALWAYS execute, in BOTH modes, so
#       a dry run exercises true conflict detection and true CI-status reads.
#     - `git push`, `gh pr merge`, `gh pr edit`, `gh pr comment`, `gh issue`
#       writes, `gh workflow run`, `gh run rerun`, and label writes are LOGGED
#       (train_side_effect ...) but NOT executed when TRAIN_APPLY != 1.
#   The merge-train.yml workflow defaults train_apply=false, so merging the PR
#   that adds this code can never make the train act. A human flips it live.
#
# No bot attribution anywhere: the train's own commits and comments must be
# authored as the repo owner with NO Co-Authored-By / "Generated with" / emoji
# lines. This is enforced in code (commit messages below) and is a hard rule.

set -euo pipefail

# --- configuration knobs (env-overridable) -----------------------------------
: "${TRAIN_APPLY:=0}"            # 0 = dry-run (default). 1 = live (writes act).
: "${MAX_BATCH:=3}"             # max PRs per batch.
: "${TRAIN_BASE_BRANCH:=trunk}" # base branch the train lands onto.
: "${TRAIN_REMOTE:=origin}"     # git remote.
: "${TRAIN_FWDFIX_CAP:=2}"      # max forward-fix (format) attempts per batch.
: "${TRAIN_FLAKE_RERUN_CAP:=1}" # max flake reruns per failing run.
: "${TRAIN_TIMEOUT_RERUN_CAP:=1}" # max timeout-only failed-job reruns per run.
: "${TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS:=6600}" # 110m inside controller's 120m cap.
: "${TRAIN_CONTROLLER_TIMEOUT_SECONDS:=7200}" # merge-train workflow job cap.
: "${TRAIN_CONTROLLER_SHUTDOWN_RESERVE_SECONDS:=300}" # state/summary persistence floor.

# Initialize one absolute polling deadline for the entire controller run. It is
# exported because batch execution is captured through command substitutions;
# every initial/retry wait must inherit the same deadline rather than resetting
# another 6600-second allowance. The configured budget leaves 600 seconds under
# the 120-minute job cap (and never less than the explicit 300-second reserve).
train_init_controller_deadline() {
  [[ -n "${TRAIN_CONTROLLER_DEADLINE_EPOCH:-}" ]] && return 0
  local usable=$((TRAIN_CONTROLLER_TIMEOUT_SECONDS - TRAIN_CONTROLLER_SHUTDOWN_RESERVE_SECONDS))
  local budget="${TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS}"
  [[ "${budget}" -gt "${usable}" ]] && budget="${usable}"
  [[ "${budget}" -gt 0 ]] || return 1
  TRAIN_CONTROLLER_DEADLINE_EPOCH=$(( $(train_now) + budget ))
  export TRAIN_CONTROLLER_DEADLINE_EPOCH
}

# Flake signature regex (classify-flake). A failing job whose log matches this
# is treated as environmental and rerun once; persistent recognized flakes may merge through. Generic timeout/exit-124 handling has higher precedence and persistent timeouts remain real.
# In addition to the line-scoped signatures below, train_log_is_flake requires
# Testcontainers, a failure-marked Ryuk event, and timeout/connection-failure
# evidence in the same failed-job log before treating a resource-reaper failure
# as a flake. The terms are often split across logger lines, so that correlation
# cannot live in this grep expression without making harmless Ryuk status lines
# sufficient.
# The Postgres deadlock signatures and the test harness's isolation
# race surfaces as transient schema-setup errors ("relation/column/schema ...
# does not exist", "does not exist at character N") when a test runs before its
# per-test schema is migrated (honua-server#1568/#2049). These are intermittent:
# the rerun clears a true race, while a genuine missing-migration bug reproduces
# and still escalates — so it is safe to treat them as flake candidates.
#
# The ASP.NET static-web-assets loader signature (StaticWebAssetsLoader / an
# obj/<config>/<tfm>/compressed/ path in a DirectoryNotFoundException) is the
# honua-server#2904 environmental race: the sharded Release test build did not
# reliably materialize the Blazor compressed-assets content root, so the test
# host aborted construction and failed EVERY test in the shard identically —
# which the attributor then blunt-escalated onto every PR in the batch. The
# host bootstrap fix (Program.cs skips the loader for API test hosts and hardens
# the opt-in demo path) removes the race; this signature is defense-in-depth so
# any residual/related static-web-assets content-root race is rerun-and-merged-
# through rather than escalating the whole batch.
: "${TRAIN_FLAKE_REGEX:=40P01|deadlock detected|relation .* does not exist|column .* does not exist|schema .* does not exist|does not exist at character|StaticWebAssetsLoader|obj/[^ ]*/compressed}"

# Heavy CI jobs the train treats as NON-BLOCKING. They run on EVERY batch (not
# shard-targeted), flake on environment (Docker registry, browser, JS/Python
# integration env), and would wedge every batch. The train lands on its SHARD
# results; real regressions in these are caught by post-merge trunk CI. The CI
# Gate / Test Suite Summary aggregators are included so a batch red ONLY on these
# (no real shard failure) lands. Newline-separated; env-overridable.
: "${TRAIN_NONBLOCKING_JOBS:=CI Gate
Test Suite Summary
Docker Build & Integration Test
Esri Leaflet Browser Tests
JavaScript Integration Tests
Python Integration Tests}"

# Labels (the train's vocabulary). The existing repo `hold` label is ALSO
# honored as an opt-out (documented "merge-train opt-out") in addition to the
# canonical train:hold below.
: "${TRAIN_LABEL_HOLD:=train:hold}"
: "${TRAIN_LABEL_ESCALATED:=train:escalated}"
: "${TRAIN_LABEL_LANDING:=train:landing}"
: "${TRAIN_LABEL_STATE:=train:state}"
: "${TRAIN_LEGACY_HOLD_LABEL:=hold}" # pre-existing opt-out label, also honored.

# Resolve the repo root relative to this script so the train works from any cwd.
TRAIN_LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TRAIN_REPO_ROOT="${TRAIN_REPO_ROOT:-$(cd "${TRAIN_LIB_DIR}/../../.." && pwd)}"
TRAIN_SHARDS_CONFIG="${TRAIN_SHARDS_CONFIG:-${TRAIN_REPO_ROOT}/.github/ci-shards.json}"
TRAIN_TARGETED_SCRIPT="${TRAIN_TARGETED_SCRIPT:-${TRAIN_REPO_ROOT}/scripts/ci/honua-server-targeted-tests.sh}"

# --- logging ------------------------------------------------------------------
# Visibility was half the prior CI nightmare, so every step emits a clear,
# greppable, leveled line. The base helpers stay backward-compatible; the
# component-aware helpers below add a [step] segment and a level so the run log
# reads `[train][select][INFO] ...`, `[train][assemble][DECISION] ...`, etc.
#
# TRAIN_STEP is the active pipeline step (set by train_step_begin). Levels:
#   INFO     — routine progress.
#   WARN     — recoverable anomaly (skips, caps, re-assembles).
#   DECISION — a choice the train made (included/skipped/escalated/landed).
: "${TRAIN_STEP:=train}"

train_log()  { _train_emit INFO "$*"; }
train_warn() { _train_emit WARN "$*"; }
train_err()  { _train_emit ERROR "$*"; }

# train_has_content <text>: 0 when the text holds at least one non-whitespace
# character; 1 when it is empty or whitespace-only.
#
# ALWAYS use this instead of `[[ -n "${text//[[:space:]]/}" ]]`. Bash's pattern
# substitution builds a new string one match at a time and is QUADRATIC in the
# input, which is invisible on a job-name list and ruinous on a job log. Measured
# on a realistic shard log (timestamped test lines): 84 KB 0.14s, 170 KB 0.56s,
# 342 KB 2.1s, 686 KB 8.4s — ~4x per doubling, so a real multi-MB 47k-line shard
# log costs MINUTES of pure CPU per emptiness check. The merge train performs one
# of these per failing job on every classification pass. The regex form stops at
# the first non-whitespace byte and measured 3-9ms across that whole range.
train_has_content() {
  [[ "$1" =~ [^[:space:]] ]]
}

# train_read_job_log <job-id>: read one completed Actions job log without
# depending on the parent workflow having finished publishing its aggregate
# log archive. `gh run view --job --log` can remain unavailable for minutes
# after the job itself is terminal (observed on Smart CI run 31940825557),
# while the job-log REST endpoint is already readable. Read the exact job REST
# resource first so an unavailable aggregate archive cannot block terminal
# classification, retaining the familiar CLI surface only as fallback. Tests
# may inject TRAIN_JOB_LOG_READER.
train_read_job_log() {
  local job_id="$1" text
  if [[ -n "${TRAIN_JOB_LOG_READER:-}" ]]; then
    "${TRAIN_JOB_LOG_READER}" "${job_id}"
    return
  fi
  if text="$(gh api "repos/${GITHUB_REPOSITORY:-honua-io/honua-server}/actions/jobs/${job_id}/logs" 2>/dev/null)" \
    && train_has_content "${text}"; then
    printf '%s\n' "${text}"
    return 0
  fi
  if text="$(gh run view --repo "${GITHUB_REPOSITORY:-honua-io/honua-server}" \
      --job "${job_id}" --log 2>/dev/null)" \
    && train_has_content "${text}"; then
    printf '%s\n' "${text}"
    return 0
  fi
  return 1
}

# train_read_job_annotations <job-id>: read the small, paginated annotation set
# attached to an exact Actions check/job. Timeout and capacity markers are
# emitted with workflow commands and therefore appear here without downloading
# a multi-megabyte console log. Tests may inject TRAIN_JOB_ANNOTATION_READER.
train_read_job_annotations() {
  local job_id="$1"
  if [[ -n "${TRAIN_JOB_ANNOTATION_READER:-}" ]]; then
    "${TRAIN_JOB_ANNOTATION_READER}" "${job_id}"
    return
  fi
  gh api --paginate \
    "repos/${GITHUB_REPOSITORY:-honua-io/honua-server}/check-runs/${job_id}/annotations?per_page=100" \
    --jq '.[] | [(.message // ""), (.raw_details // "")] | @tsv' \
    2>/dev/null
}

# train_csv_remove_exact <csv> <value>: remove an exact non-empty member from
# a comma-separated list. `grep -v` returns 1 when it removes the sole member;
# under `set -e -o pipefail` that aborted the train before all-dropped cleanup,
# metrics, and state persistence. jq always emits the resulting CSV, including
# the legitimate empty string.
train_csv_remove_exact() {
  local csv="$1" value="$2"
  jq -Rr --arg value "${value}" \
    'split(",") | map(select(length > 0 and . != $value)) | join(",")' \
    <<<"${csv}"
}

# Publish the one authoritative cross-step proof that this controller observed
# its exact batch on trunk and persisted the matching post-land journal. The
# workflow consumes this output to permit at most one immediate reconciliation
# continuation. Metrics are intentionally excluded from this control path.
train_publish_landing_step_output() {
  local landed_sha="${1:?landed SHA is required}"
  [[ "${landed_sha}" =~ ^[0-9a-f]{40,64}$ ]] || {
    train_err "refusing to publish malformed landed SHA: ${landed_sha}"
    return 1
  }
  [[ -n "${GITHUB_OUTPUT:-}" ]] || return 0
  {
    printf 'landed_this_run=true\n'
    printf 'landed_batch_sha=%s\n' "${landed_sha}"
  } >>"${GITHUB_OUTPUT}"
}
# train_decision: a first-class DECISION line — the thing the founder grep's for.
train_decision() { _train_emit DECISION "$*"; }

# _train_emit <level> <msg...>: the single formatter for every log line.
# Format: [train][<step>][<level>] <msg> (the [<step>] segment is omitted when no
# step group is active so generic lines read [train][INFO] ...). All go to stderr
# so stdout stays the scripts' machine channel (batch branch name, JSON, etc.).
_train_emit() {
  local level="$1"; shift
  if [[ -n "${TRAIN_STEP:-}" && "${TRAIN_STEP}" != "train" ]]; then
    printf '[train][%s][%s] %s\n' "${TRAIN_STEP}" "${level}" "$*" >&2
  else
    printf '[train][%s] %s\n' "${level}" "$*" >&2
  fi
}

# --- GitHub Actions grouping + annotations (no-op off-Actions) ----------------
# Each pipeline step's output is wrapped in ::group::/::endgroup:: so the
# workflow log is collapsible per step. Off Actions ($GITHUB_ACTIONS unset) these
# print a plain banner to stderr so a local dry-run still reads cleanly.
train_group() {
  TRAIN_STEP="$1"
  if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
    printf '::group::[train] %s — %s\n' "$1" "${2:-}" >&2
  else
    printf '\n----- [train] %s — %s -----\n' "$1" "${2:-}" >&2
  fi
}
train_endgroup() {
  [[ -n "${GITHUB_ACTIONS:-}" ]] && printf '::endgroup::\n' >&2 || true
  TRAIN_STEP="train"
}

# train_notice / train_annotate_warn: surface landings + escalations in the run's
# annotations (the Actions summary banner). No-op (but still logged) off Actions.
train_notice() {
  [[ -n "${GITHUB_ACTIONS:-}" ]] && printf '::notice::%s\n' "$*" >&2 || true
  train_decision "$*"
}
train_annotate_warn() {
  [[ -n "${GITHUB_ACTIONS:-}" ]] && printf '::warning::%s\n' "$*" >&2 || true
  train_warn "$*"
}

# --- step timing --------------------------------------------------------------
# train_step_begin <name> records a monotonic start; train_step_end <name> emits
# the elapsed seconds and appends "<name>=<secs>" to TRAIN_TIMINGS_FILE (if set)
# for the metrics JSON. Uses SECONDS-derived epoch math (portable, no bc).
train_now() { date -u +%s; }
declare -A _TRAIN_STEP_START 2>/dev/null || true
train_step_begin() { _TRAIN_STEP_START["$1"]="$(train_now)"; }
train_step_end() {
  local name="$1" start now dur
  start="${_TRAIN_STEP_START[$name]:-}"
  [[ -z "${start}" ]] && return 0
  now="$(train_now)"; dur=$(( now - start ))
  train_log "phase '${name}' took ${dur}s"
  [[ -n "${TRAIN_TIMINGS_FILE:-}" ]] && printf '%s=%s\n' "${name}" "${dur}" >>"${TRAIN_TIMINGS_FILE}"
  printf '%s' "${dur}"
}

# --- Step Summary dashboard ---------------------------------------------------
# train_summary <markdown-line...>: append a line to $GITHUB_STEP_SUMMARY (the
# Actions Summary tab) AND mirror it to stdout so a local dry-run prints the same
# dashboard. TRAIN_SUMMARY_FILE overrides the sink (tests / local capture).
train_summary() {
  local sink="${TRAIN_SUMMARY_FILE:-${GITHUB_STEP_SUMMARY:-}}"
  if [[ -n "${sink}" ]]; then
    printf '%s\n' "$*" >>"${sink}"
  fi
  printf '%s\n' "$*"
}

# train_side_effect: the single chokepoint for every state-mutating action.
# In live mode (TRAIN_APPLY=1) it executes the command; otherwise it logs the
# command it WOULD run and returns success. ALL pushes/merges/edits/comments/
# issue-writes/label-writes/workflow-dispatches MUST go through this so dry-run
# is provably read-only.
train_side_effect() {
  if [[ "${TRAIN_APPLY}" == "1" ]]; then
    train_log "APPLY: $*"
    # GitHub SECONDARY rate limits surface mid-burst as 401 "Bad credentials",
    # 403/429, or "secondary rate limit"/"submitted too quickly" EVEN WITH a valid
    # token and primary quota remaining (observed: 10-PR label burst 401'd on the
    # 5th write after 4 succeeded). Those are transient, so back off and retry
    # rather than failing the whole run. stderr is captured for inspection; stdout
    # flows through untouched so callers that capture command output still work.
    local attempt=1 max="${TRAIN_SIDE_EFFECT_RETRIES:-4}" delay="${TRAIN_SIDE_EFFECT_BACKOFF:-5}" rc errf
    errf="$(mktemp 2>/dev/null || echo /tmp/train_se_err.$$)"
    while :; do
      # Run directly (NOT inside `if`) so $? is the COMMAND's exit code, not the
      # if-statement's (which is 0 when the condition is false → would mask failures).
      "$@" 2>"${errf}"
      rc=$?
      if [[ "${rc}" -eq 0 ]]; then
        cat "${errf}" >&2 2>/dev/null || true
        rm -f "${errf}"
        return 0
      fi
      if [[ "${attempt}" -lt "${max}" ]] && grep -qiE \
        "Bad credentials|secondary rate limit|rate limit|submitted too quickly|retry your request|abuse detection|status code: 40[39]|status code: 429|HTTP 40[39]|HTTP 429" \
        "${errf}" 2>/dev/null; then
        train_log "transient GitHub API failure (attempt ${attempt}/${max}, rc=${rc}); backing off ${delay}s and retrying"
        sleep "${delay}"
        attempt=$(( attempt + 1 )); delay=$(( delay * 3 )); : > "${errf}"
        continue
      fi
      cat "${errf}" >&2 2>/dev/null || true
      rm -f "${errf}"
      return "${rc}"
    done
  else
    train_log "DRY-RUN (skipped): $*"
    return 0
  fi
}

# train_have: is a command available?
train_have() { command -v "$1" >/dev/null 2>&1; }

train_require() {
  local missing=0 c
  for c in "$@"; do
    if ! train_have "$c"; then train_err "missing required command: $c"; missing=1; fi
  done
  [[ "${missing}" -eq 0 ]]
}

# --- shard-terminal marker classification (pure, testable) -------------------
# scripts/ci/run-server-test-shard.sh ends a shard that could not finish with
# exactly one of these annotations, all in the same shape:
#   ::error::HONUA_SHARD_<KIND> shard='<name>' ...
#   * CAPACITY_EXHAUSTED  burned its whole CONFIGURED budget while still
#                         producing test output. Over capacity; a rerun just
#                         reproduces it at full runner cost.
#   * HANG_SUSPECTED      hit the same cap after going silent. A genuine hang,
#                         and the one shape that still earns a bounded rerun.
#   * KILLED              SIGKILLed before the runner's own kill deadline, so
#                         not a timeout at all — suspect an OOM kill.
# None of the three is a comparable failure CAUSE: the shard never finished
# executing its tests, so it produced no verdict the pre-existing-failure filter
# may compare against trunk. These predicates therefore gate the retry budget
# (classify-timeout.sh), the pre-existing-failure subtraction (preexisting.sh)
# and the early-failure observer, which is why they live here in lib.sh as the
# single shared definition.
#
# MATCHING IS ANCHORED ON PURPOSE. An unanchored `grep -F` for the bare token
# also matches prose that merely NAMES the token: the merge train's own warning
# text does, so `validate-timeout-retry.sh` prints it and every `CI Router
# Validation` job log contains it. That made a router failure permanently
# non-subtractable and escalated whole batches for a failure trunk already had.
# A real marker is the first token of its line — after an optional log timestamp
# and the `::error::`/`##[error]` workflow-command prefix (GitHub renders the
# former as the latter in downloaded logs, and the check-run annotation API
# strips it entirely) — and is immediately followed by ` shard=`.
train_log_has_shard_marker() {
  local marker="$1" text="$2"
  grep -Eq "(^|[[:space:]])((##\[error\]|::error::)[[:space:]]*)?${marker}[[:space:]]+shard=" \
    <<<"${text}"
}

# train_log_is_capacity_exhaustion <log-text>: the shard was over capacity.
train_log_is_capacity_exhaustion() {
  train_log_has_shard_marker HONUA_SHARD_CAPACITY_EXHAUSTED "$1"
}

# train_log_is_shard_hang <log-text>: the shard stalled and was capped.
train_log_is_shard_hang() {
  train_log_has_shard_marker HONUA_SHARD_HANG_SUSPECTED "$1"
}

# train_log_is_shard_killed <log-text>: the shard's test host was SIGKILLed.
train_log_is_shard_killed() {
  train_log_has_shard_marker HONUA_SHARD_KILLED "$1"
}

# train_log_is_matrix_leg_inner_timeout <log-text>: a matrix leg's OWN inner
# `timeout` wrapper fired. ci.yml matrix legs that split an inner test budget
# from their job cap (currently the postgres-compat legs) end a hung leg with:
#   ::error::HONUA_MATRIX_LEG_INNER_TIMEOUT image='<tag>' ...
# Anchored exactly like the HONUA_SHARD_* markers above and for the same
# reason: prose that merely NAMES the token (the train's own warnings, this
# repo's fixtures) must never match.
train_log_is_matrix_leg_inner_timeout() {
  grep -Eq "(^|[[:space:]])((##\[error\]|::error::)[[:space:]]*)?HONUA_MATRIX_LEG_INNER_TIMEOUT[[:space:]]+image=" \
    <<<"$1"
}

# train_join_job_names <newline-separated-jobs>: a human-readable, comma-joined
# job list for escalation comments, or a neutral phrase when none are known. An
# escalation that cannot name what failed is not actionable.
train_join_job_names() {
  local joined
  joined="$(printf '%s\n' "$1" | sed '/^$/d' | paste -sd ',' - | sed 's/,/, /g')"
  [[ -n "${joined}" ]] || joined="the selected server-test shard"
  printf '%s' "${joined}"
}

# --- flake classification (pure, testable) -----------------------------------
# train_log_is_flake <log-text>: returns 0 if the text matches a line-scoped
# flake regex or contains correlated Testcontainers/Ryuk failure evidence.
train_log_is_flake() {
  local text="$1"
  if [[ -n "${TRAIN_FLAKE_REGEX}" ]] &&
      grep -Eq "${TRAIN_FLAKE_REGEX}" <<<"${text}"; then
    return 0
  fi

  # Testcontainers may identify itself near the start of a large job log, then
  # report the resource-reaper identity and transport error on separate lines
  # much later. Read the text once: this avoids grep -q closing a pipe early
  # under pipefail, while still requiring both a failure-marked,
  # non-configuration Ryuk event and transport evidence in the same failed-job
  # log. In particular, neither `testcontainers.ryuk.disabled=false` nor a
  # successful Ryuk status plus an unrelated timeout is a resource-reaper
  # failure that may authorize optimistic merge-through.
  awk '
    {
      lines[NR] = tolower($0)
      if (lines[NR] ~ /testcontainers/) has_testcontainers = 1
    }
    END {
      if (!has_testcontainers) exit 1
      for (i = 1; i <= NR; i++) {
        ryuk_event = lines[i]
        gsub(/testcontainers[._]ryuk[._]disabled[[:space:]]*[:=][[:space:]]*(true|false)/, "", ryuk_event)
        if (ryuk_event ~ /ryuk/ &&
            ryuk_event ~ /(timed out|connection refused|fail(ed|ure)?|did not|not .*ready|unable|cannot|can not|could not|error)/) {
          has_ryuk_failure = 1
        }
        if (lines[i] ~ /(timed out|connection refused)/) has_transport_error = 1
      }
      exit !(has_ryuk_failure && has_transport_error)
    }
  ' <<<"${text}"
}

# --- attribution (pure, testable) --------------------------------------------
# train_attribute_culprits: given a newline-separated list of failing-shard
# paths (prefixes) on stdin via $1, and a set of "PR\tfile" lines on $2,
# emit the PR numbers whose changed files hit any of those prefixes (unique).
# This is the REVERSE map: failing shard -> paths[] -> which batched PR touched.
train_attribute_culprits() {
  local shard_paths="$1" pr_files="$2"
  awk -v paths="${shard_paths}" '
    BEGIN {
      n = split(paths, parr, "\n")
    }
    {
      pr = $1; file = substr($0, index($0, "\t") + 1)
      for (i = 1; i <= n; i++) {
        p = parr[i]
        if (p == "") continue
        if (index(file, p) == 1) { hit[pr] = 1 }
      }
    }
    END { for (k in hit) print k }
  ' <<<"${pr_files}" | sort -u
}

# train_shard_paths: emit the .paths[] for a shard name from ci-shards.json.
train_shard_paths() {
  local shard_name="$1" config="${2:-${TRAIN_SHARDS_CONFIG}}"
  jq -r --arg n "${shard_name}" \
    '.shards[] | select(.name == $n) | .paths[]' "${config}"
}

# --- machine-readable metrics (per-run counters) ------------------------------
# A flat key=value counter store backed by TRAIN_METRICS_KV (a temp file). The
# orchestrator bumps counters as decisions are made, then train_metrics_render
# emits a single merge-train-metrics.json uploaded as a workflow artifact.
#
# Counter keys (documented in ADR-0055 "Instrumentation"):
#   candidates selected included landed escalated
#   skipped_conflict flake_reruns forward_fixes attribution_drops
#   smartci_shard_count
# Plus the run outcome (string) and per-phase durations (from TRAIN_TIMINGS_FILE).
train_metric_set() {  # key value
  [[ -z "${TRAIN_METRICS_KV:-}" ]] && return 0
  printf '%s=%s\n' "$1" "$2" >>"${TRAIN_METRICS_KV}"
}
# train_metric_get <key> [default]: last-write-wins read of a counter.
train_metric_get() {
  local key="$1" def="${2:-0}" val
  [[ -z "${TRAIN_METRICS_KV:-}" || ! -s "${TRAIN_METRICS_KV:-/dev/null}" ]] && { printf '%s' "${def}"; return 0; }
  val="$(grep -E "^${key}=" "${TRAIN_METRICS_KV}" | tail -1 | cut -d= -f2-)"
  printf '%s' "${val:-${def}}"
}
train_metric_inc() {  # key [by]
  local key="$1" by="${2:-1}" cur
  cur="$(train_metric_get "${key}" 0)"
  train_metric_set "${key}" "$(( cur + by ))"
}

# train_timings_json: turn the "<phase>=<secs>" lines into a JSON object.
train_timings_json() {
  local f="${TRAIN_TIMINGS_FILE:-}"
  if [[ -z "${f}" || ! -s "${f}" ]]; then echo '{}'; return 0; fi
  # Last write wins per phase so a re-run phase (re-assemble) reports its latest.
  awk -F= 'NF>=2 {t[$1]=$2} END {for (k in t) print k"="t[k]}' "${f}" \
    | jq -R 'split("=") | {(.[0]): (.[1]|tonumber)}' | jq -s 'add // {}'
}

# train_metrics_render <run_timestamp> <mode> <trunk_sha> <last_landed_sha> <outcome> [shard_descriptor]
#   Emit the full merge-train-metrics.json document on stdout.
train_metrics_render() {
  local ts="$1" mode="$2" trunk_sha="$3" last_landed="$4" outcome="$5" shard_desc="${6:-}"
  local shard_count run_all early_failure
  shard_count="$(train_metric_get smartci_shard_count 0)"
  run_all="false"
  if [[ -n "${shard_desc}" ]] && jq -e . >/dev/null 2>&1 <<<"${shard_desc}"; then
    run_all="$(jq -r '.run_all // false' <<<"${shard_desc}")"
  fi
  [[ "${run_all}" != "true" && "${run_all}" != "false" ]] && run_all="false"
  local last_json="null"; [[ -n "${last_landed}" && "${last_landed}" != "null" ]] && last_json="\"${last_landed}\""
  early_failure="null"
  if [[ -n "${TRAIN_EARLY_FAILURE_FILE:-}" && -s "${TRAIN_EARLY_FAILURE_FILE}" ]]; then
    early_failure="$(jq -c . "${TRAIN_EARLY_FAILURE_FILE}" 2>/dev/null || echo null)"
  fi

  jq -n \
    --arg ts "${ts}" \
    --arg mode "${mode}" \
    --arg trunk "${trunk_sha}" \
    --argjson last "${last_json}" \
    --arg outcome "${outcome}" \
    --argjson candidates "$(train_metric_get candidates 0)" \
    --argjson selected "$(train_metric_get selected 0)" \
    --argjson included "$(train_metric_get included 0)" \
    --argjson landed "$(train_metric_get landed 0)" \
    --argjson escalated "$(train_metric_get escalated 0)" \
    --argjson skipped_conflict "$(train_metric_get skipped_conflict 0)" \
    --argjson skipped_select "$(train_metric_get skipped_select 0)" \
    --argjson flake_reruns "$(train_metric_get flake_reruns 0)" \
    --argjson timeout_reruns "$(train_metric_get timeout_reruns 0)" \
    --argjson forward_fixes "$(train_metric_get forward_fixes 0)" \
    --argjson preexisting_passes "$(train_metric_get preexisting_passes 0)" \
    --argjson autofix_attempts "$(train_metric_get autofix_attempts 0)" \
    --argjson autofix_fixes "$(train_metric_get autofix_fixes 0)" \
    --argjson attribution_drops "$(train_metric_get attribution_drops 0)" \
    --argjson shard_count "${shard_count:-0}" \
    --argjson run_all "${run_all:-false}" \
    --argjson early_failure "${early_failure}" \
    --argjson durations "$(train_timings_json)" \
    '{
      schema: "honua.merge-train.metrics/v1",
      run_timestamp: $ts,
      mode: $mode,
      outcome: $outcome,
      trunk_sha: $trunk,
      last_landed_sha: $last,
      counts: {
        candidates: $candidates,
        selected: $selected,
        skipped_by_reason: { select_ineligible: $skipped_select, assemble_conflict: $skipped_conflict },
        included: $included,
        landed: $landed,
        escalated: $escalated,
        flake_reruns: $flake_reruns,
        timeout_reruns: $timeout_reruns,
        forward_fixes: $forward_fixes,
        preexisting_passes: $preexisting_passes,
        autofix_attempts: $autofix_attempts,
        autofix_fixes: $autofix_fixes,
        attribution_drops: $attribution_drops
      },
      smart_ci: { shard_count: $shard_count, run_all: $run_all },
      early_failure_observation: $early_failure,
      phase_durations_seconds: $durations
    }'
}
