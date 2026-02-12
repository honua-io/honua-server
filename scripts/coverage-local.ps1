# Local code coverage collection and reporting script for Honua Server
# PowerShell version for Windows development

param(
    [switch]$SkipIntegration,
    [switch]$OpenReport
)

# Configuration
$SolutionDir = Split-Path $PSScriptRoot -Parent
$CoverageDir = Join-Path $SolutionDir "coverage"
$ReportsDir = Join-Path $CoverageDir "reports"

Write-Host "🔍 Honua Server - Local Coverage Collection" -ForegroundColor Blue
Write-Host "================================================"

# Clean previous results
Write-Host "🧹 Cleaning previous coverage results..." -ForegroundColor Yellow
if (Test-Path $CoverageDir) {
    Remove-Item $CoverageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $CoverageDir -Force | Out-Null
New-Item -ItemType Directory -Path $ReportsDir -Force | Out-Null

Set-Location $SolutionDir

# Check for required tools
Write-Host "🔧 Checking coverage tools..." -ForegroundColor Yellow
$reportGeneratorInstalled = & dotnet tool list --global | Select-String "dotnet-reportgenerator-globaltool"
if (-not $reportGeneratorInstalled) {
    Write-Host "Installing ReportGenerator..."
    & dotnet tool install --global dotnet-reportgenerator-globaltool
}

# Restore packages
Write-Host "📦 Restoring packages..." -ForegroundColor Yellow
& dotnet restore Honua.sln

# Build solution
Write-Host "🔨 Building solution..." -ForegroundColor Yellow
& dotnet build Honua.sln --configuration Release --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Build failed!" -ForegroundColor Red
    exit 1
}

# Run unit tests with coverage
Write-Host "🧪 Running unit tests with coverage..." -ForegroundColor Yellow
$unitTestResult = & dotnet test Honua.sln `
    --no-build `
    --configuration Release `
    --filter "Category!=Integration" `
    --collect:"XPlat Code Coverage" `
    --results-directory "$CoverageDir/unit" `
    /p:CoverletOutputFormat=cobertura `
    /p:CoverletOutput="$CoverageDir/unit/"

if ($LASTEXITCODE -ne 0) {
    Write-Host "⚠️ Unit tests had failures, but continuing with coverage..." -ForegroundColor Yellow
}

# Run integration tests with coverage (unless skipped)
if (-not $SkipIntegration) {
    Write-Host "🏗️ Running integration tests with coverage..." -ForegroundColor Yellow
    $integrationTestResult = & dotnet test Honua.sln `
        --no-build `
        --configuration Release `
        --filter "Category=Integration" `
        --collect:"XPlat Code Coverage" `
        --results-directory "$CoverageDir/integration" `
        /p:CoverletOutputFormat=cobertura `
        /p:CoverletOutput="$CoverageDir/integration/"

    if ($LASTEXITCODE -ne 0) {
        Write-Host "⚠️ Integration tests had failures, but continuing with coverage..." -ForegroundColor Yellow
    }
} else {
    Write-Host "⏭️ Skipping integration tests..." -ForegroundColor Yellow
}

# Find coverage files
$coverageFiles = Get-ChildItem -Path $CoverageDir -Filter "coverage.cobertura.xml" -Recurse
if ($coverageFiles.Count -eq 0) {
    Write-Host "❌ No coverage files found!" -ForegroundColor Red
    exit 1
}

Write-Host "Found $($coverageFiles.Count) coverage file(s)" -ForegroundColor Green

# Generate merged report
Write-Host "📊 Generating coverage report..." -ForegroundColor Yellow
$coverageReports = ($coverageFiles | ForEach-Object { $_.FullName }) -join ";"

& reportgenerator `
    "-reports:$coverageReports" `
    "-targetdir:$ReportsDir" `
    "-reporttypes:Html;Cobertura;JsonSummary;TextSummary;Badges" `
    "-verbosity:Warning" `
    "-title:Honua Server Coverage Report"

if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Failed to generate coverage report!" -ForegroundColor Red
    exit 1
}

# Display results
Write-Host ""
Write-Host "✅ Coverage report generated!" -ForegroundColor Green
Write-Host "📁 Reports location: $ReportsDir"
Write-Host "🌐 HTML report: $(Join-Path $ReportsDir "index.html")"

# Display summary if available
$summaryFile = Join-Path $ReportsDir "Summary.txt"
if (Test-Path $summaryFile) {
    Write-Host ""
    Write-Host "📈 Coverage Summary:" -ForegroundColor Blue
    Get-Content $summaryFile
}

# Coverage summary (informational only)
$summaryJsonFile = Join-Path $ReportsDir "Summary.json"
if (Test-Path $summaryJsonFile) {
    try {
        $summary = Get-Content $summaryJsonFile | ConvertFrom-Json
        $lineCoverage = [double]$summary.summary.linecoverage
        $branchCoverage = [double]$summary.summary.branchcoverage

        Write-Host ""
        Write-Host "📈 Coverage Snapshot:" -ForegroundColor Blue
        Write-Host "Line coverage: $lineCoverage%" -ForegroundColor Green
        Write-Host "Branch coverage: $branchCoverage%" -ForegroundColor Green
    }
    catch {
        Write-Host "⚠️ Could not parse coverage summary" -ForegroundColor Yellow
    }
}

# Open report if requested
if ($OpenReport -or $env:CI -eq $null) {
    $response = Read-Host "`nOpen HTML coverage report in browser? (y/N)"
    if ($response -match "^[Yy]") {
        Start-Process (Join-Path $ReportsDir "index.html")
    }
}

Write-Host ""
Write-Host "🎉 Coverage analysis complete!" -ForegroundColor Green
