# GIS Crosscutting Concerns List

This list tracks GIS-specific issues, assumptions, and follow-up priorities for Honua Server.
Coordinate transformation is currently handled in PostGIS (ST_Transform) rather than in-memory.

## Findings - High Impact
- OData geometry is modeled as Edm.Binary and serialized as Base64 WKB, which is non-standard for OData spatial types and breaks geo.* functions and standard clients. Evidence: src/Honua.Server/Features/OData/Services/ODataMetadataService.cs:103, src/Honua.Server/Features/OData/ODataQueryHandler.cs:214.
- FeatureServer where is parsed as CQL2 text rather than ArcGIS SQL, so many valid FeatureServer queries fail or behave differently. Evidence: src/Honua.Server/Features/FeatureServer/FeatureServerQueryHandler.cs:161.
- OGC collections advertise the layer storage CRS, but CRS resolution only allows a small EPSG subset, so advertised CRS can still be rejected in crs/bbox-crs/filter-crs. Evidence: src/Honua.Server/Features/OgcFeatures/OgcFeaturesUtilities.cs:93, src/Honua.Server/Features/OgcFeatures/Services/OgcCrsResolver.cs:223.
- OGC conformance declares filter classes (simple-cql, cql-text, etc.) but SQL translation only supports a limited function set, so conformance is overstated. Evidence: src/Honua.Server/Features/OgcFeatures/CoreEndpoints.cs:140, src/Honua.Postgres/Queries/Filters/PostgresSqlFilterTranslator.cs:327.
- OGC bbox accepts 6 values but ignores Z, so 3D bbox filters silently downgrade to 2D. Evidence: src/Honua.Server/Features/OgcFeatures/Services/OgcFilterProcessor.cs:587.
- FeatureServer timeRelation is ignored; temporal filtering always uses the first temporal field with simple start/end logic. Evidence: src/Honua.Server/Features/FeatureServer/FeatureServerQueryHandler.cs:604.

## Findings - Medium/Low Impact
- Z/M detection is inconsistent and only checks the first coordinate despite docs claiming full sequence checks, so hasZ/hasM flags can be wrong across APIs. Evidence: src/Honua.Server/Features/Infrastructure/Services/GeometryService.cs:208, src/Honua.Server/Features/OgcFeatures/Services/OgcFeaturesGeometryServices.cs:239. Status: addressed.
- FeatureServer service metadata ObjectIdField defaults to the literal string "DatabaseSchema.ObjectIdColumn" and is never set in the mapper. Evidence: src/Honua.Server/Features/FeatureServer/Models/FeatureServerModels.cs:95, src/Honua.Server/Features/FeatureServer/FeatureServerUtilities.cs:44.
- OData $filter for Layers is a regex-based name equality check, not a real OData expression parser. Evidence: src/Honua.Server/Features/OData/Services/ODataQueryService.cs:73.
- OGC temporal extent is always [null, null], so collections never advertise real temporal coverage. Evidence: src/Honua.Server/Features/OgcFeatures/OgcFeaturesUtilities.cs:231. Status: addressed.
- Data source abstraction currently binds all feature storage to Postgres; no runtime GeoPackage/cloud-native backends are wired in. Evidence: src/Honua.Postgres/Features/FeatureStore/ServiceCollectionExtensions.cs:23.

## Open Questions / Assumptions
- Is the limited EPSG list intentional for MVP (if so, collections should avoid advertising unsupported CRS)?
- Is OData meant to be a custom contract rather than strict OData spatial compliance?
- Is using CQL2 for FeatureServer where a deliberate temporary simplification?

## Recommended Patterns
- Centralize CRS support in a registry backed by spatial_ref_sys (or cached EPSG catalog) and use it for OGC/FeatureServer/OData validation + axis order rules.
- Consolidate geometry conversion + Z/M detection into IGeometryService and route OGC/FeatureServer/OData through it to avoid divergent behavior.
- Add protocol adapters for filters: ArcGIS SQL -> internal AST, OData -> internal AST, CQL2 -> internal AST, with shared SQL translation.
- Implement request-side CRS handling for transactions (Content-Crs / input CRS) with explicit reprojection and axis-order normalization.
- Define a CRS-aware precision/tolerance policy (rounding, simplification thresholds) and apply consistently across GeoJSON/GML/EsriJSON.

## Priority Order
1. Decide OData contract: implement true OData spatial types + payloads or document/rename it as a custom API and align metadata accordingly.
2. Replace FeatureServer where parsing with ArcGIS SQL compatibility (or explicitly limit it and update conformance docs/tests).
3. Fix OGC CRS advertisement/validation mismatch by expanding CRS support or restricting advertised CRS to what is actually accepted.
4. Close temporal + bbox gaps (timeRelation support, 3D bbox handling, temporal extent computation) and align Z/M detection.

## Prioritized Follow-ups (P0-P3)
- P0: Introduce a shared CRS/axis-order pipeline (e.g., ICrsRegistry + IGeometryTransformer) and apply it to OGC Features writes; align metadata with the actual supported CRS set.
- P1: Centralize bbox parsing/normalization (axis order, antimeridian, 2D/3D) and reuse it across OGC, FeatureServer, and OData to remove behavioral drift.
- P1: Fix tile cache invalidation for FeatureServer by evicting mvt-tiles on edits; decide on empty-tile response semantics and document them.
- P2: Implement temporal completeness (FeatureServer timeRelation semantics and real temporal extents via SQL min/max).
- P3: Expand tiles beyond WebMercator/MVT (additional TileMatrixSets, raster formats) and plan for multi-backend providers and style storage; wire in coordinate precision/tolerance enforcement using GeometryValidationOptions and CRS-specific precision.
