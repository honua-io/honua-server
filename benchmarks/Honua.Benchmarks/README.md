# Honua Server Performance Benchmarks

Comprehensive performance benchmarks for the Honua Server project using [BenchmarkDotNet](https://benchmarkdotnet.org/). These benchmarks measure critical operations to ensure performance targets are met and to identify optimization opportunities.

## Quick Start

### Running All Benchmarks
```bash
cd benchmarks/Honua.Benchmarks
dotnet run -c Release
```

QueryBenchmarks require a PostGIS connection via `ConnectionStrings__DefaultConnection` or `HONUA_BENCH_DB_URL`.

### Running Specific Benchmark Categories
```bash
# SQL query generation performance
dotnet run -c Release --filter *SqlGeneration*

# End-to-end query performance
dotnet run -c Release --filter *Query*
```

### Advanced Options
```bash
# Quick benchmarks for development
dotnet run -c Release --filter *SqlGeneration* --job short

# Export results to multiple formats
dotnet run -c Release --filter *Query* --exporters json,html,csv
```

## Benchmark Categories

### 1. SQL Generation Benchmarks (`SqlGenerationBenchmarks`)
**Purpose**: Tests SQL query building performance comparing different string construction approaches.

**Key Metrics**:
- Simple queries: < 1μs allocation overhead
- Complex queries: < 10μs
- Memory allocations: < 1KB per query

**Tests**:
- StringBuilder vs ObjectPool<StringBuilder> vs string concatenation
- Capacity optimization impact
- Dynamic WHERE clause building
- Parameter substitution performance
- ArrayPool<char> vs standard allocation

### 2. Query Benchmarks (`QueryBenchmarks`)
**Purpose**: Tests feature store query performance using a seeded PostGIS dataset.

**Key Metrics**:
- p50 < 30ms (basic queries)
- p95 < 100ms (basic queries)
- p99 < 200ms (basic queries)
**Tests**:
- Simple WHERE queries
- Spatial bbox queries
- Combined WHERE + spatial queries
- Paginated queries
- Large result sets

## Load Testing (Manual)

NBomber scenarios live in `tests/Honua.TestKit/Performance/LoadTestScenarios.cs` and are run manually for now.

## Performance Targets

| Operation Category | Target Latency | Memory Usage | Throughput |
|-------------------|----------------|--------------|------------|
| SQL Generation | < 10μs | < 1KB | N/A |
| Query Benchmarks | p95 < 100ms | < 100KB | N/A |

## Understanding Results

### Key Metrics to Watch
- **Mean**: Average execution time
- **StdDev**: Standard deviation (consistency indicator)
- **Median**: p50 latency
- **Gen0/Gen1/Gen2**: Garbage collection pressure
- **Allocated**: Memory allocated per operation

### Performance Regression Indicators
- Mean latency increase > 20%
- Memory allocation increase > 50%
- GC pressure increase (higher Gen1/Gen2 collections)
- Significant standard deviation increases

### Sample Output
```
|                    Method |      Mean |    StdDev |    Median |  Gen 0 | Allocated |
|-------------------------- |----------:|----------:|----------:|-------:|----------:|
| SimpleSelectWithObjectPool |  157.2 ns |   3.21 ns |  156.8 ns | 0.0153 |      96 B |
| SimpleSelectWithStringBuilder | 234.1 ns | 5.43 ns | 232.7 ns | 0.0229 | 144 B |
```

## Integration with CI/CD

### Performance Gates
Benchmarks can be integrated into CI/CD pipelines to catch performance regressions:

```bash
# Run performance-critical benchmarks
dotnet run -c Release --filter "SqlGeneration*" --exporters json

# Check results against baseline
./scripts/check-perf-regression.py --baseline performance-baseline.json --current BenchmarkDotNet.Artifacts/results.json
```

### Continuous Performance Monitoring
- Run nightly performance benchmarks
- Compare results against baseline
- Alert on significant regressions
- Track performance trends over time

## Development Workflow

### Before Making Performance Changes
1. Run relevant benchmarks to establish baseline
2. Make your changes
3. Re-run benchmarks to measure impact
4. Validate performance targets are met

### Adding New Benchmarks
1. Follow existing naming conventions
2. Include comprehensive XML documentation
3. Set appropriate performance targets
4. Add both synthetic and realistic test scenarios
5. Test memory usage and concurrent scenarios

### Best Practices
- Always run benchmarks in Release mode
- Use consistent hardware for comparisons
- Run multiple iterations to account for variance
- Test with realistic data sizes and scenarios
- Monitor both latency and memory usage

## Hardware Requirements

### Minimum Requirements
- 4 CPU cores
- 8GB RAM
- PostgreSQL 14+ (for QueryBenchmarks)
- 50GB free disk space

### Recommended Setup
- 8+ CPU cores
- 16+ GB RAM
- SSD storage
- Dedicated PostgreSQL instance
- Network isolation from other services

## Troubleshooting

### Common Issues

#### `OutOfMemoryException` during large file benchmarks
- Reduce file sizes in test data generation
- Increase available memory
- Run benchmarks individually instead of all at once

#### PostgreSQL connection issues
- Ensure PostgreSQL is running
- Check `ConnectionStrings__DefaultConnection` or `HONUA_BENCH_DB_URL`
- Verify database permissions

#### Inconsistent results
- Ensure system is idle during benchmarks
- Disable antivirus real-time scanning
- Run benchmarks multiple times
- Check for thermal throttling

### Performance Analysis Tools

#### Windows
```bash
# ETW profiling
dotnet run -c Release --filter *SqlGeneration* --profiler ETW

# Memory analysis
dotnet run -c Release --filter *Query* --diagnosers memory
```

#### Linux
```bash
# Perf profiling
dotnet run -c Release --filter *Query* --profiler Perf

# Memory analysis
dotnet run -c Release --filter *Query* --diagnosers memory,threading
```

## Contributing

### Adding New Benchmarks
1. Create new benchmark class following naming convention: `{Category}Benchmarks`
2. Include baseline benchmark with `[Benchmark(Baseline = true)]`
3. Add realistic test scenarios
4. Document performance targets
5. Update this README with new benchmark information
6. Add benchmark class to `BenchmarkRunner.cs`

### Performance Target Updates
Performance targets should be updated based on:
- Business requirements
- Hardware improvements
- Framework updates
- Code optimizations

Update targets in:
1. Benchmark class documentation
2. This README
3. CI/CD performance gates
4. Architecture decision records

## References

- [BenchmarkDotNet Documentation](https://benchmarkdotnet.org/)
- [.NET Performance Guidelines](https://docs.microsoft.com/en-us/dotnet/standard/performance/)
- [Honua Server Architecture Documentation](../../docs/contributor/ARCHITECTURE.md)
- [Performance Testing ADR](../../docs/contributor/adr/0011-testing-strategy.md)
