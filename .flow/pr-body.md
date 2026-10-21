docs(ci): record operator eval repair and verification limits

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

Local verification has not completed. The first current-trunk focused attempt exhausted its 25-minute outer timeout before compilation (exit 124). A bounded retry returned exit 75: `dotnet shim: reservation unavailable; command not started (retryable)`. The midpoint attempt at `f65908a6cf` exhausted 20 minutes before compilation (exit 124). No local TRX was produced. The red-landing attempt at `79e37c73c6` also exhausted its 10-minute timeout before compilation (exit 124). The bounded fast-gate retry exited 75 at dependency-graph admission with the same reservation-unavailable message; no restore, build, format, unit, architecture, or focused integration test completed. The first fast-gate attempt passed instruction/routing checks, selected 32 projects and full architecture enforcement, then waited at dependency-graph admission; it was stopped (exit 143) for a bounded retry. This is an incomplete verification checkpoint, not a staged delivery.

Commands attempted from the named worktrees:

```bash
# Delivery worktree: /home/mike/honua-io/wt-trunkred-79e37c7-server-tests-operator-eval-harness
timeout 25m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ImportDatasetJobExecutorTests' --logger 'trx;LogFileName=operator-import-current.trx' --results-directory /tmp/operator-eval-repair-results
HONUA_BUILD_SLOT_WAIT=120 timeout 4m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ImportDatasetJobExecutorTests' --logger 'trx;LogFileName=operator-import-retry.trx' --results-directory /tmp/operator-eval-repair-results
timeout 90m scripts/ci/pre-pr-check.sh --fast
HONUA_BUILD_SLOT_WAIT=120 timeout 8m scripts/ci/pre-pr-check.sh --fast

# Detached bisect worktree: /home/mike/honua-io/wt-trunkred-79e37c7-operator-bisect
# At f65908a6cf9ada5bad6f7800892d3ea227040c5c:
timeout 20m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ImportDatasetJobExecutorTests.ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance' --logger 'trx;LogFileName=midpoint.trx' --results-directory /tmp/operator-eval-bisect-results
# At 79e37c73c6abccc0856297081ce9f9203464ee7f:
timeout 10m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ImportDatasetJobExecutorTests.ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance' --logger 'trx;LogFileName=red.trx' --results-directory /tmp/operator-eval-bisect-results
```

All managed commands used PATH `dotnet`, the lane admission shim, shared compilation and its unchanged four-node cap. No project source changed and no formatter was required.

## Gate Impact
- [x] PR gates (build, test, governance): historical Operator Eval Harness regression evidence only

## Docs or Contract Impact
- [x] None — no product documentation or contract changes

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None.

## Self-review
Completed one review round against `origin/trunk` using the PR reviewer rubric. No P0/P1 findings; no P2/P3 findings. The diff changes only this handoff file. Checked the historical attribution, existing fixture repair, exact attempted commands, exit statuses, unchanged security policy, commit identity and absence of runtime/test changes. `git diff --check origin/trunk...HEAD` passed. The local verification failure is explicitly recorded and prevents staged delivery.

## Known follow-ups
No P2/P3 code follow-ups. Local `scripts/ci/pre-pr-check.sh --fast` and the focused `ImportDatasetJobExecutorTests` run must complete when build capacity is available before this packet can be marked verified. The historical empirical bisect remains incomplete. The other failing matrix families belong to their separate lanes; this packet makes no coverage claim for them.

This is a backed-up, incomplete checkpoint. No PR was opened, no CI was polled, and no staged-success JSON was written. The existing runtime/fixture repair is already on trunk in #5772; this checkpoint must not be presented as a newly implemented or locally verified fix.
