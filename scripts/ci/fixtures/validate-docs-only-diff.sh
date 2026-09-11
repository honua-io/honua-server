#!/usr/bin/env bash
# Offline fixtures for scripts/ci/docs-only-diff.sh, the PR Gate docs-only exit
# (operator ruling A, 2026-09-11; #3213).
#
# Every case builds the commit GitHub checks out for a pull request --
# refs/pull/N/merge, first parent the target-branch tip, second parent the PR
# head -- in a scratch repository and runs the classifier exactly as
# pr-gate.yml does. The last block checks the live workflow wiring and the
# classifier's own inventories against this checkout, so a renamed gate script
# or a stale reviewed exception fails here rather than silently narrowing the
# scan.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
CLASSIFIER="${SCRIPT_DIR}/docs-only-diff.sh"
WORKFLOW="${REPO_ROOT}/.github/workflows/pr-gate.yml"
SCRATCH="$(mktemp -d)"
trap 'rm -rf "${SCRATCH}"' EXIT

# The host has no global git identity, and a fixture must not depend on one.
export GIT_AUTHOR_NAME=Fixture GIT_AUTHOR_EMAIL=fixture@example.invalid
export GIT_COMMITTER_NAME=Fixture GIT_COMMITTER_EMAIL=fixture@example.invalid

PASS=0
FAIL=0
pass() { echo "PASS: $1"; PASS=$((PASS + 1)); }
fail() { echo "FAIL: $1"; FAIL=$((FAIL + 1)); }

REPO="${SCRATCH}/repo"
g() { git -c commit.gpgsign=false -c core.hooksPath=/dev/null -C "${REPO}" "$@"; }
write() {
  mkdir -p "$(dirname "${REPO}/$1")"
  printf '%s\n' "$2" > "${REPO}/$1"
}

git init -q "${REPO}"
g checkout -q -b trunk
mkdir -p "${REPO}/scripts/ci"
cp "${CLASSIFIER}" "${REPO}/scripts/ci/docs-only-diff.sh"
write README.md '# Fixture'
write AGENTS.md '# Agents'
write SECURITY.md '# Security'
write docs/guides/intro.md '# Intro'
write docs/guides/governed.md '# Read by a test through path segments'
write docs/guides/commented.md '# Named only in a whole-line comment'
write docs/howto/steps.md '# Under a directory a test enumerates'
write docs/reference/literal.md '# Read by a test through a path literal'
write docs/reference/embedded.md '# Embedded by a project'
write docs/gis/data/catalog.json '{}'
write docs/gis/data/notes.md 'generated'
write src/Lib/Lib.cs 'internal sealed class Lib { }'
# A packed README only has to exist; an embedded document is build content.
write src/Lib/Lib.csproj '<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="\" />
    <EmbeddedResource Include="..\..\docs\reference\embedded.md" />
  </ItemGroup>
</Project>'
# The reviewed existence-only references keyed to this exact source path.
write tests/dotnet/Honua.Architecture.Tests/BasicArchitectureTests.cs 'public sealed class BasicArchitectureTests
{
    public void Layout()
    {
        Directory.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "docs")).Should().BeTrue();
        File.Exists(ArchitectureTestHelpers.CombinePath(projectRoot, "AGENTS.md")).Should().BeTrue();
    }
}'
write tests/dotnet/Honua.Architecture.Tests/DocReadingTests.cs 'public sealed class DocReadingTests
{
    // docs/guides/commented.md is only mentioned in this comment.
    public void Reads()
    {
        var governed = File.ReadAllText(ArchitectureTestHelpers.CombinePath(
            root,
            "docs",
            "guides",
            "governed.md"));
        var literal = File.ReadAllText("docs/reference/literal.md");
        var howto = Directory.EnumerateFiles(Path.Combine(root, "docs", "howto"));
    }
}'
g add -A
g commit -q -m seed
BASE="$(g rev-parse HEAD)"

g checkout -q -b trunk-moved "${BASE}"
write src/Lib/Lib.cs 'internal sealed class Lib { public int Moved => 1; }'
g add -A
g commit -q -m 'trunk moves on'
MOVED="$(g rev-parse HEAD)"

start_pr() { g checkout -q -B pr "${BASE}"; }
commit_pr() {
  g add -A
  g commit -q --allow-empty -m "$1"
}
# Check out the merge commit GitHub would build for branch `pr` targeting $1.
merge_onto() {
  g checkout -q --detach "$1"
  g merge -q --no-ff --no-edit pr
}

expect() {
  local name="$1" expected="$2" out rc=0 want=1
  out="$(cd "${REPO}" && GITHUB_EVENT_NAME="${EVENT:-pull_request}" scripts/ci/docs-only-diff.sh 2>&1)" || rc=$?
  [[ "${expected}" == true ]] && want=0
  if [[ "${rc}" == "${want}" ]] && grep -qx "docs_only=${expected}" <<< "${out}"; then
    pass "${name}"
  else
    fail "${name} (exit ${rc}): ${out}"
  fi
}

# case NAME EXPECTED -- then the mutation runs on branch `pr` from BASE.
case_on() {
  local target="$1" name="$2" expected="$3"
  shift 3
  start_pr
  "$@"
  commit_pr "${name}"
  merge_onto "${target}"
  expect "${name}" "${expected}"
}
case_pr() { case_on "${BASE}" "$@"; }

edit_docs() {
  printf 'More.\n' >> "${REPO}/docs/guides/intro.md"
  write docs/guides/new.md '# New page'
  write docs/guides/diagram.svg '<svg xmlns="http://www.w3.org/2000/svg"/>'
}
edit_readme() { printf 'More.\n' >> "${REPO}/README.md"; }
edit_agents() { printf 'More.\n' >> "${REPO}/AGENTS.md"; }
edit_docs_and_src() {
  edit_docs
  printf '// touched\n' >> "${REPO}/src/Lib/Lib.cs"
}
edit_generated_json() { write docs/gis/data/catalog.json '{"changed": true}'; }
edit_generated_markdown() { printf 'More.\n' >> "${REPO}/docs/gis/data/notes.md"; }
edit_docs_yaml() { write docs/guides/config.yaml 'key: value'; }
rename_docs_to_src() { g mv docs/guides/intro.md src/Lib/intro.md; }
rename_within_docs() { g mv docs/guides/intro.md docs/guides/renamed.md; }
no_change() { :; }
bulk_docs() {
  local count="$1" i
  mkdir -p "${REPO}/docs/bulk"
  for ((i = 1; i <= count; i++)); do
    printf '# Page %d\n' "${i}" > "${REPO}/docs/bulk/page-${i}.md"
  done
}
bulk_400() { bulk_docs 400; }
bulk_401() { bulk_docs 401; }
edit_governed_segments() { printf 'More.\n' >> "${REPO}/docs/guides/governed.md"; }
edit_governed_literal() { printf 'More.\n' >> "${REPO}/docs/reference/literal.md"; }
edit_governed_directory() { write docs/howto/new-step.md '# New step'; }
edit_embedded() { printf 'More.\n' >> "${REPO}/docs/reference/embedded.md"; }
edit_commented() { printf 'More.\n' >> "${REPO}/docs/guides/commented.md"; }
delete_top_level_markdown() { g rm -q SECURITY.md; }
delete_docs_page() { g rm -q docs/guides/intro.md; }
symlink_docs_page() {
  rm "${REPO}/docs/guides/intro.md"
  ln -s new-target.md "${REPO}/docs/guides/intro.md"
}

echo "== Packet cases"
case_pr "docs prose and image only" true edit_docs
case_pr "README-only (packed by a project, content unread)" true edit_readme
case_pr "docs plus one src file" false edit_docs_and_src
case_pr "docs/gis/data JSON" false edit_generated_json
case_pr "rename docs -> src counts both sides" false rename_docs_to_src
case_pr "empty diff" false no_change
case_pr "401 changed paths" false bulk_401

echo "== Boundaries and spellings"
case_pr "400 changed paths" true bulk_400
case_pr "rename within docs" true rename_within_docs
case_pr "AGENTS.md (existence-only reference)" true edit_agents
case_pr "Markdown under docs/gis/data" false edit_generated_markdown
case_pr "non-prose file under docs" false edit_docs_yaml
case_pr "document read through path segments" false edit_governed_segments
case_pr "document read through a path literal" false edit_governed_literal
case_pr "document under a directory a test reads" false edit_governed_directory
case_pr "document a project embeds" false edit_embedded
case_pr "document named only in a whole-line comment" true edit_commented
case_pr "deleted top-level Markdown" false delete_top_level_markdown
case_pr "deleted docs page" true delete_docs_page
case_pr "docs page replaced by a symlink" false symlink_docs_page
case_on "${MOVED}" "target moved on in src; PR still docs-only" true edit_docs

echo "== Refusing to answer without a pull-request merge base"
start_pr
edit_docs
commit_pr "docs for event checks"
merge_onto "${BASE}"
EVENT=workflow_dispatch expect "workflow_dispatch event" false
EVENT=push expect "push event" false
g checkout -q --detach pr
expect "pull_request checkout that is not a merge commit" false

echo "== --github-output"
merge_onto "${BASE}"
output="${SCRATCH}/github-output"
summary="${SCRATCH}/step-summary"
: > "${output}"
: > "${summary}"
rc=0
(cd "${REPO}" && GITHUB_EVENT_NAME=pull_request GITHUB_OUTPUT="${output}" GITHUB_STEP_SUMMARY="${summary}" \
  scripts/ci/docs-only-diff.sh --github-output >/dev/null) || rc=$?
if [[ "${rc}" == 0 ]] && grep -qx 'docs_only=true' "${output}" \
  && grep -q '^PR Gate: docs-only diff, build and tests skipped (3 documentation paths; ' "${summary}"; then
  pass "docs-only answer publishes the output and the summary line"
else
  fail "docs-only answer publishes the output and the summary line (exit ${rc}): $(cat "${output}" "${summary}")"
fi
start_pr
edit_docs_and_src
commit_pr "full gate for output checks"
merge_onto "${BASE}"
: > "${output}"
: > "${summary}"
rc=0
(cd "${REPO}" && GITHUB_EVENT_NAME=pull_request GITHUB_OUTPUT="${output}" GITHUB_STEP_SUMMARY="${summary}" \
  scripts/ci/docs-only-diff.sh --github-output >/dev/null) || rc=$?
if [[ "${rc}" == 0 ]] && grep -qx 'docs_only=false' "${output}" && [[ ! -s "${summary}" ]]; then
  pass "full-gate answer exits 0 with docs_only=false and no summary line"
else
  fail "full-gate answer exits 0 with docs_only=false and no summary line (exit ${rc})"
fi
rc=0
(cd "${REPO}" && scripts/ci/docs-only-diff.sh --head HEAD >/dev/null 2>&1) || rc=$?
[[ "${rc}" == 2 ]] && pass "--head without --base is a usage error" || fail "--head without --base is a usage error (exit ${rc})"

echo "== Live inventories and workflow wiring"
missing=0
while IFS= read -r entry; do
  if [[ ! -e "${REPO_ROOT}/${entry}" ]]; then
    fail "GATE_INPUTS entry '${entry}' does not exist; the scan would silently skip it"
    missing=1
  fi
done < <(sed -n '/^GATE_INPUTS = (/,/^)/p' "${CLASSIFIER}" | grep -o '^    "[^"]*"' | tr -d ' "')
[[ "${missing}" == 0 ]] && pass "every GATE_INPUTS entry exists"

stale=0
while IFS='|' read -r reference source; do
  if [[ ! -f "${REPO_ROOT}/${source}" ]] || ! grep -qiF "${reference}" "${REPO_ROOT}/${source}"; then
    fail "REFERENCE_ONLY entry (${reference}, ${source}) is stale; delete it"
    stale=1
  fi
done < <(sed -n '/^REFERENCE_ONLY = {/,/^}/p' "${CLASSIFIER}" | sed -n 's/^    ("\([^"]*\)", "\([^"]*\)"):$/\1|\2/p')
[[ "${stale}" == 0 ]] && pass "every REFERENCE_ONLY entry names a live reference"

if python3 - "${WORKFLOW}" <<'PY'
import sys

text = open(sys.argv[1], encoding="utf-8").read()


def job(name: str, following: str) -> str:
    return text[text.index(f"\n  {name}:\n") : text.index(f"\n  {following}:\n")]


CALL = "run: scripts/ci/docs-only-diff.sh --github-output"
GATED = (
    "if: (env.REVIEW_FIRST_MODE != 'enforce' || github.event_name != 'pull_request' "
    "|| github.run_attempt > 1) && steps.docs-only.outputs.docs_only != 'true'"
)
fmt = job("format", "pr-gate")
gate = job("pr-gate", "affected-shards-select")
select = job("affected-shards-select", "affected-shard")
problems = []
if text.count(CALL) != 3:
    problems.append(f"expected the classifier in exactly three jobs, found {text.count(CALL)}")
for label, body in (("format", fmt), ("pr-gate", gate), ("affected-shards-select", select)):
    if body.count(CALL) != 1 or "id: docs-only" not in body:
        problems.append(f"{label} must classify once, as step id docs-only")
if fmt.count(GATED) != 3:
    problems.append(f"format must gate Setup .NET, Restore and Format Check on the verdict ({fmt.count(GATED)})")
if gate.count(GATED) != 12:
    problems.append(f"pr-gate must gate its twelve expensive steps on the verdict ({gate.count(GATED)})")
order = [
    "- name: Revalidate exact-head review before verification",
    "- name: Validate the docs-only classifier before consuming it",
    "- name: Classify docs-only diff",
    "- name: Documentation Command Policy (docs-only diff)",
    "- name: Free disk space",
]
positions = [gate.find(step) for step in order]
if -1 in positions or positions != sorted(positions):
    problems.append("pr-gate must classify after the review-first admission steps and before any expensive step")
policy = gate[gate.find(order[3]) :].split("- name:", 2)[1]
if "if: steps.docs-only.outputs.docs_only == 'true'" not in policy or "check-markdown-command-policy.ps1" not in policy:
    problems.append("a docs-only diff must still run the Markdown command policy")
for name in ("Verify tracked text files are UTF-8", "Verify review-first admission contract", "Admission receipt"):
    block = gate[gate.find(f"- name: {name}") :].split("- name:", 2)[1]
    if "docs-only" in block:
        problems.append(f"the always-on step {name!r} must not depend on the docs-only verdict")
if "steps.docs-only.outputs.docs_only == 'true' && 'true' || steps.select.outputs.skip" not in select:
    problems.append("a docs-only diff must publish skip=true to the affected-shard aggregate")
if problems:
    print("\n".join(problems))
    raise SystemExit(1)
PY
then
  pass "pr-gate.yml wiring"
else
  fail "pr-gate.yml wiring"
fi

echo
echo "docs-only-diff fixtures: ${PASS} passed, ${FAIL} failed"
[[ "${FAIL}" == 0 ]]
