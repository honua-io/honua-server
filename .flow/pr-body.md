test(geoprocessing): verify the repaired operator eval import fixture

# COVERS: Server Tests (Operator Eval Harness)

## Issue Link
Refs #5391 (resolved connection/schema policy remains enforced; historical fixture regression verification).

## Summary
The October 10 matrix at `79e37c73c6abccc0856297081ce9f9203464ee7f` failed one import-job integration test after PR #5761 enforced operational-schema-only connection search paths. The repair already landed in PR #5772 (`6f662b65bc2b16d6bc8062a1514741d4cc1da9ce`) and is present on this branch's current trunk base. This handoff records that existing repair; it introduces no runtime or test changes.

## Changes Made
- Preserve the owned operational import schema and all end-to-end success, feature-count, extent, progress, and provenance assertions introduced by #5772.
- Record artifact-first diagnosis and verification for this family only.
- Retain the production rejection of metadata and unconfigured schemas. No revert is appropriate for a historical regression whose fixture repair is already on trunk.

## Diagnosis
Read the [family diagnosis](https://github.com/honua-io/honua-server/commit/79e37c73c6abccc0856297081ce9f9203464ee7f#commitcomment-205858137) and culprit PR #5761 before local investigation. The first-parent range from `179d8e915c5f141ac973b770450d184561ef13fa` contains #5764 (`f65908a6cf9ada5bad6f7800892d3ea227040c5c`) followed by #5761 (`79e37c73c6abccc0856297081ce9f9203464ee7f`).

Both surviving `operator-eval-report` artifacts (11668570973 and 11670191040) report three scenarios passed with expected skips and zero failed scenarios. No shard TRX remains. The completed rerun [job log](https://github.com/honua-io/honua-server/actions/runs/38046734917/job/114212284344) reports 318 passed / one failed: `ImportDatasetJobExecutorTests.ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance`. The resolver rejects its old fixture search path with “Connection search path must contain only configured operational schemas.”

#5772 supplies a configured owned schema, keeps `honua` out of the operational search path, and verifies three materialized rows in that schema. Its production security policy remains intact.

## Testing
- [ ] Focused import-job integration tests completed
- [ ] `scripts/ci/pre-pr-check.sh --fast` passed

Verification is in progress. No local passing test or completed empirical bisect is claimed by this checkpoint.

## Gate Impact
- [x] PR gates (build, test, governance): historical Operator Eval Harness regression evidence only

## Docs or Contract Impact
- [x] None — no product documentation or contract changes

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None.

## Self-review
Pending the final verification results. Review scope is this handoff diff against latest trunk, using the PR reviewer rubric.

## Known follow-ups
None identified so far. The other failing matrix families belong to their separate lanes; this packet makes no coverage claim for them.
