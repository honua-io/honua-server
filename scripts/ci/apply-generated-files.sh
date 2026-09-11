#!/usr/bin/env bash
# Runs on the isolated writer before any credential exists. The bundle comes
# from the generator job, which executes repository build/test code, so its
# bytes are untrusted: accept only allowlisted regular files generated from
# exactly the trunk commit this writer checked out.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
source scripts/ci/generated-files.sh
bundle="${1:?Usage: apply-generated-files.sh <bundle-dir>}"
max_bytes=$((32 * 1024 * 1024))
reject() { echo "Rejected generator bundle: $*" >&2; exit 1; }

expected="$(printf '%s\n' source-sha "${GENERATED_FILES[@]/#/files/}" | LC_ALL=C sort)"
actual="$(cd "$bundle" && find . -mindepth 1 ! -type d -printf '%P\n' | LC_ALL=C sort)"
if [[ "$actual" != "$expected" ]]; then
  printf 'Expected:\n%s\nReceived:\n%s\n' "$expected" "$actual" >&2
  reject 'entries differ from the generated-file allowlist'
fi
for entry in $expected; do
  [[ -f "$bundle/$entry" && ! -L "$bundle/$entry" ]] || reject "$entry is not a regular file"
  (( $(stat -c %s "$bundle/$entry") <= max_bytes )) || reject "$entry exceeds $max_bytes bytes"
done

source_sha="$(<"$bundle/source-sha")"
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || reject 'source-sha is not a commit SHA'
head_sha="$(git rev-parse HEAD)"
# Never place outputs on any other commit: a claimed descendant of trunk could
# otherwise fast-forward unreviewed commits. A newer trunk run regenerates.
[[ "$source_sha" == "$head_sha" ]] ||
  reject "generated from $source_sha, but trunk is $head_sha"

for path in "${GENERATED_FILES[@]}"; do
  cp -- "$bundle/files/$path" "$path"
done
git diff --stat HEAD -- "${GENERATED_FILES[@]}"
