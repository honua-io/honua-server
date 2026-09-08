#!/usr/bin/env bash
# Called only in the dedicated trunk checkout, after successful regeneration.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
source scripts/ci/generated-files.sh
mode="${1:---dry-run}"
[[ "$mode" == --dry-run || "$mode" == --commit ]] || { echo 'Use --dry-run or --commit' >&2; exit 2; }
# Never sweep another caller's staged changes into the generated commit.
git diff --cached --quiet || { echo 'Index must be clean before generated commit' >&2; exit 1; }
if git diff --quiet HEAD -- "${GENERATED_FILES[@]}"; then
  echo 'No generated changes; no commit.'
  exit 0
fi
git diff --stat HEAD -- "${GENERATED_FILES[@]}"
if [[ "$mode" == --dry-run ]]; then
  echo 'Dry run: would create one generated-files commit; no commit or push performed.'
  exit 0
fi
source_sha="$(git rev-parse HEAD)"
git add -- "${GENERATED_FILES[@]}"
GIT_AUTHOR_NAME='Mike McDougall' GIT_AUTHOR_EMAIL='mike@honua.io' \
GIT_COMMITTER_NAME='Mike McDougall' GIT_COMMITTER_EMAIL='mike@honua.io' \
  git -c user.name='Mike McDougall' -c user.email='mike@honua.io' commit \
    -m 'ci: regenerate generated files on trunk' \
    -m "Generated-From: ${source_sha}" -m 'Refs #3213'
# Never rebase generated blobs onto a different source tree or force-push.
# A concurrent trunk merge gets its own queued regeneration run.
for delay in 0 10 30 60 120; do
  (( delay == 0 )) || sleep "$delay"
  if git push origin HEAD:refs/heads/trunk >push-generated.log 2>&1; then
    cat push-generated.log
    rm push-generated.log
    exit 0
  fi
  cat push-generated.log >&2
  if ! grep -Eqi 'Could not resolve host|Connection reset by peer|error connecting to api.github.com|TLS|timed out|timeout' push-generated.log; then
    rm push-generated.log
    exit 1
  fi
done
rm push-generated.log
exit 1
