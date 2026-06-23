#!/usr/bin/env bash
# Phase 2: bedrock-invoke — a thin, fail-safe AWS Bedrock (Claude) client for the
# merge train's OPTIONAL gated LLM judgment layer.
#
# This file is SOURCEABLE: it defines functions only and performs no work at
# source time. The deterministic Phase-1 train works WITHOUT this — the LLM is a
# gated enhancement that is OFF BY DEFAULT (TRAIN_LLM=0) and every caller has a
# safe deterministic fallback.
#
# Provider pattern reuse (honua-devops):
#   honua-devops's Bedrock access is billed to the founder's AWS account (no
#   Anthropic API key). We mirror that posture here: AWS-CLI SigV4 auth via the
#   ambient credential chain (a GitHub OIDC role assumed in the workflow), the
#   AWS region honua-devops deploys to (us-west-2), and the Bedrock Messages-API
#   request shape (anthropic_version=bedrock-2023-05-31, system + messages,
#   short max_tokens — these are classification calls, not generation). Model id
#   and region are env-overridable so an operator can pin whatever inference
#   profile their account has access to without editing code.
#
# ROBUSTNESS CONTRACT (never block the train on an LLM failure):
#   bedrock_ask emits a sentinel ("__BEDROCK_ERROR__") on ANY failure path — LLM
#   disabled, aws CLI missing, timeout, non-zero exit, empty/garbled output. The
#   judgment wrappers treat the sentinel (and the disabled state) as "fall back
#   to the deterministic Phase-1 behavior." A Bedrock outage degrades the train
#   to exactly Phase 1; it never wedges or escalates spuriously on its own.

# --- configuration knobs (env-overridable) -----------------------------------
# TRAIN_LLM gates the ENTIRE layer. 0 (default) => deterministic-only; the gates
# below are never even consulted. 1 => the gates MAY call Bedrock, but only in
# their ambiguous condition (see select.sh / classify-flake.sh / forward-fix.sh).
: "${TRAIN_LLM:=0}"

# Model id + region default to honua-devops's Bedrock posture. Both overridable.
# The default is a Haiku-class cross-region inference profile (cheap, fast — these
# are short classification calls). An operator pins whatever profile their AWS
# account is entitled to via BEDROCK_MODEL_ID without touching this file.
: "${BEDROCK_MODEL_ID:=us.anthropic.claude-haiku-4-5-20251001-v1:0}"
: "${AWS_REGION:=us-west-2}"

# Short cap: classification answers are tiny (yes/no + maybe a regex line).
: "${BEDROCK_MAX_TOKENS:=256}"
# Hard timeout (seconds) on the aws call so a hung endpoint can't stall a batch.
: "${BEDROCK_TIMEOUT:=30}"

# Sentinel returned on every failure/disabled path. Callers compare against this
# and take their deterministic fallback when they see it.
BEDROCK_ERROR_SENTINEL='__BEDROCK_ERROR__'

# bedrock_enabled: is the gated LLM layer turned on? (TRAIN_LLM=1)
bedrock_enabled() { [[ "${TRAIN_LLM:-0}" == "1" ]]; }

# bedrock_ask <system-prompt> <user-prompt> -> stdout answer (or the sentinel).
#
# Robust by construction: any failure (disabled, missing aws/jq, timeout,
# non-zero exit, empty body) prints the sentinel to stdout and returns 0, so a
# caller's `ans="$(bedrock_ask ...)"` never aborts under `set -e` and always has
# a defined value to branch on.
#
# Test seam: if TRAIN_BEDROCK_ASK_CMD is set, it is invoked as
#   "${TRAIN_BEDROCK_ASK_CMD}" <system> <user>
# and its stdout is used verbatim. The fixture harness sets this to a fake that
# returns canned answers (and can simulate a Bedrock error by echoing the
# sentinel), so tests never touch real Bedrock / spend money.
bedrock_ask() {
  local system="$1" user="$2"

  if ! bedrock_enabled; then
    printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"
    return 0
  fi

  if [[ -n "${TRAIN_BEDROCK_ASK_CMD:-}" ]]; then
    local out
    if ! out="$("${TRAIN_BEDROCK_ASK_CMD}" "${system}" "${user}" 2>/dev/null)"; then
      printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0
    fi
    [[ -z "${out}" ]] && out="${BEDROCK_ERROR_SENTINEL}"
    printf '%s\n' "${out}"
    return 0
  fi

  # Real path needs aws + jq. Missing either => fall back, don't crash.
  if ! command -v aws >/dev/null 2>&1 || ! command -v jq >/dev/null 2>&1; then
    printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0
  fi

  # Build the Bedrock InvokeModel body in the Anthropic Messages shape.
  # (Bedrock's native Anthropic surface: anthropic_version is the fixed Bedrock
  # marker string, NOT a Claude model id; the model is selected by --model-id.)
  local body
  if ! body="$(jq -nc \
        --argjson max "${BEDROCK_MAX_TOKENS}" \
        --arg sys "${system}" \
        --arg usr "${user}" \
        '{
           anthropic_version: "bedrock-2023-05-31",
           max_tokens: $max,
           system: $sys,
           messages: [ { role: "user", content: [ { type: "text", text: $usr } ] } ]
         }')"; then
    printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0
  fi

  # Invoke. The AWS CLI writes the model output to a path argument (not stdout),
  # so we capture it via a temp file. `timeout` caps a hung call.
  local outfile rc=0 raw text
  outfile="$(mktemp 2>/dev/null)" || { printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0; }

  timeout "${BEDROCK_TIMEOUT}" aws bedrock-runtime invoke-model \
    --region "${AWS_REGION}" \
    --model-id "${BEDROCK_MODEL_ID}" \
    --content-type "application/json" \
    --accept "application/json" \
    --body "${body}" \
    --cli-binary-format raw-in-base64-out \
    "${outfile}" >/dev/null 2>&1 || rc=$?

  if [[ "${rc}" != "0" ]]; then
    rm -f "${outfile}"
    printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0
  fi

  raw="$(cat "${outfile}" 2>/dev/null)"; rm -f "${outfile}"
  # Anthropic-on-Bedrock response: {"content":[{"type":"text","text":"..."}],...}
  text="$(jq -r '.content[]? | select(.type=="text") | .text' <<<"${raw}" 2>/dev/null | head -c 4000)"
  if [[ -z "${text}" ]]; then
    printf '%s\n' "${BEDROCK_ERROR_SENTINEL}"; return 0
  fi
  printf '%s\n' "${text}"
  return 0
}

# bedrock_is_error <answer>: true if the answer is the failure sentinel.
bedrock_is_error() { [[ "$1" == "${BEDROCK_ERROR_SENTINEL}" ]]; }

# bedrock_first_word_yes <answer>: normalize a yes/no classification. Returns 0
# (yes) only when the answer's first non-space token starts with "y" (case-insens).
# The sentinel and anything non-affirmative return 1, so an errored/ambiguous
# answer maps to "no" — callers pair this with an explicit fallback anyway.
bedrock_first_word_yes() {
  local ans="$1" first
  bedrock_is_error "${ans}" && return 1
  first="$(printf '%s' "${ans}" | tr '[:upper:]' '[:lower:]' | tr -s '[:space:]' ' ' | sed 's/^ *//' | cut -d' ' -f1)"
  [[ "${first}" == y* ]]
}
