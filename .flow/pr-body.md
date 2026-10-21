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
- The initial local historical baseline attempt at `93f6f9aeb3` exited 75 after the 40-minute build-admission limit: `dotnet shim: reservation unavailable; command not started (retryable)`. No test verdict was produced. A same-command historical retry is queued; no completed local runtime bisect is claimed.

Identify-family diagnosis: [commit comment](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205857884) and [culprit PR #5835](https://github.com/honua-io/honua-server/pull/5835) read before investigation. Artifact inventory was checked first; artifact `11710678770` is no longer downloadable. [Retained target logs](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712938) confirm 50 total, 35 passed, 15 failed. The diagnosis records the same 15 failures in the culprit PR test execution (despite its report-only green job conclusion) and intermediate trunk. This packet does not claim a completed local historical runtime bisect.

## Testing
- [ ] Integration tests updated and focused Release class passes.
- [ ] `scripts/ci/pre-pr-check.sh --fast` passes.
- [ ] Changed-project formatting verified.

Exact verification commands (queued; no local pass/fail verdict yet):

```bash
dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerQueryLimitsTests --logger 'trx;LogFileName=mapserver-query-limits.trx' --results-directory /tmp/honua-geoservices-6f74305-results
scripts/ci/pre-pr-check.sh --fast
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget dotnet test tests/dotnet/Honua.Protocols.GeoServices.Tests/Honua.Protocols.GeoServices.Tests.csproj --configuration Release --filter FullyQualifiedName~MapServerIdentifyEndpointTests --logger 'trx;LogFileName=identify.trx' --results-directory /tmp/mapserver-identify-verification
NUGET_HTTP_CACHE_PATH=/tmp/mapserver-identify-nuget scripts/ci/pre-pr-check.sh --fast
```

Both commands use the PATH lane shim with its four-node cap and shared compilation enabled. The focused run acquired capacity and has restored the GeoServices test project and shared fixture. Its test verdict is pending. The first fast-check attempt validated capability completeness and selected 33 build roots, with full catalogue/proof enforcement because `.flow/pr-body.md` is an unclassified contract input, then exited 75 before managed validation while waiting for the same worktree reservation. It will be rerun after the focused command finishes. No test or managed-build success is claimed yet.

Identify-lane progress: restore completed successfully and compilation is active. The initial fast check validated capability metadata and selected 33 build roots; its duplicate waiter was interrupted before managed validation so it can run sequentially after the focused test. No Identify test verdict is claimed yet.

## Self-review
Reviewed the diff using `honua-flow-work/tools/lib/pr-review-rubric.md`: existing endpoint and fixture reused, original exact cap assertion retained, native/default CRS coverage added, no runtime, dependency, security, or public API changes. P0/P1: none found. P2/P3 follow-ups: none identified. Verification pending.

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
