#!/usr/bin/env bash
# Validate shell syntax recursively, or validate the explicitly supplied paths.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

scripts=()
if [[ "$#" -eq 0 ]]; then
  while IFS= read -r -d '' path; do
    scripts+=("${path}")
  done < <(find scripts/ci -type f -name '*.sh' -print0 | LC_ALL=C sort -z)
else
  for path in "$@"; do
    [[ "${path}" == *.sh && -f "${path}" ]] && scripts+=("${path}")
  done
fi

if [[ "${#scripts[@]}" -eq 0 ]]; then
  echo "No shell scripts require syntax validation."
  exit 0
fi

printf 'Checking shell syntax for %d script(s)...\n' "${#scripts[@]}"
for script in "${scripts[@]}"; do
  bash -n "${script}"
done
