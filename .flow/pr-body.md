test(geoservices): parallelize FeatureServer query-parameter tests
## Issue Link
Refs honua-io/honua-server#3204 (CI efficiency plan, Phase 6.1: this migrates one more class off the serial `Database` collection; other classes remain)

## Summary
`FeatureServerQueryParameterTests` carried `[Collection("Database")]`, the GeoServices assembly's `DisableParallelization` collection. Its 56 test methods (54 `[IntegrationTest]`, 1 `[Fact]`, 1 `[Theory]` with 5 cases; 60 results) therefore held the serial lane of the **FeatureServer Query Services and Replication** shard.

The class only reads. Each case sends a GET or POST `query` request to the seed of its own per-class `WebAppFixture` (own PostgreSQL schema, schema-routed per request) and asserts on the response. It mutates neither the catalog nor any process-global `honua.*` table, and it sets no environment variables, so the serial collection gives it nothing.

This PR moves the class onto a sibling `Database.GeoServicesFeatureServerQuery` collection. It follows the template already in `tests/dotnet/Honua.Protocols.GeoServices.Tests/DatabaseShardCollections.cs` (`GeoServicesRaster` / `MapServer` / `Catalog`). The class needs no isolation beyond what that template provides.

## Changes Made
- `DatabaseShardCollections.cs`: add `DatabaseGeoServicesFeatureServerQueryCollection` (`[CollectionDefinition("Database.GeoServicesFeatureServerQuery")]`, parallelizable, no collection fixture). Its remarks record why the class is safe to parallelize.
- `FeatureServerQueryParameterTests.cs`: `[Collection("Database")]` -> `[Collection("Database.GeoServicesFeatureServerQuery")]`. No other line changes. No test is added, removed, renamed, skipped or weakened.

## Testing
I ran the affected shard locally twice against a CI-equivalent setup:
- Filter: the `FeatureServer Query Services and Replication` filter from `.github/ci-shards.json`, wrapped in `((...)&Tier!=Slow)&Tier!=Fast` as `run-server-test-shard.sh` does.
- Database: one external `postgis/postgis:16-3.4` at the CI digest (`sha256:44126d87…`), passed in through `HONUA_TEST_DB_URL` with `GSS Encryption Mode=Disable`, because Windows Npgsql otherwise stalls on GSS negotiation.
- Build: Release, `--no-build`.

| run | source | tests | passed | failed | wall | `FeatureServerQueryParameterTests` |
|---|---|---|---|---|---|---|
| before: CI trunk | run 39351090881 @ 4709145cb (`Database` attribute) | 375 | 375 | 0 | 1186 s (`duration_seconds`; 37 min budget, headroom 0.53) | 60/60. In the serial lane at 640.2–643.5 s; the previous class ends at 630.5 s and the next starts at 653.3 s, so it occupies about 23 s of the serial lane |
| after #1, local | this change on trunk 393987ccf | 375 | 374 | 1 (env) | 4352 s | 60/60, parallel phase (80.5–96.1 s) |
| after #2, local | same head | 375 | 375 | 0 (+1 class-cleanup error, env) | 7132 s | 60/60, parallel phase (146.8–162.1 s) |
| final head, focused | e57db029c (on trunk 242054c73), shard filter ∩ class | 60 | 60 | 0 | 181 s | 60/60 |

The two local runs used identical binaries, yet their wall times differ by 2780 s. That spread is host contention: a shared Windows host with several Docker certification stacks and other lanes, and 2.8–5 GB free RAM. Local wall time therefore can't resolve this change, so I didn't run a local baseline; the before figure is CI's trunk TRX. The expected CI effect, derived from that TRX:
- **Serial lane:** xUnit runs every parallel collection before it starts the `DisableParallelization` collection. Both local TRXs confirm this: `ReplicationDurabilityTests`, the first serial class, starts within 1 s of this class finishing. Removing the class from the serial lane saves its fixture start, test and teardown time, roughly 13 s of the 23 s window.
- **Parallel phase:** the class now runs in the parallel phase, which on CI currently ends at about 9 s (`FeatureServerTopFeaturesTemporalTests`). That phase stretches to about 15 s, the class's own fixture start plus 3 s of tests.
- **Net:** a saving of about 5–15 s per shard run, under 1% of 1186 s. This is one step of the Phase 6.1 migration, not a headroom fix by itself. CI's own shard timing on this PR is the authoritative after figure.

**Deadlocks:** none. The Postgres container log shows zero `deadlock` or `40P01` entries across both runs.

**Local failures:** every one is environmental and outside the migrated class. Both are the known teardown signature: a `TaskCanceledException` from `FileBackedLicenseService.StopAsync`, raised through `WebAppFixture.DisposeAsync` when the host shutdown timeout fires on a CPU-starved host.
- after #1: `FeatureServerAttachmentCapabilityAdvertisementTests.FeatureServerMetadata_WithoutAttachmentStore_DoesNotAdvertiseAttachmentOperations` (teardown inside the test).
- after #2: class-cleanup failure in `FeatureServerQueryDateBinsTests`, a class still on the serial `Database` collection, about 87 minutes after the migrated class had finished.

After both runs, trunk gained 6 more commits. None touches this shard's classes, TestKit or `ci-shards.json`; the only GeoServices test change is `ImageServerWmtsReprojectionTests`, a different shard. I merged them, rebuilt the test project in Release, and re-ran the class under the shard filter on the final head: 60/60 passed (last table row).

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
- P3: `FileBackedLicenseService.StopAsync` (`src/Honua.Hosting/Features/Licensing/FileBackedLicenseService.cs:72`) lets `WaitAsync(cancellationToken)` throw `TaskCanceledException` when the host shutdown token fires. Under load, that turns a slow teardown into a failure in whichever class disposes its fixture. It reproduces on trunk-attribute classes.
- P3: the xUnit ordering above caps what any single read-only class can save. The larger Phase 6.1 wins come from moving the long serial classes (`FeatureServerReplicationTests` 297 s, `FeatureServerSpatialReferenceTests` 130 s, `FeatureServerExceptionMappingTests` 84 s), and only where their isolation allows it.
