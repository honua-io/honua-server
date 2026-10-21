test(admin): verify runtime schemas and PMTiles publish sources
# COVERS: Server Tests (Server Features Admin Runtime Operations); Server Tests (Server Features Admin Tiles and Scenes)

## Issue Link

Refs #5624 (the source-layer authorization remains enforced; this change pins the routable publish fixtures).
Refs #5391 (preserves the runtime lane's connection-policy regression assertions on this shared branch).

## Summary

The historical Tiles and Scenes matrix failure at `493edf3b585c5fb67188113334906687fd442359` came from PR #5764 (`f65908a6cf9ada5bad6f7800892d3ea227040c5c`), which required a publish to retain the same routable source throughout archive generation and upload. Nine existing publish cases lacked the source bindings that guard requires. Trunk already repaired those fixtures in PR #5784 (`12b30dedb4d1bbadb7add1f68be30ae237dccbf4`).

The runtime failure at the same head came from PR #5761 (`79e37c73c6abccc0856297081ce9f9203464ee7f`): encrypted fixture connections named a schema that was not configured as operational. PR #5784 also repaired that isolated-host composition. The existing source/managed mutation theory now resolves its encrypted connection through the real policy and queries PostgreSQL `current_schema()` before retaining every publication and ownership check.

This branch retains that fix and adds eight cases proving every bare storage-layer fixture publishes with the service, publication, resource, and storage-binding metadata the PMTiles proxy resolves. Layer 7 also belongs to the `world` service, so the test proves a stamped source survives an otherwise ambiguous layer-only lookup. The shared branch's runtime connection-policy assertions are preserved.

Fix-forward coverage is appropriate: the original repair is already on trunk and 355 commits followed the reported red head. Reverting the historical security landing would remove authorization that these tests should continue to enforce.

## Changes Made

- Resolve the runtime fixture connection, assert its search path, and verify the schema selected by PostgreSQL for both source and managed cases.
- Exercise successful unscoped publishing for storage layers 1, 5, 7, 9, 21, 22, 31, and 100.
- Read each stored artifact's source metadata and resolve it through the production PMTiles source resolver.
- Preserve every existing publish, rollback, sanitization, and unroutable-source assertion, plus the runtime lane's schema-policy checkpoint.

## Diagnosis Evidence

The artifact inventory was checked first; the shard TRX is no longer listed. Surviving historical job logs show [111/111 passing at the last green](https://github.com/honua-io/honua-server/actions/runs/38039717015/job/114177463775), then [103 passed / 9 failed at `79e37c7`](https://github.com/honua-io/honua-server/actions/runs/38046734917/job/114212285743) and [the same nine failures at `493edf3`](https://github.com/honua-io/honua-server/actions/runs/38048019858/job/114215518668).

The [diagnosis comment](https://github.com/honua-io/honua-server/commit/493edf3b585c5fb67188113334906687fd442359#commitcomment-205888989) was read before further diagnosis, along with culprit PR #5764. Source comparison confirms the test file was unchanged at that landing and the relevant publish/proxy path was unchanged between that landing and the target. Attribution of the historical Tiles and Scenes failure at `f65908a` remains source-based. The local midpoint execution below covers the Runtime Operations theory; it does not claim a historical Tiles and Scenes test execution.

### Runtime operations attribution

Read the [runtime diagnosis](https://github.com/honua-io/honua-server/commit/493edf3b585c5fb67188113334906687fd442359#commitcomment-205861957) and culprit [PR #5761](https://github.com/honua-io/honua-server/pull/5761) before the local midpoint. The target artifact inventory had no retained runtime TRX; surviving logs show the exact resolver denial, `Connection search path must contain only configured operational schemas.`

| Revision | Execution evidence | Result |
| --- | --- | --- |
| `179d8e915c` | [Historical CI job](https://github.com/honua-io/honua-server/actions/runs/38039717015/job/114177462318), including both theory cases | 107 passed / 0 failed |
| `f65908a6cf` | Local Release run of `QualifiedLayerMutations_ThroughConnectionRoute_PreserveEffectiveStorageOwnership`, TRX checked | 2 passed / 0 failed / 0 skipped |
| `79e37c73c6` (#5761) | [Historical CI job](https://github.com/honua-io/honua-server/actions/runs/38046734917/job/114212284301), both theory cases explicitly failed | 99 passed / 8 failed |
| `493edf3b58` | [Target CI job](https://github.com/honua-io/honua-server/actions/runs/38048019858/job/114215516812), same eight failures | 99 passed / 8 failed |

This combines existing CI executions with the newly executed missing midpoint; it does not claim four newly executed local revisions. The first runtime failure is #5761. Current trunk retains the #5784 fixture fix; reverting the historical policy would remove enforced protections after the fixture contract has already been repaired.

## Testing

- [x] Unit tests added/updated
- [x] Integration tests added/updated by the preserved runtime checkpoint
- [ ] Architecture tests pass
- [ ] Manual testing performed

Completed historical verification in `/home/mike/honua-io/wt-trunkred-493edf3-runtime-bisect`, detached at `f65908a6cf9ada5bad6f7800892d3ea227040c5c`:

```sh
timeout 90m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --no-restore -p:NuGetAudit=false --filter FullyQualifiedName~QualifiedLayerMutations_ThroughConnectionRoute_PreserveEffectiveStorageOwnership --logger 'trx;LogFileName=f65908a6cf.trx' --results-directory /tmp/trunkred-493-runtime-bisect-results > /tmp/trunkred-493-runtime-bisect-f65908a6cf.log 2>&1
```

Result: **2 passed, 0 failed, 0 skipped**, with both named theory cases verified in TRX. `NuGetAudit=false` is limited to this historical diagnostic revision; no package or production policy was changed.

Combined current-branch publishing verification and the final fast gate remain in progress. Initial 30-minute attempts expired during compilation without test results. The first combined fast gate exited 75 before building because the focused run held the worktree lock; it will be rerun sequentially. The combined Release build subsequently completed, but its 90-minute wrapper expired during test execution without a complete TRX. The C# contents are unchanged from that compiled tree (only staged notes changed); the same two class filters are being retried with `--no-build`, normal console progress, and a separate test budget. Final commands and results will be recorded before staged delivery.

## Gate Impact

- [x] PR gates (build, test, governance)
- [ ] Nightly gates (conformance, performance, security)
- [ ] Release gates (packaging, publishing)
- [ ] Deploy gates (promotion, post-apply validation)
- [ ] None — no gate impact

## Docs or Contract Impact

- [x] None — no docs or contract impact

## Release/Deploy Impact

- [x] None — standard merge-and-release flow

## Breaking Changes

None.

## Self-Review

Reviewed against the PR review rubric for correctness, authorization, architecture, and test coverage. Production code is unchanged. All original assertions remain. No P0/P1 findings or P2/P3 follow-ups were identified in the added coverage. Final review will also check the completed verification and pushed head.

## Pre-PR Checklist

- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all checks passed
- [x] Commit messages follow conventional format: `type: description (#issue)`
- [x] PR title matches the shared regression coverage
- [x] Issue numbers linked above
- [x] Tests added for the regression contract
