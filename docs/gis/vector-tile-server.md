# GeoServices REST — VectorTileServer

Honua exposes an Esri-compatible **VectorTileServer** adapter under
`/rest/services/{serviceId}/VectorTileServer`. It is a thin protocol adapter over the shared
vector-tile, style, and metadata pipelines — it parses Esri-shaped requests, resolves the service
by **name** against the Metadata v2 graph, and formats Esri/Mapbox-compatible responses. It does
not reimplement tile rendering, style storage, or catalog logic.

The service is read-only and anonymous by design (it inherits the service access policy; the
canonical seed service is open). Clients hydrate service metadata with `GET` or with a
`POST {"f":"json"}` (Esri clients use the latter).

## Per-operation route matrix

| Operation | Method | Route | Response | Notes |
|---|---|---|---|---|
| Service metadata | GET / POST | `/rest/services/{serviceId}/VectorTileServer` | `application/json` | Esri VectorTileServer descriptor: `tiles` template, `tileInfo` (512px WebMercatorQuad), `defaultStyles`, `tileMap`, `capabilities: TilesOnly`, `type: indexedVector`, min/max LOD, full/initial extent. |
| Tile | GET | `/rest/services/{serviceId}/VectorTileServer/tile/{z}/{y}/{x}.pbf` | `application/vnd.mapbox-vector-tile` (200), empty (204) | Mapbox Vector Tile for the service's primary tiled layer. Esri `{z}/{y}/{x}` maps directly onto the canonical `(tileCol=x, tileRow=y, zoom=z)` tuple (shared top-left WebMercatorQuad origin). Out-of-range zoom / bad coordinates → 400. |
| Default styles | GET | `/rest/services/{serviceId}/VectorTileServer/resources/styles` | `application/json` | Mapbox GL v8 style (`root.json`) composed from the layer's stored style (or a deterministic geometry-aware default). The vector source is rewritten onto this service's tile route. |
| Style resource | GET | `/rest/services/{serviceId}/VectorTileServer/resources/styles/{**resourcePath}` | `application/json` | Serves `root.json` (and the bare path). Any other style sub-resource → 404. |
| Sprite resource | GET | `/rest/services/{serviceId}/VectorTileServer/resources/sprites/{spriteResource}` | `application/json` / `image/png` | Scoped-minimal: `sprite.json` / `sprite@2x.json` → empty sprite index (`{}`); `sprite.png` / `sprite@2x.png` → 1×1 transparent PNG. Unknown sprite resource → 404. |
| Glyph range | GET | `/rest/services/{serviceId}/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf` | `application/x-protobuf` | Scoped-minimal: a single minimal Mapbox glyph stack for the default `0-255` range. Out-of-range range → 404. The fontstack is informational — any fontstack resolves to the same minimal stack. |
| TileMap | GET | `/rest/services/{serviceId}/VectorTileServer/tilemap` | `application/json` | Top-of-pyramid availability descriptor (single `1`). |
| TileMap (block) | GET | `/rest/services/{serviceId}/VectorTileServer/tilemap/{z}/{y}/{x}/{dimension}/{dimension2}` | `application/json` | Row-major availability flags for the requested `dimension × dimension2` block. Tiles overrunning the gridset edge are `0`. Levels outside the LOD scheme or absurd dimensions → 400. |

All routes resolve the service by name and return **404** for an unknown service.

## Design decisions

These decisions were ratified under epic **#1776** (VectorTileServer) and its child tickets:

- **512px WebMercatorQuad gridset.** `tileInfo` advertises a 512×512 pixel tiling scheme on the
  WebMercatorQuad tile matrix set (`wkid 102100` / `latestWkid 3857`), top-left origin, `pbf`
  format, with the standard LOD scale ladder (`559082264.0287178` at level 0, halved per level).
- **Single primary source per service.** The composed style emits exactly one vector source
  (id `esri`) whose `tiles[]` is this service's absolute tile template; the legacy TileJSON `url`
  pointer is stripped so clients fetch tiles directly. The tile and style handlers resolve the
  service's **primary** tiled publication (preferring the `EsriVectorTileLayer` publication, then
  the lowest layer index).
- **`EsriVectorTileLayer` publication type.** VectorTileServer services publish their tiled layer
  under the `EsriVectorTileLayer` Metadata v2 publication type (wire value `esri-vector-tile-layer`),
  which is the preferred publication type the adapter resolves.
- **Sprites/glyphs scoped-minimal.** Honua does not author per-service sprite sheets or glyph
  stacks. The `resources/sprites/*` and `resources/fonts/*` routes serve deterministic in-process
  stubs (empty sprite index, 1×1 transparent PNG, one minimal glyph stack for the `0-255` range).
  The composed `root.json` references `sprite`/`glyphs` **only when the style has at least one
  `symbol` layer** (the only Mapbox GL layer type that consumes a sprite or glyph stack); otherwise
  they are omitted so the served document never advertises a reference the client cannot use.

## Implementation

- Endpoints: `src/Honua.Protocols.GeoServices/VectorTileServer/VectorTileServerEndpoints*.cs`
- Style composition: `src/Honua.Protocols.GeoServices/VectorTileServer/Services/VectorTileStyleComposer.cs`
- Sprite/glyph stub assets: `src/Honua.Protocols.GeoServices/VectorTileServer/Services/VectorTileEmbeddedAssets.cs`
- Route catalog: `src/Honua.Server/EndpointRegistry.cs`
- Integration tests: `tests/dotnet/Honua.Protocols.GeoServices.Tests/Source/VectorTileServer/`
