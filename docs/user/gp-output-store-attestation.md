# Referenced GP output persistence

Enabling `Geoprocessing:OutputStaging` requires a deployment-owned persistent
volume contract. This protects the 2026.1 whole-catalog GP GA promise: a successful
job's referenced output must remain readable after server/worker replacement and
backup restore. An arbitrary directory is no longer sufficient.

The only supported provider is `local` on a **shared persistent filesystem**.
Every serving host, local worker, and independently scheduled GDAL worker must
mount the same volume. Container writable layers, `emptyDir`, temporary directories,
and unbacked host paths are not supported production staging stores. Existing
deployments with staging disabled need no changes.

## Provisioning and topology inventory

1. Allocate the shared persistent volume outside the server/worker lifecycle.
   Record its opaque store reference in the topology inventory and in a real
   backup policy covering its bytes, volume marker, job records, registration and
   retention state. Record the policy's opaque backup/restore identity.
2. Mount that volume on every producer and consumer. Provision its root once using
   `scripts/operations/Initialize-GpOutputStore.ps1` (Windows hosts) or
   `scripts/operations/initialize-gp-output-store.sh` (Linux hosts, including
   container-mounted volumes) with the root path, store reference,
   `shared-persistent` persistence class, backup identity and backup store
   references. Both emit the same canonical form, so either may provision a volume
   the other's hosts read. A topology that tunes the key prefix, inline ceiling,
   read lease, sweep or retention settings must pass those same values here: they
   are part of the digest, so provisioning with defaults would attest a contract no
   host resolves. The scripts refuse an absent directory, a store outside the backup
   set, or an existing marker. They write `.honua-gp-store.json`, print the
   configuration digest, and the PowerShell script also emits the complete
   application configuration section as JSON. Keep that marker out of
   application-generated initialization and restore it with the volume.
3. Distribute the emitted configuration to **all** servers and workers. Only
   `LocalRootPath` may differ. The deployment inventory, not a per-pod template,
   owns the common digest. Mounting an empty replacement volume must fail startup.
   Environment variables use `Geoprocessing__OutputStaging__` followed by the field
   name; backup inventory entries use `BackupStoreReferences__0`, `__1`, etc.
4. Keep the marker under deployment access control. Its presence is an explicit
   operator attestation, not hardware inspection or a substitute for a successful
   backup rehearsal. Never copy it into a container image or synthesize it in an
   application startup hook. A falsely attested ephemeral mount cannot be detected
   from identical bytes alone.

The marker has exactly the evidence fields `Provider`, `StoreReference`,
`ConfigurationDigest`, `PersistenceClass`, and `BackupIdentity`. Identifiers cannot
contain paths, URIs or credentials. `PersistenceClass` must be `shared-persistent`.
`BackupStoreReferences` must include the current store reference.

The v1 digest is lowercase SHA-256 of UTF-8 fields separated by LF with no trailing
LF: `honua-gp-store-v1`, normalized provider `local`, store reference, persistence
class, backup identity, ordinal-sorted backup references joined by comma, key
prefix, inline byte ceiling, read-lease ticks, sweep-interval ticks, sweep-grace
ticks, orphan-retention ticks. Numeric values are invariant decimal .NET ticks
(10,000,000 per second). Mount paths and credentials are excluded. The public
`GeoprocessingOutputStoreAttestation.Create` method computes the same contract for
deployment tooling with non-default retention settings. Updating policy requires
coordinated marker/configuration replacement; a mixed rollout fails closed.

## Runtime evidence and recovery

Startup validates the mounted marker against the host's computed and declared
digests. Reads, writes, retention and cleanup operations recheck the marker; the
runtime never creates a missing root or marker. `/healthz/ready` returns 503 when
the contract disappears or changes. The registered `gp-output-store` health check
exposes credential-free provider, store reference, configuration digest, persistence
class and backup identity. The authenticated operator health snapshot includes
these fields under `health.entries[].outputStoreAttestation` for that check.
The server and headless worker also emit these five fields in the structured
`GP output store attestation` startup log, so every host can contribute evidence.

Back up the complete volume and the durable job/descriptor records. Restore them
into an isolated recovery topology, using the original store reference and marker,
before starting replacements. Force at least one output above the configured inline
ceiling, preserve its descriptor, then read it through
`GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content` with normal
authorization. Assert the descriptor identity, media type, length and independently
expected SHA-256; record readiness and the runtime attestation after recovery.
Restoring only Redis/Postgres is insufficient when referenced staging is enabled.

## Qualification receipts (#3900 and #3852)

Both issues use this same contract, including `ProductionWorkerContainerHandoffTests`
and its production-worker environment. Enumerate every topology enabling staging
and every producer/consumer in each topology. Receipts must include image digests,
topology/host inventory, each host's runtime attestation, backing volume identity,
backup artifact/policy identity, descriptor, independent expected checksum, checksum
from the authenticated server content path, replacement/restore timestamps and
the result. Reject missing hosts, differing digests, omitted backup stores, and
missing restored outputs. Do not generalize evidence to untested topologies.

Pre-cut regressions cover unattested-directory rejection, identity/policy drift,
mount loss, replacement host instances, and a restored volume read through the
server route after the source volume is removed. The restore fixture is deterministic
storage payload data, not a raster algorithm correctness receipt.

For the pre-cut deployment proof alone, run `scripts/qualification/gp-lifecycle-harness.sh`
with `HONUA_GP_LANE=output-store`, digest-addressed `HONUA_SERVER_IMAGE` and
`HONUA_WORKER_IMAGE`, and their common full revision in `HONUA_GP_SOURCE_SHA`.
This lane declares exactly topology, output-store-attestation and cleanup; it does
not certify the full lifecycle matrix. The replacement scenario also remains in
the lifecycle lane. It decodes the forced-staged GeoJSON and checks all 500 feature
IDs and two-dimensional WGS84 coordinates against an independent integer-grid
oracle before comparing bytes and descriptors across replacement.
The [2026-09-08 pre-cut execution receipt](../internal/evidence/gp-output-store-replacement-2026-09-08.md)
records the tested image pair, topology, independent expectation and remaining
candidate qualification.

`GeoprocessingOutputStoreTopologyTests` enumerates the checked-in topology
denominator on every build: each Compose service that enables staging must bind a
complete store contract, its declared digest is recomputed from that topology's own
values, and all hosts in a file must resolve one store identity. The deployment
lane adds the `output-store-attestation` scenario, which provisions the shared
volume from those same declarations, proves an existing but unattested mount is
rejected without being self-provisioned, and reads a staged artifact back through
`GET /api/geoprocessing/jobs/{jobId}/artifacts/{artifactIndex}/content` on a peer
host after every server and worker container is replaced, asserting an unchanged
descriptor and byte checksum.

Exact-candidate #3852 crash-boundary evidence and the signed release/DR receipt must
be rerun against the cut server/worker images. The release decision record on
2026-09-05 states that no candidate digest exists yet; these local regressions do
not claim candidate qualification or a completed production backup-policy rehearsal.
