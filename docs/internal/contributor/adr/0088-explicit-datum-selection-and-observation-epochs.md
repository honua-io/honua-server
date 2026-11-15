# ADR 0088: Explicit datum selection and observation epochs

## Status

Accepted

## Context

The GeoETL runtime boundary in [ADR 0038](0038-geoetl-pipeline-architecture-and-runtime-boundary.md)
places vector transformations in PostGIS. Query, GeometryServer project and file
import must agree on curated datum-operation selection. Timed operations also
need an observation epoch; silently using a reference epoch changes coordinates.

## Decision

Keep operation definitions and epoch requirements in the shared datum catalog.
Bind a finite observation epoch into an immutable selection before generating
SQL or inverting a pipeline. Protocol adapters validate selectors and return
client errors for missing required epochs. Import adapters persist the selector
and epoch in durable job parameters and execute through the existing PostGIS
pipeline path. Explicit imports reject rows with conflicting source SRIDs.

Retain the existing null default operations. Non-identity realization operations
require explicit selection. The ITRF2014/NAD83(2011) pair always requires an epoch.
A curated composite without an authority code uses a Honua name, with its
component provenance documented in the compatibility reference.

## Consequences

PostGIS remains the transformation runtime and no second projection engine is
introduced. Callers must supply the observation time for timed operations. This
bounded support does not introduce vertical transformations, general coordinate
velocity propagation or arbitrary client-supplied pipelines.
