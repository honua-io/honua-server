#!/usr/bin/env bash
set -euo pipefail

echo "🔍 Running pre-PR validation..."

echo "1. Checking canonical instructions..."
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
    --filter "FullyQualifiedName!~Honua.Server.Tests.Features.&FullyQualifiedName!~Honua.Server.Tests.Import.&FullyQualifiedName!~Honua.Server.Tests.Performance.&FullyQualifiedName!~Honua.Server.Tests.Comprehensive.&FullyQualifiedName!~Honua.Server.Tests.Admin.&FullyQualifiedName!~Honua.Server.Tests.Infrastructure.&FullyQualifiedName!~Honua.Server.Tests.Cloud.&FullyQualifiedName!~Honua.Server.Tests.Features.Protocols.Cog&FullyQualifiedName!~Honua.Server.Tests.Contract.&FullyQualifiedName!~Honua.Server.Tests.AdminEndpointTests" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Admin.|FullyQualifiedName~Honua.Server.Tests.Infrastructure.|FullyQualifiedName~Honua.Server.Tests.AdminEndpointTests" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Cloud.|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Cog|FullyQualifiedName~Honua.Server.Tests.Contract." \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Import." \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Performance." \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Comprehensive." \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GeoServicesSpatialFilterBuilderTests|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Api.Features|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Stac|FullyQualifiedName~Honua.Server.Tests.Features.API" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wms|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wmts|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.MapServer|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Tiles" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.Infrastructure|FullyQualifiedName~Honua.Server.Tests.Features.Caching|FullyQualifiedName~Honua.Server.Tests.Features.Security|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.Catalog|FullyQualifiedName~Honua.Server.Tests.Features.FileStorage|FullyQualifiedName~Honua.Server.Tests.Features.Styling" \
    --logger "console;verbosity=minimal" \
    --results-directory ./tests/TestResults \
    -- RunConfiguration.MaxCpuCount=1

dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj \
    --no-build \
    --no-restore \
    --configuration Release \
    --filter "FullyQualifiedName~Honua.Server.Tests.Features.Eval|FullyQualifiedName~Honua.Server.Tests.Features.Geoprocessing|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Ogc.Api.Processes|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Grpc|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.Mcp|FullyQualifiedName~Honua.Server.Tests.Features.Protocols.GeoServices.GPServer" \
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

echo "8. Local architecture review..."
if command -v python3 >/dev/null 2>&1; then
    python3 scripts/local-architecture-review.py || {
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
