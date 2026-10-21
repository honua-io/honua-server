docs(ci): verify existing admin layer management repair

# COVERS: Server Tests (Server Features Admin Layer Management)

## Issue Link

Refs #5624 (preserve PMTiles source authorization; verify the historical fixture repair).

## Summary

The Admin Layer Management shard at `79e37c73c6abccc0856297081ce9f9203464ee7f` failed one shared-storage publish case after #5764 required a routable source. The fixture repair already landed in #5784 (`12b30dedb4d1bbadb7add1f68be30ae237dccbf4`) and is present on this branch. This packet records verification of that existing fix forward and introduces no runtime or test changes.

## Changes Made

- Record the artifact-first diagnosis and verification of Admin Layer Management only.
- Preserve the existing routable-source fixtures and refusal-without-upload assertions from #5784.
- Preserve the earlier Operator Eval lane's incomplete checkpoint verbatim in `.flow/operator-eval-checkpoint.md`. This packet does not claim that lane's coverage or that its pending local checks passed.

## Diagnosis and Fix vs Revert

Read the [family diagnosis](https://github.com/honua-io/honua-server/commit/79e37c73c6abccc0856297081ce9f9203464ee7f#commitcomment-205860371) and [culprit PR #5764](https://github.com/honua-io/honua-server/pull/5764) before investigation. Checked the artifact inventory for run 38046734917 first: no Admin Layer Management TRX remains; only unrelated operator-eval, MapLibre, image-build and SBOM artifacts survive. The shard artifacts had seven-day retention.

The [completed rerun log](https://github.com/honua-io/honua-server/actions/runs/38046734917/job/114212284326) reports 103 passed / 1 failed / 104 total. The failure is `TileOperationJobServicePublishTests.ArchiveOrPublish_SharedStorageLayer_UsesRequestedServiceResourceMetadata(operation: "publish", serviceScoped: false, publicationLayerIndex: 7)`. It expected Completed but received Failed: `Publish could not bind the same routable source that generated the archive, so the object was not uploaded.`

The first-parent range from the supplied last green `179d8e915c5f141ac973b770450d184561ef13fa` contains exactly #5764 (`f65908a6cf9ada5bad6f7800892d3ea227040c5c`), then #5761 (`79e37c73c6abccc0856297081ce9f9203464ee7f`). #5764 introduces the generation/upload source identity guard. The test's first storage-layer binding belongs to an unpublished resource, so the bare layer ID cannot resolve to a routable source. #5761 does not change this test, execution core, source resolver or metadata graph index. The supplied diagnosis records executed green/red CI results and source-based midpoint isolation; it does not claim an executed local midpoint. This packet likewise makes no new historical executable-bisect claim.

An additional baseline attempt in `/home/mike/honua-io/wt-trunkred-79e37c7-admin-layer-bisect` at `179d8e915c5f141ac973b770450d184561ef13fa` reached restore but failed with NU1900 because the sandbox could not write NuGet's vulnerability cache. It was stopped (exit 143) before compilation. The same command retried with escalation was refused at worktree admission with `BUILD-RESERVATION-RECONCILE-REQUIRED` / `unclean guardian exit`; it was stopped (exit 143) without starting dotnet. No historical TRX was produced and the queued midpoint/red commands were not reached. No reservation was bypassed or rewritten. The focused historical command attempted was:

```bash
timeout 20m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ArchiveOrPublish_SharedStorageLayer_UsesRequestedServiceResourceMetadata' --logger 'trx;LogFileName=179d8e915c5f141ac973b770450d184561ef13fa.trx' --results-directory /tmp/admin-layer-historical-results
```

Fix forward is already on trunk in #5784. Its fixture graph supplies routable bindings for ordinary unscoped publish cases, and the ambiguous shared-storage case explicitly requires failure with no upload. The archive and service-scoped cases still check the requested metadata and decoded tile payloads. Reverting #5764 now would remove a production security repair after the fixture fix has already landed. The regression is historical, so the fresh-red revert rule does not apply.

## Testing

- [x] Focused `TileOperationJobServicePublishTests` passed in Release: 22 passed, 0 failed, 0 skipped
- [ ] Ran `scripts/ci/pre-pr-check.sh --fast` and all checks passed

Verification is in progress. Exact commands and results will be recorded before staged delivery. All managed commands use PATH `dotnet`, the lane shim's unchanged four-node cap, and shared compilation. No project source changed, so no formatter is required.

The first current-trunk focused attempt (`timeout 30m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --configuration Release --filter 'FullyQualifiedName~TileOperationJobServicePublishTests' --logger 'trx;LogFileName=admin-layer-publish.trx' --results-directory /tmp/admin-layer-repair-results`) restored and compiled dependencies, then exhausted its outer timeout during compilation (exit 124). No test result is claimed from that attempt. A longer incremental retry and the first fast-gate invocation were stopped while queued (exit 143) after inspection showed a mismatch between the sandbox's PID namespace and the host process identities used by the shared queue. Verification is being retried with normal command escalation and the same PATH shim, worktree exclusion, capacity limit and compiler settings.

The escalated focused retry completed successfully (exit 0): 22 passed, 0 failed, 0 skipped. All six shared-storage theory rows passed, including the historical unscoped publish failure; TRX: `/tmp/admin-layer-repair-results/admin-layer-publish-retry.trx`. The fast gate remains pending.

```bash
timeout 60m dotnet test tests/dotnet/Honua.Server.Tests/Honua.Server.Tests.csproj --no-restore --configuration Release --filter 'FullyQualifiedName~TileOperationJobServicePublishTests' --logger 'trx;LogFileName=admin-layer-publish-retry.trx' --results-directory /tmp/admin-layer-repair-results
```

The escalated fast gate is running; shared build-capacity admission is currently pending. Exact command:

```bash
timeout 180m scripts/ci/pre-pr-check.sh --fast
```

## Gate Impact

- [x] PR gates (build, test, governance): historical Admin Layer Management regression verification

## Docs or Contract Impact

- [x] None — no product documentation or contract changes

## Release/Deploy Impact

- [x] None — standard merge-and-release flow

## Breaking Changes

None.

## Self-review

Completed one review round against `origin/trunk` using the PR reviewer's rubric. The net diff contains only handoff documents: no runtime changes, test changes, dependency-direction changes, new endpoints or public infrastructure types. Checked the culprit boundary, existing repair, exact verification attempts, archive preservation, commit identity and source-policy assertions; corrected the checkpoint link. No P0/P1 findings. The P2 historical-bisect evidence follow-up is recorded below. `git diff --check origin/trunk...HEAD` passes. Verification remains pending; no staged-success receipt has been written.

## Known Follow-ups

P2 evidence follow-up: complete the historical executable bisect when the dedicated bisect worktree's build reservation can be reconciled. Attribution currently relies on the existing green/red CI executions and isolated source delta, not an executed local midpoint.

The preserved Operator Eval checkpoint remains unverified by this packet. Other failed families belong to their own lanes.
