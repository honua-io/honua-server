# Honua Server BenchmarkDotNet Status

## Current Coverage

- **SqlGenerationBenchmarks**: SQL string building and allocation behavior
- **QueryBenchmarks**: Feature store query latency against a seeded PostGIS dataset

## Manual Load Testing

- **NBomber scenarios**: `tests/Honua.TestKit/Performance/LoadTestScenarios.cs`
- Load tests are run manually and are not part of CI

## Planned / Backlog

- CQL2 translation benchmarks
- Geometry conversion benchmarks
- Authentication benchmarks
- File upload security benchmarks
- Memory soak benchmarks

## Runner Behavior

`BenchmarkRunner.cs` runs SQL generation and query benchmarks by default. Use `--filter` to target specific benchmark categories.

## Notes

- Query benchmarks seed an isolated schema per run so results are repeatable.
- Update `performance-baseline.json` after validated performance changes.
