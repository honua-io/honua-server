#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
# shellcheck source=scripts/ci/lib/pr-gate-ancestry.sh
. "${repo_root}/scripts/ci/lib/pr-gate-ancestry.sh"

fixture="$(mktemp -d "${RUNNER_TEMP:-/tmp}/honua-pr-gate-ancestry.XXXXXX")"
trap 'rm -rf "${fixture}"' EXIT

remote="${fixture}/remote.git"
source_repo="${fixture}/source"
checkout="${fixture}/checkout"

git init --quiet --bare "${remote}"
git init --quiet "${source_repo}"
git -C "${source_repo}" config user.email 'ci-fixture@honua.io'
git -C "${source_repo}" config user.name 'Honua CI Fixture'
git -C "${source_repo}" checkout --quiet -b trunk

printf 'base\n' > "${source_repo}/fixture.txt"
git -C "${source_repo}" add fixture.txt
git -C "${source_repo}" commit --quiet -m base
base_sha="$(git -C "${source_repo}" rev-parse HEAD)"

git -C "${source_repo}" checkout --quiet -b feature
printf 'feature\n' >> "${source_repo}/fixture.txt"
git -C "${source_repo}" commit --quiet -am feature
head_sha="$(git -C "${source_repo}" rev-parse HEAD)"

git -C "${source_repo}" checkout --quiet trunk
git -C "${source_repo}" merge --quiet --no-ff feature -m 'synthetic merge'
merge_sha="$(git -C "${source_repo}" rev-parse HEAD)"
git -C "${source_repo}" remote add origin "file://${remote}"
git -C "${source_repo}" push --quiet origin trunk feature "HEAD:refs/pull/1/merge"

git init --quiet "${checkout}"
git -C "${checkout}" remote add origin "file://${remote}"
git -C "${checkout}" fetch --quiet --depth=1 origin refs/pull/1/merge
git -C "${checkout}" checkout --quiet --detach FETCH_HEAD

[[ "$(git -C "${checkout}" rev-parse --is-shallow-repository)" == 'true' ]]
if git -C "${checkout}" merge-base --is-ancestor "${base_sha}" "${merge_sha}" 2>/dev/null; then
  echo '::error::Fixture checkout unexpectedly exposed synthetic merge ancestry.' >&2
  exit 1
fi

honua_ensure_pr_gate_ancestry \
  "${checkout}" "${base_sha}" "${head_sha}" "${merge_sha}"
git -C "${checkout}" merge-base --is-ancestor "${base_sha}" "${merge_sha}"
git -C "${checkout}" merge-base --is-ancestor "${head_sha}" "${merge_sha}"

# Both parents intentionally remain shallow roots. A triple-dot diff therefore
# has no merge-base, while the exact base-to-synthetic-merge tree delta remains
# available and is the tree PR Gate actually built.
if git -C "${checkout}" diff --name-only \
  "${base_sha}...${head_sha}" >/dev/null 2>&1; then
  echo '::error::Fixture unexpectedly exposed unbounded base/head history.' >&2
  exit 1
fi
[[ "$(git -C "${checkout}" diff --name-only "${base_sha}" "${merge_sha}")" == 'fixture.txt' ]]

printf 'unrelated\n' > "${source_repo}/unrelated.txt"
git -C "${source_repo}" checkout --quiet --orphan unrelated
git -C "${source_repo}" rm --quiet -rf .
git -C "${source_repo}" add unrelated.txt
git -C "${source_repo}" commit --quiet -m unrelated
unrelated_sha="$(git -C "${source_repo}" rev-parse HEAD)"
git -C "${source_repo}" push --quiet origin unrelated

if honua_ensure_pr_gate_ancestry \
  "${checkout}" "${base_sha}" "${unrelated_sha}" "${merge_sha}" 2>/dev/null; then
  echo '::error::An unrelated head was accepted as PR Gate merge ancestry.' >&2
  exit 1
fi

echo 'pr-gate-shallow-ancestry=ok bounded-depth=1 fail-closed=true'
