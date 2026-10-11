test(geoservices): parallelize FeatureServer query-parameter tests
## Issue Link
Refs honua-io/honua-server#3204 (CI efficiency plan, Phase 6.1: this migrates one more class off the serial `Database` collection; other classes remain)

## Summary
`FeatureServerQueryParameterTests` carried `[Collection("Database")]`, the GeoServices assembly's `DisableParallelization` collection. As a result, its 56 test methods (60 cases) held the serial lane of the **FeatureServer Endpoints Query Services and Replication** shard. The class only reads. Each case sends a GET or POST `query` request to its own per-class `WebAppFixture` (own PostgreSQL schema, schema-routed per request) and asserts on the response. It mutates neither the catalog nor any process-global `honua.*` table and sets no environment variables, so the serial collection gives it nothing.

This PR moves the class onto a sibling `Database.GeoServicesFeatureServerQuery` collection, following the template already used in `tests/dotnet/Honua.Protocols.GeoServices.Tests/DatabaseShardCollections.cs` (`GeoServicesRaster` / `MapServer` / `Catalog`). The class didn't need any isolation beyond what the template provides.

## Changes Made
- `DatabaseShardCollections.cs`: add `DatabaseGeoServicesFeatureServerQueryCollection` (`[CollectionDefinition("Database.GeoServicesFeatureServerQuery")]`, parallelizable, no collection fixture). Its remarks record why the class is safe to parallelize.
- `FeatureServerQueryParameterTests.cs`: `[Collection("Database")]` -> `[Collection("Database.GeoServicesFeatureServerQuery")]`. No other line changes. No test is added, removed, renamed, skipped or weakened.

## Testing
I ran the full shard locally against a CI-equivalent setup:
- Filter: the shard's `ci-shards.json` filter wrapped in `((...)&Tier!=Slow)&Tier!=Fast`, as `run-server-test-shard.sh` does.
- Database: one external `postgis/postgis:16-3.4` (same digest as CI) via `HONUA_TEST_DB_URL`, with `GSS Encryption Mode=Disable` because Windows Npgsql otherwise stalls on GSS negotiation.
- Build: Release, `--no-build`.

| run | head | tests | passed | failed | wall | `FeatureServerQueryParameterTests` |
|---|---|---|---|---|---|---|
| before (trunk attribute) | 3675a5a6a + attribute reverted | 647 | 644 | 3 (env) | 6002 s | 60/60 passed; serial lane, ~51 s footprint (21 s fixture start + 10 s tests + 20 s to next class) |
| after #1 | 3675a5a6a + this change | 647 | 645 | 2 (env) | 8824 s (*) | 60/60 passed; parallel phase |
| after #2 | 3675a5a6a + this change | 647 | 645 | 2 (env) | 4631 s | 60/60 passed; parallel phase, no serial-lane footprint |
| after rebase #1 | 192b3ca84 (on 6d6d61db1) | 576 of 647 when stopped | 572 | 4 (env) | stopped at ~4 h | 60/60 passed |
| final head, focused | d5a6ff3b4 (on 7b480ae29) | 60 | 60 | 0 | 544 s | 60/60 passed; shard filter ∩ class |

(*) Run #1's wall time is not comparable: I suspended the testhost for ~15 min with a diagnostic `dotnet-dump`.

I stopped the rebase #1 full-shard run after the host became too starved to finish: tests took 7–13 minutes each, and the PostGIS container later died with Docker Desktop. By then the migrated class (which runs first) had passed 60/60. Its 4 failures are the same environmental signatures in unchanged classes (3× the `StopAsync` teardown cancellation, 1× a 13-minute H3 request timeout). After trunk moved again (13 commits, none touching TestKit or `ci-shards.json`), I re-verified the final head with the shard's CI filter intersected with the class.

- **No `40P01` / deadlocks.** The Postgres log shows zero `deadlock`/`40P01` entries across all runs.
- **Every local failure is environmental and none is in the migrated class.** The same signatures appear on the trunk-attribute baseline:
  - `TaskCanceledException` from `FileBackedLicenseService.StopAsync` in `WebAppFixture.DisposeAsync`: the host shutdown timeout fires on a CPU-starved host. It hit whichever class was tearing down at the time: `FeatureServerH3EntitlementTests` (after #1), `FeatureServerApplyEditsAmbiguousWriteTests` and `FeatureServerAccessFilteringTests` (after #2), and `FeatureServerAccessFilteringTests` and `FeatureServerApplyEditsIdempotentReplayTests` (before).
  - `FeatureServerApplyEditsTransactionalCancellationTests.ApplyEdits_RetryAfterTransactionalCancellation_DoesNotConflict` hit its own internal timeout (after #1 and before).
- **Wall time.** The local wall-clock delta (6002 s -> 4631 s) is dominated by host contention: the Windows host was shared with several other lanes and Docker certification stacks, so I don't claim it as the effect of this change.
  - The honest expected effect comes from CI's trunk TRX (run 38124910778, shard `duration_seconds` 1306 of a 1560 s budget, `capacity_status: low_headroom`). There the class occupies the serial lane for about 15 s (5 s fixture start + 2 s tests + 8 s to the next class), so this PR should save roughly 10–15 s per shard run (~1%).
  - It is one step of the Phase 6.1 migration, not a headroom fix by itself.
  - Trunk shard test-step durations over the last 8 runs ranged from 1090 to 1460 s.

- [ ] Unit tests added/updated
- [x] Integration tests added/updated (collection wiring only)
- [ ] Architecture tests pass
- [ ] Manual testing performed

## Gate Impact
- [x] PR gates (build, test, governance): server-test shard scheduling only
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)
- [ ] None — no gate impact

## Docs or Contract Impact
- [x] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None

## Follow-ups (not in this PR)
- P3: `FileBackedLicenseService.StopAsync` (`src/Honua.Hosting/Features/Licensing/FileBackedLicenseService.cs:72`) lets `WaitAsync(cancellationToken)` throw `TaskCanceledException` when the host shutdown token fires. Under load, that turns a slow teardown into a test failure in whatever class disposes the fixture. It reproduces on trunk.
