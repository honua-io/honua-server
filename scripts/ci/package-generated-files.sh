#!/usr/bin/env bash
# Runs in the credential-free generator job. The writer re-validates every byte.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
source scripts/ci/generated-files.sh
out="${1:?Usage: package-generated-files.sh <bundle-dir>}"
rm -rf -- "$out"
mkdir -p -- "$out"
git rev-parse HEAD >"$out/source-sha"
for path in "${GENERATED_FILES[@]}"; do
  mkdir -p -- "$out/files/$(dirname -- "$path")"
  cp -- "$path" "$out/files/$path"
done
changed=false
git diff --quiet HEAD -- "${GENERATED_FILES[@]}" || changed=true
echo "Generated changes: $changed"
echo "changed=$changed" >>"${GITHUB_OUTPUT:-/dev/null}"
