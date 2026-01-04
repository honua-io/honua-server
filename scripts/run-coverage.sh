#!/bin/bash

# Comprehensive test coverage script for achieving 95%+ coverage

set -e

echo "🧪 Running comprehensive test suite with coverage..."

# Clean previous results
rm -rf TestResults/
rm -rf coverage-reports/
mkdir -p coverage-reports

echo "🔄 Restoring packages..."
dotnet restore

echo "🏗️ Building solution..."
dotnet build --no-restore

echo "🧪 Running unit tests..."
dotnet test tests/Honua.Core.Tests \
  --no-build \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=core-unit-tests.trx" \
  --results-directory TestResults/UnitTests

echo "🧪 Running integration tests..."
dotnet test tests/Honua.Server.Tests \
  --no-build \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=server-integration-tests.trx" \
  --results-directory TestResults/IntegrationTests

echo "🧪 Running PostgreSQL tests..."
dotnet test tests/Honua.Postgres.Tests \
  --no-build \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=postgres-tests.trx" \
  --results-directory TestResults/PostgresTests

echo "🧪 Running architecture tests..."
dotnet test tests/Honua.Architecture.Tests \
  --no-build \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=architecture-tests.trx" \
  --results-directory TestResults/ArchitectureTests

echo "🚀 Running performance tests..."
dotnet test tests/Honua.LoadTests \
  --no-build \
  --configuration Release \
  --collect:"XPlat Code Coverage" \
  --settings coverage.runsettings \
  --logger "trx;LogFileName=performance-tests.trx" \
  --results-directory TestResults/PerformanceTests

echo "📊 Generating coverage report..."

# Find all coverage files
COVERAGE_FILES=$(find TestResults -name "*.cobertura.xml" -exec echo -n "-reports:{} " \;)

# Generate comprehensive HTML report
if command -v reportgenerator &> /dev/null; then
    reportgenerator \
      $COVERAGE_FILES \
      -targetdir:coverage-reports \
      -reporttypes:Html_Dark \
      -historydir:coverage-reports/history \
      -title:"Honua Server Test Coverage" \
      -tag:"$(git rev-parse --short HEAD)" \
      -verbosity:Info
else
    echo "⚠️  ReportGenerator not found. Install with: dotnet tool install -g dotnet-reportgenerator-globaltool"
fi

# Generate JSON summary for CI
if command -v reportgenerator &> /dev/null; then
    reportgenerator \
      $COVERAGE_FILES \
      -targetdir:coverage-reports/summary \
      -reporttypes:JsonSummary
fi

echo "📈 Coverage Quality Gates Check..."

# Extract coverage percentages (this would need actual implementation)
LINE_COVERAGE=$(cat coverage-reports/summary/Summary.json | jq -r '.coverage.linecoverage' 2>/dev/null || echo "0")
BRANCH_COVERAGE=$(cat coverage-reports/summary/Summary.json | jq -r '.coverage.branchcoverage' 2>/dev/null || echo "0")

echo "Line Coverage: ${LINE_COVERAGE}%"
echo "Branch Coverage: ${BRANCH_COVERAGE}%"

# Quality gates
if (( $(echo "$LINE_COVERAGE >= 95" | bc -l 2>/dev/null || echo "0") )); then
    echo "✅ Line coverage target met (95%)"
else
    echo "❌ Line coverage below target: ${LINE_COVERAGE}% (target: 95%)"
    exit 1
fi

if (( $(echo "$BRANCH_COVERAGE >= 90" | bc -l 2>/dev/null || echo "0") )); then
    echo "✅ Branch coverage target met (90%)"
else
    echo "❌ Branch coverage below target: ${BRANCH_COVERAGE}% (target: 90%)"
    exit 1
fi

echo "✅ All coverage targets met!"
echo "📊 Full report available at: coverage-reports/index.html"