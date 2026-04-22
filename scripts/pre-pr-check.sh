#!/usr/bin/env bash
set -euo pipefail

echo "🔍 Running pre-PR validation..."

echo "1. Checking instruction sync..."
bash scripts/check-instructions-sync.sh

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

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName!~Honua.Server.Tests.Features." \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.FeatureServer|FullyQualifiedName~Honua.Server.Tests.Features.OData|FullyQualifiedName~Honua.Server.Tests.Features.OgcFeatures|FullyQualifiedName~Honua.Server.Tests.Features.API" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.OgcMaps|FullyQualifiedName~Honua.Server.Tests.Features.OgcTiles|FullyQualifiedName~Honua.Server.Tests.Features.MapServer|FullyQualifiedName~Honua.Server.Tests.Features.ImageServer|FullyQualifiedName~Honua.Server.Tests.Features.GeometryService" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.Infrastructure|FullyQualifiedName~Honua.Server.Tests.Features.Caching|FullyQualifiedName~Honua.Server.Tests.Features.Security|FullyQualifiedName~Honua.Server.Tests.Features.GeoservicesCatalog|FullyQualifiedName~Honua.Server.Tests.Features.FileStorage|FullyQualifiedName~Honua.Server.Tests.Features.Styling" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

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

echo "8. Local Claude architecture review..."
if command -v python3 >/dev/null 2>&1; then
    python3 scripts/claude-architecture-review.py || {
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
