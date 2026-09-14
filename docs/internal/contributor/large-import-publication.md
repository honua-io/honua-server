# Large imported-layer publication

GeoServices source transfer commits its imported table before automatic
publication. Publication then copies the rows into the canonical snapshot used
by tile-serving paths and registers the service. A failure in that second stage
must leave the source import available for recovery and report `NeedsReview`
with `fidelity.publication.missing`, rather than full-fidelity completion.

`LayerPublishing:MaterializationTimeoutSeconds` sets the canonical snapshot-copy
budget for PostgreSQL publication and refresh. The default is 300 seconds; the
supported range is 1–3600 seconds. The setting applies to that workload's command
and wall-clock cancellation budget. Ordinary query and discovery timeouts remain
separate. Caller cancellation and any stricter PostgreSQL statement policy still
apply. Configure the environment variable as
`LayerPublishing__MaterializationTimeoutSeconds`.

The copy stays inside the publication/refresh transaction, so timeout or
cancellation rolls back its writes. Do not bypass the copy: the current tile path
uses the canonical snapshot, while feature queries can read the source table.

After a failed publication, inspect the retained source table and catalog before
retrying. The existing admin connection layer-publication API can publish that
table without another ArcGIS download. Preserve source domains, subtypes, dates,
geometry dimensions, relationships, and style metadata when constructing the
recovery request. A publication retry alone does not prove service fidelity or
complete the original import job's reconciliation.

The full 698,843-row helicopter recovery, public query parity, restart and repeat
recovery checks are tracked by honua-server#4854. The budget and truthful verdict
fixes do not themselves constitute that qualification.
