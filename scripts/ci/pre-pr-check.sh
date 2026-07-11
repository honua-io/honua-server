#!/usr/bin/env bash
set -euo pipefail

# Pre-PR validation with the SAME smart filters CI uses, so a local run only
# builds, formats, and tests what the diff actually touches instead of grinding
# the whole solution and every server-test shard every time.
#
# Scope is derived from the diff against a base ref (default: origin/trunk):
#   - build      -> scripts/ci/compute-affected-projects.sh (affected .csproj
#                   closure, or ALL when shared infrastructure changed)
#   - server tests -> scripts/ci/honua-server-targeted-tests.sh (targeted shard
#                   subset, or run_all)
#   - unit tests -> only the *.Tests projects in the affected closure
#   - format     -> only the changed *.cs files
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
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${REPO_ROOT}"

if ! command -v jq >/dev/null 2>&1; then
    echo "❌ jq is required to read .github/ci-shards.json"
    exit 1
fi

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

echo "1. Checking canonical instructions..."
bash scripts/ci/check-instructions-sync.sh

echo "2. Restoring packages..."
dotnet restore Honua.sln

echo "3. Building with warnings as errors..."
if [[ "${AFFECTED}" == "ALL" ]]; then
    echo "   (full solution — shared infrastructure changed or full mode)"
    dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
else
    # Build only the affected project closure via a throwaway solution filter so
    # shared dependencies compile once. Architecture.Tests is always included so
    # the topology guards run.
    # The .slnf must live beside Honua.sln: its "path"/"projects" entries are
    # resolved relative to the filter file's directory, so a /tmp filter would
    # look for /tmp/Honua.sln and fail (MSB4014).
    SLNF="$(mktemp -p "${REPO_ROOT}" --suffix=.slnf)"
    trap 'rm -f "${SLNF}"' EXIT
    # A solution filter may only name projects that are members of Honua.sln.
    # Affected projects outside the solution (e.g. the customcode worker harness
    # under docker/, which has its own solution) must be dropped or MSBuild
    # fails with MSB5028; they are built by their own dedicated checks.
    sln_members="$(grep -oE '"[^"]+\.csproj"' Honua.sln | tr -d '"' | sed 's#\\#/#g' | sort -u)"
    affected_in_sln=""
    while IFS= read -r proj; do
        [[ -z "${proj}" ]] && continue
        if grep -qxF "${proj}" <<< "${sln_members}"; then
            affected_in_sln+="${proj}"$'\n'
        else
            echo "   (skipping ${proj} — not a Honua.sln member; built by its own gate)"
        fi
    done <<< "$(printf '%s\n' "${AFFECTED}" | sed 's#\\#/#g')"
    projects_json="$(printf '%s' "${affected_in_sln}" \
        | sed '/^$/d' \
        | jq -R . | jq -s 'unique')"
    projects_json="$(jq -n --argjson p "${projects_json}" \
        '($p + ["tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj"]) | unique')"
    jq -n --argjson p "${projects_json}" '{solution:{path:"Honua.sln",projects:$p}}' > "${SLNF}"
    echo "   (affected projects only: $(jq -r '.solution.projects|length' "${SLNF}") projects)"
    dotnet build "${SLNF}" --no-restore --configuration Release /p:TreatWarningsAsErrors=true
fi

echo "4. Checking code format..."
if [[ "${AFFECTED}" == "ALL" ]]; then
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
if [[ ${#FORMAT_TARGET[@]} -gt 0 ]]; then
    if ! dotnet format "${FORMAT_TARGET[@]}" "${format_scope_args[@]}" --verify-no-changes --verbosity diagnostic; then
        echo "❌ Format check failed. Running 'dotnet format' to fix..."
        dotnet format "${FORMAT_TARGET[@]}" "${format_scope_args[@]}"
        echo "✅ Code formatted. Please review changes and commit if needed."
        exit 1
    fi
fi

echo "5. Pre-pulling Docker images for faster tests..."
docker pull postgis/postgis:16-3.4-alpine > /dev/null 2>&1 || echo "   ⚠️ Could not pre-pull PostGIS image"

echo "6. Running .NET tests..."
mkdir -p ./tests/TestResults

run_unit_project() {
    local proj="$1"
    if [[ "${AFFECTED}" == "ALL" ]] || affected_contains "$(basename "${proj}")"; then
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
    RUN_ALL_SHARDS="$(jq -r '.run_all // false' <<< "${TARGETED}")"
    SHARD_REASON="$(jq -r '.reason // "unknown"' <<< "${TARGETED}")"
    echo "   Honua.Server.Tests shards (router: ${SHARD_REASON})..."
    if [[ "${RUN_ALL_SHARDS}" == "true" ]]; then
        SELECTED_SHARDS="$(jq -r '.shards[].shard_name' .github/ci-shards.json | tr -d '\r')"
    else
        SELECTED_SHARDS="$(jq -r '.shards[]?' <<< "${TARGETED}" | tr -d '\r')"
    fi
    if [[ -z "${SELECTED_SHARDS//[[:space:]]/}" ]]; then
        echo "   (no server-test shards selected for this diff)"
    else
        SHARD_PARALLELISM="${HONUA_PRE_PR_SHARD_PARALLELISM:-2}"
        SHARD_STATUS_DIR="$(mktemp -d)"
        echo "   (parallelism=${SHARD_PARALLELISM}; per-shard logs under ${SHARD_STATUS_DIR})"

        run_one_shard() {
            local shard_name="$1"
            shard_name="${shard_name%$'\r'}"
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
echo "   - tests/dotnet/Honua.Architecture.Tests (topology guards)"
dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

if [[ "${HONUA_PRE_PR_SKIP_AOT:-0}" == "1" || "${FAST}" == "1" ]]; then
    echo "7. Skipping AOT build (FAST tier or HONUA_PRE_PR_SKIP_AOT=1; AOT verified in CI)."
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

echo "8. Local architecture review..."
if command -v python3 >/dev/null 2>&1; then
    python3 scripts/ci/local-architecture-review.py || {
        echo "❌ Architecture review found blocking issues!"
        echo "   Fix violations before creating PR"
        exit 1
    }
else
    echo "⚠️  Skipping local architecture review (requires python3)"
    echo "   OpenAI architecture review will run automatically in CI"
fi

echo "✅ All pre-PR checks passed! Ready to create PR."
echo ""
echo "📋 Don't forget:"
echo "  - Commit message format: 'type: description (#issue-number)'"
echo "  - PR title matches commit message"
echo "  - PR description includes 'Fixes #<number>' or 'Closes #<number>'"
echo "  - Monitor CI for LLM architecture review feedback"
echo "  - Run with HONUA_PRE_PR_FULL=1 before release / large refactors"
