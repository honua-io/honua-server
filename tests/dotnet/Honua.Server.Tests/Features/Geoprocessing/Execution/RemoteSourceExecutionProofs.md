# Remote source execution proofs

`WfsExecutionProofTests` executes the production `RemoteSourceExecutor` and
`WfsDagSource` against a real local HTTP fixture. Only the test transport remaps
the validated public numeric URL to the local server; the production SSRF guard
is retained. The fixture contains selected rows plus wrong-type, inactive and
out-of-bounds rows. It applies type, CQL filter and bbox, caps each page below the
requested count, and independently expects keys 11, 12, 13 with exact XYZ and
Unicode/null/boolean attributes. Cases with and without `numberMatched` prove
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
