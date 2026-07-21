#!/usr/bin/env bash
# Focused offline validation for controller polling and timeout retry policy.
set -euo pipefail

TRAIN_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export TRAIN_APPLY=0
. "${TRAIN_DIR}/lib.sh"
. "${TRAIN_DIR}/classify-timeout.sh"

fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }
record="$(mktemp)"
trap 'rm -f "${record}"' EXIT
train_side_effect() { printf '%s\n' "$*" >>"${record}"; }

[[ "${TRAIN_SMART_CI_POLL_TIMEOUT_SECONDS}" == "6600" ]] || fail "poll budget is not 6600s"

TRAIN_RUN_LOG_TEXT='Error: Process completed with exit code 124.'
train_classify_timeout 123 0 || fail "first exit-124 failure was not retried"
grep -Fqx 'gh run rerun 123 --failed' "${record}" || fail "retry did not target failed jobs only"

: >"${record}"
rc=0
train_classify_timeout 123 1 || rc=$?
[[ "${rc}" == "2" ]] || fail "persistent timeout was not classified real"
[[ ! -s "${record}" ]] || fail "persistent timeout triggered another rerun"

TRAIN_RUN_LOG_TEXT='Expected 200 but received 500'
rc=0
train_classify_timeout 123 0 || rc=$?
[[ "${rc}" == "1" ]] || fail "ordinary assertion was classified as timeout"

printf 'PASS: controller timeout retry policy\n'
