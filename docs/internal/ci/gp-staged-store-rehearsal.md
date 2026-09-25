# Staged-store outage engineering rehearsal

The resilience `output-write-failure` cell uses the same native staging helper as
the crash-boundary matrix. It pauses at `native-process-started`, hides the actual
store marker/bytes, and releases the worker while the store remains unavailable.
The peer must return readiness 503 and a real 4xx/5xx artifact denial; failed
transport, redirects and partial-success 206 are not accepted. The failed attempt
must reach a durable queued retry with no artifact references while the worker
log records the actual store `WriteAsync` attestation failure. A generic retry is
insufficient. Only then does the harness restore the store and topology.

Natural recovery must execute a newer attempt and expose exactly one staged
artifact above the inline ceiling. Its 500-feature geometry/attribute oracle and
checksum are retained alongside the reached fence, outage HTTP statuses, failed
retry record, inventories, final job state and descriptor. Existing after-write
crash cells retain their original pre/post byte-equality assertion. Stopping
Redis alone cannot pass this output-write-failure cell.

The shared helper restores caller configuration and topology on every return.
Partial store moves are restored without overwriting an existing destination.
Failed restoration taints the harness: later live scenarios are recorded as not
executed, and final cleanup remains enabled. A pass is written only after cleanup
succeeds.

The existing **GP Qualification Harness Checks** workflow has an opt-in
`native_store_rehearsal` dispatch. It verifies the retained server image's source
provenance, builds the canonical worker Dockerfile at that exact source, and uses
a digest from a runner-local registry. It runs only the `output-store-outage`
lane's topology, actual outage and cleanup receipts. Its wrapper explicitly sets
`qualification: false`; the worker is not published as a release artifact.

This bounded hosted rehearsal does not establish whole-catalog GP qualification,
the full crash matrix, retention/hold recovery, production storage durability,
cloud failover, or the frozen release's server/worker identity. Those remain
under #3809/#3852 and their owning release producers. Missing prerequisites or
failed execution remain retained failures, not fixture-level qualification.
