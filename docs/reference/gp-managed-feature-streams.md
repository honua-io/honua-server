---
type: reference
title: "Managed GP feature streams"
description: "Shared staging, immutable receipts and operational requirements for large managed GeoETL outputs."
---
# Managed feature streams

Managed GeoETL source and transform outputs remain inline below the smaller of 1 MiB and the configured `Geoprocessing:OutputStaging:MaxInlineArtifactBytes` ceiling, measured in serialized feature bytes. Larger outputs require `Geoprocessing:OutputStaging` and its existing shared persistent-volume contract. Configure the same store reference, key prefix and attested persistent mount on every worker and API replica. Startup/readiness validates the configured mount identity, configuration digest and backup membership. A local temporary directory is not a supported shared store.

The dispatcher stages NDJSON under a server-owned job/attempt key and records an opaque `honua-feature-stream:v2` receipt containing the store identity, byte count, feature count and SHA-256. Producer scratch files are deleted after staging. A downstream managed step and OGC Processes value/raw result delivery use the same store reader; they verify the published receipt against the current producer attempt and submitter/tenant identity before yielding data, then verify all bytes before returning features. The existing sweeper retains referenced objects and honors read leases.

Promotion into a persistent or saved-layer workspace creates a receipt naming that exact workspace artifact. After the producer job expires, reads require the exact persisted receipt in an active, unexpired durable workspace owned by the authorized submitter and tenant. Ordinary artifact registration cannot supply managed stream receipts or reserved server publication metadata. An indexed workspace-object reference lookup keeps published and promoted bytes until canonical workspace cleanup deletes their reference rows; it does not create an indefinite object hold. Deployment includes migration132 for that index.

Limits are 256 MiB per stream and 8 MiB per serialized feature row. OGC response materialization additionally honors the configured artifact response limit. If staging is disabled or unavailable, large managed output fails explicitly. Small inline outputs retain their existing representation.

Legacy node-local `honua-feature-stream:v1` file references are rejected by managed dispatch and OGC result delivery. Existing results using those references must be rerun after shared staging is configured. The legacy codec remains available only to isolated executor tests; production dispatch always installs the trusted storage context.

The regression suite `DurableFeatureStreamTests` uses separate store clients and scratch roots, removes producer scratch before downstream and OGC reads, and checks owner/tenant, stale-attempt, corruption, missing attestation and interrupted enumeration failures. These are shared-volume contract tests, not live cloud or multi-host deployment qualification.
