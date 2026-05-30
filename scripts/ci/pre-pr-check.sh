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

BASE_REF="${HONUA_PRE_PR_BASE:-origin/trunk}"
FULL="${HONUA_PRE_PR_FULL:-0}"
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
    SLNF="$(mktemp --suffix=.slnf)"
    trap 'rm -f "${SLNF}"' EXIT
    projects_json="$(printf '%s\n' "${AFFECTED}" \
        | sed '/^$/d' \
        | sed 's#\\#/#g' \
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
run_unit_project tests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj
run_unit_project tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj

# Server-test shards: run the targeted subset (or all, when run_all).
RUN_ALL_SHARDS="$(jq -r '.run_all // false' <<< "${TARGETED}")"
SHARD_REASON="$(jq -r '.reason // "unknown"' <<< "${TARGETED}")"
echo "   Honua.Server.Tests shards (router: ${SHARD_REASON})..."
if [[ "${RUN_ALL_SHARDS}" == "true" ]]; then
    SELECTED_SHARDS="$(jq -r '.shards[].shard_name' .github/ci-shards.json)"
else
    SELECTED_SHARDS="$(jq -r '.shards[]?' <<< "${TARGETED}")"
fi
if [[ -z "${SELECTED_SHARDS//[[:space:]]/}" ]]; then
    echo "   (no server-test shards selected for this diff)"
else
    while IFS= read -r shard_name; do
        [[ -z "${shard_name}" ]] && continue
        shard_json="$(jq -c --arg n "${shard_name}" '.shards[] | select(.shard_name==$n)' .github/ci-shards.json)"
        [[ -z "${shard_json}" ]] && { echo "   ⚠️ unknown shard '${shard_name}'"; continue; }
        log_name="$(jq -r '.log_name' <<< "${shard_json}")"
        filter="$(jq -r '.filter' <<< "${shard_json}")"
        max_cpu_count="$(jq -r '.max_cpu_count // ""' <<< "${shard_json}")"
        test_timeout_minutes="$(jq -r '(.test_timeout_minutes // .timeout_minutes) | tostring' <<< "${shard_json}")"
        echo "   - ${shard_name} (test timeout ${test_timeout_minutes}m)"
        HONUA_SERVER_TEST_SHARD_NAME="${shard_name}" \
        HONUA_SERVER_TEST_FILTER="${filter}" \
        HONUA_SERVER_TEST_LOG_NAME="${log_name}" \
        HONUA_SERVER_TEST_TIMEOUT_MINUTES="${HONUA_PRE_PR_SERVER_TEST_TIMEOUT_MINUTES:-${test_timeout_minutes}}" \
        HONUA_SERVER_TEST_MAX_CPU_COUNT="${HONUA_PRE_PR_MAX_CPU_COUNT:-${max_cpu_count}}" \
        HONUA_SERVER_TEST_RESULTS_DIR="./tests/TestResults" \
        HONUA_SERVER_TEST_CONFIGURATION="Release" \
        HONUA_SERVER_TEST_HEARTBEAT_SECONDS="${HONUA_PRE_PR_HEARTBEAT_SECONDS:-30}" \
        scripts/ci/run-server-test-shard.sh
    done <<< "${SELECTED_SHARDS}"
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

if [[ "${HONUA_PRE_PR_SKIP_AOT:-0}" == "1" ]]; then
    echo "7. Skipping AOT build (HONUA_PRE_PR_SKIP_AOT=1)."
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
