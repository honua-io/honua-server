#!/usr/bin/env bash
set -euo pipefail

echo "🔍 Running pre-PR validation..."

echo "1. Checking canonical instructions..."
bash scripts/ci/check-instructions-sync.sh

echo "2. Restoring packages..."
dotnet restore Honua.sln

echo "3. Building with warnings as errors..."
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true

echo "4. Checking code format..."
if ! dotnet format Honua.sln --verify-no-changes --verbosity diagnostic; then
    echo "❌ Format check failed. Running 'dotnet format Honua.sln' to fix..."
    dotnet format Honua.sln
    echo "✅ Code formatted. Please review changes and commit if needed."
    exit 1
fi

echo "5. Pre-pulling Docker images for faster tests..."
docker pull postgis/postgis:16-3.4-alpine > /dev/null 2>&1 || echo "   ⚠️ Could not pre-pull PostGIS image"

echo "6. Running all .NET tests..."
mkdir -p ./tests/TestResults

dotnet test tests/dotnet/Honua.Core.Tests/Honua.Core.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.LoadTests/Honua.LoadTests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Postgres.Tests/Honua.Postgres.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

if ! command -v jq >/dev/null 2>&1; then
    echo "❌ jq is required to read .github/ci-shards.json"
    exit 1
fi

echo "   Running Honua.Server.Tests shards from .github/ci-shards.json..."
while IFS= read -r shard_json; do
    shard_name="$(jq -r '.shard_name' <<< "${shard_json}")"
    log_name="$(jq -r '.log_name' <<< "${shard_json}")"
    filter="$(jq -r '.filter' <<< "${shard_json}")"
    max_cpu_count="$(jq -r '.max_cpu_count' <<< "${shard_json}")"
    test_timeout_minutes="$(jq -r '.test_timeout_minutes' <<< "${shard_json}")"

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
done < <(
    jq -c '
      .shards[]
      | {
          shard_name,
          log_name,
          filter,
          max_cpu_count: (.max_cpu_count // ""),
          test_timeout_minutes: ((.test_timeout_minutes // .timeout_minutes) | tostring)
        }
    ' .github/ci-shards.json
)

dotnet test tests/dotnet/Honua.Architecture.Tests/Honua.Architecture.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

echo "7. Testing AOT build..."
cd src/Honua.Server
dotnet publish \
    --configuration Release \
    --runtime linux-x64 \
    --self-contained \
    -p:PublishAot=true \
    -p:HonuaSkipAdminClientForAotVerification=true \
    -p:StripSymbols=true \
    -o ./publish
cd ../..

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
