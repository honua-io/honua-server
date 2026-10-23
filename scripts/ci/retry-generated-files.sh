#!/usr/bin/env bash
# Only the stale-source path invokes this. Each scheduled run may dispatch at
# most two successors, each checking out and validating its own new trunk SHA.
set -euo pipefail

if [[ "${GITHUB_REF:-}" != refs/heads/trunk || "${GITHUB_RUN_ATTEMPT:-1}" != 1 ]]; then
  echo 'No automatic retry for a non-trunk run or a rerun.'
  exit 0
fi

today="$(date -u +%F)"
case "${GITHUB_EVENT_NAME:-}" in
  schedule)
    retry_date="$today"
    retry_count=0
    ;;
  workflow_dispatch)
    retry_date="${RETRY_DATE:-}"
    retry_count="${RETRY_COUNT:-0}"
    ;;
  *) exit 0 ;;
esac

if [[ "$retry_date" != "$today" ]]; then
  echo 'No automatic retry for a manual run or an expired nightly chain.'
  exit 0
fi
if [[ ! "$retry_count" =~ ^[012]$ ]]; then
  echo '::error::Invalid generated-files retry count; expected 0, 1 or 2.' >&2
  exit 1
fi
if [[ "$retry_count" == 2 ]]; then
  echo 'Nightly generated-files retry limit reached; drift remains advisory.'
  exit 0
fi

: "${GH_TOKEN:?MERGE_TRAIN_TOKEN is required for the fresh-head dispatch}"
: "${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is required}"
gh api --method POST \
  "repos/${GITHUB_REPOSITORY}/actions/workflows/generated-files-on-trunk.yml/dispatches" \
  -f ref=trunk \
  -f "inputs[retry_count]=$((retry_count + 1))" \
  -f "inputs[retry_date]=$retry_date"
echo "Scheduled fresh-trunk generated-files retry $((retry_count + 1))/2 for $retry_date."
