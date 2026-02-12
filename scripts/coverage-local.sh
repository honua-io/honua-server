#!/bin/bash
# Local code coverage collection and reporting script for Honua Server

set -euo pipefail

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
SOLUTION_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COVERAGE_DIR="${SOLUTION_DIR}/coverage"
REPORTS_DIR="${COVERAGE_DIR}/reports"

echo -e "${BLUE}🔍 Honua Server - Local Coverage Collection${NC}"
echo "================================================"

# Clean previous results
echo -e "${YELLOW}🧹 Cleaning previous coverage results...${NC}"
rm -rf "${COVERAGE_DIR}"
mkdir -p "${COVERAGE_DIR}" "${REPORTS_DIR}"

cd "${SOLUTION_DIR}"

# Install tools if needed
echo -e "${YELLOW}🔧 Checking coverage tools...${NC}"
if ! command -v reportgenerator &> /dev/null; then
    echo "Installing ReportGenerator..."
    dotnet tool install --global dotnet-reportgenerator-globaltool
fi

# Restore packages
echo -e "${YELLOW}📦 Restoring packages...${NC}"
dotnet restore Honua.sln

# Build solution
echo -e "${YELLOW}🔨 Building solution...${NC}"
dotnet build Honua.sln --configuration Release --no-restore

# Run tests with coverage
echo -e "${YELLOW}🧪 Running unit tests with coverage...${NC}"
dotnet test Honua.sln \
  --no-build \
  --configuration Release \
  --filter "Category!=Integration" \
  --collect:"XPlat Code Coverage" \
  --results-directory "${COVERAGE_DIR}/unit" \
  /p:CoverletOutputFormat=cobertura \
  /p:CoverletOutput="${COVERAGE_DIR}/unit/"

echo -e "${YELLOW}🏗️ Running integration tests with coverage...${NC}"
dotnet test Honua.sln \
  --no-build \
  --configuration Release \
  --filter "Category=Integration" \
  --collect:"XPlat Code Coverage" \
  --results-directory "${COVERAGE_DIR}/integration" \
  /p:CoverletOutputFormat=cobertura \
  /p:CoverletOutput="${COVERAGE_DIR}/integration/"

# Generate merged report
echo -e "${YELLOW}📊 Generating coverage report...${NC}"
reportgenerator \
  -reports:"${COVERAGE_DIR}/**/coverage.cobertura.xml" \
  -targetdir:"${REPORTS_DIR}" \
  -reporttypes:"Html;Cobertura;JsonSummary;TextSummary;Badges" \
  -verbosity:"Warning" \
  -title:"Honua Server Coverage Report"

# Display summary
echo -e "\n${GREEN}✅ Coverage report generated!${NC}"
echo -e "📁 Reports location: ${REPORTS_DIR}"
echo -e "🌐 Open HTML report: ${REPORTS_DIR}/index.html"

# Extract and display key metrics
if [[ -f "${REPORTS_DIR}/Summary.txt" ]]; then
    echo -e "\n${BLUE}📈 Coverage Summary:${NC}"
    cat "${REPORTS_DIR}/Summary.txt"
fi

# Coverage summary (informational only)
if [[ -f "${REPORTS_DIR}/Summary.json" ]]; then
    LINE_COVERAGE=$(jq -r '.summary.linecoverage' "${REPORTS_DIR}/Summary.json" 2>/dev/null || echo "0")
    BRANCH_COVERAGE=$(jq -r '.summary.branchcoverage' "${REPORTS_DIR}/Summary.json" 2>/dev/null || echo "0")

    echo -e "\n${BLUE}📈 Coverage Snapshot:${NC}"
    echo -e "Line coverage: ${GREEN}${LINE_COVERAGE}%${NC}"
    echo -e "Branch coverage: ${GREEN}${BRANCH_COVERAGE}%${NC}"
fi

# Open HTML report if on macOS
if [[ "$OSTYPE" == "darwin"* ]]; then
    read -p "Open HTML coverage report in browser? (y/N) " -n 1 -r
    echo
    if [[ $REPLY =~ ^[Yy]$ ]]; then
        open "${REPORTS_DIR}/index.html"
    fi
fi

echo -e "\n${GREEN}🎉 Coverage analysis complete!${NC}"
