# Performance Testing Guide

This document describes the performance testing infrastructure for Honua Server, including benchmarks, baselines, and CI integration.

## Overview

The performance testing suite consists of:

- **BenchmarkDotNet** - Microbenchmarks for latency and allocation measurements
- **NBomber** - Load/soak testing scenarios run via `scripts/run-load-soak-tests.sh`
- **CI Integration** - Automated regression checks for BenchmarkDotNet results

## Quick Start

### Prerequisites

- .NET 10.0+ SDK
- Docker (for PostgreSQL test database)
- Optional: `jq` for detailed result parsing

### Running All Benchmarks

```bash
# Run complete benchmark suite
./scripts/run-performance-tests.sh

# Run with baseline update
./scripts/run-performance-tests.sh --baseline

# Run specific benchmark category
./scripts/run-performance-tests.sh --filter Query

# Quick performance check (shorter, less accurate)
./scripts/run-performance-tests.sh --quick
```

### Running Individual Benchmarks

```bash
# Run only BenchmarkDotNet tests
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --filter *Query*

# Run SQL generation microbenchmarks
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --filter *SqlGeneration*
```

### Running Load/Soak Tests

```bash
# Quick local load test (minutes)
./scripts/run-load-soak-tests.sh --base-url http://localhost:5000 --profile quick

# Nightly-length profile (10+ minutes)
./scripts/run-load-soak-tests.sh --profile nightly

# Override steady-state duration
./scripts/run-load-soak-tests.sh --profile soak --duration 90m
```

Detailed guidance and thresholds live in `load-soak-testing.md`.

## Performance Targets

### Latency Targets (BenchmarkDotNet)

| Endpoint | Dataset | p50 | p95 | p99 |
|----------|---------|-----|-----|-----|
| **Query (100 features)** | Standard | < 50ms | < 150ms | < 300ms |
| **Query (1000 features)** | Large | < 150ms | < 400ms | < 800ms |
| **Spatial Query** | Bbox intersection | < 50ms | < 150ms | < 300ms |

### Load/Soak Targets (Manual)

NBomber scenarios and long-running memory soak checks are run via the load/soak script and reported separately from BenchmarkDotNet baselines.

## Benchmark Classes

### SqlGenerationBenchmarks

Measures SQL string construction overhead for query building:

```csharp
[Benchmark] SimpleSelectWithStringBuilder()
[Benchmark] SimpleSelectWithObjectPool()
[Benchmark] ComplexSpatialQueryWithStringBuilder()
```

### QueryBenchmarks

Measures feature store query latency with a seeded PostGIS dataset:

```csharp
[Benchmark] SimpleWhereQuery()           // Basic attribute filtering
[Benchmark] SpatialBboxQuery()           // Spatial intersection
[Benchmark] CombinedWhereAndSpatialQuery() // Combined filtering
[Benchmark] PaginatedQuery()             // Paging performance
[Benchmark] LargeResultSet()             // 1000+ features
```

## Test Data Setup

Query benchmarks seed a PostGIS table with synthetic point features and JSONB attributes
in an isolated schema before each run.

## Results and Analysis

### Output Formats

Benchmarks generate multiple output formats:

- **HTML Reports** - Interactive dashboard with charts
- **CSV Data** - Machine-readable results for analysis
- **JSON Results** - Programmatic access to metrics

### Result Structure

```
benchmark-results/
├── run_20241221_143022/
│   ├── QueryBenchmarks-report.html
│   ├── results.json
│   ├── results.csv
```

### Key Metrics

Each benchmark reports:

- **Mean/Median Latency** - Central tendency
- **95th/99th Percentile** - Tail latency
- **Memory Allocations** - GC pressure

## CI Integration

BenchmarkDotNet runs in CI on a schedule and on demand; load tests are manual.

```yaml
# .github/workflows/performance.yml
performance:
  runs-on: ubuntu-latest
  services:
    postgres:
      image: postgis/postgis:16-3.4
  steps:
    - name: Run Performance Benchmarks
      run: ./scripts/run-performance-tests.sh --quick

    - name: Baseline Comparison
      run: ./scripts/check-perf-regression.py --baseline performance-baseline.json --current performance-reports/run_*/results.json --threshold 0.10
```

### Regression Detection

CI flags regressions > 10% from baseline for BenchmarkDotNet metrics:

- **Latency Regression** - p95 latency increase
- **Allocation Regression** - increased allocations per operation

## Local Development

### Quick Performance Check

For rapid feedback during development:

```bash
# Fast check - abbreviated benchmarks
./scripts/run-performance-tests.sh --quick --filter Query

# Single benchmark method
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --job short --filter SimpleWhereQuery
```

### Profiling Integration

For detailed performance analysis:

```bash
# Run with ETW profiler (Windows)
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --profiler ETW

# Run with memory profiler
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --memory-randomization --profiler MEMORY
```

## Troubleshooting

### Common Issues

**Database Connection Failures**
```bash
# Check PostgreSQL container
docker ps | grep postgis
docker logs honua-perf-test-db

# Reset test database
docker stop honua-perf-test-db && docker rm honua-perf-test-db
```

**High Variance in Results**
```bash
# Longer warmup for stable results
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --warmupCount 10 --minIterationCount 10
```

**GC Variance**
- Re-run benchmarks to confirm allocation changes
- Minimize background processes for stable results

### Performance Analysis

**Identifying Bottlenecks**

1. **High Latency**: Check database query plans with `EXPLAIN ANALYZE`
2. **Low Throughput**: Profile with dotnet-trace for CPU hotspots
3. **Memory Issues**: Use dotnet-dump for heap analysis

**Database Performance**

```sql
-- Check query performance
SELECT query, mean_exec_time, calls
FROM pg_stat_statements
ORDER BY mean_exec_time DESC;

-- Check index usage
SELECT schemaname, tablename, indexname, idx_tup_read, idx_tup_fetch
FROM pg_stat_user_indexes;
```

## Baseline Management

### Creating Baselines

```bash
# Update baseline after validated performance improvements
./scripts/run-performance-tests.sh --baseline
git add performance-baseline.json
git commit -m "perf: update performance baseline after optimization"
```

### Baseline File Format

```json
{
  "Benchmarks": [
    {
      "Method": "SimpleWhereQuery",
      "Statistics": {
        "Mean": 45000000.0,
        "StandardError": 2000000.0,
        "Percentile95": 65000000.0,
        "Percentile99": 85000000.0
      }
    }
  ]
}
```

## Best Practices

### Writing Benchmarks

1. **Realistic Test Data** - Use representative datasets
2. **Proper Warmup** - Allow JIT compilation and connection pooling
3. **Resource Management** - Dispose resources properly
4. **Stable Environment** - Minimize background processes
5. **Meaningful Metrics** - Focus on user-facing performance

### Interpreting Results

1. **Statistical Significance** - Look at confidence intervals
2. **Percentile Analysis** - Don't just focus on averages
3. **Memory Allocations** - Consider GC impact on tail latency
4. **Regression Trends** - Track performance over time

### CI Performance

1. **Stable Infrastructure** - Use consistent CI runners
2. **Isolated Environment** - Minimize noise from other jobs
3. **Threshold Tuning** - Balance sensitivity vs false positives
4. **Baseline Updates** - Regular updates after validated changes

## Resources

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [NBomber Documentation](https://nbomber.com/)
- [.NET Performance Guidelines](https://docs.microsoft.com/en-us/dotnet/core/deploying/ready-to-run)
- [PostGIS Performance Tuning](https://postgis.net/workshops/postgis-intro/performance.html)
