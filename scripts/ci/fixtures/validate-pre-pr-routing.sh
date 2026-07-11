#!/usr/bin/env bash
# Offline fixtures for the local pre-PR CI-only fast-path classifier.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLASSIFIER="${SCRIPT_DIR}/classify-pre-pr-changes.sh"
SYNTAX_VALIDATOR="${SCRIPT_DIR}/validate-shell-syntax.sh"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "${SCRATCH}"' EXIT

PASS=0
FAIL=0
pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

REPO="${SCRATCH}/repo"
git init -q "${REPO}"
git -C "${REPO}" config user.name Test
git -C "${REPO}" config user.email test@honua.io
mkdir -p "${REPO}/scripts/ci/fixtures" "${REPO}/src/App" "${REPO}/.github/workflows" "${REPO}/.github/actions/setup-dotnet-ci"
cp "${CLASSIFIER}" "${REPO}/scripts/ci/classify-pre-pr-changes.sh"
cp "${SYNTAX_VALIDATOR}" "${REPO}/scripts/ci/validate-shell-syntax.sh"
printf '#!/usr/bin/env bash\ntrue\n' >"${REPO}/scripts/ci/fixtures/nested.sh"
printf '<Project />\n' >"${REPO}/src/App/App.csproj"
git -C "${REPO}" add .
git -C "${REPO}" commit -qm seed
BASE="$(git -C "${REPO}" rev-parse HEAD)"

classification() {
  (cd "${REPO}" && scripts/ci/classify-pre-pr-changes.sh "${BASE}" HEAD | head -1)
}

# Committed CI shell changes are eligible.
printf '# fixture\n' >>"${REPO}/scripts/ci/fixtures/nested.sh"
git -C "${REPO}" add scripts/ci/fixtures/nested.sh
git -C "${REPO}" commit -qm 'ci shell change'
[[ "$(classification)" == "CI_ONLY" ]] && pass "committed CI shell change" || fail "committed CI shell change"

# Staged, unstaged, and untracked paths all participate in classification.
printf '# staged\n' >>"${REPO}/scripts/ci/fixtures/nested.sh"
git -C "${REPO}" add scripts/ci/fixtures/nested.sh
printf '# unstaged\n' >>"${REPO}/scripts/ci/validate-shell-syntax.sh"
printf '#!/usr/bin/env bash\ntrue\n' >"${REPO}/scripts/ci/untracked.sh"
[[ "$(classification)" == "CI_ONLY" ]] && pass "working-tree CI shell changes" || fail "working-tree CI shell changes"

# Hosted build-routing exclusions remain eligible as a mixed metadata diff.
mkdir -p "${REPO}/docs"
printf 'docs\n' >"${REPO}/docs/ci.md"
printf 'name: ci\n' >"${REPO}/.github/workflows/ci.yml"
printf '{}\n' >"${REPO}/.github/ci-shards.json"
[[ "$(classification)" == "CI_ONLY" ]] && pass "hosted CI metadata parity" || fail "hosted CI metadata parity"

# Runtime/build inputs and setup-dotnet action changes must use normal checks.
printf 'class Runtime {}\n' >"${REPO}/src/App/Runtime.cs"
[[ "$(classification)" == "NORMAL" ]] && pass "runtime change falls back" || fail "runtime change falls back"
rm "${REPO}/src/App/Runtime.cs"
printf 'name: setup\n' >"${REPO}/.github/actions/setup-dotnet-ci/action.yml"
[[ "$(classification)" == "NORMAL" ]] && pass "setup-dotnet action falls back" || fail "setup-dotnet action falls back"
rm "${REPO}/.github/actions/setup-dotnet-ci/action.yml"
mkdir -p "${REPO}/.github/workflows/nested"
printf 'name: nested\n' >"${REPO}/.github/workflows/nested/ci.yml"
[[ "$(classification)" == "NORMAL" ]] && pass "nested workflow path falls back" || fail "nested workflow path falls back"
rm -rf "${REPO}/.github/workflows/nested"

# Missing refs are uncertainty, never CI-only.
unknown="$(cd "${REPO}" && scripts/ci/classify-pre-pr-changes.sh refs/heads/missing HEAD | head -1)"
[[ "${unknown}" == "UNKNOWN" ]] && pass "missing base fails safe" || fail "missing base fails safe"

# Recursive syntax validation must reach nested scripts.
printf '#!/usr/bin/env bash\nif then\n' >"${REPO}/scripts/ci/fixtures/bad.sh"
if (cd "${REPO}" && scripts/ci/validate-shell-syntax.sh >/dev/null 2>&1); then
  fail "recursive syntax rejects nested error"
else
  pass "recursive syntax rejects nested error"
fi
printf '#!/usr/bin/env bash\ntrue\n' >"${REPO}/scripts/ci/fixtures/bad.sh"
(cd "${REPO}" && scripts/ci/validate-shell-syntax.sh >/dev/null) \
  && pass "recursive syntax accepts valid scripts" \
  || fail "recursive syntax accepts valid scripts"

echo "RESULT: ${PASS} passed, ${FAIL} failed"
[[ "${FAIL}" -eq 0 ]]
