#!/usr/bin/env bash
# Only the immutable PR base supplies executable skip policy. Fail open to checks.
set -euo pipefail
echo 'generated_only=false' >> "$GITHUB_OUTPUT"
if [[ "$GITHUB_EVENT_NAME" == pull_request && "$TRUSTED_BASE_SHA" =~ ^[0-9a-f]{40}$ ]]; then
  if ! git cat-file -e "${TRUSTED_BASE_SHA}^{commit}" 2>/dev/null; then
    git fetch --no-tags --depth=1 origin "$TRUSTED_BASE_SHA" || exit 0
  fi
  policy="${RUNNER_TEMP}/generated-output-diff.py"
  if git show "${TRUSTED_BASE_SHA}:scripts/ci/generated-output-diff.py" > "$policy"; then
    python3 "$policy" --github-output
  fi
fi
