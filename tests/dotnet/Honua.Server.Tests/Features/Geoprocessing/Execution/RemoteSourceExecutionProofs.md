# Remote source execution proofs

`WfsExecutionProofTests` executes the production `RemoteSourceExecutor` and
`WfsDagSource` against a real local HTTP fixture. Only the test transport remaps
the validated public numeric URL to the local server; the production SSRF guard
is retained. The fixture contains selected rows plus wrong-type, inactive and
out-of-bounds rows. It applies type, CQL filter and bbox, caps each page below the
requested count, and independently expects keys 11, 12, 13 with exact XYZ and
Unicode/null/boolean attributes and an Int64 identifier above 2^53. A valid
GeoJSON with a duplicated page must fail the same content oracle. Cases with and without `numberMatched` prove
termination without short-page truncation or duplication. `where` is the optional
[CQL_FILTER WFS extension](https://docs.geoserver.org/main/en/user/services/wfs/vendor/),
so the upstream WFS must support that extension when a predicate is requested.

`PostgisSourceExecutionProofTests` creates a second real PostGIS database and
registers encrypted connection credentials in the actual Honua catalog database.
The production secure-connection resolver decrypts that registration; the test
observes the call while delegating to the real implementation. The SQL fixture
is committed in the test, independently of the executor SQL: rows differ in
predicate, timestamp watermark and bbox membership. The expected keys are exactly
11, 12, 13, including the watermark boundary, with literal XYZ, CRS, numeric,
Unicode/null, boolean and timestamp assertions. The table exists only in the
external database. Reading the catalog instead, dropping a predicate, truncating
the stream or retaining the raw geometry as a scalar attribute cannot pass.

Required PR Gate runs `Category=RemoteSourceExecutionProof`, retains TRX, and
fails on missing dependencies. These are pre-cut whole-catalog GP GA correctness
proofs for #3949/#3950. Exact-candidate lifecycle qualification consumes #3848;
Postgres restart/retry/transaction registration proof remains #3855. Inline
artifacts do not exercise staged storage, so #3852 is not claimed by this suite.
No successful local operation test is counted as candidate-bound recovery proof.

## Manifest-pinned WFS qualification

`qualify-wfs-candidate.sh` starts a disposable PostGIS/Redis deployment and runs
`qualify_wfs_candidate.py` through the candidate's real workflow-publication API.
`wfs_candidate_fixture.py` serves HTTPS WFS pages on an isolated Docker bridge;
only its generated certificate is added to the candidate trust store. HTTP source
validation, TLS verification, workflow orchestration, the production executor,
and job/artifact persistence remain enabled. The server is limited to one CPU
and 1 GiB of memory. The runner removes its containers and volumes on exit.

Run from the repository root on a Docker host with ports 18449 and 18450 free:

```bash
tests/dotnet/Honua.Server.Tests/Features/Geoprocessing/Execution/qualify-wfs-candidate.sh \
  ghcr.io/honua-io/honua-server@sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1 \
  /tmp/wfs-candidate-receipt.json
```

Each receipt records observed container/image identities, the image's source
revision, resource limits, child job states, parent workflow terminal state,
exact decoded output hashes, and the independent duplicate-page rejection.

`wfs-candidate-receipt.json` is the current receipt, on the 2026.1 candidate
pinned by honua-release trunk `52cc3f2c` (#349): Native AOT index
`sha256:29974ee7b722e3ae15c3b891024e5e70800f412188aeccf5ec3d32d9dac675c1`
(`nightly-548b7a5`, source `548b7a5263da5a3f2381eb43f232687cdf92b0bf`). The
unmodified harness (SHA-256 `82f9c167…ec0c`) passes. The running container
reports that image ID, repo digest and revision label. In both scenarios the
child job succeeds and the parent workflow reaches `Succeeded` with 1/1 steps.
Paging stops at `[0,1,2,3]` without `numberMatched` and at `[0,1,2]` with it.
Both decode to 564 bytes with hash `c8224a97…258ef`, the duplicate page is
rejected, and cleanup passes.

`wfs-candidate-receipt-3c52a4b.json` keeps the earlier pass on the imaged trunk
nightly `sha256:54926040d8b543cac746289c601fb77202ef4446e00a22fa5539836e4bedc8bb`
(`nightly-aot-3c52a4b`), the first image with the tenant fix below. It gives the
same pages and output hash.

`wfs-candidate-receipt-7ba4226.json` keeps the earlier failure. Both semantic
cases passed on the then manifest-pinned Native AOT image from
`7ba422672e0c751843b17beb36e954a019cc19fb`.

**The complete workflow qualification failed on that image.** After the child
succeeds, its parent remains Running with `Job state could not be observed`:
the background orchestrator loses the persisted tenant when reconstructing its
principal, so canonical job ownership correctly denies access. The harness
asserts parent terminal success and exits nonzero; semantic success does not
turn that failure green. The runtime fix carries the durable run tenant into
observation, results, retry, and cancellation, preserving the cross-tenant denial
introduced by the admission/ownership foundations.

`WorkflowOrchestrationEngineTests.ReconcileWorkflowRun_ObservesChildInPersistedTenant_ReachesTerminalSuccess`
asserts that reconciliation observes the child and reads its results in the
persisted tenant before completing the parent. The companion
`GeoprocessingJobServiceTests.JobAccess_WorkflowReconciliation_PreservesDurableTenantAndDeniesForeignJob`
uses the production job service to check scoped access and foreign-tenant
read/cancellation denial, including a tenantless legacy run.

The imaged nightly above contains that fix and passes, but it is not yet the
release manifest's pinned candidate. The whole-catalog GP GA receipt is complete
only when this harness passes again on the cut candidate digest, which must be
built from `3c52a4bffa8f9b8621a39a8868f839e0e605de78` or later. The shared lifecycle/resilience
receipt bill in #3848 is not replaced by this operation proof. Inline artifacts
are used here, so this fixture claims neither staged-storage qualification
(#3852) nor external-PostGIS sink transaction qualification (#3855).
