# Staged-store outage engineering rehearsal

The resilience `output-write-failure` cell uses the same native staging helper as
the crash-boundary matrix. It waits until the production GDAL worker has written
the deterministic 500-feature artifact, then hides both its actual shared-store
bytes and the attestation marker. The peer must become unready and its ordinary
artifact route must not serve bytes during the outage. After restoring the store
and restarting the topology, the job must converge naturally and expose exactly
one artifact with the unchanged independent numerical oracle and byte checksum.
The receipt retains the reached fence, outage HTTP statuses, inventories, job
states, result descriptor and pre/post checksums. Stopping Redis alone cannot
pass this output-store cell.

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
