# Load/Soak Testing

This document describes how to run sustained load/soak tests for Honua Server and how to review the resulting metrics.

## Goals

- Validate stability under sustained load for FeatureServer, OGC Features, OData, and Tiles.
- Track tail latency, error rate, memory usage, and database health.
- Capture CPU and memory trends during soak runs.

## Scenarios Covered

The load test suite exercises:

- `feature_query_load` (FeatureServer query)
- `spatial_query_load` (FeatureServer spatial query)
- `ogc_query_load` (OGC API Features)
- `cql_filter_load` (OGC CQL2 filtering)
- `odata_query_load` (OData query workloads)
- `tiles_load` (OGC API Tiles MVT rendering)
- `connection_pool_stress` (mixed endpoints)
- `memory_stress` (large result sets)

Scenario definitions live in `tests/Honua.TestKit/Performance/LoadTestScenarios.cs`.

## Profiles

Profiles are defined in `tests/Honua.TestKit/Performance/LoadTestProfile.cs`:

- `quick`: short local smoke run (minutes).
- `nightly`: longer CI-friendly run (10+ minutes).
- `soak`: sustained run (60+ minutes).

Adjust ramp/steady durations via CLI flags or environment variables if needed.

Representative user counts per profile:

| Scenario | quick | nightly | soak |
| --- | --- | --- | --- |
| FeatureServer query | 10 | 30 | 50 |
| OGC Features | 8 | 20 | 40 |
| OData | 6 | 15 | 25 |
| Tiles | 6 | 15 | 25 |

## Running Locally

1) Start Honua Server with metrics enabled and dev auth bypass:

```bash
HONUA_DEV_AUTH=true dotnet run --project src/Honua.Server
```

2) Run the load/soak script:

```bash
./scripts/run-load-soak-tests.sh --base-url http://localhost:5000 --profile quick
```

To sample CPU/memory from a local process:

```bash
HONUA_PROCESS_PID=<pid> ./scripts/run-load-soak-tests.sh --profile soak
```

To sample CPU/memory from a Docker container:

```bash
HONUA_DOCKER_CONTAINER=honua-server ./scripts/run-load-soak-tests.sh --profile soak
```

## Running in CI (optional)

Invoke the script from a workflow job after the server is running. Use `--profile nightly` or override durations with `--duration`.

## Metrics Captured

- **NBomber reports** (p95/p99 latency, error rate): `load-test-reports/run_*/nbomber`
- **API metrics** (memory/DB/cache/health): `load-test-reports/run_*/metrics`
- **CPU/memory samples** (docker or process): `load-test-reports/run_*/metrics/resources.csv`

Private metrics endpoints (`/api/v1/metrics/database`, `/api/v1/metrics/memory`, etc.) require `HONUA_DEV_AUTH=true`, an OIDC bearer token, or an API key (automation only).

## Failure Thresholds (initial)

These are starting points; adjust per environment:

- Error rate: <= 1% for read scenarios, <= 0.1% for metadata endpoints.
- p95 latency: <= 500ms for FeatureServer/OGC/OData; <= 750ms for Tiles.
- p99 latency: <= 1500ms across scenarios.
- Memory: working set growth <= 20% over 30 minutes; memory pressure <= 85%.
- Database: cache hit rate >= 0.90; average DB operation time <= 200ms.

## Tuning Log

Record tuning findings here as they emerge:

- None yet.
