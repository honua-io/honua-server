#!/usr/bin/env bash
set -euo pipefail

# Pre-PR validation with the SAME smart filters CI uses, so a local run only
# builds, formats, and tests what the diff actually touches instead of grinding
# the whole solution and every server-test shard every time.
#
# Scope is derived from the diff against a base ref (default: origin/trunk):
#   - build      -> scripts/ci/compute-affected-projects.sh (affected .csproj
#                   closure, or ALL when shared infrastructure changed), then
#                   PRUNED of test projects that neither run in this
#                   invocation nor contain changes: test projects are
#                   dependency leaves, so skipping them skips only their own
#                   compile + bin/ copy (each one materializes its full
#                   dependency tree + native runtimes, ~1-2 GB), while every
#                   src project still compiles. CI builds all test projects
#                   in its own shards. FULL mode disables pruning.
#   - server tests -> scripts/ci/honua-server-targeted-tests.sh picks the
#                   ADR-0037 shard subset (or run_all); that selector remains
#                   authoritative for WHICH SHARDS run (#2951 non-goal: it does
#                   not change ADR-0037 tier semantics). On top of it,
#                   scripts/ci/capability-impact.py's `select-local` crosswalk
#                   (docs/gis/data/feature-catalog.json's route/test/shard
#                   mapping — the SAME crosswalk the hosted shadow-comparison
#                   job validates; no parallel mapping) narrows a selected
#                   shard's `dotnet test --filter` down to just its proving
#                   tests, but ONLY when every changed file was safely
#                   accounted for (no unmatched source, not run_all — see
#                   "Capability narrowing" below for when this can and cannot
#                   apply). Otherwise the shard's normal full filter runs, same
#                   as before this change.
#   - unit tests -> only the *.Tests projects in the affected closure
#   - format     -> only the changed *.cs files (`dotnet format --include`)
#
# Capability narrowing — known limitation: docs/gis/data/feature-catalog.json's
# `code_location` field is, today, almost always the endpoint-registry file
# rather than the actual handler implementation (a proof-ledger evidence
# ordering gap, not an intentional design choice). That means the crosswalk
# can confidently narrow a TEST file change (matched by test-class name) but
# essentially never a plain handler/service SOURCE file change — those still
# report `unmatchedSourceFiles` and fail closed. In practice: a PR touching
# only test files gets genuine per-test narrowing within its shard(s); a PR
# touching handler/service source (with or without tests) falls back to
# running the ADR-0037-selected shard(s) in full, exactly as pre-#2951 — never
# narrower, never broader. `--dry-run` shows exactly which case a given diff
# hits before you commit to a run.
#
# CLI flags (aliases for the escape-hatch env vars below): --full, --fast,
# --dry-run, --base <ref>.
#
# Escape hatches:
#   HONUA_PRE_PR_FULL=1     force the full suite (recommended before a release
#                           or a large cross-cutting refactor).
#   HONUA_PRE_PR_BASE=<ref> override the diff base ref (default origin/trunk).
#   HONUA_PRE_PR_SKIP_AOT=1 skip the AOT publish step.
#   HONUA_PRE_PR_FAST=1     FAST tier: build + format + affected unit tests +
#                           architecture only; SKIP the heavy Honua.Server.Tests
#                           shards and the AOT publish (they run in CI / the merge
#                           queue). Used by the pre-push hook by default so the
#                           push loop stays quick. Run without it (the default
#                           SMART tier) or with HONUA_PRE_PR_FULL=1 before opening
#                           a PR for the full local gate.
#   HONUA_PRE_PR_DRY_RUN=1  (or --dry-run) print the full resolved selection —
#                           build set, targeted shards, capability-narrowed
#                           filters, format scope — and exit 0 without
#                           restoring, building, formatting, or testing
#                           anything. Safe to run with zero Docker/dotnet cost;
#                           this is the primary way to inspect scope decisions.
#   HONUA_PRE_PR_PRINT_BUILD_PLAN=1  print only the resolved build set (the
#                           .slnf project list after pruning) and exit — a
#                           narrower predecessor of --dry-run kept for
#                           back-compat script integrations.
#   HONUA_PRE_PR_SHARD_PARALLELISM=<n>  how many server-test shards to run
#                           concurrently in SMART/FULL mode (default 2). Each
#                           shard is a separate dotnet-test process with its own
#                           Postgres container, so keep this modest.
#
# A diff containing only the same CI/docs paths excluded from hosted CI's
# BUILD_FILES takes a shell-only path: governance and shell fixtures run, but
# package restore, managed builds/tests, Docker and AOT do not.

BASE_REF="${HONUA_PRE_PR_BASE:-origin/trunk}"
FULL="${HONUA_PRE_PR_FULL:-0}"
FAST="${HONUA_PRE_PR_FAST:-0}"
DRY_RUN="${HONUA_PRE_PR_DRY_RUN:-0}"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --full) FULL=1; shift ;;
        --fast) FAST=1; shift ;;
        --dry-run) DRY_RUN=1; shift ;;
        --base)
            BASE_REF="${2:-}"
            if [[ -z "${BASE_REF}" ]]; then
                echo "❌ --base requires a value" >&2
                exit 2
            fi
            shift 2
            ;;
        -h|--help)
            sed -n '2,80p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "❌ unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

if ! command -v jq >/dev/null 2>&1; then
    echo "❌ jq is required to read .github/ci-shards.json"
    exit 1
fi

# CR-safe jq: the Windows jq binary writes stdout in text mode and appends a CR
# to every line, so captured values ("$(jq ...)"), emptiness/`:-` fallbacks
# (the shard monolith fallback and `run_all` comparison below) and shard-field
# env exports would carry a trailing CR. This wrapper strips it (no-op on Linux,
# so CI is unaffected) and supersedes the earlier per-call-site `tr -d '\r'` /
# `${var%$'\r'}` patches, which are now redundant. Sourced AFTER the presence
# guard above so that guard still probes the real jq binary, not this function.
source "$(dirname "${BASH_SOURCE[0]}")/lib/jq-cr-safe.sh"

# Resolve an interpreter that actually runs. `command -v python3` alone is not
# enough on Windows: the Microsoft Store ships a python3.exe App Execution
# Alias stub that is present on PATH but exits non-zero with "Python was not
# found", while the real interpreter installs as `python`/`py`. The old guard
# therefore passed, the stub failed, and the script blamed the code —
# reporting "architecture review found blocking issues" when Python was simply
# absent (#2871). Probe each candidate by executing it, and require Python 3 so
# a legacy `python` == python2 is not selected. On Linux `python3` matches
# first, exactly as before. Resolved early (rather than only before the local
# architecture review) because the capability-impact crosswalk validation and
# select-local narrowing below also need it.
PYTHON_BIN=""
for candidate in python3 python py; do
    if command -v "${candidate}" >/dev/null 2>&1 \
        && "${candidate}" -c 'import sys; sys.exit(0 if sys.version_info[0] == 3 else 1)' >/dev/null 2>&1; then
        PYTHON_BIN="${candidate}"
        break
    fi
done

# Unified cleanup: register scratch paths here (rather than a fresh `trap ...
# EXIT` per section) so later traps do not silently clobber earlier ones.
CLEANUP_PATHS=()
cleanup_scratch() {
    local path
    for path in "${CLEANUP_PATHS[@]:-}"; do
        [[ -n "${path}" ]] && rm -rf "${path}"
    done
}
trap cleanup_scratch EXIT

# Resolve the base ref; if it is missing locally, fall back to a full run so we
# never silently under-test.
if [[ "${FULL}" != "1" ]] && ! git rev-parse --verify --quiet "${BASE_REF}" >/dev/null 2>&1; then
    echo "⚠️  Base ref '${BASE_REF}' not found locally — falling back to a full run."
    echo "    (fetch it, or set HONUA_PRE_PR_BASE, to enable smart filtering.)"
    FULL=1
fi

echo "🔍 Running pre-PR validation..."
# FULL is the most thorough tier and overrides the FAST shortcut.
if [[ "${FULL}" == "1" ]]; then
    FAST=0
fi

# Include committed, staged, unstaged and untracked files. Only positive,
# complete CI_ONLY evidence can bypass managed validation; UNKNOWN and NORMAL
# both continue through the existing fail-safe path.
PRE_PR_SCOPE="NORMAL"
PRE_PR_CHANGED_FILES=""
if [[ "${FULL}" != "1" ]]; then
    scope_output="$(scripts/ci/classify-pre-pr-changes.sh "${BASE_REF}" HEAD 2>/dev/null || echo UNKNOWN)"
    PRE_PR_SCOPE="$(head -1 <<< "${scope_output}")"
    PRE_PR_CHANGED_FILES="$(tail -n +2 <<< "${scope_output}")"
fi

if [[ "${PRE_PR_SCOPE}" == "CI_ONLY" ]]; then
    echo "    Mode: CI-SHELL-ONLY (committed + working tree vs ${BASE_REF})."
    echo "1. Checking canonical instructions..."
    bash scripts/ci/check-instructions-sync.sh

    mapfile -t changed_shell_scripts < <(
        printf '%s\n' "${PRE_PR_CHANGED_FILES}" \
            | sed '/^$/d' \
            | while IFS= read -r path; do
                [[ "${path}" == *.sh && -f "${path}" ]] && printf '%s\n' "${path}"
              done
    )
    echo "2. Checking changed shell script syntax..."
    scripts/ci/validate-shell-syntax.sh "${changed_shell_scripts[@]}"

    echo "3. Validating CI workflow and shard routing..."
    scripts/ci/validate-ci-router.sh

    echo "4. Running merge-train mode fixtures..."
    scripts/ci/merge-train/fixtures/validate-mode.sh

    if grep -qE '^scripts/ci/merge-train/' <<< "${PRE_PR_CHANGED_FILES}"; then
        echo "5. Running merge-train fixtures..."
        scripts/ci/merge-train/fixtures/validate-merge-train.sh
    else
        echo "5. Merge-train fixtures not required (surface unchanged)."
    fi

    echo "✅ CI shell-only pre-PR checks passed; managed build/test work was not required."
    exit 0
fi

# ---------------------------------------------------------------------------
# Capability-scoped selection (#2951). Two independent fail-safes decide
# whether SMART mode's selectors (compute-affected-projects.sh,
# honua-server-targeted-tests.sh, and the capability-impact.py crosswalk
# below) can be trusted for THIS diff, before any of them run:
#
#   1. Cross-cutting trigger paths — edits to shared test infra or to the
#      selector/catalog inputs themselves. These can silently invalidate the
#      selection the SAME run would compute (e.g. editing ci-shards.json
#      changes what "shard X" even means), so they force FULL unconditionally
#      rather than trusting a selector that may now disagree with its own
#      inputs.
#   2. `capability-impact.py validate` — the crosswalk's own completeness
#      check (every proving test sharded, every capability known, allowlist
#      entries live). A stale/broken crosswalk must not be allowed to narrow
#      anything, so a validation failure also forces FULL.
# ---------------------------------------------------------------------------
CAPABILITY_SELECTION_JSON="{}"
if [[ "${FULL}" != "1" ]]; then
    CROSS_CUTTING_FULL_TRIGGERS=$'Directory.Build.props\nDirectory.Packages.props\ntests/dotnet/Honua.TestKit/\n.github/ci-shards.json\nscripts/ci/capability-impact.py\ndocs/gis/data/feature-catalog.json\ndocs/gis/data/capability-keys.v1.json\n.github/capability-impact-allowlist.json\n.github/workflows/'
    cross_cutting_hit=""
    while IFS= read -r trigger_prefix; do
        [[ -z "${trigger_prefix}" ]] && continue
        if printf '%s\n' "${PRE_PR_CHANGED_FILES}" | grep -qE "^${trigger_prefix//./\\.}"; then
            cross_cutting_hit="${trigger_prefix}"
            break
        fi
    done <<< "${CROSS_CUTTING_FULL_TRIGGERS}"
    if [[ -n "${cross_cutting_hit}" ]]; then
        echo "⚠️  Cross-cutting path changed (${cross_cutting_hit}) — forcing FULL mode; the"
        echo "    scoped selectors themselves (or shared test infra) are not trustworthy mid-edit."
        FULL=1
    fi
fi

if [[ "${FULL}" != "1" ]]; then
    if [[ -z "${PYTHON_BIN}" ]]; then
        echo "⚠️  No python3 interpreter found — cannot validate the capability-impact"
        echo "    crosswalk, so falling back to FULL mode rather than trusting an"
        echo "    unvalidated selection."
        FULL=1
    elif ! "${PYTHON_BIN}" scripts/ci/capability-impact.py validate; then
        echo "⚠️  capability-impact crosswalk validation FAILED (see above) — falling"
        echo "    back to FULL mode rather than narrowing against a broken crosswalk."
        FULL=1
    fi
fi

if [[ "${FULL}" != "1" ]]; then
    CHANGED_FILES_LIST="$(mktemp -p "${REPO_ROOT}" --suffix=.changed-files.txt)"
    CLEANUP_PATHS+=("${CHANGED_FILES_LIST}")
    printf '%s\n' "${PRE_PR_CHANGED_FILES}" | sed '/^$/d' > "${CHANGED_FILES_LIST}"
    if ! CAPABILITY_SELECTION_JSON="$("${PYTHON_BIN}" scripts/ci/capability-impact.py select-local --changed-files "${CHANGED_FILES_LIST}")"; then
        echo "⚠️  capability-impact select-local failed — falling back to FULL mode."
        FULL=1
        CAPABILITY_SELECTION_JSON="{}"
    fi
fi

# Re-assert the FULL-overrides-FAST invariant: the cross-cutting/validation
# fail-safes above may have flipped FULL to 1 after the first check ran.
if [[ "${FULL}" == "1" ]]; then
    FAST=0
fi

if [[ "${FAST}" == "1" ]]; then
    echo "    Tier: FAST (build + format + unit + architecture; server-test shards"
    echo "          and AOT deferred to CI / the merge queue). Run without"
    echo "          HONUA_PRE_PR_FAST=1 for the full local gate before opening a PR."
fi
if [[ "${FULL}" == "1" ]]; then
    echo "    Mode: FULL (everything)."
    AFFECTED="ALL"
    TARGETED='{"run_all":true,"reason":"forced_full"}'
    CHANGED_CS=""
else
    echo "    Mode: SMART (diff vs ${BASE_REF}; set HONUA_PRE_PR_FULL=1 to force full)."
    AFFECTED="$(BASE_REF="${BASE_REF}" scripts/ci/compute-affected-projects.sh 2>/dev/null || echo ALL)"
    TARGETED="$(scripts/ci/honua-server-targeted-tests.sh --base "${BASE_REF}" 2>/dev/null || echo '{"run_all":true,"reason":"router_error"}')"
    CHANGED_CS="$(git diff --name-only "${BASE_REF}...HEAD" -- '*.cs' 2>/dev/null || true)"
fi

affected_contains() {
    # Usage: affected_contains <csproj-substring>
    [[ "${AFFECTED}" == "ALL" ]] && return 0
    printf '%s\n' "${AFFECTED}" | grep -qF "$1"
}

# ---------------------------------------------------------------------------
# Local run set — which test projects will this invocation actually execute.
# Shard selection is resolved HERE (not at test time) so the build step can
# prune test projects that never run locally. The affected closure of a
# shared-infrastructure edit (Honua.Core, Honua.Hosting, ...) is effectively
# the whole solution, and every test project copies its full dependency tree
# plus native runtime assets into bin/, so "build the closure" degrades into
# tens of GB and 10+ minutes spent compiling suites this script never runs
# (provider tests, unselected protocol shards). Test projects are dependency
# LEAVES — nothing references them — so dropping them from the solution
# filter removes exactly their own compile+copy cost while every src project
# still builds (listed, or pulled transitively as a ProjectReference of a
# kept test project). CI compiles everything in its own shards, so no
# warnings-as-errors coverage is lost overall; a test project that is itself
# edited stays in the build (its compile IS the local signal), and FULL mode
# keeps the build-everything behavior.
# ---------------------------------------------------------------------------
UNIT_TEST_PROJECTS=$'tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj\ntests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj\ntests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj\ntests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj'
ARCHITECTURE_TEST_PROJECT="tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"
MONOLITH_TEST_PROJECT="tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj"

RUN_ALL_SHARDS="$(jq -r '.run_all // false' <<< "${TARGETED}")"
SHARD_REASON="$(jq -r '.reason // "unknown"' <<< "${TARGETED}")"
if [[ "${FAST}" == "1" ]]; then
    SELECTED_SHARDS=""
elif [[ "${RUN_ALL_SHARDS}" == "true" ]]; then
    SELECTED_SHARDS="$(jq -r '.shards[].shard_name' .github/ci-shards.json)"
else
    SELECTED_SHARDS="$(jq -r '.shards[]?' <<< "${TARGETED}")"
fi

# A shard without a csproj runs against the Honua.Server.Tests monolith
# (mirroring run-server-test-shard.sh's fallback), so that project must be
# built whenever such a shard is selected.
SHARD_TEST_PROJECTS=""
while IFS= read -r shard_name; do
    [[ -z "${shard_name}" ]] && continue
    shard_csproj="$(jq -r --arg n "${shard_name}" \
        '.shards[] | select(.shard_name==$n) | .csproj // ""' .github/ci-shards.json)"
    SHARD_TEST_PROJECTS+="${shard_csproj:-${MONOLITH_TEST_PROJECT}}"$'\n'
done <<< "${SELECTED_SHARDS}"

RUN_TEST_PROJECTS="$(printf '%s\n%s\n%s\n' \
    "${UNIT_TEST_PROJECTS}" "${ARCHITECTURE_TEST_PROJECT}" "${SHARD_TEST_PROJECTS}" \
    | sed '/^$/d' | sort -u)"

# ---------------------------------------------------------------------------
# Capability-narrowed per-shard test filter (#2951). ADR-0037's shard
# selection above (SELECTED_SHARDS) stays authoritative for WHICH shards run;
# this section only decides whether a selected shard's `dotnet test --filter`
# can be narrowed down to its proving tests instead of running the shard's
# whole partition.
#
# Narrowing is safe ONLY when the capability crosswalk accounted for every
# changed file with zero ambiguity: `runAll` is false AND `unmatchedSourceFiles`
# is empty. Given the catalog's current code_location fidelity (almost always
# the endpoint-registry file, not the real handler — see the header comment),
# this is realistically true only for diffs that touch test files (and
# docs/non-source files), not handler/service source. That is a known,
# documented limitation, not a bug: it means narrowing degrades gracefully to
# "no narrowing, run the shard's normal filter" — the exact pre-#2951
# behavior — rather than ever under-testing a source change it cannot map.
#
# Even when narrowing is safe, a shard is only narrowed if the capability
# selector's OWN shard list also names it — i.e. two independent selectors
# (ADR-0037's path-prefix router and the capability crosswalk) must agree a
# shard is in scope before its test set is reduced. A shard ADR-0037 selected
# that the crosswalk does not corroborate keeps running its full filter.
#
# The narrowed filter itself unions two clauses so a directly-edited test
# file's FULL class always runs (not just whichever of its methods happen to
# be catalogued as a capability's "proving test" — the catalog's proving-test
# lists are curated exemplars, not an exhaustive enumeration of every test
# method in a file):
#   - FullyQualifiedName=<test>  for every catalog-matched proving test
#   - FullyQualifiedName~<Class> for every changed tests/**/*.cs file's class
# Both clause sets are ANDed with the shard's own base filter, so a shard only
# ever runs a SUBSET of what it would have run anyway — never something
# outside its partition.
# ---------------------------------------------------------------------------
declare -A NARROWED_SHARD_FILTER=()
declare -A NARROWED_SHARD_REASON=()
CAPABILITY_RUN_ALL="$(jq -r '.runAll // true' <<< "${CAPABILITY_SELECTION_JSON}" 2>/dev/null || echo true)"
CAPABILITY_UNMATCHED_COUNT="$(jq -r '.unmatchedSourceFiles | length' <<< "${CAPABILITY_SELECTION_JSON}" 2>/dev/null || echo 1)"
CAPABILITY_PROVING_TEST_COUNT="$(jq -r '.provingTestCount // 0' <<< "${CAPABILITY_SELECTION_JSON}" 2>/dev/null || echo 0)"
if [[ "${FULL}" != "1" && "${CAPABILITY_RUN_ALL}" == "false" && "${CAPABILITY_UNMATCHED_COUNT}" == "0" && -n "${SELECTED_SHARDS//[[:space:]]/}" ]]; then
    mapfile -t changed_test_classes < <(
        printf '%s\n' "${PRE_PR_CHANGED_FILES}" | sed '/^$/d' | while IFS= read -r f; do
            case "${f}" in
                tests/*.cs) basename "${f}" .cs ;;
            esac
        done | sort -u
    )
    mapfile -t proving_test_fqns < <(jq -r '.provingTests[]?' <<< "${CAPABILITY_SELECTION_JSON}")
    NARROW_CLAUSES=()
    for t in "${proving_test_fqns[@]}"; do
        [[ -n "${t}" ]] && NARROW_CLAUSES+=("FullyQualifiedName=${t}")
    done
    for c in "${changed_test_classes[@]}"; do
        [[ -n "${c}" ]] && NARROW_CLAUSES+=("FullyQualifiedName~${c}")
    done
    if [[ ${#NARROW_CLAUSES[@]} -gt 0 ]]; then
        NARROW_CLAUSE_EXPR="$(IFS='|'; echo "${NARROW_CLAUSES[*]}")"
        mapfile -t capability_shards < <(jq -r '.shards[]?' <<< "${CAPABILITY_SELECTION_JSON}")
        while IFS= read -r shard_name; do
            [[ -z "${shard_name}" ]] && continue
            corroborated=""
            for cs in "${capability_shards[@]}"; do
                [[ "${cs}" == "${shard_name}" ]] && corroborated=1 && break
            done
            if [[ -n "${corroborated}" ]]; then
                shard_base_filter="$(jq -r --arg n "${shard_name}" '.shards[] | select(.shard_name==$n) | .filter // ""' .github/ci-shards.json)"
                if [[ -n "${shard_base_filter}" ]]; then
                    NARROWED_SHARD_FILTER["${shard_name}"]="(${shard_base_filter})&(${NARROW_CLAUSE_EXPR})"
                    NARROWED_SHARD_REASON["${shard_name}"]="capability-narrowed (${CAPABILITY_PROVING_TEST_COUNT} proving tests, ${#changed_test_classes[@]} changed test classes)"
                fi
            fi
        done <<< "${SELECTED_SHARDS}"
    fi
fi

# Full committed diff — used to keep directly-edited test projects in the
# build even when they will not run locally.
CHANGED_FILES_ALL="$(git diff --name-only "${BASE_REF}...HEAD" 2>/dev/null || true)"

test_project_kept() {
    # Usage: test_project_kept <csproj-path> -> 0 keep, 1 prune.
    # Non-test projects are always kept; test projects are kept when they
    # will run locally or contain changes themselves.
    local proj="$1"
    [[ "${proj}" != tests/dotnet/* ]] && return 0
    grep -qxF "${proj}" <<< "${RUN_TEST_PROJECTS}" && return 0
    local dir
    dir="$(dirname "${proj}")/"
    grep -q "^${dir}" <<< "${CHANGED_FILES_ALL}" && return 0
    return 1
}

echo "1. Checking canonical instructions..."
bash scripts/ci/check-instructions-sync.sh

if [[ "${FULL}" == "1" ]]; then
    if [[ "${HONUA_PRE_PR_PRINT_BUILD_PLAN:-0}" == "1" ]]; then
        echo "BUILD PLAN: Honua.sln (FULL mode — every project)"
        exit 0
    fi
    if [[ "${DRY_RUN}" == "1" ]]; then
        echo "2-3. [dry-run] Would restore + build Honua.sln (FULL mode — every project)."
    else
        echo "2. Restoring packages (full solution)..."
        dotnet restore Honua.sln

        echo "3. Building with warnings as errors (full solution)..."
        # -p: not /p: — under Git Bash, MSYS path conversion rewrites a /-prefixed
        # switch into a Windows path, so MSBuild sees a bare
        # "p:TreatWarningsAsErrors=true" argument and rejects it as a second
        # project (MSB1008). The two forms are equivalent to MSBuild; the AOT step
        # below already uses -p: for the same reason.
        dotnet build Honua.sln --no-restore --configuration Release -p:TreatWarningsAsErrors=true
    fi
else
    # Compose the build set via a throwaway solution filter so shared
    # dependencies compile once. Architecture.Tests is always included so the
    # topology guards run.
    # The .slnf must live beside Honua.sln: its "path"/"projects" entries are
    # resolved relative to the filter file's directory, so a /tmp filter would
    # look for /tmp/Honua.sln and fail (MSB4014).
    SLNF="$(mktemp -p "${REPO_ROOT}" --suffix=.slnf)"
    CLEANUP_PATHS+=("${SLNF}")
    # A solution filter may only name projects that are members of Honua.sln.
    # Affected projects outside the solution (e.g. the customcode worker harness
    # under docker/, which has its own solution) must be dropped or MSBuild
    # fails with MSB5028; they are built by their own dedicated checks.
    #
    # MSBuild resolves a filter entry by string-matching it against the
    # solution's own project path strings, NOT by comparing resolved file
    # paths. Honua.sln spells them with backslashes ("src\Honua.Server\..."),
    # so a filter emitting POSIX separators names projects the solution
    # "does not contain" — MSB5028 at restore, before any check runs (#2871).
    # Keep the solution's literal spelling for every path we emit, and match
    # internally on a separator-normalized key so the rest of this script can
    # keep comparing against the forward-slash paths git and
    # compute-affected-projects.sh produce.
    declare -A sln_member_paths=()
    while IFS= read -r sln_proj; do
        [[ -z "${sln_proj}" ]] && continue
        sln_member_paths["${sln_proj//\\//}"]="${sln_proj}"
    done < <(grep -oE '"[^"]+\.csproj"' Honua.sln | tr -d '"' | tr -d '\r' | sort -u)
    sln_members="$(printf '%s\n' "${!sln_member_paths[@]}" | sort -u)"

    sln_literal_path() {
        # Usage: sln_literal_path <normalized-path> -> the path exactly as
        # Honua.sln spells it. Falls back to the input for paths the solution
        # does not list (nothing reaches the filter that way, but a silent
        # empty string would be worse than a visible MSB5028).
        printf '%s' "${sln_member_paths["$1"]:-$1}"
    }
    if [[ "${AFFECTED}" == "ALL" ]]; then
        # Shared infrastructure changed (props/sln/CI paths) or the affected
        # computation failed: every src project must compile, but test-project
        # pruning below still applies — that is where the bulk of the disk and
        # wall-clock goes.
        echo "   (build scope: all src projects — shared infrastructure changed)"
        candidates="${sln_members}"
    else
        candidates=""
        while IFS= read -r proj; do
            [[ -z "${proj}" ]] && continue
            if grep -qxF "${proj}" <<< "${sln_members}"; then
                candidates+="${proj}"$'\n'
            else
                echo "   (skipping ${proj} — not a Honua.sln member; built by its own gate)"
            fi
        done <<< "$(printf '%s\n' "${AFFECTED}" | sed 's#\\#/#g')"
    fi
    kept=""
    pruned_count=0
    while IFS= read -r proj; do
        [[ -z "${proj}" ]] && continue
        if test_project_kept "${proj}"; then
            kept+="${proj}"$'\n'
        else
            pruned_count=$((pruned_count + 1))
        fi
    done <<< "${candidates}"
    # Architecture.Tests always runs, so it is always part of the build set.
    kept+="${ARCHITECTURE_TEST_PROJECT}"$'\n'
    projects_json="$(printf '%s' "${kept}" \
        | sed '/^$/d' \
        | while IFS= read -r proj; do sln_literal_path "${proj}"; printf '\n'; done \
        | jq -R . | jq -s 'unique')"
    jq -n --argjson p "${projects_json}" '{solution:{path:"Honua.sln",projects:$p}}' > "${SLNF}"
    echo "   (build set: $(jq -r '.solution.projects|length' "${SLNF}") projects; pruned ${pruned_count} test projects that neither run locally nor changed)"
    if [[ "${HONUA_PRE_PR_PRINT_BUILD_PLAN:-0}" == "1" ]]; then
        echo "BUILD PLAN:"
        jq -r '.solution.projects[]' "${SLNF}"
        exit 0
    fi

    if [[ "${DRY_RUN}" == "1" ]]; then
        echo "2-3. [dry-run] Would restore + build the $(jq -r '.solution.projects|length' "${SLNF}")-project set above."
    else
        echo "2. Restoring packages (build set only)..."
        dotnet restore "${SLNF}"

        echo "3. Building with warnings as errors..."
        # -p: not /p: — see the FULL-mode build above (MSB1008 under Git Bash).
        dotnet build "${SLNF}" --no-restore --configuration Release -p:TreatWarningsAsErrors=true
    fi
fi

echo "4. Checking code format..."
# Scope by FULL (not AFFECTED): a force-full build scope (props/CI paths
# changed) does not widen the set of .cs files whose format could differ —
# CHANGED_CS is still the authoritative list in SMART mode.
if [[ "${FULL}" == "1" ]]; then
    FORMAT_TARGET=(Honua.sln)
    format_scope_args=()
else
    FORMAT_TARGET=(Honua.sln)
    # Restrict to the changed files; if none, skip.
    # Only format files that still exist (a changed file may have been deleted
    # or moved) and that are tracked .cs source.
    mapfile -t changed_arr < <(printf '%s\n' "${CHANGED_CS}" | sed '/^$/d' | while IFS= read -r f; do [[ -f "${f}" ]] && printf '%s\n' "${f}"; done)
    if [[ ${#changed_arr[@]} -eq 0 ]]; then
        echo "   (no changed .cs files on disk — skipping format check)"
        FORMAT_TARGET=()
    else
        format_scope_args=(--include "${changed_arr[@]}")
    fi
fi
if [[ ${#FORMAT_TARGET[@]} -eq 0 ]]; then
    : # already reported "skipping format check" above
elif [[ "${DRY_RUN}" == "1" ]]; then
    if [[ "${FULL}" == "1" ]]; then
        echo "   [dry-run] Would run 'dotnet format Honua.sln --verify-no-changes' (whole solution)."
    else
        echo "   [dry-run] Would run 'dotnet format --include <${#changed_arr[@]} changed .cs files> --verify-no-changes'."
    fi
else
    # Diagnostic verbosity makes dotnet-format log every analyzed document —
    # on a 76-project solution that is minutes of pure console I/O. The
    # failure output at normal verbosity still names each unformatted file.
    if ! dotnet format "${FORMAT_TARGET[@]}" "${format_scope_args[@]}" --verify-no-changes; then
        echo "❌ Format check failed. Running 'dotnet format' to fix..."
        dotnet format "${FORMAT_TARGET[@]}" "${format_scope_args[@]}"
        echo "✅ Code formatted. Please review changes and commit if needed."
        exit 1
    fi
fi

if [[ "${DRY_RUN}" == "1" ]]; then
    echo "5. [dry-run] Would pre-pull Docker images and run .NET tests (see selection below)."
else
    echo "5. Pre-pulling Docker images for faster tests..."
    docker pull postgis/postgis:16-3.4-alpine > /dev/null 2>&1 || echo "   ⚠️ Could not pre-pull PostGIS image"
fi

echo "6. Running .NET tests..."
mkdir -p ./tests/TestResults

run_unit_project() {
    local proj="$1"
    if [[ "${AFFECTED}" == "ALL" ]] || affected_contains "$(basename "${proj}")"; then
        if [[ "${DRY_RUN}" == "1" ]]; then
            echo "   - [dry-run] would run ${proj} (full suite)"
            return
        fi
        echo "   - ${proj}"
        dotnet test "${proj}" \
            --no-build \
            --no-restore \
            --configuration Release \
            --logger "console;verbosity=minimal" \
            --results-directory ./tests/TestResults \
            -- RunConfiguration.MaxCpuCount=1
    else
        echo "   - skip ${proj} (not affected)"
    fi
}

run_unit_project tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj
run_unit_project tests/dotnet/Honua.Core.Security.Tests/Honua.Core.Security.Tests.csproj
run_unit_project tests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj
run_unit_project tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj

# Server-test shards: run the targeted subset (or all, when run_all). FAST tier
# skips them entirely (CI / the merge queue is the gate); otherwise run them in
# parallel — each shard is its own dotnet-test process with its own Postgres
# container, so parallelism is modest by default.
if [[ "${FAST}" == "1" ]]; then
    echo "   (FAST tier: skipping Honua.Server.Tests shards — they run in CI / the merge queue)"
else
    # RUN_ALL_SHARDS / SHARD_REASON / SELECTED_SHARDS were resolved before the
    # build step so the build set could be pruned to match.
    echo "   Honua.Server.Tests shards (router: ${SHARD_REASON})..."
    if [[ -z "${SELECTED_SHARDS//[[:space:]]/}" ]]; then
        echo "   (no server-test shards selected for this diff)"
    elif [[ "${DRY_RUN}" == "1" ]]; then
        while IFS= read -r shard_name; do
            [[ -z "${shard_name}" ]] && continue
            if [[ -n "${NARROWED_SHARD_FILTER[${shard_name}]+set}" ]]; then
                echo "   - [dry-run] ${shard_name}: ${NARROWED_SHARD_REASON[${shard_name}]}"
            else
                echo "   - [dry-run] ${shard_name}: full shard filter (no safe capability narrowing)"
            fi
        done <<< "${SELECTED_SHARDS}"
    else
        SHARD_PARALLELISM="${HONUA_PRE_PR_SHARD_PARALLELISM:-2}"
        SHARD_STATUS_DIR="$(mktemp -d)"
        echo "   (parallelism=${SHARD_PARALLELISM}; per-shard logs under ${SHARD_STATUS_DIR})"

        run_one_shard() {
            local shard_name="$1"
            local safe="${shard_name//[^A-Za-z0-9_]/_}"
            local shard_json log_name filter max_cpu_count test_timeout_minutes csproj
            shard_json="$(jq -c --arg n "${shard_name}" '.shards[] | select(.shard_name==$n)' .github/ci-shards.json)"
            if [[ -z "${shard_json}" ]]; then
                echo "unknown" > "${SHARD_STATUS_DIR}/${safe}.status"
                echo "   ⚠️ unknown shard '${shard_name}'"
                return
            fi
            log_name="$(jq -r '.log_name' <<< "${shard_json}")"
            filter="$(jq -r '.filter' <<< "${shard_json}")"
            if [[ -n "${NARROWED_SHARD_FILTER[${shard_name}]+set}" ]]; then
                filter="${NARROWED_SHARD_FILTER[${shard_name}]}"
                echo "   - ${shard_name}: ${NARROWED_SHARD_REASON[${shard_name}]}"
            fi
            max_cpu_count="$(jq -r '.max_cpu_count // ""' <<< "${shard_json}")"
            test_timeout_minutes="$(jq -r '(.test_timeout_minutes // .timeout_minutes) | tostring' <<< "${shard_json}")"
            # Forward the shard's test project (ADR-0042 split protocol projects). When a shard has
            # no csproj, this is empty and run-server-test-shard.sh falls back to the Honua.Server.Tests
            # monolith — matching CI. Without this, protocol-project shards run their filter against the
            # monolith locally and silently match zero tests.
            csproj="$(jq -r '.csproj // ""' <<< "${shard_json}")"
            if HONUA_SERVER_TEST_SHARD_NAME="${shard_name}" \
               HONUA_SERVER_TEST_CSPROJ="${csproj}" \
               HONUA_SERVER_TEST_FILTER="${filter}" \
               HONUA_SERVER_TEST_LOG_NAME="${log_name}" \
               HONUA_SERVER_TEST_TIMEOUT_MINUTES="${HONUA_PRE_PR_SERVER_TEST_TIMEOUT_MINUTES:-${test_timeout_minutes}}" \
               HONUA_SERVER_TEST_MAX_CPU_COUNT="${HONUA_PRE_PR_MAX_CPU_COUNT:-${max_cpu_count}}" \
               HONUA_SERVER_TEST_RESULTS_DIR="./tests/TestResults" \
               HONUA_SERVER_TEST_CONFIGURATION="Release" \
               HONUA_SERVER_TEST_HEARTBEAT_SECONDS="${HONUA_PRE_PR_HEARTBEAT_SECONDS:-30}" \
               scripts/ci/run-server-test-shard.sh > "${SHARD_STATUS_DIR}/${safe}.log" 2>&1; then
                echo "pass" > "${SHARD_STATUS_DIR}/${safe}.status"
                echo "   ✅ ${shard_name}"
            else
                echo "fail" > "${SHARD_STATUS_DIR}/${safe}.status"
                echo "   ❌ ${shard_name} — tail of ${SHARD_STATUS_DIR}/${safe}.log:"
                tail -n 25 "${SHARD_STATUS_DIR}/${safe}.log" 2>/dev/null | sed 's/^/        /' || true
            fi
        }

        while IFS= read -r shard_name; do
            [[ -z "${shard_name}" ]] && continue
            # Throttle to SHARD_PARALLELISM concurrent shards (poll-based so it
            # works regardless of `wait -n` availability).
            while [[ "$(jobs -rp | wc -l)" -ge "${SHARD_PARALLELISM}" ]]; do sleep 0.5; done
            run_one_shard "${shard_name}" &
        done <<< "${SELECTED_SHARDS}"
        wait

        shard_failed=0
        for s in "${SHARD_STATUS_DIR}"/*.status; do
            [[ -e "${s}" ]] || continue
            [[ "$(cat "${s}")" == "pass" ]] || shard_failed=1
        done
        if [[ "${shard_failed}" -eq 1 ]]; then
            echo "❌ One or more server-test shards failed (logs in ${SHARD_STATUS_DIR})."
            exit 1
        fi
        rm -rf "${SHARD_STATUS_DIR}"
    fi
fi

# Architecture tests are cheap and guard the module topology — always run them.
if [[ "${DRY_RUN}" == "1" ]]; then
    echo "   - [dry-run] would run tests/dotnet/Honua.Architecture.Tests (topology guards)"
else
    echo "   - tests/dotnet/Honua.Architecture.Tests (topology guards)"
    dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
        --no-build \
        --no-restore \
        --configuration Release \
        --logger "console;verbosity=minimal" \
        --results-directory ./tests/TestResults \
        -- RunConfiguration.MaxCpuCount=1
fi

if [[ "${HONUA_PRE_PR_SKIP_AOT:-0}" == "1" || "${FAST}" == "1" ]]; then
    echo "7. Skipping AOT build (FAST tier or HONUA_PRE_PR_SKIP_AOT=1; AOT verified in CI)."
elif [[ "${DRY_RUN}" == "1" ]]; then
    if [[ "${AFFECTED}" == "ALL" ]] || affected_contains "Honua.Server.csproj"; then
        echo "7. [dry-run] Would test the AOT build (Honua.Server affected)."
    else
        echo "7. [dry-run] Would skip AOT build (Honua.Server not affected)."
    fi
elif [[ "${AFFECTED}" == "ALL" ]] || affected_contains "Honua.Server.csproj"; then
    echo "7. Testing AOT build..."
    (
        cd src/Honua.Server
        dotnet publish \
            --configuration Release \
            --runtime linux-x64 \
            --self-contained \
            -p:PublishAot=true \
            -p:HonuaSkipAdminClientForAotVerification=true \
            -p:HonuaSkipOracleForAotVerification=true \
            -p:HonuaSkipSnowflakeForAotVerification=true \
            -p:HonuaIncludeStacOpsDemo=false \
            -p:StripSymbols=true \
            -o ./publish
    )
else
    echo "7. Skipping AOT build (Honua.Server not affected)."
fi

if [[ "${DRY_RUN}" == "1" ]]; then
    echo "8. [dry-run] Would run the local architecture review."
elif [[ -n "${PYTHON_BIN}" ]]; then
    echo "8. Local architecture review..."
    # PYTHONIOENCODING=utf-8: the review prints emoji, but Python on Windows
    # defaults stdout to the ANSI code page (cp1252) and dies with
    # UnicodeEncodeError — while printing its own error message, so the real
    # verdict is lost and the run looks like a review failure. Already the
    # default on Linux, so this is a no-op in CI.
    PYTHONIOENCODING=utf-8 "${PYTHON_BIN}" scripts/ci/local-architecture-review.py || {
        echo "❌ Architecture review found blocking issues!"
        echo "   Fix violations before creating PR"
        exit 1
    }
else
    echo "8. Skipping local architecture review (requires python3)"
    echo "   OpenAI architecture review will run automatically in CI"
fi

# ---------------------------------------------------------------------------
# Honest summary (#2951) — deliberately plain/greppable so it can be pasted
# straight into the PR template's Testing section as the evidence for
# "Ran scripts/ci/pre-pr-check.sh and all checks passed".
# ---------------------------------------------------------------------------
echo ""
echo "📋 Selection summary"
if [[ "${FULL}" == "1" ]]; then
    echo "  - Mode: FULL (every project, every shard, whole-solution format)"
else
    if [[ "${FAST}" == "1" ]]; then
        echo "  - Mode: SMART, FAST tier (shards + AOT deferred to CI)"
    else
        echo "  - Mode: SMART"
    fi
    if [[ -n "${SLNF:-}" && -f "${SLNF:-/dev/null}" ]]; then
        echo "  - Build set: $(jq -r '.solution.projects|length' "${SLNF}") projects (AFFECTED=$([[ "${AFFECTED}" == "ALL" ]] && echo ALL || echo scoped))"
    fi
    echo "  - Format scope: ${#changed_arr[@]} changed .cs file(s)"
fi
echo "  - ADR-0037 shard router reason: ${SHARD_REASON:-n/a}"
if [[ -n "${SELECTED_SHARDS//[[:space:]]/}" ]]; then
    selected_count="$(printf '%s\n' "${SELECTED_SHARDS}" | sed '/^$/d' | wc -l | tr -d ' ')"
    total_count="$(jq -r '.shards | length' .github/ci-shards.json)"
    echo "  - Server-test shards: ${selected_count}/${total_count} selected"
    narrowed_count="${#NARROWED_SHARD_FILTER[@]}"
    if [[ "${narrowed_count}" -gt 0 ]]; then
        echo "    - ${narrowed_count} of those capability-narrowed to proving tests only: ${!NARROWED_SHARD_FILTER[*]}"
    fi
else
    echo "  - Server-test shards: none selected"
fi
if [[ "${CAPABILITY_SELECTION_JSON}" != "{}" ]]; then
    echo "  - Capability crosswalk: runAll=${CAPABILITY_RUN_ALL:-n/a}, reason=$(jq -r '.reason // "n/a"' <<< "${CAPABILITY_SELECTION_JSON}"), capabilities=$(jq -r '.capabilities|length' <<< "${CAPABILITY_SELECTION_JSON}"), provingTests=${CAPABILITY_PROVING_TEST_COUNT:-0}, unmatchedSourceFiles=${CAPABILITY_UNMATCHED_COUNT:-0}"
fi
echo ""

if [[ "${DRY_RUN}" == "1" ]]; then
    echo "🔎 Dry run complete — nothing was restored, built, formatted, or tested."
    exit 0
fi

echo "✅ All pre-PR checks passed! Ready to create PR."
echo ""
echo "📋 Don't forget:"
echo "  - Commit message format: 'type: description (#issue-number)'"
echo "  - PR title matches commit message"
echo "  - PR description includes 'Fixes #<number>' or 'Closes #<number>'"
echo "  - Monitor CI for LLM architecture review feedback"
echo "  - Run with HONUA_PRE_PR_FULL=1 before release / large refactors"
