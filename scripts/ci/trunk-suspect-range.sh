#!/usr/bin/env bash
# Print the trunk merges in BASE..HEAD as Markdown: the suspect range a red
# post-merge run points at. Reads the local clone; needs history back to BASE.
#
# Usage: scripts/ci/trunk-suspect-range.sh <base-sha> <head-sha>

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <base-sha> <head-sha>" >&2
  exit 2
fi

base="$1"
head="$2"
repository_url="${GITHUB_SERVER_URL:-https://github.com}/${GITHUB_REPOSITORY:-honua-io/honua-server}"

pull_request_number() {
  sed -nE 's/.*\(#([0-9]+)\)$/\1/p; s/^Merge pull request #([0-9]+).*/\1/p' <<<"$1" | head -n 1
}

mapfile -t merges < <(git log --first-parent --format='%H%x09%s' "${base}..${head}")

echo "## Suspect merge range"
echo
echo "\`${base}\`..\`${head}\` (${#merges[@]} merges on trunk since the last green CI run)"
echo

pull_requests=()
for merge in "${merges[@]}"; do
  sha="${merge%%$'\t'*}"
  subject="${merge#*$'\t'}"
  number="$(pull_request_number "${subject}")"
  [[ -n "${number}" ]] && pull_requests+=("[#${number}](${repository_url}/pull/${number})")
  echo "- [\`${sha:0:10}\`](${repository_url}/commit/${sha}) ${subject}"
done

echo
if [[ ${#pull_requests[@]} -gt 0 ]]; then
  echo "Merged pull requests: ${pull_requests[*]}"
else
  echo "Merged pull requests: none named in the range"
fi
