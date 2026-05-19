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

## Protocol Correctness Notes

### Collection Spatial Extents (CRS84)

The OGC Tiles spec requires collection spatial extents to use CRS84 coordinates. Honua transforms extents from the layer's native CRS to CRS84 using an in-memory transformer that supports WGS 84 (EPSG:4326) and WebMercator variants (EPSG:3857, 900913, 102100, 102113, 3785). This matches the pattern used by OGC API Features and WFS 2.0 collections.

When a layer uses a CRS that cannot be reliably transformed in-memory, the spatial extent is **omitted** rather than emitted with non-CRS84 coordinates. Clients should handle absent spatial extents gracefully.

### WKB Byte Order

The tile renderer reads WKB geometry payloads with endian awareness. Both little-endian (NDR, byte-order flag `0x01`) and big-endian (XDR, byte-order flag `0x00`) WKB payloads are supported. This applies to all geometry types rendered to raster tiles (Point, LineString, Polygon, and their Multi- variants).

## MVP Limitations

- Coverage depends on available collection metadata and matrix-set configuration.
- Full standards parity can vary by optional conformance class.
- Compatibility is continuously validated via CITE workflows; see contributor docs for operational detail.

## Validation and References

- [OGC API Tiles CITE Guide](../../contributor/cite-runbook.md#ogc-api-tiles)
- [Interactive API Specs](../../developer/api-specs/README.md)
- [Geospatial APIs Overview](../STANDARDS_APIS.md)
