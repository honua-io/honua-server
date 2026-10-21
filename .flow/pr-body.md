test(mapserver): cover identify cap in the advertised CRS

# COVERS: Server Tests (GeoServices Geometry VectorTile and Versioning)

## Issue Link
Refs #5884 (the original family repair landed in #5887; this adds endpoint regression coverage for the advertised default CRS).

## Summary
The historical shard at `6f7430587a1a8283f7e95de86c38908a3cb2ed9d` failed only `MapServerQueryLimitsTests.MapServer_Identify_WithConfiguredMaxRecordCount_AppliesGlobalCap`: expected two results, received zero. Culprit #5835 (`3565542b17a2814b041d9a5d633efb590047bbcd`) changed omitted Identify SR to the advertised EPSG:3857, while that test sent degree coordinates without an SR. Current trunk already contains #5887's explicit `sr=4326` repair.

Preserve that fix forward and extend its regression test to exercise a Web Mercator envelope without `sr`. Both cases must return exactly two results and geometries in the requested/default CRS. No runtime change or revert is needed. The regression predates multiple trailing heads and eleven landings separate the supplied green and red heads; reverting the broad culprit would also discard unrelated protocol corrections.

## Changes Made
- Retain the degree-coordinate cap case with explicit EPSG:4326.
- Add an omitted-SR EPSG:3857 envelope case under the same two-result global cap.
- Assert every returned geometry uses the expected SRID.

## Diagnosis Evidence
- [Family diagnosis](https://github.com/honua-io/honua-server/commit/6f7430587a1a8283f7e95de86c38908a3cb2ed9d#commitcomment-205859368) read before local investigation, together with culprit PR #5835.
- Artifacts checked first: run inventory no longer contains the seven-day shard TRX; direct download of artifact `11711346841` returns HTTP 410 (expired).
- [Retained target job log](https://github.com/honua-io/honua-server/actions/runs/38158644558/job/114539712856) confirms 275 passes and the one cap assertion failure.
- The cap test blob is identical (`7772c9cb7c6efe8e40ec7875baad78f50f26a4de`) at the supplied green and every first-parent landing through the red head. The native-service fallback remains through `8b45637322`, then changes to `CachedMapSpatialReferenceId` at #5835 and remains there through the red head. These are source comparisons, not local runtime verdicts.
- Historical local test results: pending.

## Testing
- [ ] Integration tests updated and focused Release class passes.
- [ ] `scripts/ci/pre-pr-check.sh --fast` passes.
- [ ] Changed-project formatting verified.

Exact verification commands and results: pending.

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
