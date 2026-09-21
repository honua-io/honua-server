# Bounded external-client roster: nightly-1cd2ef5

**Not certified: 41 pass, 11 fail, 0 skip across 52 required cells (31 receipts).** This is a pre-cut proof against the newest published trunk nightly
available at the start of this run, under operator ruling A (2026-09-16), for #3434.
The release promise is that the bounded 2026.1 supported external-client operations
work against the immutable server image. The remaining failing cells block that promise.

## Identity and reproduction

- Server: `1cd2ef5ea1e39571ef26b157dccdb6de796f5ed9`.
- Image: `ghcr.io/honua-io/honua-server@sha256:6271a044c7fe1e0a0ba122cdb69d916c8b821313dc57ae530e85173ffd2f0b76`.
- Tag: `nightly-1cd2ef5`; publication run [35504430543](https://github.com/honua-io/honua-server/actions/runs/35504430543).
- Producer: `0f64f2fd94` (full SHA in `run/candidate.json`).
- Release denominator: `2026-09-16-complete.13`, frozen from honua-release commit
  `7165e7f67937318a0c53a7292df7730961cf13c2`.
- Target: one isolated local Docker stack; all clients use the same image and seeded fixture.
- There is no release cut ID. The exact nightly digest is the pre-cut candidate identity.

Run `certification/bounded-roster/run-roster.sh` with `--tag nightly-1cd2ef5`, the
`--expect-digest` above, this directory as `--evidence`, a real-disk `--run-root`,
and an isolated `--project`. The harness records fixture/config/auth hashes,
client image identities, client versions, timestamps, operations, source SHA and durable
receipt URIs. `run/wire.jsonl.gz` and `wire-join.json` bind results to actual client traffic.
The raw run directory is named in `run/candidate.json`'s `run_id`.

## What changed since the previous proof

The mirror now follows merged release PR #361: 52 required cells, rather than 59.
The two non-addressable GDAL OAPIF operations remain explicit `excludedRequirements`.
Coverages/EDR preview rows and redundant WMTS surface rows are absent in the upstream
required denominator; their harness functions remain available. The runner excludes only
these seven explicitly governed retirements, records the reason, and executes them again
if they return to the denominator. Unknown missing requirements still fail.

GDAL 3.13.3's full OSGeo image supplies the Parquet driver. The new GeoParquet cell
checks six point coordinates and scalar attributes against `docker/cng/seed.sql`, the
2D geometry/schema types, GeoParquet 1.1 WKB metadata, bbox covering columns, independently
known extent, feature count and EPSG:4326 CRS. Raster nodata does not apply to this vector
fixture. No output snapshot is used as the oracle.

`diagnostic-before/` preserves unsuccessful assertions from producer `106419447a`.
The final producer corrects two client-API assumptions: QGIS schema checks use field type
enums rather than optional display names, and GeoParquet bounds use its supported
`covering.bbox` representation and GDAL extent. It explicitly requests EPSG:4326 in the
GDAL axis check; that check still fails, so no tolerance or coordinate assertion was
relaxed. WFS retains its original typed-schema assertions.

## Remaining blockers

| Cells | Observed failure | Evidence |
| --- | --- | --- |
| Seven GDAL/OGR OAPIF cells (landing, collections, collection, items, item, queryables, service) | GDAL reports CRS84 despite requested EPSG:4326. A separate explicit `SetActiveSRS` probe selects 4326, then the bbox request fails HTTP 400 instead of returning seeded features 3 and 4. | Per-cell observations; `gdal-explicit-crs.json`; `probe-gdal-crs.py` |
| GDAL COG dataset read and cloud COG serving | The registered fixture's tile yields a GeoServices error 404, so GDAL cannot decode the requested image. Registration/refresh succeeds; this is different from the previous S3 scan hang. | `cog-tile-error.json`; fixture registrations and cell observations |
| GDAL multidimensional Zarr coverage | Collection metadata lacks a spatial bbox; GDAL reports `Missing bbox`. Metadata describes a one-dimensional latitude grid instead of the seeded datacube. | `zarr-collection.json`; cell observation |
| OWSLib WCS | Out-of-extent request produces HTTP 500 instead of a protocol rejection. | Cell observation; previous #4997; its #5060 fix was reverted in #5064 |

The supplemental CRS probe uses the same GDAL client image and exact candidate, through
the candidate network directly. Its independent expected features come from the SQL seed;
it is diagnostic evidence, not an extra passing receipt. The raw raster responses are
also supplemental diagnostics. No licensed Esri runner is required by these 52 cells;
ArcGIS certification remains delegated to honua-esri-compat.

## Acceptance disposition

1. Frozen operations/client versions: mirrored from the exact governed denominator.
2. Every required cell executes against the pinned image: see the final verdict and receipts.
3. Identity/provenance: recorded in every receipt and `run/`; no cut ID exists yet.
4. Required positive/negative/auth/paging/schema/metadata facets execute; failing facets
   remain failures. The existing single-tenant anonymous-and-protected profile is unchanged;
   no cross-tenant GA certification is asserted.
5. Fail-closed gate: the release verifier exits 1 on the remaining failures; none is a skip
   or missing envelope. Rejection tests remain intact.
6. Delegated clients and explicit exclusions remain catalogued, with the release-owned
   denominator and its rulings authoritative for scope.
7. OWSLib/PySTAC use the existing bounded producer; no duplicate producer is added.

#3434 remains open. The supported-client certification outcome is released as unmet
because the candidate has failing required cells, not because a candidate is unavailable.

## Local validation

- 108 mirror/verifier tests and 14 bounded-harness tests passed.
- `scripts/ci/pre-pr-check.sh --fast` passed: warnings-as-errors build, 68 AI tests,
  310 architecture tests, and repository checks. The fast mode excludes server integration
  shards by design; those are not claimed as local evidence.
- No C# project changed; no solution-wide formatting was run.
