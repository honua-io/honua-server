# Honua SDK Native Design Vision

**Status:** Draft — for team review
**Date:** February 28, 2026
**Scope:** `@honua/sdk` native API direction — complementary to the compat layer (see [Compat Lifecycle Policy](ESRI_MIGRATION_PLATFORM_PLAN.md#compat-lifecycle-policy))

## Problem Statement

The Honua JS SDK has two layers: a Honua-native core (`HonuaClient`, surfaces, streaming pagination) and a large Esri compatibility facade (`@honua/sdk-esri-compat`). The compat layer is a durable, supported product surface (see [Compat Lifecycle Policy](ESRI_MIGRATION_PLATFORM_PLAN.md#compat-lifecycle-policy)) — it gets Esri shops migrated and stays available for as long as customers need it. Deprecation is not planned for the V1 horizon.

This document proposes design directions for the **native** SDK (`@honua/sdk`) that give customers reasons to adopt Honua-native APIs organically — not because the compat layer is going away, but because the native experience is genuinely better. New capability work lands in `@honua/sdk` first; the compat layer receives wrappers only for migration-critical patterns. The two surfaces coexist and interoperate.

## What Honua Already Does Well

Before proposing new directions, the existing native patterns worth preserving and doubling down on:

| Pattern | Where it lives | Why it matters |
|---------|---------------|----------------|
| Async generator pagination | `queryFeaturesStream()` on surfaces | Memory-efficient streaming that neither Esri nor Mapbox offer |
| Explicit protocol surfaces | `HonuaFeatureLayer` vs `HonuaOgcFeatures` vs `HonuaMapService` | Honest about what protocol is being spoken, not hidden behind class hierarchies |
| Request interceptors | `HonuaRequestInterceptor` with before/after/error lifecycle | Composable, testable, transparent — unlike Esri's implicit request handling |
| Typed request parameters | `QueryFeaturesRequest`, `ExportMapRequest`, etc. | IDE autocomplete and compile-time safety for query parameters Esri leaves undocumented |
| Migration tooling | Scanner + codemod + parity matrix + reconciliation | No other geospatial SDK ships structured migration tooling. This is a moat. |
| Declarative time binding | `TimeSlider.connectLayer()` via event bus | Component composition through events, not tight coupling |

These are not incremental improvements over Esri — they are architectural departures. The proposals below extend this trajectory.

## Lessons from Mapbox GL JS

### What Mapbox gets right

**1. The map is a document, not an object tree.**
Mapbox's Style Specification is a single JSON document that fully describes a map's sources, layers, and styling. It is portable (works in GL JS, GL Native, MapLibre), serializable, version-controllable, and hot-swappable at runtime via `map.setStyle(json)`. Esri has no equivalent — map configuration is scattered across class constructor options, property mutations, and imperative method calls.

**2. Data and presentation are decoupled.**
A Mapbox "source" is data. A "layer" is a rendering pass over that data. One GeoJSON source can back a fill layer, a line layer, and a symbol layer simultaneously. In Esri (and in our compat layer), a `FeatureLayer` fuses data source, query parameters, renderer, popup template, and labels into a single class.

**3. Styling is an expression engine, not a class hierarchy.**
Mapbox expressions (`["interpolate", ["linear"], ["get", "population"], 0, "#fff", 1000000, "#f00"]`) are declarative, serializable, evaluable per-feature, and composable. Esri's renderer model (`SimpleRenderer` → `UniqueValueRenderer` → `ClassBreaksRenderer`, each containing `Symbol` subclasses) is imperative, non-serializable without custom logic, and hard to compose.

**4. Interaction state is separate from data state.**
`map.setFeatureState({ source, id }, { hover: true })` changes how a feature looks without touching the data source. Paint expressions read `feature-state` at render time. This enables hover/select/highlight effects with zero re-queries. Esri requires either client-side graphic manipulation or re-querying to achieve the same.

**5. Style composition (v3).**
Style imports + named slots solve "where in a 200-layer basemap do I insert my layer?" Esri has no answer to this. Mapbox v3 lets you import a base style and inject layers at semantic positions (`"slot": "middle"`) rather than numeric indices.

### What Mapbox gets wrong (Honua opportunities)

- **No editing story.** Mapbox is read-only. No `applyEdits`, no transactions, no offline sync. Honua serves both REST and OGC write protocols — this is a structural advantage.
- **Proprietary post-v2.** Mapbox GL JS is no longer open source. MapLibre forked at v1. Honua's MapLibre alignment sidesteps this entirely.
- **No server-side query capability.** Mapbox pushes all spatial filtering to the client (tile filters, `queryRenderedFeatures`). Feature-rich server-side queries are a Honua Server strength.

## Lessons from CARTO

### What CARTO gets right

**1. The data warehouse is the engine.**
CARTO stores nothing. `vectorQuerySource({ sqlQuery: "SELECT ... FROM ..." })` executes arbitrary SQL in BigQuery/Snowflake/Redshift at read time. Tile generation happens inside the warehouse. This eliminates data duplication, leverages warehouse-scale compute, and keeps the freshest data always visible.

**2. SQL is the API.**
Instead of wrapping SQL behind proprietary query languages (Esri's REST query syntax) or filter expressions, CARTO exposes SQL directly. Their Analytics Toolbox adds 100+ spatial UDFs as native warehouse functions (`CARTO.ST_CLUSTERDBSCAN()`, `CARTO.H3_POLYFILL()`). Analysis runs where the data lives, not in a separate geoprocessing service.

**3. `fetchMap()` bridges design-time and runtime.**
Non-engineers design maps in CARTO Builder (no-code). Engineers call `fetchMap(mapId)` and get runnable deck.gl layers. This decouples visual design from application code and eliminates redeployment for style changes.

**4. Widget sources eliminate N+1 queries.**
A CARTO data source returns a `widgetSource` alongside tile data. Calling `widgetSource.getCategories()` or `widgetSource.getFormula()` reuses the same data connection, avoids separate roundtrips, and guarantees consistency with the map viewport.

**5. Named sources keep SQL server-side.**
Instead of embedding query logic in client code (`sqlQuery: "SELECT ... FROM sensitive_table ..."`), named sources let server admins define queries that the SDK references by name. Security boundary at the query definition, not the client bundle.

### What CARTO gets wrong (Honua opportunities)

- **Read-mostly.** CARTO's editing story is weak. Write operations go through the warehouse directly, not through CARTO. Honua's Feature Server supports full CRUD.
- **Visualization locked to deck.gl.** CARTO doesn't render — it generates data for deck.gl to render. This limits styling to what deck.gl supports. Honua can serve tiles to any renderer (MapLibre, Leaflet, OpenLayers, deck.gl).
- **Enterprise pricing and complexity.** CARTO's connection architecture requires a CARTO account, workspace setup, and managed connections. Honua can offer self-hosted simplicity.

## Proposed Design Directions

### Direction 1: HonuaMapSpec — MapLibre Style Spec Extension

**What:** A set of Honua-specific source types and metadata extensions to the [MapLibre Style Spec v8](../../contributor/adr/0002-maplibre-canonical-style.md), which is already the canonical style format per ADR-0002. HonuaMapSpec adds protocol-aware sources (Feature Service, OGC Features, MapServer) that MapLibre's built-in source types (`vector`, `raster`, `geojson`) do not cover.

**Why:** Enables version-controlled map configurations, no-code authoring tools, server-side rendering, and hot-swap at runtime. Decouples map design from application code. Building on MapLibre Style Spec (not inventing a new format) means existing MapLibre ecosystem tools (Maputnik editor, `@maplibre/maplibre-gl-style-spec` validator) work out of the box.

**Approach:** MapLibre Style Spec v8 supports [custom source types](https://maplibre.org/maplibre-style-spec/sources/) — any `type` value not built-in is treated as a custom source that a protocol plugin handles. HonuaMapSpec defines three custom source types (`honua-feature-service`, `honua-map-service`, `honua-ogc-features`) that carry Honua-specific connection metadata. All other style properties (layers, paint, layout, expressions) use standard MapLibre spec syntax unchanged.

**Sketch:**
```json
{
  "version": 8,
  "name": "Parcel Analysis",
  "sources": {
    "parcels": {
      "type": "honua-feature-service",
      "url": "https://gis.example.com/rest/services/parcels/FeatureServer/0",
      "definitionExpression": "status = 'active'"
    },
    "imagery": {
      "type": "honua-map-service",
      "url": "https://gis.example.com/rest/services/imagery/MapServer"
    },
    "boundaries": {
      "type": "honua-ogc-features",
      "url": "https://gis.example.com/ogc/collections/admin-boundaries"
    }
  },
  "layers": [
    {
      "id": "parcel-fill",
      "source": "parcels",
      "type": "fill",
      "paint": {
        "fill-color": ["step", ["get", "assessed_value"],
          "#f7fbff", 100000,
          "#6baed6", 500000,
          "#08306b"
        ],
        "fill-opacity": 0.7
      }
    }
  ]
}
```

**Key differentiator vs Mapbox:** First-class support for Feature Service and OGC sources (not just vector tiles and GeoJSON). Standard MapLibre expressions work client-side for rendering; Honua source plugins translate `definitionExpression` filters into server-side WHERE clauses for query optimization.

**Open questions:**
- How does the expression engine interact with server-side WHERE clauses? (Proposed: `definitionExpression` on the source is server-side; `filter` on the layer is client-side; a future `server-filter` property could push layer filters server-side when the source supports it.)
- What is the relationship to Esri's WebMap JSON? (Proposed: a separate import utility that converts WebMap JSON sources and renderers into HonuaMapSpec, reusing the existing GeoServices → MapLibre style converter from ADR-0002.)

### Direction 2: Source/Layer Separation in the Native API

**What:** The Honua-native API separates data sources from visual layers, following Mapbox's model but adapted for Honua's multi-protocol world.

**Why:** One Feature Service layer might need to appear as a fill, a label layer, and a highlight layer simultaneously. The current model (one `HonuaFeatureLayer` = one visual representation) forces duplication.

**Sketch:**
```typescript
const map = new HonuaMap({ style: baseStyle });

// Data source — knows how to fetch, does not know how to render
map.addSource("parcels", {
  type: "feature-service",
  service: honuaService,
  layerId: 0,
  definitionExpression: "status = 'active'",
});

// Multiple rendering passes over the same source
map.addLayer({
  id: "parcel-fill",
  source: "parcels",
  type: "fill",
  paint: { "fill-color": "#088", "fill-opacity": 0.5 },
});

map.addLayer({
  id: "parcel-labels",
  source: "parcels",
  type: "symbol",
  layout: { "text-field": ["get", "parcel_id"] },
});
```

**Key differentiator vs Mapbox:** Sources understand server-side query semantics. A `feature-service` source can push `definitionExpression` filters to the server and stream results via `queryFeaturesStream()`. Mapbox sources are dumb data pipes.

**Migration mechanics:** The existing `HonuaFeatureLayer` surface becomes a convenience wrapper around source + layer pairs — `HonuaFeatureLayer.create({ service, layerId, paint })` internally calls `addSource` + `addLayer` and returns a handle. The compat layer's `FeatureLayerCompat` continues to work unchanged (it targets the native `HonuaFeatureLayer` surface, which delegates to source/layer internals). A codemod rule will offer automated refactoring from `HonuaFeatureLayer` one-liners to explicit source/layer pairs when users want finer control.

**Phases:**
1. **Client-only (no server changes):** `addSource`/`addLayer` API on the SDK side. Sources use existing REST/OGC endpoints to fetch data. All filtering uses existing query parameters (`where`, `definitionExpression`, `outFields`).
2. **Server-enhanced:** New server-side capabilities (Direction 3 SQL pass-through, Direction 4 coordinated aggregation) add richer source types that require new endpoints. These are additive — Phase 1 sources continue to work.

### Direction 3: SQL Pass-Through for PostGIS

**What:** A `HonuaSqlSource` that lets the SDK send parameterized SQL queries to Honua Server, which executes them against PostGIS and returns features or tiles.

**Why:** This is Honua's version of CARTO's biggest strength. Honua Server already sits on PostGIS — exposing SQL-driven queries breaks free from the "everything through REST query parameters" constraint that limits both Esri and the current SDK.

**Sketch:**
```typescript
map.addSource("high-risk", {
  type: "sql",
  query: `
    SELECT geom, risk_score, parcel_id
    FROM parcels
    WHERE risk_score > $1
    AND ST_Intersects(geom, ST_MakeEnvelope($2, $3, $4, $5, 4326))
  `,
  params: [0.8],  // $1; $2-$5 auto-bound to viewport
});
```

**Key differentiator vs CARTO:** Self-hosted. No warehouse account required. Runs against the same PostGIS database Honua Server already manages. Could support named queries (server-defined, referenced by name in client code) for security-sensitive use cases.

**Security model:**

SQL pass-through does **not** mean arbitrary SQL execution. The server enforces multiple defense layers:

1. **Parameterized queries only.** The client sends a query template with `$N` placeholders and a separate `params` array. The server binds parameters using `NpgsqlParameter` — no string concatenation ever touches SQL.
2. **Tenancy and authorization boundaries.** Queries execute against a per-tenant PostgreSQL schema (or role) configured at the Honua Server level. The client cannot reference tables outside its tenant boundary. This is enforced by the database role's `GRANT`/`REVOKE` permissions, not by client-side filtering.
3. **Statement budgets.** The server sets `statement_timeout` and `work_mem` limits per query. To avoid leaking settings across pooled connections, these are scoped to a transaction block (`BEGIN; SET LOCAL statement_timeout = ...; SET LOCAL work_mem = ...; <query>; COMMIT;`). `SET LOCAL` resets automatically at transaction end, so no explicit cleanup is needed even if the connection returns to the pool after an error. Default: 30s timeout, 256MB work_mem. Configurable per service.
4. **Row limits.** Responses are capped at a configurable maximum (`maxRecordCount`, default 10,000). The `LIMIT` clause is injected server-side regardless of what the client sends.
5. **Function and table allowlists.** The server validates the query's AST (via `pg_parse` or equivalent) against a configurable allowlist of functions and tables. By default: PostGIS spatial functions + standard SQL aggregates are allowed; DDL, `COPY`, `pg_` catalog functions, and `SET` are rejected. Allowlists are defined in `appsettings.json` per service.
6. **Audit logging.** Every SQL pass-through query is logged with: tenant ID, authenticated user, query template (no param values), execution time, row count returned. This enables security review and abuse detection.
7. **Named queries (recommended for production).** Server admins define query templates in configuration. The client references them by name (`source: { type: "sql", namedQuery: "high-risk-parcels", params: [0.8] }`). The client never sees or sends the actual SQL. Named queries are defined in `appsettings.json` or via an admin API.

**Open questions:**
- Should ad-hoc SQL (non-named-query) be disabled by default in production, requiring explicit opt-in?
- What is the right granularity for function allowlists — per-service or per-role?

### Direction 4: Coordinated Widget Queries

**What:** When the SDK fetches features for a map viewport, it can simultaneously request aggregated statistics (counts, sums, category breakdowns) in the same roundtrip.

**Why:** Every Esri dashboard built on Feature Services makes N+1 queries: one for the map, one for each widget. These queries race, may see different data snapshots, and waste bandwidth. CARTO solves this with `widgetSource`. Honua should too.

**Sketch:**
```typescript
const source = map.getSource("parcels");

// Coordinated with the current viewport and filters — no extra roundtrip
const stats = await source.aggregate({
  count: true,
  sum: "assessed_value",
  categories: { column: "zoning_type", operation: "count" },
  histogram: { column: "assessed_value", bins: 10 },
});
// { count: 4521, sum: 1_847_000_000, categories: [...], histogram: [...] }
```

**Server-side support:** This requires a new Honua Server endpoint (or an extension to the existing query endpoint) that accepts multiple aggregation requests alongside a feature query and returns them atomically.

### Direction 5: Type-Safe Expression Builder

**What:** A TypeScript API for building style expressions with full autocomplete, type checking, and composability. Compiles to the same JSON expression format used by MapLibre/Mapbox.

**Why:** Mapbox expressions are powerful but error-prone (untyped JSON arrays, no IDE support). Esri's renderers are type-safe but not serializable or composable. Honua can have both.

**Sketch:**
```typescript
import { expr } from "@honua/sdk";

const fillColor = expr.step(
  expr.get("assessed_value"),
  "#f7fbff",          // default
  [100_000, "#6baed6"],
  [500_000, "#08306b"],
);

const hoverColor = expr.case(
  [expr.featureState("hover"), "#ff0"],
  fillColor,  // fallback to the step expression
);

map.addLayer({
  id: "parcel-fill",
  source: "parcels",
  type: "fill",
  paint: { "fill-color": hoverColor.toJSON() },
});
```

**Key differentiator:** Compile-time type checking catches `expr.get(123)` (wrong type) or `expr.interpolate("invalid", ...)` (wrong interpolation mode) before runtime. No other geospatial SDK offers this.

### Direction 6: Feature State for Zero-Cost Interaction

**What:** A `setFeatureState(sourceId, featureId, state)` API that changes visual properties of individual features without re-querying or re-fetching data. Style expressions read `feature-state` at render time.

**Why:** Hover highlights, selection rings, and interactive filtering currently require either client-side graphic overlays or re-querying the server. Feature state makes these O(1) operations.

**Sketch:**
```typescript
let hoveredId: string | number | undefined;

map.on("mousemove", "parcel-fill", (e) => {
  if (hoveredId !== undefined) {
    map.setFeatureState("parcels", hoveredId, { hover: false });
  }
  hoveredId = e.feature.id;
  map.setFeatureState("parcels", hoveredId, { hover: true });
});

map.on("mouseleave", "parcel-fill", () => {
  if (hoveredId !== undefined) {
    map.setFeatureState("parcels", hoveredId, { hover: false });
  }
  hoveredId = undefined;
});

// In the layer definition, the paint expression references feature-state:
// "fill-opacity": ["case", ["boolean", ["feature-state", "hover"], false], 1.0, 0.5]
```

**Key differentiator vs Esri:** Esri requires creating a separate `GraphicsLayer` with highlight graphics, or using `FeatureLayerView.highlight()` which is tightly coupled to the view lifecycle. Feature state is declarative, layer-independent, and serializable.

### Direction 7: Binary Wire Formats (Protobuf, FlatGeobuf, GeoArrow)

The SDK and server currently speak JSON exclusively for feature queries. MVT tiles are the sole binary format, generated by PostGIS `ST_AsMVT()`. There are three tiers of binary format opportunity, each at a different layer of the stack.

#### Tier A: `f=pbf` on Feature Query Responses (Esri-Compatible)

**What:** Add `f=pbf` as a supported output format on the existing `/query` REST endpoint, using Esri's published protobuf schema (`Esri/arcgis-pbf`).

**Why:** This is the highest-ROI binary format change. Esri's ArcGIS Maps SDK for JS 4.x requests `f=pbf` by default when the server advertises it in `supportedQueryFormats`. Implementing it means Esri clients that discover Honua endpoints automatically negotiate binary transfer with zero SDK changes on their side.

**Performance data:**
- ~50-60% smaller payloads vs JSON (before compression)
- ~65% further reduction with Brotli on top of PBF
- 3.9x less memory, 3.9x faster deserialization (benchmarked in Rust; JS gains are smaller but geometry parsing still wins because integer delta-encoded coordinates avoid float string parsing entirely)
- Attribute key/value interning: 10k features with the same 5 fields store each key once, not 50k times

**How it works:** All geometry coordinates are integer-only and delta-encoded. A `Transform` message carries `scale` and `translate` so clients recover world coordinates: `worldCoord = (deltaDecodedInt * scale) + translate`. Attribute `Value` uses a `oneof` pattern (string, float, double, sint32, sint64, uint64, bool, null). The schema is public at [github.com/Esri/arcgis-pbf](https://github.com/Esri/arcgis-pbf).

**Server-side:** `Google.Protobuf` NuGet package or `protobuf-net` for source-generated (AOT-compatible) serialization. Honua Server's existing `QueryFormatters` infrastructure already switches on format — adding a PBF formatter is a natural extension.

**SDK-side:** The compat layer's `HonuaClient` would negotiate `f=pbf` when available and decode using `@bufbuild/protobuf` (Buf's protobuf-es v2, the only fully conformance-tested JS protobuf library). Fallback to JSON is transparent.

**Current blocker:** `FeatureQueryValidationService.cs` explicitly rejects `f=pbf`. Removing that guard and adding a `PbfQueryFormatter` is the implementation path.

**Sketch — server response negotiation:**
```
Client: GET /rest/services/parcels/FeatureServer/0/query?where=1=1&f=pbf
Server: 200 OK
        Content-Type: application/x-protobuf
        Content-Encoding: br
        [~60% smaller than equivalent f=json response]
```

**Sketch — SDK transparent negotiation:**
```typescript
// SDK auto-negotiates binary when server supports it
const client = new HonuaClient({
  baseUrl: "https://gis.example.com",
  preferBinary: true, // default: true
});

// Caller sees the same typed response regardless of wire format
const features = await client.queryFeatures({
  serviceId: "parcels",
  layerId: 0,
  where: "status = 'active'",
});
```

#### Tier B: Connect Protocol for the Native SDK

**What:** Define Honua's native RPC surface as `.proto` files and serve them via the [Connect protocol](https://connectrpc.com/) (by Buf). Generate TypeScript client code and C# server handlers from the same schema.

**Why this is different from just adding `f=pbf`:** Tier A retrofits binary encoding onto the existing REST endpoints. Tier B rethinks the wire protocol for the Honua-native SDK (not the compat layer).

Connect protocol gives:
- **Binary protobuf over plain HTTP/1.1** — works in browsers natively via the Fetch API, no Envoy proxy, no HTTP/2 trailers hack
- **Typed contracts from `.proto` files** — one schema generates the C# server handler AND the TypeScript client. No hand-maintained type definitions that drift.
- **Server streaming** — maps directly to `queryFeaturesStream()`. The server pushes feature pages as protobuf frames over a single HTTP response. The SDK yields them as an async iterable. No hand-rolled page accumulation logic.
- **JSON fallback on the same endpoint** — `Content-Type: application/json` works too, making endpoints debuggable with curl and browser DevTools
- **No infrastructure changes** — ASP.NET Core supports Connect via `protobuf-net.Grpc` or Buf's `connect-dotnet`. The SDK uses `@connectrpc/connect-web`.

**Sketch — proto definition:**
```protobuf
syntax = "proto3";
package honua.v1;

service FeatureService {
  // Unary: single request, single response
  rpc QueryFeatures(QueryFeaturesRequest) returns (QueryFeaturesResponse);

  // Server streaming: single request, streamed response pages
  rpc QueryFeaturesStream(QueryFeaturesRequest) returns (stream FeaturePage);

  // Unary: aggregation
  rpc Aggregate(AggregateRequest) returns (AggregateResponse);
}

message QueryFeaturesRequest {
  string service_id = 1;
  int32 layer_id = 2;
  string where = 3;
  repeated string out_fields = 4;
  bool return_geometry = 5;
  optional int32 result_offset = 6;
  optional int32 result_record_count = 7;
  // ... spatial filter, order by, etc.
}

message FeaturePage {
  repeated Feature features = 1;
  bool exceeded_transfer_limit = 2;
}

message Feature {
  uint64 id = 1;
  repeated AttributeValue attributes = 2;
  Geometry geometry = 3;
}

message Geometry {
  GeometryType type = 1;
  repeated sint32 coords = 2; // delta-encoded
  Scale scale = 3;
  Translate translate = 4;
}
```

**Sketch — generated TypeScript client:**
```typescript
import { createClient } from "@connectrpc/connect";
import { createFetchTransport } from "@connectrpc/connect-web";
import { FeatureService } from "./gen/honua/v1/feature_service_connect";

const transport = createFetchTransport({
  baseUrl: "https://gis.example.com",
});
const client = createClient(FeatureService, transport);

// Unary call — typed request and response
const response = await client.queryFeatures({
  serviceId: "parcels",
  layerId: 0,
  where: "status = 'active'",
  outFields: ["parcel_id", "assessed_value"],
});

// Server streaming — async iterable of pages
for await (const page of client.queryFeaturesStream({
  serviceId: "parcels",
  layerId: 0,
  where: "1=1",
})) {
  renderBatch(page.features);
}
```

**Key insight:** The streaming RPC replaces the SDK's hand-built page accumulation loop (`queryFeaturesAll` / `queryFeaturesStream`). The server controls page boundaries and backpressure. The client just iterates. This is what `queryFeaturesStream()` should have been from the start.

**Relationship to Direction 4 (Coordinated Widget Queries):** Aggregation becomes a first-class RPC method (`Aggregate`) rather than an ad-hoc REST endpoint extension. The `.proto` schema makes the contract explicit and generates both sides.

**Contract governance:**

1. **Versioning strategy.** Proto packages are versioned (`honua.v1`, `honua.v2`). Within a major version, only backward-compatible changes are allowed: new fields (with default values), new RPC methods, new enum values. Breaking changes (removing fields, changing field types, renaming RPCs) require a new major version (`honua.v2`).
2. **REST parity guarantee.** Every `honua.v1` RPC method has a documented REST equivalent. The Connect protocol supports `Content-Type: application/json` on the same endpoints, so the REST surface shares the same request handler with a different serialization — reducing (but not eliminating) drift risk. To enforce parity, a required contract test matrix runs in CI: for each RPC method, the test suite sends the same logical request via both Connect binary and REST JSON transports and asserts identical response shapes. New RPC methods that lack a corresponding REST parity test fail the build.
3. **Compatibility commitments.** `honua.v1` endpoints are supported for the lifetime of the Honua V1 product. When `honua.v2` ships, `v1` enters a maintenance window (minimum 18 months) with bug fixes only. This mirrors the compat layer lifecycle policy.
4. **Buf Schema Registry.** Proto files are published to Buf Schema Registry (BSR) for dependency management. Breaking change detection is enforced in CI via `buf breaking` — PRs that break the published contract fail the build.
5. **Migration path.** Existing REST API consumers are not affected. The Connect endpoints are additive — they run alongside the existing Minimal API endpoints. The native SDK defaults to Connect but falls back to REST JSON when Connect is unavailable (older server versions).

#### Tier C: Cloud-Native Binary Formats for Bulk Access

Lower priority, but worth designing for:

**FlatGeobuf (`f=fgb`):** A binary format built on FlatBuffers (not protobuf) with a Hilbert R-Tree spatial index. The killer feature: HTTP range requests. A browser client reads the index from the file header, identifies which byte ranges contain the target bounding box, and fetches only those bytes. This enables spatial queries against files on S3/R2/Azure Blob with no server.

- Useful for: large reference datasets that change infrequently (parcel boundaries, census tracts, admin boundaries)
- Honua Server could generate FlatGeobuf exports as a bulk-download format
- SDK would consume via the `flatgeobuf` npm package

**PMTiles:** Wraps MVT tiles in a single file with an HTTP-range-request-friendly index. Honua already generates MVT tiles via PostGIS — packaging them as PMTiles enables serverless tile serving from object storage. The `pmtiles` npm package handles client-side range-request tile fetching. MapLibre has native PMTiles protocol support.

**GeoArrow IPC:** Apache Arrow's columnar memory layout for geospatial data. `@geoarrow/deck.gl-layers` can render Arrow binary buffers directly on the GPU without intermediate object allocation (zero GC pressure). Relevant for analytics dashboards with millions of features. A Honua Server endpoint returning `application/vnd.apache.arrow.stream` would let deck.gl render each IPC chunk as it arrives.

| Format | Use case | Protocol | Browser library |
|--------|----------|----------|----------------|
| FlatGeobuf | Bulk spatial export, serverless queries | HTTP Range requests | `flatgeobuf` npm |
| PMTiles | Serverless tile serving from S3/R2 | HTTP Range requests | `pmtiles` npm, MapLibre native |
| GeoArrow IPC | Analytics, large datasets, GPU upload | HTTP streaming | `@geoarrow/geoarrow-js`, `@geoarrow/deck.gl-layers` |

#### Binary Format Prioritization

| Tier | What | Server changes | SDK changes | Impact |
|------|------|---------------|-------------|--------|
| A. `f=pbf` | Protobuf query responses (Esri-compatible) | New `PbfQueryFormatter` | Protobuf decoder in client | Immediate: 50-60% smaller payloads, Esri client compatibility |
| B. Connect RPC | Native SDK wire protocol | New Connect endpoints alongside REST | Generated typed client | Architectural: typed streaming, eliminates hand-built pagination |
| C. Cloud-native formats | FlatGeobuf, PMTiles, GeoArrow | Export/packaging endpoints | Per-format npm packages | Bulk/analytics: serverless distribution, GPU rendering |

Tier A is additive (a new formatter on existing endpoints) and can ship independently. Tier B is the native SDK's long-term wire protocol and should be designed alongside Directions 1-4. Tier C is opportunistic — each format can be added independently when the use case arises.

## Prioritization Guidance

| Direction | Server changes needed | Lift | Impact |
|-----------|----------------------|------|--------|
| 5. Type-safe expression builder | None | Small | High DX win, immediate use |
| 6. Feature state | None (MapLibre native) | Small | High interaction quality |
| 7A. `f=pbf` query responses | New PbfQueryFormatter | Small-Medium | 50-60% smaller payloads, Esri client compat |
| 2. Source/layer separation | None | Medium | Architectural foundation |
| 1. HonuaMapSpec | None (Phase 1: uses existing endpoints) | Medium | Ecosystem enabler |
| 7B. Connect protocol for native SDK | New Connect endpoints | Medium-Large | Typed streaming, eliminates hand-built pagination |
| 4. Coordinated widget queries | New server endpoint | Large | Dashboard game-changer |
| 3. SQL pass-through | New server endpoint | Large | Architectural unlock |
| 7C. Cloud-native formats | Export/packaging endpoints | Per-format | Serverless distribution, GPU rendering |

Directions 5, 6, and 7A can ship quickly with no or minimal server changes. Directions 1 and 2 are client-side architectural work that lays the foundation for 3, 4, and 7B. Direction 7B (Connect protocol) should be designed alongside the native SDK surface (Directions 1-4) since it defines the wire contract for all of them.

## Strategic Framing

The compat layer says **"you can leave Esri."**

The native SDK should say **"here's why you'd want to."**

| | Esri | Mapbox | CARTO | Honua (proposed) |
|-|------|--------|-------|-----------------|
| **Data model** | Imperative OOP class tree | Declarative JSON style doc | SQL queries to your warehouse | Protocol-aware declarative spec with SQL escape hatch |
| **Styling** | Renderer class hierarchy | JSON expression engine (untyped) | deck.gl layer props | Type-safe expression builder compiling to MapLibre-compatible JSON |
| **Data/style coupling** | Fused (FeatureLayer owns both) | Separated (source + layer) | Separated (source + deck.gl layer) | Separated, with server-aware sources |
| **Wire format** | JSON + PBF (proprietary schema) | MVT tiles only (protobuf) | JSON + dynamic tiles | JSON + Esri-compatible PBF + Connect RPC (binary streaming) + MVT + FlatGeobuf/PMTiles |
| **Editing** | Full CRUD via Feature Service | None | Warehouse-direct only | Full CRUD via Feature Service + OGC + OData |
| **Streaming** | Manual pagination | Not applicable (tiles) | Tile streaming | Connect server-streaming RPC + async generators, protocol-transparent |
| **Analytics** | Separate geoprocessing service | Client-side (Turf.js) | Warehouse UDFs + widget sources | Coordinated widget queries + PostGIS |
| **Migration** | N/A (you're already here) | None | None | Scanner + codemod + parity matrix |
| **Offline** | Complex, proprietary | None | None | Opportunity: sync with conflict resolution |

## Next Steps

1. Review this document with the team.
2. Decide which directions to prototype first (recommendation: 5 → 6 → 7A → 2 → 1).
3. For Direction 7A (`f=pbf`): prototype a `PbfQueryFormatter` using the Esri PBF schema and benchmark against the existing JSON formatter.
4. For Direction 7B (Connect RPC): draft a `honua/v1/feature_service.proto` schema covering query, streaming query, and aggregation. Evaluate `protobuf-net.Grpc` for the server side and `@connectrpc/connect-web` for the SDK. Publish proto files to Buf Schema Registry with `buf breaking` CI checks.
5. For Directions 3 and 4, draft a paired Honua Server RFC for the new endpoints, including the SQL security model and named-query configuration format.
6. For Direction 2: draft the `HonuaFeatureLayer` → source/layer decomposition, including the convenience wrapper API and codemod rule.
