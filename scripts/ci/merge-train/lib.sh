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

# Flake signature regex (classify-flake). A failing job whose log matches this
# is treated as environmental and rerun once; reproducing twice => real.
: "${TRAIN_FLAKE_REGEX:=40P01|deadlock detected|ryuk|Testcontainers.*(timed out|connection refused)}"

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
train_log()  { printf '[train] %s\n' "$*" >&2; }
train_warn() { printf '[train][warn] %s\n' "$*" >&2; }
train_err()  { printf '[train][error] %s\n' "$*" >&2; }

# train_side_effect: the single chokepoint for every state-mutating action.
# In live mode (TRAIN_APPLY=1) it executes the command; otherwise it logs the
# command it WOULD run and returns success. ALL pushes/merges/edits/comments/
# issue-writes/label-writes/workflow-dispatches MUST go through this so dry-run
# is provably read-only.
train_side_effect() {
  if [[ "${TRAIN_APPLY}" == "1" ]]; then
    train_log "APPLY: $*"
    "$@"
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

# --- flake classification (pure, testable) -----------------------------------
# train_log_is_flake <log-text>: returns 0 if the text matches the flake regex.
train_log_is_flake() {
  local text="$1"
  printf '%s' "${text}" | grep -Eq "${TRAIN_FLAKE_REGEX}"
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
