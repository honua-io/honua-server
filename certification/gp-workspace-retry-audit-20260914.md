# GP workspace retry audit

The retry repair in #4780 changes the shared dispatcher used by job-entry
operations, so the operation matrix's source digest must be reviewed again.
This audit compares runtime commit `b91eff804480d685f70f359cc2787b72fa324764`
with base `ff1a463e7d48865239bb7cb13208fc7b65c4adce`.

Only `GeoprocessingDispatchJobExecutor.cs` changed within the digest's source
roots. Catalog membership, declared entry points, individual executors and
all evidence files referenced by the 98 operation rows are unchanged. The
existing semantic assertions and their method-body digests remain intact.

For each job-entry row, the successful path still establishes the same security
scope, resolves the same workspace, invokes the same handler with the same
context, and returns its result. The changed branches terminate missing-provider
and output-collision failures without retrying, preserve retries for provider
exceptions, and propagate requested cancellation after disposing the scope.
Protocol and workflow entries retain their owning execution paths. No semantic
verdict or evidence row is promoted by this audit.

Both targeted regressions failed before the repair. The repaired worker and
dispatcher suites passed 70 tests, including transient failures, requested
cancellation, terminal callbacks and late cancellation. Six durable GP tests
also passed against real Redis, including the submit-to-terminal missing-provider
case with exactly one recorded attempt. These checks support the changed failure
branches; they do not establish successful production workspace storage.

The 98 per-operation verdicts describe the manifest's existing semantic-test
scope, not a blanket shipping or desktop claim. Production workspace/artifact
storage and positive workspace/overwrite execution remain open in #4780. The
shared runtime gaps and candidate-binding obligation are retained, as are all
native desktop retests. The refreshed source digest records this bounded impact
review; the architecture gate continues to verify the digest and evidence bodies.
