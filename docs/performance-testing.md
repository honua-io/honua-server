# Performance Testing Guide

This document describes the performance testing infrastructure for Honua Server, including benchmarks, baselines, and CI integration.

## Overview

The performance testing suite consists of:

- **BenchmarkDotNet** - Microbenchmarks for precise latency measurements
- **NBomber** - Load testing framework for throughput and scalability testing
- **Memory Soak Tests** - Memory leak detection and sustained load testing
- **CI Integration** - Automated performance regression detection

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

# Run specific benchmark class
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --filter *LoadTestBenchmarks*
```

## Performance Targets

### Latency Targets (BenchmarkDotNet)

| Endpoint | Dataset | p50 | p95 | p99 |
|----------|---------|-----|-----|-----|
| **Query (100 features)** | Standard | < 50ms | < 150ms | < 300ms |
| **Query (1000 features)** | Large | < 150ms | < 400ms | < 800ms |
| **Spatial Query** | Bbox intersection | < 50ms | < 150ms | < 300ms |
| **ApplyEdits (10 features)** | Mixed CRUD | < 100ms | < 300ms | < 500ms |
| **Layer Metadata** | Service info | < 5ms | < 20ms | < 50ms |

### Throughput Targets (NBomber)

| Test Scenario | Target RPS | Max Latency (p95) |
|---------------|------------|-------------------|
| **Simple Queries** | > 1000 | < 150ms |
| **Spatial Queries** | > 500 | < 300ms |
| **Mixed Workload** | > 800 | < 200ms |
| **Sustained Load** | > 500 (5min) | Stable |

### Memory Targets

| Test | Duration | Target Memory Delta |
|------|----------|-------------------|
| **10k Queries** | ~5 minutes | < 50MB |
| **Mixed Operations** | ~3 minutes | < 20MB |
| **Connection Pool** | ~2 minutes | < 5MB |

## Benchmark Classes

### QueryBenchmarks

Measures endpoint latency for various query patterns:

```csharp
[Benchmark] SimpleWhereQuery()           // Basic attribute filtering
[Benchmark] SpatialBboxQuery()           // Spatial intersection
[Benchmark] CombinedWhereAndSpatial()    // Combined filtering
[Benchmark] PaginatedQuery()             // Paging performance
[Benchmark] LargeResultSet()             // 1000+ features
```

### LoadTestBenchmarks

Throughput and scalability testing using NBomber:

```csharp
[Benchmark] SimpleQueryThroughput()      // 1000 rps target
[Benchmark] SpatialQueryThroughput()     // 500 rps target
[Benchmark] MixedWorkloadThroughput()    // Mixed scenario
[Benchmark] SustainedLoadTest()          // 5-minute endurance
```

### MemorySoakBenchmarks

Memory leak detection and resource management:

```csharp
[Benchmark] Query_Soak_10k()             // 10,000 queries
[Benchmark] Mixed_Soak_5k()              // Mixed operations
[Benchmark] ConnectionPool_Soak_2k()     // Connection handling
```

## Test Data Setup

The benchmarks use realistic geospatial datasets:

- **Parcels Layer**: 10,000 features, mixed geometry types
- **Points of Interest**: 5,000 point features with attributes
- **Administrative Boundaries**: Complex polygons

Test data is automatically created during benchmark setup.

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
│   ├── LoadTestBenchmarks-report.html
│   ├── results.json
│   ├── results.csv
│   └── load-test-results/
│       ├── session-20241221_143022.html
│       └── session-20241221_143022.csv
```

### Key Metrics

Each benchmark reports:

- **Mean/Median Latency** - Central tendency
- **95th/99th Percentile** - Tail latency
- **Memory Allocations** - GC pressure
- **Throughput** - Requests per second
- **Error Rate** - Failure percentage

## CI Integration

Performance tests run in CI on every pull request:

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

    - name: Performance Regression Check
      run: python scripts/check-perf-regression.py --threshold 0.10
```

### Regression Detection

CI fails if performance regresses > 10% from baseline:

- **Latency Regression** - p95 latency increase
- **Throughput Regression** - RPS decrease
- **Memory Regression** - Memory delta increase

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

**Memory Test False Positives**
- Memory soak tests may show false positives due to GC timing
- Re-run tests multiple times to confirm memory leaks
- Check for external processes affecting memory measurements

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