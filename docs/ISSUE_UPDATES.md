# GitHub Issue Updates

This document contains new issues to create and updates to existing issues to improve architecture/design guidance.

---

## New Issues to Create

### Issue: Admin UI wireframes and UX design

**Labels:** `enhancement`, `design`, `phase-4`

```markdown
## Summary
Design wireframes for all Admin UI screens before implementation begins. This is a **prerequisite** for Admin UI development issues.

## Why Wireframes First
- Validate UX flows before writing code
- Identify component reuse opportunities
- Align on terminology and navigation
- Reduce rework from design changes mid-implementation

## Deliverables

### Screen Wireframes (low-fidelity)
- [ ] **Dashboard** - Service health overview, quick stats
- [ ] **Connections** - List, add, edit, test database connections
- [ ] **Tables** - Browse discovered tables, view schema
- [ ] **Layers** - List published layers, enable/disable, configure
- [ ] **Publish Wizard** - Step-by-step layer publishing flow
- [ ] **Import Wizard** - File upload, schema preview, CRS selection, import progress
- [ ] **Map Preview** - Layer preview with MapLibre, feature popup
- [ ] **Style Editor** - Maputnik integration, save/load styles
- [ ] **Settings** - OIDC config, general settings

### UX Flows
- [ ] Connection → Discovery → Publish flow
- [ ] File Import → Preview → Publish flow
- [ ] Layer enable/disable flow
- [ ] Style editing workflow

### Component Inventory
- [ ] List common UI patterns (tables, forms, wizards, modals)
- [ ] Define shared component library needs (MudBlazor components to use)
- [ ] Identify MapLibre integration points

## Tools
Recommend using Excalidraw, Figma, or hand-drawn sketches. Fidelity matters less than coverage.

## Acceptance Criteria
- All screens have wireframes reviewed and approved
- Major UX flows documented
- Component patterns identified for reuse
- Wireframes checked into `docs/wireframes/` (images or Excalidraw files)

## Blocking
This issue **blocks** the following Admin UI implementation issues:
- #25 Blazor WASM admin project setup
- #26 PostGIS connection management UI
- #27 Layer publishing from PostGIS tables
- #28 File import
- #29 Esri Service Import Wizard
- #30 Embedded Maputnik style editor
- #42 Health dashboard in admin UI
- #43 Map preview with MapLibre

## Phase
Phase 4 (Admin UI) - but should be completed **before** Phase 4 coding begins
```

---

### Issue: Admin UI Playwright integration tests

**Labels:** `enhancement`, `testing`, `phase-4`

```markdown
## Summary
Implement Playwright end-to-end tests for Admin UI critical flows.

## Context
See ADR-0010 for Admin UI testing strategy. Playwright tests complement bUnit component tests.

## Test Scenarios

### Connection Management
- [ ] Add new connection with valid credentials
- [ ] Test connection shows success/failure feedback
- [ ] Edit existing connection
- [ ] Delete connection with confirmation

### Layer Publishing
- [ ] Browse tables from connection
- [ ] Select table and configure layer
- [ ] Publish layer successfully
- [ ] Verify layer appears in FeatureServer

### File Import
- [ ] Upload GeoJSON file
- [ ] Preview schema and sample data
- [ ] Configure CRS if needed
- [ ] Complete import and verify table created

### Map Preview
- [ ] Load published layer in map
- [ ] Pan and zoom functionality
- [ ] Click feature shows popup

## Technical Setup
- [ ] Create `Honua.Admin.Playwright` test project
- [ ] Configure Playwright with WebApplicationFactory
- [ ] Set up test database with fixtures
- [ ] Add to CI pipeline (headless Chrome)

## Reference
- ADR-0010: Admin UI Architecture
- Playwright .NET docs: https://playwright.dev/dotnet/

## Acceptance Criteria
- Critical paths have Playwright coverage
- Tests run in CI (GitHub Actions)
- Test failures produce screenshots/traces
- Tests isolated with fresh database per run
```

---

## Updates to Existing Issues

### Issue #1: Database schema v1 with DbUp migrations

**Add to description:**

```markdown
## Schema Design

### Core Tables (from MVP_PLAN.md)

```sql
-- migrations/001_create_metadata_tables.sql
CREATE SCHEMA IF NOT EXISTS honua;

CREATE TABLE honua.services (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    description TEXT,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    updated_at TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE honua.layers (
    id SERIAL PRIMARY KEY,
    service_id TEXT NOT NULL REFERENCES honua.services(id) ON DELETE CASCADE,
    layer_index INT NOT NULL,
    name TEXT NOT NULL,
    description TEXT,
    table_name TEXT NOT NULL,
    geometry_field TEXT NOT NULL DEFAULT 'geom',
    object_id_field TEXT NOT NULL DEFAULT 'id',
    srid INT NOT NULL DEFAULT 4326,
    geometry_type TEXT NOT NULL,
    extent_xmin DOUBLE PRECISION,
    extent_ymin DOUBLE PRECISION,
    extent_xmax DOUBLE PRECISION,
    extent_ymax DOUBLE PRECISION,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    mvt_min_zoom INT DEFAULT 0,
    mvt_max_zoom INT DEFAULT 22,
    mvt_max_features INT DEFAULT 10000,
    created_at TIMESTAMPTZ DEFAULT NOW(),
    UNIQUE(service_id, layer_index)
);

CREATE TABLE honua.layer_fields (
    id SERIAL PRIMARY KEY,
    layer_id INT NOT NULL REFERENCES honua.layers(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    column_name TEXT NOT NULL,
    field_type TEXT NOT NULL,
    alias TEXT,
    is_nullable BOOLEAN DEFAULT TRUE,
    is_editable BOOLEAN DEFAULT TRUE,
    length INT
);

-- Index for layer lookups
CREATE INDEX idx_layers_service_id ON honua.layers(service_id);
CREATE INDEX idx_layer_fields_layer_id ON honua.layer_fields(layer_id);
```

## Additional Acceptance Criteria
- [ ] PostGIS extension enabled: `CREATE EXTENSION IF NOT EXISTS postgis`
- [ ] Schema uses `honua` namespace to avoid conflicts
- [ ] All tables have appropriate indexes
- [ ] DbUp tracks migrations in `SchemaVersions` table
```

---

### Issue #3: IFeatureStore abstraction and PostgresFeatureStore

**Add to description:**

```markdown
## Interface Design (from ARCHITECTURE.md)

```csharp
// Honua.Core/Abstractions/IFeatureStore.cs
public interface IFeatureStore
{
    // Query operations
    Task<FeatureRecord?> GetAsync(string layerId, string featureId, CancellationToken ct);
    Task<QueryResult> QueryAsync(string layerId, FeatureQuery query, CancellationToken ct);
    Task<long> CountAsync(string layerId, FeatureQuery query, CancellationToken ct);
    Task<Envelope?> GetExtentAsync(string layerId, FeatureQuery query, CancellationToken ct);

    // Edit operations
    Task<FeatureRecord> CreateAsync(string layerId, FeatureEdit edit, CancellationToken ct);
    Task<FeatureRecord> UpdateAsync(string layerId, string featureId, FeatureEdit edit, CancellationToken ct);
    Task<bool> DeleteAsync(string layerId, string featureId, CancellationToken ct);

    // Batch operations
    Task<BatchResult> ApplyEditsAsync(string layerId, BatchEdit batch, CancellationToken ct);
}
```

## Relationship to IUnitOfWork
- `IFeatureStore` is for simple operations (single transaction per call)
- `IUnitOfWork` wraps `IFeatureStore` for multi-operation transactions
- See MVP_PLAN.md "Transaction Management" section

## Additional Acceptance Criteria
- [ ] Interface signature matches ARCHITECTURE.md specification
- [ ] PostgresFeatureStore uses raw Npgsql (no Dapper/EF per ADR-0001)
- [ ] All operations use parameterized queries (SQL injection prevention)
- [ ] QueryResult includes total count for paging
```

---

### Issue #19: MVT tile endpoint with PostGIS ST_AsMVT

**Add to description:**

```markdown
## Performance Safeguards (from ARCHITECTURE.md)

### Configuration Defaults
| Setting | Default | Description |
|---------|---------|-------------|
| `MaxFeaturesPerTile` | 10,000 | Hard cap on features returned per tile |
| `TileTimeout` | 10 seconds | Query timeout for tile generation |
| `SimplifyZoom` | 10 | Zoom level below which geometries are simplified |
| `MinZoom` | 0 | Minimum supported zoom level |
| `MaxZoom` | 22 | Maximum supported zoom level |

### SQL Pattern
```sql
SELECT ST_AsMVT(tile, @LayerName, 4096, 'geom') AS mvt
FROM (
    SELECT
        {objectIdField} AS id,
        ST_AsMVTGeom(
            CASE
                WHEN @Zoom < 10 THEN ST_Simplify(ST_Transform({geomField}, 3857), @Tolerance)
                ELSE ST_Transform({geomField}, 3857)
            END,
            ST_MakeEnvelope(@XMin, @YMin, @XMax, @YMax, 3857),
            4096, 256, true
        ) AS geom
        {attributeColumns}
    FROM {tableName}
    WHERE {geomField} && ST_Transform(ST_MakeEnvelope(@XMin, @YMin, @XMax, @YMax, 3857), {srid})
    LIMIT 10000
) AS tile
WHERE geom IS NOT NULL
```

## Additional Acceptance Criteria
- [ ] Implements MaxFeaturesPerTile limit
- [ ] Implements query timeout
- [ ] Geometry simplification at low zoom levels
- [ ] Cache-Control headers set (1 hour default)
- [ ] Empty tiles return 204 No Content
- [ ] TileMath utility calculates correct Web Mercator bounds
```

---

### Issue #25: Blazor WASM admin project setup

**Add to description:**

```markdown
## Architecture Reference
See ADR-0010 for complete Admin UI architecture decisions.

## Key Decisions
- **Component Library:** MudBlazor
- **State Management:** Simple injectable services (upgrade to Fluxor if needed)
- **Testing:** bUnit for components, Playwright for E2E
- **Map Integration:** MapLibre GL JS via JS Interop

## Hosting Options
```csharp
// Program.cs - Support both integrated and standalone hosting
if (builder.Configuration.GetValue<bool>("ServeAdminUI", true))
{
    app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
}
```

## Package References
```xml
<PackageReference Include="MudBlazor" Version="7.*" />
<PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.Authentication" Version="9.*" />
```

## Blocking Issue
⚠️ **Blocked by:** Admin UI wireframes and UX design issue

## Additional Acceptance Criteria
- [ ] MudBlazor installed and configured
- [ ] Basic layout with MudBlazor AppBar and NavMenu
- [ ] HonuaApiClient service scaffolded
- [ ] Served at `/admin` path when integrated
- [ ] CORS configured for standalone hosting option
```

---

### Issue #41: CQL filter support for OGC Items

**Add to description:**

```markdown
## Architecture Reference
See ADR-0009 for Shared Filter AST pattern.

## CQL2 Specification
- Support **CQL2-Text** format (human-readable)
- CQL2-JSON is optional for MVP

## Filter AST Integration
CQL parser should produce `FilterExpression` from `Honua.Core.Queries.Filters`:

```csharp
// Example: "population > 1000 AND S_INTERSECTS(geom, POLYGON(...))"
// Produces:
new BinaryExpression(
    new BinaryExpression(
        new PropertyReference("population"),
        BinaryOperator.GreaterThan,
        new Literal(1000, LiteralType.Integer)),
    BinaryOperator.And,
    new SpatialPredicate(
        SpatialOperator.Intersects,
        new PropertyReference("geom"),
        new GeometryLiteral(wkbBytes, 4326, "WKT")));
```

## Supported CQL2 Features (MVP)
| Feature | Example | Supported |
|---------|---------|-----------|
| Comparison | `population > 1000` | ✅ |
| Logical | `a > 1 AND b < 2` | ✅ |
| LIKE | `name LIKE 'Park%'` | ✅ |
| IN | `state IN ('CA', 'NY')` | ✅ |
| IS NULL | `email IS NULL` | ✅ |
| S_INTERSECTS | `S_INTERSECTS(geom, POLYGON(...))` | ✅ |
| S_WITHIN | `S_WITHIN(geom, POLYGON(...))` | ✅ |
| S_CONTAINS | `S_CONTAINS(geom, POINT(...))` | ✅ |
| Temporal | `timestamp DURING ...` | ❌ (defer) |

## Additional Acceptance Criteria
- [ ] CQL parser produces FilterExpression AST
- [ ] Uses shared SqlFilterTranslator for SQL generation
- [ ] SQL injection prevented via parameterized queries
```

---

### Issue #21: OData metadata endpoint ($metadata)

**Add to description:**

```markdown
## CSDL Generation (Manual, no Microsoft.AspNetCore.OData)

Per ARCHITECTURE.md, we implement minimal OData without the heavy Microsoft library.

## EDM Type Mappings

| .NET/PostgreSQL Type | EDM Type |
|---------------------|----------|
| string/text | Edm.String |
| int/integer | Edm.Int32 |
| long/bigint | Edm.Int64 |
| double/float8 | Edm.Double |
| decimal/numeric | Edm.Decimal |
| bool/boolean | Edm.Boolean |
| DateTime/timestamptz | Edm.DateTimeOffset |
| Point | Edm.GeographyPoint |
| LineString | Edm.GeographyLineString |
| Polygon | Edm.GeographyPolygon |
| MultiPoint | Edm.GeographyMultiPoint |
| MultiLineString | Edm.GeographyMultiLineString |
| MultiPolygon | Edm.GeographyMultiPolygon |

## Response Format
```xml
<?xml version="1.0" encoding="utf-8"?>
<edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
  <edmx:DataServices>
    <Schema Namespace="Honua" xmlns="http://docs.oasis-open.org/odata/ns/edm">
      <EntityType Name="Feature_parks">
        <Key>
          <PropertyRef Name="objectid"/>
        </Key>
        <Property Name="objectid" Type="Edm.Int64" Nullable="false"/>
        <Property Name="name" Type="Edm.String"/>
        <Property Name="area" Type="Edm.Double"/>
        <Property Name="geometry" Type="Edm.GeographyPolygon"/>
      </EntityType>
      <EntityContainer Name="HonuaContainer">
        <EntitySet Name="parks" EntityType="Honua.Feature_parks"/>
      </EntityContainer>
    </Schema>
  </edmx:DataServices>
</edmx:Edmx>
```

## Additional Acceptance Criteria
- [ ] CSDL generated dynamically from layer schema
- [ ] Correct EDM types for geometry fields
- [ ] Content-Type: application/xml
- [ ] Excel/Power BI can parse and show schema
```

---

## Summary of Changes

| Type | Item | Status |
|------|------|--------|
| **New Issue** | Admin UI wireframes and UX design | Create |
| **New Issue** | Admin UI Playwright integration tests | Create |
| **Update** | #1 Database schema v1 | Add DDL, indexes |
| **Update** | #3 IFeatureStore abstraction | Add interface, UoW relationship |
| **Update** | #19 MVT tiles | Add safeguard thresholds |
| **Update** | #21 OData $metadata | Add CSDL generation, EDM types |
| **Update** | #25 Blazor WASM setup | Add ADR reference, blocking note |
| **Update** | #41 CQL filter support | Add AST integration |
