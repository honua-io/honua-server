#!/usr/bin/env bash
# Resolve merge-train execution mode from the GitHub event and explicit manual
# inputs. Automatic triggers are always dry-runs; only a workflow_dispatch with
# train_apply=true can enable side effects.

set -euo pipefail

output_file="${1:?usage: resolve-mode.sh <github-output-file>}"

: "${EVENT_NAME:=}"
: "${DISPATCH_APPLY:=}"
: "${DISPATCH_MAX_BATCH:=}"
: "${DISPATCH_USE_LLM:=}"
: "${DISPATCH_USE_AUTOFIX:=}"
: "${DISPATCH_AUTOFIX_MODEL:=}"
: "${DISPATCH_RESET_STATE:=}"
: "${HAVE_BEDROCK_KEYS:=false}"

apply=0
if [[ "${EVENT_NAME}" == "workflow_dispatch" && "${DISPATCH_APPLY}" == "true" ]]; then
  apply=1
fi

max_batch="${DISPATCH_MAX_BATCH:-10}"
[[ -z "${max_batch}" ]] && max_batch=10

llm=0
if [[ "${apply}" == "1" && "${DISPATCH_USE_LLM}" == "true" && "${HAVE_BEDROCK_KEYS}" == "true" ]]; then
  llm=1
fi
if [[ "${EVENT_NAME}" == "workflow_dispatch" && "${DISPATCH_USE_LLM}" == "true" && "${HAVE_BEDROCK_KEYS}" != "true" ]]; then
  echo "::warning::use_llm=true but BEDROCK_AWS_* secrets unset; running deterministic (TRAIN_LLM=0)."
fi

autofix=0
if [[ "${apply}" == "1" && "${DISPATCH_USE_AUTOFIX}" == "true" && "${HAVE_BEDROCK_KEYS}" == "true" ]]; then
  autofix=1
fi

# Recovery-only state reset. Like every other side effect it requires an
# explicit live dispatch, so no automatic trigger can clear the state issue.
reset_state=0
if [[ "${apply}" == "1" && "${DISPATCH_RESET_STATE}" == "true" ]]; then
  reset_state=1
fi
if [[ "${DISPATCH_RESET_STATE}" == "true" && "${reset_state}" != "1" ]]; then
  echo "::warning::reset_state=true is ignored without an explicit live dispatch (train_apply=true)."
fi

{
  echo "train_apply=${apply}"
  echo "max_batch=${max_batch}"
  echo "train_llm=${llm}"
  echo "train_autofix=${autofix}"
  echo "autofix_model=${DISPATCH_AUTOFIX_MODEL}"
  echo "train_reset_state=${reset_state}"
} >>"${output_file}"

echo "Resolved TRAIN_APPLY=${apply} (event=${EVENT_NAME}) MAX_BATCH=${max_batch} TRAIN_LLM=${llm} TRAIN_AUTOFIX=${autofix} TRAIN_RESET_STATE=${reset_state}"
