# Benchmark Reproduction

This guide provides step-by-step instructions to reproduce Honua's published benchmark results on your own infrastructure. For methodology and statistical approach, see [BENCHMARK_METHODOLOGY.md](BENCHMARK_METHODOLOGY.md). For the latest published numbers, see [BENCHMARK_RESULTS.md](BENCHMARK_RESULTS.md).

## Prerequisites

### Hardware

| Requirement | Minimum | Recommended |
| --- | --- | --- |
| CPU cores | 4 | 8+ |
| RAM | 8 GB | 16+ GB |
| Disk | 50 GB free (SSD) | SSD with dedicated I/O |

### Software

- .NET SDK 10.0 or later
- Docker and Docker Compose (for PostGIS and Redis containers)
- PostgreSQL client (`psql`) — for environment capture
- Python 3.10+ — for regression check script
- `jq` — to extract the baseline Git SHA from JSON
- Git — to clone the repository at a specific SHA

## Step 1: Clone and Checkout

```bash
git clone https://github.com/honua-io/honua-server.git
cd honua-server

# Check out the release tag or commit matching the published baseline
git checkout $(jq -r '.GitSHA // empty' performance-baseline.json 2>/dev/null || echo "trunk")
```

## Step 2: Start Services

The benchmark suite requires PostGIS and optionally Redis. Use the provided Docker Compose configuration:

```bash
docker compose up -d postgres redis
```

Verify services are healthy:

```bash
docker compose ps
psql "$HONUA_BENCH_DB_URL" -c "SELECT PostGIS_Full_Version();"
```

If you are not using Docker Compose, ensure:
- PostgreSQL 16 with PostGIS 3.4 is accessible
- Set `ConnectionStrings__DefaultConnection` or `HONUA_BENCH_DB_URL` to your connection string
- Redis 7 is accessible on the default port (for caching benchmarks)

## Step 3: Capture Environment

Run the environment capture script to document your hardware, runtime, and service versions:

```bash
./scripts/capture-bench-environment.sh
```

This produces a markdown disclosure block you can paste alongside your results. Compare it against the [published environment](BENCHMARK_RESULTS.md#environment-disclosure) to understand differences.

## Step 4: Run Benchmarks

### All benchmarks

```bash
cd benchmarks/Honua.Benchmarks
dotnet run -c Release
```

Or use the helper script with JSON export:

```bash
./benchmarks/run-benchmarks.sh --category All --output Json
```

### Query benchmarks

```bash
./benchmarks/run-benchmarks.sh --category Query --output Json
```

This runs the `QueryBenchmarks` class: `SimpleWhereQuery`, `SpatialBboxQuery`, `CombinedWhereAndSpatialQuery`, `PaginatedQuery`, `LargeResultSet`.

### SQL generation benchmarks

```bash
./benchmarks/run-benchmarks.sh --category SqlGeneration --output Json
```

### Specific benchmark filter

```bash
cd benchmarks/Honua.Benchmarks
dotnet run -c Release --filter "*CachingPerformance*"
```

### Short run (development iteration)

```bash
./benchmarks/run-benchmarks.sh --category Query --job Short --output Console
```

### Full run with all exporters

```bash
./benchmarks/run-benchmarks.sh --category All --output All
```

Results are written to `benchmarks/Honua.Benchmarks/BenchmarkDotNet.Artifacts/`.

## Step 5: Compare Against Published Baseline

Use the regression check script to compare your results against the published baseline:

```bash
python3 scripts/check-perf-regression.py \
  --baseline performance-baseline.json \
  --current benchmarks/Honua.Benchmarks/BenchmarkDotNet.Artifacts/results/*.json
```

The script checks:
- **Latency regression**: flags if mean latency exceeds the baseline by the configured threshold (default 10%)
- **Memory regression**: flags if allocation exceeds the baseline threshold
- **Environment mismatch**: warns if runtime or OS differs from the baseline environment

Exit codes: `0` = pass, `1` = warning (review recommended), `2` = critical regression.

See `scripts/check-perf-regression.py --help` for threshold configuration.

## Step 6: Review Results

BenchmarkDotNet produces results in the `BenchmarkDotNet.Artifacts/` directory:

| Format | File | Use case |
| --- | --- | --- |
| Console | stdout | Quick review |
| JSON | `results/*.json` | Regression comparison, CI integration |
| HTML | `results/*.html` | Visual review and sharing |
| CSV | `results/*.csv` | Spreadsheet analysis |
| Markdown | `results/*.md` | Documentation embedding |

## Interpreting Results

Key metrics to compare:

| Metric | What it measures | What to look for |
| --- | --- | --- |
| Mean | Average execution time | Should be within 20% of published baseline on similar hardware |
| P95 / P99 | Tail latency | Sensitive to background load and thermal throttling |
| Allocated | Per-operation memory | Should be stable across runs; increases suggest a regression |
| Gen0 / Gen1 / Gen2 | GC pressure | Gen2 collections under normal load indicate a problem |
| StdDev | Result consistency | High variance suggests environmental noise |

If your results differ significantly from published numbers, check:
1. Hardware differences (CPU model, core count, memory)
2. Database state (index presence, table statistics, vacuum status)
3. Background processes (antivirus, other containers, thermal throttling)
4. Runtime differences (JIT vs AOT, .NET version)

## CI Reproduction

The GitHub Actions workflow `performance-benchmarks.yml` runs the full benchmark suite on every push to trunk and on a nightly schedule. To reproduce the CI environment exactly:

1. Use `ubuntu-latest` GitHub Actions runner (or equivalent: Ubuntu 24.04, 2-core)
2. PostgreSQL 16 + PostGIS 3.4 service container
3. Redis 7 service container
4. .NET 10 SDK
5. Release configuration with default BenchmarkDotNet settings

Workflow artifacts include JSON results, HTML reports, and regression analysis for each run.
