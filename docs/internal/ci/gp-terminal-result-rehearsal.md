# Terminal-CAS worker crash and result-package recovery rehearsal

Related to #3849, #3852 and #3809. This is an engineering rehearsal, not a
candidate certificate or a complete crash-consistency matrix.

Dispatch `gp-qualification-harness-checks.yml` with `native_store_rehearsal=true`
and `native_rehearsal_lane=terminal-result-recovery`. It reuses the attested,
digest-pinned server and the canonical worker built from its exact source into
the hosted runner's local registry. No cloud deployment or public worker
publication occurs. The wrapper requires exactly three passing receipts:
topology, terminal-result-recovery, and cleanup, with `qualification:false`.

The 500-point `gdal.ogr2ogr` fixture exceeds the configured inline ceiling.
An external RESP proxy withholds the exact successful `:1` acknowledgement for
an explicitly armed terminal CAS. The fence's entire terminal record must match
the actual Redis record, with one artifact and no result package. The harness
SIGKILLs the worker and retains its actual exit code 137 (not OOM), logs and
container identity before replacing only that worker with the same image.
Server, peer, Redis and PostgreSQL container identities and start times must
remain unchanged during the proof.

After replacement the package must still be absent. An ordinary authenticated
OGC results read then synthesizes and persists the package through
`GeoprocessingJobArtifactService.GetOrSynthesizeResultPackageAsync`. Its ID must
match the terminal job/version and contain exactly one completed artifact. Its
canonical same-job content route, checksum and size must match the committed
staged descriptor; the OGC href must bind that same artifact and serving origin. Repeated reads
after 65 seconds must preserve the full persisted package and public descriptor.
The entire terminal job record is compared throughout that window; attempt,
version, progress, warnings and spec cannot silently change. Exactly one output
object remains. Durable pre-crash bytes and authenticated post-recovery bytes
are retained as nonhidden GeoJSON files, compared byte-for-byte, hashed and
checked against independent expected IDs and coordinates. The download uses
the validated recovered package URI, and its bytes must match package metadata.

Raw fence, killed/replacement container inspect records, untouched-service
identities, full terminal/package documents, descriptors, inventories and worker
logs accompany the receipt. Caller topology restoration happens after the
proof. A restoration failure fails the scenario and taints later live scenarios;
the final cleanup still runs. Redis access by the harness is read-only.

This proves worker death at the terminal acknowledgement seam and normal
read-path result-package recovery for this staged native fixture. It does not
prove queue/index repair, all terminal callbacks, raster catalog registration,
database transaction effects, retention expiry, other storage providers,
published candidate worker provenance, or whole-catalog qualification. Those
remain in the parent issues and release evidence requirements.
