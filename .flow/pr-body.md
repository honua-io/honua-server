test(admin): pin PMTiles publication and connection policy regressions
# COVERS: Server Tests (Server Features Admin Tiles and Scenes)

## Issue Link

Refs #5624 (the source-layer authorization remains enforced; this change pins the routable publish fixtures).
Refs #5391 (preserves the runtime lane's connection-policy regression assertions on this shared branch).

## Summary

The historical Tiles and Scenes matrix failure at `493edf3b585c5fb67188113334906687fd442359` came from PR #5764 (`f65908a6cf9ada5bad6f7800892d3ea227040c5c`), which required a publish to retain the same routable source throughout archive generation and upload. Nine existing publish cases lacked the source bindings that guard requires. Trunk already repaired those fixtures in PR #5784 (`12b30dedb4d1bbadb7add1f68be30ae237dccbf4`).

This branch retains that fix and adds eight cases proving every bare storage-layer fixture publishes with the service, publication, resource, and storage-binding metadata the PMTiles proxy resolves. Layer 7 also belongs to the `world` service, so the test proves a stamped source survives an otherwise ambiguous layer-only lookup. The shared branch's runtime connection-policy assertions are preserved.

Fix-forward coverage is appropriate: the original repair is already on trunk and 355 commits followed the reported red head. Reverting the historical security landing would remove authorization that these tests should continue to enforce.

## Changes Made

- Exercise successful unscoped publishing for storage layers 1, 5, 7, 9, 21, 22, 31, and 100.
- Read each stored artifact's source metadata and resolve it through the production PMTiles source resolver.
- Preserve every existing publish, rollback, sanitization, and unroutable-source assertion, plus the runtime lane's schema-policy checkpoint.

## Diagnosis Evidence

The artifact inventory was checked first; the shard TRX is no longer listed. Surviving historical job logs show [111/111 passing at the last green](https://github.com/honua-io/honua-server/actions/runs/38039717015/job/114177463775), then [103 passed / 9 failed at `79e37c7`](https://github.com/honua-io/honua-server/actions/runs/38046734917/job/114212285743) and [the same nine failures at `493edf3`](https://github.com/honua-io/honua-server/actions/runs/38048019858/job/114215518668).

The [diagnosis comment](https://github.com/honua-io/honua-server/commit/493edf3b585c5fb67188113334906687fd442359#commitcomment-205888989) was read before further diagnosis, along with culprit PR #5764. Source comparison confirms the test file was unchanged at that landing and the relevant publish/proxy path was unchanged between that landing and the target. Attribution of the unexecuted `f65908a` midpoint is source-based; no new locally executed historical bisect is claimed.

## Testing

- [x] Unit tests added/updated
- [x] Integration tests added/updated by the preserved runtime checkpoint
- [ ] Architecture tests pass
- [ ] Manual testing performed

Verification is in progress. Final commands and results will be recorded before the staged branch is delivered.

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
