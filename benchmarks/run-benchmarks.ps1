#!/usr/bin/env pwsh

<#
.SYNOPSIS
    Runs Honua Server performance benchmarks with various configurations.

.DESCRIPTION
    This script provides a convenient way to run performance benchmarks with different
    configurations, output formats, and profiling options. It handles setup, execution,
    and cleanup automatically.

.PARAMETER Category
    Benchmark category to run. Options: All, SqlGeneration, Query

.PARAMETER Job
    Job configuration. Options: Default, Short, Long, Memory

.PARAMETER Output
    Output formats. Options: Console, Json, Html, Csv, Markdown, All

.PARAMETER Profiler
    Profiler to use. Options: None, ETW (Windows), Perf (Linux)

.PARAMETER Clean
    Clean previous benchmark results before running

.PARAMETER Validate
    Run validation checks after benchmarks complete

.EXAMPLE
    ./run-benchmarks.ps1 -Category SqlGeneration -Job Short -Output Json
    Run SQL generation benchmarks with short job configuration and JSON output

.EXAMPLE
    ./run-benchmarks.ps1 -Category All -Job Default -Output All -Clean
    Run all benchmarks with default configuration, all output formats, cleaning previous results

.EXAMPLE
    ./run-benchmarks.ps1 -Category Query -Profiler ETW -Validate
    Run query benchmarks with ETW profiling and validation
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("All", "SqlGeneration", "Query")]
    [string]$Category = "All",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Default", "Short", "Long", "Memory")]
    [string]$Job = "Default",

    [Parameter(Mandatory = $false)]
    [ValidateSet("Console", "Json", "Html", "Csv", "Markdown", "All")]
    [string]$Output = "Console",

    [Parameter(Mandatory = $false)]
    [ValidateSet("None", "ETW", "Perf")]
    [string]$Profiler = "None",

    [Parameter(Mandatory = $false)]
    [switch]$Clean,

    [Parameter(Mandatory = $false)]
    [switch]$Validate
)

# Configuration
$ProjectPath = Join-Path $PSScriptRoot "Honua.Benchmarks"
$ArtifactsPath = Join-Path $ProjectPath "BenchmarkDotNet.Artifacts"
$ResultsPath = Join-Path $ArtifactsPath "results"

# Helper functions
function Write-Header {
    param([string]$Message)

    Write-Host ""
    Write-Host "=" * 80 -ForegroundColor Cyan
    Write-Host $Message -ForegroundColor Yellow
    Write-Host "=" * 80 -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Green
}

function Write-Warning {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Yellow
}

function Write-Error {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Red
}

function Test-Prerequisites {
    Write-Info "Checking prerequisites..."

    # Check .NET SDK
    try {
        $dotnetVersion = dotnet --version
        Write-Info "✓ .NET SDK version: $dotnetVersion"
    }
    catch {
        Write-Error "✗ .NET SDK not found. Please install .NET 10.0 or later."
        exit 1
    }

    # Check PostgreSQL for query benchmarks
    if ($Category -in @("All", "Query")) {
        try {
            $env:PGPASSWORD = "honua"
            $pgResult = psql -h localhost -U honua -d honua_dev -c "SELECT version();" -t 2>$null
            if ($LASTEXITCODE -eq 0) {
                Write-Info "✓ PostgreSQL connection successful"
            }
            else {
                Write-Warning "⚠ PostgreSQL connection failed. Query benchmarks may fail."
            }
        }
        catch {
            Write-Warning "⚠ PostgreSQL not available. Query benchmarks will be skipped."
        }
    }

    Write-Info "Prerequisites check complete."
}

function Clear-PreviousResults {
    if ($Clean -and (Test-Path $ArtifactsPath)) {
        Write-Info "Cleaning previous benchmark results..."
        Remove-Item $ArtifactsPath -Recurse -Force -ErrorAction SilentlyContinue
        Write-Info "✓ Previous results cleaned"
    }
}

function Build-Arguments {
    $args = @()

    # Filter by category
    if ($Category -ne "All") {
        $args += "--filter"
        $args += "*$Category*"
    }

    # Job configuration
    switch ($Job) {
        "Short" { $args += "--job"; $args += "short" }
        "Long" { $args += "--job"; $args += "long" }
        "Memory" { $args += "--job"; $args += "dry"; $args += "--diagnosers"; $args += "memory" }
    }

    # Output formats
    if ($Output -ne "Console") {
        $exporters = switch ($Output) {
            "Json" { "json" }
            "Html" { "html" }
            "Csv" { "csv" }
            "Markdown" { "markdown" }
            "All" { "json,html,csv,markdown" }
        }
        $args += "--exporters"
        $args += $exporters
    }

    # Profiler
    if ($Profiler -ne "None") {
        $args += "--profiler"
        $args += $Profiler
    }

    return $args
}

function Start-Benchmarks {
    param([string[]]$Arguments)

    Write-Header "Running Benchmarks"
    Write-Info "Category: $Category"
    Write-Info "Job: $Job"
    Write-Info "Output: $Output"
    Write-Info "Profiler: $Profiler"
    Write-Info "Arguments: $($Arguments -join ' ')"

    Push-Location $ProjectPath
    try {
        $startTime = Get-Date

        if ($Arguments.Count -gt 0) {
            dotnet run -c Release -- @Arguments
        }
        else {
            dotnet run -c Release
        }

        $endTime = Get-Date
        $duration = $endTime - $startTime

        if ($LASTEXITCODE -eq 0) {
            Write-Info "✓ Benchmarks completed successfully in $($duration.ToString('mm\:ss'))"
            return $true
        }
        else {
            Write-Error "✗ Benchmarks failed with exit code $LASTEXITCODE"
            return $false
        }
    }
    finally {
        Pop-Location
    }
}

function Show-Results {
    if (Test-Path $ResultsPath) {
        Write-Header "Benchmark Results"

        # Show latest results files
        $resultFiles = Get-ChildItem $ResultsPath -Filter "*.html" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($resultFiles) {
            $latestHtml = $resultFiles[0].FullName
            Write-Info "Latest HTML report: $latestHtml"

            if ($IsWindows) {
                Write-Info "Opening results in default browser..."
                Start-Process $latestHtml
            }
            elseif ($IsMacOS) {
                Write-Info "Opening results in default browser..."
                open $latestHtml
            }
            else {
                Write-Info "HTML report available at: $latestHtml"
            }
        }

        # Show JSON results if available
        $jsonFiles = Get-ChildItem $ResultsPath -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($jsonFiles) {
            Write-Info "JSON results: $($jsonFiles[0].FullName)"
        }

        # Show summary statistics
        $logFiles = Get-ChildItem $ArtifactsPath -Filter "*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($logFiles) {
            $latestLog = $logFiles[0].FullName
            Write-Info "Latest log: $latestLog"

            # Extract summary from log
            $summary = Get-Content $latestLog | Where-Object { $_ -match "Summary|Total time|Mean|StdDev" } | Select-Object -Last 10
            if ($summary) {
                Write-Info "Summary:"
                $summary | ForEach-Object { Write-Host "  $_" }
            }
        }
    }
    else {
        Write-Warning "No benchmark results found at $ResultsPath"
    }
}

function Test-Performance {
    if (-not $Validate) {
        return
    }

    Write-Header "Validating Performance Results"

    # Check for performance regressions
    $jsonFiles = Get-ChildItem $ResultsPath -Filter "*.json" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
    if (-not $jsonFiles) {
        Write-Warning "No JSON results found for validation"
        return
    }

    $results = Get-Content $jsonFiles[0].FullName | ConvertFrom-Json
    $failures = @()

    foreach ($benchmark in $results.Benchmarks) {
        $method = $benchmark.Method
        $mean = [double]$benchmark.Statistics.Mean
        $allocated = [double]$benchmark.Memory.Allocated

        # Define thresholds based on benchmark type
        $meanThreshold = switch -Regex ($method) {
            "SqlGeneration.*Simple" { 1000 } # 1μs in nanoseconds
            "SqlGeneration.*Complex" { 10000 } # 10μs
            default { [double]::MaxValue }
        }

        $allocatedThreshold = switch -Regex ($method) {
            "SqlGeneration" { 1024 } # 1KB
            default { [double]::MaxValue }
        }

        # Check thresholds
        if ($mean -gt $meanThreshold) {
            $failures += "❌ $method: Mean $([math]::Round($mean / 1000000, 2))ms exceeds threshold $([math]::Round($meanThreshold / 1000000, 2))ms"
        }

        if ($allocated -gt $allocatedThreshold) {
            $failures += "❌ $method: Allocated $([math]::Round($allocated / 1024, 2))KB exceeds threshold $([math]::Round($allocatedThreshold / 1024, 2))KB"
        }
    }

    if ($failures.Count -eq 0) {
        Write-Info "✅ All performance targets met!"
    }
    else {
        Write-Warning "⚠️ Performance issues detected:"
        $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    }
}

# Main execution
try {
    Write-Header "Honua Server Performance Benchmarks"

    Test-Prerequisites
    Clear-PreviousResults

    $arguments = Build-Arguments
    $success = Start-Benchmarks -Arguments $arguments

    if ($success) {
        Show-Results
        Test-Performance

        Write-Header "Benchmark Run Complete"
        Write-Info "✅ Benchmarks completed successfully!"
        Write-Info "Results saved to: $ArtifactsPath"
    }
    else {
        Write-Header "Benchmark Run Failed"
        Write-Error "❌ Benchmarks failed. Check the output above for details."
        exit 1
    }
}
catch {
    Write-Error "Fatal error: $($_.Exception.Message)"
    Write-Error $_.ScriptStackTrace
    exit 1
}
