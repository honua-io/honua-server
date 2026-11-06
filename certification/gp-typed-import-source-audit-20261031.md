# Geoprocessing typed import source audit

The change to the geoprocessing operation source roots is confined to
`ImportDatasetJobExecutor.cs`. The `import.dataset` executor now includes the
importer's promoted source fields in the publish request, while retaining the
primary key and JSON properties field. Import, job admission, progress,
provenance, extent refresh, and the other operations remain unchanged.

The existing `ExecuteAsync_StagedGeoJson_ImportsFlattensRefreshesAndRecordsProvenance`
integration proof passed against PostGIS on this branch (1 passed, no skips).
The published-layer integration tests additionally exercise typed attributes
through OGC API Features, FeatureServer, OData, and MCP, and verify the legacy
JSON-only layer. The existing `import.dataset` evidence row and the remaining
operation verdicts are retained.
