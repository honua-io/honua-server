# Benchmark Results

This page presents Honua Server's latest published benchmark results. All numbers link to the [reproduction steps](BENCHMARK_REPRODUCTION.md) and [methodology](BENCHMARK_METHODOLOGY.md) so evaluators can verify claims on their own infrastructure.

## Environment Disclosure

| Detail | Value |
| --- | --- |
| Runtime | .NET 10.0.2 (10.0.2, 10.0.225.61305) |
| OS | Linux Ubuntu 24.04.3 LTS (Noble Numbat) |
| CPU | Intel Xeon Platinum 8370C 2.80 GHz (2C/4T) |
| Database | PostgreSQL 16 + PostGIS 3.4 |
| Cache | Redis 7 |
| Compilation | JIT (see [methodology — AOT vs JIT](BENCHMARK_METHODOLOGY.md#aot-vs-jit-disclosure)) |
| Baseline date | 2026-02-07 |
| Git SHA | `21cf30e1` (see `GitSHA` in `performance-baseline.json`) |

Full environment capture procedure: [`scripts/capture-bench-environment.sh`](../../scripts/capture-bench-environment.sh).

## Query Latency

Measured with BenchmarkDotNet against a seeded PostGIS dataset with indexed spatial and attribute columns.

| Operation | Mean | P95 | P99 | Allocated |
| --- | --- | --- | --- | --- |
| Simple WHERE query | 3.43 ms | 3.46 ms | 3.46 ms | 208 KB |
| Spatial bbox query | 3.01 ms | 3.03 ms | 3.03 ms | 197 KB |
| Combined WHERE + spatial | 2.50 ms | 2.52 ms | 2.52 ms | 210 KB |
| Paginated query | 3.62 ms | 3.66 ms | 3.66 ms | 209 KB |
| Large result set | 8.73 ms | 8.81 ms | 8.81 ms | 1,910 KB |

Source: `QueryBenchmarks` class in `benchmarks/Honua.Benchmarks/`. Reproduction: [BENCHMARK_REPRODUCTION.md § Query benchmarks](BENCHMARK_REPRODUCTION.md#query-benchmarks).

## Memory Footprint

| Benchmark | Metric | Value |
| --- | --- | --- |
| Simple WHERE query | Gen0 collections | 1 |
| Spatial bbox query | Gen0 collections | 2 |
| Large result set | Gen0 / Gen1 collections | 4 / 3 |
| All query benchmarks | Gen2 collections | 0 |

Streaming and memory-soak profiling is available via `StreamingMemoryBenchmarks` and `MemorySoakBenchmarks`. See [methodology — statistical approach](BENCHMARK_METHODOLOGY.md#statistical-methodology) for measurement details.

## Throughput and Load Testing

Load testing uses NBomber scenarios (`LoadTestBenchmarks`, `LoadTestConcurrencyBenchmarks`) executed against a running Honua instance backed by PostGIS and Redis. Results depend on the target hardware and are published per-release in CI artifacts.

Throughput numbers from the nightly CI run are available in the GitHub Actions workflow artifacts for `performance-benchmarks.yml`.

## Caching Performance

`CachingPerformanceBenchmarks` measures cache hit/miss ratios and operation latency against Redis 7. Results are published alongside query benchmarks in the CI workflow artifacts.

## Operational Footprint

| Metric | Value | Notes |
| --- | --- | --- |
| AOT binary size | Published per release | See CI artifacts for `linux-x64` and `win-x64` |
| Container image size | Published per release | Based on `mcr.microsoft.com/dotnet/runtime-deps` |
| Idle memory | Published per release | Measured after startup with no active requests |
| Cold-start time | Published per release | Time from process start to first successful health check |

Honua ships as a single self-contained binary (AOT) or a standard .NET container image. There is no WAR deployment, no application server, and no runtime dependency beyond PostGIS.

## Migration Assessment Framing

This section contextualizes Honua's published numbers for teams evaluating a migration from legacy geospatial servers.

### Latency

Honua query P95 on indexed spatial data is in the low single-digit milliseconds (see table above). These numbers were captured on a 2-core CI runner — production hardware with more cores and memory will improve throughput and tail latency further.

Evaluators should run the [reproduction package](BENCHMARK_REPRODUCTION.md) on their target infrastructure and compare against their current platform's query performance under equivalent workloads.

### Resource Efficiency

- **Binary**: AOT-compiled, single-file deployment — no JVM, no application server runtime.
- **Memory**: Sub-kilobyte per-request allocations for standard queries; zero Gen2 GC pressure under normal load.
- **Container**: Minimal base image (`runtime-deps` only). No WAR packaging or servlet container layer.

Use the benchmark tables above plus the [reproduction package](BENCHMARK_REPRODUCTION.md) on your target infrastructure to size Small / Medium / Large deployments.

### Operational Simplicity

- Single binary or container image per release.
- PostGIS is the only required dependency. Redis is optional for multi-node caching.
- No application server configuration, no connection pool tuning beyond standard PostGIS best practices.
- Built-in OpenTelemetry, Prometheus metrics, and structured logging for observability.

### Cost

Cloud resource estimates should be derived from the benchmark tables and validated with the [reproduction package](BENCHMARK_REPRODUCTION.md) on the target environment. Honua's open-core model (Community edition is free under ELv2) eliminates per-core or per-user license costs for the server runtime.

## Disclaimer

Honua publishes its own benchmark results with full environment disclosure. This page does not include benchmarks of third-party products. Evaluators are encouraged to run the [reproduction package](BENCHMARK_REPRODUCTION.md) on their own infrastructure and compare against their current platform independently.

All numbers are point-in-time measurements captured under the disclosed environment. Production performance depends on hardware, dataset characteristics, query patterns, and deployment architecture. See the [methodology](BENCHMARK_METHODOLOGY.md) for statistical approach and known limitations.
