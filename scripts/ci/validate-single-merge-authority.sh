#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
workflows="${repo_root}/.github/workflows"
[[ ! -e "${workflows}/pr-merge-train.yml" ]] || {
  echo "legacy merge authority still exists" >&2; exit 1;
}
grep -Fq 'scripts/ci/merge-train/train.sh' "${workflows}/merge-train.yml" || {
  echo "merge-train.yml does not invoke the canonical controller" >&2; exit 1;
}
if grep -RIEq --include='*.yml' --include='*.yaml' \
  '^[[:space:]]+(gh[[:space:]]+pr[[:space:]]+merge|git[[:space:]]+push[^#]*(HEAD|[^ ]+):trunk)|mergePullRequest|pulls/.*/merge' \
  "${workflows}" --exclude='merge-train.yml'; then
  echo "a workflow other than merge-train.yml contains merge authority" >&2; exit 1
fi
echo "single merge authority: merge-train.yml"
