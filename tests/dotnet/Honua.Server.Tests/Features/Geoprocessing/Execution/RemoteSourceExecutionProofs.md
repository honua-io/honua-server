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
  ghcr.io/honua-io/honua-server@sha256:dd50cd81c057e37e73a6144572abdfc90d48de314d7625c54c4ef3b6eb65b0fd \
  /tmp/wfs-candidate-receipt.json
```

The committed `wfs-candidate-receipt.json` records observed container/image
identities, the image's source revision, resource limits, child job states,
exact decoded output hashes, and the independent duplicate-page rejection.
Both semantic cases pass on the manifest-pinned Native AOT image from
`7ba422672e0c751843b17beb36e954a019cc19fb`.

**The complete workflow qualification fails on that image.** After the child
succeeds, its parent remains Running with `Job state could not be observed`:
the background orchestrator loses the persisted tenant when reconstructing its
principal, so canonical job ownership correctly denies access. The harness
asserts parent terminal success and exits nonzero; semantic success does not
turn that failure green. The runtime fix carries the durable run tenant into
observation, results, retry, and cancellation, preserving the cross-tenant denial
introduced by the admission/ownership foundations.

The release's whole-catalog GP GA promise still needs a new manifest-pinned
image containing that fix and a passing rerun. The shared lifecycle/resilience
receipt bill in #3848 is not replaced by this operation proof. Inline artifacts
are used here, so this fixture claims neither staged-storage qualification
(#3852) nor external-PostGIS sink transaction qualification (#3855).
