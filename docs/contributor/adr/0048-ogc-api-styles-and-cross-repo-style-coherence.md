# ADR-0048: OGC API – Styles and Cross-Repo Style Coherence

## Status
Accepted (implementation phased — see "Sequencing"). Supersedes the one-style-per-layer
framing of **ADR-0002** as the *target* model; resolves **ADR-0040** open design question #8.

## Context

Honua needs to implement [OGC API – Styles](https://ogcapi.ogc.org/styles/) (Part 1: Core,
currently an OGC draft). More importantly, "styles" is not a single-surface concern — it
crosses every repo in the Honua estate, and today each surface addresses styles differently.
This ADR sets the coherent, strategically-correct target model and the phased path to it.

### What exists today (verified 2026-06-01)

- **MapLibre is canonical** (ADR-0002). SLD 1.0/1.1 and Esri `drawingInfo` are derived encodings.
  The bidirectional converters and validator already exist and are tested under
  `src/Honua.Server/Features/Styling/` (`MapLibreToSldConverter`, `SldToMapLibreConverter`,
  `MapLibreToGeoServicesConverter`, `GeoServicesToMapLibreConverter`, `MapLibreStyleNormalizer`).
- **The metadata-v2 graph models first-class styles — as type scaffolding only.**
  `MetadataV2ResourceType.Style`, `MetadataV2Resource.Style` (`MetadataV2ResourceStyle` with a
  multi-encoding container), `MetadataV2Resource.StyleResourceIds`,
  `MetadataV2GraphIndex.ResourcesByStyleResourceId`, and the `StyleResourceIds → Type=Style`
  referential check in `MetadataV2GraphValidation` are all present.
  **However: nothing produces a `Type=Style` resource, and `StyleResourceIds` is never
  populated.** The FeatureServer/MapServer metadata code that iterates `StyleResourceIds`
  iterates an always-empty list. This is dormant scaffolding, not a working catalog.
- **Style bytes are stored strictly per-layer.** `honua.layers.maplibre_style` +
  `geoservices_drawing_info` + revision columns (migrations `009`, `022`), read/written through
  `ILayerStyleCatalog.SetStyleAsync` keyed by an `int` storage-layer id. There is no
  styleId-keyed independent style store, and no way for one style to be shared by many layers.
- **The public read path is layerId-shaped**: `GET /api/styles/{layerId:int}.json`
  (`Features/Styling/StyleEndpoints.cs`), with `?theme=` transforms.
- **OGC API Maps styled-map is a stub.** `GET /ogc/maps/collections/{id}/styles/{styleId}/map`
  routes to `OgcMapsRenderingHandler.RenderStyledMapAsync`, which delegates to
  `IRasterMapRenderer.RenderStyledMapAsync`. The only registered implementation
  (`PostgresRasterMapRenderer`) throws `NotSupportedException` (501) — it renders raster
  coverages, not vector features. Vector→raster rendering lives in the Skia
  `RasterMapRenderingPipeline` (`Honua.Hosting`), which is wired to WMS/WMTS/MapServer but
  **not** to OGC API Maps. Styled vector rendering for OGC API Maps is therefore blocked on a
  renderer-dispatch question that is orthogonal to the Styles API.

### The strategic problem: identifier fragmentation

The same concept — "the style for this thing" — is addressed differently in every repo:

| Surface | Identifier today |
|---|---|
| Public read endpoint (`/api/styles/{id}.json`) | **layerId** (int) |
| honua-sdk-dotnet / honua-sdk-python admin clients | **layerId** (int) |
| honua-sdk-js runtime (`styleRefs`) | **styleId** (string) |
| honua-console (Studio map builder) | opaque per-layer **string** |
| geospatial-grpc | **no style message** — 2D style is an opaque `style_artifact` ArtifactRef |
| geospatial-mcp spec | defines `honua://styles/{style_id}` URIs that **nothing implements** |

OGC API – Styles' **`styleId`** is the natural unifying primitive, and the
`honua://styles/{styleId}` URI is already named (aspirationally) in the MCP spec.

## Decision

### 1. Adopt first-class, reusable style resources as the target model

A style is a first-class `MetadataV2ResourceType.Style` resource with a stable **`styleId`**,
decoupled from any single layer. Data resources reference styles via `StyleResourceIds`
(`[0]` = primary), so one style can render many layers. Each style resource carries N encodings
(`mapbox-style` canonical; `sld-1.0.0`, `sld-1.1.0`, `esri-drawing-info`, `esri-image-renderer`,
`3d-tiles-styling`, `x-*` derived), each either inline (`Body`) or external (`StorageBindingId`).

This is the strategically-correct end state and is what the metadata-v2 type system already
anticipates. It **supersedes ADR-0002's "one style per layer" storage framing** as the target
(ADR-0002's *canonical-format* decision — MapLibre — stands unchanged).

### 2. One identifier, one URI, one wire shape (cross-repo contract)

- **Identifier:** `styleId` everywhere. layerId-keyed paths (`/api/styles/{layerId}.json`,
  the admin `…/layers/{layerId}/style` endpoints) become back-compat aliases, not the contract.
- **Canonical URI:** `honua://styles/{styleId}` (matches the geospatial-mcp spec).
- **Canonical encoding:** MapLibre/Mapbox style. All other encodings are *derived* and produced
  only on the server, which is the single place conversion happens (no SDK-side conversion drift).
- **Wire shape:** a first-class `StyleRef` + `StyleEncoding` message in **geospatial-grpc**
  (proto-first, per the proto-ownership rule), mirroring `MetadataV2ResourceStyle`. 2D styles
  stop being opaque artifacts. 3D styling (`FeatureStyle3D` / `SceneStyle3D`) stays as-is and is
  referenced, not duplicated.

### 3. OGC API – Styles HTTP contract (stable across storage evolution)

Deployed under `/ogc/styles` (matching the other OGC API slices), plus per-collection styles.
Conformance base `http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/`:

| Endpoint | Methods | Conformance class |
|---|---|---|
| `/ogc/styles` (landing links), `/ogc/styles/conformance`, `/ogc/styles/openapi.json` | GET | `core` |
| `/ogc/styles` (styles list: `{styles:[{id,title,links[stylesheet]}], default?}`) | GET | `core` |
| `/ogc/styles/{styleId}` (the stylesheet; **content negotiation** by `Accept` selects the encoding) | GET | `core` + `mapbox-styles` / `sld-10` / `sld-11` |
| `/ogc/styles/{styleId}/metadata` | GET | `core` |
| `/ogc/styles/{styleId}` | PUT | `manage-styles` |
| `/ogc/styles` (default), `/ogc/styles/{styleId}` | PATCH / DELETE / POST | `manage-styles` |
| per-collection `…/collections/{id}/styles[/{styleId}]` | GET | links coherence with Features/Tiles/Maps |

Media types: `application/vnd.mapbox.style+json`, `application/vnd.ogc.sld+xml;version=1.0`,
`application/vnd.ogc.sld+xml;version=1.1`. Link relations: `stylesheet` (required, typed),
`self`, `alternate`, `describedby` (→ schema), `preview`,
`http://www.opengis.net/def/rel/ogc/1.0/styles`.

**Content negotiation derives on demand:** if SLD is requested but only `mapbox-style` is stored,
the server derives it via the existing converters and caches the result. The canonical store
only needs MapLibre.

### 4. Write surface, given current storage

Until the independent style catalog exists (Phase 2), a "style" is still physically bound to its
layer. Therefore the OGC `manage-styles` surface is **partial** in Phase 1:
- `PUT /ogc/styles/{styleId}` (update existing) + `style-validation` — adapts to
  `ILayerStyleService.UpdateStyleAsync`.
- `POST` (create standalone) and `DELETE` return `405`/`501` with a clear message until the
  catalog lands. We document the partial conformance honestly rather than baking layer-coupling
  into the create semantics.

### 5. Sequencing (phased)

The HTTP contract above is identical regardless of backing store, so we can ship the conformant
API before the storage is fully first-class:

- **Phase 0 (this ADR): strategy + issues.** No production code. Ratify the model, file the
  cross-repo issue set, correct the ADRs. *(Current step.)*
- **Phase 1: OGC API – Styles adapter over per-layer storage.** Implement `core`,
  `mapbox-styles`, `sld-10`, `sld-11`, `style-validation`, and partial `manage-styles` (PUT/PATCH)
  as a thin protocol adapter projecting existing per-layer styles. `styleId` is a stable
  per-layer identifier chosen to be forward-compatible with Phase 2. Upgrade collection
  `rel:"style"`/`rel:"styles"` links to point at `/ogc/styles`.
- **Phase 2: independent style catalog (data-layer epic).** New styleId-keyed style store +
  migration; a graph producer that emits `Type=Style` resources and populates `StyleResourceIds`;
  promote `manage-styles` to full POST/DELETE; enable one-style-many-layers reuse. The Phase 1
  API contract is preserved.
- **Phase 3: cross-repo rollout.** geospatial-grpc `StyleRef` (proto-first) → SDK styleId clients
  → console styleId picker → MCP `honua://styles/{styleId}` resources/tools. Mobile annotation
  styling stays intentionally orthogonal (documented, not unified).
- **Parallel: OGC API Maps styled-map for vector layers** — separate from the Styles API; tracked
  as its own issue against the renderer-dispatch architecture.

## Consequences

### Positive
- One identifier (`styleId`) and one URI (`honua://styles/{styleId}`) across all repos; the
  layerId/styleId/opaque-string fragmentation is resolved.
- The OGC API – Styles contract ships before the storage is fully first-class, and clients are
  unaffected when the store evolves underneath (Phase 1 → Phase 2).
- Server remains the single place style conversion happens; no SDK-side conversion drift.
- The dormant metadata-v2 style scaffolding gets a producer and storage, becoming real.

### Negative / costs
- Phase 1 ships *partial* `manage-styles` (PUT/PATCH only); full CRUD waits for Phase 2. This is
  disclosed in the conformance declaration and docs.
- Two style identity schemes coexist during the transition (layerId aliases + styleId). Aliases
  are retired only after Phase 2 + SDK rollout.
- Phase 2 is a real data-layer migration (new table + graph producer), not a thin adapter — it
  was previously mis-scoped as "already built" because the type scaffolding existed.

### Trade-offs accepted
- `ILayerStyleCatalog` (int storage-layer-id keyed) remains the Phase 1 store; the styleId-keyed
  catalog is additive in Phase 2 rather than a hard cutover, to keep per-layer default styling working.
- Styled vector map rendering for OGC API Maps is deliberately *not* coupled to the Styles API
  delivery.

## Related ADRs
- **ADR-0002** (MapLibre as Canonical Style Format) — canonical-format decision stands; its
  one-style-per-layer *storage* framing is superseded by the first-class model as the target.
- **ADR-0007** (Embedded Maputnik Style Editor) — the console styleId picker / editor consumes
  `/ogc/styles`.
- **ADR-0040** (Metadata v2 Canonical Graph) — this ADR resolves its open design question #8;
  the producer + storage for `Type=Style` / `StyleResourceIds` is the Phase 2 epic.

## Decision date
2026-06-01
