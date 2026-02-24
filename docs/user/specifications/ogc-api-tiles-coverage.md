# OGC API Tiles Coverage (MVP)

This page summarizes Honua MVP support for OGC API Tiles.

## Endpoint Coverage

| Capability | Status | Notes |
|---|---|---|
| Landing page (`/ogc/tiles`) | Implemented | Metadata and links for tiles API root |
| Conformance (`/ogc/tiles/conformance`) | Implemented | Declares implemented conformance classes |
| Collections (`/ogc/tiles/collections`) | Implemented | Collection discovery and metadata |
| Tile matrix sets (`/ogc/tiles/tileMatrixSets`) | Implemented | Matrix set listing and metadata |
| Tilesets listing (`/ogc/tiles/tiles`) | Implemented | Global tileset discovery |
| Collection tilesets (`/ogc/tiles/collections/{id}/tiles`) | Implemented | Per-collection tileset metadata |
| Tile retrieval | Implemented | Vector/raster responses by collection/tileset/tile matrix |
| OpenAPI (`/ogc/tiles/openapi.json`) | Implemented | OpenAPI description for tiles API |

## MVP Limitations

- Coverage depends on available collection metadata and matrix-set configuration.
- Full standards parity can vary by optional conformance class.
- Compatibility is continuously validated via CITE workflows; see contributor docs for operational detail.

## Validation and References

- [OGC API Tiles CITE Guide](../../contributor/cite-tiles-conformance-testing.md)
- [Interactive API Specs](../../api-specs/README.md)
- [Geospatial APIs Overview](../STANDARDS_APIS.md)
