# ADR-0022: No Transform on Write (Except Imports)

## Status
Accepted

## Context

Honua supports multiple write protocols (FeatureServer, OData, OGC Features) and stores data in PostGIS. Allowing write-time coordinate transforms introduces ambiguity about the true storage SRID, makes debugging spatial behavior harder, and can conceal upstream projection mistakes. We want a consistent rule that keeps storage SRIDs predictable, while still supporting import workflows that normalize heterogeneous datasets.

## Decision

- For non-import writes, geometry must be in the layer SRID.
- If geometry SRID is missing or zero, assume the layer SRID.
- If geometry SRID is present and differs from the layer SRID, reject the write.
- Write paths must not perform coordinate transforms.
- Imports are the sole exception: they may transform source data to the configured target/service SRID to normalize datasets at ingest.
- Read paths may still transform for output SRIDs as requested by clients.

## Consequences

### Positive
- Storage SRIDs are consistent and predictable across protocols.
- Spatial indexes and geodesic operations behave as expected.
- Projection errors surface early as validation failures.

### Negative
- Clients must pre-project data to the layer SRID before writing.
- Mismatched SRIDs result in rejected writes rather than silent correction.
- Additional validation is required at write time.
