# tiles_load on the 2026.1 capacity soak (honua-server#4918)

Why `tiles_load` failed 22,853 of 51,375 requests with p95 1223.68 ms / p99 1982.46 ms against the
822 / 1023 ms frozen lock on candidate pin `548b7a5263da5a3f2381eb43f232687cdf92b0bf`
(capacity-soak run [34923808977](https://github.com/honua-io/honua-server/actions/runs/34923808977),
receipt `capacity/548b7a5263da5a3f2381eb43f232687cdf92b0bf-34923808977.json`).

## The failing status code is 413, not a transport or native-load failure

NBomber's status-code table for `tiles_load` lists only 289 `-101` transport errors, because
`LoadTestScenarios.CreateTilesScenario` calls `Response.Fail()` without a status code for anything
that is not 200/204. The status codes are in the run's server log
(`honua-server.log` in the run artifact); every tile response in it is either **413** or **204**:

| path shape | status | count in the retained (rotated) log tail |
|---|---:|---:|
| `/ogc/tiles/collections/{n}/tiles/WebMercatorQuad/{z}/{y}/{x}` | 413 | 6 |
| `/ogc/tiles/collections/{n}/tiles/WebMercatorQuad/{z}/{y}/{x}` | 204 | 3 |

The 413 carries `Content-Type: application/problem+json` and is produced by exactly one code path
on the OGC vector-tile route: `VectorTileExecution.ExecuteAsync` translating
`TileSizeLimitExceededException` (or its own post-read guard) into
`StandardErrorHelpers.CreatePayloadTooLarge`, i.e. the encoded MVT exceeded
`Limits:Tiles:MaxTileSize` (default 512,000 bytes, enforced since #4359/#4287). This is **not** the
musl AOT native-load class of #4912: MVT encoding happens inside PostGIS (`ST_AsMVT`), so no native
library is loaded in the server process on this path.

## The 44% failure rate is a property of the envelope, and is reproducible on paper

`scripts/soak/seed_envelope.py` seeds all 10,000 features of every layer inside
`EXTENT = (-122.50, 37.70, -122.30, 37.82)` — roughly 0.2° × 0.12° of San Francisco. The scenario
requests a uniformly random zoom in {0, 1, 2} and a uniformly random tile at that zoom, so the
chance of landing on the single tile that contains the whole extent is

```
1/3 × 1/1  +  1/3 × 1/4  +  1/3 × 1/16  =  0.4375
```

against an observed failure rate of 22,853 / 51,375 = **0.4448**. Every other tile in the pyramid is
empty and answers 204.

## The dense tile is 1.49 MB and costs ~1.4 s to encode

Replayed locally against `postgis/postgis:16-3.4` with the seeder's exact feature generator (10,000
points, the same six jsonb attributes) and the exact SQL
`PostgresStorageMappedFeatureReader.GetMvtTileAsync` builds for `z=0`, `extent=4096`, `buffer=256`,
target SRID 3857 — see `measure-dense-tile.sql`:

| measurement | value |
|---|---|
| `octet_length(ST_AsMVT(...))` for `0/0/0` | **1,492,055 bytes** |
| `Limits:Tiles:MaxTileSize` default | 512,000 bytes |
| encode time, cold | 5,642 ms |
| encode time, warm (3 runs) | 1,336 / 1,365 / 1,541 ms |

The soak's own numbers agree: the failing tile requests have a mean latency of 1,785 ms on a 4-core
runner under 170 virtual users. So 22,853 requests each spent roughly 1.4 s of PostGIS CPU building
a 1.49 MB tile that the server then threw away with a 413.

## Why the tile cache never absorbed any of it

Every tile endpoint carries `CacheOutput` (`OgcTilesTile`, `OgcTilesDatasetTile`, `MvtTile`,
`H3MvtTile`), and the working set here is only 21 distinct tiles (1 + 4 + 16) with a one-hour TTL —
one miss per tile should have covered the whole 67-minute soak. It did not, because ASP.NET Core's
default output-cache policy stores **200 responses only**. The entire tile workload on this envelope
is 204 (empty tile) or 413 (over budget), so the output cache stored nothing and every one of the
51,375 requests re-ran the full query. That is not soak-specific: a sparse layer answers 204 for
nearly every tile in a real deployment, and those were uncacheable too.

## Fix

`TileOutcomeOutputCachePolicy` (`src/Honua.Hosting/Features/Caching/`) re-enables cache storage for
the two deterministic non-200 tile outcomes — 204 and 413 — and is attached to the four tile
policies that already partition their cache key by the enforced byte budget
(`ResolveTileSizeOutputCacheKey`), so a cached refusal can never outlive the limit that produced it.
The policy re-asserts every reason storage is otherwise refused (Set-Cookie, an authenticated
principal, `Cache-Control: no-store`) because it runs last in the chain.

`VectorTileExecution` now also logs the refusal (`EventId 3473`, Warning) with the encoded byte count
and the configured budget. The server previously refused 44% of tile requests without a single log
line naming the limit; with the caching fix the line is emitted once per tile per TTL rather than
once per request.

## #4916

Both attested receipts on pin `548b7a5263da5a3f2381eb43f232687cdf92b0bf` fail the frozen
worst-scenario latency lock for the same reason, so the repeat is not runner variance:

| Run | p95 | p99 | Worst scenario |
|---|---:|---:|---|
| [34923808977](https://github.com/honua-io/honua-server/actions/runs/34923808977) | 1223.68 ms | 1982.46 ms | `tiles_load` |
| [34924256277](https://github.com/honua-io/honua-server/actions/runs/34924256277) | 1221.63 ms | 1951.74 ms | `tiles_load` |

That pin, and the second sample's head `f820af6712`, are ancestors of the tile output-cache
fix in #4926. The gate value is the worst scenario, and that scenario is the uncached 204/413
tile workload this note describes. Other scenarios on the first receipt stay under the 822 ms
p95 lock; `spatial_query_load` p99 is 1401.86 ms and shares the PostGIS the uncached tile
encodes were saturating. An attested passing receipt on a candidate that contains #4926 is
still required. The soak was not rerun from this change.

## Test evidence

`tests/dotnet/Honua.Server.Tests/Features/Infrastructure/Rendering/TileOutcomeOutputCacheTests.cs`
drives the real `ConfigureOutputCaching` policy set through a TestServer and counts handler
invocations.

- With the fix: `Passed! - Failed: 0, Passed: 22, Skipped: 0` (this file plus the existing
  `VectorTileSizeLimitTests`).
- With `policy.AddPolicy<TileOutcomeOutputCachePolicy>()` removed and nothing else changed:
  `Failed! - Failed: 6, Passed: 5` — see `regression-without-fix.log`. The six failures are exactly
  the empty-tile and OGC over-budget cases re-invoking the handler on the second request; the
  authenticated-request, server-error and budget-change guards stay green either way.
