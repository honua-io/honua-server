# OGC API Maps capacity split (#5006)

## Evidence and sizing

[Run 35164215061, attempt 1](https://github.com/honua-io/honua-server/actions/runs/35164215061/attempts/1),
[job 105021712115](https://github.com/honua-io/honua-server/actions/runs/35164215061/job/105021712115),
exhausted the 22-minute test budget with output only seven seconds old.
The old `OGC API Maps and Tiles` name was misleading: its filter now selects
Maps and Records. Tiles already belongs to other shards.

The successful second attempt's artifact `10475731774`
(`server-test-results-ogc-api-maps-tiles`) contains 129 passing cases and a
771-second timing record. TRX class spans (earliest case start to latest case
end) capture the host lifecycle gaps that summed case durations miss:

| Class | Cases | Span (seconds) |
|---|---:|---:|
| OgcMapsBasicTests | 20 | 233.1 |
| OgcMapsConformanceTests | 13 | 126.6 |
| OgcMapsParameterValidationTests | 22 | 136.1 |
| OgcMapsErrorHandlingTests | 23 | 185.4 |
| OgcMapsDuplicateStorageBindingEndpointTests | 3 | 28.5 |
| OgcMapsTemporalMosaicTests | 9 | 55.5 |
| OgcMapsRenderingHandlerTests | 2 | <0.1 |
| OgcRecordsTemporalAndTitleTests | 8 | 52.3 |
| OgcRecordsEndpointTests | 12 | 2.1 |
| OgcRecordsDepthTests | 16 | 3.4 |
| OgcRecordsAuthorizationDenialTests | 1 | 9.3 |

Records overlaps the first Maps classes. The Basic and Conformance classes
occupy the final contiguous 359.7 seconds; the preceding classes occupy about
407.0 seconds. These are sizing estimates from one successful retry, not p90
measurements. A namespace-only Maps/Records split would preserve the imbalance.

## Static partition and registration

- `OGC API Maps Basic and Conformance` owns the two named integration classes.
- `OGC API Maps Rendering and Records` owns the original namespace union minus
  those two classes, including future Maps and Records classes.
- Both retain the original project, CPU setting, 22-minute inner test budget,
  32-minute job budget and workflow tier filters. No tests or gates change.
- `shard_partitions` records the original union and project, so
  `check-server-test-shard-coverage.py` proves every original class has exactly
  one child owner and no child leaks outside the parent.
- Both register unique `shard_name`, `artifact_suffix`, `log_name`, filters,
  paths and estimated dispatch ranks in `.github/ci-shards.json`, following
  PR #4849. Maps source/test paths select both; Records paths select the second;
  shared validation paths select both. The router has descriptor and owner pins.
- `ci.yml` generates `matrix_include` from this catalog and uses those names
  for jobs, logs, TRX files and `server-test-results-*` artifacts. Its matrix
  fan-in includes both automatically. No separate literal workflow entry exists.
- Capability/family mappings use `capability-impact.py::shard_names_for_test`
  against catalog filters; `audit-shard-headroom.py` also reads the catalog.
  Neither requires a duplicate registration or a code change.

## Verification

Run `bash scripts/ci/validate-ci-router.sh` for routing, ownership, partition,
capability crosswalk and workflow contract checks. Dispatch `ci.yml` on the
branch with `full_ci=true`, then download both `server-test-results-ogc-api-maps-*`
artifacts and audit their timing records with `audit-shard-headroom.py`.
Compare the two TRX case inventories with the successful baseline to verify
that the split preserves all 129 integration cases.
