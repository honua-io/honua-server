test(ci): cover STAC client and topology promotion regressions
# COVERS: Python Integration Tests; Server Tests (Server Features Admin Network and Jobs)

## Issue Link
Refs trunk-red a6581e4 (Python Integration Tests family; the a6581e4 failures were already repaired on trunk by #5887/#5892, this repairs the current red at 4ff2e7c)

## Summary
The packet's original red at a6581e4 (#5827 moved GET STAC paging to `next=offset:N`; `test_nb_page_03`/`test_nb_page_05` still asserted `token=`) was fixed forward on trunk on 10-11 by #5887 and #5892, and the family ran green on trunk through 405198df54 (run 39470379129).

The family is red again at 4ff2e7c8c2 (run 39507011395, job 118976528305): `stac_client/test_cert_common_core.py::test_cert_errh_01_unknown_collection - Failed: DID NOT RAISE APIError` (1 failed, 1115 passed); the next two trunk heads fail identically (3eddae25f2 run 39526193503 job 119012520123, f854f8fac0 run 39538752619 job 119021544626). No landing in 405198df54..4ff2e7c8c2 touches STAC (#6457 CI shards, #6456 alert 402 detail, #6455 a Postgres raster test). The cause is the unpinned `pystac-client>=0.8.0` requirement: the green job installed pystac-client 0.9.0, the red job 0.9.1. pystac-client 0.9.1 changed `Client.get_collection` to return `None` when the collection request answers 404 (it still re-raises every other `APIError`). Server behaviour is unchanged. This is an upstream dependency change, so a revert does not apply; this PR fixes forward.

## Changes Made
- CERT-ERRH-01 accepts either documented client contract for an unknown collection: `APIError` with `status_code == 404` (pystac-client < 0.9.1) or `None` (0.9.1+, which returns `None` only for a 404). Any other resolved value still fails.
- Adds a transport-level assertion so the "structured pystac-client error" evidence holds on every client version: `StacApiIO(max_retries=0).request(<collection url>)` must raise `APIError` with `status_code == 404` and a non-empty message (the same pattern CERT-AUTH-01 uses).
- The RFC 7807 body checks (`status == 404`, detail names the collection) are unchanged; the evidence note records which `get_collection` contract was observed.

## Testing
- [x] Integration tests added/updated
- [x] Manual testing performed

Verification (exact commands in the staged receipt):
- Stub STAC server check of the new assertion logic: pystac-client 0.9.0 `get_collection` raises APIError 404, 0.9.1 returns None; `StacApiIO(max_retries=0).request` raises APIError 404 under both.
- Locally built Release server (`dotnet build src/Honua.Server/Honua.Server.csproj --configuration Release`), `tests/python`, `HONUA_TEST_CONFIGURATION=Release`:
  - trunk's test file + pystac-client 0.9.1: `pytest stac_client/test_cert_common_core.py -k errh_01` → 1 failed, `DID NOT RAISE APIError` at line 752 (the CI red reproduced).
  - this branch + pystac-client 0.9.1 (CI's resolved version): `pytest stac_client/test_cert_common_core.py` → 55 passed.
  - this branch + pystac-client 0.9.0 (the pinned client-compat / cng-conformance version): same file → 55 passed.
- Local runs used `--timeout 700` and `HONUA_STAC_COMPAT_TIMEOUT=600` because of host load, plus a scratch wrapper outside the repo that starts the already-built `Honua.Server.dll` in place of `dotnet run --no-build` (the lane shim queues `run` for a build slot). No repo test infrastructure was changed.

- `CHURN_DOTNET_INNER=1 scripts/ci/pre-pr-check.sh --fast` (SMART/FAST; the staged `.flow/pr-body.md` is an unclassified input, so it selected a 32-project build plus full architecture enforcement). Build with warnings as errors: 0 warnings, 0 errors; format: no changed .cs files. `Honua.Server.Tests` Tier=Fast: 4161/4164, and the 3 failures were host-load timeouts (two `RegexMatchTimeoutException`s and a 25 ms sampler-overhead bound measured at 29 ms). Rerun in isolation (`--filter` on `ClientCertificateEnforcementMiddlewareTests`, `OpsHealthRollupSamplerTests.SamplerCapture_FromTelemetry_HasBoundedOverhead`, `SpatialCorrectnessTests.CreateEnvelopeWkb_AntimeridianCrossing_GeneratesValidWkb`): 26/26 passed. The script stops at the first red project, so its remaining steps were run by hand with the same arguments: `test-projects.py gate-fast` green, Ai.UnitTests MCP governance green, Architecture.SourceTests 116/116, local architecture review skipped (no C# changed), Architecture.Tests 282/283 (see the P2 follow-up below; this is pre-existing trunk drift, not this diff).
- `CHURN_DOTNET_INNER=1` only skips the lane shim's outer churn recorder, whose argument parser rejects `-- RunConfiguration.MaxCpuCount=1`; the slot and CPU cap still apply.

## Gate Impact
- [x] PR gates (build, test, governance)

## Docs or Contract Impact
- [x] None — no docs or contract impact

## Release/Deploy Impact
- [x] None — standard merge-and-release flow

## Breaking Changes
None

## Follow-ups
- P2: trunk `Honua.Architecture.Tests` `FeatureCatalogDriftTests.CommittedCatalog_EqualsFreshlyGeneratedOutput` fails at f854f8fac0. #6456/#6465 renamed `AlertEntitlementGateTests` cases (`...ProEdition...` became `...ExpiredPai...`) and `docs/gis/data/feature-catalog.json` was last regenerated in #6450. Needs `scripts/generate-feature-catalog.sh`; outside this packet's `tests/python/**` scope.
- P3: lane tooling. `churn-governor.py` rejects the `-- RunConfiguration.MaxCpuCount=1` passthrough, so `pre-pr-check.sh` dies at its first `dotnet test` in lanes unless `CHURN_DOTNET_INNER=1` is set.
- P3: `tests/python/requirements.txt` leaves every client library unpinned (`>=`), so upstream releases can redden the trailing matrix with no trunk change. Consider a constraints file or upper bounds for the certification clients so upgrades land deliberately.


## Admin Network and Jobs packet

The original family red is already repaired on trunk by #5887 (`01449eaed0`), which mirrors migration 128 in `tests/seed/server.yaml`. This packet preserves that fix and adds endpoint regression coverage for the request binding introduced by culprit #5828 (`7a71b1ebe4`), rather than reverting it. Five landings separate the last actual green from the reported red, and the repair is already on the current base.

### Diagnosis

Read [the diagnosis comment](https://github.com/honua-io/honua-server/commit/a6581e4d332e65e1202b8d1b45956ef73b6390d7#commitcomment-205862684) and #5828 before independently reproducing the boundary. Checked run 38135401612 artifacts first: the seven-day TRX artifact has expired. Surviving job 114470650669 logs show seven failures in `NetworkTopologyPromotionAdminEndpointsTests`, with PostgreSQL SQLSTATE 42703 from `TryGetIdempotentReplayAsync`: `request_hash` is missing. The test host skips application migrations; its old seed omitted the column added by #5828.

An independent disposable PostgreSQL reproduction extracts each commit's exact seed DDL and `PromotionColumns`, then executes the replay SELECT. `96601c1`, `96dbdb2`, `722aa9a`, and `6ca7204` pass; `7a71b1e` and `a6581e4` fail with missing `request_hash`; repair `01449eaed0` and this branch pass. This is an executable SQL boundary test, not a claim that the full xUnit class ran on every historical commit.

### Changes Made

- Strengthen `Promote_SameIdempotencyKeyReplayed_ReturnsSameResultWithoutDoublePromoting` without changing its catalogued identity. It promotes successfully, changes only the expected row version under the same key, requires the specific idempotency conflict, replays the original request successfully, and confirms one history record and the expected active generation. A legacy row with no request hash would accept the changed version, so the test exercises persisted request binding through the real endpoint and store.
- Preserve the Python lane's changes and coverage above. Preserve the Core lane's independent shared WIP ref; this lane checkpoints at `wip/fix/trunkred-a6581e4-admin-network-jobs` to avoid overwriting it.

### Verification

- `python3 /home/mike/honua-io/artifacts/trunkred-a658-admin-network-jobs-bisect.py`: PASS, all eight expected SQL boundary outcomes; isolated container removed.
- Focused Release xUnit verification and required pre-PR check: in progress. The required selector resolved 32 build roots / 105 closure projects, and restore passed for the entire closure.
- The initial sandboxed restore hit NU1900 because NuGet's shared vulnerability cache was read-only. An escalated cold build then hit a WebCIL conversion memory-allocation failure in the referenced StacOpsDemo sample. Verification restarted with `DOTNET_PROCESSOR_COUNT=4` to bound SDK task concurrency; the lane shim still enforces `-maxCpuCount:4`, the build reservation, and Roslyn shared compilation. No warning, audit, analyzer, or sample-build suppression was added.

### Self-review

Reviewed this lane's diff against the PR reviewer's rubric: endpoint assertions distinguish idempotency rejection from stale-state rejection, valid replay stays covered, unique dataset IDs isolate history, and all new HTTP responses are disposed. No production API, dependency, authorization, or shared-pipeline change. Final results and any follow-ups will be recorded after verification.
