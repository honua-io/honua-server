# GIS Crosscutting Concerns List

Archived historical audit notes and follow-up backlog for GIS/protocol issues that were tracked during earlier hardening passes. This file is retained for traceability only and is not a current design-pattern guide.

This list tracks GIS-specific issues, assumptions, and follow-up priorities for Honua Server.
Coordinate transformation is primarily handled in PostGIS (ST_Transform). For collection spatial extents, `OgcExtentTransformer.TryTransformToCrs84()` provides in-memory transforms for WGS 84 and WebMercator variants across OGC Features, OGC Tiles, and WFS 2.0.

## Findings - High Impact
- OData geometry uses Edm.Geometry/Edm.Geography with GeoJSON payloads; confirm client compatibility (Excel/Power BI) and document the spatial contract. Evidence: src/Honua.Server/Features/OData/Services/ODataMetadataService.cs:95, src/Honua.Server/Features/OData/Services/ODataGeometryConverter.cs:21.
- OGC bbox accepts 6 values and validates Z but does not apply Z in the spatial filter, so 3D bbox filters effectively downgrade to 2D. Evidence: src/Honua.Server/Features/OgcFeatures/Services/OgcFilterProcessor.cs:603.

## Findings - Medium/Low Impact
- FeatureServer where parsing uses the ArcGIS SQL parser, but parity with full ArcGIS SQL is incomplete; document unsupported functions/edge cases. Evidence: src/Honua.Server/Features/FeatureServer/FeatureServerQueryHandler.cs:232.
- OGC conformance declares core/CRS/queryables only; filtering translation remains limited, so keep docs/tests aligned with advertised classes. Evidence: src/Honua.Server/Features/OgcFeatures/CoreEndpoints.cs:155, src/Honua.Postgres/Queries/Filters/PostgresSqlFilterTranslator.cs:327.
- CRS registry is backed by Postgres spatial_ref_sys; advertised CRS now resolves through the registry. Status: addressed. Evidence: src/Honua.Postgres/Features/Infrastructure/Crs/PostgresCrsRegistry.cs:24.
- Z/M detection is inconsistent and only checks the first coordinate despite docs claiming full sequence checks, so hasZ/hasM flags can be wrong across APIs. Evidence: src/Honua.Server/Features/Infrastructure/Services/GeometryService.cs:208, src/Honua.Server/Features/OgcFeatures/Services/OgcFeaturesGeometryServices.cs:239. Status: addressed.
- FeatureServer service metadata ObjectIdField defaults to FieldNames.ObjectId and is set in the mapper. Status: addressed. Evidence: src/Honua.Server/Features/FeatureServer/Models/FeatureServerModels.cs:96.
- OData $filter for Layers uses the shared OData expression parser. Status: addressed. Evidence: src/Honua.Server/Features/OData/Services/ODataQueryService.cs:47.
- OGC temporal extent is computed from SQL min/max when temporal fields exist. Status: addressed. Evidence: src/Honua.Server/Features/OgcFeatures/OgcFeaturesUtilities.cs:239.
- OGC Tiles collection extents now use `OgcExtentTransformer.TryTransformToCrs84()` consistent with OGC Features and WFS 2.0; unsupported CRS omits spatial extent rather than emitting non-CRS84 coordinates. Status: addressed (#573). Evidence: src/Honua.Server/Features/OgcTiles/CollectionsEndpoints.cs:229-248.
- OGC Tiles WKB rendering now reads the byte-order flag and uses endian-aware reads (`BinaryPrimitives`) for all geometry types, supporting both little-endian (NDR) and big-endian (XDR) payloads. Status: addressed (#573). Evidence: src/Honua.Server/Features/OgcTiles/TileRenderer.cs:88.
- OGC Maps requests are now classified as OGC in `ProtocolRequestClassifier.IsOgc()`, ensuring OGC-specific error formatting (RFC 7807 Problem Details with `type: "about:blank"`). Status: addressed (#573). Evidence: src/Honua.Server/Features/Infrastructure/Models/ProtocolRequestClassifier.cs:25-28.
- Data source abstraction currently binds all feature storage to Postgres; no runtime GeoPackage/cloud-native backends are wired in. Evidence: src/Honua.Postgres/Features/FeatureStore/ServiceCollectionExtensions.cs:23.

## Open Questions / Assumptions
- Is OData intended to be strict OData spatial compliance or a GeoJSON-based contract with compatible metadata?
- Should 3D bbox be supported, or explicitly rejected to avoid silent 2D downgrade?
- What is the canonical axis-order policy across FeatureServer, OGC, and OData outputs?

## Recommended Patterns
- Centralize CRS support in a registry backed by spatial_ref_sys and use it for OGC/FeatureServer/OData validation + axis order rules. Status: implemented via PostgresCrsRegistry; ensure all protocols use it consistently.
- Consolidate geometry conversion + Z/M detection into IGeometryService and route OGC/FeatureServer/OData through it to avoid divergent behavior.
- Add protocol adapters for filters: ArcGIS SQL -> internal AST, OData -> internal AST, CQL2 -> internal AST, with shared SQL translation.
- Implement request-side CRS handling for transactions (Content-Crs / input CRS) with explicit reprojection and axis-order normalization instead of strict SRID equality.
- Define a CRS-aware precision/tolerance policy (rounding, simplification thresholds) and apply consistently across GeoJSON and EsriJSON.

## Priority Order
1. Decide OData spatial contract and validate with real clients (Excel/Power BI) so metadata and payloads match expectations.
2. Decide 3D bbox behavior (support vs reject) and document it to avoid silent 2D downgrade.
3. Normalize axis-order and CRS handling across protocols for reads/writes.
4. Document FeatureServer SQL and OGC filter limitations in API docs/tests.

## Prioritized Follow-ups (P0-P3)
- P0: Validate OData spatial payloads and metadata with Excel/Power BI; adjust contract or documentation accordingly.
- P1: Decide 3D bbox behavior and centralize bbox parsing/normalization (axis order, antimeridian, 2D/3D) across OGC, FeatureServer, and OData.
- P1: Normalize CRS axis-order handling across protocols for read/write paths.
- P1: Fix tile cache invalidation for FeatureServer by evicting MVT tiles on edits; decide on empty-tile response semantics and document them.
- P2: Expand filter translation coverage (FeatureServer/OData/OGC) or document unsupported functions explicitly.
- P3: Expand tiles beyond WebMercator/MVT (additional TileMatrixSets, raster formats) and plan for multi-backend providers and style storage; wire in coordinate precision/tolerance enforcement using GeometryValidationOptions and CRS-specific precision.
