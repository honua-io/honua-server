# Benchmark Methodology

This document codifies how Honua benchmarks are run, measured, and reported. It is the canonical reference for understanding what the [published results](BENCHMARK_RESULTS.md) mean and how they were produced.

## Tooling

| Tool | Purpose | Configuration |
| --- | --- | --- |
| [BenchmarkDotNet](https://benchmarkdotnet.org/) | Micro-benchmarks (query latency, SQL generation, memory) | JSON and HTML exporters enabled; default job unless overridden |
| [NBomber](https://nbomber.com/) | Load and concurrency testing | Scenarios in `tests/Honua.TestKit/Performance/LoadTestScenarios.cs` |
| `scripts/check-perf-regression.py` | Automated regression detection | Compares current vs baseline JSON; configurable thresholds |
| `scripts/capture-bench-environment.sh` | Environment disclosure capture | Outputs markdown block with runtime, OS, CPU, database versions |

## Statistical Methodology

BenchmarkDotNet applies the following by default (Honua does not override these):

- **Warmup**: automatic warmup iterations until the benchmark stabilizes
- **Measurement**: multiple measurement iterations (typically 15-100 depending on variance)
- **Outlier removal**: modified Z-score outlier detection (BenchmarkDotNet `OutlierMode.DontRemove` is NOT used — outliers are removed by default)
- **Percentile reporting**: P50 (median), P95, P99 computed from measurement iterations
- **Statistical columns**: Mean, StdDev, StdErr, Median, Min, Max

For load testing, NBomber reports requests/second, latency percentiles, and error rates over the configured duration.

### Regression Thresholds

From the existing CI configuration and `docs/contributor/benchmarks.md`:

| Metric | Warning threshold | Critical threshold |
| --- | --- | --- |
| Mean latency | > 10% increase | > 20% increase |
| Memory allocation | > 25% increase | > 50% increase |
| GC pressure | Gen1/Gen2 increase | New Gen2 collections |

These thresholds are enforced by `scripts/check-perf-regression.py` in the CI pipeline.

## Environment Isolation

### CI Runner

Published baseline results are captured on GitHub Actions `ubuntu-latest` runners:

- **OS**: Ubuntu 24.04 LTS
- **CPU**: Intel Xeon Platinum 8370C 2.80 GHz (2C/4T, shared)
- **Memory**: 7 GB available
- **.NET**: SDK version pinned in `global.json`

### Service Containers

| Service | Image | Purpose |
| --- | --- | --- |
| PostgreSQL | `postgis/postgis:16-3.4` | Spatial database for query benchmarks |
| Redis | `redis:7` | Cache layer for caching benchmarks |

Services are started as Docker containers with default configuration. No custom tuning is applied to match a "clean room" comparison baseline.

### Seed Data

Query benchmarks use the shared test seed data documented in `docs/contributor/test-seed-data.md`:

- **Row counts**: as defined by the seed data script
- **Geometry types**: points, lines, and polygons with varying complexity
- **Spatial index**: GiST indexes created on geometry columns
- **Attribute index**: B-tree indexes on commonly filtered columns

Dataset characteristics are intentionally modest to represent a baseline, not a peak-load scenario. Evaluators with larger datasets should run the [reproduction package](BENCHMARK_REPRODUCTION.md) with their own data.

## AOT vs JIT Disclosure

Every published benchmark run must disclose whether results were captured with:

- **JIT** (Just-In-Time compilation): the default `dotnet run -c Release` execution model
- **AOT** (Ahead-of-Time compilation): the `dotnet publish -c Release` Native AOT binary

The current published baseline (`performance-baseline.json`) was captured under **JIT** compilation on the CI runner. AOT results may differ due to startup characteristics and steady-state optimization differences.

When both JIT and AOT results are available, they are reported separately. Mixed JIT/AOT results are never combined in a single table without per-row disclosure.

## What Is Benchmarked

| Category | Benchmark class | What it measures |
| --- | --- | --- |
| Query latency | `QueryBenchmarks` | End-to-end query execution against PostGIS (WHERE, spatial, combined, paginated, large result sets) |
| SQL generation | `SqlGenerationBenchmarks` | SQL string construction performance (StringBuilder, ObjectPool, parameter substitution) |
| Database operations | `DatabasePerformanceBenchmarks` | Database-level operation performance |
| API endpoints | `ApiEndpointBenchmarks` | Full HTTP request-response cycle |
| Caching | `CachingPerformanceBenchmarks` | Redis cache hit/miss latency and operation throughput |
| Streaming memory | `StreamingMemoryBenchmarks` | Memory allocation during streaming responses |
| Memory soak | `MemorySoakBenchmarks` | Memory stability under sustained load |
| Load (throughput) | `LoadTestBenchmarks` | Requests/second under concurrent load |
| Load (concurrency) | `LoadTestConcurrencyBenchmarks` | Behavior under increasing concurrency levels |

## What Is NOT Benchmarked

The following are explicitly excluded from published results unless stated otherwise:

- **Network latency**: benchmarks run locally against service containers; real-world network round-trip is not measured
- **Client rendering**: map rendering, tile compositing, and UI performance are client-side concerns
- **Map tile rasterization**: unless a specific tile-generation benchmark is included and disclosed
- **Third-party product performance**: Honua does not benchmark competing products; evaluators should compare independently
- **Cold-start under AOT**: not yet included in the standard suite (tracked for future inclusion)

## References

- [Benchmark Results](BENCHMARK_RESULTS.md) — latest published numbers
- [Benchmark Reproduction](BENCHMARK_REPRODUCTION.md) — step-by-step reproduction guide
- [Contributor Benchmarks Guide](../contributor/benchmarks.md) — development-oriented benchmark documentation
- [Benchmark Publication Process](../contributor/BENCHMARK_PUBLICATION_PROCESS.md) — how to refresh the proof pack
- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [NBomber Documentation](https://nbomber.com/)
