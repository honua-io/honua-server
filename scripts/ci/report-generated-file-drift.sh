#!/usr/bin/env bash
# Generation/validation runs separately and remains strict. Only drift is advisory.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
source scripts/ci/generated-files.sh
if (( $# > 0 )); then
  GENERATED_FILES=("$@")
fi
changed="$(git diff --name-only HEAD -- "${GENERATED_FILES[@]}")"
if [[ -n "$changed" ]]; then
  echo '::notice::Generated file drift is advisory; projections will be committed on trunk after merge.'
  printf '%s\n' "$changed"
else
  echo 'Generated files are up to date.'
fi
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo '### Generated file drift (advisory)'
    echo 'Generators must succeed. Differences are regenerated on trunk after merge.'
    echo
    printf '%s\n' "${changed:-No drift.}"
  } >> "$GITHUB_STEP_SUMMARY"
fi
