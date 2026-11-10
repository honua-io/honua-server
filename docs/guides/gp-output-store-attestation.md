---
type: reference
title: "Referenced GP output persistence"
description: "Enabling Geoprocessing:OutputStaging requires object storage or a deployment-owned persistent volume contract."
---
# Referenced GP output persistence

Enabling `Geoprocessing:OutputStaging` requires a deployment-owned persistent
volume contract. This protects the 2026.1 whole-catalog GP GA promise: a successful
job's referenced output must remain readable after server/worker replacement and
backup restore. An arbitrary directory is no longer sufficient.

Two providers are supported. `object-storage` keeps outputs in the deployment's S3
or Azure Blob file storage and is the one to use when the server runs as replaceable
tasks (ECS Fargate, Kubernetes without a shared volume); see
[Object storage provider](#object-storage-provider). The rest of this page describes
`local` on a **shared persistent filesystem**.
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

## Object storage provider

Set `Geoprocessing__OutputStaging__Enabled=true` and
`Geoprocessing__OutputStaging__Provider=object-storage`. Outputs are then written to
the bucket or container that `FileStorage` already uses (`FileStorage__Provider`
must be `AwsS3` or `AzureBlob`; `Local` is refused at startup because a local
directory is not shared between instances). No volume, marker file, `LocalRootPath`,
`PersistenceClass`, `BackupIdentity` or `ConfigurationDigest` is needed: durability
and backup are properties of the bucket and belong to the deployment.

- **Keys.** An output is one object at
  `{KeyPrefix}/{jobId}/a{attempt}/{outputName}/{fileName}` (`KeyPrefix` defaults to
  `gp/outputs`). If the file storage has its own key prefix
  (`FileStorage__AwsS3__KeyPrefix` or `FileStorage__AzureBlob__BlobPrefix`), `KeyPrefix`
  must start with it, for example `honua/gp/outputs`; startup refuses anything else,
  because retention could not list the outputs.
- **Downloads.** Results are served only through the server's artifact route, which
  checks access to the job, and never through a presigned or public URL. A range
  request is honoured and the entity tag is the output's SHA-256, as with `local`.
  The server reads the object forward to the start of a requested range, so a range
  near the end of a very large output costs a read of the bytes before it between
  the server and the bucket.
- **Retention.** The orphan sweep deletes an output once its job record has been
  removed and `OrphanRetention` has passed, unless the output was registered into a
  catalog. Read leases and retention holds are small sibling objects ending in
  `.readlease` and `.hold`; do not add a bucket lifecycle rule that deletes objects
  under `KeyPrefix` sooner than `OrphanRetention`.
- **Writes.** An output is written in one conditional request, so the server first copies it to a
  private temporary file (deleted when the write ends) and an output cannot exceed the provider's
  single-request limit (5 GiB on S3). Size the task's ephemeral storage for the largest output.
- **Readiness.** The `gp-output-store` health check reports the provider and store
  reference when the bucket can be reached.
- **Not covered.** The standalone GDAL worker does not compose the file storage
  client, so it cannot use this provider yet; use it for jobs the server runs.
