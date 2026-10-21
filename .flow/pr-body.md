test(mapserver): verify Identify CRS defaults, precedence, and limits

# COVERS: Server Tests (GeoServices Geometry VectorTile and Versioning); Server Tests (GeoServices MapServer Identify)

## Issue Link
Refs #5884 (the original family repair landed in #5887; this adds endpoint regression coverage for the advertised default CRS).

## Summary
The historical shard at `6f7430587a1a8283f7e95de86c38908a3cb2ed9d` failed only `MapServerQueryLimitsTests.MapServer_Identify_WithConfiguredMaxRecordCount_AppliesGlobalCap`: expected two results, received zero. Culprit #5835 (`3565542b17a2814b041d9a5d633efb590047bbcd`) changed omitted Identify SR to the advertised EPSG:3857, while that test sent degree coordinates without an SR. Current trunk already contains #5887's explicit `sr=4326` repair.

Preserve that fix forward and extend its regression test to exercise a Web Mercator envelope without `sr`. Both cases must return exactly two results and geometries in the requested/default CRS. No runtime change or revert is needed. The regression predates multiple trailing heads and eleven landings separate the supplied green and red heads; reverting the broad culprit would also discard unrelated protocol corrections.

The Identify family independently had 15 failures (35 passes) at the same head: degree-coordinate GET/POST, point-literal baseline, JSON mapExtent, time override, and gdbVersion fixtures omitted `sr`. #5887 declares `sr=4326` in those fixtures, and #5893 adds omitted-SR Web Mercator endpoint coverage. This packet preserves both landed repairs and strengthens the latter coverage.

## Changes Made
- Retain the degree-coordinate cap case with explicit EPSG:4326.
- Add an omitted-SR EPSG:3857 envelope case under the same two-result global cap.
- Assert every returned geometry uses the expected SRID.
- Add four GET/POST endpoint cases proving explicit `sr` overrides geometry CRS and `latestWkid` overrides `wkid`, including exact feature coordinates and returned CRS.
- Require the existing omitted-SR GET/POST cases to report `wkid=3857`, in addition to projected coordinates.

## Diagnosis Evidence
- [Family diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205859368) read before local investigation, together with culprit PR #5835.
- Artifacts checked first: run inventory no longer contains the seven-day shard TRX; direct download of artifact `11711346841` returns HTTP 410 (expired).
- [Retained target job log](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712856) confirms 275 passes and the one cap assertion failure.
- The cap test blob is identical (`7772c9cb7c6efe8e40ec7875baad78f50f26a4de`) at the supplied green and every first-parent landing through the red head. The native-service fallback remains through `8b45637322`, then changes to `CachedMapSpatialReferenceId` at #5835 and remains there through the red head. These are source comparisons, not local runtime verdicts.
- Two local historical baseline attempts at `93f6f9aeb3` each exited 75 after the 40-minute build-admission limit: `dotnet shim: reservation unavailable; command not started (retryable)`. Neither started a build or test. No completed local runtime bisect is claimed yet. The adjacent replay has started at immediate predecessor `8b45637322` and will then test culprit landing `3565542b17`, using the supplied retained-CI green baseline. Historical invocations use `-p:NuGetAudit=false -p:IncludeSourceRevisionInInformationalVersion=false -p:EnableSourceLink=false` for historical dependency restore and debug/version metadata caching; current-branch verification uses normal audit and metadata settings.
- The culprit PR run `38140064141` selected Identify, Export, ImageServer, and Catalog shards, but not this geometry-family shard. Its retained Identify failures corroborate the CRS mechanism; they are not presented as an execution of the cap test.

Identify-family diagnosis: [commit comment](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205857884) and [culprit PR #5835](https://github.com/honua-io/honua-server/pull/5835) read before investigation. Artifact inventory was checked first; artifact `11710678770` is no longer downloadable. [Retained target logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712938) confirm 50 total, 35 passed, 15 failed. The diagnosis records the same 15 failures in the culprit PR test execution (despite its report-only green job conclusion) and intermediate trunk. This packet does not claim a completed local historical runtime bisect.

## Testing
- [x] `MapServerQueryLimitsTests`: Release, 4 passed, 0 failed, 0 skipped (including both CRS cap cases).
- [ ] Final Identify class verification pending.
- [ ] `scripts/ci/pre-pr-check.sh --fast` passes.
- [ ] Changed-project formatting verified.

Verification commands (initial QueryLimits run passed; final checks pending):

```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerQueryLimitsTests --logger 'trx;LogFileName=mapserver-query-limits.trx' --results-directory /tmp/honua-geoservices-6f74305-results
scripts/ci/pre-pr-check.sh --fast
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerIdentifyEndpointTests --logger 'trx;LogFileName=identify.trx' --results-directory /tmp/mapserver-identify-verification
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget scripts/ci/pre-pr-check.sh --fast
IFS= read -r HONUA_TEST_DB_URL < /tmp/mapserver-identify-sidecar/db-url
export HONUA_TEST_DB_URL
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --no-build --filter FullyQualifiedName~MapServerIdentifyEndpointTests --logger 'trx;LogFileName=identify-sidecar.trx' --results-directory /tmp/mapserver-identify-verification
```

All commands use the PATH lane shim with its four-node cap and shared compilation enabled. The initial Release `MapServerQueryLimitsTests` run completed successfully: 4 passed, 0 failed, 0 skipped; TRX `/tmp/honua-geoservices-6f74305-results/mapserver-query-limits.trx`. The first fast-check attempt validated capability completeness and selected 33 build roots, then exited 75 before managed validation while waiting for the worktree reservation. After syncing the latest `origin/trunk`, the final fast check selected 33 roots / a 105-project closure, completed restore, and started its warnings-as-errors build. Separate named-class reruns follow in the same reserved sequence. Final fast-check and Identify results remain pending. Progress at 2026-10-21 05:20 UTC: selected runtime, test infrastructure, source-architecture, provider, and cloud assemblies are compiling successfully. One MSB3026 native-asset copy retry recovered; its owning project built successfully. The predecessor replay has built Hosting and Jobs. Active logs: `/tmp/honua-geoservices-6f74305-final-verification.log` and `/tmp/honua-geoservices-6f74305-adjacent-retry.log`; predecessor detail: `/tmp/honua-geoservices-6f74305-adjacent/8b45637322c1cefc088fd5917943fa3712c3633e.log`.

Identify-lane progress as of 2026-10-21 05:12 UTC: the complete Release dependency and test assembly build succeeded. The automatic-container execution hit PostgreSQL fixture initialization (connection refused) before endpoint assertions; it was stopped. A no-build retry confirmed live localhost connectivity but repeatedly restarted database containers between fixture lifetimes. The current full-class no-build run uses the existing TestKit-supported `HONUA_TEST_DB_URL` path with a dedicated ready PostGIS 18 sidecar, retaining per-test schema isolation and every endpoint assertion. It acquired the normal lane reservation and is executing the full class. Dedicated-database transaction counters are advancing without SQL rollbacks, and no endpoint assertion failures have been reported; the final Identify test verdict is still pending. The database credential stays in a private local file and is not committed. The fast-check waiter was interrupted before managed validation so the final check can follow the focused run sequentially.

## Self-review
Reviewed the diff using `honua-flow-work/tools/lib/pr-review-rubric.md`: existing endpoint and fixture reused, original exact cap assertion retained, native/default CRS coverage added, no runtime, dependency, security, or public API changes. P0/P1: none found. P2/P3 follow-ups: none identified. Initial QueryLimits verification passed; final verification pending.

## Gate Impact
- [x] PR gates (build, test, governance)
- [x] Nightly gates (affected full-matrix family)

## Docs or Contract Impact
- [x] None — exercises the existing advertised MapServer CRS contract.

## Release/Deploy Impact
- [x] None — standard merge-and-release flow.

## Breaking Changes
None.

## Pre-PR Checklist
- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all checks passed.
- [x] Commit messages use conventional format and owner identity.
- [x] PR title matches the implementation commit message.
- [x] Existing repair issue linked above.
- [x] No PR opened and no CI/review polling; staged delivery only.
