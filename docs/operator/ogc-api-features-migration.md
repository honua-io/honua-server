# OGC API Features Migration Inventory

This first slice models OGC API Features source scan facts in Core without adding HTTP endpoints or import execution. Operators should treat the generated `MigrationSourceInventoryArtifact` as a planning artifact for deciding whether a source can proceed to automated import, assisted import, or manual review.

The inventory captures:

- landing page links for OpenAPI, conformance, documentation, and collections discovery
- conformance classes, including transaction and vendor-extension classifications
- collections, item endpoints, item encodings, queryables links, schema links, CRS declarations, and pagination links
- unsupported or manual-review signals for missing item endpoints, non-JSON item encodings, missing schema/queryables links, missing pagination links, unusual CRS declarations, vendor extensions, transaction conformance, and non-standard link relations

This slice does not fetch remote documents, enqueue imports, publish Honua layers, or generate parity evidence. Follow-on work should wire the planner to an authenticated scanner, page GeoJSON item collections through the canonical import pipeline, and emit manifest, parity, and readiness artifacts.

